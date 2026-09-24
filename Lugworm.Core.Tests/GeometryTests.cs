using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class GeometryTests
{
    static List<Vec3> Square() => new()
    {
        new(0,0,0), new(10,0,0), new(10,10,0), new(0,10,0), new(0,0,0)
    };

    [Fact]
    public void ContourLength_Square_Is40()
        => Assert.Equal(40.0, PolylineOps.ContourLength(Square()), 6);

    [Fact]
    public void SignedArea_Ccw_IsPositive()
        => Assert.True(PolylineOps.SignedAreaXy(Square()) > 0);

    [Fact]
    public void EnsureCcw_FlipsClockwiseInput()
    {
        var rev = new List<Vec3>(Square());
        rev.Reverse();
        Assert.True(PolylineOps.SignedAreaXy(PolylineOps.EnsureCcw(rev)) > 0);
    }

    [Fact]
    public void Lerp_Midpoint()
    {
        var m = PolylineOps.Lerp(new(0, 0, 0), new(10, 0, 0), 0.5);
        Assert.Equal(5.0, m.X, 9);
    }

    [Fact]
    public void RoundCorners_Square_RoundsAndStaysClosed()
    {
        var sq = Square();
        var r = PolylineOps.RoundCorners(sq, 2.0, closed: true);
        Assert.True(r.Count > sq.Count);                               // corners -> arcs
        Assert.True(PolylineOps.Dist(r[0], r[^1]) < 1e-9);             // still closed
        Assert.DoesNotContain(r, p => PolylineOps.Dist(p, new Vec3(10, 0, 0)) < 1e-9);  // sharp vertex gone
        Assert.Contains(r, p => PolylineOps.Dist(p, new Vec3(10, 0, 0)) < 2.0 + 1e-6);  // fillet within radius
    }

    [Fact]
    public void RoundCorners_ZeroRadius_Unchanged()
        => Assert.Equal(Square().Count, PolylineOps.RoundCorners(Square(), 0.0, closed: true).Count);

    [Fact]
    public void CumulativeLengths_Monotone()
    {
        var cl = PolylineOps.CumulativeLengths(Square());
        Assert.Equal(0.0, cl[0], 9);
        Assert.Equal(40.0, cl[^1], 6);
    }

    [Fact]
    public void Rdp_CollapsesCollinearToEndpoints()
    {
        var line = new List<Vec3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) };
        Assert.Equal(2, Rdp.Simplify(line, 0.01).Count);
    }

    [Fact]
    public void Rdp_DegenerateClosedRing_ReturnsEmptyNotThrow()
    {
        // slicing plane grazing a mesh edge: a "closed ring" of 2 distinct vertices
        var sliver = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(0, 0, 0) };
        Assert.Empty(Rdp.Simplify(sliver, 0.05));
        // and a ring collapsed onto a single point
        var point = new List<Vec3> { new(1, 1, 0), new(1, 1, 0), new(1, 1, 0), new(1, 1, 0) };
        Assert.Empty(Rdp.Simplify(point, 0.05));
    }

    [Fact]
    public void Rdp_SubTolClosedRing_KeptUnsimplified()
    {
        // hair-thin but valid ring: thinning would collapse it below 3 distinct
        // vertices - it must survive unsimplified instead of throwing
        var thin = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(5, 0.001, 0), new(0, 0, 0) };
        var r = Rdp.Simplify(thin, 0.05);
        Assert.Equal(4, r.Count);
        Assert.True(PolylineOps.Dist(r[0], r[^1]) < 1e-9);   // still closed
    }

    [Fact]
    public void Resample_KeepsLengthAndUniformCount()
    {
        var line = new List<Vec3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) };
        var rs = Resample.ByLength(line, 1.0);
        Assert.Equal(4, rs.Count);
        Assert.Equal(3.0, PolylineOps.ContourLength(rs), 6);
    }
}
