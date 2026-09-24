using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Adaptive layer height on a dome (test round 3.1.7): thin layers on the
/// shallow top, thick layers on the steep flank, a flat cap must not distort it.</summary>
public class AdaptiveSlopeTests
{
    /// <summary>Closed hemisphere of radius R with a flat bottom cap (what a Rhino
    /// "half sphere" Brep meshes to).</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Hemisphere(double R, int nu = 48, int nv = 24)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        for (int j = 0; j <= nv; j++)
        {
            double phi = 0.5 * Math.PI * j / nv;            // 0 = equator, pi/2 = pole
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
        int centre = V.Count; V.Add(new Vec3(0, 0, 0));   // bottom cap fan
        for (int i = 0; i < nu; i++) F.Add((centre, (i + 1) % nu, i));
        return (V, F);
    }

    [Fact]
    public void Dome_ThinLayersOnTop_ThickOnFlank()
    {
        var (V, F) = Hemisphere(40);
        var slope = Levels.SlopeFromMesh(V, F);
        Assert.True(slope(1.0) > 0.85, $"flank should read steep, got {slope(1.0):0.00}");   // cap ignored
        Assert.True(slope(39.5) < 0.15, $"top should read flat, got {slope(39.5):0.00}");

        var levels = Levels.Adaptive(0, 40, 1.0, 1.0, 3.0, slope);
        var h = Enumerable.Range(1, levels.Count - 1).Select(i => levels[i] - levels[i - 1]).ToList();
        Assert.True(h.First() > 2.5, $"bottom layer {h.First():0.00} should be near hMax");
        Assert.True(h.Last() < 1.3, $"top layer {h.Last():0.00} should be near hMin");
        for (int i = 1; i < h.Count; i++)
            Assert.True(h[i] <= h[i - 1] + 1e-9, $"layer height must not grow toward the top (layer {i})");
        Assert.True(h.Max() - h.Min() > 1.5, "adaptive produced (almost) uniform layers");
    }

    [Fact]
    public void Dome_MaximumHeightSpansCrown_StillTapersInsteadOfUsingOneMinimum()
    {
        var (v, f) = Hemisphere(6);
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 6, 1, 0.5, 5);
        var heights = Enumerable.Range(1, levels.Count - 1)
            .Select(i => levels[i] - levels[i - 1]).ToList();
        Assert.True(heights.First() > 1.0, $"Lower dome should use a larger gap, got {heights.First():F4}");
        // The last full gap approaches hMin; the remaining cap can be thinner
        // than hMin and does not get an extra out-of-bounds section.
        Assert.InRange(heights.Last(), 0.5, 0.5 + 0.1 * (5 - 0.5));
        Assert.True(heights.First() - heights.Last() > 0.5, "Dome must taper toward hMin, not use one global height");
        for (int i = 1; i < heights.Count; i++)
            Assert.True(heights[i] <= heights[i - 1] + 1e-8, "Gaps should decrease toward the crown");
    }

    [Fact]
    public void Dome_ProductionScheduler_ThinsTowardTop()
    {
        var (v, f) = Hemisphere(40);
        var levels = OverhangAdaptiveLevels.Build(v, f, 0, 40, 1, 1, 3);
        var heights = Enumerable.Range(1, levels.Count - 1)
            .Select(i => levels[i] - levels[i - 1]).ToList();
        Assert.True(heights.First() > 2.5);
        Assert.True(heights.Last() < 1.3);
        for (int i = 1; i < heights.Count; i++) Assert.True(heights[i] <= heights[i - 1] + 1e-8);
    }

    [Fact]
    public void Cylinder_AllVertical_UsesHMax()
    {
        // vertical tube 0..50 with flat caps: every printed layer is a vertical wall
        var V = new List<Vec3>(); var F = new List<(int, int, int)>();
        int n = 36;
        for (int j = 0; j <= 1; j++)
            for (int i = 0; i < n; i++)
                V.Add(new Vec3(20 * Math.Cos(2 * Math.PI * i / n), 20 * Math.Sin(2 * Math.PI * i / n), 50 * j));
        for (int i = 0; i < n; i++) { F.Add((i, (i + 1) % n, n + (i + 1) % n)); F.Add((i, n + (i + 1) % n, n + i)); }
        int c0 = V.Count; V.Add(new Vec3(0, 0, 0)); int c1 = V.Count; V.Add(new Vec3(0, 0, 50));
        for (int i = 0; i < n; i++) { F.Add((c0, (i + 1) % n, i)); F.Add((c1, n + i, n + (i + 1) % n)); }

        var levels = Levels.Adaptive(0, 50, 1.0, 1.0, 3.0, Levels.SlopeFromMesh(V, F));
        for (int i = 1; i < levels.Count; i++)
            Assert.Equal(3.0, levels[i] - levels[i - 1], 6);
    }
}
