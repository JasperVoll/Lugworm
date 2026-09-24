using System;
using System.Drawing;
using System.Globalization;
using Lugworm.Core.Doe;

namespace Lugworm.Ui
{
    /// <summary>An interactive strip drawn below a component by IamAttributes -
    /// the shared contract of the goal-curve editor and the Graph Mapper. All
    /// coordinates are canvas coordinates; Render receives the strip rectangle
    /// each pass and must cache whatever geometry its hit-testing needs.</summary>
    public interface ICanvasStrip
    {
        int StripHeight { get; }
        /// <summary>Strip width in canvas units. The attributes centre a strip
        /// narrower than the graph card and align it with the card otherwise.</summary>
        int StripWidth { get; }
        /// <summary>Draw the strip content. The attributes have already rendered a
        /// Grasshopper capsule behind <paramref name="rect"/>, so the strip must
        /// NOT paint its own opaque background or outer frame.</summary>
        void Render(System.Drawing.Graphics g, RectangleF rect);
        /// <summary>True when the press was consumed. When <see cref="Dragging"/>
        /// is true afterwards, the attributes capture the mouse.</summary>
        bool MouseDown(PointF p);
        bool Dragging { get; }
        /// <summary>True when the drag changed something (triggers a repaint).</summary>
        bool MouseMove(PointF p);
        /// <summary>Ends a drag; true when it changed something.</summary>
        bool MouseUp();
        /// <summary>Hover feedback without a drag; true when a repaint is needed.</summary>
        bool MouseHover(PointF p);
        /// <summary>Double-click (add/remove a point on custom curves); true when
        /// consumed.</summary>
        bool MouseDoubleClick(PointF p);
    }

    /// <summary>A component that hosts an on-canvas strip.
    /// Null hides the strip (e.g. before the first solve).</summary>
    public interface ICurveEditorHost
    {
        ICanvasStrip? CurveEditor { get; }
    }

    /// <summary>PROTOTYPE (2.3.0) - a Graph-Mapper-style editor for one desirability
    /// goal, drawn and edited directly inside the component: drag the ramp ends
    /// (Low/High), the target, or the curve's mid-point (shape exponent s) instead of
    /// typing "target=7 in 6..8 s=2". The edited goal is stored on the component as
    /// an override of the wired goal text and survives save/reopen; the ✕ box drops
    /// it. Limitations, stated as such: no undo integration (use ✕), the weight w=
    /// stays text-only, and the strip appears only after the first solve (it needs
    /// the model's data range for its axis).</summary>
    public sealed class GoalCurveEditor : ICanvasStrip
    {
        public int StripHeight => 96;
        public int StripWidth => IamPlot.PanelWidth;   // stacks under the profile card

        public string Title = "";
        public Goal? Goal;               // working copy - dragged in place
        public double YMin, YMax;        // horizontal axis span (response units)
        public bool Overridden;
        public Action? DragStarted;      // handle grabbed - host records the undo state
        public Action? Changed;          // drag finished - host stores + expires
        public Action? Cycle;            // click the title - next response
        public Action? Reset;            // click ✕ - drop the override

        enum Handle { None, Low, High, Target, Shape }
        Handle _drag = Handle.None;
        bool _moved;
        RectangleF _plot, _title, _reset;

        // Shape-handle probe: the curve point halfway along the rising ramp. Dragging
        // it vertically solves d = 0.5^s for s, i.e. s = ln(d)/ln(0.5), clamped.
        const double SMin = 0.1, SMax = 10.0;

        public void Render(Graphics g, RectangleF rect)
        {
            var t = IamPlot.Current();
            // background + frame come from the attributes' GH capsule (ICanvasStrip contract)
            _title = new RectangleF(rect.X + 3, rect.Y + 2, rect.Width - 40, 11);
            _reset = new RectangleF(rect.Right - 14, rect.Y + 2, 11, 11);
            string header = "goal ▸ " + Title + (Overridden ? "   [edited - ✕ resets]" : "   (drag to edit)");
            IamPlot.Title(g, t, _title.X, _title.Y, header);
            if (Overridden)
            {
                using var p = new Pen(t.Bad);
                g.DrawRectangle(p, _reset.X, _reset.Y, _reset.Width, _reset.Height);
                IamPlot.Small(g, t, _reset.X + 2f, _reset.Y, "✕");
            }

            var goal = Goal;
            var area = new Rectangle((int)rect.X + 6, (int)rect.Y + 15, (int)rect.Width - 14, (int)rect.Height - 32);
            _plot = IamPlot.Frame2D(g, t, area, "response", "d", YMin, YMax, 0, 1);
            if (goal == null || goal.Kind == GoalKind.None)
            {
                IamPlot.Small(g, t, _plot.X + 6, _plot.Y + 6, "goal 'none' - nothing to shape", muted: true);
                return;
            }

            // the curve
            using (var pen = new Pen(t.Series, 1.4f))
            {
                PointF? prev = null;
                const int n = 72;
                for (int i = 0; i <= n; i++)
                {
                    double y = YMin + (YMax - YMin) * i / n;
                    var pt = MapPoint(y, goal.D(y));
                    if (prev.HasValue) g.DrawLine(pen, prev.Value, pt);
                    prev = pt;
                }
            }

            foreach (var (h, y, d) in Handles(goal))
            {
                var pt = MapPoint(y, d);
                bool shape = h == Handle.Shape;
                using var b = new SolidBrush(shape ? t.Warn : t.Accent);
                g.FillEllipse(b, pt.X - 3.5f, pt.Y - 3.5f, 7f, 7f);
                using var ring = new Pen(t.Ink, _drag == h ? 1.6f : 0.8f);
                g.DrawEllipse(ring, pt.X - 3.5f, pt.Y - 3.5f, 7f, 7f);
            }

            string desc = goal.Describe() + $", s={goal.S.ToString("0.##", CultureInfo.InvariantCulture)}";
            IamPlot.Small(g, t, rect.X + 4, rect.Bottom - 11, IamPlot.Cut(desc, 66), muted: true);
        }

