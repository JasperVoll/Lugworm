using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Doe
{
    /// <summary>One recipe of a Pareto front as the selector stores it: factor values,
    /// the individual desirabilities of the active goals, the overall D, the names that
    /// give them meaning and the canonical identity of the front it came from. The row
    /// index is deliberately NOT part of the identity - a reordered front must keep the
    /// selection, and a replaced one must not silently point at a different recipe.</summary>
    public sealed class ParetoRecipe
    {
        public const int SchemaVersion = 1;

        public IReadOnlyList<double> X { get; }
        public IReadOnlyList<double> Di { get; }
        public double D { get; }
        public double Feasible { get; }
        public ParetoReliability? Reliability { get; }
        public string ReliabilityStatus => Reliability?.Status(ObjectiveNames) ?? "Prediction uncertainty unavailable for this front";
        public IReadOnlyList<string> FactorNames { get; }
        public IReadOnlyList<string> ObjectiveNames { get; }
        /// <summary>Canonical hash of the front this recipe was selected from.</summary>
        public string FrontHash { get; }
        /// <summary>Hash of this recipe's numbers, used by the key.</summary>
        public string RecipeHash { get; }
        /// <summary>The versioned, stable token that names this recipe on its front.</summary>
        public string Key => "iamps" + SchemaVersion.ToString(CultureInfo.InvariantCulture) + ":" + FrontHash + ":" + RecipeHash;

        public ParetoRecipe(IReadOnlyList<double> x, IReadOnlyList<double> di, double d,
                            IReadOnlyList<string> factorNames, IReadOnlyList<string> objectiveNames, string frontHash,
                            double feasible = double.NaN, ParetoReliability? reliability = null)
        {
            X = Copy(x); Di = Copy(di); D = d;
            Feasible = feasible;
            Reliability = reliability;
            FactorNames = CopyNames(factorNames); ObjectiveNames = CopyNames(objectiveNames);
            FrontHash = frontHash ?? "";
            RecipeHash = ParetoFrontSnapshot.Digest(ParetoFrontSnapshot.EncodeRecord(X, Di, D));
        }

        static double[] Copy(IReadOnlyList<double> v) { var a = new double[v.Count]; for (int i = 0; i < a.Length; i++) a[i] = v[i]; return a; }
        static string[] CopyNames(IReadOnlyList<string> v) { var a = new string[v.Count]; for (int i = 0; i < a.Length; i++) a[i] = v[i] ?? ""; return a; }

        public double[] ToBranch()
        {
            var prefix = X.Concat(Di).Concat(new[] { D });
            if (Reliability != null) return prefix.Concat(new[] { Feasible }).Concat(Reliability.ToColumns()).ToArray();
            return double.IsNaN(Feasible) ? prefix.ToArray() : prefix.Concat(new[] { Feasible }).ToArray();
        }

        /// <summary>Per-coordinate match within the versioned tolerance policy.</summary>
        public bool Matches(IReadOnlyList<double> x, IReadOnlyList<double> di)
        {
            if (x.Count != X.Count || di.Count != Di.Count) return false;
            for (int i = 0; i < X.Count; i++) if (!ParetoSelection.Near(X[i], x[i])) return false;
            for (int i = 0; i < Di.Count; i++) if (!ParetoSelection.Near(Di[i], di[i])) return false;
            return true;
        }

        /// <summary>Parses a key token; null when it is not one of ours. Internal (H8):
        /// only <see cref="ParetoSelection.Observe"/> in this file calls it.</summary>
        internal static bool TryParseKey(string? key, out string frontHash, out string recipeHash)
        {
            frontHash = recipeHash = "";
            if (string.IsNullOrWhiteSpace(key)) return false;
            var parts = key!.Trim().Split(':');
            if (parts.Length != 3 || parts[0] != "iamps1") return false;
            if (parts[1].Length != ParetoFrontSnapshot.HashLength || parts[2].Length != ParetoFrontSnapshot.HashLength) return false;
            frontHash = parts[1]; recipeHash = parts[2];
            return true;
        }
    }

    /// <summary>A validated, immutable front as supplied to the selector: every branch
    /// `[x0..x(m-1), d0..d(k-1), D]`, the ordered names, and a canonical identity that
    /// ignores branch order but notices every value, name and D.</summary>
    public sealed class ParetoFrontSnapshot
    {
        public const int SchemaVersion = 1;
        public const int MaxRecipes = 10000;
        public const int MaxFields = 256;
        public const int MaxNameLength = 256;
        public const int HashLength = 16;

        public IReadOnlyList<string> FactorNames { get; }
        public IReadOnlyList<string> ObjectiveNames { get; }
        /// <summary>Rows in the supplied branch order (the display and index mapping).</summary>
        public IReadOnlyList<ParetoRecipe> Rows { get; }
        public string Hash { get; }

        ParetoFrontSnapshot(IReadOnlyList<string> factorNames, IReadOnlyList<string> objectiveNames, List<ParetoRecipe> rows, string hash)
        { FactorNames = factorNames; ObjectiveNames = objectiveNames; Rows = rows; Hash = hash; }

        /// <summary>Validates and canonicalises. Malformed input yields false and a reason;
        /// nothing is dropped or thinned silently.</summary>
        public static bool TryParse(IReadOnlyList<IReadOnlyList<double>> branches, IReadOnlyList<string> factorNames,
                                    IReadOnlyList<string> objectiveNames, out ParetoFrontSnapshot? front, out string? error)
        {
            front = null; error = null;
            if (branches == null || factorNames == null || objectiveNames == null) { error = "front, factorNames and objectiveNames are all required"; return false; }
            int m = factorNames.Count, k = objectiveNames.Count;
            if (m == 0) { error = "factorNames is empty"; return false; }
            if (k == 0) { error = "objectiveNames is empty - no active objective"; return false; }
            if (m + k > MaxFields) { error = "more than " + MaxFields.ToString(CultureInfo.InvariantCulture) + " combined factor/objective fields"; return false; }
            if (branches.Count == 0) { error = "front is empty"; return false; }
            if (branches.Count > MaxRecipes) { error = "front has " + branches.Count.ToString(CultureInfo.InvariantCulture) + " recipes (limit " + MaxRecipes.ToString(CultureInfo.InvariantCulture) + ")"; return false; }
            var fn = new string[m]; var on = new string[k];
            for (int i = 0; i < m; i++) { fn[i] = (factorNames[i] ?? "").Trim(); if (fn[i].Length == 0 || fn[i].Length > MaxNameLength) { error = "factor name " + (i + 1).ToString(CultureInfo.InvariantCulture) + " is empty or too long"; return false; } }
            for (int i = 0; i < k; i++) { on[i] = (objectiveNames[i] ?? "").Trim(); if (on[i].Length == 0 || on[i].Length > MaxNameLength) { error = "objective name " + (i + 1).ToString(CultureInfo.InvariantCulture) + " is empty or too long"; return false; } }
            if (new HashSet<string>(fn, StringComparer.Ordinal).Count != m) { error = "factor names are not unique"; return false; }
            if (new HashSet<string>(on, StringComparer.Ordinal).Count != k) { error = "objective names are not unique"; return false; }

            int arity = m + k + 1;
            var records = new List<string>(branches.Count);
            var rows = new List<ParetoRecipe>(branches.Count);
            var xs = new List<double[]>(branches.Count); var ds = new List<double[]>(branches.Count); var dd = new List<double>(branches.Count);
            var reliability = new List<ParetoReliability?>();
            for (int b = 0; b < branches.Count; b++)
            {
                var row = branches[b];
                if (row == null || (row.Count != arity && row.Count != arity + 1 && row.Count != arity + 4 * k + 3 && row.Count != arity + 5 * k + 3) || row.Count != branches[0].Count)
                {
                    error = "branch " + b.ToString(CultureInfo.InvariantCulture) + " has " + (row?.Count ?? 0).ToString(CultureInfo.InvariantCulture)
                          + " values; expected " + arity.ToString(CultureInfo.InvariantCulture) + " ([" + m.ToString(CultureInfo.InvariantCulture) + " factors, "
                          + k.ToString(CultureInfo.InvariantCulture) + " desirabilities, D])";
                    return false;
                }
                var x = new double[m]; var di = new double[k];
                for (int i = 0; i < m; i++) { x[i] = Norm(row[i]); if (!Num.Finite(x[i])) { error = "branch " + b.ToString(CultureInfo.InvariantCulture) + ": factor " + fn[i] + " is not finite"; return false; } }
                for (int i = 0; i < k; i++)
                {
                    di[i] = Norm(row[m + i]);
                    if (!Num.Finite(di[i]) || di[i] < 0 || di[i] > 1) { error = "branch " + b.ToString(CultureInfo.InvariantCulture) + ": desirability " + on[i] + " is outside [0, 1]"; return false; }
                }
                double d = Norm(row[m + k]);
                if (!Num.Finite(d) || d < 0 || d > 1) { error = "branch " + b.ToString(CultureInfo.InvariantCulture) + ": D is outside [0, 1]"; return false; }
                xs.Add(x); ds.Add(di); dd.Add(d);
                ParetoReliability? rel = null;
                if (row.Count > arity + 1 && !ParetoReliability.TryParse(row.Skip(arity + 1).ToArray(), k, out rel))
                { error = "branch " + b.ToString(CultureInfo.InvariantCulture) + ": malformed reliability columns"; return false; }
                reliability.Add(rel);
                if (row.Count == arity + 1 && !Num.Finite(row[arity]))
                { error = "feasible must be finite"; return false; }
                if (row.Count > arity && double.IsInfinity(row[arity])) { error = "feasible must be finite or unavailable"; return false; }
                records.Add(EncodeRecord(x, di, d) + (row.Count > arity ? "|metadata=" + string.Join(",", row.Skip(arity).Select(v => v.ToString("R", CultureInfo.InvariantCulture))) : ""));
            }
            string hash = ComputeHash(fn, on, records);
            for (int b = 0; b < xs.Count; b++) rows.Add(new ParetoRecipe(xs[b], ds[b], dd[b], fn, on, hash,
                branches[b].Count > arity ? branches[b][arity] : double.NaN, reliability[b]));
            front = new ParetoFrontSnapshot(fn, on, rows, hash);
            return true;
        }

        /// <summary>Rows whose factors and desirabilities match the recipe within tolerance.</summary>
        public List<int> Matches(ParetoRecipe recipe)
        {
            var result = new List<int>();
            for (int i = 0; i < Rows.Count; i++) if (recipe.Matches(Rows[i].X, Rows[i].Di)) result.Add(i);
            return result;
        }

        /// <summary>Row whose factor vector equals <paramref name="settings"/> within tolerance
        /// (the optimizer's recommendation); -1 when absent, -2 when ambiguous.</summary>
        public int IndexOfSettings(IReadOnlyList<double>? settings)
        {
            if (settings == null || settings.Count != FactorNames.Count) return -1;
            int found = -1;
            for (int i = 0; i < Rows.Count; i++)
            {
                bool same = true;
                for (int j = 0; j < settings.Count; j++) if (!ParetoSelection.Near(Rows[i].X[j], settings[j])) { same = false; break; }
                if (!same) continue;
                if (found >= 0) return -2;
                found = i;
            }
            return found;
        }

        /// <summary>Index of the largest supplied D ("best D in supplied front", which is not
        /// necessarily the optimizer's recommendation when factor priors were active).</summary>
        public int IndexOfBestD()
        {
            int best = -1;
            for (int i = 0; i < Rows.Count; i++) if (best < 0 || Rows[i].D > Rows[best].D) best = i;
            return best;
        }

        /// <summary>Row with the greatest weighted geometric mean of the front's OWN
        /// desirabilities, exp(Σ wᵢ ln dᵢ / Σ wᵢ) with a zero dᵢ scoring 0: the Derringer–Suich
        /// aggregation applied AFTER the front (Lu, Anderson-Cook &amp; Robinson, Technometrics
        /// 2011: find the non-dominated set first, explore the weighting on it afterwards),
        /// over the response goals only. Factor priors are not on the front, so this is a
        /// marker for moving the preference across the trade-off without re-running the
        /// optimizer, not the optimizer's D. -1 with a reason when the weights do not fit
        /// the front (count, sign, finiteness, all zero) or every recipe scores 0; ties go
        /// to the lowest row index.</summary>
        public int IndexOfWeightedBest(IReadOnlyList<double>? weights, out double score, out string? problem)
        {
            score = 0; problem = null;
            if (weights == null || weights.Count == 0) { problem = "no weights"; return -1; }
            int k = ObjectiveNames.Count;
            if (weights.Count != k)
            {
                problem = "weights has " + weights.Count.ToString(CultureInfo.InvariantCulture) + " values, the front has "
                        + k.ToString(CultureInfo.InvariantCulture) + " objectives";
                return -1;
            }
            double wSum = 0;
            for (int i = 0; i < k; i++)
            {
                if (!Num.Finite(weights[i]) || weights[i] < 0) { problem = "weight " + (i + 1).ToString(CultureInfo.InvariantCulture) + " must be a finite number from 0 up"; return -1; }
                wSum += weights[i];
            }
            if (wSum <= 0) { problem = "every weight is 0"; return -1; }
            int best = -1;
            for (int r = 0; r < Rows.Count; r++)
            {
                double s = WeightedScore(Rows[r].Di, weights, wSum);
                if (best < 0 || s > score) { best = r; score = s; }
            }
            if (score <= 0) { best = -1; problem = "every recipe scores 0 for these weights, each has d = 0 on a weighted objective"; }
            return best;
        }

        static double WeightedScore(IReadOnlyList<double> di, IReadOnlyList<double> w, double wSum)
        {
            double logSum = 0;
            for (int i = 0; i < di.Count; i++)
            {
                if (w[i] <= 0) continue;
                if (di[i] <= 0) return 0;
                logSum += w[i] * Math.Log(di[i]);
            }
            return Math.Exp(logSum / wSum);
        }

        public bool SameNames(IReadOnlyList<string> factorNames, IReadOnlyList<string> objectiveNames)
            => SameList(FactorNames, factorNames) && SameList(ObjectiveNames, objectiveNames);

        static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // ---- canonical encoding ----

        internal static string EncodeRecord(IReadOnlyList<double> x, IReadOnlyList<double> di, double d)
        {
            var sb = new StringBuilder();
            foreach (double v in x) sb.Append(Norm(v).ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append('|');
            foreach (double v in di) sb.Append(Norm(v).ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append('|').Append(Norm(d).ToString("R", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string ComputeHash(string[] factorNames, string[] objectiveNames, List<string> records)
        {
            var sorted = new List<string>(records);
            sorted.Sort(StringComparer.Ordinal);   // branch order is not identity
            var sb = new StringBuilder();
            sb.Append("iamps").Append(SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(factorNames.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var n in factorNames) sb.Append(n.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(n).Append('\n');
            sb.Append(objectiveNames.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var n in objectiveNames) sb.Append(n.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(n).Append('\n');
            foreach (var r in sorted) sb.Append(r).Append('\n');
            return Digest(sb.ToString());
        }

        internal static string Digest(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(HashLength);
                for (int i = 0; i < HashLength / 2; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static double Norm(double v) => v == 0 ? 0.0 : v;   // -0 and +0 are one value
    }

    /// <summary>Immutable selection state of the Pareto Select component. The stored
    /// recipe outlives the front that produced it; <see cref="Current"/> says whether
    /// the live front still contains it under the same identity.</summary>
    public sealed class ParetoSelectionState
    {
        public ParetoRecipe? Recipe { get; }
        public bool Current { get; }
        public string Status { get; }
        /// <summary>Row of the live front that matches the stored recipe within tolerance
        /// although the front identity changed; -1 when none. Explicit acceptance is
        /// required before it becomes the selection.</summary>
        public int CandidateIndex { get; }
        /// <summary>The last headless index request consumed, as "index@frontHash".</summary>
        public string ConsumedIndexRequest { get; }
        public IReadOnlyList<double>? ConfirmValues { get; }

        public ParetoSelectionState(ParetoRecipe? recipe, bool current, string status, int candidateIndex, string consumedIndexRequest,
            IReadOnlyList<double>? confirmValues = null)
        { Recipe = recipe; Current = current; Status = status ?? ""; CandidateIndex = candidateIndex; ConsumedIndexRequest = consumedIndexRequest ?? "";
            ConfirmValues = confirmValues == null ? null : Array.AsReadOnly(confirmValues.ToArray()); }

        public static readonly ParetoSelectionState Empty = new ParetoSelectionState(null, false, "no selection", -1, "");

        public ParetoSelectionState With(ParetoRecipe? recipe, bool current, string status, int candidateIndex = -1)
            => new ParetoSelectionState(recipe, current, status, candidateIndex, ConsumedIndexRequest,
                recipe != null && Recipe?.Key == recipe.Key ? ConfirmValues : null);
        public ParetoSelectionState WithConsumed(string request)
            => new ParetoSelectionState(Recipe, Current, Status, CandidateIndex, request, ConfirmValues);
    }

    /// <summary>The pure selection transitions. Every path - window click, canvas click,
    /// headless key or index - ends in <see cref="Select"/>; <see cref="Observe"/> is
    /// what a solve runs to evaluate currency and consume headless requests.</summary>
    public static class ParetoSelection
    {
        /// <summary>Versioned tolerance policy: |a-b| &lt;= 1e-9 * max(1, |a|, |b|).</summary>
        public const double Tolerance = 1e-9;

        public static ParetoSelectionState Confirm(ParetoSelectionState state, IReadOnlyList<double> values)
        {
            if (state.Recipe == null) throw new ArgumentException("Select a recipe before confirming measurements");
            if (values.Count != state.Recipe.ObjectiveNames.Count || values.Any(v => !Num.Finite(v)))
                throw new ArgumentException("confirmValues needs one finite measurement per selected response");
            return new ParetoSelectionState(state.Recipe, state.Current, state.Status, state.CandidateIndex,
                state.ConsumedIndexRequest, values);
        }

        public static string ConfirmationReport(ParetoSelectionState state)
        {
            if (state.Recipe == null) return "no selected recipe to confirm";
            if (state.ConfirmValues == null) return "no confirmation measurements";
            var r = state.Recipe; var rel = r.Reliability;
            var lines = new List<string>();
            for (int i = 0; i < r.ObjectiveNames.Count; i++)
            {
                string measured = state.ConfirmValues[i].ToString("0.###", CultureInfo.InvariantCulture);
                if (rel != null && rel.Computed[i]) { lines.Add(r.ObjectiveNames[i] + ": computed, not checked"); continue; }
                string predicted = rel == null ? "unavailable" : rel.Predicted[i].ToString("0.###", CultureInfo.InvariantCulture);
                bool valid = rel != null && !double.IsNaN(rel.PiLo[i]) && !double.IsNaN(rel.PiHi[i]);
                string result = valid ? (state.ConfirmValues[i] >= rel!.PiLo[i] && state.ConfirmValues[i] <= rel.PiHi[i] ? "inside PI" : "outside PI") : "PI unavailable, not checked";
                string band = valid ? FormattableString.Invariant($"[{rel!.PiLo[i]:0.###}, {rel.PiHi[i]:0.###}]") : "unavailable";
                lines.Add($"{r.ObjectiveNames[i]}: {result}; measured {measured}, predicted {predicted}, interval {band}");
            }
            return string.Join("\n", lines);
        }

        public static bool Near(double a, double b) => Math.Abs(a - b) <= Tolerance * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));

        /// <summary>Explicit selection of one row of the live front.</summary>
        public static ParetoSelectionState Select(ParetoSelectionState state, ParetoFrontSnapshot front, int rowIndex)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (front == null) throw new ArgumentNullException(nameof(front));
            if (rowIndex < 0 || rowIndex >= front.Rows.Count)
                return state.With(state.Recipe, false, "selection index " + rowIndex.ToString(CultureInfo.InvariantCulture)
                    + " is out of range (front has " + front.Rows.Count.ToString(CultureInfo.InvariantCulture) + " recipes)");
            var recipe = front.Rows[rowIndex];
            return state.With(recipe, true, "current (row " + rowIndex.ToString(CultureInfo.InvariantCulture) + ")", -1);
        }

        /// <summary>Forgets the selection; the outputs go empty with an explicit status.</summary>
        public static ParetoSelectionState Reset(ParetoSelectionState state)
            => (state ?? ParetoSelectionState.Empty).With(null, false, "no selection");

        /// <summary>The solve-time evaluation: consumes a headless request (key wins over
        /// index; an invalid request never falls back to another point; an unchanged
        /// index is not a standing order), then reports whether the stored recipe is
        /// current on the supplied front.</summary>
        public static ParetoSelectionState Observe(ParetoSelectionState state, ParetoFrontSnapshot? front,
                                                   string? selectionKey, int? selectionIndex, out string? remark)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            remark = null;
            var s = state;
            bool haveKey = !string.IsNullOrWhiteSpace(selectionKey);
            if (haveKey && selectionIndex.HasValue) remark = "selectionIndex ignored - selectionKey takes precedence";

            if (haveKey)
            {
                if (!ParetoRecipe.TryParseKey(selectionKey, out string frontHash, out string recipeHash))
                    return Evaluate(s, front, "selectionKey is not a key issued by this component; no recipe was chosen");
                if (front == null)
                    return Evaluate(s, front, "selectionKey needs the live front it was issued for; none is wired");
                if (!string.Equals(frontHash, front.Hash, StringComparison.Ordinal))
                    return Evaluate(s, front, "selectionKey belongs to a different front (" + frontHash + ", live " + front.Hash + "); no fallback");
                int row = -1;
                for (int i = 0; i < front.Rows.Count; i++) if (front.Rows[i].RecipeHash == recipeHash) { row = i; break; }
                if (row < 0) return Evaluate(s, front, "selectionKey names a recipe that is not on the live front; no fallback");
                return Select(s, front, row);
            }
            if (selectionIndex.HasValue && front != null)
            {
                string ordinal = selectionIndex.Value.ToString(CultureInfo.InvariantCulture);
                string request = ordinal + "@" + front.Hash;
                string consumed = s.ConsumedIndexRequest;
                if (string.Equals(request, consumed, StringComparison.Ordinal))
                {
                    // same ordinal on the same front: already applied, nothing to redo
                }
                else if (consumed.StartsWith(ordinal + "@", StringComparison.Ordinal))
                {
                    // the same ordinal on a DIFFERENT front is not a standing order: the
                    // retained recipe is flagged and a new request (or a click) is required
                    return Evaluate(s, front, "selectionIndex " + ordinal + " was applied to a previous front - change it (or use a key) to select on this front");
                }
                else
                {
                    var chosen = Select(s, front, selectionIndex.Value).WithConsumed(request);
                    if (chosen.Current) return chosen;
                    return Evaluate(chosen, front, chosen.Status);   // out of range: keep the stored recipe, say why
                }
            }
            else if (selectionIndex.HasValue && front == null)
                return Evaluate(s, front, "selectionIndex needs a live front; none is wired");
            return Evaluate(s, front, null);
        }

        /// <summary>Currency of the stored recipe on the supplied front.</summary>
        public static ParetoSelectionState Evaluate(ParetoSelectionState state, ParetoFrontSnapshot? front, string? note)
        {
            string prefix = string.IsNullOrEmpty(note) ? "" : note + "; ";
            var recipe = state.Recipe;
            if (recipe == null) return state.With(null, false, prefix + "no selection");
            if (front == null) return state.With(recipe, false, prefix + "restored; current front unavailable");
            if (!front.SameNames(recipe.FactorNames, recipe.ObjectiveNames))
                return state.With(recipe, false, prefix + "factor or objective names changed - the stored recipe is kept but not current");
            var matches = front.Matches(recipe);
            bool sameFront = string.Equals(front.Hash, recipe.FrontHash, StringComparison.Ordinal);
            if (sameFront)
            {
                if (matches.Count == 1) return state.With(recipe, true, prefix + "current (row " + matches[0].ToString(CultureInfo.InvariantCulture) + ")", -1);
                if (matches.Count > 1) return state.With(recipe, false, prefix + "ambiguous - " + matches.Count.ToString(CultureInfo.InvariantCulture) + " identical recipes on the front", matches[0]);
                return state.With(recipe, false, prefix + "stored recipe is not on the front", -1);
            }
            if (matches.Count == 1)
                return state.With(recipe, false, prefix + "front changed - the stored recipe still matches row " + matches[0].ToString(CultureInfo.InvariantCulture)
                    + "; select it again to accept the new front", matches[0]);
            if (matches.Count > 1)
                return state.With(recipe, false, prefix + "front changed - ambiguous, " + matches.Count.ToString(CultureInfo.InvariantCulture) + " matching recipes", matches[0]);
            return state.With(recipe, false, prefix + "front changed - the stored recipe is no longer on it", -1);
        }
    }

    /// <summary>The component's persisted payload: recipe, status, consumed request, the
    /// bounded displayed front and the optional optimum marker. Versioned JSON with
    /// invariant round-trip doubles; size and count limits are checked before anything
    /// is allocated on read.</summary>
    public static class ParetoArchive
    {
        public const int Version = 2;
        public const int MaxBytes = 32 * 1024 * 1024;

        public static string Write(ParetoSelectionState state, ParetoFrontSnapshot? displayed, IReadOnlyList<double>? optimum)
        {
            var sb = new StringBuilder();
            sb.Append("{\"version\":").Append(Version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"status\":"); Duet.JsonText.Quote(sb, state.Status);
            sb.Append(",\"consumed\":"); Duet.JsonText.Quote(sb, state.ConsumedIndexRequest);
            if (state.ConfirmValues != null) { sb.Append(",\"confirmValues\":"); Numbers(sb, state.ConfirmValues); }
            sb.Append(",\"recipe\":");
            if (state.Recipe == null) sb.Append("null");
            else
            {
                var r = state.Recipe;
                sb.Append("{\"frontHash\":"); Duet.JsonText.Quote(sb, r.FrontHash);
                sb.Append(",\"factorNames\":"); Strings(sb, r.FactorNames);
                sb.Append(",\"objectiveNames\":"); Strings(sb, r.ObjectiveNames);
                sb.Append(",\"x\":"); Numbers(sb, r.X);
                sb.Append(",\"di\":"); Numbers(sb, r.Di);
                sb.Append(",\"D\":").Append(r.D.ToString("R", CultureInfo.InvariantCulture));
                if (!double.IsNaN(r.Feasible)) sb.Append(",\"feasible\":").Append(r.Feasible.ToString("R", CultureInfo.InvariantCulture));
                if (r.Reliability != null) { sb.Append(",\"reliability\":"); Numbers(sb, r.Reliability.ToColumns()); }
                sb.Append('}');
            }
            sb.Append(",\"front\":");
            if (displayed == null) sb.Append("null");
            else
            {
                sb.Append("{\"hash\":"); Duet.JsonText.Quote(sb, displayed.Hash);
                sb.Append(",\"factorNames\":"); Strings(sb, displayed.FactorNames);
                sb.Append(",\"objectiveNames\":"); Strings(sb, displayed.ObjectiveNames);
                sb.Append(",\"rows\":[");
                for (int i = 0; i < displayed.Rows.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var row = displayed.Rows[i];
                    Numbers(sb, row.ToBranch());
                }
                sb.Append("]}");
            }
            sb.Append(",\"optimum\":");
            if (optimum == null) sb.Append("null"); else Numbers(sb, optimum);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Reads an archive. Returns false with a warning for malformed, oversized
        /// or future-version payloads; a partially usable one restores what it can.</summary>
        public static bool TryRead(string? text, out ParetoSelectionState state, out ParetoFrontSnapshot? displayed,
                                   out double[]? optimum, out string? warning)
        {
            state = ParetoSelectionState.Empty; displayed = null; optimum = null; warning = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text!.Length > MaxBytes) { warning = "Pareto selection archive exceeds " + (MaxBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB; ignored"; return false; }
            Duet.JsonValue json;
            try { json = Duet.JsonValue.Parse(text, MaxBytes, 16, 4_000_000); }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException)
            { warning = "Pareto selection archive is not readable: " + ex.Message; return false; }
            if (json.Kind != Duet.JsonKind.Object) { warning = "Pareto selection archive is not an object"; return false; }
            int version = json.Int("version", -1);
            if (version < 1 || version > Version) { warning = "Pareto selection archive version " + version.ToString(CultureInfo.InvariantCulture) + " is not supported"; return false; }

            ParetoRecipe? recipe = null;
            var r = json["recipe"];
            if (r.Kind == Duet.JsonKind.Object)
            {
                var fn = StringsOf(r["factorNames"]); var on = StringsOf(r["objectiveNames"]);
                var x = NumbersOf(r["x"]); var di = NumbersOf(r["di"]);
                double d = r.Num("D", double.NaN);
                ParetoReliability? reliability = null;
                var columns = NumbersOf(r["reliability"]);
                if (columns != null && on != null && !ParetoReliability.TryParse(columns, on.Length, out reliability))
                    warning = "Pareto selection archive: reliability is malformed and was dropped";
                if (fn != null && on != null && x != null && di != null && x.Length == fn.Length && di.Length == on.Length
                    && !double.IsNaN(d) && !double.IsInfinity(d) && AllFinite(x) && AllFinite(di))
                    recipe = new ParetoRecipe(x, di, d, fn, on, r.Str("frontHash"), r.Num("feasible", double.NaN), reliability);
                else warning = "Pareto selection archive: the stored recipe is malformed and was dropped";
            }
            state = new ParetoSelectionState(recipe, false, json.Str("status", recipe == null ? "no selection" : "restored"), -1, json.Str("consumed"));
            var confirmation = NumbersOf(json["confirmValues"]);
            if (confirmation != null)
            {
                try { state = ParetoSelection.Confirm(state, confirmation); }
                catch (ArgumentException) { warning ??= "Pareto selection archive: invalid confirmation was dropped"; }
            }

            var f = json["front"];
            if (f.Kind == Duet.JsonKind.Object)
            {
                var fn = StringsOf(f["factorNames"]); var on = StringsOf(f["objectiveNames"]);
                var rowsJson = f["rows"];
                if (fn != null && on != null && rowsJson.Kind == Duet.JsonKind.Array && rowsJson.Count <= ParetoFrontSnapshot.MaxRecipes)
                {
                    var rows = new List<IReadOnlyList<double>>(rowsJson.Count);
                    bool ok = true;
                    for (int i = 0; i < rowsJson.Count && ok; i++)
                    {
                        var row = NumbersOf(rowsJson[i]);
                        if (row == null) ok = false; else rows.Add(row);
                    }
                    string? err = null;
                    if (ok && ParetoFrontSnapshot.TryParse(rows, fn, on, out var parsed, out err)) displayed = parsed;
                    else warning ??= "Pareto selection archive: the stored front is malformed (" + (err ?? "rows") + ") and was dropped";
                }
                else warning ??= "Pareto selection archive: the stored front is malformed and was dropped";
            }
            var opt = NumbersOf(json["optimum"]);
            if (opt != null && AllFinite(opt)) optimum = opt;
            return true;
        }

        static void Strings(StringBuilder sb, IReadOnlyList<string> values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Count; i++) { if (i > 0) sb.Append(','); Duet.JsonText.Quote(sb, values[i]); }
            sb.Append(']');
        }

        static void Numbers(StringBuilder sb, IReadOnlyList<double> values)
        {
            sb.Append('[');
            for (int i = 0; i < values.Count; i++) { if (i > 0) sb.Append(','); sb.Append(double.IsNaN(values[i]) ? "null" : double.IsPositiveInfinity(values[i]) ? "\"Infinity\"" : double.IsNegativeInfinity(values[i]) ? "\"-Infinity\"" : values[i].ToString("R", CultureInfo.InvariantCulture)); }
            sb.Append(']');
        }

        static string[]? StringsOf(Duet.JsonValue v)
        {
            if (v.Kind != Duet.JsonKind.Array || v.Count > ParetoFrontSnapshot.MaxFields) return null;
            var a = new string[v.Count];
            for (int i = 0; i < a.Length; i++) { if (v[i].Kind != Duet.JsonKind.String) return null; a[i] = v[i].AsString(); }
            return a;
        }

        static double[]? NumbersOf(Duet.JsonValue v)
        {
            if (v.Kind != Duet.JsonKind.Array || v.Count > 6 * ParetoFrontSnapshot.MaxFields + 4) return null;
            var a = new double[v.Count];
            for (int i = 0; i < a.Length; i++)
            {
                if (v[i].Kind == Duet.JsonKind.Null) a[i] = double.NaN;
                else if (v[i].Kind == Duet.JsonKind.String && v[i].AsString() == "Infinity") a[i] = double.PositiveInfinity;
                else if (v[i].Kind == Duet.JsonKind.String && v[i].AsString() == "-Infinity") a[i] = double.NegativeInfinity;
                else if (v[i].Kind != Duet.JsonKind.Number) return null;
                else a[i] = v[i].AsNumber();
            }
            return a;
        }

        static bool AllFinite(double[] v) { foreach (double d in v) if (double.IsNaN(d) || double.IsInfinity(d)) return false; return true; }
    }
}
