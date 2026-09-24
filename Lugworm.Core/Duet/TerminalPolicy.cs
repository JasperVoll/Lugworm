using System;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Duet
{
    /// <summary>How a terminal line may be sent. <see cref="Refused"/> means the
    /// line is on neither list and never reaches the board.</summary>
    public enum TerminalRoute
    {
        /// <summary>Not on the diagnostic allowlist and not an advanced command.</summary>
        Refused,
        /// <summary>Read-only allowlisted command (M115, M122, …). Moves nothing, so it
        /// runs on the serialized non-state-changing path and stays usable while machine
        /// trust is diagnostic-only.</summary>
        Diagnostic,
        /// <summary>Motion/config/job command. Runs inside the trusted gated transaction,
        /// needs confirm arming, and invalidates the tracked arm configuration.</summary>
        Advanced,
    }

    /// <summary>What the caller should do with a terminal press.</summary>
    public enum TerminalSendDecision
    {
        /// <summary>Refuse: <see cref="TerminalVerdict.Reason"/> says why.</summary>
        Refuse,
        /// <summary>First press on an advanced line: arm the confirmation window and
        /// send nothing. The next press within the window sends.</summary>
        Arm,
        /// <summary>Send it.</summary>
        Send,
    }

    /// <summary>Result of classifying and gating one terminal line.</summary>
    public sealed class TerminalVerdict
    {
        public TerminalSendDecision Decision { get; }
        public TerminalRoute Route { get; }
        /// <summary>Null when the decision is <see cref="TerminalSendDecision.Send"/>.</summary>
        public string? Reason { get; }
        /// <summary>The lexed line; null when lexing failed or the line was empty.</summary>
        public GcodeLine? Line { get; }
        /// <summary>The trimmed input text.</summary>
        public string Text { get; }

        public TerminalVerdict(TerminalSendDecision decision, TerminalRoute route, string? reason,
                               GcodeLine? line, string text)
        {
            Decision = decision;
            Route = route;
            Reason = reason;
            Line = line;
            Text = text ?? "";
        }
    }

    /// <summary>The terminal's classification and confirm-arming rules, shared by the
    /// hidden <c>DuetTerminalComponent</c> and the console's Terminal tab so the two
    /// cannot drift. Pure: the caller supplies the clock.</summary>
    /// <remarks>
    /// The allowlist and the advanced set are moved verbatim from
    /// <c>DuetTerminalComponent.IsDiagnostic</c>/<c>IsAdvanced</c>; changing either
    /// changes what the machine will accept from a text box, so both are pinned by
    /// <c>TerminalPolicyTests</c>.
    /// </remarks>
    public static class TerminalPolicy
    {
        /// <summary>How long an advanced command stays armed after the first press.</summary>
        public const double ConfirmWindowSeconds = 5.0;

        /// <summary>Read-only commands that move nothing. Kept byte-identical to the
        /// 2.6.0 Terminal component.</summary>
        public static bool IsDiagnostic(GcodeLine? line)
        {
            if (line == null) return false;
            if (line.CommandLetter == 'M')
                return line.CommandNumber == 20 || line.CommandNumber == 105 || line.CommandNumber == 114
                    || line.CommandNumber == 115 || line.CommandNumber == 119 || line.CommandNumber == 122
                    || line.CommandNumber == 408 || line.CommandNumber == 503;
            return false;
        }

        /// <summary>Motion, configuration and job commands: allowed, but only behind the
        /// confirm arming, and they invalidate the tracked arm configuration. Kept
        /// byte-identical to the 2.6.0 Terminal component.</summary>
        public static bool IsAdvanced(GcodeLine? line)
        {
            if (line == null) return false;
            if (line.CommandLetter == 'G')
                return (line.CommandNumber >= 0 && line.CommandNumber <= 3)
                    || line.CommandNumber == 28 || line.CommandNumber == 53
                    || line.CommandNumber == 90 || line.CommandNumber == 91 || line.CommandNumber == 92;
            if (line.CommandLetter != 'M') return false;
            return line.CommandNumber == 32 || line.CommandNumber == 37 || line.CommandNumber == 999
                || line.CommandNumber == 201 || line.CommandNumber == 204 || line.CommandNumber == 205
                || line.CommandNumber == 566 || line.CommandNumber == 84 || line.CommandNumber == 220
                || line.CommandNumber == 221 || line.CommandNumber == 104 || line.CommandNumber == 109
                || line.CommandNumber == 140 || line.CommandNumber == 190 || line.CommandNumber == 106
                || line.CommandNumber == 107 || line.CommandNumber == 302;
        }

        /// <summary>True for a console-entered M999. The console's Reset button only
        /// fires while the board is Halted/Off (decision D4); without this the Terminal
        /// tab would be a raw bypass around that stricter rule.</summary>
        public static bool IsReset(GcodeLine? line)
            => line != null && line.CommandLetter == 'M' && line.CommandNumber == 999;

        /// <summary>M999, or Klipper's FIRMWARE_RESTART / RESTART - the same recovery
        /// act, so the same Halted/Off-only rule.</summary>
        public static bool IsReset(string? text)
            => KlipperCommands.TryGetName(text, out string name) && KlipperCommands.IsReset(name);

        /// <summary>Classifies <paramref name="text"/> without any gating - lexing plus
        /// the two lists. <paramref name="line"/> is null when the text is empty or does
        /// not lex.</summary>
        public static TerminalRoute Classify(string? text, out GcodeLine? line, out string? lexError)
        {
            line = null;
            lexError = null;
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return TerminalRoute.Refused;
            // Klipper extended commands never lex; a fixed set is classified by name
            // (read-only queries diagnostic, the rest advanced), everything else refused
            if (KlipperCommands.TryGetName(trimmed, out string extended))
            {
                if (KlipperCommands.IsDiagnostic(extended)) return TerminalRoute.Diagnostic;
                if (KlipperCommands.IsAdvanced(extended)) return TerminalRoute.Advanced;
                return TerminalRoute.Refused;
            }
            try { line = GcodeText.Lex(trimmed, 1); }
            catch (FormatException ex) { lexError = ex.Message; return TerminalRoute.Refused; }
            if (IsDiagnostic(line)) return TerminalRoute.Diagnostic;
            if (IsAdvanced(line)) return TerminalRoute.Advanced;
            return TerminalRoute.Refused;
        }

        /// <summary>True while an advanced press is still inside its confirmation window.</summary>
        public static bool IsConfirmArmed(DateTime confirmUntilUtc, DateTime nowUtc)
            => nowUtc < confirmUntilUtc;

        /// <summary>Decides what a Send press does.</summary>
        /// <param name="text">The raw text box content.</param>
        /// <param name="confirmUntilUtc">When the current confirmation window expires;
        /// <see cref="DateTime.MinValue"/> when nothing is armed.</param>
        /// <param name="nowUtc">The caller's clock.</param>
        /// <param name="resetAllowed">Whether an M999 may be sent - the console passes the
        /// D4 predicate (Halted/Off) here. The hidden Terminal component passes true, which
        /// is its unchanged 2.6.0 behaviour.</param>
        /// <param name="resetRefusal">Reason shown when <paramref name="resetAllowed"/> is false.</param>
        public static TerminalVerdict Decide(string? text, DateTime confirmUntilUtc, DateTime nowUtc,
                                             bool resetAllowed = true,
                                             string resetRefusal = "reset (M999) is only allowed while the machine is halted or off")
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0)
                return new TerminalVerdict(TerminalSendDecision.Refuse, TerminalRoute.Refused,
                                           "enter one G-code line", null, trimmed);

            var route = Classify(trimmed, out var line, out string? lexError);
            if (lexError != null)
                return new TerminalVerdict(TerminalSendDecision.Refuse, TerminalRoute.Refused,
                                           "line rejected: " + lexError, null, trimmed);
            if (route == TerminalRoute.Refused)
                return new TerminalVerdict(TerminalSendDecision.Refuse, TerminalRoute.Refused,
                                           "command is not on the diagnostic allowlist", line, trimmed);

            if (route == TerminalRoute.Diagnostic)
                return new TerminalVerdict(TerminalSendDecision.Send, route, null, line, trimmed);

            // Advanced from here on.
            if (!resetAllowed && (IsReset(line) || IsReset(trimmed)))
                return new TerminalVerdict(TerminalSendDecision.Refuse, route, resetRefusal, line, trimmed);

            // The first press arms the window and sends nothing; the second press inside
            // the window is the send gate.
            if (!IsConfirmArmed(confirmUntilUtc, nowUtc))
                return new TerminalVerdict(TerminalSendDecision.Arm, route,
                                           "armed - press Send again to confirm", line, trimmed);

            return new TerminalVerdict(TerminalSendDecision.Send, route, null, line, trimmed);
        }

        /// <summary>The confirmation deadline a caller should store after an
        /// <see cref="TerminalSendDecision.Arm"/> decision.</summary>
        public static DateTime ArmUntil(DateTime nowUtc) => nowUtc.AddSeconds(ConfirmWindowSeconds);
    }
}
