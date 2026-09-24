using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Doe;

namespace Lugworm.Ui
{
    /// <summary>What the selector plots: the displayed front (live or archived), the
    /// stored recipe, the candidate match, the optimum marker and the hover. Built by
    /// the component from Core state; the plot never decides anything.</summary>
    public sealed class ParetoPlotModel
    {
        public ParetoFrontSnapshot? Front;
        public bool Archived;
        public ParetoRecipe? Selected;
        /// <summary>Row of the displayed front that IS the selection (current), else -1.</summary>
        public int SelectedIndex = -1;
        /// <summary>Row that matches the stored recipe on a changed front, else -1.</summary>
        public int CandidateIndex = -1;
        /// <summary>Row marked as the optimum: -1 none, -2 ambiguous.</summary>
        public int OptimumIndex = -1;
        public string OptimumLabel = "";
        /// <summary>Row that scores best under the wired weights (the aggregation applied
        /// after the front, response goals only), else -1.</summary>
        public int WeightedIndex = -1;
        /// <summary>Legend text for the weighted marker, or why the weights were not applied.</summary>
        public string WeightedLabel = "";
        public int HoverIndex = -1;
        public string Status = "";
    }

    /// <summary>Screen geometry of the last render, for hit testing at the same mapping.</summary>
    public sealed class ParetoPlotGeometry
    {
        public int Kind;   // 0 invalid, 1 strip, 2 scatter, 3 parallel
        public PointF[] Points = Array.Empty<PointF>();          // kind 1, 2
        public PointF[][] Polylines = Array.Empty<PointF[]>();   // kind 3
        public RectangleF Plot;
    }

    /// <summary>Renders a Pareto front for graphical selection and answers "which recipe
    /// is under this point" with the same mapping, so the canvas strip and the window
    /// cannot disagree. k = 2: scatter on d0/d1; k &gt; 2: parallel coordinates over every
    /// active desirability axis; k = 1: a labelled one-axis strip. Axes are desirability
    /// (0-1), never raw response units. Markers: amber diamond = the optimizer's
    /// recommendation (or best D), green square = best under the wired weights, accent
    /// ring = the selection, dashed ring = a candidate on a changed front. Overlaps
    /// resolve deterministically: the nearest wins, a tie goes to the lowest branch index.</summary>
    public static class ParetoSelectionPlot
    {
        public const float HitRadius = 9f;

        public static ParetoPlotGeometry Render(Graphics g, RectangleF rect, IamPlot.Theme t, ParetoPlotModel m)
        {
            var geo = new ParetoPlotGeometry();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var front = m.Front;
            float top = rect.Y + 2;
            string title = front == null ? "Pareto front - nothing to show"
                         : (m.Archived ? "Pareto front (archived - current front unavailable)" : "Pareto front (non-dominated recipes)");
            IamPlot.Title(g, t, rect.X + 4, top, title);
            top += 13;
            if (front == null)
            {
                IamPlot.Small(g, t, rect.X + 4, top + 4, "wire Desirability's pareto output, factorNames and the active objective names", muted: true);
                if (m.Selected != null) IamPlot.Small(g, t, rect.X + 4, top + 16, "stored recipe: " + Recipe(m.Selected), muted: true);
                return geo;
            }
            int k = front.ObjectiveNames.Count;
            var area = new Rectangle((int)rect.X + 4, (int)top, (int)rect.Width - 10, (int)(rect.Bottom - top) - 26);
            if (k == 2) Scatter(g, t, m, front, area, geo);
            else if (k == 1) Strip(g, t, m, front, area, geo);
            else Parallel(g, t, m, front, area, geo);

            // status line + hover tooltip
            IamPlot.Small(g, t, rect.X + 4, rect.Bottom - 12, IamPlot.Cut(m.Status, 70), muted: true);
            if (m.HoverIndex >= 0 && m.HoverIndex < front.Rows.Count)
                Tooltip(g, t, rect, m.HoverIndex, front, geo);
            return geo;
        }

