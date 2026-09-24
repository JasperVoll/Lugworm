using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Non-dominated sorting and thinning (2.3.0). Fixtures are small enough
/// to verify by inspection; the derivations are in the comments.</summary>
[Collection(ExclusivePerformanceCollection.Name)]
public class ParetoTests
{
    [Fact]
    public void Computed_ResponseHasNaNPiHalf()
    {
        var (up, down, goals) = AntiCorrelatedSetup(); up.IsComputed = true;
        var r = DesirabilityOptimizer.Optimize(new[] { up, down }, goals, gridPerDim: 5, collectFront: true);
        Assert.All(r.Front, p => { Assert.True(double.IsNaN(p.PiHalf[0])); Assert.True(p.Computed[0]); });
        Assert.True(ParetoFrontSnapshot.TryParse(r.Front.Select(p => (IReadOnlyList<double>)p.ToBranch()).ToArray(), up.FactorNames,
            new[] { "up", "down" }, out var snapshot, out var error), error);
        var state = ParetoSelection.Select(ParetoSelectionState.Empty, snapshot!, 0);
        state = ParetoSelection.Confirm(state, new[] { 100.0, 0.0 });
        Assert.Contains("up: computed, not checked", ParetoSelection.ConfirmationReport(state));
        Assert.True(ParetoArchive.TryRead(ParetoArchive.Write(state, snapshot, null), out var copy, out _, out _, out _));
        Assert.True(copy.Recipe!.Reliability!.Computed[0]);
    }

