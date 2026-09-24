using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>
/// The Grasshopper preview and the emitted G-code must agree on height. Every emitter
/// writes <c>Z + ZOffset</c>, but the preview polylines used to be collected at the raw
/// sliced Z, so a non-zero offset made the canvas draw the toolpath at the layer height
/// while the printer ran above it.
/// </summary>
public class GcodeZOffsetPreviewTests
{
    const double LayerZ = 4.0;

    static List<List<Vec3>> Layer() => new()
    {
        new List<Vec3>
        {
            new(0, 0, LayerZ), new(10, 0, LayerZ), new(10, 10, LayerZ),
            new(0, 10, LayerZ), new(0, 0, LayerZ),
        }
    };

    static GcodeConfig Cfg(double zOffset) => new()
    {
        LayerHeight = LayerZ,
        Width = 2.0,
        RamDiameter = 10.0,
        PressureCoeff = 1.0,
        PrintSpeed = 20.0,
        ZOffset = zOffset,
        CoordinateFrame = GcodeCoordinateFrame.Identity,
    };

    static GcodeResult Build(double zOffset)
        => GcodeBuilder.Build(new[] { Layer() }, Cfg(zOffset));

    /// <summary>Every Z word on an extruding move in the emitted program.</summary>
    static List<double> PrintedZs(GcodeResult r)
    {
        var zs = new List<double>();
        foreach (var line in r.Lines)
        {
            if (!line.StartsWith("G1 ") || !line.Contains(" E")) continue;
            int i = line.IndexOf(" Z");
            if (i < 0) continue;
            string token = line.Substring(i + 2).Split(' ')[0];
            zs.Add(double.Parse(token, CultureInfo.InvariantCulture));
        }
        return zs;
    }

    static List<double> PreviewZs(GcodeResult r)
        => r.PrintPolys.SelectMany(p => p).Select(p => p.Z).Distinct().ToList();

    [Fact]
    public void WithNoOffsetPreviewAndOutputBothSitAtTheLayerHeight()
    {
        var r = Build(0.0);
        Assert.All(PreviewZs(r), z => Assert.Equal(LayerZ, z, 6));
        Assert.NotEmpty(PrintedZs(r));
        Assert.All(PrintedZs(r), z => Assert.Equal(LayerZ, z, 3));
    }

    /// <summary>The regression: preview showed 4 while the machine was told 8.</summary>
    [Fact]
    public void PreviewTracksTheOffsetInsteadOfDisagreeingWithTheOutput()
    {
        var r = Build(4.0);
        Assert.All(PreviewZs(r), z => Assert.Equal(LayerZ + 4.0, z, 6));
        Assert.All(PrintedZs(r), z => Assert.Equal(LayerZ + 4.0, z, 3));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(4.0)]
    [InlineData(-1.5)]
    public void PreviewAndOutputNeverDisagree(double zOffset)
    {
        var r = Build(zOffset);
        double previewZ = Assert.Single(PreviewZs(r));
        Assert.All(PrintedZs(r), z => Assert.Equal(previewZ, z, 3));
    }

    /// <summary>StartPoint has always been "as commanded, incl. ZOffset" - the preview
    /// now uses the same convention rather than a second, conflicting one.</summary>
    [Fact]
    public void PreviewAgreesWithStartPoint()
    {
        var r = Build(4.0);
        Assert.NotNull(r.StartPoint);
        Assert.Equal(r.StartPoint!.Value.Z, Assert.Single(PreviewZs(r)), 6);
    }

    [Fact]
    public void TravelPreviewIsRaisedToo()
    {
        var two = new List<List<Vec3>>
        {
            new List<Vec3> { new(0, 0, LayerZ), new(10, 0, LayerZ) },
            new List<Vec3> { new(50, 50, LayerZ), new(60, 50, LayerZ) },
        };
        var cfg = Cfg(4.0);
        var r = GcodeBuilder.Build(new[] { two }, cfg);

        Assert.NotEmpty(r.TravelPolys);
        // no travel point may still sit at the un-offset geometry height
        Assert.DoesNotContain(r.TravelPolys.SelectMany(p => p),
                              p => System.Math.Abs(p.Z - LayerZ) < 1e-9);
    }

    /// <summary>Zero offset must leave the polylines untouched, not merely unchanged in
    /// value - this is the path every existing definition takes.</summary>
    [Fact]
    public void ZeroOffsetIsAnExactNoOp()
    {
        var r = Build(0.0);
        Assert.All(r.PrintPolys.SelectMany(p => p), p => Assert.Equal(LayerZ, p.Z, 9));
    }
}
