using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Duet;
using Lugworm.Ui.Console.Pages;
using SkiaSharp;

namespace Lugworm.Ui.Console
{
    /// <summary>One clickable region of the drawn surface.</summary>
    public sealed class HitRegion
    {
        public SKRect Rect { get; }
        public string ActionId { get; }
        public bool Enabled { get; }
        /// <summary>Hover text for this region, or null. The drawn surface has no controls
        /// of its own to hang a tooltip on, so the text rides with the hit rectangle that
        /// the same paint produced and can never outlive it.</summary>
        public string? Tip { get; }

        public HitRegion(SKRect rect, string actionId, bool enabled, string? tip = null)
        { Rect = rect; ActionId = actionId; Enabled = enabled; Tip = tip; }
    }

    /// <summary>The console's drawn control tree: the immediate-mode coordinator that owns
    /// the page state, the shell (navigation rail and status header) and the hit-region
    /// registry. A pure view: it renders a <see cref="PrintConsoleState"/> plus a
    /// <see cref="ButtonSet"/> and reports which action id a click landed on. It holds no
    /// machine facts and decides no gates - every enabled flag and every refusal string
    /// comes from the ButtonSet the canvas capsules use, so the window and the canvas
    /// cannot disagree.</summary>
    /// <remarks>Immediate mode: each paint rebuilds the hit regions, so layout and hit
    /// testing can never drift apart. Page renderers live under <c>Pages/</c>.</remarks>
    public sealed class ConsoleView
    {
        public enum Tab { Dashboard, Control, Job, Terminal, Logs }

        static readonly (string name, ConsoleIcon icon)[] Nav =
        {
            ("Dashboard", ConsoleIcon.Dashboard), ("Control", ConsoleIcon.Control), ("Job", ConsoleIcon.Job),
            ("Terminal", ConsoleIcon.Terminal), ("Logs", ConsoleIcon.Logs),
        };

        readonly List<HitRegion> _regions = new List<HitRegion>();
        ConsoleTheme _theme = new ConsoleTheme(1f);
        Tab _activeTab = Tab.Dashboard;

        public ConsoleInteractionState Interaction { get; } = new ConsoleInteractionState();

        /// <summary>The page. Every new console session opens on the dashboard; the page
        /// is never persisted. Setting it starts the navigation indicator slide.</summary>
        public Tab ActiveTab
        {
            get => _activeTab;
            set
            {
                if (value == _activeTab) return;
                Interaction.Navigate((int)_activeTab, (int)value);
                _activeTab = value;
            }
        }

        public int SelectedPoint { get; set; } = -1;
        public string? HotId { get => Interaction.HotId; set => Interaction.SetHot(value); }

        /// <summary>The most rows of the go-to list a page shows; a shorter card shows fewer,
        /// with the pager's row reserved first. The list is paged rather than truncated: a
        /// generated calibration grid is usually larger than one page, and every point in
        /// it has to be reachable.</summary>
        public const int PointsPerPage = 6;
        /// <summary>Current page of the go-to list; clamped at paint time.</summary>
        public int PointPage { get; set; }

        // ---- page-local view state (never machine facts) ----
        /// <summary>Current page of the Job page's Recent prints list; clamped at paint.</summary>
        public int RunPage { get; set; }
        /// <summary>Pages the last paint needed, so the form can clamp the page it sets.</summary>
        public int RunPageCount { get; internal set; } = 1;
        public string? SelectedJobId { get; set; }
        public bool ShowValidationMessages { get; set; }
        /// <summary>Which category the Session events list shows;
        /// <see cref="ConsoleEventCategory.All"/> shows every line.</summary>
        public ConsoleEventCategory EventFilter { get; set; } = ConsoleEventCategory.All;

        /// <summary>Where the terminal input box must be placed by the form. The text box
        /// is a native control on purpose: text entry, IME, selection and clipboard are not
        /// worth reimplementing on a canvas.</summary>
        public SKRect TerminalInputRect { get; private set; }
        public bool TerminalInputVisible { get; private set; }

        public void SetTheme(ConsoleTheme theme) => _theme = theme ?? throw new ArgumentNullException(nameof(theme));

