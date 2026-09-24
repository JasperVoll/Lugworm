using System;
using System.Collections.Generic;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    public static class Spiral
    {
        /// <summary>Nested closed rings (outer-&gt;inner) -&gt; ONE inward spiral path with
        /// bounded nearest-point handoffs (continuous =&gt; no in-layer retraction).
        /// Test-only convenience (kept internal, H8): production infill goes through
        /// <see cref="InwardPaths"/>.</summary>
        internal static List<Vec3> Connected(IReadOnlyList<IReadOnlyList<Vec3>> rings)
            => Chain.Rings(rings);

        /// <summary>Infill entry point: one nested stack becomes ONE true inward
        /// spiral; ring sets with several islands at one nesting
        /// depth cannot be spiralled without extruding across the gap, so they degrade to
        /// independent rings whose seams line up with <paramref name="anchor"/>.</summary>
        public static List<List<Vec3>> InwardPaths(IReadOnlyList<IReadOnlyList<Vec3>> rings, Vec3? anchor = null)
            => InwardPaths(rings, anchor, out _);

        /// <summary>Infill entry point with a diagnostic when independent rings are used.</summary>
        public static List<List<Vec3>> InwardPaths(IReadOnlyList<IReadOnlyList<Vec3>> rings,
                                                   Vec3? anchor, out string? diagnostic)
        {
            diagnostic = null;
            var clean = CleanRings(rings);
            if (clean.Count == 0) return new List<List<Vec3>>();
            var chain = NestedChain(clean, out diagnostic);
            if (chain == null) return Chain.RingPaths(rings, anchor);
            var sp = Blend(chain, anchor);
            return sp.Count >= 2 ? new List<List<Vec3>> { sp } : new List<List<Vec3>>();
        }

        /// <summary>The blend itself, over rings that are already cleaned (CCW, no closing
        /// duplicate) and already ordered outermost-first.</summary>
        static List<Vec3> Blend(List<List<Vec3>> clean, Vec3? anchor)
        {
            var path = new List<Vec3>();
            if (clean.Count == 0) return path;

            var split = new List<List<Vec3>>(clean.Count);
            var outerCentroid = SeamAnchor.Centroid(clean[0]);
            Vec3 refPt = anchor ?? new Vec3(outerCentroid.X + 1.0, outerCentroid.Y, outerCentroid.Z);
            double angle = Math.Atan2(refPt.Y - outerCentroid.Y, refPt.X - outerCentroid.X);
            var outerHit = SeamAnchor.RayHit(clean[0], outerCentroid, angle)
                         ?? SeamAnchor.ClosestOnRing(clean[0], refPt);
            split.Add(SeamAnchor.SplitAt(clean[0], outerHit.SegmentIndex, outerHit.T));
            for (int i = 1; i < clean.Count; i++)
            {
                var hit = SeamAnchor.ClosestOnRing(clean[i], split[i - 1][0]);
                split.Add(SeamAnchor.SplitAt(clean[i], hit.SegmentIndex, hit.T));
            }

            for (int i = 0; i < split.Count - 1; i++)
            {
                var a = split[i];
                var b = split[i + 1];
                var ca = PolylineOps.CumulativeLengths(a);
                var cb = PolylineOps.CumulativeLengths(b);
                int n = Math.Max(32, Math.Max(a.Count, b.Count));
                int ia = 0, ib = 0;
                for (int k = 0; k < n; k++)
                {
                    double t = (double)k / n;
                    var pa = PointAtFraction(a, ca, t, ref ia);
                    var pb = PointAtFraction(b, cb, t, ref ib);
                    path.Add(PolylineOps.Lerp(pa, pb, t));
                }
            }
            // innermost ring: full closed trace
            path.AddRange(split[split.Count - 1]);
            return path;
        }

        static List<List<Vec3>> CleanRings(IReadOnlyList<IReadOnlyList<Vec3>> rings)
        {
            var clean = new List<List<Vec3>>();
            foreach (var r in rings)
            {
                if (r == null || r.Count < 3) continue;
                var ring = new List<Vec3>(r);
                if (PolylineOps.IsClosed(ring, 1e-6)) ring.RemoveAt(ring.Count - 1);
                if (ring.Count < 3) continue;
                if (PolylineOps.SignedAreaXy(ring) < 0) ring.Reverse();   // uniform CCW
                clean.Add(ring);
            }
            return clean;
        }

        static Vec3 PointAtFraction(List<Vec3> closedRing, double[] cum, double t, ref int seg)
        {
            double total = cum[cum.Length - 1];
            if (total <= 1e-12) return closedRing[0];
            double s = t * total;
            while (seg < cum.Length - 2 && cum[seg + 1] < s) seg++;
            double len = cum[seg + 1] - cum[seg];
            double u = len > 1e-12 ? (s - cum[seg]) / len : 0.0;
            return PolylineOps.Lerp(closedRing[seg], closedRing[seg + 1], u);
        }

        /// <summary>The rings ordered outermost-first along their containment chain, or
        /// null (with <paramref name="diagnostic"/> set) when they are not one chain.
        /// <para>Nesting is decided by real containment, never by bounding boxes: each
        /// ring's nesting depth is the number of LARGER-area rings that contain its area
        /// centroid (<see cref="Nesting.ContainsXy"/> on <see cref="SeamAnchor.Centroid"/>),
        /// which is the containment forest written as one number per ring. The stack can
        /// be spiralled exactly when that forest is a single chain - one ring at every
        /// depth - and the chain is then depth 0, 1, 2, ... outward to inward.</para>
        /// <para>Two failures of the old bounding-box test disappear with it. An annulus:
        /// <see cref="Offset.ConcentricRings"/> offsets both skins on every iteration and so
        /// returns outer0, hole0, outer1, hole1, ..., which failed on the first hole ring;
        /// by depth those same rings sort into outer0..outerN, holeM..hole0 (a hole ring
        /// always encloses less area than any outer ring of the same region, so the ranking
        /// cannot mix them up) - one chain spiralling from the outer skin to the hole. And a
        /// small island whose bounding box merely sits inside a bigger ring's box - in the
        /// notch of an L, say - used to pass as nested, and the spiral then blended across
        /// empty space; by containment it is a second island at the same depth.</para>
        /// <para>The widest step in an annulus chain is the last outer ring to the first
        /// hole ring, and it crosses at most two spacings of SOLID material - erosion stops
        /// once the band is thinner than that - so the blend underfills at worst, never
        /// extrudes over a void.</para></summary>
        internal static List<List<Vec3>>? NestedChain(List<List<Vec3>> rings, out string? diagnostic)
        {
            diagnostic = null;
            int n = rings.Count;
            var chain = new List<List<Vec3>>(n);
            if (n == 0) return chain;

            var areas = new double[n];
            var probes = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                areas[i] = Math.Abs(PolylineOps.SignedAreaXy(rings[i]));
                probes[i] = SeamAnchor.Centroid(rings[i]);
            }

            var depths = new int[n];
            var perDepth = new int[n];
            for (int i = 0; i < n; i++)
            {
                int depth = 0;
                for (int j = 0; j < n; j++)
                    if (j != i && areas[j] > areas[i] && Nesting.ContainsXy(rings[j], probes[i])) depth++;
                depths[i] = depth;
                perDepth[depth]++;
            }

            // a single chain is exactly one ring per depth; anything else has islands, and
            // the shallowest crowded depth is the one worth naming. (A depth with no ring
            // forces another to hold two, so scanning for >1 finds every non-chain.)
            for (int d = 0; d < n; d++)
                if (perDepth[d] > 1)
                {
                    diagnostic = $"rings not strictly nested ({perDepth[d]} islands at depth {d}) - independent rings";
                    return null;
                }

            for (int d = 0; d < n; d++) chain.Add(null!);
            for (int i = 0; i < n; i++) chain[depths[i]] = rings[i];
            return chain;
        }
    }
}
