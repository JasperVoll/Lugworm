using System.Collections.Generic;
using Clipper2Lib;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Clipping
{
    /// <summary>Vec3 (XY) &lt;-&gt; Clipper2 double-precision paths.
    /// Precision 4 == 0.1 micron, matching the Python _PCSCALE = 1e4.</summary>
    internal static class ClipperConvert
    {
        public const int Precision = 4;

        static PathD ToPathD(IReadOnlyList<Vec3> poly)
        {
            int n = poly.Count;
            if (n > 1 && PolylineOps.Dist(poly[0], poly[n - 1]) <= 1e-6) n--;
            var p = new PathD(n + 1);
            for (int i = 0; i < n; i++) p.Add(new PointD(poly[i].X, poly[i].Y));
            if (n > 0) p.Add(new PointD(poly[0].X, poly[0].Y));
            return p;
        }

        public static PathsD ToPathsD(IReadOnlyList<IReadOnlyList<Vec3>> polys, int minPts)
        {
            var ps = new PathsD();
            foreach (var poly in polys)
            {
                if (poly == null || poly.Count < minPts) continue;
                int distinct = 0;
                for (int i = 0; i < poly.Count; i++)
                {
                    bool seen = false;
                    for (int j = 0; j < i; j++)
                        if (PolylineOps.Dist(poly[i], poly[j]) <= 1e-9) { seen = true; break; }
                    if (!seen) distinct++;
                }
                if (distinct >= 3) ps.Add(ToPathD(poly));
            }
            return ps;
        }

        public static List<Vec3> FromPathD(PathD path, double z)
        {
            var outp = new List<Vec3>(path.Count + 1);
            foreach (var pt in path) outp.Add(new Vec3(pt.x, pt.y, z));
            if (outp.Count > 1 && PolylineOps.Dist(outp[0], outp[outp.Count - 1]) > 1e-6)
                outp.Add(outp[0]);
            return outp;
        }
    }
}
