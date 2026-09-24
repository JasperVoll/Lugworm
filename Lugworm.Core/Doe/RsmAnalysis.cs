using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;

namespace Lugworm.Core.Doe
{
    public enum RsmModelType { Linear, Interactions, Quadratic }
    public enum ResponseTransform { None, Log1p, Sqrt, BoxCox }
    public enum RsmModelKind { Response, Feasibility }

    /// <summary>One model term: product of coded factors with the given powers
    /// (all zero = intercept). Powers ≤ 2, total degree ≤ 2.</summary>
    public sealed class RsmTerm
    {
        public int[] Powers = Array.Empty<int>();
        public string Label = "";       // "1", "A", "AB", "A²"
        public double Coef, Se = double.NaN, T = double.NaN, P = double.NaN;

        public double Eval(double[] xCoded)
        {
            double v = 1;
            for (int j = 0; j < Powers.Length; j++)
                for (int p = 0; p < Powers[j]; p++) v *= xCoded[j];
            return v;
        }
    }

    public sealed class AnovaRow
    {
        public string Source = "";
        public double Ss = double.NaN, Ms = double.NaN, F = double.NaN, P = double.NaN;
        public int Df;
    }

    /// <summary>Fitted response-surface model for one response. Prediction works in
    /// actual units (inputs are coded internally with the stored factor ranges).</summary>
    public sealed class RsmModel
    {
        public string ResponseName = "";
        public ResponseTransform Transform;
        public RsmModelKind Kind;
        public int DroppedInfeasible;
        public double MaxTrainingLeverage = double.NaN;
        public bool IsComputed;
        public double Lambda = 1;

        public double TransformValue(double y)
        {
            if (Transform == ResponseTransform.Log1p)
            {
                if (y <= -1) throw new ArgumentException("log1p requires response values greater than -1");
                return Math.Log(1 + y);
            }
            if (Transform == ResponseTransform.Sqrt)
            {
                if (y < 0) throw new ArgumentException("sqrt requires nonnegative response values");
                return Math.Sqrt(y);
            }
            if (Transform == ResponseTransform.BoxCox)
            {
                if (y <= 0) throw new ArgumentException("boxcox requires positive response values");
                return Math.Abs(Lambda) < 1e-10 ? Math.Log(y) : (Math.Pow(y, Lambda) - 1) / Lambda;
            }
            return y;
        }

        /// <summary>Monotone inverse, extended to the response-domain boundary for intervals.</summary>
        public double BackTransform(double value)
        {
            if (Transform == ResponseTransform.Log1p) return Math.Exp(value) - 1;
            if (Transform == ResponseTransform.Sqrt) return Math.Pow(Math.Max(0, value), 2);
            if (Transform == ResponseTransform.BoxCox)
            {
                if (Math.Abs(Lambda) < 1e-10) return Math.Exp(value);
                double b = 1 + Lambda * value;
                return b <= 0 ? (Lambda > 0 ? 0 : double.PositiveInfinity) : Math.Pow(b, 1 / Lambda);
            }
            return value;
        }
        public string[] FactorNames = Array.Empty<string>();
        public double[] Lows = Array.Empty<double>(), Highs = Array.Empty<double>(), Centres = Array.Empty<double>();
        public RsmModelType ModelType = RsmModelType.Quadratic;
        public List<RsmTerm> Terms = new List<RsmTerm>();
        public List<string> DroppedTerms = new List<string>();   // aliased → removed, with reason
        public List<AnovaRow> Anova = new List<AnovaRow>();
        public double R2 = double.NaN, R2Adj = double.NaN, R2Pred = double.NaN;
        public double Press = double.NaN, Rmse = double.NaN;
        public int NRuns, DfResidual;
        public string Notes = "";
        /// <summary>(XᵀX)⁻¹ of the coded design (terms × terms), stored by the fitter
        /// so prediction intervals survive a JSON round-trip that drops the raw runs.
        /// Null on models restored from pre-2.3.0 JSON - intervals then report
        /// unavailable (see RsmInference).</summary>
        public double[][]? CovUnscaled;
        // training data kept for plots (predicted vs actual, contour overlays)
        public double[][] XActual = Array.Empty<double[]>();
        public double[] Y = Array.Empty<double>();

