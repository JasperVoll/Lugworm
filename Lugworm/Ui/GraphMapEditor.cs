using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;

namespace Lugworm.Ui
{
    /// <summary>The Graph Mapper's on-canvas editor: the stock Graph Mapper's
    /// job (drag grips, shape the curve) with the mechanics it lacks -
    /// hover-highlighted handles, a live numeric readout while dragging, grid
    /// snapping (Ctrl = 0.05, Shift = 0.01), the wired data drawn as ticks on both
    /// axes, an equation line, undo via the host's drag-start hook, and a reset
    /// box. Owns no data: the component owns the <see cref="GraphMap"/>; this class
    /// turns mouse gestures into parameter changes on it.</summary>
    public sealed class GraphMapEditor : ICanvasStrip
    {
        // roughly the stock Graph Mapper's footprint - half the old panel width
        public int StripHeight => 132;
        public int StripWidth => 190;

        public GraphMap Map = GraphMap.Default(GraphMapKind.Linear);
        /// <summary>Wired input values normalized to the input domain (ticks on the
        /// t axis) and their mapped outputs normalized to the output domain (ticks
        /// on the y axis). Capped by the component; may be empty.</summary>
        public double[] TicksT = Array.Empty<double>();
        public double[] TicksY = Array.Empty<double>();
        public Action? DragStarted;      // grip grabbed - host records the undo state
        public Action? Changed;          // drag finished - host expires the solution
        public Action? ResetRequested;   // ↺ box
        public Action? ReseedRequested;  // ⚄ box (Perlin only)

        sealed class Grip
        {
            public int Id;
            public double T, Y;          // unit-square position
            public string Label = "";
            /// <summary>-1 for an anchor/parameter grip; else the Spline point whose
            /// tangent handle this is, with <see cref="Side"/>.</summary>
            public int HandleOf = -1;
            public GraphHandleSide Side;
        }

        // handle grips take ids from HandleBase + 2×point (+1 for the out side), so
        // they never collide with the per-family anchor ids (0…63)
        const int HandleBase = 1000;
        const float HandleLenPx = 22f;

        int _dragId = -1, _hoverId = -1;
        // the Spline point whose handles are shown: the last anchor hovered or
        // dragged (Illustrator reveals handles for the selected anchor)
        int _focus = -1;
        bool _moved;
        RectangleF _plot, _reset, _reseed;

        static bool IsHandle(int id) => id >= HandleBase;
        static int HandlePoint(int id) => (id - HandleBase) / 2;
        static GraphHandleSide HandleSide(int id) => (GraphHandleSide)((id - HandleBase) % 2);
        static int HandleId(int point, GraphHandleSide side) => HandleBase + 2 * point + (int)side;

        // ── rendering ────────────────────────────────────────────────────────

