using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Lugworm.Core.Doe
{
    public enum GoalKind { None, Maximize, Minimize, Target, Range }

    /// <summary>Derringer–Suich goal for one response (or one factor prior).
    /// Weight is the geometric-mean importance; S is the shape exponent
    /// (1 = linear ramp, &gt;1 = stricter near the bound).</summary>
    public sealed class Goal
    {
        public string Name = "";
        public GoalKind Kind = GoalKind.None;
        public double Low = double.NaN, High = double.NaN, Target = double.NaN;
        public double Weight = 1.0;
        public double S = 1.0;

        /// <summary>Parse "max", "min", "target=7", "target=7 in 6..8", "range=6..8",
        /// "none" - optionally with "w=2" and "s=2" suffixes, e.g. "min w=1.5".</summary>
        public static Goal Parse(string text, string name, double defLow, double defHigh)
        {
            var g = new Goal { Name = name, Low = defLow, High = defHigh };
            foreach (var tok in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = tok.Trim().ToLowerInvariant();
                if (t == "max" || t == "maximize" || t == "maximise") g.Kind = GoalKind.Maximize;
                else if (t == "min" || t == "minimize" || t == "minimise") g.Kind = GoalKind.Minimize;
                else if (t == "none" || t == "ignore") g.Kind = GoalKind.None;
                else if (t.StartsWith("target=", StringComparison.Ordinal)) { g.Kind = GoalKind.Target; g.Target = Num(t.Substring(7)); }
                else if (t.StartsWith("range=", StringComparison.Ordinal)) { g.Kind = GoalKind.Range; ParseRange(t.Substring(6), g); }
                else if (t == "in") { /* connective: "target=7 in 6..8" */ }
                else if (t.Contains("..")) ParseRange(t.StartsWith("in", StringComparison.Ordinal) ? t.Substring(2) : t, g);
                else if (t.StartsWith("w=", StringComparison.Ordinal)) g.Weight = Num(t.Substring(2));
                else if (t.StartsWith("s=", StringComparison.Ordinal)) g.S = Num(t.Substring(2));
                else if (t == "low") { g.Kind = GoalKind.Minimize; }     // factor priors
                else if (t == "high") { g.Kind = GoalKind.Maximize; }
                else throw new ArgumentException($"Cannot parse goal token '{tok}' for {name}.");
            }
            return g;
        }

        static void ParseRange(string s, Goal g)
        {
            var parts = s.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                throw new ArgumentException(
                    $"Cannot parse range '{s}' for {g.Name}: expected 'low..high' (e.g. 6..8).");
            g.Low = Num(parts[0]); g.High = Num(parts[1]);
        }

        static double Num(string s)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new ArgumentException($"Cannot parse number '{s}' in goal expression.");
            return v;
        }

        /// <summary>Individual desirability d(y) ∈ [0, 1].</summary>
        public double D(double y)
        {
            switch (Kind)
            {
                case GoalKind.None: return 1.0;
                case GoalKind.Maximize:
                    if (y <= Low) return 0;
                    if (y >= High) return 1;
                    return Math.Pow((y - Low) / (High - Low), S);
                case GoalKind.Minimize:
                    if (y >= High) return 0;
                    if (y <= Low) return 1;
                    return Math.Pow((High - y) / (High - Low), S);
                case GoalKind.Target:
                    if (y < Low || y > High) return 0;
                    return y <= Target
                        ? Math.Pow(Target - Low <= 0 ? 1 : (y - Low) / (Target - Low), S)
                        : Math.Pow(High - Target <= 0 ? 1 : (High - y) / (High - Target), S);
                case GoalKind.Range:
                    return y >= Low && y <= High ? 1 : 0;
                default: return 1;
            }
        }

        public string Describe()
        {
            switch (Kind)
            {
                case GoalKind.Maximize: return $"maximize  (0 at {F(Low)}, 1 at {F(High)}, w={F(Weight)})";
                case GoalKind.Minimize: return $"minimize  (1 at {F(Low)}, 0 at {F(High)}, w={F(Weight)})";
                case GoalKind.Target: return $"target {F(Target)}  in [{F(Low)}, {F(High)}], w={F(Weight)}";
                case GoalKind.Range: return $"keep in [{F(Low)}, {F(High)}], w={F(Weight)}";
                default: return "ignored";
            }
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public sealed class DesirabilityResult
    {
        public double[] XBest = Array.Empty<double>();     // actual units
        public double DOverall;
        public double[] YPred = Array.Empty<double>();     // per response
        public double[] DResponses = Array.Empty<double>();
        public double[] DFactors = Array.Empty<double>();
        /// <summary>Distinct local optima of the SCALARISED D - not a Pareto set.
        /// The non-dominated trade-off lives in <see cref="Front"/>.</summary>
        public List<(double[] x, double d)> Alternatives = new List<(double[], double)>();
        /// <summary>Non-dominated recipes over the individual desirabilities of the
        /// active response goals (empty unless the front was requested). Ordered by
        /// descending DOverall. <see cref="FrontObjectives"/> names the axes.</summary>
        public List<ParetoPoint> Front = new List<ParetoPoint>();
        public string[] FrontObjectives = Array.Empty<string>();
        public long MaskedCandidates, CandidateCount;
        public double FeasibleThreshold = 0.5;
        public bool HasFeasibility;
        public int InteriorRefinementEvaluations;
        public string Notes = "";
    }

    /// <summary>Derringer–Suich multi-response optimisation over fitted RSM models:
    /// overall D = weighted geometric mean of individual desirabilities, maximised by
    /// exhaustive grid search plus Nelder–Mead refinement. Factor priors (e.g. "prefer
    /// low layer height") join the geometric mean exactly like in the FBI2026 paper.</summary>
    public static class DesirabilityOptimizer
    {
        /// <param name="models">one fitted model per response (same factor space)</param>
        /// <param name="responseGoals">goal per model (same order)</param>
        /// <param name="factorGoals">optional prior per factor (null/None = no prior)</param>
        /// <param name="gridPerDim">grid resolution per factor (clamped so total ≤ 500k)</param>
        /// <param name="collectFront">also collect the non-dominated set over the
        /// individual response desirabilities (factor priors deliberately excluded -
        /// they are preferences on the settings, not competing objectives). The
        /// desirability transform is used here only as the per-response scaling that
        /// makes a target= or range= goal orderable; dominance never sees the weights,
        /// which enter D alone. Off by default: it evaluates every d even where the
        /// scalarised D short-circuits to 0, so the default call path cost is unchanged.</param>
        /// <param name="maxFront">crowding-distance thinning cap for the front</param>
        public static DesirabilityResult Optimize(IReadOnlyList<RsmModel> models,
            IReadOnlyList<Goal> responseGoals, IReadOnlyList<Goal>? factorGoals = null,
            double[]? lows = null, double[]? highs = null, int gridPerDim = 15, int topK = 5,
            bool collectFront = false, int maxFront = 200, double feasibleThreshold = 0.5)
        {
            if (models.Count == 0) throw new ArgumentException("No models supplied.");
            if (double.IsNaN(feasibleThreshold) || feasibleThreshold < 0 || feasibleThreshold > 1)
                throw new ArgumentException("feasibleThreshold must be between 0 and 1");
            var masks = models.Where(model => model.Kind == RsmModelKind.Feasibility).ToArray();
            if (responseGoals.Count == models.Count - masks.Length && masks.Length > 0)
            {
                int next = 0;
                responseGoals = models.Select(model => model.Kind == RsmModelKind.Feasibility
                    ? new Goal { Kind = GoalKind.None } : responseGoals[next++]).ToArray();
            }
            if (models.Count != responseGoals.Count)
                throw new ArgumentException($"{models.Count} models but {responseGoals.Count} response goals.");
            var m0 = models[0];
            int m = m0.FactorCount;
            foreach (var mdl in models)
                if (mdl.FactorCount != m || !mdl.FactorNames.SequenceEqual(m0.FactorNames))
                    throw new ArgumentException("All models must share the same factors (order + names).");
            lows ??= m0.Lows; highs ??= m0.Highs;
            factorGoals ??= Enumerable.Repeat(new Goal { Kind = GoalKind.None }, m).ToList();
            double Feasibility(double[] x) => masks.Length == 0 ? double.NaN : masks.Min(model => model.Predict(x));
            bool Allowed(double[] x) => masks.Length == 0 || Feasibility(x) >= feasibleThreshold;
            long masked = 0, considered = 0;

            // effective factor-prior goals get factor bounds as their ramp range
            var fGoals = factorGoals.Select((g, j) =>
            {
                if (g.Kind == GoalKind.None) return g;
                var c = new Goal { Name = m0.FactorNames[j], Kind = g.Kind, Weight = g.Weight, S = g.S, Target = g.Target };
                c.Low = double.IsNaN(g.Low) ? lows[j] : g.Low;
                c.High = double.IsNaN(g.High) ? highs[j] : g.High;
                return c;
            }).ToArray();

            Func<double[], double> overall = x =>
            {
                if (!Allowed(x)) return 0;
                double logSum = 0, wSum = 0;
                for (int r = 0; r < models.Count; r++)
                {
                    if (models[r].Kind == RsmModelKind.Feasibility || responseGoals[r].Kind == GoalKind.None) continue;
                    double d = responseGoals[r].D(models[r].Predict(x));
                    if (d <= 0) return 0;
                    logSum += responseGoals[r].Weight * Math.Log(d);
                    wSum += responseGoals[r].Weight;
                }
                for (int j = 0; j < m; j++)
                {
                    if (fGoals[j].Kind == GoalKind.None) continue;
                    double d = fGoals[j].D(x[j]);
                    if (d <= 0) return 0;
                    logSum += fGoals[j].Weight * Math.Log(d);
                    wSum += fGoals[j].Weight;
                }
                return wSum <= 0 ? 0 : Math.Exp(logSum / wSum);
            };

            // active response objectives for the (optional) Pareto front
            int[] frontGoals = Enumerable.Range(0, models.Count)
                .Where(r => models[r].Kind != RsmModelKind.Feasibility && responseGoals[r].Kind != GoalKind.None).ToArray();
            var frontCandidates = collectFront && frontGoals.Length > 0
                ? new List<ParetoPoint>() : null;
            // running argmax per front objective: the candidate where each dᵢ peaks so
            // far. Refined after the search so the front's endpoints are the local
            // maxima of dᵢ, not the nearest grid cells.
            var extremeX = new double[frontGoals.Length][];
            var extremeD = new double[frontGoals.Length];
            void Collect(double[] x, double d)
            {
                if (frontCandidates == null) return;
                if (!Allowed(x)) return;
                // No zero short-circuit here: a recipe with one dᵢ = 0 can still be
                // best-in-class on another objective, and those are exactly the
                // points the scalarised D hides.
                var dv = new double[frontGoals.Length];
                for (int r = 0; r < frontGoals.Length; r++)
                {
                    dv[r] = responseGoals[frontGoals[r]].D(models[frontGoals[r]].Predict(x));
                    if (extremeX[r] == null || dv[r] > extremeD[r]) { extremeX[r] = (double[])x.Clone(); extremeD[r] = dv[r]; }
                }
                frontCandidates.Add(new ParetoPoint { X = (double[])x.Clone(), D = dv, DOverall = d, Feasible = Feasibility(x) });
                // periodic reduction keeps memory bounded on the 500k grid:
                // NonDominated(NonDominated(A) ∪ B) == NonDominated(A ∪ B)
                // because dominance is transitive
                if (frontCandidates.Count >= 20_000)
                {
                    frontCandidates = ParetoFront.NonDominated(frontCandidates);
                    // Two perfectly anti-correlated responses make EVERY grid point
                    // non-dominated - the archive itself then hits the trigger and
                    // each further insert would re-sort 20k points (quadratic blow-up
                    // on exactly the trade-off case the front exists for). Thin the
                    // interim archive with the same extreme-preserving crowding rule
                    // the final output uses; this may drop interior points a later
                    // candidate would not have dominated, which the thinned output
                    // could not have kept anyway.
                    if (frontCandidates.Count >= 10_000)
                        frontCandidates = ParetoFront.Thin(frontCandidates, Math.Max(maxFront * 4, 1000));
                }
            }

            // ── grid search ──
            int g0 = Math.Max(3, gridPerDim);
            while (Math.Pow(g0, m) > 500_000 && g0 > 3) g0--;
            var idx = new int[m];
            var x0 = new double[m];
            var best = new List<(double[] x, double d)>();
            void Consider(double[] x, double d)
            {
                considered++;
                if (!Allowed(x)) { masked++; return; }
                Collect(x, d);
                if (d <= 0) return;
                best.Add(((double[])x.Clone(), d));
                if (best.Count > 4 * topK)
                    best = best.OrderByDescending(b => b.d).Take(2 * topK).ToList();
            }
            long total = (long)Math.Pow(g0, m);
            const long GridCap = 500_000;
            string? sampleNote = null;
            if (total > GridCap)
            {
                // even the minimum 3^m grid explodes past ~11 factors - fall back to
                // deterministic (fixed-seed) uniform sampling with the same budget
                // instead of iterating for minutes/hours.
                sampleNote = FormattableString.Invariant($"grid {g0}^{m} = {total:N0} points exceeds the {GridCap:N0} cap - ") +
                             FormattableString.Invariant($"searched {GridCap:N0} deterministic samples instead");
                var rnd = new Random(12345);
                for (long it = 0; it < GridCap; it++)
                {
                    for (int j = 0; j < m; j++)
                        x0[j] = lows[j] + rnd.NextDouble() * (highs[j] - lows[j]);
                    Consider(x0, overall(x0));
                }
            }
            else
            {
                for (long it = 0; it < total; it++)
                {
                    long t = it;
                    for (int j = 0; j < m; j++)
                    {
                        idx[j] = (int)(t % g0); t /= g0;
                        x0[j] = lows[j] + idx[j] * (highs[j] - lows[j]) / (g0 - 1);
                    }
                    Consider(x0, overall(x0));
                }
            }
            best = best.OrderByDescending(b => b.d).ToList();

            var res = new DesirabilityResult { HasFeasibility = masks.Length > 0,
                MaskedCandidates = masked, CandidateCount = considered, FeasibleThreshold = feasibleThreshold };
            if (best.Count == 0)
            {
                res.Notes = $"no feasible point: some goal has d=0 everywhere on the {g0}^{m} grid - relax bounds/goals";
                res.XBest = Enumerable.Range(0, m).Select(j => 0.5 * (lows[j] + highs[j])).ToArray();
            }
            else
            {
                // ── Nelder-Mead refinement from the top grid cells ──
                var refined = new List<(double[] x, double d)>();
                foreach (var (xs, _) in best.Take(Math.Max(3, topK)))
                {
                    var r = NelderMead(overall, xs, lows, highs);
                    refined.Add(r);
                }
                refined.AddRange(best);
                refined = refined.OrderByDescending(b => b.d).ToList();
                res.XBest = refined[0].x;
                res.DOverall = refined[0].d;

                // distinct alternatives (min separation: one grid cell)
                var minSep = Enumerable.Range(0, m).Select(j => (highs[j] - lows[j]) / g0).ToArray();
                foreach (var cand in refined.Skip(1))
                {
                    if (res.Alternatives.Count >= topK - 1) break;
                    bool distinct = Enumerable.Range(0, m).Any(j => Math.Abs(cand.x[j] - res.XBest[j]) > minSep[j])
                        && res.Alternatives.All(a => Enumerable.Range(0, m).Any(j => Math.Abs(cand.x[j] - a.x[j]) > minSep[j]));
                    if (distinct) res.Alternatives.Add(cand);
                }
            }

            if (sampleNote != null)
                res.Notes = res.Notes.Length > 0 ? res.Notes + "; " + sampleNote : sampleNote;

            if (frontCandidates != null)
            {
                // the refined optimum is not a grid point - insert it so the invariant
                // "the maximiser of a positively-weighted geometric mean is Pareto
                // optimal" is visible in the output rather than only true on paper
                Collect(res.XBest, res.DOverall);
                foreach (var (ax, ad) in res.Alternatives) Collect(ax, ad);
                // Each objective's extreme is Nelder-Mead refined on that dᵢ ALONE, never
                // on D, so the weights stay out of the front: the grid cell where dᵢ peaks
                // moves to the local maximum of dᵢ and the refined recipe joins the
                // candidates, where it is non-dominated by construction. Skipped on a
                // plateau (dᵢ already 1) and where dᵢ is 0 everywhere; a refinement that
                // does not improve is not inserted, so a grid extreme stays a grid point.
                for (int o = 0; o < frontGoals.Length; o++)
                {
                    if (extremeX[o] == null || extremeD[o] <= 0 || extremeD[o] >= 1) continue;
                    int goalIndex = frontGoals[o];
                    var refinedExtreme = NelderMead(x => Allowed(x) ? responseGoals[goalIndex].D(models[goalIndex].Predict(x)) : -1, extremeX[o], lows, highs);
                    if (refinedExtreme.d > extremeD[o] + 1e-12)
                        Collect(refinedExtreme.x, overall(refinedExtreme.x));
                }
                var front = ParetoFront.NonDominated(frontCandidates);
                res.Front = ParetoFront.Thin(front, maxFront)
                    .OrderByDescending(pt => pt.DOverall).ToList();
                // Local augmented Tchebycheff search from each retained interior recipe.
                int refinementCount = 0;
                for (int index = 0; index < res.Front.Count; index++)
                {
                    var original = res.Front[index];
                    bool Same(double[] a, double[] b) => a.Length == b.Length && a.Zip(b, (u, v) => Math.Abs(u - v) < 1e-9).All(v => v);
                    if (Same(original.X, res.XBest) || extremeX.Any(x => x != null && Same(original.X, x))) continue;
                    if (maxFront > 0 && refinementCount >= maxFront) break;
                    refinementCount++;
                    var weights = original.D.Select(d => 1 / Math.Max(d, 1e-9)).ToArray();
                    double sum = weights.Sum();
                    for (int i = 0; i < weights.Length; i++) weights[i] /= sum;
                    double Scalar(double[] x)
                    {
                        res.InteriorRefinementEvaluations++;
                        if (!Allowed(x)) return double.NegativeInfinity;
                        double worst = 0, totalLoss = 0;
                        for (int i = 0; i < weights.Length; i++)
                        {
                            int goal = frontGoals[i];
                            double loss = 1 - responseGoals[goal].D(models[goal].Predict(x));
                            worst = Math.Max(worst, weights[i] * loss); totalLoss += loss;
                        }
                        return -(worst + 1e-3 * totalLoss);
                    }
                    var improved = NelderMead(Scalar, original.X, lows, highs, maxIter: 200, maxEvaluations: 200);
                    if (!Allowed(improved.x)) continue;
                    var candidate = new ParetoPoint { X = improved.x, DOverall = overall(improved.x), Feasible = Feasibility(improved.x),
                        D = frontGoals.Select(i => responseGoals[i].D(models[i].Predict(improved.x))).ToArray() };
                    int relation = ParetoFront.Compare(candidate.D, original.D, 1e-9);
                    if (relation > 0 || (relation == 0 && candidate.DOverall > original.DOverall)) res.Front[index] = candidate;
                }
                res.Front = ParetoFront.NonDominated(res.Front).OrderByDescending(pt => pt.DOverall).ToList();
                // crowding-distance thinning must not evict the recommendation
                // itself - it is the one point the report reasons about
                if (res.DOverall > 0 && !res.Front.Any(pt =>
                        Enumerable.Range(0, m).All(j => Math.Abs(pt.X[j] - res.XBest[j]) < 1e-9))
                    && front.Any(pt =>
                        Enumerable.Range(0, m).All(j => Math.Abs(pt.X[j] - res.XBest[j]) < 1e-9)))
                    res.Front.Insert(0, front.First(pt =>
                        Enumerable.Range(0, m).All(j => Math.Abs(pt.X[j] - res.XBest[j]) < 1e-9)));
                res.Front = ParetoFront.NonDominated(res.Front).OrderByDescending(pt => pt.DOverall).ToList();
                res.FrontObjectives = frontGoals.Select(r => models[r].ResponseName).ToArray();
                // Inference is needed only for retained recipes, not the up-to-500k search cells.
                foreach (var point in res.Front) CollectReliability(point, frontGoals.Select(i => models[i]).ToArray());
            }

            res.YPred = models.Select(mdl => mdl.Predict(res.XBest)).ToArray();
            res.DResponses = Enumerable.Range(0, models.Count)
                .Select(r => responseGoals[r].Kind == GoalKind.None ? double.NaN : responseGoals[r].D(res.YPred[r])).ToArray();
            res.DFactors = Enumerable.Range(0, m)
                .Select(j => fGoals[j].Kind == GoalKind.None ? double.NaN : fGoals[j].D(res.XBest[j])).ToArray();
            return res;
        }

        public static void CollectReliability(ParetoPoint point, IReadOnlyList<RsmModel> objectives)
        {
            var bands = objectives.Select(model => RsmInference.PredictionInterval(model, point.X)).ToArray();
            point.Predicted = bands.Select(b => b.Fit).ToArray();
            point.PiLo = bands.Select(b => b.Lo).ToArray();
            point.PiHi = bands.Select(b => b.Hi).ToArray();
            point.PiHalf = bands.Select(b => b.Valid ? (b.Hi - b.Lo) / 2 : double.NaN).ToArray();
            point.Computed = objectives.Select(model => model.IsComputed).ToArray();
            if (objectives.Count == 0) return;
            var first = objectives[0];
            point.Leverage = RsmInference.Leverage(first, point.X);
            double maximum = first.MaxTrainingLeverage;
            if (double.IsNaN(maximum) && first.XActual.Length > 0)
                maximum = first.XActual.Max(row => RsmInference.Leverage(first, row));
            point.ExtrapolationFlag = point.Leverage > maximum + 1e-12;
        }

        /// <summary>Bounded Nelder–Mead (reflection/expansion/contraction/shrink) on −D;
        /// points are clamped to the box. Small and dependency-free by design.</summary>
        internal static (double[] x, double d) NelderMead(Func<double[], double> f,
            double[] start, double[] lows, double[] highs, int maxIter = 400, int maxEvaluations = int.MaxValue)
        {
            int m = start.Length;
            int evaluations = 0;
            double Clamped(double[] x)
            {
                if (evaluations >= maxEvaluations) return double.PositiveInfinity;
                evaluations++;
                for (int j = 0; j < m; j++) x[j] = Math.Min(highs[j], Math.Max(lows[j], x[j]));
                return -f(x);
            }
            var pts = new List<double[]> { (double[])start.Clone() };
            for (int j = 0; j < m; j++)
            {
                var p = (double[])start.Clone();
                double step = 0.05 * (highs[j] - lows[j]);
                p[j] += p[j] + step <= highs[j] ? step : -step;
                pts.Add(p);
            }
            var fv = pts.Select(Clamped).ToArray();

            for (int it = 0; it < maxIter && evaluations < maxEvaluations; it++)
            {
                var order = Enumerable.Range(0, pts.Count).OrderBy(i => fv[i]).ToArray();
                pts = order.Select(i => pts[i]).ToList();
                fv = order.Select(i => fv[i]).ToArray();
                if (Math.Abs(fv[fv.Length - 1] - fv[0]) < 1e-10) break;

                var centroid = new double[m];
                for (int i = 0; i < pts.Count - 1; i++)
                    for (int j = 0; j < m; j++) centroid[j] += pts[i][j] / (pts.Count - 1);

                double[] Mix(double alpha)
                {
                    var p = new double[m];
                    for (int j = 0; j < m; j++) p[j] = centroid[j] + alpha * (centroid[j] - pts[pts.Count - 1][j]);
                    return p;
                }
                var xr = Mix(1.0); double fr = Clamped(xr);
                if (fr < fv[0])
                {
                    var xe = Mix(2.0); double fe = Clamped(xe);
                    if (fe < fr) { pts[pts.Count - 1] = xe; fv[fv.Length - 1] = fe; }
                    else { pts[pts.Count - 1] = xr; fv[fv.Length - 1] = fr; }
                }
                else if (fr < fv[fv.Length - 2]) { pts[pts.Count - 1] = xr; fv[fv.Length - 1] = fr; }
                else
                {
                    var xcn = Mix(-0.5); double fc = Clamped(xcn);
                    if (fc < fv[fv.Length - 1]) { pts[pts.Count - 1] = xcn; fv[fv.Length - 1] = fc; }
                    else
                        for (int i = 1; i < pts.Count; i++)
                        {
                            for (int j = 0; j < m; j++) pts[i][j] = pts[0][j] + 0.5 * (pts[i][j] - pts[0][j]);
                            fv[i] = Clamped(pts[i]);
                        }
                }
            }
            int bi = Array.IndexOf(fv, fv.Min());
            return (pts[bi], -fv[bi]);
        }

        /// <summary>Readable optimisation report for a GH panel.</summary>
        public static string Report(DesirabilityResult r, IReadOnlyList<RsmModel> models,
            IReadOnlyList<Goal> responseGoals, IReadOnlyList<Goal>? factorGoals = null)
        {
            if (responseGoals.Count != models.Count)
            {
                int next = 0;
                responseGoals = models.Select(model => model.Kind == RsmModelKind.Feasibility
                    ? new Goal { Kind = GoalKind.None } : responseGoals[next++]).ToArray();
            }
            var sb = new StringBuilder();
            sb.AppendLine("══ Desirability Optimisation (Derringer–Suich) ══");
            sb.AppendLine(FormattableString.Invariant($"overall D = {r.DOverall.ToString("0.000", CultureInfo.InvariantCulture)}"));
            if (r.Notes.Length > 0) sb.AppendLine("note: " + r.Notes);
            if (r.HasFeasibility)
                sb.AppendLine(FormattableString.Invariant($"{r.MaskedCandidates} of {r.CandidateCount} candidates masked as infeasible (threshold {r.FeasibleThreshold:0.###})"));
            sb.AppendLine();
            sb.AppendLine("Recommended settings:");
            var m0 = models[0];
            for (int j = 0; j < m0.FactorCount; j++)
            {
                string prior = factorGoals != null && j < factorGoals.Count && factorGoals[j].Kind != GoalKind.None
                    ? $"   prior: {factorGoals[j].Describe()}  d={F2(r.DFactors[j])}" : "";
                sb.AppendLine(FormattableString.Invariant($"  {m0.FactorNames[j],-24}= {r.XBest[j].ToString("0.####", CultureInfo.InvariantCulture)}{prior}"));
            }
            sb.AppendLine();
            sb.AppendLine(FormattableString.Invariant($"{"Response",-28}{"Goal",-38}{"Pred.",10}{"d",7}"));
            for (int i = 0; i < models.Count; i++)
                sb.AppendLine(FormattableString.Invariant($"{models[i].ResponseName,-28}{responseGoals[i].Describe(),-38}{F3(r.YPred[i]),10}{F2(r.DResponses[i]),7}"));
            // 95 % prediction intervals at the recommendation: the honest width of
            // "what will the next print measure" - see RsmInference
            var bands = models.Select(mdl => RsmInference.PredictionInterval(mdl, r.XBest)).ToArray();
            if (bands.Any(b => b.Valid) || models.Any(model => model.IsComputed))
            {
                sb.AppendLine();
                sb.AppendLine("95% prediction interval at the recommendation (one new print):");
                for (int i = 0; i < models.Count; i++)
                {
                    if (models[i].Kind == RsmModelKind.Feasibility) continue;
                    if (models[i].IsComputed) { sb.AppendLine(models[i].ResponseName + ": computed response, no measurement error"); continue; }
                    sb.AppendLine(FormattableString.Invariant($"  {models[i].ResponseName,-28}{(bands[i].Valid ? $"[{F3(bands[i].Lo)}, {F3(bands[i].Hi)}]" : "unavailable (no covariance on this model)")}"));
                    if (models[i].Transform != ResponseTransform.None) sb.AppendLine(RsmInference.IntervalScaleReport(models[i]));
                }
            }
            if (r.Alternatives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Alternatives (distinct local optima of the scalarised D - NOT a Pareto set):");
                // "within-PI" is a coverage check, not an equivalence test: it says the
                // alternative's predictions lie inside the recommendation's prediction
                // intervals on EVERY response. A response without an interval is missing
                // coverage, so the marker is withheld and the gap is reported instead.
                var checkedResponses = Enumerable.Range(0, models.Count).Where(i => models[i].Kind != RsmModelKind.Feasibility && !models[i].IsComputed).ToArray();
                bool allBands = checkedResponses.Length > 0 && checkedResponses.All(i => bands[i].Valid);
                foreach (var (x, d) in r.Alternatives)
                {
                    bool withinPi = allBands
                        && checkedResponses.All(i => bands[i].Contains(models[i].Predict(x)));
                    sb.AppendLine($"  D={F2(d)}{(withinPi ? " within-PI" : "")}  " + string.Join("  ",
                        x.Select((v, j) => $"{m0.FactorNames[j]}={v.ToString("0.###", CultureInfo.InvariantCulture)}")));
                }
                if (allBands)
                    sb.AppendLine("  (within-PI: predictions within available recommendation prediction intervals on every response - a coverage check, not an equivalence test; the D ranking among such alternatives is not resolved by the data)");
                else if (bands.Any(b => b.Valid))
                    sb.AppendLine(FormattableString.Invariant($"  ({checkedResponses.Count(i => !bands[i].Valid)} of {checkedResponses.Length} responses have no prediction interval - the within-PI marker is withheld)"));
            }
            if (r.Front.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(FormattableString.Invariant($"Pareto front ({r.Front.Count} non-dominated recipes over d[{string.Join(", ", r.FrontObjectives)}]):"));
                sb.AppendLine("  (the axes are the goal desirabilities d, so min, max, target and range goals order alike; dominance ignores the weights w - they enter only the D column and the re-inserted optimum)");
                for (int o = 0; o < r.FrontObjectives.Length; o++)
                {
                    var ext = r.Front[0];
                    foreach (var pt in r.Front) if (pt.D[o] > ext.D[o]) ext = pt;
                    sb.AppendLine($"  best {r.FrontObjectives[o]}: d={F2(ext.D[o])} at " + string.Join("  ",
                        ext.X.Select((v, j) => $"{m0.FactorNames[j]}={v.ToString("0.###", CultureInfo.InvariantCulture)}")));
                }
                bool bestOnFront = r.Front.Any(pt => pt.X.Length == r.XBest.Length
                    && Enumerable.Range(0, pt.X.Length).All(j => Math.Abs(pt.X[j] - r.XBest[j]) < 1e-9));
                sb.AppendLine(bestOnFront
                    ? "  the D-optimum lies on the front (as it must for positive weights)"
                    : (factorGoals != null && factorGoals.Any(g => g.Kind != GoalKind.None)
                        ? "  the D-optimum is not on the front: factor priors shape D, and the front is ordered over response desirabilities only"
                        : "  WARNING: the D-optimum is not on the front - report this, it indicates a defect"));
                sb.AppendLine("  (the front is sampled from the search grid and locally refined, then thinned by crowding distance; the D-optimum, its alternatives and each objective's extreme are Nelder-Mead refined off the grid; it can hold one more recipe than maxFront when the optimum is re-inserted, and is a subset of the true trade-off surface - pick a recipe with Pareto Select)");
            }
            return sb.ToString();
        }

        static string F2(double v) => double.IsNaN(v) ? "-" : v.ToString("0.00", CultureInfo.InvariantCulture);
        static string F3(double v) => double.IsNaN(v) ? "-" : v.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
