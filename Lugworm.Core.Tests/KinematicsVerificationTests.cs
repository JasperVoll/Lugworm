using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Regression cover for the kinematics-verification chain that gates every
/// upload and print start, and for the sampled-index contract the robot preview
/// depends on. Each fact here corresponds to a defect that shipped unnoticed because
/// nothing exercised these paths.</summary>
public class KinematicsVerificationTests
{
    // The verbatim shape of a bare M669 reply from the lab board (RRF 3.5.1).
    // Note there is no R word - RRF omits it when the minimum radius is unset.
    const string LiveM669 = "Kinematics is Scara, P549.06 D457.23 A-41.1:319.1 B-151.2:151.2 C0.00:0.00:0.00 S200.00 T4.00";

    [Fact]
    public void M669_RealReplyWithoutR_Parses()
    {
        // Requiring R made every real reply unparseable, so KinematicsProbed never
        // became true and upload/start was refused on a correctly configured board.
        Assert.True(DuetPollClient.TryParseM669(LiveM669, out var k));
        Assert.Equal(549.06, k.P, 3);
        Assert.Equal(457.23, k.D, 3);
        Assert.Equal(-41.1, k.AMin, 3);
        Assert.Equal(319.1, k.AMax, 3);
        Assert.Equal(-151.2, k.BMin, 3);
        Assert.Equal(151.2, k.BMax, 3);
        Assert.False(k.HasR);
    }

    [Fact]
    public void M669_WithR_ReportsIt()
    {
        Assert.True(DuetPollClient.TryParseM669(
            "Kinematics is Scara, P549.06 D457.23 A-41.1:319.1 B-151.2:151.2 R235", out var k));
        Assert.True(k.HasR);
        Assert.Equal(235, k.R, 3);
    }

    [Fact]
    public void M669_MissingRequiredWord_Fails()
    {
        Assert.False(DuetPollClient.TryParseM669("Kinematics is Scara, P549.06 D457.23", out _));
    }

    /// <summary>The field names RepRapFirmware actually publishes under move.kinematics.
    /// Taken from ScaraKinematics::objectModelTable, which declares exactly: crosstalk,
    /// distalLength, minRadius, name, proximalLength, psiLimits, thetaLimits, xOffset,
    /// yOffset.
    ///
    /// This is the regression guard for a defect that 602 green tests missed: the parser
    /// and every fixture used invented names (proximalArmLength, distalArmAngleLimits,
    /// ...), so the suite verified the parser against a board that does not exist. On real
    /// hardware nothing parsed, KinematicsProbed stayed false, and that both raised a
    /// standing "kinematics are unverified" warning and refused upload/start.</summary>
    const string RealScaraModel =
        "{\"name\":\"Scara\",\"proximalLength\":549.06,\"distalLength\":457.23,"
        + "\"thetaLimits\":[-41.1,319.1],\"psiLimits\":[-151.2,151.2],"
        + "\"crosstalk\":[0,0,0],\"xOffset\":0,\"yOffset\":0}";

    [Fact]
    public void ModelKinematics_RealRrfFieldNames_Parses()
    {
        Assert.True(DuetPollClient.TryParseModelKinematics(JsonValue.Parse(RealScaraModel), out var k));
        Assert.Equal(549.06, k.P, 3);
        Assert.Equal(457.23, k.D, 3);
        Assert.Equal(-41.1, k.AMin, 3);
        Assert.Equal(319.1, k.AMax, 3);
        Assert.Equal(-151.2, k.BMin, 3);
        Assert.Equal(151.2, k.BMax, 3);
        // The board carries M669 R235, but minRadius is reported only when RRF has a
        // requested minimum radius to report; absent must not fail the parse.
        Assert.False(k.HasR);
    }

    /// <summary>The shape a DEPTH-LIMITED rr_model query returns: correct scalar fields,
    /// but nested arrays collapsed to "[]". `rr_model` defaults to depth 1, so querying
    /// move.kinematics with flags "v" instead of "vnd4" produced exactly this - and the
    /// arms' angle limits are the two values the parse cannot do without.
    ///
    /// It must fail closed rather than treat the empty arrays as zeros, which would
    /// compare the machine against a 0..0 joint range. The real defence is the depth flag
    /// in ProbeModelKinematicsAsync; this pins the parser's half of it.</summary>
    /// <summary>The PROSE form is what a bare M669 actually returns - captured verbatim
    /// from the lab board (RRF 3.5.1) on 2026-08-24. The P/D/A/B letter form the parser
    /// originally required is the SET syntax, so every real reply was rejected.</summary>
    [Fact]
    public void M669_ProseReply_FromRealBoard_Parses()
    {
        const string reply =
            "Kinematics is Scara, proximal arm 549.06mm range -41.1 to 319.1° (continuous), "
            + "distal arm 457.23mm range -151.2 to 151.2° (continuous), "
            + "crosstalk 0.0:0.0:0.0, bed origin (0.0, 0.0), segments/sec 100, "
            + "min. segment length 1.00";

        Assert.True(DuetPollClient.TryParseM669(reply, out var k));
        Assert.Equal(549.06, k.P, 3);
        Assert.Equal(457.23, k.D, 3);
        Assert.Equal(-41.1, k.AMin, 3);
        Assert.Equal(319.1, k.AMax, 3);
        Assert.Equal(-151.2, k.BMin, 3);
        Assert.Equal(151.2, k.BMax, 3);
        Assert.False(k.HasR);   // the prose report has no minimum-radius field at all
    }

