using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Core.Doe;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>RSM evaluation of measured DoE samples (stage 2 of the FBI2026
    /// workflow): reads the filled plan table (.xlsx/.csv, as exported by the DoE
    /// component) or direct data trees, fits one polynomial model per response (OLS,
    /// per-term t/p, ANOVA with lack-of-fit, PRESS/R²pred), and shows effect bars +
    /// response-surface contours directly on the canvas. Works for any number of
    /// factors and responses.</summary>
    public class RsmEvalComponent : IamComponentBase, IGraphHost
    {
        public RsmEvalComponent()
            : base("RSM Evaluate", "RSM",
                   "Press Compute with factor names and measured table or direct data to fit response-surface models.",
                   "Lugworm", "6 Calibrate")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0008-4a8b-9c3d-aa0000000008");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Rsm();
        protected override bool Heavy => true;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("tablePath", "P", "Measured .xlsx or .csv table path with named factor and response columns, used unless direct X data is supplied", "core", PType.Text),
            new ParamSpec("factorNames", "N", "Factor column names in model order, from DoE Design's factorNames output", "core", PType.Text, GH_ParamAccess.list)
                .HintText("layer_height\nprint_speed\npressure"),
            new ParamSpec("responseNames", "R", "Response column names to fit (empty = all other numeric columns)", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("model", "M", "Fit type: Linear, 2FI (with interactions) or Quadratic, dropping terms the data cannot distinguish", "core", PType.Text)
                .HintText("Quadratic"),
            // coding ranges (optional - default: min/max of the data)
            new ParamSpec("lows", "Lo", "Factor low levels in actual units and factorNames order (unwired = data minima)", "ranges", PType.Number, GH_ParamAccess.list),
            new ParamSpec("highs", "Hi", "Factor high levels in actual units and factorNames order (unwired = data maxima)", "ranges", PType.Number, GH_ParamAccess.list),
            new ParamSpec("centres", "Ce", "Center per factor away from the midpoint (default: midpoint)", "ranges", PType.Number, GH_ParamAccess.list),
            // direct data instead of a file
            new ParamSpec("X", "X", "Factor values in actual units, one branch per run in factorNames order, overriding tablePath", "direct", PType.Number, GH_ParamAccess.tree),
            new ParamSpec("Y", "Y", "Measured responses in response units, one branch per run matching X and responseNames order", "direct", PType.Number, GH_ParamAccess.tree),
            // export
            new ParamSpec("xlsxReport", "Xl", "Local .xlsx file or folder to export on Compute with Export enabled, overwriting the target", "export", PType.Text),
            new ParamSpec("transform", "Tr", "Scale each response is fitted on. Use log1p or sqrt for counts, boxcox picks the exponent", "core", PType.Text, GH_ParamAccess.list).HintText("none"),
            new ParamSpec("computed", "Cm", "Use yes for a calculated response with no measurement error, otherwise no", "core", PType.Text, GH_ParamAccess.list).HintText("no"),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("ranges", "Coding-range overrides (lows/highs/centres)"),
            ("direct", "Direct data inputs (X/Y trees instead of a file)"),
            ("export", "Excel report export input"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("model", "Model type dropdown", new[]
            {
                ("Quadratic (full RSM)", "\"Quadratic\""),
                ("2FI (mains + interactions)", "\"2FI\""),
                ("Linear (main effects)", "\"Linear\""),
            }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("models", "M", "Fitted RSM models. Wire into the Desirability component", GH_ParamAccess.list);
            pManager.AddTextParameter("report", "T", "Readable analysis report: coefficients, ANOVA and fit statistics. Connect a panel", GH_ParamAccess.item);
            pManager.AddTextParameter("equations", "E", "Model equations in coded and actual units, one per response", GH_ParamAccess.list);
            pManager.AddNumberParameter("predicted", "Yp", "Model predictions for the input runs, one branch per response", GH_ParamAccess.tree);
            pManager.AddTextParameter("json", "J", "Model coefficients as JSON for archiving or external tools", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
            pManager.AddTextParameter("diag", "Dg", "Regression diagnostics per response: leverage, studentized residuals, Cook's distance and VIF. Connect a panel", GH_ParamAccess.list);
        }

        sealed class Result
        {
            public List<RsmModel> Models = new();
            public string Report = "", Info = "";
            public List<string> Equations = new(), Json = new(), Diag = new();
            public GH_Structure<GH_Number> Pred = new();
            public Bitmap? Graph;
        }

        Result? _last;
        public Bitmap? GraphImage => _last?.Graph;

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var factorNames = GetStringList(DA, "factorNames").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var responseNames = GetStringList(DA, "responseNames").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            string modelText = GetString(DA, "model", "Quadratic").Trim();
            var type = modelText.StartsWith("2", StringComparison.OrdinalIgnoreCase) || modelText.ToLowerInvariant().Contains("inter")
                ? RsmModelType.Interactions
                : modelText.StartsWith("lin", StringComparison.OrdinalIgnoreCase) ? RsmModelType.Linear : RsmModelType.Quadratic;

            if (factorNames.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The factorNames input is required"); return null; }
            int m = factorNames.Count;

            // ── collect data: file table or direct trees ──
            double[][] x;
            double[]? feasible = null;
            var yCols = new List<(string name, double[] y)>();
            if (Active.Contains("direct") && GetCurveTreeSafeHasData())
            {
                var (xt, yt) = GetDirectData(DA);
                if (xt == null || yt == null) return null;
                x = xt;
                if (responseNames.Count == 0)
                    responseNames = Enumerable.Range(1, yt[0].Length).Select(i => "response_" + i).ToList();
                for (int k = 0; k < responseNames.Count; k++)
                    yCols.Add((responseNames[k], yt.Select(row => k < row.Length ? row[k] : double.NaN).ToArray()));
            }
            else
            {
                string path = GetString(DA, "tablePath", "").Trim();
                if (path.Length == 0)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Set tablePath, or enable Direct inputs and supply both X and Y"); return null; }
                // background read (AUD-PERF-001), keyed by path + file identity: a slow
                // share or big workbook no longer stalls the solver thread
                DataTableLite? t;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) throw new FileNotFoundException("file not found", path);
                    string readKey = FormattableString.Invariant(
                        $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                    t = _tableReader.ResultFor(readKey);
                    if (t == null)
                    {
                        if (_tableReader.ErrorFor(readKey) is string readError)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, readError);
                            _tableReader.ForgetError();
                            return null;
                        }
                        string tp = path;
                        _tableReader.Start(this, readKey, ct => TableIo.Read(tp));
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Loading " + path + " …");
                        return null;
                    }
                }
                catch (Exception ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Table read failed: " + ex.Message); return null; }

                var fIdx = new int[m];
                try { feasible = t.FeasibilityColumn(); }
                catch (ArgumentException ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return null; }
                for (int j = 0; j < m; j++)
                {
                    fIdx[j] = t.ColumnIndex(factorNames[j]);
                    if (fIdx[j] < 0)
                    { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Factor column '{factorNames[j]}' not found in {string.Join(", ", t.Headers)}"); return null; }
                }
                x = Enumerable.Range(0, t.Rows.Length)
                    .Select(i => fIdx.Select(c => c < t.Rows[i].Length ? t.ParseCell(t.Rows[i][c]) ?? double.NaN : double.NaN).ToArray())
                    .ToArray();

                if (responseNames.Count == 0)
                    responseNames = Enumerable.Range(0, t.Headers.Length)
                        .Where(c => !fIdx.Contains(c) && t.IsNumeric(c)
                                    && !t.Headers[c].StartsWith("x_", StringComparison.OrdinalIgnoreCase)
                                    && !t.Headers[c].Equals("Run", StringComparison.OrdinalIgnoreCase))
                        .Where(c => !t.Headers[c].Equals("feasible", StringComparison.OrdinalIgnoreCase))
                        .Select(c => t.Headers[c]).ToList();
                foreach (var rn in responseNames)
                {
                    if (rn.Equals("feasible", StringComparison.OrdinalIgnoreCase)) continue;
                    int c = t.ColumnIndex(rn);
                    if (c < 0)
                    { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Response column '{rn}' not found"); return null; }
                    yCols.Add((rn, t.NumericColumn(c)));
                }
            }
            if (yCols.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No response columns found. Fill the measurement columns first"); return null; }

            // ── coding ranges ──
            var lows = Active.Contains("ranges") ? GetNumberList(DA, "lows") : new List<double>();
            var highs = Active.Contains("ranges") ? GetNumberList(DA, "highs") : new List<double>();
            var centres = Active.Contains("ranges") ? GetNumberList(DA, "centres") : new List<double>();
            var lo = new double[m];
            var hi = new double[m];
            double[]? ce = centres.Count == m ? centres.ToArray() : null;
            for (int j = 0; j < m; j++)
            {
                var col = x.Select(r => r[j]).Where(v => !double.IsNaN(v)).ToArray();
                if (col.Length == 0)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Factor '{factorNames[j]}' has no numeric data"); return null; }
                lo[j] = j < lows.Count ? lows[j] : col.Min();
                hi[j] = j < highs.Count ? highs[j] : col.Max();
                if (!(hi[j] > lo[j]))
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Factor '{factorNames[j]}': high must be above low. Is the column constant?"); return null; }
            }

            // ── fit per response ──
            var reports = new List<string>();
            var transforms = GetStringList(DA, "transform");
            var computed = GetStringList(DA, "computed");
            if (computed.Count > 1 && computed.Count != yCols.Count)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "computed needs one value or one per response"); return null; }
            if (transforms.Count > 1 && transforms.Count != yCols.Count)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "transform needs one value or one per response"); return null; }
            for (int response = 0; response < yCols.Count; response++)
            {
                var (name, y) = yCols[response];
                string computedText = computed.Count == 0 ? "no" : computed[computed.Count == 1 ? 0 : response].Trim().ToLowerInvariant();
                if (computedText != "yes" && computedText != "no")
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "computed must be yes or no"); return null; }
                string transformText = transforms.Count == 0 ? "none" : transforms[transforms.Count == 1 ? 0 : response].Trim();
                if (!Enum.TryParse(transformText, true, out ResponseTransform transform) ||
                    !Enum.IsDefined(typeof(ResponseTransform), transform) || char.IsDigit(transformText.FirstOrDefault()))
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "transform must be none, log1p, sqrt or boxcox"); return null; }
                int nOk = y.Count(v => !double.IsNaN(v));
                if (nOk < 3)
                {
                    res.Info += $"[{name}: only {nOk} values, skipped]  ";
                    continue;
                }
                RsmModel mdl;
                try { mdl = RsmFitter.Fit(name, factorNames.ToArray(), lo, hi, ce, x, y, type, transform, feasible, computedText == "yes"); }
                catch (ArgumentException ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, name + ": " + ex.Message); return null; }
                res.Models.Add(mdl);
                reports.Add(RsmFitter.Report(mdl));
                res.Equations.Add(mdl.EquationCoded() + mdl.EquationActual());
                res.Json.Add(mdl.ToJson());
                var path = new GH_Path(res.Models.Count - 1);
                foreach (var row in mdl.XActual)
                    res.Pred.Append(new GH_Number(mdl.Predict(row)), path);

                // influence/collinearity diagnostics - in clay DoE the single bad
                // print run driving the whole surface is the NORMAL failure mode,
                // so it gets a runtime message, not just a panel line
                var dg = RsmInference.Diagnose(mdl);
                res.Diag.Add(RsmInference.Report(dg));
                var infl = dg.Runs.Where(r => r.Influential).ToList();
                if (infl.Count > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{name}: run(s) {string.Join(", ", infl.Select(r => r.Run + 1))} have Cook's D above 1. " +
                        "A single run is steering the fitted surface. Measure or print it again before trusting it");
                if (dg.Vif.Any(v => v > 10))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{name}: VIF above 10 on {string.Join(", ", dg.VifTerms.Where((_, j) => dg.Vif[j] > 10))}. " +
                        "These coefficients cannot be interpreted one by one because the design columns are nearly collinear");
            }
            if (res.Models.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No fittable responses. " + res.Info); return null; }

            if (feasible != null)
            {
                var mask = RsmFitter.FitFeasibility(factorNames.ToArray(), lo, hi, ce, x, feasible);
                res.Models.Add(mask); res.Json.Add(mask.ToJson());
                reports.Add(RsmFitter.Report(mask));
            }
            res.Report = string.Join("\n", reports);
            res.Info = $"{res.Models.Count} response model(s), {res.Models[0].NRuns} runs, {m} factors ({type})  " + res.Info;

            if (Active.Contains("export"))
            {
                string xp = GetString(DA, "xlsxReport", "").Trim();
                if (xp.Length > 0)
                {
                    try
                    {
                        // background export (AUD-PERF-001), keyed by path + report content
                        string p = Lugworm.Core.Gcode.SavePath.Resolve(xp, "rsm_report.xlsx");
                        ulong reportHash = ContentHash.Fnv1a(res.Report);
                        string saveKey = FormattableString.Invariant(
                            $"{p}|{res.Report.Length}|{reportHash:X16}");
                        if (_reportWriter.ResultFor(saveKey) is string savedPath)
                            res.Info += $"  saved:{savedPath}";
                        else if (_reportWriter.ErrorFor(saveKey) is string saveError)
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, saveError);
                            _reportWriter.ForgetError();
                        }
                        else
                        {
                            var models = res.Models;
                            _reportWriter.Start(this, saveKey, ct =>
                            {
                                try { DoeExport.RsmToXlsx(models, p); return p; }
                                catch (Exception ex)
                                { throw new System.IO.IOException(Lugworm.Core.Gcode.SavePath.Explain(ex, p)); }
                            });
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Exporting " + p + " …");
                            res.Info += "  exporting…";
                        }
                    }
                    catch (Exception ex)
                    { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Report export failed: " + Lugworm.Core.Gcode.SavePath.Explain(ex, xp)); }
                }
            }

            try { res.Graph = GraphRenderer.RsmPanel(res.Models); }
            catch (Exception ex)
            {
                // As in Desirability: the fits are done and emitted, only the canvas panel
                // is lost. Broad on purpose - System.Drawing on a starved or headless GDI
                // host has no documented exception set.
                res.Graph = null;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "The canvas graph could not be drawn (" + ex.Message + "). The outputs are unaffected");
            }

            foreach (var mdl in res.Models)
            {
                var badR2 = !double.IsNaN(mdl.R2Adj) && mdl.R2Adj < 0.5;
                if (badR2)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{mdl.ResponseName}: weak fit (R²adj = {mdl.R2Adj:0.00}). Model predictions are unreliable");
                var lof = mdl.Anova.FirstOrDefault(a => a.Source == "Lack of Fit");
                if (lof != null && !double.IsNaN(lof.P) && lof.P < 0.05)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{mdl.ResponseName}: significant lack of fit (p = {lof.P:0.000}). Consider a different model type");
            }

            Message = $"{res.Models.Count} model(s) - {type}";
            return res;
        }

        bool GetCurveTreeSafeHasData()
        {
            int i = Params.IndexOfInputParam("X");
            return i >= 0 && Params.Input[i].SourceCount > 0;
        }

        (double[][]?, double[][]?) GetDirectData(IGH_DataAccess DA)
        {
            int xi = Params.IndexOfInputParam("X");
            int yi = Params.IndexOfInputParam("Y");
            if (xi < 0 || yi < 0) return (null, null);
            if (!DA.GetDataTree(xi, out GH_Structure<GH_Number> xt) ||
                !DA.GetDataTree(yi, out GH_Structure<GH_Number> yt))
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Direct mode needs both X and Y trees"); return (null, null); }
            var x = xt.Branches.Select(b => b.Select(v => v?.Value ?? double.NaN).ToArray()).ToArray();
            var y = yt.Branches.Select(b => b.Select(v => v?.Value ?? double.NaN).ToArray()).ToArray();
            if (x.Length != y.Length)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"X has {x.Length} runs but Y has {y.Length}"); return (null, null); }
            return (x, y);
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            _last = r;
            DA.SetDataList(0, r.Models.Select(mm => new GH_ObjectWrapper(mm)));
            DA.SetData(1, r.Report);
            DA.SetDataList(2, r.Equations);
            DA.SetDataTree(3, r.Pred);
            DA.SetDataList(4, r.Json);
            DA.SetData(5, r.Info);
            DA.SetDataList(6, r.Diag);
            Attributes?.ExpireLayout();
        }

        // background file work (AUD-PERF-001)
        readonly BackgroundFileWork<DataTableLite> _tableReader = new("table read");
        readonly BackgroundFileWork<string> _reportWriter = new("report export");

        public override void RemovedFromDocument(GH_Document document)
        {
            _tableReader.CancelAndDetach();
            _reportWriter.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) { _tableReader.CancelAndDetach(); _reportWriter.CancelAndDetach(); }
            base.DocumentContextChanged(document, context);
        }
    }
}
