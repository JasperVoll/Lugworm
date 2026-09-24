using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    /// <summary>Budgeted preview simplification: retain the largest chord deviations
    /// first so straight sections do not consume the points needed by corners.</summary>
    public static class PreviewReduction
    {
        public static List<Vec3> Reduce(IReadOnlyList<Vec3> path, int budget)
        {
            budget = Math.Max(2, budget);
            if (path.Count <= budget) return new List<Vec3>(path);
            var selected = new SortedSet<int> { 0, path.Count - 1 };
            var pending = new SortedSet<(double error, int start, int end, int split)>();
            Add(0, path.Count - 1);
            while (selected.Count < budget && pending.Count > 0)
            {
                var next = pending.Max;
                pending.Remove(next);
                selected.Add(next.split);
                Add(next.start, next.split);
                Add(next.split, next.end);
            }
            var result = new List<Vec3>(selected.Count);
            foreach (int i in selected) result.Add(path[i]);
            return result;

            void Add(int start, int end)
            {
                if (end - start < 2) return;
                var a = path[start]; var b = path[end];
                double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                double length2 = dx * dx + dy * dy + dz * dz;
                double worst = -1;
                int split = start + 1;
                for (int i = start + 1; i < end; i++)
                {
                    var p = path[i];
                    double t = length2 > 1e-20 ? Math.Max(0, Math.Min(1,
                        ((p.X - a.X) * dx + (p.Y - a.Y) * dy + (p.Z - a.Z) * dz) / length2)) : 0;
                    double ex = p.X - a.X - t * dx, ey = p.Y - a.Y - t * dy, ez = p.Z - a.Z - t * dz;
                    double error = ex * ex + ey * ey + ez * ez;
                    if (error > worst) { worst = error; split = i; }
                }
                if (worst > 1e-16) pending.Add((worst, start, end, split));
            }
        }
    }
}
