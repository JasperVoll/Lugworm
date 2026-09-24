using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Core.Doe;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Graphical recipe selection on the Desirability component's Pareto front
    /// (3.0.0, additive - Desirability's own outputs are untouched). Takes the front
    /// tree plus the factor and active-objective names, draws it (k = 2 scatter, k &gt; 2
    /// parallel coordinates, k = 1 strip), and lets the operator pick a recipe on the
    /// canvas strip, in a larger window, or headlessly by key or index.
    /// Why the front is drawn over desirabilities and not over measured responses: a
    /// desirability function does two separate jobs. The first maps one response onto
    /// 0..1 "bigger is better" from its goal (min, max, target, range and the acceptance
    /// bounds), which is what makes a target goal Pareto-orderable at all; the second
    /// aggregates those numbers into one D with the weights. The front uses only the
    /// first job, so it does not depend on the weights, and this component is where the
    /// second job is applied AFTER the front - by clicking, or by wiring weights to see
    /// where a preference lands (Lu, Anderson-Cook &amp; Robinson, Technometrics 2011;
    /// Trautmann &amp; Weihs, SFB 475 TR 63/2004). The chosen
    /// recipe is persisted with the document by its VALUES and the front's identity,
    /// never by row index, so a reordered front keeps it and a replaced front flags it
    /// stale instead of silently pointing at a different recipe. The archived front is
    /// stored too, so the plot and outputs come back without re-running DoE.</summary>
    public class ParetoSelectComponent : IamComponentBase, ICurveEditorHost, IButtonRowHost
    {
        public ParetoSelectComponent()
            : base("Pareto Select", "Pareto",
                   "Supply a Pareto front with its factor and objective names, then pick a recipe by clicking, or wire weights to see where a preference lands.",
                   "Lugworm", "6 Calibrate")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0030-4beb-9c3d-aa0000000030");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.ParetoSelect();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("front", "Pf", "The pareto output of Desirability, one branch per recipe: factor values, one desirability per active goal, D. Optional once a selection is stored", "core", PType.Number, GH_ParamAccess.tree),
            new ParamSpec("factorNames", "N", "Factor names from DoE Design in model order, exactly one per factor value", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("objectiveNames", "O", "Names of the active goals in model order, the objectives output of Desirability. The axes are desirabilities from 0 to 1, not response units", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("selectionKey", "K", "Recipe key from this component's key output for selection without the picker, valid only for its original front", "core", PType.Text),
            new ParamSpec("selectionIndex", "I", "Select a recipe by current branch index from 0 (-1 = none), reapplied only when this input changes", "core", PType.Integer).Hint(-1, 2000, -1, 0),
            new ParamSpec("optimumSettings", "S", "Desirability settings output to mark the recommended recipe on the plot (unwired = highest D)", "core", PType.Number, GH_ParamAccess.list),
            new ParamSpec("weights", "W", "Importance per active objective in objectiveNames order, 0 or more each. Marks the recipe with the highest weighted geometric mean of the front's own desirabilities, response goals only, without re-running Desirability", "core", PType.Number, GH_ParamAccess.list),
            new ParamSpec("confirmValues", "Cv", "Measured values for the selected recipe in objectiveNames order", "confirm", PType.Number, GH_ParamAccess.list),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[] { ("confirm", "Confirm selected recipe") };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("settings", "S", "Selected factor values in actual units, factorNames order. Empty without a selection", GH_ParamAccess.list);
            pManager.AddNumberParameter("di", "d", "Selected recipe's desirability per active objective, 0 to 1", GH_ParamAccess.list);
            pManager.AddNumberParameter("D", "D", "Selected recipe's overall desirability from 0 to 1", GH_ParamAccess.item);
            pManager.AddTextParameter("key", "K", "Recipe key for selectionKey, valid only for the front it was issued from", GH_ParamAccess.item);
            pManager.AddTextParameter("status", "T", "Selection status: current, stale (front changed), restored, ambiguous, or why a request was refused", GH_ParamAccess.item);
            pManager.AddBooleanParameter("current", "C", "True only when the stored recipe is verified on the live front under the same identity", GH_ParamAccess.item);
            pManager.AddTextParameter("confirmReport", "Cr", "Confirmation measurements checked against the selected recipe's prediction intervals", GH_ParamAccess.item);
        }

        ParetoSelectionState _state = ParetoSelectionState.Empty;
        ParetoFrontSnapshot? _displayed;
        bool _archived;
        double[]? _optimum;
        double[]? _weights;   // live input only: the weighted marker is a preference probe, not part of the archive
        string? _archiveWarning;
        ParetoSelectionStrip? _strip;
        ParetoSelectionForm? _form;

        public ICanvasStrip? CurveEditor => _strip;

        public IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows => new[]
        {
            new[]
            {
                new ButtonDef("pick", "Pick...", GH_Palette.Black, _displayed != null, "Open the recipe picker after supplying front, factorNames and objectiveNames, or using a restored front"),
                new ButtonDef("reset", "Reset", GH_Palette.Grey, _state.Recipe != null, "Clear the current recipe with one press, with no wired inputs required (undoable)"),
            },
        };

        public void OnCapsulePressed(string id)
        {
            if (id == "pick") ShowWindow();
            else if (id == "reset") ResetSelection();
        }

        sealed class Result
        {
            public ParetoSelectionState State = ParetoSelectionState.Empty;
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var branches = new List<IReadOnlyList<double>>();
            int fi = Params.IndexOfInputParam("front");
            if (fi >= 0 && DA.GetDataTree(fi, out GH_Structure<GH_Number> tree) && tree != null)
                foreach (var branch in tree.Branches)
                    branches.Add(branch.Select(v => v?.Value ?? double.NaN).ToArray());   // a null item is malformed, never skipped
            var factorNames = GetStringList(DA, "factorNames");
            var objectiveNames = GetStringList(DA, "objectiveNames");
            string key = GetString(DA, "selectionKey", "").Trim();
            int indexInput = GetInt(DA, "selectionIndex", -1);
            int? index = indexInput >= 0 ? indexInput : (int?)null;
            var optimum = GetNumberList(DA, "optimumSettings");
            _optimum = optimum.Count > 0 ? optimum.ToArray() : null;
            var weights = GetNumberList(DA, "weights");
            _weights = weights.Count > 0 ? weights.ToArray() : null;

            ParetoFrontSnapshot? front = null;
            if (branches.Count > 0 || factorNames.Count > 0 || objectiveNames.Count > 0)
            {
                if (ParetoFrontSnapshot.TryParse(branches, factorNames, objectiveNames, out front, out string? error)) { }
                else
                {
                    front = null;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Front rejected: " + error + ". The last recipe is kept");
                }
            }

            if (_archiveWarning != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _archiveWarning);
                _archiveWarning = null;
            }

            _state = ParetoSelection.Observe(_state, front, key.Length > 0 ? key : null, index, out string? remark);
            var confirmation = GetNumberList(DA, "confirmValues");
            if (confirmation.Count > 0)
            {
                try { _state = ParetoSelection.Confirm(_state, confirmation); }
                catch (ArgumentException ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, ex.Message); }
            }
            if (front != null) { _displayed = front; _archived = false; }
            else if (_displayed != null) _archived = true;

            if (remark != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, remark);
            if (_weights != null && _displayed != null
                && _displayed.IndexOfWeightedBest(_weights, out _, out string? weightProblem) < 0 && weightProblem != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Weights not applied (" + weightProblem + "). Supply one weight from 0 up per active objective, in objectiveNames order");
            if (_state.Recipe != null && !_state.Current) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _state.Status);
            else if (_state.Recipe == null) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, _state.Status);
            if (_displayed != null && _displayed.Rows.Count > 0)
            {
                int k = _displayed.ObjectiveNames.Count;
                Message = _displayed.Rows.Count.ToString(CultureInfo.InvariantCulture) + " recipes - k=" + k.ToString(CultureInfo.InvariantCulture)
                        + (_archived ? " - archived" : "");
            }
            else Message = null;

            RebuildStrip();
            return new Result { State = _state };
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = ((Result)result).State;
            if (r.Recipe != null)
            {
                DA.SetDataList(0, r.Recipe.X);
                DA.SetDataList(1, r.Recipe.Di);
                DA.SetData(2, r.Recipe.D);
                DA.SetData(3, r.Recipe.Key);
            }
            DA.SetData(4, r.Status);
            DA.SetData(5, r.Current);
            DA.SetData(6, ParetoSelection.ConfirmationReport(r));
            Attributes?.ExpireLayout();
        }

        // ---- plot model: the same mapping for the strip and the window ----

        ParetoPlotModel BuildModel()
        {
            var m = new ParetoPlotModel { Front = _displayed, Archived = _archived, Selected = _state.Recipe, Status = _state.Status };
            if (_displayed != null)
            {
                if (_state.Recipe != null)
                {
                    var matches = _displayed.Matches(_state.Recipe);
                    if (_state.Current && matches.Count == 1) m.SelectedIndex = matches[0];
                    else if (_archived && matches.Count == 1 && string.Equals(_displayed.Hash, _state.Recipe.FrontHash, StringComparison.Ordinal))
                        m.SelectedIndex = matches[0];   // the archived front IS the one it was chosen on
                    else m.CandidateIndex = _state.CandidateIndex >= 0 ? _state.CandidateIndex : (matches.Count == 1 ? matches[0] : -1);
                }
                if (_optimum != null)
                {
                    int at = _displayed.IndexOfSettings(_optimum);
                    m.OptimumIndex = at;
                    m.OptimumLabel = at >= 0 ? "optimizer recommendation" : at == -2 ? "optimum ambiguous" : "optimumSettings not on this front";
                }
                else
                {
                    m.OptimumIndex = _displayed.IndexOfBestD();
                    m.OptimumLabel = "best D in supplied front";
                }
                if (_weights != null)
                {
                    // the Derringer-Suich aggregation applied after the front: response
                    // goals only, so it is a preference probe and never replaces D
                    int at = _displayed.IndexOfWeightedBest(_weights, out double score, out string? problem);
                    m.WeightedIndex = at;
                    m.WeightedLabel = at >= 0
                        ? "best for w=" + string.Join(",", _weights.Select(w => w.ToString("0.##", CultureInfo.InvariantCulture)))
                          + " (" + score.ToString("0.00", CultureInfo.InvariantCulture) + ", response goals only)"
                        : "weights not applied: " + (problem ?? "");
                }
            }
            return m;
        }

        void RebuildStrip()
        {
            if (_displayed == null && _state.Recipe == null) { _strip = null; return; }
            _strip ??= new ParetoSelectionStrip
            {
                Selected = row => SelectRow(row),
                OpenWindow = ShowWindow,
            };
            _strip.Model = BuildModel();
            if (_form != null && !_form.IsDisposed) _form.SetModel(BuildModel());
        }

        /// <summary>The one selection path for the strip, the window and the keyboard:
        /// undoable, then exactly one expiry on the GH UI thread.</summary>
        void SelectRow(int row)
        {
            var front = _displayed;
            if (front == null || row < 0 || row >= front.Rows.Count) return;
            RecordUndoEvent("Lugworm: Pareto selection");
            _state = ParetoSelection.Select(_state, front, row);
            ExpireSolution(true);
        }

        void ResetSelection()
        {
            if (_state.Recipe == null) return;
            RecordUndoEvent("Lugworm: Pareto selection reset");
            _state = ParetoSelection.Reset(_state);
            ExpireSolution(true);
        }

        void ShowWindow()
        {
            if (_form == null || _form.IsDisposed)
                _form = new ParetoSelectionForm(SelectRow);
            _form.SetModel(BuildModel());
            if (!_form.Visible) _form.Show();
            _form.Activate();
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Pick in window...", (s, e) => ShowWindow(), _displayed != null);
            Menu_AppendItem(menu, "Reset selection", (s, e) => ResetSelection(), _state.Recipe != null);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            DisposeForm();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) DisposeForm();
            base.DocumentContextChanged(document, context);
        }

        void DisposeForm()
        {
            var form = _form;
            _form = null;
            if (form != null && !form.IsDisposed) form.Dispose();
        }

        // ---- persistence: recipe by value + front identity + archived front, versioned ----

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("iamParetoSelection", ParetoArchive.Write(_state, _displayed, _optimum));
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string text = "";
            if (reader.TryGetString("iamParetoSelection", ref text) && !string.IsNullOrEmpty(text))
            {
                // Restores data only: no window, no optimizer, no machine work.
                if (ParetoArchive.TryRead(text, out var state, out var displayed, out var optimum, out string? warning))
                {
                    _state = state;
                    _displayed = displayed;
                    _archived = displayed != null;
                    _optimum = optimum;
                }
                _archiveWarning = warning;
            }
            return base.Read(reader);
        }
    }
}
