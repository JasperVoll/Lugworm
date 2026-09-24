using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console.Pages
{
    /// <summary>The logging workspace: retention controls, a recording/synchronisation
    /// banner, the session's job list, the structured view of the selected job and the
    /// live session events with a severity filter. Clear live view clears the displayed
    /// tail only; discard is a separate, confirmed action handled by the form.</summary>
    public static class LogsPage
    {
        public static void Paint(ConsolePaintContext ctx, SKRect area, bool narrow)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var view = ctx.View;
            var l = s.Logging;
            var selected = Selected(l, view);

            // ---- control row ----
            var row = ConsoleLayout.Take(ref area, t.ButtonHeight, t.Gap);
            float x = row.Left;
            var keep = p.ButtonAuto(x, row.Top, l.KeepAll ? ConsoleIcon.Check : ConsoleIcon.Save, "Keep all job logs", ConsoleRole.Secondary, true, "log:keepall", selected: l.KeepAll);
            x = keep.Right + t.Gap;
            var change = p.ButtonAuto(x, row.Top, ConsoleIcon.Folder, "Change location", ConsoleRole.Secondary, true, "log:changedest");
            x = change.Right + t.Gap;
            bool canSave = selected != null && l.Destination != null && !selected.Permanent
                        && selected.State != LogRetentionState.Saved && selected.State != LogRetentionState.Faulted && selected.State != LogRetentionState.Discarded;
            var save = p.ButtonAuto(x, row.Top, ConsoleIcon.Save, "Save this job", ConsoleRole.Primary, canSave, selected != null ? "log:save:" + selected.Id : "log:save:");
            x = save.Right + t.Gap;
            bool canRetry = false;
            foreach (var j in l.Jobs) if (j.State == LogRetentionState.WaitingToSync) canRetry = true;
            var retry = p.ButtonAuto(x, row.Top, ConsoleIcon.Retry, "Retry", ConsoleRole.Secondary, canRetry, "log:retry");
            x = retry.Right + t.Gap;
            // A Saved job's staging buffer was deleted when its copy was verified, so there
            // is nothing local left to export: the kept copy is at the destination.
            bool saved = selected != null && selected.State == LogRetentionState.Saved;
            p.ButtonAuto(x, row.Top, ConsoleIcon.Export, "Export copy", ConsoleRole.Secondary,
                         selected != null && selected.State != LogRetentionState.Discarded && !saved,
                         selected != null ? "log:export:" + selected.Id : "log:export:",
                         tip: saved ? "This job is saved in " + (selected!.DestinationDirectory ?? "the destination folder") + ". Copy it from there" : null);

            // ---- destination + banner ----
            var banner = ConsoleLayout.Take(ref area, t.ButtonHeight, t.GapLarge);
            if (l.Attention != null) p.Strip(banner, ConsoleIcon.Warning, l.Attention + "   (local buffer intact - Retry or Change location)", l.Attention.StartsWith("logging fault", StringComparison.Ordinal) ? t.Danger : t.Warning);
            else
            {
                string text;
                if (l.Current != null)
                {
                    string job = l.Current.Label + " - " + (l.Current.JobName.Length > 0 ? l.Current.JobName : l.Current.Id);
                    text = job + (l.Destination != null ? "   copies to " + l.Destination : "   no folder chosen");
                }
                else if (l.Destination == null)
                    text = "No folder chosen. Keep all job logs and Save this job both copy to a folder you pick - Change location opens the picker.";
                else if (l.KeepAll)
                    text = "Every job is copied to " + l.Destination;
                else
                    text = "Jobs are buffered locally; kept jobs are copied to " + l.Destination;
                p.Strip(banner, l.KeepAll ? ConsoleIcon.Sync : ConsoleIcon.Logs, text, l.KeepAll ? t.Primary : t.TextSecondary);
            }

            // ---- columns: jobs + details | events ----
            SKRect left, right;
            if (narrow)
            {
                left = ConsoleLayout.Take(ref area, area.Height * 0.55f, t.GapLarge);
                right = area;
            }
            else
            {
                var cols = ConsoleLayout.Columns(area, 2, t.GapLarge);
                left = cols[0]; right = cols[1];
            }
            Jobs(ctx, left, l, selected);
            Events(ctx, right);
        }

        static JobLogEntry? Selected(LogRetentionStatus l, ConsoleView view)
        {
            if (view.SelectedJobId != null)
                foreach (var j in l.Jobs) if (j.Id == view.SelectedJobId) return j;
            if (l.Current != null) return l.Current;
            return l.Jobs.Count > 0 ? l.Jobs[l.Jobs.Count - 1] : null;
        }

        static void Jobs(ConsolePaintContext ctx, SKRect cell, LogRetentionStatus l, JobLogEntry? selected)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State;
            float listH = Math.Min(cell.Height * 0.45f, t.RowHeight * 6 + t.Pad * 2 + t.FontHeader + t.Gap);
            var listRect = ConsoleLayout.Take(ref cell, listH, t.GapLarge);
            var inner = p.Card(listRect, "Jobs this session");
            float y = inner.Top;
            if (l.Jobs.Count == 0)
                p.Draw("No job observed yet - the next print records automatically", inner.Left, y + t.FontBody, t.FontBody, t.TextMuted);
            int capacity = Math.Max(1, (int)(inner.Height / t.RowHeight));
            int from = Math.Max(0, l.Jobs.Count - capacity);
            for (int i = from; i < l.Jobs.Count; i++)
            {
                var j = l.Jobs[i];
                var rect = new SKRect(inner.Left, y, inner.Right, y + t.RowHeight);
                bool isSel = selected != null && selected.Id == j.Id;
                p.Row(rect, "log:select:" + j.Id, isSel);
                string name = j.JobName.Length > 0 ? j.JobName : j.Id;
                float badgeW = p.Measure(j.Label, t.FontSmall, true) + t.Px(16f);
                p.DrawClipped((j.Recovered ? "recovered  " : "") + name, rect.Left + t.GapLarge, rect.MidY + t.FontBody * 0.36f, rect.Width - badgeW - t.GapLarge * 3, t.FontBody, isSel ? t.Text : t.TextSecondary, isSel);
                p.Badge(rect.Right - badgeW - t.Gap, rect.MidY - t.Px(11f), j.Label, ConsoleView.StateColor(j.State, t));
                y += t.RowHeight;
            }

            // structured view of the selected job
            var detail = p.Card(cell, selected == null ? "Job record" : "Job record - " + (selected.JobName.Length > 0 ? selected.JobName : selected.Id));
            y = detail.Top;
            if (selected == null) { p.Draw("Select a job to see its record", detail.Left, y + t.FontBody, t.FontBody, t.TextMuted); return; }
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "State: " + selected.Label + (selected.Permanent ? " (kept)" : " (temporary)") + (selected.Complete ? ", complete" : ", recording"),
                "Started: " + selected.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv) + "   id " + selected.Id,
                "Staged " + (selected.StagedBytes / 1024.0).ToString("0.#", inv) + " KB, synced " + (selected.SyncedBytes / 1024.0).ToString("0.#", inv) + " KB",
                "CSV: " + (selected.State == LogRetentionState.Saved && selected.DestinationDirectory != null
                    ? selected.DestinationDirectory + "\\print.csv" : selected.StagingDirectory + "\\print.csv"),
            };
            if (selected.DestinationDirectory != null && selected.State != LogRetentionState.Saved) lines.Add("Destination: " + selected.DestinationDirectory);
            if (selected.LastError != null) lines.Add("Last error: " + selected.LastError);
            foreach (var line in lines)
            {
                if (y + t.FontBody > detail.Bottom) break;
                bool alarm = line.StartsWith("Last error", StringComparison.Ordinal);
                p.DrawClipped(line, detail.Left, y + t.FontBody, detail.Width, t.FontSmall, alarm ? t.Warning : t.TextSecondary);
                y += t.FontBody + t.Unit;
            }
            y += t.Unit;
            if (s.JobLayers.Count > 0 && y + t.FontBody < detail.Bottom)
            {
                p.Draw("Layers (current job)", detail.Left, y + t.FontSmall, t.FontSmall, t.TextMuted);
                y += t.FontSmall + t.Unit;
                using (var mono = new SKPaint { IsAntialias = true, TextSize = t.FontSmall, Typeface = ConsoleTheme.Mono, Color = t.TextSecondary })
                {
                    int cap = Math.Max(0, (int)((detail.Bottom - y - t.ButtonHeight) / (t.FontSmall + t.Unit)));
                    int start = Math.Max(0, s.JobLayers.Count - cap);
                    for (int i = start; i < s.JobLayers.Count; i++)
                    {
                        ctx.Canvas.DrawText(ConsolePrimitives.Clip(s.JobLayers[i], detail.Width, mono), detail.Left, y + t.FontSmall, mono);
                        y += t.FontSmall + t.Unit;
                    }
                }
            }
            bool canDiscard = selected.Complete && selected.State != LogRetentionState.Saved && !(ctx.State.Logging.Current != null && ctx.State.Logging.Current.Id == selected.Id);
            if (detail.Bottom - t.ButtonHeight > y)
                p.ButtonAuto(detail.Left, detail.Bottom - t.ButtonHeight, ConsoleIcon.Trash, "Discard buffer...", ConsoleRole.Destructive, canDiscard, "log:discard:" + selected.Id);
        }

        static readonly (string Label, ConsoleEventCategory Category)[] EventFilters =
        {
            ("All", ConsoleEventCategory.All), ("Connection", ConsoleEventCategory.Connection),
            ("Job", ConsoleEventCategory.Job), ("Motion", ConsoleEventCategory.Motion),
            ("Firmware", ConsoleEventCategory.Firmware), ("Errors", ConsoleEventCategory.Errors),
        };

        static void Events(ConsolePaintContext ctx, SKRect cell)
        {
            var t = ctx.T; var p = ctx.P; var s = ctx.State; var view = ctx.View;
            var inner = p.Card(cell, "Session events");
            float clearW = t.Px(150f);
            float segH = t.ButtonHeight - t.Px(6f);
            var items = new List<(string, string, bool)>();
            foreach (var f in EventFilters) items.Add((f.Label, "log:filter:" + (int)f.Category, view.EventFilter == f.Category));
            // Six tabs need their measured width; when the card cannot hold them beside
            // "Clear live view" the button drops to its own line (2026-09-09: the labels
            // overran their slices at the default window width).
            bool oneRow = p.SegmentedWidth(items) + t.Gap + clearW <= inner.Width;
            var seg = oneRow
                ? new SKRect(inner.Left, inner.Top, inner.Right - clearW - t.Gap, inner.Top + segH)
                : new SKRect(inner.Left, inner.Top, inner.Right, inner.Top + segH);
            p.Segmented(seg, items);
            float clearY = oneRow ? inner.Top : seg.Bottom + t.Gap;
            p.ButtonAuto(inner.Right - clearW, clearY, ConsoleIcon.None, "Clear live view", ConsoleRole.Secondary, true, "clearlog", minWidth: clearW);
            float y = (oneRow ? seg.Bottom : clearY + t.ButtonHeight) + t.GapLarge;
            // The raw log is unchanged on disk and on the `log` component output; this
            // collapses only what is drawn here, so a repeated line (arm configuration
            // unknown fired 21 times in a row during the 2026-09-09 dome startup) shows as
            // one line with a count and time range instead of twenty-one identical ones.
            var grouped = ConsoleEventGrouping.Group(s.LogTail);
            var lines = new List<ConsoleEventGrouping.GroupedEvent>();
            for (int i = grouped.Count - 1; i >= 0 && lines.Count < 200; i--)
                if (ConsoleEventGrouping.Matches(view.EventFilter, grouped[i].Category)) lines.Add(grouped[i]);
            lines.Reverse();
            using (var mono = new SKPaint { IsAntialias = true, TextSize = t.FontSmall, Typeface = ConsoleTheme.Mono, Color = t.TextSecondary })
            {
                float lineH = t.FontSmall + t.Unit;
                int capacity = Math.Max(1, (int)((inner.Bottom - y) / lineH));
                int from = Math.Max(0, lines.Count - capacity);
                if (lines.Count == 0)
                {
                    string empty = view.EventFilter == ConsoleEventCategory.All ? "(log empty)"
                        : view.EventFilter == ConsoleEventCategory.Errors ? "No errors"
                        : "No " + view.EventFilter.ToString().ToLowerInvariant() + " events";
                    p.Draw(empty, inner.Left, y + t.FontSmall, t.FontSmall, t.TextMuted);
                }
                for (int i = from; i < lines.Count; i++)
                {
                    string text = lines[i].Display();
                    mono.Color = lines[i].Category == ConsoleEventCategory.Errors || DashboardPage.IsAlarm(lines[i].Message) ? t.Warning : t.TextSecondary;
                    ctx.Canvas.DrawText(ConsolePrimitives.Clip(text, inner.Width, mono), inner.Left, y + t.FontSmall, mono);
                    y += lineH;
                }
            }
        }
    }
}