        public string? HitTest(float x, float y)
        {
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                var r = _regions[i];
                if (r.Rect.Contains(x, y)) return r.Enabled ? r.ActionId : null;
            }
            return null;
        }

        /// <summary>The topmost region under the point WHATEVER its enabled state, for
        /// tooltips only. <see cref="HitTest"/> deliberately reports null over a disabled
        /// control so a click cannot leak through; a disabled control is however precisely
        /// the one whose hover text matters, because it carries the refusal reason.</summary>
        public string? RegionAt(float x, float y)
        {
            for (int i = _regions.Count - 1; i >= 0; i--)
                if (_regions[i].Rect.Contains(x, y)) return _regions[i].ActionId;
            return null;
        }

        /// <summary>Hover text registered for an action id by the last paint, or null.</summary>
        public string? TipFor(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                var r = _regions[i];
                if (r.Tip != null && string.Equals(r.ActionId, id, StringComparison.Ordinal)) return r.Tip;
            }
            return null;
        }

        internal void AddRegion(SKRect rect, string id, bool enabled, string? tip = null)
            => _regions.Add(new HitRegion(rect, id, enabled, tip));

        internal void SetTerminalInput(SKRect rect, bool visible) { TerminalInputRect = rect; TerminalInputVisible = visible; }

        /// <param name="cartesian">The machine's Cartesian axes map to the Rhino frame as
        /// identity. Meaningful only with <paramref name="frameKnown"/>.</param>
        /// <param name="frameKnown">The mechanism is established. Defaults to false so a
        /// caller that does not know cannot accidentally assert the Cartesian identity.</param>
        public void Paint(SKCanvas canvas, int width, int height,
                          PrintConsoleState state, ButtonSet buttons,
                          IReadOnlyList<string> pointLabels, string rendererLabel,
                          bool cartesian = false, bool frameKnown = false)
        {
            if (canvas == null) throw new ArgumentNullException(nameof(canvas));
            _regions.Clear();
            TerminalInputVisible = false;
            var t = _theme;
            canvas.Clear(t.Background);
            var ctx = new ConsolePaintContext(canvas, t, state, buttons, pointLabels ?? Array.Empty<string>(), Interaction, this,
                                              rendererLabel ?? "", cartesian, frameKnown);
            try
            {
                var layout = new ConsoleLayout(t, width, height);
                PaintRail(ctx, layout.Rail);
                PaintHeader(ctx, layout.Header);
                var page = layout.Content;
                switch (ActiveTab)
                {
                    case Tab.Control: ControlPage.Paint(ctx, page, layout.Narrow); break;
                    case Tab.Job: JobPage.Paint(ctx, page, layout.Narrow); break;
                    case Tab.Terminal: TerminalPage.Paint(ctx, page); break;
                    case Tab.Logs: LogsPage.Paint(ctx, page, layout.Narrow); break;
                    default: DashboardPage.Paint(ctx, page, layout.Narrow); break;
                }
                Interaction.AfterPaint(pendingIndicator: state.PendingAction != null);
            }
            finally { ctx.P.Dispose(); }
        }

        // ---- shell: navigation rail --------------------------------------------------------

        void PaintRail(ConsolePaintContext ctx, SKRect rail)
        {
            var t = ctx.T; var c = ctx.Canvas; var p = ctx.P;
            using (var fill = new SKPaint { Color = t.Navigation, Style = SKPaintStyle.Fill })
                c.DrawRect(rail, fill);
            using (var line = new SKPaint { Color = t.Border, StrokeWidth = t.Px(1f) })
                c.DrawLine(rail.Right, rail.Top, rail.Right, rail.Bottom, line);

            float y = rail.Top + t.Pad;
            p.Draw("Lugworm", rail.Left + t.Pad, y + t.FontHeader * 0.85f, t.FontHeader, t.Text, true);
            p.Draw("Duet Console", rail.Left + t.Pad, y + t.FontHeader + t.FontSmall + t.Px(2f), t.FontSmall, t.TextMuted);
            y += t.FontHeader + t.FontSmall + t.PadLarge;

            float itemH = t.Px(44f);
            float indicator = Interaction.NavPosition((int)ActiveTab);
            float iy = y + indicator * (itemH + t.Unit);
            using (var ind = new SKPaint { Color = t.Primary, Style = SKPaintStyle.Fill, IsAntialias = true })
                c.DrawRoundRect(new SKRect(rail.Left, iy + t.Px(8f), rail.Left + t.Px(3f), iy + itemH - t.Px(8f)), t.Px(1.5f), t.Px(1.5f), ind);

            for (int i = 0; i < Nav.Length; i++)
            {
                var rect = new SKRect(rail.Left + t.Gap, y, rail.Right - t.Gap, y + itemH);
                bool active = (int)ActiveTab == i;
                string id = "tab:" + i.ToString(CultureInfo.InvariantCulture);
                float hover = Interaction.Hover(id);
                if (active || hover > 0)
                    using (var bg = new SKPaint { Color = active ? t.PanelElevated : ConsolePrimitives.Blend(t.Navigation, t.PanelElevated, hover), Style = SKPaintStyle.Fill, IsAntialias = true })
                        c.DrawRoundRect(rect, t.Corner, t.Corner, bg);
                var fg = active ? t.Text : t.TextSecondary;
                float ix = rect.Left + t.GapLarge;
                ConsoleIcons.Draw(c, Nav[i].icon, new SKRect(ix, rect.MidY - t.IconSize / 2, ix + t.IconSize, rect.MidY + t.IconSize / 2), fg, t.Px(1.75f));
                p.Draw(Nav[i].name, ix + t.IconSize + t.GapLarge, rect.MidY + t.FontButton * 0.36f, t.FontButton, fg, active);
                ctx.Region(rect, id, true);
                y += itemH + t.Unit;
            }

            p.Draw("renderer: " + ctx.RendererLabel, rail.Left + t.Pad, rail.Bottom - t.Pad, t.FontSmall, t.TextMuted);
        }

        // ---- shell: status header ------------------------------------------------------------

        void PaintHeader(ConsolePaintContext ctx, SKRect header)
        {
            var t = ctx.T; var c = ctx.Canvas; var p = ctx.P; var s = ctx.State; var b = ctx.Buttons;
            using (var fill = new SKPaint { Color = t.Navigation, Style = SKPaintStyle.Fill })
                c.DrawRect(header, fill);
            using (var line = new SKPaint { Color = t.Border, StrokeWidth = t.Px(1f) })
                c.DrawLine(header.Left, header.Bottom, header.Right, header.Bottom, line);

            float x = header.Left + t.Pad;
            float base1 = header.Top + t.Px(24f);
            float base2 = header.Top + t.Px(46f);

            string host = string.IsNullOrWhiteSpace(s.Host) ? "no host" : s.Host.Trim();
            float w = p.Lamp(x, base1, ConnectionColor(s.Connection, t), host, t.FontButton);
            string board = s.BoardType.Length > 0 ? s.BoardType + (s.Legacy ? " (legacy)" : "") : "board unknown";
            p.DrawClipped(Title(PrintConsoleButtons.Lower(s.Connection)) + " - " + board, x, base2, t.Px(220f), t.FontSmall, t.TextMuted);
            x += Math.Max(w, t.Px(200f)) + t.PadLarge;

            string state = s.MachineState.ToString().ToLowerInvariant();
            p.Draw(state, x, base1, t.FontButton, t.Text, true);
            p.DrawClipped(Readiness(s), x, base2, t.Px(220f), t.FontSmall, t.TextSecondary);
            x += t.Px(200f) + t.PadLarge;

            // the session control on the right: whichever applies, pending while connecting
            float right = header.Right - t.Pad;
            bool connectShown = s.Connection == ConsoleConnection.None || s.Connection == ConsoleConnection.Disconnected
                             || s.Connection == ConsoleConnection.Error || s.Connection == ConsoleConnection.Connecting;
            string sessionId = connectShown ? "connect" : "disconnect";
            string label = connectShown ? (s.Connection == ConsoleConnection.Connecting ? "Connecting..." : "Connect") : "Disconnect";
            float bw = p.Measure(label, t.FontButton, true) + t.Px(28f) + t.IconSize + t.Gap;
            var br = new SKRect(right - bw, header.MidY - t.ButtonHeight / 2, right, header.MidY + t.ButtonHeight / 2);
            p.Button(br, connectShown ? ConsoleIcon.Connection : ConsoleIcon.Disconnect, label,
                     connectShown ? ConsoleRole.Primary : ConsoleRole.Secondary,
                     connectShown ? b.Connect : b.Disconnect, sessionId,
                     pending: s.Connection == ConsoleConnection.Connecting || s.Connection == ConsoleConnection.Reconnecting);
            right = br.Left - t.PadLarge;

            var logging = s.Logging;
            string logLabel = "Log: " + logging.Badge;
            float logW = p.Measure(logLabel, t.FontButton, true) + t.Px(28f);
            float lampR = t.Px(5f);
            float lampSpan = lampR * 2 + t.Gap;
            var logRect = new SKRect(right - logW, header.MidY - t.ButtonHeight / 2, right, header.MidY + t.ButtonHeight / 2);
            p.Lamp(logRect.Left - lampSpan, header.MidY + t.FontButton * 0.36f, BadgeColor(logging.Badge, t), "", t.FontButton);
            p.Button(logRect, ConsoleIcon.None, logLabel, ConsoleRole.Secondary, true, "tab:" + ((int)Tab.Logs).ToString(CultureInfo.InvariantCulture));
            right = logRect.Left - lampSpan - t.PadLarge;

            float jobW = Math.Max(t.Px(120f), right - x);
            if (s.JobFile.Length > 0)
            {
                p.DrawClipped(s.JobFile, x, base1, jobW, t.FontBody, t.Text, true);
                var bar = new SKRect(x, base2 - t.Px(9f), x + Math.Min(jobW - t.Px(70f), t.Px(180f)), base2 - t.Px(1f));
                p.Progress(bar, s.Progress);
                string pct = PrintConsoleState.ProgressText(s.Progress, s.Layer, s.LayerCount);
                p.DrawClipped(pct, bar.Right + t.Gap, base2, x + jobW - (bar.Right + t.Gap), t.FontSmall, t.TextSecondary);
            }
            else p.Draw("no job", x, base1, t.FontBody, t.TextMuted);
        }

        /// <summary>Concise readiness: what still stands between the machine and a print.</summary>
        internal static string Readiness(PrintConsoleState s)
        {
            if (s.Connection != ConsoleConnection.Connected) return "Not connected";
            if (s.TrustReason != null) return "Trust: " + s.TrustReason;
            if (s.MachineState == MachineStatus.Halted || s.MachineState == MachineStatus.Off) return "Halted - reset to recover";
            if (!s.AllHomed) return "Not homed";
            if (!s.ArmConfigurationKnown) return "Arm configuration unknown";
            if (s.MachineState == MachineStatus.Idle) return "Ready";
            return "Busy";
        }

        /// <summary>Capitalises the first character of an enum-derived word for display;
        /// <see cref="PrintConsoleButtons.Lower"/> stays untouched in Core for the
        /// component's text output.</summary>
        internal static string Title(string s)
            => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        internal static SKColor ConnectionColor(ConsoleConnection c, ConsoleTheme t)
        {
            switch (c)
            {
                case ConsoleConnection.Connected: return t.Success;
                case ConsoleConnection.Connecting:
                case ConsoleConnection.Reconnecting: return t.Warning;
                case ConsoleConnection.Error: return t.Danger;
                default: return t.TextMuted;
            }
        }

        internal static SKColor BadgeColor(string badge, ConsoleTheme t)
        {
            switch (badge)
            {
                case "Recording":
                case "Syncing": return t.Primary;
                case "Waiting to sync": return t.Warning;
                case "Saved": return t.Success;
                case "Logging fault": return t.Danger;
                default: return t.TextSecondary;
            }
        }

        internal static SKColor StateColor(LogRetentionState state, ConsoleTheme t)
        {
            switch (state)
            {
                case LogRetentionState.PermanentRecording:
                case LogRetentionState.Syncing: return t.Primary;
                case LogRetentionState.WaitingToSync:
                case LogRetentionState.RecoveredTemporary: return t.Warning;
                case LogRetentionState.Saved: return t.Success;
                case LogRetentionState.Faulted: return t.Danger;
                default: return t.TextSecondary;
            }
        }
    }
}
