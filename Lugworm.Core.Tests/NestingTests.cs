using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Hole-orientation fix (refinement note P4 of 2026-07): outers CCW / holes CW, inward
/// offsets must GROW holes, infill must stay out of hole interiors.</summary>
public class NestingTests
{
    /// <summary>CCW circle (closed: last == first).</summary>
    static List<Vec3> Circle(double cx, double cy, double r, int segs = 64, bool ccw = true)
    {
        var pts = new List<Vec3>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            double a = 2 * Math.PI * i / segs * (ccw ? 1 : -1);
            pts.Add(new Vec3(cx + r * Math.Cos(a), cy + r * Math.Sin(a), 0));
        }
        return pts;
    }

    static List<Vec3> Square(double half, bool ccw = true)
    {
        var pts = new List<Vec3>
        {
            new(-half, -half, 0), new(half, -half, 0), new(half, half, 0), new(-half, half, 0), new(-half, -half, 0),
        };
        if (!ccw) pts.Reverse();
        return pts;
    }

    // ---- orientation by nesting depth ----

    [Fact]
    public void Orient_DonutBothInputOrientations_OuterCcwHoleCw()
    {
        foreach (var holeInputCcw in new[] { true, false })
        {
            var rings = new List<IReadOnlyList<Vec3>> { Square(40), Circle(0, 0, 10, 64, holeInputCcw) };
            var oriented = Nesting.Orient(rings);
            Assert.Equal(2, oriented.Count);
            Assert.True(PolylineOps.SignedAreaXy(oriented[0]) > 0, "outer must be CCW");
            Assert.True(PolylineOps.SignedAreaXy(oriented[1]) < 0, "hole must be CW");
        }
    }

    [Fact]
    public void Orient_IslandInHole_ThreeDepths()
    {
        var rings = new List<IReadOnlyList<Vec3>>
        {
            Square(40),                       // depth 0 -> CCW
            Circle(0, 0, 20),                 // depth 1 (hole) -> CW
            Circle(0, 0, 8),                  // depth 2 (island) -> CCW
        };
        var oriented = Nesting.Orient(rings);
        Assert.True(PolylineOps.SignedAreaXy(oriented[0]) > 0);
        Assert.True(PolylineOps.SignedAreaXy(oriented[1]) < 0);
        Assert.True(PolylineOps.SignedAreaXy(oriented[2]) > 0);
    }

    [Fact]
    public void Orient_SingleRing_MatchesOldEnsureCcw()
    {
        var cw = Square(20, ccw: false);
        var oriented = Nesting.Orient(new List<IReadOnlyList<Vec3>> { cw });
        Assert.True(PolylineOps.SignedAreaXy(oriented[0]) > 0);   // parity with old behaviour
    }

    // ---- inward offset: hole must GROW, outer must SHRINK ----

    [Fact]
    public void InwardOffset_Donut_HoleGrowsOuterShrinks()
    {
        var oriented = Nesting.Orient(new List<IReadOnlyList<Vec3>>
        {
            Square(40), Circle(0, 0, 10),
        });
        var off = Offset.Polygons(oriented.Select(r => (IReadOnlyList<Vec3>)r).ToList(), -2.0, 0.0);
        Assert.Equal(2, off.Count);

        var outer = off.Single(r => PolylineOps.SignedAreaXy(r) > 0);
        var hole = off.Single(r => PolylineOps.SignedAreaXy(r) < 0);

        // outer 80x80 square -2 -> ~76x76: |x|,|y| <= 38
        foreach (var p in outer) Assert.True(Math.Abs(p.X) <= 38.01 && Math.Abs(p.Y) <= 38.01);
        // hole r=10 must GROW to ~12 (was: shrank to 8 with the old forced-CCW bug)
        foreach (var p in hole)
        {
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.InRange(r, 11.8, 12.2);
        }
    }

    // ---- infill: hole interiors must stay empty ----

    [Fact]
    public void ClipLines_Donut_SpansAvoidHole()
    {
        var oriented = Nesting.Orient(new List<IReadOnlyList<Vec3>>
        {
            Square(40), Circle(0, 0, 10),
        });
        var polys = oriented.Select(r => (IReadOnlyList<Vec3>)r).ToList();
        // horizontal line through the middle: must split into two spans, gap = hole
        var spans = Infill.ClipLines(new[] { (new Vec3(-50, 0, 0), new Vec3(50, 0, 0)) }, polys);
        Assert.Equal(2, spans.Count);
        foreach (var (a, b) in spans)
            foreach (var p in new[] { a, b })
                Assert.True(Math.Abs(p.X) >= 9.9 || Math.Abs(p.X) >= 39.9,
                    $"span endpoint x={p.X:0.00} lies inside the hole");
        // with the OLD bug this was one single span straight through the hole
    }

    [Fact]
    public void Zigzag_Donut_NoPointInsideHole()
    {
        var oriented = Nesting.Orient(new List<IReadOnlyList<Vec3>>
        {
            Square(40), Circle(0, 0, 10),
        });
        var polys = oriented.Select(r => (IReadOnlyList<Vec3>)r).ToList();
        var zig = Infill.Zigzag(polys, 4.0, 0.0, 0.0);
        Assert.True(zig.Count > 10);
        // span ENDPOINTS must never sit inside the hole (the connector jumps between
        // spans are a separate known limitation, see audit)
        foreach (var p in zig)
        {
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            Assert.True(r >= 9.9, $"zigzag endpoint at r={r:0.00} inside the hole");
        }
    }

    // ---- concentric rings stay inside the material band ----

    [Fact]
    public void ConcentricRings_Donut_StayBetweenHoleAndOuter()
    {
        var rings = Offset.ConcentricRings(new List<IReadOnlyList<Vec3>>
        {
            Circle(0, 0, 40, 128), Circle(0, 0, 10, 64),
        }, 4.0, 0.0);
        Assert.True(rings.Count >= 4);
        foreach (var ring in rings)
            foreach (var p in ring)
            {
                double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
                Assert.InRange(r, 9.9, 40.1);   // never inside the hole, never outside
            }
    }
}
