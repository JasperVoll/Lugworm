using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Layer height schedules (planar + adaptive). Port of clay_core_v2 §8.</summary>
    public static class Levels
    {
        public static List<double> Planar(double zmin, double zmax, double first, double layerH)
        {
            if (!(layerH > 0))
                throw new ArgumentOutOfRangeException(nameof(layerH),
                    $"layer height must be > 0 (got {layerH})");
            double z0 = zmin + (first > 0 ? first : 0.5 * layerH);
            var levels = new List<double>();
            double z = z0;
            while (z < zmax) { levels.Add(z); z += layerH; }
            return levels.Count > 0 ? levels : new List<double> { z0 };
        }

        /// <summary>Variable layer height. slopeFn(z)->0..1 (1=steep/flat wall->hMax,
        /// 0=shallow/overhang->hMin). Greedy march from the base up.</summary>
        public static List<double> Adaptive(double zmin, double zmax, double first,
                                            double hMin, double hMax, Func<double, double> slopeFn)
        {
            if (!(hMin > 0))
                throw new ArgumentOutOfRangeException(nameof(hMin),
                    $"minimum layer height must be > 0 (got {hMin})");
            if (hMax < hMin)
                throw new ArgumentOutOfRangeException(nameof(hMax),
                    $"maximum layer height {hMax} must be >= minimum {hMin}");
            double z = zmin + (first > 0 ? first : 0.5 * hMin);
            var levels = new List<double>();
            int guard = 0;
            while (z < zmax && guard < 100000)
            {
                levels.Add(z);
                double f = Math.Max(0.0, Math.Min(1.0, slopeFn(z)));
                z += hMin + (hMax - hMin) * f;
                guard++;
            }
            return levels.Count > 0 ? levels : new List<double> { zmin + 0.5 * hMin };
        }

        /// <summary>Slope field for <see cref="Adaptive"/> from a triangle mesh: the
        /// area-weighted mean of 1-|n.z| per Z band (0 = flat, 1 = vertical). Faces that
        /// are exactly horizontal (caps, flat tops) are skipped - they never form a
        /// printed wall and would otherwise pull the band toward "flat" and force
        /// hMin where the wall is vertical. Empty bands borrow the nearest filled one.</summary>
        public static Func<double, double> SlopeFromMesh(IReadOnlyList<Vec3> V,
                                                         IReadOnlyList<(int a, int b, int c)> F,
                                                         int bins = 32)
        {
            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var v in V) { if (v.Z < zmin) zmin = v.Z; if (v.Z > zmax) zmax = v.Z; }
            double span = Math.Max(zmax - zmin, 1e-6);
            var wsum = new double[bins];
            var fsum = new double[bins];
            foreach (var (a, b, c) in F)
            {
                Vec3 p = V[a], q = V[b], r = V[c];
                double ux = q.X - p.X, uy = q.Y - p.Y, uz = q.Z - p.Z;
                double vx = r.X - p.X, vy = r.Y - p.Y, vz = r.Z - p.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);   // 2 x area
                if (len <= 1e-12) continue;
                double anz = Math.Abs(nz) / len;
                if (anz > 0.9999) continue;   // horizontal cap
                double cz = (p.Z + q.Z + r.Z) / 3.0;
                int bi = Math.Min(bins - 1, Math.Max(0, (int)((cz - zmin) / span * bins)));
                wsum[bi] += len;
                fsum[bi] += len * (1.0 - anz);
            }
            var f = new double?[bins];
            for (int i = 0; i < bins; i++) if (wsum[i] > 0) f[i] = fsum[i] / wsum[i];
            return z =>
            {
                int bi = Math.Min(bins - 1, Math.Max(0, (int)((z - zmin) / span * bins)));
                for (int d = 0; d < bins; d++)
                {
                    if (bi - d >= 0 && f[bi - d].HasValue) return f[bi - d]!.Value;
                    if (bi + d < bins && f[bi + d].HasValue) return f[bi + d]!.Value;
                }
                return 1.0;   // no sloped face anywhere: behave like a vertical wall
            };
        }
    }
}
