using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Lugworm.Core.Analysis;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>The stock Graph Mapper's job - remap a stream of numbers through an
    /// on-canvas curve - with the mechanics the original lacks: hover-highlighted
    /// grips, a live numeric readout, grid snapping (Ctrl 0.05 / Shift 0.01), the
    /// wired data drawn as ticks on both axes, real undo (ctrl+Z restores the
    /// pre-drag curve), per-family parameter memory when switching graph types, an
    /// equation output, and explicit input/output domains as wireable inputs
    /// instead of a modal dialog. Graph families: Linear, Bézier, Power (Square
    /// Root = e 0.5), Sine, Parabola, Gaussian, Sigmoid, Perlin.</summary>
    public sealed class GraphMapperComponent : IamComponentBase, ICurveEditorHost, IDropdownHost
    {
        public GraphMapperComponent()
            : base("Graph Mapper", "Graph Map",
                   "Edit the curve without inputs, or supply numbers to remap them through it.",
                   "Lugworm", "1 Slice")
        {
            // the strip is interactive from the moment the component is dropped,
            // so its callbacks must not wait for the first solve
            SyncEditor();
        }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0027-4bbb-9c3d-aa0000000027");

        GraphMap _map = GraphMap.Default(GraphMapKind.Linear);
        // per-family parameter memory: switching Bezier -> Sine -> Bezier restores
        // the Bézier exactly (the stock mapper forgets); whole maps, so a Spline's
        // tangent handles come back with its points
        readonly Dictionary<GraphMapKind, GraphMap> _memory = new();
        readonly GraphMapEditor _editor = new();
        public ICanvasStrip? CurveEditor => _editor;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("t", "t", "Values to remap through the curve", "core", PType.Number, GH_ParamAccess.list),
            new ParamSpec("inMin", "I0", "Input domain start (maps to the graph's left edge)", "domain", PType.Number).Hint(-10000, 10000, 0, 3),
            new ParamSpec("inMax", "I1", "Input domain end (maps to the graph's right edge)", "domain", PType.Number).Hint(-10000, 10000, 1, 3),
            new ParamSpec("outMin", "O0", "Output range at the graph's bottom edge", "domain", PType.Number).Hint(-10000, 10000, 0, 3),
            new ParamSpec("outMax", "O1", "Output range at the graph's top edge", "domain", PType.Number).Hint(-10000, 10000, 1, 3),
            new ParamSpec("clip", "C", "Values outside the domain are pinned to the graph edges either way; false only reports the result as unclipped in info", "domain", PType.Boolean).HintBool(true),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("domain", "Input / output domains (default unit ↦ unit)"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("y", "y", "Remapped values, one per input", GH_ParamAccess.list);
            pManager.AddTextParameter("equation", "E", "The curve as a formula over normalized t", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Graph type, domains, value count", GH_ParamAccess.item);
            pManager.AddTextParameter("map", "M", "Curve definition text for Slice's profile input, used with infill 6 and Wave inputs enabled", GH_ParamAccess.item);
        }

        // ── graph-type dropdown (on-canvas capsule) ──
        public string DropdownLabel => _map.Kind.ToString();

        public void FillDropdown(ToolStripDropDown menu)
        {
            foreach (GraphMapKind kind in Enum.GetValues(typeof(GraphMapKind)))
            {
                var captured = kind;
                var item = new ToolStripMenuItem(captured.ToString()) { Checked = captured == _map.Kind };
                item.Click += (o, e) => SwitchKind(captured);
                menu.Items.Add(item);
            }
            // the bridge from any analytic family into free point editing: sample
            // the current curve and keep shaping it point by point
            if (!_map.IsCustom)
            {
                menu.Items.Add(new ToolStripSeparator());
                var toPoly = new ToolStripMenuItem("Convert → Polyline (9 pts)");
                toPoly.Click += (o, e) => ConvertTo(GraphMapKind.Polyline);
                menu.Items.Add(toPoly);
                var toSpline = new ToolStripMenuItem("Convert → Spline (9 pts)");
                toSpline.Click += (o, e) => ConvertTo(GraphMapKind.Spline);
                menu.Items.Add(toSpline);
            }
        }

        void ConvertTo(GraphMapKind target)
        {
            RecordUndoEvent("Lugworm: graph convert");
            _memory[_map.Kind] = _map.Copy();
            _map = _map.ToCustom(target, 9);
            SyncEditor();
            ExpireSolution(true);
        }

        void SwitchKind(GraphMapKind kind)
        {
            if (kind == _map.Kind) return;
            RecordUndoEvent("Lugworm: graph type");
            _memory[_map.Kind] = _map.Copy();
            _map = GraphMap.Default(kind);
            // custom families are variable-length, so their memory cannot be
            // validated against the default's length - any even point list works
            if (_memory.TryGetValue(kind, out var stored)
                && (stored.P.Length == _map.P.Length
                    || (_map.IsCustom && stored.P.Length >= 4 && stored.P.Length % 2 == 0)))
            {
                _map.P = (double[])stored.P.Clone();
                _map.H = (double[])stored.H.Clone();
            }
            SyncEditor();
            ExpireSolution(true);
        }

        void SyncEditor()
        {
            _editor.Map = _map;
            _editor.DragStarted = () => RecordUndoEvent("Lugworm: graph edit");
            _editor.Changed = () => ExpireSolution(true);
            _editor.ResetRequested = () =>
            {
                RecordUndoEvent("Lugworm: graph reset");
                _map = GraphMap.Default(_map.Kind);
                _memory.Remove(_map.Kind);
                SyncEditor();
                ExpireSolution(true);
            };
            _editor.ReseedRequested = () =>
            {
                RecordUndoEvent("Lugworm: graph reseed");
                _map.P[0] = Math.Round(_map.P[0]) + 1;
                ExpireSolution(true);
            };
        }

        sealed class Result
        {
            public List<double> Y = new();
            public string Equation = "", Info = "", Map = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            SyncEditor();
            var values = GetNumberList(DA, "t");
            double inMin = GetNumber(DA, "inMin", 0), inMax = GetNumber(DA, "inMax", 1);
            double outMin = GetNumber(DA, "outMin", 0), outMax = GetNumber(DA, "outMax", 1);
            bool clip = GetBool(DA, "clip", true);
            if (!(inMax > inMin))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The input domain is empty or inverted, so every value maps through t = 0.5");

            var res = new Result();
            foreach (double v in values)
                res.Y.Add(_map.Map(v, inMin, inMax, outMin, outMax, clip));
            res.Equation = _map.Describe();
            // the curve as data: Slice deserializes this and evaluates f(t) itself, so the
            // infill resolution is the slicer's business, not a sampling count fixed here
            res.Map = _map.Serialize();
            res.Info = $"{_map.Kind} - {values.Count} value(s) - in [{inMin:0.###}, {inMax:0.###}] → out [{outMin:0.###}, {outMax:0.###}]"
                     + (clip ? "" : " - unclipped");

            // the wired data as axis ticks on the editor (capped - ticks are a
            // reading aid, not a histogram)
            const int cap = 200;
            int stride = Math.Max(1, values.Count / cap);
            _editor.TicksT = values.Where((_, i) => i % stride == 0)
                .Select(v => inMax > inMin ? (v - inMin) / (inMax - inMin) : 0.5).ToArray();
            _editor.TicksY = res.Y.Where((_, i) => i % stride == 0)
                .Select(v => Math.Abs(outMax - outMin) > 1e-12 ? (v - outMin) / (outMax - outMin) : 0.5).ToArray();

            Message = _map.Kind.ToString();
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataList(0, r.Y);
            DA.SetData(1, r.Equation);
            DA.SetData(2, r.Info);
            DA.SetData(3, r.Map);
            Attributes?.ExpireLayout();
        }

        // ── icon: runtime-drawn (see icons-src/Lugworm/IamGraphMapper.svg for the
        // pipeline source; swap to IamIcons.Load once the PNG is generated) ──
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= DrawIcon();

        static Bitmap DrawIcon()
        {
            var bmp = new Bitmap(24, 24);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var frame = new Pen(Color.FromArgb(18, 51, 79), 1.6f))
                g.DrawRectangle(frame, 2, 2, 20, 20);
            using (var curve = new Pen(Color.FromArgb(40, 105, 159), 2.2f))
                g.DrawBezier(curve, 4, 19, 12, 19, 12, 5, 20, 5);
            using (var grip = new SolidBrush(Color.FromArgb(205, 228, 255)))
            using (var ring = new Pen(Color.FromArgb(18, 51, 79), 1f))
            {
                foreach (var (x, y) in new[] { (4, 19), (20, 5) })
                {
                    g.FillEllipse(grip, x - 2.5f, y - 2.5f, 5, 5);
                    g.DrawEllipse(ring, x - 2.5f, y - 2.5f, 5, 5);
                }
            }
            return bmp;
        }

        // ── persistence: the curve and the per-family memory ──

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString("iamGraphMap", _map.Serialize());
            writer.SetInt32("iamGraphMemCount", _memory.Count);
            int i = 0;
            foreach (var kv in _memory)
            {
                writer.SetString("iamGraphMem" + i, kv.Value.Serialize());
                i++;
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string map = "";
            if (reader.TryGetString("iamGraphMap", ref map) && map.Length > 0)
                try { _map = GraphMap.Deserialize(map); }
                catch (Exception ex)
                {
                    // The saved curve is the whole point of this component, so reverting to
                    // the default silently is the one thing this must not do. The default
                    // still stands in - nothing throws into the document load - but the
                    // archive notice survives the solve that would clear a plain message.
                    AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                        "The saved graph curve could not be read (" + ex.Message
                        + "). This component reverted to its default curve. Edit it again before printing");
                }
            _memory.Clear();
            int count = 0;
            int lostFamilies = 0;
            if (reader.TryGetInt32("iamGraphMemCount", ref count))
                for (int i = 0; i < count; i++)
                {
                    string entry = "";
                    if (!reader.TryGetString("iamGraphMem" + i, ref entry)) continue;
                    try
                    {
                        var m = GraphMap.Deserialize(entry);
                        _memory[m.Kind] = m;
                    }
                    catch (Exception) { lostFamilies++; /* skip a bad entry, keep the rest */ }
                }
            if (lostFamilies > 0)
                // The remembered per-family curves are a convenience, not the live one, so
                // this is a remark rather than a warning - but a curve that quietly stopped
                // coming back when the operator switches families is worth one line.
                AddArchiveNotice(GH_RuntimeMessageLevel.Remark,
                    lostFamilies + " remembered graph curve(s) could not be read and were dropped. "
                    + "Their families start from the default again");
            SyncEditor();
            return base.Read(reader);
        }
    }
}
