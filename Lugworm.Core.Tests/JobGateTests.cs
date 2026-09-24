using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class JobGateTests
{
    static RobotLimits Lim() => new RobotLimits { ReachMin = 235.0 };

    static List<string> GoodJob() => new List<string>
    {
        "; test job", "G21", "G90", "M83", "G28",
        "G1 X600 Y0 Z5 F3000",
        "G1 X610 Y-10 Z5 E1.0 F1200",
        "G1 X620 Y-20 Z5 E1.0",
    };   // negative-Y half-plane - the machine's real working envelope (M208 Y<=0)

    [Fact]
    public void GoodJob_Allowed()
    {
        var res = JobGate.Validate(GoodJob(), Lim());
        Assert.True(res.Allowed);
        Assert.Equal(0, res.Errors);
        Assert.Equal(3, res.Moves);
        Assert.Contains("OK", res.Summary);
    }

    [Fact]
    public void StartPoint_IsTheStartOfTheFirstExtrudingMove()
    {
        // GoodJob travels to (600, 0, 5) first and extrudes from there: the print
        // start is where the first layer begins, not the first move's target.
        var res = JobGate.Validate(GoodJob(), Lim());
        Assert.True(res.StartPoint.HasValue);
        Assert.Equal(600.0, res.StartPoint!.Value.X, 9);
        Assert.Equal(0.0, res.StartPoint.Value.Y, 9);
        Assert.Equal(5.0, res.StartPoint.Value.Z, 9);
    }

    [Fact]
    public void StartPoint_IsNullWithoutAnExtrudingMove()
    {
        var travelOnly = new List<string> { "G21", "G90", "M83", "G28", "G1 X600 Y0 Z5 F3000", "G1 X650 Y-50 Z5" };
        Assert.Null(JobGate.Validate(travelOnly, Lim()).StartPoint);
        // a parse failure has no start either
        Assert.Null(JobGate.Validate(new List<string> { "G1 X600 Y0 Z5", "G98" }, Lim()).StartPoint);
    }

    [Fact]
    public void OutOfReachMove_Refused()
    {
        var job = GoodJob();
        job.Add("G1 X1200 Y0 Z5 E1.0");   // r=1200 > 1006.29
        var res = JobGate.Validate(job, Lim());
        Assert.False(res.Allowed);
        Assert.True(res.Errors > 0);
        Assert.Contains("DANGER", res.Summary);
    }

    [Fact]
    public void DeadZoneMove_Refused()
    {
        var job = GoodJob();
        job.Add("G1 X100 Y0 Z5");   // r=100 < 235 dead zone
        var res = JobGate.Validate(job, Lim());
        Assert.False(res.Allowed);
    }

    [Fact]
    public void EmptyProgram_Refused()
    {
        var res = JobGate.Validate(new List<string> { "; nothing", "M83" }, Lim());
        Assert.False(res.Allowed);
        Assert.Contains("no motion", res.Summary);
    }

    [Fact]
    public void ChordAcrossDeadZone_Refused()
    {
        // both endpoints legal and in-envelope (r ≈ 316, X in [-300,300]) but the
        // straight move between them passes the mast at min r = 100 - endpoints-only
        // checking used to let this print.
        var job = new List<string>
        {
            "G21", "G90", "M83", "G28",
            "G1 X300 Y-100 Z5 F3000",
            "G1 X-300 Y-100 Z5 E1.0 F1200",
        };
        var res = JobGate.Validate(job, Lim());
        Assert.False(res.Allowed);
        // the interior crosses the R=235 dead-zone; under RRF the folded elbow also
        // drives J3 out of range there - either is a mid-segment safety catch.
        Assert.Contains(res.Checks.Problems,
            p => p.Sev == Severity.Error &&
                 (p.Message.Contains("dead-zone") || p.Message.Contains("joint range")));
    }

    [Fact]
    public void MastClearanceWarning_BlocksByDefault_PolicyCanAllow()
    {
        // exploration limits (derived annulus, joints unchecked): TCP at r=100 makes
        // link 2 sweep ~46mm from the mast - warning severity, but the gate must
        // treat it as blocking unless mastAsError is explicitly turned off
        var lim = new RobotLimits { ReachMin = 0, CheckJointRanges = false };
        var job = new List<string> { "M83", "G28", "G1 X100 Y0 Z5 E1 F600" };

        var strict = JobGate.Validate(job, lim);
        Assert.False(strict.Allowed);

        var relaxed = JobGate.Validate(job, lim, mastAsError: false);
        Assert.True(relaxed.Allowed, relaxed.Summary);
        Assert.True(relaxed.Warnings > 0);
    }

    [Fact]
    public void BlockOnWarnings_RefusesOverspeedJob()
    {
        var job = GoodJob();
        job.Add("G1 X630 Y-30 Z5 E1.0 F12000");   // 200 mm/s > MaxFeed 150 → warning
        var open = JobGate.Validate(job, Lim());
        var strict = JobGate.Validate(job, Lim(), blockOnWarnings: true);
        Assert.True(open.Allowed && open.Warnings > 0, open.Summary);
        Assert.False(strict.Allowed);
        Assert.Contains("blocked by policy", strict.Summary);
    }

    [Fact]
    public void Hash_ChangesWithPolicyFlags()
    {
        var a = JobGate.Validate(GoodJob(), Lim());
        var b = JobGate.Validate(GoodJob(), Lim(), blockOnWarnings: true);
        var c = JobGate.Validate(GoodJob(), Lim(), mastAsError: false);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Fact]
    public void ParityWithRobotChecks()
    {
        var job = GoodJob();
        var res = JobGate.Validate(job, Lim());
        var direct = RobotChecks.Run(GcodeParser.Parse(job), Lim(), GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.Equal(direct.Problems.Count(p => p.Sev == Severity.Error), res.Errors);
        Assert.Equal(direct.Problems.Count(p => p.Sev == Severity.Warning), res.Warnings);
    }

    [Fact]
    public void Hash_ChangesOnSingleCharacterEdit()
    {
        var a = JobGate.Validate(GoodJob(), Lim());
        var job = GoodJob();
        job[5] = "G1 X600 Y0 Z6 F3000";   // one digit changed
        var b = JobGate.Validate(job, Lim());
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Hash_ChangesOnLimitChange()
    {
        var a = JobGate.Validate(GoodJob(), Lim());
        var lim = Lim(); lim.ZMax = 900;
        var b = JobGate.Validate(GoodJob(), lim);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Hash_StableForSameInput()
    {
        var a = JobGate.Validate(GoodJob(), Lim());
        var b = JobGate.Validate(GoodJob(), Lim());
        Assert.Equal(a.Hash, b.Hash);
    }
}
