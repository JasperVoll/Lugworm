using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Gate tests for the generic Cartesian Duet path (2.2.0). The Cartesian
/// mechanism must keep every machine-state refusal the SCARA gate has (stale
/// snapshot, not homed, not idle, feed cap, step whitelist) while dropping only the
/// arm-specific checks (reach annulus, dead zone, IK, arm configuration, mast).
/// The SCARA path itself must be bit-identical to before - the mechanism parameter
/// defaults to Scara everywhere.</summary>
public class CartesianDuetGateTests
{
    // a plausible generic Duet machine: 300x300 bed, 200 Z
    static RobotLimits CartLim() => new RobotLimits
    {
        XMin = 0,
        XMax = 300,
        YMin = 0,
        YMax = 300,
        ZMin = 0,
        ZMax = 200,
        CheckEnvelope = true,
        MaxFeed = 150.0,
    };

    static ObjectModelSnapshot Snap(double x = 150, double y = 150, double z = 10,
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

    // ---- jog ---------------------------------------------------------------

    [Fact]
    public void Jog_InsideEnvelope_Allowed_WithoutArmConfiguration()
    {
        // positive-Y target and a position inside the SCARA dead-zone radius:
        // both would be refused by the SCARA path, so passing proves the
        // arm-specific checks are actually off
        var plan = JogPlanner.Plan(Snap(x: 100, y: 100), new JogRequest { Dx = 10, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.True(plan.Ok, plan.RefusalReason);
        Assert.Equal("M120\nG90\nG53 G1 X110 F1200\nM121", plan.Gcode);
        Assert.False(plan.FinalConfiguration.Known);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Jog_SamePosition_ScaraPathStillRefuses()
    {
        // characterisation: the same request through the default (SCARA) mechanism
        // is refused - (100,100) is deep inside the Potterbot dead zone and Y > 0
        var plan = JogPlanner.Plan(Snap(x: 100, y: 100), new JogRequest { Dx = 10, FeedMmS = 20 },
            new RobotLimits { ReachMin = 235.0 });
        Assert.False(plan.Ok);
    }

    [Fact]
    public void Jog_BeyondEnvelope_Refused()
    {
        var plan = JogPlanner.Plan(Snap(x: 295, y: 150), new JogRequest { Dx = 10, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(plan.Ok);
        Assert.Contains("exceeds envelope maximum", plan.RefusalReason);
    }

    [Fact]
    public void Jog_BelowZMin_Refused()
    {
        var plan = JogPlanner.Plan(Snap(z: 0), new JogRequest { Dz = -0.1, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(plan.Ok);
        // 0.1 is a whitelisted step, so the refusal must come from the Z bound
        Assert.Contains("Z", plan.RefusalReason);
    }

    [Fact]
    public void Jog_StateGateStillApplies()
    {
        var stale = JogPlanner.Plan(Snap(ageS: 10), new JogRequest { Dx = 1, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(stale.Ok);
        Assert.Contains("stale", stale.RefusalReason);

        var unhomed = JogPlanner.Plan(Snap(homed: false), new JogRequest { Dx = 1, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(unhomed.Ok);
        Assert.Contains("homed", unhomed.RefusalReason);

        var busy = JogPlanner.Plan(Snap(status: MachineStatus.Processing),
            new JogRequest { Dx = 1, FeedMmS = 20 }, CartLim(),
            mechanism: RobotMechanism.Cartesian);
        Assert.False(busy.Ok);
    }

    [Fact]
    public void Jog_FeedCapAndStepWhitelistStillApply()
    {
        var fast = JogPlanner.Plan(Snap(), new JogRequest { Dx = 1, FeedMmS = 500 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(fast.Ok);
        Assert.Contains("feed", fast.RefusalReason);

        var oddStep = JogPlanner.Plan(Snap(), new JogRequest { Dx = 3, FeedMmS = 20 },
            CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(oddStep.Ok);
        Assert.Contains("not in", oddStep.RefusalReason);
    }

    // ---- upload gate -------------------------------------------------------

    static List<string> CartesianJob() => new List<string>
    {
        "; generic cartesian job", "G21", "G90", "M83", "G28",
        "G1 X100 Y100 Z5 F3000",
        "G1 X110 Y120 Z5 E1.0 F1200",
        "G1 X120 Y140 Z5 E1.0",
    };

    [Fact]
    public void CartesianJob_Allowed_WhereScaraRefuses()
    {
        var cart = JobGate.Validate(CartesianJob(), CartLim(),
            mechanism: RobotMechanism.Cartesian);
        Assert.True(cart.Allowed, cart.Summary);
        Assert.Equal(0, cart.Errors);
        Assert.Equal(3, cart.Moves);

        // the identical job through the SCARA gate is refused (dead zone, Y > 0)
        var scara = JobGate.Validate(CartesianJob(), new RobotLimits { ReachMin = 235.0 });
        Assert.False(scara.Allowed);
    }

    [Fact]
    public void CartesianJob_EnvelopeViolation_Refused()
    {
        var job = CartesianJob();
        job.Add("G1 X400 Y100 Z5 E1.0");   // X400 > XMax 300
        var res = JobGate.Validate(job, CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(res.Allowed);
        Assert.True(res.Errors > 0);
        Assert.Contains("DANGER", res.Summary);
    }

    [Fact]
    public void PerAxisG28_SplitByKinematics()
    {
        // Maintainer decision 2026-08-31: per-axis G28 is refused only where the
        // refusal has a physical basis - SCARA joint homing moves both Cartesian
        // axes. A Cartesian profile routes allowPerAxisHome through the gate, so
        // ordinary Cartesian job files (G28 X / G28 Y / G28 Z) validate.
        var job = new List<string>
        {
            "G21", "G90", "M83", "G28 X", "G28 Y", "G28 Z",
            "G1 X100 Y100 Z5 F3000", "G1 X110 Y120 Z5 E1.0 F1200",
        };
        var cart = JobGate.Validate(job, CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.True(cart.Allowed, cart.Summary);

        // the SCARA boundary is unchanged: same file, default mechanism, refused
        var scara = JobGate.Validate(job, new RobotLimits { ReachMin = 235.0 });
        Assert.False(scara.Allowed);
        Assert.Contains("parse failed", scara.Summary);
    }

    [Fact]
    public void PerAxisG28_UnknownParameterStillRejected()
    {
        // allowPerAxisHome opens X/Y/Z only - any other G28 word stays refused
        var job = new List<string> { "G21", "G90", "M83", "G28 C", "G1 X100 Y100 Z5 F3000" };
        var res = JobGate.Validate(job, CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(res.Allowed);
        Assert.Contains("parse failed", res.Summary);
    }

    [Fact]
    public void CartesianJob_OverspeedIsWarning_AndBlockOnWarningsRefuses()
    {
        var job = CartesianJob();
        job.Add("G1 X130 Y150 Z5 E1.0 F12000");   // 200 mm/s > MaxFeed 150
        var open = JobGate.Validate(job, CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.True(open.Allowed);
        Assert.True(open.Warnings > 0);

        var strict = JobGate.Validate(job, CartLim(), blockOnWarnings: true,
            mechanism: RobotMechanism.Cartesian);
        Assert.False(strict.Allowed);
    }

    [Fact]
    public void MechanismEntersGateHash()
    {
        var cart = JobGate.Validate(CartesianJob(), CartLim(), mechanism: RobotMechanism.Cartesian);
        var scara = JobGate.Validate(CartesianJob(), CartLim());
        Assert.NotEqual(cart.Hash, scara.Hash);
    }

    // ---- CartesianChecks internals ----------------------------------------

    [Fact]
    public void ArcInterior_OutsideEnvelope_RefusedDespiteLegalEndpoints()
    {
        // CW semicircle from (40,100) to (40,200) about (40,150), radius 50: the
        // interior passes through (-10,150), 10mm outside XMin=0, while both
        // endpoints are legal - exactly the case endpoint-only validation misses.
        var job = new List<string>
        {
            "G21", "G90", "M83", "G28",
            "G1 X40 Y100 Z5 F3000",
            "G2 X40 Y200 I0 J50 E1.0 F1200",
        };
        var res = JobGate.Validate(job, CartLim(), mechanism: RobotMechanism.Cartesian);
        Assert.False(res.Allowed);
        Assert.Contains("below envelope minimum", res.Summary + string.Join(";",
            res.Checks.Problems.ConvertAll(p => p.Message)));
    }

    [Fact]
    public void CartesianChecks_RunPoints_FlagsEachViolation()
    {
        var res = CartesianChecks.RunPoints(new[]
        {
            new Vec3(150, 150, 10),     // fine
            new Vec3(-5, 150, 10),      // X below
            new Vec3(150, 350, 10),     // Y above
            new Vec3(150, 150, 250),    // Z above
        }, CartLim());
        Assert.False(res.ReachOk);
        Assert.Equal(3, res.Problems.Count);
        Assert.False(res.FinalConfiguration.Known);
        Assert.Empty(res.Iks);
    }
}
