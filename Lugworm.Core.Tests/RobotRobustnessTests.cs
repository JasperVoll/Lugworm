using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Phase 4: joint-limit enforcement, indexed problem reports, wrap-safe
/// jerk math, elbow consistency, and the FK self-check against the SHIPPED
/// robot.xml (linked from Lugworm/Resources - no hand-copied fixture).</summary>
public class RobotRobustnessTests
{
    static RobotDefinition ShippedDefinition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "robot.xml");
        return RobotDefinition.Parse(File.ReadAllText(path));
    }

    // ---- FK self-check: posing chain vs analytic FK, expectations derived
    // ---- from the definition file itself (link lengths, joint homes/ranges) ----

    [Fact]
    public void SelfCheck_ShippedRobotXml_Passes()
    {
        var def = ShippedDefinition();
        var failures = RobotSelfCheck.Run(def, tol: 1e-6);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void SelfCheck_CorruptedDefinition_Fails()
    {
        // XY posing is exact BY CONSTRUCTION (pivots re-derived from firmware lengths),
        // so the load-bearing calibration the self-check guards is the Z convention:
        // nozzle-Z == J2 holds only while J2.home equals the Jtip pivot Z (robot.xml).
        // Corrupt that coupling - the self-check must notice.
        var def = ShippedDefinition();
        def.Joint("J2")!.Home += 5.0;
        var failures = RobotSelfCheck.Run(def, tol: 1e-3);
        Assert.True(failures.Count > 0, "corrupted J2 home must produce FK Z-deviations");
    }

    // ---- what J1 swings (robot.xml joint chain) ----

    /// <summary>Only the Fundament is static. Verified on the machine 2026-08-26: J1
    /// swings the WHOLE Z-rail about the pivot together with the proximal arm, and the
    /// base underneath it stays put.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, -45, 250)]
    [InlineData(-41.1, -151.2, 400)]
    [InlineData(319.1, 151.2, 120)]
    public void BaseIsStaticForEveryPose(double j1, double j3, double j2)
    {
        var def = ShippedDefinition();
        var home = RobotPosing.LinkWorldTransforms(def, def.Joint("J1")!.Home,
            def.Joint("J3")!.Home, def.Joint("J2")!.Home);
        var posed = RobotPosing.LinkWorldTransforms(def, j1, j3, j2);
        for (int i = 0; i < 16; i++)
            Assert.Equal(home["base"][i], posed["base"][i], 9);
    }

    [Fact]
    public void RailSwingsWithJ1ButDoesNotRideTheCarriage()
    {
        var def = ShippedDefinition();
        var a = RobotPosing.LinkWorldTransforms(def, 0, 0, 100);
        var turned = RobotPosing.LinkWorldTransforms(def, 90, 0, 100);
        var lifted = RobotPosing.LinkWorldTransforms(def, 0, 0, 300);

        // J1 swings the rail...
        Assert.NotEqual(a["mast"][0], turned["mast"][0], 6);
        // ...while J2 slides the carriage along it and leaves the rail alone
        for (int i = 0; i < 16; i++)
            Assert.Equal(a["mast"][i], lifted["mast"][i], 9);

        // the proximal arm does both
        Assert.NotEqual(a["link1"][0], turned["link1"][0], 6);
        Assert.NotEqual(a["link1"][11], lifted["link1"][11], 6);
    }

    // ---- joint-range enforcement (was: parsed but never checked) ----

    [Fact]
    public void RunPoints_J1OutOfRange_FlaggedWithIndex()
    {
        // the firmware J1 range spans 360.2° (every angle admissible via ±360 wrap),
        // so use a NARROW range to exercise the violation path
        var lim = new RobotLimits { J1Min = -90, J1Max = 90 };
        double ang = 170.0 * Math.PI / 180.0;
        var pts = new List<Vec3>
        {
            new Vec3(700, 0, 10),                                        // fine (J1 ~ -33)
            new Vec3(700 * Math.Cos(ang), 700 * Math.Sin(ang), 10),      // J1 ~ 137 out of range
        };
        var res = RobotChecks.RunPoints(pts, lim);
        Assert.False(res.ReachOk);
        var probs = res.Problems.Where(p => p.Message.Contains("J1") && p.Message.Contains("joint range")).ToList();
        Assert.NotEmpty(probs);
        Assert.All(probs, p =>
        {
            Assert.Equal(1, p.Index);
            Assert.Contains("move 1", p.ToString());
        });
        // sanity: the IK itself considered the point reachable
        Assert.True(res.Iks[1].Valid);
    }

    [Fact]
    public void RunPoints_J1WrapEquivalent_NotFalselyFlagged()
    {
        // firmware range -311.1..49.1 spans 360.2°: a normalized IK angle of ~137°
        // corresponds to a physical -223°, which IS inside the range. The naive
        // [min,max] compare flagged this whole sector as DANGER - regression guard.
        var lim = new RobotLimits { CheckEnvelope = false };   // real joint ranges; isolate the joint check from the +Y envelope
        double ang = 170.0 * Math.PI / 180.0;
        var pts = new List<Vec3> { new Vec3(700 * Math.Cos(ang), 700 * Math.Sin(ang), 10) };
        var res = RobotChecks.RunPoints(pts, lim);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("joint range"));
        Assert.True(res.ReachOk);
    }

    [Fact]
    public void RunPoints_J2TravelExceeded_Flagged()
    {
        var lim = new RobotLimits();   // default ZMax 400 and J2Max 400 - Z=1100 exceeds the J2 travel
        var pts = new List<Vec3> { new Vec3(700, 0, 1100) };
        var res = RobotChecks.RunPoints(pts, lim);
        Assert.Contains(res.Problems, p => p.Message.Contains("J2 carriage") && p.Sev == Severity.Error);
    }

    [Fact]
    public void RunPoints_JointChecksCanBeDisabled()
    {
        var lim = new RobotLimits { CheckJointRanges = false };
        double ang = 170.0 * Math.PI / 180.0;
        var pts = new List<Vec3> { new Vec3(700 * Math.Cos(ang), 700 * Math.Sin(ang), 10) };
        var res = RobotChecks.RunPoints(pts, lim);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("joint range"));
    }

    // ---- wrap-safe jerk: J1 crossing the ±180° seam is a SMALL move ----

    [Fact]
    public void Run_JerkAcrossAngleSeam_NoFalsePositive()
    {
        var lim = new RobotLimits { CheckJointRanges = false };   // poses sit outside firmware range on purpose
        double L1 = lim.L1, L2 = lim.L2;
        // two TCPs from FK at J1 = 179° and −179° (true ΔJ1 = 2°), same J3
        var (x1, y1, _) = ScaraKinematics.Forward(179.0, 10.0, 100.0, L1, L2, 0.0);
        var (x2, y2, _) = ScaraKinematics.Forward(-179.0, 10.0, 100.0, L1, L2, 0.0);
        var prog = GcodeParser.Parse(new[]
        {
            "M83",
            FormattableString.Invariant($"G1 X{x1:0.000} Y{y1:0.000} Z100 E1 F1200"),
            FormattableString.Invariant($"G1 X{x2:0.000} Y{y2:0.000} Z100 E1 F1200"),
        });
        var res = RobotChecks.Run(prog, lim, GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("jerk"));
    }

    // ---- elbow parameter flows into the checks (consistency with the display) ----

    [Fact]
    public void RunPoints_ElbowChangesEmittedJoints()
    {
        var pts = new List<Vec3> { new Vec3(600, 200, 50) };
        var up = RobotChecks.RunPoints(pts, new RobotLimits(), elbow: 0);
        var down = RobotChecks.RunPoints(pts, new RobotLimits(), elbow: 1);
        Assert.True(up.Iks[0].Valid && down.Iks[0].Valid);
        Assert.True(Math.Abs(up.Iks[0].J3 + down.Iks[0].J3) < 1e-6,   // J3 mirrors
            $"elbow-up J3 {up.Iks[0].J3:0.00} vs elbow-down J3 {down.Iks[0].J3:0.00}");
        Assert.NotEqual(up.Iks[0].J1, down.Iks[0].J1);
    }

    // ---- syntax problems are global (index -1) ----

    [Fact]
    public void Run_SyntaxProblems_HaveNoMoveIndex()
    {
        var prog = GcodeParser.Parse(new[] { "G1 X700 Y0 Z1 F1200" });   // no M83/M82
        var res = RobotChecks.Run(prog, new RobotLimits(), GcodeFlavor.DuetRrf, arcSupport: true);
        var syn = res.Problems.Single(p => p.Message.Contains("extrusion mode is ambiguous"));
        Assert.Equal(-1, syn.Index);
        Assert.DoesNotContain("move", syn.ToString());
    }
}
