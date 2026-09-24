using System;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests
{
    public class LivePathDeviationTests
    {
        private static Vec3 P(double x, double y = 0, double z = 0) => new Vec3(x, y, z);
        private static LivePathSegment S(Vec3 a, Vec3 b) => new LivePathSegment(a, b);

        [Fact]
        public void ReferenceImplementationExists()
        {
            Assert.NotNull(typeof(Vec3).Assembly.GetType("Lugworm.Core.Duet.LivePathDeviation"));
        }

        [Theory]
        [InlineData(5, 0, 0, 0)]
        [InlineData(5, 3, 4, 5)]
        [InlineData(-3, 4, 0, 5)]
        [InlineData(13, 0, 4, 5)]
        public void MeasuresClampedThreeDimensionalDistance(double x, double y, double z, double expected)
        {
            var index = new LivePathDeviation(new[] { S(P(0), P(10)) });
            var result = index.Query(P(x, y, z));
            Assert.True(result.Available);
            Assert.Equal(expected, result.DistanceMm, 10);
            Assert.False(result.Ambiguous);
            Assert.Equal(0, result.SegmentIndex);
        }

        [Fact]
        public void DegenerateSegmentIsAnIsolatedPoint()
        {
            var result = new LivePathDeviation(new[] { S(P(2, 3), P(2, 3)) }).Query(P(2, 3, 7));
            Assert.True(result.Available);
            Assert.Equal(7, result.DistanceMm, 10);
        }

        [Fact]
        public void ExplicitSegmentsDoNotBridgeDiscontinuities()
        {
            var result = new LivePathDeviation(new[] { S(P(0), P(1)), S(P(10), P(11)) }).Query(P(5));
            Assert.Equal(4, result.DistanceMm, 10);
        }

        [Fact]
        public void SampledBreaksKeepRestartedMotionButExcludeTheUncommandedGap()
        {
            var index = LivePathDeviation.FromPolyline(new[] { P(0), P(10), P(40), P(50) },
                new[] { true, false, true, false });
            Assert.Equal(15, index.Query(P(25)).DistanceMm, 10);
            Assert.Equal(0, index.Query(P(45)).DistanceMm, 10);
        }

        [Fact]
        public void CrossingAndRepeatedPassesAreAmbiguous()
        {
            var crossing = new LivePathDeviation(new[] { S(P(-1), P(1)), S(P(0, -1), P(0, 1)) });
            Assert.True(crossing.Query(P(0)).Ambiguous);
            Assert.False(crossing.Query(P(0.5)).Ambiguous);
            var repeated = new LivePathDeviation(new[] { S(P(0), P(10)), S(P(10), P(0)) });
            Assert.True(repeated.Query(P(4, 3)).Ambiguous);
        }

        [Fact]
        public void ToleranceAppliesToDistanceNotSquaredDistance()
        {
            var index = new LivePathDeviation(new[] { S(P(0, 10), P(1, 10)), S(P(0, 10.0000005), P(1, 10.0000005)) });
            Assert.True(index.Query(P(0.5), 1e-6).Ambiguous);
            Assert.False(index.Query(P(0.5), 1e-8).Ambiguous);
        }

        [Fact]
        public void SnapshotIsImmutableAndPolylineMakesOnlyConsecutiveSegments()
        {
            var source = new[] { S(P(0), P(10)) };
            var index = new LivePathDeviation(source);
            source[0] = S(P(100), P(110));
            Assert.Equal(0, index.Query(P(4)).DistanceMm);
            var points = new[] { P(0), P(10), P(10, 10) };
            var polyline = LivePathDeviation.FromPolyline(points);
            points[1] = P(500);
            Assert.Equal(2, polyline.SegmentCount);
            Assert.Equal(1, polyline.Query(P(11, 5)).DistanceMm, 10);
        }

        [Fact]
        public void InvalidOrIncompleteReferencesAndQueriesAreUnavailable()
        {
            AssertUnavailable(new LivePathDeviation(Array.Empty<LivePathSegment>()).Query(P(0)));
            AssertUnavailable(new LivePathDeviation(new[] { S(P(0), P(double.NaN)) }).Query(P(0)));
            AssertUnavailable(new LivePathDeviation(new[] { S(P(0), P(1)), S(P(2), P(3)) }, 1).Query(P(0)));
            var index = new LivePathDeviation(new[] { S(P(0), P(1)) });
            AssertUnavailable(index.Query(P(double.PositiveInfinity)));
            AssertUnavailable(index.Query(P(0), double.NaN));
            AssertUnavailable(index.Query(P(0), -1));
            AssertUnavailable(index.Query(P(0), maxSegmentChecks: 0));
        }

        [Fact]
        public void ExhaustedQueryBudgetNeverReturnsApproximateDistanceAsAccurate()
        {
            var segments = new LivePathSegment[100];
            for (int i = 0; i < segments.Length; i++) segments[i] = S(P(-1), P(1));
            var result = new LivePathDeviation(segments).Query(P(0), maxSegmentChecks: 3);
            AssertUnavailable(result);
            Assert.InRange(result.SegmentChecks, 0, 3);
        }

        [Fact]
        public void SpatialIndexPrunesWellSeparatedSegmentsAndRetainsOriginalIndex()
        {
            var segments = new LivePathSegment[10000];
            for (int i = 0; i < segments.Length; i++) segments[i] = S(P(i * 10), P(i * 10 + 1));
            var result = new LivePathDeviation(segments).Query(P(12340.5, 3), maxSegmentChecks: 64);
            Assert.True(result.Available);
            Assert.Equal(3, result.DistanceMm, 10);
            Assert.Equal(1234, result.SegmentIndex);
            Assert.InRange(result.SegmentChecks, 1, 64);
        }

        private static void AssertUnavailable(LivePathDeviationResult result)
        {
            Assert.False(result.Available);
            Assert.True(double.IsNaN(result.DistanceMm));
            Assert.Equal(-1, result.SegmentIndex);
        }
    }
}
