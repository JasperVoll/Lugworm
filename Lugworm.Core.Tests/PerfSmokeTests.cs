using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lugworm.Core.Arcs;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace Lugworm.Core.Tests;

/// <summary>Timing smoke tests for the P3 hot paths (2026-07-14 run). These print
/// timings (dotnet test --logger "console;verbosity=detailed") and assert wall-clock
/// ceilings that scale with machine load (D5, 2026-09-14) so they never flake on a
/// slow or busy machine - the 2026-07-14 run report (archived outside the repository)
/// measured Overhang 3913 -> 316 ms and VariableFlow 1714 -> 177 ms after SegmentGrid.
/// The [Trait("Category", "Perf")] tag sits on each timing fact individually, not
/// on the class, because two of them (Iso_ThousandLayers_ProducesExpectedContourCount,
/// SegmentGrid_100kSegments_FarQueryFindsNearestPoint) are functional-only and must
/// always run even under --filter "Category!=Perf".</summary>
[Collection(ExclusivePerformanceCollection.Name)]
public class PerfSmokeTests
{
    readonly ITestOutputHelper _out;
    public PerfSmokeTests(ITestOutputHelper o) { _out = o; }

    // D5 (2026-09-14): see ParetoTests / PerfReference for the convention - every
    // wall-clock budget below is K * this, not a fixed number of seconds.
    static readonly Lazy<double> s_referenceMs = new(PerfReference.MeasureMs);

