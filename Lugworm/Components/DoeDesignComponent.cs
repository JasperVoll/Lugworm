using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Core.Doe;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Generates a DoE run plan (DSD / BBD / Taguchi / 2-level full factorial)
    /// for print-parameter calibration - the first stage of the FBI2026 workflow.
    /// Universal: any number of factors within each design's supported range; the run
    /// matrix adapts automatically. Exports a styled .xlsx (or .csv) with empty
    /// response columns that can be filled in the lab and linked straight into the
    /// RSM evaluation component.</summary>
    public class DoeDesignComponent : IamComponentBase
    {
        public DoeDesignComponent()
            : base("DoE Design", "DoE",
                   "Supply factor names and matching low and high levels to generate an experiment plan.",
                   "Lugworm", "6 Calibrate")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0007-4a7b-9c3d-aa0000000007");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.DoeDesign();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("names", "N", "Factor names, one per item, for example layer_height, print_speed, pressure", "core", PType.Text, GH_ParamAccess.list)
                .HintText("layer_height\nprint_speed\npressure"),
            new ParamSpec("lows", "Lo", "Factor low levels in actual units, in the same order and count as names", "core", PType.Number, GH_ParamAccess.list)
                .HintText("2.5\n30\n1.0"),
            new ParamSpec("highs", "Hi", "Factor high levels in actual units, above lows and in the same order and count as names", "core", PType.Number, GH_ParamAccess.list)
                .HintText("4.5\n50\n1.3"),
            new ParamSpec("design", "D", "Design type: DSD (screening, 4 to 12 factors), BBD (RSM, 3 to 7), Taguchi (2 to 13), FullFact2 (1 to 10), FracFact2 (fractional two level, 4 to 9)", "core", PType.Text)
                .HintText("BBD"),
            new ParamSpec("responses", "R", "Response names. They become empty columns in the exported plan for lab measurements", "core", PType.Text, GH_ParamAccess.list)
                .HintText("layer_width\nsurface_defects\nprint_time"),
            // advanced
            new ParamSpec("centres", "Ce", "Optional center level per factor away from the midpoint, for example pressure 1.15 in [1.0, 1.3]", "advanced", PType.Number, GH_ParamAccess.list),
            new ParamSpec("centerPts", "Cp", "Extra or total center points (-1 = design default, BBD 3 to 6, DSD 0)", "advanced", PType.Integer).Hint(-1, 10, -1, 0),
            new ParamSpec("levels", "Lv", "Taguchi factor levels, 2 or 3. DSD and BBD always use 3", "advanced", PType.Integer).Hint(2, 3, 3, 0),
            new ParamSpec("seed", "Se", "Seed for the run order. 0 or higher randomizes with that seed, -1 keeps standard order", "advanced", PType.Integer).Hint(-1, 9999, 0, 0),
            new ParamSpec("fracRuns", "Fr", "FracFact2: target runs 8 or 16 (-1 = auto: smallest with resolution ≥ IV)", "advanced", PType.Integer).Hint(-1, 16, -1, 0),
            // power
            new ParamSpec("powerSigma", "Ps", "Expected measurement scatter in response units, greater than 0 and required with powerEffect for the power estimate", "power", PType.Number).Hint(0, 100, 0, 3),
            new ParamSpec("powerEffect", "Pe", "Smallest response change to detect in response units, one value for all factors or one per factor", "power", PType.Number, GH_ParamAccess.list),
            new ParamSpec("powerModel", "Pm", "Model that will be fitted to the data: Linear, 2FI or Quadratic. Power depends on its residual degrees of freedom", "power", PType.Text).HintText("Linear"),
            new ParamSpec("powerSims", "Pk", "Number of simulated experiments for the power estimate (more gives a steadier estimate)", "power", PType.Integer).Hint(50, 2000, 400, 0),
            // export
            new ParamSpec("xlsxPath", "Xl", "Local .xlsx file or folder to export on solve with Export enabled, overwriting the target", "export", PType.Text),
            new ParamSpec("csvPath", "Cs", "Local .csv file or folder to export on solve with Export enabled, overwriting the target", "export", PType.Text),
            new ParamSpec("replicates", "Rp", "Copies of every design row, including center points", "advanced", PType.Integer).Hint(1, 20, 1, 0),
            new ParamSpec("augment", "Au", "none keeps the plan, ccd-face appends face centered star and center runs to a FullFact2 or FracFact2 plan", "advanced", PType.Text).HintText("none"),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("advanced", "Advanced options (centres, center points, levels, seed)"),
            ("power", "Power analysis (can this plan detect the effect?)"),
            ("export", "Excel / CSV export inputs"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("design", "Design type dropdown", new[]
            {
                ("BBD (RSM, 3 to 7 factors)", "\"BBD\""),
                ("DSD (screening, 4 to 12 factors)", "\"DSD\""),
                ("Taguchi (OA, 2 to 13 factors)", "\"Taguchi\""),
                ("FullFact2 (two level, 1 to 10 factors)", "\"FullFact2\""),
                ("FracFact2 (fractional two level, 4 to 9 factors)", "\"FracFact2\""),
            }),
            ("augment", "Plan augmentation dropdown", new[]
            {
                ("none (keep the plan)", "\"none\""),
                ("ccd-face (append star and center runs)", "\"ccd-face\""),
            }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("runs", "R", "Run plan in actual units, one branch per run, factor values in input order", GH_ParamAccess.tree);
            pManager.AddNumberParameter("coded", "X", "Run plan in coded units, -1 to +1", GH_ParamAccess.tree);
            pManager.AddTextParameter("factorNames", "N", "Factor names. Wire into the RSM component", GH_ParamAccess.list);
            pManager.AddTextParameter("runLabels", "L", "Run labels (BBD01, …)", GH_ParamAccess.list);
            pManager.AddTextParameter("table", "T", "Readable run table. Connect a panel", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Design summary and warnings", GH_ParamAccess.item);
            pManager.AddTextParameter("power", "Pw", "Monte Carlo power per factor at α = 0.05. Enable the power group and connect a panel", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public GH_Structure<GH_Number> Runs = new();
            public GH_Structure<GH_Number> Coded = new();
            public List<string> Names = new();
            public List<string> Labels = new();
            public string Table = "", Info = "", Power = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var names = GetStringList(DA, "names").Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            var lows = GetNumberList(DA, "lows");
            var highs = GetNumberList(DA, "highs");
            var centres = Active.Contains("advanced") ? GetNumberList(DA, "centres") : new List<double>();
            string design = GetString(DA, "design", "BBD").Trim();
            int centerPts = GetInt(DA, "centerPts", -1);
            int levels = Math.Max(2, Math.Min(3, GetInt(DA, "levels", 3)));
            int seed = GetInt(DA, "seed", 0);
            int replicates = GetInt(DA, "replicates", 1);
            int fracRuns = GetInt(DA, "fracRuns", -1);
            string augment = GetString(DA, "augment", "none").Trim();

            if (names.Count == 0 || lows.Count != names.Count || highs.Count != names.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Inputs names ({names.Count}), lows ({lows.Count}) and highs ({highs.Count}) must have the same length");
                return null;
            }
            for (int j = 0; j < names.Count; j++)
                if (!(highs[j] > lows[j]))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Factor '{names[j]}': high must be above low");
                    return null;
                }

            var factors = names.Select((n, j) => new FactorDef(n, lows[j], highs[j],
                design.Equals("Taguchi", StringComparison.OrdinalIgnoreCase) ? levels : 3,
                j < centres.Count ? centres[j] : (double?)null)).ToList();

            DoePlan plan;
            try
            {
                if (replicates < 1) throw new ArgumentException("replicates must be at least 1");
                plan = design.ToUpperInvariant() switch
                {
                    "DSD" => DoeDesign.Dsd(factors, Math.Max(0, centerPts < 0 ? 0 : centerPts)),
                    "BBD" => DoeDesign.Bbd(factors, centerPts),
                    "TAGUCHI" => TaguchiArrays.Taguchi(factors),
                    "FULLFACT2" => DoeDesign.FullFactorial2(factors, Math.Max(0, centerPts < 0 ? 0 : centerPts)),
                    "FRACFACT2" => DoeDesign.FractionalFactorial2(factors, fracRuns, Math.Max(0, centerPts < 0 ? 0 : centerPts)),
                    _ => throw new ArgumentException($"unknown design '{design}' (use DSD, BBD, Taguchi, FullFact2 or FracFact2)"),
                };
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return null;
            }
            plan.Replicate(replicates);
            plan.Randomize(seed);
            if (!augment.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                if (!augment.Equals("ccd-face", StringComparison.OrdinalIgnoreCase))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "augment must be none or ccd-face");
                    return null;
                }
                // an explicit choice, applied after the original block is final: the rows a
                // lab has already printed keep their order and labels, the star block gets
                // its own shuffle, and centerPts (3 when unset) sets its centre runs
                if (DoeDesign.CanAugment(plan)) DoeDesign.AugmentFaceCentred(plan, factors, centerPts < 0 ? 3 : centerPts, seed);
                else AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "augment ignored for this design");
            }

            for (int i = 0; i < plan.RunCount; i++)
            {
                var path = new GH_Path(i);
                foreach (var v in plan.Actual[i]) res.Runs.Append(new GH_Number(v), path);
                foreach (var v in plan.Coded[i]) res.Coded.Append(new GH_Number(v), path);
            }
            res.Names = plan.FactorNames.ToList();
            res.Labels = plan.RunLabels.ToList();
            res.Table = DoeDesign.TextTable(plan);
            res.Info = plan.Notes;

            PowerResult? powerResult = null;
            if (Active.Contains("power"))
            {
                double sigma = GetNumber(DA, "powerSigma", 0.0);
                var effects = GetNumberList(DA, "powerEffect");
                if (sigma > 0 && effects.Count > 0)
                {
                    // one effect broadcasts to every factor; otherwise counts must match
                    double[] eff = effects.Count == 1
                        ? Enumerable.Repeat(effects[0], names.Count).ToArray()
                        : effects.ToArray();
                    string pm = GetString(DA, "powerModel", "Linear").Trim();
                    var ptype = pm.StartsWith("2", StringComparison.OrdinalIgnoreCase) || pm.ToLowerInvariant().Contains("inter")
                        ? RsmModelType.Interactions
                        : pm.StartsWith("quad", StringComparison.OrdinalIgnoreCase) ? RsmModelType.Quadratic : RsmModelType.Linear;
                    try
                    {
                        var pw = PowerAnalysis.MonteCarloPower(plan.Actual, plan.FactorNames,
                            lows.ToArray(), highs.ToArray(),
                            centres.Count == names.Count ? centres.ToArray() : null,
                            ptype, sigma, eff, nSims: GetInt(DA, "powerSims", 400));
                        powerResult = pw;
                        res.Power = PowerAnalysis.Report(pw);
                        var weak = Enumerable.Range(0, names.Count)
                            .Where(j => !double.IsNaN(pw.Power[j]) && pw.Power[j] < 0.8).ToList();
                        if (weak.Count > 0)
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                                $"Power below 0.8 for {string.Join(", ", weak.Select(j => names[j]))}. " +
                                "This plan may miss the stated effect. Add runs or widen the ranges before printing");
                    }
                    catch (Exception ex)
                    { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Power analysis failed: " + ex.Message); }
                }
                else if (sigma > 0 || effects.Count > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "Power analysis needs powerSigma above 0 and at least one powerEffect");
            }

            var responses = GetStringList(DA, "responses").Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
            if (Active.Contains("export"))
            {
                // Background exports (AUD-PERF-001): the plan and the response names are
                // copied into an owned snapshot here on the solver thread - a later edit
                // cannot change what gets written - and path probing, directory creation
                // and the write itself all run on the worker.
                string xlsx = GetString(DA, "xlsxPath", "").Trim();
                string csv = GetString(DA, "csvPath", "").Trim();
                // the power report (when computed) rides along on its own sheet; the plan sheet is unchanged
                if (xlsx.Length > 0) res.Info += Export(_xlsxWriter, DoeExportSnapshot.CapturePlan(plan, responses, xlsx, power: powerResult));
                if (csv.Length > 0) res.Info += Export(_csvWriter, DoeExportSnapshot.CapturePlan(plan, responses, csv, csv: true));
            }

            Message = $"{plan.Design} - {plan.RunCount} runs";
            return res;
        }

        // ---- background exports (AUD-PERF-001) ----------------------------------------

        readonly BackgroundFileWork<string> _xlsxWriter = new("xlsx export");
        readonly BackgroundFileWork<string> _csvWriter = new("csv export");

        /// <summary>One export through the worker. A completed key reports its path; a
        /// failed key reports once and is forgotten so the next solve retries; anything
        /// else starts (or keeps) the request and says so while the last outputs stand.</summary>
        string Export(BackgroundFileWork<string> writer, DoeExportSnapshot job)
        {
            if (writer.ResultFor(job.Key) is string saved) return "  saved:" + saved;
            if (writer.ErrorFor(job.Key) is string error)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, error);
                writer.ForgetError();
                return "";
            }
            writer.Start(this, job.Key, job.Export);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Exporting the plan in the background …");
            return "  exporting...";
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _xlsxWriter.CancelAndDetach();
            _csvWriter.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) { _xlsxWriter.CancelAndDetach(); _csvWriter.CancelAndDetach(); }
            base.DocumentContextChanged(document, context);
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataTree(0, r.Runs);
            DA.SetDataTree(1, r.Coded);
            DA.SetDataList(2, r.Names);
            DA.SetDataList(3, r.Labels);
            DA.SetData(4, r.Table);
            DA.SetData(5, r.Info);
            DA.SetData(6, r.Power);
        }
    }
}
