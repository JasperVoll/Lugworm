using System;
using System.Linq;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class RobotProfileTests
{
    [Fact]
    public void GcodeSimulationUsesProgramAwareScaraChecks()
    {
        var profile = RobotCatalog.Get("potterbot-scara-v4");
        var limits = profile.CreateScaraLimits();
        var program = GcodeParser.Parse(new[]
        {
            "G21", "G90", "M83", "G28",
            "G1 X500 Y-300 Z10 F1200",
            "G1 X525 Y-300 Z10 E1 F1200",
        }, limits.CalibratedHomePose);
        var points = new List<Vec3>
        {
            new Vec3(program.Moves[0].StartX, program.Moves[0].StartY, program.Moves[0].StartZ),
            new Vec3(program.Moves[0].X, program.Moves[0].Y, program.Moves[0].Z),
            new Vec3(program.Moves[1].X, program.Moves[1].Y, program.Moves[1].Z),
        };

        var expected = RobotChecks.Run(program, limits, GcodeFlavor.DuetRrf,
            arcSupport: true, elbow: 0);
        var actual = RobotSimulation.SolveProgram(profile, program, points,
            GcodeFlavor.DuetRrf, arcSupport: true,
            options: new RobotSimulationOptions { ScaraArmMode = 0 });

        Assert.Equal(expected.Iks.Count, actual.JointsDeg.Count);
        Assert.Equal(expected.Iks[^1].J1Resolved, actual.JointsDeg[^1][0], 9);
        Assert.Equal(expected.Iks[^1].J3Resolved, actual.JointsDeg[^1][1], 9);
    }

    [Theory]
    [InlineData("none", RobotMechanism.None, RobotProgramKind.RepRapGcode)]
    [InlineData("generic-cartesian", RobotMechanism.Cartesian, RobotProgramKind.RepRapGcode)]
    [InlineData("generic-duet-cartesian", RobotMechanism.Cartesian, RobotProgramKind.RepRapGcode)]
    [InlineData("generic-klipper", RobotMechanism.Cartesian, RobotProgramKind.RepRapGcode)]
    [InlineData("generic-marlin", RobotMechanism.Cartesian, RobotProgramKind.RepRapGcode)]
    [InlineData("potterbot-scara-v4", RobotMechanism.Scara, RobotProgramKind.RepRapGcode)]
    public void BuiltInProfiles_HaveExpectedMechanismAndProgram(
        string id, RobotMechanism mechanism, RobotProgramKind program)
    {
        var profile = RobotCatalog.Get(id);

        Assert.Equal(id, profile.Id);
        Assert.Equal(mechanism, profile.Mechanism);
        Assert.Equal(program, profile.ProgramKind);
    }

    [Theory]
    [InlineData("SCARA", "potterbot-scara-v4")]
    [InlineData("Potterbot Scara v4", "potterbot-scara-v4")]
    [InlineData("Cartesian", "generic-cartesian")]
    [InlineData("klipper", "generic-klipper")]
    [InlineData("marlin", "generic-marlin")]
    [InlineData("Duet Cartesian", "generic-duet-cartesian")]
    [InlineData("", "none")]
    public void Resolve_AcceptsHumanFriendlyAliases(string value, string expectedId)
    {
        Assert.Equal(expectedId, RobotCatalog.Resolve(value).Id);
    }

    [Fact]
    public void DefinitionText_RoundTripsStably()
    {
        var profile = RobotCatalog.Get("potterbot-scara-v4");

        string text = RobotDefinitionText.Serialize(profile);
        var parsed = RobotDefinitionText.Parse(text);

        Assert.Equal("iamlab-robot:v1:potterbot-scara-v4", text);
        Assert.Same(profile, parsed);
    }

    [Theory]
    [InlineData("iamlab-robot:v2:potterbot-scara-v4")]
    [InlineData("iamlab-robot:v1:not-a-robot")]
    [InlineData("wrong-prefix:v1:potterbot-scara-v4")]
    public void DefinitionText_InvalidDefinitionFailsClosed(string text)
    {
        var error = Assert.Throws<FormatException>(() => RobotDefinitionText.Parse(text));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Catalog_ContainsEverySelectableBuiltInOnce()
    {
        var all = RobotCatalog.All;

        Assert.Equal(6, all.Count);
        Assert.Equal(all.Count, all.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Transport and default dialect per profile.</summary>
    [Theory]
    [InlineData("none", RobotControlKind.None, GcodeFlavor.DuetRrf)]
    [InlineData("generic-cartesian", RobotControlKind.None, GcodeFlavor.DuetRrf)]
    [InlineData("generic-duet-cartesian", RobotControlKind.Duet, GcodeFlavor.DuetRrf)]
    [InlineData("potterbot-scara-v4", RobotControlKind.Duet, GcodeFlavor.DuetRrf)]
    [InlineData("generic-klipper", RobotControlKind.None, GcodeFlavor.Klipper)]
    [InlineData("generic-marlin", RobotControlKind.None, GcodeFlavor.Marlin)]
    public void Profiles_CarryTransportAndDefaultFlavor(string id, RobotControlKind control, GcodeFlavor flavor)
    {
        var p = RobotCatalog.Get(id);
        Assert.Equal(control, p.ControlKind);
        Assert.Equal(flavor, p.DefaultFlavor);
        Assert.Equal(GcodeCoordinateFrame.Identity == p.GcodeCoordinateFrame, id != "potterbot-scara-v4");
        // the wire format round-trips the new ids too
        Assert.Same(p, RobotDefinitionText.Parse(RobotDefinitionText.Serialize(p)));
    }

    [Fact]
    public void ScaraProfile_CreatesIndependentCalibratedLimits()
    {
        var profile = RobotCatalog.Get("potterbot-scara-v4");

        var a = profile.CreateScaraLimits();
        var b = profile.CreateScaraLimits();
        a.L1 = 123;

        Assert.Equal(549.06, b.L1, 6);
        Assert.Equal(457.23, b.L2, 6);
        Assert.Equal(235.0, b.ReachMin, 6);
        Assert.Equal(400.0, b.ZMax, 6);
        Assert.Equal(1, b.HomedArmMode);
        Assert.True(b.CalibratedHomePose.HasValue);
    }

    [Fact]
    public void WrongMechanismLimitFactoryFailsClearly()
    {
        var profile = RobotCatalog.Get("generic-cartesian");

        Assert.Throws<InvalidOperationException>(() => profile.CreateScaraLimits());
    }

    [Theory]
    [InlineData("none", GcodeCoordinateFrame.Identity)]
    [InlineData("generic-cartesian", GcodeCoordinateFrame.Identity)]
    [InlineData("generic-duet-cartesian", GcodeCoordinateFrame.Identity)]
    [InlineData("potterbot-scara-v4", GcodeCoordinateFrame.LegacyScara)]
    public void ProfilesDeclareTheirGcodeCoordinateFrame(string id, GcodeCoordinateFrame expected)
    {
        Assert.Equal(expected, RobotCatalog.Get(id).GcodeCoordinateFrame);
    }

    [Fact]
    public void GenericDuetProfile_DeclaresDuetControlWithoutRobotKinematics()
    {
        var profile = RobotCatalog.Get("generic-duet-cartesian");

        Assert.Equal(RobotControlKind.Duet, profile.ControlKind);
        Assert.False(profile.HasKinematics);
        Assert.Equal(GcodeCoordinateFrame.Identity, profile.GcodeCoordinateFrame);
    }

    [Fact]
    public void Simulation_NoneKeepsToolpathUsableWithoutKinematics()
    {
        var points = new List<Vec3> { new(0, 0, 0), new(10, 0, 0) };

        var result = RobotSimulation.Solve(RobotCatalog.Get("none"), points);

        Assert.True(result.ReachOk);
        Assert.False(result.KinematicsApplied);
        Assert.Empty(result.JointsDeg);
        Assert.Equal(points, result.Toolpath);
    }

    [Fact]
    public void Simulation_GenericCartesianDoesNotRequireRobotModel()
    {
        var result = RobotSimulation.Solve(
            RobotCatalog.Get("generic-cartesian"),
            new List<Vec3> { new(2500, -3000, 800) });

        Assert.True(result.ReachOk);
        Assert.False(result.KinematicsApplied);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Simulation_ScaraDispatchesExistingChecks()
    {
        var result = RobotSimulation.Solve(
            RobotCatalog.Get("potterbot-scara-v4"),
            new List<Vec3> { new(400, -100, 50), new(420, -100, 50) },
            options: new RobotSimulationOptions { ScaraArmMode = 1 });

        Assert.True(result.KinematicsApplied);
        Assert.Equal(2, result.JointsDeg.Count);
        Assert.All(result.JointsDeg, joints => Assert.Equal(3, joints.Length));
    }
}
