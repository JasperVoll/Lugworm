using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Replay of the 2026-09-09 dome run against the intended path taken from its own
/// G-code.</summary>
/// <remarks>
/// <para>Incident facts these tests encode, not re-litigate: the operator confirms the
/// robot did NOT make the seven excursions at CSV sequences 397, 405, 453, 492, 528, 535
/// and 595. They are false REPORTED positions, already present in the raw CSV upstream of
/// any viewport interpolation. Their source - controller, transport, parsing or
/// publication - is not localized, because the per-poll HTTP bodies were not retained.</para>
/// <para>What the tests must therefore show is that the handling is right whatever the
/// source turns out to be: each spike is FLAGGED and PRESERVED with its raw coordinates,
/// its neighbours stay clean, the segment is not broken around it (that would hide the
/// excursion), and a sustained deviation is not quietly absorbed either. A cosmetic
/// smoothing or projection pass would fail every one of these.</para>
/// </remarks>
public class DomeReplayTests
{
    /// <summary>The operator-confirmed false reported positions.</summary>
    static readonly long[] Spikes = { 397, 405, 453, 492, 528, 535, 595 };

    static IReadOnlyList<PrintLogRecord> Log()
        => TelemetryReplayFixtures.LoadLog("dome-20260909", "print.csv");

    static SameZPathIndex Path()
        => TelemetryReplayFixtures.LoadIntendedPath("dome-20260909", "intended-path-layers.gcode");

    [Fact]
    public void Fixture_HasTheRecordedRunAndTheTrimmedPath()
    {
        var log = Log();
        Assert.Equal(248, log.Count);
        Assert.All(log, r => Assert.Equal(1, r.Schema));   // a schema-1 log still imports
        var path = Path();
        Assert.True(path.Valid);
        Assert.True(path.SegmentCount > 4000, "the trimmed path should carry the five spike layers");
    }

    [Fact]
    public void EverySpike_IsFlaggedSuspectAndKeepsItsRawCoordinates()
    {
        var log = Log();
        var before = log.Select(r => r.Position).ToArray();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());

