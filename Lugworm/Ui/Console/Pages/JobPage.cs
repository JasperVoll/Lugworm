using System;
using System.Globalization;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using SkiaSharp;

namespace Lugworm.Ui.Console.Pages
{
    /// <summary>The explicit Validate -> Upload -> Start workflow. A completed stage shows
    /// its evidence, the active stage is prominent, a blocked stage explains itself. No
    /// stage may look passed before the state confirms it. Cancel is separated.</summary>
    public static class JobPage
    {
        public static void Paint(ConsolePaintContext ctx, SKRect area, bool narrow)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var b = ctx.Buttons;
            bool pending = s.PendingAction != null;
            var v = s.Validation;
            bool validated = v != null && !v.Danger;
            bool uploaded = s.Uploaded != null && s.Uploaded.Hash == s.GcodeHash;

            if (area.Width <= 0 || area.Height <= 0) return;
            float pauseW = Math.Min(area.Width, p.ButtonWidth(ConsoleIcon.Pause, "Pause", t.Px(110f)));
            float resumeW = Math.Min(area.Width, p.ButtonWidth(ConsoleIcon.Resume, "Resume print", t.Px(140f)));
            float cancelW = Math.Min(area.Width, p.ButtonWidth(ConsoleIcon.Cancel, "Cancel job", t.Px(150f)));
            bool splitPauseResume = pauseW + t.Gap + resumeW > area.Width;
            bool splitCancel = splitPauseResume || pauseW + t.Gap + resumeW + t.GapLarge + cancelW > area.Width;
            int controlRows = 1 + (splitPauseResume ? 1 : 0) + (splitCancel ? 1 : 0);
            // One extra row above the running controls: Go to print start and its caption.
            float startRowH = t.ButtonHeight + t.Gap;
            float controlsH = startRowH + t.ButtonHeight * controlRows + t.Gap * (controlRows - 1);
            // Reserve the complete progress bar and its caption before the Start button.
            float detailH = t.Unit + t.Px(12f) + t.FontSmall;
            float stageH = t.Pad * 2 + t.FontHeader + t.Gap + t.FontBody + t.Gap
                           + detailH + t.GapLarge + t.PrimaryButtonHeight;
            // Stack only when all stages and running controls fit; shorter windows retain columns.
            bool stacked = narrow && area.Height >= stageH * 3 + t.GapLarge * 3 + controlsH;
            if (area.Height < stageH + t.GapLarge + controlsH) return;
            var row = ConsoleLayout.Take(ref area, stacked ? stageH * 3 + t.GapLarge * 2 : stageH, t.GapLarge);
            SKRect[] cells = stacked
                ? new[] { new SKRect(row.Left, row.Top, row.Right, row.Top + stageH),
                          new SKRect(row.Left, row.Top + stageH + t.GapLarge, row.Right, row.Top + stageH * 2 + t.GapLarge),
                          new SKRect(row.Left, row.Top + (stageH + t.GapLarge) * 2, row.Right, row.Top + stageH * 3 + t.GapLarge * 2) }
                : ConsoleLayout.Columns(row, 3, t.GapLarge);

