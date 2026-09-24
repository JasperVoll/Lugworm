using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Hand-derived fixtures for the 2.3.0 inference pack. Every expected
/// number below is worked out in the comments from the OLS definitions - no
/// third-party tool generated them.</summary>
public class RsmInferenceTests
{
    [Fact]
    public void Transform_IntervalsAreBackTransformedBoundByBound()
    {
        var plain = FitLine();
        var y = plain.Y.Select(v => Math.Exp(v) - 1).ToArray();
        var m = RsmFitter.Fit("y", plain.FactorNames, plain.Lows, plain.Highs, plain.Centres,
            plain.XActual, y, RsmModelType.Linear, ResponseTransform.Log1p);
        foreach (bool prediction in new[] { true, false })
        {
            var b = prediction ? RsmInference.PredictionInterval(plain, new[] { 0.0 }) : RsmInference.ConfidenceInterval(plain, new[] { 0.0 });
            var t = prediction ? RsmInference.PredictionInterval(m, new[] { 0.0 }) : RsmInference.ConfidenceInterval(m, new[] { 0.0 });
            Assert.Equal(Math.Exp(b.Lo) - 1, t.Lo, 8);
            Assert.Equal(Math.Exp(b.Hi) - 1, t.Hi, 6);
            Assert.True(t.Hi - t.Fit > t.Fit - t.Lo);
        }
        Assert.Equal(RsmInference.Diagnose(plain).Runs[0].Residual, RsmInference.Diagnose(m).Runs[0].Residual, 10);
    }

