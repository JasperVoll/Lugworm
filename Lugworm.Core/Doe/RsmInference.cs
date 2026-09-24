using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;

namespace Lugworm.Core.Doe
{
    /// <summary>Interval on a single model prediction. <c>Fit</c> is the point
    /// estimate the model has always returned; <c>Lo</c>/<c>Hi</c> bound it at the
    /// stated confidence level.</summary>
    public readonly struct PredictionBand
    {
        public readonly double Fit, Lo, Hi;
        public PredictionBand(double fit, double lo, double hi) { Fit = fit; Lo = lo; Hi = hi; }
        public bool Valid => !double.IsNaN(Lo) && !double.IsNaN(Hi);
        public bool Contains(double y) => Valid && y >= Lo && y <= Hi;
        public override string ToString()
            => Valid ? FormattableString.Invariant($"{Fit:0.###} [{Lo:0.###}, {Hi:0.###}]") : FormattableString.Invariant($"{Fit:0.###} [no interval]");
    }

    /// <summary>Per-run regression diagnostics of one fitted model.</summary>
    public sealed class RunDiagnostic
    {
        public int Run;                     // index into RsmModel.XActual / Y
        public double Residual;
        /// <summary>Internally studentized residual e_i / (RMSE×√(1−h_i)).</summary>
        public double Studentized;
        /// <summary>Hat-matrix diagonal h_i ∈ (0, 1].</summary>
        public double Leverage;
        /// <summary>Cook's distance (Cook 1977): the squared shift of the whole
        /// coefficient vector, in SE units, when this run is deleted.</summary>
        public double CookD;
        public bool HighLeverage;           // h_i > 2p/n
        public bool Influential;            // CookD > 1 (strong) - see report for 4/n
    }

    public sealed class RsmDiagnostics
    {
        public string ResponseName = "";
        public List<RunDiagnostic> Runs = new List<RunDiagnostic>();
        /// <summary>Variance inflation factor per non-intercept term, aligned with
        /// <see cref="VifTerms"/>. 1 = orthogonal; &gt; 10 conventionally flags a
        /// coefficient whose individual value is not interpretable (Marquardt 1970).</summary>
        public double[] Vif = Array.Empty<double>();
        public string[] VifTerms = Array.Empty<string>();
        public double LeverageCutoff = double.NaN;   // 2p/n
        public string Notes = "";
    }

    /// <summary>Inference on fitted RSM models: prediction/confidence intervals,
    /// delete-one influence (Cook's distance), leverage, and variance inflation.
    /// Written from the standard OLS definitions (Cook, Technometrics 1977;
    /// Marquardt, Technometrics 1970; Montgomery, Design and Analysis of
    /// Experiments, ch. 10) - all are elementary functions of the design matrix,
    /// the residuals and (XᵀX)⁻¹, which the fitter already computes.</summary>
    public static class RsmInference
    {
        /// <summary>95 % (by default) prediction interval for ONE NEW observation at
        /// <paramref name="actual"/>: ŷ ± t×RMSE×√(1 + x₀ᵀ(XᵀX)⁻¹x₀). This is the
        /// honest answer to "what will the next print measure" - wider than the
        /// confidence interval because a single run carries its own noise.</summary>
        public static PredictionBand PredictionInterval(RsmModel m, double[] actual, double alpha = 0.05)
            => Band(m, actual, alpha, forNewObservation: true);

        /// <summary>Confidence interval for the MEAN response at
        /// <paramref name="actual"/>: ŷ ± t×RMSE×√(x₀ᵀ(XᵀX)⁻¹x₀).</summary>
        public static PredictionBand ConfidenceInterval(RsmModel m, double[] actual, double alpha = 0.05)
            => Band(m, actual, alpha, forNewObservation: false);

