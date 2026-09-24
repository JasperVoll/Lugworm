using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>5.0.0: the Live View trail keeps the whole print. On 2026-09-23 a 64-minute
/// print with four pauses lost everything before its first pause, because a 5,000-point
/// budget discarded the oldest points once one 0.2 mm compaction per run had freed almost
/// nothing on curved walls. These drive <see cref="TrailRetention"/> exactly the way
/// <c>LiveViewportController.EnforceRetentionLocked</c> does.</summary>
public class TrailRetentionTests
{
    const int Budget = 5000;

    /// <summary>Runs of observed points with their retention state, and the untouched
    /// originals to measure the error against.</summary>
    sealed class Trail
    {
        public readonly List<List<Vec3>> Runs = new();
        public readonly List<List<Vec3>> Originals = new();
        public readonly List<TrailRetention.RunState> States = new();
        public long Pruned;

        public int Count => Runs.Sum(r => r.Count);

        public void NewRun()
        {
            Runs.Add(new List<Vec3>()); Originals.Add(new List<Vec3>()); States.Add(new TrailRetention.RunState());
        }

        public void Append(Vec3 p)
        {
            if (Runs.Count == 0) NewRun();
            Runs[^1].Add(p); Originals[^1].Add(p);
            Enforce();
        }

        void Enforce()
        {
            if (Count <= Budget) return;
            for (int i = 0; i < Runs.Count; i++) { States[i].Count = Runs[i].Count; States[i].IsLive = i == Runs.Count - 1; }
            while (TrailRetention.Next(States, Budget) is TrailRetention.Step step)
            {
                var run = Runs[step.RunIndex];
                int n = step.End - step.Start;
                var kept = Rdp.SimplifyIndices(run.GetRange(step.Start, n), step.ToleranceMm);
                var keptPoints = kept.Select(k => run[step.Start + k]).ToList();
                run.RemoveRange(step.Start, n); run.InsertRange(step.Start, keptPoints);
                TrailRetention.Commit(States[step.RunIndex], step, n - kept.Count);
                Assert.Equal(run.Count, States[step.RunIndex].Count);
            }
            long cap = (long)Budget * TrailRetention.HardCapFactor;
            while (Count > cap)
            {
                int drop = (int)Math.Min(Runs[0].Count, Count - cap);
                Runs[0].RemoveRange(0, drop); TrailRetention.DropFront(States[0], drop); Pruned += drop;
                if (Runs[0].Count == 0) { Runs.RemoveAt(0); Originals.RemoveAt(0); States.RemoveAt(0); }
            }
        }
    }

    /// <summary>A vase wall: a circle per layer, points 3.75 mm apart (15 mm/s at 250 ms).</summary>
    static IEnumerable<Vec3> Vase(int layers, double radius = 50, double layerHeight = 3)
    {
        int perLayer = (int)Math.Round(2 * Math.PI * radius / 3.75);
        for (int l = 0; l < layers; l++)
            for (int k = 0; k < perLayer; k++)
            {
                double a = 2 * Math.PI * k / perLayer;
                yield return new Vec3(radius * Math.Cos(a), radius * Math.Sin(a), l * layerHeight);
            }
    }

