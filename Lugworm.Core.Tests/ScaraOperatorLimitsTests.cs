using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>On the SCARA the X/Y box belongs to the operator (M208 in config.g, the
/// xMin..yMax inputs): changing it never refuses a job, a jog or a move (maintainer
/// decision of 2026-11-04). The geometric guard that stays is the elbow: a print job
/// keeps J3 at least 5 degrees away from the straight arm.</summary>
public class ScaraOperatorLimitsTests
{
    static readonly Vec3 HomePose = new Vec3(200, -500, 400);

    static RobotLimits Limits() => new RobotLimits
    {
        ReachMin = 235.0,
        HomedArmMode = 1,
        CalibratedHomePose = HomePose,
        RequireCalibratedHomePose = true,
    };

    static string[] Job(double x, double y)
    {
        string X = x.ToString("0.###", CultureInfo.InvariantCulture);
        string Y = y.ToString("0.###", CultureInfo.InvariantCulture);
        return new[]
        {
            "M83", "G28", "G90",
            $"G1 X{X} Y{Y} Z10 F3000",
            $"G1 X{X} Y{Y} Z10.5 E1 F600",
        };
    }

    static void SetBound(RobotLimits lim, string bound, double value)
    {
        switch (bound)
        {
            case "xMin": lim.XMin = value; break;
            case "xMax": lim.XMax = value; break;
            case "yMin": lim.YMin = value; break;
            case "yMax": lim.YMax = value; break;
            default: throw new ArgumentException(bound);
        }
    }

    // (578.5, -689.4) passes every SCARA check (see J1SeamMarginTests); each row puts
    // one operator X/Y bound on the wrong side of it.
    [Theory]
    [InlineData("xMin", 700.0)]
    [InlineData("xMax", 100.0)]
    [InlineData("yMin", -600.0)]
    [InlineData("yMax", -800.0)]
    public void JobGate_IgnoresTheXYEnvelope(string bound, double value)
    {
        var lim = Limits();
        SetBound(lim, bound, value);

        var res = JobGate.Validate(Job(578.5, -689.4), lim, elbow: 1);

        Assert.True(res.Allowed, res.Summary);
    }

    [Fact]
    public void Jog_IgnoresTheXYEnvelope()
    {
        var lim = new RobotLimits { ReachMin = 235.0, YMax = -100.0 };
        var snap = new ObjectModelSnapshot
        {
            Status = MachineStatus.Idle,
            RawStatus = "idle",
            TakenUtc = DateTime.UtcNow,
            AllHomed = true,
            Axes = new List<AxisState>
            {
                new AxisState { Letter = "X", MachinePosition = 600, Homed = true },
                new AxisState { Letter = "Y", MachinePosition = 0, Homed = true },
                new AxisState { Letter = "Z", MachinePosition = 100, Homed = true },
            },
        };

        var plan = JogPlanner.Plan(snap, new JogRequest { Dx = 10, FeedMmS = 20 }, lim);

        Assert.True(plan.Ok, plan.RefusalReason);
    }

    // The lab board's original M208 is X[-300,10000] Y[-10000,0]; the operator may
    // tighten or widen it in any direction.
    [Theory]
    [InlineData(-300.0, 800.0, -10000.0, 0.0)]
    [InlineData(-300.0, 10000.0, -900.0, 0.0)]
    [InlineData(-1000.0, 10000.0, -10000.0, 300.0)]
    public void FirmwareXYBox_IsNotComparedOnScara(double xMin, double xMax, double yMin, double yMax)
    {
        var snap = new ObjectModelSnapshot
        {
            Status = MachineStatus.Idle,
            TakenUtc = DateTime.UtcNow,
            Axes = new List<AxisState>
            {
                new AxisState { Letter = "X", Min = xMin, Max = xMax },
                new AxisState { Letter = "Y", Min = yMin, Max = yMax },
                new AxisState { Letter = "Z", Min = 0, Max = 400 },
            },
        };

        var findings = FirmwareLimitCheck.CompareFindings(snap, new RobotLimits(), RobotMechanism.Scara);

        Assert.Empty(findings);
    }

    // Targets on bearing -20 deg at the radius of a given elbow angle, hand-computed
    // from L1 549.06 / L2 457.23: r = sqrt(L1^2 + L2^2 + 2 L1 L2 cos J3).
    [Theory]
    [InlineData(945.282, -344.054, false)]   // J3 3.0 deg
    [InlineData(944.746, -343.859, false)]   // J3 4.9 deg
    [InlineData(944.675, -343.833, true)]    // J3 5.1 deg
    [InlineData(943.319, -343.340, true)]    // J3 8.0 deg
    public void JobGate_RefusesTargetsWithinFiveDegreesOfTheStraightArm(double x, double y, bool allowed)
    {
        var res = JobGate.Validate(Job(x, y), Limits(), elbow: 1);

        Assert.Equal(allowed, res.Allowed);
        if (!allowed)
            Assert.Contains(res.Checks.Problems,
                p => p.Sev == Severity.Error && p.Message.Contains("full extension"));
    }

    [Fact]
    public void Jogs_AreNotHeldToTheElbowMargin()
    {
        var check = RobotChecks.RunPoints(
            new[] { new Vec3(900, -330, 100), new Vec3(945.282, -344.054, 100) },
            Limits(), ArmConfiguration.Seed(1));

        Assert.DoesNotContain(check.Problems, p => p.Sev == Severity.Error);
    }
}
