using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console.Pages
{
    /// <summary>A larger native command field (placed by the form over the reserved
    /// rectangle), an icon-plus-label Send, a full-width amber confirmation strip naming
    /// the exact armed command and its timeout, the last reply and a readable history.
    /// The second press keeps the existing terminal policy; this page only draws it.</summary>
    public static class TerminalPage
    {
        public static void Paint(ConsolePaintContext ctx, SKRect area)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var b = ctx.Buttons;
            float sendW = t.Px(150f);
            var input = new SKRect(area.Left, area.Top, area.Right - sendW - t.Gap, area.Top + t.PrimaryButtonHeight);
            ctx.View.SetTerminalInput(input, true);
            bool armed = TerminalPolicy.IsConfirmArmed(s.TerminalConfirmUntilUtc, DateTime.UtcNow);
            p.Button(new SKRect(input.Right + t.Gap, area.Top, area.Right, area.Top + t.PrimaryButtonHeight), ConsoleIcon.Terminal,
                     armed ? "Confirm send" : "Send", armed ? ConsoleRole.Warning : ConsoleRole.Primary, b.Send, "sendbox",
                     pending: s.PendingAction != null && s.PendingAction.StartsWith("sending", StringComparison.Ordinal),
                     // the gate key is "send", not the control id, so the default lookup would miss it
                     tip: b.Send ? null : b.Reasons.TryGetValue("send", out string? sendWhy) ? sendWhy : null);
            float y = area.Top + t.PrimaryButtonHeight + t.Unit + t.Gap;

            if (armed)
            {
                double remaining = Math.Max(0, (s.TerminalConfirmUntilUtc - DateTime.UtcNow).TotalSeconds);
                string text = "Press Confirm send within " + remaining.ToString("0", CultureInfo.InvariantCulture) + " s to send: "
                            + (s.TerminalArmedText.Length > 0 ? s.TerminalArmedText : "(the line in the box)")
                            + "   - motion and configuration commands invalidate the tracked arm configuration";
                p.Strip(new SKRect(area.Left, y, area.Right, y + t.ButtonHeight), ConsoleIcon.Warning, text, t.Warning);
                y += t.ButtonHeight + t.GapLarge;
            }
            else
            {
                p.Draw("Diagnostics (M115, M122, M114 ...) send at once. Motion, configuration and job commands need a second press to confirm.",
                       area.Left, y + t.FontSmall, t.FontSmall, t.TextMuted);
                y += t.FontSmall + t.GapLarge;
            }

            var replyRect = new SKRect(area.Left, y, area.Right, y + t.Px(64f));
            var inner = p.Card(replyRect, null);
            p.Draw("last reply", inner.Left, inner.Top + t.FontSmall, t.FontSmall, t.TextMuted);
            using (var mono = new SKPaint { IsAntialias = true, TextSize = t.FontBody, Typeface = ConsoleTheme.Mono, Color = t.Text })
                ctx.Canvas.DrawText(ConsolePrimitives.Clip(s.LastReply.Length > 0 ? s.LastReply : "-", inner.Width, mono), inner.Left, inner.Top + t.FontSmall + t.Unit + t.FontBody, mono);
            y = replyRect.Bottom + t.GapLarge;

            var history = new SKRect(area.Left, y, area.Right, area.Bottom);
            if (history.Height < t.Px(60f)) return;
            var hi = p.Card(history, "History");
            var lines = new List<string>();
            for (int i = s.LogTail.Count - 1; i >= 0 && lines.Count < 40; i--) lines.Add(s.LogTail[i]);
            lines.Reverse();
            using (var mono = new SKPaint { IsAntialias = true, TextSize = t.FontSmall, Typeface = ConsoleTheme.Mono, Color = t.Text })
            {
                float lineH = t.FontSmall + t.Unit;
                int capacity = Math.Max(1, (int)(hi.Height / lineH));
                int from = Math.Max(0, lines.Count - capacity);
                float ly = hi.Top;
                if (lines.Count == 0) p.Draw("(nothing sent yet)", hi.Left, ly + t.FontSmall, t.FontSmall, t.TextMuted);
                for (int i = from; i < lines.Count; i++)
                {
                    bool sent = lines[i].Contains("] > ");
                    mono.Color = sent ? t.Primary : DashboardPage.IsAlarm(lines[i]) ? t.Warning : t.TextSecondary;
                    ctx.Canvas.DrawText(ConsolePrimitives.Clip(lines[i], hi.Width, mono), hi.Left, ly + t.FontSmall, mono);
                    ly += lineH;
                }
            }
        }
    }
}
