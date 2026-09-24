using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class BricklayerTests
{
    const double LayerW = 2.0;
    const double LayerH = 1.0;

    static TaggedPath Wall(int wallIndex, params (double x, double y)[] pts)
        => new TaggedPath
        {
            Points = pts.Select(p => new Vec3(p.x, p.y, 0.0)).ToList(),
            Role = PathRole.Wall,
            WallIndex = wallIndex,
        };

    /// <summary>An axis-aligned square ring of the given half-width, closed.</summary>
    static TaggedPath Ring(double half, int wallIndex)
        => Wall(wallIndex, (-half, -half), (half, -half), (half, half), (-half, half), (-half, -half));

    static List<LayerPaths> Stack(int layers, Func<int, IEnumerable<TaggedPath>> build)
    {
        var stack = new List<LayerPaths>();
        for (int i = 0; i < layers; i++)
        {
            var lp = new LayerPaths { Level = i * LayerH, Index = i };
            foreach (var p in build(i))
            {
                // every layer sits at its own level
                p.Points = p.Points.Select(q => new Vec3(q.X, q.Y, i * LayerH)).ToList();
                lp.Paths.Add(p);
            }
            stack.Add(lp);
        }
        return stack;
    }

    static BricklayerOptions Opts(double overlap = 0.5, int ramp = 0, bool outer = false)
        => new BricklayerOptions
        {
            LayerHeight = LayerH,
            LayerWidth = LayerW,
            ShiftFraction = 0.5,
            OverlapFraction = overlap,
            ShiftOuter = outer,
            RampLayers = ramp,
        };

    static double ZOf(TaggedPath p) => p.Points[0].Z;

    [Fact]
    public void ConcentricRings_OneLayerApart_InnerRises_OuterStaysFlat()
    {
        // outer half-width 10, inner 8: every point of the inner ring is exactly one
        // layer from the outer ring, corners included
        var stack = Stack(1, _ => new[] { Ring(10, 0), Ring(8, 1) });
        var report = Bricklayer.Apply(stack, Opts());

        Assert.Equal(1, report.PathsShifted);
        Assert.Equal(0.0, ZOf(stack[0].Paths[0]), 9);              // outer flat
        Assert.Equal(0.5 * LayerH, ZOf(stack[0].Paths[1]), 9);     // inner raised
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void ConcentricRings_ThreeLayersApart_NeitherShifts()
    {
        var stack = Stack(1, _ => new[] { Ring(10, 0), Ring(4, 1) });   // 6 mm apart, layer 2
        var report = Bricklayer.Apply(stack, Opts());

        Assert.Equal(0, report.PathsShifted);
        Assert.All(stack[0].Paths, p => Assert.Equal(0.0, ZOf(p), 9));
    }

    /// <summary>The overlap gate, both sides of 0.5. Loop B is 100 mm long; `alongside`
    /// mm of it run one layer from the 110 mm loop A and the rest peels away.</summary>
    [Theory]
    [InlineData(40.0, false)]
    [InlineData(60.0, true)]
    public void OverlapGate_DecidesAtHalfTheShorterLoop(double alongside, bool expectShift)
    {
        var a = Wall(0, (0, 0), (110, 0));
        var b = Wall(1, (0, LayerW), (alongside, LayerW), (alongside, LayerW + (100.0 - alongside)));
        var stack = Stack(1, _ => new[] { a, b });

        var report = Bricklayer.Apply(stack, Opts());

        Assert.Equal(expectShift ? 1 : 0, report.PathsShifted);
        Assert.Equal(expectShift ? 0.5 * LayerH : 0.0, ZOf(stack[0].Paths[1]), 9);
    }

    [Fact]
    public void ThreeMutuallyAdjacentLoops_ShiftNothing_AndAreReported()
    {
        // layer 4: the three lines are 2, 2 and 4 mm apart, so all three pairs pass both
        // gates and the adjacency graph is a triangle - an odd cycle
        var stack = Stack(1, _ => new[]
        {
            Wall(0, (0, 0), (100, 0)),
            Wall(1, (0, 2), (100, 2)),
            Wall(2, (0, 4), (100, 4)),
        });
        var o = Opts();
        o.LayerWidth = 4.0;

        var report = Bricklayer.Apply(stack, o);

        Assert.Equal(0, report.PathsShifted);
        Assert.All(stack[0].Paths, p => Assert.Equal(0.0, ZOf(p), 9));
        Assert.Contains(report.Notes, n => n.Contains("non-bipartite"));
    }

    [Fact]
    public void FirstAndLastLayer_StayFlat_WithOneRampLayer()
    {
        var stack = Stack(4, _ => new[] { Ring(10, 0), Ring(8, 1) });
        var report = Bricklayer.Apply(stack, Opts(ramp: 1));

        Assert.Equal(2, report.LayersShifted);          // layers 1 and 2 only
        Assert.Equal(0 * LayerH, ZOf(stack[0].Paths[1]), 9);
        Assert.Equal(1 * LayerH + 0.5 * LayerH, ZOf(stack[1].Paths[1]), 9);
        Assert.Equal(2 * LayerH + 0.5 * LayerH, ZOf(stack[2].Paths[1]), 9);
        Assert.Equal(3 * LayerH, ZOf(stack[3].Paths[1]), 9);
    }

    [Fact]
    public void ShiftOuter_FlipsWhichLoopRises()
    {
        var stack = Stack(1, _ => new[] { Ring(10, 0), Ring(8, 1) });
        Bricklayer.Apply(stack, Opts(outer: true));

        Assert.Equal(0.5 * LayerH, ZOf(stack[0].Paths[0]), 9);   // outer raised instead
        Assert.Equal(0.0, ZOf(stack[0].Paths[1]), 9);
    }

    [Fact]
    public void ZeroShift_IsANoOp()
    {
        var stack = Stack(3, _ => new[] { Ring(10, 0), Ring(8, 1) });
        var o = Opts();
        o.ShiftFraction = 0.0;

        var report = Bricklayer.Apply(stack, o);

        Assert.Equal(0, report.PathsShifted);
        Assert.Equal(0, report.LayersProcessed);
        for (int i = 0; i < 3; i++)
            Assert.All(stack[i].Paths, p => Assert.Equal(i * LayerH, ZOf(p), 9));
    }

    [Fact]
    public void InfillAndOpenSections_AreNeverTouched()
    {
        var stack = Stack(1, _ => new[]
        {
            Ring(10, 0),
            Ring(8, 1),
            new TaggedPath { Points = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0) }, Role = PathRole.Infill, WallIndex = -1 },
            new TaggedPath { Points = new List<Vec3> { new Vec3(0, 1, 0), new Vec3(1, 1, 0) }, Role = PathRole.OpenSection, WallIndex = -1 },
        });
        Bricklayer.Apply(stack, Opts());

        Assert.Equal(0.0, ZOf(stack[0].Paths[2]), 9);
        Assert.Equal(0.0, ZOf(stack[0].Paths[3]), 9);
    }

    [Fact]
    public void BadOptions_Throw()
    {
        var stack = Stack(1, _ => new[] { Ring(10, 0) });
        Assert.Throws<ArgumentOutOfRangeException>(() => Bricklayer.Apply(stack, new BricklayerOptions { LayerHeight = 0, LayerWidth = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bricklayer.Apply(stack, new BricklayerOptions { LayerHeight = 1, LayerWidth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bricklayer.Apply(stack, new BricklayerOptions { LayerHeight = 1, LayerWidth = 1, OverlapFraction = 1.0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bricklayer.Apply(stack, new BricklayerOptions { LayerHeight = 1, LayerWidth = 1, ShiftFraction = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Bricklayer.Apply(stack, new BricklayerOptions { LayerHeight = 1, LayerWidth = 1, ShiftFraction = double.PositiveInfinity }));
    }

    [Fact]
    public void ConicalWalls_UseXyAdjacency_AndInnerWallRises()
    {
        const double angle = Math.PI / 6.0;
        TaggedPath ConeRing(double radius, int wallIndex)
        {
            var points = new List<Vec3>();
            for (int i = 0; i <= 64; i++)
            {
                double t = 2.0 * Math.PI * i / 64.0;
                points.Add(new Vec3(radius * Math.Cos(t), radius * Math.Sin(t),
                                    -Math.Tan(angle) * radius));
            }
            return new TaggedPath { Points = points, Role = PathRole.Wall, WallIndex = wallIndex, Closed = true };
        }

        var layer = new LayerPaths { Level = 0.0, Index = 0 };
        layer.Paths.Add(ConeRing(10.0, 0));
        layer.Paths.Add(ConeRing(8.0, 1));

        var report = Bricklayer.Apply(new[] { layer }, Opts());

        Assert.Equal(1, report.PathsShifted);
    }

    [Fact]
    public void AdaptiveLevels_UseTheUpwardLocalGapForShift()
    {
        var stack = Stack(3, _ => new[] { Ring(10, 0), Ring(8, 1) });
        double[] levels = { 0.0, 0.25, 1.25 };
        for (int i = 0; i < stack.Count; i++)
        {
            double delta = levels[i] - stack[i].Level;
            stack[i].Level = levels[i];
            foreach (var path in stack[i].Paths)
                path.Points = path.Points.Select(p => new Vec3(p.X, p.Y, p.Z + delta)).ToList();
        }

        Bricklayer.Apply(stack, Opts());

        Assert.Equal(0.125, ZOf(stack[0].Paths[1]), 9);
        Assert.Equal(0.75, ZOf(stack[1].Paths[1]), 9);
        Assert.Equal(1.75, ZOf(stack[2].Paths[1]), 9); // last layer uses the previous gap
    }

    /// <summary>The claim in <see cref="Bricklayer"/>'s own documentation: because the
    /// inverse conical map subtracts a function of the radius only and leaves XY alone,
    /// raising a contour in the sheared frame and raising it after the map give the same
    /// points. That is why the pass runs uniformly for both slice modes.</summary>
    [Fact]
    public void ConicalShift_EqualsShearedFrameShift()
    {
        const double cone = 0.35, cx = 3.0, cy = -2.0, dz = 0.5;
        var sheared = new List<Vec3>();
        for (int i = 0; i <= 24; i++)
        {
            double t = i / 24.0 * 2 * Math.PI;
            sheared.Add(new Vec3(cx + 30 * Math.Cos(t), cy + 18 * Math.Sin(t), 7.0));
        }
        var raisedFirst = sheared.Select(p => new Vec3(p.X, p.Y, p.Z + dz)).ToList();

        var mapThenRaise = Conical.InverseContourRefined(sheared, cone, cx, cy, LayerBuilder.ConeChordTol)
                                  .Select(p => new Vec3(p.X, p.Y, p.Z + dz)).ToList();
        var raiseThenMap = Conical.InverseContourRefined(raisedFirst, cone, cx, cy, LayerBuilder.ConeChordTol);

        Assert.Equal(mapThenRaise.Count, raiseThenMap.Count);
        for (int i = 0; i < mapThenRaise.Count; i++)
        {
            Assert.Equal(mapThenRaise[i].X, raiseThenMap[i].X, 9);
            Assert.Equal(mapThenRaise[i].Y, raiseThenMap[i].Y, 9);
            Assert.Equal(mapThenRaise[i].Z, raiseThenMap[i].Z, 9);
        }
    }

    // ------------------------------------------------ round parts (4.4.0, D10)

    /// <summary>A regular polygon ring at `z` - what a round part's section actually is
    /// once the slicer has cut it, as against the exact rectangles above.</summary>
    static List<Vec3> Polygon(double radius, int sides, double z)
    {
        var pts = new List<Vec3>(sides + 1);
        for (int i = 0; i < sides; i++)
        {
            double t = 2.0 * Math.PI * i / sides;
            pts.Add(new Vec3(radius * Math.Cos(t), radius * Math.Sin(t), z));
        }
        pts.Add(pts[0]);
        return pts;
    }

    /// <summary>A round part's wall stack built the way the pipeline builds it - real
    /// Clipper offsets through <see cref="LayerBuilder"/>, not rings hand-placed at exact
    /// distances. A non-zero `coneRad` slices it the conical way.</summary>
    static List<LayerPaths> RoundStack(int layers, double radius, double spacing,
                                       int wallCount, double coneRad = 0.0, int sides = 48)
    {
        var stack = new List<LayerPaths>();
        for (int i = 0; i < layers; i++)
        {
            double z = i * LayerH;
            var perims = new List<IReadOnlyList<Vec3>> { Polygon(radius, sides, z) };
            var layer = LayerBuilder.BuildConicalTagged(perims, new LayerBuildOptions
            {
                Level = z,
                WallCount = wallCount,
                WallSpacing = spacing,
                Infill = 0,
                ConeAngleRad = coneRad,
            });
            layer.Index = i;
            stack.Add(layer);
        }
        return stack;
    }

    /// <summary>Largest XY distance from wall loop `inner`'s vertices to wall loop
    /// `outer` - the quantity the adjacency distance gate is compared against.</summary>
    static double WallGap(LayerPaths layer, int outer, int inner)
    {
        var a = layer.Paths[outer].Points;
        var b = layer.Paths[inner].Points;
        double worst = 0.0;
        foreach (var q in b)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < a.Count; i++)
            {
                double d = PolylineOps.PointSegDist(new Vec3(q.X, q.Y, 0.0),
                                                    new Vec3(a[i].X, a[i].Y, 0.0),
                                                    new Vec3(a[i + 1].X, a[i + 1].Y, 0.0));
                if (d < best) best = d;
            }
            if (best > worst) worst = best;
        }
        return worst;
    }

    /// <summary>D10. A multi-wall round part used to report "0 wall loop(s) raised on
    /// 0/8 eligible layers", because Clipper's inward offset lands the measured wall gap
    /// a hair OUTSIDE one layer width while the distance gate's epsilon was relative
    /// (1e-9), so no two loops on a curved wall ever counted as adjacent. The rectangle
    /// fixtures above never caught it: their offsets are exact.</summary>
    [Fact]
    public void CylinderStack_ShiftsEveryOtherWall_OnEveryEligibleLayer()
    {
        const double spacing = 3.0;
        var stack = RoundStack(6, 20.0, spacing, 3);
        double gap = WallGap(stack[0], 0, 1);

        var o = Opts(ramp: 1);
        o.LayerWidth = spacing;
        var report = Bricklayer.Apply(stack, o);

        // the finding itself: the offset does NOT land at exactly one layer, so a gate of
        // exactly LayerWidth can never fire here - and the tolerance does cover it
        Assert.True(gap > spacing, $"wall gap {gap:0.#########} is not outside the layer width");
        Assert.True(gap < Bricklayer.AdjacencyGate(spacing),
                    $"wall gap {gap:0.#########} is outside the widened gate");

        // three walls chain outer-middle-inner, so exactly the middle one rises, on each
        // of the four layers between the ramps
        Assert.Equal(4, report.LayersShifted);
        Assert.Equal(4, report.PathsShifted);
        Assert.Empty(report.Notes);
        for (int i = 1; i <= 4; i++)
        {
            Assert.Equal(0.0, stack[i].Paths[0].ZShift, 9);
            Assert.Equal(0.5 * LayerH, stack[i].Paths[1].ZShift, 9);
            Assert.Equal(0.0, stack[i].Paths[2].ZShift, 9);
        }
    }

    /// <summary>The thinning `Offset.Polygons` applies to every ring moves a finely
    /// tessellated part's gap two orders further out than the 0.1 um grid quantum does
    /// (1.3e-2 mm on a 256-gon at spacing 6), which is why the tolerance is relative to
    /// the layer width rather than a fixed epsilon.</summary>
    [Theory]
    [InlineData(48, 3.0)]
    [InlineData(48, 6.0)]
    [InlineData(256, 3.0)]
    [InlineData(256, 6.0)]
    public void OffsetGap_StaysInsideTheAdjacencyTolerance(int sides, double spacing)
    {
        var stack = RoundStack(1, 20.0, spacing, 3, 0.0, sides);
        double gap = WallGap(stack[0], 0, 1);

        Assert.True(gap > spacing, $"wall gap {gap:0.#########} is not outside the layer width");
        Assert.True(gap < Bricklayer.AdjacencyGate(spacing),
                    $"wall gap {gap:0.#########} is outside the widened gate");
    }

    /// <summary>The same on a conically sliced stack, where the inverse map leaves each
    /// loop at its own Z: adjacency is an XY question, so the shift must happen exactly
    /// as it does on the planar stack.</summary>
    [Fact]
    public void ConicalStack_ShiftsEveryOtherWall()
    {
        const double spacing = 3.0;
        var stack = RoundStack(6, 20.0, spacing, 3, coneRad: 20.0 * Math.PI / 180.0);

        var o = Opts(ramp: 1);
        o.LayerWidth = spacing;
        var report = Bricklayer.Apply(stack, o);

        Assert.Equal(4, report.LayersShifted);
        Assert.Equal(4, report.PathsShifted);
        Assert.Empty(report.Notes);
        // the stack really is conical: one layer's three loops sit at three heights
        var mid = stack[2];
        Assert.True(Math.Abs(mid.Paths[0].Points[0].Z - mid.Paths[2].Points[0].Z) > 0.5,
                    "the conical layer's loops are not at different heights");
    }

    /// <summary>The negative control for the widened gate: one and a half layer widths
    /// apart is still not adjacent, so the tolerance did not turn the distance gate off.</summary>
    [Fact]
    public void RoundLoops_OneAndAHalfLayersApart_AreStillNotAdjacent()
    {
        const double spacing = 3.0;
        var stack = RoundStack(3, 20.0, 1.5 * spacing, 2);
        var o = Opts();
        o.LayerWidth = spacing;

        var report = Bricklayer.Apply(stack, o);

        Assert.Equal(0, report.PathsShifted);
        Assert.Equal(0, report.LayersShifted);
    }
}
