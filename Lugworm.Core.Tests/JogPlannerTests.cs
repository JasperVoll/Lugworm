using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class JogPlannerTests
{
    // Potterbot Scara v4 firmware limits (config.g M669 R235; reach derived L1+L2 = 1006.29)
    static RobotLimits Lim() => new RobotLimits { ReachMin = 235.0 };

    static ObjectModelSnapshot Snap(double x = 600, double y = 0, double z = 100,
                                    MachineStatus status = MachineStatus.Idle,
                                    bool homed = true, double ageS = 0)
        => new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            TakenUtc = DateTime.UtcNow - TimeSpan.FromSeconds(ageS),
            AllHomed = homed,
            Axes = new List<AxisState>
            {
                new AxisState { Letter = "X", MachinePosition = x, Homed = homed },
                new AxisState { Letter = "Y", MachinePosition = y, Homed = homed },
                new AxisState { Letter = "Z", MachinePosition = z, Homed = homed },
            },
        };

    [Fact]
    public void HappyPath_EmitsBracketedAbsoluteMove()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dx = 10, FeedMmS = 20 }, Lim());
        Assert.True(plan.Ok);
        Assert.Equal("M120\nG90\nG53 G1 X610 F1200\nM121", plan.Gcode);
        Assert.Equal(610, plan.TargetX, 9);
    }

    [Fact]
    public void DuplicateStaleClick_ResendsSameAbsoluteTarget()
    {
        // two presses planned against the SAME (stale) snapshot must emit the SAME
        // absolute target - idempotent, never an accumulating relative delta
        var snap = Snap();
        var p1 = JogPlanner.Plan(snap, new JogRequest { Dx = 50, FeedMmS = 20 }, Lim());
        var p2 = JogPlanner.Plan(snap, new JogRequest { Dx = 50, FeedMmS = 20 }, Lim());
        Assert.True(p1.Ok && p2.Ok);
        Assert.Equal(p1.Gcode, p2.Gcode);
        Assert.Contains("X650", p1.Gcode);
    }

    [Fact]
    public void SnapshotOlderThanLastSend_Refused()
    {
        var snap = Snap(ageS: 1);
        var plan = JogPlanner.Plan(snap, new JogRequest { Dx = 10, FeedMmS = 20 }, Lim(),
                                   notBeforeUtc: DateTime.UtcNow);
        Assert.False(plan.Ok);
        Assert.Contains("predates", plan.RefusalReason);
    }

    [Fact]
    public void TinyFeed_NeverEmitsF0()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dx = 1, FeedMmS = 0.005 }, Lim());
        Assert.True(plan.Ok);
        Assert.DoesNotContain("F0", plan.Gcode);
        Assert.Contains("F1", plan.Gcode);
    }

    [Fact]
    public void NoSnapshot_Refused()
    {
        var plan = JogPlanner.Plan(null, new JogRequest { Dx = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("connect", plan.RefusalReason);
    }

    [Fact]
    public void StaleSnapshot_Refused()
    {
        var plan = JogPlanner.Plan(Snap(ageS: 10), new JogRequest { Dx = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("stale", plan.RefusalReason);
    }

    [Theory]
    [InlineData(MachineStatus.Processing)]
    [InlineData(MachineStatus.Busy)]
    [InlineData(MachineStatus.Halted)]
    [InlineData(MachineStatus.Simulating)]
    public void NotIdle_Refused(MachineStatus status)
    {
        var plan = JogPlanner.Plan(Snap(status: status), new JogRequest { Dx = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("only when idle", plan.RefusalReason);
    }

    [Fact]
    public void Paused_RefusedByDefault()
    {
        var plan = JogPlanner.Plan(Snap(status: MachineStatus.Paused), new JogRequest { Dz = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("only when idle", plan.RefusalReason);
    }

    [Fact]
    public void Paused_MaintenanceOverride_Allowed()
    {
        var plan = JogPlanner.Plan(Snap(status: MachineStatus.Paused), new JogRequest { Dz = 1 }, Lim(),
            maintenanceOverride: true);
        Assert.True(plan.Ok);
    }

    [Fact]
    public void NotHomed_Refused()
    {
        var plan = JogPlanner.Plan(Snap(homed: false), new JogRequest { Dx = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("homed", plan.RefusalReason);
    }

    [Fact]
    public void StepNotInWhitelist_Refused()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dx = 3.7 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("allowed set", plan.RefusalReason);
    }

    [Fact]
    public void TwoAxesAtOnce_Refused()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dx = 1, Dy = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("one axis", plan.RefusalReason);
    }

    [Fact]
    public void ZeroDistance_Refused()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest(), Lim());
        Assert.False(plan.Ok);
    }

    [Fact]
    public void FeedOverCap_Refused()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dx = 1, FeedMmS = 80 }, Lim(), maxFeedMmS: 50);
        Assert.False(plan.Ok);
        Assert.Contains("cap", plan.RefusalReason);
    }

    [Fact]
    public void TargetBeyondReach_Refused()
    {
        // at X=1000 (r near the 1006.29 annulus edge) a +10 jog leaves the workspace
        var plan = JogPlanner.Plan(Snap(x: 1000), new JogRequest { Dx = 10 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("target rejected", plan.RefusalReason);
    }

    [Fact]
    public void TargetInsideDeadZone_Refused()
    {
        var plan = JogPlanner.Plan(Snap(x: 240), new JogRequest { Dx = -10 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("target rejected", plan.RefusalReason);
    }

    [Fact]
    public void TargetBelowBed_Refused()
    {
        var plan = JogPlanner.Plan(Snap(z: 0.05), new JogRequest { Dz = -1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("target rejected", plan.RefusalReason);
    }

    [Fact]
    public void SegmentInterior_ChordAcrossDeadZone_FlagsBothViolationKinds()
    {
        // chord between two fully legal points on opposite sides of the mast
        // (min r = 200 at x = 0): the interior must flag both the dead-zone
        // crossing and the joint-range violation the endpoints never see
        var res = new Lugworm.Core.Robot.CheckResult();
        Lugworm.Core.Robot.RobotChecks.CheckSegmentInterior(res, Lim(),
            new Lugworm.Core.Geometry.Vec3(600, 200, 100),
            new Lugworm.Core.Geometry.Vec3(-600, 200, 100), 0);
        Assert.False(res.ReachOk);
        Assert.Contains(res.Problems, p => p.Message.Contains("dead-zone"));
        Assert.Contains(res.Problems, p => p.Message.Contains("joint range"));
    }

    [Fact]
    public void PathSegmentOverMast_Refused()
    {
        // travel between two legal waypoints whose chord crosses the mast: both
        // endpoints sit inside the envelope (X in [-300,10000], Y<=0) at r ~ 316,
        // but the straight move passes through r = 100 - the mid-segment check must
        // refuse it even though the endpoints are individually legal.
        var snap = Snap(x: 300, y: -100);
        var plan = JogPlanner.PlanPath(snap,
            new[] { new Lugworm.Core.Geometry.Vec3(300, -100, 100),
                    new Lugworm.Core.Geometry.Vec3(-300, -100, 100) },
            20, Lim());
        Assert.False(plan.Ok);
        // the mid-segment sample folds the elbow past its limit (or crosses the
        // R=235 dead-zone) - either proves the interior, not just endpoints, was checked
        string reason = Assert.IsType<string>(plan.RefusalReason);
        Assert.True(reason.Contains("joint range")
                 || reason.Contains("dead-zone"), reason);
    }

    [Fact]
    public void NearSingularity_WarnsButAllows()
    {
        // r ≈ 990 > 0.98 * 1006.29 - legal but near the outer singularity
        var plan = JogPlanner.Plan(Snap(x: 980), new JogRequest { Dx = 10 }, Lim());
        Assert.True(plan.Ok);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void MissingAxes_Refused()
    {
        var snap = Snap();
        snap.Axes = new List<AxisState> { new AxisState { Letter = "X", Homed = true } };
        var plan = JogPlanner.Plan(snap, new JogRequest { Dx = 1 }, Lim());
        Assert.False(plan.Ok);
        Assert.Contains("missing", plan.RefusalReason);
    }

    [Fact]
    public void NegativeSteps_UseWhitelistMagnitude()
    {
        var plan = JogPlanner.Plan(Snap(), new JogRequest { Dy = -50, FeedMmS = 10 }, Lim());
        Assert.True(plan.Ok);
        Assert.Contains("G53 G1 Y-50 F600", plan.Gcode);
    }

    // D4 (4.4.0): the emitted G53 G1 line is machine text, not UI - fractional
    // coordinates must stay dot-decimal under a German ambient culture, same
    // fixture pattern as CultureInvarianceTests. Uses PlanPath (the multi-waypoint
    // planner) since the single-axis Plan() above only ever moves by a whitelisted
    // step (0.1/1/10/50mm) from an integer test snapshot.
    [Fact]
    public void PlanPathGcode_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            var snap = Snap(x: 400.25, y: -200.125, z: 50.5);
            var waypoints = new[]
            {
                new Vec3(400.25, -200.125, 50.5),
                new Vec3(600.75, -100.375, 50.5),
                new Vec3(600.75, -100.375, 2.25),
            };

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var invPlan = JogPlanner.PlanPath(snap, waypoints, 20.5, Lim());
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var dePlan = JogPlanner.PlanPath(snap, waypoints, 20.5, Lim());

            Assert.True(invPlan.Ok, invPlan.RefusalReason);
            Assert.True(dePlan.Ok, dePlan.RefusalReason);
            Assert.Equal(invPlan.Gcode, dePlan.Gcode);
            Assert.DoesNotContain(",", invPlan.Gcode);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
