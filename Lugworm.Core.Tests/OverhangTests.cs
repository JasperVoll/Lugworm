using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class OverhangTests
{
    /// <summary>Closed circle (last point = first), radius r, at plane through c with
    /// normal n (n must be unit; builds an orthonormal frame around it).</summary>
    static List<Vec3> Circle(Vec3 c, double r, Vec3 n, int segs = 64)
    {
        // frame
        var ux = Math.Abs(n.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
        // u = ux × n normalized
        var u = Unit(Cross(ux, n));
        var v = Cross(n, u);
        var pts = new List<Vec3>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            double a = 2 * Math.PI * i / segs;
            pts.Add(new Vec3(
                c.X + r * (Math.Cos(a) * u.X + Math.Sin(a) * v.X),
                c.Y + r * (Math.Cos(a) * u.Y + Math.Sin(a) * v.Y),
                c.Z + r * (Math.Cos(a) * u.Z + Math.Sin(a) * v.Z)));
        }
        return pts;
    }

    static Vec3 Cross(Vec3 a, Vec3 b)
        => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static Vec3 Unit(Vec3 a)
    {
        double l = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        return new Vec3(a.X / l, a.Y / l, a.Z / l);
    }

    static List<List<List<Vec3>>> Layers(params List<Vec3>[] contours)
        => contours.Select(c => new List<List<Vec3>> { c }).ToList();

    // ---- vertical wall: 0 deg everywhere ----

    [Fact]
    public void VerticalCylinder_ZeroOverhang()
    {
        var z = new Vec3(0, 0, 1);
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 20, z),
            Circle(new Vec3(0, 0, 3), 20, z),
            Circle(new Vec3(0, 0, 6), 20, z));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        foreach (var s in res.Segments.Where(s => s.Layer > 0))
        {
            Assert.True(s.AngleLayerDeg < 2.0, $"layer angle {s.AngleLayerDeg:0.0}");
            Assert.True(s.AngleWorldDeg < 2.0, $"world angle {s.AngleWorldDeg:0.0}");
            Assert.Equal(OverhangClass.Safe, s.Class);
        }
        Assert.Equal(0.0, res.Layers[1].PctWarning, 3);
    }

    // ---- 45 deg cone: overhang = 45 deg, world == layer (planar layers) ----

    [Fact]
    public void Cone45_Overhang45_BothDefinitions()
    {
        var z = new Vec3(0, 0, 1);
        double h = 3.0;
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 26, z, 128),
            Circle(new Vec3(0, 0, h), 26 - h, z, 128),      // 45° inward lean
            Circle(new Vec3(0, 0, 2 * h), 26 - 2 * h, z, 128));
        // fail threshold at 40° so the whole 45° cone is unambiguously critical
        // (chord discretization measures a fraction of a degree under the ideal 45)
        var res = Overhang.Analyze(layers, new OverhangOptions { WarnDeg = 25, FailDeg = 40 });
        foreach (var s in res.Segments.Where(s => s.Layer > 0))
        {
            Assert.InRange(s.AngleLayerDeg, 40.0, 50.0);
            Assert.InRange(s.AngleWorldDeg, 40.0, 50.0);
            // planar layers: the two definitions must agree
            Assert.True(Math.Abs(s.AngleLayerDeg - s.AngleWorldDeg) < 3.0);
            Assert.Equal(OverhangClass.Critical, s.Class);
        }
        Assert.Equal(100.0, res.Layers[1].PctCritical, 1);
    }

    // ---- shallow lean below warn threshold stays Safe ----

    [Fact]
    public void ShallowLean_ClassifiedSafe()
    {
        var z = new Vec3(0, 0, 1);
        double h = 3.0;
        double shift = h * Math.Tan(10.0 * Math.PI / 180.0);   // 10° lean
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 20, z, 128),
            Circle(new Vec3(shift, 0, h), 20, z, 128));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        foreach (var s in res.Segments.Where(s => s.Layer > 0))
            Assert.Equal(OverhangClass.Safe, s.Class);
        // the leaning side must still measure ~10° at its extreme
        Assert.InRange(res.Layers[1].MaxLayerDeg, 5.0, 15.0);
    }

    // ---- non-planar: tilted rings stacked along their own normal ----
    // layer-relative says 0° (perfectly stacked), world-Z says 30° -> definitions diverge

    [Fact]
    public void TiltedRings_LayerRelativeZero_WorldThirty()
    {
        double tilt = 30.0 * Math.PI / 180.0;
        var n = new Vec3(Math.Sin(tilt), 0, Math.Cos(tilt));   // ring normal, 30° off Z
        double h = 3.0;
        var c0 = new Vec3(0, 0, 10);
        var c1 = new Vec3(c0.X + h * n.X, c0.Y + h * n.Y, c0.Z + h * n.Z);
        var layers = Layers(Circle(c0, 20, n, 128), Circle(c1, 20, n, 128));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        var segs = res.Segments.Where(s => s.Layer == 1).ToList();
        Assert.True(segs.Count > 0);
        double meanLayer = segs.Average(s => s.AngleLayerDeg);
        double meanWorld = segs.Average(s => s.AngleWorldDeg);
        Assert.True(meanLayer < 5.0, $"layer-relative should be ~0, got {meanLayer:0.0}");
        Assert.InRange(meanWorld, 25.0, 35.0);
    }

    // ---- first layer is bed-supported ----

    [Fact]
    public void FirstLayer_AlwaysSafe()
    {
        var z = new Vec3(0, 0, 1);
        var layers = Layers(Circle(new Vec3(0, 0, 0), 20, z));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        Assert.All(res.Segments, s => Assert.Equal(OverhangClass.Safe, s.Class));
        Assert.Equal(0.0, res.Layers[0].MaxLayerDeg, 6);
    }

    // ---- CSV summary ----

    [Fact]
    public void Csv_HasHeaderAndOneLinePerLayer()
    {
        var z = new Vec3(0, 0, 1);
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 20, z),
            Circle(new Vec3(0, 0, 3), 20, z));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        var csv = res.CsvLines();
        Assert.Equal(3, csv.Count);
        Assert.StartsWith("layer,segments,max_overhang_deg", csv[0]);
        Assert.StartsWith("0,", csv[1]);
        Assert.StartsWith("1,", csv[2]);
    }

    // ---- custom thresholds change classification ----

    [Fact]
    public void Thresholds_AreConfigurable()
    {
        var z = new Vec3(0, 0, 1);
        double h = 3.0;
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 26, z, 128),
            Circle(new Vec3(0, 0, h), 26 - h, z, 128));   // 45°
        var strict = Overhang.Analyze(layers, new OverhangOptions { WarnDeg = 10, FailDeg = 20 });
        Assert.All(strict.Segments.Where(s => s.Layer > 0),
            s => Assert.Equal(OverhangClass.Critical, s.Class));
        var lax = Overhang.Analyze(layers, new OverhangOptions { WarnDeg = 50, FailDeg = 80 });
        Assert.All(lax.Segments.Where(s => s.Layer > 0),
            s => Assert.Equal(OverhangClass.Safe, s.Class));
    }

    // ---- regression: non-planar layers with near-equal seam Z crashed Rhino ----
    // Overhang sizes the support grid to 2×|Δz| of the layers' FIRST points. On
    // non-planar (conical) layers that difference can be arbitrarily small but
    // > 1e-9, which rasterized every segment into millions of grid cells and killed
    // the process by memory. SegmentGrid now floors/rescales the cell to a budget.

    /// <summary>Non-planar closed contour: circle with a sine wave in Z; the seam
    /// (first point) sits at zSeam regardless of the wave.</summary>
    static List<Vec3> WavyCircle(double r, double zBase, double amp, double zSeam, int segs = 256)
    {
        var pts = new List<Vec3>(segs + 1);
        for (int i = 0; i <= segs; i++)
        {
            double a = 2 * Math.PI * i / segs;
            double z = i == 0 || i == segs ? zSeam : zBase + amp * Math.Sin(3 * a);
            pts.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
        }
        return pts;
    }

    [Fact]
    public void NonPlanarLayers_NearEqualSeamZ_CompletesWithoutMemoryBlowup()
    {
        // layer 0 seam at z=0, layer 1 seam at z=1e-6 → old cell = 2e-6 mm over a
        // 100 mm contour: certain OOM before the fix; completes in ms after
        var layers = Layers(
            WavyCircle(50, 0.0, 0.0, 0.0),
            WavyCircle(50, 2.0, 1.5, 1e-6));
        var res = Overhang.Analyze(layers, new OverhangOptions());
        Assert.Equal(2, res.Layers.Count);
        Assert.True(res.Segments.Count > 0);
        Assert.All(res.Segments, s => Assert.False(double.IsNaN(s.AngleLayerDeg)));
    }

    // ---- reference toggle: the same tilted rings are Safe locally, Warning globally ----
    // Both angles are always computed; the reference only decides which one the
    // thresholds classify (and so the class percentages).

    [Fact]
    public void TiltedRings_GlobalReference_ClassifiesByWorldAngle()
    {
        double tilt = 30.0 * Math.PI / 180.0;
        var n = new Vec3(Math.Sin(tilt), 0, Math.Cos(tilt));
        double h = 3.0;
        var c0 = new Vec3(0, 0, 10);
        var c1 = new Vec3(c0.X + h * n.X, c0.Y + h * n.Y, c0.Z + h * n.Z);
        var layers = Layers(Circle(c0, 20, n, 128), Circle(c1, 20, n, 128));

        var local = Overhang.Analyze(layers, new OverhangOptions { WarnDeg = 25, FailDeg = 45 });
        Assert.Equal(OverhangReference.Local, local.Reference);
        Assert.All(local.Segments.Where(s => s.Layer == 1), s => Assert.Equal(OverhangClass.Safe, s.Class));
        Assert.Equal(0.0, local.Layers[1].PctWarning, 3);

        var global = Overhang.Analyze(layers, new OverhangOptions { WarnDeg = 25, FailDeg = 45, Reference = OverhangReference.Global });
        Assert.Equal(OverhangReference.Global, global.Reference);
        Assert.All(global.Segments.Where(s => s.Layer == 1), s => Assert.Equal(OverhangClass.Warning, s.Class));
        Assert.Equal(100.0, global.Layers[1].PctWarning, 3);
        Assert.Equal(0.0, global.Layers[1].PctCritical, 3);
        // layer 0 is bed-supported under either reference
        Assert.All(global.Segments.Where(s => s.Layer == 0), s => Assert.Equal(OverhangClass.Safe, s.Class));

        // the angles themselves do not move with the reference
        Assert.Equal(local.Segments.Count, global.Segments.Count);
        for (int i = 0; i < local.Segments.Count; i++)
        {
            Assert.Equal(local.Segments[i].AngleLayerDeg, global.Segments[i].AngleLayerDeg, 9);
            Assert.Equal(local.Segments[i].AngleWorldDeg, global.Segments[i].AngleWorldDeg, 9);
            Assert.Equal(global.Segments[i].AngleWorldDeg, global.Segments[i].AngleDeg(OverhangReference.Global), 9);
            Assert.Equal(global.Segments[i].AngleLayerDeg, global.Segments[i].AngleDeg(OverhangReference.Local), 9);
        }
    }

    [Fact]
    public void Cone45_GlobalReference_StillCritical()
    {
        // planar layers: the two readings agree, so the class does not depend on the reference
        var z = new Vec3(0, 0, 1);
        double h = 3.0;
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 26, z, 128),
            Circle(new Vec3(0, 0, h), 26 - h, z, 128));
        var opt = new OverhangOptions { WarnDeg = 25, FailDeg = 40, Reference = OverhangReference.Global };
        var res = Overhang.Analyze(layers, opt);
        Assert.All(res.Segments.Where(s => s.Layer > 0), s => Assert.Equal(OverhangClass.Critical, s.Class));
        Assert.Equal(100.0, res.Layers[1].PctCritical, 1);
    }

    [Fact]
    public void Csv_GlobalReference_NamesTheReferenceFirst()
    {
        var z = new Vec3(0, 0, 1);
        var layers = Layers(
            Circle(new Vec3(0, 0, 0), 20, z),
            Circle(new Vec3(0, 0, 3), 20, z));
        var csv = Overhang.Analyze(layers, new OverhangOptions { Reference = OverhangReference.Global }).CsvLines();
        Assert.Equal(4, csv.Count);
        Assert.StartsWith("# reference: global", csv[0]);
        Assert.StartsWith("layer,segments,max_overhang_deg", csv[1]);
        Assert.StartsWith("0,", csv[2]);
        Assert.StartsWith("1,", csv[3]);
    }

    [Fact]
    public void SegmentGrid_PathologicalCellSize_StaysCorrect()
    {
        // the rescued cell must not change results: nearest distances match a
        // brute-force scan
        var contour = Circle(new Vec3(0, 0, 0), 50, new Vec3(0, 0, 1), 200);
        var contours = new List<List<Vec3>> { contour };
        var grid = new SegmentGrid(contours, cellSize: 2e-6);   // pathological
        var reference = new SegmentGrid(contours);              // sane auto cell

        var rnd = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            var p = new Vec3(rnd.NextDouble() * 120 - 60, rnd.NextDouble() * 120 - 60, rnd.NextDouble() * 4 - 2);
            var (_, d1) = grid.Nearest(p);
            var (_, d2) = reference.Nearest(p);
            Assert.Equal(d2, d1, 6);
        }
    }
}
