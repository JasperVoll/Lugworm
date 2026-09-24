using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Bounded vertex count on offset rings (D2).
///
/// Clipper's round joins tessellate every convex corner at the working precision
/// (4 == 0.1 um) and an iterated inward offset INHERITS the previous ring's vertices
/// on top of the fresh arc steps, so a hole side compounds: on this annulus the
/// hole-side rings measured 81 -> 481 -> 1117 -> 2090 vertices over three iterations
/// while the outer-side rings stayed at 81. Thinning each ring with Rdp at
/// <see cref="Offset.SimplifyTolerance"/> holds all four at 81.
///
/// Feeding an arc tolerance into Clipper's own offsetter does NOT fix this: one arc
/// step per convex corner is the floor, so the counts only fall to 81 -> 161 -> 321
/// -> 641 (exactly 2n-1 per iteration, and identical for arc tolerances of 0.015,
/// 0.02 and 0.05 mm) - still geometric growth, because inherited vertices are never
/// removed. That is why the thinning lives on our side of the Clipper call.</summary>
public class OffsetTests
{
    const double Spacing = 3.0;

    static List<Vec3> Circle(double r, int segments, bool ccw)
    {
        var ring = new List<Vec3>(segments + 1);
        for (int k = 0; k <= segments; k++)
        {
            double a = 2 * Math.PI * k / segments;
            if (!ccw) a = -a;
            ring.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), 0));
        }
        return ring;
    }

    /// <summary>r=40 outer, r=20 hole, both sampled at 80 segments.</summary>
    static List<IReadOnlyList<Vec3>> Annulus()
        => new List<IReadOnlyList<Vec3>> { Circle(40, 80, true), Circle(20, 80, false) };

    static double Length(IReadOnlyList<Vec3> ring)
    {
        double s = 0;
        for (int i = 1; i < ring.Count; i++) s += PolylineOps.Dist2D(ring[i - 1], ring[i]);
        return s;
    }

    static double MeanRadius(IReadOnlyList<Vec3> ring)
    {
        int n = ring.Count - 1;
        double cx = 0, cy = 0;
        for (int i = 0; i < n; i++) { cx += ring[i].X; cy += ring[i].Y; }
        cx /= n; cy /= n;
        double s = 0;
        for (int i = 0; i < n; i++)
            s += Math.Sqrt((ring[i].X - cx) * (ring[i].X - cx) + (ring[i].Y - cy) * (ring[i].Y - cy));
        return s / n;
    }

    /// <summary>Worst distance from any vertex of <paramref name="from"/> to the
    /// polyline <paramref name="to"/> - one direction of the Hausdorff distance.</summary>
    static double MaxDeviation(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to)
    {
        double worst = 0;
        foreach (var p in from)
        {
            double best = double.MaxValue;
            for (int i = 1; i < to.Count; i++)
                best = Math.Min(best, PolylineOps.PointSegDist(p, to[i - 1], to[i]));
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    [Fact]
    public void SimplifyTolerance_IsTwoOrdersBelowALayer_AndCappedAt20Micron()
    {
        Assert.Equal(0.015, Offset.SimplifyTolerance(3.0), 12);
        Assert.Equal(0.015, Offset.SimplifyTolerance(-3.0), 12);   // sign-independent
        Assert.Equal(0.02, Offset.SimplifyTolerance(50.0), 12);    // the cap bites on big deltas
        Assert.Equal(0.0, Offset.SimplifyTolerance(0.0), 12);
    }

    [Fact]
    public void Annulus_EveryRingStaysBelowTheTolerancesVertexBudget()
    {
        double tol = Offset.SimplifyTolerance(Spacing);           // 0.015 mm
        var rings = Offset.ConcentricRings(Annulus(), Spacing, 0.0);
        Assert.True(rings.Count >= 8, $"expected the annulus to close after 3 iterations, got {rings.Count} rings");

        foreach (var ring in rings)
        {
            double len = Length(ring);
            Assert.True(ring.Count < 2 * len / tol,
                $"ring of length {len:0.00} mm carries {ring.Count} vertices, over the 2*len/tol budget of {2 * len / tol:0}");

            // The bound that actually bites: a ring of mean radius R thinned at
            // tolerance t needs about 2*pi*sqrt(R/(8t)) vertices to stay within t of
            // the circle, and these rings are circles. The raw Clipper rings exceed
            // this by up to 21x.
            double warranted = 2 * Math.PI * Math.Sqrt(MeanRadius(ring) / (8 * tol));
            Assert.True(ring.Count <= 1.25 * warranted,
                $"ring at R={MeanRadius(ring):0.00} carries {ring.Count} vertices, tolerance warrants ~{warranted:0}");
        }
    }

    [Fact]
    public void Annulus_HoleSideVertexCountDoesNotCompound()
    {
        var rings = Offset.ConcentricRings(Annulus(), Spacing, 0.0);
        // hole side: the rings that grew inward from r=20 (mean radius under 30)
        var holeSide = rings.Where(r => MeanRadius(r) < 30.0).ToList();
        Assert.Equal(4, holeSide.Count);

        // Before the thinning these were 81, 481, 1117, 2090 - every iteration
        // roughly doubled. Nothing may grow by more than half again now.
        for (int i = 1; i < holeSide.Count; i++)
            Assert.True(holeSide[i].Count <= 1.5 * holeSide[i - 1].Count,
                $"hole-side ring {i} has {holeSide[i].Count} vertices after {holeSide[i - 1].Count}: " +
                $"counts are compounding ({string.Join(", ", holeSide.Select(r => r.Count))})");
    }

    [Fact]
    public void Annulus_ThinnedRingsStayWithinToleranceOfTheRawClipperRings()
    {
        double tol = Offset.SimplifyTolerance(Spacing);
        var current = Nesting.Orient(Annulus());

        for (int step = 0; step < 3; step++)
        {
            var source = current.Cast<IReadOnlyList<Vec3>>().ToList();
            var raw = Offset.Polygons(source, -Spacing, 0.0, 0.0);      // simplifyTol 0 == Clipper's own ring
            var thin = Offset.Polygons(source, -Spacing, 0.0);          // derived tolerance
            raw.RemoveAll(r => Math.Abs(PolylineOps.SignedAreaXy(r)) <= Spacing * Spacing);
            thin.RemoveAll(r => Math.Abs(PolylineOps.SignedAreaXy(r)) <= Spacing * Spacing);
            Assert.Equal(raw.Count, thin.Count);

            for (int i = 0; i < thin.Count; i++)
            {
                int j = raw.FindIndex(r => Math.Abs(MeanRadius(r) - MeanRadius(thin[i])) < 0.5);
                Assert.True(j >= 0, $"step {step}: no raw ring matches the thinned ring at R={MeanRadius(thin[i]):0.00}");
                // both directions: no thinned vertex moved off the raw ring, and no
                // stretch of the raw ring was cut away by more than the tolerance
                Assert.True(MaxDeviation(thin[i], raw[j]) < tol,
                    $"step {step} ring {i}: thinned vertex {MaxDeviation(thin[i], raw[j]):0.000000} mm off the raw ring");
                Assert.True(MaxDeviation(raw[j], thin[i]) < tol,
                    $"step {step} ring {i}: raw ring departs {MaxDeviation(raw[j], thin[i]):0.000000} mm from the thinned ring");
            }
            current = thin;
        }
    }

    [Fact]
    public void Polygons_SimplifyTolZero_KeepsClippersRawRing()
    {
        var square = new List<IReadOnlyList<Vec3>>
        {
            new List<Vec3> { new(0,0,0), new(20,0,0), new(20,20,0), new(0,20,0), new(0,0,0) }
        };
        var raw = Offset.Polygons(square, -2.0, 0.0, 0.0);
        var thin = Offset.Polygons(square, -2.0, 0.0);
        Assert.Single(raw);
        Assert.Single(thin);
        // an inward offset of a convex square rounds nothing, so both are the square
        Assert.Equal(raw[0].Count, thin[0].Count);
        Assert.True(MaxDeviation(thin[0], raw[0]) < 1e-9);
    }

    [Fact]
    public void Polygons_OutwardOffsetOfASquare_RoundsCornersButStaysBounded()
    {
        double delta = 4.0;
        double tol = Offset.SimplifyTolerance(delta);
        var square = new List<IReadOnlyList<Vec3>>
        {
            new List<Vec3> { new(0,0,0), new(20,0,0), new(20,20,0), new(0,20,0), new(0,0,0) }
        };
        var raw = Offset.Polygons(square, delta, 0.0, 0.0);
        var thin = Offset.Polygons(square, delta, 0.0);
        Assert.Single(raw);
        Assert.Single(thin);
        Assert.True(thin[0].Count < raw[0].Count / 4,
            $"four rounded corners thinned to {thin[0].Count} vertices from {raw[0].Count}");
        Assert.True(MaxDeviation(thin[0], raw[0]) < tol);
        Assert.True(MaxDeviation(raw[0], thin[0]) < tol);
    }
}
