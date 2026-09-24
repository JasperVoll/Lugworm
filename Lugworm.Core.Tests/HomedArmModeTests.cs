using System;
using System.Collections.Generic;
using System.IO;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The SCARA arm mode after a full G28 is fixed by the homing endstops, not by
/// the operator's elbow preference. Before 1.12.0 every consumer defaulted elbow to 0
/// (up) while the lab Potterbot rests in mode 1 (down), so the simulated arm was drawn
/// as the mirror-image branch and a verified home latched the wrong configuration.
///
/// The expected joint values here are MEASURED, not derived from this code: `M114` after
/// a full G28 on 2026-08-24 reported X:200 Y:-500 Z:400 with joints -18.489 / -116.054
/// (bring-up memo of 2026-08-25, Step 11, archived outside the repository). At that pose BOTH arm modes
/// satisfy the firmware's A-41.1:319.1 / B-151.2:151.2 limits, so InverseRrf never falls
/// back - whichever mode it is asked for is the one it returns. That is exactly why the
/// seed has to be right rather than merely legal.</summary>
public class HomedArmModeTests
{
    static readonly Vec3 HomePose = new Vec3(200, -500, 400);

    // Measured on the machine; see the class remark.
    const double HomeJ1Mode1 = -18.489;
    const double HomeJ3Mode1 = -116.054;
    // The mirror branch the old elbow-0 default produced.
    const double HomeJ1Mode0 = 242.092;
    const double HomeJ3Mode0 = 116.054;

    static RobotLimits Limits(int homedArmMode) => new RobotLimits
    {
        ReachMin = 235.0,
        HomedArmMode = homedArmMode,
    };

    // A home-anchored program: G28 before the first motion, so the first move STARTS at
    // the calibrated home pose. That start point is validated in the !havePoint branch
    // from the initial seed - the branch a fix confined to the mid-program G28 reseed
    // would never reach.
    static ParsedProgram HomeAnchored() => GcodeParser.Parse(
        new[] { "M83", "G28", "G90", "G1 X300 Y-500 Z400 F1200" }, HomePose);

    // ---- robot.xml armMode parsing -------------------------------------------

    static string XmlWithHomePose(string homePoseElement) => @"
<ScaraRobot name='t' units='mm' angleUnits='deg'>
  <Base x='0' y='0' z='0'/>
  <Links>
    <Link name='base' layer='Base'/>
    <Link name='link1' layer='UpperArm1' length='549.06'/>
    <Link name='link2' layer='LowerArm2' length='457.23'/>
  </Links>
  <Reach min='235' max='1006.29'/>
  " + homePoseElement + @"
  <Joints>
    <Joint name='J1' type='revolute' axis='0 0 1' parent='base' child='link1'
           pivot='0 0 0' home='0' min='-41.1' max='319.1'/>
  </Joints>
</ScaraRobot>";

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void RobotXml_ArmMode_Parses(string attribute, int expected)
    {
        var def = RobotDefinition.Parse(XmlWithHomePose(
            $"<HomePose calibrated='true' x='200' y='-500' z='400' armMode='{attribute}'/>"));
        Assert.Equal(expected, def.HomedArmMode);
    }

    [Fact]
    public void RobotXml_ArmModeAbsent_IsNullAndSilent()
    {
        // An older robot.xml must keep parsing without complaint; the consumer falls back
        // to the RobotLimits default rather than inventing a mode.
        var def = RobotDefinition.Parse(XmlWithHomePose(
            "<HomePose calibrated='true' x='200' y='-500' z='400'/>"));
        Assert.Null(def.HomedArmMode);
        Assert.DoesNotContain(def.ParseWarnings, w => w.Contains("armMode"));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("down")]
    [InlineData("")]
    public void RobotXml_ArmModeInvalid_IsRejectedWithAWarning(string attribute)
    {
        // Fail visible, not silent: an unusable value must not masquerade as mode 0.
        var def = RobotDefinition.Parse(XmlWithHomePose(
            $"<HomePose calibrated='true' x='200' y='-500' z='400' armMode='{attribute}'/>"));
        Assert.Null(def.HomedArmMode);
        Assert.Contains(def.ParseWarnings, w => w.Contains("armMode"));
    }

