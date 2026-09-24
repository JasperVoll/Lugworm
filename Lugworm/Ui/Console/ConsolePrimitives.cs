using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>Everything a page renderer needs for one paint: the canvas, theme, state,
    /// button gate, labels, interaction state and the region registry of the coordinator.
    /// Machine facts come from <see cref="State"/> and <see cref="Buttons"/> only.</summary>
    public sealed class ConsolePaintContext
    {
        public SKCanvas Canvas { get; }
        public ConsoleTheme T { get; }
        public PrintConsoleState State { get; }
        public ButtonSet Buttons { get; }
        public IReadOnlyList<string> PointLabels { get; }
        public ConsoleInteractionState Interaction { get; }
        public ConsoleView View { get; }
        public string RendererLabel { get; }
        public ConsolePrimitives P { get; }

        /// <summary>The machine's Cartesian axes map to the Rhino frame as identity. Only
        /// meaningful while <see cref="FrameKnown"/> is true. A machine fact, so it arrives
        /// from the component with the state rather than being inferred here.</summary>
        public bool Cartesian { get; }
        /// <summary>The mechanism is established (the robot profile resolved). False means
        /// no page may claim a Rhino direction for a machine axis: the Cartesian identity
        /// and the mirrored SCARA frame disagree, so a guess would point the wrong way.</summary>
        public bool FrameKnown { get; }

        public ConsolePaintContext(SKCanvas canvas, ConsoleTheme theme, PrintConsoleState state, ButtonSet buttons,
                                   IReadOnlyList<string> pointLabels, ConsoleInteractionState interaction,
                                   ConsoleView view, string rendererLabel,
                                   bool cartesian = false, bool frameKnown = false)
        {
            Canvas = canvas; T = theme; State = state; Buttons = buttons; PointLabels = pointLabels;
            Interaction = interaction; View = view; RendererLabel = rendererLabel;
            Cartesian = cartesian; FrameKnown = frameKnown;
            P = new ConsolePrimitives(this);
        }

        /// <param name="tip">Hover text for this region, or null. Registered with the hit
        /// rectangle rather than in a table of its own, so a tip can never name a control
        /// that this paint did not draw.</param>
        public void Region(SKRect rect, string id, bool enabled, string? tip = null)
            => View.AddRegion(rect, id, enabled, tip);
    }

    /// <summary>Cards, buttons, badges, progress bars and text treatments. Draws from the
    /// theme; decides nothing - enabled flags and reasons arrive from the button gate.</summary>
    public sealed class ConsolePrimitives
    {
        readonly ConsolePaintContext _c;
        readonly Dictionary<(float, bool), SKPaint> _text = new Dictionary<(float, bool), SKPaint>();
        SKPaint? _mono, _fill, _stroke;

        internal ConsolePrimitives(ConsolePaintContext c) { _c = c; }

        ConsoleTheme T => _c.T;
        SKCanvas Canvas => _c.Canvas;

        // ---- paints (cached per paint context; disposed with it) ----

        public SKPaint Text(float size, bool semibold = false)
        {
            if (_text.TryGetValue((size, semibold), out var p)) return p;
            p = new SKPaint { IsAntialias = true, TextSize = size, Typeface = semibold ? ConsoleTheme.Semibold : ConsoleTheme.Regular, Color = T.Text };
            _text[(size, semibold)] = p;
            return p;
        }

        public SKPaint Mono(float size)
            => _mono ??= new SKPaint { IsAntialias = true, TextSize = size, Typeface = ConsoleTheme.Mono, Color = T.Text };

        SKPaint Fill(SKColor color)
        {
            _fill ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            _fill.Color = color;
            return _fill;
        }

        SKPaint Stroke(SKColor color, float width)
        {
            _stroke ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
            _stroke.Color = color; _stroke.StrokeWidth = width;
            return _stroke;
        }

        internal void Dispose()
        {
            foreach (var p in _text.Values) p.Dispose();
            _text.Clear();
            _mono?.Dispose(); _fill?.Dispose(); _stroke?.Dispose();
        }

        // ---- text ----

        public float Draw(string text, float x, float baseline, float size, SKColor color, bool semibold = false)
        {
            var p = Text(size, semibold);
            p.Color = color;
            Canvas.DrawText(text ?? "", x, baseline, p);
            return p.MeasureText(text ?? "");
        }

        public float Measure(string text, float size, bool semibold = false) => Text(size, semibold).MeasureText(text ?? "");

        /// <summary>Draws text clipped with an ellipsis to <paramref name="maxWidth"/>.</summary>
        public void DrawClipped(string text, float x, float baseline, float maxWidth, float size, SKColor color, bool semibold = false)
        {
            var p = Text(size, semibold);
            p.Color = color;
            Canvas.DrawText(Clip(text, maxWidth, p), x, baseline, p);
        }

        public static string Clip(string? value, float maxWidth, SKPaint paint)
        {
            if (value == null || maxWidth <= 0) return "";
            if (paint.MeasureText(value) <= maxWidth) return value;
            for (int len = value.Length - 1; len > 3; len--)
            {
                string candidate = value.Substring(0, len) + "...";
                if (paint.MeasureText(candidate) <= maxWidth) return candidate;
            }
            return paint.MeasureText("...") <= maxWidth ? "..." : "";
        }

        /// <summary>Word-wraps text into at most <paramref name="maxLines"/> lines; returns the height used.</summary>
        public float DrawWrapped(string text, float x, float top, float maxWidth, float size, SKColor color, int maxLines = 3)
        {
            var p = Text(size);
            p.Color = color;
            float lineH = size * 1.35f;
            var words = (text ?? "").Split(' ');
            string line = "";
            int lines = 0;
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (p.MeasureText(candidate) <= maxWidth || line.Length == 0) { line = candidate; continue; }
                Canvas.DrawText(line, x, top + lineH * lines + size, p);
                lines++;
                line = word;
                if (lines == maxLines - 1) break;
            }
            if (line.Length > 0 && lines < maxLines)
            {
                Canvas.DrawText(Clip(line, maxWidth, p), x, top + lineH * lines + size, p);
                lines++;
            }
            return lineH * lines;
        }

        public void Heading(string text, float x, float baseline)
            => Draw(text, x, baseline, T.FontHeader, T.Text, true);

        public void Label(string text, float x, float baseline)
            => Draw(text, x, baseline, T.FontSmall, T.TextMuted);

        // ---- surfaces ----

        /// <summary>A card with an optional title; returns the content rectangle.</summary>
        public SKRect Card(SKRect rect, string? title = null, bool elevated = false)
        {
            Canvas.DrawRoundRect(rect, T.CardCorner, T.CardCorner, Fill(elevated ? T.PanelElevated : T.Panel));
            Canvas.DrawRoundRect(rect, T.CardCorner, T.CardCorner, Stroke(T.Border, T.Px(1f)));
            // one-pixel top highlight for depth without gloss
            Canvas.DrawLine(rect.Left + T.CardCorner, rect.Top + T.Px(1f), rect.Right - T.CardCorner, rect.Top + T.Px(1f),
                            Stroke(new SKColor(0xFF, 0xFF, 0xFF, 0x12), T.Px(1f)));
            float top = rect.Top + T.Pad;
            if (!string.IsNullOrEmpty(title))
            {
                DrawClipped(title!, rect.Left + T.Pad, top + T.FontHeader * 0.85f,
                            Math.Max(0, rect.Width - T.Pad * 2), T.FontHeader, T.Text, true);
                top += T.FontHeader + T.Gap;
            }
            return new SKRect(rect.Left + T.Pad, top, rect.Right - T.Pad, rect.Bottom - T.Pad);
        }

        public void Divider(float x0, float x1, float y)
            => Canvas.DrawLine(x0, y, x1, y, Stroke(T.Border, T.Px(1f)));

        // ---- controls ----

        /// <summary>A button with icon and label. Hover, press and pending feedback come
        /// from the interaction state; the hit rectangle never moves. Disabled controls are
        /// drawn subdued; refusal reasons remain available in tooltips.</summary>
        /// <param name="tip">Optional hover text, registered with the hit rectangle. It is
        /// carried by the DISABLED region too - a control the operator cannot press is
        /// exactly the one whose tooltip has something to say. A disabled button without
        /// one gets the gate's refusal reason for its id, so every refusal is readable.</param>
        public void Button(SKRect rect, ConsoleIcon icon, string label, ConsoleRole role, bool enabled, string id,
                           bool pending = false, bool selected = false, string? tip = null)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            if (!enabled && tip == null && _c.Buttons.Reasons.TryGetValue(id, out string? reason)) tip = reason;
            var i = _c.Interaction;
            float hover = enabled ? i.Hover(id) : 0f;
            bool pressed = enabled && i.IsPressed(id);
            SKColor bg;
            SKColor fg;
            SKColor border = T.Border;
            if (!enabled) { bg = T.Disabled; fg = T.TextMuted; }
            else
            {
                switch (role)
                {
                    case ConsoleRole.Primary:
                        bg = pressed ? T.PrimaryPressed : Blend(T.Primary, T.PrimaryHover, hover); fg = T.Text; border = bg; break;
                    case ConsoleRole.Home:
                        bg = pressed ? Darken(T.Home, 0.15f) : Blend(T.Home, Lighten(T.Home, 0.12f), hover); fg = T.Text; border = bg; break;
                    case ConsoleRole.Warning:
                        bg = pressed ? Darken(T.Warning, 0.15f) : Blend(T.Warning, Lighten(T.Warning, 0.1f), hover); fg = T.TextOn(ConsoleRole.Warning); border = bg; break;
                    case ConsoleRole.Error:
                        bg = pressed ? Darken(T.Danger, 0.15f) : Blend(T.Danger, Lighten(T.Danger, 0.1f), hover); fg = T.Text; border = bg; break;
                    case ConsoleRole.Destructive:
                        bg = pressed ? T.PanelElevated : Blend(T.Panel, T.PanelElevated, hover); fg = T.Danger; border = Blend(T.Danger, Lighten(T.Danger, 0.2f), hover); break;
                    default:
                        bg = pressed ? T.Panel : Blend(T.PanelElevated, Lighten(T.PanelElevated, 0.08f), hover); fg = T.Text;
                        border = Blend(T.Border, T.BorderStrong, hover); break;
                }
                if (selected) { border = T.Primary; }
            }
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Fill(bg));
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Stroke(border, T.Px(1f)));
            if (enabled && hover > 0 && !pressed)
                Canvas.DrawLine(rect.Left + T.Corner, rect.Top + T.Px(1f), rect.Right - T.Corner, rect.Top + T.Px(1f),
                                Stroke(new SKColor(0xFF, 0xFF, 0xFF, (byte)(0x28 * hover)), T.Px(1f)));

            float dy = pressed ? T.Px(1f) : 0f;   // a one-pixel depression of the content only
            var lp = Text(T.FontButton, true);
            lp.Color = fg;
            float iconW = icon == ConsoleIcon.None ? 0 : T.IconSize + T.Gap;
            float spinW = pending ? T.IconSize + T.Gap : 0;
            string clippedLabel = Clip(label, Math.Max(0, rect.Width - T.Px(28f) - iconW - spinW), lp);
            float labelW = lp.MeasureText(clippedLabel);
            float total = iconW + labelW + spinW;
            float x = rect.MidX - total / 2f;
            float cy = rect.MidY + dy;
            Canvas.Save();
            Canvas.ClipRect(rect);
            if (icon != ConsoleIcon.None)
            {
                ConsoleIcons.Draw(Canvas, icon, new SKRect(x, cy - T.IconSize / 2, x + T.IconSize, cy + T.IconSize / 2), fg, T.Px(1.75f));
                x += iconW;
            }
            Canvas.DrawText(clippedLabel, x, cy + T.FontButton * 0.36f, lp);
            x += labelW;
            if (pending) Spinner(new SKRect(x + T.Gap, cy - T.IconSize / 2, x + T.Gap + T.IconSize, cy + T.IconSize / 2), fg);
            Canvas.Restore();
            _c.Region(rect, id, enabled, tip);
        }

        /// <summary>Auto-width button at (x, y); returns its rectangle.</summary>
        public SKRect ButtonAuto(float x, float y, ConsoleIcon icon, string label, ConsoleRole role, bool enabled, string id,
                                 float minWidth = 0, bool pending = false, bool tall = false, bool selected = false,
                                 float maxWidth = float.MaxValue, string? tip = null)
        {
            float w = Math.Max(0, Math.Min(maxWidth, ButtonWidth(icon, label, minWidth, pending)));
            var rect = new SKRect(x, y, x + w, y + (tall ? T.PrimaryButtonHeight : T.ButtonHeight));
            Button(rect, icon, label, role, enabled, id, pending, selected, tip);
            return rect;
        }

        public float ButtonWidth(ConsoleIcon icon, string label, float minWidth = 0, bool pending = false)
            => Math.Max(minWidth, Measure(label, T.FontButton, true) + T.Px(28f)
                + (icon == ConsoleIcon.None ? 0 : T.IconSize + T.Gap) + (pending ? T.IconSize + T.Gap : 0));

        /// <summary>A compact icon-only utility (tooltip carried by the caller); returns its rect.</summary>
        public SKRect IconButton(float x, float y, ConsoleIcon icon, bool enabled, string id, float size = 0)
        {
            float s = size > 0 ? size : T.ButtonHeight;
            var rect = new SKRect(x, y, x + s, y + s);
            Button(rect, icon, "", ConsoleRole.Secondary, enabled, id);
            return rect;
        }

        /// <summary>A pill badge; returns its width.</summary>
        public float Badge(float x, float y, string text, SKColor color, bool filled = false)
        {
            var p = Text(T.FontSmall, true);
            float w = p.MeasureText(text) + T.Px(16f);
            float h = T.Px(22f);
            var rect = new SKRect(x, y, x + w, y + h);
            if (filled)
            {
                Canvas.DrawRoundRect(rect, h / 2, h / 2, Fill(color));
                p.Color = Luma(color) > 0.55f ? new SKColor(0x11, 0x14, 0x17) : T.Text;
            }
            else
            {
                Canvas.DrawRoundRect(rect, h / 2, h / 2, Fill(new SKColor(color.Red, color.Green, color.Blue, 0x33)));
                Canvas.DrawRoundRect(rect, h / 2, h / 2, Stroke(color, T.Px(1f)));
                p.Color = color;
            }
            Canvas.DrawText(text, x + T.Px(8f), y + h / 2 + T.FontSmall * 0.36f, p);
            return w;
        }

        public void Progress(SKRect rect, double fraction, SKColor? color = null)
        {
            Canvas.DrawRoundRect(rect, rect.Height / 2, rect.Height / 2, Fill(T.Disabled));
            if (fraction > 0)
            {
                float w = (float)(rect.Width * Math.Min(1.0, fraction));
                var done = new SKRect(rect.Left, rect.Top, rect.Left + Math.Max(w, rect.Height), rect.Bottom);
                Canvas.DrawRoundRect(done, rect.Height / 2, rect.Height / 2, Fill(color ?? T.Primary));
            }
        }

        /// <summary>A status lamp with adjacent text (never colour alone); returns the width used.</summary>
        public float Lamp(float x, float baseline, SKColor color, string text, float size = 0,
                          float maxWidth = float.MaxValue)
        {
            float s = size > 0 ? size : T.FontBody;
            float r = T.Px(5f);
            Canvas.DrawCircle(x + r, baseline - s * 0.32f, r, Fill(color));
            string clippedText = Clip(text, Math.Max(0, maxWidth - r * 2 - T.Gap), Text(s));
            float w = Draw(clippedText, x + r * 2 + T.Gap, baseline, s, T.Text);
            return r * 2 + T.Gap + w;
        }

        public void Spinner(SKRect box, SKColor color)
        {
            float phase = _c.Interaction.SpinnerPhase;
            using (var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = T.Px(2f), Color = color, StrokeCap = SKStrokeCap.Round })
            {
                var r = box; r.Inflate(-T.Px(2f), -T.Px(2f));
                Canvas.DrawArc(r, phase * 360f, 270f, false, p);
            }
        }

        /// <summary>A segmented control; returns the rect. Ids are prefix + value.</summary>
        /// <summary>The width a segmented control needs to show every label unclipped,
        /// measured at the active (bold) weight so a tab does not resize when selected.</summary>
        public float SegmentedWidth(IReadOnlyList<(string label, string id, bool active)> items)
        {
            var measure = Text(T.FontBody, true);
            float total = 0;
            for (int i = 0; i < items.Count; i++) total += measure.MeasureText(items[i].label ?? "") + T.Px(20f);
            return total;
        }

        public void Segmented(SKRect rect, IReadOnlyList<(string label, string id, bool active)> items)
        {
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Fill(T.Panel));
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Stroke(T.Border, T.Px(1f)));
            // Segment widths follow the measured labels (2026-09-09: six equal slices ran
            // "Connection" and "Firmware" into their neighbours). Spare width is shared
            // evenly; when the row is narrower than the labels need, every segment shrinks
            // in proportion and its label is clipped inside it, never into the next one.
            var measure = Text(T.FontBody, true);
            float[] need = new float[items.Count];
            float total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                need[i] = measure.MeasureText(items[i].label ?? "") + T.Px(20f);
                total += need[i];
            }
            float scale = total > rect.Width && total > 0 ? rect.Width / total : 1f;
            float spare = total < rect.Width && items.Count > 0 ? (rect.Width - total) / items.Count : 0f;
            float x = rect.Left;
            for (int i = 0; i < items.Count; i++)
            {
                var (label, id, active) = items[i];
                float w = need[i] * scale + spare;
                var seg = new SKRect(x, rect.Top, x + w, rect.Bottom);
                x += w;
                float hover = _c.Interaction.Hover(id);
                if (active)
                {
                    var inner = seg; inner.Inflate(-T.Px(2f), -T.Px(2f));
                    Canvas.DrawRoundRect(inner, T.Corner - T.Px(1f), T.Corner - T.Px(1f), Fill(T.Primary));
                }
                else if (hover > 0)
                {
                    var inner = seg; inner.Inflate(-T.Px(2f), -T.Px(2f));
                    Canvas.DrawRoundRect(inner, T.Corner - T.Px(1f), T.Corner - T.Px(1f), Fill(Blend(T.Panel, T.PanelElevated, hover)));
                }
                var p = Text(T.FontBody, active);
                p.Color = active ? T.Text : T.TextSecondary;
                string shown = Clip(label, Math.Max(0, w - T.Px(8f)), p);
                Canvas.DrawText(shown, seg.MidX - p.MeasureText(shown) / 2, seg.MidY + T.FontBody * 0.36f, p);
                _c.Region(seg, id, true);
            }
        }

        /// <summary>A pressable row (list item); returns whether it is the selected one.</summary>
        public void Row(SKRect rect, string id, bool selected, bool enabled = true)
        {
            float hover = enabled ? _c.Interaction.Hover(id) : 0f;
            if (selected) Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Fill(T.PanelElevated));
            else if (hover > 0) Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Fill(Blend(T.Panel, T.PanelElevated, hover)));
            if (selected)
                Canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top + T.Px(4f), rect.Left + T.Px(3f), rect.Bottom - T.Px(4f)), T.Px(1.5f), T.Px(1.5f), Fill(T.Primary));
            _c.Region(rect, id, enabled);
        }

        /// <summary>The amber confirmation strip of an armed terminal command.</summary>
        public void Strip(SKRect rect, ConsoleIcon icon, string text, SKColor color)
        {
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Fill(new SKColor(color.Red, color.Green, color.Blue, 0x2E)));
            Canvas.DrawRoundRect(rect, T.Corner, T.Corner, Stroke(color, T.Px(1f)));
            float x = rect.Left + T.GapLarge;
            ConsoleIcons.Draw(Canvas, icon, new SKRect(x, rect.MidY - T.IconSize / 2, x + T.IconSize, rect.MidY + T.IconSize / 2), color, T.Px(1.75f));
            x += T.IconSize + T.Gap;
            DrawClipped(text, x, rect.MidY + T.FontBody * 0.36f, rect.Right - x - T.GapLarge, T.FontBody, T.Text, true);
        }

        // ---- colour helpers ----

        public static SKColor Blend(SKColor a, SKColor b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
                               (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));
        }

        public static SKColor Lighten(SKColor c, float amount) => Blend(c, SKColors.White, amount);
        public static SKColor Darken(SKColor c, float amount) => Blend(c, SKColors.Black, amount);
        static float Luma(SKColor c) => (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;

        public static string Invariant(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
    }
}
