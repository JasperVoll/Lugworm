using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

public class PreviewReductionTests
{
    [Fact]
    public void StraightSamplesDoNotConsumeCornerBudget()
    {
        var points = new List<Vec3>();
        for (int i = 0; i <= 1000; i++) points.Add(new Vec3(i * 0.1, 0, 0));
        for (int i = 1; i <= 100; i++)
        {
            double angle = Math.PI * i / 200;
            points.Add(new Vec3(100 + 10 * Math.Sin(angle), 10 - 10 * Math.Cos(angle), 0));
        }
        var reduced = PreviewReduction.Reduce(points, 12);
        Assert.InRange(reduced.Count, 3, 12);
        Assert.Equal(points[0], reduced[0]);
        Assert.Equal(points[points.Count - 1], reduced[reduced.Count - 1]);
        // Every original point must stay within 0.1 mm of the reduced polyline.
        foreach (var p in points)
        {
            double best = double.PositiveInfinity;
            for (int i = 1; i < reduced.Count; i++)
            {
                var a = reduced[i - 1]; var b = reduced[i];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
                best = Math.Min(best, PolylineOps.Dist(p, new Vec3(a.X + t * dx, a.Y + t * dy, 0)));
            }
            Assert.InRange(best, 0, 0.1);
        }
    }

    [Fact]
    public void ClosedPathPreservesClosureAndShape()
    {
        var points = new List<Vec3>();
        for (int i = 0; i <= 1000; i++)
            points.Add(new Vec3(10 * Math.Cos(2 * Math.PI * i / 1000), 10 * Math.Sin(2 * Math.PI * i / 1000), 2));
        var reduced = PreviewReduction.Reduce(points, 33);
        Assert.Equal(33, reduced.Count);
        Assert.True(PolylineOps.Dist(reduced[0], reduced[reduced.Count - 1]) < 1e-9);
        Assert.All(reduced, p => Assert.Equal(2, p.Z));
    }

    [Fact]
    public void StraightRunCollapsesToItsEndpoints()
    {
        var points = new List<Vec3>();
        for (int i = 0; i < 100; i++) points.Add(new Vec3(i, 2 * i, 3 * i));
        Assert.Equal(2, PreviewReduction.Reduce(points, 10).Count);
    }
}
