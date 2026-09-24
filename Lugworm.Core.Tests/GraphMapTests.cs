using System;
using System.Linq;
using Lugworm.Core.Analysis;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The Graph Mapper's curve families (2.4.0). Expected values are worked
/// by hand from the closed forms in the comments.</summary>
public class GraphMapTests
{
    [Fact]
    public void Linear_DefaultIsIdentity()
    {
        var g = GraphMap.Default(GraphMapKind.Linear);
        foreach (double t in new[] { 0.0, 0.25, 0.5, 1.0 })
            Assert.Equal(t, g.Evaluate(t), 12);
    }

    [Fact]
    public void Linear_CanInvert()
    {
        var g = GraphMap.Default(GraphMapKind.Linear);
        g.P[0] = 1; g.P[1] = 0;              // y = 1 − t
        Assert.Equal(1.0, g.Evaluate(0), 12);
        Assert.Equal(0.25, g.Evaluate(0.75), 12);
    }

    [Fact]
    public void Power_MatchesClosedForm()
    {
        var g = GraphMap.Default(GraphMapKind.Power);
        Assert.Equal(0.5, g.Evaluate(0.5), 12);       // e = 1 → identity
        g.P[0] = 2;
        Assert.Equal(0.25, g.Evaluate(0.5), 12);      // 0.5² = 0.25
        g.P[0] = 0.5;
        Assert.Equal(Math.Sqrt(0.5), g.Evaluate(0.5), 12);   // the Square Root preset
    }

    [Fact]
    public void Gaussian_PeaksAtMu_AndSigmaPointIsExpHalf()
    {
        var g = GraphMap.Default(GraphMapKind.Gaussian);
        g.P[0] = 0.4; g.P[1] = 0.9; g.P[2] = 0.1;
        Assert.Equal(0.9, g.Evaluate(0.4), 12);
        // at t = mu + sigma: h×e^(−1/2)
        Assert.Equal(0.9 * Math.Exp(-0.5), g.Evaluate(0.5), 12);
    }

    [Fact]
    public void Sigmoid_IsHalfAtMidpoint_AndSaturates()
    {
        var g = GraphMap.Default(GraphMapKind.Sigmoid);
        g.P[0] = 0.3; g.P[1] = 12;
        Assert.Equal(0.5, g.Evaluate(0.3), 12);
        Assert.True(g.Evaluate(1.0) > 0.99);
        Assert.True(g.Evaluate(0.0) < 0.03);
    }

    [Fact]
    public void Parabola_VertexAndCurvature()
    {
        var g = GraphMap.Default(GraphMapKind.Parabola);
        g.P[0] = 0.5; g.P[1] = 0.0; g.P[2] = 4.0;    // y = 4(t−0.5)²
        Assert.Equal(0.0, g.Evaluate(0.5), 12);
        Assert.Equal(1.0, g.Evaluate(0.0), 12);
        Assert.Equal(0.25, g.Evaluate(0.75), 12);    // 4×0.0625
    }

    [Fact]
    public void Bezier_SymmetricControlsGiveIdentity()
    {
        // P1 = (1/3, 1/3), P2 = (2/3, 2/3): x(u) = u and y(u) = u exactly, so the
        // solved curve is the identity to bisection accuracy
        var g = GraphMap.Default(GraphMapKind.Bezier);
        foreach (double t in new[] { 0.0, 0.2, 0.5, 0.8, 1.0 })
            Assert.Equal(t, g.Evaluate(t), 9);
    }

    [Fact]
    public void Bezier_EaseShapeIsMonotoneAndHitsEndpoints()
    {
        var g = GraphMap.Default(GraphMapKind.Bezier);
        g.P[1] = 0.9; g.P[2] = 0.0; g.P[3] = 0.1; g.P[4] = 1.0;   // hard ease-in-out
        Assert.Equal(0.0, g.Evaluate(0), 9);
        Assert.Equal(1.0, g.Evaluate(1), 9);
        double prev = -1;
        for (int i = 0; i <= 40; i++)
        {
            double y = g.Evaluate(i / 40.0);
            Assert.True(y >= prev - 1e-9, $"non-monotone at {i / 40.0}");
            prev = y;
        }
    }