    /// <summary>Same reply without the degree signs or "(continuous)" suffixes, which
    /// firmware builds vary on.</summary>
    [Fact]
    public void M669_ProseReply_WithoutDegreeSigns_Parses()
    {
        const string reply =
            "Kinematics is Scara, proximal arm 549.06mm range -41.1 to 319.1, "
            + "distal arm 457.23mm range -151.2 to 151.2, crosstalk 0.0:0.0:0.0";

        Assert.True(DuetPollClient.TryParseM669(reply, out var k));
        Assert.Equal(549.06, k.P, 3);
        Assert.Equal(151.2, k.BMax, 3);
    }

    /// <summary>A non-SCARA prose report has no arm clauses and must fail closed.</summary>
    [Fact]
    public void M669_ProseReply_NonScara_Rejected()
    {
        Assert.False(DuetPollClient.TryParseM669("Kinematics is Cartesian", out _));
    }

    [Fact]
    public void ModelKinematics_DepthLimitedReply_EmptyArrays_Rejected()
    {
        var json = JsonValue.Parse(
            "{\"name\":\"Scara\",\"proximalLength\":549.06,\"distalLength\":457.23,"
            + "\"thetaLimits\":[],\"psiLimits\":[],\"crosstalk\":[]}");
        Assert.False(DuetPollClient.TryParseModelKinematics(json, out _));
    }

    /// <summary>A single-element limit array is equally unusable - min and max are both
    /// required - and must not be read as a range.</summary>
    [Fact]
    public void ModelKinematics_TruncatedLimitArray_Rejected()
    {
        var json = JsonValue.Parse(
            "{\"name\":\"Scara\",\"proximalLength\":549.06,\"distalLength\":457.23,"
            + "\"thetaLimits\":[-41.1],\"psiLimits\":[-151.2,151.2]}");
        Assert.False(DuetPollClient.TryParseModelKinematics(json, out _));
    }

    [Fact]
    public void ModelKinematics_RealFieldNames_WithMinRadius_ParsesR()
    {
        var json = JsonValue.Parse(
            "{\"name\":\"Scara\",\"proximalLength\":549.06,\"distalLength\":457.23,"
            + "\"thetaLimits\":[-41.1,319.1],\"psiLimits\":[-151.2,151.2],\"minRadius\":235}");
        Assert.True(DuetPollClient.TryParseModelKinematics(json, out var k));
        Assert.True(k.HasR);
        Assert.Equal(235.0, k.R, 3);
    }

    [Fact]
    public void ModelKinematics_ScaraObject_Parses()
    {
        // Legacy/alternate spellings stay accepted for other or older builds.
        var json = JsonValue.Parse(
            "{\"name\":\"scara\",\"proximalArmLength\":549.06,\"distalArmLength\":457.23,"
            + "\"proximalArmAngleLimits\":[-41.1,319.1],\"distalArmAngleLimits\":[-151.2,151.2]}");
        Assert.True(DuetPollClient.TryParseModelKinematics(json, out var k));
        Assert.Equal(549.06, k.P, 3);
        Assert.Equal(457.23, k.D, 3);
        Assert.Equal(319.1, k.AMax, 3);
        Assert.Equal(-151.2, k.BMin, 3);
        Assert.False(k.HasR);   // absent from the object model, same as the M669 report
    }

    [Fact]
    public void ModelKinematics_NonScara_Rejected()
    {
        // A board reporting different kinematics must leave the machine unverified
        // (fail closed), never silently compare a SCARA model against it.
        var json = JsonValue.Parse(
            "{\"name\":\"cartesian\",\"proximalArmLength\":549.06,\"distalArmLength\":457.23,"
            + "\"proximalArmAngleLimits\":[-41.1,319.1],\"distalArmAngleLimits\":[-151.2,151.2]}");
        Assert.False(DuetPollClient.TryParseModelKinematics(json, out _));
    }

    [Fact]
    public void ModelKinematics_MissingFields_Rejected()
    {
        var json = JsonValue.Parse("{\"name\":\"scara\",\"proximalArmLength\":549.06}");
        Assert.False(DuetPollClient.TryParseModelKinematics(json, out _));
    }

