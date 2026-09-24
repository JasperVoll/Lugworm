using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Settings
{
    /// <summary>The four value kinds a settings file can carry. Everything a
    /// Grasshopper slider, toggle, panel or value list holds is one of these; a point,
    /// curve, plane or colour deliberately is not - a settings file describes process
    /// parameters, never geometry.</summary>
    public enum SettingsKind { Number, Integer, Bool, Text }

    /// <summary>One captured input value: a scalar or a list of one kind, written and
    /// read under the invariant culture so a file authored on a German machine loads
    /// byte-identically anywhere (AUD-CULT-001).</summary>
    public sealed class SettingsValue : IEquatable<SettingsValue>
    {
        public SettingsKind Kind { get; }
        public bool IsList { get; }

        readonly double[] _numbers;
        readonly long[] _integers;
        readonly bool[] _bools;
        readonly string[] _texts;

        SettingsValue(SettingsKind kind, bool isList, double[]? n = null, long[]? i = null,
                      bool[]? b = null, string[]? t = null)
        {
            Kind = kind; IsList = isList;
            _numbers = n ?? Array.Empty<double>();
            _integers = i ?? Array.Empty<long>();
            _bools = b ?? Array.Empty<bool>();
            _texts = t ?? Array.Empty<string>();
        }

        // ---- factories ----
        public static SettingsValue Number(double value)
            => new SettingsValue(SettingsKind.Number, false, n: new[] { RequireFinite(value) });
        public static SettingsValue Integer(long value)
            => new SettingsValue(SettingsKind.Integer, false, i: new[] { value });
        public static SettingsValue Bool(bool value)
            => new SettingsValue(SettingsKind.Bool, false, b: new[] { value });
        public static SettingsValue Text(string value)
            => new SettingsValue(SettingsKind.Text, false, t: new[] { value ?? "" });

        public static SettingsValue Numbers(IEnumerable<double> values)
            => new SettingsValue(SettingsKind.Number, true,
                                 n: (values ?? Array.Empty<double>()).Select(RequireFinite).ToArray());
        public static SettingsValue Integers(IEnumerable<long> values)
            => new SettingsValue(SettingsKind.Integer, true, i: (values ?? Array.Empty<long>()).ToArray());
        public static SettingsValue Bools(IEnumerable<bool> values)
            => new SettingsValue(SettingsKind.Bool, true, b: (values ?? Array.Empty<bool>()).ToArray());
        public static SettingsValue Texts(IEnumerable<string> values)
            => new SettingsValue(SettingsKind.Text, true,
                                 t: (values ?? Array.Empty<string>()).Select(v => v ?? "").ToArray());

        /// <summary>Validates a settings value as finite (throws otherwise) rather than
        /// just reporting whether it is - a different contract from Geometry.Num.Finite,
        /// which it uses for the actual check.</summary>
        static double RequireFinite(double v)
        {
            if (!Num.Finite(v))
                throw new ArgumentOutOfRangeException(nameof(v), "settings values must be finite");
            return v;
        }

        /// <summary>Element count; 1 for a scalar.</summary>
        public int Count => Kind switch
        {
            SettingsKind.Number => _numbers.Length,
            SettingsKind.Integer => _integers.Length,
            SettingsKind.Bool => _bools.Length,
            _ => _texts.Length,
        };

        public IReadOnlyList<double> NumberValues => _numbers;
        public IReadOnlyList<long> IntegerValues => _integers;
        public IReadOnlyList<bool> BoolValues => _bools;
        public IReadOnlyList<string> TextValues => _texts;

        // ---- scalar reads (a list answers with its first element) ----
        public double AsNumber(double def = 0) => Kind switch
        {
            SettingsKind.Number => _numbers.Length > 0 ? _numbers[0] : def,
            SettingsKind.Integer => _integers.Length > 0 ? _integers[0] : def,
            SettingsKind.Bool => _bools.Length > 0 ? (_bools[0] ? 1 : 0) : def,
            _ => _texts.Length > 0 && double.TryParse(_texts[0], NumberStyles.Float,
                     CultureInfo.InvariantCulture, out double d) ? d : def,
        };

        public long AsInteger(long def = 0) => Kind switch
        {
            SettingsKind.Integer => _integers.Length > 0 ? _integers[0] : def,
            SettingsKind.Number => _numbers.Length > 0
                ? (long)Math.Round(_numbers[0], MidpointRounding.AwayFromZero) : def,
            SettingsKind.Bool => _bools.Length > 0 ? (_bools[0] ? 1 : 0) : def,
            _ => _texts.Length > 0 && long.TryParse(_texts[0], NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out long l) ? l : def,
        };

        public bool AsBool(bool def = false) => Kind switch
        {
            SettingsKind.Bool => _bools.Length > 0 ? _bools[0] : def,
            SettingsKind.Number => _numbers.Length > 0 ? Math.Abs(_numbers[0]) > 1e-12 : def,
            SettingsKind.Integer => _integers.Length > 0 ? _integers[0] != 0 : def,
            _ => _texts.Length > 0 && bool.TryParse(_texts[0], out bool b) ? b : def,
        };

        /// <summary>Invariant text of the first element.</summary>
        public string AsText(string def = "") => Count > 0 ? TextAt(0) : def;

        /// <summary>Invariant text of one element - exactly the form written to JSON for
        /// numbers, integers and booleans, and the raw string for text.</summary>
        public string TextAt(int index)
        {
            if (index < 0 || index >= Count) return "";
            return Kind switch
            {
                SettingsKind.Number => NumberText(_numbers[index]),
                SettingsKind.Integer => _integers[index].ToString(CultureInfo.InvariantCulture),
                SettingsKind.Bool => _bools[index] ? "true" : "false",
                _ => _texts[index],
            };
        }

        /// <summary>Every element as invariant text, in order.</summary>
        public IReadOnlyList<string> AllText()
        {
            var outp = new string[Count];
            for (int i = 0; i < outp.Length; i++) outp[i] = TextAt(i);
            return outp;
        }

        /// <summary>Round-trip number text that always reads back as a fractional
        /// literal, so 1.0 stays a Number and never degrades into an Integer.</summary>
        internal static string NumberText(double v)
        {
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            if (s.IndexOf('.') < 0 && s.IndexOf('E') < 0 && s.IndexOf('e') < 0) s += ".0";
            return s;
        }

        internal void WriteJson(StringBuilder sb)
        {
            if (!IsList) { WriteElement(sb, 0); return; }
            sb.Append('[');
            for (int i = 0; i < Count; i++)
            {
                if (i > 0) sb.Append(", ");
                WriteElement(sb, i);
            }
            sb.Append(']');
        }

        void WriteElement(StringBuilder sb, int index)
        {
            if (index >= Count) { sb.Append("null"); return; }
            if (Kind == SettingsKind.Text) JsonText.Quote(sb, _texts[index]);
            else sb.Append(TextAt(index));
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            WriteJson(sb);
            return sb.ToString();
        }

        public bool Equals(SettingsValue? other)
        {
            if (other == null || other.Kind != Kind || other.IsList != IsList || other.Count != Count) return false;
            for (int i = 0; i < Count; i++)
            {
                if (Kind == SettingsKind.Number)
                {
                    if (!_numbers[i].Equals(other._numbers[i])) return false;
                }
                else if (!string.Equals(TextAt(i), other.TextAt(i), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as SettingsValue);

        public override int GetHashCode()
        {
            int h = ((int)Kind * 397) ^ (IsList ? 1 : 0);
            for (int i = 0; i < Count; i++) h = (h * 397) ^ TextAt(i).GetHashCode();
            return h;
        }

        public override string ToString() => ToJson();

        // ---- reading one JSON member ----
        internal static SettingsValue FromJson(string inputName, JsonValue value)
        {
            switch (value.Kind)
            {
                case JsonKind.String: return Text(value.AsString());
                case JsonKind.Bool: return Bool(value.AsBool());
                case JsonKind.Number: return Scalar(inputName, value);
                case JsonKind.Array: return FromArray(inputName, value);
                default:
                    throw new FormatException("input \"" + inputName + "\" has an unsupported value");
            }
        }

        static SettingsValue Scalar(string inputName, JsonValue value)
        {
            double d = value.AsNumber();
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new FormatException("input \"" + inputName + "\" is not a finite number");
            if (!value.IsIntegerLiteral) return Number(d);
            // long.MaxValue widens to 2^63 exactly, so the comparison must exclude it or
            // the unchecked cast wraps to long.MinValue
            if (!(d >= -9223372036854775808.0 && d < 9223372036854775808.0))
                throw new FormatException("input \"" + inputName + "\" is out of the integer range");
            return Integer((long)d);
        }

        static SettingsValue FromArray(string inputName, JsonValue array)
        {
            int n = array.Count;
            // an empty list has no kind of its own; text is the one that converts
            // into every wired object without losing information
            if (n == 0) return Texts(Array.Empty<string>());

            var first = array[0];
            switch (first.Kind)
            {
                case JsonKind.String:
                    {
                        var v = new string[n];
                        for (int i = 0; i < n; i++) v[i] = Element(inputName, array[i], JsonKind.String).AsString();
                        return Texts(v);
                    }
                case JsonKind.Bool:
                    {
                        var v = new bool[n];
                        for (int i = 0; i < n; i++) v[i] = Element(inputName, array[i], JsonKind.Bool).AsBool();
                        return Bools(v);
                    }
                case JsonKind.Number when AllIntegerLiterals(array):
                    {
                        var v = new long[n];
                        for (int i = 0; i < n; i++) v[i] = Scalar(inputName, array[i]).AsInteger();
                        return Integers(v);
                    }
                case JsonKind.Number:
                    {
                        var v = new double[n];
                        for (int i = 0; i < n; i++)
                        {
                            double d = Element(inputName, array[i], JsonKind.Number).AsNumber();
                            if (double.IsNaN(d) || double.IsInfinity(d))
                                throw new FormatException("input \"" + inputName + "\" contains a non-finite number");
                            v[i] = d;
                        }
                        return Numbers(v);
                    }
                default:
                    throw new FormatException("input \"" + inputName + "\" has an unsupported list value");
            }
        }

        static bool AllIntegerLiterals(JsonValue array)
        {
            for (int i = 0; i < array.Count; i++)
                if (array[i].Kind != JsonKind.Number || !array[i].IsIntegerLiteral) return false;
            return true;
        }

        static JsonValue Element(string inputName, JsonValue element, JsonKind expected)
        {
            if (element.Kind != expected)
                throw new FormatException("input \"" + inputName + "\" mixes value types in its list");
            return element;
        }
    }

    /// <summary>One Lugworm component's saved inputs: the stable component key, the
    /// active feature groups and one value per captured input. Versioned JSON in the
    /// ConsoleSettings style - unknown versions, a missing component key and malformed
    /// values throw, missing optional fields default, unknown keys are ignored so an
    /// older build still reads a newer file's shared part. Pure Core: this class knows
    /// the text, never the canvas and never the file system.</summary>
    public sealed class ComponentSettings
    {
        public const int CurrentVersion = 1;

        /// <summary>Stable key of the component class (e.g. "gcode"). Files are matched
        /// by THIS field, never by file name.</summary>
        public string Component { get; }
        /// <summary>The component's display name when the file was written (informative).</summary>
        public string DisplayName { get; }
        /// <summary>Plugin version that wrote the file (informative).</summary>
        public string Plugin { get; }
        /// <summary>Feature groups that were switched on, sorted; "core" is never listed.</summary>
        public IReadOnlyList<string> Groups { get; }
        /// <summary>Captured input values by input name.</summary>
        public IReadOnlyDictionary<string, SettingsValue> Inputs { get; }

        public ComponentSettings(string component,
                                 string? displayName = null,
                                 string? plugin = null,
                                 IEnumerable<string>? groups = null,
                                 IReadOnlyDictionary<string, SettingsValue>? inputs = null)
        {
            if (string.IsNullOrWhiteSpace(component))
                throw new ArgumentException("a settings file needs a component key", nameof(component));
            Component = component.Trim();
            DisplayName = displayName ?? "";
            Plugin = plugin ?? "";
            Groups = (groups ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.Ordinal)
                .ToArray();
            var map = new Dictionary<string, SettingsValue>(StringComparer.Ordinal);
            if (inputs != null)
                foreach (var kv in inputs)
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null) map[kv.Key] = kv.Value;
            Inputs = map;
        }

        /// <summary>Pretty, deterministic JSON: fixed key order at the root, inputs
        /// sorted by name (ordinal), so two saves of the same state are byte-identical.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"iamlab-settings\": ").Append(CurrentVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"component\": ").Append(JsonText.Quote(Component)).Append(",\n");
            sb.Append("  \"displayName\": ").Append(JsonText.Quote(DisplayName)).Append(",\n");
            sb.Append("  \"plugin\": ").Append(JsonText.Quote(Plugin)).Append(",\n");
            sb.Append("  \"groups\": [");
            for (int i = 0; i < Groups.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(JsonText.Quote(Groups[i]));
            }
            sb.Append("],\n");
            sb.Append("  \"inputs\": {");
            var names = Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < names.Length; i++)
            {
                sb.Append(i > 0 ? ",\n    " : "\n    ");
                sb.Append(JsonText.Quote(names[i])).Append(": ");
                Inputs[names[i]].WriteJson(sb);
            }
            sb.Append(names.Length > 0 ? "\n  }\n" : "}\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>Parses a settings file. Unknown versions, a missing component key and
        /// malformed input values throw <see cref="FormatException"/> naming the input;
        /// missing optional fields default and unknown keys are ignored.</summary>
        public static ComponentSettings Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("settings text is empty");
            var json = JsonValue.Parse(text);
            if (json.Kind != JsonKind.Object) throw new FormatException("settings root is not an object");

            var versionValue = json["iamlab-settings"];
            if (versionValue.Kind != JsonKind.Number)
                throw new FormatException("not a Lugworm settings file (no \"iamlab-settings\" version)");
            int version = versionValue.AsInt(-1);
            if (version < 1 || version > CurrentVersion)
                throw new FormatException("unsupported settings version " + version.ToString(CultureInfo.InvariantCulture));

            var componentValue = json["component"];
            if (componentValue.Kind != JsonKind.String || componentValue.AsString().Trim().Length == 0)
                throw new FormatException("settings file has no \"component\" key");

            var groups = new List<string>();
            var groupArray = json["groups"];
            if (groupArray.Kind == JsonKind.Array)
                for (int i = 0; i < groupArray.Count; i++)
                    if (groupArray[i].Kind == JsonKind.String) groups.Add(groupArray[i].AsString());

            var inputs = new Dictionary<string, SettingsValue>(StringComparer.Ordinal);
            var inputObject = json["inputs"];
            if (inputObject.Kind == JsonKind.Object)
                foreach (string key in inputObject.Keys)
                    inputs[key] = SettingsValue.FromJson(key, inputObject[key]);
            else if (inputObject.Kind != JsonKind.Null)
                throw new FormatException("\"inputs\" is not an object");

            return new ComponentSettings(componentValue.AsString(),
                                         json["displayName"].AsString(),
                                         json["plugin"].AsString(),
                                         groups, inputs);
        }

        public static bool TryParse(string? text, out ComponentSettings? settings, out string? error)
        {
            try
            {
                settings = Parse(text ?? "");
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException)
            {
                settings = null;
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>What one scan of a settings folder found: the winning file per component
    /// key, the files that could not be read, and the component keys several files
    /// claim.</summary>
    public sealed class SettingsFolderIndex
    {
        public IReadOnlyDictionary<string, (string path, ComponentSettings settings)> ByComponent { get; }
        public IReadOnlyList<(string path, string error)> Unreadable { get; }
        public IReadOnlyList<(string component, IReadOnlyList<string> paths)> Duplicates { get; }

        public SettingsFolderIndex(IReadOnlyDictionary<string, (string path, ComponentSettings settings)> byComponent,
                                   IReadOnlyList<(string path, string error)> unreadable,
                                   IReadOnlyList<(string component, IReadOnlyList<string> paths)> duplicates)
        {
            ByComponent = byComponent;
            Unreadable = unreadable;
            Duplicates = duplicates;
        }

        public static readonly SettingsFolderIndex Empty = new SettingsFolderIndex(
            new Dictionary<string, (string, ComponentSettings)>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<(string, string)>(),
            Array.Empty<(string, IReadOnlyList<string>)>());

        public bool IsEmpty => ByComponent.Count == 0 && Unreadable.Count == 0 && Duplicates.Count == 0;

        /// <summary>The settings for one component key, or null.</summary>
        public ComponentSettings? For(string? componentKey)
            => componentKey != null && ByComponent.TryGetValue(componentKey, out var hit) ? hit.settings : null;

        /// <summary>The winning file path for one component key, or null.</summary>
        public string? PathFor(string? componentKey)
            => componentKey != null && ByComponent.TryGetValue(componentKey, out var hit) ? hit.path : null;
    }

    /// <summary>Pure folder scanning: the caller reads the files (Core never touches the
    /// file system), this turns the texts into an index. Matching is by the "component"
    /// field inside each file, never by file name; when several files claim one
    /// component the alphabetically first path (ordinal, case-insensitive) wins and the
    /// rest are reported as duplicates.</summary>
    public static class SettingsFolder
    {
        public const string PreferredExtension = ".lwset.json";
        /// <summary>The extension files carried before the 5.0.0 rename; they still load.</summary>
        public const string LegacyExtension = ".iamset.json";

        /// <summary>True for a file the folder component offers to the scan
        /// (<c>*.lwset.json</c>, the older <c>*.iamset.json</c> and plain <c>*.json</c>).</summary>
        static bool IsSettingsFile(string? path)
            => !string.IsNullOrEmpty(path) && path!.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        public static SettingsFolderIndex Scan(IEnumerable<(string path, string text)> files)
        {
            var byComponent = new Dictionary<string, (string path, ComponentSettings settings)>(StringComparer.OrdinalIgnoreCase);
            var claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var unreadable = new List<(string path, string error)>();
            if (files == null)
                return new SettingsFolderIndex(byComponent, unreadable, Array.Empty<(string, IReadOnlyList<string>)>());

            // sort once, then first-wins per key: the winner is the alphabetically
            // first path whatever order the caller enumerated the folder in
            var candidates = files
                .Where(f => IsSettingsFile(f.path))
                .OrderBy(f => f.path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.path, StringComparer.Ordinal)
                .ToList();

            foreach (var (path, text) in candidates)
            {
                if (!ComponentSettings.TryParse(text, out var settings, out string? error) || settings == null)
                {
                    unreadable.Add((path, error ?? "could not be read"));
                    continue;
                }
                if (!claims.TryGetValue(settings.Component, out var paths))
                {
                    paths = new List<string>();
                    claims[settings.Component] = paths;
                }
                paths.Add(path);
                if (!byComponent.ContainsKey(settings.Component))
                    byComponent[settings.Component] = (path, settings);
            }

            var duplicates = claims
                .Where(kv => kv.Value.Count > 1)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (kv.Key, (IReadOnlyList<string>)kv.Value.ToArray()))
                .ToArray();

            return new SettingsFolderIndex(byComponent, unreadable, duplicates);
        }
    }
}
