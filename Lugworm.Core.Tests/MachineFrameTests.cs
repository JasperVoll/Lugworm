using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The Rhino design frame is ground truth; the machine's Cartesian frame is
/// its reflection about y = -x. These pin the map itself, the one place it is applied
/// inside Core (G-code emission), and the round trip back through the parser.</summary>
public class MachineFrameTests
{
    // ---- the map -------------------------------------------------------------

    [Fact]
    public void MapsTheMeasuredHomePose()
    {
        // The pose the firmware reports after a full G28 is X200 Y-500 Z400; the
        // operator sees that same physical point at Rhino X500 Y-200 Z400.
        var rhino = MachineFrame.ToRhino(new Vec3(200, -500, 400));
        Assert.Equal(500, rhino.X, 9);
        Assert.Equal(-200, rhino.Y, 9);
        Assert.Equal(400, rhino.Z, 9);

        var machine = MachineFrame.ToMachine(new Vec3(500, -200, 400));
        Assert.Equal(200, machine.X, 9);
        Assert.Equal(-500, machine.Y, 9);
        Assert.Equal(400, machine.Z, 9);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(200, -500, 400)]
    [InlineData(-33.5, 264.25, 12.5)]
    [InlineData(1e-9, -1e9, 7)]
    public void IsItsOwnInverse(double x, double y, double z)
    {
        var p = new Vec3(x, y, z);
        var back = MachineFrame.ToRhino(MachineFrame.ToMachine(p));
        // negation is exact in IEEE-754, so the round trip is bit-for-bit
        Assert.Equal(p.X, back.X);
        Assert.Equal(p.Y, back.Y);
        Assert.Equal(p.Z, back.Z);
    }

    [Fact]
    public void PreservesDistancesAndRadii()
    {
        var a = new Vec3(120, -430, 0);
        var b = new Vec3(-55, 260, 0);
        var ma = MachineFrame.ToMachine(a);
        var mb = MachineFrame.ToMachine(b);
        double d = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        double md = Math.Sqrt((ma.X - mb.X) * (ma.X - mb.X) + (ma.Y - mb.Y) * (ma.Y - mb.Y));
        Assert.Equal(d, md, 9);
        // the reach annulus is centred on the shared origin, so radii are invariant -
        // that is why RobotLimits.ReachMin/ReachMax need no conversion anywhere
        Assert.Equal(Math.Sqrt(a.X * a.X + a.Y * a.Y),
                     Math.Sqrt(ma.X * ma.X + ma.Y * ma.Y), 9);
    }

    [Fact]
    public void ZeroDoesNotBecomeNegativeZero()
    {
        // -0 formats as "-0.000" in emitted G-code and in every coordinate a person
        // reads, so the map must keep zero positive.
        var m = MachineFrame.ToMachine(new Vec3(0, 0, 0));
        Assert.False(double.IsNegative(m.X));
        Assert.False(double.IsNegative(m.Y));
        MachineFrame.ToRhino(0, 0, out double rx, out double ry);
        Assert.False(double.IsNegative(rx));
        Assert.False(double.IsNegative(ry));
    }

    [Fact]
    public void ReversesArcSense()
    {
        Assert.Equal(3, MachineFrame.FlipArcCode(2));
        Assert.Equal(2, MachineFrame.FlipArcCode(3));
        Assert.Equal(1, MachineFrame.FlipArcCode(1));
        MachineFrame.ToMachineArcOffset(3, -7, out double i, out double j);
        Assert.Equal(7, i, 9);
        Assert.Equal(-3, j, 9);
    }

    /// <summary>The identity `RobotModel.DrawnJoints` rests on: reflecting the arm is
    /// the same as posing it at reflected joint values, because the reflection maps a
    /// direction at angle phi to -90 - phi. Both the nozzle AND the elbow land in the
    /// same place either way, so the whole arm matches - which is why the viewport can
    /// pose the assembly at these values instead of mirroring the meshes, and keep
    /// every part its true handedness.</summary>
    [Theory]
    [InlineData(-18.489, -116.054, 400)]   // the measured post-home pose
    [InlineData(0, 0, 0)]
    [InlineData(90, -45, 250)]
    [InlineData(-41.1, -151.2, 120)]
    public void ReflectingTheArmEqualsPosingAtReflectedJoints(double j1, double j3, double j2)
    {
        const double L1 = 549.06, L2 = 457.23;
        double dj1 = -90.0 - j1, dj3 = -j3;

        var machine = ScaraKinematics.Forward(j1, j3, j2, L1, L2, 0.0);
        var expected = MachineFrame.ToRhino(new Vec3(machine.x, machine.y, machine.z));
        var drawn = ScaraKinematics.Forward(dj1, dj3, j2, L1, L2, 0.0);
        Assert.Equal(expected.X, drawn.x, 6);
        Assert.Equal(expected.Y, drawn.y, 6);
        Assert.Equal(expected.Z, drawn.z, 6);

        var (mex, mey) = ScaraKinematics.Elbow(j1, L1);
        MachineFrame.ToRhino(mex, mey, out double rex, out double rey);
        var (dex, dey) = ScaraKinematics.Elbow(dj1, L1);
        Assert.Equal(rex, dex, 6);
        Assert.Equal(rey, dey, 6);
    }