        public void Render(Graphics g, RectangleF rect)
        {
            var t = IamPlot.Current();
            // background + frame come from the attributes' GH capsule (ICanvasStrip contract)
            IamPlot.Title(g, t, rect.X + 5, rect.Y + 3, Map.Kind.ToString()
                + (Map.IsCustom ? $" - {Map.PointCount} pts" : ""));
            _reset = new RectangleF(rect.Right - 17, rect.Y + 3, 12, 12);
            using (var p = new Pen(t.InkMuted)) g.DrawRectangle(p, _reset.X, _reset.Y, _reset.Width, _reset.Height);
            IamPlot.Small(g, t, _reset.X + 2f, _reset.Y, "↺", muted: true);
            if (Map.Kind == GraphMapKind.Perlin)
            {
                _reseed = new RectangleF(_reset.X - 16, rect.Y + 3, 12, 12);
                using var p = new Pen(t.InkMuted);
                g.DrawRectangle(p, _reseed.X, _reseed.Y, _reseed.Width, _reseed.Height);
                IamPlot.Small(g, t, _reseed.X + 1f, _reseed.Y, "⚄", muted: true);
            }
            else _reseed = RectangleF.Empty;

            var area = new Rectangle((int)rect.X + 6, (int)rect.Y + 16, (int)rect.Width - 14, (int)rect.Height - 33);
            _plot = IamPlot.Frame2D(g, t, area, "t", "y", 0, 1, 0, 1);

            // the wired data as ticks: t values on the bottom edge, mapped outputs
            // on the left edge - the stock mapper never shows what it maps
            using (var tick = new Pen(t.SeriesMuted))
            {
                foreach (double tt in TicksT)
                {
                    float x = (float)(_plot.X + Num.Clamp01(tt) * _plot.Width);
                    g.DrawLine(tick, x, _plot.Bottom - 4, x, _plot.Bottom - 1);
                }
                foreach (double ty in TicksY)
                {
                    float y = (float)(_plot.Bottom - Num.Clamp01(ty) * _plot.Height);
                    g.DrawLine(tick, _plot.X + 1, y, _plot.X + 4, y);
                }
            }

            // Bézier control polygon behind the curve, like every curve editor
            if (Map.Kind == GraphMapKind.Bezier)
                using (var dash = new Pen(t.InkMuted) { DashStyle = DashStyle.Dash })
                {
                    var pts = new[]
                    {
                        UnitPoint(0, Map.P[0]), UnitPoint(Map.P[1], Map.P[2]),
                        UnitPoint(Map.P[3], Map.P[4]), UnitPoint(1, Map.P[5]),
                    };
                    g.DrawLines(dash, pts);
                }

            using (var pen = new Pen(t.Series, 1.5f))
            {
                PointF? prev = null;
                const int n = 96;
                for (int i = 0; i <= n; i++)
                {
                    double x = i / (double)n;
                    var pt = UnitPoint(x, Map.Evaluate(x));
                    if (prev.HasValue) g.DrawLine(pen, prev.Value, pt);
                    prev = pt;
                }
            }

            var grips = Grips();
            // tangent handles of the focused Spline point: stems first, so the
            // anchor and the handle squares sit on top of them
            using (var stem = new Pen(t.InkMuted, 1f))
                foreach (var grip in grips)
                {
                    if (grip.HandleOf < 0) continue;
                    var a = UnitPoint(Map.P[2 * grip.HandleOf], Map.P[2 * grip.HandleOf + 1]);
                    g.DrawLine(stem, a, UnitPoint(grip.T, grip.Y));
                }

            foreach (var grip in grips)
            {
                var pt = UnitPoint(grip.T, grip.Y);
                bool active = grip.Id == _dragId, hot = grip.Id == _hoverId;
                if (grip.HandleOf >= 0)
                {
                    // square handle: filled when the user set it, hollow while it is
                    // still the automatic monotone tangent
                    bool user = Map.HasUserHandle(grip.HandleOf, grip.Side);
                    float s = active || hot ? 4f : 3f;
                    using var hb = new SolidBrush(user ? (active ? t.Accent : t.Series) : t.Panel);
                    g.FillRectangle(hb, pt.X - s, pt.Y - s, 2 * s, 2 * s);
                    using var hr = new Pen(active || hot ? t.Accent : t.Ink, active ? 1.4f : 0.8f);
                    g.DrawRectangle(hr, pt.X - s, pt.Y - s, 2 * s, 2 * s);
                    continue;
                }
                float r = active || hot ? 4.5f : 3.5f;
                using var b = new SolidBrush(active ? t.Accent : hot ? t.Warn : t.Series);
                g.FillEllipse(b, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                using var ring = new Pen(t.Ink, active ? 1.6f : 0.8f);
                g.DrawEllipse(ring, pt.X - r, pt.Y - r, 2 * r, 2 * r);
                if (hot && !active)
                    IamPlot.Small(g, t, pt.X + 6, pt.Y - 10, grip.Label, muted: true);
            }

            // readout: exact grip coordinates while dragging, the curve otherwise;
            // the double-click affordance shows on custom families
            string line = _dragId >= 0
                ? DragReadout()
                : Map.HasHandles
                    ? "2×click add/remove - drag handles, ⇧ breaks"
                    : Map.IsCustom
                        ? "2×click: add / remove point"
                        : IamPlot.Cut(Map.Describe(), 40);
            IamPlot.Small(g, t, rect.X + 5, rect.Bottom - 12, line, muted: _dragId < 0);
        }

        string DragReadout()
        {
            if (IsHandle(_dragId))
            {
                int i = HandlePoint(_dragId);
                var side = HandleSide(_dragId);
                double slope = Map.HandleSlope(i, side);
                bool broken = Map.HandleSlope(i, GraphHandleSide.In) != Map.HandleSlope(i, GraphHandleSide.Out);
                return $"point {i + 1} - {(side == GraphHandleSide.In ? "in" : "out")} slope {F(slope)}{(broken ? "  (corner)" : "")}";
            }
            foreach (var grip in Grips())
                if (grip.Id == _dragId)
                    return $"{grip.Label}:  t = {F(grip.T)}   y = {F(grip.Y)}";
            return "";
        }

        /// <summary>Unit-square position of a handle grip: a fixed screen length along
        /// the tangent direction, so a handle reads the same at every slope. The plot
        /// is not square, so the direction is formed in pixels and converted back.</summary>
        (double t, double y) HandleUnit(int point, GraphHandleSide side)
        {
            double m = Map.HandleSlope(point, side);
            double w = Math.Max(1, _plot.Width), h = Math.Max(1, _plot.Height);
            double dx = w, dy = -m * h;                       // screen vector of (1, m)
            double len = Math.Sqrt(dx * dx + dy * dy);
            dx /= len; dy /= len;
            if (side == GraphHandleSide.In) { dx = -dx; dy = -dy; }
            return (Map.P[2 * point] + HandleLenPx * dx / w, Map.P[2 * point + 1] - HandleLenPx * dy / h);
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        PointF UnitPoint(double t, double y)
            => new PointF(_plot.X + (float)(Num.Clamp01(t) * _plot.Width),
                          _plot.Bottom - (float)(Num.Clamp01(y) * _plot.Height));

        // ── grips per graph family ───────────────────────────────────────────

        List<Grip> Grips()
        {
            var p = Map.P;
            var grips = new List<Grip>();
            void Add(int id, double t, double y, string label)
                => grips.Add(new Grip { Id = id, T = t, Y = y, Label = label });
            switch (Map.Kind)
            {
                case GraphMapKind.Linear:
                    Add(0, 0, p[0], "start");
                    Add(1, 1, p[1], "end");
                    break;
                case GraphMapKind.Power:
                    Add(0, 0.5, Math.Pow(0.5, p[0]), "exponent");
                    break;
                case GraphMapKind.Sine:
                    {
                        double crest = Wrap01((0.25 - p[2]) / Math.Max(0.25, p[1]));
                        Add(0, crest, Num.Clamp01(0.5 + p[0]), "amplitude/phase");
                        Add(1, Num.Clamp01(crest + 0.5 / Math.Max(0.25, p[1])), Num.Clamp01(0.5 - p[0]), "frequency");
                        break;
                    }
                case GraphMapKind.Parabola:
                    {
                        Add(0, p[0], p[1], "vertex");
                        double te = p[0] >= 0.5 ? 0.0 : 1.0;
                        Add(1, te, Num.Clamp01(p[1] + p[2] * (te - p[0]) * (te - p[0])), "curvature");
                        break;
                    }
                case GraphMapKind.Gaussian:
                    Add(0, p[0], p[1], "peak");
                    Add(1, Num.Clamp01(p[0] + p[2]), Num.Clamp01(p[1] * Math.Exp(-0.5)), "width");
                    break;
                case GraphMapKind.Sigmoid:
                    Add(0, p[0], 0.5, "midpoint");
                    Add(1, Num.Clamp01(p[0] + Math.Log(9) / Math.Max(1, p[1])), 0.9, "steepness");
                    break;
                case GraphMapKind.Bezier:
                    Add(0, 0, p[0], "start");
                    Add(1, p[1], p[2], "control 1");
                    Add(2, p[3], p[4], "control 2");
                    Add(3, 1, p[5], "end");
                    break;
                case GraphMapKind.Perlin:
                    Add(0, 0.04, p[3], "base");
                    Add(1, 0.3, Num.Clamp01(p[3] + p[2]), "amplitude");
                    Add(2, Num.Clamp01(p[1] / 12.0), 0.04, "frequency");
                    break;
                case GraphMapKind.Conic:
                    Add(0, 0.5, Map.Evaluate(0.5), "eccentricity");
                    break;
                case GraphMapKind.Exponential:
                    Add(0, 0.5, Map.Evaluate(0.5), "curvature");
                    break;
                case GraphMapKind.Steps:
                    {
                        int n = (int)Math.Max(2, Math.Min(20, Math.Round(p[0])));
                        Add(0, 1.0 / n, 1.0 / (n - 1), "step count");
                        break;
                    }
                case GraphMapKind.Polyline:
                case GraphMapKind.Spline:
                    for (int i = 0; i < p.Length / 2; i++)
                        Add(i, p[2 * i], p[2 * i + 1], $"point {i + 1}");
                    break;
            }
            // the focused Spline point's tangent handles - listed last so hit-testing
            // (which walks the list backwards) prefers them over the anchor below
            int count = Map.PointCount;
            if (Map.HasHandles && _focus >= 0 && _focus < count)
            {
                if (_focus > 0)
                {
                    var (t, y) = HandleUnit(_focus, GraphHandleSide.In);
                    grips.Add(new Grip { Id = HandleId(_focus, GraphHandleSide.In), T = t, Y = y, Label = "in", HandleOf = _focus, Side = GraphHandleSide.In });
                }
                if (_focus < count - 1)
                {
                    var (t, y) = HandleUnit(_focus, GraphHandleSide.Out);
                    grips.Add(new Grip { Id = HandleId(_focus, GraphHandleSide.Out), T = t, Y = y, Label = "out", HandleOf = _focus, Side = GraphHandleSide.Out });
                }
            }
            return grips;
        }

        /// <summary>A handle drag sets the slope from the mouse's offset to its anchor
        /// (in unit-square terms, so the plot's aspect does not skew it). Plain drag
        /// keeps the point smooth (both sides follow), Shift moves one side only and
        /// makes a corner, Ctrl snaps the slope to quarters.</summary>
        void ApplyHandleDrag(PointF p)
        {
            int i = HandlePoint(_dragId);
            var side = HandleSide(_dragId);
            if (i < 0 || i >= Map.PointCount) return;
            var a = UnitPoint(Map.P[2 * i], Map.P[2 * i + 1]);
            double w = Math.Max(1, _plot.Width), h = Math.Max(1, _plot.Height);
            double dt = (p.X - a.X) / w, dy = (a.Y - p.Y) / h;
            const double minDt = 0.02;   // a handle never points backwards or straight up
            dt = side == GraphHandleSide.Out ? Math.Max(minDt, dt) : Math.Min(-minDt, dt);
            double slope = dy / dt;
            if ((Control.ModifierKeys & Keys.Control) != 0) slope = Math.Round(slope * 4) / 4;
            if ((Control.ModifierKeys & Keys.Shift) != 0) Map.SetHandle(i, side, slope);
            else Map.SetHandlePair(i, slope);
        }

        void ApplyDrag(int id, double t, double y)
        {
            var p = Map.P;
            switch (Map.Kind)
            {
                case GraphMapKind.Linear:
                    p[id] = y;
                    break;
                case GraphMapKind.Power:
                    p[0] = Math.Max(0.05, Math.Min(20, Math.Log(Math.Max(0.001, Math.Min(0.999, y))) / Math.Log(0.5)));
                    break;
                case GraphMapKind.Sine:
                    if (id == 0)
                    {
                        p[0] = Math.Max(-0.5, Math.Min(0.5, y - 0.5));
                        p[2] = Wrap01(0.25 - p[1] * t);
                    }
                    else
                    {
                        double crest = Wrap01((0.25 - p[2]) / Math.Max(0.25, p[1]));
                        double half = Math.Max(0.0625, t - crest);   // half period ≥ 1/16
                        p[1] = Math.Max(0.25, Math.Min(8, 0.5 / half));
                        p[2] = Wrap01(0.25 - p[1] * crest);          // crest stays put
                    }
                    break;
                case GraphMapKind.Parabola:
                    if (id == 0) { p[0] = t; p[1] = y; }
                    else
                    {
                        double te = p[0] >= 0.5 ? 0.0 : 1.0;
                        double d = Math.Max(0.05, Math.Abs(te - p[0]));
                        p[2] = (y - p[1]) / (d * d);
                    }
                    break;
                case GraphMapKind.Gaussian:
                    if (id == 0) { p[0] = t; p[1] = Math.Max(0.02, y); }
                    else p[2] = Math.Max(0.02, Math.Min(2, Math.Abs(t - p[0])));
                    break;
                case GraphMapKind.Sigmoid:
                    if (id == 0) p[0] = t;
                    else p[1] = Math.Max(1, Math.Min(60, Math.Log(9) / Math.Max(0.02, t - p[0])));
                    break;
                case GraphMapKind.Bezier:
                    if (id == 0) p[0] = y;
                    else if (id == 1) { p[1] = t; p[2] = y; }
                    else if (id == 2) { p[3] = t; p[4] = y; }
                    else p[5] = y;
                    break;
                case GraphMapKind.Perlin:
                    if (id == 0) p[3] = y;
                    else if (id == 1) p[2] = Math.Max(0.02, y - p[3]);
                    else p[1] = Math.Max(0.5, Math.Min(12, t * 12.0));
                    break;
                case GraphMapKind.Conic:
                    // invert y(0.5) = 1 − (1 − 0.5^a)^(1/a) for a by bisection -
                    // monotone in a over the allowed range
                    p[0] = SolveMonotone(a =>
                    {
                        double v = 1.0 - Math.Pow(1.0 - Math.Pow(0.5, a), 1.0 / a);
                        return v;
                    }, y, 0.3, 8, increasingIn: false);
                    break;
                case GraphMapKind.Exponential:
                    // invert y(0.5) = (e^(k/2) − 1)/(e^k − 1); decreasing in k
                    p[0] = SolveMonotone(k => Math.Abs(k) < 1e-6
                            ? 0.5 : (Math.Exp(k * 0.5) - 1) / (Math.Exp(k) - 1),
                        y, -10, 10, increasingIn: false);
                    break;
                case GraphMapKind.Steps:
                    // the grip sits on the first tread edge at t = 1/n
                    p[0] = Math.Max(2, Math.Min(20, Math.Round(1.0 / Math.Max(0.05, t))));
                    break;
                case GraphMapKind.Polyline:
                case GraphMapKind.Spline:
                    Map.MovePoint(id, t, y);
                    break;
            }
        }

        /// <summary>Bisection inverse of a monotone 1D map - the generic way a
        /// mid-curve grip drives a single shape parameter.</summary>
        static double SolveMonotone(Func<double, double> f, double target,
                                    double lo, double hi, bool increasingIn)
        {
            for (int i = 0; i < 48; i++)
            {
                double mid = 0.5 * (lo + hi);
                bool below = f(mid) < target;
                if (below == increasingIn) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        static double Wrap01(double v)
        {
            v %= 1.0;
            return v < 0 ? v + 1 : v;
        }

        // ── interaction ──────────────────────────────────────────────────────

        public bool MouseDown(PointF p)
        {
            _moved = false;
            if (Map.Kind == GraphMapKind.Perlin && !_reseed.IsEmpty && _reseed.Contains(p))
            { ReseedRequested?.Invoke(); return true; }
            if (_reset.Contains(p)) { ResetRequested?.Invoke(); return true; }
            int hit = HitGrip(p);
            if (hit >= 0)
            {
                _dragId = hit;
                if (Map.HasHandles) _focus = IsHandle(hit) ? HandlePoint(hit) : hit;
                DragStarted?.Invoke();
                return true;
            }
            return false;
        }

        public bool Dragging => _dragId >= 0;

        public bool MouseMove(PointF p)
        {
            if (_dragId < 0) return false;
            _moved = true;
            if (IsHandle(_dragId)) { ApplyHandleDrag(p); return true; }
            double t = Num.Clamp01(_plot.Width > 0 ? (p.X - _plot.X) / _plot.Width : 0);
            double y = Num.Clamp01(_plot.Height > 0 ? (_plot.Bottom - p.Y) / _plot.Height : 0);
            // grid snapping: Ctrl coarse, Shift fine - the stock mapper has neither
            double snap = (Control.ModifierKeys & Keys.Control) != 0 ? 0.05
                        : (Control.ModifierKeys & Keys.Shift) != 0 ? 0.01 : 0;
            if (snap > 0)
            {
                t = Math.Round(t / snap) * snap;
                y = Math.Round(y / snap) * snap;
            }
            ApplyDrag(_dragId, t, y);
            return true;
        }

        public bool MouseUp()
        {
            bool changed = _dragId >= 0 && _moved;
            _dragId = -1;
            _moved = false;
            if (changed) Changed?.Invoke();
            return changed;
        }

        public bool MouseHover(PointF p)
        {
            int hit = Dragging ? _hoverId : HitGrip(p);
            // hovering an anchor of a Spline reveals that point's handles; they stay
            // until another anchor is hovered, so the cursor can travel to them
            bool focusChanged = false;
            if (Map.HasHandles && hit >= 0 && !IsHandle(hit) && hit != _focus)
            { _focus = hit; focusChanged = true; }
            if (hit == _hoverId) return focusChanged;
            _hoverId = hit;
            return true;
        }

        /// <summary>Custom families only: double-click a grip removes its point
        /// (never below 2), double-click empty plot adds one at the cursor, and on
        /// a Spline double-click a tangent handle returns both of the point's handles
        /// to automatic. All record an undo snapshot via <see cref="DragStarted"/> first.</summary>
        public bool MouseDoubleClick(PointF p)
        {
            if (!Map.IsCustom) return false;
            // the double-click's own first press may have armed a drag - disarm it,
            // the gesture is add/remove, not move
            _dragId = -1;
            _moved = false;
            int hit = HitGrip(p);
            if (hit >= 0 && IsHandle(hit))
            {
                int i = HandlePoint(hit);
                if (Map.HasUserHandle(i, GraphHandleSide.In) || Map.HasUserHandle(i, GraphHandleSide.Out))
                {
                    DragStarted?.Invoke();
                    Map.ResetHandle(i);
                    Changed?.Invoke();
                }
                return true;   // consumed either way - never falls through to add a point
            }
            if (hit >= 0)
            {
                if (Map.PointCount <= 2) return true;   // consumed; floor of 2 points
                DragStarted?.Invoke();
                if (Map.RemovePoint(hit)) Changed?.Invoke();
                _hoverId = -1;
                _focus = -1;
                return true;
            }
            if (!_plot.Contains(p)) return false;
            double t = Num.Clamp01(_plot.Width > 0 ? (p.X - _plot.X) / _plot.Width : 0);
            double y = Num.Clamp01(_plot.Height > 0 ? (_plot.Bottom - p.Y) / _plot.Height : 0);
            DragStarted?.Invoke();
            Map.AddPoint(t, y);
            Changed?.Invoke();
            return true;
        }

        int HitGrip(PointF p)
        {
            // backwards: handle grips are listed last and must win over the anchor
            // they sit next to
            var grips = Grips();
            for (int k = grips.Count - 1; k >= 0; k--)
            {
                var grip = grips[k];
                var pt = UnitPoint(grip.T, grip.Y);
                if (Math.Abs(pt.X - p.X) <= 7 && Math.Abs(pt.Y - p.Y) <= 7)
                    return grip.Id;
            }
            return -1;
        }
    }
}
