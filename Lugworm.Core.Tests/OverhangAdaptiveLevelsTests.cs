using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class OverhangAdaptiveLevelsTests
{
    static (List<Vec3> V, List<(int, int, int)> F) Wall()
        => (new List<Vec3> { new(0, 0, 0), new(0, 1000, 0), new(0, 0, 20) },
            new List<(int, int, int)> { (0, 1, 2) });

    static void Face(List<Vec3> v, List<(int, int, int)> f, Vec3 a, Vec3 b, Vec3 c)
    { int i = v.Count; v.AddRange(new[] { a, b, c }); f.Add((i, i + 1, i + 2)); }

    [Fact]
    public void SmallOverhang_IsNotAveragedAway_AndRefinementStaysLocal()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 8), new(10, 0, 9), new(0, 1, 8));
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        Assert.Equal(1, levels[0]);
        Assert.Equal(3, levels[1] - levels[0], 8);
        Assert.Contains(Enumerable.Range(1, levels.Count - 1), i =>
            levels[i - 1] < 9 && levels[i] > 8 && levels[i] - levels[i - 1] < 0.6);
        Assert.Equal(3, levels.Last() - levels[levels.Count - 2], 8);
        for (int i = 1; i < levels.Count; i++)
            Assert.InRange(levels[i] - levels[i - 1], 0.5 - 1e-9, 3 + 1e-9);
        Assert.All(levels, z => Assert.True(z < 20));
    }

    [Fact]
    public void FullFaceExtent_ConstrainsLayersFarFromCentroid()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 0), new(200, 0, 20), new(0, 1, 0));
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        Assert.All(Enumerable.Range(1, levels.Count - 1), i =>
            Assert.InRange(levels[i] - levels[i - 1], 0.5, 0.6));
    }

    [Fact]
    public void Lookahead_DetectsThinFeatureBetweenUniformLevels()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 2), new(10, 0, 2.01), new(0, 1, 2));
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        // A layer may end at the feature's start, but cannot jump over it using
        // the vertical wall's hMax. Only intervals crossing it must be thin.
        int crossing = Enumerable.Range(1, levels.Count - 1)
            .First(i => levels[i] > 2 && levels[i - 1] < 2.01);
        Assert.InRange(levels[crossing] - levels[crossing - 1], 0.5, 0.501);
    }

    [Fact]
    public void GlobalHorizontalCaps_DoNotThinVerticalWalls_ButInternalLedgesDo()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 0), new(10, 0, 0), new(0, 10, 0));
        Face(v, f, new(0, 0, 20), new(10, 0, 20), new(0, 10, 20));
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        Assert.All(Enumerable.Range(1, levels.Count - 1), i =>
            Assert.Equal(3, levels[i] - levels[i - 1], 8));
        Face(v, f, new(0, 0, 1.25), new(10, 0, 1.25), new(0, 10, 1.25));
        levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        Assert.Equal(0.5, levels[1] - levels[0], 8);
    }

    [Fact]
    public void WindingTranslationAndDuplicateFaces_DoNotChangeSpacing()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 8), new(10, 0, 9), new(0, 1, 8));
        var expected = OverhangAdaptiveLevels.Build(v, f, 0, 20, 0, 0.5, 3);
        var shifted = v.Select(p => p + new Vec3(33, -21, 40)).ToList();
        var reversed = f.Select(t => (t.Item3, t.Item2, t.Item1)).ToList();
        reversed.AddRange(reversed.ToArray());
        reversed.Add((0, 0, 0)); // degenerate face
        var actual = OverhangAdaptiveLevels.Build(shifted, reversed, 40, 60, 0, 0.5, 3);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i] + 40, actual[i], 8);
        Assert.Equal(0.25, expected[0], 8);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 1)]
    [InlineData(double.NaN, 3)]
    [InlineData(1, double.PositiveInfinity)]
    public void InvalidHeights_Throw(double min, double max)
    {
        var (v, f) = Wall();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, min, max));
    }

    [Fact]
    public void ClosedExpandingGeometry_ProducesPlanarContours_AndThinsWithOverhang()
    {
        double previousStep = double.MaxValue;
        foreach (double growth in new[] { 0.0, 1.0, 10.0 })
        {
            var v = new List<Vec3>();
            foreach (double z in new[] { 0.0, 20.0 })
            {
                double radius = 5 + growth * z;
                v.AddRange(new[] { new Vec3(-radius, -radius, z), new Vec3(radius, -radius, z),
                    new Vec3(radius, radius, z), new Vec3(-radius, radius, z) });
            }
            var f = new List<(int, int, int)> { (0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7) };
            for (int i = 0; i < 4; i++)
            { int j = (i + 1) % 4; f.Add((i, j, j + 4)); f.Add((i, j + 4, i + 4)); }
            var levels = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
            double step = levels[1] - levels[0];
            Assert.True(step < previousStep);
            previousStep = step;
            var contours = Iso.PlanarContoursAtLevels(v, f, v.Select(p => p.Z).ToList(), levels, 0.001);
            Assert.Equal(levels.Count, contours.Count);
            for (int i = 0; i < levels.Count; i++)
            {
                var ring = Assert.Single(contours[i]);
                Assert.True(ring[0].DistanceTo(ring[ring.Count - 1]) < 0.001);
                Assert.All(ring, point => Assert.Equal(levels[i], point.Z, 6));
                Assert.Equal(5 + growth * levels[i], ring.Max(p => p.X), 3);
            }
        }
    }

    [Fact]
    public void CoplanarRetriangulation_PreservesSchedule()
    {
        var (v, f) = Wall();
        Face(v, f, new(0, 0, 0), new(200, 0, 20), new(0, 1, 0));
        var expected = OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 0.5, 3);
        var subdivided = new List<(int, int, int)>();
        foreach (var (a, b, c) in f)
        {
            int centre = v.Count;
            v.Add((v[a] + v[b] + v[c]) * (1.0 / 3.0));
            subdivided.Add((a, b, centre)); subdivided.Add((b, c, centre)); subdivided.Add((c, a, centre));
        }
        var actual = OverhangAdaptiveLevels.Build(v, subdivided, 0, 20, 1, 0.5, 3);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i], 8);
    }

    [Fact]
    public void InvalidGeometryOrBounds_Throw_AndLayerBudgetNeverTruncates()
    {
        var (v, f) = Wall();
        Assert.Throws<ArgumentOutOfRangeException>(() => OverhangAdaptiveLevels.Build(v, f, 20, 0, 1, 1, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => OverhangAdaptiveLevels.Build(v, f, 0, 20, double.NaN, 1, 3));
        Assert.Throws<InvalidOperationException>(() => OverhangAdaptiveLevels.Build(v, f, 0, 20, 0, 0.0001, 0.0001));
        v[0] = new Vec3(double.NaN, 0, 0);
        Assert.Throws<ArgumentException>(() => OverhangAdaptiveLevels.Build(v, f, 0, 20, 1, 1, 3));
    }
}
