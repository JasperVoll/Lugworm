using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>P6 feature tests (2026-07-14 run): self-collision screens and
/// slope-compensated flow.</summary>
public class FeatureTests
{
    // ---- SegSegDist ---------------------------------------------------------

    [Fact]
    public void SegSegDist_KnownCases()
    {
        // parallel horizontal segments 5 apart
        Assert.Equal(5.0, PolylineOps.SegSegDist(
            new Vec3(0, 0, 0), new Vec3(10, 0, 0),
            new Vec3(0, 5, 0), new Vec3(10, 5, 0)), 9);
        // crossing at right angles, 2 apart in Z
        Assert.Equal(2.0, PolylineOps.SegSegDist(
            new Vec3(-5, 0, 0), new Vec3(5, 0, 0),
            new Vec3(0, -5, 2), new Vec3(0, 5, 2)), 9);
        // endpoint-to-endpoint
        Assert.Equal(Math.Sqrt(3), PolylineOps.SegSegDist(
            new Vec3(0, 0, 0), new Vec3(1, 1, 1),
            new Vec3(2, 2, 2), new Vec3(3, 3, 3)), 9);
        // degenerate (point) segments
        Assert.Equal(1.0, PolylineOps.SegSegDist(
            new Vec3(0, 0, 0), new Vec3(0, 0, 0),
            new Vec3(1, 0, 0), new Vec3(1, 0, 0)), 9);
    }

    // ---- SCARA mast clearance ---------------------------------------------------

    [Fact]
    public void ScaraMastClearance_CloseTarget_Warns()
    {
        // TCP at r=100: legal by derived reach (|L1-L2| ≈ 90.8) but link 2 sweeps
        // ~46mm from the mast axis - must warn. ReachMin=0 derives the geometric
        // annulus on purpose: this test targets the mast check, not the dead zone.
        var lim = new RobotLimits { ReachMin = 0 };
        var res = RobotChecks.RunPoints(new List<Vec3> { new(100, 0, 50) }, lim);
        Assert.Contains(res.Problems, p => p.Message.Contains("mast"));
    }

    [Fact]
    public void ScaraMastClearance_FarTarget_Clean()
    {
        var lim = new RobotLimits();
        var res = RobotChecks.RunPoints(new List<Vec3> { new(700, 0, 50) }, lim);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("mast"));
    }

    [Fact]
    public void ScaraMastClearance_Disabled_NoWarning()
    {
        var lim = new RobotLimits { MastClearance = 0, ReachMin = 0 };
        var res = RobotChecks.RunPoints(new List<Vec3> { new(100, 0, 50) }, lim);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("mast"));
    }

    // ---- slope-compensated flow ---------------------------------------------------

    static List<Vec3> Circle(double r, double z, int n = 90)
    {
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
        }
        return pts;
    }

    static IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> Layers(params List<Vec3>[] contours)
        => contours.Select(c => (IReadOnlyList<IReadOnlyList<Vec3>>)new List<IReadOnlyList<Vec3>> { c }).ToList();

    [Fact]
    public void SlopeFlow_VerticalStack_NoBoost()
    {
        // coaxial circles, same radius: support vector is vertical, slope 0 → m = 1
        var layers = Layers(Circle(50, 0), Circle(50, 2));
        var m = SlopeFlow.Multipliers(layers, kRef: 1.5, refDeg: 45);
        Assert.All(m[1][0], v => Assert.Equal(1.0, v, 6));
    }

    [Fact]
    public void SlopeFlow_InclinedStack_BoostsTowardKRef()
    {
        // radius grows 2mm while climbing 2mm → 45° stagger → full kRef at refDeg 45
        var layers = Layers(Circle(50, 0), Circle(52, 2));
        var m = SlopeFlow.Multipliers(layers, kRef: 1.5, refDeg: 45);
        Assert.All(m[1][0], v => Assert.InRange(v, 1.4, 1.5));
    }

    [Fact]
    public void SlopeFlow_KRefOne_IsNoOp()
    {
        var layers = Layers(Circle(50, 0), Circle(60, 2));
        var m = SlopeFlow.Multipliers(layers, kRef: 1.0, refDeg: 45);
        Assert.All(m.SelectMany(b => b).SelectMany(c => c), v => Assert.Equal(1.0, v, 12));
    }

    [Fact]
    public void SlopeFlow_FirstLayer_AlwaysOne()
    {
        var layers = Layers(Circle(50, 0), Circle(60, 2));
        var m = SlopeFlow.Multipliers(layers, kRef: 1.8, refDeg: 45);
        Assert.All(m[0][0], v => Assert.Equal(1.0, v, 12));
    }
}
