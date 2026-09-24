using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Gcode
{
    /// <summary>Klipper's extended commands (upper-case words with underscores and
    /// KEY=value parameters) are not G/M words, so the shared lexer rejects them.
    /// This is the one place that recognises them and says which may appear where:
    /// a job may carry exactly the three the generator emits (fail-closed, like the
    /// M98 rule - user macros stay refused), the terminal treats a few read-only
    /// queries as diagnostic and the rest of a fixed set as advanced.</summary>
    public static class KlipperCommands
    {
        public const string SetPressureAdvance = "SET_PRESSURE_ADVANCE";
        public const string SetVelocityLimit = "SET_VELOCITY_LIMIT";
        public const string SetPrintStatsInfo = "SET_PRINT_STATS_INFO";
        public const string FirmwareRestart = "FIRMWARE_RESTART";
        public const string Restart = "RESTART";
        public const string SetHeaterTemperature = "SET_HEATER_TEMPERATURE";
        public const string TemperatureWait = "TEMPERATURE_WAIT";
        public const string TurnOffHeaters = "TURN_OFF_HEATERS";
        public const string CancelPrint = "CANCEL_PRINT";
        public const string Pause = "PAUSE";
        public const string Resume = "RESUME";
        public const string SdcardPrintFile = "SDCARD_PRINT_FILE";
        public const string SetFanSpeed = "SET_FAN_SPEED";
        public const string Status = "STATUS";
        public const string Help = "HELP";
        public const string QueryEndstops = "QUERY_ENDSTOPS";
        public const string GetPosition = "GET_POSITION";

        static readonly HashSet<string> JobAllowed = new HashSet<string>(StringComparer.Ordinal)
            { SetPressureAdvance, SetVelocityLimit, SetPrintStatsInfo };
        static readonly HashSet<string> DiagnosticSet = new HashSet<string>(StringComparer.Ordinal)
            { Status, Help, QueryEndstops, GetPosition };
        static readonly HashSet<string> AdvancedSet = new HashSet<string>(StringComparer.Ordinal)
        {
            SetPressureAdvance, SetVelocityLimit, SetPrintStatsInfo, FirmwareRestart, Restart,
            SetHeaterTemperature, TemperatureWait, TurnOffHeaters, CancelPrint, Pause, Resume,
            SdcardPrintFile, SetFanSpeed,
        };
        // extended names without an underscore that must still be recognised
        static readonly HashSet<string> BareNames = new HashSet<string>(StringComparer.Ordinal)
            { Restart, Pause, Resume, Status, Help };

        public static string JobAllowedList => string.Join(", ", JobAllowed);

        /// <summary>True when the line is a Klipper extended command; <paramref name="name"/>
        /// is its upper-case name. Plain G/M/T/N words are never extended, so ordinary
        /// G-code cannot be misread - only an all-caps identifier with an underscore
        /// (or one of the known bare names) qualifies.</summary>
        public static bool TryGetName(string? raw, out string name)
        {
            name = "";
            if (raw == null) return false;
            string line;
            try { line = GcodeText.Strip(raw); }
            catch (FormatException) { return false; }
            if (line.Length == 0) return false;
            int end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            string token = line.Substring(0, end);
            if (token.Length < 2 || !char.IsLetter(token[0])) return false;
            bool underscore = false;
            foreach (char c in token)
            {
                if (c == '_') { underscore = true; continue; }
                if (!char.IsLetterOrDigit(c)) return false;
            }
            string upper = token.ToUpperInvariant();
            // "G1", "M104", "T0", "N10" are word+number, never extended
            if ((upper[0] == 'G' || upper[0] == 'M' || upper[0] == 'T' || upper[0] == 'N') && char.IsDigit(upper[1]) && !underscore)
                return false;
            if (!underscore && !BareNames.Contains(upper)) return false;
            name = upper;
            return true;
        }

        public static bool IsExtended(string? raw) => TryGetName(raw, out _);

        public static bool IsJobAllowed(string name) => JobAllowed.Contains(name ?? "");
        public static bool IsDiagnostic(string name) => DiagnosticSet.Contains(name ?? "");
        public static bool IsAdvanced(string name) => AdvancedSet.Contains(name ?? "");
        public static bool IsReset(string name) => name == FirmwareRestart || name == Restart;

        /// <summary>KEY=value parameters of an extended line (keys upper-cased).</summary>
        public static Dictionary<string, string> Parameters(string raw)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string line;
            try { line = GcodeText.Strip(raw); }
            catch (FormatException) { return result; }
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                result[parts[i].Substring(0, eq).ToUpperInvariant()] = parts[i].Substring(eq + 1);
            }
            return result;
        }

        public static string Format(string name, params (string key, double value)[] args)
        {
            var sb = new System.Text.StringBuilder(name);
            foreach (var (key, value) in args)
                sb.Append(' ').Append(key).Append('=').Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
