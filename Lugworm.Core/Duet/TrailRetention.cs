using System;
using System.Collections.Generic;

namespace Lugworm.Core.Duet
{
    /// <summary>Bounded retention of the Live View trail: when the observed trail outgrows
    /// its point budget, older stretches are simplified in steps of increasing tolerance
    /// instead of the oldest points being thrown away, so a long print keeps its first
    /// layers with a stated error.</summary>
    /// <remarks>
    /// <para>5.0.0. The old rule compacted each completed run once at 0.2 mm and then
    /// discarded from the front. On curved walls at 250 ms polls the points sit about
    /// 3.75 mm apart, 0.2 mm frees almost nothing, and a 2026-09-23 print lost everything
    /// before its first pause 45 minutes in.</para>
    /// <para>Every run carries compaction <see cref="Segment"/>s: consecutive stretches and
    /// the ladder rung each has reached. Simplifying already simplified points adds its
    /// error to the earlier one, so a stretch at rung r is within <see cref="BoundMm"/>(r),
    /// the running sum of <see cref="StepLadderMm"/>, of what was observed.</para>
    /// <para>The planner returns ONE step at a time. The caller simplifies that range,
    /// reports how many points it removed through <see cref="Commit"/>, which rebases the
    /// run's segment boundaries, and asks again. No index is read after it went stale.</para>
    /// </remarks>
    public static class TrailRetention
    {
        /// <summary>Tolerance of each successive simplification, mm. The running sums are
        /// 0.2, 0.5, 1, 2 and 5 mm.</summary>
        public static readonly double[] StepLadderMm = { 0.2, 0.3, 0.5, 1.0, 3.0 };

        /// <summary>The newest points of the live run stay at full detail: the part the
        /// operator is watching. 600 is about 2.5 minutes at 250 ms polls.</summary>
        public const int LiveTailKeep = 600;

        /// <summary>A stretch shorter than this is not worth a simplification pass.</summary>
        public const int MinRangePoints = 32;

        /// <summary>When every stretch has reached the top rung the trail may still grow to
        /// this multiple of the budget before the oldest points are discarded. Bounds memory
        /// and draw cost on a pathological multi-hour print.</summary>
        public const int HardCapFactor = 4;

        /// <summary>Accumulated error bound of a stretch at <paramref name="rung"/>, mm;
        /// 0 for a stretch never simplified (rung -1).</summary>
        public static double BoundMm(int rung)
        {
            double sum = 0;
            for (int i = 0; i <= rung && i < StepLadderMm.Length; i++) sum += StepLadderMm[i];
            return sum;
        }

        /// <summary>A compacted stretch: it ends before point <see cref="End"/> (exclusive)
        /// and starts where the previous segment ended.</summary>
        public readonly struct Segment
        {
            public readonly int End;
            public readonly int Rung;
            public Segment(int end, int rung) { End = end; Rung = rung; }
        }

        /// <summary>One run's retention state. <see cref="Count"/> must be the run's
        /// current point count when the planner is asked.</summary>
        public sealed class RunState
        {
            public int Count;
            /// <summary>The run still being appended to.</summary>
            public bool IsLive;
            public readonly List<Segment> Segments = new List<Segment>();

            /// <summary>Largest accumulated error bound of any stretch in this run, mm.</summary>
            public double MaxBoundMm
            {
                get
                {
                    double max = 0;
                    foreach (var s in Segments) max = Math.Max(max, BoundMm(s.Rung));
                    return max;
                }
            }
        }

        /// <summary>The next simplification: points [Start, End) of run RunIndex, simplified
        /// with ToleranceMm, reach rung Rung.</summary>
        public readonly struct Step
        {
            public readonly int RunIndex, Start, End, Rung;
            public double ToleranceMm => StepLadderMm[Rung];
            public Step(int runIndex, int start, int end, int rung)
            { RunIndex = runIndex; Start = start; End = end; Rung = rung; }
        }

