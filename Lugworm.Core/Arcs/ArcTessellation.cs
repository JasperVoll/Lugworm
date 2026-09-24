using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Arcs
{
    /// <summary>One bounded, shared geometric representation of a move.  The
    /// G-code writer keeps its native line/arc command, while consumers that need
    /// a polyline (occupancy, preview, and statistics) use this helper so they do
    /// not each implement a subtly different arc approximation.</summary>
    public static class ArcTessellation
    {
        public static List<Vec3> ToPolyline(Move move, double maxDeviation = 0.05,
                                             int maxSegments = 4096)
        {
            var points = new List<Vec3>();
            if (move.Kind == MoveKind.Line)
            {
                points.Add(move.P0);
                points.Add(move.P1);
                return points;
            }

            int count = SegmentCount(move, maxDeviation, maxSegments);
            points.Capacity = count + 1;
            for (int i = 0; i <= count; i++)
                points.Add(move.PointAt((double)i / count));
            return points;
        }

        /// <summary>Exact length of a planar circular arc with linear Z motion.
        /// The XY arc length and the Z change form a constant-pitch helix.</summary>
        public static double Length(Move move)
        {
            if (move.Kind == MoveKind.Line) return PolylineOps.Dist(move.P0, move.P1);
            double sweep = Math.Abs(Sweep(move));
            double xy = sweep * Math.Abs(move.R);
            double dz = move.P1.Z - move.P0.Z;
            return Math.Sqrt(xy * xy + dz * dz);
        }

        public static double Sweep(Move move) => move.SweepAngle();

        static int SegmentCount(Move move, double maxDeviation, int maxSegments)
        {
            if (maxSegments < 1) maxSegments = 1;
            double sweep = Math.Abs(Sweep(move));
            if (sweep < 1e-12 || move.R < 1e-12) return 1;
            if (!(maxDeviation > 0.0) || double.IsNaN(maxDeviation)) return maxSegments;

            double ratio = 1.0 - maxDeviation / Math.Abs(move.R);
            double maxAngle = ratio <= -1.0 ? Math.PI
                : ratio >= 1.0 ? Math.PI / 180.0
                : 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, ratio)));
            int count = (int)Math.Ceiling(sweep / Math.Max(maxAngle, 1e-6));
            if (count < 1) count = 1;
            return Math.Min(maxSegments, count);
        }
    }
}
