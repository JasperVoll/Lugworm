using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The Legend component's Core: colour schemes, nice ticks and the
/// iamlab-legend:v1 wire (4.0.0).</summary>
public class LegendSpecTests
{
    [Fact]
    public void Rgb_HexRoundTrip_AndShortForm()
    {
        Assert.True(Rgb.TryParse("#5AA05A", out var c));
        Assert.Equal((byte)90, c.R); Assert.Equal((byte)160, c.G); Assert.Equal((byte)90, c.B);
        Assert.Equal("#5AA05A", c.Hex);
        Assert.True(Rgb.TryParse("fff", out var w));
        Assert.Equal("#FFFFFF", w.Hex);
        Assert.False(Rgb.TryParse("#12345", out _));
        Assert.False(Rgb.TryParse("", out _));
        Assert.Equal("#804040", Rgb.Lerp(new Rgb(0, 0, 0), new Rgb(255, 128, 128), 0.5).Hex);
    }

    [Fact]
    public void Schemes_EndsMatchAnchors_AndPlasmaViridisReuseTheExistingScales()
    {
        // blues is matplotlib Blues since 4.0.0 (the in-house ramp is gone)
        Assert.Equal("#F7FBFF", ColorSchemes.At(ColorSchemes.Blues, null, 0).Hex);
        Assert.Equal("#08306B", ColorSchemes.At(ColorSchemes.Blues, null, 1).Hex);
        Assert.Equal(Rgb.Of(PlasmaColorScale.Map(0.3)), ColorSchemes.At(ColorSchemes.Plasma, null, 0.3));
        Assert.Equal(Rgb.Of(DeviationColorScale.Map(0.7)), ColorSchemes.At(ColorSchemes.Viridis, null, 0.7));
        // out of range and NaN clamp
        Assert.Equal(ColorSchemes.At(ColorSchemes.Greys, null, 0), ColorSchemes.At(ColorSchemes.Greys, null, -3));
        Assert.Equal(ColorSchemes.At(ColorSchemes.Greys, null, 0), ColorSchemes.At(ColorSchemes.Greys, null, double.NaN));
        // custom ramp interpolates its own stops; one stop is a constant; none falls back to blues
        var custom = new[] { new Rgb(0, 0, 0), new Rgb(200, 100, 0) };
        Assert.Equal(new Rgb(100, 50, 0), ColorSchemes.At(ColorSchemes.Custom, custom, 0.5));
        Assert.Equal(custom[1], ColorSchemes.At(ColorSchemes.Custom, new[] { custom[1] }, 0.1));
        Assert.Equal(ColorSchemes.At(ColorSchemes.Blues, null, 0.5), ColorSchemes.At(ColorSchemes.Custom, null, 0.5));
        Assert.True(ColorSchemes.IsKnown("Plasma"));
        Assert.False(ColorSchemes.IsKnown("no-such-palette"));
        Assert.Equal(11, ColorSchemes.AnchorsOf(ColorSchemes.Plasma).Count);
    }