    static double DistanceToPolyline(Vec3 p, IReadOnlyList<Vec3> line)
    {
        if (line.Count == 1) return PolylineOps.Dist(p, line[0]);
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            var a = line[i - 1]; var b = line[i];
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double len2 = dx * dx + dy * dy + dz * dz;
            double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / len2));
            best = Math.Min(best, PolylineOps.Dist(p, new Vec3(a.X + t * dx, a.Y + t * dy, a.Z + t * dz)));
        }
        return best;
    }

    [Fact]
    public void Bounds_AreTheRunningSumOfTheSteps()
    {
        Assert.Equal(0, TrailRetention.BoundMm(-1), 12);
        Assert.Equal(new[] { 0.2, 0.5, 1.0, 2.0, 5.0 },
                     Enumerable.Range(0, 5).Select(r => Math.Round(TrailRetention.BoundMm(r), 9)).ToArray());
    }

    [Fact]
    public void NeverPausedPrint_PastTheHardCap_KeepsItsFirstPoint()
    {
        // one run, 300 layers, about 25,000 observations: the old rule never compacted the
        // live run and discarded the first 20,000 of them
        var trail = new Trail();
        var all = Vase(300).ToList();
        Assert.True(all.Count > 20000);
        foreach (var p in all) trail.Append(p);

        Assert.Equal(0, trail.Pruned);
        Assert.True(trail.Count <= Budget, $"{trail.Count} points kept");
        Assert.Equal(all[0], trail.Runs[0][0]);
        // the newest LiveTailKeep observations are untouched
        var tail = trail.Runs[^1].Skip(trail.Runs[^1].Count - TrailRetention.LiveTailKeep).ToList();
        Assert.Equal(all.Skip(all.Count - TrailRetention.LiveTailKeep), tail);
    }

    [Fact]
    public void PausedPrint_KeepsEveryRun_WithinItsStatedBound()
    {
        // the 2026-09-23 shape: four pauses split the print into five runs
        var trail = new Trail();
        var all = Vase(150).ToList();
        int[] pausesAt = { 2673, 4612, 9000, 10500 };
        for (int i = 0; i < all.Count; i++)
        {
            if (pausesAt.Contains(i)) trail.NewRun();
            trail.Append(all[i]);
        }
        Assert.Equal(0, trail.Pruned);
        Assert.Equal(5, trail.Runs.Count);
        Assert.True(trail.Count <= Budget);
        for (int r = 0; r < trail.Runs.Count; r++)
        {
            Assert.Equal(trail.Originals[r][0], trail.Runs[r][0]);
            Assert.Equal(trail.Originals[r][^1], trail.Runs[r][^1]);
            double bound = trail.States[r].MaxBoundMm;
            Assert.True(bound <= 5.0 + 1e-9);
            foreach (var p in trail.Originals[r])
                Assert.True(DistanceToPolyline(p, trail.Runs[r]) <= bound + 1e-6,
                            $"run {r}: an observation is {DistanceToPolyline(p, trail.Runs[r]):0.###} mm off, bound {bound:0.##} mm");
        }
        // the oldest run degraded at least as far as the newest completed one
        Assert.True(trail.States[0].MaxBoundMm >= trail.States[3].MaxBoundMm);
    }

    [Fact]
    public void Planner_TakesTheLowestRungFirst_OldestFirstWithinARung()
    {
        var older = new TrailRetention.RunState { Count = 100 };
        older.Segments.Add(new TrailRetention.Segment(100, 1));
        var newer = new TrailRetention.RunState { Count = 100 };
        var live = new TrailRetention.RunState { Count = TrailRetention.LiveTailKeep + 50, IsLive = true };
        var step = TrailRetention.Next(new[] { older, newer, live }, 10);
        Assert.NotNull(step);
        Assert.Equal(1, step!.Value.RunIndex);               // rung -1 before rung 1
        Assert.Equal(0, step.Value.Rung);
        Assert.Equal((0, 100), (step.Value.Start, step.Value.End));

        TrailRetention.Commit(newer, step.Value, 60);
        Assert.Equal(40, newer.Count);
        Assert.Equal(new[] { (40, 0) }, newer.Segments.Select(s => (s.End, s.Rung)));

        // next: the live run's aged part [0, 50), still at rung -1
        var next = TrailRetention.Next(new[] { older, newer, live }, 10)!.Value;
        Assert.Equal((2, 0, 50, 0), (next.RunIndex, next.Start, next.End, next.Rung));
    }

    [Fact]
    public void Commit_SplitsAPartialStretchAndShiftsLaterBoundaries()
    {
        var run = new TrailRetention.RunState { Count = 300 };
        run.Segments.Add(new TrailRetention.Segment(100, 2));
        run.Segments.Add(new TrailRetention.Segment(200, 1));
        // a live tail cut the second stretch short: only [100, 150) is simplified to rung 2
        TrailRetention.Commit(run, new TrailRetention.Step(0, 100, 150, 2), 20);
        Assert.Equal(280, run.Count);
        // [0,100) and [100,130) are both rung 2 and merge; [130,180) keeps rung 1
        Assert.Equal(new[] { (130, 2), (180, 1) }, run.Segments.Select(s => (s.End, s.Rung)));

        TrailRetention.DropFront(run, 150);
        Assert.Equal(130, run.Count);
        Assert.Equal(new[] { (30, 1) }, run.Segments.Select(s => (s.End, s.Rung)));
    }

    [Fact]
    public void UnderBudget_NothingIsPlanned()
    {
        var run = new TrailRetention.RunState { Count = Budget };
        Assert.Null(TrailRetention.Next(new[] { run }, Budget));
    }
}
