using System;
using System.Collections.Generic;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class ZigzagConnectTests
{
    static List<Vec3> Sq100() => new()
    { new(0,0,0), new(100,0,0), new(100,100,0), new(0,100,0) };

    static double DistToBoundary(Vec3 p, IReadOnlyList<Vec3> poly)
    {
        double best = double.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var s = poly[j]; var e = poly[i];
            double ex = e.X - s.X, ey = e.Y - s.Y, l2 = ex * ex + ey * ey;
            double t = Math.Max(0.0, Math.Min(1.0, ((p.X - s.X) * ex + (p.Y - s.Y) * ey) / l2));
            double cx = s.X + t * ex - p.X, cy = s.Y + t * ey - p.Y;
            best = Math.Min(best, Math.Sqrt(cx * cx + cy * cy));
        }
        return best;
    }

    [Fact]
    public void Zigzag_Square_IsOneConnectedPolyline_StartingOnWall()
    {
        var sq = Sq100();
        var paths = Infill.ZigzagSpans(new[] { sq }, 10.0, 0.0, 0.0, out var diag);
        Assert.Null(diag);
        Assert.True(paths.Count <= 2, $"expected <= 2 polylines, got {paths.Count}");
        var pl = paths[0];
        Assert.True(pl.Count >= 20, $"expected >= 20 vertices, got {pl.Count}");
        for (int i = 1; i < pl.Count; i++)
        {
            double d = Math.Sqrt((pl[i].X - pl[i - 1].X) * (pl[i].X - pl[i - 1].X) + (pl[i].Y - pl[i - 1].Y) * (pl[i].Y - pl[i - 1].Y));
            Assert.True(d <= 100.0 + 1e-6, $"vertex {i} jump {d}");
        }
        // consecutive rows are joined by a connector no longer than 1.5 * spacing
        for (int i = 2; i < pl.Count; i += 2)
        {
            double d = Math.Sqrt((pl[i].X - pl[i - 1].X) * (pl[i].X - pl[i - 1].X) + (pl[i].Y - pl[i - 1].Y) * (pl[i].Y - pl[i - 1].Y));
            Assert.True(d <= 15.0, $"connector {i} length {d} > 15");
        }
        Assert.True(DistToBoundary(pl[0], sq) < 1e-6, "first vertex must lie on the wall polygon");
    }

    [Fact]
    public void Zigzag_Anchor_StartsAtNearestEnd()
    {
        var sq = Sq100();
        var paths = Infill.ZigzagSpans(new[] { sq }, 10.0, 0.0, 0.0, out _, anchor: new Vec3(100, 100, 0));
        var pl = paths[0];
        Assert.True(pl[0].Y > 50.0, "anchored start should be on the upper half");
    }

    [Fact]
    public void LayerBuilder_Zigzag_StartsAtTheInnermostWallSeam()
    {
        // final test round 3.1.4: the anchor existed on ZigzagSpans but LayerBuilder never passed
        // it, so the zigzag started at the bottom row wherever the wall seam was. D2 now
        // puts that seam at the exact +X hit from the area centroid.
        var ring = new List<Vec3> { new(100, 100, 0), new(0, 100, 0), new(0, 0, 0), new(100, 0, 0), new(100, 100, 0) };
        var built = LayerBuilder.Build(new[] { ring },
            new LayerBuildOptions { Level = 0.0, WallCount = 1, Infill = 1, WallSpacing = 10.0, AngleDeg = 0.0 });
        Assert.True(built.Count >= 2, $"expected wall + infill, got {built.Count}");
        var wall = built[0];
        var seam = wall[wall.Count - 1];
        Assert.Equal(100.0, seam.X, 9);
        Assert.Equal(50.0, seam.Y, 9);
        var zig = built[1];
        double dStart = Math.Sqrt((zig[0].X - seam.X) * (zig[0].X - seam.X) + (zig[0].Y - seam.Y) * (zig[0].Y - seam.Y));
        double dEnd = Math.Sqrt((zig[^1].X - seam.X) * (zig[^1].X - seam.X) + (zig[^1].Y - seam.Y) * (zig[^1].Y - seam.Y));
        Assert.True(dStart <= dEnd + 1e-9, $"start is {dStart} from the seam, end {dEnd}");
    }
}
