using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>
    /// Converts the segment-ending extrusion flags used by G-code parsing and
    /// toolpath sampling into maximal visible layer runs. A scrub position is always
    /// evaluated from the start of the job so a continuous deposition path remains
    /// one run instead of becoming one separately capped mesh per slider tick.
    /// </summary>
    public static class ExtrusionHistory
    {
        public static List<List<Vec3>> VisibleRuns(
            IReadOnlyList<Vec3> points,
            IReadOnlyList<bool> extruding,
            int maxIndex)
            => Runs(points, extruding, maxIndex, depositing: true);

        /// <summary>
        /// Maximal runs of consecutive TRAVEL (non-depositing) segments up to the scrub
        /// index - the complement of <see cref="VisibleRuns"/>. Lets a viewer draw where
        /// the tool moved without laying material, which no layer mesh can show.
        /// </summary>
        public static List<List<Vec3>> TravelRuns(
            IReadOnlyList<Vec3> points,
            IReadOnlyList<bool> extruding,
            int maxIndex)
            => Runs(points, extruding, maxIndex, depositing: false);

        static List<List<Vec3>> Runs(
            IReadOnlyList<Vec3> points,
            IReadOnlyList<bool> extruding,
            int maxIndex,
            bool depositing)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (extruding == null) throw new ArgumentNullException(nameof(extruding));

            var runs = new List<List<Vec3>>();
            if (maxIndex < 1 || points.Count < 2 || extruding.Count < 2) return runs;

            int last = Math.Min(maxIndex, Math.Min(points.Count, extruding.Count) - 1);
            List<Vec3>? current = null;
            for (int k = 1; k <= last; k++)
            {
                if (extruding[k] == depositing)
                {
                    if (current == null) current = new List<Vec3> { points[k - 1] };
                    current.Add(points[k]);
                }
                else if (current != null)
                {
                    if (current.Count >= 2) runs.Add(current);
                    current = null;
                }
            }

            if (current != null && current.Count >= 2) runs.Add(current);
            return runs;
        }
    }
}
