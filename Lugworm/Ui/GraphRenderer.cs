using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Doe;

namespace Lugworm.Ui
{
    /// <summary>The DoE panels rendered straight onto the component canvas:
    /// per-response effect bars + response-surface contours (RSM component) and the
    /// desirability profile + optional Pareto scatter (Desirability component).
    /// Pure GDI+, no chart library; every colour, font and primitive comes from
    /// <see cref="IamPlot"/> so the panels read as one visual system on light and
    /// dark canvases alike.</summary>
    public static class GraphRenderer
    {
        const int W = IamPlot.PanelWidth;
        const int RowH = 118;

        /// <summary>One row per response: effect bars (left) and a contour of the two
        /// strongest factors (right, quadratic surface, remaining factors at centre).</summary>
        public static Bitmap RsmPanel(IReadOnlyList<RsmModel> models, int maxResponses = 6)
        {
            var t = IamPlot.Current();
            int n = Math.Min(models.Count, maxResponses);
            var bmp = IamPlot.NewPanel(t, RowH * Math.Max(n, 1) + (models.Count > n ? 12 : 0), out var g);
            using (g)
            {
                for (int i = 0; i < n; i++)
                {
                    var area = new Rectangle(0, i * RowH, W, RowH);
                    DrawEffectBars(g, t, models[i], new Rectangle(area.X + 4, area.Y + 2, (int)(W * 0.52) - 8, RowH - 6));
                    DrawContour(g, t, models[i], new Rectangle(area.X + (int)(W * 0.52) + 2, area.Y + 2, (int)(W * 0.48) - 8, RowH - 6));
                    if (i > 0) using (var p = new Pen(t.Grid)) g.DrawLine(p, 2, area.Y, W - 2, area.Y);
                }
                if (models.Count > n)
                    IamPlot.Small(g, t, 6, RowH * n, $"… {models.Count - n} more responses (see report output)", muted: true);
            }
            return bmp;
        }

        static void DrawEffectBars(Graphics g, IamPlot.Theme t, RsmModel m, Rectangle rc)
        {
            IamPlot.Title(g, t, rc.X, rc.Y,
                $"{m.ResponseName}   R²={(double.IsNaN(m.R2) ? "-" : m.R2.ToString("0.00", CultureInfo.InvariantCulture))}" +
                $"  adj={(double.IsNaN(m.R2Adj) ? "-" : m.R2Adj.ToString("0.00", CultureInfo.InvariantCulture))}");

            var terms = m.Terms.Where(tm => tm.Label != "1")
                               .OrderByDescending(tm => Math.Abs(tm.Coef)).ToList();
            if (terms.Count == 0) return;
            double maxAbs = terms.Max(tm => Math.Abs(tm.Coef));
            if (maxAbs <= 0) maxAbs = 1;

            int top = rc.Y + 14;
            int barH = Math.Max(6, Math.Min(13, (rc.Height - 26) / terms.Count - 2));
            int labelW = 26;
            int barMax = rc.Width - labelW - 44;
            int i = 0;
            for (; i < terms.Count; i++)
            {
                int y = top + i * (barH + 2);
                if (y + barH > rc.Bottom - 10) { IamPlot.Small(g, t, rc.X, y, "…", muted: true); break; }
                var tm = terms[i];
                bool sig = !double.IsNaN(tm.P) && tm.P < 0.05;
                IamPlot.Small(g, t, rc.X, y, tm.Label);
                IamPlot.HBar(g, t, new Rectangle(rc.X + labelW, y, (int)(Math.Abs(tm.Coef) / maxAbs * barMax), barH - 1),
                    1.0, sig ? t.Accent : t.Series, outline: false);
                string lbl = double.IsNaN(tm.P) ? IamPlot.Fmt(tm.Coef) : $"{IamPlot.Fmt(tm.Coef)}{RsmFitter.Stars(tm.P)}";
                IamPlot.Small(g, t, rc.X + labelW + (int)(Math.Abs(tm.Coef) / maxAbs * barMax) + 2, y, lbl, muted: true);
            }
            IamPlot.Legend(g, t, rc.X, rc.Bottom - 9,
                (t.Accent, "p < 0.05"), (t.Series, "n.s."));
        }

