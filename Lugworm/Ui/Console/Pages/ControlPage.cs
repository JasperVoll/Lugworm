using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console.Pages
{
    /// <summary>Spatial jogging: an XY pad arranged as a cross, a separate Z column, a
    /// segmented step selector, live position with homed lamps, grouped homing and
    /// origin capture, and a paged calibration-point list with its Go to action. The
    /// jog ids are the press-and-hold ids the form drives from MouseDown/MouseUp.</summary>
    public static class ControlPage
    {
        public static void Paint(ConsolePaintContext ctx, SKRect area, bool narrow)
        {
            var t = ctx.T;
            float jogH = t.Px(330f);
            var top = ConsoleLayout.Take(ref area, jogH, t.GapLarge);
            if (narrow)
            {
                Jog(ctx, top);
                var second = ConsoleLayout.Take(ref area, t.Px(260f), t.GapLarge);
                var cols = ConsoleLayout.Columns(second, 2, t.GapLarge);
                Position(ctx, new SKRect(cols[0].Left, cols[0].Top, cols[0].Right, cols[0].Bottom));
                HomingAndOrigin(ctx, cols[1]);
            }
            else
            {
                var cols = ConsoleLayout.Columns(top, 2, t.GapLarge);
                Jog(ctx, cols[0]);
                var right = cols[1];
                var pos = ConsoleLayout.Take(ref right, t.Px(118f), t.GapLarge);
                Position(ctx, pos);
                HomingAndOrigin(ctx, right);
            }
            if (area.Height > t.Px(120f)) Points(ctx, area);
        }

        static void Jog(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var b = ctx.Buttons;
            var inner = p.Card(cell, "Jog");
            // step selector
            var items = new List<(string, string, bool)>();
            foreach (double step in JogPlanner.StepWhitelist)
            {
                string label = step.ToString("0.###", CultureInfo.InvariantCulture) + " mm";
                items.Add((label, "step:" + step.ToString("0.###", CultureInfo.InvariantCulture), step.Equals(s.Step)));
            }
            var seg = new SKRect(inner.Left, inner.Top, inner.Right, inner.Top + t.ButtonHeight);
            p.Segmented(seg, items);

            // pad: 3x3 cross plus a Z column
            float size = t.Px(64f);
            float gap = t.Gap;
            float padTop = seg.Bottom + t.GapLarge;
            float padLeft = inner.Left;
            float cx = padLeft + size + gap, cy = padTop + size + gap;
            // The pad is labelled with MACHINE axes and drawn with arrows, which read as
            // screen directions; on a SCARA they are neither. The tooltip says which Rhino
            // axis each button actually moves along, and carries the refusal reason when
            // the gate is shut, so the help never displaces it (finding U1, row 8.3).
            // The reason id is "jog" - the pad and the go-to move share one gate.
            string? why = b.Jog ? null : b.Reasons.TryGetValue("jog", out string? reason) ? reason : null;
            string? Tip(string id) => JogDirectionHelp.Describe(id, ctx.Cartesian, ctx.FrameKnown, why);
            p.Button(new SKRect(cx, padTop, cx + size, padTop + size), ConsoleIcon.ArrowUp, "Y+", ConsoleRole.Primary, b.Jog, "jogyplus", tip: Tip("jogyplus"));
            p.Button(new SKRect(padLeft, cy, padLeft + size, cy + size), ConsoleIcon.ArrowLeft, "X-", ConsoleRole.Primary, b.Jog, "jogxminus", tip: Tip("jogxminus"));
            p.Button(new SKRect(cx + size + gap, cy, cx + size * 2 + gap, cy + size), ConsoleIcon.ArrowRight, "X+", ConsoleRole.Primary, b.Jog, "jogxplus", tip: Tip("jogxplus"));
            p.Button(new SKRect(cx, cy + size + gap, cx + size, cy + size * 2 + gap), ConsoleIcon.ArrowDown, "Y-", ConsoleRole.Primary, b.Jog, "jogyminus", tip: Tip("jogyminus"));
            // centre: the step, as a reminder of what one press moves
            var centre = new SKRect(cx, cy, cx + size, cy + size);
            string stepLabel = s.Step.ToString("0.###", CultureInfo.InvariantCulture) + " mm";
            p.Draw(stepLabel, centre.MidX - p.Measure(stepLabel, t.FontHeader, true) / 2,
                   centre.MidY + t.FontHeader * 0.36f, t.FontHeader, t.TextSecondary, true);

            float zx = cx + size * 2 + gap * 2 + t.PadLarge;
            p.Button(new SKRect(zx, padTop, zx + size, padTop + size), ConsoleIcon.ArrowUp, "Z+", ConsoleRole.Primary, b.Jog, "jogzplus", tip: Tip("jogzplus"));
            p.Draw("Z", zx + size / 2 - p.Measure("Z", t.FontHeader, true) / 2, cy + size / 2 + t.FontHeader * 0.36f, t.FontHeader, t.TextSecondary, true);
            p.Button(new SKRect(zx, cy + size + gap, zx + size, cy + size * 2 + gap), ConsoleIcon.ArrowDown, "Z-", ConsoleRole.Primary, b.Jog, "jogzminus", tip: Tip("jogzminus"));

            float y = cy + size * 2 + gap + t.Gap;
            p.Draw("Hold to repeat; release stops after the current step", inner.Left, y + t.FontSmall, t.FontSmall, t.TextMuted);
        }

        static void Position(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            var inner = p.Card(cell, "Position (machine, mm)");
            var cols = ConsoleLayout.Columns(new SKRect(inner.Left, inner.Top, inner.Right, inner.Bottom), 3, t.Gap);
            string[] axes = { "X", "Y", "Z" };
            bool[] homed = { s.XHomed, s.YHomed, s.ZHomed };
            for (int i = 0; i < 3; i++)
            {
                double? v = s.Position.HasValue ? (i == 0 ? s.Position.Value.X : i == 1 ? s.Position.Value.Y : s.Position.Value.Z) : (double?)null;
                p.Lamp(cols[i].Left, cols[i].Top + t.FontSmall, homed[i] ? t.Success : t.TextMuted, axes[i] + (homed[i] ? " homed" : " unhomed"), t.FontSmall);
                p.Draw(v.HasValue ? v.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-", cols[i].Left, cols[i].Top + t.FontSmall + t.Unit + t.FontValue, t.FontValue, t.Text, true);
            }
        }

        static void HomingAndOrigin(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var b = ctx.Buttons;
            var inner = p.Card(cell, "Homing and origin");
            float y = inner.Top;
            var all = p.ButtonAuto(inner.Left, y, ConsoleIcon.Home, "Home all", ConsoleRole.Home, b.Home, "home", minWidth: t.Px(150f), tall: true);
            float x = all.Right + t.Gap;
            float small = t.Px(58f);
            var hx = new SKRect(x, y, x + small, y + t.PrimaryButtonHeight); p.Button(hx, ConsoleIcon.None, "X", ConsoleRole.Secondary, b.HomeX, "homex"); x += small + t.Unit;
            var hy = new SKRect(x, y, x + small, y + t.PrimaryButtonHeight); p.Button(hy, ConsoleIcon.None, "Y", ConsoleRole.Secondary, b.HomeY, "homey"); x += small + t.Unit;
            var hz = new SKRect(x, y, x + small, y + t.PrimaryButtonHeight); p.Button(hz, ConsoleIcon.None, "Z", ConsoleRole.Secondary, b.HomeZ, "homez");
            y += t.PrimaryButtonHeight + t.Unit;
            p.Draw("Per-axis homing leaves the arm configuration unknown until a full home", inner.Left, y + t.FontSmall, t.FontSmall, t.TextMuted);
            y += t.GapLarge;
            p.Divider(inner.Left, inner.Right, y);
            y += t.GapLarge;
            // the gate key is "capture", not the control id, so the default lookup would miss it
            p.ButtonAuto(inner.Left, y, ConsoleIcon.Target, "Set bed origin", ConsoleRole.Secondary, b.Capture, "setbedorigin",
                         tip: b.Capture ? null : b.Reasons.TryGetValue("capture", out string? captureWhy) ? captureWhy : null);
        }

        static void Points(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var b = ctx.Buttons; var view = ctx.View; var labels = ctx.PointLabels;
            var inner = p.Card(cell, "Calibration points");
            // Rows per page follow the card, with the pager's row reserved first whenever
            // the list needs one (the Recent prints rule): six fixed rows put the pager
            // below the surface at the default window size, and a pager that cannot be
            // reached hides every point after the first page. Paging stays view-local; the
            // form only steps the page, and it is clamped here.
            float pagerH = t.Unit + t.ButtonHeight;
            int perPage = Math.Max(1, Math.Min(ConsoleView.PointsPerPage, (int)(inner.Height / t.RowHeight)));
            if (labels.Count > perPage)
                perPage = Math.Max(1, Math.Min(ConsoleView.PointsPerPage, (int)((inner.Height - pagerH) / t.RowHeight)));
            int pages = Math.Max(1, (labels.Count + perPage - 1) / perPage);
            if (view.PointPage >= pages) view.PointPage = pages - 1;
            if (view.PointPage < 0) view.PointPage = 0;
            int first = view.PointPage * perPage;
            int last = Math.Min(labels.Count, first + perPage);
            float listW = Math.Min(inner.Width * 0.55f, t.Px(420f));
            float y = inner.Top;
            if (labels.Count == 0) p.Draw("No points wired to the points input", inner.Left, y + t.FontBody, t.FontBody, t.TextMuted);
            for (int i = first; i < last; i++)
            {
                var rect = new SKRect(inner.Left, y, inner.Left + listW, y + t.RowHeight);
                bool selected = view.SelectedPoint == i;
                p.Row(rect, "point:" + i.ToString(CultureInfo.InvariantCulture), selected);
                p.DrawClipped(labels[i], rect.Left + t.GapLarge, rect.MidY + t.FontBody * 0.36f, listW - t.GapLarge * 2, t.FontBody, selected ? t.Text : t.TextSecondary, selected);
                y += t.RowHeight;
            }
            if (pages > 1)
            {
                y += t.Unit;
                var prev = p.IconButton(inner.Left, y, ConsoleIcon.ArrowLeft, view.PointPage > 0, "pointprevious", t.ButtonHeight);
                var next = p.IconButton(prev.Right + t.Unit, y, ConsoleIcon.ArrowRight, view.PointPage < pages - 1, "pointnext", t.ButtonHeight);
                p.Draw("Page " + (view.PointPage + 1).ToString(CultureInfo.InvariantCulture) + " / " + pages.ToString(CultureInfo.InvariantCulture)
                       + "   (" + labels.Count.ToString(CultureInfo.InvariantCulture) + " points)", next.Right + t.GapLarge, y + t.ButtonHeight / 2 + t.FontSmall * 0.36f, t.FontSmall, t.TextMuted);
            }
            // go-to sits beside the list, with the selection named so it stays visible when paged away
            float gx = inner.Left + listW + t.PadLarge;
            bool hasSelection = view.SelectedPoint >= 0 && view.SelectedPoint < labels.Count;
            var go = p.ButtonAuto(gx, inner.Top, ConsoleIcon.Position, "Go to", ConsoleRole.Primary, b.GoTo && hasSelection, "goto", minWidth: t.Px(140f), tall: true);
            p.DrawClipped(hasSelection ? "Selected: " + labels[view.SelectedPoint] : "Selected: -", gx, go.Bottom + t.Gap + t.FontBody, inner.Right - gx, t.FontBody, t.TextSecondary);
        }
    }
}
