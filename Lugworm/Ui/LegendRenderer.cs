using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Lugworm.Core.Analysis;

namespace Lugworm.Ui
{
    /// <summary>Where a legend lives: anchored to the viewport (2D overlay, pixels),
    /// fixed to a plane in model space (model units), or floating - a camera-facing
    /// billboard at a point whose screen size stays constant (pixels).</summary>
    public enum LegendPlacement { Viewport = 0, Plane = 1, Floating = 2 }

    /// <summary>Viewport corners and edges for <see cref="LegendPlacement.Viewport"/>;
    /// for <see cref="LegendPlacement.Floating"/> the corner that sits on the point.</summary>
    public enum LegendAnchor { TopLeft = 0, TopRight = 1, BottomLeft = 2, BottomRight = 3, Top = 4, Bottom = 5, Left = 6, Right = 7 }

    public enum LegendOrientation { Horizontal = 0, Vertical = 1 }

    /// <summary>Presentation choices that are NOT part of the legend wire (a scale
    /// means the same thing wherever it is drawn): geometry, text and placement.
    /// Sizes are pixels for viewport/floating and model units for a plane.</summary>
    public sealed class LegendStyle
    {
        public LegendOrientation Orientation = LegendOrientation.Horizontal;
        public double Length = 220;
        public double Thickness = 14;
        public double TextSize = 11;
        public string Font = "Segoe UI";
        public Color TextColor = Color.FromArgb(50, 52, 56);
        public bool Frame;
        public Color FrameColor = Color.FromArgb(235, 246, 248, 250);
        public Color FrameEdge = Color.FromArgb(170, 170, 168);
        public double Margin = 18;
        public bool ShowTitle = true;
        public bool ShowTicks = true;
        public bool ShowUnavailable = true;
    }

    /// <summary>A legend laid out in its own 2D frame: x to the right, y UP, origin at
    /// the bottom-left of the content. Every placement maps this frame - the 2D
    /// overlay flips y, the plane and floating billboards use <c>Plane.PointAt</c>.
    /// Pure geometry: no Rhino display calls, so it can be reasoned about and the
    /// three backends cannot disagree about where a tick sits.</summary>
    public sealed class LegendLayout
    {
        public enum Align { Left, Center, Right }

        public readonly struct Cell
        {
            public readonly RectangleF Rect; public readonly Color Color;
            public Cell(RectangleF rect, Color color) { Rect = rect; Color = color; }
        }
        public readonly struct Label
        {
            public readonly PointF At; public readonly string Text; public readonly Align Align; public readonly bool Title;
            public Label(PointF at, string text, Align align, bool title) { At = at; Text = text; Align = align; Title = title; }
        }
        public readonly struct Stroke
        {
            public readonly PointF A, B; public readonly float Width;
            public Stroke(PointF a, PointF b, float width) { A = a; B = b; Width = width; }
        }
        public readonly struct Disc
        {
            public readonly PointF Center; public readonly float Radius; public readonly Color Color;
            public Disc(PointF c, float r, Color color) { Center = c; Radius = r; Color = color; }
        }

        public readonly List<Cell> Cells = new();
        public readonly List<Label> Labels = new();
        public readonly List<Stroke> Strokes = new();
        public readonly List<Disc> Discs = new();
        /// <summary>Content bounds in the layout frame (y up); a frame panel adds padding around it.</summary>
        public RectangleF Bounds;
        public float Padding;

        const int RampCells = 64;

        /// <summary>Approximate text width - Rhino offers no 2D text measurement, and a
        /// ~0.55 em average is what a humanist UI face comes out at.</summary>
        public static float TextWidth(string s, double size) => (float)(s.Length * 0.55 * size);

        static Color ToColor(Rgb c) => Color.FromArgb(c.R, c.G, c.B);

