using System;
using System.Collections.Generic;

namespace Lugworm.Core.Geometry
{
    public static class PolylineOps
    {
        public static double Dist(Vec3 a, Vec3 b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X)
                       + (a.Y - b.Y) * (a.Y - b.Y)
                       + (a.Z - b.Z) * (a.Z - b.Z));

        public static double Dist2D(Vec3 a, Vec3 b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Squared XY distance (no sqrt) - the shared helper behind the several
        /// private "Dist2" copies that only ever compared distances.</summary>
        public static double Dist2Xy(Vec3 a, Vec3 b)
            => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

        /// <summary>Squared XYZ distance (no sqrt).</summary>
        public static double Dist2(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static Vec3 Lerp(Vec3 a, Vec3 b, double t)
            => new Vec3(a.X + (b.X - a.X) * t,
                        a.Y + (b.Y - a.Y) * t,
                        a.Z + (b.Z - a.Z) * t);

        public static double ContourLength(IReadOnlyList<Vec3> pts)
        {
            double s = 0.0;
            for (int i = 0; i < pts.Count - 1; i++) s += Dist(pts[i], pts[i + 1]);
            return s;
        }

        /// <summary>Returns [0, l1, l1+l2, ...] cumulative arc length.</summary>
        public static double[] CumulativeLengths(IReadOnlyList<Vec3> pts)
        {
            var outp = new double[pts.Count];
            outp[0] = 0.0;
            for (int i = 0; i < pts.Count - 1; i++)
                outp[i + 1] = outp[i] + Dist(pts[i], pts[i + 1]);
            return outp;
        }

        public static bool IsClosed(IReadOnlyList<Vec3> pts, double tol = 1e-6)
            => pts.Count >= 3 && Dist(pts[0], pts[pts.Count - 1]) <= tol;

        /// <summary>Shoelace signed area in XY (CCW positive). Closed or open ok.</summary>
        public static double SignedAreaXy(IReadOnlyList<Vec3> pts)
        {
            int n = pts.Count;
            double s = 0.0;
            for (int i = 0; i < n; i++)
            {
                var p1 = pts[i];
                var p2 = pts[(i + 1) % n];
                s += p1.X * p2.Y - p2.X * p1.Y;
            }
            return 0.5 * s;
        }

        public static List<Vec3> EnsureCcw(IReadOnlyList<Vec3> pts)
        {
            if (SignedAreaXy(pts) >= 0) return new List<Vec3>(pts);
            var rev = new List<Vec3>(pts);
            rev.Reverse();
            return rev;
        }

        /// <summary>Rounds sharp corners of a polyline to ~<paramref name="radius"/> mm
        /// (overfill correction for clay: a continuous, sharp-corner-free path). Each corner
        /// is replaced by a quadratic Bézier that leaves and rejoins the edges tangentially,
        /// set back by min(radius, 0.49*edge) so adjacent fillets never overlap. Near-straight
        /// corners (turn &lt; ~15°) are left as-is. Open polylines keep their two endpoints;
        /// closed ones round every corner and stay closed.</summary>
        public static List<Vec3> RoundCorners(IReadOnlyList<Vec3> pts, double radius, bool closed)
        {
            if (radius <= 1e-9 || pts.Count < 3) return new List<Vec3>(pts);

            // work on the unique vertices (drop the duplicate closing point if present)
            var v = new List<Vec3>(pts);
            if (closed && Dist(v[0], v[v.Count - 1]) <= 1e-9) v.RemoveAt(v.Count - 1);
            int n = v.Count;
            if (n < 3) return new List<Vec3>(pts);

            const double straight = 15.0 * Math.PI / 180.0;   // skip corners flatter than this
            var outp = new List<Vec3>(n * 3);

            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) { outp.Add(v[i]); continue; }
                var V = v[i];
                var A = v[(i - 1 + n) % n];
                var B = v[(i + 1) % n];
                var u = Unit(Sub(A, V));
                var w = Unit(Sub(B, V));
                double cos = Math.Max(-1.0, Math.Min(1.0, u.X * w.X + u.Y * w.Y + u.Z * w.Z));
                double turn = Math.PI - Math.Acos(cos);   // 0 = straight, larger = sharper
                if (turn < straight) { outp.Add(V); continue; }

                double d = Math.Min(radius, 0.49 * Math.Min(Dist(A, V), Dist(B, V)));
                if (d <= 1e-9) { outp.Add(V); continue; }
                var p1 = Lerp(V, A, d / Dist(A, V));
                var p2 = Lerp(V, B, d / Dist(B, V));
                int steps = Math.Max(2, Math.Min(12, (int)Math.Round(turn / (Math.PI / 12.0))));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    double mt = 1.0 - t;
                    outp.Add(new Vec3(
                        mt * mt * p1.X + 2 * mt * t * V.X + t * t * p2.X,
                        mt * mt * p1.Y + 2 * mt * t * V.Y + t * t * p2.Y,
                        mt * mt * p1.Z + 2 * mt * t * V.Z + t * t * p2.Z));
                }
            }
            if (closed && outp.Count > 0) outp.Add(outp[0]);
            return outp;
        }

        static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vec3 Unit(Vec3 v)
        {
            double n = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return n < 1e-12 ? new Vec3(0, 0, 0) : new Vec3(v.X / n, v.Y / n, v.Z / n);
        }

        /// <summary>Minimum distance between segments a0-a1 and b0-b1 (3D).
        /// Standard clamped closest-point-of-approach formulation.</summary>
        public static double SegSegDist(Vec3 a0, Vec3 a1, Vec3 b0, Vec3 b1)
        {
            var d1 = a1 - a0; var d2 = b1 - b0; var r = a0 - b0;
            double A = d1.Dot(d1), E = d2.Dot(d2), F = d2.Dot(r);
            double s, t;
            if (A <= 1e-18 && E <= 1e-18) return r.Length;
            if (A <= 1e-18) { s = 0; t = Num.Clamp01(F / E); }
            else
            {
                double C = d1.Dot(r);
                if (E <= 1e-18) { t = 0; s = Num.Clamp01(-C / A); }
                else
                {
                    double B = d1.Dot(d2), den = A * E - B * B;
                    s = den > 1e-18 ? Num.Clamp01((B * F - C * E) / den) : 0.0;
                    t = (B * s + F) / E;
                    if (t < 0) { t = 0; s = Num.Clamp01(-C / A); }
                    else if (t > 1) { t = 1; s = Num.Clamp01((B - C) / A); }
                }
            }
            var cp1 = a0 + d1 * s;
            var cp2 = b0 + d2 * t;
            return cp1.DistanceTo(cp2);
        }

        /// <summary>Distance from point p to segment a-b (3D).</summary>
        public static double PointSegDist(Vec3 p, Vec3 a, Vec3 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double l2 = dx * dx + dy * dy + dz * dz;
            if (l2 <= 1e-18) return Dist(p, a);
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / l2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double cx = a.X + dx * t, cy = a.Y + dy * t, cz = a.Z + dz * t;
            return Math.Sqrt((p.X - cx) * (p.X - cx)
                           + (p.Y - cy) * (p.Y - cy)
                           + (p.Z - cz) * (p.Z - cz));
        }
    }
}