        public int FactorCount => FactorNames.Length;

        public double[] ToCoded(double[] actual)
        {
            var x = new double[FactorCount];
            for (int j = 0; j < FactorCount; j++)
            {
                var f = new FactorDef(FactorNames[j], Lows[j], Highs[j], 3, Centres[j]);
                x[j] = f.ToCoded(actual[j]);
            }
            return x;
        }

        public double PredictCoded(double[] xCoded)
            => BackTransform(PredictTransformedCoded(xCoded));

        public double PredictTransformedCoded(double[] xCoded)
        {
            double y = 0;
            foreach (var t in Terms) y += t.Coef * t.Eval(xCoded);
            return y;
        }

        public double Predict(double[] actual) => PredictCoded(ToCoded(actual));

        /// <summary>Equation in coded units (x ∈ [−1, 1]), with the factor legend.</summary>
        public string EquationCoded()
        {
            var sb = new StringBuilder($"{EquationResponse} = ");
            bool first = true;
            foreach (var t in Terms)
            {
                double c = t.Coef;
                if (first) { sb.Append(Num(c)); first = false; }
                else sb.Append(c >= 0 ? " + " : " − ").Append(Num(Math.Abs(c)));
                if (t.Label != "1") sb.Append("×").Append(t.Label);
            }
            sb.AppendLine();
            for (int j = 0; j < FactorCount; j++)
                sb.AppendLine(FormattableString.Invariant(
                    $"   {(char)('A' + j)} = coded {FactorNames[j]}  [{Num(Lows[j])} … {Num(Highs[j])}], centre {Num(Centres[j])}"));
            return sb.ToString();
        }

        /// <summary>Equation expanded to actual units. Only exact when every factor's
        /// centre is the midpoint of its range (linear coding); otherwise returns a note.</summary>
        public string EquationActual()
        {
            for (int j = 0; j < FactorCount; j++)
                if (Math.Abs(Centres[j] - 0.5 * (Lows[j] + Highs[j])) > 1e-9 * Math.Max(1, Math.Abs(Highs[j])))
                    return "(actual-unit equation omitted: non-midpoint centre level → piecewise coding)";
            // x_j = alpha_j + beta_j * v_j
            var alpha = new double[FactorCount];
            var beta = new double[FactorCount];
            for (int j = 0; j < FactorCount; j++)
            {
                beta[j] = 2.0 / (Highs[j] - Lows[j]);
                alpha[j] = -(Highs[j] + Lows[j]) / (Highs[j] - Lows[j]);
            }
            // expand Σ coef×Π x_j^p_j into a polynomial over v (exponent-vector keyed)
            var poly = new Dictionary<string, double>();
            foreach (var t in Terms)
            {
                var partial = new Dictionary<string, double> { { ExpKey(new int[FactorCount]), t.Coef } };
                for (int j = 0; j < FactorCount; j++)
                    for (int p = 0; p < t.Powers[j]; p++)
                        partial = MultiplyLinear(partial, j, alpha[j], beta[j]);
                foreach (var kv in partial)
                    poly[kv.Key] = poly.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
            }
            var sb = new StringBuilder($"{EquationResponse} = ");
            bool first = true;
            foreach (var kv in poly.OrderBy(k => k.Key.Count(ch => ch != '0')).ThenBy(k => k.Key))
            {
                if (Math.Abs(kv.Value) < 1e-12) continue;
                double c = kv.Value;
                if (first) { sb.Append(Num(c)); first = false; }
                else sb.Append(c >= 0 ? " + " : " − ").Append(Num(Math.Abs(c)));
                var exps = kv.Key.Split('|').Select(int.Parse).ToArray();
                for (int j = 0; j < FactorCount; j++)
                {
                    if (exps[j] == 0) continue;
                    sb.Append("×").Append(FactorNames[j]);
                    if (exps[j] > 1) sb.Append("²");
                }
            }
            return sb.ToString();
        }

