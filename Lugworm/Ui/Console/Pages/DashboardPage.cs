using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console.Pages
{
    /// <summary>The launch page: is the printer connected, is it ready, what is printing,
    /// what should the operator do next. Cards for readiness, position, the active job
    /// and logging, a short list of recent events, and ONE state-derived primary action
    /// that delegates to the same handler and gate as the full-page control.</summary>
    public static class DashboardPage
    {
        public static void Paint(ConsolePaintContext ctx, SKRect area, bool narrow)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var b = ctx.Buttons;

            // ---- primary action row ----
            var (id, label, enabled) = ConsoleNextAction.Decide(s, b);
            bool pending = s.PendingAction != null;
            // The reason a disabled primary action cannot run yet gets its own reserved
            // line under the button (never drawn until we know it is needed), so it can
            // never overlap the cards below and never floats loose over other content.
            string? why = null;
            bool showReason = id.Length > 0 && !enabled && !pending && b.Reasons.TryGetValue(id, out why) && !string.IsNullOrEmpty(why);
            float reasonH = showReason ? t.FontSmall + t.Unit : 0f;
            var row = ConsoleLayout.Take(ref area, t.PrimaryButtonHeight + t.FontSmall + t.Gap + reasonH, t.GapLarge);
            p.Draw("Next", row.Left, row.Top + t.FontSmall, t.FontSmall, t.TextMuted);
            float y = row.Top + t.FontSmall + t.Unit;
            if (id.Length > 0)
            {
                var rect = p.ButtonAuto(row.Left, y, IconFor(id), pending ? s.PendingAction! : label,
                                        id == "reset" ? ConsoleRole.Warning : id.StartsWith("tab:", StringComparison.Ordinal) ? ConsoleRole.Secondary : ConsoleRole.Primary,
                                        enabled && !pending, id, minWidth: t.Px(200f), pending: pending, tall: true);
                if (showReason)
                    p.DrawClipped(why!, row.Left, rect.Bottom + t.Unit + t.FontSmall, row.Width, t.FontSmall, t.TextSecondary);
            }
            else p.Draw(label, row.Left, y + t.PrimaryButtonHeight / 2 + t.FontButton * 0.36f, t.FontButton, t.TextSecondary, true);

            // ---- four cards ----
            float cardH = t.Px(150f);
            var grid = ConsoleLayout.Take(ref area, narrow ? cardH * 4 + t.GapLarge * 3 : cardH * 2 + t.GapLarge, t.GapLarge);
            SKRect[] cells;
            if (narrow)
            {
                cells = new SKRect[4];
                for (int i = 0; i < 4; i++) cells[i] = new SKRect(grid.Left, grid.Top + i * (cardH + t.GapLarge), grid.Right, grid.Top + i * (cardH + t.GapLarge) + cardH);
            }
            else
            {
                var top = new SKRect(grid.Left, grid.Top, grid.Right, grid.Top + cardH);
                var bottom = new SKRect(grid.Left, grid.Top + cardH + t.GapLarge, grid.Right, grid.Top + cardH * 2 + t.GapLarge);
                var c1 = ConsoleLayout.Columns(top, 2, t.GapLarge);
                var c2 = ConsoleLayout.Columns(bottom, 2, t.GapLarge);
                cells = new[] { c1[0], c1[1], c2[0], c2[1] };
            }
            Readiness(ctx, cells[0]);
            Position(ctx, cells[1]);
            Job(ctx, cells[2]);
            Logging(ctx, cells[3]);

            // ---- recent events ----
            if (area.Height > t.Px(80f))
            {
                var inner = p.Card(area, "Recent events");
                var lines = Recent(s.LogTail, 6);
                float ly = inner.Top;
                if (lines.Count == 0) p.Draw("Nothing logged yet", inner.Left, ly + t.FontBody, t.FontBody, t.TextMuted);
                foreach (var line in lines)
                {
                    if (ly + t.FontBody > inner.Bottom) break;
                    bool alarm = IsAlarm(line);
                    p.DrawClipped(line, inner.Left, ly + t.FontBody, inner.Width, t.FontSmall, alarm ? t.Warning : t.TextSecondary, alarm);
                    ly += t.FontBody + t.Unit;
                }
            }
        }

        static void Readiness(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            var inner = p.Card(cell, "Readiness");
            float y = inner.Top + t.FontBody;
            p.Lamp(inner.Left, y, ConsoleView.ConnectionColor(s.Connection, t), "Connection: " + ConsoleView.Title(PrintConsoleButtons.Lower(s.Connection)));
            y += t.FontBody + t.Gap;
            string state = s.MachineState.ToString().ToLowerInvariant();
            p.Lamp(inner.Left, y, s.MachineState == MachineStatus.Idle ? t.Success
                                 : s.MachineState == MachineStatus.Halted || s.MachineState == MachineStatus.Off ? t.Danger : t.Warning,
                   "Machine: " + ConsoleView.Title(state));
            y += t.FontBody + t.Gap;
            string homed = "Homed: X " + YesNo(s.XHomed) + "  Y " + YesNo(s.YHomed) + "  Z " + YesNo(s.ZHomed);
            p.Lamp(inner.Left, y, s.AllHomed ? t.Success : t.Warning, homed);
            y += t.FontBody + t.Gap;
            p.Lamp(inner.Left, y, s.ArmConfigurationKnown ? t.Success : t.TextMuted, "Arm configuration: " + (s.ArmConfigurationKnown ? "known" : "unknown"));
        }

        static void Position(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            var inner = p.Card(cell, "Position (machine, mm)");
            var cols = ConsoleLayout.Columns(new SKRect(inner.Left, inner.Top, inner.Right, inner.Top + t.FontValue + t.FontSmall + t.Gap), 3, t.Gap);
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                double? v = s.Position.HasValue ? (i == 0 ? s.Position.Value.X : i == 1 ? s.Position.Value.Y : s.Position.Value.Z) : (double?)null;
                p.Draw(axes[i], cols[i].Left, cols[i].Top + t.FontSmall, t.FontSmall, t.TextMuted);
                p.Draw(v.HasValue ? v.Value.ToString("0.00", CultureInfo.InvariantCulture) : "-", cols[i].Left, cols[i].Top + t.FontSmall + t.Unit + t.FontValue, t.FontValue, t.Text, true);
            }
            float y = inner.Top + t.FontValue + t.FontSmall + t.GapLarge + t.FontSmall;
            string bedOrigin = s.BedOrigin.HasValue ? "Bed origin captured" : "";
            string zZero = s.ZZero.HasValue ? "Z zero captured" : "";
            string captured = bedOrigin.Length > 0 && zZero.Length > 0 ? bedOrigin + "   " + zZero : bedOrigin + zZero;
            if (captured.Length > 0) p.Draw(captured, inner.Left, y, t.FontSmall, t.TextSecondary);
        }

        static void Job(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            var inner = p.Card(cell, "Active job");
            float y = inner.Top + t.FontBody;
            if (s.JobFile.Length == 0)
            {
                p.Draw("No job running", inner.Left, y, t.FontBody, t.TextMuted);
                y += t.FontBody + t.Gap;
                p.DrawClipped(s.GcodeLines > 0
                    ? s.GcodeLines.ToString(CultureInfo.InvariantCulture) + " G-code lines wired" + (s.Uploaded != null ? ", uploaded: " + s.Uploaded.Path : ", not uploaded")
                    : "No G-code wired", inner.Left, y, inner.Width, t.FontSmall, t.TextSecondary);
                return;
            }
            p.DrawClipped(s.JobFile, inner.Left, y, inner.Width, t.FontBody, t.Text, true);
            y += t.FontBody + t.Gap;
            p.Progress(new SKRect(inner.Left, y, inner.Right, y + t.Px(10f)), s.Progress);
            y += t.Px(10f) + t.Gap + t.FontBody;
            p.Draw(PrintConsoleState.ProgressText(s.Progress, s.Layer, s.LayerCount)
                   + "   " + ConsoleView.Title(s.MachineState.ToString().ToLowerInvariant()), inner.Left, y, t.FontBody, t.TextSecondary);
        }

        static void Logging(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            var inner = p.Card(cell, "Logging");
            var l = s.Logging;
            float y = inner.Top;
            p.Badge(inner.Left, y, l.Badge, ConsoleView.BadgeColor(l.Badge, t));
            y += t.Px(22f) + t.Gap + t.FontBody;
            p.Draw("Every job is recorded to a local buffer.", inner.Left, y, t.FontBody, t.Text);
            y += t.FontBody + t.Unit;
            p.DrawClipped(l.Destination != null ? "Kept jobs are copied to " + l.Destination : "No folder chosen - Logs → Change location picks one (local or network)", inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.TextSecondary);
            y += t.FontSmall + t.Gap;
            if (l.Attention != null) p.DrawClipped(l.Attention, inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.Warning, true);
            else if (l.Current != null) p.DrawClipped("Current: " + (l.Current.JobName.Length > 0 ? l.Current.JobName : l.Current.Id) + " - " + l.Current.Label, inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.TextSecondary);
        }

        internal static ConsoleIcon IconFor(string id)
        {
            switch (id)
            {
                case "connect": return ConsoleIcon.Connection;
                case "home": return ConsoleIcon.Home;
                case "validate": return ConsoleIcon.Validate;
                case "upload": return ConsoleIcon.Upload;
                case "start": return ConsoleIcon.Start;
                case "resume": return ConsoleIcon.Resume;
                case "reset": return ConsoleIcon.Reset;
                case "tab:1": return ConsoleIcon.Control;
                case "tab:2": return ConsoleIcon.Job;
                default: return ConsoleIcon.None;
            }
        }

        internal static List<string> Recent(IReadOnlyList<string> log, int max)
        {
            var result = new List<string>();
            // warnings and failures first, then the newest of the rest
            for (int i = log.Count - 1; i >= 0 && result.Count < max; i--)
                if (IsAlarm(log[i])) result.Add(log[i]);
            for (int i = log.Count - 1; i >= 0 && result.Count < max; i--)
                if (!IsAlarm(log[i])) result.Add(log[i]);
            return result;
        }

        internal static bool IsAlarm(string line)
#if NETFRAMEWORK
            => line.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("M112", StringComparison.Ordinal) >= 0;
#else
            => line.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warn", StringComparison.OrdinalIgnoreCase)
            || line.Contains("M112", StringComparison.Ordinal);
#endif

        static string YesNo(bool v) => v ? "yes" : "no";
    }
}