        static PredictionBand Band(RsmModel m, double[] actual, double alpha, bool forNewObservation)
        {
            double fit = m.Predict(actual);
            if (m.IsComputed || m.Kind == RsmModelKind.Feasibility) return new PredictionBand(fit, double.NaN, double.NaN);
            var cov = EnsureCov(m);
            if (cov == null || m.DfResidual <= 0 || double.IsNaN(m.Rmse))
                return new PredictionBand(fit, double.NaN, double.NaN);
            var xc = m.ToCoded(actual);
            int p = m.Terms.Count;
            var x0 = new double[p];
            for (int j = 0; j < p; j++) x0[j] = m.Terms[j].Eval(xc);
            double v = QuadForm(cov, x0);
            if (v < 0 || double.IsNaN(v)) return new PredictionBand(fit, double.NaN, double.NaN);
            double t = StudentT.InvCDF(0, 1, m.DfResidual, 1 - alpha / 2.0);
            double half = t * m.Rmse * Math.Sqrt((forNewObservation ? 1.0 : 0.0) + v);
            double transformedFit = m.PredictTransformedCoded(xc);
            return new PredictionBand(fit, m.BackTransform(transformedFit - half), m.BackTransform(transformedFit + half));
        }

        static double QuadForm(double[][] cov, double[] x)
        {
            double s = 0;
            for (int i = 0; i < x.Length; i++)
                for (int j = 0; j < x.Length; j++)
                    s += x[i] * cov[i][j] * x[j];
            return s;
        }

        public static double Leverage(RsmModel m, double[] actual)
        {
            var cov = EnsureCov(m);
            if (cov == null) return double.NaN;
            var coded = m.ToCoded(actual);
            return QuadForm(cov, m.Terms.Select(term => term.Eval(coded)).ToArray());
        }

        /// <summary>(XᵀX)⁻¹ of the fitted (coded) design. The fitter stores it; a
        /// model that still carries its design data can rebuild it, and a model
        /// restored from pre-2.3.0 JSON (no covariance, no data) cannot - intervals
        /// are then reported as unavailable rather than guessed.</summary>
        internal static double[][]? EnsureCov(RsmModel m)
        {
            if (m.CovUnscaled != null) return m.CovUnscaled;
            if (m.XActual.Length == 0 || m.Terms.Count == 0) return null;
            var xc = m.XActual.Select(m.ToCoded).ToArray();
            int n = xc.Length, p = m.Terms.Count;
            if (n < p) return null;
            var X = Matrix<double>.Build.Dense(n, p, (i, j) => m.Terms[j].Eval(xc[i]));
            var inv = (X.TransposeThisAndMultiply(X)).Inverse();
            m.CovUnscaled = ToJagged(inv);
            return m.CovUnscaled;
        }

        internal static double[][] ToJagged(Matrix<double> a)
        {
            var rows = new double[a.RowCount][];
            for (int i = 0; i < a.RowCount; i++)
            {
                rows[i] = new double[a.ColumnCount];
                for (int j = 0; j < a.ColumnCount; j++) rows[i][j] = a[i, j];
            }
            return rows;
        }