    [Fact]
    public void Registry_EveryPaletteResolves_FamiliesAndAliases()
    {
        // 5.0.0: 35 engineering palettes, 32 matplotlib, two R, one Lugworm; no ggsci
        Assert.Equal(35, ColorSchemes.All.Count);
        foreach (var s in ColorSchemes.All)
        {
            Assert.True(ColorSchemes.IsKnown(s.Name), s.Name);
            Assert.Same(s, ColorSchemes.Resolve(s.Name.ToUpperInvariant()));
            Assert.True(s.Stops.Count >= 2, s.Name);
            Assert.True(ColorSchemes.Names.Contains(s.Name), s.Name);
        }
        Assert.Equal(ColorSchemes.Custom, ColorSchemes.Names[ColorSchemes.Names.Count - 1]);
        Assert.Null(ColorSchemes.Resolve(ColorSchemes.Custom));
        Assert.Equal(32, ColorSchemes.All.Count(s => s.Family == "mpl"));
        Assert.Equal(2, ColorSchemes.All.Count(s => s.Family == "r"));
        Assert.DoesNotContain(ColorSchemes.All, s => s.Family == "ggsci");
        Assert.Equal("lugworm", ColorSchemes.Resolve(ColorSchemes.Traffic)!.Family);
        // the retired in-house ramp names still resolve; three moved to a kept ramp in 5.0.0
        Assert.Equal("Blues", ColorSchemes.Resolve("blues")!.Name);
        Assert.Equal("Greens", ColorSchemes.Resolve(ColorSchemes.Greens)!.Name);
        Assert.Equal("Greys", ColorSchemes.Resolve(ColorSchemes.Greys)!.Name);
        Assert.Equal("Purples", ColorSchemes.Resolve(ColorSchemes.Pinks)!.Name);
        Assert.Equal("Oranges", ColorSchemes.Resolve(ColorSchemes.Ambers)!.Name);
        Assert.Equal("Greys", ColorSchemes.Resolve("grays")!.Name);
        Assert.Equal("Greys", ColorSchemes.Resolve("gray")!.Name);
        // matplotlib magma ends, the two R palettes
        Assert.Equal("#000004", ColorSchemes.At(ColorSchemes.Magma, null, 0).Hex);
        Assert.Equal("#FCFDBF", ColorSchemes.At(ColorSchemes.Magma, null, 1).Hex);
        Assert.Equal("#E69F00", ColorSchemes.Resolve("Okabe-Ito")!.Stops[1].Hex);
        Assert.Equal("#FF0000", ColorSchemes.Resolve("rainbow")!.Stops[0].Hex);
        // removed in 5.0.0: no longer known, and a spec naming one falls back to blues
        foreach (string removed in new[] { "npg", "gsea", "d3-category20", "YlOrBr", "RdPu", "Pastel1", "heat", "R4" })
        {
            Assert.False(ColorSchemes.IsKnown(removed), removed);
            Assert.Null(ColorSchemes.Resolve(removed));
            Assert.Equal(ColorSchemes.At(ColorSchemes.Blues, null, 0.4), ColorSchemes.At(removed, null, 0.4));
        }
        Assert.True(ColorSchemes.IsQualitative("tab10"));
        Assert.False(ColorSchemes.IsQualitative("magma"));
        // plasma and viridis stay routed to the Core scale classes, and their registry stops agree
        Assert.Equal(Rgb.Of(PlasmaColorScale.Map(1)), ColorSchemes.Resolve(ColorSchemes.Plasma)!.Stops[10]);
        Assert.Equal(Rgb.Of(DeviationColorScale.Map(0)), ColorSchemes.Resolve(ColorSchemes.Viridis)!.Stops[0]);
    }

    [Fact]
    public void Registry_PalettesAnswerToFrozenNumbers()
    {
        // the numbers are documented in docs/reference/legend-palette-table.md and typed by
        // users; they are append-only, so these must never change
        var pinned = new (int Number, string Name)[]
        {
            (0, "viridis"), (1, "plasma"), (3, "magma"), (5, "turbo"), (7, "Blues"),
            (14, "coolwarm"), (23, "RdYlGn"), (26, "rainbow"), (27, "tab10"), (33, "Okabe-Ito"), (34, "traffic"),
        };
        foreach (var (number, name) in pinned)
        {
            string text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(name, ColorSchemes.All[number].Name);
            Assert.Equal(name, ColorSchemes.Normalize(text));
            Assert.Equal(name, ColorSchemes.Normalize(" " + text + " "));
            Assert.Same(ColorSchemes.Resolve(name), ColorSchemes.Resolve(text));
            Assert.True(ColorSchemes.IsKnown(text));
            Assert.Equal(number, ColorSchemes.NumberOf(name));
            Assert.Equal(number, ColorSchemes.NumberOf(text));
        }
        // plasma and viridis by number still route to their exact scale classes
        Assert.Equal(Rgb.Of(PlasmaColorScale.Map(0.37)), ColorSchemes.At("1", null, 0.37));
        Assert.Equal(Rgb.Of(DeviationColorScale.Map(0.61)), ColorSchemes.At("0", null, 0.61));
        // out of range, negative and non-integer text is refused, never wrapped or rounded
        foreach (string bad in new[] { "35", "-1", "999", "2.0", "1e1", "0x3" })
        {
            Assert.False(ColorSchemes.IsKnown(bad), bad);
            Assert.Null(ColorSchemes.Resolve(bad));
            Assert.Equal(-1, ColorSchemes.NumberOf(bad));
        }
        Assert.Equal(-1, ColorSchemes.NumberOf(ColorSchemes.Custom));
        // a number on the wire is written as the palette's name
        var spec = new LegendSpec(LegendType.Continuous, scheme: "14");
        Assert.Equal("coolwarm", spec.Scheme);
        Assert.Contains("scheme=coolwarm", spec.Serialize());
    }

