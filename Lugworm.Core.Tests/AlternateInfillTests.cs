using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class AlternateInfillTests
{
    static GcodeConfig Config() => new()
    {
        CoordinateFrame = GcodeCoordinateFrame.Identity,
        AlternateInfill = true,
        LayerHeight = 1,
        Width = 1,
        RamDiameter = 10,
        PressureCoeff = 1,
        Retraction = RetractionMode.LightRetraction,
        FirstLayerSpeedFactor = 1
    };

    static IReadOnlyList<IReadOnlyList<Vec3>> Layer(double z) => new IReadOnlyList<Vec3>[]
    {
        new Vec3[] { new(0, 0, z), new(10, 0, z), new(10, 10, z), new(0, 10, z), new(0, 0, z) },
        new Vec3[] { new(2, 2, z), new(5, 2, z), new(8, 2, z) }
    };

    static string[] LayerLines(GcodeResult result, int layer, int total)
        => result.Lines.SkipWhile(l => l != $"; layer {layer}/{total}").Skip(1)
            .TakeWhile(l => !l.StartsWith("; layer ")).ToArray();

    [Fact]
    public void NextLayerRisesAtInfillEndThenPrintsBackToWall()
    {
        var result = GcodeBuilder.Build(new[] { Layer(1), Layer(2) }, Config());
        var lines = LayerLines(result, 2, 2);
        var movement = lines.Where(l => l.StartsWith("G0 X") || l.StartsWith("G1 X")).ToArray();
        int marker = result.Lines.IndexOf("; layer 2/2");
        Assert.StartsWith("G1 X8.000 Y2.000 Z2.000", result.Lines[marker - 1]);
        Assert.StartsWith("G1 X5.000 Y2.000 Z2.000", movement[0]);
        Assert.StartsWith("G1 X2.000 Y2.000 Z2.000", movement[1]);
    }

    [Fact]
    public void ObjectParityRestartsAndEmptyBranchesDoNotConsumeLayer()
    {
        var layers = new[] { Layer(1), Array.Empty<IReadOnlyList<Vec3>>(), Layer(1), Layer(2) };
        var result = GcodeBuilder.Build(layers, Config(), objectIds: new[] { 4, 9, 9, 9 });
        var second = LayerLines(result, 2, 3);
        int wall = Array.FindIndex(second, l => l.StartsWith("G1 X10.000 Y0.000"));
        int infill = Array.FindIndex(second, l => l.StartsWith("G1 X5.000 Y2.000"));
        Assert.True(wall >= 0 && infill > wall);
        int marker = result.Lines.IndexOf("; layer 3/3");
        Assert.StartsWith("G1 X8.000 Y2.000 Z2.000", result.Lines[marker - 1]);
    }

    [Fact]
    public void ReversalKeepsFlowAndHeightSamplesOnTheirOriginalSegments()
    {
        var samples = new IReadOnlyList<IReadOnlyList<double>>[]
        {
            new double[][] { new[] { 1.0, 1, 1, 1, 1 }, new[] { 1.0, 2, 6 } },
            new double[][] { new[] { 1.0, 1, 1, 1, 1 }, new[] { 1.0, 2, 6 } }
        };
        var heights = new IReadOnlyList<IReadOnlyList<double>>[]
        {
            new double[][] { new[] { 1.0, 1, 1, 1, 1 }, new[] { 0.5, 1.0, 2.0 } },
            new double[][] { new[] { 1.0, 1, 1, 1, 1 }, new[] { 0.5, 1.0, 2.0 } }
        };
        var result = GcodeBuilder.Build(new[] { Layer(1), Layer(2) }, Config(), samples, heights);
        var lines = LayerLines(result, 2, 2);
        var back = lines.Single(l => l.StartsWith("G1 X5.000 Y2.000 Z2.000"));
        double e = double.Parse(back.Split(' ').Single(t => t.StartsWith("E")).Substring(1), CultureInfo.InvariantCulture);
        double expected = 3 * RamExtrusion.RamTravelPerPathMm(1, 1.5, 10, 1) * 4;
        Assert.Equal(expected, e, 5);
    }

    [Fact]
    public void DisabledOptionPreservesWallFirstOrder()
    {
        var config = Config(); config.AlternateInfill = false;
        var lines = LayerLines(GcodeBuilder.Build(new[] { Layer(1), Layer(2) }, config), 2, 2);
        Assert.True(Array.FindIndex(lines, l => l.StartsWith("G1 X10.000 Y0.000")) <
                    Array.FindIndex(lines, l => l.StartsWith("G1 X5.000 Y2.000")));
    }
}
