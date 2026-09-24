using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>Segment-based seam geometry for closed XY rings.</summary>
    public static class SeamAnchor
    {
        const double Epsilon = 1e-12;

        /// <summary>Returns the farthest intersection of an XY ray with a closed ring.</summary>
        public static (Vec3 Point, int SegmentIndex, double T)? RayHit(
            IReadOnlyList<Vec3> ring, Vec3 origin, double angleRad)
        {
            int count = UniqueCount(ring);
            if (count < 2) return null;

            double dx = Math.Cos(angleRad), dy = Math.Sin(angleRad);
            double bestDistance = double.NegativeInfinity;
            (Vec3 Point, int SegmentIndex, double T)? best = null;
            for (int i = 0; i < count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % count];
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double ax = a.X - origin.X, ay = a.Y - origin.Y;
                double denominator = Cross(dx, dy, ex, ey);

                if (Math.Abs(denominator) <= Epsilon)
                {
                    if (Math.Abs(Cross(ax, ay, dx, dy)) > Epsilon) continue;
                    ConsiderCollinear(a, i, 0.0);
                    ConsiderCollinear(b, i, 1.0);
                    continue;
                }

                double distance = Cross(ax, ay, ex, ey) / denominator;
                double t = Cross(ax, ay, dx, dy) / denominator;
                if (distance < -Epsilon || t < -Epsilon || t > 1.0 + Epsilon) continue;
                distance = Math.Max(0.0, distance);
                t = Num.Clamp01(t);
                if (distance > bestDistance + Epsilon)
                {
                    bestDistance = distance;
                    best = (PolylineOps.Lerp(a, b, t), i, t);
                }
            }
            return best;

            void ConsiderCollinear(Vec3 point, int segmentIndex, double t)
            {
                double distance = (point.X - origin.X) * dx + (point.Y - origin.Y) * dy;
                if (distance < -Epsilon || distance <= bestDistance + Epsilon) return;
                bestDistance = Math.Max(0.0, distance);
                best = (point, segmentIndex, t);
            }
        }

        /// <summary>Returns the closest point on the ring's XY segments.</summary>
        public static (Vec3 Point, int SegmentIndex, double T) ClosestOnRing(
            IReadOnlyList<Vec3> ring, Vec3 point)
        {
            int count = UniqueCount(ring);
            if (count < 2)
                throw new ArgumentException("a seam ring needs at least two distinct points", nameof(ring));

            double bestDistance2 = double.PositiveInfinity;
            Vec3 bestPoint = ring[0];
            int bestSegment = 0;
            double bestT = 0.0;
            for (int i = 0; i < count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length2 = dx * dx + dy * dy;
                double t = length2 > Epsilon
                    ? Num.Clamp01(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length2)
                    : 0.0;
                var candidate = PolylineOps.Lerp(a, b, t);
                double qx = candidate.X - point.X, qy = candidate.Y - point.Y;
                double distance2 = qx * qx + qy * qy;
                if (distance2 >= bestDistance2) continue;
                bestDistance2 = distance2;
                bestPoint = candidate;
                bestSegment = i;
                bestT = t;
            }
            return (bestPoint, bestSegment, bestT);
        }

        /// <summary>Starts a ring at a point on one of its segments and closes it again.</summary>
        public static List<Vec3> SplitAt(IReadOnlyList<Vec3> ring, int segmentIndex, double t)
        {
            int count = UniqueCount(ring);
            if (count < 2)
                throw new ArgumentException("a seam ring needs at least two distinct points", nameof(ring));
            if (segmentIndex < 0 || segmentIndex >= count)
                throw new ArgumentOutOfRangeException(nameof(segmentIndex));
            if (double.IsNaN(t) || double.IsInfinity(t))
                throw new ArgumentOutOfRangeException(nameof(t));

            t = Num.Clamp01(t);
            var seam = PolylineOps.Lerp(ring[segmentIndex], ring[(segmentIndex + 1) % count], t);
            var result = new List<Vec3>(count + 2) { seam };
            for (int offset = 1; offset <= count; offset++)
            {
                var vertex = ring[(segmentIndex + offset) % count];
                if (PolylineOps.Dist(result[result.Count - 1], vertex) > Epsilon) result.Add(vertex);
            }
            if (PolylineOps.Dist(result[result.Count - 1], seam) > Epsilon) result.Add(seam);
            else result[result.Count - 1] = seam;
            return result;
        }

        /// <summary>Returns the XY area centroid. Z is the mean ring height.</summary>
        public static Vec3 Centroid(IReadOnlyList<Vec3> ring)
        {
            int count = UniqueCount(ring);
            if (count == 0) throw new ArgumentException("a seam ring cannot be empty", nameof(ring));

            double twiceArea = 0.0, x = 0.0, y = 0.0, z = 0.0;
            for (int i = 0; i < count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % count];
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                x += (a.X + b.X) * cross;
                y += (a.Y + b.Y) * cross;
                z += a.Z;
            }
            if (Math.Abs(twiceArea) <= Epsilon)
            {
                x = 0.0;
                y = 0.0;
                for (int i = 0; i < count; i++) { x += ring[i].X; y += ring[i].Y; }
                return new Vec3(x / count, y / count, z / count);
            }
            return new Vec3(x / (3.0 * twiceArea), y / (3.0 * twiceArea), z / count);
        }

        static int UniqueCount(IReadOnlyList<Vec3> ring)
        {
            if (ring == null) throw new ArgumentNullException(nameof(ring));
            int count = ring.Count;
            if (count > 1 && PolylineOps.Dist(ring[0], ring[count - 1]) <= Epsilon) count--;
            return count;
        }

        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
    }
}