    [Fact]
    public void Classes_QualitativePalettePicksColoursOneByOne()
    {
        var tab = ColorSchemes.Resolve("tab10")!;
        var s = new LegendSpec(LegendType.Classes, scheme: "tab10", breaks: new[] { 1.0, 2.0, 3.0 });
        Assert.Equal(tab.Stops[0], s.ClassColor(0));
        Assert.Equal(tab.Stops[2], s.ClassColor(2));
        var many = new LegendSpec(LegendType.Groups, scheme: "tab10", labels: Enumerable.Range(0, 12).Select(i => "g" + i).ToArray());
        Assert.Equal(tab.Stops[1], many.ClassColor(11));   // cycles past the palette's end
        var rev = new LegendSpec(LegendType.Classes, scheme: "tab10", breaks: new[] { 1.0 }, reverse: true);
        Assert.Equal(tab.Stops[9], rev.ClassColor(0));
        // a ramp is still sampled evenly over the classes
        Assert.Equal(ColorSchemes.At(ColorSchemes.Magma, null, 0), new LegendSpec(LegendType.Classes, scheme: "magma", breaks: new[] { 1.0, 2.0 }).ClassColor(0));
    }

    [Fact]
    public void Continuous_CustomStopsInterpolateByPosition_AndRoundTrip()
    {
        var colors = new[] { new Rgb(0, 0, 0), new Rgb(255, 255, 255), new Rgb(255, 0, 0) };
        var stops = new[] { 0.0, 0.25, 1.0 };
        var s = new LegendSpec(LegendType.Continuous, min: 0, max: 1, scheme: ColorSchemes.Custom, colors: colors, stops: stops);
        Assert.Equal(new Rgb(128, 128, 128), s.ColorFor(0.125));
        Assert.Equal(new Rgb(255, 255, 255), s.ColorFor(0.25));
        Assert.Equal(new Rgb(255, 128, 128), s.ColorFor(0.625));
        string wire = s.Serialize();
        Assert.Contains("stops=0,0.25,1", wire);
        var back = LegendSpec.Parse(wire);
        Assert.Equal(stops, back.Stops.ToArray());
        Assert.Equal(new Rgb(128, 128, 128), back.ColorFor(0.125));
        // mismatched or unordered positions fall back to even spacing, never throw
        Assert.Empty(new LegendSpec(LegendType.Continuous, colors: colors, stops: new[] { 0.0, 1.0 }).Stops);
        Assert.Empty(new LegendSpec(LegendType.Continuous, colors: colors, stops: new[] { 0.0, 0.9, 0.5 }).Stops);
        Assert.Empty(LegendSpec.Parse("iamlab-legend:v1:colors=#000000,#FFFFFF;stops=0.5").Stops);
        Assert.Equal(new Rgb(128, 128, 128), new LegendSpec(LegendType.Continuous, scheme: ColorSchemes.Custom, colors: new[] { colors[0], colors[1] }, stops: new[] { 0.0, 1.0 }).ColorFor(0.5));
    }

