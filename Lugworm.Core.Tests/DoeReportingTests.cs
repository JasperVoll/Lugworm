using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>M5a: the report may only claim what the numbers support, and the optional
/// power sheet must not touch the plan sheet a lab fills in.</summary>
public class DoeReportingTests
{
    [Fact]
    public void Computed_ResponseIsSkippedByWithinPiMarker()
    {
        var measured = NoisyLine(); var computed = WithoutCovariance(measured); computed.IsComputed = true;
        var goals = new[] { Goal.Parse("max", "y", 0, 6), Goal.Parse("min", "time", 0, 6) };
        var r = Recommendation(measured, computed);
        string report = DesirabilityOptimizer.Report(r, new[] { measured, computed }, goals);
        Assert.Contains("D=0.95 within-PI", report);
        Assert.Contains("computed response, no measurement error", report);
        Assert.DoesNotContain("marker is withheld", report);
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(report, DesirabilityOptimizer.Report(r, new[] { measured, computed }, goals)); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Feasible_ReportCountsMaskedCandidates()
    {
        var m = NoisyLine();
        var mask = RsmModel.FromJson(m.ToJson()); mask.Kind = RsmModelKind.Feasibility;
        mask.Terms[0].Coef = 0.5; mask.Terms[1].Coef = -0.5;
        var goals = new[] { Goal.Parse("max", "y", 0, 6) };
        var models = new[] { m, mask };
        var r = DesirabilityOptimizer.Optimize(models, goals, gridPerDim: 5, collectFront: true);
        Assert.Equal(2, r.MaskedCandidates); Assert.Equal(5, r.CandidateCount);
        string report = DesirabilityOptimizer.Report(r, models, goals);
        Assert.Contains("2 of 5 candidates masked as infeasible (threshold 0.5)", report);
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(report, DesirabilityOptimizer.Report(r, models, goals)); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>y = 3 + 2A with deterministic residuals of +-0.5, so the prediction
    /// interval at A = 1 has real width (about +-1.5) and contains the prediction at
    /// A = 0.9 (4.8) but not the one at A = -1 (1.0).</summary>
    static RsmModel NoisyLine()
    {
        var x = new[] { new[] { -1.0 }, new[] { -0.5 }, new[] { 0.0 }, new[] { 0.5 }, new[] { 1.0 }, new[] { -1.0 }, new[] { 1.0 } };
        var y = new[] { 1.5, 1.5, 3.5, 3.5, 4.5, 0.5, 5.5 };
        return RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, new[] { 0.0 }, x, y, RsmModelType.Linear);
    }

    static RsmModel WithoutCovariance(RsmModel m)
    {
        string json = m.ToJson();
        int covAt = json.IndexOf(",\"cov\":", StringComparison.Ordinal);
        return RsmModel.FromJson(json.Substring(0, covAt) + "}");
    }

    static DesirabilityResult Recommendation(params RsmModel[] models)
    {
        var xBest = new[] { 1.0 };
        var r = new DesirabilityResult
        {
            XBest = xBest,
            YPred = models.Select(m => m.Predict(xBest)).ToArray(),
            DResponses = models.Select(_ => 1.0).ToArray(),
            DFactors = new[] { double.NaN },
            DOverall = 1.0,
        };
        r.Alternatives.Add((new[] { 0.9 }, 0.95));   // predictions inside the recommendation's PI
        r.Alternatives.Add((new[] { -1.0 }, 0.1));   // far outside it
        return r;
    }

    [Fact]
    public void Within_PI_marker_is_a_coverage_check_and_never_claims_equivalence()
    {
        var m = NoisyLine();
        var goals = new[] { Goal.Parse("max", "y", 0, 6) };
        string report = DesirabilityOptimizer.Report(Recommendation(m), new[] { m }, goals, null);
        var lines = report.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.DoesNotContain("indistinguishable", report);
        Assert.DoesNotContain("≈best", report);
        string near = lines.Single(l => l.Contains("A=0.9"));
        string far = lines.Single(l => l.Contains("A=-1"));
        Assert.Contains("within-PI", near);
        Assert.DoesNotContain("within-PI", far);
        Assert.Contains("a coverage check, not an equivalence test", report);
        Assert.Contains("predictions within available recommendation prediction intervals", report);
    }

    [Fact]
    public void Missing_interval_on_any_response_withholds_the_marker_and_reports_the_gap()
    {
        var m = NoisyLine();
        var legacy = WithoutCovariance(m);
        var goals = new[] { Goal.Parse("max", "y", 0, 6), Goal.Parse("max", "y", 0, 6) };
        string report = DesirabilityOptimizer.Report(Recommendation(m, legacy), new[] { m, legacy }, goals, null);
        Assert.DoesNotContain("within-PI  ", report);   // no alternative carries the marker
        Assert.DoesNotContain("indistinguishable", report);
        Assert.Contains("1 of 2 responses have no prediction interval - the within-PI marker is withheld", report);
        Assert.Contains("unavailable (no covariance on this model)", report);
    }

    [Fact]
    public void Front_note_records_sampling_and_priors_instead_of_calling_an_off_front_optimum_a_defect()
    {
        var m = NoisyLine();
        var goals = new[] { Goal.Parse("max", "y", 0, 6) };
        var r = Recommendation(m);
        r.Front.Add(new ParetoPoint { X = new[] { 0.5 }, D = new[] { 0.75 }, DOverall = 0.5 });
        r.FrontObjectives = new[] { "y" };
        string plain = DesirabilityOptimizer.Report(r, new[] { m }, goals, null);
        Assert.Contains("WARNING: the D-optimum is not on the front", plain);
        Assert.Contains("sampled from the search grid and locally refined", plain);
        Assert.Contains("one more recipe than maxFront", plain);
        var priors = new[] { Goal.Parse("low", "A", double.NaN, double.NaN) };
        string withPriors = DesirabilityOptimizer.Report(r, new[] { m }, goals, priors);
        Assert.Contains("factor priors shape D", withPriors);
        Assert.DoesNotContain("indicates a defect", withPriors);
    }

    [Fact]
    public void Power_sheet_is_additive_and_the_plan_sheet_is_unchanged()
    {
        var plan = DoeDesign.FullFactorial2(new[] { new FactorDef("speed", 10, 20), new FactorDef("pressure", 1, 2) });
        var responses = new[] { "width" };
        var power = new PowerResult
        {
            FactorNames = new[] { "speed", "pressure" },
            Power = new[] { 0.91, double.NaN },
            EffectSizes = new[] { 1.5, 0.25 },
            Sigma = 0.4,
            Alpha = 0.05,
            NSims = 400,
            Seed = 12345,
            ModelType = RsmModelType.Linear,
            McError = 0.025,
            Notes = "hand-derived",
        };
        string plain = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        string withPower = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            DoeExportSnapshot.CapturePlan(plan, responses, plain).Export(CancellationToken.None);
            DoeExportSnapshot.CapturePlan(plan, responses, withPower, power: power).Export(CancellationToken.None);
            var sheetPlain = XlsxReader.ReadSheet(plain, "DoE_Plan");
            var sheetWith = XlsxReader.ReadSheet(withPower, "DoE_Plan");
            Assert.Equal(sheetPlain.Length, sheetWith.Length);
            for (int i = 0; i < sheetPlain.Length; i++) Assert.Equal(sheetPlain[i], sheetWith[i]);
            Assert.ThrowsAny<Exception>(() => XlsxReader.ReadSheet(plain, "Power"));   // no power, no sheet
            var cells = XlsxReader.ReadSheet(withPower, "Power").SelectMany(x => x).ToArray();
            Assert.Contains("speed", cells);
            Assert.Contains("pressure", cells);
            Assert.Contains("0.91", cells);
            Assert.Contains("aliased", cells);
            Assert.Contains("1.5", cells);
            Assert.Contains("0.4", cells);
            Assert.Contains("0.05", cells);
            Assert.Contains("400", cells);
            Assert.Contains("12345", cells);
            Assert.Contains("Linear", cells);
            Assert.Contains(cells, c => c.Contains("hand-derived"));
            // the snapshot key sees the power data: same plan, different power, different key
            var a = DoeExportSnapshot.CapturePlan(plan, responses, "p.xlsx", power: power);
            var b = DoeExportSnapshot.CapturePlan(plan, responses, "p.xlsx");
            Assert.NotEqual(a.Key, b.Key);
        }
        finally { File.Delete(plain); File.Delete(withPower); }
    }

    // D4 (4.4.0): the recommendation/alternatives/Pareto-front report and the plan's
    // text table are GH panel text, not UI - both must stay dot-decimal under a
    // German ambient culture, same fixture pattern as CultureInvarianceTests.
    [Fact]
    public void DesirabilityReport_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var m = NoisyLine();
        var goals = new[] { Goal.Parse("max", "y", 0, 6) };
        var r = Recommendation(m);
        r.Front.Add(new ParetoPoint { X = new[] { 0.5 }, D = new[] { 0.75 }, DOverall = 0.5 });
        r.FrontObjectives = new[] { "y" };
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = DesirabilityOptimizer.Report(r, new[] { m }, goals, null);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string german = DesirabilityOptimizer.Report(r, new[] { m }, goals, null);
            Assert.Equal(invariant, german);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void PlanTextTable_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        var plan = DoeDesign.FullFactorial2(new[] { new FactorDef("speed", 10, 20), new FactorDef("pressure", 1, 2) });
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string invariant = DoeDesign.TextTable(plan);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string german = DoeDesign.TextTable(plan);
            Assert.Equal(invariant, german);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
