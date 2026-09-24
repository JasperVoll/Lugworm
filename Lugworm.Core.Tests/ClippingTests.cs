using System;
using System.Collections.Generic;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class ClippingTests
{
    static List<Vec3> Sq20() => new()
    { new(0,0,0), new(20,0,0), new(20,20,0), new(0,20,0) };

    [Fact]
    public void InwardOffset_ReturnsSmallerRing()
    {
        var inner = Offset.Polygons(new[] { Sq20() }, -2.0, 0.0);
        Assert.True(inner.Count >= 1 && inner[0].Count >= 4);
        double a = Math.Abs(PolylineOps.SignedAreaXy(inner[0]));
        Assert.True(a > 200.0 && a < 400.0);   // ~16x16 = 256
    }

    [Fact]
    public void ConcentricRings_AreNested()
    {
        var rings = Offset.ConcentricRings(new[] { Sq20() }, 2.0, 0.0);
        Assert.True(rings.Count >= 4);
    }

    [Fact]
    public void Zigzag_ReturnsPath_InBounds()
    {
        var zz = Infill.Zigzag(new[] { Sq20() }, spacing: 2.0, angleDeg: 0.0, z: 0.0);
        Assert.True(zz.Count > 4);
        Assert.All(zz, p => Assert.True(p.X >= -0.1 && p.X <= 20.1 && p.Y >= -0.1 && p.Y <= 20.1));
    }

    [Fact]
    public void ClipLines_KeepsOnlyInsideSpan()
    {
        var line = (new Vec3(-5, 10, 0), new Vec3(25, 10, 0));
        var segs = Infill.ClipLines(new[] { line }, new[] { Sq20() });
        Assert.Single(segs);
        double lo = Math.Min(segs[0].Item1.X, segs[0].Item2.X);
        double hi = Math.Max(segs[0].Item1.X, segs[0].Item2.X);
        Assert.Equal(0.0, lo, 3);
        Assert.Equal(20.0, hi, 3);
    }

    [Fact]
    public void InfillPatterns_SkipNullAndDegenerateRings()
    {
        IReadOnlyList<Vec3>[] boundary = { null!, new List<Vec3> { new(0, 0, 0) }, Sq20() };

        var zigzag = Infill.ZigzagSpans(boundary, 2.0, 0.0, 0.0, out var zigzagDiagnostic);
        var wave = Infill.WaveSpans(boundary, 2.0, 0.0, 0.0, new SineProfile(),
                                    new Infill.WaveOptions(), out var waveDiagnostic);

        Assert.Null(zigzagDiagnostic);
        Assert.Null(waveDiagnostic);
        Assert.NotEmpty(zigzag);
        Assert.NotEmpty(wave);
    }
}
