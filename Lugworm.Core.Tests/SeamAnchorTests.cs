using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

public class SeamAnchorTests
{
    [Fact]
    public void RayHit_Square_ReturnsExactEdgeMidpoint()
    {
        var square = new List<Vec3>
        {
            new(-10, -10, 2), new(10, -10, 2), new(10, 10, 2),
            new(-10, 10, 2), new(-10, -10, 2),
        };

        var hit = SeamAnchor.RayHit(square, new Vec3(0, 0, 2), 0.0);

        Assert.NotNull(hit);
        Assert.Equal(10.0, hit!.Value.Point.X, 12);
        Assert.Equal(0.0, hit.Value.Point.Y, 12);
        Assert.Equal(1, hit.Value.SegmentIndex);
        Assert.Equal(0.5, hit.Value.T, 12);
    }

    [Fact]
    public void RayHit_ConcaveC_ReturnsFarthestCrossing()
    {
        var c = ConcaveC();

        var hit = SeamAnchor.RayHit(c, new Vec3(-5, 0, 0), 0.0);

        Assert.NotNull(hit);
        Assert.Equal(-2.0, hit!.Value.Point.X, 12);
        Assert.Equal(0.0, hit.Value.Point.Y, 12);
    }

    [Fact]
    public void MissInsideCOpening_FallsBackToClosestPoint()
    {
        var c = ConcaveC();
        var origin = new Vec3(0, 0, 0);

        Assert.Null(SeamAnchor.RayHit(c, origin, 0.0));
        var closest = SeamAnchor.ClosestOnRing(c, origin);

        Assert.Equal(0.0, closest.Point.X, 12);
        Assert.Equal(2.0, PolylineOps.Dist(origin, closest.Point), 12);
    }

    [Fact]
    public void SplitAt_PreservesLengthAndClosureWithoutInteriorDuplicates()
    {
        var square = new List<Vec3>
        {
            new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0),
            new(-10, 10, 0), new(-10, -10, 0),
        };
        double before = PolylineOps.ContourLength(square);

        var split = SeamAnchor.SplitAt(square, 1, 0.5);

        Assert.True(PolylineOps.IsClosed(split));
        Assert.Equal(before, PolylineOps.ContourLength(split), 9);
        Assert.Equal(new Vec3(10, 0, 0), split[0]);
        Assert.Equal(split[0], split[^1]);
        Assert.Equal(split.Count - 1, split.Take(split.Count - 1).Distinct().Count());
        for (int i = 1; i < split.Count; i++)
            Assert.True(PolylineOps.Dist(split[i - 1], split[i]) > 0.0);
    }

    [Fact]
    public void Centroid_UsesAreaRatherThanVertexDensity()
    {
        var unevenSquare = new List<Vec3>
        {
            new(0, 0, 3), new(2, 0, 3), new(4, 0, 3), new(6, 0, 3),
            new(10, 0, 3), new(10, 10, 3), new(0, 10, 3), new(0, 0, 3),
        };

        var centroid = SeamAnchor.Centroid(unevenSquare);

        Assert.Equal(5.0, centroid.X, 12);
        Assert.Equal(5.0, centroid.Y, 12);
        Assert.Equal(3.0, centroid.Z, 12);
    }

    static List<Vec3> ConcaveC() => new()
    {
        new(-4, -4, 0), new(4, -4, 0), new(4, -2, 0), new(-2, -2, 0),
        new(-2, 2, 0), new(4, 2, 0), new(4, 4, 0), new(-4, 4, 0),
        new(-4, -4, 0),
    };
}