    [Fact]
    public void ModelKinematics_NullOrEmpty_Rejected()
    {
        Assert.False(DuetPollClient.TryParseModelKinematics(null, out _));
        Assert.False(DuetPollClient.TryParseModelKinematics(JsonValue.Parse("[]"), out _));
    }

    [Fact]
    public void CompareKinematics_AbsentR_RaisesNoMismatch()
    {
        // The board reports no R; ReachMin is 235. Treating "absent" as 0 produced a
        // spurious Error finding that refused upload on a correct machine.
        var firmware = new M669Parameters
        {
            P = 549.06,
            D = 457.23,
            AMin = -41.1,
            AMax = 319.1,
            BMin = -151.2,
            BMax = 151.2,
            HasR = false,
        };
        var lim = new RobotLimits
        {
            L1 = 549.06,
            L2 = 457.23,
            J1Min = -41.1,
            J1Max = 319.1,
            J3Min = -151.2,
            J3Max = 151.2,
            ReachMin = 235,
        };
        Assert.Empty(FirmwareLimitCheck.CompareKinematics(firmware, lim));
    }

    [Fact]
    public void CompareKinematics_ReportedRMismatch_StillFlagged()
    {
        var firmware = new M669Parameters
        {
            P = 549.06,
            D = 457.23,
            AMin = -41.1,
            AMax = 319.1,
            BMin = -151.2,
            BMax = 151.2,
            R = 100,
            HasR = true,
        };
        var lim = new RobotLimits
        {
            L1 = 549.06,
            L2 = 457.23,
            J1Min = -41.1,
            J1Max = 319.1,
            J3Min = -151.2,
            J3Max = 151.2,
            ReachMin = 235,
        };
        var findings = FirmwareLimitCheck.CompareKinematics(firmware, lim);
        Assert.Contains(findings, f => f.Message.Contains("R"));
    }

    [Fact]
    public void CompareKinematics_ArmLengthMismatch_IsAnError()
    {
        var firmware = new M669Parameters
        {
            P = 500,
            D = 457.23,
            AMin = -41.1,
            AMax = 319.1,
            BMin = -151.2,
            BMax = 151.2,
        };
        var lim = new RobotLimits
        {
            L1 = 549.06,
            L2 = 457.23,
            J1Min = -41.1,
            J1Max = 319.1,
            J3Min = -151.2,
            J3Max = 151.2,
        };
        var findings = FirmwareLimitCheck.CompareKinematics(firmware, lim);
        Assert.Contains(findings, f => f.Severity == Severity.Error);
    }
}

/// <summary>The renderer requires CheckResult.Iks to be index-for-index with the
/// sampled toolpath. When no G28 anchors the start pose the start point is not
/// validated, but the sampler still emits it - the slot must still exist.</summary>
public class SampledIndexContractTests
{
    static RobotLimits Lim() => new RobotLimits { ReachMin = 235 };

