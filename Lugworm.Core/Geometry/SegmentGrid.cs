using System;
using System.Collections.Generic;

namespace Lugworm.Core.Geometry
{
    /// <summary>Reusable uniform 3D hash grid over polyline segments.  Segment
    /// insertion follows the traversed voxel path instead of rasterising the full
    /// segment AABB.  Queries remain exact: every traversed cell contains the
    /// segment and the far-query fallback scans the compact segment store rather
    /// than expanding an unbounded empty shell.</summary>
    public sealed class SegmentGrid
    {
        readonly List<(Vec3 A, Vec3 B)> _segs = new List<(Vec3, Vec3)>();
        readonly Dictionary<(int x, int y, int z), List<int>> _cells
            = new Dictionary<(int, int, int), List<int>>();
        double _requestedCell;
        double _cell;
        int _minX, _minY, _minZ, _maxX, _maxY, _maxZ;
        double _minWorldX, _minWorldY, _minWorldZ, _maxWorldX, _maxWorldY, _maxWorldZ;
        int[] _stamp = new int[0];
        int _query;

        public int SegmentCount => _segs.Count;

        public SegmentGrid(IReadOnlyList<IReadOnlyList<Vec3>> contours, double cellSize = 0.0)
        {
            _requestedCell = cellSize;
            Rebuild(contours);
        }

        /// <summary>Replace the indexed layer while retaining the dictionary and
        /// segment buffers. This is used by layer-by-layer flow and overhang work.</summary>
        public void Rebuild(IReadOnlyList<IReadOnlyList<Vec3>> contours, double? cellSize = null)
        {
            if (cellSize.HasValue) _requestedCell = cellSize.Value;
            _segs.Clear();
            _cells.Clear();
            _query = 0;
            double totalLength = 0.0;
            int segmentCount = 0;
            _minWorldX = _minWorldY = _minWorldZ = double.MaxValue;
            _maxWorldX = _maxWorldY = _maxWorldZ = double.MinValue;
            foreach (var contour in contours)
            {
                if (contour == null || contour.Count == 0) continue;
                if (contour.Count == 1)
                {
                    AddSegment(contour[0], contour[0]);
                    continue;
                }
                for (int i = 0; i + 1 < contour.Count; i++)
                {
                    AddSegment(contour[i], contour[i + 1]);
                    totalLength += PolylineOps.Dist(contour[i], contour[i + 1]);
                    segmentCount++;
                }
            }

            _cell = _requestedCell > 1e-9 ? _requestedCell
                : segmentCount > 0 && totalLength > 1e-9
                    ? Math.Max(4.0 * totalLength / segmentCount, 1e-6)
                    : 1.0;
            if (segmentCount > 0 && totalLength > 1e-9)
                _cell = Math.Max(_cell, 0.5 * totalLength / segmentCount);

            // DDA insertion is proportional to segment traversal length. Grow a
            // pathological tiny requested cell until the index is bounded.
            const double Budget = 2_000_000.0;
            for (int guard = 0; guard < 32; guard++)
            {
                double estimate = 0.0;
                foreach (var seg in _segs)
                {
                    estimate += 1.0 + (Math.Abs(seg.A.X - seg.B.X)
                        + Math.Abs(seg.A.Y - seg.B.Y) + Math.Abs(seg.A.Z - seg.B.Z)) / _cell;
                    if (estimate > Budget) break;
                }
                if (estimate <= Budget) break;
                _cell *= Math.Max(1.5, estimate / Budget);
            }

            _minX = _minY = _minZ = int.MaxValue;
            _maxX = _maxY = _maxZ = int.MinValue;
            for (int i = 0; i < _segs.Count; i++)
            {
                var seg = _segs[i];
                int x0 = Floor(Math.Min(seg.A.X, seg.B.X)), x1 = Floor(Math.Max(seg.A.X, seg.B.X));
                int y0 = Floor(Math.Min(seg.A.Y, seg.B.Y)), y1 = Floor(Math.Max(seg.A.Y, seg.B.Y));
                int z0 = Floor(Math.Min(seg.A.Z, seg.B.Z)), z1 = Floor(Math.Max(seg.A.Z, seg.B.Z));
                _minX = Math.Min(_minX, x0); _maxX = Math.Max(_maxX, x1);
                _minY = Math.Min(_minY, y0); _maxY = Math.Max(_maxY, y1);
                _minZ = Math.Min(_minZ, z0); _maxZ = Math.Max(_maxZ, z1);
                AddTraversedCells(i, seg.A, seg.B);
            }
            if (_stamp.Length < _segs.Count) _stamp = new int[_segs.Count];
            else Array.Clear(_stamp, 0, _stamp.Length);
        }

        void AddSegment(Vec3 a, Vec3 b)
        {
            _segs.Add((a, b));
            _minWorldX = Math.Min(_minWorldX, Math.Min(a.X, b.X));
            _minWorldY = Math.Min(_minWorldY, Math.Min(a.Y, b.Y));
            _minWorldZ = Math.Min(_minWorldZ, Math.Min(a.Z, b.Z));
            _maxWorldX = Math.Max(_maxWorldX, Math.Max(a.X, b.X));
            _maxWorldY = Math.Max(_maxWorldY, Math.Max(a.Y, b.Y));
            _maxWorldZ = Math.Max(_maxWorldZ, Math.Max(a.Z, b.Z));
        }