        /// <summary>Leverage, studentized residuals, Cook's distance per run, and
        /// VIF per non-intercept term. Needs the design data the fitter keeps on the
        /// model; a JSON-restored model without it returns an empty result with a
        /// note instead of throwing.</summary>
        public static RsmDiagnostics Diagnose(RsmModel m)
        {
            var d = new RsmDiagnostics { ResponseName = m.ResponseName };
            int n = m.XActual.Length, p = m.Terms.Count;
            if (n == 0 || p == 0 || m.Y.Length != n)
            { d.Notes = "no design data on this model (restored from JSON?) - diagnostics unavailable"; return d; }
            if (m.DfResidual <= 0 || double.IsNaN(m.Rmse) || m.Rmse <= 0)
            { d.Notes = "saturated or degenerate fit - diagnostics unavailable"; return d; }

            var xc = m.XActual.Select(m.ToCoded).ToArray();
            var X = Matrix<double>.Build.Dense(n, p, (i, j) => m.Terms[j].Eval(xc[i]));
            var cov = EnsureCov(m);
            if (cov == null) { d.Notes = "covariance unavailable"; return d; }
            var C = Matrix<double>.Build.DenseOfRowArrays(cov);
            double mse = m.Rmse * m.Rmse;
            d.LeverageCutoff = 2.0 * p / n;

            for (int i = 0; i < n; i++)
            {
                var xi = X.Row(i);
                double h = xi.DotProduct(C * xi);
                double yHat = 0;
                for (int j = 0; j < p; j++) yHat += m.Terms[j].Coef * X[i, j];
                double e = m.TransformValue(m.Y[i]) - yHat;
                double denom = 1 - h;
                double stud = denom > 1e-12 ? e / (m.Rmse * Math.Sqrt(denom)) : double.NaN;
                double cook = denom > 1e-12 ? e * e * h / (p * mse * denom * denom) : double.PositiveInfinity;
                d.Runs.Add(new RunDiagnostic
                {
                    Run = i,
                    Residual = e,
                    Studentized = stud,
                    Leverage = h,
                    CookD = cook,
                    HighLeverage = h > d.LeverageCutoff + 1e-12,
                    Influential = cook > 1.0,
                });
            }

            // VIF over the non-intercept columns via the inverse correlation matrix:
            // VIF_j = [R⁻¹]_jj, exactly 1 for an orthogonal (coded) design.
            int q = p - 1;
            if (q >= 2)
            {
                var terms = m.Terms.Skip(1).ToArray();
                var cols = new double[q][];
                bool degenerate = false;
                for (int j = 0; j < q; j++)
                {
                    cols[j] = new double[n];
                    for (int i = 0; i < n; i++) cols[j][i] = X[i, j + 1];
                    double mean = cols[j].Average();
                    double ss = cols[j].Sum(v => (v - mean) * (v - mean));
                    if (ss < 1e-12) { degenerate = true; break; }
                    double sd = Math.Sqrt(ss);
                    for (int i = 0; i < n; i++) cols[j][i] = (cols[j][i] - mean) / sd;
                }
                if (!degenerate)
                {
                    var R = Matrix<double>.Build.Dense(q, q, (a, b) =>
                    {
                        double s = 0;
                        for (int i = 0; i < n; i++) s += cols[a][i] * cols[b][i];
                        return s;
                    });
                    var rInv = R.Inverse();
                    d.VifTerms = terms.Select(t => t.Label).ToArray();
                    d.Vif = Enumerable.Range(0, q).Select(j => rInv[j, j]).ToArray();
                }
                else d.Notes = "a constant predictor column blocks VIF";
            }
            else if (q == 1)
            {
                d.VifTerms = new[] { m.Terms[1].Label };
                d.Vif = new[] { 1.0 };   // a single predictor cannot inflate itself
            }
            return d;
        }

        /// <summary>Fixed-width diagnostics block for a GH panel, appended to the
        /// fitter's report. Flags only - the numbers are all there for reading.</summary>
        public static string Report(RsmDiagnostics d)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"── Diagnostics - {d.ResponseName} ──"));
            if (d.Notes.Length > 0) sb.AppendLine("note: " + d.Notes);
            if (d.Runs.Count > 0)
            {
                sb.AppendLine(FormattableString.Invariant($"{"Run",4}{"Resid",10}{"Stud.",8}{"Lever",8}{"CookD",8}  Flags   (leverage cutoff 2p/n = {F3(d.LeverageCutoff)}; Cook's D > 1 strong, > {F3(4.0 / d.Runs.Count)} worth a look)"));
                foreach (var r in d.Runs)
                {
                    string flags = (r.Influential ? "INFL " : "") + (r.HighLeverage ? "LEV" : "");
                    sb.AppendLine(FormattableString.Invariant($"{r.Run + 1,4}{F3(r.Residual),10}{F2(r.Studentized),8}{F3(r.Leverage),8}{F3(r.CookD),8}  {flags}"));
                }
            }
            if (d.Vif.Length > 0)
            {
                sb.AppendLine("VIF (1 = orthogonal, > 10 = coefficient not individually interpretable):");
                for (int j = 0; j < d.Vif.Length; j++)
                    sb.AppendLine(FormattableString.Invariant($"  {d.VifTerms[j],-8}{F2(d.Vif[j]),8}{(d.Vif[j] > 10 ? "  ⚠" : "")}"));
            }
            return sb.ToString();
        }

        static string F3(double v) => double.IsNaN(v) ? "-" : v.ToString("0.000", CultureInfo.InvariantCulture);
        public static string IntervalScaleReport(RsmModel m) => m.Transform == ResponseTransform.None ? ""
            : "Intervals computed on the transformed scale and back-transformed bound by bound (asymmetric).";
        static string F2(double v) => double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
