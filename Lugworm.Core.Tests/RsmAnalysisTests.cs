using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Doe/RsmAnalysis.cs. RsmFitter.Fit, RsmModel.Predict/ToJson/FromJson/
/// EquationActual and the Anova/DroppedTerms/R2Adj/Press/Rmse fields already have
/// direct coverage in DoeTests.RsmTests and RsmInferenceTests.cs (which also covers
/// RsmInference.cs, a separate file). This file adds the public entry points those
/// leave untouched: RsmModel.PredictCoded and ToCoded exercised directly against a
/// hand-built model (isolating the pure evaluation from the fitter's numerics),
/// RsmModel.EquationCoded's exact formatting, and RsmFitter.Report/RsmFitter.Stars,
/// which no existing test calls at all.</summary>
public class RsmAnalysisTests
{
    [Fact]
    public void Computed_FlagRoundTripsJson()
    {
        var m = OneFactorModel(); m.IsComputed = true;
        var copy = RsmModel.FromJson(m.ToJson());
        Assert.True(copy.IsComputed); Assert.Equal(m.Predict(new[] { 0.2 }), copy.Predict(new[] { 0.2 }));
        Assert.Contains("computed response, no measurement error", RsmFitter.Report(copy));
        Assert.False(RsmInference.PredictionInterval(copy, new[] { 0.0 }).Valid);
        Assert.False(RsmInference.ConfidenceInterval(copy, new[] { 0.0 }).Valid);
    }

