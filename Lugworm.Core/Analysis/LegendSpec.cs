using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Duet;

namespace Lugworm.Core.Analysis
{
    /// <summary>What a legend explains: a continuous ramp, thresholded classes,
    /// named groups, or a size scale.</summary>
    public enum LegendType { Continuous, Classes, Groups, Size }

    /// <summary>RGB bytes - the UI layer turns them into System.Drawing colours.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public Rgb(int r, int g, int b) : this((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b)) { }
        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        public static Rgb Of((byte R, byte G, byte B) t) => new Rgb(t.R, t.G, t.B);

        /// <summary><c>#RRGGBB</c>, upper case.</summary>
        public string Hex => "#" + R.ToString("X2", CultureInfo.InvariantCulture)
                                 + G.ToString("X2", CultureInfo.InvariantCulture)
                                 + B.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>Parses <c>#RRGGBB</c> or <c>RRGGBB</c> (also <c>#RGB</c>).</summary>
        public static bool TryParse(string? text, out Rgb rgb)
        {
            rgb = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text!.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = "" + s[0] + s[0] + s[1] + s[1] + s[2] + s[2];
            if (s.Length != 6) return false;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            rgb = new Rgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }

        public static Rgb Lerp(Rgb a, Rgb b, double u)
        {
            u = u < 0 ? 0 : u > 1 ? 1 : u;
            return new Rgb((int)Math.Round(a.R + (b.R - a.R) * u),
                           (int)Math.Round(a.G + (b.G - a.G) * u),
                           (int)Math.Round(a.B + (b.B - a.B) * u));
        }