        public static LegendLayout Build(LegendSpec spec, LegendStyle style)
        {
            var lay = new LegendLayout();
            bool vertical = style.Orientation == LegendOrientation.Vertical;
            float L = (float)Math.Max(1, style.Length), T = (float)Math.Max(1, style.Thickness);
            float ts = (float)Math.Max(1, style.TextSize);
            float titleSize = ts * 1.15f;
            float gap = ts * 0.55f;                  // text-to-swatch gap
            float tick = ts * 0.4f;                  // tick length
            float titleGap = ts * 1.0f;              // content-to-title gap: a full text height, so the
                                                     // title clears the top swatch row and the mark labels

            switch (spec.Type)
            {
                case LegendType.Classes:
                case LegendType.Groups:
                    BuildSwatches(spec, style, lay, vertical, T, ts, gap);
                    break;
                case LegendType.Size:
                    BuildSizes(spec, lay, vertical, T, ts, gap);
                    break;
                default:
                    BuildRamp(spec, style, lay, vertical, L, T, ts, gap, tick);
                    break;
            }

            // title above everything, left-aligned with the content
            float top = lay.Bounds.Bottom;   // in a y-up frame Bottom is the larger y
            if (style.ShowTitle && spec.Title.Length > 0)
            {
                lay.Labels.Add(new Label(new PointF(lay.Bounds.Left, top + titleGap), spec.Title, Align.Left, true));
                lay.Bounds = Union(lay.Bounds, new RectangleF(lay.Bounds.Left, top + titleGap, TextWidth(spec.Title, titleSize), titleSize));
            }
            lay.Padding = style.Frame ? gap : 0;
            return lay;
        }

