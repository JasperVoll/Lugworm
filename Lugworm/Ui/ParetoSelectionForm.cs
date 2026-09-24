using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Lugworm.Ui
{
    /// <summary>The modeless, larger Pareto picker. Draws the same plot with the same
    /// hit-test mapping as the canvas strip; a click or Enter reports a selection to the
    /// component (which owns undo and expiry), arrow keys walk the front, Escape closes.
    /// It never runs the optimizer and never solves anything itself.</summary>
    public sealed class ParetoSelectionForm : Form
    {
        readonly Action<int> _onSelected;
        readonly PlotPanel _panel;
        readonly Label _reliability;
        ParetoPlotModel _model = new ParetoPlotModel();
        int _keyboardIndex = -1;

        public ParetoSelectionForm(Action<int> onSelected)
        {
            _onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
            Text = "Lugworm Pareto Select";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(480, 360);
            ShowInTaskbar = true;
            KeyPreview = true;
            _panel = new PlotPanel(this) { Dock = DockStyle.Fill };
            _reliability = new Label { Dock = DockStyle.Bottom, Height = 30, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
            Controls.Add(_panel);
            Controls.Add(_reliability);
            KeyDown += OnKey;
        }

        /// <summary>Replaces the model (immutable snapshot from the component) and repaints.</summary>
        public void SetModel(ParetoPlotModel model)
        {
            _model = model ?? new ParetoPlotModel();
            _keyboardIndex = _model.SelectedIndex;
            UpdateTitle();
            UpdateReliability();
            _panel.Invalidate();
        }

        void UpdateTitle()
        {
            Text = "Lugworm Pareto Select - " + (_model.Front == null ? "no front" : _model.Front.Rows.Count.ToString(CultureInfo.InvariantCulture) + " recipes")
                 + (_model.Archived ? " (archived)" : "") + (_model.Status.Length > 0 ? " - " + _model.Status : "");
        }

        void UpdateReliability()
        {
            int index = _model.HoverIndex >= 0 ? _model.HoverIndex : _model.SelectedIndex;
            var recipe = _model.Front != null && index >= 0 && index < _model.Front.Rows.Count
                ? _model.Front.Rows[index] : _model.Selected;
            _reliability.Text = recipe?.ReliabilityStatus ?? "Hover over or select a recipe";
        }

        void OnKey(object? sender, KeyEventArgs e)
        {
            var front = _model.Front;
            if (front == null || front.Rows.Count == 0) return;
            if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                // walk the front in objective-1 order so the arrows move along the trade-off
                var order = Enumerable.Range(0, front.Rows.Count).OrderBy(i => front.Rows[i].Di[0]).ThenBy(i => i).ToArray();
                int pos = Array.IndexOf(order, _keyboardIndex);
                bool forward = e.KeyCode == Keys.Right || e.KeyCode == Keys.Up;
                pos = pos < 0 ? (forward ? 0 : order.Length - 1) : Math.Max(0, Math.Min(order.Length - 1, pos + (forward ? 1 : -1)));
                _keyboardIndex = order[pos];
                _model.HoverIndex = _keyboardIndex;
                UpdateReliability();
                _panel.Invalidate();
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && _keyboardIndex >= 0)
            {
                _onSelected(_keyboardIndex);   // keyboard confirmation goes through the same path as a click
                e.Handled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }

        sealed class PlotPanel : Panel
        {
            readonly ParetoSelectionForm _owner;
            ParetoPlotGeometry _geo = new ParetoPlotGeometry();

            public PlotPanel(ParetoSelectionForm owner)
            {
                _owner = owner;
                DoubleBuffered = true;
                ResizeRedraw = true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var t = IamPlot.Current();
                using (var bg = new SolidBrush(t.Panel)) e.Graphics.FillRectangle(bg, ClientRectangle);
                var rect = new RectangleF(8, 8, ClientSize.Width - 16, ClientSize.Height - 16);
                _geo = ParetoSelectionPlot.Render(e.Graphics, rect, t, _owner._model);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int hit = ParetoSelectionPlot.HitTest(_geo, e.Location);
                if (hit != _owner._model.HoverIndex)
                {
                    _owner._model.HoverIndex = hit;
                    _owner.UpdateReliability();
                    Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                if (_owner._model.HoverIndex >= 0) { _owner._model.HoverIndex = -1; _owner.UpdateReliability(); Invalidate(); }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button != MouseButtons.Left) return;
                int hit = ParetoSelectionPlot.HitTest(_geo, e.Location);
                if (hit >= 0) _owner._onSelected(hit);
            }
        }
    }
}
