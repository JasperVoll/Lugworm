using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Variable layer height (refinement note P5 of 2026-07): local-layer-height measurement
/// + its substitution for h in GcodeBuilder's E-kinematics
/// (E = L×h×w/(π(d/2)²)×k, GCODE_Essential_Overview lineage). The flow-multiplier
/// channel stays reserved for material calibration and must be unaffected.</summary>
public class VariableFlowTests
{
    static List<Vec3> Line(double z0, double z1, double y = 0, int n = 11)
    {
        var pts = new List<Vec3>(n);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            pts.Add(new Vec3(t * 100.0, y, z0 + t * (z1 - z0)));
        }
        return pts;
    }

    static IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> Branches(params List<Vec3>[] layers)
        => layers.Select(l => (IReadOnlyList<IReadOnlyList<Vec3>>)new List<IReadOnlyList<Vec3>> { l }).ToList();

    // ---- local-height measurement ----

    [Fact]
    public void AdaptivePlanarOverhang_UsesVerticalGap_AndBuilderUsesThatHeight()
    {
        var b = Branches(Line(1, 1), Line(1.5, 1.5, y: 2));
        var ht = VariableFlow.LocalHeights(b, 1, minMul: 0.1, objectIds: new[] { 0, 0 });
        Assert.All(ht[1][0], h => Assert.Equal(0.5, h, 9));
        var heights = ht.Select(branch => (IReadOnlyList<IReadOnlyList<double>>)
            branch.Select(c => (IReadOnlyList<double>)c).ToList()).ToList();
        var cfg = new GcodeConfig { LayerHeight = 1, Width = 2, RamDiameter = 20, PressureCoeff = 1 };
        var result = GcodeBuilder.Build(b, cfg, null, heights);
        Assert.Equal(0.3, result.Volume, 6); // 100*2*1 + 100*2*0.5 mm^3
    }

    [Fact]
    public void PlanarExplicitObjectIds_PreserveLargeGapClampAndFirstLayer()
    {
        var b = Branches(Line(1, 1), Line(4, 4), Line(1, 1));
        var ht = VariableFlow.LocalHeights(b, 1, maxMul: 2.5, objectIds: new[] { 0, 0, 1 });
        Assert.All(ht[1][0], h => Assert.Equal(2.5, h, 9));
        Assert.All(ht[2][0], h => Assert.Equal(1, h, 9));
    }

    [Fact]
    public void UniformSpacing_HeightsEqualNominal()
    {
        var b = Branches(Line(0, 0), Line(3, 3), Line(6, 6));
        var ht = VariableFlow.LocalHeights(b, nominalH: 3.0);
        foreach (var branch in ht) foreach (var ct in branch) foreach (var h in ct)
            Assert.Equal(3.0, h, 2);
    }

    [Fact]
    public void Wedge_HeightsFollowLocalGap()
    {
        // layer 1 rises from 2 to 4 mm above a flat layer 0
        var b = Branches(Line(0, 0), Line(2, 4));
        var ht = VariableFlow.LocalHeights(b, nominalH: 3.0);
        var hs = ht[1][0];
        Assert.True(hs.First() < hs.Last(), "local height must grow with the gap");
        Assert.InRange(hs.First(), 1.9, 2.2);
        Assert.InRange(hs.Last(), 3.8, 4.1);
    }

    [Fact]
    public void FirstLayer_AlwaysNominal()
    {
        var b = Branches(Line(1, 5));   // even a sloped first layer
        var ht = VariableFlow.LocalHeights(b, nominalH: 3.0);
        Assert.All(ht[0][0], h => Assert.Equal(3.0, h, 9));
    }

    [Fact]
    public void Clamps_Respected()
    {
        var b = Branches(Line(0, 0), Line(0.1, 20));   // gaps 0.1..20 over nominal 3
        var ht = VariableFlow.LocalHeights(b, 3.0, minMul: 0.5, maxMul: 1.5);
        foreach (var h in ht[1][0]) Assert.InRange(h, 1.5, 4.5);   // 0.5*3 .. 1.5*3
    }

    [Fact]
    public void NewObjectDiscontinuity_TreatedAsBaseLayer()
    {
        // sequential multi-object: "layer below" is another object's top, 50 mm away
        var b = Branches(Line(0, 0), Line(50, 50));
        var ht = VariableFlow.LocalHeights(b, 3.0);
        Assert.All(ht[1][0], h => Assert.Equal(3.0, h, 9));   // median > 2*nominal -> nominal
    }

    // ---- height substitution in GcodeBuilder's E-kinematics ----

    static double[] PrintEValues(GcodeResult r)
        => r.Lines.Where(l => l.StartsWith("G1 X") && l.Contains("E"))
                  .Select(l => double.Parse(l.Substring(l.IndexOf('E') + 1),
                                            CultureInfo.InvariantCulture))
                  .ToArray();

    [Fact]
    public void Builder_SubstitutesLocalHeightInEFactor()
    {
        // one contour, two 10 mm segments, per-point heights [2, 2, 4] mm:
        // segment heights = mean of endpoints -> 2.0 and 3.0
        // E_seg = L * (h_seg * w / ramArea) * k  - the classic ram model with h local
        var pts = new List<Vec3> { new(0, 0, 5), new(10, 0, 5), new(20, 0, 5) };
        var cfg = new GcodeConfig { LayerHeight = 2, Width = 10, RamDiameter = 20, PressureCoeff = 1.0 };
        double ramArea = Math.PI * 100.0;

        var branches = new List<IReadOnlyList<IReadOnlyList<Vec3>>>
        { new List<IReadOnlyList<Vec3>> { pts } };
        var heights = new List<IReadOnlyList<IReadOnlyList<double>>>
        { new List<IReadOnlyList<double>> { new List<double> { 2.0, 2.0, 4.0 } } };

        var r = GcodeBuilder.Build(branches, cfg, null, heights);
        var e = PrintEValues(r);
        Assert.Equal(2, e.Length);
        Assert.Equal(10.0 * (2.0 * 10.0 / ramArea), e[0], 4);
        Assert.Equal(10.0 * (3.0 * 10.0 / ramArea), e[1], 4);
        // volume integrates the local height: 10*2*10 + 10*3*10 = 500 mm^3 = 0.5 cm^3
        Assert.Equal(0.5, r.Volume, 4);
    }

    [Fact]
    public void Builder_NoHeightTree_UnchangedKinematics()
    {
        // without a height tree the E per mm must be exactly the classic ram model
        var pts = new List<Vec3> { new(0, 0, 5), new(10, 0, 5), new(20, 0, 5) };
        var cfg = new GcodeConfig { LayerHeight = 2, Width = 10, RamDiameter = 20, PressureCoeff = 1.3 };
        double ef = 2.0 * 10.0 / (Math.PI * 100.0) * 1.3;

        var branches = new List<IReadOnlyList<IReadOnlyList<Vec3>>>
        { new List<IReadOnlyList<Vec3>> { pts } };
        var r = GcodeBuilder.Build(branches, cfg);
        var e = PrintEValues(r);
        Assert.Equal(2, e.Length);
        Assert.All(e, v => Assert.Equal(10.0 * ef, v, 4));
        // classic volume: 20mm * 2 * 10 = 400 mm^3 = 0.4 cm^3 (PressureCoeff is material, not volume)
        Assert.Equal(0.4, r.Volume, 4);
    }

    [Fact]
    public void Builder_FlowAndHeightChannelsIndependent()
    {
        // material flow multiplier (viscosity calibration) multiplies ON TOP of the
        // geometric local height - the channels must not interfere
        var pts = new List<Vec3> { new(0, 0, 5), new(10, 0, 5) };
        var cfg = new GcodeConfig { LayerHeight = 2, Width = 10, RamDiameter = 20, PressureCoeff = 1.0 };
        double ramArea = Math.PI * 100.0;

        var branches = new List<IReadOnlyList<IReadOnlyList<Vec3>>>
        { new List<IReadOnlyList<Vec3>> { pts } };
        var flows = new List<IReadOnlyList<IReadOnlyList<double>>>
        { new List<IReadOnlyList<double>> { new List<double> { 1.2, 1.2 } } };
        var heights = new List<IReadOnlyList<IReadOnlyList<double>>>
        { new List<IReadOnlyList<double>> { new List<double> { 3.0, 3.0 } } };

        var r = GcodeBuilder.Build(branches, cfg, flows, heights);
        var e = PrintEValues(r);
        Assert.Single(e);
        Assert.Equal(10.0 * (3.0 * 10.0 / ramArea) * 1.2, e[0], 4);
    }

    // ---- AUD-COR-003: reject non-finite / non-positive parameters --------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void LocalHeights_InvalidNominal_Throws(double nominal)
    {
        var b = Branches(Line(0, 0), Line(3, 3));
        Assert.Throws<ArgumentException>(() => VariableFlow.LocalHeights(b, nominal));
    }

    [Theory]
    [InlineData(double.NaN, 2.5)]
    [InlineData(0.25, double.NaN)]
    [InlineData(double.NegativeInfinity, 2.5)]
    [InlineData(0.25, double.PositiveInfinity)]
    [InlineData(0.0, 2.5)]
    [InlineData(0.25, -1.0)]
    public void LocalHeights_InvalidClampMultipliers_Throw(double mn, double mx)
    {
        var b = Branches(Line(0, 0), Line(3, 3));
        Assert.Throws<ArgumentException>(() => VariableFlow.LocalHeights(b, 3.0, mn, mx));
    }

    [Fact]
    public void LocalHeights_SwappedClampMultipliers_Throw()
    {
        var b = Branches(Line(0, 0), Line(3, 3));
        Assert.Throws<ArgumentException>(() => VariableFlow.LocalHeights(b, 3.0, 2.5, 0.25));
    }

    [Theory]
    [InlineData(double.NaN, 45.0)]
    [InlineData(double.PositiveInfinity, 45.0)]
    [InlineData(0.0, 45.0)]
    [InlineData(-0.5, 45.0)]
    [InlineData(1.5, double.NaN)]
    [InlineData(1.5, double.PositiveInfinity)]
    [InlineData(1.5, 0.0)]
    public void SlopeFlow_InvalidParameters_Throw(double kRef, double refDeg)
    {
        var b = Branches(Line(0, 0), Line(3, 3));
        Assert.Throws<ArgumentException>(() => SlopeFlow.Multipliers(b, kRef, refDeg));
    }

    [Fact]
    public void SlopeFlow_NaNMaxDistance_Throws()
    {
        var b = Branches(Line(0, 0), Line(3, 3));
        Assert.Throws<ArgumentException>(
            () => SlopeFlow.Multipliers(b, 1.5, 45.0, maxDistance: double.NaN));
    }
}