        /// <summary>Draggable handles for the current goal kind, as (id, y, d).</summary>
        (Handle h, double y, double d)[] Handles(Goal goal)
        {
            switch (goal.Kind)
            {
                case GoalKind.Maximize:
                    return new[]
                    {
                        (Handle.Low, goal.Low, 0.0), (Handle.High, goal.High, 1.0),
                        (Handle.Shape, Mid(goal.Low, goal.High), goal.D(Mid(goal.Low, goal.High))),
                    };
                case GoalKind.Minimize:
                    return new[]
                    {
                        (Handle.Low, goal.Low, 1.0), (Handle.High, goal.High, 0.0),
                        (Handle.Shape, Mid(goal.Low, goal.High), goal.D(Mid(goal.Low, goal.High))),
                    };
                case GoalKind.Target:
                    return new[]
                    {
                        (Handle.Low, goal.Low, 0.0), (Handle.Target, goal.Target, 1.0), (Handle.High, goal.High, 0.0),
                        (Handle.Shape, Mid(goal.Low, goal.Target), goal.D(Mid(goal.Low, goal.Target))),
                    };
                case GoalKind.Range:
                    return new[] { (Handle.Low, goal.Low, 1.0), (Handle.High, goal.High, 1.0) };
                default:
                    return Array.Empty<(Handle, double, double)>();
            }
        }

        static double Mid(double a, double b) => a + 0.5 * (b - a);

        PointF MapPoint(double y, double d)
            => IamPlot.Map(Rectangle.Round(_plot), y, YMin, YMax, d, 0, 1);

        double YAt(PointF p)
            => _plot.Width > 0 ? YMin + (p.X - _plot.X) / _plot.Width * (YMax - YMin) : YMin;

        double DAt(PointF p)
            => _plot.Height > 0 ? Math.Max(0.001, Math.Min(0.999, (_plot.Bottom - p.Y) / _plot.Height)) : 0.5;

        /// <summary>True when the press was consumed (drag started or a click zone
        /// hit). The caller captures the mouse while a drag is live.</summary>
        public bool MouseDown(PointF p)
        {
            _moved = false;
            if (_title.Contains(p)) { Cycle?.Invoke(); return true; }
            if (Overridden && _reset.Contains(p)) { Reset?.Invoke(); return true; }
            var goal = Goal;
            if (goal == null || goal.Kind == GoalKind.None) return false;
            foreach (var (h, y, d) in Handles(goal))
            {
                var pt = MapPoint(y, d);
                if (Math.Abs(pt.X - p.X) <= 6 && Math.Abs(pt.Y - p.Y) <= 6)
                {
                    _drag = h;
                    DragStarted?.Invoke();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Hover feedback is the Graph Mapper's refinement; the goal strip
        /// keeps its simpler always-visible handles.</summary>
        public bool MouseHover(PointF p) => false;

        /// <summary>The goal grammar has a fixed handle set - nothing to add.</summary>
        public bool MouseDoubleClick(PointF p) => false;

        public bool Dragging => _drag != Handle.None;

        public bool MouseMove(PointF p)
        {
            var goal = Goal;
            if (goal == null || _drag == Handle.None) return false;
            _moved = true;
            double y = YAt(p);
            switch (_drag)
            {
                case Handle.Low:
                    goal.Low = goal.Kind == GoalKind.Target
                        ? Math.Min(y, goal.Target - Eps())
                        : Math.Min(y, goal.High - Eps());
                    break;
                case Handle.High:
                    goal.High = goal.Kind == GoalKind.Target
                        ? Math.Max(y, goal.Target + Eps())
                        : Math.Max(y, goal.Low + Eps());
                    break;
                case Handle.Target:
                    goal.Target = Math.Max(goal.Low + Eps(), Math.Min(goal.High - Eps(), y));
                    break;
                case Handle.Shape:
                    goal.S = Math.Max(SMin, Math.Min(SMax, Math.Log(DAt(p)) / Math.Log(0.5)));
                    break;
            }
            return true;
        }

        double Eps() => 1e-6 * Math.Max(1.0, Math.Abs(YMax - YMin));

        /// <summary>True when a drag actually changed something (fires Changed).</summary>
        public bool MouseUp()
        {
            bool changed = _drag != Handle.None && _moved;
            _drag = Handle.None;
            _moved = false;
            if (changed) Changed?.Invoke();
            return changed;
        }

        /// <summary>The dragged goal as the same text grammar the goals input speaks -
        /// what the host stores as the override.</summary>
        public static string ToText(Goal g)
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.####", inv);
            string suffix = (Math.Abs(g.Weight - 1) > 1e-12 ? $" w={N(g.Weight)}" : "")
                          + (Math.Abs(g.S - 1) > 1e-12 ? $" s={N(g.S)}" : "");
            return g.Kind switch
            {
                GoalKind.Maximize => $"max {N(g.Low)}..{N(g.High)}{suffix}",
                GoalKind.Minimize => $"min {N(g.Low)}..{N(g.High)}{suffix}",
                GoalKind.Target => $"target={N(g.Target)} in {N(g.Low)}..{N(g.High)}{suffix}",
                GoalKind.Range => $"range={N(g.Low)}..{N(g.High)}{suffix}",
                _ => "none",
            };
        }
    }
}