        foreach (long seq in Spikes)
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, seq);
            var v = verdicts[i];
            Assert.True(v.PathDistanceMm > 5.0,
                $"seq {seq} should be more than 5 mm from its own layer, was {v.PathDistanceMm:0.###}");
            Assert.Equal(TrailQuality.SuspectPathDisagreement, v.Quality);
            // preserved exactly: no projection, no smoothing, no deletion
            Assert.Equal(before[i].X, log[i].Position.X, 12);
            Assert.Equal(before[i].Y, log[i].Position.Y, 12);
            Assert.Equal(before[i].Z, log[i].Position.Z, 12);
        }
    }

    [Fact]
    public void SpikeDistances_MatchTheAnalysisWithinRounding()
    {
        // Values from docs/dev/analysis/dome-20260909/dome-summary.json, computed
        // independently by the Python script against the full G-code.
        var expected = new Dictionary<long, double>
        {
            { 397, 10.551 }, { 405, 15.744 }, { 453, 18.412 }, { 492, 12.394 },
            { 528, 15.154 }, { 535, 15.714 }, { 595, 11.932 },
        };
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        foreach (var kv in expected)
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, kv.Key);
            Assert.Equal(kv.Value, verdicts[i].PathDistanceMm, 2);
        }
    }

    [Fact]
    public void TheNeighboursOfEverySpike_StayOnThePathAndUnflagged()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        foreach (long seq in Spikes)
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, seq);
            foreach (int j in new[] { i - 1, i + 1 })
            {
                if (j < 0 || j >= log.Count) continue;
                var v = verdicts[j];
                if (double.IsNaN(v.PathDistanceMm)) continue;   // a layer outside the trimmed path
                Assert.True(v.PathDistanceMm < 0.15,
                    $"seq {log[j].Seq} next to {seq} should be within 0.15 mm, was {v.PathDistanceMm:0.###}");
                Assert.NotEqual(TrailQuality.SuspectPathDisagreement, v.Quality);
            }
        }
    }

    [Fact]
    public void ASpikeIsIsolated_WithinItsLayerAtAPlausibleSpeedAndMonotonicFileProgress()
    {
        // This is why a gross speed threshold or a reconnect-only segmentation misses them,
        // and why the path comparison has to exist at all.
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        foreach (long seq in Spikes)
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, seq);
            var row = log[i];
            var previous = log[i - 1];
            Assert.Equal(previous.Layer, row.Layer);                       // within a layer
            Assert.Equal(previous.Position.Z, row.Position.Z, 6);          // Z unchanged
            Assert.True(row.FilePosition > previous.FilePosition);         // file advancing
            Assert.InRange(row.SpeedObserved!.Value, 30.0, 55.0);          // reported speed is plausible
            Assert.False(verdicts[i].SegmentStarted,
                $"seq {seq} must stay inside its segment - breaking it would hide the excursion");
            Assert.NotEqual(TrailQuality.SuspectSpeed, verdicts[i].Quality);
        }
    }

    [Fact]
    public void NoOtherSampleIsFlaggedForPathDisagreement()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        var flagged = new List<long>();
        for (int i = 0; i < log.Count; i++)
            if (verdicts[i].Quality == TrailQuality.SuspectPathDisagreement) flagged.Add(log[i].Seq);
        Assert.Equal(Spikes.OrderBy(s => s).ToArray(), flagged.OrderBy(s => s).ToArray());
    }

    [Fact]
    public void TheBodyOfTheRunSitsOnItsIntendedPath()
    {
        // Median same-Z distance in the analysis is 0.093 mm. If the replay's geometry
        // were wrong, that number would not come out of a completely separate parser.
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        var distances = verdicts.Select(v => v.PathDistanceMm).Where(d => !double.IsNaN(d)).OrderBy(d => d).ToArray();
        // The trimmed fixture carries five of the run's 62 layers (59 recorded rows), so a
        // comparison is available for those and reported unavailable everywhere else.
        Assert.True(distances.Length >= 40,
            "the five trimmed layers should give a comparison for most of their rows, got " + distances.Length);
        double median = distances[distances.Length / 2];
        Assert.InRange(median, 0.0, 0.35);
    }

    [Fact]
    public void SustainedDeviation_StaysVisibleOnEverySampleAndIsNeverAbsorbed()
    {
        // A real tracking fault: the rest of layer 10 offset by 9 mm - a layer the trimmed
        // intended path covers. Nothing may decide that a long enough disagreement is the
        // new normal.
        var log = Log().ToList();
        int from = TelemetryReplayFixtures.IndexOfSeq(log, 493);
        int to = TelemetryReplayFixtures.IndexOfSeq(log, 502);
        // Pushed 40 mm outward from the layer's own centre, so every shifted sample is off
        // the contour rather than landing on another pass of the same spiral.
        double cx = 0, cy = 0;
        for (int i = from; i <= to; i++) { cx += log[i].Position.X; cy += log[i].Position.Y; }
        cx /= (to - from + 1); cy /= (to - from + 1);
        var offset = new List<int>();
        for (int i = from; i <= to; i++)
        {
            var p = log[i].Position;
            double dx = p.X - cx, dy = p.Y - cy;
            double n = Math.Sqrt(dx * dx + dy * dy);
            if (n < 1e-9) { dx = 1; dy = 0; n = 1; }
            log[i] = Shift(log[i], 40.0 * dx / n, 40.0 * dy / n);
            offset.Add(i);
        }
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        int flagged = 0;
        foreach (int i in offset)
        {
            if (double.IsNaN(verdicts[i].PathDistanceMm)) continue;
            Assert.Equal(TrailQuality.SuspectPathDisagreement, verdicts[i].Quality);
            Assert.True(verdicts[i].ConsecutivePathDisagreements > 0);
            flagged++;
        }
        Assert.True(flagged >= 8, "a sustained deviation must stay flagged sample by sample");
        Assert.True(verdicts[offset[offset.Count - 1]].ConsecutivePathDisagreements >= 8,
            "the run of disagreements must be counted, so a tracking fault is not read as isolated glitches");
    }

    [Fact]
    public void TheRunIsOneSegmentApartFromItsKnownBoundaries()
    {
        // The recorded run has no reconnect gap large enough to break it and no file
        // rollback, so the observed trail must not fall apart into fragments - an
        // over-eager policy would be as wrong as an over-permissive one.
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log, Path());
        int breaks = verdicts.Count(v => v.SegmentStarted);
        Assert.InRange(breaks, 1, 6);
    }

    [Fact]
    public void TheHaltedRunIsNotAFinishedPrint()
    {
        // manifest.complete=true describes the LOG. The run itself ended Halted, which is
        // what the outcome field now carries.
        var manifest = JobLogManifest.Parse(
            System.IO.File.ReadAllText(TelemetryReplayFixtures.Path("dome-20260909", "manifest.json")));
        Assert.True(manifest.Complete);
        Assert.Null(manifest.Outcome);   // a version-1 manifest knows nothing about the outcome

        var log = Log();
        var last = log[log.Count - 1];
        Assert.Equal(PrintLogEventKind.JobEnd, last.Kind);
        Assert.Equal("Halted", last.TerminalStatus);
    }

    static PrintLogRecord Shift(PrintLogRecord r, double dx, double dy)
    {
        var copy = new PrintLogRecord
        {
            Seq = r.Seq,
            Kind = r.Kind,
            JobRun = r.JobRun,
            JobFile = r.JobFile,
            Layer = r.Layer,
            LayerSource = r.LayerSource,
            TakenUtc = r.TakenUtc,
            JobElapsedSeconds = r.JobElapsedSeconds,
            DeltaSeconds = r.DeltaSeconds,
            Position = new Vec3(r.Position.X + dx, r.Position.Y + dy, r.Position.Z),
            SpeedObserved = r.SpeedObserved,
            SpeedRequested = r.SpeedRequested,
            SpeedTop = r.SpeedTop,
            SpeedFactorPercent = r.SpeedFactorPercent,
            RawExtrusion = r.RawExtrusion,
            FractionPrinted = r.FractionPrinted,
            FilePosition = r.FilePosition,
            Status = r.Status,
            BoundaryUncertaintySeconds = r.BoundaryUncertaintySeconds,
            FinishReason = r.FinishReason,
            TerminalStatus = r.TerminalStatus,
            Schema = r.Schema,
            PositionValid = r.PositionValid,
        };
        return copy;
    }
}
