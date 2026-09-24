using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>Connected Fermat spiral over nested concentric rings (Zhao et al. 2016,
    /// specialised to offset rings): all rings are cut open inside a narrow seam
    /// corridor; EVEN rings carry the inward arm, ODD rings the outward arm, with the
    /// radial bridges running inside the corridor where every ring is open - so the
    /// two arms interleave without crossing any ring. The defining Fermat property
    /// holds: the path START and END sit next to each other on the outermost rings.</summary>
    public static class Fermat
    {
        /// <param name="rings">one nested stack of closed rings (e.g. from
        /// Offset.ConcentricRings); they are re-ordered outermost-first by nesting
        /// depth, so an annulus' interleaved outer/hole rings are accepted as given</param>
        /// <param name="spacing">ring spacing, mm - sets the seam-corridor width</param>
        /// <param name="seamRef">optional seam reference (e.g. the wall seam): every
        /// ring is cut at the exact segment point facing it, so the path starts and ends next
        /// to the boundary seam. Default: far along +X from the outer centroid.</param>
        public static List<Vec3> Spiral(IReadOnlyList<IReadOnlyList<Vec3>> rings, double spacing = 0, Vec3? seamRef = null)
        {
            var spans = PathSpans(rings, spacing, seamRef);
            var result = new List<Vec3>();
            foreach (var span in spans) result.AddRange(span);
            return result;
        }

        /// <summary>Infill entry point: one nested stack becomes ONE continuous
        /// Fermat path (the spans of <see cref="PathSpans"/> joined by their implicit
        /// corridor bridges); ring sets with several islands at one nesting depth cannot
        /// be bridged without extruding across the gap, so they degrade to independent
        /// rings whose seams line up with <paramref name="seamRef"/>.</summary>
        public static List<List<Vec3>> Paths(IReadOnlyList<IReadOnlyList<Vec3>> rings, double spacing = 0, Vec3? seamRef = null)
            => Paths(rings, spacing, seamRef, out _);

        /// <summary>Infill entry point with a diagnostic when independent rings are used.</summary>
        public static List<List<Vec3>> Paths(IReadOnlyList<IReadOnlyList<Vec3>> rings, double spacing,
                                             Vec3? seamRef, out string? diagnostic)
        {
            diagnostic = null;
            var clean = new List<List<Vec3>>();
            foreach (var r in rings)
                if (r != null && r.Count >= 3) clean.Add(r.Take(r.Count - 1).ToList());
            if (clean.Count == 0) return new List<List<Vec3>>();
            if (Lugworm.Core.Paths.Spiral.NestedChain(clean, out diagnostic) == null)
                return Chain.RingPaths(rings, seamRef);
            var one = Spiral(rings, spacing, seamRef);
            return one.Count >= 2 ? new List<List<Vec3>> { one } : new List<List<Vec3>>();
        }

        static List<List<Vec3>> PathSpans(IReadOnlyList<IReadOnlyList<Vec3>> rings, double spacing = 0, Vec3? seamRef = null)
        {
            var clean = new List<List<Vec3>>();
            foreach (var r in rings)
            {
                if (r == null || r.Count < 3 || !PolylineOps.IsClosed(r, 1e-3))
                    throw new ArgumentException("Fermat spiral requires closed rings", nameof(rings));
                var ring = r.Take(r.Count - 1).ToList();
                var distinct = new HashSet<string>();
                foreach (var p in ring) distinct.Add(p.X.ToString("R", CultureInfo.InvariantCulture) + ":" + p.Y.ToString("R", CultureInfo.InvariantCulture));
                if (distinct.Count < 3)
                    throw new ArgumentException("Fermat spiral requires nondegenerate rings", nameof(rings));
                if (PolylineOps.SignedAreaXy(ring) < 0) ring.Reverse();   // uniform CCW
                clean.Add(ring);
            }
            if (clean.Count == 0) return new List<List<Vec3>>();

            // the corridor construction needs one nested stack, and it has to walk that
            // stack outward-to-inward: nesting depth orders the rings (an annulus arrives
            // interleaved outer0, hole0, outer1, hole1, ...), and several islands at one
            // depth degrade to independent rings
            var chain = Lugworm.Core.Paths.Spiral.NestedChain(clean, out _);

            // seam reference: the caller's seam (wall end) or far along +X from the
            // outermost ring's centroid
            if (!seamRef.HasValue)
            {
                var c0 = SeamAnchor.Centroid((chain ?? clean)[0]);
                seamRef = new Vec3(c0.X + 1e6, c0.Y, c0.Z);
            }
            if (chain == null) return Chain.RingPaths(rings, seamRef);
            if (chain.Count == 1)
                return new List<List<Vec3>> { SplitAtSeam(chain[0], seamRef.Value) };

            if (spacing <= 1e-9) spacing = EstimateSpacing(chain);
            double g = Math.Max(spacing, 1e-3);

            // open every ring in the corridor: even rings (inward arm) get a ±1g
            // window, odd rings (outward arm) ±2g so the inward bridges pass through
            var arcs = new List<List<Vec3>>();
            for (int i = 0; i < chain.Count; i++)
            {
                double w = i % 2 == 0 ? g : 2 * g;
                arcs.Add(OpenAtSeam(chain[i], seamRef.Value, w, w));
            }

            // bridges are implicit: consecutive arc endpoints all sit inside the seam
            // corridor, so the straight connection between them crosses only open gaps
            var result = new List<List<Vec3>>();
            for (int i = 0; i < arcs.Count; i += 2) result.Add(arcs[i]);          // inward arm
            for (int i = OddDeepest(arcs.Count); i >= 1; i -= 2) result.Add(arcs[i]);   // outward arm
            return result;
        }

        static int OddDeepest(int count) => (count - 1) % 2 == 1 ? count - 1 : count - 2;

        /// <summary>Cut the ring open around its exact segment point facing <paramref name="seamRef"/>:
        /// remove the arc [−wBefore, +wAfter] (arc lengths) around the seam and return
        /// the remaining open polyline, starting just after the seam (traversal keeps
        /// the ring's CCW orientation).</summary>
        internal static List<Vec3> OpenAtSeam(IReadOnlyList<Vec3> ring, Vec3 seamRef,
                                              double wBefore, double wAfter)
        {
            var split = SplitAtSeam(ring, seamRef);
            split.RemoveAt(split.Count - 1);
            var pts = split;
            int n = pts.Count;
            // cumulative arc lengths starting at the exact inserted seam point
            var cum = new double[n + 1];
            for (int i = 0; i < n; i++)
                cum[i + 1] = cum[i] + Math.Sqrt(PolylineOps.Dist2Xy(pts[i], pts[(i + 1) % n]));
            double total = cum[n];
            double w1 = Math.Min(wAfter, total * 0.2);
            double w2 = Math.Min(wBefore, total * 0.2);

            // keep the arc from wAfter … total−wBefore (measured from the seam)
            var outp = new List<Vec3> { PointAtArc(pts, cum, w1) };
            for (int i = 0; i < n; i++)
            {
                if (cum[i] > w1 && cum[i] < total - w2) outp.Add(pts[i]);
                // also the wrap point (index n == index 0 again) is excluded by design
            }
            outp.Add(PointAtArc(pts, cum, total - w2));
            return outp;
        }

        static List<Vec3> SplitAtSeam(IReadOnlyList<Vec3> ring, Vec3 seamRef)
        {
            var centroid = SeamAnchor.Centroid(ring);
            double angle = Math.Atan2(seamRef.Y - centroid.Y, seamRef.X - centroid.X);
            var hit = SeamAnchor.RayHit(ring, centroid, angle)
                   ?? SeamAnchor.ClosestOnRing(ring, seamRef);
            return SeamAnchor.SplitAt(ring, hit.SegmentIndex, hit.T);
        }

        static Vec3 PointAtArc(List<Vec3> pts, double[] cum, double s)
        {
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                if (cum[i + 1] >= s)
                {
                    double seg = cum[i + 1] - cum[i];
                    double t = seg > 1e-12 ? (s - cum[i]) / seg : 0;
                    var a = pts[i];
                    var b = pts[(i + 1) % n];
                    return new Vec3(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z));
                }
            }
            return pts[0];
        }

        static double EstimateSpacing(List<List<Vec3>> rings)
        {
            // median nearest-distance between consecutive ring start points
            var ds = new List<double>();
            for (int i = 1; i < rings.Count; i++)
            {
                double bestD = double.MaxValue;
                foreach (var p in rings[i])
                    bestD = Math.Min(bestD, PolylineOps.Dist2Xy(p, rings[i - 1][0]));
                ds.Add(Math.Sqrt(bestD));
            }
            ds.Sort();
            return ds.Count > 0 ? Math.Max(ds[ds.Count / 2], 0.5) : 2.0;
        }
    }
}