        int Floor(double value) => (int)Math.Floor(value / _cell);

        void AddTraversedCells(int segment, Vec3 a, Vec3 b)
        {
            int x = Floor(a.X), y = Floor(a.Y), z = Floor(a.Z);
            int ex = Floor(b.X), ey = Floor(b.Y), ez = Floor(b.Z);
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            int sx = Math.Sign(dx), sy = Math.Sign(dy), sz = Math.Sign(dz);
            double tx = NextBoundary(a.X, x, sx, dx), ty = NextBoundary(a.Y, y, sy, dy), tz = NextBoundary(a.Z, z, sz, dz);
            double dtx = StepDelta(dx), dty = StepDelta(dy), dtz = StepDelta(dz);
            int maxSteps = 4 + Math.Abs(ex - x) + Math.Abs(ey - y) + Math.Abs(ez - z);
            int guard = 0;
            while (true)
            {
                AddCell((x, y, z), segment);
                if (x == ex && y == ey && z == ez) break;
                if (++guard > maxSteps) break;
                if (tx <= ty && tx <= tz) { x += sx; tx += dtx; }
                else if (ty <= tz) { y += sy; ty += dty; }
                else { z += sz; tz += dtz; }
            }
        }

        double NextBoundary(double value, int cell, int step, double delta)
        {
            if (step == 0) return double.PositiveInfinity;
            double boundary = (step > 0 ? cell + 1 : cell) * _cell;
            return (boundary - value) / delta;
        }

        static double StepDelta(double delta) => Math.Abs(delta) < 1e-18
            ? double.PositiveInfinity : 1.0 / Math.Abs(delta);

        void AddCell((int x, int y, int z) key, int segment)
        {
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(2);
            list.Add(segment);
        }

        /// <summary>Nearest point on any segment. An optional finite distance cap
        /// lets callers reject a disconnected object without searching through empty
        /// space; the default remains the exact historical query.</summary>
        public (Vec3 q, double dist) Nearest(Vec3 p, double maxDistance = double.PositiveInfinity)
        {
            if (_segs.Count == 0) return (p, double.MaxValue);
            if (double.IsNaN(maxDistance) || maxDistance < 0.0) maxDistance = double.PositiveInfinity;
            if (maxDistance < double.PositiveInfinity && LowerBoundToWorld(p) > maxDistance)
                return (p, double.MaxValue);

            int cx = Floor(p.X), cy = Floor(p.Y), cz = Floor(p.Z);
            int maxRing = Math.Max(Math.Max(Math.Max(cx - _minX, _maxX - cx),
                                             Math.Max(cy - _minY, _maxY - cy)),
                                   Math.Max(cz - _minZ, _maxZ - cz));
            if (maxRing > 64)
                return ScanAll(p, maxDistance);

            _query++;
            if (_query == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _query = 1; }
            double best = maxDistance;
            Vec3 bestPoint = p;
            for (int r = 0; r <= maxRing; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                        for (int dz = -r; dz <= r; dz++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != r) continue;
                            if (!_cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                            foreach (int s in list)
                            {
                                if (_stamp[s] == _query) continue;
                                _stamp[s] = _query;
                                var q = ClosestOnSegment(p, _segs[s].A, _segs[s].B);
                                double d = PolylineOps.Dist(p, q);
                                if (d < best) { best = d; bestPoint = q; }
                            }
                        }
                if (best <= r * _cell) break;
            }
            return best == maxDistance && maxDistance < double.PositiveInfinity
                ? (p, double.MaxValue) : (bestPoint, best);
        }

        (Vec3 q, double dist) ScanAll(Vec3 p, double maxDistance)
        {
            double best = maxDistance;
            Vec3 bestPoint = p;
            for (int i = 0; i < _segs.Count; i++)
            {
                var q = ClosestOnSegment(p, _segs[i].A, _segs[i].B);
                double d = PolylineOps.Dist(p, q);
                if (d < best) { best = d; bestPoint = q; }
            }
            return best == maxDistance && maxDistance < double.PositiveInfinity
                ? (p, double.MaxValue) : (bestPoint, best);
        }

        double LowerBoundToWorld(Vec3 p)
        {
            double dx = p.X < _minWorldX ? _minWorldX - p.X : p.X > _maxWorldX ? p.X - _maxWorldX : 0.0;
            double dy = p.Y < _minWorldY ? _minWorldY - p.Y : p.Y > _maxWorldY ? p.Y - _maxWorldY : 0.0;
            double dz = p.Z < _minWorldZ ? _minWorldZ - p.Z : p.Z > _maxWorldZ ? p.Z - _maxWorldZ : 0.0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static Vec3 ClosestOnSegment(Vec3 p, Vec3 a, Vec3 b)
        {
            double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            double len2 = ux * ux + uy * uy + uz * uz;
            if (len2 < 1e-18) return a;
            double t = ((p.X - a.X) * ux + (p.Y - a.Y) * uy + (p.Z - a.Z) * uz) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return new Vec3(a.X + t * ux, a.Y + t * uy, a.Z + t * uz);
        }
    }
}
