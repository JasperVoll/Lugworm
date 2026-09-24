using System.Collections.Generic;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class GcodeBuilderTests
{
    [Fact]
    public void RamOutput_MatchesV4Baseline()
    {
        var square0 = new List<Vec3>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0), new(0, 0, 0),
        };
        var config = new GcodeConfig
        {
            LayerHeight = 1.0,
            Width = 2.0,
            RamDiameter = 10.0,
            PressureCoeff = 1.0,
            PrintSpeed = 20.0,
            CoordinateFrame = GcodeCoordinateFrame.Identity,
            Flavor = GcodeFlavor.DuetRrf,
        };

        var actual = string.Join("\n", GcodeBuilder.Build(
            new[] { new List<List<Vec3>> { square0 } }, config).Lines);

        const string v4Baseline =
            "G0 X0.000 Y0.000 Z1.000 F3600\n" +
            "G0 X0.000 Y0.000 Z0.000 F3600\n" +
            "; layer 1/1\n" +
            "G1 X10.000 Y0.000 Z0.000 F600 E0.25465\n" +
            "G1 X10.000 Y10.000 Z0.000 F600 E0.25465\n" +
            "G1 X0.000 Y10.000 Z0.000 F600 E0.25465\n" +
            "G1 X0.000 Y0.000 Z0.000 F600 E0.25465";

        Assert.Equal(v4Baseline, actual);

        config.Flavor = GcodeFlavor.Marlin;
        Assert.Equal(new[]
        {
            "; --- BEGIN HEADER ---", "; PRINTSPEED=20", "; TRAVELSPEED=60", "; LAYERHEIGHT=1", "; LINEWIDTH=2",
            "; RAMDIAMETER=10", "; PRESSURECOEFF=1", "; FLAVOR=Marlin", "G21 ; mm", "G90 ; absolute XYZ",
            "G28 ; home all axes", "M83 ; relative E", "G92 E0", "; --- END HEADER ---", "",
        }, Header.Make(config).Split('\n'));
        Assert.Equal(new[]
        {
            "; --- BEGIN FOOTER ---", "G92 E0", "G91 ; relative", "G0 Z20 F3600 ; lift clear of the print before homing",
            "G90 ; absolute", "G28 ; home all axes", "M84 ; steppers off", "; --- END FOOTER ---", "",
        }, Header.Footer(config).Split('\n'));

        config.Flavor = GcodeFlavor.DuetRrf;
        Assert.DoesNotContain(Header.Footer(config).Split('\n'), line => line.StartsWith("M84"));
        Assert.DoesNotContain(GcodeBuilder.Build(
            new[] { new List<List<Vec3>> { square0 } }, config).Lines,
            line => line.StartsWith("M106") || line.StartsWith("M104") || line.StartsWith("SET_"));
    }

    [Fact]
    public void AlternateInfill_ReversesOpenPathAndKeepsLayerOrderAligned()
    {
        var c = new GcodeConfig
        {
            LayerHeight = 1,
            Width = 1,
            RamDiameter = 10,
            PressureCoeff = 1,
            PrintSpeed = 20,
            TravelSpeed = 60,
            CoordinateFrame = GcodeCoordinateFrame.Identity,
            AlternateInfill = true
        };
        var outer0 = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(0, 0, 0) };
        var infill0 = new List<Vec3> { new(2, 2, 0), new(8, 2, 0) };
        var outer1 = new List<Vec3> { new(0, 0, 1), new(10, 0, 1), new(0, 0, 1) };
        var infill1 = new List<Vec3> { new(2, 2, 1), new(8, 2, 1) };
        var flow = new[] {
            new[] { new[] { 1.0, 1.0, 1.0 }, new[] { 2.0, 3.0 } },
            new[] { new[] { 4.0, 4.0, 4.0 }, new[] { 5.0, 6.0 } } };
        var result = GcodeBuilder.Build(new[] { new[] { outer0, infill0 }, new[] { outer1, infill1 } }, c, flow);
        int layer = result.Lines.IndexOf("; layer 2/2");
        Assert.True(layer >= 0);
        Assert.Contains(result.Lines.GetRange(layer + 1, result.Lines.Count - layer - 1),
            line => line.StartsWith("G1 X2.000 Y2.000 Z1.000"));
    }
}
