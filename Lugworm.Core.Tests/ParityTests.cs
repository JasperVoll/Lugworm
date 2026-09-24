using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

// Cross-implementation parity vs the reference Python engine (clay_core_v2).
// NOTE: parity is asserted only for modules whose algorithm is IMPLEMENTATION-
// IDENTICAL on both sides (arc fitting, RDP). Polygon offset / infill are NOT
// compared here: the Python dev env has no Clipper installed (HAS_CLIP=False),
// so Python falls back to a naive bisector offset while C# uses Clipper2 - the
// two are intentionally different. Offset/infill correctness is covered by the
// behavioral ClippingTests instead.
public class ParityTests
{
    static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "foundation.json");
        Assert.True(File.Exists(path), $"fixture missing: {path} - run tests/parity/dump_foundation_fixtures.py");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    static Vec3 PtFromJson(JsonElement a)
        => new Vec3(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());

    static List<Vec3> Circle(int count, double degStep, double r)
        => Enumerable.Range(0, count)
            .Select(k =>
            {
                double a = k * degStep * Math.PI / 180.0;
                return new Vec3(r * Math.Cos(a), r * Math.Sin(a), 0.0);
            })
            .ToList();

    [Fact]
    public void FitArcs_MatchesPython_TotalLength()
    {
        var fx = Load().GetProperty("fit_arcs_circle");
        var moves = ArcFit.FitArcs(Circle(41, 4.0, 20.0), tol: 0.05);
        double csTotal = moves.Sum(ArcFit.ArcLengthOf);
        double pyTotal = fx.GetProperty("total_len").GetDouble();
        Assert.Equal(pyTotal, csTotal, 6);   // same algorithm => near-exact
        Assert.Equal(fx.GetProperty("n_arcs").GetInt32(), moves.Count(m => m.Kind == MoveKind.Arc));
        Assert.Equal(fx.GetProperty("n_moves").GetInt32(), moves.Count);
    }

    [Fact]
    public void Rdp_MatchesPython_Output()
    {
        var fx = Load().GetProperty("rdp");
        var input = fx.GetProperty("input").EnumerateArray().Select(PtFromJson).ToList();
        double tol = fx.GetProperty("tol").GetDouble();
        var expected = fx.GetProperty("output").EnumerateArray().Select(PtFromJson).ToList();
        var actual = Rdp.Simplify(input, tol);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, 9);
            Assert.Equal(expected[i].Y, actual[i].Y, 9);
            Assert.Equal(expected[i].Z, actual[i].Z, 9);
        }
    }
}
