using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class NonPlanarInfillTests
{
    const double LayerH = 1.0;

    /// <summary>A straight infill row along +X at the layer's own height, plus one wall
    /// so the role filter has something to leave alone.</summary>
    static List<LayerPaths> Stack(int layers, double length = 100.0)
    {
        var stack = new List<LayerPaths>();
        for (int i = 0; i < layers; i++)
        {
            double z = i * LayerH;
            stack.Add(new LayerPaths
            {
                Level = z,
                Index = i,
                Paths =
                {
                    new TaggedPath
                    {
                        Points = new List<Vec3> { new Vec3(0, 0, z), new Vec3(length, 0, z) },
                        Role = PathRole.Infill,
                        WallIndex = -1,
                    },
                    new TaggedPath
                    {
                        Points = new List<Vec3> { new Vec3(0, 5, z), new Vec3(length, 5, z) },
                        Role = PathRole.Wall,
                        WallIndex = 0,
                    },
                },
            });
        }
        return stack;
    }

    static NonPlanarOptions Opts(double amp = 0.3, int ramp = 1, double phase = 0.5)
        => new NonPlanarOptions
        {
            LayerHeight = LayerH,
            Amplitude = amp,
            Wavelength = 20.0,
            DirectionDeg = 0.0,
            Resolution = 1.0,
            PhasePerLayer = phase,
            RampLayers = ramp,
            Profile = new SineProfile(),
        };

    static List<Vec3> Infill(LayerPaths layer) => layer.Paths.First(p => p.Role == PathRole.Infill).Points;
    static List<Vec3> WallOf(LayerPaths layer) => layer.Paths.First(p => p.Role == PathRole.Wall).Points;

    [Fact]
    public void ZeroAmplitude_IsANoOp()
    {
        var stack = Stack(5);
        var before = stack.Select(l => Infill(l).ToList()).ToList();

        NonPlanarInfill.Apply(stack, Opts(amp: 0.0));

        for (int i = 0; i < stack.Count; i++)
        {
            Assert.Equal(before[i].Count, Infill(stack[i]).Count);
            for (int k = 0; k < before[i].Count; k++)
                Assert.Equal(before[i][k].Z, Infill(stack[i])[k].Z, 12);
        }
    }

    [Fact]
    public void FirstAndLastLayer_StayFlat()
    {
        var stack = Stack(6);
        NonPlanarInfill.Apply(stack, Opts());

        foreach (var p in Infill(stack[0])) Assert.Equal(0 * LayerH, p.Z, 9);
        foreach (var p in Infill(stack[5])) Assert.Equal(5 * LayerH, p.Z, 9);
        // a middle layer really did move
        Assert.Contains(Infill(stack[3]), p => Math.Abs(p.Z - 3 * LayerH) > 1e-6);
    }

    [Fact]
    public void Displacement_NeverExceedsTheAmplitude()
    {
        var stack = Stack(8);
        var o = Opts(amp: 0.4);
        NonPlanarInfill.Apply(stack, o);

        for (int i = 0; i < stack.Count; i++)
            foreach (var p in Infill(stack[i]))
                Assert.True(Math.Abs(p.Z - i * LayerH) <= o.Amplitude + 1e-9,
                    $"layer {i} moved {Math.Abs(p.Z - i * LayerH)} > {o.Amplitude}");
    }

    [Fact]
    public void AmplitudeOverTheClamp_IsRefusedNotClamped()
    {
        var stack = Stack(5);
        var o = Opts(amp: 0.5);       // MaxStepFraction 0.4 x layer height 1.0 = 0.4
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NonPlanarInfill.Apply(stack, o));
        Assert.Contains("exceeds", ex.Message);
        // and nothing was modified on the way to the throw
        foreach (var p in Infill(stack[2])) Assert.Equal(2 * LayerH, p.Z, 12);
    }

    [Fact]
    public void AdaptiveGap_RefusesAmplitudeThatWouldReverseLayerOrdering()
    {
        var stack = Stack(2, length: 5.0);
        stack[1].Level = 0.2;
        foreach (var path in stack[1].Paths)
            path.Points = path.Points.Select(p => new Vec3(p.X, p.Y, 0.2)).ToList();
        var o = Opts(amp: 1.0, ramp: 0, phase: 0.5);
        o.LayerHeight = 3.0; // the old nominal-height check incorrectly accepted this

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NonPlanarInfill.Apply(stack, o));

        Assert.Contains("actual layer gap", ex.Message);
        Assert.All(Infill(stack[0]), p => Assert.Equal(0.0, p.Z, 12));
        Assert.All(Infill(stack[1]), p => Assert.Equal(0.2, p.Z, 12));
    }

    [Fact]
    public void FlatEndRamp_IsIncludedInActualGapSafetyCheck()
    {
        var stack = Stack(3);
        stack[1].Level = 0.5;
        stack[2].Level = 0.7;
        for (int i = 0; i < stack.Count; i++)
            foreach (var path in stack[i].Paths)
                path.Points = path.Points.Select(p => new Vec3(p.X, p.Y, stack[i].Level)).ToList();

        // Pair 1-2 has one full-amplitude layer and one flat end layer. A=0.15 is safe:
        // 0.15 <= 2 * 0.4 * 0.2. Ignoring the ramp would reject it.
        NonPlanarInfill.Apply(stack, Opts(amp: 0.15, ramp: 1));

        Assert.Contains(Infill(stack[1]), p => Math.Abs(p.Z - 0.5) > 1e-6);
        Assert.All(Infill(stack[2]), p => Assert.Equal(0.7, p.Z, 12));
    }

    [Fact]
    public void MissingLayerHeightOrProfile_Throws()
    {
        var stack = Stack(3);
        var noHeight = Opts(); noHeight.LayerHeight = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => NonPlanarInfill.Apply(stack, noHeight));
        var noProfile = Opts(); noProfile.Profile = null;
        Assert.Throws<ArgumentNullException>(() => NonPlanarInfill.Apply(stack, noProfile));
    }

    [Fact]
    public void WallsAreBitForBitUntouchedWhenWallAmplitudeIsZero()
    {
        var stack = Stack(6);
        var before = stack.Select(l => WallOf(l).ToList()).ToList();

        NonPlanarInfill.Apply(stack, Opts());

        for (int i = 0; i < stack.Count; i++)
        {
            Assert.Equal(before[i].Count, WallOf(stack[i]).Count);
            for (int k = 0; k < before[i].Count; k++)
                Assert.Equal(before[i][k], WallOf(stack[i])[k]);
        }
    }

    [Fact]
    public void NestedWalls_UseQuarterTurnLoopPhaseAndStayExactlyClosed()
    {
        var layer = WallLayer(0.0);
        var o = WallOptions(includeOuter: true);

        NonPlanarInfill.Apply(new[] { layer }, o);

        var outer = layer.Paths[0].Points;
        var inner = layer.Paths[1].Points;
        // Output thinning drops the vertices the wave did not bend, so a given X no longer
        // survives on both loops - but a crest always does, and the loop phase is exactly
        // what moves it. Both loops reach the full amplitude and no more.
        var outerCrest = outer.MaxBy(p => p.Z);
        var innerCrest = inner.MaxBy(p => p.Z);
        Assert.Equal(o.WallAmplitude, outerCrest.Z, 9);
        Assert.Equal(o.WallAmplitude, innerCrest.Z, 9);
        // a quarter turn of loop phase moves the crest a quarter wavelength against +X,
        // modulo the period - the two squares crest at different multiples of it
        double shift = innerCrest.X - outerCrest.X + 0.25 * o.Wavelength;
        Assert.Equal(0.0, shift - o.Wavelength * Math.Round(shift / o.Wavelength), 9);
        Assert.Equal(outer[0], outer[^1]);
        Assert.Equal(inner[0], inner[^1]);
    }

    [Fact]
    public void OuterWallStaysFlatUnlessExplicitlyIncluded()
    {
        var defaultLayer = WallLayer(0.0);
        var includedLayer = WallLayer(0.0);
        var beforeOuter = defaultLayer.Paths[0].Points.ToList();

        NonPlanarInfill.Apply(new[] { defaultLayer }, WallOptions(includeOuter: false));
        NonPlanarInfill.Apply(new[] { includedLayer }, WallOptions(includeOuter: true));

        Assert.Equal(beforeOuter, defaultLayer.Paths[0].Points);
        Assert.Contains(defaultLayer.Paths[1].Points, p => Math.Abs(p.Z) > 1e-6);
        Assert.Contains(includedLayer.Paths[0].Points, p => Math.Abs(p.Z) > 1e-6);
    }

    [Fact]
    public void WallAmplitudeGuardUsesTheSameLayerGapLimitAsInfill()
    {
        // 1 mm gaps, no ramp: the limit is MaxStepFraction x gap = 0.4 mm for walls as for infill.
        var refused = Enumerable.Range(0, 3).Select(i => WallLayer(i)).ToList();
        var unsafeOptions = WallOptions(includeOuter: true);
        unsafeOptions.WallAmplitude = 0.5;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => NonPlanarInfill.Apply(refused, unsafeOptions));

        Assert.Contains("wall amplitude", ex.Message);
        Assert.All(refused[1].Paths.SelectMany(p => p.Points), p => Assert.Equal(1.0, p.Z, 12));

        var accepted = Enumerable.Range(0, 3).Select(i => WallLayer(i)).ToList();
        var safeOptions = WallOptions(includeOuter: true);
        safeOptions.WallAmplitude = 0.2;
        NonPlanarInfill.Apply(accepted, safeOptions);
        Assert.Contains(accepted[1].Paths.SelectMany(p => p.Points), p => Math.Abs(p.Z - 1.0) > 1e-6);
    }

    [Fact]
    public void WallAmplitudeGuardSeesTheBricklayerRampBoundary()
    {
        // Four 1 mm layers; the inner loop is bricklayer-shifted by 0.5 on layers 1 and 2 only
        // (flat at both ends, as Bricklayer's ramp leaves it). Between layer 0 and 1 and between
        // 2 and 3 the inner loop's real gap is 0.5 mm, so the wall limit is 0.4 x 0.5 = 0.2 mm
        // even though the level gap is 1 mm. The infill limit is unaffected.
        static List<LayerPaths> Stack()
        {
            var layers = Enumerable.Range(0, 4).Select(i => WallLayer(i)).ToList();
            foreach (int li in new[] { 1, 2 })
            {
                var inner = layers[li].Paths[1];
                inner.Points = inner.Points.Select(p => new Vec3(p.X, p.Y, p.Z + 0.5)).ToList();
                inner.ZShift = 0.5;
            }
            return layers;
        }

        var refusedOptions = WallOptions(includeOuter: false);
        refusedOptions.WallAmplitude = 0.3;
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NonPlanarInfill.Apply(Stack(), refusedOptions));
        Assert.Contains("wall amplitude", ex.Message);
        Assert.Contains("0.2", ex.Message);

        var acceptedOptions = WallOptions(includeOuter: false);
        acceptedOptions.WallAmplitude = 0.15;
        var accepted = Stack();
        NonPlanarInfill.Apply(accepted, acceptedOptions);
        Assert.Contains(accepted[1].Paths[1].Points, p => Math.Abs(p.Z - 1.5) > 1e-6);

        // the outer loop is never shifted here and not modulated either, so with the outer wall
        // excluded only the inner loop's gap counts; the infill guard still sees the full 1 mm
        var infillOptions = WallOptions(includeOuter: false);
        infillOptions.WallAmplitude = 0.0;
        infillOptions.Amplitude = 0.3;
        NonPlanarInfill.Apply(Stack(), infillOptions);
    }

    [Fact]
    public void ConicalWallModulation_EqualsModulationInTheShearedFrame()
    {
        const double cone = 0.35, cx = 3.0, cy = -2.0, radius = 12.0, level = 7.0;
        const int segments = 64;
        var sheared = new List<Vec3>();
        for (int i = 0; i <= segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            sheared.Add(new Vec3(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle), level));
        }
        double chord = PolylineOps.Dist(sheared[0], sheared[1]);
        var optionsA = WallOptions(includeOuter: false);
        optionsA.Resolution = chord;
        var optionsB = WallOptions(includeOuter: false);
        optionsB.Resolution = chord;

        var shearedLayer = LayerWithWall(sheared, level, wallIndex: 1);
        NonPlanarInfill.Apply(new[] { shearedLayer }, optionsA);
        var modulateThenMap = Conical.InverseContourRefined(
            shearedLayer.Paths[0].Points, cone, cx, cy, LayerBuilder.ConeChordTol);

        var mapped = Conical.InverseContourRefined(sheared, cone, cx, cy, LayerBuilder.ConeChordTol);
        var mappedLayer = LayerWithWall(mapped, level, wallIndex: 1);
        NonPlanarInfill.Apply(new[] { mappedLayer }, optionsB);
        var mapThenModulate = mappedLayer.Paths[0].Points;

        Assert.Equal(modulateThenMap.Count, mapThenModulate.Count);
        for (int i = 0; i < modulateThenMap.Count; i++)
        {
            Assert.Equal(modulateThenMap[i].X, mapThenModulate[i].X, 9);
            Assert.Equal(modulateThenMap[i].Y, mapThenModulate[i].Y, 9);
            Assert.Equal(modulateThenMap[i].Z, mapThenModulate[i].Z, 9);
        }
    }

    [Fact]
    public void RowIsResampledToTheResolution_AndGrowsInThreeDimensionalLength()
    {
        var stack = Stack(3, length: 100.0);
        var o = Opts(amp: 0.3, ramp: 0);
        var dense = DenseInfill(stack, 1, o);
        NonPlanarInfill.Apply(stack, o);

        var row = Infill(stack[1]);
        Assert.Equal(101, dense.Count);                                // 100 mm at 1 mm steps
        // the profile is evaluated at every one of those samples; the output then carries
        // only the ones the wave bent, which is fewer
        Assert.True(row.Count < dense.Count, $"{row.Count} is not fewer than {dense.Count}");
        Assert.Equal(100.0, PolylineOps.ContourLength(
            row.Select(p => new Vec3(p.X, p.Y, 0)).ToList()), 6);      // XY length unchanged

        // the 3-D length is the arc length of z = A sin(2*pi*x/L), computed here by dense
        // direct sampling of the same analytic curve - independent of the code under test
        double expected = 0.0;
        const int n = 200000;
        double prevX = 0, prevZ = 0;
        for (int i = 1; i <= n; i++)
        {
            double x = 100.0 * i / n;
            double z = o.Amplitude * Math.Sin(2 * Math.PI * x / o.Wavelength + 2 * Math.PI * o.PhasePerLayer);
            expected += Math.Sqrt((x - prevX) * (x - prevX) + (z - prevZ) * (z - prevZ));
            prevX = x; prevZ = z;
        }
        double actual = PolylineOps.ContourLength(row);
        Assert.True(actual > 100.0, "a modulated row must be longer than its XY shadow");
        // the polyline chords the sine at 1 mm, so it is slightly under the true arc length
        Assert.InRange(actual, expected - 0.05, expected + 1e-6);
    }

    [Fact]
    public void HalfTurnPerLayer_PutsTheNextLayersTroughOverThisLayersCrest()
    {
        var stack = Stack(5);
        var o = Opts(amp: 0.3, ramp: 0, phase: 0.5);
        NonPlanarInfill.Apply(stack, o);

        var a = Infill(stack[1]);
        var b = Infill(stack[2]);
        // the two rows are sampled identically in XY, so compare point by point
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            double da = a[i].Z - 1 * LayerH;
            double db = b[i].Z - 2 * LayerH;
            Assert.Equal(-da, db, 9);   // exactly out of phase
        }
    }

    [Theory]
    [InlineData(0, 5, 1, 0.0)]
    [InlineData(1, 5, 1, 1.0)]
    [InlineData(4, 5, 1, 0.0)]
    [InlineData(2, 5, 0, 1.0)]
    [InlineData(1, 5, 2, 0.5)]
    public void Ramp_IsZeroAtTheEndsAndOneInTheMiddle(int index, int count, int ramp, double expected)
        => Assert.Equal(expected, NonPlanarInfill.Ramp(index, count, ramp), 9);

    [Fact]
    public void LongWavelengthWalls_ThinToAFifthOfTheirSamplesOrFewer()
    {
        var layer = WallLayer(0.0);
        var o = WallOptions(includeOuter: true);
        o.Wavelength = 200.0;                  // 200 samples per period at 1 mm resolution
        var stack = new[] { layer };
        int dense = DenseWall(stack, 0, 0, o).Count + DenseWall(stack, 0, 1, o).Count;

        NonPlanarInfill.Apply(stack, o);

        int thinned = layer.Paths.Sum(p => p.Points.Count);
        Assert.True(thinned * 5 <= dense, $"{dense} -> {thinned} points is less than a 5x cut");
    }

    [Fact]
    public void LongWavelengthInfillStack_ThinsToAFifthOfItsSamplesOrFewer()
    {
        var stack = Stack(8, length: 100.0);
        var o = Opts(amp: 0.3, ramp: 0);
        o.Wavelength = 200.0;
        int dense = 0;
        for (int i = 0; i < stack.Count; i++) dense += DenseInfill(stack, i, o).Count;

        NonPlanarInfill.Apply(stack, o);

        int thinned = stack.Sum(l => Infill(l).Count);
        Assert.True(thinned * 5 <= dense, $"{dense} -> {thinned} points is less than a 5x cut");
    }

    [Fact]
    public void ShortWavelengthWalls_KeepTheVerticesTheWaveNeeds()
    {
        // Thinning is not blanket decimation: the same fixture at a tenth of the wavelength
        // bends ten times as often and keeps the samples to show it.
        var wide = WallLayer(0.0);
        var wideOptions = WallOptions(includeOuter: true);
        wideOptions.Wavelength = 200.0;
        var tight = WallLayer(0.0);
        var tightOptions = WallOptions(includeOuter: true);
        tightOptions.Wavelength = 20.0;

        NonPlanarInfill.Apply(new[] { wide }, wideOptions);
        NonPlanarInfill.Apply(new[] { tight }, tightOptions);

        int wideCount = wide.Paths.Sum(p => p.Points.Count);
        int tightCount = tight.Paths.Sum(p => p.Points.Count);
        Assert.True(tightCount > 3 * wideCount, $"20 mm kept {tightCount}, 200 mm kept {wideCount}");
    }

    [Fact]
    public void ThinnedPaths_StayWithinTheThinningToleranceInZ()
    {
        var stack = Stack(5, length: 100.0);
        var o = Opts(amp: 0.3, ramp: 0);
        o.Wavelength = 200.0;
        var denseRow = DenseInfill(stack, 2, o);
        NonPlanarInfill.Apply(stack, o);
        double rowTol = NonPlanarInfill.ThinTolerance(o.Amplitude);
        double rowDev = MaxZDeviation(denseRow, Infill(stack[2]));
        Assert.True(rowDev <= rowTol, $"row deviates {rowDev} > {rowTol}");

        var layer = WallLayer(0.0);
        var wallOptions = WallOptions(includeOuter: true);
        wallOptions.Wavelength = 200.0;
        var single = new[] { layer };
        var denseOuter = DenseWall(single, 0, 0, wallOptions);
        var denseInner = DenseWall(single, 0, 1, wallOptions);
        NonPlanarInfill.Apply(single, wallOptions);
        double wallTol = NonPlanarInfill.ThinTolerance(wallOptions.WallAmplitude);
        double outerDev = MaxZDeviation(denseOuter, layer.Paths[0].Points);
        double innerDev = MaxZDeviation(denseInner, layer.Paths[1].Points);
        Assert.True(outerDev <= wallTol, $"outer loop deviates {outerDev} > {wallTol}");
        Assert.True(innerDev <= wallTol, $"inner loop deviates {innerDev} > {wallTol}");
    }

    [Fact]
    public void ThinnedLoops_KeepTheirSeamAndExactClosure()
    {
        var layer = WallLayer(0.0);
        var o = WallOptions(includeOuter: true);
        o.Wavelength = 200.0;
        var single = new[] { layer };
        var denseOuter = DenseWall(single, 0, 0, o);
        var denseInner = DenseWall(single, 0, 1, o);

        NonPlanarInfill.Apply(single, o);

        var outer = layer.Paths[0].Points;
        var inner = layer.Paths[1].Points;
        Assert.True(outer.Count < denseOuter.Count);
        Assert.True(inner.Count < denseInner.Count);
        // Rdp.Simplify would re-start a ring at the vertex farthest from its centroid;
        // these loops still begin where the slicer seamed them
        Assert.Equal(denseOuter[0], outer[0]);
        Assert.Equal(denseInner[0], inner[0]);
        Assert.Equal(outer[0], outer[^1]);
        Assert.Equal(inner[0], inner[^1]);
        Assert.True(layer.Paths[0].Closed);
        Assert.True(layer.Paths[1].Closed);
    }

    // The modulated path before thinning: exactly what Modulate samples at o.Resolution.
    // Every fixture here runs the wave along +X, so the direction vector is (1, 0).

    static List<Vec3> DenseInfill(IList<LayerPaths> stack, int li, NonPlanarOptions o)
        => NonPlanarInfill.Samples(stack[li].Paths.First(p => p.Role == PathRole.Infill).Points,
                                   o.Amplitude * NonPlanarInfill.Ramp(li, stack.Count, o.RampLayers),
                                   o.PhasePerLayer * li, o, 1.0, 0.0, closed: false);

    static List<Vec3> DenseWall(IList<LayerPaths> stack, int li, int wallIndex, NonPlanarOptions o)
        => NonPlanarInfill.Samples(
               stack[li].Paths.First(p => p.Role == PathRole.Wall && p.WallIndex == wallIndex).Points,
               o.WallAmplitude * NonPlanarInfill.Ramp(li, stack.Count, o.RampLayers),
               o.PhasePerLayer * li + o.LoopPhaseTurns * Math.Max(0, wallIndex),
               o, 1.0, 0.0, closed: true);

    /// <summary>Largest Z gap between a dense modulated path and the thinned path that
    /// replaced it, read at every dropped sample by interpolating the thinned polyline over
    /// the XY arc length the two share. Thinning keeps a subsequence of the dense vertices
    /// and never moves one in XY, so the comparison is exact rather than a nearest-point
    /// approximation - and the subsequence itself is asserted on the way.</summary>
    static double MaxZDeviation(IReadOnlyList<Vec3> dense, IReadOnlyList<Vec3> thin)
    {
        var kept = new List<int>();
        for (int i = 0, j = 0; i < dense.Count && j < thin.Count; i++)
            if (dense[i].X == thin[j].X && dense[i].Y == thin[j].Y && dense[i].Z == thin[j].Z)
            { kept.Add(i); j++; }
        Assert.Equal(thin.Count, kept.Count);

        var cum = PolylineOps.CumulativeLengths(dense.Select(p => new Vec3(p.X, p.Y, 0)).ToList());
        double worst = 0.0;
        for (int s = 0; s + 1 < kept.Count; s++)
        {
            int a = kept[s], b = kept[s + 1];
            double span = cum[b] - cum[a];
            for (int i = a + 1; i < b; i++)
            {
                double t = span > 0.0 ? (cum[i] - cum[a]) / span : 0.0;
                double z = dense[a].Z + (dense[b].Z - dense[a].Z) * t;
                worst = Math.Max(worst, Math.Abs(dense[i].Z - z));
            }
        }
        return worst;
    }

    static NonPlanarOptions WallOptions(bool includeOuter) => new()
    {
        LayerHeight = LayerH,
        Amplitude = 0.0,
        WallAmplitude = 0.2,
        Wavelength = 8.0,
        DirectionDeg = 0.0,
        Resolution = 1.0,
        PhasePerLayer = 0.0,
        LoopPhaseTurns = 0.25,
        IncludeOuterWall = includeOuter,
        RampLayers = 0,
        Profile = new SineProfile(),
    };

    static LayerPaths WallLayer(double z)
    {
        var layer = new LayerPaths { Level = z, Index = (int)z };
        layer.Paths.Add(WallSquare(10, z, 0));
        layer.Paths.Add(WallSquare(6, z, 1));
        return layer;
    }

    static TaggedPath WallSquare(double half, double z, int wallIndex)
        => new()
        {
            Points = new List<Vec3>
            {
                new(-half, -half, z), new(half, -half, z), new(half, half, z),
                new(-half, half, z), new(-half, -half, z),
            },
            Role = PathRole.Wall,
            WallIndex = wallIndex,
            Closed = true,
        };

    static LayerPaths LayerWithWall(List<Vec3> points, double level, int wallIndex)
    {
        var layer = new LayerPaths { Level = level };
        layer.Paths.Add(new TaggedPath
        {
            Points = points.ToList(),
            Role = PathRole.Wall,
            WallIndex = wallIndex,
            Closed = true,
        });
        return layer;
    }
}
