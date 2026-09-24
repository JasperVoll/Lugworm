using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

public class SlicingTests
{
    // ---- conical (mirror t_conical) ----
    [Fact]
    public void Cone_ForwardRaisesZ_InverseIsExact()
    {
        double ang = 30.0 * Math.PI / 180.0;
        var p = new Vec3(5, 0, 3);
        var fp = Conical.ForwardZ(p, ang, 0, 0);
        var back = Conical.InversePt(fp, ang, 0, 0);
        Assert.True(fp.Z > p.Z);
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Z, back.Z, 9);
    }

    [Fact]
    public void LocalSlope_Is30Deg()
    {
        double ang = 30.0 * Math.PI / 180.0;
        var slope = Conical.LocalSlopeDeg(new List<Vec3> { new(0, 0, 0), new(10, 0, Math.Tan(ang) * 10) });
        Assert.Equal(30.0, slope, 1);
    }

    // ---- levels ----
    [Fact]
    public void PlanarLevels_SpanRange()
    {
        var lv = Levels.Planar(0, 10, 0, 1.0);   // first=0 -> start at 0.5
        Assert.True(lv.Count >= 9);
        Assert.True(lv[0] >= 0.4 && lv[0] <= 0.6);
        Assert.True(lv[^1] < 10.0);
    }

    [Fact]
    public void AdaptiveLevels_ConstantSlope_UniformStep()
    {
        var lv = Levels.Adaptive(0, 10, 0, 0.5, 1.5, z => 0.5);  // f=0.5 -> step 1.0
        Assert.True(lv.Count >= 9);
    }

    // ---- iso-contours (mirror t_iso) ----
    static (List<Vec3> V, List<(int, int, int)> F) GridMesh(int nx, int ny, double sp)
    {
        var V = new List<Vec3>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
                V.Add(new Vec3(i * sp, j * sp, 0.0));
        var F = new List<(int, int, int)>();
        int Idx(int i, int j) => j * nx + i;
        for (int j = 0; j < ny - 1; j++)
            for (int i = 0; i < nx - 1; i++)
            {
                F.Add((Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1)));
                F.Add((Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1)));
            }
        return (V, F);
    }

    [Fact]
    public void Iso_LineAtX5_SpansFullY()
    {
        var (V, F) = GridMesh(11, 11, 1.0);
        var scalar = V.Select(v => v.X).ToList();
        var rings = Iso.Contour(V, F, scalar, level: 5.0);
        Assert.True(rings.Count >= 1);
        var pl = rings.OrderByDescending(r => r.Count).First();
        Assert.All(pl, p => Assert.True(Math.Abs(p.X - 5.0) < 1e-6));
        Assert.True(pl.Max(p => p.Y) - pl.Min(p => p.Y) > 9.0);
    }

    [Fact]
    public void IsoLayers_ProducesMultiple()
    {
        var (V, F) = GridMesh(11, 11, 1.0);
        var scalar = V.Select(v => v.X).ToList();
        var layers = Iso.Layers(V, F, scalar, start: 1.0, step: 2.0);
        Assert.True(layers.Count >= 3);
    }
}
