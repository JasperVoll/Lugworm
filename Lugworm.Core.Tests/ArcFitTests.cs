using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class ArcFitTests
{
    static List<Vec3> CircleArc(int count, double degStep, double r)
    {
        var pts = new List<Vec3>();
        for (int k = 0; k < count; k++)
        {
            double a = k * degStep * Math.PI / 180.0;
            pts.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), 0.0));
        }
        return pts;
    }

    [Fact]
    public void Circle_ProducesArcs_FewMoves()
    {
        var pts = CircleArc(41, 4.0, 20.0);          // 0..160 deg
        var moves = ArcFit.FitArcs(pts, tol: 0.05);
        Assert.True(moves.Count(m => m.Kind == MoveKind.Arc) >= 1);
        Assert.True(moves.Count < 6);
        double tot = moves.Sum(ArcFit.ArcLengthOf);
        Assert.Equal(160.0 * Math.PI / 180.0 * 20.0, tot, 1); // within ~0.5
        var arc = moves.First(m => m.Kind == MoveKind.Arc);
        Assert.Equal(20.0, arc.R, 1);                 // fitted radius ~20
    }

    [Fact]
    public void StraightLine_NoArcs()
    {
        var sl = new List<Vec3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0), new(4, 0, 0) };
        Assert.All(ArcFit.FitArcs(sl, 0.05), m => Assert.Equal(MoveKind.Line, m.Kind));
    }

    [Fact]
    public void NonPlanarRun_NoArcs()
    {
        var npz = new List<Vec3>();
        for (int k = 0; k < 20; k++)
        {
            double a = k * 4.0 * Math.PI / 180.0;
            npz.Add(new Vec3(20 * Math.Cos(a), 20 * Math.Sin(a), k * 0.5));
        }
        Assert.All(ArcFit.FitArcs(npz, 0.05), m => Assert.Equal(MoveKind.Line, m.Kind));
    }

    // ---- firmware convention: G2/G3 with coincident XY endpoints = FULL circle ----
    [Fact]
    public void FullCircle_CoincidentEndpoints_IsTwoPi_NotCollapsed()
    {
        var p = new Vec3(10, 0, 0);
        var cw = Move.Arc(p, p, 0, 0, true, 10);
        var ccw = Move.Arc(p, p, 0, 0, false, 10);
        Assert.Equal(-2 * Math.PI, cw.SweepAngle(), 9);
        Assert.Equal(2 * Math.PI, ccw.SweepAngle(), 9);
        Assert.Equal(2 * Math.PI * 10, ArcFit.ArcLengthOf(ccw), 6);
        Assert.Equal(2 * Math.PI * 10, ArcFit.ArcLengthOf(cw), 6);

        // PointAt traverses the circle instead of collapsing to P0
        var half = ccw.PointAt(0.5);
        Assert.Equal(-10.0, half.X, 6);
        Assert.Equal(0.0, half.Y, 6);
        var quarterCw = cw.PointAt(0.25);
        Assert.Equal(0.0, quarterCw.X, 6);
        Assert.Equal(-10.0, quarterCw.Y, 6);   // CW from (10,0) → (0,-10)

        // tessellation yields a non-degenerate ring reaching the far side
        var poly = ArcTessellation.ToPolyline(ccw, 0.05);
        Assert.True(poly.Count >= 8, $"expected a dense full-circle polyline, got {poly.Count} points");
        double minX = double.MaxValue;
        foreach (var q in poly) minX = Math.Min(minX, q.X);
        Assert.True(minX < -9.0, "tessellation must reach the far side of the circle");
    }
}