    [Fact]
    public void Sine_AmplitudeClampedToGraphArea()
    {
        var g = GraphMap.Default(GraphMapKind.Sine);
        g.P[0] = 2.0;   // wildly over-amplified - the unit clamp must hold
        for (int i = 0; i <= 40; i++)
        {
            double y = g.Evaluate(i / 40.0);
            Assert.InRange(y, 0.0, 1.0);
        }
    }

    [Fact]
    public void Perlin_DeterministicPerSeed_DifferentAcrossSeeds()
    {
        var a = GraphMap.Default(GraphMapKind.Perlin);
        var b = GraphMap.Default(GraphMapKind.Perlin);
        for (int i = 0; i <= 20; i++)
            Assert.Equal(a.Evaluate(i / 20.0), b.Evaluate(i / 20.0), 12);
        b.P[0] = 2;
        bool differs = Enumerable.Range(0, 21).Any(i =>
            Math.Abs(a.Evaluate(i / 20.0) - b.Evaluate(i / 20.0)) > 1e-6);
        Assert.True(differs);
    }

    [Fact]
    public void Map_RemapsDomains()
    {
        var g = GraphMap.Default(GraphMapKind.Linear);
        // [0,10] → identity curve → [100,200]: v=2.5 → t=0.25 → 125
        Assert.Equal(125.0, g.Map(2.5, 0, 10, 100, 200), 9);
        // clip on: out-of-domain input pins to the edge
        Assert.Equal(100.0, g.Map(-5, 0, 10, 100, 200), 9);
        Assert.Equal(200.0, g.Map(15, 0, 10, 100, 200), 9);
        // degenerate input domain: everything through t = 0.5
        Assert.Equal(150.0, g.Map(7, 3, 3, 100, 200), 9);
    }

    [Fact]
    public void Conic_CircleQuadrantAtA2()
    {
        // a = 2: y = 1 − √(1 − t²); at t = 0.6 → 1 − 0.8 = 0.2
        var g = GraphMap.Default(GraphMapKind.Conic);
        Assert.Equal(0.2, g.Evaluate(0.6), 12);
        Assert.Equal(0.0, g.Evaluate(0), 12);
        Assert.Equal(1.0, g.Evaluate(1), 12);
    }

    [Fact]
    public void Exponential_ClosedForm_AndIdentityLimit()
    {
        var g = GraphMap.Default(GraphMapKind.Exponential);
        g.P[0] = 1.0;
        // (e^0.5 − 1)/(e − 1) = 0.377540668…
        Assert.Equal((Math.Exp(0.5) - 1) / (Math.E - 1), g.Evaluate(0.5), 12);
        g.P[0] = 0.0;                     // the k → 0 limit is the identity
        Assert.Equal(0.5, g.Evaluate(0.5), 12);
        g.P[0] = -3.0;                    // negative k: log-like, above the diagonal
        Assert.True(g.Evaluate(0.5) > 0.5);
    }

    [Fact]
    public void Steps_QuantizesIntoEqualTreads()
    {
        var g = GraphMap.Default(GraphMapKind.Steps);   // n = 4: treads 0, 1/3, 2/3, 1
        Assert.Equal(0.0, g.Evaluate(0.1), 12);
        Assert.Equal(1.0 / 3, g.Evaluate(0.3), 12);
        Assert.Equal(2.0 / 3, g.Evaluate(0.6), 12);
        Assert.Equal(1.0, g.Evaluate(0.9), 12);
        Assert.Equal(1.0, g.Evaluate(1.0), 12);   // top edge stays on the last tread
    }