        public bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
        public override bool Equals(object? obj) => obj is Rgb o && Equals(o);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public static bool operator ==(Rgb a, Rgb b) => a.Equals(b);
        public static bool operator !=(Rgb a, Rgb b) => !a.Equals(b);
        public override string ToString() => Hex;
    }

    /// <summary>How a palette is meant to be read: a ramp (sequential), a ramp with
    /// a meaningful middle (diverging), a set of distinct categories (qualitative -
    /// classes pick its colours one by one instead of sampling a ramp), or a ramp
    /// whose ends meet (cyclic).</summary>
    public enum ColorSchemeKind { Sequential, Diverging, Qualitative, Cyclic }

    /// <summary>One named palette in the registry: its family (<c>mpl</c>, <c>r</c>,
    /// <c>lugworm</c>), kind, colour stops (evenly spaced) and the
    /// aliases that resolve to it. Immutable and Rhino-free.</summary>
    public sealed class ColorScheme
    {
        public string Name { get; }
        public string Family { get; }
        public ColorSchemeKind Kind { get; }
        public IReadOnlyList<Rgb> Stops { get; }
        public IReadOnlyList<string> Aliases { get; }
        public bool Qualitative => Kind == ColorSchemeKind.Qualitative;

        public ColorScheme(string name, string family, ColorSchemeKind kind, Rgb[] stops, params string[] aliases)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("scheme needs a name", nameof(name));
            if (stops == null || stops.Length == 0) throw new ArgumentException("scheme needs colours", nameof(stops));
            Name = name; Family = family ?? ""; Kind = kind; Stops = stops; Aliases = aliases ?? Array.Empty<string>();
        }

        public override string ToString() => Family + ":" + Name;
    }

    /// <summary>The generated palette table lives in <c>ColorSchemeData.cs</c>
    /// (<c>tools/gen_color_schemes.py</c>); this half holds the one helper it calls.</summary>
    static partial class ColorSchemeData
    {
        internal static Rgb[] Hex(params string[] hex)
        {
            var list = new Rgb[hex.Length];
            for (int i = 0; i < hex.Length; i++)
                if (!Rgb.TryParse(hex[i], out list[i])) throw new ArgumentException("bad colour " + hex[i]);
            return list;
        }
    }

    /// <summary>The palette registry the Legend draws from: 35 engineering palettes
    /// (matplotlib colormaps and two R <c>grDevices</c> palettes, generated into
    /// <c>ColorSchemeData.cs</c>), including the two scales Core already owned - Plasma
    /// (interlayer time) and Viridis (live deviation), which keep routing to their
    /// scale classes so those colours never move - and the overhang traffic classes.
    /// Every palette has a frozen number, its index in <see cref="All"/>, and answers to
    /// it as well as to its name. Names are case-insensitive; the retired in-house ramp
    /// names (<c>blues</c>, <c>greens</c>, <c>greys</c>, <c>pinks</c>, <c>ambers</c>)
    /// resolve to the kept maps of the same meaning. <c>custom</c> means "use the spec's
    /// own colour list".</summary>
    public static class ColorSchemes
    {
        public const string Plasma = "plasma", Viridis = "viridis", Magma = "magma", Inferno = "inferno",
                            Cividis = "cividis", Blues = "blues", Greens = "greens", Greys = "greys",
                            Pinks = "pinks", Ambers = "ambers", Traffic = "traffic", Custom = "custom";

        static readonly ColorScheme[] _all;
        static readonly Dictionary<string, ColorScheme> _byName = new Dictionary<string, ColorScheme>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] _names;

        static ColorSchemes()
        {
            var pool = new List<ColorScheme>
            {
                // the Core-owned scales, sampled from their own classes
                new ColorScheme(Viridis, "mpl", ColorSchemeKind.Sequential, Sampled(DeviationColorScale.Map, 11)),
                new ColorScheme(Plasma, "mpl", ColorSchemeKind.Sequential, Sampled(PlasmaColorScale.Map, 11)),
                new ColorScheme(Traffic, "lugworm", ColorSchemeKind.Sequential,
                                ColorSchemeData.Hex("#5AA05A", "#EB9B00", "#D22828")),
            };
            pool.AddRange(ColorSchemeData.All);
            var byOwnName = new Dictionary<string, ColorScheme>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in pool) byOwnName[s.Name] = s;
            // number order: a palette's number is its index, fixed by the generator
            var numbered = ColorSchemeData.Numbered;
            _all = new ColorScheme[numbered.Length];
            for (int i = 0; i < numbered.Length; i++)
                _all[i] = byOwnName.TryGetValue(numbered[i], out var s) ? s
                    : throw new InvalidOperationException("palette number " + i.ToString(CultureInfo.InvariantCulture)
                                                          + " names '" + numbered[i] + "', which is not defined");
            if (pool.Count != _all.Length)
                throw new InvalidOperationException("every palette needs a number: " + pool.Count.ToString(CultureInfo.InvariantCulture)
                                                    + " defined, " + _all.Length.ToString(CultureInfo.InvariantCulture) + " numbered");
            foreach (var s in _all)
            {
                _byName[s.Name] = s;
                foreach (var a in s.Aliases) _byName[a] = s;
            }
            var names = new string[_all.Length + 1];
            for (int i = 0; i < _all.Length; i++) names[i] = _all[i].Name;
            names[_all.Length] = Custom;
            _names = names;
        }

        /// <summary>Every registered palette in number order: <c>All[n]</c> is palette n.</summary>
        public static IReadOnlyList<ColorScheme> All => _all;

        /// <summary>Every scheme name a spec may carry, in number order, plus <c>custom</c>.</summary>
        public static IReadOnlyList<string> Names => _names;

        /// <summary>A palette number ("14") as its registry name ("coolwarm"); any other text
        /// comes back trimmed, unchanged. Out-of-range numbers stay numbers and so stay unknown.</summary>
        public static string Normalize(string? nameOrNumber)
        {
            string key = (nameOrNumber ?? "").Trim();
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0 && n < _all.Length)
                return _all[n].Name;
            return key;
        }

        /// <summary>The number of a palette given by name, alias or number, or -1.</summary>
        public static int NumberOf(string? nameOrNumber)
            => Resolve(nameOrNumber) is ColorScheme s ? Array.IndexOf(_all, s) : -1;

        /// <summary>The palette for a name, alias or number, or null (also null for <c>custom</c>).</summary>
        public static ColorScheme? Resolve(string? nameOrNumber)
        {
            string key = Normalize(nameOrNumber);
            if (key.Length == 0) return null;
            return _byName.TryGetValue(key, out var s) ? s : null;
        }

        public static bool IsKnown(string? nameOrNumber)
        {
            if (nameOrNumber == null) return false;
            string key = Normalize(nameOrNumber);
            return string.Equals(key, Custom, StringComparison.OrdinalIgnoreCase) || _byName.ContainsKey(key);
        }

        /// <summary>True for a palette whose colours are categories rather than a ramp.</summary>
        public static bool IsQualitative(string? name) => Resolve(name)?.Qualitative == true;

        /// <summary>The palette's stops (used to seed discrete classes and to draw a
        /// custom list); an unknown name gives blues.</summary>
        public static IReadOnlyList<Rgb> AnchorsOf(string? name)
            => (Resolve(name) ?? _byName[Blues]).Stops;

        static Rgb[] Sampled(Func<double, (byte R, byte G, byte B)> map, int count)
        {
            var list = new Rgb[count];
            for (int i = 0; i < count; i++) list[i] = Rgb.Of(map(i / (count - 1.0)));
            return list;
        }

        /// <summary>Colour at <paramref name="t"/> in [0, 1] on a named ramp; a
        /// <c>custom</c> ramp interpolates <paramref name="custom"/> evenly (one colour =
        /// constant, none = blues). NaN and out-of-range clamp.</summary>
        public static Rgb At(string? name, IReadOnlyList<Rgb>? custom, double t)
            => At(name, custom, null, t);

        /// <summary>As above; a custom ramp with <paramref name="customStops"/> (one
        /// position in [0, 1] per colour, ascending) interpolates by position - the
        /// Gradient component's grips - and falls back to even spacing when the
        /// positions do not match the colours.</summary>
        public static Rgb At(string? name, IReadOnlyList<Rgb>? custom, IReadOnlyList<double>? customStops, double t)
        {
            if (double.IsNaN(t)) t = 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            string key = Normalize(name);
            if (string.Equals(key, Plasma, StringComparison.OrdinalIgnoreCase)) return Rgb.Of(PlasmaColorScale.Map(t));
            if (string.Equals(key, Viridis, StringComparison.OrdinalIgnoreCase)) return Rgb.Of(DeviationColorScale.Map(t));
            bool isCustom = string.Equals(key, Custom, StringComparison.OrdinalIgnoreCase) && custom != null && custom.Count > 0;
            IReadOnlyList<Rgb> anchors = isCustom ? custom! : AnchorsOf(key);
            if (anchors.Count == 1) return anchors[0];
            if (isCustom && customStops != null && customStops.Count == anchors.Count)
                return AtPositions(anchors, customStops, t);
            double x = t * (anchors.Count - 1);
            int i = (int)Math.Floor(x);
            if (i >= anchors.Count - 1) return anchors[anchors.Count - 1];
            return Rgb.Lerp(anchors[i], anchors[i + 1], x - i);
        }

        static Rgb AtPositions(IReadOnlyList<Rgb> colors, IReadOnlyList<double> stops, double t)
        {
            int n = colors.Count;
            if (t <= stops[0]) return colors[0];
            if (t >= stops[n - 1]) return colors[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                double a = stops[i], b = stops[i + 1];
                if (t < a || t > b) continue;
                double span = b - a;
                if (span <= 1e-12) return colors[i + 1];
                return Rgb.Lerp(colors[i], colors[i + 1], (t - a) / span);
            }
            return colors[n - 1];
        }
    }

    /// <summary>"Nice" axis ticks after Heckbert (Graphics Gems I, 1990): the
    /// step is 1, 2 or 5 times a power of ten, chosen so about
    /// <paramref name="count"/> ticks fall inside [min, max].</summary>
    public static class NiceTicks
    {
        public static double NiceStep(double range, int count)
        {
            if (!(range > 0) || double.IsInfinity(range)) return 1;
            count = Math.Max(2, count);
            double raw = range / (count - 1);
            double exp = Math.Floor(Math.Log10(raw));
            double f = raw / Math.Pow(10, exp);
            double nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
            return nf * Math.Pow(10, exp);
        }

        /// <summary>Tick values inside [min, max] (inclusive with tolerance),
        /// ascending. A non-finite domain yields 0 and 1; a degenerate one yields its
        /// single value.</summary>
        public static double[] Inside(double min, double max, int count)
        {
            if (double.IsNaN(min) || double.IsNaN(max) || double.IsInfinity(min) || double.IsInfinity(max))
                return new[] { 0.0, 1.0 };
            if (max < min) { double s = min; min = max; max = s; }
            if (!(max - min > 0)) return new[] { min };
            double step = NiceStep(max - min, count);
            double start = Math.Ceiling(min / step - 1e-9) * step;
            var ticks = new List<double>();
            double tol = step * 1e-6;
            for (double v = start; v <= max + tol; v += step)
            {
                double r = Math.Round(v / step) * step;   // kill accumulation error
                if (r >= min - tol && r <= max + tol) ticks.Add(Math.Abs(r) < tol ? 0.0 : r);
                if (ticks.Count > 1000) break;
            }
            return ticks.ToArray();
        }

        /// <summary>Decimal places that show a step of this size without noise.</summary>
        public static int DecimalsFor(double step)
        {
            if (!(step > 0) || double.IsInfinity(step)) return 0;
            int d = (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
            return Math.Max(0, Math.Min(6, d));
        }
    }

    /// <summary>The scale a legend explains - immutable, Rhino-free, and carried
    /// between components as the wire text <c>iamlab-legend:v1:key=value;…</c>
    /// (the Print Bed / Robot Definition pattern), so the component that coloured
    /// geometry and the Legend that explains it can never disagree. Placement,
    /// fonts and sizes are the Legend component's own inputs and are NOT part of
    /// the wire - a scale means the same thing wherever it is drawn.</summary>
    public sealed class LegendSpec
    {
        public const string Prefix = "iamlab-legend:";
        public const string Version = "v1";

        public LegendType Type { get; }
        public string Title { get; }
        public string Unit { get; }
        public double Min { get; }
        public double Max { get; }
        /// <summary>A <see cref="ColorSchemes"/> name; <c>custom</c> uses <see cref="Colors"/>.</summary>
        public string Scheme { get; }
        /// <summary>Custom ramp stops (continuous/size) or one colour per class/group.</summary>
        public IReadOnlyList<Rgb> Colors { get; }
        /// <summary>Optional positions in [0, 1] for <see cref="Colors"/> on a continuous
        /// custom ramp (the Gradient component's grips): one per colour, ascending.
        /// Empty = evenly spaced.</summary>
        public IReadOnlyList<double> Stops { get; }
        /// <summary>Classes: ascending thresholds (k thresholds = k + 1 classes).
        /// Size: the sample values the size legend shows.</summary>
        public IReadOnlyList<double> Breaks { get; }
        /// <summary>Classes/groups: one name per class or group; continuous: tick
        /// label overrides (optional).</summary>
        public IReadOnlyList<string> Labels { get; }
        /// <summary>Annotated threshold ticks on a continuous scale (e.g. fast/slow).</summary>
        public IReadOnlyList<(double Value, string Label)> Marks { get; }
        /// <summary>The swatch for values the scale cannot place (NaN), or null.</summary>
        public (Rgb Color, string Label)? Unavailable { get; }
        public bool Reverse { get; }
        /// <summary>-1 = derive from the tick step.</summary>
        public int Decimals { get; }
        /// <summary>0 = nice ticks.</summary>
        public int Ticks { get; }

        public LegendSpec(LegendType type, string? title = null, string? unit = null,
                          double min = 0, double max = 1, string? scheme = null,
                          IReadOnlyList<Rgb>? colors = null, IReadOnlyList<double>? breaks = null,
                          IReadOnlyList<string>? labels = null,
                          IReadOnlyList<(double Value, string Label)>? marks = null,
                          (Rgb Color, string Label)? unavailable = null,
                          bool reverse = false, int decimals = -1, int ticks = 0,
                          IReadOnlyList<double>? stops = null)
        {
            Type = type;
            Title = title ?? "";
            Unit = unit ?? "";
            if (double.IsNaN(min) || double.IsInfinity(min)) min = 0;
            if (double.IsNaN(max) || double.IsInfinity(max)) max = 1;
            Min = min; Max = max;
            // a palette number becomes its name here, so the wire always carries names
            Scheme = string.IsNullOrWhiteSpace(scheme) ? ColorSchemes.Blues : ColorSchemes.Normalize(scheme).ToLowerInvariant();
            Colors = colors ?? Array.Empty<Rgb>();
            Stops = ValidStops(stops, Colors.Count);
            var b = new List<double>();
            if (breaks != null) foreach (double v in breaks) if (!double.IsNaN(v) && !double.IsInfinity(v)) b.Add(v);
            if (type == LegendType.Classes) b.Sort();
            Breaks = b;
            Labels = labels ?? Array.Empty<string>();
            var m = new List<(double Value, string Label)>();
            if (marks != null) foreach (var mk in marks) if (!double.IsNaN(mk.Value) && !double.IsInfinity(mk.Value)) m.Add(mk);
            Marks = m;
            Unavailable = unavailable;
            Reverse = reverse;
            Decimals = decimals < 0 ? -1 : Math.Min(6, decimals);
            Ticks = Math.Max(0, Math.Min(50, ticks));
        }

        /// <summary>Positions are kept only when there is one finite, ascending value in
        /// [0, 1] per colour; anything else means "evenly spaced" rather than an error,
        /// so a hand-typed wire cannot break the legend.</summary>
        static IReadOnlyList<double> ValidStops(IReadOnlyList<double>? stops, int colorCount)
        {
            if (stops == null || stops.Count == 0 || stops.Count != colorCount || colorCount < 2)
                return Array.Empty<double>();
            var list = new double[stops.Count];
            double prev = double.NegativeInfinity;
            for (int i = 0; i < stops.Count; i++)
            {
                double v = stops[i];
                if (double.IsNaN(v) || double.IsInfinity(v) || v < prev) return Array.Empty<double>();
                list[i] = v < 0 ? 0 : v > 1 ? 1 : v;
                prev = v;
            }
            return list;
        }

        public int ClassCount => Type switch
        {
            LegendType.Classes => Breaks.Count + 1,
            LegendType.Groups => Math.Max(Labels.Count, Colors.Count),
            _ => 0,
        };

        /// <summary>Which class a value falls in (0 … breaks.Count); -1 for NaN.</summary>
        public int ClassOf(double value)
        {
            if (double.IsNaN(value)) return -1;
            int i = 0;
            while (i < Breaks.Count && value >= Breaks[i]) i++;
            return i;
        }

        /// <summary>Position of a value on the continuous/size scale in [0, 1],
        /// honouring <see cref="Reverse"/>; NaN stays NaN.</summary>
        public double Fraction(double value)
        {
            if (double.IsNaN(value)) return double.NaN;
            double span = Max - Min;
            double t = Math.Abs(span) > 1e-12 ? (value - Min) / span : 0.5;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return Reverse ? 1 - t : t;
        }

        /// <summary>Colour of class or group <paramref name="index"/>: the spec's own
        /// list when it has one; a qualitative palette's colours picked one by one
        /// (cycling past its end); else the ramp sampled evenly over the classes.</summary>
        public Rgb ClassColor(int index)
        {
            int n = Math.Max(1, ClassCount);
            if (index < 0) return Unavailable?.Color ?? Rgb.Of(PlasmaColorScale.Unavailable);
            if (Colors.Count > 0) return Colors[Math.Min(index, Colors.Count - 1)];
            var scheme = ColorSchemes.Resolve(Scheme);
            if (scheme != null && scheme.Qualitative)
            {
                int count = scheme.Stops.Count;
                int pick = index % count;
                return scheme.Stops[Reverse ? count - 1 - pick : pick];
            }
            double t = n == 1 ? 0.5 : (double)index / (n - 1);
            return ColorSchemes.At(Scheme, null, Reverse ? 1 - t : t);
        }

        /// <summary>The colour the scale gives a value - for continuous scales the
        /// ramp, for classes the class colour, for groups the colour of the group
        /// whose index the value names, for size the base colour.</summary>
        public Rgb ColorFor(double value)
        {
            switch (Type)
            {
                case LegendType.Classes:
                    return ClassColor(ClassOf(value));
                case LegendType.Groups:
                    return double.IsNaN(value) ? ClassColor(-1) : ClassColor((int)Math.Round(value));
                case LegendType.Size:
                    return Colors.Count > 0 ? Colors[0] : ColorSchemes.At(Scheme, null, 0.75);
                default:
                    if (double.IsNaN(value)) return Unavailable?.Color ?? Rgb.Of(PlasmaColorScale.Unavailable);
                    return ColorSchemes.At(Scheme, Colors, Stops, Fraction(value));
            }
        }

        /// <summary>Continuous scale ticks: the user's count over the domain, else
        /// nice ticks; always includes both domain ends.</summary>
        public double[] TickValues()
        {
            double lo = Math.Min(Min, Max), hi = Math.Max(Min, Max);
            var list = new List<double>();
            if (Ticks >= 2)
                for (int i = 0; i < Ticks; i++) list.Add(lo + (hi - lo) * i / (Ticks - 1));
            else
            {
                list.Add(lo);
                foreach (double v in NiceTicks.Inside(lo, hi, 5))
                    if (v - lo > (hi - lo) * 0.04 && hi - v > (hi - lo) * 0.04) list.Add(v);
                list.Add(hi);
            }
            return list.ToArray();
        }

        public int EffectiveDecimals()
        {
            if (Decimals >= 0) return Decimals;
            double span = Math.Abs(Max - Min);
            return NiceTicks.DecimalsFor(NiceTicks.NiceStep(span > 0 ? span : 1, 5));
        }

        public string Format(double value)
        {
            if (double.IsNaN(value)) return Unavailable?.Label ?? "n/a";
            return value.ToString("F" + EffectiveDecimals().ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                   + (Unit.Length > 0 ? " " + Unit : "");
        }

        /// <summary>Label of class <paramref name="index"/>: the spec's label when
        /// given, else the threshold range ("&lt; 25 °", "25 ° – 45 °", "≥ 45 °" - the
        /// unit follows every number).</summary>
        public string ClassLabel(int index)
        {
            if (index < 0) return Unavailable?.Label ?? "n/a";
            if (index < Labels.Count && Labels[index].Length > 0) return Labels[index];
            if (Type == LegendType.Groups) return "group " + (index + 1).ToString(CultureInfo.InvariantCulture);
            if (Breaks.Count == 0) return "all";
            if (index == 0) return "< " + Format(Breaks[0]);
            if (index >= Breaks.Count) return "≥ " + Format(Breaks[Breaks.Count - 1]);
            return Format(Breaks[index - 1]) + " – " + Format(Breaks[index]);
        }

        // ── wire format ──

        public string Serialize()
        {
            var sb = new StringBuilder(Prefix).Append(Version).Append(':');
            // scalar values are escaped here; list values arrive with every item
            // already escaped (EscapeItem is a superset), so they are appended raw.
            // The first-field test is explicit: a title ending in ':' must not be
            // mistaken for the version separator and swallow the next ';'.
            bool first = true;
            void Add(string key, string value, bool raw = false)
            {
                if (!first) sb.Append(';');
                first = false;
                sb.Append(key).Append('=').Append(raw ? value : Escape(value));
            }
            string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            Add("type", Type.ToString().ToLowerInvariant());
            if (Title.Length > 0) Add("title", Title);
            if (Unit.Length > 0) Add("unit", Unit);
            Add("min", N(Min));
            Add("max", N(Max));
            Add("scheme", Scheme);
            if (Colors.Count > 0) Add("colors", JoinList(Colors, c => c.Hex), raw: true);
            if (Stops.Count > 0) Add("stops", JoinList(Stops, N), raw: true);
            if (Breaks.Count > 0) Add("breaks", JoinList(Breaks, N), raw: true);
            if (Labels.Count > 0) Add("labels", JoinList(Labels, s => s), raw: true);
            if (Marks.Count > 0)
            {
                var parts = new string[Marks.Count];
                for (int i = 0; i < parts.Length; i++) parts[i] = N(Marks[i].Value) + "|" + EscapeItem(Marks[i].Label);
                Add("marks", string.Join(",", parts), raw: true);
            }
            if (Unavailable.HasValue)
                Add("na", Unavailable.Value.Color.Hex + "|" + EscapeItem(Unavailable.Value.Label), raw: true);
            if (Reverse) Add("reverse", "1");
            if (Decimals >= 0) Add("decimals", Decimals.ToString(CultureInfo.InvariantCulture));
            if (Ticks > 0) Add("ticks", Ticks.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string JoinList<T>(IReadOnlyList<T> items, Func<T, string> f)
        {
            var parts = new string[items.Count];
            for (int i = 0; i < items.Count; i++) parts[i] = EscapeItem(f(items[i]));
            return string.Join(",", parts);
        }

        // values are escaped so ';' '=' and '%' never break the record; inside a
        // list item the list separator ',' and the pair separator '|' are escaped
        // too. Parsing splits on the raw separators first and unescapes each piece.
        static string Escape(string s)
            => s.Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D");
        static string EscapeItem(string s)
            => s.Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D").Replace(",", "%2C").Replace("|", "%7C");
        static string Unescape(string s)
            => s.Replace("%7C", "|").Replace("%2C", ",").Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%");

        public static bool IsWire(string? text)
            => text != null && text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>Parses the wire text. A wrong prefix or version throws; unknown
        /// keys are ignored and missing keys keep their defaults, so a newer
        /// producer never breaks an older consumer.</summary>
        public static LegendSpec Parse(string text)
        {
            if (!IsWire(text)) throw new FormatException("legend wire must start with '" + Prefix + Version + ":'");
            string body = text.Trim().Substring(Prefix.Length);
            int colon = body.IndexOf(':');
            if (colon < 0) throw new FormatException("legend wire has no version");
            string version = body.Substring(0, colon);
            if (!string.Equals(version, Version, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("unsupported legend wire version '" + version + "'");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in body.Substring(colon + 1).Split(new[] { ';' }))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                fields[part.Substring(0, eq).Trim()] = part.Substring(eq + 1);
            }
            var inv = CultureInfo.InvariantCulture;
            string Get(string key) => fields.TryGetValue(key, out var v) ? v : "";
            double Num(string key, double def)
                => double.TryParse(Get(key), NumberStyles.Float, inv, out double d) && !double.IsInfinity(d) ? d : def;
            int Int(string key, int def) => int.TryParse(Get(key), NumberStyles.Integer, inv, out int i) ? i : def;
            // raw items: split on the list separator first, unescape afterwards
            string[] RawList(string key)
            {
                string raw = Get(key);
                return raw.Length == 0 ? Array.Empty<string>() : raw.Split(new[] { ',' });
            }
            string[] List(string key)
            {
                var items = RawList(key);
                for (int i = 0; i < items.Length; i++) items[i] = Unescape(items[i]);
                return items;
            }
            (string head, string tail) Pair(string raw)
            {
                int bar = raw.IndexOf('|');
                return bar < 0 ? (Unescape(raw), "") : (Unescape(raw.Substring(0, bar)), Unescape(raw.Substring(bar + 1)));
            }

            LegendType type = LegendType.Continuous;
            switch (Get("type").Trim().ToLowerInvariant())
            {
                case "classes": type = LegendType.Classes; break;
                case "groups": type = LegendType.Groups; break;
                case "size": type = LegendType.Size; break;
            }
            var colors = new List<Rgb>();
            foreach (string s in List("colors")) if (Rgb.TryParse(s, out var c)) colors.Add(c);
            var stops = new List<double>();
            foreach (string s in List("stops"))
                if (double.TryParse(s, NumberStyles.Float, inv, out double d)) stops.Add(d);
            var breaks = new List<double>();
            foreach (string s in List("breaks"))
                if (double.TryParse(s, NumberStyles.Float, inv, out double d)) breaks.Add(d);
            var marks = new List<(double, string)>();
            foreach (string s in RawList("marks"))
            {
                var (num, label) = Pair(s);
                if (double.TryParse(num, NumberStyles.Float, inv, out double d) && !double.IsInfinity(d))
                    marks.Add((d, label));
            }
            (Rgb, string)? na = null;
            if (Get("na").Length > 0)
            {
                var (hex, label) = Pair(Get("na"));
                if (Rgb.TryParse(hex, out var nc)) na = (nc, label);
            }
            return new LegendSpec(type,
                title: Unescape(Get("title")), unit: Unescape(Get("unit")),
                min: Num("min", 0), max: Num("max", 1),
                scheme: ColorSchemes.IsKnown(Unescape(Get("scheme"))) ? Unescape(Get("scheme")) : (colors.Count > 0 ? ColorSchemes.Custom : ColorSchemes.Blues),
                colors: colors, breaks: breaks, labels: List("labels"), marks: marks, unavailable: na,
                reverse: Get("reverse") == "1" || Get("reverse").Equals("true", StringComparison.OrdinalIgnoreCase),
                decimals: Int("decimals", -1), ticks: Int("ticks", 0), stops: stops);
        }

        public static bool TryParse(string? text, out LegendSpec? spec, out string? error)
        {
            try { spec = Parse(text ?? ""); error = null; return true; }
            catch (FormatException ex) { spec = null; error = ex.Message; return false; }
        }

        public override string ToString() => Serialize();
    }
}
