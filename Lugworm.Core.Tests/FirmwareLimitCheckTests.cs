using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class FirmwareLimitCheckTests
{
    // The lab board's real M208: X[-300,10000] Y[-10000,0] Z[0,400]. When yMin/yMax
    // are omitted the axis mirrors X (kept for the Z-only cases below).
    static ObjectModelSnapshot Snap(double zMin, double zMax,
        double xMin = -1100, double xMax = 1100, double? yMin = null, double? yMax = null)
        => new ObjectModelSnapshot
        {
            Status = MachineStatus.Idle,
            TakenUtc = DateTime.UtcNow,
            Axes = new List<AxisState>
            {
                new AxisState { Letter = "X", Min = xMin, Max = xMax },
                new AxisState { Letter = "Y", Min = yMin ?? xMin, Max = yMax ?? xMax },
                new AxisState { Letter = "Z", Min = zMin, Max = zMax },
            },
        };

    // Software model matching the shipped negative-Y half-plane defaults, Z aside.
    static RobotLimits Defaults(double zMax = 400.0) => new RobotLimits
    {
        ZMax = zMax,
        XMin = -300,
        XMax = 10000,
        YMin = -10000,
        YMax = 0,
        CheckEnvelope = true,
    };

    // ---- Z axis (unchanged contract) ---------------------------------------

    [Fact]
    public void SoftwareAllowsMoreZThanFirmware_HardWarning()
    {
        var lim = Defaults(zMax: 800.0);
        var warns = FirmwareLimitCheck.Compare(Snap(0, 400, -300, 10000, -10000, 0), lim);
        Assert.Contains(warns, w => w.Contains("exceeds the firmware"));
    }

    [Fact]
    public void FirmwareAllowsMoreZ_InformationalOnly()
    {
        var lim = Defaults(zMax: 400.0);
        var warns = FirmwareLimitCheck.Compare(Snap(0, 1100, -300, 10000, -10000, 0), lim);
        Assert.Contains(warns, w => w.Contains("informational"));
    }

    [Fact]
    public void LegacyPoll_MinMaxZero_Skipped()
    {
        var lim = Defaults(zMax: 800.0);
        // every axis 0/0 - no limit data on a legacy rr_status poll
        Assert.Empty(FirmwareLimitCheck.Compare(Snap(0, 0, 0, 0, 0, 0), lim));
    }

    // ---- X/Y envelope (new signed per-bound contract) ----------------------

    [Fact]
    public void RealBoardMatchesShippedDefaults_NoFindings()
    {
        // firmware X[-300,10000] Y[-10000,0] Z[0,400] == the shipped software envelope
        Assert.Empty(FirmwareLimitCheck.CompareFindings(
            Snap(0, 400, -300, 10000, -10000, 0), Defaults()));
    }

    [Fact]
    public void SoftwareAllowsMoreYThanFirmware_IsErrorSeverity()
    {
        // the Y0->Y300 hand-edit case, but reversed onto the software side: software
        // permits +300 while firmware caps at 0 -> the board clips those moves.
        var lim = Defaults();
        lim.YMax = 300;
        var findings = FirmwareLimitCheck.CompareFindings(
            Snap(0, 400, -300, 10000, -10000, 0), lim);
        Assert.Contains(findings, f => f.Severity == Severity.Error
            && f.Message.Contains("Y maximum") && f.Message.Contains("exceeds firmware"));
    }

    [Fact]
    public void FirmwareAllowsMoreYThanSoftware_IsInformationalNotError()
    {
        // firmware hand-edited to Y+300 while the software stays at Y0: the real drift
        // direction. Software is more restrictive (safe) - reported, but not blocking.
        var findings = FirmwareLimitCheck.CompareFindings(
            Snap(0, 400, -300, 10000, -10000, 300), Defaults());
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
        Assert.Contains(findings, f => f.Severity == Severity.Info && f.Message.Contains("Y"));
    }

    [Fact]
    public void LargeXBound_IsCompared_NotSkipped()
    {
        // regression guard: the old >=9000 skip silently ignored X and Y. A software
        // X maximum beyond the firmware's 10000 must now be caught, large value or not.
        var lim = Defaults();
        lim.XMax = 11000;
        var findings = FirmwareLimitCheck.CompareFindings(
            Snap(0, 400, -300, 10000, -10000, 0), lim);
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("X maximum"));
    }

    [Fact]
    public void EnvelopeCheckDisabled_SkipsXY()
    {
        var lim = Defaults();
        lim.YMax = 300;            // would otherwise error
        lim.CheckEnvelope = false;
        var findings = FirmwareLimitCheck.CompareFindings(
            Snap(0, 400, -300, 10000, -10000, 0), lim);
        Assert.DoesNotContain(findings, f => f.Message.Contains("Y") || f.Message.Contains("X"));
    }

    // ---- M669 kinematics cross-check ---------------------------------------

    [Fact]
    public void KinematicsMatch_NoFindings()
    {
        var lim = new RobotLimits(); // L1/L2/J-limits/ReachMin already the board values
        var m669 = new M669Parameters
        {
            P = lim.L1,
            D = lim.L2,
            AMin = lim.J1Min,
            AMax = lim.J1Max,
            BMin = lim.J3Min,
            BMax = lim.J3Max,
            R = lim.ReachMin,
        };
        Assert.Empty(FirmwareLimitCheck.CompareKinematics(m669, lim));
    }

    [Fact]
    public void KinematicsLinkMismatch_IsError()
    {
        var lim = new RobotLimits();
        var m669 = new M669Parameters
        {
            P = lim.L1 + 5,
            D = lim.L2,
            AMin = lim.J1Min,
            AMax = lim.J1Max,
            BMin = lim.J3Min,
            BMax = lim.J3Max,
            R = lim.ReachMin,
        };
        var findings = FirmwareLimitCheck.CompareKinematics(m669, lim);
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("P"));
    }
}