    [Fact]
    public void NiceTicks_HeckbertSteps()
    {
        Assert.Equal(20.0, NiceTicks.NiceStep(100, 6), 12);      // raw 20 → 20
        Assert.Equal(0.5, NiceTicks.NiceStep(2, 5), 12);         // raw 0.5 → 0.5
        Assert.Equal(50.0, NiceTicks.NiceStep(120, 4), 12);      // raw 40 → 50
        Assert.Equal(new[] { 0.0, 20.0, 40.0, 60.0, 80.0, 100.0 }, NiceTicks.Inside(0, 100, 6));
        Assert.Equal(new[] { 30.0, 40.0, 50.0 }, NiceTicks.Inside(25, 52, 4));
        Assert.Equal(new[] { 5.0 }, NiceTicks.Inside(5, 5, 4));
        Assert.Equal(2, NiceTicks.DecimalsFor(0.05));
        Assert.Equal(0, NiceTicks.DecimalsFor(20));
        Assert.Equal(1, NiceTicks.DecimalsFor(0.5));
    }

    [Fact]
    public void Continuous_ColoursFractionsTicksAndFormat()
    {
        var s = new LegendSpec(LegendType.Continuous, title: "interlayer time", unit: "s", min: 0, max: 120,
                               scheme: ColorSchemes.Plasma,
                               marks: new[] { (10.0, "fast"), (120.0, "slow") },
                               unavailable: (new Rgb(150, 150, 150), "no layer below"));
        Assert.Equal(0.25, s.Fraction(30), 12);
        Assert.Equal(1.0, s.Fraction(500), 12);
        Assert.True(double.IsNaN(s.Fraction(double.NaN)));
        Assert.Equal(Rgb.Of(PlasmaColorScale.Map(0.25)), s.ColorFor(30));
        Assert.Equal(new Rgb(150, 150, 150), s.ColorFor(double.NaN));
        var ticks = s.TickValues();
        Assert.Equal(0.0, ticks.First());
        Assert.Equal(120.0, ticks.Last());
        Assert.Equal(new[] { 0.0, 50.0, 100.0, 120.0 }, ticks);   // Heckbert step 50 over 120
        Assert.Equal("30 s", s.Format(30));
        Assert.Equal("no layer below", s.Format(double.NaN));
        var rev = new LegendSpec(LegendType.Continuous, min: 0, max: 10, reverse: true);
        Assert.Equal(0.8, rev.Fraction(2), 12);
        var fixedTicks = new LegendSpec(LegendType.Continuous, min: 0, max: 1, ticks: 3, decimals: 2);
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, fixedTicks.TickValues());
        Assert.Equal("0.50", fixedTicks.Format(0.5));
    }

    [Fact]
    public void Classes_OverhangLegendMatchesTheComponentColours()
    {
        var s = OverhangColors.Legend(25, 45);
        Assert.Equal(LegendType.Classes, s.Type);
        Assert.Equal(3, s.ClassCount);
        Assert.Equal(0, s.ClassOf(10));
        Assert.Equal(1, s.ClassOf(25));
        Assert.Equal(2, s.ClassOf(70));
        Assert.Equal(-1, s.ClassOf(double.NaN));
        Assert.Equal(OverhangColors.Safe, s.ColorFor(10));
        Assert.Equal(OverhangColors.Warning, s.ColorFor(30));
        Assert.Equal(OverhangColors.Critical, s.ColorFor(45));
        Assert.Equal("safe", s.ClassLabel(0));
        Assert.Equal("critical", s.ClassLabel(2));
        // fail below warn is lifted to warn, like the component does
        Assert.Equal(new[] { 30.0, 30.0 }, OverhangColors.Legend(30, 10).Breaks.ToArray());
        // the global reference names itself in the title, in both modes, and survives the wire
        Assert.Equal("overhang", s.Title);
        Assert.Equal("overhang (global)", OverhangColors.Legend(25, 45, OverhangReference.Global).Title);
        Assert.Equal("overhang (global)", OverhangColors.ContinuousLegend(25, 45, OverhangReference.Global).Title);
        Assert.Equal("overhang", OverhangColors.ContinuousLegend(25, 45).Title);
        Assert.Equal("overhang (global)", LegendSpec.Parse(OverhangColors.Legend(25, 45, OverhangReference.Global).Serialize()).Title);
        // unlabelled classes describe their range
        var ranges = new LegendSpec(LegendType.Classes, unit: "°", breaks: new[] { 45.0, 25.0 }, decimals: 0);
        Assert.Equal(new[] { 25.0, 45.0 }, ranges.Breaks.ToArray());
        Assert.Equal("< 25 °", ranges.ClassLabel(0));
        Assert.Equal("25 ° – 45 °", ranges.ClassLabel(1));
        Assert.Equal("≥ 45 °", ranges.ClassLabel(2));
        // scheme-sampled class colours when no list is given
        Assert.Equal(ColorSchemes.At(ColorSchemes.Greens, null, 0), new LegendSpec(LegendType.Classes, scheme: "greens", breaks: new[] { 1.0, 2.0 }).ClassColor(0));
    }

    [Fact]
    public void Overhang_MagmaClassesAndContinuousRamp()
    {
        // the three class colours are fixed magma samples (deep purple / red-orange / light orange)
        Assert.Equal("#3B0F70", OverhangColors.Safe.Hex);
        Assert.Equal("#CA4070", OverhangColors.Warning.Hex);
        Assert.Equal("#FEB780", OverhangColors.Critical.Hex);
        Assert.Equal(ColorSchemes.At(ColorSchemes.Magma, null, OverhangColors.WarningT), OverhangColors.Of(OverhangClass.Warning));
        // continuous: 0 → magma start, critical → magma end, above saturates, NaN grey
        Assert.Equal(ColorSchemes.At(ColorSchemes.Magma, null, 0), OverhangColors.Continuous(0, 25, 45));
        Assert.Equal(ColorSchemes.At(ColorSchemes.Magma, null, 0.5), OverhangColors.Continuous(22.5, 25, 45));
        Assert.Equal(ColorSchemes.At(ColorSchemes.Magma, null, 1), OverhangColors.Continuous(80, 25, 45));
        Assert.Equal(Rgb.Of(PlasmaColorScale.Unavailable), OverhangColors.Continuous(double.NaN, 25, 45));
        Assert.Equal(30.0, OverhangColors.ContinuousMax(30, 10));   // fail lifted to warn
        Assert.Equal(1.0, OverhangColors.ContinuousMax(0, 0));      // never a flat domain
        var s = OverhangColors.ContinuousLegend(25, 45);
        Assert.Equal(LegendType.Continuous, s.Type);
        Assert.Equal(ColorSchemes.Magma, s.Scheme);
        Assert.Equal(0, s.Min); Assert.Equal(45, s.Max);
        Assert.Equal(new[] { (25.0, "warning"), (45.0, "critical") }, s.Marks.ToArray());
        Assert.Equal(OverhangColors.Continuous(30, 25, 45), s.ColorFor(30));
        var back = LegendSpec.Parse(s.Serialize());
        Assert.Equal(s.Marks, back.Marks);
        Assert.Equal(s.ColorFor(12), back.ColorFor(12));
    }

    [Fact]
    public void Groups_AndSize()
    {
        var g = new LegendSpec(LegendType.Groups, labels: new[] { "wall", "infill", "skirt" },
                               colors: new[] { new Rgb(1, 1, 1), new Rgb(2, 2, 2), new Rgb(3, 3, 3) });
        Assert.Equal(3, g.ClassCount);
        Assert.Equal(new Rgb(2, 2, 2), g.ColorFor(1));
        Assert.Equal(new Rgb(3, 3, 3), g.ColorFor(7));      // clamps to the last colour
        Assert.Equal("infill", g.ClassLabel(1));
        Assert.Equal("group 5", new LegendSpec(LegendType.Groups, colors: new[] { new Rgb(1, 1, 1) }).ClassLabel(4));
        var size = new LegendSpec(LegendType.Size, min: 0, max: 4, breaks: new[] { 1.0, 2.0, 4.0 }, colors: new[] { new Rgb(9, 9, 9) });
        Assert.Equal(0.5, size.Fraction(2), 12);
        Assert.Equal(new Rgb(9, 9, 9), size.ColorFor(3));
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, size.Breaks.ToArray());   // size breaks keep their order
    }

    [Fact]
    public void Wire_RoundTrips_EscapesAndIgnoresUnknownKeys()
    {
        var s = new LegendSpec(LegendType.Continuous, title: "time; a=b", unit: "s", min: 0, max: 120.5,
                               scheme: ColorSchemes.Plasma,
                               marks: new[] { (10.0, "fast, really|slow") },
                               unavailable: (new Rgb(150, 150, 150), "no layer below"),
                               reverse: true, decimals: 1, ticks: 4);
        string wire = s.Serialize();
        Assert.StartsWith("iamlab-legend:v1:type=continuous;", wire);
        Assert.Contains("title=time%3B a%3Db", wire);
        Assert.Contains("marks=10|fast%2C really%7Cslow", wire);
        var r = LegendSpec.Parse(wire);
        Assert.Equal(s.Type, r.Type);
        Assert.Equal(s.Title, r.Title);
        Assert.Equal(s.Unit, r.Unit);
        Assert.Equal(s.Min, r.Min); Assert.Equal(s.Max, r.Max);
        Assert.Equal(s.Scheme, r.Scheme);
        Assert.Equal(s.Marks, r.Marks);
        Assert.Equal(s.Unavailable, r.Unavailable);
        Assert.True(r.Reverse);
        Assert.Equal(1, r.Decimals);
        Assert.Equal(4, r.Ticks);

        var ov = LegendSpec.Parse(OverhangColors.Legend(25, 45).Serialize());
        Assert.Equal(new[] { OverhangColors.Safe, OverhangColors.Warning, OverhangColors.Critical }, ov.Colors.ToArray());
        Assert.Equal(new[] { 25.0, 45.0 }, ov.Breaks.ToArray());
        Assert.Equal(new[] { "safe", "warning", "critical" }, ov.Labels.ToArray());
        Assert.Equal(ColorSchemes.Custom, ov.Scheme);

        // unknown keys ignored, missing keys default, unknown scheme with colours → custom
        var loose = LegendSpec.Parse("iamlab-legend:v1:type=classes;future=42;colors=#FF0000,#00FF00;scheme=no-such-palette");
        Assert.Equal(LegendType.Classes, loose.Type);
        Assert.Equal(2, loose.Colors.Count);
        Assert.Equal(ColorSchemes.Custom, loose.Scheme);
        Assert.Equal(0.0, loose.Min); Assert.Equal(1.0, loose.Max);

        Assert.Throws<FormatException>(() => LegendSpec.Parse("iamlab-bed:v1:w=1"));
        Assert.Throws<FormatException>(() => LegendSpec.Parse("iamlab-legend:v2:type=size"));
        Assert.False(LegendSpec.TryParse("nope", out _, out string? err));
        Assert.NotNull(err);
        Assert.True(LegendSpec.IsWire("  iamlab-legend:v1:min=0"));
        Assert.False(LegendSpec.IsWire(null));
    }

    [Fact]
    public void Wire_IsCultureInvariant()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var s = new LegendSpec(LegendType.Continuous, min: 0.5, max: 120.25, breaks: new[] { 1.5 }, decimals: 2);
            string wire = s.Serialize();
            Assert.Contains("min=0.5;max=120.25", wire);
            Assert.Equal("1.50", LegendSpec.Parse(wire).Format(1.5));
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Fact]
    public void Wire_RoundTrips_FieldValuesEndingInAColon()
    {
        var spec = new LegendSpec(LegendType.Continuous, title: "Time:", unit: "s:", min: 3, max: 120,
                                  marks: new[] { (10.0, "a"), (double.NaN, "dropped") });
        var back = LegendSpec.Parse(spec.Serialize());
        Assert.Equal("Time:", back.Title);
        Assert.Equal("s:", back.Unit);
        Assert.Equal(3, back.Min);
        Assert.Equal(120, back.Max);
        Assert.Single(back.Marks);
    }
}
