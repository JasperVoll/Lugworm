using System.Collections.Generic;

namespace Lugworm.Core.Geometry
{
    public static class Rdp
    {
        /// <summary>Douglas–Peucker thinning. Keeps endpoints (closed stays closed).
        /// Degenerate closed rings (fewer than 3 distinct vertices - e.g. the slicing
        /// plane grazing a mesh vertex or edge) return an EMPTY list so callers can
        /// skip the ring; a ring that collapses below tol returns unsimplified.</summary>
        public static List<Vec3> Simplify(IReadOnlyList<Vec3> pts, double tol)
        {
            int n = pts.Count;
            if (n < 3) return new List<Vec3>(pts);
            bool closed = PolylineOps.IsClosed(pts, 1e-6);
            if (closed)
            {
                var ring = new List<Vec3>(n - 1);
                for (int i = 0; i < n - 1; i++) ring.Add(pts[i]);
                var distinct = new List<Vec3>();
                foreach (var p in ring)
                {
                    bool duplicate = false;
                    foreach (var q in distinct)
                        if (PolylineOps.Dist(p, q) <= 1e-9) { duplicate = true; break; }
                    if (!duplicate) distinct.Add(p);
                }
                if (distinct.Count < 3)
                    return new List<Vec3>();   // unprintable sliver - caller skips it
                if (tol <= 0) { distinct.Add(distinct[0]); return distinct; }
                double cx = 0, cy = 0;
                foreach (var p in distinct) { cx += p.X; cy += p.Y; }
                cx /= distinct.Count; cy /= distinct.Count;
                int start = 0; double far = double.NegativeInfinity;
                for (int i = 0; i < distinct.Count; i++)
                {
                    double dx = distinct[i].X - cx, dy = distinct[i].Y - cy;
                    double d = dx * dx + dy * dy;
                    if (d > far) { far = d; start = i; }
                }
                var rotated = new List<Vec3>(distinct.Count + 1);
                for (int i = 0; i < distinct.Count; i++) rotated.Add(distinct[(start + i) % distinct.Count]);
                rotated.Add(rotated[0]);
                var simplified = SimplifyOpen(rotated, tol);
                if (simplified.Count < 4)
                {
                    // the whole ring lies within tol of a chord: thinning would
                    // destroy it, so keep the ring unsimplified instead of failing
                    distinct.Add(distinct[0]);
                    return distinct;
                }
                if (!PolylineOps.IsClosed(simplified, 1e-6)) simplified.Add(simplified[0]);
                return simplified;
            }
            if (tol <= 0) return new List<Vec3>(pts);
            return SimplifyOpen(pts, tol);
        }

        /// <summary>Douglas-Peucker over an OPEN polyline that returns the kept INDICES
        /// instead of the points, so a caller carrying per-point metadata (an observed
        /// trail's deviation values and ambiguity flags) can thin all of its parallel arrays
        /// by the same decision.</summary>
        /// <remarks>Used by the live viewport's bounded retention: an older completed run is
        /// compacted to a stated tolerance rather than having its oldest points thrown away,
        /// so a long print keeps its early layers with a known error instead of losing them
        /// silently. Endpoints are always kept; <paramref name="tol"/> at or below zero keeps
        /// everything.</remarks>
        public static List<int> SimplifyIndices(IReadOnlyList<Vec3> pts, double tol)
        {
            int n = pts == null ? 0 : pts.Count;
            var kept = new List<int>(n < 2 ? 2 : n);
            if (n == 0) return kept;
            if (n < 3 || tol <= 0)
            {
                for (int k = 0; k < n; k++) kept.Add(k);
                return kept;
            }
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                if (j <= i + 1) continue;
                Vec3 a = pts![i], b = pts[j];
                double dmax = -1.0;
                int idx = -1;
                for (int k = i + 1; k < j; k++)
                {
                    double d = PolylineOps.PointSegDist(pts[k], a, b);
                    if (d > dmax) { dmax = d; idx = k; }
                }
                if (dmax > tol && idx != -1)
                {
                    keep[idx] = true;
                    stack.Push((i, idx));
                    stack.Push((idx, j));
                }
            }
            for (int k = 0; k < n; k++) if (keep[k]) kept.Add(k);
            return kept;
        }

        static List<Vec3> SimplifyOpen(IReadOnlyList<Vec3> pts, double tol)
        {
            int n = pts.Count;
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (i, j) = stack.Pop();
                if (j <= i + 1) continue;
                Vec3 a = pts[i], b = pts[j];
                double dmax = -1.0;
                int idx = -1;
                for (int k = i + 1; k < j; k++)
                {
                    double d = PolylineOps.PointSegDist(pts[k], a, b);
                    if (d > dmax) { dmax = d; idx = k; }
                }
                if (dmax > tol && idx != -1)
                {
                    keep[idx] = true;
                    stack.Push((i, idx));
                    stack.Push((idx, j));
                }
            }
            var outp = new List<Vec3>();
            for (int k = 0; k < n; k++) if (keep[k]) outp.Add(pts[k]);
            return outp;
        }
    }
}
