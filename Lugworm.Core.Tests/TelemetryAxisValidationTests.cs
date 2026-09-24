using System;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The parsing-layer rule the 2026-09-09 telemetry review added: a valid X/Y/Z
/// sample can never be fabricated from a coordinate the machine did not report as a
/// number. Before this, <c>JsonValue.Num</c> mapped a missing field, a string or an
/// object to 0 and a boolean to 0/1, so a response that simply omitted
/// <c>machinePosition</c> produced a well-formed position at the machine origin.</summary>
public class TelemetryAxisValidationTests
{
    static JsonValue State(string status = "processing")
        => JsonValue.Parse("{\"status\":\"" + status + "\"}");

    static JsonValue Axes(string json) => JsonValue.Parse(json);

    const string Good =
        "[{\"letter\":\"X\",\"machinePosition\":100.5,\"userPosition\":100.5,\"homed\":true}," +
        " {\"letter\":\"Y\",\"machinePosition\":-50,\"userPosition\":-50,\"homed\":true}," +
        " {\"letter\":\"Z\",\"machinePosition\":12.25,\"userPosition\":12.25,\"homed\":true}]";

    // ---- the rule itself ---------------------------------------------------------

    [Fact]
    public void CoherentAxes_AreValidAndCarryTheReportedCoordinates()
    {
        var snap = ObjectModelParser.FromRrModel(State(), Axes(Good), null);

        Assert.Equal(PositionValidity.Valid, snap.PositionValidity);
        Assert.Null(snap.PositionInvalidReason);
        Assert.NotNull(snap.MachinePosition);
        var p = snap.MachinePosition!.Value;
        Assert.Equal(100.5, p.X, 9);
        Assert.Equal(-50.0, p.Y, 9);
        Assert.Equal(12.25, p.Z, 9);
    }

    [Fact]
    public void MissingCoordinate_IsNotAPositionAtTheOrigin()
    {
        // Y carries no machinePosition at all: the old parser read 0 for it.
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":100.5,\"homed\":true}," +
            " {\"letter\":\"Y\",\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        var snap = ObjectModelParser.FromRrModel(State(), axes, null);

        Assert.False(snap.PositionValid);
        Assert.Null(snap.MachinePosition);
        Assert.Null(snap.Axis("Y"));                     // never a fabricated zero
        Assert.Contains("Y", snap.PositionInvalidReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void StringCoordinate_IsRejected()
    {
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":\"100.5\",\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        var snap = ObjectModelParser.FromRrModel(State(), axes, null);

        Assert.Equal(PositionValidity.NotNumeric, snap.PositionValidity);
        Assert.Null(snap.Axis("X"));
    }

    [Fact]
    public void BooleanCoordinate_IsNotOneMillimetre()
    {
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":true,\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        var snap = ObjectModelParser.FromRrModel(State(), axes, null);

        Assert.Equal(PositionValidity.NotNumeric, snap.PositionValidity);
        Assert.Null(snap.MachinePosition);
    }

    [Fact]
    public void ObjectCoordinate_IsRejected()
    {
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":{\"value\":100.5},\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        Assert.Equal(PositionValidity.NotNumeric,
                     ObjectModelParser.FromRrModel(State(), axes, null).PositionValidity);
    }

    [Fact]
    public void NullCoordinate_IsRejected()
    {
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":null,\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        Assert.False(ObjectModelParser.FromRrModel(State(), axes, null).PositionValid);
    }

    [Fact]
    public void DuplicateAxis_IsUndecidableRatherThanLastWins()
    {
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":100.5,\"homed\":true}," +
            " {\"letter\":\"X\",\"machinePosition\":180.0,\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":12.25,\"homed\":true}]");

        var snap = ObjectModelParser.FromRrModel(State(), axes, null);

        Assert.Equal(PositionValidity.DuplicateAxis, snap.PositionValidity);
        Assert.Null(snap.MachinePosition);
    }

    [Fact]
    public void NoAxesAtAll_IsUnreported()
    {
        var snap = ObjectModelParser.FromRrModel(State(), Axes("[]"), null);
        Assert.Equal(PositionValidity.Unreported, snap.PositionValidity);
        Assert.Null(snap.MachinePosition);
    }

    [Fact]
    public void NonPositionAxisWithoutCoordinates_DoesNotInvalidateThePosition()
    {
        // U is a hidden parameter axis on the lab machine; it has no coordinate to read
        // and must not make the head position unusable.
        var axes = Axes(
            "[{\"letter\":\"X\",\"machinePosition\":1,\"homed\":true}," +
            " {\"letter\":\"Y\",\"machinePosition\":2,\"homed\":true}," +
            " {\"letter\":\"Z\",\"machinePosition\":3,\"homed\":true}," +
            " {\"letter\":\"U\",\"homed\":false,\"visible\":false}]");

        var snap = ObjectModelParser.FromRrModel(State(), axes, null);

        Assert.True(snap.PositionValid);
        Assert.True(snap.AllHomed);   // a hidden unhomed axis still does not gate homing
    }

    // ---- the legacy rr_status path ------------------------------------------------

    [Fact]
    public void LegacyStatus_ShortCoordinateArrayIsNotAPosition()
    {
        var status = JsonValue.Parse(
            "{\"status\":\"I\",\"coords\":{\"machine\":[10,20],\"xyz\":[10,20],\"axesHomed\":[1,1,1]}}");

        var snap = ObjectModelParser.FromRrStatus(status, null);

        Assert.Equal(PositionValidity.MissingAxis, snap.PositionValidity);
        Assert.Null(snap.MachinePosition);
        Assert.False(snap.AllHomed);
    }

