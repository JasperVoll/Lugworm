using System;
using System.Globalization;
using System.Text;

namespace Lugworm.Core.Duet
{
    /// <summary>The console's user-global preferences: permanent logging, its destination,
    /// reduced motion and the last accepted window bounds. Versioned
    /// JSON, invariant culture, no secrets - the active page, terminal text, passwords and
    /// confirmation state are deliberately not here. The plugin stores this atomically
    /// under %LOCALAPPDATA%; this class only knows the text.</summary>
    public sealed class ConsoleSettings
    {
        /// <summary>Version 3 removes thermal presets. Version-1 and version-2 files
        /// parse unchanged; obsolete version-2 fields are ignored.</summary>
        public const int CurrentVersion = 3;

        public bool KeepAllJobLogs { get; set; }
        /// <summary>Local or UNC folder; null = none chosen (permanent logging stays off).</summary>
        public string? Destination { get; set; }
        public bool ReducedMotion { get; set; }
        /// <summary>x, y, width, height in logical pixels; null = let the window decide.</summary>
        public int[]? WindowBounds { get; set; }
        public ConsoleSettings Copy() => new ConsoleSettings
        {
            KeepAllJobLogs = KeepAllJobLogs,
            Destination = Destination,
            ReducedMotion = ReducedMotion,
            WindowBounds = WindowBounds == null ? null : (int[])WindowBounds.Clone(),
        };

        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\"version\":").Append(CurrentVersion.ToString(inv));
            sb.Append(",\"keepAllJobLogs\":").Append(KeepAllJobLogs ? "true" : "false");
            sb.Append(",\"destination\":");
            if (Destination == null) sb.Append("null"); else JsonText.Quote(sb, Destination);
            sb.Append(",\"reducedMotion\":").Append(ReducedMotion ? "true" : "false");
            sb.Append(",\"windowBounds\":");
            if (WindowBounds == null || WindowBounds.Length != 4) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(WindowBounds[i].ToString(inv));
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Parses settings text. Unknown versions and malformed text throw; a
        /// missing field keeps its default. Callers fall back to defaults on failure and
        /// report once (the file is a convenience, never a gate).</summary>
        public static ConsoleSettings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("settings text is empty");
            var json = JsonValue.Parse(text);
            if (json.Kind != JsonKind.Object) throw new FormatException("settings root is not an object");
            int version = json.Int("version", -1);
            if (version < 1 || version > CurrentVersion)
                throw new FormatException("unsupported settings version " + version.ToString(CultureInfo.InvariantCulture));
            var s = new ConsoleSettings
            {
                KeepAllJobLogs = json.Bool("keepAllJobLogs", false),
                ReducedMotion = json.Bool("reducedMotion", false),
            };
            var dest = json["destination"];
            s.Destination = dest.Kind == JsonKind.String && dest.AsString().Length > 0 ? dest.AsString() : null;
            var bounds = json["windowBounds"];
            if (bounds.Kind == JsonKind.Array && bounds.Count == 4)
            {
                var b = new int[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                {
                    var v = bounds[i];
                    if (v.Kind != JsonKind.Number) { ok = false; break; }
                    b[i] = v.AsInt();
                }
                if (ok && b[2] > 0 && b[3] > 0) s.WindowBounds = b;
            }
            if (s.KeepAllJobLogs && s.Destination == null) s.KeepAllJobLogs = false;   // never on without a place to go
            return s;
        }

        public static bool TryParse(string? text, out ConsoleSettings settings, out string? error)
        {
            try
            {
                settings = Parse(text ?? "");
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException)
            {
                settings = new ConsoleSettings();
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Minimal JSON text writing (the reader is <see cref="JsonLite"/>).</summary>
    public static class JsonText
    {
        public static void Quote(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            Quote(sb, value);
            return sb.ToString();
        }
    }
}
