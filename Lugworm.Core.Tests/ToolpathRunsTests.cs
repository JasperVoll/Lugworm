using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Finding V2, test-round row 10a.8: the Live View renderer showed a different
/// program than the file - the first segment of every extrusion run was missing, a run made
/// of a single segment vanished, travel/seam/coast moves were never drawn at all, and a
/// mid-file G28 was drawn as a straight commanded move that the file never contained.
///
/// The cause was reading a per-SEGMENT flag list as if it were per-point: ext[k] describes
/// the segment ENDING at point k, and the old walk skipped every point whose own flag was
/// false, which throws away the segment that ENTERS each run.</summary>
public class ToolpathRunsTests
{
    // ---- helpers -------------------------------------------------------------------

    static List<string> Program(params string[] lines)
    {
        var l = new List<string> { "G21", "G90", "M83" };
        l.AddRange(lines);
        return l;
    }

    static (List<Vec3> pts, List<bool> ext, List<double> feeds, List<bool> breaks)
        Sample(IEnumerable<string> lines, Vec3? home = null)
        => MoveSampler.SampleWithBreaks(GcodeParser.Parse(lines, home).Moves);

    static ToolpathRunSet BuildFrom(IEnumerable<string> lines, Vec3? home = null)
    {
        var s = Sample(lines, home);
        return ToolpathRunBuilder.Build(s.pts, s.ext, s.breaks);
    }

    static void AssertSame(Vec3 expected, Vec3 actual, double tol = 1e-9)
        => Assert.True(PolylineOps.Dist(expected, actual) <= tol,
                       "expected " + expected + " but was " + actual);

    static bool Has(ToolpathRun run, Vec3 p)
    {
        for (int i = 0; i < run.Points.Count; i++)
            if (PolylineOps.Dist(run.Points[i], p) <= 1e-9) return true;
        return false;
    }

    /// <summary>Total drawn segments. Every segment the file commands has to appear in
    /// exactly one run, so this is the "nothing was lost" invariant.</summary>
    static int SegmentCount(ToolpathRunSet set)
    {
        int n = 0;
        foreach (var run in set.Runs) n += run.Points.Count - 1;
        return n;
    }