    [Fact]
    public void Collect_FillsPiHalfAndLeverage_MatchesRsmInference()
    {
        var x = new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 } };
        var m = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null,
            x, new[] { 0.0, 2.0, 4.0, 6.0 }, RsmModelType.Linear);
        var result = DesirabilityOptimizer.Optimize(new[] { m }, new[] { Goal.Parse("max", "y", 0, 8) }, collectFront: true);
        Assert.NotEmpty(result.Front);
        foreach (var pt in result.Front)
        {
            var band = RsmInference.PredictionInterval(m, pt.X);
            Assert.Equal((band.Hi - band.Lo) / 2, pt.PiHalf[0], 12);
            Assert.Equal(band.Lo, pt.PiLo[0]); Assert.Equal(band.Hi, pt.PiHi[0]);
            Assert.Equal(RsmInference.Leverage(m, pt.X), pt.Leverage, 12);
        }
    }

    [Fact]
    public void Extrapolation_FlagsPointsBeyondTrainingLeverage()
    {
        var x = new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 } };
        var fitted = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null,
            x, new[] { 0.0, 2.0, 4.0, 6.0 }, RsmModelType.Linear);
        var m = RsmModel.FromJson(fitted.ToJson()); // leverage threshold survives dropping the raw runs
        Assert.Equal(0.5, m.MaxTrainingLeverage, 12);
        var inside = new ParetoPoint { X = new[] { 0.0 } }; DesirabilityOptimizer.CollectReliability(inside, new[] { m });
        var outside = new ParetoPoint { X = new[] { 2.0 } }; DesirabilityOptimizer.CollectReliability(outside, new[] { m });
        Assert.Equal(0.25, inside.Leverage, 12); Assert.False(inside.ExtrapolationFlag);
        Assert.Equal(1.25, outside.Leverage, 12); Assert.True(outside.ExtrapolationFlag);
    }

    [Fact]
    public void Optimize_MaskedRegionNeverAppearsOnTheFront()
    {
        var x = new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 } };
        var response = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null,
            x, new[] { 0.0, 0.0, 1.0, 1.0 }, RsmModelType.Linear);
        var mask = RsmFitter.FitFeasibility(response.FactorNames, response.Lows, response.Highs, null,
            x, new[] { 1.0, 1.0, 0.0, 0.0 });
        var goals = new[] { Goal.Parse("max", "y", 0, 1) };
        var free = DesirabilityOptimizer.Optimize(new[] { response }, goals, collectFront: true);
        var constrained = DesirabilityOptimizer.Optimize(new[] { response, mask }, goals, collectFront: true);
        Assert.True(free.XBest[0] > 0.99);
        Assert.InRange(constrained.XBest[0], -0.01, 1e-10);
        Assert.NotEmpty(constrained.Front);
        Assert.All(constrained.Front, pt => Assert.True(pt.Feasible >= 0.5));
        mask.Terms[0].Coef = 0; mask.Terms[1].Coef = 0;
        var empty = DesirabilityOptimizer.Optimize(new[] { response, mask }, goals, collectFront: true);
        Assert.Empty(empty.Front); Assert.Equal(0, empty.DOverall);
    }

    // D5 (2026-09-14): one reference measurement per class; every [Trait("Category",
    // "Perf")] budget below is K * this, not a fixed number of seconds - see
    // PerfReference and docs/4-unit-tests/TESTING.md.
    static readonly Lazy<double> s_referenceMs = new(PerfReference.MeasureMs);

    static ParetoPoint P(double dOverall, params double[] d)
        => new ParetoPoint { X = new[] { dOverall }, D = d, DOverall = dOverall };

    [Fact]
    public void TwoObjectives_HandCheckedFront()
    {
        // (0.7,0.2) is dominated by (0.8,0.5); (0.4,0.4) by (0.5,0.8) and (0.8,0.5).
        // The four staircase points dominate nothing among themselves.
        var pts = new List<ParetoPoint>
        {
            P(1, 1.0, 0.0), P(2, 0.8, 0.5), P(3, 0.5, 0.8), P(4, 0.0, 1.0),
            P(5, 0.4, 0.4), P(6, 0.7, 0.2),
        };
        var front = ParetoFront.NonDominated(pts);
        Assert.Equal(4, front.Count);
        Assert.DoesNotContain(front, f => f.DOverall == 5 || f.DOverall == 6);
    }

    [Fact]
    public void ZeroDesirability_CanStillBeOnTheFront()
    {
        // The motivating regression: d₁ = 0 annihilates the scalarised D, but
        // (1, 0) is the best available on objective 1 and must appear.
        var pts = new List<ParetoPoint> { P(0, 1.0, 0.0), P(1, 0.5, 0.5) };
        var front = ParetoFront.NonDominated(pts);
        Assert.Equal(2, front.Count);
        Assert.Contains(front, f => f.D[0] == 1.0 && f.D[1] == 0.0);
    }

    [Fact]
    public void Duplicates_CollapseToTheHigherDOverall()
    {
        var pts = new List<ParetoPoint> { P(1, 0.6, 0.6), P(9, 0.6, 0.6), P(2, 0.6, 0.6) };
        var front = ParetoFront.NonDominated(pts);
        Assert.Single(front);
        Assert.Equal(9, front[0].DOverall);
    }

    [Fact]
    public void OneDominatingPoint_FrontOfOne()
    {
        var pts = new List<ParetoPoint> { P(1, 0.9, 0.9), P(2, 0.5, 0.8), P(3, 0.8, 0.1) };
        var front = ParetoFront.NonDominated(pts);
        Assert.Single(front);
        Assert.Equal(1, front[0].DOverall);
    }

    [Fact]
    public void Degenerate_SingleCandidate_And_SingleObjective()
    {
        Assert.Single(ParetoFront.NonDominated(new List<ParetoPoint> { P(1, 0.3, 0.4) }));

        // k = 1: the front is the single best point
        var pts = new List<ParetoPoint> { P(1, 0.2), P(2, 0.9), P(3, 0.5) };
        var front = ParetoFront.NonDominated(pts);
        Assert.Single(front);
        Assert.Equal(0.9, front[0].D[0], 12);
    }

    [Fact]
    public void Thin_RespectsCapAndKeepsEveryObjectiveArgmax()
    {
        // 7 points on the line d₁ + d₂ = 1: all non-dominated. Thinning to 3 must
        // keep both endpoints (the per-objective argmaxes), whatever else it drops.
        var front = Enumerable.Range(0, 7)
            .Select(i => P(i, i / 6.0, 1 - i / 6.0)).ToList();
        var thinned = ParetoFront.Thin(front, 3);
        Assert.True(thinned.Count <= 3);
        Assert.Contains(thinned, f => f.D[0] == 1.0);
        Assert.Contains(thinned, f => f.D[1] == 1.0);
    }

    [Fact]
    public void Optimize_XBestLiesOnTheFront()
    {
        // The maximiser of a positively-weighted geometric mean is Pareto optimal,
        // so XBest must appear on the collected front. Two competing linear models
        // over one factor: y1 = x (maximise) vs y2 = -x (maximise) - every point of
        // [-1, 1] is non-dominated and the front spans the whole trade-off.
        RsmModel Line(double slope) => RsmFitter.Fit("y" + slope, new[] { "A" },
            new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 },
            new[] { new[] { -1.0 }, new[] { -0.5 }, new[] { 0.5 }, new[] { 1.0 } },
            new[] { -slope, -0.5 * slope, 0.5 * slope, slope }, RsmModelType.Linear);
        var models = new[] { Line(1.0), Line(-1.0) };
        var goals = new[]
        {
            Goal.Parse("max", "y1", -1, 1),
            Goal.Parse("max", "y-1", -1, 1),
        };
        var res = DesirabilityOptimizer.Optimize(models, goals, collectFront: true, maxFront: 50);
        Assert.True(res.DOverall > 0);
        Assert.NotEmpty(res.Front);
        Assert.Contains(res.Front, pt =>
            Math.Abs(pt.X[0] - res.XBest[0]) < 1e-9);
        Assert.Equal(2, res.FrontObjectives.Length);
    }

    [Fact]
    public void Optimize_FrontEndpointsAreRefinedExtremes_NotGridCells()
    {
        // One factor A on [-1, 1], grid of 5: A in {-1, -0.5, 0, 0.5, 1}. y1 = A with
        // target=0.3 in -1..1 peaks at A = 0.3, which is not a grid point: on the grid
        // d1 is (0 + 1) / (0.3 + 1) = 0.769 at A = 0 and (1 - 0.5) / (1 - 0.3) = 0.714
        // at A = 0.5. y2 = -A maximised over -1..1 gives d2 = (1 - A) / 2, so the two
        // trade off and the front runs from A = -1 (d2 = 1) toward A = 0.3 (d1 = 1).
        // Refining the d1 argmax alone must put A = 0.3 with d1 = 1 on the front; the
        // d2 argmax is already on its plateau boundary at the grid point A = -1.
        RsmModel Line(string name, double slope) => RsmFitter.Fit(name, new[] { "A" },
            new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 },
            new[] { new[] { -1.0 }, new[] { -0.5 }, new[] { 0.5 }, new[] { 1.0 } },
            new[] { -slope, -0.5 * slope, 0.5 * slope, slope }, RsmModelType.Linear);
        var models = new[] { Line("y1", 1.0), Line("y2", -1.0) };
        var goals = new[]
        {
            Goal.Parse("target=0.3 in -1..1", "y1", -1, 1),
            Goal.Parse("max", "y2", -1, 1),
        };
        var res = DesirabilityOptimizer.Optimize(models, goals, gridPerDim: 5, collectFront: true, maxFront: 50);
        var peak = Assert.Single(res.Front, pt => pt.D[0] > 0.999);
        Assert.Equal(0.3, peak.X[0], 3);                       // off the grid, at the target
        Assert.Contains(res.Front, pt => pt.D[1] > 0.999 && Math.Abs(pt.X[0] + 1) < 1e-9);
        // the extreme is what the report names for that objective
        string report = DesirabilityOptimizer.Report(res, models, goals);
        Assert.Contains("best y1: d=1.00 at A=0.3", report);
        Assert.Contains("each objective's extreme are Nelder-Mead refined", report);
        // no refinement is inserted where it cannot improve: a single plateau goal keeps
        // its grid extreme, so the front is still one grid point
        var plateau = DesirabilityOptimizer.Optimize(new[] { Line("y2", -1.0) },
            new[] { Goal.Parse("max", "y2", -1, 1) }, gridPerDim: 5, collectFront: true);
        var only = Assert.Single(plateau.Front);
        Assert.Equal(-1.0, only.X[0], 9);
    }

    [Fact]
    public void Optimize_FrontOffByDefault()
    {
        RsmModel m = RsmFitter.Fit("y", new[] { "A" },
            new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 },
            new[] { new[] { -1.0 }, new[] { 0.0 }, new[] { 1.0 } },
            new[] { 0.0, 1.0, 2.0 }, RsmModelType.Linear);
        var res = DesirabilityOptimizer.Optimize(new[] { m },
            new[] { Goal.Parse("max", "y", -1, 1) });
        Assert.Empty(res.Front);
    }

    // Worst case for the streaming archive: two exactly opposed responses make
    // every distinct grid point non-dominated, so without interim thinning the
    // reduction re-sorts the whole archive per insert (quadratic). 3 factors at
    // 60/dim = 216k evaluations, every d-vector distinct.
    static (RsmModel up, RsmModel down, Goal[] goals) AntiCorrelatedSetup()
    {
        double[][] xs =
        {
            new[] { -1.0, -1.0, -1.0 }, new[] { 1.0, -1.0, 1.0 },
            new[] { -1.0, 1.0, 0.5 }, new[] { 1.0, 1.0, -0.5 },
        };
        double[] Y(double sign) => xs.Select(r => sign * (r[0] + 0.1 * r[1] + 0.01 * r[2])).ToArray();
        var up = RsmFitter.Fit("up", new[] { "A", "B", "C" },
            new[] { -1.0, -1.0, -1.0 }, new[] { 1.0, 1.0, 1.0 }, null, xs, Y(1), RsmModelType.Linear);
        var down = RsmFitter.Fit("down", new[] { "A", "B", "C" },
            new[] { -1.0, -1.0, -1.0 }, new[] { 1.0, 1.0, 1.0 }, null, xs, Y(-1), RsmModelType.Linear);
        var goals = new[]
        {
            Goal.Parse("max", "up", -2, 2),
            Goal.Parse("max", "down", -2, 2),
        };
        return (up, down, goals);
    }

    [Fact]
    public void Optimize_AntiCorrelatedResponses_HugeFront_FrontSizeAndThinning()
    {
        // Functional half of the split (D5): must always run, regardless of the
        // Category=Perf filter, so the thinning behavior stays covered even when
        // the timing companion below is excluded.
        var (up, down, goals) = AntiCorrelatedSetup();
        var res = DesirabilityOptimizer.Optimize(new[] { up, down }, goals,
            gridPerDim: 60, collectFront: true, maxFront: 200);
        Assert.NotEmpty(res.Front);
        Assert.True(res.Front.Count <= 201);   // thinned (+1 if XBest was re-inserted)
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Optimize_AntiCorrelatedResponses_HugeFront_WithinBudget()
    {
        // D5: budget = 180 * reference. On the 2026-09-14 dev machine the
        // reference is ~110ms, so this reproduces the prior fixed 20s budget at
        // rest; it grows automatically when the machine is contended, which is
        // exactly what let this test fail once at 24.4s under load (see
        // docs/4-unit-tests/TESTING.md).
        var (up, down, goals) = AntiCorrelatedSetup();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = DesirabilityOptimizer.Optimize(new[] { up, down }, goals,
            gridPerDim: 60, collectFront: true, maxFront: 200);
        sw.Stop();
        Assert.InRange(result.InteriorRefinementEvaluations, 1, 200 * 200);
        double budgetMs = 180 * s_referenceMs.Value;
        Assert.True(sw.Elapsed.TotalMilliseconds < budgetMs,
            $"anti-correlated front collection took {sw.Elapsed.TotalMilliseconds:0}ms " +
            $"(budget {budgetMs:0}ms = 180x reference {s_referenceMs.Value:0}ms)");
    }

    [Fact]
    public void Refine_InteriorPointsLeaveTheGrid_OnAntiCorrelatedResponses()
    {
        var (up, down, goals) = AntiCorrelatedSetup();
        var r = DesirabilityOptimizer.Optimize(new[] { up, down }, goals, gridPerDim: 5, collectFront: true, maxFront: 20);
        bool OffGrid(ParetoPoint p) => p.X.Any(x => Math.Abs((x + 1) * 2 - Math.Round((x + 1) * 2)) > 1e-5);
        Assert.Contains(r.Front, p => OffGrid(p) && p.X.Zip(r.XBest, (a, b) => Math.Abs(a - b)).Any(d => d > 1e-5));
        Assert.InRange(r.InteriorRefinementEvaluations, 1, 20 * 200);
    }

    [Fact]
    public void Refine_NeverProducesADominatedRow()
    {
        var (up, down, goals) = AntiCorrelatedSetup();
        var r = DesirabilityOptimizer.Optimize(new[] { up, down }, goals, gridPerDim: 7, collectFront: true, maxFront: 30);
        foreach (var a in r.Front)
            foreach (var b in r.Front)
                Assert.False(a.D.Zip(b.D, (x, y) => x >= y).All(v => v) && a.D.Zip(b.D, (x, y) => x > y + 1e-9).Any(v => v));
    }

    [Fact]
    public void NonDominated_500kBy4_ProducesValidFront()
    {
        // Functional half of the split (D5): always runs.
        var front = NonDominated500kBy4();
        Assert.NotEmpty(front);
        // sanity: no front member dominates another (spot-check the definition)
        for (int a = 0; a < Math.Min(front.Count, 50); a++)
            for (int b = 0; b < Math.Min(front.Count, 50); b++)
                if (a != b)
                    Assert.False(Enumerable.Range(0, 4).All(j => front[a].D[j] >= front[b].D[j] + 1e-9));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void NonDominated_500kBy4_WithinBudget()
    {
        // D5: budget = 140 * reference (see Optimize_AntiCorrelatedResponses_..
        // above for the convention).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        NonDominated500kBy4();
        sw.Stop();
        double budgetMs = 140 * s_referenceMs.Value;
        Assert.True(sw.Elapsed.TotalMilliseconds < budgetMs,
            $"500k x 4 non-dominated sort took {sw.Elapsed.TotalMilliseconds:0}ms " +
            $"(budget {budgetMs:0}ms = 140x reference {s_referenceMs.Value:0}ms)");
    }

    // the desirability grid cap is 500k points; 4 objectives is a realistic
    // multi-response study. Includes the streaming-reduction pattern the
    // optimizer uses (batches of 20k). Same seed every call so the functional
    // and timing halves of the split exercise the identical archive.
    static List<ParetoPoint> NonDominated500kBy4()
    {
        var rnd = new Random(42);
        var batch = new List<ParetoPoint>(20_000);
        List<ParetoPoint> archive = new List<ParetoPoint>();
        for (int i = 0; i < 500_000; i++)
        {
            var d = new double[4];
            for (int j = 0; j < 4; j++) d[j] = rnd.NextDouble();
            batch.Add(new ParetoPoint { X = new[] { (double)i }, D = d, DOverall = d.Sum() });
            if (batch.Count == 20_000)
            {
                archive.AddRange(batch);
                archive = ParetoFront.NonDominated(archive);
                batch.Clear();
            }
        }
        archive.AddRange(batch);
        return ParetoFront.NonDominated(archive);
    }
}