    // ---- G-code emission -----------------------------------------------------

    static GcodeConfig Cfg() => new()
    {
        LayerHeight = 1.0,
        Width = 2.0,
        RamDiameter = 10.0,
        PressureCoeff = 1.0,
        PrintSpeed = 20.0,
    };

    static List<List<Vec3>> Branch(List<Vec3> contour) => new() { contour };

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Fact]
    public void EmittedWordsAreMachineCoordinates_PreviewsStayRhino()
    {
        // an L drawn in Rhino, deliberately asymmetric so a swap cannot pass as a no-op
        var path = new List<Vec3> { new(500, -200, 0), new(560, -200, 0), new(560, -140, 0) };
        var r = GcodeBuilder.Build(new[] { Branch(path) }, Cfg());

        foreach (var p in path)
        {
            var m = MachineFrame.ToMachine(p);
            string xy = $"X{m.X.ToString("0.000", Inv)} Y{m.Y.ToString("0.000", Inv)}";
            Assert.Contains(r.Lines, l => l.Contains(xy));
        }
        // and the design-frame coordinates must NOT appear as emitted words
        Assert.DoesNotContain(r.Lines, l => l.Contains("X560.000"));

        // the preview polylines and the start point are what Rhino draws: unmapped
        var printed = r.PrintPolys.SelectMany(p => p).ToList();
        Assert.Contains(printed, p => Math.Abs(p.X - 560) < 1e-6 && Math.Abs(p.Y + 140) < 1e-6);
        Assert.Equal(500, r.StartPoint!.Value.X, 6);
        Assert.Equal(-200, r.StartPoint!.Value.Y, 6);
    }

    [Fact]
    public void EmittedArcsFlipDirectionAndOffsets()
    {
        // a full CCW circle in Rhino; the reflection makes it CW for the machine
        var circle = Enumerable.Range(0, 61)
            .Select(k =>
            {
                double a = k * 6.0 * Math.PI / 180.0;
                return new Vec3(500 + 20 * Math.Cos(a), -200 + 20 * Math.Sin(a), 0.0);
            }).ToList();
        var cfg = Cfg(); cfg.UseArcs = true; cfg.ArcTol = 0.05;
        var r = GcodeBuilder.Build(new[] { Branch(circle) }, cfg);

        Assert.True(r.Arcs, "expected the arc fitter to emit arcs:\n" + string.Join("\n", r.Lines));
        var arcs = r.Lines.Where(l => l.StartsWith("G2") || l.StartsWith("G3")).ToList();
        Assert.True(arcs.Count > 0, "no G2/G3 lines:\n" + string.Join("\n", r.Lines));
        Assert.All(arcs, l => Assert.StartsWith("G2", l));   // CCW in Rhino -> CW here
    }

    /// <summary>The strongest statement of the contract: a toolpath drawn in Rhino,
    /// emitted as G-code, then read back by the fail-closed parser - which is
    /// deliberately machine-frame, like the firmware it models - and mapped home,
    /// returns the same geometry. If either half of the map were wrong or missing,
    /// this diverges.</summary>
    [Fact]
    public void GcodeRoundTripsBackToTheDesignFrame()
    {
        var path = new List<Vec3>
        {
            new(500, -200, 0), new(560, -200, 0), new(560, -140, 0), new(500, -140, 0),
        };
        var r = GcodeBuilder.Build(new[] { Branch(path) }, Cfg());
        var prog = GcodeParser.Parse(r.Lines);
        Assert.NotEmpty(prog.Moves);

        var back = prog.Moves
            .Select(mv => MachineFrame.ToRhino(new Vec3(mv.X, mv.Y, mv.Z)))
            .ToList();
        foreach (var p in path)
            Assert.Contains(back, q => Math.Abs(q.X - p.X) < 1e-3 && Math.Abs(q.Y - p.Y) < 1e-3);
    }

    /// <summary>The machine's M208 box is a MACHINE-frame fact, so a Rhino point must
    /// be mapped before it is tested. The lab envelope is X-300:10000 Y-10000:0, which
    /// in Rhino is the half-plane X &gt;= 0 with Y &lt;= 300 - a different rectangle, so
    /// testing an unmapped point would accept and reject the wrong points.</summary>
    [Fact]
    public void EnvelopeIsAMachineFrameBox()
    {
        var lim = new RobotLimits { ReachMin = 235.0 };   // ships the M208 defaults

        // the calibrated home pose, in Rhino: legal
        var home = MachineFrame.ToMachine(new Vec3(500, -200, 100));
        var ok = RobotChecks.RunPoints(new[] { home }, lim, ArmConfiguration.Seed(1));
        Assert.True(ok.ReachOk);

        // its mirror image about the machine's own axes is outside the half-plane
        var outside = MachineFrame.ToMachine(new Vec3(-500, 200, 100));
        var bad = RobotChecks.RunPoints(new[] { outside }, lim, ArmConfiguration.Seed(1));
        Assert.False(bad.ReachOk);
    }
}