        static Dictionary<string, double> MultiplyLinear(Dictionary<string, double> poly, int j,
                                                          double a, double b)
        {
            var res = new Dictionary<string, double>();
            foreach (var kv in poly)
            {
                var exps = kv.Key.Split('|').Select(int.Parse).ToArray();
                Add(res, kv.Key, kv.Value * a);
                exps[j]++;
                Add(res, ExpKey(exps), kv.Value * b);
            }
            return res;
        }

        static void Add(Dictionary<string, double> d, string k, double v)
        { d[k] = d.TryGetValue(k, out var e) ? e + v : v; }

        static string ExpKey(int[] exps) => string.Join("|", exps);

        static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        string EquationResponse => Transform == ResponseTransform.None ? ResponseName
            : Transform == ResponseTransform.BoxCox ? $"BoxCox({ResponseName}, {Num(Lambda)})"
            : $"{Transform.ToString().ToLowerInvariant()}({ResponseName})";

        // ── JSON (self-contained writer/parser for this fixed schema) ────────────

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            J(sb, "response", ResponseName); sb.Append(",");
            J(sb, "model", ModelType.ToString()); sb.Append(",");
            J(sb, "Transform", Transform.ToString()); sb.Append(",");
            J(sb, "Kind", Kind.ToString()); sb.Append(",");
            sb.Append(FormattableString.Invariant($"\"DroppedInfeasible\":{DroppedInfeasible},"));
            sb.Append("\"MaxTrainingLeverage\":").Append(D(MaxTrainingLeverage)).Append(",");
            sb.Append("\"IsComputed\":").Append(IsComputed ? "true," : "false,");
            sb.Append("\"Lambda\":").Append(D(Lambda)).Append(",");
            JArr(sb, "factors", FactorNames); sb.Append(",");
            JArr(sb, "lows", Lows); sb.Append(",");
            JArr(sb, "highs", Highs); sb.Append(",");
            JArr(sb, "centres", Centres); sb.Append(",");
            JArr(sb, "terms", Terms.Select(t => t.Label)); sb.Append(",");
            sb.Append("\"powers\":[").Append(string.Join(",", Terms.Select(t => "[" + string.Join(",", t.Powers) + "]"))).Append("],");
            JArr(sb, "coefs", Terms.Select(t => t.Coef)); sb.Append(",");
            JArr(sb, "p_values", Terms.Select(t => t.P)); sb.Append(",");
            sb.Append(FormattableString.Invariant($"\"R2\":{D(R2)},\"R2_adj\":{D(R2Adj)},\"R2_pred\":{D(R2Pred)},\"PRESS\":{D(Press)},\"RMSE\":{D(Rmse)},\"n\":{NRuns},\"df_res\":{DfResidual}"));
            if (CovUnscaled != null)
                sb.Append(",\"cov\":[")
                  .Append(string.Join(",", CovUnscaled.Select(row => "[" + string.Join(",", row.Select(D)) + "]")))
                  .Append("]");
            sb.Append("}");
            return sb.ToString();
        }