        static void DrawContour(Graphics g, IamPlot.Theme t, RsmModel m, Rectangle rc)
        {
            if (m.FactorCount < 2 || m.Terms.Count < 2)
            {
                IamPlot.Small(g, t, rc.X, rc.Y + 14, "n/a", muted: true);
                return;
            }
            // two strongest factors by summed |coef| over all terms involving them
            var strength = new double[m.FactorCount];
            foreach (var tm in m.Terms)
                for (int j = 0; j < m.FactorCount; j++)
                    if (tm.Powers[j] > 0) strength[j] += Math.Abs(tm.Coef);
            var order = Enumerable.Range(0, m.FactorCount).OrderByDescending(j => strength[j]).ToArray();
            int fx = Math.Min(order[0], order[1]), fy = Math.Max(order[0], order[1]);

            var plot = new Rectangle(rc.X + 14, rc.Y + 12, rc.Width - 20, rc.Height - 24);
            const int G = 36;
            var z = new double[G, G];
            double zMin = double.MaxValue, zMax = double.MinValue;
            var x = new double[m.FactorCount];
            for (int i = 0; i < G; i++)
                for (int j = 0; j < G; j++)
                {
                    for (int k = 0; k < m.FactorCount; k++) x[k] = 0;
                    x[fx] = -1 + 2.0 * i / (G - 1);
                    x[fy] = -1 + 2.0 * j / (G - 1);
                    double v = m.PredictCoded(x);
                    z[i, j] = v;
                    if (v < zMin) zMin = v;
                    if (v > zMax) zMax = v;
                }
            double span = zMax - zMin;
            if (span <= 0) span = 1;
            float cw = (float)plot.Width / G, ch = (float)plot.Height / G;
            for (int i = 0; i < G; i++)
                for (int j = 0; j < G; j++)
                {
                    using var b = new SolidBrush(IamPlot.Heat(t, (z[i, j] - zMin) / span));
                    g.FillRectangle(b, plot.X + i * cw, plot.Bottom - (j + 1) * ch, cw + 0.7f, ch + 0.7f);
                }
            // design points, in the ink colour so they read on both themes
            using (var ink = new SolidBrush(t.Ink))
                foreach (var row in m.XActual)
                {
                    var xc = m.ToCoded(row);
                    float px = plot.X + (float)((xc[fx] + 1) / 2 * plot.Width);
                    float py = plot.Bottom - (float)((xc[fy] + 1) / 2 * plot.Height);
                    g.FillEllipse(ink, px - 1.5f, py - 1.5f, 3f, 3f);
                }
            using (var axis = new Pen(t.Axis)) g.DrawRectangle(axis, plot.X, plot.Y, plot.Width, plot.Height);
            IamPlot.Small(g, t, plot.X, plot.Bottom + 1,
                $"{IamPlot.Cut(m.FactorNames[fx])} {IamPlot.Fmt(m.Lows[fx])}…{IamPlot.Fmt(m.Highs[fx])}");
            using (var sfV = new StringFormat(StringFormatFlags.DirectionVertical))
            using (var b = new SolidBrush(t.Ink))
                g.DrawString(IamPlot.Cut(m.FactorNames[fy]), IamPlot.FSmall, b, rc.X - 1, plot.Y, sfV);
            // colour key: low (green) left, high (red) right - one meaning everywhere
            using (var lo = new SolidBrush(IamPlot.Heat(t, 0)))
                g.DrawString(IamPlot.Fmt(zMin), IamPlot.FSmall, lo, plot.X, rc.Y);
            string hiS = IamPlot.Fmt(zMax);
            using (var hi = new SolidBrush(IamPlot.Heat(t, 1)))
                g.DrawString(hiS, IamPlot.FSmall, hi, plot.Right - g.MeasureString(hiS, IamPlot.FSmall).Width, rc.Y);
        }