    [Fact]
    public void Polyline_InterpolatesAndHoldsEnds()
    {
        var g = GraphMap.Default(GraphMapKind.Polyline);
        g.P = new[] { 0.2, 0.4, 0.6, 0.8, 1.0, 0.0 };
        Assert.Equal(0.4, g.Evaluate(0.0), 12);          // before first point: hold
        Assert.Equal(0.6, g.Evaluate(0.4), 12);          // halfway 0.2→0.6: 0.4→0.8
        Assert.Equal(0.4, g.Evaluate(0.8), 12);          // halfway 0.6→1.0: 0.8→0.0
    }

    [Fact]
    public void Spline_PassesThroughPoints_MonotoneData_NoOvershoot()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        g.P = new[] { 0.0, 0.0, 0.3, 0.1, 0.7, 0.9, 1.0, 1.0 };
        foreach (var (t, y) in new[] { (0.0, 0.0), (0.3, 0.1), (0.7, 0.9), (1.0, 1.0) })
            Assert.Equal(y, g.Evaluate(t), 12);
        // Fritsch–Carlson guarantee: monotone data → monotone curve, bounded by
        // the neighbouring points (no Catmull-Rom-style overshoot)
        double prev = -1;
        for (int i = 0; i <= 100; i++)
        {
            double y = g.Evaluate(i / 100.0);
            Assert.True(y >= prev - 1e-9);
            prev = y;
        }
    }

    [Fact]
    public void CustomPoints_AddRemoveMove()
    {
        var g = GraphMap.Default(GraphMapKind.Polyline);   // (0,0)(0.5,0.5)(1,1)
        int at = g.AddPoint(0.25, 0.9);
        Assert.Equal(1, at);
        Assert.Equal(4, g.PointCount);
        Assert.Equal(0.9, g.Evaluate(0.25), 12);

        g.MovePoint(1, 0.75, 0.9);   // t clamped below its right neighbour (0.5)
        Assert.True(g.P[2] < 0.5);

        Assert.True(g.RemovePoint(1));
        Assert.Equal(3, g.PointCount);
        Assert.True(g.RemovePoint(1));
        Assert.False(g.RemovePoint(0));   // never below 2 points
        Assert.Equal(-1, GraphMap.Default(GraphMapKind.Power).AddPoint(0.5, 0.5));
    }

    [Fact]
    public void ToCustom_SamplesTheCurrentCurve()
    {
        var g = GraphMap.Default(GraphMapKind.Power);
        g.P[0] = 2;
        var custom = g.ToCustom(GraphMapKind.Polyline, samples: 5);
        Assert.Equal(GraphMapKind.Polyline, custom.Kind);
        Assert.Equal(5, custom.PointCount);
        Assert.Equal(0.25, custom.Evaluate(0.5), 12);     // sample sits on t = 0.5
        Assert.Throws<ArgumentException>(() => g.ToCustom(GraphMapKind.Power));
    }

    [Fact]
    public void CustomSerialization_KeepsVariableLength()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        g.AddPoint(0.4, 0.7);
        var r = GraphMap.Deserialize(g.Serialize());
        Assert.Equal(g.PointCount, r.PointCount);
        Assert.Equal(g.P, r.P);
        // structurally invalid stored vector (odd length) falls back to the default
        var bad = GraphMap.Deserialize("Polyline;0;0;1");
        Assert.Equal(GraphMap.Default(GraphMapKind.Polyline).P, bad.P);
    }

    [Fact]
    public void SerializeRoundTrips_AndOldVectorsKeepDefaults()
    {
        var g = GraphMap.Default(GraphMapKind.Gaussian);
        g.P[0] = 0.31; g.P[1] = 0.77; g.P[2] = 0.09;
        var r = GraphMap.Deserialize(g.Serialize());
        Assert.Equal(GraphMapKind.Gaussian, r.Kind);
        Assert.Equal(g.P, r.P);

        // a shorter stored vector (older build) keeps defaults for the tail
        var partial = GraphMap.Deserialize("Gaussian;0.2");
        Assert.Equal(0.2, partial.P[0], 12);
        Assert.Equal(GraphMap.Default(GraphMapKind.Gaussian).P[2], partial.P[2], 12);

        Assert.Throws<FormatException>(() => GraphMap.Deserialize("NotAKind;1;2"));
        Assert.Throws<FormatException>(() => GraphMap.Deserialize("Linear;abc"));
    }

    /// <summary>Row 3.2.4: out-of-domain inputs pin to the graph edges whether or
    /// not clip is on - Evaluate clamps t into the unit square by design, so both
    /// states map an input of 15 in [0,10] to the curve value at the right edge.</summary>
    [Fact]
    public void Map_OutOfDomainPinsToGraphEdge_WithAndWithoutClip()
    {
        var g = GraphMap.Default(GraphMapKind.Linear);
        double edge = g.Map(10, 0, 10, 100, 200);
        Assert.Equal(200.0, edge, 12);
        Assert.Equal(edge, g.Map(15, 0, 10, 100, 200, clip: true), 12);
        Assert.Equal(edge, g.Map(15, 0, 10, 100, 200, clip: false), 12);
        Assert.Equal(100.0, g.Map(-3, 0, 10, 100, 200, clip: false), 12);
    }

    // ── Spline tangent handles (4.0.0) ──

    /// <summary>Two points (0,0)-(1,1): the automatic tangents are both 1, so the
    /// Hermite segment is the identity. Flattening both handles turns it into the
    /// smoothstep 3u² − 2u³, whose value at u = 0.25 is 0.15625 by hand.</summary>
    [Fact]
    public void SplineHandles_AutomaticByDefault_SetHandleReshapesSegment()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        g.P = new[] { 0.0, 0.0, 1.0, 1.0 };
        Assert.False(g.HasUserHandle(0, GraphHandleSide.Out));
        Assert.Equal(0, g.UserHandleCount);
        Assert.Equal(1.0, g.HandleSlope(0, GraphHandleSide.Out), 12);
        Assert.Equal(0.25, g.Evaluate(0.25), 12);

        g.SetHandlePair(0, 0.0);
        g.SetHandlePair(1, 0.0);
        Assert.Equal(4, g.UserHandleCount);
        Assert.Equal(0.15625, g.Evaluate(0.25), 12);
        Assert.Equal(0.5, g.Evaluate(0.5), 12);
        // the points themselves are still interpolated
        Assert.Equal(0.0, g.Evaluate(0.0), 12);
        Assert.Equal(1.0, g.Evaluate(1.0), 12);
    }

    /// <summary>A point whose in and out handles differ is a corner: the left
    /// derivative follows the in handle, the right one the (still automatic) out
    /// tangent.</summary>
    [Fact]
    public void SplineHandles_DifferingSidesMakeACorner()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        g.P = new[] { 0.0, 0.0, 0.5, 0.5, 1.0, 1.0 };
        g.SetHandle(1, GraphHandleSide.In, 0.0);          // Shift-drag: one side only
        Assert.True(g.HasUserHandle(1, GraphHandleSide.In));
        Assert.False(g.HasUserHandle(1, GraphHandleSide.Out));
        const double e = 1e-4;
        double left = (g.Evaluate(0.5) - g.Evaluate(0.5 - e)) / e;
        double right = (g.Evaluate(0.5 + e) - g.Evaluate(0.5)) / e;
        Assert.InRange(left, -0.01, 0.01);
        Assert.InRange(right, 0.95, 1.05);
    }

    [Fact]
    public void SplineHandles_ClampAndReset()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        double auto = g.HandleSlope(1, GraphHandleSide.Out);
        g.SetHandle(1, GraphHandleSide.Out, 50);
        Assert.Equal(GraphMap.MaxHandleSlope, g.HandleSlope(1, GraphHandleSide.Out), 12);
        g.SetHandle(1, GraphHandleSide.Out, double.NaN);   // ignored, never "sets" NaN
        Assert.True(g.HasUserHandle(1, GraphHandleSide.Out));
        g.ResetHandle(1);
        Assert.False(g.HasUserHandle(1, GraphHandleSide.Out));
        Assert.Equal(auto, g.HandleSlope(1, GraphHandleSide.Out), 12);
        // out of range and non-spline kinds are no-ops
        g.SetHandle(99, GraphHandleSide.In, 1);
        var poly = GraphMap.Default(GraphMapKind.Polyline);
        poly.SetHandlePair(0, 2);
        Assert.Equal(0, poly.UserHandleCount);
        Assert.False(poly.HasHandles);
    }

    /// <summary>Handles travel with their points through insertions and removals.</summary>
    [Fact]
    public void SplineHandles_AddRemovePoint_KeepAlignment()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);          // 4 points
        g.SetHandlePair(2, 1.5);
        int at = g.AddPoint(0.1, 0.05);                         // inserted before point 2
        Assert.Equal(1, at);
        Assert.Equal(5, g.PointCount);
        Assert.Equal(g.P.Length, g.H.Length);
        Assert.False(g.HasUserHandle(1, GraphHandleSide.Out));
        Assert.True(g.HasUserHandle(3, GraphHandleSide.Out));
        Assert.Equal(1.5, g.HandleSlope(3, GraphHandleSide.In), 12);
        Assert.True(g.RemovePoint(0));
        Assert.True(g.HasUserHandle(2, GraphHandleSide.Out));
        Assert.Equal(g.P.Length, g.H.Length);
        // MovePoint leaves handles alone
        g.MovePoint(2, 0.6, 0.4);
        Assert.True(g.HasUserHandle(2, GraphHandleSide.Out));
    }

    /// <summary>The 2.5.0 text format is emitted unchanged while no handle is set
    /// (saved files keep round-tripping through older readers); with handles a
    /// trailing h= token carries them, empty entries meaning automatic.</summary>
    [Fact]
    public void SplineHandles_Serialization()
    {
        var g = GraphMap.Default(GraphMapKind.Spline);
        Assert.Equal("Spline;0;0;0.25;0.1;0.75;0.9;1;1", g.Serialize());

        g.SetHandle(1, GraphHandleSide.Out, 2.5);
        string text = g.Serialize();
        Assert.Equal("Spline;0;0;0.25;0.1;0.75;0.9;1;1;h=,,,2.5,,,,", text);
        var r = GraphMap.Deserialize(text);
        Assert.Equal(g.P, r.P);
        Assert.Equal(1, r.UserHandleCount);
        Assert.Equal(2.5, r.HandleSlope(1, GraphHandleSide.Out), 12);
        Assert.False(r.HasUserHandle(1, GraphHandleSide.In));
        Assert.Equal(g.Evaluate(0.4), r.Evaluate(0.4), 12);

        // pre-4.0.0 text: all automatic
        var old = GraphMap.Deserialize("Spline;0;0;0.5;0.5;1;1");
        Assert.Equal(0, old.UserHandleCount);
        Assert.Equal(3, old.PointCount);
        // a handle list that does not match the point list is dropped, not fatal
        var mismatch = GraphMap.Deserialize("Spline;0;0;1;1;h=1,2,3");
        Assert.Equal(2, mismatch.PointCount);
        Assert.Equal(0, mismatch.UserHandleCount);
        // a Polyline never emits handles and ignores a stray token
        var poly = GraphMap.Deserialize("Polyline;0;0;1;1;h=1,1,1,1");
        Assert.Equal(0, poly.UserHandleCount);
        Assert.DoesNotContain("h=", GraphMap.Default(GraphMapKind.Polyline).Serialize());

        // Copy is deep
        var copy = g.Copy();
        copy.ResetHandle(1);
        Assert.Equal(1, g.UserHandleCount);
        Assert.Equal(0, copy.UserHandleCount);
    }
}