    [Fact]
    public void ShippedRobotXml_DeclaresArmModeOne()
    {
        // The linked resource, not a hand-copied fixture (ARCHI section 21), so this
        // cannot drift from what the plugin actually embeds.
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "robot.xml");
        var def = RobotDefinition.Parse(File.ReadAllText(path));
        Assert.Equal(1, def.HomedArmMode);
        Assert.True(def.CalibratedHomePose.HasValue);
        Assert.Equal(200, def.CalibratedHomePose!.Value.X, 3);
        Assert.Equal(-500, def.CalibratedHomePose.Value.Y, 3);
        Assert.Equal(400, def.CalibratedHomePose.Value.Z, 3);
    }

    // ---- the initial seed, which is the one an ordinary job hits ---------------

    [Fact]
    public void LeadingG28_ResolvesTheMeasuredMachineJoints()
    {
        var res = RobotChecks.Run(HomeAnchored(), Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 0);

        // Iks[0] is the start point: the post-home pose itself.
        Assert.NotEmpty(res.Iks);
        var home = res.Iks[0];
        Assert.True(home.Valid);
        Assert.Equal(1, home.ArmMode);
        Assert.Equal(HomeJ1Mode1, home.J1, 2);
        Assert.Equal(HomeJ3Mode1, home.J3, 2);
    }

    [Fact]
    public void LeadingG28_IgnoresTheElbowInput()
    {
        // elbow 0 and elbow 1 must agree, because neither of them decides a home-anchored
        // walk. Before 1.12.0 these produced mirror-image poses.
        var withUp = RobotChecks.Run(HomeAnchored(), Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 0);
        var withDown = RobotChecks.Run(HomeAnchored(), Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 1);

        Assert.Equal(withDown.Iks[0].ArmMode, withUp.Iks[0].ArmMode);
        Assert.Equal(withDown.Iks[0].J1, withUp.Iks[0].J1, 6);
        Assert.Equal(withDown.Iks[0].J3, withUp.Iks[0].J3, 6);
    }

    [Fact]
    public void LeadingG28_HomedArmModeIsWhatActuallyDrivesTheBranch()
    {
        // The counterpart: change the machine's homed mode and the resolved branch flips.
        // Without this, the test above would also pass if the seed were hard-coded to 1.
        var res = RobotChecks.Run(HomeAnchored(), Limits(0), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 1);

        Assert.Equal(0, res.Iks[0].ArmMode);
        Assert.Equal(HomeJ1Mode0, res.Iks[0].J1, 2);
        Assert.Equal(HomeJ3Mode0, res.Iks[0].J3, 2);
    }

    [Fact]
    public void TrackedSessionConfiguration_StillOutranksHomedArmMode()
    {
        // A live session's verified configuration is machine truth and must win over the
        // static robot.xml value.
        var res = RobotChecks.Run(HomeAnchored(), Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 1,
            initialConfiguration: ArmConfiguration.Seed(0));

        Assert.Equal(0, res.Iks[0].ArmMode);
    }

    // ---- the non-home-anchored path, where elbow still governs -----------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WithoutAHome_ElbowStillGoverns(int elbow)
    {
        // No G28, no calibrated pose: the walk has nothing to anchor to, so the operator
        // preference remains the only available seed. Narrowing that away would break
        // exploration mode.
        var prog = GcodeParser.Parse(new[] { "M83", "G90", "G1 X200 Y-500 Z400 F1200" });
        var res = RobotChecks.Run(prog, Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: elbow);

        Assert.NotEmpty(res.MoveIks);
        Assert.Equal(elbow, res.MoveIks[0].ArmMode);
    }

    // ---- the mid-program G28 teleport reseed -----------------------------------

    [Fact]
    public void MidProgramG28_ReseedsFromHomedArmMode()
    {
        var prog = GcodeParser.Parse(new[]
        {
            "M83", "G28", "G90",
            "G1 X300 Y-500 Z400 F1200",
            "G28",
            "G1 X400 Y-500 Z400 F1200",
        }, HomePose);
        var res = RobotChecks.Run(prog, Limits(1), GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 0);

        Assert.All(res.MoveIks, ik => Assert.Equal(1, ik.ArmMode));
    }

    // ---- cache identity ---------------------------------------------------------

    [Fact]
    public void LimitsKey_DistinguishesHomedArmMode()
    {
        // Without this the SolveCache would replay a result computed in the old branch.
        string up = ContentHash.LimitsKey(Limits(0), elbow: 1, firmware: 0, arcSupport: true);
        string down = ContentHash.LimitsKey(Limits(1), elbow: 1, firmware: 0, arcSupport: true);
        Assert.NotEqual(up, down);
    }

    // ---- the verified-home replay the Jog component performs --------------------

    [Fact]
    public void VerifiedHomeReplay_FromEndstopTriggerPose_ResolvesModeOne()
    {
        // What DuetJogComponent does after a verified full G28: build the trigger pose
        // from the endstop joint minima, replay homeall.g's park move from it, and walk
        // the result. The distal minimum is negative, so mode 1 is the only physically
        // correct answer - and the seed now comes from HomedArmMode, not the elbow input.
        var lim = Limits(1);
        var trigger = ScaraKinematics.Forward(lim.J1Min, lim.J3Min, 400.0 - lim.BaseH,
            lim.L1, lim.L2, lim.BaseH);
        var triggerPose = new Vec3(trigger.x, trigger.y, trigger.z);

        Assert.True(HomeMacroReader.TryParse(
            "G91\nG1 H1 X-360 Y-360 F1000\nG90\nG0 X200 Y-500 F4000\n",
            out var plan, out string error));
        Assert.Equal("", error);

        var points = plan.Replay(triggerPose);
        points.Insert(0, triggerPose);
        var res = RobotChecks.RunPoints(points, lim, ArmConfiguration.Seed(lim.HomedArmMode));

        Assert.True(res.ReachOk);
        Assert.True(res.FinalConfiguration.Known);
        Assert.Equal(1, res.FinalConfiguration.ArmMode);

        // The park endpoint is the measured home pose, in the measured branch.
        var parked = res.Iks[res.Iks.Count - 1];
        Assert.Equal(HomeJ1Mode1, parked.J1, 2);
        Assert.Equal(HomeJ3Mode1, parked.J3, 2);
    }

    [Fact]
    public void EndstopTriggerPose_IsInsideTheDeadZoneBound()
    {
        // The trigger pose must itself be reachable, or the replay that establishes the
        // configuration would fail closed and every gated action would stay refused.
        var lim = Limits(1);
        var t = ScaraKinematics.Forward(lim.J1Min, lim.J3Min, 400.0, lim.L1, lim.L2, lim.BaseH);
        double r = Math.Sqrt(t.x * t.x + t.y * t.y);
        Assert.InRange(r, lim.ReachMin, lim.L1 + lim.L2);
    }
}
