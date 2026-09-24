using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class WaveInfillTests
{
    const double Side = 60.0;
    const double Spacing = 4.0;
    const double Wavelength = 10.0;

    static readonly List<IReadOnlyList<Vec3>> Square = new List<IReadOnlyList<Vec3>>
    {
        new List<Vec3>
        {
            new Vec3(0, 0, 0), new Vec3(Side, 0, 0), new Vec3(Side, Side, 0),
            new Vec3(0, Side, 0), new Vec3(0, 0, 0),
        },
    };

    /// <summary>The first row's centre-line, matching <c>ZigzagSpans</c>' own placement.</summary>
    const double Y0 = Spacing * 0.5;

    static Infill.WaveOptions Opts(bool tangent = false, double clearance = 0.0, double res = 0.25)
        => new Infill.WaveOptions
        {
            Wavelength = Wavelength,
            Tangent = tangent,
            Clearance = clearance,
            Resolution = res,
        };

    static int Row(Vec3 p) => (int)Math.Round((p.Y - Y0) / Spacing);
    static double Deviation(Vec3 p) => p.Y - (Y0 + Row(p) * Spacing);

    // ── amplitude ──

    [Fact]
    public void Tangent_PutsTheMirroredPeaksExactlyTogether()
    {
        double a = Opts(tangent: true).AmplitudeFor(Spacing);
        // neighbouring centre-lines are Spacing apart and lean towards each other by `a`
        Assert.Equal(0.0, Spacing - 2 * a, 6);
        Assert.True(a < Spacing / 2, "amplitude must stay strictly inside the row band");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.5)]
    public void WithoutTangent_ThePeaksStopTheClearanceShort(double clearance)
    {
        double a = Opts(clearance: clearance).AmplitudeFor(Spacing);
        Assert.Equal(clearance, Spacing - 2 * a, 6);
    }

    [Fact]
    public void ClearanceDefaultsToAFifthOfTheSpacing()
        => Assert.Equal(0.2 * Spacing, Spacing - 2 * Opts().AmplitudeFor(Spacing), 6);

    // ── geometry ──

    [Fact]
    public void ZeroAmplitude_ProducesTheSameRowsAsZigzag()
    {
        // clearance == spacing drives the amplitude to zero, so the rows are straight
        var wave = Infill.WaveSpans(Square, Spacing, 0.0, 0.0, new SineProfile(),
                                    Opts(clearance: Spacing), out string? wd);
        var zig = Infill.ZigzagSpans(Square, Spacing, 0.0, 0.0, out string? zd);

        Assert.Null(wd);
        Assert.Null(zd);
        var waveRows = wave.SelectMany(s => s).Select(Row).Distinct().OrderBy(r => r).ToList();
        var zigRows = zig.SelectMany(s => s).Select(Row).Distinct().OrderBy(r => r).ToList();
        Assert.Equal(zigRows, waveRows);
    }

    [Fact]
    public void AdjacentRowsAreMirrored()
    {
        var spans = Infill.WaveSpans(Square, Spacing, 0.0, 0.0, new SineProfile(), Opts(), out _);
        double a = Opts().AmplitudeFor(Spacing);

        // x = 22.5 is a crest of sin(2*pi*x/10): 22.5 = 2 periods + a quarter
        var atCrest = spans.SelectMany(s => s)
                           .Where(p => Math.Abs(p.X - 22.5) < 0.13)
                           .GroupBy(Row)
                           .ToDictionary(g => g.Key, g => g.Average(Deviation));

        Assert.True(atCrest.Count >= 4, $"expected several rows at the sample x, got {atCrest.Count}");
        foreach (var kv in atCrest)
        {
            double expected = (kv.Key & 1) == 0 ? a : -a;   // even rows crest up, odd rows down
            Assert.Equal(expected, kv.Value, 1);
        }
    }

    [Fact]
    public void EveryPointStaysInsideTheBoundary()
    {
        var spans = Infill.WaveSpans(Square, Spacing, 0.0, 0.0, new SineProfile(), Opts(tangent: true), out _);
        Assert.NotEmpty(spans);
        foreach (var p in spans.SelectMany(s => s))
        {
            Assert.InRange(p.X, -1e-6, Side + 1e-6);
            Assert.InRange(p.Y, -1e-6, Side + 1e-6);
        }
    }

    [Fact]
    public void RowRecoveryHoldsAtTheAmplitudeLimit()
    {
        // clearance 0.008 on spacing 4 => amplitude 1.996 = 0.499 x spacing
        var spans = Infill.WaveSpans(Square, Spacing, 0.0, 0.0, new SineProfile(),
                                     Opts(clearance: 0.008), out _);
        Assert.NotEmpty(spans);
        foreach (var p in spans.SelectMany(s => s))
            Assert.True(Math.Abs(Deviation(p)) < Spacing / 2,
                $"point at y={p.Y} left its own row band (deviation {Deviation(p)})");
    }

    [Fact]
    public void RotatedByAngle_StillFills()
    {
        var spans = Infill.WaveSpans(Square, Spacing, 37.0, 1.5, new SineProfile(), Opts(), out string? d);
        Assert.Null(d);
        Assert.NotEmpty(spans);
        foreach (var p in spans.SelectMany(s => s)) Assert.Equal(1.5, p.Z, 9);
    }

    [Theory]
    [InlineData(0.0)]      // spacing
    [InlineData(-1.0)]
    public void NonPositiveSpacing_IsRefusedWithADiagnostic(double spacing)
    {
        var spans = Infill.WaveSpans(Square, spacing, 0.0, 0.0, new SineProfile(), Opts(), out string? d);
        Assert.Empty(spans);
        Assert.NotNull(d);
    }

    [Fact]
    public void MissingProfileOrOptions_IsRefusedWithADiagnostic()
    {
        Assert.Empty(Infill.WaveSpans(Square, Spacing, 0.0, 0.0, null!, Opts(), out string? d1));
        Assert.NotNull(d1);
        Assert.Empty(Infill.WaveSpans(Square, Spacing, 0.0, 0.0, new SineProfile(), null!, out string? d2));
        Assert.NotNull(d2);
    }

    // ── profiles ──

    [Theory]
    [InlineData(0.0)]
    [InlineData(23.0)]
    public void ConcaveShoulders_ConnectAlongWallClearanceBoundary(double angle)
    {
        var outline = new List<IReadOnlyList<Vec3>> { new List<Vec3>
        {
            new Vec3(0, 0, 0), new Vec3(80, 0, 0), new Vec3(80, 16, 0),
            new Vec3(55, 26, 0), new Vec3(55, 50, 0), new Vec3(25, 50, 0),
            new Vec3(25, 26, 0), new Vec3(0, 16, 0), new Vec3(0, 0, 0),
        } };
        var boundary = Offset.Polygons(outline, -2.0, 1.5);
        var paths = Infill.WaveSpans(boundary, 6, angle, 1.5, new SineProfile(),
            Opts(), out var diagnostic);
        Assert.Null(diagnostic);
        var path = Assert.Single(paths);
        // Check entire segments, not just vertices: a chord across the shoulder
        // can have both ends inside while crossing the wall-clearance area.
        for (int i = 1; i < path.Count; i++)
        {
            var a = path[i - 1]; var b = path[i];
            double length = PolylineOps.Dist(a, b);
            var clipped = Infill.ClipLines(new[] { (a, b) }, boundary);
            double inside = clipped.Sum(s => PolylineOps.Dist(s.Item1, s.Item2));
            // Clipper may omit an exactly coincident boundary segment.
            if (Math.Abs(inside - length) <= 0.002) continue;
            for (int j = 0; j <= 10; j++)
            {
                var p = PolylineOps.Lerp(a, b, j / 10.0);
                Assert.Contains(boundary, ring => PolylineOps.Dist2Xy(p,
                    Lugworm.Core.Paths.SeamAnchor.ClosestOnRing(ring, p).Point) <= 1e-6);
            }
        }
    }

    [Fact]
    public void SeparateIslands_RemainSeparateWavePaths()
    {
        var boundary = new List<IReadOnlyList<Vec3>>(Square);
        boundary.Add(Square[0].Select(p => new Vec3(p.X + 100, p.Y, p.Z)).ToList());
        var paths = Infill.WaveSpans(boundary, Spacing, 0, 0, new SineProfile(), Opts(), out _);
        Assert.NotEmpty(paths);
        foreach (var path in paths)
            Assert.True(path.All(p => p.X <= Side + 1e-6) || path.All(p => p.X >= 100 - 1e-6));
    }

    static GraphMap SineGraph() => new GraphMap { Kind = GraphMapKind.Sine, P = new[] { 0.5, 1.0, 0.0 } };

    [Fact]
    public void GraphProfile_MatchesTheSine_WhenTheCurveRepeats()
    {
        var graph = new GraphProfile(SineGraph(), repeat: true);
        var sine = new SineProfile();
        for (int i = 0; i <= 200; i++)
        {
            double u = i / 200.0;
            Assert.Equal(sine.At(u), graph.At(u), 9);
        }
    }

    [Fact]
    public void PingPong_HalvesThePeriod_SoItIsNotTheSineCurve()
    {
        var pingPong = new GraphProfile(SineGraph(), repeat: false);
        var sine = new SineProfile();
        // documents the trap: the default tiling is deliberately not the plain sine
        Assert.Contains(Enumerable.Range(0, 200), i =>
            Math.Abs(pingPong.At(i / 200.0) - sine.At(i / 200.0)) > 1e-3);
    }

    [Fact]
    public void PingPong_TilesContinuously_EvenWhenTheGraphsEndsDiffer()
    {
        // Linear 0 -> 1: f(0) = 0, f(1) = 1, the worst case for a repeated curve
        var ramp = new GraphMap { Kind = GraphMapKind.Linear, P = new[] { 0.0, 1.0 } };
        var pingPong = new GraphProfile(ramp, repeat: false);
        var repeated = new GraphProfile(ramp, repeat: true);

        Assert.True(repeated.EndsMismatch);
        // across a period boundary the ping-pong value barely moves; the repeat jumps the
        // full range
        double jumpPingPong = Math.Abs(pingPong.At(1.0 - 1e-6) - pingPong.At(1e-6));
        double jumpRepeat = Math.Abs(repeated.At(1.0 - 1e-6) - repeated.At(1e-6));
        Assert.True(jumpPingPong < 1e-4, $"ping-pong jumped {jumpPingPong}");
        Assert.True(jumpRepeat > 1.9, $"repeat should jump the full range, jumped {jumpRepeat}");
    }

    [Fact]
    public void GraphProfile_KeepsItsOwnCopyOfTheCurve()
    {
        var map = SineGraph();
        var profile = new GraphProfile(map, repeat: true);
        double before = profile.At(0.25);
        map.P[0] = 0.1;                       // the component keeps editing its map
        Assert.Equal(before, profile.At(0.25), 12);
    }

    [Fact]
    public void GraphDrivenWaveInfill_Fills()
    {
        var spans = Infill.WaveSpans(Square, Spacing, 0.0, 0.0,
                                     new GraphProfile(SineGraph(), repeat: true), Opts(), out string? d);
        Assert.Null(d);
        Assert.NotEmpty(spans);
        foreach (var p in spans.SelectMany(s => s))
            Assert.True(Math.Abs(Deviation(p)) < Spacing / 2);
    }

    [Fact]
    public void SerializedGraph_RoundTripsThroughTheMapOutput()
    {
        // what the Graph Mapper's `map` output carries and what Slice reads back
        var original = SineGraph();
        var restored = GraphMap.Deserialize(original.Serialize());
        Assert.Equal(original.Kind, restored.Kind);
        for (int i = 0; i <= 50; i++)
        {
            double t = i / 50.0;
            Assert.Equal(original.Evaluate(t), restored.Evaluate(t), 12);
        }
    }
}
