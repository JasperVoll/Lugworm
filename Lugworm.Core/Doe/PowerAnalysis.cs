using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Lugworm.Core.Doe
{
    public sealed class PowerResult
    {
        public string[] FactorNames = Array.Empty<string>();
        /// <summary>Estimated power per factor's main effect at <see cref="Alpha"/>;
        /// NaN where the term is aliased in this design under the chosen model.</summary>
        public double[] Power = Array.Empty<double>();
        public double[] EffectSizes = Array.Empty<double>();
        public double Sigma;
        public double Alpha;
        public int NSims;
        /// <summary>Random seed of the simulation, so the report is reproducible.</summary>
        public int Seed;
        public RsmModelType ModelType;
        /// <summary>Monte-Carlo standard error of a power estimate near its value
        /// (≈ √(p(1−p)/nSims) at p = 0.5, the worst case).</summary>
        public double McError;
        public string Notes = "";
    }

    /// <summary>Monte-Carlo power evaluation of a generated design: simulate
    /// y = Xβ + ε with the postulated effect sizes and noise, refit with the
    /// SHIPPED fitter, and count how often each main effect reaches significance.
    /// Simulation is chosen over parametric noncentral-F power deliberately: the
    /// estimate then reflects the analysis that will actually be run - including
    /// the fitter's aliased-term dropping on DSD/Taguchi designs - and needs no
    /// noncentral distributions. Concept as in the skpr package (Morgan-Wall &amp;
    /// Khoury, J. Stat. Software 99(1), 2021 - cited for the method, no code
    /// consulted; skpr is GPL).</summary>
    public static class PowerAnalysis
    {
        /// <param name="xActual">the generated plan, runs × factors, actual units</param>
        /// <param name="effectSizes">postulated full low→high change in the response
        /// per factor (response units). The coded main-effect coefficient is half of
        /// this, because coded factors span [-1, 1].</param>
        /// <param name="sigma">residual standard deviation of the response, e.g. the
        /// RMSE of a previous campaign, or a measurement-repeatability estimate</param>
        /// <param name="modelType">the model that will be fitted to the real data -
        /// power depends on its residual df, so match it to the planned analysis</param>
        public static PowerResult MonteCarloPower(double[][] xActual, string[] factorNames,
            double[] lows, double[] highs, double[]? centres,
            RsmModelType modelType, double sigma, double[] effectSizes,
            int nSims = 400, double alpha = 0.05, int seed = 12345)
        {
            if (xActual == null || xActual.Length == 0) throw new ArgumentException("empty design");
            int m = factorNames.Length;
            if (effectSizes.Length != m) throw new ArgumentException($"{m} factors need {m} effect sizes (got {effectSizes.Length})");
            if (!(sigma > 0)) throw new ArgumentException("sigma must be > 0");
            if (nSims < 20) throw new ArgumentException("nSims must be at least 20");

            var res = new PowerResult
            {
                FactorNames = factorNames,
                EffectSizes = effectSizes,
                Sigma = sigma,
                Alpha = alpha,
                NSims = nSims,
                Seed = seed,
                ModelType = modelType,
                McError = Math.Sqrt(0.25 / nSims),
                Power = Enumerable.Repeat(double.NaN, m).ToArray(),
            };

            // coded design + true mean: only main effects are non-zero, at half the
            // stated low→high change (coded range is 2)
            centres ??= Enumerable.Range(0, m).Select(j => 0.5 * (lows[j] + highs[j])).ToArray();
            var probe = new RsmModel { FactorNames = factorNames, Lows = lows, Highs = highs, Centres = centres };
            int n = xActual.Length;
            var xc = xActual.Select(probe.ToCoded).ToArray();
            var mean = new double[n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < m; j++)
                    mean[i] += 0.5 * effectSizes[j] * xc[i][j];

            var hits = new int[m];
            var seen = new bool[m];
            var rnd = new Random(seed);
            var y = new double[n];
            var mainLabels = Enumerable.Range(0, m).Select(j => ((char)('A' + j)).ToString()).ToArray();
            var dropNotes = new HashSet<string>(StringComparer.Ordinal);
            for (int s = 0; s < nSims; s++)
            {
                for (int i = 0; i < n; i++) y[i] = mean[i] + sigma * Gauss(rnd);
                var fit = RsmFitter.Fit("power-sim", factorNames, lows, highs, centres, xActual, y, modelType);
                if (fit.DfResidual <= 0)
                {
                    res.Notes = "saturated model - no residual df, power undefined; add runs or simplify the model";
                    return res;
                }
                for (int j = 0; j < m; j++)
                {
                    var term = fit.Terms.FirstOrDefault(t => t.Label == mainLabels[j]);
                    if (term == null) continue;    // aliased in this design
                    seen[j] = true;
                    if (!double.IsNaN(term.P) && term.P < alpha) hits[j]++;
                }
                foreach (var dropped in fit.DroppedTerms) dropNotes.Add(dropped);
            }
            for (int j = 0; j < m; j++)
                if (seen[j]) res.Power[j] = (double)hits[j] / nSims;
            if (dropNotes.Count > 0)
                res.Notes = "dropped by the fitter: " + string.Join(", ", dropNotes.OrderBy(v => v, StringComparer.Ordinal));
            return res;
        }

        /// <summary>Standard-normal draw (Marsaglia polar); MathNet's samplers are
        /// avoided only to keep the RNG call count deterministic per simulation.</summary>
        static double Gauss(Random rnd)
        {
            double u, v, s;
            do
            {
                u = 2.0 * rnd.NextDouble() - 1.0;
                v = 2.0 * rnd.NextDouble() - 1.0;
                s = u * u + v * v;
            } while (s >= 1.0 || s == 0.0);
            return u * Math.Sqrt(-2.0 * Math.Log(s) / s);
        }

        public static string Report(PowerResult r)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"── Monte-Carlo power ({r.NSims} sims, α={r.Alpha.ToString("0.###", inv)}, σ={r.Sigma.ToString("0.###", inv)}, model {r.ModelType}, MC error ≈ ±{r.McError.ToString("0.00", inv)}) ──"));
            sb.AppendLine(FormattableString.Invariant($"{"Factor",-24}{"Δ(lo→hi)",12}{"power",8}"));
            for (int j = 0; j < r.FactorNames.Length; j++)
                sb.AppendLine($"{r.FactorNames[j],-24}{r.EffectSizes[j].ToString("0.###", inv),12}"
                    + $"{(double.IsNaN(r.Power[j]) ? "aliased" : r.Power[j].ToString("0.00", inv)),8}"
                    + (r.Power[j] < 0.8 ? "  ⚠ under the conventional 0.8" : ""));
            if (r.Notes.Length > 0) sb.AppendLine("note: " + r.Notes);
            sb.AppendLine("power = probability this design detects the stated effect at α, given σ. Below 0.8: add runs, widen ranges, or accept the risk before printing.");
            return sb.ToString();
        }
    }
}
