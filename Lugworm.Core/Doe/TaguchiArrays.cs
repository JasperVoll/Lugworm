using System;
using System.Collections.Generic;
using System.Linq;

namespace Lugworm.Core.Doe
{
    /// <summary>Taguchi orthogonal arrays. 2-level arrays (L4/L8/L16) come from
    /// Sylvester-Hadamard matrices, L12 from the Plackett-Burman construction, and
    /// 3-level arrays (L9/L27) from GF(3) linear combinations; the mixed L18 is the
    /// canonical published table. Every array is checked for balance and pairwise
    /// orthogonality by unit tests.</summary>
    public static class TaguchiArrays
    {
        /// <summary>Generate a Taguchi plan. Levels are taken from each FactorDef
        /// (2 or 3). The smallest standard array that fits is selected automatically,
        /// or pass <paramref name="forceArray"/> ("L4","L8","L9","L12","L16","L18","L27").</summary>
        public static DoePlan Taguchi(IReadOnlyList<FactorDef> factors, string? forceArray = null)
        {
            int m = factors.Count;
            if (m < 2) throw new ArgumentException("Taguchi needs ≥ 2 factors.");
            bool any3 = factors.Any(f => f.Levels >= 3);
            int n2 = factors.Count(f => f.Levels <= 2);

            string name = forceArray ?? Select(m, any3, n2);
            int[][] levels = GetArray(name);            // runs × cols, entries 1-based
            int[] colLevels = ColumnLevels(name);

            // assign factors to columns: 2-level factors to 2-level cols first (L18)
            var order = Enumerable.Range(0, m)
                .OrderBy(i => factors[i].Levels)        // 2-level factors first
                .ToArray();
            var colOf = new int[m];
            var used = new bool[colLevels.Length];
            foreach (int fi in order)
            {
                int want = factors[fi].Levels <= 2 ? 2 : 3;
                int col = Array.FindIndex(colLevels, c => c == want);
                // find first unused col of matching level, else any unused col of ≥ level
                col = -1;
                for (int cIdx = 0; cIdx < colLevels.Length; cIdx++)
                    if (!used[cIdx] && colLevels[cIdx] == want) { col = cIdx; break; }
                if (col < 0)
                    for (int cIdx = 0; cIdx < colLevels.Length; cIdx++)
                        if (!used[cIdx] && colLevels[cIdx] > want) { col = cIdx; break; }
                if (col < 0)
                    throw new ArgumentException($"{name} cannot host {m} factors with the requested levels.");
                used[col] = true;
                colOf[fi] = col;
            }

            var coded = new List<double[]>();
            foreach (var run in levels)
            {
                var row = new double[m];
                for (int fi = 0; fi < m; fi++)
                {
                    int lv = run[colOf[fi]];
                    int nl = colLevels[colOf[fi]];
                    // 1..nl → coded: 2-level {-1,+1}; 3-level {-1,0,+1}
                    // a 2-level factor in a 3-level col maps levels {1,2,3}→{-1,+1,+1}? No:
                    // never place 2-level factors in 3-level cols unless forced; then use
                    // dummy-level technique (level 3 repeats level 1 → -1).
                    if (factors[fi].Levels <= 2)
                        row[fi] = lv == 2 ? 1 : -1;               // dummy: 3→-1 (repeat low)
                    else
                        row[fi] = nl == 2 ? (lv == 1 ? -1 : 1) : lv - 2;   // 1,2,3 → -1,0,1
                }
                coded.Add(row);
            }

            var plan = DoeDesign.Build($"Taguchi {name}", factors, coded,
                $"{name} orthogonal array: {coded.Count} runs; main effects only (interactions aliased)");
            return plan;
        }

        static string Select(int m, bool any3, int n2)
        {
            if (!any3)
            {
                if (m <= 3) return "L4";
                if (m <= 7) return "L8";
                if (m <= 11) return "L12";
                if (m <= 15) return "L16";
                throw new ArgumentException("Too many 2-level factors (max 15, L16).");
            }
            int n3 = m - n2;
            if (n2 <= 0 && n3 <= 4) return "L9";
            if (n2 <= 1 && n3 <= 7) return "L18";
            if (n2 <= 0 && n3 <= 13) return "L27";
            if (n3 <= 7) return "L18";               // extra 2-level factors go to 3-level cols via dummy levels
            if (n3 + n2 <= 13) return "L27";
            throw new ArgumentException("Too many factors for the supported arrays (L9/L18/L27).");
        }