    static List<Vec3> Circle(double r, double z, int n)
    {
        var pts = new List<Vec3>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double a = 2 * Math.PI * i / n;
            pts.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
        }
        return pts;
    }

    static double TimeMs(Action a, int reps = 3)
    {
        a(); // warmup
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    readonly struct PerfSample
    {
        public readonly double Milliseconds;
        public readonly long Allocated;
        public PerfSample(double milliseconds, long allocated)
        { Milliseconds = milliseconds; Allocated = allocated; }
    }

    static PerfSample Measure(Action action, int reps = 1)
    {
        action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) action();
        sw.Stop();
        return new PerfSample(sw.Elapsed.TotalMilliseconds / reps,
            (GC.GetAllocatedBytesForCurrentThread() - before) / Math.Max(1, reps));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Zigzag_LargeBoundary()
    {
        // 150mm bowl section, 1mm resample (~950 pts), 2mm spacing -> ~150 scanlines
        // D5: budget = 45 * reference (was a fixed 5000ms) - see ParetoTests.
        var boundary = new List<IReadOnlyList<Vec3>> { Circle(150, 0, 950) };
        double ms = TimeMs(() => Infill.Zigzag(boundary, 2.0, 45.0, 0.0));
        double budgetMs = 45 * s_referenceMs.Value;
        _out.WriteLine($"Zigzag 150mm/2mm: {ms:0.0} ms (budget {budgetMs:0.0} ms)");
        Assert.True(ms < budgetMs);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void ConcentricRings_FineSpacing()
    {
        // r=40 cylinder layer at 1 mm spacing -> ~40 rings (test round 3.1.4: "concentric takes 2 s")
        // D5: budget = 9 * reference (was a fixed 1000ms) - see ParetoTests.
        var ring = Circle(40, 0, 360);
        double ms = TimeMs(() =>
        {
            var rings = Offset.ConcentricRings(new[] { ring }, 1.0, 0.0);
            _out.WriteLine($"rings: {rings.Count}, points: {rings.Sum(r => r.Count)}");
        });
        double budgetMs = 9 * s_referenceMs.Value;
        _out.WriteLine($"ConcentricRings r40 sp1: {ms:0.0} ms (budget {budgetMs:0.0} ms)");
        Assert.True(ms < budgetMs);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void NestingOrient_ManyRings()
    {
        // 60 concentric rings x 400 pts
        // D5: budget = 90 * reference (was a fixed 10000ms) - see ParetoTests.
        var rings = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < 60; i++) rings.Add(Circle(150 - i * 2, 0, 400));
        double ms = TimeMs(() => Nesting.Orient(rings));
        double budgetMs = 90 * s_referenceMs.Value;
        _out.WriteLine($"Nesting.Orient 60x400: {ms:0.0} ms (budget {budgetMs:0.0} ms)");
        Assert.True(ms < budgetMs);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void VariableFlow_LargeLayers()
    {
        // 30 layers x 2000 points (vase at 1mm sampling)
        // D5: budget = 550 * reference (was a fixed 60000ms) - see ParetoTests.
        var branches = new List<IReadOnlyList<IReadOnlyList<Vec3>>>();
        for (int li = 0; li < 30; li++)
            branches.Add(new List<IReadOnlyList<Vec3>> { Circle(100 + Math.Sin(li * 0.3) * 10, li * 2.0, 2000) });
        double ms = TimeMs(() => VariableFlow.LocalHeights(branches, 2.0), reps: 1);
        double budgetMs = 550 * s_referenceMs.Value;
        _out.WriteLine($"VariableFlow 30x2000: {ms:0.0} ms (budget {budgetMs:0.0} ms)");
        Assert.True(ms < budgetMs);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Overhang_LargeLayers()
    {
        // D5: budget = 550 * reference (was a fixed 60000ms) - see ParetoTests.
        var layers = new List<IReadOnlyList<IReadOnlyList<Vec3>>>();
        for (int li = 0; li < 30; li++)
            layers.Add(new List<IReadOnlyList<Vec3>> { Circle(100 - li * 1.5, li * 2.0, 2000) });
        double ms = TimeMs(() => Overhang.Analyze(layers, new OverhangOptions()), reps: 1);
        double budgetMs = 550 * s_referenceMs.Value;
        _out.WriteLine($"Overhang 30x2000: {ms:0.0} ms (budget {budgetMs:0.0} ms)");
        Assert.True(ms < budgetMs);
    }

    static (List<Vec3> vertices, List<(int, int, int)> faces, List<double> scalar, List<double> levels) IsoThousandLayersSetup()
    {
        var vertices = new List<Vec3>
        {
            new Vec3(-10, -10, 0), new Vec3(10, -10, 10), new Vec3(10, 10, 10),
            new Vec3(-10, 10, 0)
        };
        var faces = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };
        var scalar = vertices.Select(v => v.Z).ToList();
        var levels = Enumerable.Range(0, 1000).Select(i => 0.01 + i * 0.0099).ToList();
        return (vertices, faces, scalar, levels);
    }

    [Fact]
    public void Iso_ThousandLayers_ProducesExpectedContourCount()
    {
        // Functional half of the split (D5): always runs.
        var (vertices, faces, scalar, levels) = IsoThousandLayersSetup();
        Assert.Equal(1000, Iso.PlanarContoursAtLevels(vertices, faces, scalar, levels).Count);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Iso_ThousandLayers_UsesBuckets()
    {
        // D5: ms budget = 90 * reference (was a fixed 10000ms) - see ParetoTests.
        // The allocation budget is unaffected by machine load, so it stays fixed.
        var (vertices, faces, scalar, levels) = IsoThousandLayersSetup();
        var sample = Measure(() => Iso.PlanarContoursAtLevels(vertices, faces, scalar, levels), 1);
        double budgetMs = 90 * s_referenceMs.Value;
        _out.WriteLine($"Iso 1000 levels: {sample.Milliseconds:0.0} ms (budget {budgetMs:0.0} ms), {sample.Allocated / 1024.0:0.0} KiB");
        Assert.True(sample.Milliseconds < budgetMs);
        Assert.True(sample.Allocated < 128L * 1024 * 1024);
    }

    static List<Vec3> SegmentGrid100kContour()
    {
        var contour = new List<Vec3>(100001);
        for (int i = 0; i <= 100000; i++)
            contour.Add(new Vec3(i * 0.02, Math.Sin(i * 0.01), 2.0));
        return contour;
    }

    [Fact]
    public void SegmentGrid_100kSegments_FarQueryFindsNearestPoint()
    {
        // Functional half of the split (D5): always runs.
        var contours = new List<IReadOnlyList<Vec3>> { SegmentGrid100kContour() };
        var grid = new SegmentGrid(contours);
        var hit = grid.Nearest(new Vec3(1e7, -1e7, 1e7));
        Assert.True(hit.dist > 1e6);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void SegmentGrid_100kSegments_RebuildAndFarQuery()
    {
        // D5: ms budget = 270 * reference (was a fixed 30000ms) - see ParetoTests.
        // The allocation budget is unaffected by machine load, so it stays fixed.
        var contours = new List<IReadOnlyList<Vec3>> { SegmentGrid100kContour() };
        var sample = Measure(() =>
        {
            var grid = new SegmentGrid(contours);
            grid.Nearest(new Vec3(1e7, -1e7, 1e7));
            grid.Rebuild(contours);
        }, 1);
        double budgetMs = 270 * s_referenceMs.Value;
        _out.WriteLine($"SegmentGrid 100k + far query: {sample.Milliseconds:0.0} ms (budget {budgetMs:0.0} ms), {sample.Allocated / 1024.0:0.0} KiB");
        Assert.True(sample.Milliseconds < budgetMs);
        Assert.True(sample.Allocated < 512L * 1024 * 1024);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void ArcFit_100kWorstCase_IsBounded()
    {
        // D5: ms budget = 270 * reference (was a fixed 30000ms) - see ParetoTests.
        // The allocation budget is unaffected by machine load, so it stays fixed.
        var pts = Circle(100, 4, 100000);
        // A small perturbation keeps the fitter checking the tolerance boundary
        // without allowing an unbounded candidate window.
        for (int i = 0; i < pts.Count; i += 97)
            pts[i] = new Vec3(pts[i].X + 0.01, pts[i].Y - 0.01, pts[i].Z);
        var sample = Measure(() => ArcFit.FitArcs(pts, 0.05), 1);
        double budgetMs = 270 * s_referenceMs.Value;
        _out.WriteLine($"ArcFit 100k boundary: {sample.Milliseconds:0.0} ms (budget {budgetMs:0.0} ms), {sample.Allocated / 1024.0:0.0} KiB");
        Assert.True(sample.Milliseconds < budgetMs);
        Assert.True(sample.Allocated < 256L * 1024 * 1024);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Nesting_DeeplyNestedRings_StaysWithinBudget()
    {
        // D5: ms budget = 270 * reference (was a fixed 30000ms) - see ParetoTests.
        // The allocation budget is unaffected by machine load, so it stays fixed.
        var rings = new List<IReadOnlyList<Vec3>>();
        for (int i = 0; i < 500; i++) rings.Add(Circle(300 - i * 0.5, 0, 64));
        var sample = Measure(() => Nesting.Orient(rings), 1);
        double budgetMs = 270 * s_referenceMs.Value;
        _out.WriteLine($"Nesting 500 deep rings: {sample.Milliseconds:0.0} ms (budget {budgetMs:0.0} ms), {sample.Allocated / 1024.0:0.0} KiB");
        Assert.True(sample.Milliseconds < budgetMs);
        Assert.True(sample.Allocated < 512L * 1024 * 1024);
    }
}
