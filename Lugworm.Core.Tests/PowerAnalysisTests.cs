using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Monte-Carlo power (2.3.0). Exact power values are not hand-derivable,
/// but the estimator's calibration properties are: at effect 0 the rejection rate
/// IS the α-level by definition, a many-σ effect is essentially always detected,
/// power is monotone in effect size, and a fixed seed is reproducible.</summary>
[Collection(ExclusivePerformanceCollection.Name)]
public class PowerAnalysisTests
{
    [Fact]
    public void Replicates_RaisePowerAtFixedEffect()
    {
        var p = DoeDesign.FullFactorial2(new[] { new FactorDef("A", -1, 1), new FactorDef("B", -1, 1) });
        PowerResult Estimate() => PowerAnalysis.MonteCarloPower(p.Actual, p.FactorNames,
            p.Lows, p.Highs, null, RsmModelType.Linear, 1, new[] { 2.0, 2.0 }, nSims: 400, seed: 42);
        var before = Estimate(); p.Replicate(5); var after = Estimate();
        Assert.True(after.Power[0] > before.Power[0] + 0.3);
        Assert.True(after.Power[1] > before.Power[1] + 0.3);
        var model = RsmFitter.Fit("y", p.FactorNames, p.Lows, p.Highs, null,
            p.Actual, p.Actual.Select(x => x[0] + x[1]).ToArray(), RsmModelType.Linear);
        Assert.Equal(17, model.DfResidual);
    }

    // D5 (2026-09-14): see ParetoTests / PerfReference for the convention -
    // wall-clock budgets below are K * this, not a fixed number of seconds.
    static readonly Lazy<double> s_referenceMs = new(PerfReference.MeasureMs);

    // 2² factorial replicated 5× (20 runs): orthogonal, well-conditioned, df = 17
    static double[][] Design()
        => Enumerable.Range(0, 5).SelectMany(_ => new[]
        {
            new[] { -1.0, -1.0 }, new[] { 1.0, -1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 1.0 },
        }).ToArray();

    static PowerResult Run(double effectA, double effectB, int seed = 12345, int sims = 400)
        => PowerAnalysis.MonteCarloPower(Design(), new[] { "A", "B" },
            new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, null,
            RsmModelType.Linear, sigma: 1.0, new[] { effectA, effectB },
            nSims: sims, alpha: 0.05, seed: seed);

    [Fact]
    public void ZeroEffect_PowerIsTheAlphaLevel()
    {
        // With no true effect, "power" is the type-I error rate, 0.05 by
        // construction. 400 sims → MC standard error √(0.05×0.95/400) ≈ 0.011;
        // the band below is ±6 of those.
        var r = Run(0, 0);
        Assert.InRange(r.Power[0], 0.0, 0.12);
        Assert.InRange(r.Power[1], 0.0, 0.12);
    }

    [Fact]
    public void LargeEffect_IsEssentiallyAlwaysDetected()
    {
        // Δ(lo→hi) = 3σ on a 20-run orthogonal design: se(β) = σ/√20 ≈ 0.224σ and
        // the coded coefficient is 1.5σ ≈ 6.7 standard errors - detection is near
        // certain.
        var r = Run(3.0, 3.0);
        Assert.True(r.Power[0] >= 0.95, $"power A = {r.Power[0]}");
        Assert.True(r.Power[1] >= 0.95, $"power B = {r.Power[1]}");
    }

    [Fact]
    public void Power_IsMonotoneInEffectSize()
    {
        double small = Run(0.4, 0).Power[0];
        double medium = Run(0.8, 0).Power[0];
        double large = Run(1.6, 0).Power[0];
        Assert.True(small < medium + 0.05, $"{small} vs {medium}");   // MC slack
        Assert.True(medium < large + 0.05, $"{medium} vs {large}");
        Assert.True(large > small);
    }

    [Fact]
    public void SameSeed_IsReproducible()
    {
        var a = Run(0.8, 0.4, seed: 7);
        var b = Run(0.8, 0.4, seed: 7);
        Assert.Equal(a.Power[0], b.Power[0], 12);
        Assert.Equal(a.Power[1], b.Power[1], 12);
    }

    [Fact]
    public void QuadraticModelOnTwoLevelDesign_ReportsTheDrop()
    {
        // A² / B² are aliased with the intercept on a 2-level design; the fitter
        // drops them and the power result says so instead of hiding it - this is
        // the reason the simulation refits with the SHIPPED fitter.
        var r = PowerAnalysis.MonteCarloPower(Design(), new[] { "A", "B" },
            new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, null,
            RsmModelType.Quadratic, sigma: 1.0, new[] { 1.0, 1.0 },
            nSims: 50, alpha: 0.05, seed: 1);
        Assert.Contains("dropped", r.Notes);
        Assert.False(double.IsNaN(r.Power[0]));   // main effects still estimated
    }

    [Fact]
    public void InvalidInputs_Refused()
    {
        Assert.Throws<ArgumentException>(() => PowerAnalysis.MonteCarloPower(Design(),
            new[] { "A", "B" }, new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, null,
            RsmModelType.Linear, sigma: 0.0, new[] { 1.0, 1.0 }));
        Assert.Throws<ArgumentException>(() => PowerAnalysis.MonteCarloPower(Design(),
            new[] { "A", "B" }, new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, null,
            RsmModelType.Linear, sigma: 1.0, new[] { 1.0 }));   // effect-count mismatch
    }

    // D4 (4.4.0): PowerAnalysis.Report is a GH panel string, not UI - it must stay
    // dot-decimal under a German ambient culture, same fixture pattern as
    // CultureInvarianceTests.
    [Fact]
    public void Report_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var r = Run(0.8, 0.4, seed: 7);
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = PowerAnalysis.Report(r);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string german = PowerAnalysis.Report(r);
            Assert.Equal(invariant, german);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void FourHundredSims_WithinBudget()
    {
        // D5: budget = 90 * reference - see ParetoTests for the convention.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Run(1.0, 0.5);
        sw.Stop();
        double budgetMs = 90 * s_referenceMs.Value;
        Assert.True(sw.Elapsed.TotalMilliseconds < budgetMs,
            $"400 power sims took {sw.Elapsed.TotalMilliseconds:0}ms (budget {budgetMs:0}ms = 90x reference {s_referenceMs.Value:0}ms)");
    }
}
