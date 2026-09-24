using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace Lugworm.Core.Tests;

/// <summary>Final test round 2.6.0, section 8 row 9: adaptive (3.1.7) and
/// conical (3.1.8) on round objects.</summary>
public class FinalTestRoundSlicingTests
{
    readonly ITestOutputHelper _out;
    public FinalTestRoundSlicingTests(ITestOutputHelper o) { _out = o; }

    /// <summary>Closed upper half of a UV sphere (radius R) with a flat bottom cap -
    /// what a Rhino "half sphere" Brep meshes to.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Hemisphere(double R, int nu = 64, int nv = 32)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        for (int j = 0; j <= nv; j++)
        {
            double phi = 0.5 * Math.PI * j / nv;
            for (int i = 0; i < nu; i++)
            {
                double th = 2 * Math.PI * i / nu;
                V.Add(new Vec3(R * Math.Cos(phi) * Math.Cos(th), R * Math.Cos(phi) * Math.Sin(th), R * Math.Sin(phi)));
            }
        }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = j * nu + i, b = j * nu + (i + 1) % nu, c = (j + 1) * nu + i, d = (j + 1) * nu + (i + 1) % nu;
                F.Add((a, b, d)); F.Add((a, d, c));
            }
        int centre = V.Count; V.Add(new Vec3(0, 0, 0));
        for (int i = 0; i < nu; i++) F.Add((centre, (i + 1) % nu, i));
        return (V, F);
    }

    /// <summary>Closed solid of revolution: radius profile r(z) sampled on nv rows
    /// between z=0 and z=h, capped flat at both ends.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Revolve(Func<double, double> radius, double h, int nu = 72, int nv = 40)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        for (int j = 0; j <= nv; j++)
        {
            double z = h * j / nv;
            double r = Math.Max(radius(z), 1e-3);
            for (int i = 0; i < nu; i++)
            {
                double th = 2 * Math.PI * i / nu;
                V.Add(new Vec3(r * Math.Cos(th), r * Math.Sin(th), z));
            }
        }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = j * nu + i, b = j * nu + (i + 1) % nu, c = (j + 1) * nu + i, d = (j + 1) * nu + (i + 1) % nu;
                F.Add((a, b, d)); F.Add((a, d, c));
            }
        int c0 = V.Count; V.Add(new Vec3(0, 0, 0));
        int c1 = V.Count; V.Add(new Vec3(0, 0, h));
        for (int i = 0; i < nu; i++)
        {
            F.Add((c0, (i + 1) % nu, i));
            F.Add((c1, nv * nu + i, nv * nu + (i + 1) % nu));
        }
        return (V, F);
    }

    /// <summary>The Core half of the component's conical path (SlicerComponent
    /// SliceObject, mode 1): shear every vertex by tan(a)*r about (cx,cy), slice
    /// planar in the sheared frame, map the rings back.</summary>
    static (List<double> levels, List<List<List<Vec3>>> rings) SliceConical(
        List<Vec3> V, List<(int, int, int)> F, double coneDeg, double layerH, double first, double cx, double cy)
    {
        double ang = coneDeg * Math.PI / 180.0;
        var sheared = V.Select(v => Conical.ForwardZ(v, ang, cx, cy)).ToList();
        double zmin = sheared.Min(v => v.Z), zmax = sheared.Max(v => v.Z);
        var levels = Levels.Planar(zmin, zmax, first, layerH);
        var scalar = sheared.Select(v => v.Z).ToList();
        var bucketed = Iso.PlanarContoursAtLevels(sheared, F, scalar, levels, 1e-3);
        var rings = new List<List<List<Vec3>>>();
        foreach (var layer in bucketed)
            rings.Add(layer.Select(pl => Conical.InverseContour(pl, ang, cx, cy)).ToList());
        return (levels, rings);
    }

    // ---------------- 3.1.7 adaptive ----------------

    [Fact]
    public void Row317_HalfSphere_Adaptive_ThickAtBase_ThinAtTop()
    {
        var (V, F) = Hemisphere(50);
        var levels = Levels.Adaptive(0, 50, 3.0, 1.0, 3.0, Levels.SlopeFromMesh(V, F));
        var h = Enumerable.Range(1, levels.Count - 1).Select(i => levels[i] - levels[i - 1]).ToList();
        Assert.True(h.Count >= 10, $"only {levels.Count} layers");
        _out.WriteLine("layer heights: " + string.Join(" ", h.Select(x => x.ToString("0.00"))));
        Assert.True(h[0] > 2.5, $"base layer {h[0]:0.00} should be near hMax (vertical wall)");
        Assert.True(h[^1] < 1.3, $"top layer {h[^1]:0.00} should be near hMin (shallow top)");
        Assert.True(h.Max() - h.Min() > 1.5, "adaptive produced (almost) uniform layers");
        for (int i = 1; i < h.Count; i++)
            Assert.True(h[i] <= h[i - 1] + 1e-9, $"layer height must not grow toward the top (layer {i})");
    }

    // ---------------- 3.1.8 conical ----------------

    [Fact]
    public void Row318_Cylinder_Conical_RingsStayHorizontal_ByDesign()
    {
        // surface of revolution about the shear centre: the radial shear moves every
        // point of a horizontal circle by the SAME amount, so the section in the
        // sheared frame is a horizontal circle and so is its inverse image
        const int nu = 72;
        var (V, F) = Revolve(_ => 30.0, 60.0, nu);
        var (_, rings) = SliceConical(V, F, 20.0, 3.0, 3.0, 0, 0);
        // the shear is exact at the vertices; a section point on a diagonal edge lies
        // on the chord (radius 30*cos(pi/nu)), so its inverse shift is short by up to
        // tan(a)*30*(1-cos(pi/nu)) - 0.01 mm at 72 segments, 1e-5 at Rhino's meshing
        double chord = Math.Tan(20.0 * Math.PI / 180.0) * 30.0 * (1.0 - Math.Cos(Math.PI / nu));
        int checkedRings = 0;
        foreach (var layer in rings)
            foreach (var ring in layer)
            {
                if (ring.Count < 8) continue;
                double lo = ring.Min(p => p.Z), hi = ring.Max(p => p.Z);
                Assert.True(hi - lo <= chord + 1e-9, $"ring Z varies by {hi - lo:e2} (chord bound {chord:e2})");
                checkedRings++;
            }
        Assert.True(checkedRings >= 10, $"only {checkedRings} rings");
    }

    [Fact]
    public void Row318_Cone_Conical_LayerSpacingDiffersFromPlanar()
    {
        // cone r(z) = 40*(1 - z/80): sheared height z' = z + tan(20 deg)*r(z) =
        // 14.56 + 0.818 z, so a 3 mm step in z' is a 3.67 mm step up the wall.
        // Levels below tan(a)*40 = 14.56 cut the sheared base cap instead: those are
        // the cone-shaped bottom layers of conical slicing, rings ON the bed (z = 0)
        double tan = Math.Tan(20.0 * Math.PI / 180.0);
        var (V, F) = Revolve(z => 40.0 * (1.0 - z / 80.0), 80.0);
        var (levels, rings) = SliceConical(V, F, 20.0, 3.0, 3.0, 0, 0);
        Assert.Equal(levels.Count, rings.Count);
        var ringZ = rings.Where(l => l.Count > 0).Select(l => l[0][0].Z).ToList();
        int bedRings = ringZ.Count(z => Math.Abs(z) < 1e-6);
        int expectBed = levels.Count(l => l < tan * 40.0);
        _out.WriteLine($"conical: {ringZ.Count} rings ({bedRings} on the bed), planar: {Levels.Planar(0, 80, 3.0, 3.0).Count} levels");
        Assert.Equal(expectBed, bedRings);
        var wall = ringZ.Where(z => z > 1e-6).ToList();
        Assert.True(wall.Count >= 10, $"only {wall.Count} wall rings");
        var spacing = Enumerable.Range(1, wall.Count - 1).Select(i => wall[i] - wall[i - 1]).ToList();
        double expectSpacing = 3.0 / (1.0 - tan * 40.0 / 80.0);
        Assert.All(spacing, s => Assert.InRange(s, expectSpacing - 0.05, expectSpacing + 0.05));
    }

    static List<Vec3> Circle(double r, double z, int n = 72)
    {
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * (i % n) / n;
            pts.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
        }
        return pts;
    }

    [Fact]
    public void Row318_Cylinder_Conical_WallsAndInfillFollowTheCone()
    {
        // one layer of a cylinder r=30 in conical mode: the outer ring was cut at
        // `level` in the sheared frame and mapped back to level - tan(a)*30 (planar,
        // by design). The second wall and the infill lie INSIDE that ring, so they
        // must climb the cone z = level - tan(a)*r toward the axis - that is the
        // only place conical mode is visible on a round object.
        double ang = 20.0 * Math.PI / 180.0, tan = Math.Tan(ang), R = 30.0, spacing = 5.0, level = 40.0;
        var ring = Circle(R, level - tan * R);
        var curves = LayerBuilder.BuildConical(new[] { ring }, new LayerBuildOptions
        {
            Level = level,
            WallCount = 2,
            Infill = 1,
            WallSpacing = spacing,
            AngleDeg = 0.0,
            ConeAngleRad = ang,
            Cx = 0,
            Cy = 0,
        });
        Assert.True(curves.Count >= 3, $"expected outer wall, inner wall and infill, got {curves.Count} curves");

        double worst = 0.0;
        foreach (var c in curves)
            foreach (var q in c)
            {
                double expect = level - tan * Math.Sqrt(q.X * q.X + q.Y * q.Y);
                worst = Math.Max(worst, Math.Abs(q.Z - expect));
            }
        Assert.True(worst < 1e-6, $"walls/infill leave the cone surface by up to {worst:0.###} mm (planar infill = conical no-op)");

        double outerZ = curves[0].Average(q => q.Z), innerZ = curves[1].Average(q => q.Z);
        Assert.InRange(innerZ - outerZ, tan * spacing - 0.05, tan * spacing + 0.05);
        var infill = curves.Skip(2).SelectMany(c => c).ToList();
        Assert.True(infill.Max(q => q.Z) - infill.Min(q => q.Z) > 3.0, "infill should rise toward the axis");

        // and the printed MOVES follow the cone too: the midpoint of every straight
        // segment sags below the layer surface by no more than the chord tolerance
        // (a 2-point zigzag span through the axis would sag by tan(a)*20 = 7 mm)
        double sag = 0.0;
        foreach (var c in curves)
            for (int k = 0; k + 1 < c.Count; k++)
            {
                double mx = 0.5 * (c[k].X + c[k + 1].X), my = 0.5 * (c[k].Y + c[k + 1].Y);
                double cone = level - tan * Math.Sqrt(mx * mx + my * my);
                sag = Math.Max(sag, cone - 0.5 * (c[k].Z + c[k + 1].Z));
            }
        Assert.True(sag <= LayerBuilder.ConeChordTol + 1e-9, $"a printed move sags {sag:0.###} mm below the conical layer");
        Assert.Equal(ring.Count, curves[0].Count);   // the round perimeter itself is not densified
    }

    [Fact]
    public void Row318_InverseContourRefined_SpanThroughAxis_GetsApex_RingUnchanged()
    {
        double ang = 20.0 * Math.PI / 180.0, tan = Math.Tan(ang);
        // through the axis: rim, apex, rim - the two halves are straight cone
        // generatrices, so nothing else is needed
        var span = new List<Vec3> { new Vec3(-20, 0, 40), new Vec3(20, 0, 40) };
        var mapped = Conical.InverseContourRefined(span, ang, 0, 0, 0.05);
        Assert.Equal(3, mapped.Count);
        Assert.Equal(40.0 - tan * 20.0, mapped[0].Z, 9);
        Assert.Equal(40.0, mapped[1].Z, 9);
        Assert.Equal(40.0 - tan * 20.0, mapped[2].Z, 9);

        // off-axis chord: the cone height along it is curved, so points are inserted
        // until every printed move stays within the chord tolerance
        var chord = new List<Vec3> { new Vec3(-20, 5, 40), new Vec3(20, 5, 40) };
        var refined = Conical.InverseContourRefined(chord, ang, 0, 0, 0.05);
        Assert.True(refined.Count > 3 && refined.Count < 200, $"{refined.Count} points");
        for (int k = 1; k < refined.Count; k++)
        {
            Assert.True(refined[k].X > refined[k - 1].X, "points must stay ordered along the span");
            double mx = 0.5 * (refined[k].X + refined[k - 1].X), my = 0.5 * (refined[k].Y + refined[k - 1].Y);
            double cone = 40.0 - tan * Math.Sqrt(mx * mx + my * my);
            Assert.True(cone - 0.5 * (refined[k].Z + refined[k - 1].Z) <= 0.05 + 1e-9, "move sags below the cone");
        }

        var ring = Circle(30.0, 40.0);
        var same = Conical.InverseContourRefined(ring, ang, 0, 0, 0.05);
        Assert.Equal(ring.Count, same.Count);
        Assert.Equal(Conical.InverseContour(ring, ang, 0, 0).Select(q => q.Z), same.Select(q => q.Z));
    }

    [Fact]
    public void Row318_ZeroConeAngle_BuildConical_EqualsPlanarBuild()
    {
        var ring = Circle(30.0, 12.0);
        var planar = LayerBuilder.Build(new[] { ring },
            new LayerBuildOptions { Level = 12.0, WallCount = 2, Infill = 1, WallSpacing = 5.0, AngleDeg = 0.0 });
        var conical = LayerBuilder.BuildConical(new[] { ring }, new LayerBuildOptions
        {
            Level = 12.0,
            WallCount = 2,
            Infill = 1,
            WallSpacing = 5.0,
            AngleDeg = 0.0,
            ConeAngleRad = 0.0,
            Cx = 0,
            Cy = 0,
        });
        Assert.Equal(planar.Count, conical.Count);
        for (int i = 0; i < planar.Count; i++)
        {
            Assert.Equal(planar[i].Count, conical[i].Count);
            for (int k = 0; k < planar[i].Count; k++)
                Assert.Equal(0.0, planar[i][k].DistanceTo(conical[i][k]), 12);
        }
    }
}
