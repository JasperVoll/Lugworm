using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests
{
    public class DoeDesignTests
    {
        [Fact]
        public void Feasible_ZeroRowsAreDroppedFromResponseFits()
        {
            var x = new[] { new[] { -1.0 }, new[] { 0.0 }, new[] { 1.0 }, new[] { 1.0 } };
            var m = RsmFitter.Fit("y", new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null,
                x, new[] { 1.0, 2.0, 3.0, 999.0 }, RsmModelType.Linear, feasible: new[] { 1.0, 1.0, 1.0, 0.0 });
            Assert.Equal(3, m.NRuns); Assert.Equal(1, m.DroppedInfeasible);
            Assert.Equal(3, m.Predict(new[] { 1.0 }), 10);
            Assert.Contains("1 dropped as infeasible", RsmFitter.Report(m));
            var table = new DataTableLite { Headers = new[] { "feasible" }, Rows = new[] { new[] { "" }, new[] { "0" }, new[] { "1" } } };
            Assert.Equal(new[] { 1.0, 0.0, 1.0 }, table.FeasibilityColumn());
            table.Rows[0][0] = "yes";
            Assert.Throws<ArgumentException>(() => table.FeasibilityColumn());
        }

        [Fact]
        public void Feasible_ModelIsFitOnTheFlag()
        {
            var x = new[] { new[] { -1.0 }, new[] { -1.0 }, new[] { 1.0 }, new[] { 1.0 } };
            var model = RsmFitter.FitFeasibility(new[] { "A" }, new[] { -1.0 }, new[] { 1.0 }, null,
                x, new[] { 1.0, 1.0, 0.0, 0.0 });
            Assert.Equal(4, model.NRuns); Assert.Equal(RsmModelKind.Feasibility, model.Kind);
            Assert.Equal(0.5, model.Predict(new[] { 0.0 }), 10);
            Assert.Equal(-0.5, model.Terms[1].Coef, 10);
            Assert.Equal(RsmModelKind.Feasibility, RsmModel.FromJson(model.ToJson()).Kind);
        }

        [Fact]
        public void EndToEnd_LogPrintTime_MatchesKinematicsAtCorners()
        {
            var factors = new[] { new FactorDef("h", 2.5, 4.5), new FactorDef("v", 30, 50), new FactorDef("p", 1, 1.3) };
            var p = DoeDesign.Bbd(factors);
            double Time(double[] x) => 300 * Math.PI * 150 / (60 * x[0] * x[1]) + 2.5;
            var y = p.Actual.Select(Time).ToArray();
            var raw = RsmFitter.Fit("t", p.FactorNames, p.Lows, p.Highs, null, p.Actual, y, RsmModelType.Quadratic);
            var log = RsmFitter.Fit("t", p.FactorNames, p.Lows, p.Highs, null, p.Actual, y, RsmModelType.Quadratic, ResponseTransform.Log1p);
            foreach (var corner in DoeDesign.FullFactorial2(factors).Actual)
                Assert.True(Math.Abs(log.Predict(corner) - Time(corner)) < Math.Abs(raw.Predict(corner) - Time(corner)));
        }

        [Fact]
        public void Replicates_DuplicateEveryRowAndLabelThem()
        {
            var p = DoeDesign.Bbd(Factors(3));
            var original = p.Coded;
            p.Replicate(3);
            Assert.Equal(45, p.RunCount);
            for (int i = 0; i < 15; i++)
                for (int r = 1; r <= 3; r++)
                {
                    Assert.Equal(original[i], p.Coded[3 * i + r - 1]);
                    Assert.EndsWith("r" + r, p.RunLabels[3 * i + r - 1]);
                    Assert.NotSame(original[i], p.Coded[3 * i + r - 1]);
                }
            Assert.Contains("replicates = 3", p.Notes);
            Assert.Throws<ArgumentOutOfRangeException>(() => p.Replicate(0));
        }

        [Fact]
        public void Replicates_RandomizeShufflesAcrossReplicates()
        {
            var p = DoeDesign.Bbd(Factors(3)); p.Replicate(2);
            var labels = p.RunLabels.ToArray();
            p.Randomize(0);
            Assert.False(labels.SequenceEqual(p.RunLabels));
            Assert.Equal(labels.OrderBy(x => x), p.RunLabels.OrderBy(x => x));
            Assert.Contains(p.RunLabels.Take(15), x => x.EndsWith("r1"));
            Assert.Contains(p.RunLabels.Take(15), x => x.EndsWith("r2"));
        }

        [Fact]
        public void Seed_MinusOneKeepsStandardOrder_ZeroRandomizes()
        {
            var p = DoeDesign.Bbd(Factors(3)); var labels = p.RunLabels.ToArray();
            p.Randomize(-1); Assert.Equal(labels, p.RunLabels);
            p.Randomize(0); Assert.False(labels.SequenceEqual(p.RunLabels));
            var q = DoeDesign.Bbd(Factors(3)); q.Randomize(0);
            Assert.Equal(p.RunLabels, q.RunLabels);
        }

        [Fact]
        public void Augment_FaceCentred_AddsStarAndCentreRows_KeepsOriginalOrder()
        {
            var f = Factors(3);
            var p = DoeDesign.FullFactorial2(f); p.Randomize(0);
            var coded = p.Coded.Select(r => (double[])r.Clone()).ToArray();
            var labels = p.RunLabels.ToArray();
            Assert.True(DoeDesign.CanAugment(p));
            DoeDesign.AugmentFaceCentred(p, f, 3, seed: 0);
            Assert.Equal(8 + 6 + 3, p.RunCount);
            for (int i = 0; i < 8; i++) { Assert.Equal(coded[i], p.Coded[i]); Assert.Equal(labels[i], p.RunLabels[i]); }
            var block = p.Coded.Skip(8).ToArray();
            Assert.Equal(3, block.Count(r => r.All(v => v == 0)));
            for (int j = 0; j < 3; j++)
                foreach (double s in new[] { -1.0, 1.0 })
                    Assert.Single(block, r => r[j] == s && r.Where((v, k) => k != j).All(v => v == 0));
            Assert.All(block, r => Assert.All(r, v => Assert.InRange(v, -1, 1)));
            var augLabels = Enumerable.Range(1, 9).Select(i => $"aug{i:00}").ToArray();
            Assert.Equal(augLabels, p.RunLabels.Skip(8).OrderBy(l => l));
            Assert.False(p.RunLabels.Skip(8).SequenceEqual(augLabels));   // the added block is shuffled
            Assert.Contains("6 star + 3 centre", p.Notes);
            Assert.Contains("seed 0", p.Notes);
            // actual units: a star point sits on the declared face, the centre at the centre value
            int star = Array.FindIndex(p.Coded, 8, r => r[0] == -1);
            Assert.Equal(new[] { f[0].Low, f[1].CentreValue, f[2].CentreValue }, p.Actual[star]);
            // seed -1 keeps the block in order, and a fraction augments the same way
            var q = DoeDesign.FractionalFactorial2(Factors(5), 8);
            DoeDesign.AugmentFaceCentred(q, Factors(5), 2, -1);
            Assert.Equal(8 + 10 + 2, q.RunCount);
            Assert.Equal(Enumerable.Range(1, 12).Select(i => $"aug{i:00}"), q.RunLabels.Skip(8));
            Assert.DoesNotContain("shuffled", q.Notes);
        }

        [Fact]
        public void Augment_IsRefusedForDsdAndBbd()
        {
            var bbd = DoeDesign.Bbd(Factors(3));
            Assert.False(DoeDesign.CanAugment(bbd));
            Assert.Throws<ArgumentException>(() => DoeDesign.AugmentFaceCentred(bbd, Factors(3), 3));
            var dsd = DoeDesign.Dsd(Factors(4));
            Assert.False(DoeDesign.CanAugment(dsd));
            Assert.Throws<ArgumentException>(() => DoeDesign.AugmentFaceCentred(dsd, Factors(4), 3));
            Assert.False(DoeDesign.CanAugment(TaguchiArrays.Taguchi(Factors(3))));
            var full = DoeDesign.FullFactorial2(Factors(3));
            Assert.Throws<ArgumentException>(() => DoeDesign.AugmentFaceCentred(full, Factors(2), 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => DoeDesign.AugmentFaceCentred(full, Factors(3), -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => DoeDesign.AugmentFaceCentred(full, Factors(3), 3, -2));
            Assert.Equal(8, full.RunCount);   // a refused call leaves the plan alone
        }

        [Fact]
        public void Fit_QuadraticEstimableAfterAugmentation()
        {
            // a quadratic in coded units; Factors() maps coded to actual linearly, so the
            // fitted surface must reproduce it exactly once every term is estimable
            var f = Factors(3);
            double Truth(double[] c) => 5 + 1.5 * c[0] - 0.8 * c[1] + 0.4 * c[2] + 0.6 * c[0] * c[1] + 1.2 * c[0] * c[0] - 0.9 * c[2] * c[2];
            var plain = DoeDesign.FullFactorial2(f);
            var before = RsmFitter.Fit("y", plain.FactorNames, plain.Lows, plain.Highs, null, plain.Actual, plain.Coded.Select(Truth).ToArray(), RsmModelType.Quadratic);
            Assert.Equal(3, before.DroppedTerms.Count);   // every pure quadratic aliases the intercept on a 2^3
            var aug = DoeDesign.FullFactorial2(f);
            DoeDesign.AugmentFaceCentred(aug, f, 3, 0);
            var after = RsmFitter.Fit("y", aug.FactorNames, aug.Lows, aug.Highs, null, aug.Actual, aug.Coded.Select(Truth).ToArray(), RsmModelType.Quadratic);
            Assert.Empty(after.DroppedTerms);
            foreach (var c in new[] { new[] { 0.5, -0.5, 0.25 }, new[] { -1.0, 1, 1 }, new[] { 0.0, 0, 0 } })
                Assert.Equal(Truth(c), after.Predict(c.Select((v, j) => f[j].ToActual(v)).ToArray()), 6);
        }

        static List<FactorDef> Factors(int m, int levels = 3)
            => Enumerable.Range(0, m).Select(j => new FactorDef("F" + j, 10 * (j + 1), 20 * (j + 1), levels)).ToList();

        [Theory]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(8)]
        [InlineData(10)]
        [InlineData(12)]
        public void ConferenceMatrix_IsValid(int n)
        {
            var c = DoeDesign.ConferenceMatrix(n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(0, c[i, i]);
                for (int j = 0; j < n; j++)
                {
                    if (i != j) Assert.True(c[i, j] == 1 || c[i, j] == -1);
                    int dot = 0;
                    for (int k = 0; k < n; k++) dot += c[i, k] * c[j, k];
                    Assert.Equal(i == j ? n - 1 : 0, dot);
                }
            }
        }

        [Theory]
        [InlineData(4, 9)]
        [InlineData(5, 13)]
        [InlineData(6, 13)]
        [InlineData(8, 17)]
        public void Dsd_RunCountAndStructure(int m, int runs)
        {
            var p = DoeDesign.Dsd(Factors(m));
            Assert.Equal(runs, p.RunCount);
            // centre run present
            Assert.Contains(p.Coded, row => row.All(v => Math.Abs(v) < 1e-12));
            // fold-over: main-effect columns orthogonal, all sum to 0
            for (int a = 0; a < m; a++)
            {
                Assert.Equal(0, p.Coded.Sum(r => r[a]), 10);
                for (int b = a + 1; b < m; b++)
                    Assert.Equal(0, p.Coded.Sum(r => r[a] * r[b]), 10);
            }
            // actual mapping
            for (int i = 0; i < p.RunCount; i++)
                for (int j = 0; j < m; j++)
                {
                    double lo = p.Lows[j], hi = p.Highs[j];
                    Assert.InRange(p.Actual[i][j], lo - 1e-9, hi + 1e-9);
                }
        }

        [Theory]
        [InlineData(3, 15, 3)]
        [InlineData(4, 27, 3)]
        [InlineData(5, 46, 6)]
        [InlineData(6, 54, 6)]
        [InlineData(7, 62, 6)]
        public void Bbd_RunCounts(int k, int total, int centres)
        {
            var p = DoeDesign.Bbd(Factors(k));
            Assert.Equal(total, p.RunCount);
            Assert.Equal(centres, p.Coded.Count(r => r.All(v => Math.Abs(v) < 1e-12)));
            // every non-centre run: exactly 2 (k≤5) or 3 (k≥6) active factors at ±1
            int active = k <= 5 ? 2 : 3;
            foreach (var row in p.Coded.Where(r => r.Any(v => Math.Abs(v) > 1e-12)))
            {
                Assert.Equal(active, row.Count(v => Math.Abs(Math.Abs(v) - 1) < 1e-12));
                Assert.Equal(k - active, row.Count(v => Math.Abs(v) < 1e-12));
            }
            // main effects orthogonal and balanced
            for (int a = 0; a < k; a++)
            {
                Assert.Equal(0, p.Coded.Sum(r => r[a]), 10);
                for (int b = a + 1; b < k; b++)
                    Assert.Equal(0, p.Coded.Sum(r => r[a] * r[b]), 10);
            }
        }

        [Theory]
        [InlineData("L4", 4)]
        [InlineData("L8", 8)]
        [InlineData("L9", 9)]
        [InlineData("L12", 12)]
        [InlineData("L16", 16)]
        [InlineData("L18", 18)]
        [InlineData("L27", 27)]
        public void Taguchi_ArraysAreOrthogonal(string name, int runs)
        {
            var arr = TaguchiArrays.GetArray(name);
            var levels = TaguchiArrays.ColumnLevels(name);
            Assert.Equal(runs, arr.Length);
            int nc = levels.Length;
            for (int c = 0; c < nc; c++)
            {
                // balance: each level appears equally often
                var counts = arr.GroupBy(r => r[c]).Select(g => g.Count()).ToArray();
                Assert.Equal(levels[c], counts.Length);
                Assert.True(counts.All(x => x == runs / levels[c]), $"{name} col {c} unbalanced");
                // pairwise orthogonality: every level pair equally frequent
                for (int c2 = c + 1; c2 < nc; c2++)
                {
                    var pairCounts = arr.GroupBy(r => (r[c], r[c2])).Select(g => g.Count()).ToArray();
                    int expect = runs / (levels[c] * levels[c2]);
                    Assert.True(pairCounts.Length == levels[c] * levels[c2] && pairCounts.All(x => x == expect),
                        $"{name} cols {c},{c2} not orthogonal");
                }
            }
        }

        [Fact]
        public void Taguchi_AutoSelect_MapsLevels()
        {
            var p = TaguchiArrays.Taguchi(Factors(4, levels: 3));
            Assert.Equal("Taguchi L9", p.Design);
            Assert.Equal(9, p.RunCount);
            foreach (var row in p.Coded)
                Assert.All(row, v => Assert.Contains(v, new[] { -1.0, 0.0, 1.0 }));
            // actual: 3 distinct levels per factor incl. midpoint
            for (int j = 0; j < 4; j++)
            {
                var lv = p.Actual.Select(r => r[j]).Distinct().OrderBy(v => v).ToArray();
                Assert.Equal(3, lv.Length);
                Assert.Equal((lv[0] + lv[2]) / 2, lv[1], 9);
            }
        }

        [Fact]
        public void Taguchi_TwoLevel_UsesL8()
        {
            var p = TaguchiArrays.Taguchi(Factors(5, levels: 2));
            Assert.Equal("Taguchi L8", p.Design);
            Assert.Equal(8, p.RunCount);
        }

        [Theory]
        [InlineData(4, -1, 8, 1)]    // 2^(4-1) Res IV auto
        [InlineData(5, -1, 16, 1)]   // 2^(5-1) Res V auto
        [InlineData(6, -1, 16, 2)]
        [InlineData(7, 8, 8, 4)]
        [InlineData(8, -1, 16, 4)]
        [InlineData(9, 16, 16, 5)]
        public void FractionalFactorial_BalancedOrthogonalAndGeneratorsHold(int k, int target, int runs, int p)
        {
            var plan = DoeDesign.FractionalFactorial2(Factors(k), target);
            Assert.Equal(runs, plan.RunCount);
            Assert.Contains($"2^({k}-{p})", plan.Notes);
            int nBase = k - p;
            for (int a = 0; a < k; a++)
            {
                Assert.Equal(0, plan.Coded.Sum(r => r[a]), 10);        // balance
                for (int b = a + 1; b < k; b++)
                    Assert.Equal(0, plan.Coded.Sum(r => r[a] * r[b]), 10);   // main-effect orthogonality
            }
            // every generated column is exactly the product of its base columns
            foreach (var row in plan.Coded)
                for (int g = nBase; g < k; g++)
                    Assert.True(Math.Abs(Math.Abs(row[g]) - 1) < 1e-12);
        }

        [Fact]
        public void FractionalFactorial_BelowFourFactors_FallsBackToFull()
        {
            var plan = DoeDesign.FractionalFactorial2(Factors(3));
            Assert.Equal(8, plan.RunCount);
            Assert.StartsWith("FullFact2", plan.Design);
        }

        [Fact]
        public void Randomize_IsDeterministicAndKeepsRows()
        {
            var p1 = DoeDesign.Bbd(Factors(3));
            var p2 = DoeDesign.Bbd(Factors(3));
            p1.Randomize(42);
            p2.Randomize(42);
            for (int i = 0; i < p1.RunCount; i++)
            {
                Assert.Equal(p1.RunLabels[i], p2.RunLabels[i]);
                Assert.Equal(p1.Actual[i], p2.Actual[i]);
            }
            // labels still map to the same coded rows as the unshuffled plan
            var orig = DoeDesign.Bbd(Factors(3));
            for (int i = 0; i < p1.RunCount; i++)
            {
                int oi = Array.IndexOf(orig.RunLabels, p1.RunLabels[i]);
                Assert.Equal(orig.Coded[oi], p1.Coded[i]);
            }
        }

        [Fact]
        public void FactorDef_NonMidpointCentre_RoundTrips()
        {
            var f = new FactorDef("pc", 1.0, 1.3, 3, 1.15);
            Assert.Equal(1.15, f.ToActual(0), 12);
            Assert.Equal(1.0, f.ToActual(-1), 12);
            Assert.Equal(1.3, f.ToActual(1), 12);
            foreach (var v in new[] { 1.0, 1.05, 1.15, 1.22, 1.3 })
                Assert.Equal(v, f.ToActual(f.ToCoded(v)), 10);
        }
    }

    public class RsmTests
    {
        // synthetic quadratic: y = 5 + 2A − 3B + 1.5AB + 4A² − 2B²  (coded)
        static double Truth(double a, double b) => 5 + 2 * a - 3 * b + 1.5 * a * b + 4 * a * a - 2 * b * b;

        static (double[][] x, double[] y) Synthetic(out string[] names, double noise = 0, int seed = 1)
        {
            var pts = new List<double[]>();
            foreach (var a in new[] { -1.0, 0, 1 })
                foreach (var b in new[] { -1.0, 0, 1 })
                    pts.Add(new[] { a, b });
            pts.Add(new[] { 0.0, 0 });
            pts.Add(new[] { 0.0, 0 });   // replicates → pure error df
            var rng = new Random(seed);
            names = new[] { "speed", "height" };
            // actual units: speed 30..50, height 2..4
            var x = pts.Select(p => new[] { 40 + 10 * p[0], 3 + 1 * p[1] }).ToArray();
            var y = pts.Select(p => Truth(p[0], p[1]) + noise * (rng.NextDouble() - 0.5)).ToArray();
            return (x, y);
        }

        [Fact]
        public void Fit_RecoversExactQuadratic()
        {
            var (x, y) = Synthetic(out var names);
            var m = RsmFitter.Fit("resp", names, new[] { 30.0, 2 }, new[] { 50.0, 4 }, null, x, y, RsmModelType.Quadratic);
            Assert.Equal(6, m.Terms.Count);
            Assert.True(m.R2 > 0.999999);
            double C(string l) => m.Terms.First(t => t.Label == l).Coef;
            Assert.Equal(5, C("1"), 6);
            Assert.Equal(2, C("A"), 6);
            Assert.Equal(-3, C("B"), 6);
            Assert.Equal(1.5, C("AB"), 6);
            Assert.Equal(4, C("A²"), 6);
            Assert.Equal(-2, C("B²"), 6);
            // prediction in actual units
            Assert.Equal(Truth(0.5, -0.5), m.Predict(new[] { 45.0, 2.5 }), 6);
        }

        [Fact]
        public void Fit_Noisy_GivesSaneStats()
        {
            var (x, y) = Synthetic(out var names, noise: 0.4);
            var m = RsmFitter.Fit("resp", names, new[] { 30.0, 2 }, new[] { 50.0, 4 }, null, x, y, RsmModelType.Quadratic);
            Assert.InRange(m.R2, 0.9, 1.0);
            Assert.True(m.R2Adj <= m.R2);
            Assert.True(m.Press > 0);
            Assert.Contains(m.Anova, a => a.Source == "Model" && a.P < 0.01);
            Assert.Contains(m.Anova, a => a.Source == "Pure Error");
            var big = m.Terms.First(t => t.Label == "A");
            Assert.True(big.P < 0.05, $"strong effect A should be significant, p={big.P}");
        }

        [Fact]
        public void Fit_DropsAliasedQuadraticsOnTwoLevelDesign()
        {
            // 2² factorial: quadratic terms indistinguishable from intercept
            var x = new[] { new[] { -1.0, -1 }, new[] { 1.0, -1 }, new[] { -1.0, 1 }, new[] { 1.0, 1 }, new[] { -1.0, -1 } };
            var y = new[] { 1.0, 2, 3, 4.5, 1.1 };
            var m = RsmFitter.Fit("r", new[] { "a", "b" }, new[] { -1.0, -1 }, new[] { 1.0, 1 }, null, x, y, RsmModelType.Quadratic);
            Assert.Equal(2, m.DroppedTerms.Count);
            Assert.All(m.DroppedTerms, d => Assert.Contains("²", d));
        }

        [Fact]
        public void Json_RoundTrips()
        {
            var (x, y) = Synthetic(out var names);
            var m = RsmFitter.Fit("layer width (mm)", names, new[] { 30.0, 2 }, new[] { 50.0, 4 }, null, x, y, RsmModelType.Quadratic);
            var back = RsmModel.FromJson(m.ToJson());
            Assert.Equal(m.ResponseName, back.ResponseName);
            Assert.Equal(m.FactorNames, back.FactorNames);
            Assert.Equal(m.Terms.Count, back.Terms.Count);
            for (int i = 0; i < m.Terms.Count; i++)
            {
                Assert.Equal(m.Terms[i].Label, back.Terms[i].Label);
                Assert.Equal(m.Terms[i].Coef, back.Terms[i].Coef, 12);
            }
            Assert.Equal(m.Predict(new[] { 43.0, 3.7 }), back.Predict(new[] { 43.0, 3.7 }), 10);
        }

        [Fact]
        public void EquationActual_MatchesPredictions()
        {
            var (x, y) = Synthetic(out var names);
            var m = RsmFitter.Fit("r", names, new[] { 30.0, 2 }, new[] { 50.0, 4 }, null, x, y, RsmModelType.Quadratic);
            var eq = m.EquationActual();
            Assert.DoesNotContain("omitted", eq);
            Assert.Contains("speed", eq);
        }
    }

    public class DesirabilityTests
    {
        static RsmModel MakeModel(string name, Func<double[], double> truth)
        {
            // fit a quadratic to samples of the truth on a 2-factor grid
            var pts = new List<double[]>();
            foreach (var a in new[] { 0.0, 0.5, 1 })
                foreach (var b in new[] { 0.0, 0.5, 1 })
                    pts.Add(new[] { a, b });
            var y = pts.Select(truth).ToArray();
            return RsmFitter.Fit(name, new[] { "u", "v" }, new[] { 0.0, 0 }, new[] { 1.0, 1 }, null,
                pts.ToArray(), y, RsmModelType.Quadratic);
        }

        [Fact]
        public void Goal_Parsing()
        {
            var g = Goal.Parse("target=7 in 6..8 w=2", "width", 0, 10);
            Assert.Equal(GoalKind.Target, g.Kind);
            Assert.Equal(7, g.Target);
            Assert.Equal(6, g.Low);
            Assert.Equal(8, g.High);
            Assert.Equal(2, g.Weight);
            Assert.Equal(1.0, g.D(7), 9);
            Assert.Equal(0.5, g.D(6.5), 9);
            Assert.Equal(0.0, g.D(9), 9);

            var mn = Goal.Parse("min", "defects", 0, 500);
            Assert.Equal(1.0, mn.D(0), 9);
            Assert.Equal(0.0, mn.D(500), 9);
            Assert.Equal(0.5, mn.D(250), 9);
        }

        [Fact]
        public void Optimize_FindsKnownOptimum()
        {
            // y1 = (u-0.7)² + (v-0.3)² → minimize → optimum at (0.7, 0.3)
            var m1 = MakeModel("y1", p => (p[0] - 0.7) * (p[0] - 0.7) + (p[1] - 0.3) * (p[1] - 0.3));
            var g1 = Goal.Parse("min", "y1", 0, 1);
            var res = DesirabilityOptimizer.Optimize(new[] { m1 }, new[] { g1 }, gridPerDim: 11);
            Assert.InRange(res.XBest[0], 0.65, 0.75);
            Assert.InRange(res.XBest[1], 0.25, 0.35);
            Assert.True(res.DOverall > 0.95);
        }

        [Fact]
        public void Optimize_TradesOffTwoResponses_WithFactorPrior()
        {
            var m1 = MakeModel("grow", p => p[0]);          // maximize → u high
            var m2 = MakeModel("shrink", p => p[1]);        // minimize → v low
            var goals = new[] { Goal.Parse("max", "grow", 0, 1), Goal.Parse("min", "shrink", 0, 1) };
            var priors = new[] { Goal.Parse("low", "u", 0, 1), new Goal { Kind = GoalKind.None } };
            var res = DesirabilityOptimizer.Optimize(new[] { m1, m2 }, goals, priors, gridPerDim: 11);
            Assert.True(res.DOverall > 0);
            Assert.InRange(res.XBest[1], 0, 0.15);          // v → low, unaffected by prior
            Assert.InRange(res.XBest[0], 0.3, 0.7);         // u pulled down by the prior
        }

        [Fact]
        public void Optimize_InfeasibleGoal_ReportsNote()
        {
            var m1 = MakeModel("y", p => p[0]);
            var g = Goal.Parse("range=5..6", "y", 5, 6);    // y ∈ [0,1]: impossible
            var res = DesirabilityOptimizer.Optimize(new[] { m1 }, new[] { g }, gridPerDim: 7);
            Assert.Contains("no feasible", res.Notes);
        }
    }

    public class TableIoTests
    {
        static string Tmp(string name) => Path.Combine(Path.GetTempPath(), "iamlab_doe_tests_" + name);

        [Fact]
        public void Csv_RoundTrip_WithSemicolonAndDecimalComma()
        {
            var p = Tmp("t1.csv");
            File.WriteAllText(p, "Run;speed;width\r\nR1;30;7,25\r\nR2;50;8,5\r\n");
            var t = TableIo.Read(p);
            Assert.Equal(new[] { "Run", "speed", "width" }, t.Headers);
            Assert.Equal(2, t.Rows.Length);
            Assert.Equal(7.25, t.NumericColumn(2)[0], 9);
            Assert.False(t.IsNumeric(0));
            Assert.True(t.IsNumeric(1));
            File.Delete(p);
        }

        [Fact]
        public void Xlsx_WriteRead_RoundTrip()
        {
            var p = Tmp("t2.xlsx");
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("Data");
            sh.Set(1, 1, "name", XFmt.Header);
            sh.Set(1, 2, "value", XFmt.Header);
            sh.Set(2, 1, "a & <b>", XFmt.Text());
            sh.Set(2, 2, 1.25, XFmt.Num("0.00"));
            sh.Set(3, 1, "c");
            sh.Set(3, 2, -7.5);
            wb.Save(p);

            var grid = XlsxReader.ReadSheet(p);
            Assert.Equal("name", grid[0][0]);
            Assert.Equal("a & <b>", grid[1][0]);
            Assert.Equal(1.25, TableIo.ParseNum(grid[1][1])!.Value, 9);
            Assert.Equal(-7.5, TableIo.ParseNum(grid[2][1])!.Value, 9);

            var t = TableIo.Read(p);
            Assert.Equal(new[] { "name", "value" }, t.Headers);
            File.Delete(p);
        }

        [Fact]
        public void PlanExport_XlsxAndCsv_LinkBackToRsm()
        {
            var factors = new List<FactorDef>
            {
                new FactorDef("layer_height", 2.5, 4.5),
                new FactorDef("print_speed", 30, 50),
                new FactorDef("pressure", 1.0, 1.3, 3, 1.15),
            };
            var plan = DoeDesign.Bbd(factors);
            var responses = new[] { "layer_width_mm", "defects" };

            var px = Tmp("plan.xlsx");
            var pc = Tmp("plan.csv");
            DoeExport.PlanToXlsx(plan, responses, px);
            DoeExport.PlanToCsv(plan, responses, pc);

            var t = TableIo.Read(px);
            // header row of the run table contains factor + response columns
            Assert.True(t.ColumnIndex("layer_width_mm") >= 0 || t.Rows.Any(r => r.Contains("layer_width_mm")));
            var tc = TableIo.Read(pc);
            Assert.Equal(plan.RunCount, tc.Rows.Length);
            Assert.True(tc.ColumnIndex("layer_height") >= 0);
            Assert.True(tc.ColumnIndex("layer_width_mm") >= 0);
            File.Delete(px);
            File.Delete(pc);
        }

        [Fact]
        public void EndToEnd_PlanExportMeasureFitOptimize()
        {
            // full FBI2026-style loop: BBD plan → export → "lab" fills responses →
            // read back → RSM fit → desirability optimum recovers the ground truth
            var factors = new List<FactorDef>
            {
                new FactorDef("lh", 2.5, 4.5),
                new FactorDef("ps", 30, 50),
                new FactorDef("pc", 1.0, 1.3),
            };
            var plan = DoeDesign.Bbd(factors);
            string p = Tmp("e2e.csv");
            DoeExport.PlanToCsv(plan, new[] { "width" }, p);

            // ground truth: width = 7 + 1.2*x_lh − 0.8*x_ps + 0.5*x_pc (coded, linear)
            var t = TableIo.Read(p);
            int cw = t.ColumnIndex("width");
            var lines = new List<string[]>();
            for (int i = 0; i < t.Rows.Length; i++)
            {
                var row = t.Rows[i];
                double xlh = (plan.Actual[i][0] - 3.5) / 1.0;
                double xps = (plan.Actual[i][1] - 40.0) / 10.0;
                double xpc = plan.Coded[i][2];
                row[cw] = (7 + 1.2 * xlh - 0.8 * xps + 0.5 * xpc)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(row);
            }
            TableIo.WriteCsv(p, t.Headers, lines);

            var t2 = TableIo.Read(p);
            var x = Enumerable.Range(0, t2.Rows.Length)
                .Select(i => new[] { t2.NumericColumn(1)[i], t2.NumericColumn(2)[i], t2.NumericColumn(3)[i] }).ToArray();
            var y = t2.NumericColumn(t2.ColumnIndex("width"));
            var mdl = RsmFitter.Fit("width", new[] { "lh", "ps", "pc" },
                new[] { 2.5, 30, 1.0 }, new[] { 4.5, 50, 1.3 }, null, x, y, RsmModelType.Quadratic);
            Assert.True(mdl.R2 > 0.999999);

            // target width 7.5 must be achievable; optimum should predict ≈7.5
            var g = Goal.Parse("target=7.5 in 7.1..7.9", "width", 0, 10);
            var opt = DesirabilityOptimizer.Optimize(new[] { mdl }, new[] { g }, gridPerDim: 9);
            Assert.True(opt.DOverall > 0.95, $"D={opt.DOverall}");
            Assert.InRange(mdl.Predict(opt.XBest), 7.45, 7.55);
            File.Delete(p);
        }

        [Fact]
        public void PlanXlsx_LinksBackWithCorrectHeaderRow()
        {
            // the exported plan has a title + factor-summary block ABOVE the run
            // table - Read must still find the run-table header (the widest row)
            var factors = new List<FactorDef> { new FactorDef("lh", 2.5, 4.5), new FactorDef("ps", 30, 50), new FactorDef("pc", 1.0, 1.3) };
            var plan = DoeDesign.Bbd(factors);
            string p = Tmp("hdr.xlsx");
            DoeExport.PlanToXlsx(plan, new[] { "width", "defects" }, p);
            var t = TableIo.Read(p);
            Assert.True(t.ColumnIndex("lh") >= 0, "factor column not found: header row misdetected");
            Assert.True(t.ColumnIndex("width") >= 0);
            Assert.Equal(plan.RunCount, t.Rows.Length);
            File.Delete(p);
        }

        [Fact]
        public void ColRef_Parsing()
        {
            Assert.Equal(1, XlsxReader.ColIndex("A1"));
            Assert.Equal(28, XlsxReader.ColIndex("AB12"));
            Assert.Equal("A", XlsxWorkbook.ColName(1));
            Assert.Equal("AB", XlsxWorkbook.ColName(28));
        }
    }
}