    /// <summary>Mirrors ToolpathSampler.SampleMoves' counting rule: the first move
    /// contributes its whole polyline, every later move contributes all but its
    /// start point (which is the previous move's end).</summary>
    static int SampledPointCount(IReadOnlyList<ParsedMove> moves, double arcTol)
    {
        int count = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            var poly = RobotChecks.SampleMovePath(moves[i], arcTol);
            count += i == 0 ? poly.Count : poly.Count - 1;
        }
        return count;
    }

    [Fact]
    public void HeaderlessGcode_IkCountMatchesSampleCount()
    {
        // No G28: HomePoseKnown is false, so the start point is deliberately not
        // validated. Before the fix Iks was short by one and the robot component
        // aborted with "validated pose/sample mismatch" on every headerless job.
        var prog = GcodeParser.Parse(new[]
        {
            "G90",
            "G1 X400 Y-500 Z10 F1200",
            "G1 X420 Y-520 Z10",
        });
        Assert.False(prog.HomePoseKnown);

        var check = RobotChecks.Run(prog, Lim(), GcodeFlavor.DuetRrf, arcSupport: true, elbow: 0);
        Assert.Equal(SampledPointCount(prog.Moves, 0.05), check.Iks.Count);
    }

    [Fact]
    public void HeaderlessGcode_UnknownStartSlotIsMarkedInvalid()
    {
        // The placeholder must never be mistaken for a validated pose.
        var prog = GcodeParser.Parse(new[] { "G90", "G1 X400 Y-500 Z10 F1200" });
        var check = RobotChecks.Run(prog, Lim(), GcodeFlavor.DuetRrf, arcSupport: true, elbow: 0);

        Assert.NotEmpty(check.Iks);
        Assert.False(check.Iks[0].Valid);
        Assert.Contains("start pose unknown", check.Iks[0].Error);
        // Flagged so publishers of joint angles can drop it - its (0,0,0) is filler,
        // and emitting it would put a bogus J1/J3/J2 value into the component outputs.
        Assert.True(check.FirstSampleUnvalidated);
    }

    [Fact]
    public void HomedGcode_HasNoPlaceholderSlot()
    {
        // With the pose anchored, the start point is genuinely validated and the flag
        // must stay clear so nothing is skipped from the joint outputs.
        // The pose is known only when a G28 precedes motion AND a calibrated home pose
        // is supplied - the measured post-homeall.g pose, here X200 Y-500 Z400.
        var prog = GcodeParser.Parse(new[]
        {
            "G28",
            "G90",
            "G1 X400 Y-500 Z10 F1200",
        }, new Vec3(200, -500, 400));
        Assert.True(prog.HomePoseKnown);

        var check = RobotChecks.Run(prog, Lim(), GcodeFlavor.DuetRrf, arcSupport: true, elbow: 0);
        Assert.False(check.FirstSampleUnvalidated);
    }

    [Fact]
    public void MoveIks_StayOnePerMove_EvenWhenAnArcIsSampled()
    {
        // The J1/J3/J2 outputs publish MoveIks. Arcs are validated along the swept
        // path, so Iks holds interior samples - publishing those would change the
        // output list length of every saved definition that wires these outputs.
        var prog = GcodeParser.Parse(new[]
        {
            "G28",
            "G90",
            "G1 X400 Y-500 Z10 F1200",
            // centre (400,-600), r=100 from both endpoints - the parser rejects any
            // arc whose two radii disagree, so the geometry has to be exact
            "G2 X500 Y-600 I0 J-100",
            "G1 X520 Y-620",
        }, new Vec3(200, -500, 400));

        var check = RobotChecks.Run(prog, Lim(), GcodeFlavor.DuetRrf, arcSupport: true, elbow: 0);

        Assert.Equal(prog.Moves.Count, check.MoveIks.Count);
        // ...and the sampled sequence really is longer, i.e. the two are not the same
        // list by accident - that is the whole reason they had to be separated.
        Assert.True(check.Iks.Count > check.MoveIks.Count);
    }

    [Fact]
    public void MoveIks_OnePerInputPoint_ForRunPoints()
    {
        var pts = new List<Vec3>
        {
            new Vec3(400, -500, 10),
            new Vec3(450, -550, 10),
            new Vec3(500, -600, 10),
        };
        var check = RobotChecks.RunPoints(pts, Lim(), ArmConfiguration.Seed(0));
        Assert.Equal(pts.Count, check.MoveIks.Count);
    }
}

/// <summary>Cover for the session-level types Phase 3/4 introduced. Their consumers
/// (MachineSession, DuetJogComponent) live in the plugin project and are out of reach of
/// this suite, so the reachable units are pinned here.</summary>
public class ArmConfigurationAndHomeMacroTests
{
    [Fact]
    public void Unknown_IsNotKnown_AndSeedIs()
    {
        Assert.False(ArmConfiguration.Unknown.Known);
        var seeded = ArmConfiguration.Seed(1);
        Assert.True(seeded.Known);
        Assert.Equal(1, seeded.ArmMode);
        Assert.Equal(0, seeded.ThetaTurns);
    }

    [Fact]
    public void HomeMacro_ParksWithAbsoluteMove()
    {
        // The real homeall.g shape: home the arms, then park at an absolute pose.
        Assert.True(HomeMacroReader.TryParse(
            "G91\nG1 H1 X-360 Y-360 F1000\nG90\nG0 X200 Y-500 F4000\n",
            out var plan, out string error));
        Assert.Equal("", error);
        Assert.True(plan.HasTrigger);

        var points = plan.Replay(new Vec3(0, 0, 400));
        Assert.NotEmpty(points);
        var last = points[points.Count - 1];
        Assert.Equal(200, last.X, 3);
        Assert.Equal(-500, last.Y, 3);
    }

    [Fact]
    public void HomeMacro_RelativeParkIsAppliedToTheTriggerPose()
    {
        Assert.True(HomeMacroReader.TryParse(
            "G91\nG1 H1 X-360 Y-360 F1000\nG1 X10 Y-20 F4000\n",
            out var plan, out _));
        var points = plan.Replay(new Vec3(100, -200, 400));
        var last = points[points.Count - 1];
        Assert.Equal(110, last.X, 3);
        Assert.Equal(-220, last.Y, 3);
    }

    [Fact]
    public void HomeMacro_NullText_FailsClosed()
    {
        Assert.False(HomeMacroReader.TryParse(null!, out _, out string error));
        Assert.NotEqual("", error);
    }
}
