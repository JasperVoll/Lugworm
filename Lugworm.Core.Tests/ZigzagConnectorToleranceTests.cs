using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>4.3.0: row ends come out of Clipper rounded to its 0.1 um grid, so on a curved
/// or oblique boundary the connector to the next row starts a hair outside the edge it sits
/// on. The connector guard used a parametric 1e-6 crossing test, called that a proper
/// crossing and cut the zigzag into one polyline per row: 22 travels for 23 rows on a
/// circle. The guard now needs both connector ends more than a tolerance away from the
/// edge line, on opposite sides, before it counts a crossing.</summary>
public class ZigzagConnectorToleranceTests
{
    static List<Vec3> Circle(int n, double r)
    {
        var ring = new List<Vec3>(n + 1);
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / n;
            ring.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), 0));
        }
        ring.Add(ring[0]);
        return ring;
    }

    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(360)]
    public void Circle_IsOneConnectedPolyline(int sides)
    {
        var paths = Infill.ZigzagSpans(new[] { (IReadOnlyList<Vec3>)Circle(sides, 34) }, 3.0, 0.0, 0.0,
                                       out var diag, new Vec3(34, 0, 0));
        Assert.Null(diag);
        Assert.Single(paths);
        Assert.True(paths[0].Count >= 40, $"expected the 23 rows in one polyline, got {paths[0].Count} vertices");
    }

    [Fact]
    public void RotatedSquare_IsOneConnectedPolyline()
    {
        var sq = new List<Vec3> { new(0, 0, 0), new(60, 0, 0), new(60, 60, 0), new(0, 60, 0), new(0, 0, 0) };
        var paths = Infill.ZigzagSpans(new[] { (IReadOnlyList<Vec3>)sq }, 3.0, 30.0, 0.0, out _, new Vec3(60, 30, 0));
        Assert.Single(paths);
    }

    static readonly List<Vec3> Outer60 = new() { new(0, 0, 0), new(60, 0, 0), new(60, 60, 0), new(0, 60, 0), new(0, 0, 0) };
    static readonly List<Vec3> Hole20 = new() { new(20, 20, 0), new(20, 40, 0), new(40, 40, 0), new(40, 20, 0), new(20, 20, 0) };

    [Fact]
    public void Hole_IsNeverCrossed()
    {
        // a 60 x 60 square with a 20 x 20 hole: the rows through the hole band are two
        // pieces each, and no connector may run through the hole
        var paths = Infill.ZigzagSpans(new[] { (IReadOnlyList<Vec3>)Outer60, Hole20 }, 3.0, 0.0, 0.0, out _, new Vec3(60, 30, 0));
        Assert.NotEmpty(paths);
        foreach (var path in paths)
            for (int i = 1; i < path.Count; i++)
            {
                var mid = PolylineOps.Lerp(path[i - 1], path[i], 0.5);
                bool insideHole = mid.X > 20 + 1e-6 && mid.X < 40 - 1e-6 && mid.Y > 20 + 1e-6 && mid.Y < 40 - 1e-6;
                Assert.False(insideHole, $"segment {i} of a path runs through the hole at ({mid.X:0.##},{mid.Y:0.##})");
            }
    }

    [Fact]
    public void Hole_RingIsNeverExtrudedTwice()
    {
        // a boundary connector runs along the hole's ring; the fill must not lay a second
        // bead over a stretch of that ring an earlier connector already covered
        var paths = Infill.ZigzagSpans(new[] { (IReadOnlyList<Vec3>)Outer60, Hole20 }, 3.0, 0.0, 0.0, out _, new Vec3(60, 30, 0));
        const double bin = 0.5;
        var hits = new Dictionary<int, int>();
        foreach (var path in paths)
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1]; var b = path[i];
                if (!OnRing(Hole20, a) || !OnRing(Hole20, b)) continue;
                double len = PolylineOps.Dist(a, b);
                var seen = new HashSet<int>();
                for (int s = 1; s * 0.25 < len; s++)
                {
                    var p = PolylineOps.Lerp(a, b, s * 0.25 / len);
                    var c = Lugworm.Core.Paths.SeamAnchor.ClosestOnRing(Hole20, p);
                    seen.Add((int)Math.Floor((c.SegmentIndex + c.T) * 20.0 / bin));
                }
                foreach (int k in seen) hits[k] = hits.TryGetValue(k, out int n) ? n + 1 : 1;
            }
        int worst = hits.Count == 0 ? 0 : hits.Values.Max();
        Assert.True(worst <= 1, $"a stretch of the hole ring is extruded {worst} times");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(23.0)]
    public void ConcaveShoulders_ConnectAlongWallClearanceBoundary(double angle)
    {
        // the zigzag twin of the wave test: the rows above the shoulders are shorter, and
        // their ends join along the offset boundary instead of starting a new path
        var outline = new List<IReadOnlyList<Vec3>> { new List<Vec3>
        {
            new Vec3(0, 0, 0), new Vec3(80, 0, 0), new Vec3(80, 16, 0),
            new Vec3(55, 26, 0), new Vec3(55, 50, 0), new Vec3(25, 50, 0),
            new Vec3(25, 26, 0), new Vec3(0, 16, 0), new Vec3(0, 0, 0),
        } };
        var boundary = Offset.Polygons(outline, -2.0, 1.5);
        var paths = Infill.ZigzagSpans(boundary, 6, angle, 1.5, out var diagnostic);
        Assert.Null(diagnostic);
        var path = Assert.Single(paths);
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            double length = PolylineOps.Dist(a, b);
            var clipped = Infill.ClipLines(new[] { (a, b) }, boundary);
            double inside = clipped.Sum(s => PolylineOps.Dist(s.Item1, s.Item2));
            if (Math.Abs(inside - length) <= 0.002) continue;
            for (int j = 0; j <= 10; j++)
            {
                var p = PolylineOps.Lerp(a, b, j / 10.0);
                Assert.Contains(boundary, ring => OnRing(ring, p));
            }
        }
    }

    [Fact]
    public void SeparateIslands_RemainSeparatePaths()
    {
        var left = new List<Vec3> { new(0, 0, 0), new(30, 0, 0), new(30, 30, 0), new(0, 30, 0), new(0, 0, 0) };
        var right = left.Select(p => new Vec3(p.X + 100, p.Y, p.Z)).ToList();
        var paths = Infill.ZigzagSpans(new[] { (IReadOnlyList<Vec3>)left, right }, 3.0, 0.0, 0.0, out _);
        Assert.True(paths.Count >= 2);
        foreach (var path in paths)
            Assert.True(path.All(p => p.X <= 30 + 1e-6) || path.All(p => p.X >= 100 - 1e-6));
    }

    static bool OnRing(IReadOnlyList<Vec3> ring, Vec3 p)
        => PolylineOps.Dist2Xy(p, Lugworm.Core.Paths.SeamAnchor.ClosestOnRing(ring, p).Point) <= 1e-6;
}
