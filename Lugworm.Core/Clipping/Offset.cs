using System;
using System.Collections.Generic;
using Clipper2Lib;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Clipping
{
    public static class Offset
    {
        /// <summary>Thinning tolerance applied to an offset ring, in mm: two orders of
        /// magnitude below a clay layer and far below the slice tolerance, so the ring
        /// keeps its shape while shedding vertices no geometry asked for.</summary>
        /// <remarks>Clipper's round joins tessellate every convex corner at the
        /// working precision (4 == 0.1 um), and an iterated offset INHERITS the
        /// previous ring's vertices on top of the fresh arc steps, so a hole side
        /// doubles its count every iteration (81 -&gt; 481 -&gt; 1117 -&gt; 2090 on an
        /// r20 hole at spacing 3). Thinning the ring after each offset bounds the
        /// count at what the ring's own curvature warrants regardless of iteration -
        /// see the note in docs/ARCHI.md section 10.</remarks>
        public static double SimplifyTolerance(double delta)
            => Math.Min(0.02, Math.Abs(delta) / 200.0);

        /// <summary>Offset closed XY polygons by `delta` mm (negative = inward).
        /// Returns closed polylines at height z, thinned to <paramref name="simplifyTol"/>.</summary>
        /// <param name="simplifyTol">Thinning tolerance in mm. Negative (the default)
        /// derives it from <paramref name="delta"/> via <see cref="SimplifyTolerance"/>;
        /// zero returns Clipper's raw ring, which is what the tests measure against.</param>
        public static List<List<Vec3>> Polygons(IReadOnlyList<IReadOnlyList<Vec3>> polys,
                                                double delta, double z = 0.0,
                                                double simplifyTol = -1.0)
        {
            var result = new List<List<Vec3>>();
            var subj = ClipperConvert.ToPathsD(polys, minPts: 3);
            if (subj.Count == 0) return result;
            // via ClipperCompat: another plugin's Clipper2Lib version may be loaded -
            // the direct call throws MissingMethodException when signatures moved
            var sol = ClipperCompat.InflatePaths(subj, delta, 2.0, ClipperConvert.Precision);
            if (simplifyTol < 0.0) simplifyTol = SimplifyTolerance(delta);
            foreach (var path in sol)
            {
                if (path.Count < 3) continue;
                var ring = ClipperConvert.FromPathD(path, z);
                if (simplifyTol <= 0.0) { result.Add(ring); continue; }
                // Rdp re-starts a closed ring at its farthest-from-centroid vertex.
                // Harmless here: LayerBuilder re-anchors every wall through SeamAnchor
                // and Spiral/Fermat split rings at exact projected points, so no caller
                // reads the ring's start vertex. An empty result means Rdp found fewer
                // than three distinct vertices - an unprintable sliver, so drop it.
                var thinned = Rdp.Simplify(ring, simplifyTol);
                if (thinned.Count >= 4) result.Add(thinned);
            }
            return result;
        }

        /// <summary>Iteratively offset inward by `spacing` -> nested rings (outer first).</summary>
        public static List<List<Vec3>> ConcentricRings(IReadOnlyList<IReadOnlyList<Vec3>> boundary,
                                                       double spacing, double z = 0.0, int maxRings = 500)
        {
            var rings = new List<List<Vec3>>();
            // nesting-aware orientation (outer CCW / hole CW) so inward offsets grow
            // holes into the material instead of shrinking them
            var current = Nesting.Orient(boundary);
            rings.AddRange(current);
            for (int k = 0; k < maxRings; k++)
            {
                var nxt = Polygons(current, -Math.Abs(spacing), z);
                nxt.RemoveAll(r => r.Count < 3 ||
                                   Math.Abs(PolylineOps.SignedAreaXy(r)) <= spacing * spacing);
                if (nxt.Count == 0) break;
                rings.AddRange(nxt);
                current = nxt;
            }
            return rings;
        }
    }
}
