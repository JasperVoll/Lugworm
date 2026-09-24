using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Doe
{
    /// <summary>Owned, read-only export inputs captured before leaving the solver.
    /// Capture performs no filesystem operations. Export (including path probing and
    /// directory creation) belongs on a background worker. Only fields consumed by
    /// the corresponding workbook writer are copied; these are not prediction models.</summary>
    public sealed class DoeExportSnapshot
    {
        readonly string _path, _defaultName;
        readonly Action<string> _write;
        public string Key { get; }

        DoeExportSnapshot(string path, string defaultName, Action<string> write, Action<BinaryWriter> fingerprint)
        {
            _path = path; _defaultName = defaultName; _write = write;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                { writer.Write(path); writer.Write(defaultName); fingerprint(writer); }
                using (var sha = SHA256.Create())
                    Key = BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }

        public string Export(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = _path;
            try
            {
                destination = SavePath.Resolve(_path, _defaultName);
                cancellationToken.ThrowIfCancellationRequested();
                _write(destination);
                return destination;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new IOException(SavePath.Explain(ex, destination), ex); }
        }

        public static DoeExportSnapshot CapturePlan(DoePlan plan, IReadOnlyList<string> responses,
            string path, bool csv = false, PowerResult? power = null)
        {
            var p = new DoePlan
            {
                Design = plan.Design,
                Notes = plan.Notes,
                FactorNames = plan.FactorNames.ToArray(),
                RunLabels = plan.RunLabels.ToArray(),
                Lows = plan.Lows.ToArray(),
                Highs = plan.Highs.ToArray(),
                Coded = plan.Coded.Select(row => row.ToArray()).ToArray(),
                Actual = plan.Actual.Select(row => row.ToArray()).ToArray()
            };
            var names = responses.ToArray();
            // the optional power report is captured too (the xlsx writer ignores it for csv)
            var pw = power == null ? null : new PowerResult
            {
                FactorNames = power.FactorNames.ToArray(),
                Power = power.Power.ToArray(),
                EffectSizes = power.EffectSizes.ToArray(),
                Sigma = power.Sigma,
                Alpha = power.Alpha,
                NSims = power.NSims,
                Seed = power.Seed,
                ModelType = power.ModelType,
                McError = power.McError,
                Notes = power.Notes,
            };
            return new DoeExportSnapshot(path, csv ? "doe_plan.csv" : "doe_plan.xlsx",
                destination => { if (csv) DoeExport.PlanToCsv(p, names, destination); else DoeExport.PlanToXlsx(p, names, destination, pw); },
                writer =>
                {
                    writer.Write(p.Design); writer.Write(p.Notes); Strings(writer, p.FactorNames); Strings(writer, p.RunLabels);
                    Numbers(writer, p.Lows); Numbers(writer, p.Highs);
                    writer.Write(p.Coded.Length); foreach (var row in p.Coded) Numbers(writer, row);
                    writer.Write(p.Actual.Length); foreach (var row in p.Actual) Numbers(writer, row);
                    Strings(writer, names);
                    writer.Write(pw != null);
                    if (pw != null)
                    {
                        Strings(writer, pw.FactorNames); Numbers(writer, pw.Power); Numbers(writer, pw.EffectSizes);
                        writer.Write(pw.Sigma); writer.Write(pw.Alpha); writer.Write(pw.NSims); writer.Write(pw.Seed);
                        writer.Write((int)pw.ModelType); writer.Write(pw.McError); writer.Write(pw.Notes);
                    }
                });
        }

        public static DoeExportSnapshot CaptureDesirability(DesirabilityResult result, IReadOnlyList<RsmModel> models,
            IReadOnlyList<Goal> goals, IReadOnlyList<Goal>? factorGoals, string path,
            IReadOnlyList<(string label, DesirabilityResult r)>? sweep = null)
        {
            var recipe = Recipe(result);
            var modelNames = models.Select(m => new RsmModel { ResponseName = m.ResponseName, FactorNames = m.FactorNames.ToArray() }).ToArray();
            var goalCopies = goals.Select(CopyGoal).ToArray();
            var priorCopies = factorGoals?.Select(CopyGoal).ToArray();
            var cases = sweep?.Select(s => (s.label, r: Recipe(s.r))).ToArray();
            return new DoeExportSnapshot(path, "desirability_report.xlsx",
                destination => DoeExport.DesirabilityToXlsx(recipe, modelNames, goalCopies, priorCopies, destination, cases),
                writer =>
                {
                    WriteRecipe(writer, recipe); writer.Write(modelNames.Length);
                    foreach (var model in modelNames) { writer.Write(model.ResponseName); Strings(writer, model.FactorNames); }
                    WriteGoals(writer, goalCopies); WriteGoals(writer, priorCopies);
                    writer.Write(cases?.Length ?? 0);
                    if (cases != null) foreach (var item in cases) { writer.Write(item.label); WriteRecipe(writer, item.r); }
                });
        }

        static DesirabilityResult Recipe(DesirabilityResult r) => new DesirabilityResult
        {
            XBest = r.XBest.ToArray(),
            YPred = r.YPred.ToArray(),
            DResponses = r.DResponses.ToArray(),
            DFactors = r.DFactors.ToArray(),
            DOverall = r.DOverall,
            Notes = r.Notes
        };
        static Goal CopyGoal(Goal g) => new Goal
        {
            Name = g.Name,
            Kind = g.Kind,
            Low = g.Low,
            High = g.High,
            Target = g.Target,
            Weight = g.Weight,
            S = g.S
        };
        static void Strings(BinaryWriter w, string[] values) { w.Write(values.Length); foreach (string s in values) w.Write(s); }
        static void Numbers(BinaryWriter w, double[] values) { w.Write(values.Length); foreach (double d in values) w.Write(d); }
        static void WriteRecipe(BinaryWriter w, DesirabilityResult r)
        {
            Numbers(w, r.XBest); Numbers(w, r.YPred); Numbers(w, r.DResponses); Numbers(w, r.DFactors);
            w.Write(r.DOverall); w.Write(r.Notes);
        }
        static void WriteGoals(BinaryWriter w, Goal[]? goals)
        {
            w.Write(goals?.Length ?? 0);
            if (goals == null) return;
            foreach (var g in goals)
            {
                w.Write(g.Name); w.Write((int)g.Kind); w.Write(g.Low); w.Write(g.High);
                w.Write(g.Target); w.Write(g.Weight); w.Write(g.S);
            }
        }
    }
}
