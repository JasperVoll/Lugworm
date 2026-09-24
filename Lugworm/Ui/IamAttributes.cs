using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace Lugworm.Ui
{
    /// <summary>A component that hosts an on-canvas Compute button.</summary>
    public interface IComputeButtonHost
    {
        bool AutoCompute { get; }
        bool CacheStale { get; }
        string ButtonLabel { get; }
        void OnComputePressed();
    }

    /// <summary>Optional on-canvas dropdown (e.g. the test-geometry shape picker).</summary>
    public interface IDropdownHost
    {
        string DropdownLabel { get; }
        void FillDropdown(ToolStripDropDown menu);
    }

    /// <summary>Optional on-canvas graph panel (RSM / desirability plots).</summary>
    public interface IGraphHost
    {
        /// <summary>Current plot, or null to hide the panel.</summary>
        Bitmap? GraphImage { get; }
    }

    /// <summary>One on-canvas action capsule (jog arrow, Connect, E-STOP, …).</summary>
    public sealed class ButtonDef
    {
        public readonly string Id;
        public readonly string Label;
        public readonly GH_Palette Palette;
        public readonly bool Enabled;
        public readonly string? Tooltip;
        public ButtonDef(string id, string label, GH_Palette palette = GH_Palette.Black,
                         bool enabled = true, string? tooltip = null)
        { Id = id; Label = label; Palette = palette; Enabled = enabled; Tooltip = tooltip; }
    }

    /// <summary>Optional rows of action capsules below the component (Duet control).
    /// Rows are re-queried every layout/render pass so labels and enabled states can
    /// follow the machine state. Presses arrive on the UI thread.</summary>
    public interface IButtonRowHost
    {
        IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows { get; }
        void OnCapsulePressed(string id);
    }

    /// <summary>Shared attributes for all Lugworm components. Bottom strip:
    /// a "+ inputs" button that wires sliders / toggles / value lists / panels to all
    /// visible unconnected inputs, plus (Heavy components only) the Compute button -
    /// grey in Auto mode, amber when the cache is stale, black otherwise. Components
    /// may additionally expose a dropdown row (IDropdownHost) or a graph panel below
    /// the buttons (IGraphHost).</summary>
    public class IamAttributes : GH_ComponentAttributes
    {
        const int BtnH = 22;
        const int WireH = 16;
        const int DropH = 18;
        const int GraphPad = 3;
        const int RowH = 20;

        RectangleF _compute, _wire, _drop, _graph, _curve;
        readonly List<(RectangleF rect, ButtonDef def)> _buttons = new List<(RectangleF, ButtonDef)>();
        readonly IamComponentBase _owner;
        /// <summary>Bounds union the canvas must hit-test against. A wide button row is
        /// deliberately allowed to overflow <see cref="GH_Attributes{T}.Bounds"/> (see
        /// Layout) so long labels are not truncated and the output grips keep their
        /// positions - but Grasshopper routes clicks by pick region, so without this the
        /// overflowing capsules are drawn and cannot be pressed.</summary>
        RectangleF _pickBounds;

        public IamAttributes(IamComponentBase owner) : base(owner)
        {
            _owner = owner;
        }

        IComputeButtonHost Host => _owner;
        IDropdownHost? Drop => _owner as IDropdownHost;
        Bitmap? Graph => (_owner as IGraphHost)?.GraphImage;
        IButtonRowHost? Rows => _owner as IButtonRowHost;
        ICanvasStrip? Curve => (_owner as ICurveEditorHost)?.CurveEditor;

        protected override void Layout()
        {
            base.Layout();
            var b = Bounds;
            var img = Graph;

            if (Drop != null)
            {
                b.Height += DropH;
                _drop = new RectangleF(b.Left + 2, b.Bottom - DropH, b.Width - 4, DropH - 2);
            }

            if (_owner.IsHeavy)
            {
                b.Height += BtnH;
                _compute = new RectangleF(b.Left + 2, b.Bottom - BtnH, b.Width - 4 - 26, BtnH - 2);
                _wire = new RectangleF(_compute.Right + 2, b.Bottom - BtnH, 24, BtnH - 2);
            }
            else
            {
                b.Height += WireH;
                _compute = RectangleF.Empty;
                _wire = new RectangleF(b.Left + 2, b.Bottom - WireH, b.Width - 4, WireH - 2);
            }

            // action-capsule rows (Duet control) between the strip and the graph
            _buttons.Clear();
            var rows = Rows?.ButtonRows;
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (row == null || row.Count == 0) continue;
                    b.Height += RowH;
                    float availableW = b.Width - 4;
                    float need = 0;
                    foreach (var def in row)
                        need += GH_FontServer.StringWidth(def.Label, GH_FontServer.Standard) + 12;
                    float rowW = Math.Min(Math.Max(availableW, need), b.Width * 3f);
                    float x0 = b.Left + 2 - (rowW - availableW) / 2f;
                    float w = rowW / row.Count;
                    for (int i = 0; i < row.Count; i++)
                    {
                        var r = new RectangleF(x0 + i * w, b.Bottom - RowH, w - 2, RowH - 2);
                        _buttons.Add((r, row[i]));
                    }
                }
            }

            if (img != null)
            {
                // graph card hangs below the capsule; it may overflow to the right so
                // output grips keep their positions (bounds width stays untouched)
                b.Height += img.Height + 2 * GraphPad;
                _graph = new RectangleF(b.Left + GraphPad, b.Bottom - img.Height - GraphPad,
                                        img.Width, img.Height);
            }
            else _graph = RectangleF.Empty;

            // interactive strip (goal-curve editor / Graph Mapper) below the graph.
            // A strip as wide as the graph card aligns with it (the DoE cards stack
            // as one column); a narrower one centres under the component like any
            // Grasshopper widget.
            var strip = Curve;
            if (strip != null)
            {
                b.Height += strip.StripHeight + 2 * GraphPad;
                float stripW = strip.StripWidth;
                float x = img != null && Math.Abs(stripW - img.Width) < 1
                    ? b.Left + GraphPad
                    : b.Left + (b.Width - stripW) / 2f;
                _curve = new RectangleF(x, b.Bottom - strip.StripHeight - GraphPad,
                    stripW, strip.StripHeight);
            }
            else _curve = RectangleF.Empty;

            Bounds = b;

            // A row wider than the component (rowW may reach 3x the bounds width) is
            // centred, so it spills past BOTH edges. Bounds stays narrow on purpose:
            // widening it would drag the parameter grips off the component body. The
            // canvas, however, only offers a click to the attributes whose pick region
            // contains the point, so the spilled capsules - "Home all" is the leftmost
            // and worst affected - were unreachable. Track the union separately and
            // report it from IsPickRegion.
            _pickBounds = b;
            foreach (var (rect, _) in _buttons) _pickBounds = RectangleF.Union(_pickBounds, rect);
            if (!_graph.IsEmpty) _pickBounds = RectangleF.Union(_pickBounds, _graph);
            if (!_curve.IsEmpty) _pickBounds = RectangleF.Union(_pickBounds, _curve);
        }

        /// <summary>Accepts clicks on capsules that overflow <see cref="Bounds"/>.
        /// Falls back to the base behaviour for everything inside the component body.</summary>
        public override bool IsPickRegion(PointF point)
            => _pickBounds.Contains(point) || base.IsPickRegion(point);

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects) return;

            if (Drop != null)
            {
                var cap = GH_Capsule.CreateTextCapsule(_drop, _drop, GH_Palette.White,
                    "▾ " + Drop.DropdownLabel, 2, 0);
                cap.Render(graphics, Selected, Owner.Locked, false);
                cap.Dispose();
            }

            if (_owner.IsHeavy)
            {
                GH_Palette palette = Host.AutoCompute
                    ? GH_Palette.Grey
                    : (Host.CacheStale ? GH_Palette.Warning : GH_Palette.Black);
                var cap = GH_Capsule.CreateTextCapsule(_compute, _compute, palette, Host.ButtonLabel, 2, 0);
                cap.Render(graphics, Selected, Owner.Locked, false);
                cap.Dispose();
            }

            var wireCap = GH_Capsule.CreateTextCapsule(_wire, _wire, GH_Palette.Grey,
                _owner.IsHeavy ? "⇤" : "⇤ inputs", 1, 0);
            wireCap.Render(graphics, Selected, Owner.Locked, false);
            wireCap.Dispose();

            foreach (var (rect, def) in _buttons)
            {
                var cap = GH_Capsule.CreateTextCapsule(rect, rect,
                    def.Enabled ? def.Palette : GH_Palette.Locked, def.Label, 1, 0);
                cap.Render(graphics, Selected, Owner.Locked || !def.Enabled, false);
                cap.Dispose();
            }

            var img = Graph;
            if (img != null && !_graph.IsEmpty)
            {
                // the mat behind the panel follows the plot theme, so a dark-canvas
                // panel is not framed by a white flash (the bitmap itself is rebuilt
                // theme-aware per Compute - see IamPlot.Current)
                var theme = IamPlot.Current();
                using (var mat = new SolidBrush(theme.Bg))
                    graphics.FillRectangle(mat, _graph.X - 1, _graph.Y - 1, _graph.Width + 2, _graph.Height + 2);
                graphics.DrawImage(img, _graph.X, _graph.Y, _graph.Width, _graph.Height);
                using (var frame = new Pen(theme.Frame))
                    graphics.DrawRectangle(frame, _graph.X - 1, _graph.Y - 1, _graph.Width + 2, _graph.Height + 2);
            }

            var curve = Curve;
            if (curve != null && !_curve.IsEmpty)
            {
                // Grasshopper-native face behind the strip: same rounded capsule,
                // shadow and skin behaviour as every other widget on the canvas
                var cap = GH_Capsule.CreateCapsule(_curve, GH_Palette.Normal, 4, 0);
                cap.Render(graphics, Selected, Owner.Locked, false);
                cap.Dispose();
                curve.Render(graphics, _curve);
            }
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            foreach (var (rect, def) in _buttons)
            {
                if (!rect.Contains(point)) continue;
                if (string.IsNullOrWhiteSpace(def.Tooltip)) return;
                e.Title = def.Label;
                e.Text = def.Tooltip;
                return;
            }
            base.SetupTooltip(point, e);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // goal-curve editor (prototype): a press on a handle starts a captured
                // drag; title/reset clicks are consumed without capture
                var curve = Curve;
                if (curve != null && !Owner.Locked && _curve.Contains(e.CanvasLocation)
                    && curve.MouseDown(e.CanvasLocation))
                    return curve.Dragging ? GH_ObjectResponse.Capture : GH_ObjectResponse.Handled;

                if (!_compute.IsEmpty && _compute.Contains(e.CanvasLocation))
                {
                    Host.OnComputePressed();
                    return GH_ObjectResponse.Handled;
                }
                if (_wire.Contains(e.CanvasLocation))
                {
                    // consults the document's settings folder first (SettingsRegistry);
                    // without one this is the built-in ParamSpec wiring as before
                    SettingsIo.WireInputs(_owner);
                    return GH_ObjectResponse.Handled;
                }
                if (Drop != null && _drop.Contains(e.CanvasLocation))
                {
                    var menu = new ContextMenuStrip();
                    Drop.FillDropdown(menu);
                    // dispose after close - a new strip is allocated per click and
                    // undisposed strips leak native handles over long sessions
                    menu.Closed += (s, args) => menu.BeginInvoke((System.Action)menu.Dispose);
                    menu.Show(canvas, canvas.PointToClient(Cursor.Position));
                    return GH_ObjectResponse.Handled;
                }
                if (!Owner.Locked && Rows != null)
                {
                    foreach (var (rect, def) in _buttons)
                    {
                        if (!rect.Contains(e.CanvasLocation)) continue;
                        if (def.Enabled) Rows.OnCapsulePressed(def.Id);
                        return GH_ObjectResponse.Handled;
                    }
                }
            }
            return base.RespondToMouseDown(canvas, e);
        }

        public override GH_ObjectResponse RespondToMouseMove(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            var curve = Curve;
            if (curve != null && curve.Dragging)
            {
                if (curve.MouseMove(e.CanvasLocation)) canvas.Refresh();
                return GH_ObjectResponse.Handled;
            }
            // hover feedback (handle highlighting) without consuming the event, so
            // component dragging and wire hovering behave exactly as before
            if (curve != null && !_curve.IsEmpty && curve.MouseHover(e.CanvasLocation))
                canvas.Refresh();
            return base.RespondToMouseMove(canvas, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            var curve = Curve;
            if (curve != null && curve.Dragging)
            {
                curve.MouseUp();   // fires Changed on the host when something moved
                return GH_ObjectResponse.Release;
            }
            return base.RespondToMouseUp(canvas, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            var curve = Curve;
            if (curve != null && !Owner.Locked && _curve.Contains(e.CanvasLocation)
                && curve.MouseDoubleClick(e.CanvasLocation))
            {
                canvas.Refresh();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDoubleClick(canvas, e);
        }
    }
}
