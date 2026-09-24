using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Lugworm.Ui
{
    /// <summary>The one visual system for every on-canvas DoE plot (effect bars,
    /// contours, desirability profile, Pareto scatter, and whatever comes next).
    /// One palette defined once, one type scale, shared axis/tick/legend/bar
    /// primitives - so the graphs read as a family instead of per-plot styling.
    /// Theme-aware: colours resolve against the Grasshopper canvas skin at build
    /// time, so panels stay legible on a dark canvas. Panels are bitmaps rebuilt
    /// per Compute; a mid-session theme switch refreshes on the next solve.</summary>
    public static class IamPlot
    {
        /// <summary>Semantic colours resolved for the current canvas theme.</summary>
        public sealed class Theme
        {
            public bool Dark;
            public Color Bg, Panel, Ink, InkMuted, Grid, Axis, Frame;
            public Color Good, Bad, Warn, Series, SeriesMuted, Accent;
        }

        /// <summary>Canvas skin luminance decides light vs dark. Grasshopper only
        /// exposes the skin on the UI thread that also runs Compute, so reading it
        /// here is safe; a failure (headless test host) falls back to light.</summary>
        public static Theme Current()
        {
            bool dark = false;
            try
            {
                var back = Grasshopper.GUI.Canvas.GH_Skin.canvas_back;
                dark = 0.299 * back.R + 0.587 * back.G + 0.114 * back.B < 110;
            }
            // GH_Skin is static Grasshopper canvas state that does not exist in a headless
            // test host; there is no theme to read and the light one below is correct.
            catch (Exception) { /* no canvas (tests) - light theme */ }
            return dark
                ? new Theme
                {
                    Dark = true,
                    Bg = Color.FromArgb(38, 38, 40),
                    Panel = Color.FromArgb(48, 48, 51),
                    Ink = Color.FromArgb(228, 226, 220),
                    InkMuted = Color.FromArgb(158, 156, 150),
                    Grid = Color.FromArgb(66, 66, 70),
                    Axis = Color.FromArgb(126, 124, 118),
                    Frame = Color.FromArgb(90, 90, 94),
                    Good = Color.FromArgb(96, 190, 125),
                    Bad = Color.FromArgb(232, 106, 94),
                    Warn = Color.FromArgb(224, 176, 82),
                    Series = Color.FromArgb(110, 168, 224),
                    SeriesMuted = Color.FromArgb(84, 118, 152),
                    Accent = Color.FromArgb(236, 132, 120),
                }
                : new Theme
                {
                    Dark = false,
                    Bg = Color.White,
                    Panel = Color.FromArgb(248, 248, 246),
                    Ink = Color.FromArgb(32, 32, 32),
                    InkMuted = Color.FromArgb(112, 112, 112),
                    Grid = Color.FromArgb(228, 228, 226),
                    Axis = Color.FromArgb(90, 90, 90),
                    Frame = Color.FromArgb(180, 180, 178),
                    Good = Color.FromArgb(26, 152, 80),
                    Bad = Color.FromArgb(215, 48, 39),
                    Warn = Color.FromArgb(196, 148, 32),
                    Series = Color.FromArgb(41, 128, 185),
                    SeriesMuted = Color.FromArgb(148, 184, 210),
                    Accent = Color.FromArgb(192, 57, 43),
                };
        }

        // one type scale for every panel; sizes chosen to stay legible at the
        // default canvas zoom without dwarfing the component body
        public static readonly Font FTitle = new Font("Arial", 7.5f, FontStyle.Bold);
        public static readonly Font FSmall = new Font("Arial", 6.5f);

        /// <summary>Standard panel width - every DoE graph shares it so stacked
        /// panels align on the canvas.</summary>
        public const int PanelWidth = 372;

        public static Bitmap NewPanel(Theme t, int height, out Graphics g)
        {
            var bmp = new Bitmap(PanelWidth, Math.Max(height, 24));
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(t.Bg);
            return bmp;
        }

        public static void Title(Graphics g, Theme t, float x, float y, string text)
        {
            using var b = new SolidBrush(t.Ink);
            g.DrawString(text, FTitle, b, x, y);
        }

        public static void Small(Graphics g, Theme t, float x, float y, string text, bool muted = false)
        {
            using var b = new SolidBrush(muted ? t.InkMuted : t.Ink);
            g.DrawString(text, FSmall, b, x, y);
        }

        public static void SmallRight(Graphics g, Theme t, float right, float y, string text, bool muted = false)
        {
            float w = g.MeasureString(text, FSmall).Width;
            Small(g, t, right - w, y, text, muted);
        }

        /// <summary>Frames a plot area, draws light gridlines plus min/max tick labels
        /// on both axes, and returns the inner rectangle data should be mapped into.
        /// Deliberately min/max-only ticks: at canvas size a full tick series turns
        /// into overlapping noise, and the DoE reader needs the range, not a ruler.</summary>
        public static Rectangle Frame2D(Graphics g, Theme t, Rectangle rc,
            string xLabel, string yLabel, double xMin, double xMax, double yMin, double yMax)
        {
            var plot = new Rectangle(rc.X + 16, rc.Y + 4, rc.Width - 22, rc.Height - 26);
            // a quiet panel fill so the plot area reads as its own surface - on the
            // bitmap panels and on a GH capsule face alike
            using (var face = new SolidBrush(t.Bg)) g.FillRectangle(face, plot);
            using (var grid = new Pen(t.Grid))
            {
                for (int i = 1; i < 4; i++)
                {
                    int gx = plot.X + plot.Width * i / 4;
                    int gy = plot.Y + plot.Height * i / 4;
                    g.DrawLine(grid, gx, plot.Y, gx, plot.Bottom);
                    g.DrawLine(grid, plot.X, gy, plot.Right, gy);
                }
            }
            using (var axis = new Pen(t.Axis)) g.DrawRectangle(axis, plot.X, plot.Y, plot.Width, plot.Height);

            Small(g, t, plot.X, plot.Bottom + 2, Fmt(xMin), muted: true);
            SmallRight(g, t, plot.Right, plot.Bottom + 2, Fmt(xMax), muted: true);
            float xlW = g.MeasureString(xLabel, FSmall).Width;
            Small(g, t, plot.X + (plot.Width - xlW) / 2, plot.Bottom + 12, xLabel);

            using (var sfV = new StringFormat(StringFormatFlags.DirectionVertical))
            using (var b = new SolidBrush(t.Ink))
            using (var bm = new SolidBrush(t.InkMuted))
            {
                g.DrawString(Fmt(yMax), FSmall, bm, rc.X - 2, plot.Y, sfV);
                g.DrawString(yLabel, FSmall, b, rc.X - 2, plot.Y + plot.Height / 3, sfV);
            }
            Small(g, t, plot.X + 1, plot.Bottom - 10, Fmt(yMin), muted: true);
            return plot;
        }

        public static PointF Map(Rectangle plot, double x, double xMin, double xMax,
                                 double y, double yMin, double yMax)
        {
            double sx = xMax > xMin ? (x - xMin) / (xMax - xMin) : 0.5;
            double sy = yMax > yMin ? (y - yMin) / (yMax - yMin) : 0.5;
            return new PointF(plot.X + (float)(sx * plot.Width),
                              plot.Bottom - (float)(sy * plot.Height));
        }

        /// <summary>One-line legend of coloured swatch + label pairs.</summary>
        public static void Legend(Graphics g, Theme t, float x, float y,
                                  params (Color colour, string label)[] entries)
        {
            foreach (var (colour, label) in entries)
            {
                using (var b = new SolidBrush(colour)) g.FillRectangle(b, x, y + 2, 7, 7);
                Small(g, t, x + 9, y, label, muted: true);
                x += 12 + g.MeasureString(label, FSmall).Width;
            }
        }

        /// <summary>Horizontal quantity bar with an outlined track - the shared shape
        /// of the effect bars and the desirability profile.</summary>
        public static void HBar(Graphics g, Theme t, Rectangle track, double fraction, Color fill,
                                bool outline = true)
        {
            if (outline) using (var p = new Pen(t.Frame)) g.DrawRectangle(p, track);
            int w = (int)(Math.Max(0, Math.Min(1, fraction)) * track.Width);
            using var b = new SolidBrush(fill);
            g.FillRectangle(b, track.X, track.Y, Math.Max(w, 1), track.Height);
        }

        /// <summary>Sequential low→high colormap for response contours (green → amber
        /// → red, the paper's RdYlGn_r reading: red = high response). Shared by every
        /// heat-mapped plot so colour keeps one meaning across the tab.</summary>
        public static Color Heat(Theme t, double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            Color lo = t.Dark ? Color.FromArgb(56, 142, 91) : Color.FromArgb(26, 152, 80);
            Color mid = t.Dark ? Color.FromArgb(214, 190, 110) : Color.FromArgb(254, 224, 139);
            Color hi = t.Dark ? Color.FromArgb(198, 84, 72) : Color.FromArgb(215, 48, 39);
            return v < 0.5 ? Lerp(lo, mid, v * 2) : Lerp(mid, hi, (v - 0.5) * 2);
        }

        /// <summary>Red (0) → green (1) for desirabilities.</summary>
        public static Color DGrad(Theme t, double d)
            => Lerp(t.Bad, t.Good, Math.Max(0, Math.Min(1, d)));

        public static Color Lerp(Color a, Color b, double v)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * v), (int)(a.G + (b.G - a.G) * v),
                              (int)(a.B + (b.B - a.B) * v));

        public static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "-";
            double a = Math.Abs(v);
            return a >= 1000 ? v.ToString("0", CultureInfo.InvariantCulture)
                 : a >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
                 : v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Cut(string s, int n = 12) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