        /// <summary>Desirability profile: one bar per response (and factor prior), the
        /// overall D bar, and - when the front was collected over exactly two
        /// objectives - the Pareto scatter underneath. More than two objectives get a
        /// text note instead of a plot: projecting k objectives onto two axes would
        /// lie about the trade-off, so nothing is drawn.</summary>
        public static Bitmap DesirabilityPanel(DesirabilityResult res, IReadOnlyList<RsmModel> models,
            IReadOnlyList<Goal>? factorGoals)
        {
            var t = IamPlot.Current();
            var rows = new List<(string label, double d, string extra)>();
            for (int i = 0; i < models.Count; i++)
                if (!double.IsNaN(res.DResponses[i]))
                    rows.Add((IamPlot.Cut(models[i].ResponseName, 20), res.DResponses[i], "ŷ=" + IamPlot.Fmt(res.YPred[i])));
            if (factorGoals != null && models.Count > 0)
                for (int j = 0; j < models[0].FactorCount && j < res.DFactors.Length; j++)
                    if (!double.IsNaN(res.DFactors[j]))
                        rows.Add(("prior " + IamPlot.Cut(models[0].FactorNames[j], 14), res.DFactors[j], ""));

            bool scatter = res.Front.Count > 0 && res.FrontObjectives.Length == 2;
            bool frontNote = res.Front.Count > 0 && res.FrontObjectives.Length > 2;
            int profileH = 20 + rows.Count * 15 + 22;
            int h = profileH + (scatter ? 132 : 0) + (frontNote ? 12 : 0);
            var bmp = IamPlot.NewPanel(t, h, out var g);
            using (g)
            {
                IamPlot.Title(g, t, 4, 2, "Desirability profile (Derringer–Suich)");
                int labelW = 118, barMax = W - labelW - 84;
                int y = 18;
                foreach (var (label, d, extra) in rows)
                {
                    IamPlot.Small(g, t, 4, y + 1, label);
                    IamPlot.HBar(g, t, new Rectangle(labelW, y, barMax, 11), d, IamPlot.DGrad(t, d));
                    IamPlot.Small(g, t, labelW + barMax + 3, y + 1,
                        $"{d.ToString("0.00", CultureInfo.InvariantCulture)}  {extra}", muted: true);
                    y += 15;
                }
                y += 3;
                IamPlot.Title(g, t, 4, y, "overall D");
                IamPlot.HBar(g, t, new Rectangle(labelW, y, barMax, 12), res.DOverall, IamPlot.DGrad(t, res.DOverall));
                IamPlot.Title(g, t, labelW + barMax + 3, y, res.DOverall.ToString("0.000", CultureInfo.InvariantCulture));
                y += 18;

                if (scatter) DrawFrontScatter(g, t, res, new Rectangle(4, y, W - 8, 126));
                else if (frontNote)
                    IamPlot.Small(g, t, 4, y,
                        $"front: {res.Front.Count} recipes over {res.FrontObjectives.Length} objectives - no 2D plot (see pareto output)",
                        muted: true);
            }
            return bmp;
        }

        static void DrawFrontScatter(Graphics g, IamPlot.Theme t, DesirabilityResult res, Rectangle rc)
        {
            IamPlot.Title(g, t, rc.X, rc.Y, "Pareto front (non-dominated recipes)");
            var area = new Rectangle(rc.X + 4, rc.Y + 12, rc.Width - 10, rc.Height - 24);
            var plot = IamPlot.Frame2D(g, t, area,
                "d " + IamPlot.Cut(res.FrontObjectives[0], 18),
                "d " + IamPlot.Cut(res.FrontObjectives[1], 14),
                0, 1, 0, 1);

            // the front, drawn as steps between points sorted along objective 1
            var pts = res.Front.OrderBy(p => p.D[0]).ToList();
            using (var line = new Pen(t.SeriesMuted))
                for (int i = 1; i < pts.Count; i++)
                {
                    var a = IamPlot.Map(plot, pts[i - 1].D[0], 0, 1, pts[i - 1].D[1], 0, 1);
                    var b = IamPlot.Map(plot, pts[i].D[0], 0, 1, pts[i].D[1], 0, 1);
                    g.DrawLine(line, a, b);
                }
            using (var dot = new SolidBrush(t.Series))
                foreach (var p in pts)
                {
                    var q = IamPlot.Map(plot, p.D[0], 0, 1, p.D[1], 0, 1);
                    g.FillEllipse(dot, q.X - 2f, q.Y - 2f, 4f, 4f);
                }
            // the D-optimum, marked so the chosen point is visible on the trade-off
            var bestOnFront = res.Front.FirstOrDefault(p =>
                p.X.Length == res.XBest.Length &&
                Enumerable.Range(0, p.X.Length).All(j => Math.Abs(p.X[j] - res.XBest[j]) < 1e-9));
            if (bestOnFront != null)
            {
                var q = IamPlot.Map(plot, bestOnFront.D[0], 0, 1, bestOnFront.D[1], 0, 1);
                using var accent = new Pen(t.Accent, 1.6f);
                g.DrawEllipse(accent, q.X - 4f, q.Y - 4f, 8f, 8f);
            }
            IamPlot.Legend(g, t, plot.X + 2, plot.Y + 2,
                (t.Series, $"{res.Front.Count} recipes"), (t.Accent, "D-optimum"));
        }
    }
}
