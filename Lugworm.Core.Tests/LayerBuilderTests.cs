using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class LayerBuilderTests
{
    static List<Vec3> Square(double size = 100, double z = 2)
        => new()
        {
            new(0, 0, z), new(size, 0, z), new(size, size, z),
            new(0, size, z), new(0, 0, z),
        };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TaggedResult_FlattensToLegacyResult_ForEveryExistingInfillMode(int infill)
    {
        var perimeters = new[] { (IReadOnlyList<Vec3>)Square() };
        var options = new LayerBuildOptions { Level = 2, WallCount = 2, Infill = infill, WallSpacing = 10, AngleDeg = 0.35 };

        var legacy = LayerBuilder.Build(perimeters, options);
        var tagged = LayerBuilder.BuildTagged(perimeters, options);
        var flattened = tagged.Flatten();

        Assert.Equal(legacy.Count, flattened.Count);
        for (int i = 0; i < legacy.Count; i++) AssertPathEqual(legacy[i], flattened[i]);
        Assert.Equal(2, tagged.Level);
    }

    [Fact]
    public void WallIndices_AscendInward()
    {
        var tagged = LayerBuilder.BuildTagged(
            new[] { (IReadOnlyList<Vec3>)Square() },
            new LayerBuildOptions { Level = 2, WallCount = 3, Infill = 0, WallSpacing = 10, AngleDeg = 0 });

        Assert.Equal(new[] { 0, 1, 2 },
            tagged.Paths.Where(p => p.Role == PathRole.Wall).Select(p => p.WallIndex));
        Assert.All(tagged.Paths, p => Assert.True(p.Closed));
    }

    [Fact]
    public void SeparateWallAndInfillSpacing_AppliesToTheirOwnOffsets()
    {
        var tagged = LayerBuilder.BuildTagged(
            new[] { (IReadOnlyList<Vec3>)Square() },
            new LayerBuildOptions { Level = 2, WallCount = 2, Infill = 2, WallSpacing = 5, InfillSpacing = 20, AngleDeg = 0 });

        var walls = tagged.Paths.Where(p => p.Role == PathRole.Wall).ToArray();
        Assert.Equal(2, walls.Length);
        Assert.Equal(100, Extent(walls[0].Points));
        Assert.Equal(90, Extent(walls[1].Points)); // 5 mm from wall to wall

        var infill = tagged.Paths.Where(p => p.Role == PathRole.Infill).ToArray();
        Assert.True(infill.Length >= 2);
        Assert.Equal(80, Extent(infill[0].Points)); // 5 mm from last wall to infill
        Assert.Equal(40, Extent(infill[1].Points)); // 20 mm between concentric passes
    }

    [Fact]
    public void OpenSections_ArePassedThroughAndTaggedOpen()
    {
        var open = new List<Vec3> { new(1, 2, 3), new(4, 5, 3), new(8, 5, 3) };

        var tagged = LayerBuilder.BuildTagged(
            new[] { (IReadOnlyList<Vec3>)open },
            new LayerBuildOptions { Level = 3, WallCount = 2, Infill = 4, WallSpacing = 10, AngleDeg = 0 });

        var path = Assert.Single(tagged.Paths);
        Assert.Equal(PathRole.OpenSection, path.Role);
        Assert.Equal(-1, path.WallIndex);
        Assert.False(path.Closed);
        AssertPathEqual(open, path.Points);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void MultipleIslands_ForwardTheIndependentRingDiagnostic(int infill)
    {
        var left = Square(-10, 0, 6, 0);
        var right = Square(10, 0, 6, 0);

        var tagged = LayerBuilder.BuildTagged(
            new[] { (IReadOnlyList<Vec3>)left, right },
            new LayerBuildOptions { Level = 0, WallCount = 1, Infill = infill, WallSpacing = 2, AngleDeg = 0 });

        Assert.Equal("rings not strictly nested (2 islands at depth 0) - independent rings",
                     tagged.Diagnostic);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void SingleNestedIsland_HasNoFallbackDiagnostic(int infill)
    {
        var tagged = LayerBuilder.BuildTagged(
            new[] { (IReadOnlyList<Vec3>)Square(0, 0, 10, 0) },
            new LayerBuildOptions { Level = 0, WallCount = 1, Infill = infill, WallSpacing = 2, AngleDeg = 0 });

        Assert.Null(tagged.Diagnostic);
    }

    /// <summary>A hollow section is ONE nested stack - outer skin, infill, hole skin -
    /// so the spiral modes must walk it from the outermost infill ring inward to the
    /// ring around the hole. Before 4.4.0 the bounding-box nesting test failed on the
    /// first hole ring (`Offset.ConcentricRings` returns an annulus interleaved as
    /// outer0, hole0, outer1, hole1, ...) and both modes degraded to six independent
    /// rings on every layer.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Annulus_IsOneSpiralBetweenTheTwoSkins(int infill)
    {
        const double spacing = 3.0, outerSkin = 40.0, holeSkin = 20.0;
        var perimeters = new[] { (IReadOnlyList<Vec3>)Circle(outerSkin, 0), Circle(holeSkin, 0) };

        var tagged = LayerBuilder.BuildTagged(perimeters,
            new LayerBuildOptions { Level = 0, WallCount = 1, Infill = infill, WallSpacing = spacing, InfillSpacing = spacing, AngleDeg = 0 });

        Assert.Null(tagged.Diagnostic);
        var path = Assert.Single(tagged.Paths.Where(p => p.Role == PathRole.Infill)).Points;
        var radii = path.Select(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)).ToArray();

        // the infill band runs from one wall distance inside the outer skin (r = 37) to
        // one wall distance outside the hole skin (r = 23) and touches neither
        Assert.True(radii.Max() <= outerSkin - 0.9 * spacing,
                    $"came within {outerSkin - radii.Max():0.000} mm of the outer skin");
        Assert.True(radii.Min() >= holeSkin + 0.9 * spacing,
                    $"came within {radii.Min() - holeSkin:0.000} mm of the hole skin");
        Assert.True(radii.Min() >= holeSkin + spacing - 0.25,
                    $"dipped to r = {radii.Min():0.000}, inside the hole ring at r = {holeSkin + spacing:0.000}");

        // a Fermat path deliberately returns outward on its second arm; only the plain
        // inward spiral is monotone
        if (infill != 3) return;
        for (int i = 1; i < radii.Length; i++)
            Assert.True(radii[i] <= radii[i - 1] + 0.05,
                        $"radius grew from {radii[i - 1]:0.000} to {radii[i]:0.000} at point {i}");
    }

    public static IEnumerable<object[]> StableRoundStackCases()
    {
        foreach (bool conical in new[] { false, true })
            foreach (int wallCount in new[] { 1, 2 })
                for (int infill = 1; infill <= 5; infill++)
                    yield return new object[] { conical, wallCount, infill };
    }

    [Theory]
    [MemberData(nameof(StableRoundStackCases))]
    public void RoundStack_RandomVertexOriginsKeepWallAndInfillAnglesBelowHalfDegree(
        bool conical, int wallCount, int infill)
    {
        const int layers = 12;
        const double cone = 20.0 * Math.PI / 180.0;
        var wallAngles = new List<double>();
        var infillAngles = new List<double>();
        var zigzagDirections = new List<int>();

        for (int layer = 0; layer < layers; layer++)
        {
            double level = 1.0 + layer;
            var perimeter = NoisyRound(layer, level, conical ? cone : 0.0);
            var options = new LayerBuildOptions
            {
                Level = level,
                WallCount = wallCount,
                Infill = infill,
                WallSpacing = 3.0,
                InfillSpacing = 3.0,
                AngleDeg = 0.0,
                ConeAngleRad = conical ? cone : 0.0,
            };
            var tagged = conical
                ? LayerBuilder.BuildConicalTagged(new[] { (IReadOnlyList<Vec3>)perimeter }, options)
                : LayerBuilder.BuildTagged(new[] { (IReadOnlyList<Vec3>)perimeter }, options);

            var lastWall = tagged.Paths.Last(p => p.Role == PathRole.Wall).Points;
            var firstInfill = tagged.Paths.First(p => p.Role == PathRole.Infill).Points;
            wallAngles.Add(AngleDeg(lastWall[^1]));
            infillAngles.Add(AngleDeg(firstInfill[0]));

            if (infill == 1)
            {
                int direction = 0;
                for (int i = 1; i < firstInfill.Count && direction == 0; i++)
                    direction = Math.Sign(firstInfill[i].X - firstInfill[i - 1].X);
                Assert.NotEqual(0, direction);
                zigzagDirections.Add(direction);
            }
        }

        double wallSpread = CircularSpreadDeg(wallAngles);
        double infillSpread = CircularSpreadDeg(infillAngles);
        Assert.True(wallSpread < 0.5, $"wall angle spread was {wallSpread:0.######} deg");
        Assert.True(infillSpread < 0.5,
            $"infill angle spread was {infillSpread:0.######} deg: {string.Join(", ", infillAngles.Select(a => a.ToString("0.###")))}");
        if (infill == 1) Assert.Single(zigzagDirections.Distinct());
    }

    static List<Vec3> NoisyRound(int layer, double level, double coneAngle)
    {
        const int vertices = 720;
        var random = new Random(104729 + layer * 7919);
        int start = random.Next(vertices);
        var points = new List<Vec3>(vertices + 1);
        for (int offset = 0; offset < vertices; offset++)
        {
            int vertex = (start + offset) % vertices;
            double angle = 2.0 * Math.PI * vertex / vertices;
            double radius = 40.0 + (2.0 * random.NextDouble() - 1.0) * 0.01;
            double x = radius * Math.Cos(angle);
            double y = radius * Math.Sin(angle);
            double z = level - Math.Tan(coneAngle) * radius;
            points.Add(new Vec3(x, y, z));
        }
        points.Add(points[0]);
        return Rdp.Simplify(points, 0.1);
    }

    static List<Vec3> Circle(double radius, double z, int segments = 180)
    {
        var ring = new List<Vec3>(segments + 1);
        for (int i = 0; i < segments; i++)
        {
            double a = 2.0 * Math.PI * i / segments;
            ring.Add(new Vec3(radius * Math.Cos(a), radius * Math.Sin(a), z));
        }
        ring.Add(ring[0]);
        return ring;
    }

    static List<Vec3> Square(double cx, double cy, double size, double z)
    {
        double h = size * 0.5;
        return new List<Vec3>
        {
            new(cx - h, cy - h, z), new(cx + h, cy - h, z),
            new(cx + h, cy + h, z), new(cx - h, cy + h, z),
            new(cx - h, cy - h, z),
        };
    }

    static double AngleDeg(Vec3 point)
    {
        double angle = Math.Atan2(point.Y, point.X) * 180.0 / Math.PI;
        return angle < 0.0 ? angle + 360.0 : angle;
    }

    static double CircularSpreadDeg(IEnumerable<double> angles)
    {
        var sorted = angles.OrderBy(a => a).ToArray();
        if (sorted.Length < 2) return 0.0;
        double largestGap = sorted[0] + 360.0 - sorted[^1];
        for (int i = 1; i < sorted.Length; i++)
            largestGap = Math.Max(largestGap, sorted[i] - sorted[i - 1]);
        return 360.0 - largestGap;
    }

    static void AssertPathEqual(IReadOnlyList<Vec3> expected, IReadOnlyList<Vec3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X);
            Assert.Equal(expected[i].Y, actual[i].Y);
            Assert.Equal(expected[i].Z, actual[i].Z);
        }
    }

    static double Extent(IReadOnlyList<Vec3> points)
        => points.Max(p => p.X) - points.Min(p => p.X);
}