        static void Scatter(Graphics g, IamPlot.Theme t, ParetoPlotModel m, ParetoFrontSnapshot f, Rectangle area, ParetoPlotGeometry geo)
        {
            var plot = IamPlot.Frame2D(g, t, area, "d " + IamPlot.Cut(f.ObjectiveNames[0], 18), "d " + IamPlot.Cut(f.ObjectiveNames[1], 14), 0, 1, 0, 1);
            geo.Kind = 2; geo.Plot = plot;
            var pts = new PointF[f.Rows.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = IamPlot.Map(plot, f.Rows[i].Di[0], 0, 1, f.Rows[i].Di[1], 0, 1);
            geo.Points = pts;
            var order = Enumerable.Range(0, pts.Length).OrderBy(i => f.Rows[i].Di[0]).ToArray();
            using (var line = new Pen(t.SeriesMuted))
                for (int j = 1; j < order.Length; j++) g.DrawLine(line, pts[order[j - 1]], pts[order[j]]);
            for (int i = 0; i < pts.Length; i++) ReliabilityDot(g, t, pts[i], f.Rows[i], 2.5f);
            Marks(g, t, m, pts);
            Legend(g, t, m, f, plot);
        }

        static void Strip(Graphics g, IamPlot.Theme t, ParetoPlotModel m, ParetoFrontSnapshot f, Rectangle area, ParetoPlotGeometry geo)
        {
            var plot = new Rectangle(area.X + 16, area.Y + area.Height / 2 - 14, area.Width - 22, 28);
            using (var face = new SolidBrush(t.Bg)) g.FillRectangle(face, plot);
            using (var axis = new Pen(t.Axis)) g.DrawLine(axis, plot.X, plot.Y + plot.Height / 2, plot.Right, plot.Y + plot.Height / 2);
            IamPlot.Small(g, t, plot.X, plot.Bottom + 2, "0", muted: true);
            IamPlot.SmallRight(g, t, plot.Right, plot.Bottom + 2, "1", muted: true);
            IamPlot.Small(g, t, plot.X, plot.Y - 12, "d " + IamPlot.Cut(f.ObjectiveNames[0], 30));
            geo.Kind = 1; geo.Plot = plot;
            var pts = new PointF[f.Rows.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(plot.X + (float)(f.Rows[i].Di[0] * plot.Width), plot.Y + plot.Height / 2f);
            geo.Points = pts;
            for (int i = 0; i < pts.Length; i++) ReliabilityDot(g, t, pts[i], f.Rows[i], 3f);
            Marks(g, t, m, pts);
            Legend(g, t, m, f, plot);
        }

        static void Parallel(Graphics g, IamPlot.Theme t, ParetoPlotModel m, ParetoFrontSnapshot f, Rectangle area, ParetoPlotGeometry geo)
        {
            int k = f.ObjectiveNames.Count;
            if (k == 0)
            {
                IamPlot.Small(g, t, area.X, area.Y, "no active objective - nothing to select", muted: true);
                geo.Kind = 0;
                return;
            }
            var plot = new Rectangle(area.X + 18, area.Y + 14, area.Width - 26, area.Height - 30);
            using (var face = new SolidBrush(t.Bg)) g.FillRectangle(face, plot);
            geo.Kind = 3; geo.Plot = plot;
            float spacing = k > 1 ? plot.Width / (float)(k - 1) : 0;
            int cut = Math.Max(4, (int)(spacing / 5.5f));
            using (var axis = new Pen(t.Axis))
            using (var grid = new Pen(t.Grid))
            {
                for (int a = 0; a < k; a++)
                {
                    float x = k > 1 ? plot.X + a * spacing : plot.X + plot.Width / 2f;
                    g.DrawLine(axis, x, plot.Y, x, plot.Bottom);
                    for (int q = 1; q < 4; q++) g.DrawLine(grid, x - 2, plot.Y + plot.Height * q / 4f, x + 2, plot.Y + plot.Height * q / 4f);
                    string label = IamPlot.Cut(f.ObjectiveNames[a], cut);
                    float w = g.MeasureString(label, IamPlot.FSmall).Width;
                    float lx = Math.Max(area.X, Math.Min(area.Right - w, x - w / 2));
                    IamPlot.Small(g, t, lx, plot.Y - 12, label);
                }
            }
            IamPlot.Small(g, t, plot.X - 16, plot.Y - 3, "1", muted: true);
            IamPlot.Small(g, t, plot.X - 16, plot.Bottom - 9, "0", muted: true);
            var lines = new PointF[f.Rows.Count][];
            for (int i = 0; i < f.Rows.Count; i++)
            {
                var poly = new PointF[k];
                for (int a = 0; a < k; a++)
                {
                    float x = k > 1 ? plot.X + a * spacing : plot.X + plot.Width / 2f;
                    poly[a] = new PointF(x, plot.Bottom - (float)(f.Rows[i].Di[a] * plot.Height));
                }
                lines[i] = poly;
            }
            geo.Polylines = lines;
            using (var pen = new Pen(Color.FromArgb(150, t.Series)))
                for (int i = 0; i < lines.Length; i++) if (i != m.SelectedIndex && i != m.HoverIndex && i != m.CandidateIndex) DrawPoly(g, pen, lines[i]);
            if (m.CandidateIndex >= 0 && m.CandidateIndex < lines.Length)
                using (var pen = new Pen(t.Warn, 2f) { DashStyle = DashStyle.Dash }) DrawPoly(g, pen, lines[m.CandidateIndex]);
            if (m.HoverIndex >= 0 && m.HoverIndex < lines.Length && m.HoverIndex != m.SelectedIndex)
                using (var pen = new Pen(t.Ink, 2f)) DrawPoly(g, pen, lines[m.HoverIndex]);
            if (m.SelectedIndex >= 0 && m.SelectedIndex < lines.Length)
                using (var pen = new Pen(t.Accent, 2.6f)) DrawPoly(g, pen, lines[m.SelectedIndex]);
            if (m.OptimumIndex >= 0 && m.OptimumIndex < lines.Length)
                using (var b = new SolidBrush(t.Warn))
                    foreach (var p in lines[m.OptimumIndex]) g.FillPolygon(b, Diamond(p, 4.5f));
            if (m.WeightedIndex >= 0 && m.WeightedIndex < lines.Length)
                using (var pen = new Pen(t.Good, 1.8f))
                    foreach (var p in lines[m.WeightedIndex]) g.DrawRectangle(pen, p.X - 4.5f, p.Y - 4.5f, 9f, 9f);
            for (int i = 0; i < lines.Length; i++)
                foreach (var point in lines[i]) ReliabilityDot(g, t, point, f.Rows[i], 2.5f);
            Legend(g, t, m, f, plot);
        }

        static void ReliabilityDot(Graphics g, IamPlot.Theme t, PointF p, ParetoRecipe recipe, float radius)
        {
            if (recipe.Reliability?.ExtrapolationFlag == true)
            {
                using (var background = new SolidBrush(t.Bg)) g.FillEllipse(background, p.X - radius, p.Y - radius, radius * 2, radius * 2);
                using (var pen = new Pen(t.Series)) g.DrawEllipse(pen, p.X - radius, p.Y - radius, radius * 2, radius * 2);
            }
            else using (var dot = new SolidBrush(t.Series)) g.FillEllipse(dot, p.X - radius, p.Y - radius, radius * 2, radius * 2);
        }

        static void DrawPoly(Graphics g, Pen pen, PointF[] poly)
        {
            if (poly.Length == 1) { g.DrawEllipse(pen, poly[0].X - 3, poly[0].Y - 3, 6, 6); return; }
            g.DrawLines(pen, poly);
        }

        static void Marks(Graphics g, IamPlot.Theme t, ParetoPlotModel m, PointF[] pts)
        {
            if (m.OptimumIndex >= 0 && m.OptimumIndex < pts.Length)
                using (var b = new SolidBrush(t.Warn)) g.FillPolygon(b, Diamond(pts[m.OptimumIndex], 6f));
            if (m.WeightedIndex >= 0 && m.WeightedIndex < pts.Length)
                using (var pen = new Pen(t.Good, 1.8f))
                    g.DrawRectangle(pen, pts[m.WeightedIndex].X - 7f, pts[m.WeightedIndex].Y - 7f, 14f, 14f);
            if (m.CandidateIndex >= 0 && m.CandidateIndex < pts.Length)
                using (var pen = new Pen(t.Warn, 1.6f) { DashStyle = DashStyle.Dash })
                    g.DrawEllipse(pen, pts[m.CandidateIndex].X - 6f, pts[m.CandidateIndex].Y - 6f, 12f, 12f);
            if (m.HoverIndex >= 0 && m.HoverIndex < pts.Length)
                using (var pen = new Pen(t.Ink, 1.4f))
                    g.DrawEllipse(pen, pts[m.HoverIndex].X - 5f, pts[m.HoverIndex].Y - 5f, 10f, 10f);
            if (m.SelectedIndex >= 0 && m.SelectedIndex < pts.Length)
            {
                using (var pen = new Pen(t.Accent, 2f)) g.DrawEllipse(pen, pts[m.SelectedIndex].X - 6f, pts[m.SelectedIndex].Y - 6f, 12f, 12f);
                using (var b = new SolidBrush(t.Accent)) g.FillEllipse(b, pts[m.SelectedIndex].X - 3f, pts[m.SelectedIndex].Y - 3f, 6f, 6f);
            }
        }

        static void Legend(Graphics g, IamPlot.Theme t, ParetoPlotModel m, ParetoFrontSnapshot f, Rectangle plot)
        {
            var entries = new List<(Color, string)> { (t.Series, f.Rows.Count.ToString(CultureInfo.InvariantCulture) + " recipes") };
            if (m.SelectedIndex >= 0) entries.Add((t.Accent, "selected"));
            else if (m.Selected != null) entries.Add((t.Warn, m.CandidateIndex >= 0 ? "stored recipe (candidate, dashed)" : "stored recipe not on this front"));
            if (m.OptimumIndex >= 0) entries.Add((t.Warn, m.OptimumLabel));
            else if (m.OptimumIndex == -2) entries.Add((t.Warn, "optimum ambiguous"));
            else if (m.OptimumLabel.Length > 0) entries.Add((t.InkMuted, m.OptimumLabel));
            if (m.WeightedIndex >= 0) entries.Add((t.Good, m.WeightedLabel));
            else if (m.WeightedLabel.Length > 0) entries.Add((t.InkMuted, m.WeightedLabel));
            IamPlot.Legend(g, t, plot.X + 2, plot.Y + 2, entries.ToArray());
        }

        static PointF[] Diamond(PointF c, float r)
            => new[] { new PointF(c.X, c.Y - r), new PointF(c.X + r, c.Y), new PointF(c.X, c.Y + r), new PointF(c.X - r, c.Y) };

        static void Tooltip(Graphics g, IamPlot.Theme t, RectangleF rect, int index, ParetoFrontSnapshot f, ParetoPlotGeometry geo)
        {
            string text = "#" + index.ToString(CultureInfo.InvariantCulture) + "  " + Recipe(f.Rows[index]);
            var size = g.MeasureString(text, IamPlot.FSmall);
            PointF anchor = geo.Kind == 3 && geo.Polylines.Length > index && geo.Polylines[index].Length > 0
                ? geo.Polylines[index][0] : geo.Points.Length > index ? geo.Points[index] : new PointF(rect.X, rect.Y);
            float x = Math.Max(rect.X + 2, Math.Min(rect.Right - size.Width - 4, anchor.X + 8));
            float y = anchor.Y - size.Height - 6 < rect.Y + 14 ? anchor.Y + 8 : anchor.Y - size.Height - 6;
            using (var bg = new SolidBrush(Color.FromArgb(235, t.Panel))) g.FillRectangle(bg, x - 2, y - 1, size.Width + 4, size.Height + 2);
            using (var frame = new Pen(t.Frame)) g.DrawRectangle(frame, x - 2, y - 1, size.Width + 4, size.Height + 2);
            IamPlot.Small(g, t, x, y, text);
        }

        /// <summary>One-line factor recipe + desirabilities + D, invariant.</summary>
        public static string Recipe(ParetoRecipe r)
        {
            var parts = new List<string>();
            for (int i = 0; i < r.X.Count; i++) parts.Add(IamPlot.Cut(r.FactorNames[i], 10) + "=" + IamPlot.Fmt(r.X[i]));
            var ds = new List<string>();
            for (int i = 0; i < r.Di.Count; i++) ds.Add("d" + (i + 1).ToString(CultureInfo.InvariantCulture) + "=" + r.Di[i].ToString("0.00", CultureInfo.InvariantCulture));
            return string.Join(" ", parts) + "  |  " + string.Join(" ", ds) + "  D=" + r.D.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>Nearest recipe to <paramref name="p"/> within <see cref="HitRadius"/>;
        /// ties go to the lowest branch index. -1 when nothing is near.</summary>
        public static int HitTest(ParetoPlotGeometry geo, PointF p)
        {
            if (geo == null) return -1;
            int best = -1; double bestD = HitRadius;
            if (geo.Kind == 1 || geo.Kind == 2)
            {
                for (int i = 0; i < geo.Points.Length; i++)
                {
                    double d = Dist(geo.Points[i], p);
                    if (d < bestD - 1e-9) { bestD = d; best = i; }
                }
                return best;
            }
            if (geo.Kind == 3)
            {
                for (int i = 0; i < geo.Polylines.Length; i++)
                {
                    double d = SegmentDistance(geo.Polylines[i], p);
                    if (d < bestD - 1e-9) { bestD = d; best = i; }
                }
            }
            return best;
        }

        static double Dist(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        static double SegmentDistance(PointF[] poly, PointF p)
        {
            if (poly.Length == 1) return Dist(poly[0], p);
            double best = double.PositiveInfinity;
            for (int i = 1; i < poly.Length; i++)
            {
                var a = poly[i - 1]; var b = poly[i];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len2 = dx * dx + dy * dy;
                double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
                double qx = a.X + t * dx, qy = a.Y + t * dy;
                best = Math.Min(best, Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy)));
            }
            return best;
        }
    }

    /// <summary>The on-canvas picker: the plot as an interactive strip under the
    /// component. A click selects the nearest recipe (reported to the host, which owns
    /// undo and expiry), hover previews it, a double-click opens the larger window.</summary>
    public sealed class ParetoSelectionStrip : ICanvasStrip
    {
        public int StripHeight => 176;
        public int StripWidth => IamPlot.PanelWidth;

        public ParetoPlotModel Model = new ParetoPlotModel();
        public Action<int>? Selected;
        public Action? OpenWindow;
        ParetoPlotGeometry _geo = new ParetoPlotGeometry();
        RectangleF _rect;

        public void Render(Graphics g, RectangleF rect)
        {
            _rect = rect;
            var t = IamPlot.Current();
            _geo = ParetoSelectionPlot.Render(g, rect, t, Model);
        }

        public bool MouseDown(PointF p)
        {
            if (!_rect.Contains(p)) return false;
            int hit = ParetoSelectionPlot.HitTest(_geo, p);
            if (hit >= 0) Selected?.Invoke(hit);
            return true;   // consumed either way: a miss must not start dragging the component
        }

        public bool Dragging => false;
        public bool MouseMove(PointF p) => false;
        public bool MouseUp() => false;

        public bool MouseHover(PointF p)
        {
            int hit = _rect.Contains(p) ? ParetoSelectionPlot.HitTest(_geo, p) : -1;
            if (hit == Model.HoverIndex) return false;
            Model.HoverIndex = hit;
            return true;
        }

        public bool MouseDoubleClick(PointF p)
        {
            if (!_rect.Contains(p)) return false;
            OpenWindow?.Invoke();
            return true;
        }
    }
}
