using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Replay of the 2026-09-02 two-run log (a copy of <c>docs/dev/analysis/dome-20260902/Log.txt</c>).</summary>
/// <remarks>
/// <para>This fixture holds the coordinate discontinuities the dome capture does not:
/// sequence 31 to 32 changes Z from 400 to -0.852 with a reported 1,432 mm/s, sequence 42
/// to 43 returns Z from -389.893 to 0, and the last eleven samples of run 2 move at Z=400
/// after the printed layers - the lift, G28 and M84 both saved G-code files end with.</para>
/// <para>The rule these prove is that such samples are SEGMENTED, not smoothed: the log
/// keeps every reported coordinate, and no drawn or exported run is ever allowed to span a
/// known discontinuity.</para>
/// </remarks>
public class Log20260902ReplayTests
{
    static IReadOnlyList<PrintLogRecord> Log()
        => TelemetryReplayFixtures.LoadLog("log-20260902", "print.csv");

    [Fact]
    public void Fixture_HasBothRuns()
    {
        var log = Log();
        Assert.Equal(182, log.Count);
        Assert.Equal(new[] { 1, 2 }, log.Select(r => r.JobRun).Distinct().OrderBy(v => v).ToArray());
    }

    [Fact]
    public void TheDescentAtSequence32_IsADiscontinuityNotAMovement()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        int i = TelemetryReplayFixtures.IndexOfSeq(log, 32);

        Assert.Equal(400.0, log[i - 1].Position.Z, 6);
        Assert.Equal(-0.852, log[i].Position.Z, 6);
        Assert.True(verdicts[i].SegmentStarted);
        Assert.NotEqual(verdicts[i - 1].SegmentId, verdicts[i].SegmentId);
        Assert.Equal(TrailQuality.Discontinuity, verdicts[i].Quality);
        // the reported coordinates survive untouched - the log is evidence, not a drawing
        Assert.Equal(176.191, log[i].Position.X, 6);
        Assert.Equal(-674.999, log[i].Position.Y, 6);
    }

    [Fact]
    public void TheReturnAtSequence43_IsADiscontinuity()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        int i = TelemetryReplayFixtures.IndexOfSeq(log, 43);

        Assert.Equal(-389.893, log[i - 1].Position.Z, 6);
        Assert.Equal(0.0, log[i].Position.Z, 6);
        Assert.True(verdicts[i].SegmentStarted);
        Assert.Equal(TrailQuality.Discontinuity, verdicts[i].Quality);
        // A 419 mm step is a coordinate reset before it is a fast move, so that rule
        // reports it - the speed check would also have caught it (419 mm over 1.6 s is
        // 262 mm/s against 21.5 requested), but the more specific reason is the useful one.
        Assert.Contains("coordinate jump", verdicts[i].Reason!, StringComparison.Ordinal);
        Assert.True(log[i - 1].Position.DistanceTo(log[i].Position) > 400);
    }

    [Fact]
    public void TheRunTwoTailAtZ400_IsFooterTravelNotPrinting()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        var tail = new List<int>();
        for (int i = 0; i < log.Count; i++)
            if (log[i].JobRun == 2 && Math.Abs(log[i].Position.Z - 400.0) < 1e-6 && log[i].Seq > 100)
                tail.Add(i);

        Assert.True(tail.Count >= 10, "the fixture ends with the lift/G28/M84 tail at Z=400");
        foreach (int i in tail)
        {
            Assert.Equal(TrailClass.StartupOrFooter, verdicts[i].Classification);
            Assert.NotEqual(TrailClass.Printing, verdicts[i].Classification);
        }
    }

    [Fact]
    public void TheApproachDescentEarlyInRunTwo_IsAlsoNotPrinting()
    {
        // Sequences 33 to 42 descend from Z=-39 to Z=-390 with the file position and the
        // extrusion both standing still: approach motion, not deposited material.
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        for (long seq = 33; seq <= 42; seq++)
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, seq);
            Assert.NotEqual(TrailClass.Printing, verdicts[i].Classification);
        }
    }

    [Fact]
    public void NoSegmentSpansAKnownDiscontinuity()
    {
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        foreach (long seq in new long[] { 32, 43 })
        {
            int i = TelemetryReplayFixtures.IndexOfSeq(log, seq);
            Assert.NotEqual(verdicts[i - 1].SegmentId, verdicts[i].SegmentId);
        }
        // and the run boundary itself
        int firstOfRunTwo = -1;
        for (int i = 1; i < log.Count; i++)
            if (log[i].JobRun == 2 && log[i - 1].JobRun == 1) { firstOfRunTwo = i; break; }
        Assert.True(firstOfRunTwo > 0);
        Assert.NotEqual(verdicts[firstOfRunTwo - 1].SegmentId, verdicts[firstOfRunTwo].SegmentId);
    }

    [Fact]
    public void EverySegmentIsInternallyConsistent()
    {
        // The property the whole policy exists to guarantee: within one segment, two
        // consecutive rows never cross a run boundary, never go backwards in time, and
        // never roll the file position back.
        var log = Log();
        var verdicts = TelemetryReplayFixtures.Replay(log);
        for (int i = 1; i < log.Count; i++)
        {
            if (verdicts[i].SegmentId != verdicts[i - 1].SegmentId) continue;
            Assert.Equal(log[i - 1].JobRun, log[i].JobRun);
            Assert.True(log[i].TakenUtc >= log[i - 1].TakenUtc);
            if (log[i].FilePosition.HasValue && log[i - 1].FilePosition.HasValue)
                Assert.True(log[i].FilePosition!.Value >= log[i - 1].FilePosition!.Value);
        }
    }

    [Fact]
    public void NoIntendedPath_MeansNoDeviationVerdictAtAll()
    {
        // Neither run has a verified intended path here, so every comparison must report
        // unknown rather than a comfortable zero.
        var verdicts = TelemetryReplayFixtures.Replay(Log());
        Assert.All(verdicts, v => Assert.True(double.IsNaN(v.PathDistanceMm)));
        Assert.DoesNotContain(verdicts, v => v.Quality == TrailQuality.SuspectPathDisagreement);
    }
}
