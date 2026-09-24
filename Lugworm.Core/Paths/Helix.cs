using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>Builds one continuous, layer-to-layer contour helix.</summary>
    public static class Helix
    {
        /// <summary>
        /// Joins one closed contour per layer. Each contour is rotated to the
        /// nearest point of the preceding layer and explicitly re-closed after
        /// rotation, then its Z rises from the current level to the next one.
        /// </summary>
        public static List<Vec3> Build(IReadOnlyList<IReadOnlyList<Vec3>> contours,
                                       IReadOnlyList<double> levels,
                                       double layerHeight,
                                       bool adaptiveLevels = false)
        {
            var result = new List<Vec3>();
            Vec3? previous = null;
            for (int i = 0; i < contours.Count; i++)
            {
                var source = contours[i];
                if (source == null || source.Count < 3) continue;
                int count = PolylineOps.IsClosed(source, 1e-3) ? source.Count - 1 : source.Count;
                if (count < 3) continue;

                List<Vec3> points;
                if (previous.HasValue)
                {
                    var hit = SeamAnchor.ClosestOnRing(source, previous.Value);
                    points = SeamAnchor.SplitAt(source, hit.SegmentIndex, hit.T);
                }
                else points = SeamAnchor.SplitAt(source, 0, 0.0);

                var cumulative = PolylineOps.CumulativeLengths(points);
                double total = cumulative[cumulative.Length - 1];
                if (total <= 1e-9) continue;
                double z0 = i < levels.Count ? levels[i] : levels.Count > 0
                    ? levels[levels.Count - 1] + (i - levels.Count + 1) * layerHeight
                    : i * layerHeight;
                double rise = !adaptiveLevels ? layerHeight
                    : i + 1 < levels.Count ? levels[i + 1] - z0
                    : i > 0 && i < levels.Count ? z0 - levels[i - 1]
                    : layerHeight;
                for (int k = 0; k < points.Count; k++)
                {
                    double t = cumulative[k] / total;
                    result.Add(new Vec3(points[k].X, points[k].Y, z0 + t * rise));
                }
                previous = result[result.Count - 1];
            }
            return result;
        }
    }
}