        /// <summary>The next step while the runs hold more than <paramref name="budget"/>
        /// points, or null when they fit or nothing is left to simplify.</summary>
        /// <remarks>The lowest rung goes first, oldest stretch first within a rung. So every
        /// stretch reaches a rung before any goes further, early layers degrade evenly, and
        /// points that newly left the live tail climb to where the rest already is before
        /// anyone is simplified again.</remarks>
        public static Step? Next(IReadOnlyList<RunState> runs, int budget)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            long total = 0;
            foreach (var r in runs) total += r.Count;
            if (total <= budget) return null;

            int bestRung = int.MaxValue;
            Step? best = null;
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                int eligibleEnd = run.IsLive ? run.Count - LiveTailKeep : run.Count;
                int start = 0;
                foreach (var seg in run.Segments)
                {
                    int end = Math.Min(seg.End, eligibleEnd);
                    Consider(i, start, end, seg.Rung);
                    start = seg.End;
                }
                Consider(i, start, eligibleEnd, -1);
            }
            return best;

            void Consider(int runIndex, int start, int end, int rung)
            {
                if (end - start < MinRangePoints || rung + 1 >= StepLadderMm.Length) return;
                // strictly lower only: the first (oldest) stretch of a rung wins
                if (rung >= bestRung) return;
                bestRung = rung;
                best = new Step(runIndex, start, end, rung + 1);
            }
        }

        /// <summary>Records that <paramref name="step"/> was applied and removed
        /// <paramref name="removed"/> points from inside it: the stretch now holds the
        /// step's rung, every later boundary shifts, and neighbours of equal rung merge.</summary>
        public static void Commit(RunState run, Step step, int removed)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            // simplification keeps both ends of the stretch
            if (removed < 0 || removed > Math.Max(0, step.End - step.Start - 2))
                throw new ArgumentOutOfRangeException(nameof(removed));
            var rebuilt = new List<Segment>(run.Segments.Count + 2);
            int start = 0;
            bool placed = false;
            foreach (var seg in run.Segments)
            {
                // a segment wholly before the step, the part of one before it, the step itself,
                // the part of one after it, and segments wholly after it (shifted)
                if (seg.End <= step.Start) { rebuilt.Add(seg); start = seg.End; continue; }
                if (start < step.Start) rebuilt.Add(new Segment(step.Start, seg.Rung));
                if (!placed) { rebuilt.Add(new Segment(step.End - removed, step.Rung)); placed = true; }
                if (seg.End > step.End) rebuilt.Add(new Segment(seg.End - removed, seg.Rung));
                start = seg.End;
            }
            if (!placed)
            {
                // the step lay in the uncompacted part after the last segment
                if (start < step.Start) rebuilt.Add(new Segment(step.Start, -1));
                rebuilt.Add(new Segment(step.End - removed, step.Rung));
            }
            run.Segments.Clear();
            foreach (var seg in rebuilt)
            {
                int n = run.Segments.Count;
                if (n > 0 && run.Segments[n - 1].Rung == seg.Rung) run.Segments[n - 1] = seg;
                else run.Segments.Add(seg);
            }
            // a trailing stretch at rung -1 is the implicit uncompacted remainder
            while (run.Segments.Count > 0 && run.Segments[run.Segments.Count - 1].Rung < 0)
                run.Segments.RemoveAt(run.Segments.Count - 1);
            run.Count -= removed;
        }

        /// <summary>Records that the oldest <paramref name="count"/> points of the run were
        /// discarded (past the hard cap only).</summary>
        public static void DropFront(RunState run, int count)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (count <= 0) return;
            var kept = new List<Segment>(run.Segments.Count);
            foreach (var seg in run.Segments)
                if (seg.End - count > 0) kept.Add(new Segment(seg.End - count, seg.Rung));
            run.Segments.Clear();
            run.Segments.AddRange(kept);
            run.Count = Math.Max(0, run.Count - count);
        }
    }
}