        static void BuildRamp(LegendSpec spec, LegendStyle style, LegendLayout lay, bool vertical,
                              float L, float T, float ts, float gap, float tick)
        {
            // the bar itself: RampCells cells from the scale's own colours
            for (int i = 0; i < RampCells; i++)
            {
                double t = (i + 0.5) / RampCells;
                double value = spec.Min + (spec.Max - spec.Min) * (spec.Reverse ? 1 - t : t);
                var c = ToColor(spec.ColorFor(value));
                float a = L * i / RampCells, b = L * (i + 1) / RampCells + 0.5f;
                lay.Cells.Add(vertical
                    ? new Cell(new RectangleF(0, a, T, b - a), c)
                    : new Cell(new RectangleF(a, 0, b - a, T), c));
            }
            var bar = vertical ? new RectangleF(0, 0, T, L) : new RectangleF(0, 0, L, T);
            lay.Bounds = bar;

            if (style.ShowTicks)
            {
                double lo = Math.Min(spec.Min, spec.Max), hi = Math.Max(spec.Min, spec.Max);
                double span = hi - lo;
                foreach (double v in spec.TickValues())
                {
                    float f = (float)(span > 0 ? (v - lo) / span : 0.5);
                    if (spec.Reverse) f = 1 - f;
                    string text = spec.Format(v);
                    if (vertical)
                    {
                        float y = L * f;
                        lay.Strokes.Add(new Stroke(new PointF(T, y), new PointF(T + tick, y), 1f));
                        lay.Labels.Add(new Label(new PointF(T + tick + gap * 0.6f, y - ts * 0.35f), text, Align.Left, false));
                        lay.Bounds = Union(lay.Bounds, new RectangleF(T + tick + gap * 0.6f, y - ts * 0.35f, TextWidth(text, ts), ts));
                    }
                    else
                    {
                        float x = L * f;
                        lay.Strokes.Add(new Stroke(new PointF(x, 0), new PointF(x, -tick), 1f));
                        var align = f < 0.01f ? Align.Left : f > 0.99f ? Align.Right : Align.Center;
                        lay.Labels.Add(new Label(new PointF(x, -tick - gap * 0.6f - ts), text, align, false));
                        float w = TextWidth(text, ts);
                        float x0 = align == Align.Left ? x : align == Align.Right ? x - w : x - w / 2;
                        lay.Bounds = Union(lay.Bounds, new RectangleF(x0, -tick - gap * 0.6f - ts, w, ts));
                    }
                }
                // annotated marks (fast / slow …) cross the bar and label on the far side
                foreach (var (value, label) in spec.Marks)
                {
                    float f = (float)(span > 0 ? Math.Max(0, Math.Min(1, (value - lo) / span)) : 0.5);
                    if (spec.Reverse) f = 1 - f;
                    if (vertical)
                    {
                        float y = L * f;
                        lay.Strokes.Add(new Stroke(new PointF(-tick, y), new PointF(T + tick * 0.5f, y), 1.5f));
                        if (label.Length > 0)
                        {
                            lay.Labels.Add(new Label(new PointF(-tick - gap * 0.6f, y - ts * 0.35f), label, Align.Right, false));
                            lay.Bounds = Union(lay.Bounds, new RectangleF(-tick - gap * 0.6f - TextWidth(label, ts), y - ts * 0.35f, TextWidth(label, ts), ts));
                        }
                    }
                    else
                    {
                        float x = L * f;
                        lay.Strokes.Add(new Stroke(new PointF(x, -tick * 0.5f), new PointF(x, T + tick), 1.5f));
                        if (label.Length > 0)
                        {
                            lay.Labels.Add(new Label(new PointF(x, T + tick + gap * 0.4f), label, Align.Center, false));
                            float w = TextWidth(label, ts);
                            lay.Bounds = Union(lay.Bounds, new RectangleF(x - w / 2, T + tick + gap * 0.4f, w, ts));
                        }
                    }
                }
            }

            if (style.ShowUnavailable && spec.Unavailable.HasValue)
            {
                var (rgb, label) = spec.Unavailable.Value;
                var c = ToColor(rgb);
                if (vertical)
                {
                    float y = -T - gap;
                    lay.Cells.Add(new Cell(new RectangleF(0, y, T, T), c));
                    lay.Labels.Add(new Label(new PointF(T + gap * 0.6f, y + T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(0, y, T + gap * 0.6f + TextWidth(label, ts), T));
                }
                else
                {
                    float x = lay.Bounds.Right + gap * 1.5f;
                    lay.Cells.Add(new Cell(new RectangleF(x, 0, T, T), c));
                    lay.Labels.Add(new Label(new PointF(x + T + gap * 0.6f, T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(x, 0, T + gap * 0.6f + TextWidth(label, ts), T));
                }
            }
        }

        static void BuildSwatches(LegendSpec spec, LegendStyle style, LegendLayout lay, bool vertical,
                                  float T, float ts, float gap)
        {
            int n = Math.Max(1, spec.ClassCount);
            float row = Math.Max(T, ts) + gap;
            // classes read top-down like a table, so index 0 sits at the top (largest y)
            lay.Bounds = RectangleF.Empty;
            float x = 0, y = 0;
            for (int i = 0; i < n; i++)
            {
                var c = ToColor(spec.ClassColor(i));
                string label = spec.ClassLabel(i);
                if (vertical)
                {
                    float yy = (n - 1 - i) * row;
                    lay.Cells.Add(new Cell(new RectangleF(0, yy, T, T), c));
                    lay.Labels.Add(new Label(new PointF(T + gap, yy + T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(0, yy, T + gap + TextWidth(label, ts), Math.Max(T, ts)));
                }
                else
                {
                    lay.Cells.Add(new Cell(new RectangleF(x, y, T, T), c));
                    lay.Labels.Add(new Label(new PointF(x + T + gap * 0.6f, y + T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    float w = T + gap * 0.6f + TextWidth(label, ts);
                    lay.Bounds = Union(lay.Bounds, new RectangleF(x, y, w, Math.Max(T, ts)));
                    x += w + gap * 2;
                }
            }
            if (style.ShowUnavailable && spec.Unavailable.HasValue)
            {
                var (rgb, label) = spec.Unavailable.Value;
                var c = ToColor(rgb);
                if (vertical)
                {
                    float yy = -row;
                    lay.Cells.Add(new Cell(new RectangleF(0, yy, T, T), c));
                    lay.Labels.Add(new Label(new PointF(T + gap, yy + T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(0, yy, T + gap + TextWidth(label, ts), Math.Max(T, ts)));
                }
                else
                {
                    lay.Cells.Add(new Cell(new RectangleF(x, y, T, T), c));
                    lay.Labels.Add(new Label(new PointF(x + T + gap * 0.6f, y + T * 0.5f - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(x, y, T + gap * 0.6f + TextWidth(label, ts), Math.Max(T, ts)));
                }
            }
        }

        static void BuildSizes(LegendSpec spec, LegendLayout lay, bool vertical,
                               float T, float ts, float gap)
        {
            // cartographic convention: area proportional to the value → radius ∝ √fraction
            var samples = new List<double>(spec.Breaks);
            if (samples.Count == 0)
            {
                double lo = Math.Min(spec.Min, spec.Max), hi = Math.Max(spec.Min, spec.Max);
                samples.Add(lo); samples.Add(lo + (hi - lo) * 0.5); samples.Add(hi);
            }
            float rMax = T, rMin = Math.Max(1.5f, T * 0.18f);
            var c = ToColor(spec.ColorFor(samples[samples.Count - 1]));
            lay.Bounds = RectangleF.Empty;
            float cursor = 0;
            foreach (double v in samples)
            {
                double f = spec.Fraction(v);
                float r = rMin + (rMax - rMin) * (float)Math.Sqrt(double.IsNaN(f) ? 0 : f);
                string label = spec.Format(v);
                if (vertical)
                {
                    var centre = new PointF(rMax, cursor + r);
                    lay.Discs.Add(new Disc(centre, r, c));
                    lay.Labels.Add(new Label(new PointF(2 * rMax + gap, centre.Y - ts * 0.35f), label, Align.Left, false));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(0, cursor, 2 * rMax + gap + TextWidth(label, ts), 2 * r));
                    cursor += 2 * r + gap;
                }
                else
                {
                    var centre = new PointF(cursor + r, rMax);
                    lay.Discs.Add(new Disc(centre, r, c));
                    lay.Labels.Add(new Label(new PointF(centre.X, -ts - gap * 0.4f), label, Align.Center, false));
                    float w = Math.Max(2 * r, TextWidth(label, ts));
                    lay.Bounds = Union(lay.Bounds, new RectangleF(centre.X - w / 2, -ts - gap * 0.4f, w, ts + gap * 0.4f + 2 * rMax));
                    cursor += Math.Max(2 * r, TextWidth(label, ts)) + gap * 1.5f;
                }
            }
        }

        static RectangleF Union(RectangleF a, RectangleF b)
        {
            // RectangleF.IsEmpty is the '||' form (either extent <= 0), unlike Rectangle's
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            float l = Math.Min(a.Left, b.Left), r = Math.Max(a.Right, b.Right);
            float bo = Math.Min(a.Top, b.Top), t = Math.Max(a.Bottom, b.Bottom);   // y-up frame: Top = min y
            return new RectangleF(l, bo, r - l, t - bo);
        }
    }

    /// <summary>The three backends over one <see cref="LegendLayout"/>.</summary>
    public static class LegendRenderer
    {
        /// <summary>Viewport-anchored overlay. The layout frame (y up, origin bottom-left)
        /// is placed by <paramref name="anchor"/> inside the viewport with the style margin.</summary>
        public static void Draw2d(DisplayPipeline display, Size viewport, LegendLayout lay, LegendStyle style, LegendAnchor anchor)
        {
            float pad = lay.Padding;
            var b = lay.Bounds;
            float totalW = b.Width + 2 * pad, totalH = b.Height + 2 * pad;
            float m = (float)style.Margin;
            // screen origin of the layout's bottom-left corner
            float ox, oy;
            switch (anchor)
            {
                case LegendAnchor.TopLeft: ox = m; oy = m + totalH; break;
                case LegendAnchor.TopRight: ox = viewport.Width - m - totalW; oy = m + totalH; break;
                case LegendAnchor.BottomRight: ox = viewport.Width - m - totalW; oy = viewport.Height - m; break;
                case LegendAnchor.Top: ox = (viewport.Width - totalW) / 2; oy = m + totalH; break;
                case LegendAnchor.Bottom: ox = (viewport.Width - totalW) / 2; oy = viewport.Height - m; break;
                case LegendAnchor.Left: ox = m; oy = (viewport.Height + totalH) / 2; break;
                case LegendAnchor.Right: ox = viewport.Width - m - totalW; oy = (viewport.Height + totalH) / 2; break;
                default: ox = m; oy = viewport.Height - m; break;   // bottom-left
            }
            ox += pad - b.Left;
            oy -= pad - b.Top;   // b.Top is the frame's min y
            PointF S(PointF p) => new PointF(ox + p.X, oy - p.Y);

            if (style.Frame)
            {
                var tl = S(new PointF(b.Left - pad, b.Bottom + pad));
                var rect = new Rectangle((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(totalW), (int)Math.Round(totalH));
                display.Draw2dRectangle(rect, style.FrameEdge, 1, style.FrameColor);
            }
            foreach (var cell in lay.Cells)
            {
                var tl = S(new PointF(cell.Rect.Left, cell.Rect.Bottom));
                var rect = new Rectangle((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
                                         Math.Max(1, (int)Math.Round(cell.Rect.Width)), Math.Max(1, (int)Math.Round(cell.Rect.Height)));
                display.Draw2dRectangle(rect, cell.Color, 0, cell.Color);
            }
            foreach (var disc in lay.Discs)
            {
                // no 2D disc primitive: a filled polygon of 2D lines is heavier than a
                // TextDot-free approximation with concentric rectangles - draw a ring of
                // short lines instead (radius steps of one pixel)
                var c = S(disc.Center);
                for (float r = disc.Radius; r > 0; r -= 1f)
                {
                    int segs = Math.Max(12, (int)(r * 2));
                    PointF prev = new PointF(c.X + r, c.Y);
                    for (int i = 1; i <= segs; i++)
                    {
                        double a = 2 * Math.PI * i / segs;
                        var p = new PointF(c.X + (float)(r * Math.Cos(a)), c.Y + (float)(r * Math.Sin(a)));
                        display.Draw2dLine(prev, p, disc.Color, 1.5f);
                        prev = p;
                    }
                }
            }
            foreach (var s in lay.Strokes)
                Stroke2d(display, S(s.A), S(s.B), style.TextColor, s.Width);
            foreach (var lab in lay.Labels)
            {
                int h = (int)Math.Round(lab.Title ? style.TextSize * 1.15 : style.TextSize);
                var p = S(lab.At);   // the label box's bottom-left corner on screen
                // Rhino places 2D text by its own rules (the documentation says lower-left
                // justified; the box it paints depends on the font and the Rhino version,
                // and the title used to land on the bar). Measure the box the text takes
                // at the origin and move the definition point so that box sits with its
                // bottom-left where the layout put it. The measured width also replaces
                // the 0.55 em estimate for right and centre alignment.
                var box = Measure2d(display, lab.Text, h, style.Font);
                bool measured = box.Width > 0 && box.Height > 0;
                float w = measured ? box.Width : LegendLayout.TextWidth(lab.Text, h);
                float x = lab.Align == LegendLayout.Align.Left ? p.X : lab.Align == LegendLayout.Align.Right ? p.X - w : p.X - w / 2;
                float dx = measured ? -box.Left : 0f, dy = measured ? -box.Bottom : 0f;
                display.Draw2dText(lab.Text, style.TextColor, new Point2d(x + dx, p.Y + dy), false, h, style.Font);
            }
        }

        /// <summary>The screen rectangle <c>Draw2dText</c> paints for a definition point
        /// at the origin, or an empty rectangle when the pipeline cannot say.</summary>
        static Rectangle Measure2d(DisplayPipeline display, string text, int height, string font)
        {
            // best effort: a pipeline that cannot measure (or throws from inside a
            // preview callback) leaves the estimate in charge, exactly as before
            try { return display.Measure2dText(text, new Point2d(0, 0), false, 0.0, height, font); }
            catch (Exception) { return Rectangle.Empty; }
        }

        /// <summary>A tick or mark on the 2D overlay. Every legend stroke is axis-aligned,
        /// and a filled rectangle is what the bar cells are drawn with, so the ticks are
        /// drawn the same way: <c>Draw2dLine</c> left them invisible in Rhino 8's
        /// Grasshopper preview while the cells next to them showed.</summary>
        static void Stroke2d(DisplayPipeline display, PointF a, PointF b, Color color, float width)
        {
            float w = Math.Max(1f, width);
            bool vertical = Math.Abs(a.X - b.X) < 0.5f, horizontal = Math.Abs(a.Y - b.Y) < 0.5f;
            if (!vertical && !horizontal) { display.Draw2dLine(a, b, color, width); return; }
            float left = vertical ? a.X - w / 2 : Math.Min(a.X, b.X);
            float top = horizontal ? a.Y - w / 2 : Math.Min(a.Y, b.Y);
            float rw = vertical ? w : Math.Abs(b.X - a.X);
            float rh = horizontal ? w : Math.Abs(b.Y - a.Y);
            var rect = new Rectangle((int)Math.Round(left), (int)Math.Round(top),
                                     Math.Max(1, (int)Math.Round(rw)), Math.Max(1, (int)Math.Round(rh)));
            display.Draw2dRectangle(rect, color, 0, color);
        }

        /// <summary>Model-space legend on <paramref name="plane"/> (its origin is the
        /// layout's bottom-left corner after <paramref name="anchor"/> shifts it),
        /// scaled by <paramref name="scale"/> model units per layout unit.</summary>
        public static (Mesh mesh, List<Text3d> texts, List<Plane> frames, List<string> labels, BoundingBox box)
            Build3d(LegendLayout lay, LegendStyle style, Plane plane, double scale, LegendAnchor anchor)
        {
            float pad = lay.Padding;
            var b = lay.Bounds;
            double w = (b.Width + 2 * pad) * scale, h = (b.Height + 2 * pad) * scale;
            // shift so the requested corner sits on the plane origin
            double dx = 0, dy = 0;
            switch (anchor)
            {
                case LegendAnchor.TopLeft: dy = -h; break;
                case LegendAnchor.TopRight: dx = -w; dy = -h; break;
                case LegendAnchor.BottomRight: dx = -w; break;
                case LegendAnchor.Top: dx = -w / 2; dy = -h; break;
                case LegendAnchor.Bottom: dx = -w / 2; break;
                case LegendAnchor.Left: dy = -h / 2; break;
                case LegendAnchor.Right: dx = -w; dy = -h / 2; break;
            }
            dx -= (b.Left - pad) * scale;
            dy -= (b.Top - pad) * scale;
            Point3d P(double x, double y) => plane.PointAt(dx + x * scale, dy + y * scale);

            var mesh = new Mesh();
            void Quad(RectangleF r, Color c)
            {
                int i = mesh.Vertices.Count;
                mesh.Vertices.Add(P(r.Left, r.Top));
                mesh.Vertices.Add(P(r.Right, r.Top));
                mesh.Vertices.Add(P(r.Right, r.Bottom));
                mesh.Vertices.Add(P(r.Left, r.Bottom));
                for (int k = 0; k < 4; k++) mesh.VertexColors.Add(c);
                mesh.Faces.AddFace(i, i + 1, i + 2, i + 3);
            }
            if (style.Frame) Quad(new RectangleF(b.Left - pad, b.Top - pad, b.Width + 2 * pad, b.Height + 2 * pad), style.FrameColor);
            foreach (var cell in lay.Cells) Quad(cell.Rect, cell.Color);
            foreach (var disc in lay.Discs)
            {
                int centre = mesh.Vertices.Count;
                mesh.Vertices.Add(P(disc.Center.X, disc.Center.Y));
                mesh.VertexColors.Add(disc.Color);
                const int segs = 32;
                for (int i = 0; i <= segs; i++)
                {
                    double a = 2 * Math.PI * i / segs;
                    mesh.Vertices.Add(P(disc.Center.X + disc.Radius * Math.Cos(a), disc.Center.Y + disc.Radius * Math.Sin(a)));
                    mesh.VertexColors.Add(disc.Color);
                }
                for (int i = 0; i < segs; i++) mesh.Faces.AddFace(centre, centre + 1 + i, centre + 2 + i);
            }
            // tick strokes as thin quads so they bake with the mesh
            foreach (var s in lay.Strokes)
            {
                double half = Math.Max(0.35, s.Width * 0.5);
                var v = new PointF(s.B.X - s.A.X, s.B.Y - s.A.Y);
                float len = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);
                if (len < 1e-6f) continue;
                var n = new PointF(-v.Y / len * (float)half, v.X / len * (float)half);
                int i0 = mesh.Vertices.Count;
                mesh.Vertices.Add(P(s.A.X + n.X, s.A.Y + n.Y));
                mesh.Vertices.Add(P(s.B.X + n.X, s.B.Y + n.Y));
                mesh.Vertices.Add(P(s.B.X - n.X, s.B.Y - n.Y));
                mesh.Vertices.Add(P(s.A.X - n.X, s.A.Y - n.Y));
                for (int k = 0; k < 4; k++) mesh.VertexColors.Add(style.TextColor);
                mesh.Faces.AddFace(i0, i0 + 1, i0 + 2, i0 + 3);
            }
            if (mesh.Faces.Count > 0) { mesh.Normals.ComputeNormals(); mesh.Compact(); }

            var texts = new List<Text3d>();
            var frames = new List<Plane>();
            var labels = new List<string>();
            foreach (var lab in lay.Labels)
            {
                double size = (lab.Title ? style.TextSize * 1.15 : style.TextSize) * scale;
                var at = P(lab.At.X, lab.At.Y);
                var tp = new Plane(at, plane.XAxis, plane.YAxis);
                var t3 = new Text3d(lab.Text, tp, size)
                {
                    FontFace = style.Font,
                    HorizontalAlignment = lab.Align == LegendLayout.Align.Left ? TextHorizontalAlignment.Left
                                        : lab.Align == LegendLayout.Align.Right ? TextHorizontalAlignment.Right
                                        : TextHorizontalAlignment.Center,
                    VerticalAlignment = TextVerticalAlignment.Bottom,
                };
                texts.Add(t3);
                frames.Add(tp);
                labels.Add(lab.Text);
            }
            var box = mesh.Faces.Count > 0 ? mesh.GetBoundingBox(false) : BoundingBox.Empty;
            foreach (var f in frames) box.Union(f.Origin);
            box.Union(P(b.Left - pad, b.Top - pad));
            box.Union(P(b.Right + pad, b.Bottom + pad));
            return (mesh, texts, frames, labels, box);
        }
    }
}
