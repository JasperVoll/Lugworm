using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Core.Doe;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Derringer–Suich desirability optimisation over fitted RSM models
    /// (stage 3 of the FBI2026 workflow): one goal per response ("min", "max",
    /// "target=7 in 6..8", "range=a..b", optional "w=" weight and "s=" shape),
    /// optional factor priors ("low"/"high", e.g. prefer low layer height), grid
    /// search + Nelder-Mead refinement, and an optional recipe sweep over a series
    /// of targets (e.g. one recipe per target layer width).
    /// PROTOTYPE: the on-canvas goal-curve strip (below the profile) lets one goal
    /// at a time be shaped by dragging instead of typing; the edited goal is stored
    /// on the component as an override of the wired text and survives save/reopen.</summary>
    public class DesirabilityComponent : IamComponentBase, IGraphHost, ICurveEditorHost
    {
        public DesirabilityComponent()
            : base("Desirability", "Desire",
                   "Press Compute with fitted models and matching goals to find the best parameter recipe.",
                   "Lugworm", "6 Calibrate")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0009-4a9b-9c3d-aa0000000009");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Desirability();
        protected override bool Heavy => true;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("models", "M", "RSM Evaluate models or JSON output, one fitted model per response", "core", PType.Generic, GH_ParamAccess.list),
            new ParamSpec("goals", "G", "One goal per model in matching order: min, max, target=7 in 6..8, range=6..8 or none (optional w=2, s=2)", "core", PType.Text, GH_ParamAccess.list)
                .HintText("target=7 in 6..8 w=2\nmin\nmin"),
            // priors
            new ParamSpec("factorPriors", "Fp", "Optional prior per factor: 'none', 'low', 'high' or 'target=x'. Add 'w=' for weight. For example prefer a low layer height for bonding", "priors", PType.Text, GH_ParamAccess.list)
                .HintText("low w=1.5\nnone\nhigh w=1.5"),
            // bounds / search
            new ParamSpec("lows", "Lo", "Search minima in actual units, one per factor in model order (otherwise model limits)", "bounds", PType.Number, GH_ParamAccess.list),
            new ParamSpec("highs", "Hi", "Search maxima in actual units, one per factor in model order (otherwise model limits)", "bounds", PType.Number, GH_ParamAccess.list),
            new ParamSpec("gridPerDim", "Gr", "Grid resolution per factor, clamped so the total stays at or below 500k points", "bounds", PType.Integer).Hint(5, 60, 15, 0),
            // sweep
            new ParamSpec("sweepResponse", "Sr", "Model response name to optimize at each sweepValues target, required for the sweep", "sweep", PType.Text),
            new ParamSpec("sweepValues", "Sv", "Target values in sweepResponse units, one optimized recipe per value", "sweep", PType.Number, GH_ParamAccess.list)
                .HintText("6.5\n7\n7.5\n8"),
            new ParamSpec("sweepBand", "Sb", "Acceptance band around each sweep target, plus or minus, in response units", "sweep", PType.Number).Hint(0.05, 5, 0.4, 2),
            // pareto front
            new ParamSpec("paretoOn", "Po", "Collect the non-dominated recipes over the individual goal desirabilities, for Pareto Select. Goal bounds shape the front, weights do not", "pareto", PType.Boolean),
            new ParamSpec("maxFront", "Pn", "Maximum retained tradeoff recipes, preserving each objective's best recipe even if this cap is exceeded", "pareto", PType.Integer).Hint(20, 2000, 200, 0),
            // export
            new ParamSpec("xlsxPath", "Xl", "Local .xlsx file or folder to export on Compute with Export enabled, overwriting the target", "export", PType.Text),
            new ParamSpec("feasibleThreshold", "Ft", "Predicted feasibility below this value is treated as unprintable", "bounds", PType.Number).Hint(0, 1, 0.5, 2),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("priors", "Factor priors (prefer low/high factor levels)"),
            ("bounds", "Search bounds / grid resolution"),
            ("sweep", "Recipe sweep (one recipe per target value)"),
            ("pareto", "Pareto front (tradeoffs)"),
            ("export", "Excel export input"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("settings", "S", "Recommended factor settings in actual units, factor order of the models", GH_ParamAccess.list);
            pManager.AddNumberParameter("D", "D", "Overall desirability, 0 to 1", GH_ParamAccess.item);
            pManager.AddNumberParameter("predicted", "Yp", "Predicted responses at the recommended settings", GH_ParamAccess.list);
            pManager.AddTextParameter("report", "T", "Readable optimization report. Connect a panel", GH_ParamAccess.item);
            pManager.AddNumberParameter("alternatives", "A", "Other local best recipes, one branch each: factor values in model order, then overall desirability D", GH_ParamAccess.tree);
            pManager.AddNumberParameter("sweep", "Sw", "Sweep recipes, one branch per target: target, factors, D, predictions", GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
            pManager.AddNumberParameter("interval", "Pi", "95% prediction limits in response units, one branch per response: low, predicted, high (empty without model uncertainty data)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("pareto", "Pf", "Tradeoff recipes with Pareto enabled and paretoOn true, one branch each: factors, active-goal desirabilities, D. Wire to Pareto Select front", GH_ParamAccess.tree);
            pManager.AddTextParameter("objectives", "O", "Names of the active goals in model order, without none goals. The axes of the pareto output. Wire to Pareto Select objectiveNames", GH_ParamAccess.list);
        }

        sealed class Result
        {
            public DesirabilityResult Opt = new();
            public List<RsmModel> Models = new();
            public List<Goal> Goals = new();
            public List<Goal>? Priors;
            public List<string> Objectives = new();
            public string Report = "", Info = "";
            public GH_Structure<GH_Number> Alternatives = new(), Sweep = new(), Interval = new(), Pareto = new();
            public Bitmap? Graph;
        }

        Result? _last;
        public Bitmap? GraphImage => _last?.Graph;

        // ---- goal-curve editor (prototype) ----
        // response name -> goal text override, edited by dragging the on-canvas
        // curve. An override wins over the wired goals text for that response and
        // is persisted with the document; the strip's ✕ removes it.
        readonly Dictionary<string, string> _goalOverrides = new(StringComparer.OrdinalIgnoreCase);
        int _editorIndex;
        GoalCurveEditor? _editor;
        public ICanvasStrip? CurveEditor => _editor;

        void RebuildEditor()
        {
            var last = _last;
            if (last == null || last.Models.Count == 0) { _editor = null; return; }
            _editorIndex = ((_editorIndex % last.Models.Count) + last.Models.Count) % last.Models.Count;
            var mdl = last.Models[_editorIndex];
            var active = last.Goals[_editorIndex];
            // the editor drags a CLONE - the optimisation's goal list stays immutable
            var working = new Goal
            {
                Name = active.Name,
                Kind = active.Kind,
                Low = active.Low,
                High = active.High,
                Target = active.Target,
                Weight = active.Weight,
                S = active.S,
            };
            double yLo = mdl.Y.Length > 0 ? mdl.Y.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Min() : 0;
            double yHi = mdl.Y.Length > 0 ? mdl.Y.Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max() : 1;
            if (!double.IsNaN(working.Low)) yLo = Math.Min(yLo, working.Low);
            if (!double.IsNaN(working.High)) yHi = Math.Max(yHi, working.High);
            double pad = 0.08 * Math.Max(1e-9, yHi - yLo);
            _editor = new GoalCurveEditor
            {
                Title = $"{mdl.ResponseName} ({working.Kind})",
                Goal = working,
                YMin = yLo - pad,
                YMax = yHi + pad,
                Overridden = _goalOverrides.ContainsKey(mdl.ResponseName),
                // snapshot component state (Write) so ctrl+Z restores the pre-drag
                // goal - closes the prototype's stated no-undo gap
                DragStarted = () => RecordUndoEvent("Lugworm: goal curve"),
                Changed = () =>
                {
                    _goalOverrides[mdl.ResponseName] = GoalCurveEditor.ToText(working);
                    if (_editor != null) _editor.Overridden = true;
                    ExpireSolution(true);
                },
                Cycle = () =>
                {
                    _editorIndex++;
                    RebuildEditor();
                    Attributes?.ExpireLayout();
                    Grasshopper.Instances.RedrawCanvas();
                },
                Reset = () =>
                {
                    _goalOverrides.Remove(mdl.ResponseName);
                    RebuildEditor();
                    ExpireSolution(true);
                },
            };
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();

            // models: unwrap RsmModel objects or parse JSON strings
            foreach (var o in GetObjectList(DA, "models"))
            {
                switch (o)
                {
                    case RsmModel mdl: res.Models.Add(mdl); break;
                    case string js when js.TrimStart().StartsWith("{", StringComparison.Ordinal):
                        try { res.Models.Add(RsmModel.FromJson(js)); }
                        catch (Exception ex)
                        { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Model JSON parse failed: " + ex.Message); return null; }
                        break;
                    case null: break;
                    default:
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Ignored input of type {o.GetType().Name} in models");
                        break;
                }
            }
            if (res.Models.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No models wired. Connect the models output of the RSM component"); return null; }
            var m0 = res.Models[0];

            // goals
            var goalTexts = GetStringList(DA, "goals");
            if (goalTexts.Count == res.Models.Count(m => m.Kind == RsmModelKind.Response))
            {
                int next = 0;
                goalTexts = res.Models.Select(m => m.Kind == RsmModelKind.Feasibility ? "none" : goalTexts[next++]).ToList();
            }
            if (goalTexts.Count != res.Models.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{res.Models.Count} models need {res.Models.Count} goals, got {goalTexts.Count}. The order must match. Responses: " +
                    string.Join(", ", res.Models.Select(mm => mm.ResponseName)));
                return null;
            }
            try
            {
                for (int i = 0; i < res.Models.Count; i++)
                {
                    var mdl = res.Models[i];
                    if (mdl.Kind == RsmModelKind.Feasibility)
                    { res.Goals.Add(new Goal { Name = mdl.ResponseName, Kind = GoalKind.None }); continue; }
                    double yLo = mdl.Y.Length > 0 ? mdl.Y.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Min() : 0;
                    double yHi = mdl.Y.Length > 0 ? mdl.Y.Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max() : 1;
                    var goal = Goal.Parse(goalTexts[i], mdl.ResponseName, yLo, yHi);
                    // a canvas-edited goal wins over the wired text for its response
                    // (prototype - see the curve strip; ✕ there removes the override)
                    if (_goalOverrides.TryGetValue(mdl.ResponseName, out var ov))
                    {
                        try
                        {
                            goal = Goal.Parse(ov, mdl.ResponseName, yLo, yHi);
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                                $"Goal for '{mdl.ResponseName}' edited on the canvas: {ov}. The ✕ on the curve strip resets it");
                        }
                        // Broad: Goal.Parse rejects a malformed override by throwing, and
                        // the text came off the canvas strip in some older build's format.
                        // Either way the stored override is dropped and said so below.
                        catch (Exception)
                        {
                            _goalOverrides.Remove(mdl.ResponseName);
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                                $"Stored goal override for '{mdl.ResponseName}' no longer parses and was dropped");
                        }
                    }
                    res.Goals.Add(goal);
                }

                if (Active.Contains("priors"))
                {
                    var priorTexts = GetStringList(DA, "factorPriors");
                    res.Priors = Enumerable.Range(0, m0.FactorCount).Select(j =>
                        j < priorTexts.Count && priorTexts[j].Trim().Length > 0
                            ? Goal.Parse(priorTexts[j], m0.FactorNames[j], double.NaN, double.NaN)
                            : new Goal { Kind = GoalKind.None }).ToList();
                }
            }
            catch (Exception ex)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return null; }
            // the front's axes, emitted whether or not the front is collected so the wire
            // to Pareto Select can be made before paretoOn is switched on
            res.Objectives = res.Goals.Where(g => g.Kind != GoalKind.None).Select(g => g.Name).ToList();

            var lows = Active.Contains("bounds") ? GetNumberList(DA, "lows") : new List<double>();
            var highs = Active.Contains("bounds") ? GetNumberList(DA, "highs") : new List<double>();
            double[]? lo = lows.Count == m0.FactorCount ? lows.ToArray() : null;
            double[]? hi = highs.Count == m0.FactorCount ? highs.ToArray() : null;
            int grid = GetInt(DA, "gridPerDim", 15);

            bool paretoOn = Active.Contains("pareto") && GetBool(DA, "paretoOn", false);
            int maxFront = GetInt(DA, "maxFront", 200);
            double feasibleThreshold = GetNumber(DA, "feasibleThreshold", 0.5);
            try
            {
                res.Opt = DesirabilityOptimizer.Optimize(res.Models, res.Goals, res.Priors, lo, hi, grid,
                    collectFront: paretoOn, maxFront: maxFront, feasibleThreshold: feasibleThreshold);
            }
            catch (Exception ex)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return null; }

            res.Report = DesirabilityOptimizer.Report(res.Opt, res.Models, res.Goals, res.Priors);
            res.Info = $"D={res.Opt.DOverall:0.000}  ({res.Models.Count} responses, {m0.FactorCount} factors, grid {grid}/dim + Nelder-Mead)";
            if (res.Opt.Notes.Length > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, res.Opt.Notes);

            for (int a = 0; a < res.Opt.Alternatives.Count; a++)
            {
                var path = new GH_Path(a);
                foreach (var v in res.Opt.Alternatives[a].x) res.Alternatives.Append(new GH_Number(v), path);
                res.Alternatives.Append(new GH_Number(res.Opt.Alternatives[a].d), path);
            }

            // 95% prediction interval per response at the recommendation
            for (int i = 0; i < res.Models.Count; i++)
            {
                var band = RsmInference.PredictionInterval(res.Models[i], res.Opt.XBest);
                if (!band.Valid) continue;
                var path = new GH_Path(i);
                res.Interval.Append(new GH_Number(band.Lo), path);
                res.Interval.Append(new GH_Number(band.Fit), path);
                res.Interval.Append(new GH_Number(band.Hi), path);
            }

            for (int f = 0; f < res.Opt.Front.Count; f++)
            {
                var path = new GH_Path(f);
                foreach (var v in res.Opt.Front[f].ToBranch()) res.Pareto.Append(new GH_Number(v), path);
            }
            if (paretoOn)
                res.Info += $"  front: {res.Opt.Front.Count} non-dominated";

            // ── sweep ──
            var sweepResults = new List<(string label, DesirabilityResult r)>();
            if (Active.Contains("sweep"))
            {
                string sr = GetString(DA, "sweepResponse", "").Trim();
                var vals = GetNumberList(DA, "sweepValues");
                double band = GetNumber(DA, "sweepBand", 0.4);
                int si = res.Models.FindIndex(mm => mm.ResponseName.Equals(sr, StringComparison.OrdinalIgnoreCase));
                if (sr.Length > 0 && si < 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"The sweepResponse '{sr}' is not among: {string.Join(", ", res.Models.Select(mm => mm.ResponseName))}");
                else if (si >= 0 && vals.Count > 0)
                {
                    for (int k = 0; k < vals.Count; k++)
                    {
                        var goals = res.Goals.ToList();
                        goals[si] = new Goal
                        {
                            Name = sr,
                            Kind = GoalKind.Target,
                            Target = vals[k],
                            Low = vals[k] - band,
                            High = vals[k] + band,
                            Weight = res.Goals[si].Weight,
                            S = res.Goals[si].S,
                        };
                        var opt = DesirabilityOptimizer.Optimize(res.Models, goals, res.Priors, lo, hi, grid, feasibleThreshold: feasibleThreshold);
                        sweepResults.Add(($"{sr}={vals[k]:0.###}", opt));
                        var path = new GH_Path(k);
                        res.Sweep.Append(new GH_Number(vals[k]), path);
                        foreach (var v in opt.XBest) res.Sweep.Append(new GH_Number(v), path);
                        res.Sweep.Append(new GH_Number(opt.DOverall), path);
                        foreach (var v in opt.YPred) res.Sweep.Append(new GH_Number(v), path);
                    }
                    res.Info += $"  sweep: {vals.Count} recipes for {sr}";
                }
            }

            if (Active.Contains("export"))
            {
                // Background export (AUD-PERF-001): the recipe, model names, goals, priors
                // and sweep are copied into an owned snapshot on the solver thread; path
                // probing, directory creation and the write run on the worker.
                string xp = GetString(DA, "xlsxPath", "").Trim();
                if (xp.Length > 0)
                    res.Info += Export(_reportWriter, DoeExportSnapshot.CaptureDesirability(
                        res.Opt, res.Models, res.Goals, res.Priors, xp, sweepResults.Count > 0 ? sweepResults : null));
            }

            try { res.Graph = GraphRenderer.DesirabilityPanel(res.Opt, res.Models, res.Priors); }
            catch (Exception ex)
            {
                // The optimisation above already succeeded and every number it produced is
                // still emitted - only the canvas panel is lost, so this must not fail the
                // solve. It used to vanish with no explanation at all; say why.
                // Broad on purpose: the panel is drawn through System.Drawing, whose
                // failures on a starved or headless GDI host are not a documented set.
                res.Graph = null;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "The canvas graph could not be drawn (" + ex.Message + "). The outputs are unaffected");
            }

            Message = $"D = {res.Opt.DOverall:0.00}";
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            _last = r;
            RebuildEditor();
            DA.SetDataList(0, r.Opt.XBest);
            DA.SetData(1, r.Opt.DOverall);
            DA.SetDataList(2, r.Opt.YPred);
            DA.SetData(3, r.Report);
            DA.SetDataTree(4, r.Alternatives);
            DA.SetDataTree(5, r.Sweep);
            DA.SetData(6, r.Info);
            DA.SetDataTree(7, r.Interval);
            DA.SetDataTree(8, r.Pareto);
            DA.SetDataList(9, r.Objectives);
            Attributes?.ExpireLayout();
        }

        // ---- background export (AUD-PERF-001) -----------------------------------

        readonly BackgroundFileWork<string> _reportWriter = new("report export");

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
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Exporting the report in the background …");
            return "  exporting...";
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _reportWriter.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) _reportWriter.CancelAndDetach();
            base.DocumentContextChanged(document, context);
        }

        // ---- persistence: canvas-edited goal overrides survive save/reopen ------

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("iamGoalOverrideCount", _goalOverrides.Count);
            int i = 0;
            foreach (var kv in _goalOverrides)
            {
                writer.SetString("iamGoalOverrideName" + i, kv.Key);
                writer.SetString("iamGoalOverrideText" + i, kv.Value);
                i++;
            }
            writer.SetInt32("iamGoalEditorIndex", _editorIndex);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            _goalOverrides.Clear();
            int count = 0;
            if (reader.TryGetInt32("iamGoalOverrideCount", ref count))
                for (int i = 0; i < count; i++)
                {
                    string name = "", text = "";
                    if (reader.TryGetString("iamGoalOverrideName" + i, ref name)
                        && reader.TryGetString("iamGoalOverrideText" + i, ref text)
                        && name.Length > 0 && text.Length > 0)
                        _goalOverrides[name] = text;
                }
            int idx = 0;
            if (reader.TryGetInt32("iamGoalEditorIndex", ref idx)) _editorIndex = idx;
            return base.Read(reader);
        }
    }
}