            // ---- 1 Validate (also the "Print again" entry point for a loaded past run) ----
            {
                var run = s.SelectedRun;
                var inner = p.Card(cells[0], run != null ? "1  Print again" : "1  Validate", elevated: v == null);
                float y = inner.Top;
                string status = v == null ? (s.GcodeLines > 0 ? "not validated" : "no G-code wired")
                              : v.Danger ? "REFUSED" : v.Warned ? "passed with warnings" : "passed";
                p.Lamp(inner.Left, y + t.FontBody, v == null ? t.TextMuted : v.Danger ? t.Danger : v.Warned ? t.Warning : t.Success, status, maxWidth: inner.Width);
                y += t.FontBody + t.Gap;
                // The facts of what would run: for a loaded run, its own name, size, hash
                // and how it ended the first time - a reprint is the same bytes or nothing.
                string detail = run != null
                    ? run.JobName + "  " + s.GcodeLines.ToString(CultureInfo.InvariantCulture) + " lines  "
                      + run.ShortHash + "  was " + ConsoleLists.Outcome(run)
                    : s.GcodeLines.ToString(CultureInfo.InvariantCulture) + " lines";
                p.DrawClipped(detail, inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.TextSecondary);
                y += detailH + t.GapLarge;
                // Print again is NOT a new command: it runs this same Validate -> verified
                // Upload -> explicit Start flow on the loaded bytes, through the same gates.
                p.ButtonAuto(inner.Left, y, ConsoleIcon.Validate,
                             pending && s.PendingAction == "validating" ? "Validating..." : run != null ? "Print again" : "Validate",
                             validated ? ConsoleRole.Secondary : ConsoleRole.Primary, b.Validate, "validate", minWidth: t.Px(130f), pending: pending && s.PendingAction == "validating", maxWidth: inner.Width);
            }
            // ---- 2 Upload ----
            {
                var inner = p.Card(cells[1], "2  Upload", elevated: validated && !uploaded);
                float y = inner.Top;
                string status = s.Uploaded == null ? "nothing uploaded" : uploaded ? "uploaded" : "uploaded file is stale";
                p.Lamp(inner.Left, y + t.FontBody, s.Uploaded == null ? t.TextMuted : uploaded ? t.Success : t.Warning, status, maxWidth: inner.Width);
                y += t.FontBody + t.Gap;
                p.DrawClipped(s.Uploaded != null ? s.Uploaded.Path + " (" + s.Uploaded.Size.ToString(CultureInfo.InvariantCulture) + " bytes)" : "-",
                              inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.TextSecondary);
                y += detailH + t.GapLarge;
                bool up = pending && s.PendingAction != null && s.PendingAction.StartsWith("uploading", StringComparison.Ordinal);
                p.ButtonAuto(inner.Left, y, ConsoleIcon.Upload, up ? "Uploading..." : "Upload", uploaded ? ConsoleRole.Secondary : ConsoleRole.Primary,
                             b.Upload, "upload", minWidth: t.Px(130f), pending: up, maxWidth: inner.Width);
            }
            // ---- 3 Start ----
            {
                bool printing = s.MachineState == MachineStatus.Processing || s.MachineState == MachineStatus.Simulating;
                var inner = p.Card(cells[2], "3  " + (s.DryRun ? "Start (dry run, M37)" : "Start"), elevated: uploaded && !printing);
                float y = inner.Top;
                string status = printing ? (s.MachineState == MachineStatus.Simulating ? "simulating" : "printing")
                              : s.MachineState == MachineStatus.Paused ? "paused" : s.JobFile.Length > 0 ? s.MachineState.ToString().ToLowerInvariant() : "idle";
                p.Lamp(inner.Left, y + t.FontBody, printing ? t.Primary : s.MachineState == MachineStatus.Paused ? t.Warning : t.TextMuted, status, maxWidth: inner.Width);
                y += t.FontBody + t.Gap;
                if (s.JobFile.Length > 0)
                {
                    p.Progress(new SKRect(inner.Left, y + t.Unit, inner.Right, y + t.Unit + t.Px(8f)), s.Progress);
                    p.DrawClipped(PrintConsoleState.ProgressText(s.Progress, s.Layer, s.LayerCount),
                           inner.Left, y + t.Px(12f) + t.FontSmall + t.Unit, inner.Width, t.FontSmall, t.TextSecondary);
                }
                else p.DrawClipped("-", inner.Left, y + t.FontSmall, inner.Width, t.FontSmall, t.TextSecondary);
                y += detailH + t.GapLarge;
                bool starting = pending && s.PendingAction != null && s.PendingAction.StartsWith("starting", StringComparison.Ordinal);
                p.ButtonAuto(inner.Left, y, ConsoleIcon.Start, starting ? "Starting..." : s.StartLabel, ConsoleRole.Primary, b.Start, "start",
                             minWidth: t.Px(130f), pending: starting, tall: true, maxWidth: inner.Width);
            }

            // ---- Go to print start: between a validation and a Start, so the operator can
            // see where the first layer lands. Same planner and gates as the Control page's
            // go-to; the target is the validated job's own first extruding move at Z 0.
            var controls = ConsoleLayout.Take(ref area, controlsH, t.GapLarge);
            string? startWhy = b.GoToStart ? null : b.Reasons.TryGetValue("gotostart", out string? reason) ? reason : null;
            var startBtn = p.ButtonAuto(controls.Left, controls.Top, ConsoleIcon.Position, "Go to print start", ConsoleRole.Secondary,
                                        b.GoToStart, "gotostart", minWidth: t.Px(150f), maxWidth: controls.Width, tip: startWhy);
            p.DrawClipped(PrintStartLabel(ctx), startBtn.Right + t.GapLarge, controls.Top + t.ButtonHeight / 2 + t.FontSmall * 0.36f,
                          controls.Right - startBtn.Right - t.GapLarge, t.FontSmall, t.TextSecondary);
            controls.Top += startRowH;

