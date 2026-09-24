using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Calibration;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class TestSpecimenTests
{
    // ── the nominal dimensions, pinned against the standards ──
    // These numbers are the whole point of the feature: a specimen that is not at the
    // standard's dimensions is not that standard's specimen. They must not drift.

    [Theory]
    [InlineData(StandardSpecimen.En196Prism, "DIN EN 196-1", 160.0, 40.0, 40.0, 100.0)]
    [InlineData(StandardSpecimen.Iso178Bar, "DIN EN ISO 178", 80.0, 10.0, 4.0, 64.0)]
    [InlineData(StandardSpecimen.AstmD790Bar, "ASTM D790", 127.0, 12.7, 3.2, 51.2)]
    public void BendingSpecimens_MatchTheirStandard(StandardSpecimen which, string standard,
                                                    double x, double y, double z, double span)
    {
        var s = TestSpecimens.Nominal(which);
        Assert.Equal(standard, s.Standard);
        Assert.Equal("3-point bending", s.Test);
        Assert.Equal(x, s.X, 6);
        Assert.Equal(y, s.Y, 6);
        Assert.Equal(z, s.Z, 6);
        Assert.Equal(span, s.Span, 6);
        Assert.False(s.Round);
    }

    [Theory]
    [InlineData(StandardSpecimen.En12390Cube, 150.0, 150.0, 150.0, false)]
    [InlineData(StandardSpecimen.En12390Cylinder, 150.0, 0.0, 300.0, true)]
    [InlineData(StandardSpecimen.AstmD695Prism, 12.7, 12.7, 25.4, false)]
    public void CompressionSpecimens_MatchTheirStandard(StandardSpecimen which,
                                                        double x, double y, double z, bool round)
    {
        var s = TestSpecimens.Nominal(which);
        Assert.Equal("compression", s.Test);
        Assert.Equal(x, s.X, 6);
        Assert.Equal(y, s.Y, 6);
        Assert.Equal(z, s.Z, 6);
        Assert.Equal(round, s.Round);
        Assert.Equal(0.0, s.Span);      // a compression body has no bending span
    }

    /// <summary>Both plastics bending bars are specified at a 16:1 span-to-depth ratio,
    /// so the span must follow the thickness rather than be an independent number.</summary>
    [Theory]
    [InlineData(StandardSpecimen.Iso178Bar)]
    [InlineData(StandardSpecimen.AstmD790Bar)]
    public void PlasticsBendingBars_KeepTheSixteenToOneSpanRatio(StandardSpecimen which)
    {
        var s = TestSpecimens.Nominal(which);
        Assert.Equal(16.0, s.Span / s.Z, 6);
    }

    /// <summary>EN 196-1 breaks the prism in bending and then crushes the halves, so the
    /// span has to sit inside the prism with room for the supports.</summary>
    [Fact]
    public void En196Span_FitsInsideThePrism()
    {
        var s = TestSpecimens.Nominal(StandardSpecimen.En196Prism);
        Assert.True(s.Span < s.X, "the support span cannot exceed the prism length");
        Assert.Equal(40.0, s.Y, 6);   // the halves are crushed between 40 x 40 mm platens
        Assert.Equal(40.0, s.Z, 6);
    }

    [Fact]
    public void EveryDefinedSpecimenHasNominalDimensions()
    {
        foreach (StandardSpecimen which in Enum.GetValues(typeof(StandardSpecimen)))
        {
            var s = TestSpecimens.Nominal(which);
            Assert.False(string.IsNullOrWhiteSpace(s.Standard));
            Assert.False(string.IsNullOrWhiteSpace(s.Test));
            Assert.True(s.X > 0 && s.Z > 0, $"{which} has a zero dimension");
            Assert.True(s.Round || s.Y > 0, $"{which} is a prism with no width");
        }
    }

    [Fact]
    public void UnknownSpecimen_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => TestSpecimens.Nominal((StandardSpecimen)99));

    // ── scaling ──

    [Fact]
    public void Scaling_ScalesTheSpanToo_SoTheRigStillMatchesTheBody()
    {
        var s = TestSpecimens.Nominal(StandardSpecimen.En196Prism).Scaled(2.5);
        Assert.Equal(400.0, s.X, 6);
        Assert.Equal(100.0, s.Y, 6);
        Assert.Equal(100.0, s.Z, 6);
        Assert.Equal(250.0, s.Span, 6);
        Assert.Equal("DIN EN 196-1", s.Standard);
    }

    [Fact]
    public void ScalingByOne_ChangesNothing()
    {
        var a = TestSpecimens.Nominal(StandardSpecimen.AstmD695Prism);
        var b = a.Scaled(1.0);
        Assert.Equal(a.X, b.X, 12);
        Assert.Equal(a.Y, b.Y, 12);
        Assert.Equal(a.Z, b.Z, 12);
        Assert.Equal(a.Span, b.Span, 12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonPositiveScale_Throws(double scale)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => TestSpecimens.Nominal(StandardSpecimen.En12390Cube).Scaled(scale));

    [Fact]
    public void Thinnest_IsTheDimensionThatDecidesPrintability()
    {
        Assert.Equal(3.2, TestSpecimens.Nominal(StandardSpecimen.AstmD790Bar).Thinnest, 6);
        Assert.Equal(150.0, TestSpecimens.Nominal(StandardSpecimen.En12390Cylinder).Thinnest, 6);
        // the two plastics bars are under one LDM layer at nominal size - the reason
        // specimenScale exists at all
        Assert.True(TestSpecimens.Nominal(StandardSpecimen.Iso178Bar).Thinnest < TestSpecimens.ThinWarningMm);
        Assert.True(TestSpecimens.Nominal(StandardSpecimen.AstmD790Bar).Thinnest < TestSpecimens.ThinWarningMm);
        Assert.False(TestSpecimens.Nominal(StandardSpecimen.En196Prism).Thinnest < TestSpecimens.ThinWarningMm);
    }

    [Fact]
    public void Describe_NamesTheStandardAndTheSpan()
    {
        string bending = TestSpecimens.Nominal(StandardSpecimen.En196Prism).Describe();
        Assert.Contains("DIN EN 196-1", bending);
        Assert.Contains("span=100", bending);

        string compression = TestSpecimens.Nominal(StandardSpecimen.En12390Cube).Describe();
        Assert.DoesNotContain("span", compression);   // no bending span on a cube

        Assert.Contains("Ø150", TestSpecimens.Nominal(StandardSpecimen.En12390Cylinder).Describe());
    }
}

