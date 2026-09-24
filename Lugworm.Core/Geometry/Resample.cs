using System;
using System.Collections.Generic;

namespace Lugworm.Core.Geometry
{
    public static class Resample
    {
        /// <summary>Resample a polyline to ~uniform spacing `step` (3D). Keeps ends.</summary>
        public static List<Vec3> ByLength(IReadOnlyList<Vec3> pts, double step)
        {
            if (step <= 0 || pts.Count < 2) return new List<Vec3>(pts);
            var cl = PolylineOps.CumulativeLengths(pts);
            double total = cl[cl.Length - 1];
            if (total <= 1e-9) return new List<Vec3>(pts);
            int n = Math.Max(1, (int)Math.Round(total / step));
            var outp = new List<Vec3>(n + 1);
            int j = 0;
            for (int k = 0; k <= n; k++)
            {
                double target = total * k / n;
                while (j < cl.Length - 2 && cl[j + 1] < target) j++;
                double seg = cl[j + 1] - cl[j];
                double t = seg <= 1e-12 ? 0.0 : (target - cl[j]) / seg;
                outp.Add(PolylineOps.Lerp(pts[j], pts[j + 1], t));
            }
            return outp;
        }
    }
}
