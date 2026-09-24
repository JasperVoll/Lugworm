using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Clipping
{
    /// <summary>Nesting-aware ring orientation. A slice section can contain holes
    /// (and islands inside holes): Clipper2 encodes solids vs holes by orientation
    /// (outer = CCW, hole = CW under positive fill), so before offsetting or infill
    /// clipping every ring must be oriented by its even-odd nesting depth -
    /// forcing everything CCW (the old behaviour) makes inward wall offsets shrink
    /// holes instead of growing them and lets infill fill hole interiors.</summary>
    public static class Nesting
    {
        /// <summary>Orient closed rings by nesting depth: even depth (outer) → CCW,
        /// odd depth (hole) → CW. Rings with fewer than 3 points are dropped.</summary>
        public static List<List<Vec3>> Orient(IReadOnlyList<IReadOnlyList<Vec3>> rings)
        {
            var outp = new List<List<Vec3>>(rings.Count);
            var kept = new List<RingInfo>(rings.Count);
            for (int original = 0; original < rings.Count; original++)
            {
                var r = rings[original];
                if (r == null || r.Count < 3) continue;
                var closed = new List<Vec3>(r);
                if (!PolylineOps.IsClosed(closed, 1e-6)) closed.Add(closed[0]);
                double area = Math.Abs(PolylineOps.SignedAreaXy(closed));
                if (area < 1e-12) continue;
                var info = new RingInfo(original, closed, area);
                kept.Add(info);
            }

            if (kept.Count == 0) return outp;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var ring in kept)
            {
                minX = Math.Min(minX, ring.MinX); minY = Math.Min(minY, ring.MinY);
                maxX = Math.Max(maxX, ring.MaxX); maxY = Math.Max(maxY, ring.MaxY);
            }
            double cell = Math.Max(Math.Max(maxX - minX, maxY - minY) /
                                   Math.Max(1.0, Math.Sqrt(kept.Count)), 1e-6);
            var index = new Dictionary<(int x, int y), List<int>>();
            var overflow = new List<int>();
            var order = new List<int>(kept.Count);
            for (int i = 0; i < kept.Count; i++) order.Add(i);
            order.Sort((a, b) => kept[b].Area.CompareTo(kept[a].Area));

            const long MaxCellsPerRing = 4096;
            foreach (int current in order)
            {
                var ring = kept[current];
                var probe = RepresentativePoint(ring.Points);
                int px = Cell(probe.X, minX, cell), py = Cell(probe.Y, minY, cell);
                int bestParent = -1;
                double bestArea = double.MaxValue;
                if (index.TryGetValue((px, py), out var candidates))
                    foreach (int candidate in candidates)
                        TryParent(kept, candidate, current, probe, ref bestParent, ref bestArea);
                for (int oi = 0; oi < overflow.Count; oi++)
                    TryParent(kept, overflow[oi], current, probe, ref bestParent, ref bestArea);

                ring.Parent = bestParent;
                ring.Depth = bestParent < 0 ? 0 : kept[bestParent].Depth + 1;
                long inserted = 0;
                int x0 = Cell(ring.MinX, minX, cell), x1 = Cell(ring.MaxX, minX, cell);
                int y0 = Cell(ring.MinY, minY, cell), y1 = Cell(ring.MaxY, minY, cell);
                inserted = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                if (inserted > MaxCellsPerRing)
                {
                    overflow.Add(current);
                }
                else
                {
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            if (!index.TryGetValue((x, y), out var list))
                                index[(x, y)] = list = new List<int>(2);
                            list.Add(current);
                        }
                }
            }

            kept.Sort((a, b) => a.OriginalIndex.CompareTo(b.OriginalIndex));
            foreach (var ring in kept)
            {
                bool wantCcw = ring.Depth % 2 == 0;
                bool isCcw = PolylineOps.SignedAreaXy(ring.Points) > 0;
                var oriented = new List<Vec3>(ring.Points);
                if (isCcw != wantCcw) oriented.Reverse();
                outp.Add(oriented);
            }
            return outp;
        }

        sealed class RingInfo
        {
            public readonly int OriginalIndex;
            public readonly List<Vec3> Points;
            public readonly double Area;
            public readonly double MinX, MinY, MaxX, MaxY;
            public int Parent = -1, Depth;

            public RingInfo(int originalIndex, List<Vec3> points, double area)
            {
                OriginalIndex = originalIndex; Points = points; Area = area;
                MinX = MaxX = points[0].X; MinY = MaxY = points[0].Y;
                for (int i = 1; i < points.Count; i++)
                {
                    MinX = Math.Min(MinX, points[i].X); MaxX = Math.Max(MaxX, points[i].X);
                    MinY = Math.Min(MinY, points[i].Y); MaxY = Math.Max(MaxY, points[i].Y);
                }
            }
        }

        static int Cell(double value, double origin, double cell)
        {
            double q = Math.Floor((value - origin) / cell);
            if (q <= int.MinValue) return int.MinValue;
            if (q >= int.MaxValue) return int.MaxValue;
            return (int)q;
        }

        static void TryParent(List<RingInfo> rings, int candidate, int current, Vec3 probe,
                              ref int bestParent, ref double bestArea)
        {
            var parent = rings[candidate];
            var child = rings[current];
            if (parent.Area <= child.Area || parent.MinX > probe.X || parent.MaxX < probe.X
                || parent.MinY > probe.Y || parent.MaxY < probe.Y) return;
            if (parent.Area >= bestArea || !ContainsXy(parent.Points, probe)) return;
            bestParent = candidate;
            bestArea = parent.Area;
        }

        /// <summary>Number of rings that contain the probe point of <paramref name="ring"/>
        /// - 0 for an outer boundary, 1 for a hole, 2 for an island in a hole, …</summary>
        public static int Depth(IReadOnlyList<IReadOnlyList<Vec3>> rings, int index)
        {
            if (index < 0 || index >= rings.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (rings[index] == null || rings[index].Count < 3)
                throw new ArgumentException(
                    $"ring {index} is degenerate ({rings[index]?.Count ?? 0} points, need >= 3) - cannot compute nesting depth");
            int depth = 0;
            var probe = RepresentativePoint(rings[index]);
            for (int j = 0; j < rings.Count; j++)
                if (j != index && rings[j].Count >= 3 && ContainsXy(rings[j], probe)) depth++;
            return depth;
        }

        /// <summary>A point that is strictly representative of the ring's interior side:
        /// the midpoint of the first edge nudged towards the ring's own inside would be
        /// ideal, but a plain vertex works for the containment-vs-OTHER-rings test as
        /// long as rings don't share vertices; use the vertex averaged slightly towards
        /// the ring centroid to dodge exactly-shared vertices between touching rings.</summary>
        static Vec3 RepresentativePoint(IReadOnlyList<Vec3> ring)
        {
            int n = ring.Count;
            // drop duplicate closing point for the centroid
            if (n > 1 && PolylineOps.Dist(ring[0], ring[n - 1]) < 1e-12) n--;
            double cx = 0, cy = 0;
            for (int i = 0; i < n; i++) { cx += ring[i].X; cy += ring[i].Y; }
            cx /= n; cy /= n;
            var v = ring[0];
            // 1% towards the centroid: off any shared vertex/edge, still ~on the ring
            return new Vec3(v.X + 0.01 * (cx - v.X), v.Y + 0.01 * (cy - v.Y), v.Z);
        }

        /// <summary>Even-odd point-in-polygon (XY, ray cast along +X, half-open rule).</summary>
        public static bool ContainsXy(IReadOnlyList<Vec3> ring, Vec3 p)
        {
            int n = ring.Count;
            if (n > 1 && PolylineOps.Dist(ring[0], ring[n - 1]) < 1e-12) n--;   // ignore closing dup
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double yi = ring[i].Y, yj = ring[j].Y;
                double xi = ring[i].X, xj = ring[j].X;
                if ((yi > p.Y) != (yj > p.Y))
                {
                    double xCross = xi + (p.Y - yi) / (yj - yi) * (xj - xi);
                    if (p.X < xCross) inside = !inside;
                }
            }
            return inside;
        }
    }
}