public class ZigzagPathTests
{
    static ZigzagPathOptions Opts(int passes = 4, int layers = 1, bool arcs = false)
        => new ZigzagPathOptions
        {
            PassLength = 100,
            Pitch = 10,
            Passes = passes,
            Layers = layers,
            LayerHeight = 3,
            ArcTurns = arcs,
        };

    [Fact]
    public void OneLayerPerRequestedLayer()
    {
        Assert.Single(ZigzagPath.Build(Opts(layers: 1)));
        Assert.Equal(5, ZigzagPath.Build(Opts(layers: 5)).Count);
    }

    [Fact]
    public void SharpTurns_GiveTwoPointsPerPass()
    {
        var layer = ZigzagPath.Build(Opts(passes: 6))[0];
        Assert.Equal(12, layer.Count);
    }

    [Fact]
    public void PassesAlternateDirection()
    {
        var layer = ZigzagPath.Build(Opts(passes: 4))[0];
        for (int i = 0; i < 4; i++)
        {
            double x0 = layer[2 * i].X, x1 = layer[2 * i + 1].X;
            bool rightward = x1 > x0;
            Assert.Equal((i & 1) == 0, rightward);   // even passes run +X, odd passes -X
        }
    }

    [Fact]
    public void PassesSitOnThePitch_AndAreCentredOnTheOrigin()
    {
        var layer = ZigzagPath.Build(Opts(passes: 5))[0];
        var ys = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(layer[2 * i].Y, layer[2 * i + 1].Y, 9);   // a pass is straight
            ys.Add(layer[2 * i].Y);
        }
        for (int i = 1; i < ys.Count; i++) Assert.Equal(10.0, ys[i] - ys[i - 1], 9);
        Assert.Equal(0.0, ys.Sum() / ys.Count, 9);
        Assert.Equal(0.0, layer.Sum(p => p.X) / layer.Count, 9);
    }

    [Fact]
    public void LayersStackOnTheLayerHeight_AndAlternateTheirStartSide()
    {
        var layers = ZigzagPath.Build(Opts(layers: 3));
        for (int i = 0; i < 3; i++)
            Assert.All(layers[i], p => Assert.Equal(i * 3.0, p.Z, 9));
        // layer 1 starts at the opposite end from layer 0, so the seam moves
        Assert.Equal(-layers[0][0].X, layers[1][0].X, 9);
        Assert.Equal(layers[0][0].X, layers[2][0].X, 9);
    }

    [Fact]
    public void ArcTurns_AddPointsAndKeepThePathContinuous()
    {
        var sharp = ZigzagPath.Build(Opts(passes: 4, arcs: false))[0];
        var round = ZigzagPath.Build(Opts(passes: 4, arcs: true))[0];
        Assert.True(round.Count > sharp.Count);

        // no jumps: with a 10 mm pitch and 8 chords per half circle, every step is short
        for (int i = 1; i < round.Count; i++)
            Assert.True(PolylineOps.Dist(round[i - 1], round[i]) <= 100.0 + 1e-9);

        // the turn bulges past the end of the pass rather than cutting inside it
        double maxX = round.Max(p => p.X);
        Assert.True(maxX > 50.0, $"a rounded turn should reach past x=50, got {maxX}");
        Assert.Equal(55.0, maxX, 6);      // 50 + pitch/2
    }

    [Fact]
    public void ArcTurns_AreLongerThanSharpOnes()
    {
        double sharp = ZigzagPath.TotalLength(ZigzagPath.Build(Opts(passes: 6, arcs: false)));
        double round = ZigzagPath.TotalLength(ZigzagPath.Build(Opts(passes: 6, arcs: true)));
        Assert.True(round > sharp, "a half circle is longer than turning on the spot");
    }

    [Fact]
    public void TotalLength_CountsEveryLayer()
    {
        double one = ZigzagPath.TotalLength(ZigzagPath.Build(Opts(layers: 1)));
        double four = ZigzagPath.TotalLength(ZigzagPath.Build(Opts(layers: 4)));
        Assert.Equal(4 * one, four, 6);
        // 4 passes of 100 mm plus 3 sharp connectors of 10 mm
        Assert.Equal(430.0, one, 6);
    }

    [Fact]
    public void TotalLength_OfNothingIsZero()
        => Assert.Equal(0.0, ZigzagPath.TotalLength(new List<List<Vec3>>()), 9);

    [Theory]
    [InlineData(0, 10, 8, 1)]         // pass length
    [InlineData(100, 0, 8, 1)]        // pitch
    [InlineData(100, 10, 1, 1)]       // fewer than two passes is not a zig-zag
    [InlineData(100, 10, 8, 0)]       // layers
    public void InvalidOptions_Throw(double passLength, double pitch, int passes, int layers)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ZigzagPath.Build(new ZigzagPathOptions
        {
            PassLength = passLength,
            Pitch = pitch,
            Passes = passes,
            Layers = layers,
        }));

    [Fact]
    public void NullOptions_Throw()
        => Assert.Throws<ArgumentNullException>(() => ZigzagPath.Build(null!));

    [Fact]
    public void NonPositiveLayerHeight_ThrowsOnlyWhenItIsActuallyUsed()
    {
        var single = Opts(layers: 1);
        single.LayerHeight = 0;
        ZigzagPath.Build(single);          // one layer never reads the height

        var stacked = Opts(layers: 2);
        stacked.LayerHeight = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => ZigzagPath.Build(stacked));
    }
}
