using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Arcs
{
    public static class ArcFit
    {
        /// <summary>Circumcircle of three XY points -> (cx,cy,r), or null if collinear.</summary>
        public static (double cx, double cy, double r)? CircleFrom3(Vec3 p0, Vec3 p1, Vec3 p2)
        {
            double ax = p0.X, ay = p0.Y, bx = p1.X, by = p1.Y, cx = p2.X, cy = p2.Y;
            double d = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-12) return null;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / d;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / d;
            double r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
            return (ux, uy, r);
        }

        /// <summary>Greedy arc fitting (ArcWelder-style). Circle and deviation
        /// state are advanced once per input point.  The bounded window prevents
        /// a nearly circular 100k-point contour from turning into an accidental
        /// quadratic operation. Only fits in XY on runs with ~constant Z.</summary>
        public static List<Move> FitArcs(IReadOnlyList<Vec3> pts, double tol = 0.05,
            int minPts = 4, double maxRadius = 1e4, double planarZTol = 1e-4,
            int maxWindow = 512)
        {
            int n = pts.Count;
            var moves = new List<Move>();
            if (n < 2) return moves;
            if (minPts < 3) minPts = 3;
            if (maxWindow < minPts) maxWindow = minPts;
            if (!(tol >= 0.0) || double.IsNaN(tol)) tol = 0.05;
            int i = 0;
            while (i < n - 1)
            {
                int bestJ = -1;
                (double cx, double cy, double r, int sign) best = default;
                if (i + minPts - 1 <= n - 1 &&
                    Math.Abs(pts[i + 1].Z - pts[i].Z) <= planarZTol &&
                    Math.Abs(pts[i + 2].Z - pts[i].Z) <= planarZTol)
                {
                    var circ = CircleFrom3(pts[i], pts[i + 1], pts[i + 2]);
                    if (circ.HasValue && circ.Value.r <= maxRadius && circ.Value.r > 1e-12)
                    {
                        int sign = 0;
                        double previousAngle = Math.Atan2(pts[i + 1].Y - circ.Value.cy,
                                                         pts[i + 1].X - circ.Value.cx);
                        double maxError = 0.0;
                        int end = Math.Min(n - 1, i + maxWindow - 1);
                        for (int j = i + 2; j <= end; j++)
                        {
                            if (Math.Abs(pts[j].Z - pts[i].Z) > planarZTol) break;
                            double dx = pts[j].X - circ.Value.cx, dy = pts[j].Y - circ.Value.cy;
                            double radialError = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - circ.Value.r);
                            if (radialError > maxError) maxError = radialError;
                            if (maxError > tol) break;

                            double angle = Math.Atan2(dy, dx);
                            double da = angle - previousAngle;
                            while (da > Math.PI) da -= 2.0 * Math.PI;
                            while (da < -Math.PI) da += 2.0 * Math.PI;
                            if (Math.Abs(da) > 1e-9)
                            {
                                int s = da > 0 ? 1 : -1;
                                if (sign == 0) sign = s;
                                else if (s != sign) break;
                            }
                            double sagitta = circ.Value.r * (1.0 - Math.Cos(Math.Abs(da) * 0.5));
                            if (sagitta > tol) break;
                            // A linearly changing Z is acceptable only when its
                            // midpoint cannot leave the planar tolerance band.
                            double z0 = pts[i].Z;
                            int midIndex = (i + j) / 2;
                            double zExpected = z0 + (pts[j].Z - z0) *
                                ((double)(midIndex - i) / Math.Max(1, j - i));
                            double zMid = pts[midIndex].Z;
                            if (Math.Abs(zMid - zExpected) > planarZTol) break;
                            bestJ = j;
                            best = (circ.Value.cx, circ.Value.cy, circ.Value.r, sign);
                            previousAngle = angle;
                        }
                    }
                }
                if (bestJ - i + 1 >= minPts && bestJ != -1 && best.sign != 0)
                {
                    bool cw = best.sign < 0;   // atan2 increasing = CCW = G3; sign<0 = CW = G2
                    moves.Add(Move.Arc(pts[i], pts[bestJ], best.cx, best.cy, cw, best.r));
                    i = bestJ;
                }
                else
                {
                    moves.Add(Move.Line(pts[i], pts[i + 1]));
                    i += 1;
                }
            }
            return moves;
        }

        public static double ArcLengthOf(Move m)
        {
            if (m.Kind == MoveKind.Line) return PolylineOps.Dist(m.P0, m.P1);
            return Math.Abs(m.SweepAngle()) * m.R;   // centralized sweep incl. full-circle convention
        }
    }
}