        public static RsmModel FromJson(string json)
        {
            var p = new MiniJson(json);
            var obj = (Dictionary<string, object?>)p.Parse()!;
            var m = new RsmModel
            {
                ResponseName = (string)obj["response"]!,
                FactorNames = Strings(obj["factors"]),
                Lows = Doubles(obj["lows"]),
                Highs = Doubles(obj["highs"]),
                Centres = Doubles(obj["centres"]),
                R2 = Dbl(obj, "R2"),
                R2Adj = Dbl(obj, "R2_adj"),
                R2Pred = Dbl(obj, "R2_pred"),
                Press = Dbl(obj, "PRESS"),
                Rmse = Dbl(obj, "RMSE"),
                NRuns = (int)Dbl(obj, "n"),
                DfResidual = (int)Dbl(obj, "df_res"),
            };
            if (obj.TryGetValue("model", out var mt) && Enum.TryParse((string)mt!, out RsmModelType t)) m.ModelType = t;
            if (obj.TryGetValue("Transform", out var tr) && Enum.TryParse((string)tr!, out ResponseTransform transform)) m.Transform = transform;
            if (obj.ContainsKey("Lambda")) m.Lambda = Dbl(obj, "Lambda");
            if (obj.TryGetValue("Kind", out var kind) && Enum.TryParse((string)kind!, out RsmModelKind mk)) m.Kind = mk;
            if (obj.ContainsKey("DroppedInfeasible")) m.DroppedInfeasible = (int)Dbl(obj, "DroppedInfeasible");
            m.MaxTrainingLeverage = Dbl(obj, "MaxTrainingLeverage");
            m.IsComputed = obj.TryGetValue("IsComputed", out var computed) && computed is bool flag && flag;
            var labels = Strings(obj["terms"]);
            var coefs = Doubles(obj["coefs"]);
            var pv = obj.TryGetValue("p_values", out var pvo) ? Doubles(pvo) : new double[coefs.Length];
            var powers = ((List<object?>)obj["powers"]!)
                .Select(row => ((List<object?>)row!).Select(v => (int)(double)v!).ToArray()).ToArray();
            for (int i = 0; i < labels.Length; i++)
                m.Terms.Add(new RsmTerm { Label = labels[i], Powers = powers[i], Coef = coefs[i], P = i < pv.Length ? pv[i] : double.NaN });
            if (obj.TryGetValue("cov", out var cv) && cv is List<object?> covRows)
                m.CovUnscaled = covRows.Select(row => Doubles(row)).ToArray();
            return m;
        }

        static string[] Strings(object? o) => ((List<object?>)o!).Select(v => (string)v!).ToArray();
        static double[] Doubles(object? o) => ((List<object?>)o!).Select(v => v == null ? double.NaN : (double)v).ToArray();
        static double Dbl(Dictionary<string, object?> o, string k) => o.TryGetValue(k, out var v) && v != null ? (double)v : double.NaN;

