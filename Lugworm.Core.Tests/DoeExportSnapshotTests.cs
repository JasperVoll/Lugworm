using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests
{
    public class DoeExportSnapshotTests
    {
        static DoePlan Plan() => DoeDesign.FullFactorial2(new[] { new FactorDef("speed", 10.25, 20.5) });

        [Fact]
        public void PlanCapture_OwnsRowsAndNames_AndRetainsCsvContent()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var plan = Plan(); var responses = new[] { "width" };
                var job = DoeExportSnapshot.CapturePlan(plan, responses, path, csv: true);
                plan.Actual[0][0] = 999; plan.Coded[0][0] = 88;
                plan.FactorNames[0] = "changed"; plan.RunLabels[0] = "changed"; responses[0] = "changed";
                job.Export(CancellationToken.None);
                string text = File.ReadAllText(path);
                Assert.Contains("speed", text); Assert.Contains("width", text); Assert.Contains("10.25", text);
                Assert.DoesNotContain("999", text); Assert.DoesNotContain("changed", text);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReportCapture_OwnsRecipeModelNamesGoalsAndSweep()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
            try
            {
                var recipe = new DesirabilityResult
                {
                    XBest = new[] { 12.5 },
                    DFactors = new[] { double.NaN },
                    DResponses = new[] { .75 },
                    YPred = new[] { 7.25 },
                    DOverall = .75
                };
                var model = new RsmModel { FactorNames = new[] { "speed" }, ResponseName = "width" };
                var goal = Goal.Parse("target=7 in 6..8", "width", 0, 1);
                var sweep = new[] { ("case one", recipe) };
                var job = DoeExportSnapshot.CaptureDesirability(recipe, new[] { model }, new[] { goal }, null, path, sweep);
                recipe.XBest[0] = 999; recipe.YPred[0] = 888; recipe.DOverall = 0;
                model.FactorNames[0] = "changed"; model.ResponseName = "changed"; goal.Target = 99;
                job.Export(CancellationToken.None);
                var sheet = XlsxReader.ReadSheet(path, "Desirability").SelectMany(x => x).ToArray();
                Assert.Contains("speed", sheet); Assert.Contains("width", sheet); Assert.Contains("12.5", sheet);
                Assert.Contains(sheet, x => x.Contains("target 7 "));
                Assert.DoesNotContain("999", sheet); Assert.DoesNotContain("changed", sheet);
                Assert.Contains("12.5", XlsxReader.ReadSheet(path, "Sweep").SelectMany(x => x));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Key_IsCultureInvariant_AndDetectsUnroundedChangesAndResponses()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                var plan = Plan();
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                var first = DoeExportSnapshot.CapturePlan(plan, new[] { "width" }, "plan.xlsx");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(first.Key, DoeExportSnapshot.CapturePlan(plan, new[] { "width" }, "plan.xlsx").Key);
                plan.Actual[0][0] += 1e-10;
                Assert.NotEqual(first.Key, DoeExportSnapshot.CapturePlan(plan, new[] { "width" }, "plan.xlsx").Key);
                Assert.NotEqual(first.Key, DoeExportSnapshot.CapturePlan(Plan(), new[] { "other" }, "plan.xlsx").Key);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void CanceledJob_DoesNotTouchDestination_AndFailedJobCanRetry()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
            try
            {
                var job = DoeExportSnapshot.CapturePlan(Plan(), Array.Empty<string>(), path, csv: true);
                Assert.Throws<OperationCanceledException>(() => job.Export(new CancellationToken(true)));
                Assert.False(File.Exists(path));
                using (var locked = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    Assert.Throws<IOException>(() => job.Export(CancellationToken.None));
                Assert.Equal(path, job.Export(CancellationToken.None));
                Assert.Contains("speed", File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }
    }
}
