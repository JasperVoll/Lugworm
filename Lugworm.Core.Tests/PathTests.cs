using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class PathTests
{
    static List<List<Vec3>> SquareRings()
    {
        var sq = Resample.ByLength(
            new List<Vec3> { new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0), new(0, 0, 0) }, 1.0);
        return Offset.ConcentricRings(new[] { sq }, 2.0, 0.0);
    }

    static double MaxEdge(List<List<Vec3>> rings)
        => rings.SelectMany(r => Enumerable.Range(0, r.Count - 1)
                .Select(i => PolylineOps.Dist2D(r[i], r[i + 1]))).Max();

    [Fact]
    public void ConnectedSpiral_IsSinglePath_BoundedHandoffs()
    {
        var rings = SquareRings();
        double bound = MaxEdge(rings) + 3 * 2.0;
        var sp = Spiral.Connected(rings);
        Assert.True(sp.Count > 10);
        double maxJump = Enumerable.Range(0, sp.Count - 1).Max(i => PolylineOps.Dist2D(sp[i], sp[i + 1]));
        Assert.True(maxJump <= bound);
    }

    [Fact]
    public void Fermat_SinglePath_StartNearEnd()
    {
        var rings = SquareRings();
        double bound = MaxEdge(rings) + 4 * Math.Sqrt(2) * 2.0 + 1.5;
        var fm = Fermat.Spiral(rings, 2.0);
        Assert.True(fm.Count > 10);
        double maxJump = Enumerable.Range(0, fm.Count - 1).Max(i => PolylineOps.Dist2D(fm[i], fm[i + 1]));
        Assert.True(maxJump <= bound, $"max jump {maxJump} > {bound}");
        Assert.True(PolylineOps.Dist2D(fm[0], fm[^1]) < 8 * 2.0);
    }

    static List<IReadOnlyList<Vec3>> CircleRings(int count, double r0, double spacing, double z = 0.0)
    {
        var rings = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < count; i++)
        {
            double r = r0 - i * spacing;
            var ring = new List<Vec3>();
            for (int k = 0; k <= 90; k++)
            {
                double a = 2 * Math.PI * k / 90;
                ring.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
            }
            rings.Add(ring);
        }
        return rings;
    }

    static double MaxJump(List<Vec3> p)
        => Enumerable.Range(0, p.Count - 1).Max(i => PolylineOps.Dist2D(p[i], p[i + 1]));

    [Fact]
    public void SpiralInward_OnePath_RadiusDecreasesMonotonically_StartsAtAnchor()
    {
        double spacing = 3.0;
        var rings = CircleRings(6, 30, spacing, 1.0);
        var anchor = new Vec3(33, 0, 1.0);                       // "wall seam" just outside, on +X
        var paths = Spiral.InwardPaths(rings, anchor);
        Assert.Single(paths);
        var sp = paths[0];
        Assert.True(sp.Count > 300);
        Assert.True(PolylineOps.Dist2D(sp[0], anchor) < spacing + 0.5, $"start {sp[0].X:0.0},{sp[0].Y:0.0} not at the anchor");
        // a true spiral: the radius never grows along the path (small tolerance for chord sag)
        double prev = double.MaxValue, minR = double.MaxValue;
        foreach (var pt in sp)
        {
            double rr = Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
            Assert.True(rr <= prev + 0.05, $"radius grew from {prev:0.00} to {rr:0.00}");
            prev = rr; minR = Math.Min(minR, rr);
        }
        Assert.True(minR < 15.5 && minR > 14.5);                  // reaches the innermost ring (r=15)
        // continuous: no hop longer than one ring edge + one spacing
        Assert.True(MaxJump(sp) < 3.0, $"jump {MaxJump(sp):0.00}");
        Assert.All(sp, pt => Assert.Equal(1.0, pt.Z, 9));
    }

    [Fact]
    public void SpiralInward_TwoIslands_FallsBackToSeparateRings()
    {
        var left = CircleRings(2, 10, 3)[0];
        var right = new List<Vec3>();
        foreach (var p in CircleRings(1, 10, 3)[0]) right.Add(new Vec3(p.X + 40, p.Y, 0));
        var paths = Spiral.InwardPaths(new List<IReadOnlyList<Vec3>> { left, right }, null,
                                       out string? diagnostic);
        Assert.Equal(2, paths.Count);                             // never extrudes across the gap
        Assert.Equal("rings not strictly nested (2 islands at depth 0) - independent rings", diagnostic);
    }

    [Fact]
    public void FermatPaths_OnePath_SeamAtAnchor()
    {
        double spacing = 3.0;
        var rings = CircleRings(6, 30, spacing, 1.0);
        var anchor = new Vec3(0, 33, 1.0);                        // wall seam on +Y, not the default +X
        var paths = Fermat.Paths(rings, spacing, anchor, out string? diagnostic);
        Assert.Single(paths);
        Assert.Null(diagnostic);
        var fm = paths[0];
        Assert.True(fm.Count > 200);
        Assert.True(fm[0].Y > 20 && Math.Abs(fm[0].X) < 10, $"start not at the +Y seam: {fm[0].X:0.0},{fm[0].Y:0.0}");
        Assert.True(fm[^1].Y > 20 && Math.Abs(fm[^1].X) < 10, $"end not at the +Y seam: {fm[^1].X:0.0},{fm[^1].Y:0.0}");
        Assert.True(MaxJump(fm) < Math.Sqrt(Math.Pow(4 * spacing, 2) + Math.Pow(2 * spacing, 2)) + 1.5);
    }

    [Fact]
    public void FermatPaths_TwoIslands_ReportIndependentRingFallback()
    {
        var left = CircleRings(2, 10, 3)[0];
        var right = left.Select(p => new Vec3(p.X + 40, p.Y, p.Z)).ToList();

        var paths = Fermat.Paths(new List<IReadOnlyList<Vec3>> { left, right }, 3, null,
                                 out string? diagnostic);

        Assert.Equal(2, paths.Count);
        Assert.Equal("rings not strictly nested (2 islands at depth 0) - independent rings", diagnostic);
    }

    [Fact]
    public void Fermat_CircularRings_ProperSpiral()
    {
        // 6 concentric circles, spacing 3 - the canonical Fermat case
        double spacing = 3.0;
        var rings = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < 6; i++)
        {
            double r = 30 - i * spacing;
            var ring = new List<Vec3>();
            for (int k = 0; k <= 90; k++)
            {
                double a = 2 * Math.PI * k / 90;
                ring.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), 1.0));
            }
            rings.Add(ring);
        }
        var fm = Fermat.Spiral(rings, spacing);
        Assert.True(fm.Count > 200);

        // both endpoints near the seam (+X side) on the OUTER rings - the Fermat property
        Assert.True(fm[0].X > 20, $"start not on the outer boundary: x={fm[0].X:0.0}");
        Assert.True(fm[^1].X > 20, $"end not on the outer boundary: x={fm[^1].X:0.0}");
        Assert.True(PolylineOps.Dist2D(fm[0], fm[^1]) < 6 * spacing);

        // no jump longer than the corridor diagonal (≤ ~4g across + 2 rings radially)
        double maxJump = Enumerable.Range(0, fm.Count - 1).Max(i => PolylineOps.Dist2D(fm[i], fm[i + 1]));
        Assert.True(maxJump < Math.Sqrt(Math.Pow(4 * spacing, 2) + Math.Pow(2 * spacing, 2)) + 1.5,
            $"bridge too long: {maxJump:0.0}");

        // covers (almost) the full length of every ring
        double perims = 0;
        foreach (var r in rings)
            for (int k = 1; k < r.Count; k++) perims += PolylineOps.Dist2D(r[k - 1], r[k]);
        double len = 0;
        for (int k = 1; k < fm.Count; k++) len += PolylineOps.Dist2D(fm[k - 1], fm[k]);
        Assert.True(len > 0.85 * perims, $"path {len:0} covers too little of {perims:0}");

        // every vertex lies on one of the rings (bridges only ADD segments, not points)
        foreach (var pt in fm)
        {
            double rr = Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
            bool onRing = false;
            for (int i = 0; i < 6; i++)
                if (Math.Abs(rr - (30 - i * spacing)) < 0.35) { onRing = true; break; }
            Assert.True(onRing, $"vertex off all rings: r={rr:0.00}");
        }
    }

    [Fact]
    public void Helix_RotatedClosedContoursRemainClosedAndStacked()
    {
        var layers = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < 3; i++)
        {
            // Deliberately change the vertex origin on each layer. This catches
            // the old rotation bug that retained the old duplicate endpoint and
            // consequently dropped the closing edge.
            var q = new List<Vec3>
            {
                new(0, 0, i), new(20, 0, i), new(20, 20, i), new(0, 20, i), new(0, 0, i)
            };
            int shift = (i + 1) % 4;
            var rotated = new List<Vec3>();
            for (int k = 0; k < 4; k++) rotated.Add(q[(shift + k) % 4]);
            rotated.Add(rotated[0]);
            layers.Add(rotated);
        }

        var helix = Helix.Build(layers, new[] { 0.5, 1.5, 2.5 }, 1.0);
        Assert.Equal(15, helix.Count);
        for (int i = 0; i < 3; i++)
        {
            var first = helix[i * 5];
            var last = helix[i * 5 + 4];
            Assert.Equal(first.X, last.X, 9);
            Assert.Equal(first.Y, last.Y, 9);
            Assert.Equal(0.5 + i, first.Z, 9);
            Assert.Equal(1.5 + i, last.Z, 9);
        }
        Assert.Equal(1.5, helix[4].Z, 9);
        Assert.Equal(1.5, helix[5].Z, 9);
    }

    [Fact]
    public void Helix_EdgeSeamStaysAbovePreviousTurnWhenTriangleSplitsMove()
    {
        var first = new Vec3[] { new(10, 0, 1), new(20, 0, 1), new(20, 20, 1), new(0, 20, 1), new(0, 0, 1), new(10, 0, 1) };
        var second = new Vec3[] { new(6, 0, 2), new(20, 0, 2), new(20, 20, 2), new(0, 20, 2), new(0, 0, 2), new(6, 0, 2) };
        var helix = Helix.Build(new[] { first, second }, new[] { 1.0, 2.0 }, 1);
        // Same XY seam at the turn handoff, even though it is no longer a vertex.
        Assert.Equal(new Vec3(10, 0, 2), helix[5]);
        Assert.Equal(helix[5], helix[6]);
        foreach (var corner in new[] { (0.0, 0.0), (20.0, 0.0), (20.0, 20.0), (0.0, 20.0) })
        {
            var heights = helix.Where(p => p.X == corner.Item1 && p.Y == corner.Item2).Select(p => p.Z).ToArray();
            Assert.Equal(2, heights.Length);
            Assert.Equal(1, heights[1] - heights[0], 9);
        }
    }

    [Fact]
    public void SpiralAndFermat_HandleSplitBoxRingsWithDifferentOrigins()
    {
        // Sectioning a triangulated box can split a straight edge at a different
        // vertex on every layer. Both patterns must still recognize one nested
        // island and emit finite, connected paths at that layer's Z.
        var outer = new List<Vec3>
        {
            new(0, 0, 4), new(10, 0, 4), new(20, 0, 4), new(20, 20, 4),
            new(0, 20, 4), new(0, 0, 4)
        };
        var inner = new List<Vec3>
        {
            new(4, 4, 4), new(16, 4, 4), new(16, 16, 4), new(4, 16, 4), new(4, 4, 4)
        };
        // Rotate the inner ring's origin, as happens when contour segments come
        // from a different triangle or edge of the box.
        var shifted = new List<Vec3> { inner[1], inner[2], inner[3], inner[0], inner[1] };
        var rings = new List<IReadOnlyList<Vec3>> { outer, shifted };

        var spiral = Spiral.InwardPaths(rings, new Vec3(20, 10, 4));
        var fermat = Fermat.Paths(rings, 4, new Vec3(20, 10, 4));
        Assert.Single(spiral);
        Assert.Single(fermat);
        Assert.True(spiral[0].Count > 8);
        Assert.True(fermat[0].Count > 8);
        Assert.All(spiral[0], p => Assert.Equal(4, p.Z, 9));
        Assert.All(fermat[0], p => Assert.Equal(4, p.Z, 9));
        Assert.All(spiral[0].Skip(1), p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        Assert.All(fermat[0].Skip(1), p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void LayerInfill_SplitBoxOriginsProduceSameXYStack(int mode)
    {
        var a = new List<Vec3> { new(0, 0, 1), new(10, 0, 1), new(20, 0, 1), new(20, 20, 1), new(0, 20, 1), new(0, 0, 1) };
        var b = new List<Vec3> { new(20, 7, 2), new(20, 20, 2), new(0, 20, 2), new(0, 0, 2), new(6, 0, 2), new(20, 0, 2), new(20, 7, 2) };
        var one = LayerBuilder.BuildTagged(new[] { (IReadOnlyList<Vec3>)a },
            new LayerBuildOptions { Level = 1, WallCount = 1, Infill = mode, WallSpacing = 3, InfillSpacing = 3, AngleDeg = 0 });
        var two = LayerBuilder.BuildTagged(new[] { (IReadOnlyList<Vec3>)b },
            new LayerBuildOptions { Level = 2, WallCount = 1, Infill = mode, WallSpacing = 3, InfillSpacing = 3, AngleDeg = 0 });
        var p1 = one.Paths.Where(p => p.Role == PathRole.Infill).SelectMany(p => p.Points).ToList();
        var p2 = two.Paths.Where(p => p.Role == PathRole.Infill).SelectMany(p => p.Points).ToList();
        Assert.NotEmpty(p1);
        Assert.Equal(p1.Count, p2.Count);
        var xy1 = p1.Select(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6))).ToArray();
        var xy2 = p2.Select(p => (Math.Round(p.X, 6), Math.Round(p.Y, 6))).ToArray();
        Assert.Equal(xy1, xy2);
    }

    static List<Vec3> LShape(double z = 0)
        => new()
        {
            new(0, 0, z), new(40, 0, z), new(40, 20, z), new(20, 20, z),
            new(20, 40, z), new(0, 40, z), new(0, 0, z),
        };

    static List<Vec3> SquareRing(double cx, double cy, double size, double z = 0)
    {
        double h = size * 0.5;
        return new List<Vec3>
        {
            new(cx - h, cy - h, z), new(cx + h, cy - h, z), new(cx + h, cy + h, z),
            new(cx - h, cy + h, z), new(cx - h, cy - h, z),
        };
    }

    /// <summary>A bounding box is not containment: the 6 x 6 island sits in the L's
    /// notch, so its box lies inside the L's box while the island itself lies outside
    /// the L. The pre-4.4.0 bbox nesting test accepted that as one nested stack and
    /// blended the spiral across the notch - extruding over air.</summary>
    [Fact]
    public void SpiralInward_IslandOnlyInsideTheBoundingBox_FallsBackToSeparateRings()
    {
        var rings = new List<IReadOnlyList<Vec3>> { LShape(), SquareRing(30, 30, 6) };

        var paths = Spiral.InwardPaths(rings, null, out string? diagnostic);

        Assert.Equal(2, paths.Count);
        Assert.Equal("rings not strictly nested (2 islands at depth 0) - independent rings", diagnostic);
    }

    [Fact]
    public void FermatPaths_IslandOnlyInsideTheBoundingBox_FallsBackToSeparateRings()
    {
        var rings = new List<IReadOnlyList<Vec3>> { LShape(), SquareRing(30, 30, 6) };

        var paths = Fermat.Paths(rings, 3, null, out string? diagnostic);

        Assert.Equal(2, paths.Count);
        Assert.Equal("rings not strictly nested (2 islands at depth 0) - independent rings", diagnostic);
    }

    /// <summary>Offset.ConcentricRings returns an annulus interleaved - outer0, hole0,
    /// outer1, hole1, ... - because every inward iteration offsets both skins. Nesting
    /// depth sorts that into ONE chain outer0..outerN, holeM..hole0, which is a genuine
    /// inward spiral across the whole band.</summary>
    [Fact]
    public void SpiralInward_InterleavedAnnulusRings_AreOneChainFromOuterToHole()
    {
        const double spacing = 3.0;
        var rings = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < 3; i++)
        {
            rings.Add(CircleRings(1, 37 - i * spacing, spacing)[0]);   // outer skin inward
            rings.Add(CircleRings(1, 23 + i * spacing, spacing)[0]);   // hole skin outward
        }

        var paths = Spiral.InwardPaths(rings, new Vec3(40, 0, 0), out string? diagnostic);

        Assert.Null(diagnostic);
        var sp = Assert.Single(paths);
        double prev = double.MaxValue, min = double.MaxValue, max = double.MinValue;
        foreach (var pt in sp)
        {
            double r = Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
            Assert.True(r <= prev + 0.05, $"radius grew from {prev:0.00} to {r:0.00}");
            prev = r; min = Math.Min(min, r); max = Math.Max(max, r);
        }
        Assert.True(max > 36.9 && max < 37.1, $"outermost radius {max:0.00}");
        Assert.True(min > 22.9 && min < 23.1, $"innermost radius {min:0.00}");
    }

    /// <summary>The point of bounding the offset rings (D2): everything downstream of
    /// them is O(n). An annulus whose hole-side rings used to reach 2090 vertices fed a
    /// spiral of the same order; with the rings thinned at the offset tolerance, the
    /// spiral over the SAME geometry stays inside the budget its own length warrants.
    /// The path must still be one continuous spiral - thinning may not open a hop.</summary>
    [Fact]
    public void Annulus_SpiralOverThinnedRings_StaysWithinTheToleranceVertexBudget()
    {
        const double spacing = 3.0;
        double tol = Offset.SimplifyTolerance(spacing);
        var outer = new List<Vec3>();
        var hole = new List<Vec3>();
        for (int k = 0; k <= 80; k++)
        {
            double a = 2 * Math.PI * k / 80;
            outer.Add(new Vec3(40 * Math.Cos(a), 40 * Math.Sin(a), 0));
            hole.Add(new Vec3(20 * Math.Cos(-a), 20 * Math.Sin(-a), 0));
        }
        var rings = Offset.ConcentricRings(
            new List<IReadOnlyList<Vec3>> { outer, hole }, spacing, 0.0);

        var paths = Spiral.InwardPaths(rings, new Vec3(40, 0, 0), out _);
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            double len = Enumerable.Range(0, path.Count - 1)
                                   .Sum(i => PolylineOps.Dist2D(path[i], path[i + 1]));
            Assert.True(path.Count < 2 * len / tol,
                $"spiral of length {len:0.0} mm carries {path.Count} vertices");
            Assert.True(MaxJump(path) < 2 * spacing,
                $"thinning opened a {MaxJump(path):0.00} mm hop in the spiral");
        }
        Assert.True(paths.Sum(p => p.Count) < 1200,
            $"spiral total {paths.Sum(p => p.Count)} vertices - the raw rings alone were over 3700");
    }
}
