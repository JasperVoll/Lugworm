using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>An intended-path reference the continuity policy can ask "how far is this
    /// observation from the path the job says it should be on".</summary>
    /// <remarks>Answering false is the normal case, not an error: a job started outside the
    /// console has no intended path, and a sample above or below every reference layer has
    /// no comparable place on it. An unavailable comparison must be reported as unavailable,
    /// never as zero deviation.</remarks>
    public interface IIntendedPathReference
    {
        /// <summary>Nearest distance from <paramref name="point"/> (machine frame, mm) to the
        /// intended path AT THE SAMPLE'S OWN ELEVATION. False when no reference segment lies
        /// at that elevation.</summary>
        bool TryDistanceMm(Vec3 point, out double distanceMm);
    }

    /// <summary>Same-elevation nearest-distance index over an intended toolpath.</summary>
    /// <remarks>
    /// <para>Deliberately NOT the 3D nearest distance that <see cref="LivePathDeviation"/>
    /// measures. In the 2026-09-09 dome capture the 3D nearest place on the path for the
    /// worst false sample was on the NEXT layer, 18.23 mm away, while the nearest place on
    /// the layer the sample claims to be printing was 18.41 mm away: a 3D metric quietly
    /// answers a different question and understates a within-layer excursion. Layer
    /// elevations there are about 4 mm apart, so restricting the candidates to the
    /// sample's own layer is both well defined and much cheaper.</para>
    /// <para>The index is a copy of the segments bucketed by elevation. A query collects
    /// the buckets overlapping [z - window, z + window], keeps only segments whose BOTH
    /// endpoints lie inside that window, and returns the smallest exact point-to-segment
    /// distance. It never modifies, projects or replaces the queried point.</para>
    /// </remarks>
    public sealed class SameZPathIndex : IIntendedPathReference
    {
        /// <summary>Half-height of the elevation window, mm. 0.05 matches the tolerance the
        /// analysis script used, which absorbs the rounding between a reported Z of 42.598
        /// and the G-code's Z42.597 without reaching the next layer.</summary>
        public const double DefaultZWindowMm = 0.05;

        readonly double _window;
        readonly Dictionary<long, List<LivePathSegment>> _buckets = new Dictionary<long, List<LivePathSegment>>();
        readonly int _count;

        public int SegmentCount => _count;
        public double ZWindowMm => _window;
        /// <summary>False when no usable reference segment was supplied; every query then
        /// reports unavailable.</summary>
        public bool Valid => _count > 0;

        public SameZPathIndex(IReadOnlyList<LivePathSegment> segments, double zWindowMm = DefaultZWindowMm)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            _window = zWindowMm > 0 && !double.IsNaN(zWindowMm) && !double.IsInfinity(zWindowMm)
                ? zWindowMm : DefaultZWindowMm;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (!Num.Finite(s.A) || !Num.Finite(s.B)) continue;
                long lo = Bucket(Math.Min(s.A.Z, s.B.Z));
                long hi = Bucket(Math.Max(s.A.Z, s.B.Z));
                for (long b = lo; b <= hi; b++)
                {
                    if (!_buckets.TryGetValue(b, out var list)) _buckets[b] = list = new List<LivePathSegment>();
                    list.Add(s);
                }
                _count++;
            }
        }

        /// <summary>Consecutive points become consecutive segments; nothing else is joined.
        /// A gap in the point list is a gap in the reference, never an invented line.</summary>
        public static SameZPathIndex FromPolyline(IReadOnlyList<Vec3> points, double zWindowMm = DefaultZWindowMm)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            var segments = new List<LivePathSegment>(Math.Max(0, points.Count - 1));
            for (int i = 0; i + 1 < points.Count; i++) segments.Add(new LivePathSegment(points[i], points[i + 1]));
            return new SameZPathIndex(segments, zWindowMm);
        }

        public bool TryDistanceMm(Vec3 point, out double distanceMm)
        {
            distanceMm = double.NaN;
            if (_count == 0 || !Num.Finite(point)) return false;
            double best = double.PositiveInfinity;
            long lo = Bucket(point.Z - _window);
            long hi = Bucket(point.Z + _window);
            for (long b = lo; b <= hi; b++)
            {
                if (!_buckets.TryGetValue(b, out var list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    if (Math.Abs(s.A.Z - point.Z) > _window || Math.Abs(s.B.Z - point.Z) > _window) continue;
                    double d = LivePathDeviation.Distance(s, point, out _);
                    if (d < best) best = d;
                }
            }
            if (double.IsPositiveInfinity(best)) return false;
            distanceMm = best;
            return true;
        }

        long Bucket(double z) => (long)Math.Floor(z / _window);
    }
}
