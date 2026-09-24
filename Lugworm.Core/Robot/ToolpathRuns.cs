using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>What a drawn run of the intended path IS. The distinction is not cosmetic:
    /// a print run is where the file lays material, a travel run is where it repositions,
    /// and a renderer that shows only the first is showing a different program than the one
    /// that will run.</summary>
    public enum ToolpathRunKind
    {
        /// <summary>Material is laid along every segment of this run.</summary>
        Print,
        /// <summary>No material: a travel, a seam approach, a coast, a layer change.</summary>
        Travel,
    }

    /// <summary>One continuous polyline of the intended path, all of one kind.</summary>
    public sealed class ToolpathRun
    {
        public ToolpathRunKind Kind { get; }
        /// <summary>Machine frame, mm, in file order. Always at least two points: a run IS
        /// a sequence of segments, and a single point is not one.</summary>
        public IReadOnlyList<Vec3> Points { get; }

        public ToolpathRun(ToolpathRunKind kind, IReadOnlyList<Vec3> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count < 2)
                throw new ArgumentException("a run needs at least two points - one point is not a segment", nameof(points));
            Kind = kind;
            Points = points;
        }
    }

    /// <summary>Every run of one intended path, plus what was given up to draw it.</summary>
    public sealed class ToolpathRunSet
    {
        public IReadOnlyList<ToolpathRun> Runs { get; }
        /// <summary>Drawn points across every run, print and travel together.</summary>
        public int PointCount { get; }
        /// <summary>Non-null only when display simplification actually reduced detail; names
        /// the applied tolerance in mm so the viewport can report the reduction instead of
        /// hiding it. A viewer who is not told the path was thinned will read the thinned
        /// path as the file.</summary>
        public string? DetailNote { get; }

        public ToolpathRunSet(IReadOnlyList<ToolpathRun> runs, string? detailNote = null)
        {
            Runs = runs ?? throw new ArgumentNullException(nameof(runs));
            int count = 0;
            for (int i = 0; i < runs.Count; i++) count += runs[i].Points.Count;
            PointCount = count;
            DetailNote = detailNote;
        }

        public static readonly ToolpathRunSet Empty = new ToolpathRunSet(Array.Empty<ToolpathRun>(), null);
    }

    /// <summary>Builds the drawn runs of an intended path from the sampled sequence, and
    /// thins them for display within a stated geometric error.</summary>
    public static class ToolpathRunBuilder
    {
        /// <summary>Bisection steps used to find the smallest tolerance that meets the point
        /// budget. Sixteen halvings resolve the tolerance to maxDeviation/65536, far finer
        /// than any difference a viewport can show, and the count is fixed so the parse cost
        /// is bounded whatever the job.</summary>
        const int BisectionSteps = 16;

        /// <summary>Two run endpoints closer than this are the same point (the closed-ring
        /// test). Matches <see cref="Rdp"/>'s own closure tolerance.</summary>
        const double ClosureToleranceMm = 1e-6;

        /// <summary>Builds runs from SEGMENTS. <paramref name="extruding"/>[k] describes the
        /// segment ENDING at k, so segment k spans points[k-1] -&gt; points[k]. Every run
        /// carries both its starting point and its endpoint. A print/travel change SHARES
        /// the boundary point between the two runs, so no geometry is lost and the two keep
        /// separate styling. A <paramref name="breakBefore"/>[k] starts a new run without
        /// any connecting segment: unknown homing motion is absence, never a line.</summary>
        /// <remarks>The defect this replaces walked POINTS and skipped every point whose own
        /// flag was false. For flags false/true/true it emitted only the last two points, so
        /// the first segment of every extrusion run was lost, and a run made of a single
        /// segment vanished completely - one surviving point, and runs of one point were
        /// dropped. Travel, seam and coast moves were skipped outright, which is why the
        /// renderer showed a different program than the file (test-round row 10a.8).</remarks>
        public static ToolpathRunSet Build(IReadOnlyList<Vec3> points,
                                           IReadOnlyList<bool> extruding,
                                           IReadOnlyList<bool> breakBefore)
        {
            if (points == null || points.Count < 2) return ToolpathRunSet.Empty;
            var runs = new List<ToolpathRun>();
            List<Vec3>? current = null;
            var currentKind = ToolpathRunKind.Travel;

            // Walk SEGMENTS, not points. Segment k is points[k-1] -> points[k] and its kind
            // is extruding[k]; index 0 is a point with no segment before it and is therefore
            // never inspected here - it enters the first run as that run's start.
            for (int k = 1; k < points.Count; k++)
            {
                if (breakBefore != null && k < breakBefore.Count && breakBefore[k])
                {
                    // No commanded motion reaches points[k]. Close what is open and start
                    // over AT points[k]: the machine got there somehow, but not by anything
                    // this file describes, and a line would claim otherwise.
                    Flush(runs, current, currentKind);
                    current = null;
                    continue;
                }
                var kind = extruding != null && k < extruding.Count && extruding[k]
                    ? ToolpathRunKind.Print : ToolpathRunKind.Travel;
                if (current == null || kind != currentKind)
                {
                    Flush(runs, current, currentKind);
                    // The boundary point belongs to BOTH runs. Sharing it is what keeps the
                    // print and the travel that reaches it touching on screen while they
                    // keep their own styling; dropping it from one of them would open a
                    // one-segment hole exactly where the file has none.
                    current = new List<Vec3> { points[k - 1] };
                    currentKind = kind;
                }
                current.Add(points[k]);
            }
            Flush(runs, current, currentKind);
            return runs.Count == 0 ? ToolpathRunSet.Empty : new ToolpathRunSet(runs, null);
        }

        /// <summary>Closes an open run. A run of fewer than two points is not a polyline -
        /// it can only happen when a break lands on the last point, and a lone point says
        /// nothing a line could say.</summary>
        static void Flush(List<ToolpathRun> runs, List<Vec3>? current, ToolpathRunKind kind)
        {
            if (current != null && current.Count >= 2) runs.Add(new ToolpathRun(kind, current.ToArray()));
        }

        /// <summary>Bounded-error display simplification. Returns the set unchanged when it
        /// is already within budget. Never drops a run, never removes a run's endpoints,
        /// never shortcuts a corner beyond <paramref name="maxDeviationMm"/>.</summary>
        /// <remarks>
        /// <para>This replaces an every-Nth-point stride. A stride has no error bound at all:
        /// it deletes whichever vertices happen to fall between its steps, so a corner, a
        /// seam or a whole short feature disappears depending only on where the count landed.
        /// Ramer-Douglas-Peucker instead deletes a vertex only when it lies within
        /// <paramref name="maxDeviationMm"/> of the chord that replaces it, and keeps
        /// endpoints and corners by construction.</para>
        /// <para>The smallest tolerance that meets the budget is found by bisection over
        /// [0, maxDeviationMm], so a job that barely exceeds the budget is barely thinned.
        /// When even <paramref name="maxDeviationMm"/> cannot meet the budget the tolerance
        /// is NOT raised: the extra points are drawn and the shortfall is reported, because
        /// flattening the shape further to hit a number would misrepresent the file.</para>
        /// </remarks>
        public static ToolpathRunSet Simplify(ToolpathRunSet set, int pointBudget, double maxDeviationMm)
        {
            if (set == null) return ToolpathRunSet.Empty;
            if (pointBudget <= 0 || set.PointCount <= pointBudget) return set;
            // No error budget means no licence to move anything: the detail stands.
            if (!(maxDeviationMm > 0) || double.IsNaN(maxDeviationMm)) return set;

            var atMax = Apply(set, maxDeviationMm);
            if (CountOf(atMax) > pointBudget)
                return new ToolpathRunSet(atMax,
                    Note(maxDeviationMm, CountOf(atMax), set.PointCount, pointBudget, met: false));

            double lo = 0.0, hi = maxDeviationMm;
            for (int i = 0; i < BisectionSteps; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (CountOf(Apply(set, mid)) <= pointBudget) hi = mid; else lo = mid;
            }
            var result = Apply(set, hi);
            int count = CountOf(result);
            // hi always met the budget (it started at a tolerance that did, and only ever
            // moved to a mid that did), so this branch is the reduced one by construction.
            return new ToolpathRunSet(result, count < set.PointCount
                ? Note(hi, count, set.PointCount, pointBudget, met: true) : null);
        }

        static int CountOf(IReadOnlyList<ToolpathRun> runs)
        {
            int n = 0;
            for (int i = 0; i < runs.Count; i++) n += runs[i].Points.Count;
            return n;
        }

        static string Note(double tolerance, int drawn, int original, int budget, bool met)
        {
            var inv = CultureInfo.InvariantCulture;
            string head = "display detail reduced: " + tolerance.ToString("0.####", inv)
                        + " mm tolerance, " + drawn.ToString(inv) + " of "
                        + original.ToString(inv) + " points drawn";
            return met
                ? head + " (budget " + budget.ToString(inv) + ")"
                : head + " - still above the " + budget.ToString(inv)
                       + " point budget, kept at the maximum allowed error rather than thinned further";
        }

        /// <summary>Every run simplified at one tolerance. A run is only replaced when the
        /// result is shorter AND still starts and ends where it did; anything else keeps the
        /// original, so no run is ever dropped, reversed or re-seamed by the thinning.</summary>
        static List<ToolpathRun> Apply(ToolpathRunSet set, double tolerance)
        {
            var outp = new List<ToolpathRun>(set.Runs.Count);
            for (int i = 0; i < set.Runs.Count; i++)
            {
                var run = set.Runs[i];
                var thinned = SimplifyRun(run.Points, tolerance);
                outp.Add(ReferenceEquals(thinned, run.Points) ? run : new ToolpathRun(run.Kind, thinned));
            }
            return outp;
        }

        static IReadOnlyList<Vec3> SimplifyRun(IReadOnlyList<Vec3> points, double tolerance)
        {
            if (points.Count <= 2 || !(tolerance > 0)) return points;
            if (!PolylineOps.IsClosed(points, ClosureToleranceMm))
                return Guard(points, Rdp.Simplify(points, tolerance));

            // A closed run is split at its midpoint and each half thinned as an OPEN
            // polyline. Rdp's own closed-ring path rotates the ring to start at the vertex
            // farthest from the centroid, which would move the seam - and the seam of a
            // print run is the point it shares with the travel run that reaches it, so
            // moving it would tear a visible hole between two runs that touch in the file.
            int mid = points.Count / 2;
            var head = Slice(points, 0, mid);
            var tail = Slice(points, mid, points.Count - 1);
            var thinHead = Guard(head, Rdp.Simplify(head, tolerance));
            var thinTail = Guard(tail, Rdp.Simplify(tail, tolerance));
            if (ReferenceEquals(thinHead, head) && ReferenceEquals(thinTail, tail)) return points;
            var joined = new List<Vec3>(thinHead.Count + thinTail.Count - 1);
            for (int i = 0; i < thinHead.Count; i++) joined.Add(thinHead[i]);
            for (int i = 1; i < thinTail.Count; i++) joined.Add(thinTail[i]);
            return joined;
        }

        /// <summary>points[from..to] inclusive.</summary>
        static List<Vec3> Slice(IReadOnlyList<Vec3> points, int from, int to)
        {
            var outp = new List<Vec3>(to - from + 1);
            for (int i = from; i <= to; i++) outp.Add(points[i]);
            return outp;
        }

        /// <summary>Accepts a thinned polyline only when it is genuinely shorter and still
        /// begins and ends on the original's endpoints. Rdp returns an EMPTY list for a
        /// degenerate ring and re-seams a closed one; either would silently delete drawn
        /// geometry, so the original is kept instead.</summary>
        static IReadOnlyList<Vec3> Guard(IReadOnlyList<Vec3> original, List<Vec3> thinned)
        {
            if (thinned.Count < 2 || thinned.Count >= original.Count) return original;
            if (PolylineOps.Dist(thinned[0], original[0]) > ClosureToleranceMm) return original;
            if (PolylineOps.Dist(thinned[thinned.Count - 1], original[original.Count - 1]) > ClosureToleranceMm)
                return original;
            return thinned;
        }
    }
}