        static void J(StringBuilder sb, string k, string v) => sb.Append(FormattableString.Invariant($"\"{k}\":\"{Esc(v)}\""));
        static void JArr(StringBuilder sb, string k, IEnumerable<string> vs)
            => sb.Append(FormattableString.Invariant($"\"{k}\":[")).Append(string.Join(",", vs.Select(v => $"\"{Esc(v)}\""))).Append("]");
        static void JArr(StringBuilder sb, string k, IEnumerable<double> vs)
            => sb.Append(FormattableString.Invariant($"\"{k}\":[")).Append(string.Join(",", vs.Select(D))).Append("]");
        static string D(double v) => double.IsNaN(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>Minimal JSON reader sufficient for RsmModel.ToJson output (objects,
    /// arrays, strings, numbers, null). Not a general-purpose parser.</summary>
    internal sealed class MiniJson
    {
        readonly string _s; int _i;
        public MiniJson(string s) { _s = s; }

        public object? Parse() { SkipWs(); return Value(); }

        FormatException Bad(string expected) => new FormatException(
            _i >= _s.Length
                ? $"Malformed JSON: unexpected end of input (expected {expected})."
                : $"Malformed JSON: expected {expected} at position {_i}, found '{_s[_i]}'.");

        char Cur(string expected)
        {
            if (_i >= _s.Length) throw Bad(expected);
            return _s[_i];
        }

        object? Value()
        {
            char c = Cur("a JSON value");
            if (c == '{') return Obj();
            if (c == '[') return Arr();
            if (c == '"') return Str();
            if (c == 'n') { Expect("null"); return null; }
            if (c == 't') { Expect("true"); return true; }
            if (c == 'f') { Expect("false"); return false; }
            int start = _i;
#if NETFRAMEWORK
            while (_i < _s.Length && "+-.eE0123456789NaInfity".IndexOf(_s[_i]) >= 0) _i++;
#else
            while (_i < _s.Length && "+-.eE0123456789NaInfity".Contains(_s[_i])) _i++;
#endif
            var tok = _s.Substring(start, _i - start);
            if (tok.Length == 0 ||
                !double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                if (tok == "NaN") return double.NaN;
                if (tok == "Infinity") return double.PositiveInfinity;
                if (tok == "-Infinity") return double.NegativeInfinity;
                throw new FormatException($"Malformed JSON: cannot parse '{tok}' as a number at position {start}.");
            }
            return num;
        }

        void Expect(string literal)
        {
            if (_i + literal.Length > _s.Length ||
                string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
                throw Bad($"'{literal}'");
            _i += literal.Length;
        }

        Dictionary<string, object?> Obj()
        {
            var d = new Dictionary<string, object?>();
            _i++; SkipWs();
            if (Cur("'}' or a key") == '}') { _i++; return d; }
            while (true)
            {
                SkipWs();
                string k = Str();
                SkipWs();
                if (Cur("':'") != ':') throw Bad("':'");
                _i++;
                SkipWs();
                d[k] = Value();
                SkipWs();
                char c = Cur("',' or '}'");
                if (c == ',') { _i++; continue; }
                if (c != '}') throw Bad("',' or '}'");
                _i++; return d;
            }
        }

        List<object?> Arr()
        {
            var l = new List<object?>();
            _i++; SkipWs();
            if (Cur("']' or a value") == ']') { _i++; return l; }
            while (true)
            {
                SkipWs();
                l.Add(Value());
                SkipWs();
                char c = Cur("',' or ']'");
                if (c == ',') { _i++; continue; }
                if (c != ']') throw Bad("',' or ']'");
                _i++; return l;
            }
        }

        string Str()
        {
            if (Cur("'\"'") != '"') throw Bad("'\"'");
            var sb = new StringBuilder();
            _i++; // '"'
            while (Cur("closing '\"'") != '"')
            {
                if (_s[_i] == '\\') { _i++; sb.Append(Cur("escape character") == 'n' ? '\n' : _s[_i]); }
                else sb.Append(_s[_i]);
                _i++;
            }
            _i++;
            return sb.ToString();
        }

        void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
    }

    /// <summary>OLS response-surface fitting with the diagnostics of the FBI2026
    /// analysis: per-term t/p, partial-SS ANOVA, lack-of-fit from replicates,
    /// PRESS / predicted R². Aliased terms (rank-deficient columns, e.g. quadratics
    /// on a 2-level design) are detected and dropped transparently.</summary>
    public static class RsmFitter
    {
        /// <param name="xActual">runs × factors, actual units</param>
        /// <param name="y">response values (NaN rows are excluded)</param>
        public static RsmModel Fit(string responseName, string[] factorNames,
                                   double[] lows, double[] highs, double[]? centres,
                                   double[][] xActual, double[] y, RsmModelType type,
                                   ResponseTransform transform = ResponseTransform.None, double[]? feasible = null,
                                   bool isComputed = false)
        {
            if (feasible != null && (feasible.Length != y.Length || feasible.Any(v => v != 0 && v != 1)))
                throw new ArgumentException("feasible must contain one 0 or 1 per run");
            int m = factorNames.Length;
            centres ??= Enumerable.Range(0, m).Select(j => 0.5 * (lows[j] + highs[j])).ToArray();
            var model = new RsmModel
            {
                ResponseName = responseName,
                FactorNames = factorNames,
                Lows = lows,
                Highs = highs,
                Centres = centres,
                ModelType = type,
                Transform = transform,
                IsComputed = isComputed,
                DroppedInfeasible = feasible?.Count(v => v == 0) ?? 0,
            };

            // keep only complete rows
            var rows = Enumerable.Range(0, y.Length)
                .Where(i => (feasible == null || feasible[i] == 1) && !double.IsNaN(y[i]) && xActual[i].All(v => !double.IsNaN(v))).ToArray();
            int n = rows.Length;
            model.NRuns = n;
            model.XActual = rows.Select(i => xActual[i]).ToArray();
            model.Y = rows.Select(i => y[i]).ToArray();
            if (model.Y.Any(double.IsInfinity)) throw new ArgumentException("Response values must be finite");
            if (transform == ResponseTransform.BoxCox && n >= 3)
            {
                if (model.Y.Any(v => v <= 0)) throw new ArgumentException("boxcox requires positive response values");
                double best = double.NegativeInfinity;
                double selectedLambda = 1;
                double logSum = model.Y.Sum(v => Math.Log(v));
                for (int step = -20; step <= 20; step++)
                {
                    model.Lambda = step / 10.0;
                    var transformed = model.Y.Select(model.TransformValue).ToArray();
                    var trial = Fit(responseName, factorNames, lows, highs, centres,
                        model.XActual, transformed, type);
                    double sse = trial.XActual.Select((row, i) => Math.Pow(transformed[i] - trial.Predict(row), 2)).Sum();
                    double score = -n * 0.5 * Math.Log(Math.Max(sse / n, 1e-30)) + (model.Lambda - 1) * logSum;
                    if (score > best) { best = score; selectedLambda = model.Lambda; }
                }
                model.Lambda = selectedLambda;
            }
            var yFit = model.Y.Select(model.TransformValue).ToArray();
            var xc = model.XActual.Select(model.ToCoded).ToArray();

            // term list
            var terms = BuildTerms(m, type);
            if (n < 3) { model.Notes = "too few runs to fit"; return model; }

            // rank screen (modified Gram-Schmidt): drop aliased columns
            var kept = new List<RsmTerm>();
            var basis = new List<double[]>();
            foreach (var t in terms)
            {
                var col = xc.Select(t.Eval).ToArray();
                double n0 = Norm(col);
                var r = (double[])col.Clone();
                foreach (var b in basis)
                {
                    double dot = 0, bb = 0;
                    for (int i = 0; i < n; i++) { dot += r[i] * b[i]; bb += b[i] * b[i]; }
                    for (int i = 0; i < n; i++) r[i] -= dot / bb * b[i];
                }
                if (Norm(r) > 1e-7 * Math.Max(n0, 1) && kept.Count < n)
                { kept.Add(t); basis.Add(r); }
                else
                    model.DroppedTerms.Add(t.Label + (kept.Count >= n ? " (insufficient runs)" : " (aliased in this design)"));
            }
            model.Terms = kept;
            int p = kept.Count;

            // OLS
            var X = Matrix<double>.Build.Dense(n, p, (i, j) => kept[j].Eval(xc[i]));
            var Y = Vector<double>.Build.Dense(yFit);
            var qr = X.QR();
            var beta = qr.Solve(Y);
            var inv = (X.TransposeThisAndMultiply(X)).Inverse();
            model.CovUnscaled = RsmInference.ToJagged(inv);
            model.MaxTrainingLeverage = model.XActual.Max(row => RsmInference.Leverage(model, row));

            // The Gram-Schmidt screen keeps X numerically full-rank, but a barely-kept
            // column can still leave (XᵀX)⁻¹ garbage - flag it instead of showing NaN
            // dashes with no explanation.
            bool illConditioned = false;
            for (int j = 0; j < p && !illConditioned; j++)
                if (double.IsNaN(inv[j, j]) || double.IsInfinity(inv[j, j]) || inv[j, j] < 0)
                    illConditioned = true;
            if (illConditioned)
                model.Notes = (model.Notes.Length > 0 ? model.Notes + "; " : "") +
                    "design matrix is near-singular: standard errors / p-values unreliable - " +
                    "add runs or drop terms (coefficients themselves are the least-squares fit)";

            var yHat = X * beta;
            var resid = Y - yHat;
            double ssRes = resid.DotProduct(resid);
            double yBar = Y.Average();
            double ssTot = Y.Sum(v => (v - yBar) * (v - yBar));
            double ssReg = Math.Max(0, ssTot - ssRes);
            int dfRes = n - p, dfReg = p - 1;
            model.DfResidual = dfRes;
            double msRes = dfRes > 0 ? ssRes / dfRes : double.NaN;
            model.Rmse = dfRes > 0 ? Math.Sqrt(msRes) : double.NaN;
            model.R2 = ssTot > 0 ? 1 - ssRes / ssTot : double.NaN;
            model.R2Adj = ssTot > 0 && dfRes > 0 ? 1 - ssRes / dfRes / (ssTot / (n - 1)) : double.NaN;

            for (int j = 0; j < p; j++)
            {
                kept[j].Coef = beta[j];
                if (dfRes > 0)
                {
                    kept[j].Se = Math.Sqrt(Math.Max(0, msRes * inv[j, j]));
                    kept[j].T = kept[j].Se > 0 ? beta[j] / kept[j].Se : double.NaN;
                    kept[j].P = double.IsNaN(kept[j].T) ? double.NaN
                        : 2 * (1 - StudentT.CDF(0, 1, dfRes, Math.Abs(kept[j].T)));
                }
            }

            // PRESS / predicted R² via hat diagonal
            if (dfRes > 0)
            {
                double press = 0;
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    var xi = X.Row(i);
                    double h = xi.DotProduct(inv * xi);
                    if (h >= 1 - 1e-10) { ok = false; break; }
                    double e = resid[i] / (1 - h);
                    press += e * e;
                }
                if (ok)
                {
                    model.Press = press;
                    model.R2Pred = ssTot > 0 ? 1 - press / ssTot : double.NaN;
                }
            }

            // ANOVA (partial SS for single-df terms: SS_j = b_j² / inv_jj)
            var anova = new List<AnovaRow>();
            if (dfRes > 0 && dfReg > 0)
            {
                double fM = ssReg / dfReg / msRes;
                anova.Add(new AnovaRow { Source = "Model", Ss = ssReg, Df = dfReg, Ms = ssReg / dfReg, F = fM, P = 1 - FisherSnedecor.CDF(dfReg, dfRes, fM) });
                for (int j = 1; j < p; j++)     // skip intercept
                {
                    double ssJ = inv[j, j] > 0 ? beta[j] * beta[j] / inv[j, j] : double.NaN;
                    double fJ = ssJ / msRes;
                    anova.Add(new AnovaRow { Source = kept[j].Label, Ss = ssJ, Df = 1, Ms = ssJ, F = fJ, P = 1 - FisherSnedecor.CDF(1, dfRes, fJ) });
                }

                // lack of fit from replicate groups (identical coded rows)
                double ssPe = 0; int dfPe = 0;
                foreach (var g in Enumerable.Range(0, n).GroupBy(i => string.Join("|", xc[i].Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)))))
                {
                    var vals = g.Select(i => yFit[i]).ToArray();
                    if (vals.Length < 2) continue;
                    double mean = vals.Average();
                    ssPe += vals.Sum(v => (v - mean) * (v - mean));
                    dfPe += vals.Length - 1;
                }
                if (dfPe > 0 && dfRes - dfPe > 0)
                {
                    double ssLof = Math.Max(0, ssRes - ssPe);
                    int dfLof = dfRes - dfPe;
                    double fL = ssLof / dfLof / (ssPe / dfPe);
                    anova.Add(new AnovaRow { Source = "Lack of Fit", Ss = ssLof, Df = dfLof, Ms = ssLof / dfLof, F = fL, P = 1 - FisherSnedecor.CDF(dfLof, dfPe, fL) });
                    anova.Add(new AnovaRow { Source = "Pure Error", Ss = ssPe, Df = dfPe, Ms = ssPe / dfPe });
                }
                anova.Add(new AnovaRow { Source = "Residual", Ss = ssRes, Df = dfRes, Ms = msRes });
                anova.Add(new AnovaRow { Source = "Total (corr.)", Ss = ssTot, Df = n - 1 });
            }
            else
                model.Notes = "saturated model (0 residual df): no significance statistics";
            model.Anova = anova;
            return model;
        }

        internal static List<RsmTerm> BuildTerms(int m, RsmModelType type)
        {
            var terms = new List<RsmTerm> { new RsmTerm { Powers = new int[m], Label = "1" } };
            for (int j = 0; j < m; j++)
            {
                var pw = new int[m]; pw[j] = 1;
                terms.Add(new RsmTerm { Powers = pw, Label = ((char)('A' + j)).ToString() });
            }
            if (type == RsmModelType.Interactions || type == RsmModelType.Quadratic)
                for (int a = 0; a < m - 1; a++)
                    for (int b = a + 1; b < m; b++)
                    {
                        var pw = new int[m]; pw[a] = 1; pw[b] = 1;
                        terms.Add(new RsmTerm { Powers = pw, Label = $"{(char)('A' + a)}{(char)('A' + b)}" });
                    }
            if (type == RsmModelType.Quadratic)
                for (int j = 0; j < m; j++)
                {
                    var pw = new int[m]; pw[j] = 2;
                    terms.Add(new RsmTerm { Powers = pw, Label = $"{(char)('A' + j)}²" });
                }
            return terms;
        }

        static double Norm(double[] v) => Math.Sqrt(v.Sum(x => x * x));

        public static RsmModel FitFeasibility(string[] names, double[] lows, double[] highs,
            double[]? centres, double[][] x, double[] flags)
        {
            if (flags.Length != x.Length || flags.Any(v => v != 0 && v != 1))
                throw new ArgumentException("feasible must contain one 0 or 1 per run");
            int m = names.Length;
            int complete = x.Count(row => row.All(v => !double.IsNaN(v)));
            var type = complete > 1 + m + m * (m - 1) / 2 ? RsmModelType.Interactions : RsmModelType.Linear;
            var model = Fit("feasible", names, lows, highs, centres, x, flags, type);
            model.Kind = RsmModelKind.Feasibility;
            return model;
        }

        /// <summary>Fixed-width text report (coefficients + ANOVA + fit stats) for a
        /// GH panel: the "transparent" face of the analysis.</summary>
        public static string Report(RsmModel m)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"══ RSM - {m.ResponseName}  ({m.ModelType}, n={m.NRuns}) ══"));
            if (m.DroppedInfeasible > 0) sb.AppendLine(FormattableString.Invariant($"{m.DroppedInfeasible} dropped as infeasible"));
            if (m.Kind == RsmModelKind.Feasibility) sb.AppendLine("feasibility: linear-probability model on all run flags");
            if (m.IsComputed) sb.AppendLine("computed response, no measurement error");
            if (m.Transform != ResponseTransform.None)
                sb.AppendLine($"transform: {m.Transform}" +
                    (m.Transform == ResponseTransform.BoxCox ? FormattableString.Invariant($" (lambda = {m.Lambda:0.0})") : "") +
                    "; fit statistics and diagnostics are on the transformed scale");
            sb.AppendLine(FormattableString.Invariant($"R²={F3(m.R2)}  R²adj={F3(m.R2Adj)}  R²pred={F3(m.R2Pred)}  RMSE={F3(m.Rmse)}  PRESS={F3(m.Press)}"));
            if (m.DroppedTerms.Count > 0)
                sb.AppendLine("dropped: " + string.Join(", ", m.DroppedTerms));
            if (m.Notes.Length > 0) sb.AppendLine("note: " + m.Notes);
            if (m.Transform != ResponseTransform.None) sb.AppendLine(RsmInference.IntervalScaleReport(m));
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"{"Term",-8}{"Coef",12}{"SE",12}{"t",9}{"p",9}  Sig"));
            foreach (var t in m.Terms)
                sb.AppendLine(FormattableString.Invariant($"{t.Label,-8}{F4(t.Coef),12}{F4(t.Se),12}{F2(t.T),9}{F4(t.P),9}  {Stars(t.P)}"));
            if (m.Anova.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(FormattableString.Invariant($"{"ANOVA",-14}{"SS",12}{"df",5}{"MS",12}{"F",9}{"p",9}  Sig"));
                foreach (var a in m.Anova)
                    sb.AppendLine(FormattableString.Invariant($"{a.Source,-14}{F4(a.Ss),12}{a.Df,5}{F4(a.Ms),12}{F2(a.F),9}{F4(a.P),9}  {Stars(a.P)}"));
            }
            return sb.ToString();
        }

        public static string Stars(double p)
            => double.IsNaN(p) ? "" : p < 0.001 ? "***" : p < 0.01 ? "**" : p < 0.05 ? "*" : "";
        static string F4(double v) => double.IsNaN(v) ? "-" : v.ToString("0.0000", CultureInfo.InvariantCulture);
        static string F3(double v) => double.IsNaN(v) ? "-" : v.ToString("0.000", CultureInfo.InvariantCulture);
        static string F2(double v) => double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
