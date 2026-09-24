using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lugworm.Core.Doe
{
    /// <summary>One experimental factor: name plus its actual-unit range. Coded level
    /// -1 maps to Low, +1 to High, 0 to Centre (defaults to the midpoint).</summary>
    public sealed class FactorDef
    {
        public string Name;
        public double Low, High;
        public double? Centre;          // optional non-midpoint centre level
        public int Levels;              // 2 or 3 - only Taguchi distinguishes

        public FactorDef(string name, double low, double high, int levels = 3, double? centre = null)
        { Name = name; Low = low; High = high; Levels = levels; Centre = centre; }

        public double CentreValue => Centre ?? 0.5 * (Low + High);

        /// <summary>Coded [-1, 1] → actual units (0 honours a non-midpoint centre).</summary>
        public double ToActual(double coded)
        {
            if (Math.Abs(coded) < 1e-12) return CentreValue;
            return coded < 0
                ? CentreValue + coded * (CentreValue - Low)
                : CentreValue + coded * (High - CentreValue);
        }

        public double ToCoded(double actual)
        {
            double c = CentreValue;
            if (Math.Abs(actual - c) < 1e-12) return 0.0;
            return actual < c
                ? (actual - c) / (c - Low)
                : (actual - c) / (High - c);
        }
    }

    /// <summary>A generated experimental design: run matrix in coded and actual units.</summary>
    public sealed class DoePlan
    {
        public string Design = "";              // "DSD", "BBD", "Taguchi L18", "FullFact2"
        public string[] FactorNames = Array.Empty<string>();
        public double[] Lows = Array.Empty<double>(), Highs = Array.Empty<double>();
        public double[][] Coded = Array.Empty<double[]>();   // runs × m
        public double[][] Actual = Array.Empty<double[]>();
        public string[] RunLabels = Array.Empty<string>();
        public string Notes = "";

        public int RunCount => Coded.Length;
        public int FactorCount => FactorNames.Length;

        /// <summary>Replicate every row, including centres, before shuffling the plan.</summary>
        public void Replicate(int replicates)
        {
            if (replicates < 1) throw new ArgumentOutOfRangeException(nameof(replicates));
            if (replicates > 1)
            {
                var rows = Enumerable.Range(0, RunCount)
                    .SelectMany(i => Enumerable.Range(1, replicates).Select(r => (i, r))).ToArray();
                Coded = rows.Select(row => (double[])Coded[row.i].Clone()).ToArray();
                Actual = rows.Select(row => (double[])Actual[row.i].Clone()).ToArray();
                RunLabels = rows.Select(row => RunLabels[row.i] + "r" + row.r.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            }
            Notes += (Notes.Length > 0 ? " | " : "") + FormattableString.Invariant($"replicates = {replicates}");
        }

        /// <summary>Fisher–Yates shuffle of the run order (labels travel with rows).
        /// Deterministic for a given seed so a plan can be reproduced.</summary>
        public void Randomize(int seed)
        {
            if (seed == -1) return;
            if (seed < -1) throw new ArgumentOutOfRangeException(nameof(seed));
            var rng = new Random(seed);
            for (int i = Coded.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (Coded[i], Coded[j]) = (Coded[j], Coded[i]);
                (Actual[i], Actual[j]) = (Actual[j], Actual[i]);
                (RunLabels[i], RunLabels[j]) = (RunLabels[j], RunLabels[i]);
            }
            Notes += (Notes.Length > 0 ? " | " : "") + $"run order randomized (seed {seed})";
        }
    }

    /// <summary>Generators for the three calibration designs of the FBI2026 workflow
    /// (definitive screening, Box-Behnken, Taguchi) plus a 2-level full factorial.
    /// All construction is deterministic and verified by unit tests
    /// (orthogonality / balance properties), not copied blindly from tables.</summary>
    public static class DoeDesign
    {
        // ── DSD ──────────────────────────────────────────────────────────────────

        /// <summary>Definitive screening design (Jones &amp; Nachtsheim 2011, conference-
        /// matrix construction of Xiao, Lin &amp; Bai 2012): runs = [C; −C; 0], 2m+1 runs
        /// for even m. Odd m uses the (m+1)-column design with the last column dropped
        /// (the recommended "fake factor" approach, 2m+3 runs). Supports 4–12 factors.</summary>
        public static DoePlan Dsd(IReadOnlyList<FactorDef> factors, int extraCentre = 0)
        {
            int m = factors.Count;
            if (m < 4) throw new ArgumentException("DSD needs ≥ 4 factors (use BBD or full factorial below that).");
            if (m > 12) throw new ArgumentException("DSD supported for 4–12 factors.");

            int n = m % 2 == 0 ? m : m + 1;      // conference-matrix order
            int[,] c = ConferenceMatrix(n);

            var coded = new List<double[]>();
            for (int i = 0; i < n; i++)
            {
                var plus = new double[m];
                var minus = new double[m];
                for (int j = 0; j < m; j++) { plus[j] = c[i, j]; minus[j] = -c[i, j]; }
                coded.Add(plus);
                coded.Add(minus);
            }
            for (int k = 0; k <= extraCentre; k++) coded.Add(new double[m]);   // ≥1 centre run

            string note = m % 2 == 0
                ? $"DSD: {coded.Count} runs (2×{n} fold-over pairs + {1 + extraCentre} centre)"
                : $"DSD (odd m, {n}-col construction, last col dropped): {coded.Count} runs";
            return Build("DSD", factors, coded, note);
        }

        /// <summary>Conference matrix C (n×n, zero diagonal, ±1 off-diagonal,
        /// C×Cᵀ = (n−1)×I) found by deterministic lexicographic backtracking with the
        /// first row/column normalised to +1. Cached per order. Orders 4–12 all admit
        /// solutions; the search is instant at these sizes.</summary>
        internal static int[,] ConferenceMatrix(int n)
        {
            lock (ConfCache)
            {
                if (ConfCache.TryGetValue(n, out var hit)) return hit;
                var rows = new List<int[]>();
                var r0 = new int[n];
                for (int j = 1; j < n; j++) r0[j] = 1;
                rows.Add(r0);
                if (!SearchConferenceRow(rows, n))
                    throw new InvalidOperationException($"No conference matrix of order {n} found.");
                var c = new int[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) c[i, j] = rows[i][j];
                ConfCache[n] = c;
                return c;
            }
        }

        static readonly Dictionary<int, int[,]> ConfCache = new Dictionary<int, int[,]>();

        static bool SearchConferenceRow(List<int[]> rows, int n)
        {
            int i = rows.Count;
            if (i == n) return true;
            // row i: [0]=+1 (normalised), [i]=0, remaining n-2 entries ±1
            var free = new List<int>();
            for (int j = 1; j < n; j++) if (j != i) free.Add(j);
            var row = new int[n];
            row[0] = 1;
            for (long mask = 0; mask < 1L << free.Count; mask++)
            {
                for (int k = 0; k < free.Count; k++)
                    row[free[k]] = ((mask >> k) & 1) == 0 ? 1 : -1;
                bool ok = true;
                foreach (var prev in rows)
                {
                    int dot = 0;
                    for (int j = 0; j < n; j++) dot += prev[j] * row[j];
                    if (dot != 0) { ok = false; break; }
                }
                if (!ok) continue;
                rows.Add((int[])row.Clone());
                if (SearchConferenceRow(rows, n)) return true;
                rows.RemoveAt(rows.Count - 1);
            }
            return false;
        }

        // ── BBD ──────────────────────────────────────────────────────────────────

        static readonly Dictionary<int, int> BbdDefaultCentres = new Dictionary<int, int>
        { { 3, 3 }, { 4, 3 }, { 5, 6 }, { 6, 6 }, { 7, 6 } };

        // Box & Behnken (1960): k=3..5 use all factor pairs at ±1 (2² each);
        // k=6,7 use balanced incomplete blocks of three factors at ±1 (2³ each).
        static readonly int[][] Bbd6Blocks =
        {
            new[]{0,1,3}, new[]{1,2,4}, new[]{2,3,5}, new[]{3,4,0}, new[]{4,5,1}, new[]{5,0,2},
        };
        static readonly int[][] Bbd7Blocks =   // Fano-plane triples
        {
            new[]{0,1,3}, new[]{1,2,4}, new[]{2,3,5}, new[]{3,4,6}, new[]{4,5,0}, new[]{5,6,1}, new[]{6,0,2},
        };

        /// <summary>Box-Behnken design for 3–7 factors. centrePoints &lt; 0 → standard
        /// default (3/3/6/6/6). All runs stay inside the ±1 face centres - no corner
        /// extremes, which suits print processes where corner recipes often fail.</summary>
        public static DoePlan Bbd(IReadOnlyList<FactorDef> factors, int centrePoints = -1)
        {
            int k = factors.Count;
            if (k < 3 || k > 7) throw new ArgumentException("BBD supported for 3–7 factors.");
            if (centrePoints < 0) centrePoints = BbdDefaultCentres[k];

            var coded = new List<double[]>();
            if (k <= 5)
            {
                for (int a = 0; a < k - 1; a++)
                    for (int b = a + 1; b < k; b++)
                        foreach (var (sa, sb) in FourSigns)
                        {
                            var row = new double[k];
                            row[a] = sa; row[b] = sb;
                            coded.Add(row);
                        }
            }
            else
            {
                var blocks = k == 6 ? Bbd6Blocks : Bbd7Blocks;
                foreach (var blk in blocks)
                    for (int s = 0; s < 8; s++)
                    {
                        var row = new double[k];
                        for (int j = 0; j < 3; j++)
                            row[blk[j]] = ((s >> j) & 1) == 0 ? -1 : 1;
                        coded.Add(row);
                    }
            }
            for (int i = 0; i < centrePoints; i++) coded.Add(new double[k]);

            string note = $"BBD: {coded.Count} runs ({coded.Count - centrePoints} design + {centrePoints} centre); " +
                          "supports full quadratic RSM";
            return Build("BBD", factors, coded, note);
        }

        static readonly (int, int)[] FourSigns = { (-1, -1), (-1, 1), (1, -1), (1, 1) };

        // ── Full factorial (2-level) ─────────────────────────────────────────────

        public static DoePlan FullFactorial2(IReadOnlyList<FactorDef> factors, int centrePoints = 0)
        {
            int k = factors.Count;
            if (k < 1 || k > 10) throw new ArgumentException("Full factorial supported for 1–10 factors.");
            var coded = new List<double[]>();
            for (int s = 0; s < 1 << k; s++)
            {
                var row = new double[k];
                for (int j = 0; j < k; j++) row[j] = ((s >> j) & 1) == 0 ? -1 : 1;
                coded.Add(row);
            }
            for (int i = 0; i < centrePoints; i++) coded.Add(new double[k]);
            return Build("FullFact2", factors, coded,
                $"2^{k} full factorial: {coded.Count} runs (+{centrePoints} centre); mains + all interactions, no pure quadratics");
        }

        // ── Fractional factorial (2-level, 2^(k−p)) ─────────────────────────────

        // standard minimum-aberration generators; letters = base-factor products.
        // key: (factors, runs) → generators for the extra factors, in order
        static readonly Dictionary<(int k, int runs), string[]> FracGenerators = new Dictionary<(int, int), string[]>
        {
            { (4, 8),   new[] { "ABC" } },                          // Res IV
            { (5, 8),   new[] { "AB", "AC" } },                     // Res III
            { (5, 16),  new[] { "ABCD" } },                         // Res V
            { (6, 8),   new[] { "AB", "AC", "BC" } },               // Res III
            { (6, 16),  new[] { "ABC", "BCD" } },                   // Res IV
            { (7, 8),   new[] { "AB", "AC", "BC", "ABC" } },        // Res III (saturated)
            { (7, 16),  new[] { "ABC", "BCD", "ACD" } },            // Res IV
            { (8, 16),  new[] { "BCD", "ACD", "ABC", "ABD" } },     // Res IV
            { (9, 16),  new[] { "ABC", "BCD", "ACD", "ABD", "ABCD" } }, // Res III
        };

        static readonly Dictionary<(int k, int runs), string> FracResolution = new Dictionary<(int, int), string>
        {
            { (4, 8), "IV" }, { (5, 8), "III" }, { (5, 16), "V" }, { (6, 8), "III" },
            { (6, 16), "IV" }, { (7, 8), "III" }, { (7, 16), "IV" }, { (8, 16), "IV" }, { (9, 16), "III" },
        };

        /// <summary>Regular 2^(k−p) fractional factorial for 4–9 factors using standard
        /// minimum-aberration generators. <paramref name="targetRuns"/> 8 or 16 selects
        /// the fraction; ≤0 picks the smallest with resolution ≥ IV (falls back to the
        /// 8-run Res III design only when nothing better exists). The defining relation
        /// and resolution are reported in Notes - check the aliasing before trusting
        /// interaction estimates.</summary>
        public static DoePlan FractionalFactorial2(IReadOnlyList<FactorDef> factors, int targetRuns = -1, int centrePoints = 0)
        {
            int k = factors.Count;
            if (k < 4) return FullFactorial2(factors, Math.Max(0, centrePoints));   // no fraction needed below 4
            if (k > 9) throw new ArgumentException("Fractional factorial supported for 4–9 factors (use DSD above that).");

            int runs;
            if (targetRuns == 8 || targetRuns == 16)
            {
                runs = targetRuns;
                if (!FracGenerators.ContainsKey((k, runs)))
                    throw new ArgumentException($"no standard 2^({k}-p) design with {runs} runs - use {(runs == 8 ? 16 : 8)} runs");
            }
            else
            {
                runs = FracResolution.TryGetValue((k, 16), out var r16) && (r16 == "IV" || r16 == "V") ? 16 : 8;
                if (FracResolution.TryGetValue((k, 8), out var r8) && r8 == "IV") runs = 8;
            }

            var gens = FracGenerators[(k, runs)];
            int nBase = k - gens.Length;
            var coded = new List<double[]>();
            for (int s = 0; s < runs; s++)
            {
                var row = new double[k];
                for (int j = 0; j < nBase; j++) row[j] = ((s >> j) & 1) == 0 ? -1 : 1;
                for (int gi = 0; gi < gens.Length; gi++)
                {
                    double v = 1;
                    foreach (char ch in gens[gi]) v *= row[ch - 'A'];
                    row[nBase + gi] = v;
                }
                coded.Add(row);
            }
            for (int i = 0; i < centrePoints; i++) coded.Add(new double[k]);

            string rel = string.Join(", ", gens.Select((gn, gi) => $"{(char)('A' + nBase + gi)}={gn}"));
            string res = FracResolution[(k, runs)];
            var plan = Build("FracFact2", factors, coded,
                $"2^({k}-{gens.Length}) fractional factorial, resolution {res}: {coded.Count} runs; " +
                $"generators {rel}" + (res == "III" ? " - mains aliased with 2-factor interactions!" : ""));
            return plan;
        }

        // ── augmentation ─────────────────────────────────────────────────────────

        /// <summary>Whether <see cref="AugmentFaceCentred"/> accepts the plan: the two-level
        /// factorials only, whose corners plus face-centred star points form a central
        /// composite design with alpha = 1.</summary>
        public static bool CanAugment(DoePlan plan)
            => plan.Design == "FullFact2" || plan.Design == "FracFact2";

        /// <summary>Extends a two-level plan to a face-centred central composite design after
        /// the fact, as an explicit choice: 2m star points at ±1 on each axis (alpha = 1, so no
        /// run leaves the declared factor range) plus <paramref name="centrePoints"/> centre
        /// runs are appended after the original rows and labelled aug01…, so a block that has
        /// already been printed keeps its order and its labels. Only the added block is
        /// shuffled with <paramref name="seed"/> (-1 keeps it in order); Notes state what
        /// was added. Any other design is refused - the fitter never proposes this.</summary>
        public static void AugmentFaceCentred(DoePlan plan, IReadOnlyList<FactorDef> factors, int centrePoints, int seed = -1)
        {
            if (!CanAugment(plan)) throw new ArgumentException("augment applies to FullFact2 and FracFact2 plans only");
            if (factors.Count != plan.FactorCount) throw new ArgumentException("factors must match the plan");
            if (centrePoints < 0) throw new ArgumentOutOfRangeException(nameof(centrePoints));
            if (seed < -1) throw new ArgumentOutOfRangeException(nameof(seed));
            int m = plan.FactorCount;
            var block = new List<double[]>();
            for (int j = 0; j < m; j++)
                foreach (double s in new[] { -1.0, 1.0 })
                {
                    var row = new double[m];
                    row[j] = s;
                    block.Add(row);
                }
            for (int i = 0; i < centrePoints; i++) block.Add(new double[m]);
            var labels = Enumerable.Range(1, block.Count).Select(i => $"aug{i:00}").ToArray();
            if (seed >= 0)
            {
                var rng = new Random(seed);
                for (int i = block.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (block[i], block[j]) = (block[j], block[i]);
                    (labels[i], labels[j]) = (labels[j], labels[i]);
                }
            }
            plan.Coded = plan.Coded.Concat(block).ToArray();
            plan.Actual = plan.Actual.Concat(block.Select(row =>
            {
                var a = new double[m];
                for (int j = 0; j < m; j++) a[j] = factors[j].ToActual(row[j]);
                return a;
            })).ToArray();
            plan.RunLabels = plan.RunLabels.Concat(labels).ToArray();
            plan.Notes += (plan.Notes.Length > 0 ? " | " : "")
                + FormattableString.Invariant($"augmented to a face-centred CCD: {2 * m} star + {centrePoints} centre runs appended")
                + (seed >= 0 ? FormattableString.Invariant($", added block shuffled (seed {seed})") : "");
        }

        // ── shared ───────────────────────────────────────────────────────────────

        internal static DoePlan Build(string design, IReadOnlyList<FactorDef> factors,
                                      List<double[]> coded, string note)
        {
            int m = factors.Count;
            var plan = new DoePlan
            {
                Design = design,
                FactorNames = factors.Select(f => f.Name).ToArray(),
                Lows = factors.Select(f => f.Low).ToArray(),
                Highs = factors.Select(f => f.High).ToArray(),
                Coded = coded.ToArray(),
                Actual = coded.Select(row =>
                {
                    var a = new double[m];
                    for (int j = 0; j < m; j++) a[j] = factors[j].ToActual(row[j]);
                    return a;
                }).ToArray(),
                Notes = note,
            };
            string tag = design.StartsWith("Taguchi", StringComparison.Ordinal) ? "T" : design.Substring(0, Math.Min(3, design.Length)).ToUpperInvariant();
            plan.RunLabels = Enumerable.Range(1, plan.RunCount).Select(i => $"{tag}{i:00}").ToArray();
            return plan;
        }

        /// <summary>Fixed-width text table of the run plan - readable in a GH panel.</summary>
        public static string TextTable(DoePlan p)
        {
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"{p.Design}  |  {p.RunCount} runs × {p.FactorCount} factors"));
            sb.AppendLine(p.Notes);
            var heads = new List<string> { "Run" };
            heads.AddRange(p.FactorNames);
            heads.AddRange(p.FactorNames.Select(n => "x_" + n));
            var widths = heads.Select(h => Math.Max(h.Length, 8)).ToArray();
            sb.AppendLine(string.Join("  ", heads.Select((h, i) => h.PadRight(widths[i]))));
            for (int r = 0; r < p.RunCount; r++)
            {
                var cells = new List<string> { p.RunLabels[r] };
                cells.AddRange(p.Actual[r].Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
                cells.AddRange(p.Coded[r].Select(v => v.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture)));
                sb.AppendLine(string.Join("  ", cells.Select((cell, i) => cell.PadRight(widths[i]))));
            }
            return sb.ToString();
        }
    }
}
