using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The bounded diagnostic window. It exists because the 2026-09-09 capture could
/// not be localized: the CSV showed the excursion, and the HTTP bodies that produced it
/// were already gone.</summary>
public class RawTelemetryWindowTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 7, 48, 0, DateTimeKind.Utc);

    static RawTelemetryRecord Rec(long id, string body = "{\"result\":[]}")
        => new RawTelemetryRecord
        {
            AcquisitionId = id,
            PollId = id,
            ConnectionGeneration = 1,
            RequestUtc = T0.AddSeconds(id * 0.4),
            ReceivedUtc = T0.AddSeconds(id * 0.4 + 0.05),
            Endpoint = "rr_model?key=move.axes&flags=vnd4",
            ApiMode = "rr_model",
            Body = body,
            PositionValidity = "Valid",
            MachinePosition = new Vec3(id, -id, 5),
        };

    [Fact]
    public void TheRingIsBounded()
    {
        var window = new RawTelemetryWindow(capacity: 10);
        for (int i = 1; i <= 100; i++) window.Add(Rec(i));
        Assert.Equal(10, window.Count);
        Assert.Equal(90, window.Overwritten);
        Assert.Equal(91, window.Snapshot()[0].AcquisitionId);
    }

    [Fact]
    public void BodiesAreTruncatedToTheCapAndSayTheirRealLength()
    {
        var window = new RawTelemetryWindow(capacity: 4, maxBodyChars: 32);
        var record = Rec(1, new string('x', 500));
        window.Add(record);

        Assert.Equal(32, record.Body.Length);
        Assert.Equal(500, record.BodyLength);
        Assert.True(record.BodyTruncated);
    }

    [Fact]
    public void ATriggerCapturesThePollsBeforeAndAfterIt()
    {
        var captures = new List<RawTelemetryCapture>();
        var window = new RawTelemetryWindow(captures.Add, capacity: 20, maxBodyChars: 64,
                                            postTriggerRecords: 3);
        for (int i = 1; i <= 5; i++) window.Add(Rec(i));
        window.Trigger("SuspectPathDisagreement", T0.AddSeconds(2));
        Assert.Empty(captures);                        // still collecting the post window
        for (int i = 6; i <= 8; i++) window.Add(Rec(i));

        var capture = Assert.Single(captures);
        Assert.Equal("SuspectPathDisagreement", capture.Reason);
        // five before the trigger (the fifth IS the suspect one) plus three after
        Assert.Equal(8, capture.Records.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                     capture.Records.Select(r => r.AcquisitionId).ToArray());
    }

    [Fact]
    public void EveryPollIsOffered_NotEveryFifth()
    {
        // The dome spikes sit between exported CSV rows; a sampled window would have lost
        // the neighbours that prove each excursion is isolated.
        var captures = new List<RawTelemetryCapture>();
        var window = new RawTelemetryWindow(captures.Add, capacity: 200, postTriggerRecords: 2);
        for (int i = 1; i <= 40; i++) window.Add(Rec(i));
        window.Trigger("suspect", T0);
        window.Add(Rec(41));
        window.Add(Rec(42));

        var capture = Assert.Single(captures);
        Assert.Equal(42, capture.Records.Count);
        for (int i = 0; i < 42; i++) Assert.Equal(i + 1, capture.Records[i].AcquisitionId);
    }

    [Fact]
    public void ASecondTriggerExtendsThePendingCaptureInsteadOfStartingAStorm()
    {
        var captures = new List<RawTelemetryCapture>();
        var window = new RawTelemetryWindow(captures.Add, capacity: 50, postTriggerRecords: 3);
        window.Add(Rec(1));
        window.Trigger("SuspectPathDisagreement", T0);
        window.Add(Rec(2));
        window.Trigger("SuspectSpeed", T0.AddSeconds(1));
        window.Add(Rec(3));
        window.Add(Rec(4));
        Assert.Empty(captures);
        window.Add(Rec(5));

        var capture = Assert.Single(captures);
        Assert.Contains("SuspectPathDisagreement", capture.Reason, StringComparison.Ordinal);
        Assert.Contains("SuspectSpeed", capture.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FlushHandsOverAnIncompleteWindow()
    {
        var captures = new List<RawTelemetryCapture>();
        var window = new RawTelemetryWindow(captures.Add, capacity: 50, postTriggerRecords: 10);
        window.Add(Rec(1));
        window.Trigger("halted before the post window filled", T0);
        window.Add(Rec(2));
        window.Flush();

        var capture = Assert.Single(captures);
        Assert.Equal(2, capture.Records.Count);      // a truncated window is still evidence
    }

    [Fact]
    public void ACaptureReportsWhatRolledOutOfTheRingBeforeIt()
    {
        var captures = new List<RawTelemetryCapture>();
        var window = new RawTelemetryWindow(captures.Add, capacity: 5, postTriggerRecords: 0);
        for (int i = 1; i <= 12; i++) window.Add(Rec(i));
        window.Trigger("suspect", T0);

        var capture = Assert.Single(captures);
        Assert.Equal(5, capture.Records.Count);
        Assert.Equal(7, capture.OverwrittenBefore);
    }

    [Fact]
    public void ASinkThatThrows_NeverReachesThePollLoop()
    {
        var window = new RawTelemetryWindow(_ => throw new InvalidOperationException("disk on fire"),
                                            capacity: 4, postTriggerRecords: 0);
        window.Add(Rec(1));
        window.Trigger("suspect", T0);   // must not throw
        Assert.Equal(1, window.Captures);
    }

    [Fact]
    public void EachRecordSerializesAsOneJsonObjectLine()
    {
        var capture = new RawTelemetryCapture("SuspectPathDisagreement", T0,
            new[] { Rec(1), Rec(2) }, 3);
        var lines = capture.ToJsonLines();

        Assert.Equal(3, lines.Count);
        foreach (string line in lines)
        {
            Assert.StartsWith("{", line, StringComparison.Ordinal);
            Assert.EndsWith("}", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            var json = JsonValue.Parse(line);        // parses as JSON, so the file is readable
            Assert.Equal(JsonKind.Object, json.Kind);
        }
        Assert.Equal("SuspectPathDisagreement", JsonValue.Parse(lines[0]).Str("capture"));
        Assert.Equal(3, JsonValue.Parse(lines[0]).Int("overwrittenBefore"));

        var first = JsonValue.Parse(lines[1]);
        Assert.Equal(1, first.Int("acq"));
        Assert.Equal("rr_model", first.Str("api"));
        Assert.Equal("Valid", first.Str("positionValidity"));
        Assert.Equal(3, first.At("machine").Count);
    }

    [Fact]
    public void ABodyWithQuotesAndNewlines_StaysOneParsableLine()
    {
        var record = Rec(1, "{\"err\":\"bad \\\"thing\\\"\",\n\"result\":null}");
        var capture = new RawTelemetryCapture("note", T0, new[] { record }, 0);
        var line = capture.ToJsonLines()[1];
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Equal(record.Body, JsonValue.Parse(line).Str("body"));
    }

    [Fact]
    public void ANoteRecordsATransportFailureWithNoBody()
    {
        var record = new RawTelemetryRecord
        {
            PollId = 9,
            ReceivedUtc = T0,
            ApiMode = "rr_model",
            Note = "transport failure: System.Net.Http.HttpRequestException: connection reset",
        };
        var json = JsonValue.Parse(record.ToJson());
        Assert.Contains("connection reset", json.Str("note"), StringComparison.Ordinal);
        Assert.Equal("", json.Str("body"));
    }

    [Fact]
    public void ARawBodyProbeCarriesTheCoordinateRequestTiming()
    {
        var probe = new RawBodyProbe("rr_model?key=move.axes&flags=vnd4", "rr_model", "{}",
                                     T0, T0.AddMilliseconds(37), 4, 2);
        Assert.Equal("rr_model", probe.ApiMode);
        Assert.Equal(4, probe.ConnectionGeneration);
        Assert.Equal(2, probe.Page);
        Assert.Equal(37, (probe.ReceivedUtc - probe.RequestUtc).TotalMilliseconds, 6);
    }
}
