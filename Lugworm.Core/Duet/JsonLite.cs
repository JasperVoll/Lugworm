using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lugworm.Core.Duet
{
    public enum JsonKind { Object, Array, String, Number, Bool, Null }

    /// <summary>Minimal read-only JSON tree for the Duet object model. Hand-rolled
    /// (like Doe/XlsxWriter) so Core stays dependency-free on net48 - System.Text.Json
    /// pulls in a bindings-conflict minefield under Rhino 7. Parses the full JSON
    /// grammar; exposes terse path lookups ("a.b[2].c") that return defaults instead
    /// of throwing, because RRF omits fields freely between firmware versions.</summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; }
        readonly Dictionary<string, JsonValue>? _obj;
        readonly List<JsonValue>? _arr;
        readonly string? _str;
        readonly double _num;
        readonly bool _bool;
        readonly bool _integerLiteral;

        JsonValue(JsonKind kind, Dictionary<string, JsonValue>? obj = null, List<JsonValue>? arr = null,
                  string? str = null, double num = 0, bool b = false, bool integerLiteral = false)
        { Kind = kind; _obj = obj; _arr = arr; _str = str; _num = num; _bool = b; _integerLiteral = integerLiteral; }

        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);
        internal static JsonValue Of(string s) => new JsonValue(JsonKind.String, str: s);
        internal static JsonValue Of(double d, bool integerLiteral = false)
            => new JsonValue(JsonKind.Number, num: d, integerLiteral: integerLiteral);
        internal static JsonValue Of(bool b) => new JsonValue(JsonKind.Bool, b: b);
        internal static JsonValue OfObject(Dictionary<string, JsonValue> o) => new JsonValue(JsonKind.Object, obj: o);
        internal static JsonValue OfArray(List<JsonValue> a) => new JsonValue(JsonKind.Array, arr: a);

        public bool IsNull => Kind == JsonKind.Null;

        /// <summary>True when this number came from a literal with no fraction and no
        /// exponent ("2", not "2.0" or "2e0"). RRF never needs the distinction, but a
        /// settings file does: it is what keeps an integer input an integer across a
        /// save/load round trip (Settings/ComponentSettings.cs).</summary>
        public bool IsIntegerLiteral => Kind == JsonKind.Number && _integerLiteral;

        public int Count => Kind == JsonKind.Array ? _arr!.Count
                          : Kind == JsonKind.Object ? _obj!.Count : 0;
        public IEnumerable<string> Keys => Kind == JsonKind.Object
            ? (IEnumerable<string>)_obj!.Keys : Array.Empty<string>();

        /// <summary>Object member lookup; JsonValue.Null when absent or not an object.</summary>
        public JsonValue this[string key]
            => Kind == JsonKind.Object && _obj!.TryGetValue(key, out var v) ? v : Null;

        /// <summary>Array element lookup; JsonValue.Null when out of range or not an array.</summary>
        public JsonValue this[int index]
            => Kind == JsonKind.Array && index >= 0 && index < _arr!.Count ? _arr[index] : Null;

        // ---- typed accessors (never throw) ----
        public string AsString(string def = "") => Kind == JsonKind.String ? _str! : def;
        public double AsNumber(double def = 0) => Kind == JsonKind.Number ? _num
            : Kind == JsonKind.Bool ? (_bool ? 1 : 0) : def;
        public bool AsBool(bool def = false) => Kind == JsonKind.Bool ? _bool
            : Kind == JsonKind.Number ? Math.Abs(_num) > 1e-12 : def;
        public int AsInt(int def = 0) => Kind == JsonKind.Number ? (int)Math.Round(_num) : def;

        // ---- path lookups: "a.b[2].c" ----
        public JsonValue At(string path)
        {
            var cur = this;
            int i = 0;
            while (i < path.Length && !cur.IsNull)
            {
                if (path[i] == '.') { i++; continue; }
                if (path[i] == '[')
                {
                    int close = path.IndexOf(']', i);
                    if (close < 0) return Null;
                    if (!int.TryParse(path.Substring(i + 1, close - i - 1), NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out int idx)) return Null;
                    cur = cur[idx];
                    i = close + 1;
                }
                else
                {
                    int end = i;
                    while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
                    cur = cur[path.Substring(i, end - i)];
                    i = end;
                }
            }
            return cur;
        }
        public string Str(string path, string def = "") => At(path).AsString(def);
        public double Num(string path, double def = 0) => At(path).AsNumber(def);
        public bool Bool(string path, bool def = false) => At(path).AsBool(def);
        public int Int(string path, int def = 0) => At(path).AsInt(def);

        // ---- parser ----
        /// <summary>Parses a JSON document. Throws FormatException (with character
        /// position) on malformed input - callers treat that as a protocol error.</summary>
        const int DefaultMaxCharacters = 4 * 1024 * 1024;
        const int DefaultMaxDepth = 64;
        const int DefaultMaxNodes = 100000;

        public static JsonValue Parse(string text)
            => Parse(text, DefaultMaxCharacters, DefaultMaxDepth, DefaultMaxNodes);

        public static JsonValue Parse(string text, int maxCharacters, int maxDepth, int maxNodes)
        {
            if (text == null) throw new FormatException("JSON input is null");
            if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters), "JSON parser limits must be positive");
            if (maxDepth < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth), "JSON parser limits must be positive");
            if (maxNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxNodes), "JSON parser limits must be positive");
            if (text.Length > maxCharacters)
                throw new FormatException($"JSON body exceeds the {maxCharacters} character limit");
            int pos = 0;
            int nodes = 0;
            var v = ParseValue(text, ref pos, 0, ref nodes, maxDepth, maxNodes);
            SkipWs(text, ref pos);
            if (pos != text.Length)
                throw new FormatException($"trailing content at position {pos}");
            return v;
        }

        static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
        }

        static JsonValue ParseValue(string s, ref int p, int depth, ref int nodes,
                                    int maxDepth, int maxNodes)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw new FormatException($"unexpected end of JSON at position {p}");
            if (depth > maxDepth) throw new FormatException($"JSON nesting exceeds the {maxDepth} level limit at position {p}");
            if (++nodes > maxNodes) throw new FormatException($"JSON node count exceeds the {maxNodes} node limit");
            char c = s[p];
            switch (c)
            {
                case '{': return ParseObject(s, ref p, depth, ref nodes, maxDepth, maxNodes);
                case '[': return ParseArray(s, ref p, depth, ref nodes, maxDepth, maxNodes);
                case '"': return Of(ParseString(s, ref p));
                case 't': Expect(s, ref p, "true"); return Of(true);
                case 'f': Expect(s, ref p, "false"); return Of(false);
                case 'n': Expect(s, ref p, "null"); return Null;
                default:
                    {
                        double d = ParseNumber(s, ref p, out bool integerLiteral);
                        return Of(d, integerLiteral);
                    }
            }
        }

        static void Expect(string s, ref int p, string word)
        {
            if (p + word.Length > s.Length || string.CompareOrdinal(s, p, word, 0, word.Length) != 0)
                throw new FormatException($"invalid literal at position {p}");
            p += word.Length;
        }

        static JsonValue ParseObject(string s, ref int p, int depth, ref int nodes,
                                     int maxDepth, int maxNodes)
        {
            var obj = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            p++; // '{'
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return OfObject(obj); }
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"')
                    throw new FormatException($"expected object key at position {p}");
                string key = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':')
                    throw new FormatException($"expected ':' at position {p}");
                p++;
                obj[key] = ParseValue(s, ref p, depth + 1, ref nodes, maxDepth, maxNodes);
                SkipWs(s, ref p);
                if (p >= s.Length) throw new FormatException($"unterminated object at position {p}");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return OfObject(obj); }
                throw new FormatException($"expected ',' or '}}' at position {p}");
            }
        }

        static JsonValue ParseArray(string s, ref int p, int depth, ref int nodes,
                                    int maxDepth, int maxNodes)
        {
            var arr = new List<JsonValue>();
            p++; // '['
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return OfArray(arr); }
            while (true)
            {
                arr.Add(ParseValue(s, ref p, depth + 1, ref nodes, maxDepth, maxNodes));
                SkipWs(s, ref p);
                if (p >= s.Length) throw new FormatException($"unterminated array at position {p}");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return OfArray(arr); }
                throw new FormatException($"expected ',' or ']' at position {p}");
            }
        }

        static string ParseString(string s, ref int p)
        {
            p++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= s.Length) throw new FormatException($"unterminated string at position {p}");
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) throw new FormatException($"unterminated escape at position {p}");
                char e = s[p++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (p + 4 > s.Length) throw new FormatException($"bad \\u escape at position {p}");
                        sb.Append((char)ushort.Parse(s.Substring(p, 4), NumberStyles.HexNumber,
                                                     CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw new FormatException($"bad escape '\\{e}' at position {p - 1}");
                }
            }
        }

        static double ParseNumber(string s, ref int p, out bool integerLiteral)
        {
            int start = p;
            if (p < s.Length && (s[p] == '-' || s[p] == '+')) p++;
            while (p < s.Length && (char.IsDigit(s[p]) || s[p] == '.' || s[p] == 'e' || s[p] == 'E'
                                    || s[p] == '-' || s[p] == '+'))
            {
                // sign only valid right after an exponent marker past the leading sign
                if ((s[p] == '-' || s[p] == '+') && p > start && s[p - 1] != 'e' && s[p - 1] != 'E') break;
                p++;
            }
            string literal = p > start ? s.Substring(start, p - start) : "";
            if (p == start || !double.TryParse(literal, NumberStyles.Float,
                                               CultureInfo.InvariantCulture, out double d))
                throw new FormatException($"invalid number at position {start}");
            integerLiteral = literal.IndexOf('.') < 0 && literal.IndexOf('e') < 0 && literal.IndexOf('E') < 0;
            // .NET Core parses exponent overflow ("1e999") to +/-Infinity where .NET
            // Framework fails the parse; reject non-finite on every TFM so a malformed
            // response can never satisfy a motion gate (AUD-SAF-003)
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new FormatException($"non-finite number at position {start}");
            return d;
        }
    }
}
