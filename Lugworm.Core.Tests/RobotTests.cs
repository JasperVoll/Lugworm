using System;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class RobotTests
{
    const double L1 = 608.466, L2 = 509.484, BASE = 0.0;

    [Fact]
    public void ForwardThenInverse_RoundTrips()
    {
        var (x, y, z) = ScaraKinematics.Forward(20.0, 50.0, 100.0, L1, L2, BASE);
        var ik = ScaraKinematics.Inverse(x, y, z, L1, L2, BASE, elbow: 0);
        Assert.True(ik.Valid);
        var (fx, fy, fz) = ScaraKinematics.Forward(ik.J1, ik.J3, ik.J2, L1, L2, BASE);
        Assert.Equal(x, fx, 6);
        Assert.Equal(y, fy, 6);
        Assert.Equal(z, fz, 6);
        Assert.Equal(100.0, ik.J2, 9);
    }

    [Fact]
    public void Inverse_Unreachable_Invalid()
    {
        var ik = ScaraKinematics.Inverse(2000, 0, 0, L1, L2, BASE);
        Assert.False(ik.Valid);
        Assert.Contains("unreachable", ik.Error);
    }

    [Fact]
    public void Inverse_TooClose_Invalid()
    {
        var ik = ScaraKinematics.Inverse(10, 0, 0, L1, L2, BASE);  // r=10 < |L1-L2|=98.98
        Assert.False(ik.Valid);
        Assert.Contains("too close", ik.Error);
    }

    [Fact]
    public void Parser_ReadsMovesModesAndArcs()
    {
        var lines = new[]
        {
            "; header", "M83", "G28", "G90",
            "G1 X100 Y0 Z1 E0.5 F1200",
            "G1 X150 Y20 Z1 E1.0 F1200",
            "G2 X160 Y30 I5 J5 E1.2 F1200",
        };
        var prog = GcodeParser.Parse(lines);
        Assert.Equal(3, prog.Moves.Count);
        Assert.True(prog.HasEMode);
        Assert.True(prog.RelativeE);
        Assert.True(prog.HasHome);
        Assert.True(prog.HasArcs);
        Assert.True(prog.Moves[0].Extrude);
        Assert.Equal(100.0, prog.Moves[0].X, 6);
        Assert.Equal(1200.0, prog.Moves[0].F, 6);
    }

    [Fact]
    public void Parse_G20InchUnits_Throws()
    {
        var ex = Assert.Throws<FormatException>(() =>
            GcodeParser.Parse(new[] { "G20", "G1 X10 Y0 Z1 F1200" }));
        Assert.Contains("G20", ex.Message);
    }

    [Fact]
    public void Parse_G21_IsAccepted()
    {
        var prog = GcodeParser.Parse(new[] { "G21", "M83", "G1 X300 Y0 Z1 F1200" });
        Assert.Single(prog.Moves);
    }

    [Fact]
    public void Parse_RFormatArc_Throws()
    {
        var ex = Assert.Throws<FormatException>(() =>
            GcodeParser.Parse(new[] { "G1 X300 Y0 Z1 F1200", "G2 X310 Y10 R5 F1200" }));
        Assert.Contains("R-format", ex.Message);
    }

    [Fact]
    public void Parse_ArcWithoutIJ_Throws()
    {
        var ex = Assert.Throws<FormatException>(() =>
            GcodeParser.Parse(new[] { "G1 X300 Y0 Z1 F1200", "G2 X310 Y10 F1200" }));
        Assert.Contains("arc center", ex.Message);
    }

    [Fact]
    public void JobGate_G20Job_Refused()
    {
        var res = Lugworm.Core.Duet.JobGate.Validate(
            new[] { "G20", "M83", "G1 X12 Y0 Z0.1 E1 F1200" },
            new RobotLimits { ReachMin = 235.0 });
        Assert.False(res.Allowed);
        Assert.Contains("parse failed", res.Summary);
    }

    [Fact]
    public void Checks_FlagsUnreachableMove()
    {
        var prog = GcodeParser.Parse(new[] { "M83", "G1 X2000 Y0 Z1 E1 F1200" });
        var res = RobotChecks.Run(prog, new RobotLimits(), GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.False(res.ReachOk);
        Assert.Contains(res.Problems, p => p.Sev == Severity.Error && p.Message.Contains("unreachable"));
    }

    [Fact]
    public void Checks_MarlinArcsWithoutSupport_Warns()
    {
        var prog = GcodeParser.Parse(new[] { "M83", "G1 X100 Y0 Z1 F1200", "G2 X110 Y10 I5 J5 F1200" });
        var res = RobotChecks.Run(prog, new RobotLimits(), GcodeFlavor.Marlin, arcSupport: false);
        Assert.Contains(res.Problems, p => p.Message.Contains("ARC_SUPPORT"));
    }

    [Fact]
    public void Checks_ReachableProgram_Ok()
    {
        var prog = GcodeParser.Parse(new[] { "M83", "G28", "G1 X300 Y0 Z1 E0.5 F1200", "G1 X300 Y-100 Z1 E1 F1200" });
        var res = RobotChecks.Run(prog, new RobotLimits(), GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.True(res.ReachOk);
    }

    [Fact]
    public void Checks_RunPoints_FlagsReachAndDeadZone()
    {
        var pts = new[]
        {
            new Vec3(400, 0, 5),    // reachable
            new Vec3(150, 0, 5),    // inside the R=235 dead-zone
            new Vec3(2000, 0, 5),   // unreachable
        };
        var res = RobotChecks.RunPoints(pts, new RobotLimits { ReachMin = 235 });
        Assert.Equal(3, res.Iks.Count);
        Assert.False(res.ReachOk);
        Assert.Contains(res.Problems, p => p.Message.Contains("dead-zone"));
        Assert.Contains(res.Problems, p => p.Message.Contains("unreachable"));
    }

    [Fact]
    public void Placement_LandsBottomCenterOnPrintCenter()
    {
        var bboxCenter = new Vec3(100, 50, 10);
        double minZ = 5;
        var printCenter = new Vec3(300, 200, 2);

        // printCenter alone: centroid->printCenter.XY, base->printCenter.Z
        var a = Placement.Offset(bboxCenter, minZ, printCenter, zOffset: 0, move: new Vec3(0, 0, 0));
        Assert.Equal(300 - 100, a.X, 6);
        Assert.Equal(200 - 50, a.Y, 6);
        Assert.Equal(2 - 5, a.Z, 6);

        // zOffset + move are optional nudges added on top
        var b = Placement.Offset(bboxCenter, minZ, printCenter, zOffset: 1, move: new Vec3(10, 20, 30));
        Assert.Equal(300 - 100 + 10, b.X, 6);
        Assert.Equal(200 - 50 + 20, b.Y, 6);
        Assert.Equal(2 - 5 + 1 + 30, b.Z, 6);
    }

    [Fact]
    public void Checks_InsideDeadZone_Flagged()
    {
        // r = 150 mm: reachable by the two links but inside the firmware R=235 dead-zone
        var prog = GcodeParser.Parse(new[] { "M83", "G1 X150 Y0 Z1 F600" });
        var res = RobotChecks.Run(prog, new RobotLimits { ReachMin = 235 }, GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.False(res.ReachOk);
        Assert.Contains(res.Problems, p => p.Sev == Severity.Error && p.Message.Contains("dead-zone"));
    }

    // ---- robot definition + posing (single-quoted attrs so it embeds cleanly) ----
    const double P = 549.39, Dd = 458.62;   // firmware link lengths
    const string RobotXml = @"
<ScaraRobot name='Potterbot-Scara-v4' units='mm' angleUnits='deg'>
  <Base x='0' y='0' z='0'/>
  <Links>
    <Link name='base'     layer='Base'/>
    <Link name='mast'     layer='LinearRailZ'/>
    <Link name='carriage'/>
    <Link name='link1'    layer='UpperArm1' length='549.39' axialScale='1.0245'/>
    <Link name='link2'    layer='LowerArm2' length='458.62' axialScale='1.0096'/>
    <Link name='tool'     layer='Nozzle'/>
  </Links>
  <Reach min='235' max='1008.01'/>
  <Joints>
    <Joint name='J1'  type='revolute'  axis='0 0 1' parent='base'     child='mast'
           pivot='-22.739203 123.034922 519.886900'       home='136.8776'   min='-311.1' max='49.1'/>
    <Joint name='J2'  type='prismatic' axis='0 0 1' parent='mast'     child='carriage'
           pivot='0 99.980388 49.9'                       home='239.937263' min='0'    max='1030'/>
    <Joint name='Jshoulder' type='fixed'            parent='carriage' child='link1'/>
    <Joint name='J3'  type='revolute'  axis='0 0 1' parent='link1'    child='link2'
           pivot='-414.149550 489.596919 542.386953'      home='126.7827'   min='-151.93' max='151.93'/>
    <Joint name='Jtip' type='fixed'                 parent='link2'    child='tool'
           pivot='-464.308585 38.125453 239.937263'/>
  </Joints>
</ScaraRobot>";

    [Fact]
    public void RobotDef_Parses()
    {
        var def = RobotDefinition.Parse(RobotXml);
        Assert.Equal(6, def.Links.Count);
        Assert.Equal(5, def.Joints.Count);
        Assert.Equal(235.0, def.ReachMin, 6);
        Assert.Equal(1008.01, def.ReachMax, 6);
        Assert.Equal(JointKind.Prismatic, def.Joint("J2")!.Kind);
        Assert.Equal(JointKind.Revolute, def.Joint("J1")!.Kind);
        Assert.Equal("LinearRailZ", def.Link("mast")!.Layer);
        Assert.True(string.IsNullOrEmpty(def.Link("carriage")!.Layer));   // virtual link
        Assert.Equal(P, def.Link("link1")!.Length, 6);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, -40, 250)]
    [InlineData(-60, 90, 700)]
    [InlineData(136.8776, 126.7827, 239.937263)]  // home pose
    public void Posing_NozzleTracks_ForwardKinematics(double j1, double j3, double j2)
    {
        var def = RobotDefinition.Parse(RobotXml);
        var tip = RobotPosing.ToolTipWorld(def, j1, j3, j2);
        var (fx, fy, fz) = ScaraKinematics.Forward(j1, j3, j2, P, Dd, 0.0);
        Assert.Equal(fx, tip.X, 6);
        Assert.Equal(fy, tip.Y, 6);
        Assert.Equal(fz, tip.Z, 6);   // Z calibration: nozzle Z == J2
    }

    [Fact]
    public void Posing_HomePose_NoArticulation()
    {
        var def = RobotDefinition.Parse(RobotXml);
        // at the home joint values the column does not lift and the arms do not rotate:
        // every link's world transform equals the base registration (pure translation).
        var t = RobotPosing.LinkWorldTransforms(def, 136.8776, 126.7827, 239.937263);
        var link1 = t["link1"];
        // rotation block ~ identity (no yaw): m00==m11==1, off-diagonals ~0.
        // tolerance 1e-3 absorbs the rounding in the stored home angle.
        Assert.True(Math.Abs(link1[0] - 1.0) < 1e-3);
        Assert.True(Math.Abs(link1[5] - 1.0) < 1e-3);
        Assert.True(Math.Abs(link1[1]) < 1e-3);
        Assert.True(Math.Abs(link1[4]) < 1e-3);
    }
}
