using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>One straight reference segment of the intended path, machine frame, mm.
    /// Segments are explicit: two consecutive segments are joined only where the first
    /// ends exactly where the second starts, so a travel gap or a discontinuity is never
    /// bridged by an invented line.</summary>
    public readonly struct LivePathSegment
    {
        public Vec3 A { get; }
        public Vec3 B { get; }
        public LivePathSegment(Vec3 a, Vec3 b) { A = a; B = b; }

        internal bool Finite => Num.Finite(A) && Num.Finite(B);
    }

    /// <summary>Nearest geometric path distance of one observation. <see cref="Available"/>
    /// false means the comparison could not be made - an invalid reference, an invalid
    /// query or an exhausted work budget - and must be shown as unavailable, never as
    /// zero deviation.</summary>
    public readonly struct LivePathDeviationResult
    {
        public bool Available { get; }
        /// <summary>Nearest distance to the intended path, mm; NaN when unavailable.</summary>
        public double DistanceMm { get; }
        /// <summary>Index of the nearest segment, -1 when unavailable.</summary>
        public int SegmentIndex { get; }
        /// <summary>True when another, geometrically distinct place on the path is within
        /// the tolerance of the nearest distance (a crossing, a repeated pass, a nearby
        /// layer). The distance is still correct; the correspondence is not.</summary>
        public bool Ambiguous { get; }
        /// <summary>Segments examined by this query.</summary>
        public int SegmentChecks { get; }
        /// <summary>Arc length along the path, mm, of the nearest point; NaN when
        /// unavailable. Segment order is path order, so this is the progress the file has
        /// made when the machine is at that point.</summary>
        public double PathPosition { get; }
        /// <summary>The nearest point on the path itself, machine frame; default when
        /// unavailable.</summary>
        public Vec3 Point { get; }

        public LivePathDeviationResult(bool available, double distanceMm, int segmentIndex, bool ambiguous, int segmentChecks)
            : this(available, distanceMm, segmentIndex, ambiguous, segmentChecks, double.NaN, default(Vec3)) { }

        public LivePathDeviationResult(bool available, double distanceMm, int segmentIndex, bool ambiguous,
                                       int segmentChecks, double pathPosition, Vec3 point)
        {
            Available = available; DistanceMm = distanceMm; SegmentIndex = segmentIndex;
            Ambiguous = ambiguous; SegmentChecks = segmentChecks; PathPosition = pathPosition; Point = point;
        }

        public static LivePathDeviationResult Unavailable(int checks = 0)
            => new LivePathDeviationResult(false, double.NaN, -1, false, checks);
    }

    /// <summary>Bounded spatial index over an intended toolpath, answering "how far is this
    /// observed position from the nearest place on the path". Built once per job revision;
    /// queried at telemetry cadence, never per animation frame.</summary>
    /// <remarks>
    /// <para>The metric is nearest geometric path distance in 3D. It does not measure
    /// following error in time, missed physical steps or which segment was actually
    /// executing; a nearby layer, a crossing or a repeated pass can be as close as the
    /// intended one, and such cases are reported <see cref="LivePathDeviationResult.Ambiguous"/>
    /// rather than silently resolved.</para>
    /// <para>The index is an axis-aligned bounding-volume tree over immutable copies of
    /// the segments. A query prunes subtrees whose box lies farther than the best distance
    /// plus the tolerance, counts every segment it examines, and gives up - unavailable,
    /// not approximate - when that count reaches the caller's budget.</para>
    /// <para>The segments are also parametrized by ARC LENGTH in the order they were given,
    /// which is path order (2026-09-16): <see cref="PointAt"/> maps a progress value back
    /// onto the path, <see cref="Locate"/> searches only a window of progress, and a query
    /// reports the progress of its nearest point. A break between two segments (the second
    /// does not start where the first ended) adds no length; the parameter simply continues,
    /// and <see cref="Continuous"/> says whether two progress values have one between them.</para>
    /// </remarks>
    public sealed class LivePathDeviation
    {
        public const int DefaultMaxSegments = 2_000_000;
        public const double DefaultToleranceMm = 1e-6;
        public const int DefaultMaxSegmentChecks = 4096;
        const int LeafSize = 8;

        readonly LivePathSegment[] _segments;
        readonly bool _valid;
        // flattened tree: per node bounds + either children or a leaf range
        readonly double[] _minX = Array.Empty<double>(), _minY = Array.Empty<double>(), _minZ = Array.Empty<double>();
        readonly double[] _maxX = Array.Empty<double>(), _maxY = Array.Empty<double>(), _maxZ = Array.Empty<double>();
        readonly int[] _left = Array.Empty<int>(), _right = Array.Empty<int>();   // -1 for leaves
        readonly int[] _leafStart = Array.Empty<int>(), _leafCount = Array.Empty<int>();
        readonly int[] _order = Array.Empty<int>();   // segment indices in leaf order
        // arc-length parametrization in segment (= path) order: _start[i] is the progress at
        // segment i's A, and _joined[i] says segment i begins exactly where i-1 ended
        readonly double[] _start = Array.Empty<double>();
        readonly bool[] _joined = Array.Empty<bool>();
        readonly double _length;

        /// <summary>Number of reference segments (copied; the caller's array may change).</summary>
        public int SegmentCount => _segments.Length;

        /// <summary>False when the reference is unusable: empty, non-finite, or larger than
        /// the segment budget. Every query then reports unavailable.</summary>
        public bool Valid => _valid;

        /// <summary>Total arc length of the segments, mm; 0 when invalid.</summary>
        public double Length => _length;

        public LivePathDeviation(IReadOnlyList<LivePathSegment> segments, int maxSegments = DefaultMaxSegments)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            _segments = new LivePathSegment[segments.Count];
            bool ok = segments.Count > 0 && segments.Count <= maxSegments;
            for (int i = 0; i < _segments.Length; i++)
            {
                _segments[i] = segments[i];
                if (!_segments[i].Finite) ok = false;
            }
            _valid = ok;
            if (!ok) return;

            int n = _segments.Length;
            _start = new double[n];
            _joined = new bool[n];
            double acc = 0;
            for (int i = 0; i < n; i++)
            {
                _start[i] = acc;
                acc += _segments[i].A.DistanceTo(_segments[i].B);
                _joined[i] = i > 0 && SamePoint(_segments[i - 1].B, _segments[i].A);
            }
            _length = acc;
            _order = new int[n];
            for (int i = 0; i < n; i++) _order[i] = i;
            var cx = new double[n]; var cy = new double[n]; var cz = new double[n];
            for (int i = 0; i < n; i++)
            {
                var s = _segments[i];
                cx[i] = 0.5 * (s.A.X + s.B.X); cy[i] = 0.5 * (s.A.Y + s.B.Y); cz[i] = 0.5 * (s.A.Z + s.B.Z);
            }
            int maxNodes = 2 * ((n + LeafSize - 1) / LeafSize) + 1;
            var minX = new List<double>(maxNodes); var minY = new List<double>(maxNodes); var minZ = new List<double>(maxNodes);
            var maxX = new List<double>(maxNodes); var maxY = new List<double>(maxNodes); var maxZ = new List<double>(maxNodes);
            var left = new List<int>(maxNodes); var right = new List<int>(maxNodes);
            var leafStart = new List<int>(maxNodes); var leafCount = new List<int>(maxNodes);

            // iterative build: each stack entry is (node index, range)
            var stack = new Stack<(int node, int start, int count)>();
            int root = NewNode(minX, minY, minZ, maxX, maxY, maxZ, left, right, leafStart, leafCount);
            stack.Push((root, 0, n));
            while (stack.Count > 0)
            {
                var (node, start, count) = stack.Pop();
                double bx0 = double.PositiveInfinity, by0 = double.PositiveInfinity, bz0 = double.PositiveInfinity;
                double bx1 = double.NegativeInfinity, by1 = double.NegativeInfinity, bz1 = double.NegativeInfinity;
                for (int k = start; k < start + count; k++)
                {
                    var s = _segments[_order[k]];
                    bx0 = Math.Min(bx0, Math.Min(s.A.X, s.B.X)); bx1 = Math.Max(bx1, Math.Max(s.A.X, s.B.X));
                    by0 = Math.Min(by0, Math.Min(s.A.Y, s.B.Y)); by1 = Math.Max(by1, Math.Max(s.A.Y, s.B.Y));
                    bz0 = Math.Min(bz0, Math.Min(s.A.Z, s.B.Z)); bz1 = Math.Max(bz1, Math.Max(s.A.Z, s.B.Z));
                }
                minX[node] = bx0; minY[node] = by0; minZ[node] = bz0;
                maxX[node] = bx1; maxY[node] = by1; maxZ[node] = bz1;
                if (count <= LeafSize)
                {
                    leafStart[node] = start; leafCount[node] = count;
                    continue;
                }
                // split at the median centroid along the widest axis
                double ex = bx1 - bx0, ey = by1 - by0, ez = bz1 - bz0;
                double[] key = ex >= ey && ex >= ez ? cx : ey >= ez ? cy : cz;
                int mid = start + count / 2;
                Select(_order, key, start, start + count - 1, mid);
                int l = NewNode(minX, minY, minZ, maxX, maxY, maxZ, left, right, leafStart, leafCount);
                int r = NewNode(minX, minY, minZ, maxX, maxY, maxZ, left, right, leafStart, leafCount);
                left[node] = l; right[node] = r;
                stack.Push((l, start, mid - start));
                stack.Push((r, mid, start + count - mid));
            }
            _minX = minX.ToArray(); _minY = minY.ToArray(); _minZ = minZ.ToArray();
            _maxX = maxX.ToArray(); _maxY = maxY.ToArray(); _maxZ = maxZ.ToArray();
            _left = left.ToArray(); _right = right.ToArray();
            _leafStart = leafStart.ToArray(); _leafCount = leafCount.ToArray();
        }

        static int NewNode(List<double> minX, List<double> minY, List<double> minZ,
                           List<double> maxX, List<double> maxY, List<double> maxZ,
                           List<int> left, List<int> right, List<int> leafStart, List<int> leafCount)
        {
            minX.Add(0); minY.Add(0); minZ.Add(0); maxX.Add(0); maxY.Add(0); maxZ.Add(0);
            left.Add(-1); right.Add(-1); leafStart.Add(0); leafCount.Add(0);
            return left.Count - 1;
        }

        /// <summary>Quickselect: partitions <paramref name="order"/>[lo..hi] so that the
        /// element at <paramref name="k"/> is the k-th smallest by <paramref name="key"/>.</summary>
        static void Select(int[] order, double[] key, int lo, int hi, int k)
        {
            while (lo < hi)
            {
                double pivot = key[order[(lo + hi) / 2]];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (key[order[i]] < pivot) i++;
                    while (key[order[j]] > pivot) j--;
                    if (i <= j)
                    {
                        int t = order[i]; order[i] = order[j]; order[j] = t;
                        i++; j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;
            }
        }

        /// <summary>Consecutive points become consecutive segments; nothing else is joined.</summary>
        public static LivePathDeviation FromPolyline(IReadOnlyList<Vec3> points, int maxSegments = DefaultMaxSegments)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            var segments = new LivePathSegment[Math.Max(0, points.Count - 1)];
            for (int i = 0; i < segments.Length; i++) segments[i] = new LivePathSegment(points[i], points[i + 1]);
            return new LivePathDeviation(segments, maxSegments);
        }

        /// <summary>Indexes all sampled segments except explicitly uncommanded gaps.</summary>
        public static LivePathDeviation FromPolyline(IReadOnlyList<Vec3> points,
            IReadOnlyList<bool> breakBefore, int maxSegments = DefaultMaxSegments)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (breakBefore == null) throw new ArgumentNullException(nameof(breakBefore));
            if (breakBefore.Count != points.Count) throw new ArgumentException("Break count must match points.", nameof(breakBefore));
            var segments = new List<LivePathSegment>();
            for (int i = 1; i < points.Count; i++)
                if (!breakBefore[i]) segments.Add(new LivePathSegment(points[i - 1], points[i]));
            return new LivePathDeviation(segments, maxSegments);
        }

        /// <summary>Nearest path distance of <paramref name="point"/> (machine frame, mm).</summary>
        /// <param name="toleranceMm">Two distinct places on the path closer than this to
        /// the nearest distance make the correspondence ambiguous. Applied to distances,
        /// not squared distances.</param>
        /// <param name="maxSegmentChecks">Work budget; exceeding it yields unavailable.</param>
        public LivePathDeviationResult Query(Vec3 point, double toleranceMm = DefaultToleranceMm,
                                             int maxSegmentChecks = DefaultMaxSegmentChecks)
        {
            if (!_valid) return LivePathDeviationResult.Unavailable();
            if (double.IsNaN(point.X) || double.IsInfinity(point.X)
                || double.IsNaN(point.Y) || double.IsInfinity(point.Y)
                || double.IsNaN(point.Z) || double.IsInfinity(point.Z))
                return LivePathDeviationResult.Unavailable();
            if (double.IsNaN(toleranceMm) || toleranceMm < 0 || double.IsInfinity(toleranceMm))
                return LivePathDeviationResult.Unavailable();
            if (maxSegmentChecks <= 0) return LivePathDeviationResult.Unavailable();

            double bestD = double.PositiveInfinity; int bestI = -1; Vec3 bestP = default;
            double secondD = double.PositiveInfinity;
            int checks = 0;

            // depth-first with the nearer child first; explicit stack so deep paths cannot
            // overflow, and a box is pruned once it cannot beat best + tolerance
            var stack = new Stack<int>(64);
            stack.Push(0);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (BoxDistance(node, point) > bestD + toleranceMm) continue;
                if (_left[node] < 0)
                {
                    int start = _leafStart[node], end = start + _leafCount[node];
                    for (int k = start; k < end; k++)
                    {
                        if (checks >= maxSegmentChecks)
                            return LivePathDeviationResult.Unavailable(checks);
                        checks++;
                        Consider(_order[k], point, toleranceMm, ref bestD, ref bestI, ref bestP, ref secondD);
                    }
                    continue;
                }
                int l = _left[node], r = _right[node];
                double dl = BoxDistance(l, point), dr = BoxDistance(r, point);
                if (dl <= dr) { stack.Push(r); stack.Push(l); }
                else { stack.Push(l); stack.Push(r); }
            }
            if (bestI < 0) return LivePathDeviationResult.Unavailable(checks);
            bool ambiguous = !double.IsPositiveInfinity(secondD) && secondD - bestD <= toleranceMm;
            return new LivePathDeviationResult(true, bestD, bestI, ambiguous, checks, PositionOf(bestI, bestP), bestP);
        }

        /// <summary>Nearest path distance of <paramref name="point"/> among the segments
        /// whose progress overlaps [<paramref name="fromPosition"/>, <paramref name="toPosition"/>]
        /// only. This is how a display that knows roughly how far the file has got avoids
        /// matching a neighbouring layer, a crossing or an earlier pass that happens to be
        /// nearer in space: those are outside the window, so they are never candidates.</summary>
        /// <remarks>A linear scan of the window, no tree: at telemetry cadence a window of a
        /// few hundred millimetres is a few hundred segments. Unavailable when the reference
        /// is invalid, the point or the window is not finite, or the window is empty.</remarks>
        public LivePathDeviationResult Locate(Vec3 point, double fromPosition, double toPosition,
                                              double toleranceMm = DefaultToleranceMm)
        {
            if (!_valid || !Num.Finite(point)) return LivePathDeviationResult.Unavailable();
            if (double.IsNaN(fromPosition) || double.IsNaN(toPosition) || double.IsInfinity(fromPosition)
                || double.IsInfinity(toPosition) || toPosition < fromPosition)
                return LivePathDeviationResult.Unavailable();
            if (double.IsNaN(toleranceMm) || toleranceMm < 0 || double.IsInfinity(toleranceMm))
                return LivePathDeviationResult.Unavailable();
            if (toPosition < 0 || fromPosition > _length) return LivePathDeviationResult.Unavailable();

            double bestD = double.PositiveInfinity; int bestI = -1; Vec3 bestP = default;
            double secondD = double.PositiveInfinity;
            int checks = 0;
            for (int i = SegmentAt(fromPosition); i < _segments.Length && _start[i] <= toPosition; i++)
            {
                checks++;
                Consider(i, point, toleranceMm, ref bestD, ref bestI, ref bestP, ref secondD, fromPosition, toPosition);
            }
            if (bestI < 0) return LivePathDeviationResult.Unavailable(checks);
            bool ambiguous = !double.IsPositiveInfinity(secondD) && secondD - bestD <= toleranceMm;
            return new LivePathDeviationResult(true, bestD, bestI, ambiguous, checks, PositionOf(bestI, bestP), bestP);
        }

        /// <summary>The point on the path at progress <paramref name="position"/> (mm of arc
        /// length from the start), clamped to the path's ends. A progress inside a break
        /// resolves onto the segment before it.</summary>
        public Vec3 PointAt(double position)
        {
            if (!_valid) return default(Vec3);
            if (double.IsNaN(position) || position <= 0) return _segments[0].A;
            if (position >= _length) return _segments[_segments.Length - 1].B;
            int i = SegmentAt(position);
            var s = _segments[i];
            double len = s.A.DistanceTo(s.B);
            if (len <= 0) return s.A;
            double t = (position - _start[i]) / len;
            if (t >= 1) return s.B;
            return s.A + (s.B - s.A) * t;
        }

        /// <summary>True when the path commands motion all the way between two progress
        /// values: no segment strictly inside the range begins somewhere other than where
        /// its predecessor ended.</summary>
        public bool Continuous(double a, double b)
        {
            if (!_valid || double.IsNaN(a) || double.IsNaN(b)) return false;
            int lo = SegmentAt(Math.Min(a, b)), hi = SegmentAt(Math.Max(a, b));
            for (int k = lo + 1; k <= hi; k++) if (!_joined[k]) return false;
            return true;
        }

        /// <summary>Progress, mm, of a point known to lie on segment
        /// <paramref name="segmentIndex"/>.</summary>
        public double PositionOf(int segmentIndex, Vec3 pointOnSegment)
        {
            if (!_valid || segmentIndex < 0 || segmentIndex >= _segments.Length) return double.NaN;
            return _start[segmentIndex] + _segments[segmentIndex].A.DistanceTo(pointOnSegment);
        }

        /// <summary>The last segment whose start is at or before <paramref name="position"/>,
        /// clamped to the segment range.</summary>
        int SegmentAt(double position)
        {
            int lo = 0, hi = _segments.Length - 1;
            if (position <= 0) return 0;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (_start[mid] <= position) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>One candidate segment against the running best and runner-up. With a
        /// progress window the candidate is the nearest point of the segment INSIDE the
        /// window, so a long segment that merely touches the window cannot answer from
        /// outside it.</summary>
        void Consider(int idx, Vec3 point, double toleranceMm,
                      ref double bestD, ref int bestI, ref Vec3 bestP, ref double secondD,
                      double fromPosition = double.NegativeInfinity, double toPosition = double.PositiveInfinity)
        {
            double d = Distance(_segments[idx], point, out Vec3 closest);
            if (!double.IsNegativeInfinity(fromPosition) || !double.IsPositiveInfinity(toPosition))
            {
                var seg = _segments[idx];
                double len = seg.A.DistanceTo(seg.B);
                double lo = fromPosition - _start[idx], hi = toPosition - _start[idx];
                double along = seg.A.DistanceTo(closest);
                if (len > 0 && (along < lo || along > hi))
                {
                    double clamped = along < lo ? lo : hi;
                    if (clamped < 0) clamped = 0; else if (clamped > len) clamped = len;
                    closest = seg.A + (seg.B - seg.A) * (clamped / len);
                    d = point.DistanceTo(closest);
                }
            }
            if (d < bestD)
            {
                // the old best becomes the runner-up unless it is the same place on the
                // path as the new best
                if (bestI >= 0 && !SameJoint(bestI, idx, bestP, closest, toleranceMm))
                    secondD = bestD;
                bestD = d; bestI = idx; bestP = closest;
            }
            else if (d < secondD && !SameJoint(bestI, idx, bestP, closest, toleranceMm))
            {
                secondD = d;
            }
        }

        static bool SamePoint(Vec3 v, Vec3 w)
            => v.X.Equals(w.X) && v.Y.Equals(w.Y) && v.Z.Equals(w.Z);

        /// <summary>True when two candidates are the shared vertex of consecutive, joined
        /// segments - the same place on the path, not two places.</summary>
        bool SameJoint(int i, int j, Vec3 pi, Vec3 pj, double tol)
        {
            if (i < 0 || j < 0) return false;
            int lo = Math.Min(i, j), hi = Math.Max(i, j);
            if (hi - lo != 1) return false;
            var v = _segments[lo].B;
            if (!SamePoint(v, _segments[hi].A)) return false;
            return pi.DistanceTo(v) <= tol && pj.DistanceTo(v) <= tol;
        }

        double BoxDistance(int node, Vec3 p)
        {
            double dx = p.X < _minX[node] ? _minX[node] - p.X : p.X > _maxX[node] ? p.X - _maxX[node] : 0;
            double dy = p.Y < _minY[node] ? _minY[node] - p.Y : p.Y > _maxY[node] ? p.Y - _maxY[node] : 0;
            double dz = p.Z < _minZ[node] ? _minZ[node] - p.Z : p.Z > _maxZ[node] ? p.Z - _maxZ[node] : 0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Point-to-segment distance with the parameter clamped to [0, 1]; a
        /// degenerate segment is its point.</summary>
        public static double Distance(LivePathSegment s, Vec3 p, out Vec3 closest)
        {
            var ab = s.B - s.A;
            double len2 = ab.Dot(ab);
            if (len2 <= 0)
            {
                closest = s.A;
                return p.DistanceTo(s.A);
            }
            double t = (p - s.A).Dot(ab) / len2;
            if (t <= 0) closest = s.A;
            else if (t >= 1) closest = s.B;
            else closest = s.A + ab * t;
            return p.DistanceTo(closest);
        }
    }
}
