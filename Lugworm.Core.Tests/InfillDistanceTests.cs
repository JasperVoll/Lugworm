using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class InfillDistanceTests
{
    static IReadOnlyList<Vec3>[] Box(double z) => new IReadOnlyList<Vec3>[]
    {
        new Vec3[] { new(0, 0, z), new(100, 0, z), new(100, 100, z), new(0, 100, z), new(0, 0, z) }
    };

    [Fact]
    public void ZigzagPitchChangesWithoutMovingWallsOrInfillBoundary()
    {
        var dense = LayerBuilder.BuildTagged(Box(2),
            new LayerBuildOptions { Level = 2, WallCount = 2, Infill = 1, WallSpacing = 5, InfillSpacing = 10, AngleDeg = 0 });
        var sparse = LayerBuilder.BuildTagged(Box(2),
            new LayerBuildOptions { Level = 2, WallCount = 2, Infill = 1, WallSpacing = 5, InfillSpacing = 20, AngleDeg = 0 });
        Assert.Equal(dense.Paths[1].Points, sparse.Paths[1].Points);
        foreach (var pair in new[] { (dense, 10.0), (sparse, 20.0) })
        {
            var points = pair.Item1.Paths.Where(p => p.Role == PathRole.Infill).SelectMany(p => p.Points).ToArray();
            Assert.Equal(10, points.Min(p => p.X), 6);
            Assert.Equal(90, points.Max(p => p.X), 6);
            var rows = points.Select(p => p.Y).Distinct().OrderBy(y => y).ToArray();
            Assert.True(rows.Length >= 3);
            for (int i = 1; i < rows.Length; i++) Assert.Equal(pair.Item2, rows[i] - rows[i - 1], 6);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void IndependentDistancesRemainOnConicalSurface(int mode)
    {
        const double level = 40, angle = 0.2;
        var perimeters = Box(level).Select(r => (IReadOnlyList<Vec3>)r
            .Select(p => Conical.InversePt(p, angle, 0, 0)).ToArray()).ToArray();
        var result = LayerBuilder.BuildConicalTagged(perimeters, new LayerBuildOptions
        {
            Level = level,
            WallCount = 2,
            Infill = mode,
            WallSpacing = 5,
            InfillSpacing = 12,
            AngleDeg = 0,
            ConeAngleRad = angle,
            Cx = 0,
            Cy = 0,
        });
        Assert.Contains(result.Paths, p => p.Role == PathRole.Infill);
        foreach (var path in result.Paths)
            foreach (var p in path.Points)
                Assert.Equal(level - Math.Tan(angle) * Math.Sqrt(p.X * p.X + p.Y * p.Y), p.Z, 6);
        var walls = result.Paths.Where(p => p.Role == PathRole.Wall).ToArray();
        Assert.Equal(5, walls[1].Points.Min(p => p.X), 6);
    }
}