    [Fact]
    public void Transform_Log1p_FitsExactExponentialSurface()
    {
        var x = Enumerable.Range(0, 9).Select(i => new[] { (i - 4) / 4.0 }).ToArray();
        var y = x.Select(r => Math.Exp(2 + 0.4 * r[0] + 0.2 * r[0] * r[0]) - 1).ToArray();
        var m = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null, x, y,
            RsmModelType.Quadratic, ResponseTransform.Log1p);
        Assert.Equal(2, m.Terms[0].Coef, 10);
        Assert.Equal(0.4, m.Terms[1].Coef, 10);
        Assert.Equal(Math.Exp(2 + 0.4 * 0.3 + 0.2 * 0.09) - 1, m.Predict(new[] { 0.3 }), 10);
        Assert.InRange(m.Rmse, 0, 1e-12);
    }

    [Fact]
    public void Transform_BoxCox_LambdaRecoversKnownValue()
    {
        // sqrt(y) = 3+x, so lambda=0.5 gives 2*(sqrt(y)-1)=4+2x exactly.
        var x = Enumerable.Range(0, 21).Select(i => new[] { (i - 10) / 10.0 }).ToArray();
        var y = x.Select(r => Math.Pow(3 + r[0], 2)).ToArray();
        var m = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null, x, y,
            RsmModelType.Linear, ResponseTransform.BoxCox);
        Assert.Equal(0.5, m.Lambda, 10);
        Assert.Equal(12.25, m.Predict(new[] { 0.5 }), 9);
    }

    [Theory]
    [InlineData(ResponseTransform.None)]
    [InlineData(ResponseTransform.Log1p)]
    [InlineData(ResponseTransform.Sqrt)]
    [InlineData(ResponseTransform.BoxCox)]
    public void Transform_RoundTripsJson(ResponseTransform transform)
    {
        var m = OneFactorModel(); m.Transform = transform; m.Lambda = 0.3;
        var copy = RsmModel.FromJson(m.ToJson());
        Assert.Equal(transform, copy.Transform); Assert.Equal(0.3, copy.Lambda);
        Assert.Equal(m.Predict(new[] { 0.2 }), copy.Predict(new[] { 0.2 }), 12);
    }

    [Fact]
    public void Transform_ReportIsCultureInvariant()
    {
        var m = OneFactorModel(); m.Transform = ResponseTransform.BoxCox; m.Lambda = 0.3;
        string expected = RsmFitter.Report(m);
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(expected, RsmFitter.Report(m)); }
        finally { CultureInfo.CurrentCulture = old; }
        Assert.Contains("lambda = 0.3", expected);
        Assert.Contains("back-transformed bound by bound", expected);
    }

    static RsmTerm Term(string label, double coef, params int[] powers)
        => new RsmTerm { Label = label, Coef = coef, Powers = powers };

    // A hand-built model, not the output of Fit(): isolates PredictCoded/ToCoded/
    // EquationCoded from the fitter's own numerics.
    static RsmModel OneFactorModel() => new RsmModel
    {
        ResponseName = "y",
        FactorNames = new[] { "spd" },
        Lows = new[] { -1.0 },
        Highs = new[] { 1.0 },
        Centres = new[] { 0.0 },
        Terms = new List<RsmTerm> { Term("1", 3, 0), Term("A", 2, 1) },
    };

    static RsmModel TwoFactorModel() => new RsmModel
    {
        ResponseName = "y",
        FactorNames = new[] { "spd", "dep" },
        Lows = new[] { -1.0, -1.0 },
        Highs = new[] { 1.0, 1.0 },
        Centres = new[] { 0.0, 0.0 },
        Terms = new List<RsmTerm>
        {
            Term("1", 3, 0, 0),
            Term("A", 2, 1, 0),
            Term("B", -4, 0, 1),
        },
    };

    [Theory]
    [InlineData(0.0, 3.0)]
    [InlineData(1.0, 5.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(0.5, 4.0)]
    public void PredictCoded_SumsCoefficientTimesTermEvaluation(double xCoded, double expected)
    {
        var m = OneFactorModel();
        Assert.Equal(expected, m.PredictCoded(new[] { xCoded }), 12);
    }

    [Fact]
    public void PredictCoded_TwoFactors_EvaluatesEachTermAgainstItsOwnFactor()
    {
        var m = TwoFactorModel();
        // 3 + 2*0.5 - 4*(-0.25) = 3 + 1 + 1 = 5
        Assert.Equal(5.0, m.PredictCoded(new[] { 0.5, -0.25 }), 12);
    }

    [Theory]
    [InlineData(0.0, 0.0)]   // centre -> coded 0
    [InlineData(1.0, 1.0)]   // high -> coded 1
    [InlineData(-1.0, -1.0)] // low -> coded -1
    [InlineData(0.5, 0.5)]   // midpoint coding is linear between centre and high
    public void ToCoded_MapsLowCentreHighToMinusOneZeroOne(double actual, double expectedCoded)
    {
        var m = OneFactorModel();
        Assert.Equal(expectedCoded, m.ToCoded(new[] { actual })[0], 12);
    }

    [Fact]
    public void Predict_IsEquivalentToToCodedThenPredictCoded()
    {
        var m = OneFactorModel();
        Assert.Equal(m.PredictCoded(m.ToCoded(new[] { 1.0 })), m.Predict(new[] { 1.0 }), 12);
        Assert.Equal(5.0, m.Predict(new[] { 1.0 }), 12);
    }

    [Fact]
    public void EquationCoded_StartsWithResponseNameAndInterceptOmittingTheMultiplySign()
    {
        var eq = OneFactorModel().EquationCoded();
        // intercept term (label "1") never gets a "×1" suffix
        Assert.StartsWith("y = 3", eq);
        Assert.DoesNotContain("×1", eq);
    }

    [Fact]
    public void EquationCoded_FormatsPositiveAndNegativeTermsWithTheirLabels()
    {
        var eq = TwoFactorModel().EquationCoded();
        Assert.Contains("y = 3 + 2×A", eq);       // "×" between coefficient and label
        Assert.Contains("4×B", eq);                // magnitude only; sign is the separate glyph before it
        Assert.Contains(" − 4×B", eq);         // "− " (U+2212 minus) precedes a negative term
    }

    [Fact]
    public void EquationCoded_ListsOneFactorLegendLinePerFactorWithRangeAndCentre()
    {
        var eq = TwoFactorModel().EquationCoded();
        Assert.Contains("A = coded spd  [-1 … 1], centre 0", eq);
        Assert.Contains("B = coded dep  [-1 … 1], centre 0", eq);
    }

    // ---- RsmFitter.Stars ------------------------------------------------------

    [Theory]
    [InlineData(0.0001, "***")]
    [InlineData(0.0009, "***")]
    [InlineData(0.001, "**")]     // boundary: not strictly < 0.001
    [InlineData(0.005, "**")]
    [InlineData(0.01, "*")]       // boundary: not strictly < 0.01
    [InlineData(0.03, "*")]
    [InlineData(0.05, "")]        // boundary: not strictly < 0.05
    [InlineData(0.5, "")]
    [InlineData(double.NaN, "")]
    public void Stars_MapsPValueToSignificanceMarker(double p, string expected)
    {
        Assert.Equal(expected, RsmFitter.Stars(p));
    }

    // ---- RsmFitter.Report ------------------------------------------------------

    // Same synthetic quadratic used by DoeTests.RsmTests.Fit_Noisy_GivesSaneStats,
    // reproduced locally (with a fixed seed) so this file does not depend on that
    // class's private helpers.
    static double Truth(double a, double b) => 5 + 2 * a - 3 * b + 1.5 * a * b + 4 * a * a - 2 * b * b;

    static RsmModel FitNoisyQuadratic()
    {
        var pts = new List<double[]>();
        foreach (var a in new[] { -1.0, 0, 1 })
            foreach (var b in new[] { -1.0, 0, 1 })
                pts.Add(new[] { a, b });
        pts.Add(new[] { 0.0, 0 });
        pts.Add(new[] { 0.0, 0 });   // replicates -> pure error df
        var rng = new Random(1);
        var x = pts.Select(p => new[] { 40 + 10 * p[0], 3 + 1 * p[1] }).ToArray();
        var y = pts.Select(p => Truth(p[0], p[1]) + 0.4 * (rng.NextDouble() - 0.5)).ToArray();
        return RsmFitter.Fit("resp", new[] { "speed", "height" }, new[] { 30.0, 2 }, new[] { 50.0, 4 },
            null, x, y, RsmModelType.Quadratic);
    }

    [Fact]
    public void Report_IncludesResponseModelTypeRunCountAndFitStatistics()
    {
        var m = FitNoisyQuadratic();
        string report = RsmFitter.Report(m);
        Assert.Contains($"RSM - {m.ResponseName}", report);
        Assert.Contains($"({m.ModelType}, n={m.NRuns})", report);
        Assert.Contains("R²=", report);
        Assert.Contains("R²adj=", report);
        Assert.Contains("R²pred=", report);
        Assert.Contains("RMSE=", report);
        Assert.Contains("PRESS=", report);
    }

    [Fact]
    public void Report_ListsEveryTermLabelAndTheAnovaSourcesWithStars()
    {
        var m = FitNoisyQuadratic();
        string report = RsmFitter.Report(m);
        Assert.Contains("Term", report);
        foreach (var t in m.Terms)
            Assert.Contains(t.Label, report);

        Assert.Contains("ANOVA", report);
        foreach (var a in m.Anova)
            Assert.Contains(a.Source, report);

        // the strong linear effect A must read as significant, with the same
        // marker RsmFitter.Stars would independently compute for its p-value
        var termA = m.Terms.First(t => t.Label == "A");
        Assert.True(termA.P < 0.05, $"expected effect A to be significant, p={termA.P}");
        string starsA = RsmFitter.Stars(termA.P);
        Assert.NotEqual("", starsA);
        Assert.Contains(starsA, report);
    }

    [Fact]
    public void Report_IncludesDroppedTermsLineWhenTermsWereAliasedAway()
    {
        // 2² factorial: the two quadratic terms are indistinguishable from the
        // intercept and RsmFitter drops them (same construction as DoeTests.
        // RsmTests.Fit_DropsAliasedQuadraticsOnTwoLevelDesign).
        var x = new[] { new[] { -1.0, -1 }, new[] { 1.0, -1 }, new[] { -1.0, 1 }, new[] { 1.0, 1 }, new[] { -1.0, -1 } };
        var y = new[] { 1.0, 2, 3, 4.5, 1.1 };
        var m = RsmFitter.Fit("r", new[] { "a", "b" }, new[] { -1.0, -1 }, new[] { 1.0, 1 }, null, x, y, RsmModelType.Quadratic);
        Assert.NotEmpty(m.DroppedTerms);

        string report = RsmFitter.Report(m);
        Assert.Contains("dropped: " + string.Join(", ", m.DroppedTerms), report);
    }

    [Fact]
    public void Report_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            var m = FitNoisyQuadratic();
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = RsmFitter.Report(m);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string german = RsmFitter.Report(m);
            Assert.Equal(invariant, german);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
