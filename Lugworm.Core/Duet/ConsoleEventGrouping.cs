using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>The Console event view's category filter. <see cref="ConsoleEventGrouping.Classify"/>
    /// assigns every log line to one of these by cheap keyword rules; a message that
    /// matches none of the named categories is left as <see cref="All"/>, which doubles as
    /// "no specific category" for classification and "show everything" for filtering.</summary>
    public enum ConsoleEventCategory
    {
        All = 0,
        Connection = 1,
        Job = 2,
        Motion = 3,
        Firmware = 4,
        Errors = 5,
    }

    /// <summary>Display-only grouping and classification of the Console session log
    /// (<c>PrintConsoleState.LogTail</c>, appended one line per call to
    /// <c>MachineSession.AppendLog</c> as <c>"[HH:mm:ss] message"</c>). Neither operation
    /// here touches the raw log kept on disk or the <c>log</c> component output - both stay
    /// one line per event, forever. This exists because routine chatter can repeat the same
    /// line many times in a row: the 2026-09-09 dome incident logged "arm configuration
    /// unknown: an axis is unhomed" 21 times before homing completed, and an operator
    /// scanning the Console for a real event should see one line with a count and a time
    /// range, not twenty-one identical ones - while the count and every original line
    /// remain available as diagnostic evidence in <see cref="GroupedEvent.RawLines"/>.</summary>
    public static class ConsoleEventGrouping
    {
        /// <summary>One display line: a single log entry, or a run of consecutive
        /// identical messages collapsed together.</summary>
        public sealed class GroupedEvent
        {
            /// <summary>The message with its "[HH:mm:ss] " prefix removed (the raw line,
            /// unchanged, when it did not carry one).</summary>
            public string Message { get; }
            /// <summary>How many consecutive raw lines this display line stands for; 1 for
            /// an ungrouped line.</summary>
            public int Count { get; }
            /// <summary>Timestamp of the first occurrence in the run, "" when the source
            /// line had none.</summary>
            public string FirstTime { get; }
            /// <summary>Timestamp of the last occurrence in the run - equal to
            /// <see cref="FirstTime"/> when <see cref="Count"/> is 1.</summary>
            public string LastTime { get; }
            /// <summary>The category a keyword scan of <see cref="Message"/> assigned.</summary>
            public ConsoleEventCategory Category { get; }
            /// <summary>Every raw line the group stands for, oldest first - the diagnostic
            /// evidence a count-only display would otherwise discard.</summary>
            public IReadOnlyList<string> RawLines { get; }

            internal GroupedEvent(string message, int count, string firstTime, string lastTime,
                                   ConsoleEventCategory category, IReadOnlyList<string> rawLines)
            {
                Message = message ?? "";
                Count = count;
                FirstTime = firstTime ?? "";
                LastTime = lastTime ?? "";
                Category = category;
                RawLines = rawLines ?? Array.Empty<string>();
            }

            /// <summary>The text the Console actually draws: "[first - last] message (x21)"
            /// when the run has more than one line, "[time] message" for a single line, or
            /// the bare message when the source line carried no timestamp.</summary>
            public string Display()
            {
                string stamp = FirstTime.Length == 0
                    ? ""
                    : Count > 1 && !string.Equals(FirstTime, LastTime, StringComparison.Ordinal)
                        ? "[" + FirstTime + " - " + LastTime + "] "
                        : "[" + FirstTime + "] ";
                string suffix = Count > 1 ? " (x" + Count.ToString(CultureInfo.InvariantCulture) + ")" : "";
                return stamp + Message + suffix;
            }
        }

        /// <summary>Splits a raw <c>"[HH:mm:ss] message"</c> line into its timestamp and
        /// message. A line that does not start with a bracketed 8-character stamp is
        /// returned whole as the message with an empty timestamp - grouping and
        /// classification both still work on it, they just cannot show a time.</summary>
        public static (string Time, string Message) Split(string line)
        {
            if (string.IsNullOrEmpty(line) || line[0] != '[') return ("", line ?? "");
            int close = line.IndexOf(']');
            // "[HH:mm:ss]" is exactly 10 characters (indices 0-9); a shorter prefix is not
            // the stamp AppendLog emits, so treat the whole line as an unstamped message.
            if (close < 9) return ("", line);
            string time = line.Substring(1, close - 1);
            string rest = line.Substring(close + 1);
            if (rest.Length > 0 && rest[0] == ' ') rest = rest.Substring(1);
            return (time, rest);
        }

        /// <summary>Collapses consecutive lines whose message - ignoring the timestamp
        /// prefix - is identical. Non-consecutive repeats are NOT merged: two occurrences
        /// of the same text separated by other traffic are two distinct events, not one
        /// ongoing condition, and merging them would hide the intervening activity. An
        /// empty or null input returns an empty list.</summary>
        public static List<GroupedEvent> Group(IReadOnlyList<string> lines)
        {
            var result = new List<GroupedEvent>();
            if (lines == null) return result;
            int i = 0;
            while (i < lines.Count)
            {
                var (time, message) = Split(lines[i]);
                var raw = new List<string> { lines[i] };
                string lastTime = time;
                int j = i + 1;
                while (j < lines.Count)
                {
                    var (t2, m2) = Split(lines[j]);
                    if (!string.Equals(m2, message, StringComparison.Ordinal)) break;
                    raw.Add(lines[j]);
                    lastTime = t2;
                    j++;
                }
                result.Add(new GroupedEvent(message, raw.Count, time, lastTime, Classify(message), raw));
                i = j;
            }
            return result;
        }

        /// <summary>Cheap keyword classification, first match wins. Order matters: a
        /// failure/refusal reads as <see cref="ConsoleEventCategory.Errors"/> even when its
        /// text also mentions a connection or a job, because that is the more actionable
        /// signal for an operator scanning the log.</summary>
        public static ConsoleEventCategory Classify(string message)
        {
            if (string.IsNullOrEmpty(message)) return ConsoleEventCategory.All;
            if (ContainsAny(message, "fail", "error", "refused", "conflict", "fault", "mismatch", "m112", "halted", "unconfirmed"))
                return ConsoleEventCategory.Errors;
            if (ContainsAny(message, "connect", "reconnect", "identity", "fingerprint", "session", "host", "transport"))
                return ConsoleEventCategory.Connection;
            if (ContainsAny(message, "job", "upload", "print", "layer", "validat", "gcode", "g-code"))
                return ConsoleEventCategory.Job;
            if (ContainsAny(message, "home", "jog", "arm configuration", "axis", "position", "motion", "reset", "m999"))
                return ConsoleEventCategory.Motion;
            if (ContainsAny(message, "firmware", "board", "api ", "baseline"))
                return ConsoleEventCategory.Firmware;
            return ConsoleEventCategory.All;
        }

        static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var needle in needles)
#if NETFRAMEWORK
                if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
#else
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
#endif
            return false;
        }

        /// <summary>True when a line classified as <paramref name="category"/> should show
        /// under <paramref name="filter"/> - <see cref="ConsoleEventCategory.All"/> as a
        /// filter shows everything, regardless of category.</summary>
        public static bool Matches(ConsoleEventCategory filter, ConsoleEventCategory category)
            => filter == ConsoleEventCategory.All || filter == category;
    }
}
