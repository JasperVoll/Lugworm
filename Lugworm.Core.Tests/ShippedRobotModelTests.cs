using System;
using System.IO;
using System.Linq;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The verified robot model, pinned value by value.
///
/// `Lugworm/Resources/robot.xml` is the whole model - kinematics, mesh grouping,
/// joint chain, the calibrated home pose and the base placement - and it has been
/// corrected against the physical machine four times now, twice in the same week and
/// twice in opposite directions. Every one of those regressions was invisible to the
/// other tests: the FK self-check cannot see which link a joint drives, because a
/// vertical lift commutes with a vertical-axis rotation and the nozzle lands in the
/// same place either way.
///
/// So this file is a deliberate change-detector, not a behaviour test. It fails on ANY
/// edit to the shipped definition. If you meant the edit, the machine has been measured
/// and the drawn robot has been looked at next to it - update the numbers here and say
/// in the commit what was measured. If you did not mean it, that is the point.
///
/// State verified on the machine, 2026-08-26:
///   - jogging and the live view track correctly under the Rhino/machine frame map;
///   - J1 swings the whole Z-rail about the pivot with the proximal arm, and only the
///     Fundament stays put;
///   - the base sits right at rotDeg -180, offset (3, 82).
/// Amended 2026-09-04: offsetY 82 -> 44. With the J1 pivot on the Z-rail axis the column
/// turns about the right point, but the Fundament was observed 38 mm too far in +Y and
/// was moved 38 mm in -Y. Nothing else about the pose changed.</summary>
public class ShippedRobotModelTests
{
    static RobotDefinition Shipped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "robot.xml");
        return RobotDefinition.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void ParsesWithoutComplaint()
    {
        var def = Shipped();
        Assert.Empty(def.ParseWarnings);
        Assert.Equal("Potterbot-Scara-v4", def.Name);
    }

    // ---- the joint chain: WHICH link each joint drives ----------------------

    /// <summary>The regression that keeps coming back. `mast` is the Z-rail; it hangs
    /// off J1, so the base rotation swings it. `carriage` is virtual and rides J2 up
    /// the rail. `link1` is rigidly attached to the carriage, so it inherits both.</summary>
    [Theory]
    [InlineData("J1", JointKind.Revolute, "base", "mast")]
    [InlineData("J2", JointKind.Prismatic, "mast", "carriage")]
    [InlineData("Jshoulder", JointKind.Fixed, "carriage", "link1")]
    [InlineData("J3", JointKind.Revolute, "link1", "link2")]
    [InlineData("Jtip", JointKind.Fixed, "link2", "tool")]
    public void JointChainIsUnchanged(string name, JointKind kind, string parent, string child)
    {
        var joint = Shipped().Joint(name);
        Assert.NotNull(joint);
        Assert.Equal(kind, joint!.Kind);
        Assert.Equal(parent, joint.Parent);
        Assert.Equal(child, joint.Child);
    }

    [Fact]
    public void ChainHasNoOtherJoints()
    {
        var names = Shipped().Joints.Select(j => j.Name).ToArray();
        Assert.Equal(new[] { "J1", "J2", "Jshoulder", "J3", "Jtip" }, names);
    }

    /// <summary>Parents must be resolvable before their children, because the poser
    /// walks the joint list in document order.</summary>
    [Fact]
    public void ChainIsTopologicallyOrdered()
    {
        var def = Shipped();
        var known = new System.Collections.Generic.HashSet<string> { "base" };
        foreach (var joint in def.Joints)
        {
            Assert.True(known.Contains(joint.Parent),
                $"{joint.Name} is declared before its parent '{joint.Parent}'");
            known.Add(joint.Child);
        }
    }

    // ---- measured geometry: pivots, homes, firmware ranges ------------------

    [Theory]
    [InlineData("J1", 0, 99.980388, 519.886900, 136.7483, -41.1, 319.1)]   // re-centred on the rail axis 2026-09-02
    [InlineData("J2", 0, 99.980388, 49.9, 239.937263, 0, 400)]
    [InlineData("J3", -414.149550, 489.596919, 542.386953, 126.9121, -151.2, 151.2)]
    public void DrivenJointsAreUnchanged(string name, double px, double py, double pz,
                                         double home, double min, double max)
    {
        var joint = Shipped().Joint(name)!;
        Assert.Equal(px, joint.Pivot.X, 6);
        Assert.Equal(py, joint.Pivot.Y, 6);
        Assert.Equal(pz, joint.Pivot.Z, 6);
        Assert.Equal(home, joint.Home, 6);
        Assert.Equal(min, joint.Min, 6);   // M669 A/B, M208 Z
        Assert.Equal(max, joint.Max, 6);
    }

    [Fact]
    public void NozzlePivotIsUnchanged()
    {
        // J2.home must equal this Z or the "nozzle Z == J2" calibration breaks
        var jtip = Shipped().Joint("Jtip")!;
        Assert.Equal(-464.308585, jtip.Pivot.X, 6);
        Assert.Equal(38.125453, jtip.Pivot.Y, 6);
        Assert.Equal(239.937263, jtip.Pivot.Z, 6);
        Assert.Equal(Shipped().Joint("J2")!.Home, jtip.Pivot.Z, 6);
    }

    // ---- links: which .3dm layer each is, and the firmware lengths ----------

    [Theory]
    [InlineData("base", "Base", 0.0)]
    [InlineData("mast", "LinearRailZ", 0.0)]
    [InlineData("carriage", "", 0.0)]            // virtual - no geometry
    [InlineData("link1", "UpperArm1", 549.06)]   // M669 P
    [InlineData("link2", "LowerArm2", 457.23)]   // M669 D
    [InlineData("tool", "Nozzle", 0.0)]
    public void LinksAreUnchanged(string name, string layer, double length)
    {
        var link = Shipped().Link(name);
        Assert.NotNull(link);
        Assert.Equal(layer, link!.Layer);
        Assert.Equal(length, link.Length, 6);
        Assert.Null(link.Object);   // no object-name filter since the 2026-07-17 re-export
    }

    [Fact]
    public void LinkListIsUnchanged()
    {
        var names = Shipped().Links.Select(l => l.Name).ToArray();
        Assert.Equal(new[] { "base", "mast", "carriage", "link1", "link2", "tool" }, names);
    }

    // ---- registration, reach, home pose, base placement ---------------------

    [Fact]
    public void RegistrationIsAPureTranslation()
    {
        // BasePlacement's rotation is documented as readable straight off the Rhino
        // viewport, which holds only while the registration adds no yaw of its own.
        var def = Shipped();
        Assert.Equal(0.0, def.BaseYaw, 9);
        Assert.Equal(0.0, def.Base.X, 9);
        Assert.Equal(0.0, def.Base.Y, 9);
        Assert.Equal(0.0, def.Base.Z, 9);
    }

    [Fact]
    public void ReachAnnulusIsUnchanged()
    {
        var def = Shipped();
        Assert.Equal(235.0, def.ReachMin, 6);      // M669 R
        Assert.Equal(1006.29, def.ReachMax, 6);    // P + D
        Assert.Equal(def.Link("link1")!.Length + def.Link("link2")!.Length, def.ReachMax, 6);
    }

    [Fact]
    public void CalibratedHomePoseIsUnchanged()
    {
        // MACHINE coordinates, as the firmware reports them - Rhino X500 Y-200 Z400
        var def = Shipped();
        Assert.True(def.HomePosePresent);
        Assert.True(def.HomePoseCalibrated);
        Assert.Equal(200.0, def.CalibratedHomePose!.Value.X, 6);
        Assert.Equal(-500.0, def.CalibratedHomePose!.Value.Y, 6);
        Assert.Equal(400.0, def.CalibratedHomePose!.Value.Z, 6);
        Assert.Equal(1, def.HomedArmMode);   // measured -18.489 / -116.054
    }

    [Fact]
    public void BasePlacementIsUnchanged()
    {
        // The Fundament is the one link no joint drives, so this is its whole pose.
        // Confirmed against the machine 2026-08-26; offsetY corrected on it 2026-09-04.
        var def = Shipped();
        Assert.NotNull(def.BasePlacement);
        var (rot, offX, offY) = def.BasePlacement!.Value;
        Assert.Equal(-180.0, rot, 6);
        Assert.Equal(3.0, offX, 6);
        Assert.Equal(44.0, offY, 6);
    }

    // ---- the properties the numbers above exist to produce ------------------

    [Fact]
    public void PosedNozzleStillEqualsAnalyticForwardKinematics()
    {
        Assert.Empty(RobotSelfCheck.Run(Shipped(), tol: 1e-6));
    }

    [Fact]
    public void HomePoseIsReachableInTheMachinesOwnArmMode()
    {
        var def = Shipped();
        var lim = new RobotLimits
        {
            L1 = def.Link("link1")!.Length,
            L2 = def.Link("link2")!.Length,
            ReachMin = def.ReachMin,
            ReachMax = def.ReachMax,
            J1Min = def.Joint("J1")!.Min,
            J1Max = def.Joint("J1")!.Max,
            J3Min = def.Joint("J3")!.Min,
            J3Max = def.Joint("J3")!.Max,
            HomedArmMode = def.HomedArmMode ?? 1,
        };
        var home = def.CalibratedHomePose!.Value;
        var ik = ScaraKinematics.InverseRrf(home.X, home.Y, home.Z, lim, lim.HomedArmMode);

        Assert.True(ik.Valid, ik.Error);
        Assert.Equal(lim.HomedArmMode, ik.ArmMode);
        // the joints M114 reported after a full G28 on 2026-08-24
        Assert.Equal(-18.489, ik.J1, 2);
        Assert.Equal(-116.054, ik.J3, 2);
    }
}
