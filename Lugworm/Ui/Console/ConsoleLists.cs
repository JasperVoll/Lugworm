using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>List surfaces the Job page needs and the shared primitives do not have:
    /// the Recent prints list and its selection strip. A pure view - every enabled flag
    /// comes from the <see cref="ButtonSet"/> the transaction uses, and nothing here reads
    /// or writes a machine fact.</summary>
    public static class ConsoleLists
    {
        /// <summary>Height one recent-print row needs: two text lines plus breathing room.</summary>
        public static float RowHeight(ConsoleTheme t) => t.FontBody + t.FontSmall + t.Px(12f);

        /// <summary>Height the card wants for <paramref name="rows"/> rows, a selection
        /// strip and a pager. The caller decides how much of it there is.</summary>
        public static float WantedHeight(ConsoleTheme t, int rows, bool selection, bool pager)
            => t.Pad * 2 + t.FontHeader + t.Gap
             + (selection ? t.ButtonHeight + t.FontSmall + t.Gap * 2 : 0)
             + Math.Max(1, rows) * RowHeight(t)
             + (pager ? t.ButtonHeight + t.Gap : 0);

        /// <summary>Draws the Recent prints card into <paramref name="rect"/>. Selection is
        /// by run id, never by name: two prints of one file are two runs.</summary>
        public static void RecentPrints(ConsolePaintContext ctx, SKRect rect, IReadOnlyList<RecentPrintEntry> runs,
                                        RecentPrintEntry? selected, string? note, bool canSelect, int page,
                                        out int pageCount)
        {
            var t = ctx.T; var p = ctx.P;
            pageCount = 1;
            var inner = p.Card(rect, "Recent prints");
            float y = inner.Top;

            if (selected != null)
            {
                // What is loaded, and what a reprint of it would run. The hash is shown
                // because it is the only thing that identifies these bytes.
                var clear = p.ButtonAuto(inner.Right - Math.Min(inner.Width, p.ButtonWidth(ConsoleIcon.None, "Clear selection", t.Px(120f))),
                                         y, ConsoleIcon.None, "Clear selection", ConsoleRole.Secondary, canSelect, "history:clear",
                                         minWidth: t.Px(120f), maxWidth: inner.Width);
                p.DrawClipped("Loaded: " + selected.JobName + "  " + selected.ShortHash,
                              inner.Left, y + t.ButtonHeight * 0.5f + t.FontBody * 0.36f,
                              Math.Max(0, clear.Left - inner.Left - t.Gap), t.FontBody, t.Text, true);
                y += t.ButtonHeight + t.Gap;
                if (!string.IsNullOrEmpty(note))
                {
                    p.DrawClipped(note!, inner.Left, y + t.FontSmall, inner.Width, t.FontSmall,
                                  selected.ArchiveAvailable ? t.Warning : t.Danger);
                    y += t.FontSmall + t.Gap;
                }
            }

            if (runs.Count == 0)
            {
                p.Draw("No print recorded yet - every job is logged automatically", inner.Left, y + t.FontBody, t.FontBody, t.TextMuted);
                return;
            }

            float rowH = RowHeight(t);
            float listBottom = inner.Bottom;
            bool paged = false;
            int perPage = Math.Max(1, (int)((listBottom - y) / rowH));
            if (runs.Count > perPage)
            {
                // Reserve the pager, then recompute: a pager that eats the last row must
                // not hide a run behind a control that exists to reach it.
                listBottom = inner.Bottom - t.ButtonHeight - t.Gap;
                perPage = Math.Max(1, (int)((listBottom - y) / rowH));
                paged = runs.Count > perPage;
            }
            pageCount = Math.Max(1, (runs.Count + perPage - 1) / perPage);
            int current = Math.Max(0, Math.Min(page, pageCount - 1));
            int from = current * perPage;

            for (int i = from; i < runs.Count && i < from + perPage; i++)
            {
                var run = runs[i];
                var row = new SKRect(inner.Left, y, inner.Right, y + rowH);
                if (row.Bottom > listBottom) break;
                bool reprintable = ReprintPolicy.RefuseSelection(run) == null;
                bool enabled = canSelect && reprintable;
                bool isSelected = selected != null && string.Equals(selected.Id, run.Id, StringComparison.Ordinal);
                p.Row(row, "history:select:" + run.Id, isSelected, enabled);
                float x = row.Left + t.Gap;
                float w = Math.Max(0, row.Right - x - t.Gap);
                p.DrawClipped(run.JobName, x, row.Top + t.FontBody, w * 0.6f, t.FontBody, enabled ? t.Text : t.TextSecondary, isSelected);
                string when = run.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                p.DrawClipped(when, x + w * 0.62f, row.Top + t.FontBody, w * 0.38f, t.FontSmall, t.TextSecondary);
                string second = Outcome(run) + "  -  " + run.MachineText
                              + (run.ArchiveAvailable
                                 ? "  -  " + run.ShortHash + ", " + Bytes(run.GcodeBytes)
                                 : "  -  not available for reprint");
                p.DrawClipped(second, x, row.Top + t.FontBody + t.FontSmall + t.Px(4f), w, t.FontSmall,
                              run.ArchiveAvailable ? t.TextSecondary : t.TextMuted);
                y = row.Bottom;
            }

            if (!paged) return;
            float py = Math.Min(y + t.Gap, inner.Bottom - t.ButtonHeight);
            var newer = p.ButtonAuto(inner.Left, py, ConsoleIcon.None, "Newer", ConsoleRole.Secondary,
                                     current > 0, "historyprevious", minWidth: t.Px(90f), maxWidth: inner.Width * 0.45f);
            p.ButtonAuto(newer.Right + t.Gap, py, ConsoleIcon.None, "Older", ConsoleRole.Secondary,
                         current < pageCount - 1, "historynext", minWidth: t.Px(90f), maxWidth: inner.Width * 0.45f);
            p.DrawClipped("page " + (current + 1).ToString(CultureInfo.InvariantCulture) + " of "
                          + pageCount.ToString(CultureInfo.InvariantCulture),
                          inner.Right - t.Px(110f), py + t.ButtonHeight * 0.5f + t.FontSmall * 0.36f,
                          t.Px(110f), t.FontSmall, t.TextMuted);
        }

        /// <summary>How the run ENDED in the operator's words. A finished log is not a
        /// finished print, so an unrecorded outcome says so instead of implying success.</summary>
        public static string Outcome(RecentPrintEntry run)
        {
            if (run.Outcome == null) return "outcome unknown";
            switch (run.Outcome)
            {
                case "Idle": return run.DryRun ? "dry run finished" : "finished";
                case "Halted": return "halted";
                case "Cancelling":
                case "Cancelled": return "cancelled";
                case "ConnectionLost": return "connection lost";
                default: return run.Outcome.ToLowerInvariant();
            }
        }

        static string Bytes(long value)
        {
            if (value >= 1024 * 1024)
                return (value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (value >= 1024) return (value / 1024).ToString(CultureInfo.InvariantCulture) + " kB";
            return value.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }
}
