using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    public enum SeamMode
    {
        /// <summary>Keep the current seam.</summary>
        Keep = 0,
        /// <summary>Align seams to one fixed direction.</summary>
        Align = 1,
        /// <summary>Stagger seams by layer.</summary>
        Stagger = 2,
        /// <summary>Place the seam nearest a target point.</summary>
        Nearest = 3,
        /// <summary>Place the seam nearest the guide curve.</summary>
        Guide = 4,
        /// <summary>Track the seam from the previous layer.</summary>
        Track = 5,
    }

    public static class SeamPlacer
    {
        // The seam ray must clear the contour bounding box.
        private const double SeamRayClearance = 10.0;

        /// <summary>Returns the index of the candidate with the smallest geometric
        /// distance. Invalid distances are ignored and ties keep input order.</summary>
        public static int NearestCandidateIndex(IReadOnlyList<double> distances)
        {
            int best = -1;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < distances.Count; i++)
            {
                double distance = distances[i];
                if (double.IsNaN(distance) || distance < 0 || distance >= bestDistance) continue;
                best = i;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>Chooses which end of a relevant open path should meet the wall.
        /// A supplied seam target chooses between the two endpoints; without one,
        /// the endpoint geometrically closest to the wall is used.</summary>
        public static bool ReverseOpenPath(Vec3 openStart, Vec3 openEnd,
                                           double wallDistanceAtStart, double wallDistanceAtEnd,
                                           Vec3? userTarget)
        {
            if (userTarget.HasValue)
                return PolylineOps.Dist2(userTarget.Value, openEnd) < PolylineOps.Dist2(userTarget.Value, openStart);
            return wallDistanceAtEnd < wallDistanceAtStart;
        }

        /// <summary>Chooses a stable start for an open path. Wall proximity excludes an
        /// interior end first, then the seam target, previous layer and +X polar order
        /// break the remaining tie in that order.</summary>
        public static (bool Reverse, Vec3 Start) OrientOpenPath(
            Vec3 openStart, Vec3 openEnd,
            double wallDistanceAtStart, double wallDistanceAtEnd,
            Vec3? seamTarget, Vec3? previousStart,
            Vec3 layerCentroid, double layerWidth)
        {
            if (double.IsNaN(layerWidth) || double.IsInfinity(layerWidth) || layerWidth < 0.0)
                throw new ArgumentOutOfRangeException(nameof(layerWidth));
            wallDistanceAtStart = ValidDistance(wallDistanceAtStart);
            wallDistanceAtEnd = ValidDistance(wallDistanceAtEnd);

            bool startEligible = !(wallDistanceAtStart > 2.0 * wallDistanceAtEnd
                                   && wallDistanceAtStart > layerWidth);
            bool endEligible = !(wallDistanceAtEnd > 2.0 * wallDistanceAtStart
                                 && wallDistanceAtEnd > layerWidth);
            if (startEligible && !endEligible) return (false, openStart);
            if (endEligible && !startEligible) return (true, openEnd);

            if (seamTarget.HasValue)
            {
                double startDistance = Math.Sqrt(PolylineOps.Dist2(seamTarget.Value, openStart));
                double endDistance = Math.Sqrt(PolylineOps.Dist2(seamTarget.Value, openEnd));
                if (Math.Abs(startDistance - endDistance) >= layerWidth)
                    return endDistance < startDistance ? (true, openEnd) : (false, openStart);
            }

            if (previousStart.HasValue)
            {
                double startDistance = PolylineOps.Dist2(previousStart.Value, openStart);
                double endDistance = PolylineOps.Dist2(previousStart.Value, openEnd);
                if (startDistance != endDistance)
                    return endDistance < startDistance ? (true, openEnd) : (false, openStart);
            }

            double startAngle = PolarAngle(openStart, layerCentroid);
            double endAngle = PolarAngle(openEnd, layerCentroid);
            return endAngle < startAngle ? (true, openEnd) : (false, openStart);
        }

        static double ValidDistance(double distance)
            => double.IsNaN(distance) || distance < 0.0 ? double.PositiveInfinity : distance;

        static double PolarAngle(Vec3 point, Vec3 centroid)
        {
            double angle = Math.Atan2(point.Y - centroid.Y, point.X - centroid.X);
            return angle < 0.0 ? angle + 2.0 * Math.PI : angle;
        }

        /// <summary>Computes the target point used to place a contour seam.</summary>
        public static Vec3? TargetFor(
            SeamMode mode,
            Vec3 centroid,
            double bboxDiagonal,
            double seamAngleDeg,
            int layerIndex,
            Vec3? seamPoint,
            Vec3? guideClosestPoint,
            Vec3? trackTarget)
        {
            switch (mode)
            {
                case SeamMode.Keep:
                    return null;

                case SeamMode.Align:
                case SeamMode.Stagger:
                    double angleDeg = seamAngleDeg * (mode == SeamMode.Stagger ? layerIndex : 1.0);
                    double angleRad = angleDeg * Math.PI / 180.0;
                    double radius = bboxDiagonal + SeamRayClearance;
                    return new Vec3(
                        centroid.X + Math.Cos(angleRad) * radius,
                        centroid.Y + Math.Sin(angleRad) * radius,
                        centroid.Z);

                case SeamMode.Nearest:
                    return seamPoint;

                case SeamMode.Guide:
                    return guideClosestPoint;

                case SeamMode.Track:
                    return trackTarget;

                default:
                    return null;
            }
        }
    }
}
