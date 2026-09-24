using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Conical (non-planar) transform for a fixed (non-tilted) nozzle.
    /// Shear Z by radius about (cx,cy): z' = z + tan(angle)*r. Slice planar in
    /// z'-space, then inverse-map contour points back. XY untouched => exact inverse.
    /// Port of clay_core_v2 §5.</summary>
    public static class Conical
    {
        public static double ConeShift(double x, double y, double angleRad, double cx, double cy)
            => Math.Tan(angleRad) * Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

        public static Vec3 ForwardZ(Vec3 p, double angleRad, double cx, double cy)
            => new Vec3(p.X, p.Y, p.Z + ConeShift(p.X, p.Y, angleRad, cx, cy));

        public static Vec3 InversePt(Vec3 p, double angleRad, double cx, double cy)
            => new Vec3(p.X, p.Y, p.Z - ConeShift(p.X, p.Y, angleRad, cx, cy));

        public static List<Vec3> InverseContour(IReadOnlyList<Vec3> pts, double angleRad, double cx, double cy)
        {
            var outp = new List<Vec3>(pts.Count);
            foreach (var p in pts) outp.Add(InversePt(p, angleRad, cx, cy));
            return outp;
        }

        /// <summary>Inverse-map a polyline whose edges are straight in the sheared frame,
        /// inserting points wherever the straight printed move between two mapped points
        /// would leave the cone surface z = z' - tan(angle)*r by more than chordTol.
        /// Mapping the vertices alone is exact AT the vertices, but the move between
        /// them is a straight chord: an infill span through the axis then sags below its
        /// layer by tan(angle)*r (7 mm at r = 20, 20 deg) and a long box edge by
        /// millimetres. The cone height along a straight segment is concave in the
        /// segment parameter, so the chord lies on one side of it and the midpoint
        /// deviation is at least half the maximum: refining while the midpoint is off by
        /// more than chordTol/2 bounds the whole edge by chordTol. Edges that already
        /// stay within the bound (a ring around the axis) come back unchanged.</summary>
        public static List<Vec3> InverseContourRefined(IReadOnlyList<Vec3> pts, double angleRad,
                                                       double cx, double cy, double chordTol)
        {
            var outp = new List<Vec3>(pts.Count);
            if (pts.Count == 0) return outp;
            double halfTol = 0.5 * Math.Max(chordTol, 1e-9);
            outp.Add(InversePt(pts[0], angleRad, cx, cy));
            for (int i = 0; i + 1 < pts.Count; i++)
                Refine(pts[i], pts[i + 1], angleRad, cx, cy, halfTol, 0, outp);
            return outp;
        }

        static void Refine(Vec3 a, Vec3 b, double angleRad, double cx, double cy,
                           double halfTol, int depth, List<Vec3> outp)
        {
            var bm = InversePt(b, angleRad, cx, cy);
            if (depth < 16)
            {
                var am = InversePt(a, angleRad, cx, cy);
                var m = new Vec3(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), 0.5 * (a.Z + b.Z));
                var mm = InversePt(m, angleRad, cx, cy);
                if (Math.Abs(mm.Z - 0.5 * (am.Z + bm.Z)) > halfTol)
                {
                    Refine(a, m, angleRad, cx, cy, halfTol, depth + 1, outp);
                    Refine(m, b, angleRad, cx, cy, halfTol, depth + 1, outp);
                    return;
                }
            }
            outp.Add(bm);
        }

        /// <summary>Max rise/run angle between consecutive points (deg). For clearance
        /// checks: a fixed nozzle collides if this exceeds the nozzle clearance.</summary>
        public static double LocalSlopeDeg(IReadOnlyList<Vec3> pts)
        {
            double mx = 0.0;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double run = PolylineOps.Dist2D(pts[i], pts[i + 1]);
                double rise = Math.Abs(pts[i + 1].Z - pts[i].Z);
                if (run > 1e-9) mx = Math.Max(mx, Math.Atan2(rise, run) * 180.0 / Math.PI);
            }
            return mx;
        }
    }
}