            // ---- running controls: pause / resume together, cancel apart ----
            var pause = p.ButtonAuto(controls.Left, controls.Top, ConsoleIcon.Pause, "Pause", ConsoleRole.Secondary, b.Pause, "pause", minWidth: pauseW, maxWidth: pauseW);
            float resumeY = splitPauseResume ? pause.Bottom + t.Gap : controls.Top;
            var resume = p.ButtonAuto(splitPauseResume ? controls.Left : pause.Right + t.Gap, resumeY,
                                      ConsoleIcon.Resume, "Resume print", ConsoleRole.Secondary, b.Resume, "resume", minWidth: resumeW, maxWidth: resumeW);
            float cancelY = splitCancel ? resume.Bottom + t.Gap : controls.Top;
            p.ButtonAuto(controls.Right - cancelW, cancelY, ConsoleIcon.Cancel, "Cancel job", ConsoleRole.Destructive,
                         b.Stop, "stop", minWidth: cancelW, maxWidth: cancelW);

            // ---- Recent prints: below the running controls, above the messages ----
            // It takes only what is left over a readable messages card, so the two never
            // overlap and the footer stays reachable at every DPI.
            {
                var runs = s.RecentRuns;
                bool pager = runs.Count > 3;
                float wanted = ConsoleLists.WantedHeight(t, Math.Min(runs.Count, 4), s.SelectedRun != null, pager);
                float spare = area.Height - t.Px(60f) - t.GapLarge;
                float recentH = Math.Min(wanted, spare);
                float minimum = t.Pad * 2 + t.FontHeader + t.Gap + ConsoleLists.RowHeight(t);
                if (recentH >= minimum)
                {
                    var recentRect = ConsoleLayout.Take(ref area, recentH, t.GapLarge);
                    ConsoleLists.RecentPrints(ctx, recentRect, runs, s.SelectedRun, s.RunSelectionNote,
                                              b.History, ctx.View.RunPage, out int pages);
                    ctx.View.RunPageCount = pages;
                }
            }

            // ---- validation messages ----
            if (area.Height < t.Px(60f)) return;
            var inner2 = p.Card(area, "Validation messages");
            float yy = inner2.Top;
            if (v == null) { p.Draw("run Validate to see the checks", inner2.Left, yy + t.FontBody, t.FontBody, t.TextMuted); return; }
            int shown = ctx.View.ShowValidationMessages ? v.Messages.Count : Math.Min(v.Messages.Count, 3);
            for (int i = 0; i < shown; i++)
            {
                if (yy + t.FontBody > inner2.Bottom - t.ButtonHeight) break;
                bool alarm = i == 0 ? v.Danger : DashboardPage.IsAlarm(v.Messages[i]);
                p.DrawClipped(v.Messages[i], inner2.Left, yy + t.FontBody, inner2.Width, t.FontSmall, alarm ? t.Warning : t.TextSecondary, i == 0);
                yy += t.FontBody + t.Unit;
            }
            if (v.Messages.Count > 3)
            {
                yy += t.Unit;
                p.ButtonAuto(inner2.Left, Math.Min(yy, inner2.Bottom - t.ButtonHeight), ConsoleIcon.None,
                             ctx.View.ShowValidationMessages ? "Show fewer" : "Show all " + v.Messages.Count.ToString(CultureInfo.InvariantCulture) + " messages",
                             ConsoleRole.Secondary, true, "job:messages", maxWidth: inner2.Width);
            }
        }

        /// <summary>Names the validated job's print start in both frames: the machine
        /// coordinates the move will be commanded in, and - once the mechanism is
        /// established - the Rhino coordinates the operator designed in (identity for a
        /// Cartesian profile, the y = -x mirror for the SCARA). With no frame known only
        /// the machine numbers are claimed, like the jog help.</summary>
        static string PrintStartLabel(ConsolePaintContext ctx)
        {
            var v = ctx.State.Validation;
            if (v == null) return "Print start: - (validate the job first)";
            if (v.Danger) return "Print start: - (validation refused the job)";
            if (!v.StartPoint.HasValue) return "Print start: - (the job has no extruding move)";
            var inv = CultureInfo.InvariantCulture;
            var m = v.StartPoint.Value;
            string machine = "machine (" + m.X.ToString("0.#", inv) + ", " + m.Y.ToString("0.#", inv) + ") at Z 0";
            if (!ctx.FrameKnown) return "Print start: " + machine;
            double rx = m.X, ry = m.Y;
            if (!ctx.Cartesian) MachineFrame.ToRhino(m.X, m.Y, out rx, out ry);
            return "Print start: Rhino (" + rx.ToString("0.#", inv) + ", " + ry.ToString("0.#", inv) + ") - " + machine;
        }
    }
}