        internal static int[] ColumnLevels(string name)
        {
            switch (name)
            {
                case "L4": return Repeat(2, 3);
                case "L8": return Repeat(2, 7);
                case "L12": return Repeat(2, 11);
                case "L16": return Repeat(2, 15);
                case "L9": return Repeat(3, 4);
                case "L27": return Repeat(3, 13);
                case "L18":
                    var c = new int[8]; c[0] = 2;
                    for (int i = 1; i < 8; i++) c[i] = 3;
                    return c;
                default: throw new ArgumentException("Unknown array " + name);
            }
        }

        static int[] Repeat(int v, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = v; return a; }

        /// <summary>Array rows with 1-based level codes.</summary>
        internal static int[][] GetArray(string name)
        {
            switch (name)
            {
                case "L4": return Hadamard2Level(4);
                case "L8": return Hadamard2Level(8);
                case "L16": return Hadamard2Level(16);
                case "L12": return PlackettBurman12();
                case "L9": return Gf3Array(2, new[] { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, 2 } });
                case "L27":
                    return Gf3Array(3, new[]
                    {
                        new[]{1,0,0}, new[]{0,1,0}, new[]{1,1,0}, new[]{1,2,0},
                        new[]{0,0,1}, new[]{1,0,1}, new[]{1,0,2}, new[]{0,1,1},
                        new[]{0,1,2}, new[]{1,1,1}, new[]{1,1,2}, new[]{1,2,1}, new[]{1,2,2},
                    });
                case "L18": return L18;
                default: throw new ArgumentException("Unknown array " + name);
            }
        }

        /// <summary>2-level OA from the Sylvester-Hadamard matrix of order n: drop the
        /// all-ones column, map +→1, −→2. n runs × (n−1) columns.</summary>
        static int[][] Hadamard2Level(int n)
        {
            var h = new int[n, n];
            h[0, 0] = 1;
            for (int size = 1; size < n; size *= 2)
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                    {
                        h[i + size, j] = h[i, j];
                        h[i, j + size] = h[i, j];
                        h[i + size, j + size] = -h[i, j];
                    }
            var rows = new int[n][];
            for (int i = 0; i < n; i++)
            {
                rows[i] = new int[n - 1];
                for (int j = 1; j < n; j++) rows[i][j - 1] = h[i, j] > 0 ? 1 : 2;
            }
            return rows;
        }

        /// <summary>Plackett-Burman N=12: cyclic generator row + all-minus row.</summary>
        static int[][] PlackettBurman12()
        {
            int[] gen = { 1, 1, -1, 1, 1, 1, -1, -1, -1, 1, -1 };
            var rows = new List<int[]>();
            for (int r = 0; r < 11; r++)
            {
                var row = new int[11];
                for (int j = 0; j < 11; j++) row[j] = gen[(j - r + 11) % 11] > 0 ? 1 : 2;
                rows.Add(row);
            }
            rows.Add(Enumerable.Repeat(2, 11).ToArray());
            return rows.ToArray();
        }

        /// <summary>3-level OA over GF(3): runs are all base-factor tuples; each column
        /// is a linear combination Σ cᵢ×aᵢ mod 3, reported 1-based.</summary>
        static int[][] Gf3Array(int nBase, int[][] colCoefs)
        {
            int runs = (int)Math.Pow(3, nBase);
            var rows = new int[runs][];
            for (int r = 0; r < runs; r++)
            {
                var a = new int[nBase];
                int t = r;
                for (int i = 0; i < nBase; i++) { a[i] = t % 3; t /= 3; }
                rows[r] = colCoefs.Select(cc =>
                {
                    int s = 0;
                    for (int i = 0; i < nBase; i++) s += cc[i] * a[i];
                    return s % 3 + 1;
                }).ToArray();
            }
            return rows;
        }

        // Canonical L18 (2^1 × 3^7) - verified orthogonal by unit test.
        static readonly int[][] L18 =
        {
            new[]{1,1,1,1,1,1,1,1},
            new[]{1,1,2,2,2,2,2,2},
            new[]{1,1,3,3,3,3,3,3},
            new[]{1,2,1,1,2,2,3,3},
            new[]{1,2,2,2,3,3,1,1},
            new[]{1,2,3,3,1,1,2,2},
            new[]{1,3,1,2,1,3,2,3},
            new[]{1,3,2,3,2,1,3,1},
            new[]{1,3,3,1,3,2,1,2},
            new[]{2,1,1,3,3,2,2,1},
            new[]{2,1,2,1,1,3,3,2},
            new[]{2,1,3,2,2,1,1,3},
            new[]{2,2,1,2,3,1,3,2},
            new[]{2,2,2,3,1,2,1,3},
            new[]{2,2,3,1,2,3,2,1},
            new[]{2,3,1,3,2,3,1,2},
            new[]{2,3,2,1,3,1,2,3},
            new[]{2,3,3,2,1,2,3,1},
        };
    }
}