    // Single coded factor, duplicated levels:
    //   x = {-1,-1, 1, 1},  y = {0, 2, 4, 6}
    // OLS:  b1 = Σxy/Σx² = (0×-1 + 2×-1 + 4 + 6)/4 = 8/4 = 2,  b0 = ȳ = 3
    // fits {1,1,5,5} → residuals {-1,+1,-1,+1} → SSres = 4, dfRes = 2, MSE = 2,
    // RMSE = √2.  (XᵀX)⁻¹ = diag(1/4, 1/4).  h_i = 1/4 + 1/4 = 0.5 every run.
    // Cook's D_i = e²×h / (p×MSE×(1-h)²) = 1×0.5 / (2×2×0.25) = 0.5 every run.
    // Studentized e_i/(RMSE×√(1-h)) = ±1/(√2×√0.5) = ±1.
    static RsmModel FitLine()
        => RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 },
            new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 } },
            new[] { 0.0, 2.0, 4.0, 6.0 }, RsmModelType.Linear);

    [Fact]
    public void PredictionInterval_MatchesHandDerivation()
    {
        var m = FitLine();
        Assert.Equal(Math.Sqrt(2), m.Rmse, 9);

        // at x0 = 0: x0ᵀCx0 = 1/4 (intercept only), t(0.975, df=2) = 4.30265273
        // PI half-width = t×RMSE×√(1 + 1/4) = 4.30265273×√2×√1.25 = 6.8031
        var pi = RsmInference.PredictionInterval(m, new[] { 0.0 });
        Assert.Equal(3.0, pi.Fit, 9);
        Assert.Equal(3.0 - 6.8031, pi.Lo, 3);
        Assert.Equal(3.0 + 6.8031, pi.Hi, 3);

        // CI half-width = t×RMSE×√(1/4) = 4.30265273×√2×0.5 = 3.0425
        var ci = RsmInference.ConfidenceInterval(m, new[] { 0.0 });
        Assert.Equal(3.0 - 3.0425, ci.Lo, 3);
        Assert.Equal(3.0 + 3.0425, ci.Hi, 3);
        Assert.True(pi.Hi - pi.Lo > ci.Hi - ci.Lo);   // one new print is noisier than the mean
    }

    [Fact]
    public void Diagnostics_MatchHandDerivation()
    {
        var d = RsmInference.Diagnose(FitLine());
        Assert.Equal(4, d.Runs.Count);
        foreach (var r in d.Runs)
        {
            Assert.Equal(0.5, r.Leverage, 9);
            Assert.Equal(0.5, r.CookD, 9);
            Assert.Equal(1.0, Math.Abs(r.Studentized), 9);
            Assert.False(r.Influential);         // 0.5 < 1
            Assert.False(r.HighLeverage);        // cutoff 2p/n = 1
        }
        Assert.Equal(1.0, d.LeverageCutoff, 9);
    }

    [Fact]
    public void Leverage_FlagsTheIsolatedRun()
    {
        // x = {-1,-1, 1, 1, 4}: x̄ = 0.8, Σ(x-x̄)² = 3.24×2 + 0.04×2 + 10.24 = 16.8
        // h_extreme = 1/5 + 10.24/16.8 = 0.80952 > cutoff 2p/n = 0.8
        // h_others  = 1/5 + 3.24/16.8 = 0.39286 or 1/5 + 0.04/16.8 = 0.20238
        // (y carries a little jitter so the fit is not exact - leverage is a
        //  property of x alone, so the jitter does not enter these numbers)
        var m = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 },
            new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 }, new[] { 4.0 } },
            new[] { -1.1, -0.9, 0.9, 1.1, 4.0 }, RsmModelType.Linear);
        var d = RsmInference.Diagnose(m);
        Assert.Equal(0.80952, d.Runs[4].Leverage, 4);
        Assert.True(d.Runs[4].HighLeverage);
        Assert.All(d.Runs.Take(4), r => Assert.False(r.HighLeverage));
    }

    [Fact]
    public void Vif_OrthogonalDesignIsExactlyOne()
    {
        // 2² factorial: the two coded columns are orthogonal → VIF = 1 exactly
        var m = RsmFitter.Fit("y", new[] { "A", "B" },
            new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 0.0 },
            new[] { new[] { -1.0, -1.0 }, new[] { 1.0, -1.0 }, new[] { -1.0, 1.0 }, new[] { 1.0, 1.0 } },
            new[] { 0.0, 1.0, 2.0, 3.5 }, RsmModelType.Linear);
        var d = RsmInference.Diagnose(m);
        Assert.Equal(new[] { "A", "B" }, d.VifTerms);
        Assert.Equal(1.0, d.Vif[0], 9);
        Assert.Equal(1.0, d.Vif[1], 9);
    }

    [Fact]
    public void Vif_CorrelatedColumnsMatchHandDerivation()
    {
        // x1 = {-1,1,-1,1}, x2 = {-1,1,-0.2,0.2} (both mean 0):
        //   Σx1x2 = 2.4, Σx1² = 4, Σx2² = 2.08
        //   r² = 2.4²/(4×2.08) = 5.76/8.32 = 9/13
        //   VIF = 1/(1 - 9/13) = 13/4 = 3.25 exactly, for both columns
        var m = RsmFitter.Fit("y", new[] { "A", "B" },
            new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 0.0 },
            new[] { new[] { -1.0, -1.0 }, new[] { 1.0, 1.0 }, new[] { -1.0, -0.2 }, new[] { 1.0, 0.2 } },
            new[] { 0.0, 1.0, 2.0, 3.0 }, RsmModelType.Linear);
        var d = RsmInference.Diagnose(m);
        Assert.Equal(3.25, d.Vif[0], 9);
        Assert.Equal(3.25, d.Vif[1], 9);
    }

    [Fact]
    public void Intervals_SurviveJsonRoundTrip()
    {
        var m = FitLine();
        var restored = RsmModel.FromJson(m.ToJson());
        // the raw runs are not serialized, but the covariance is - intervals match
        Assert.Empty(restored.XActual);
        var a = RsmInference.PredictionInterval(m, new[] { 0.5 });
        var b = RsmInference.PredictionInterval(restored, new[] { 0.5 });
        Assert.Equal(a.Lo, b.Lo, 9);
        Assert.Equal(a.Hi, b.Hi, 9);
    }

    [Fact]
    public void Intervals_UnavailableOnPre230Json_NotGuessed()
    {
        var m = FitLine();
        // simulate a pre-2.3.0 JSON payload: same schema minus the cov block
        string json = m.ToJson();
        int covAt = json.IndexOf(",\"cov\":", StringComparison.Ordinal);
        string legacy = json.Substring(0, covAt) + "}";
        var restored = RsmModel.FromJson(legacy);
        var band = RsmInference.PredictionInterval(restored, new[] { 0.0 });
        Assert.Equal(3.0, band.Fit, 9);   // the point estimate still works
        Assert.False(band.Valid);          // the interval honestly does not
        var d = RsmInference.Diagnose(restored);
        Assert.Empty(d.Runs);
        Assert.Contains("unavailable", d.Notes);
    }

    // D4 (4.4.0): RsmModel's JSON round trip and the diagnostics report are machine
    // text (settings/report round-trip paths), not UI - both must stay dot-decimal
    // under a German ambient culture, same fixture pattern as CultureInvarianceTests.
    [Fact]
    public void JsonRoundTrip_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var m = FitLine();
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariantJson = m.ToJson();
            var invariantRestored = RsmModel.FromJson(invariantJson);
            var invariantPi = RsmInference.PredictionInterval(invariantRestored, new[] { 0.5 });

            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string germanJson = m.ToJson();
            var germanRestored = RsmModel.FromJson(germanJson);
            var germanPi = RsmInference.PredictionInterval(germanRestored, new[] { 0.5 });

            Assert.Equal(invariantJson, germanJson);
            Assert.Equal(invariantPi.Lo, germanPi.Lo, 9);
            Assert.Equal(invariantPi.Hi, germanPi.Hi, 9);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void DiagnosticsReport_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var d = RsmInference.Diagnose(FitLine());
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = RsmInference.Report(d);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string german = RsmInference.Report(d);
            Assert.Equal(invariant, german);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