    static double MaxDeviation(IReadOnlyList<Vec3> original, IReadOnlyList<Vec3> simplified)
    {
        double worst = 0;
        foreach (var p in original)
        {
            double best = double.MaxValue;
            for (int i = 1; i < simplified.Count; i++)
                best = Math.Min(best, PolylineOps.PointSegDist(p, simplified[i - 1], simplified[i]));
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    /// <summary>A move built straight from coordinates, so a discontinuity can be expressed
    /// that the modeled G-code subset cannot produce on its own.</summary>
    static ParsedMove Seg(Vec3 from, Vec3 to, bool extrude, double feed = 600)
        => new ParsedMove(to.X, to.Y, to.Z, feed, extrude, false, 0, 0, 1, extrude ? 1.0 : 0.0,
                          new GcodeCoordinateState(to, default, to, 0, 0, 0),
                          from.X, from.Y, from.Z, from.X, from.Y, from.Z, 0, 0, 0);

    // ---- 1. the exact regression ----------------------------------------------------

    [Fact]
    public void SingleExtrusionSegment_SurvivesAsATwoPointPrintRun()
    {
        // Synthetic: three points is the smallest shape of the defect. The old point-walk
        // kept only the points whose own flag was true - here just the last one - and then
        // dropped the run for having fewer than two points.
        var points = new List<Vec3> { new Vec3(0, 0, 0.2), new Vec3(10, 0, 0.2) };
        var ext = new List<bool> { false, true };
        var breaks = new List<bool> { true, false };

        var set = ToolpathRunBuilder.Build(points, ext, breaks);

        var run = Assert.Single(set.Runs);
        Assert.Equal(ToolpathRunKind.Print, run.Kind);
        Assert.Equal(2, run.Points.Count);
        AssertSame(points[0], run.Points[0]);
        AssertSame(points[1], run.Points[1]);
    }

    [Fact]
    public void SingleExtrusionSegment_FromParsedGcode_SurvivesWithBothEndpoints()
    {
        var set = BuildFrom(Program("G1 X10 Y0 Z0 F600 E1"));

        var run = Assert.Single(set.Runs);
        Assert.Equal(ToolpathRunKind.Print, run.Kind);
        AssertSame(new Vec3(0, 0, 0), run.Points[0]);
        AssertSame(new Vec3(10, 0, 0), run.Points[run.Points.Count - 1]);
    }

    // ---- 2. kinds, order, shared boundaries, nothing lost ---------------------------

    [Fact]
    public void TravelPrintCoastTravel_KeepsKindsInOrderAndSharesBoundaryPoints()
    {
        var sampled = Sample(Program(
            "G0 X0 Y0 Z0.2 F3000",        // travel down to the start
            "G1 X10 Y0 Z0.2 F600 E1",     // print
            "G1 X20 Y0 Z0.2 F600 E1",     // print
            "G1 X25 Y0 Z0.2 F600",        // coast: commanded motion, no material
            "G0 X40 Y10 Z0.2 F3000"));    // travel away
        var set = ToolpathRunBuilder.Build(sampled.pts, sampled.ext, sampled.breaks);

        Assert.Equal(new[] { ToolpathRunKind.Travel, ToolpathRunKind.Print, ToolpathRunKind.Travel },
                     set.Runs.Select(r => r.Kind).ToArray());

        // Every commanded segment is drawn exactly once, and no run invents one.
        Assert.Equal(sampled.pts.Count - 1, SegmentCount(set));

        // The boundary point belongs to BOTH neighbours, so the two runs touch instead of
        // leaving a one-segment hole where the styling changes.
        AssertSame(set.Runs[0].Points[set.Runs[0].Points.Count - 1], set.Runs[1].Points[0]);
        AssertSame(set.Runs[1].Points[set.Runs[1].Points.Count - 1], set.Runs[2].Points[0]);

        AssertSame(new Vec3(0, 0, 0.2), set.Runs[1].Points[0]);
        AssertSame(new Vec3(20, 0, 0.2), set.Runs[1].Points[set.Runs[1].Points.Count - 1]);
    }

    // ---- 3. closed contour ----------------------------------------------------------

    [Fact]
    public void ClosedContour_KeepsItsClosingPoint()
    {
        var set = BuildFrom(Program(
            "G0 X0 Y0 Z0.2 F3000",
            "G1 X10 Y0 F600 E1",
            "G1 X10 Y10 F600 E1",
            "G1 X0 Y10 F600 E1",
            "G1 X0 Y0 F600 E1"));

        var print = Assert.Single(set.Runs.Where(r => r.Kind == ToolpathRunKind.Print));
        Assert.Equal(5, print.Points.Count);
        AssertSame(new Vec3(0, 0, 0.2), print.Points[0]);
        AssertSame(new Vec3(0, 0, 0.2), print.Points[print.Points.Count - 1]);
        Assert.True(PolylineOps.IsClosed(print.Points, 1e-9));
    }

    // ---- 4. layer transition --------------------------------------------------------

    [Fact]
    public void VerticalLayerTransition_IsATravelRunNotAHole()
    {
        var set = BuildFrom(Program(
            "G0 X0 Y0 Z0.2 F3000",
            "G1 X10 Y0 Z0.2 F600 E1",
            "G0 X10 Y0 Z0.4 F3000",       // the layer change: pure Z
            "G1 X0 Y0 Z0.4 F600 E1"));

        Assert.Equal(new[] { ToolpathRunKind.Travel, ToolpathRunKind.Print,
                             ToolpathRunKind.Travel, ToolpathRunKind.Print },
                     set.Runs.Select(r => r.Kind).ToArray());

        var lift = set.Runs[2];
        Assert.Equal(2, lift.Points.Count);
        AssertSame(new Vec3(10, 0, 0.2), lift.Points[0]);
        AssertSame(new Vec3(10, 0, 0.4), lift.Points[1]);
    }

    // ---- 5. arcs --------------------------------------------------------------------

    [Fact]
    public void ArcMove_IsTessellatedIntoOneRun()
    {
        var set = BuildFrom(Program(
            "G0 X10 Y0 Z0.2 F3000",
            "G3 X-10 Y0 I-10 J0 F600 E5"));   // half circle, r = 10, about the origin

        var print = Assert.Single(set.Runs.Where(r => r.Kind == ToolpathRunKind.Print));
        Assert.True(print.Points.Count > 5, "the arc must be tessellated, not drawn as a chord");
        AssertSame(new Vec3(10, 0, 0.2), print.Points[0]);
        AssertSame(new Vec3(-10, 0, 0.2), print.Points[print.Points.Count - 1]);
        foreach (var p in print.Points)
            Assert.True(Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - 10.0) < 1e-6,
                        "every tessellated point stays on the commanded radius");
    }

    // ---- 6. islands -----------------------------------------------------------------

    [Fact]
    public void TwoIslands_DoNotJoinIntoOnePrintRun()
    {
        var set = BuildFrom(Program(
            "G0 X0 Y0 Z0.2 F3000",
            "G1 X10 Y0 Z0.2 F600 E1",     // island A
            "G0 X50 Y0 Z0.2 F3000",       // travel between islands
            "G1 X60 Y0 Z0.2 F600 E1"));   // island B

        var prints = set.Runs.Where(r => r.Kind == ToolpathRunKind.Print).ToList();
        Assert.Equal(2, prints.Count);
        Assert.False(Has(prints[0], new Vec3(50, 0, 0.2)));
        Assert.False(Has(prints[1], new Vec3(10, 0, 0.2)));

        // The travel between them IS in the file, so it is drawn - as travel.
        var travel = set.Runs.Single(r => r.Kind == ToolpathRunKind.Travel && Has(r, new Vec3(50, 0, 0.2)));
        AssertSame(new Vec3(10, 0, 0.2), travel.Points[0]);
        AssertSame(new Vec3(50, 0, 0.2), travel.Points[travel.Points.Count - 1]);
    }

    // ---- 7. breaks: G28 and a coordinate reset --------------------------------------

    [Fact]
    public void MidFileG28_BreaksTheRunAndDrawsNoConnectingSegment()
    {
        // A calibrated home pose is what makes the jump visible at all: the position after
        // G28 is the home pose, not wherever the previous move ended.
        var home = new Vec3(5, 5, 50);
        var sampled = Sample(Program(
            "G28",
            "G0 X0 Y0 Z0.2 F3000",
            "G1 X10 Y0 Z0.2 F600 E1",
            "G28",                          // <- the machine goes home; the route is not in the file
            "G0 X50 Y0 Z0.2 F3000",
            "G1 X60 Y0 Z0.2 F600 E1"), home);

        int jump = sampled.pts.FindLastIndex(p => PolylineOps.Dist(p, home) <= 1e-9);
        Assert.True(jump > 0);
        Assert.True(sampled.breaks[jump], "the first point after an unresolved G28 must be flagged");
        Assert.True(sampled.breaks[0], "the very first point has no segment before it");
        Assert.Equal(2, sampled.breaks.Count(b => b));

        var set = ToolpathRunBuilder.Build(sampled.pts, sampled.ext, sampled.breaks);
        Assert.Contains(set.Runs, run => Has(run, home) && Has(run, new Vec3(50, 0, 0.2)));
        foreach (var run in set.Runs)
            Assert.False(Has(run, new Vec3(10, 0, 0.2)) && Has(run, new Vec3(50, 0, 0.2)),
                         "no run may span the break - that segment is not in the file");

        // and nothing is drawn between the two, in either direction
        foreach (var run in set.Runs)
            for (int i = 1; i < run.Points.Count; i++)
            {
                bool bridge = PolylineOps.Dist(run.Points[i - 1], new Vec3(10, 0, 0.2)) <= 1e-9
                           && PolylineOps.Dist(run.Points[i], new Vec3(50, 0, 0.2)) <= 1e-9;
                Assert.False(bridge);
            }
    }

    [Fact]
    public void CoordinateResetThatDisplacesTheStart_BreaksTheRun()
    {
        // G92 in the modeled subset re-labels coordinates without moving the machine, so it
        // correctly produces NO break (see G92_ReLabelsWithoutMoving). The rule the sampler
        // actually enforces is about the geometry: a move whose start is not where the last
        // one ended was reached by motion the file does not describe.
        var moves = new List<ParsedMove>
        {
            Seg(new Vec3(0, 0, 0.2), new Vec3(10, 0, 0.2), extrude: true),
            Seg(new Vec3(40, 0, 0.2), new Vec3(50, 0, 0.2), extrude: true),   // start displaced
        };
        var s = MoveSampler.SampleWithBreaks(moves);

        Assert.Equal(new[] { true, false, true, false }, s.breakBefore.ToArray());

        var set = ToolpathRunBuilder.Build(s.pts, s.ext, s.breakBefore);
        Assert.Equal(2, set.Runs.Count);
        AssertSame(new Vec3(0, 0, 0.2), set.Runs[0].Points[0]);
        AssertSame(new Vec3(10, 0, 0.2), set.Runs[0].Points[1]);
        AssertSame(new Vec3(40, 0, 0.2), set.Runs[1].Points[0]);
        AssertSame(new Vec3(50, 0, 0.2), set.Runs[1].Points[1]);
        foreach (var run in set.Runs)
            Assert.False(Has(run, new Vec3(10, 0, 0.2)) && Has(run, new Vec3(50, 0, 0.2)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.02)]
    public void LegacyConsumersKeepTheirIndicesAcrossDisplacedStarts(double? tolerance)
    {
        var moves = new[]
        {
            Seg(new Vec3(0, 0, 0), new Vec3(10, 0, 0), true),
            Seg(new Vec3(40, 0, 0), new Vec3(50, 0, 0), false),
        };
        var expected = HistoricalSampler(moves, tolerance);
        var actual = MoveSampler.SampleLegacy(moves, tolerance);
        Assert.Equal(expected.pts, actual.pts);
        Assert.Equal(expected.ext, actual.ext);
        Assert.Equal(expected.feeds, actual.feeds);
    }

    [Fact]
    public void G92_ReLabelsWithoutMoving_SoNoBreakIsInvented()
    {
        var sampled = Sample(Program(
            "G1 X10 Y0 Z0.2 F600 E1",
            "G92 X0 Y0",                    // datum shift; the machine does not move
            "G1 X5 Y0 Z0.2 F600 E1"));      // logical X5 == physical X15

        Assert.Equal(new[] { true, false, false }, sampled.breaks.ToArray());
        AssertSame(new Vec3(10, 0, 0.2), sampled.pts[1]);
        AssertSame(new Vec3(15, 0, 0.2), sampled.pts[2]);

        var set = ToolpathRunBuilder.Build(sampled.pts, sampled.ext, sampled.breaks);
        var print = Assert.Single(set.Runs.Where(r => r.Kind == ToolpathRunKind.Print));
        Assert.True(Has(print, new Vec3(10, 0, 0.2)) && Has(print, new Vec3(15, 0, 0.2)),
                    "the segment across a G92 is real commanded motion and must stay drawn");
    }

    // ---- 8. bounded-error display simplification ------------------------------------

    static ToolpathRunSet BigSet(out IReadOnlyList<Vec3> longRun, out IReadOnlyList<Vec3> shortRun)
    {
        var big = new List<Vec3>(6000);
        for (int i = 0; i < 6000; i++) big.Add(new Vec3(i * 0.1, 5.0 * Math.Sin(i * 0.02), 0.2));
        var small = new List<Vec3>
        {
            new Vec3(1000, 50, 0.2), new Vec3(1001, 50, 0.2), new Vec3(1002, 50.4, 0.2),
        };
        longRun = big; shortRun = small;
        return new ToolpathRunSet(new[]
        {
            new ToolpathRun(ToolpathRunKind.Print, big),
            new ToolpathRun(ToolpathRunKind.Travel, small),
        });
    }

    [Fact]
    public void OverBudget_IsSimplifiedWithinTolerance_KeepsEveryRunAndItsEndpoints()
    {
        var set = BigSet(out var longRun, out var shortRun);
        const int budget = 400;
        const double maxDev = 0.5;

        var drawn = ToolpathRunBuilder.Simplify(set, budget, maxDev);

        Assert.Equal(set.Runs.Count, drawn.Runs.Count);
        Assert.True(drawn.PointCount <= budget,
                    "budget " + budget + " but drew " + drawn.PointCount);
        Assert.True(drawn.PointCount < set.PointCount);
        Assert.NotNull(drawn.DetailNote);
        Assert.Contains("mm", drawn.DetailNote!, StringComparison.Ordinal);
        Assert.Contains(drawn.PointCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        drawn.DetailNote!, StringComparison.Ordinal);

        for (int i = 0; i < drawn.Runs.Count; i++)
        {
            var before = set.Runs[i].Points;
            var after = drawn.Runs[i].Points;
            Assert.Equal(set.Runs[i].Kind, drawn.Runs[i].Kind);
            Assert.True(after.Count >= 2);
            AssertSame(before[0], after[0]);
            AssertSame(before[before.Count - 1], after[after.Count - 1]);
            Assert.True(MaxDeviation(before, after) <= maxDev + 1e-9,
                        "run " + i + " deviated by " + MaxDeviation(before, after));
        }

        // A short run inside a large job is not erased by the thinning of the large one:
        // it keeps its kind, both its ends and at least one drawable segment.
        Assert.Equal(ToolpathRunKind.Travel, drawn.Runs[1].Kind);
        Assert.True(drawn.Runs[1].Points.Count >= 2);
        AssertSame(shortRun[0], drawn.Runs[1].Points[0]);
        AssertSame(shortRun[shortRun.Count - 1], drawn.Runs[1].Points[drawn.Runs[1].Points.Count - 1]);
        Assert.Equal(6000, longRun.Count);
    }

    [Fact]
    public void BudgetThatCannotBeMet_KeepsMaxToleranceAndSaysSo()
    {
        var set = BigSet(out _, out _);
        const double maxDev = 0.5;

        var drawn = ToolpathRunBuilder.Simplify(set, pointBudget: 10, maxDeviationMm: maxDev);

        Assert.Equal(set.Runs.Count, drawn.Runs.Count);
        Assert.True(drawn.PointCount > 10, "the budget genuinely cannot be met at this tolerance");
        Assert.True(drawn.PointCount < set.PointCount);
        Assert.NotNull(drawn.DetailNote);
        Assert.Contains("0.5", drawn.DetailNote!, StringComparison.Ordinal);
        for (int i = 0; i < drawn.Runs.Count; i++)
            Assert.True(MaxDeviation(set.Runs[i].Points, drawn.Runs[i].Points) <= maxDev + 1e-9);
    }

    [Fact]
    public void WithinBudget_IsReturnedUnchangedAndUnannotated()
    {
        var set = BuildFrom(Program(
            "G0 X0 Y0 Z0.2 F3000",
            "G1 X10 Y0 Z0.2 F600 E1"));

        var drawn = ToolpathRunBuilder.Simplify(set, 40000, 0.5);

        Assert.Same(set, drawn);
        Assert.Null(drawn.DetailNote);
    }

    [Fact]
    public void ClosedRunOverBudget_KeepsItsSeamAndStaysClosed()
    {
        // Rdp's own closed-ring path re-seams the ring at the vertex farthest from the
        // centroid. A print run's seam is the point it shares with the travel that reaches
        // it, so moving it would tear a hole between two runs that touch in the file.
        var ring = new List<Vec3>(721);
        for (int i = 0; i <= 720; i++)
        {
            double a = i * Math.PI / 360.0;
            ring.Add(new Vec3(20 * Math.Cos(a), 20 * Math.Sin(a), 0.2));
        }
        var set = new ToolpathRunSet(new[] { new ToolpathRun(ToolpathRunKind.Print, ring) });

        var drawn = ToolpathRunBuilder.Simplify(set, 100, 0.5);

        var run = Assert.Single(drawn.Runs);
        AssertSame(ring[0], run.Points[0]);
        AssertSame(ring[ring.Count - 1], run.Points[run.Points.Count - 1]);
        Assert.True(PolylineOps.IsClosed(run.Points, 1e-9));
        Assert.True(MaxDeviation(ring, run.Points) <= 0.5 + 1e-9);
    }

    // ---- 9. the builder is frame-agnostic -------------------------------------------

    [Fact]
    public void Builder_IsFrameAgnostic_MachineInMachineOut()
    {
        var machine = new List<Vec3>
        {
            new Vec3(0, 0, 0.2), new Vec3(10, 0, 0.2), new Vec3(20, 5, 0.2), new Vec3(30, 5, 0.4),
        };
        var ext = new List<bool> { false, true, true, false };
        var breaks = new List<bool> { true, false, false, false };
        var rhino = machine.Select(MachineFrame.ToRhino).ToList();

        var a = ToolpathRunBuilder.Build(machine, ext, breaks);
        var b = ToolpathRunBuilder.Build(rhino, ext, breaks);

        // Same topology either way - the builder never converts and never assumes a frame.
        Assert.Equal(a.Runs.Select(r => r.Kind).ToArray(), b.Runs.Select(r => r.Kind).ToArray());
        Assert.Equal(a.Runs.Select(r => r.Points.Count).ToArray(),
                     b.Runs.Select(r => r.Points.Count).ToArray());
        // and the points come back exactly as they went in
        for (int i = 0; i < a.Runs.Count; i++)
            for (int k = 0; k < a.Runs[i].Points.Count; k++)
            {
                AssertSame(b.Runs[i].Points[k], MachineFrame.ToRhino(a.Runs[i].Points[k]));
                Assert.Contains(machine, p => PolylineOps.Dist(p, a.Runs[i].Points[k]) <= 1e-12);
            }
    }

    // ---- MoveSampler parity with the historical sampler ------------------------------

    /// <summary>The historical algorithm, written out from the sampler it replaces: only the
    /// FIRST move contributes its start point, every later point carries its own move's
    /// extrude flag and feed. Recomputed here rather than snapshotted, so the parity check
    /// cannot be satisfied by agreeing with the new code.</summary>
    static (List<Vec3> pts, List<bool> ext, List<double> feeds)
        HistoricalSampler(IReadOnlyList<ParsedMove> moves, double? sharedArcTol)
    {
        var pts = new List<Vec3>();
        var ext = new List<bool>();
        var feeds = new List<double>();
        foreach (var parsed in moves)
        {
            var p0 = new Vec3(parsed.StartX, parsed.StartY, parsed.StartZ);
            var p1 = new Vec3(parsed.X, parsed.Y, parsed.Z);
            Move move = Move.Line(p0, p1);
            if (parsed.Arc)
            {
                double cx = p0.X + parsed.I, cy = p0.Y + parsed.J;
                double r = Math.Sqrt((p0.X - cx) * (p0.X - cx) + (p0.Y - cy) * (p0.Y - cy));
                if (r > 1e-9 && !double.IsNaN(r)) move = Move.Arc(p0, p1, cx, cy, parsed.G == 2, r);
            }
            var poly = sharedArcTol.HasValue
                ? RobotChecks.SampleMovePath(parsed, sharedArcTol.Value)
                : ArcTessellation.ToPolyline(move, 0.05, 4096);
            if (pts.Count == 0) { pts.Add(poly[0]); ext.Add(false); feeds.Add(0.0); }
            for (int i = 1; i < poly.Count; i++)
            { pts.Add(poly[i]); ext.Add(parsed.Extrude); feeds.Add(parsed.F > 0 ? parsed.F / 60.0 : 0.0); }
        }
        return (pts, ext, feeds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.02)]
    [InlineData(0.2)]
    public void MoveSampler_MatchesTheHistoricalSampler_OnAMixedProgram(double? sharedArcTol)
    {
        var moves = GcodeParser.Parse(Program(
            "G0 X10 Y0 Z0.2 F3000",
            "G1 X20 Y0 Z0.2 F600 E1",
            "G3 X20 Y20 I0 J10 F600 E3",
            "G2 X20 Y0 I0 J-10 F600 E3",
            "G1 X0 Y0 Z0.2 F600",
            "G0 X0 Y0 Z5 F3000")).Moves;

        var expected = HistoricalSampler(moves, sharedArcTol);
        var actual = MoveSampler.SampleLegacy(moves, sharedArcTol);

        Assert.Equal(expected.pts.Count, actual.pts.Count);
        for (int i = 0; i < expected.pts.Count; i++)
        {
            Assert.Equal(expected.pts[i].X, actual.pts[i].X);
            Assert.Equal(expected.pts[i].Y, actual.pts[i].Y);
            Assert.Equal(expected.pts[i].Z, actual.pts[i].Z);
        }
        Assert.Equal(expected.ext, actual.ext);
        Assert.Equal(expected.feeds, actual.feeds);
        Assert.Equal(actual.pts.Count, actual.breakBefore.Count);
        // a continuous program breaks only at its very first point
        Assert.Equal(1, actual.breakBefore.Count(b => b));
        Assert.True(actual.breakBefore[0]);
    }

    [Fact]
    public void MoveSampler_EmptyProgram_IsEmpty()
    {
        var s = MoveSampler.SampleWithBreaks(Array.Empty<ParsedMove>());
        Assert.Empty(s.pts);
        Assert.Empty(s.breakBefore);
        Assert.Same(ToolpathRunSet.Empty, ToolpathRunBuilder.Build(s.pts, s.ext, s.breakBefore));
    }
}