    [Fact]
    public void LegacyStatus_NonNumericCoordinateIsRejected()
    {
        var status = JsonValue.Parse(
            "{\"status\":\"I\",\"coords\":{\"machine\":[10,\"20\",30],\"axesHomed\":[1,1,1]}}");

        var snap = ObjectModelParser.FromRrStatus(status, null);

        Assert.Equal(PositionValidity.NotNumeric, snap.PositionValidity);
        Assert.Null(snap.Axis("Y"));
    }

    [Fact]
    public void LegacyStatus_GoodCoordinatesAreValid()
    {
        var status = JsonValue.Parse(
            "{\"status\":\"I\",\"coords\":{\"machine\":[10,20,30],\"xyz\":[11,21,31],\"axesHomed\":[1,1,1]}}");

        var snap = ObjectModelParser.FromRrStatus(status, null);

        Assert.True(snap.PositionValid);
        Assert.Equal(30.0, snap.MachinePosition!.Value.Z, 9);
        Assert.Equal(31.0, snap.UserPosition!.Value.Z, 9);
        Assert.True(snap.AllHomed);
    }

    // ---- acquisition identity and timing ------------------------------------------

    [Fact]
    public void AcquisitionStamp_DatesTheSnapshotByTheCoordinateResponse()
    {
        var request = new DateTime(2026, 9, 9, 7, 48, 53, 0, DateTimeKind.Utc);
        var received = request.AddMilliseconds(231);
        var stamp = new AcquisitionStamp
        {
            AcquisitionId = 4242,
            RequestStartedUtc = request,
            ResponseReceivedUtc = received,
            ConnectionGeneration = 7,
            Composite = true,
            CoordinatePages = 2,
        };

        var snap = ObjectModelParser.FromRrModel(State(), Axes(Good), null, null, null, stamp);

        Assert.Equal(4242, snap.AcquisitionId);
        Assert.Equal(7, snap.ConnectionGeneration);
        Assert.True(snap.CompositeObservation);
        // TakenUtc is the COORDINATE response's arrival, not the end of the parse - the
        // metadata the poll fetches afterwards must not move the observation in time.
        Assert.Equal(received, snap.TakenUtc);
        Assert.Equal(0.231, stamp.RoundTripSeconds, 6);
    }

    [Fact]
    public void DelayedCosmeticMetadata_DoesNotMoveTheCoordinateTimestamps()
    {
        var request = new DateTime(2026, 9, 9, 7, 48, 53, 0, DateTimeKind.Utc);
        var stamp = new AcquisitionStamp
        {
            AcquisitionId = 1,
            RequestStartedUtc = request,
            ResponseReceivedUtc = request.AddMilliseconds(40),
            ConnectionGeneration = 1,
        };
        // The job key is read after the coordinates on the modern path; a slow file-info
        // read must not make the position look newer than it is.
        var job = JsonValue.Parse(
            "{\"file\":{\"fileName\":\"0:/gcodes/print.gcode\",\"size\":1000},\"filePosition\":250,\"layer\":3}");

        var snap = ObjectModelParser.FromRrModel(State(), Axes(Good), job, null, null, stamp);

        Assert.Equal(request.AddMilliseconds(40), snap.TakenUtc);
        Assert.Equal(3, snap.Job!.Layer);
    }

    [Fact]
    public void UnstampedSnapshot_KeepsTheOldParseTimeBehaviour()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var snap = ObjectModelParser.FromRrModel(State(), Axes(Good), null);

        Assert.Equal(0, snap.AcquisitionId);
        Assert.Equal(-1, snap.ConnectionGeneration);
        Assert.False(snap.CompositeObservation);
        Assert.True(snap.TakenUtc > before);
    }

    [Fact]
    public void AcquisitionIds_AreStrictlyIncreasing()
    {
        long a = AcquisitionCounter.Next();
        long b = AcquisitionCounter.Next();
        long c = AcquisitionCounter.Next();
        Assert.True(b > a);
        Assert.True(c > b);
    }

    // ---- the coherent-input guarantee ---------------------------------------------

    [Fact]
    public void CoherentValidInput_CannotProduceACoordinateItDidNotContain()
    {
        // The regression the plan asks for at the parsing layer: whatever finite numbers a
        // well-formed response carries, the parsed position is exactly those numbers - the
        // parser has no path that substitutes, defaults or rounds a coordinate.
        double[][] cases =
        {
            new[] { 0.0, 0.0, 0.0 },
            new[] { 370.791, -467.519, 25.109 },
            new[] { -1e6, 1e6, 1e-9 },
            new[] { 148.629, -430.502, 5.315 },
        };
        foreach (var c in cases)
        {
            string json = "[{\"letter\":\"X\",\"machinePosition\":" + R(c[0]) + ",\"homed\":true},"
                        + " {\"letter\":\"Y\",\"machinePosition\":" + R(c[1]) + ",\"homed\":true},"
                        + " {\"letter\":\"Z\",\"machinePosition\":" + R(c[2]) + ",\"homed\":true}]";
            var snap = ObjectModelParser.FromRrModel(State(), Axes(json), null);
            Assert.True(snap.PositionValid);
            var p = snap.MachinePosition!.Value;
            Assert.Equal(c[0], p.X, 9);
            Assert.Equal(c[1], p.Y, 9);
            Assert.Equal(c[2], p.Z, 9);
        }
    }

    static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void NonFiniteCoordinate_IsARefusedResponse()
    {
        // 1e999 parses to +Infinity on .NET Core; the JSON parser rejects it outright so a
        // malformed body can never reach a motion gate.
        Assert.Throws<FormatException>(() => JsonValue.Parse(
            "[{\"letter\":\"X\",\"machinePosition\":1e999,\"homed\":true}]"));
    }
}
