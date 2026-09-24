using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>A narrow, independent representation of a live homeall.g macro.
    /// It intentionally does not use GcodeParser: homing moves legitimately use
    /// H1/H2, which is not part of the job-file grammar.</summary>
    public sealed class HomeMacroPlan
    {
        public readonly List<HomeMacroMove> PostTriggerMoves = new List<HomeMacroMove>();
        public bool HasTrigger { get; internal set; }

        /// <summary>Replays every post-trigger Cartesian move from a verified
        /// endstop pose. The verification provider owns that pose; this class
        /// only applies the macro's G90/G91 modal state.</summary>
        public List<Vec3> Replay(Vec3 verifiedTriggerPose)
        {
            var result = new List<Vec3>();
            Vec3 current = verifiedTriggerPose;
            foreach (var move in PostTriggerMoves)
            {
                double x = move.Absolute
                    ? (move.X.HasValue ? move.X.Value : current.X)
                    : current.X + (move.X ?? 0.0);
                double y = move.Absolute
                    ? (move.Y.HasValue ? move.Y.Value : current.Y)
                    : current.Y + (move.Y ?? 0.0);
                double z = move.Absolute
                    ? (move.Z.HasValue ? move.Z.Value : current.Z)
                    : current.Z + (move.Z ?? 0.0);
                current = new Vec3(x, y, z);
                if (move.HasPosition) result.Add(current);
            }
            return result;
        }
    }

    public readonly struct HomeMacroMove
    {
        public readonly int G;
        public readonly bool Absolute;
        public readonly double? X, Y, Z, Feed;
        public bool HasPosition => X.HasValue || Y.HasValue || Z.HasValue;

        public HomeMacroMove(int g, bool absolute, double? x, double? y, double? z, double? feed)
        { G = g; Absolute = absolute; X = x; Y = y; Z = z; Feed = feed; }
    }

    public static class HomeMacroReader
    {
        const string Number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        const string WordPattern = @"(?<letter>[A-Za-z])\s*(?<value>" + Number + ")";

        /// <summary>`M98 P"0:/sys/homez.g"` and the unquoted legacy form. Matched
        /// before the numeric word tokenizer because the P parameter is a path,
        /// not a number.</summary>
        static readonly Regex IncludePattern = new Regex(
            @"^M98\s+P\s*(?:""(?<path>[^""]*)""|(?<path>[^\s;]+))\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Total macro files one home may pull in, root included. A real
        /// homeall.g calls three per-axis macros; the cap only exists so a malformed
        /// or cyclic set cannot spin the downloader.</summary>
        public const int MaxMacroFiles = 16;

        /// <summary>Nesting depth of M98 calls, root counted as depth 1.</summary>
        public const int MaxIncludeDepth = 6;

        /// <summary>Non-motion commands that may appear in a home macro without
        /// affecting the replayed park path. Kept deliberately short: anything not
        /// listed still fails the parse, because an unrecognised line could be
        /// motion this reader would silently drop.</summary>
        static bool IsInertMCode(int code) => code == 400 || code == 116;

        /// <summary>Backwards-compatible overload. Without a resolver an `M98`
        /// macro call cannot be followed, so it fails closed.</summary>
        public static bool TryParse(string text, out HomeMacroPlan plan, out string error)
            => TryParse(text, null, out plan, out error);

        /// <summary>Parses a home macro, following `M98` calls through
        /// <paramref name="resolveMacro"/>. The resolver is handed a canonical path
        /// (see <see cref="NormalizeMacroPath"/>) and returns the macro text, or null
        /// when that file is not available - which fails the parse rather than
        /// silently skipping a file that may contain motion.</summary>
        public static bool TryParse(string text, Func<string, string?>? resolveMacro,
                                    out HomeMacroPlan plan, out string error)
        {
            plan = new HomeMacroPlan();
            error = "";
            if (text == null) { error = "homeall.g is unreadable"; return false; }

            var result = new HomeMacroPlan();
            bool absolute = true;
            int files = 1;
            var stack = new List<string> { NormalizeMacroPath("homeall.g") };
            if (!ParseInto(text, "homeall.g", result, resolveMacro, stack, ref files,
                           ref absolute, out error))
                return false;

            if (!result.HasTrigger)
            {
                error = "homeall.g: contains no G1 H1/H2 homing trigger";
                return false;
            }
            plan = result;
            return true;
        }

        /// <summary>Canonical paths of every macro this text calls with `M98`.
        /// Lets a caller pre-fetch the whole include tree (breadth-first, feeding
        /// each fetched text back through this method) before parsing.</summary>
        public static List<string> ExtractIncludes(string? text)
        {
            var found = new List<string>();
            if (text == null) return found;
            foreach (string source in text.Replace("\r", "").Split('\n'))
            {
                string line = StripComment(source);
                if (line.Length == 0) continue;
                var match = IncludePattern.Match(line);
                if (!match.Success) continue;
                string path = NormalizeMacroPath(match.Groups["path"].Value);
                if (path.Length > 0 && !found.Contains(path))
                    found.Add(path);
            }
            return found;
        }

        /// <summary>Canonical form of an `M98 P…` path: forward slashes, lower case,
        /// no `0:` volume prefix, and a bare filename resolved against `/sys` the way
        /// RepRapFirmware resolves a macro called from `/sys`.</summary>
        public static string NormalizeMacroPath(string? path)
        {
            string p = (path ?? "").Trim().Replace('\\', '/');
            if (p.Length == 0) return "";
            int volume = p.IndexOf(':');
            if (volume >= 0) p = p.Substring(volume + 1);
            if (!p.StartsWith("/", StringComparison.Ordinal)) p = "/sys/" + p;
            return p.ToLowerInvariant();
        }

        // ---- internals ----------------------------------------------------

        static string StripComment(string source)
        {
            string line = source;
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line.Substring(0, comment);
            return line.Trim();
        }

        static bool ParseInto(string text, string sourceName, HomeMacroPlan plan,
                              Func<string, string?>? resolveMacro, List<string> stack,
                              ref int files, ref bool absolute, out string error)
        {
            error = "";
            int lineNumber = 0;
            foreach (string source in (text ?? "").Replace("\r", "").Split('\n'))
            {
                lineNumber++;
                string line = StripComment(source);
                if (line.Length == 0) continue;

                var include = IncludePattern.Match(line);
                if (include.Success)
                {
                    if (!FollowInclude(include.Groups["path"].Value, sourceName, lineNumber,
                                       plan, resolveMacro, stack, ref files, ref absolute, out error))
                        return false;
                    continue;
                }

                var words = Regex.Matches(line, WordPattern, RegexOptions.CultureInvariant);
                string remainder = Regex.Replace(line, WordPattern, "", RegexOptions.CultureInvariant);
                if (words.Count == 0 || remainder.Trim().Length != 0)
                    return Fail(out error, sourceName, lineNumber, "contains an unsupported token");

                int gCount = 0, mCount = 0, g = -1, m = -1;
                var seen = new HashSet<char>();
                double? x = null, y = null, z = null, f = null, h = null, p = null, s = null;
                foreach (Match word in words)
                {
                    char letter = char.ToUpperInvariant(word.Groups["letter"].Value[0]);
                    if (!seen.Add(letter))
                        return Fail(out error, sourceName, lineNumber, $"repeats parameter {letter}");
                    if (!double.TryParse(word.Groups["value"].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value)
                        || double.IsNaN(value) || double.IsInfinity(value))
                        return Fail(out error, sourceName, lineNumber, "contains a non-finite value");

                    if (letter == 'G')
                    {
                        gCount++;
                        if (value != Math.Truncate(value))
                            return Fail(out error, sourceName, lineNumber, "G command is not integral");
                        g = (int)value;
                    }
                    else if (letter == 'M')
                    {
                        mCount++;
                        if (value != Math.Truncate(value))
                            return Fail(out error, sourceName, lineNumber, "M command is not integral");
                        m = (int)value;
                    }
                    else if (letter == 'X') x = value;
                    else if (letter == 'Y') y = value;
                    else if (letter == 'Z') z = value;
                    else if (letter == 'F') f = value;
                    else if (letter == 'H') h = value;
                    else if (letter == 'P') p = value;
                    else if (letter == 'S') s = value;
                    else return Fail(out error, sourceName, lineNumber, $"parameter {letter} is not allowed");
                }

                if (mCount > 0)
                {
                    if (mCount > 1 || gCount > 0)
                        return Fail(out error, sourceName, lineNumber, "must contain exactly one command");
                    if (!IsInertMCode(m))
                        return Fail(out error, sourceName, lineNumber,
                            $"M{m} is outside the supported home subset");
                    if (x.HasValue || y.HasValue || z.HasValue || f.HasValue
                        || h.HasValue || p.HasValue || s.HasValue)
                        return Fail(out error, sourceName, lineNumber, $"M{m} cannot have parameters");
                    continue;   // waits for motion to finish; nothing to replay
                }
                if (gCount != 1)
                    return Fail(out error, sourceName, lineNumber, "must contain exactly one G command");
                if ((p.HasValue || s.HasValue) && g != 4)
                    return Fail(out error, sourceName, lineNumber,
                        $"parameter {(p.HasValue ? 'P' : 'S')} is not allowed on G{g}");

                if (g == 4)
                {
                    if (x.HasValue || y.HasValue || z.HasValue || f.HasValue || h.HasValue)
                        return Fail(out error, sourceName, lineNumber, "G4 accepts only P/S");
                    continue;   // dwell; no motion
                }
                if (g == 90 || g == 91)
                {
                    if (x.HasValue || y.HasValue || z.HasValue || f.HasValue || h.HasValue)
                        return Fail(out error, sourceName, lineNumber, $"G{g} cannot have parameters");
                    absolute = g == 90;
                    continue;
                }
                if (g != 0 && g != 1)
                    return Fail(out error, sourceName, lineNumber,
                        $"G{g} is outside the supported home subset");
                if (h.HasValue)
                {
                    if (g != 1 || (h.Value != 1.0 && h.Value != 2.0))
                        return Fail(out error, sourceName, lineNumber,
                            "only G1 H1/H2 homing triggers are supported");
                    plan.HasTrigger = true;
                    plan.PostTriggerMoves.Clear();
                    continue;
                }

                if (plan.HasTrigger)
                    plan.PostTriggerMoves.Add(new HomeMacroMove(g, absolute, x, y, z, f));
            }
            return true;
        }

        static bool FollowInclude(string rawPath, string sourceName, int lineNumber,
                                  HomeMacroPlan plan, Func<string, string?>? resolveMacro,
                                  List<string> stack, ref int files, ref bool absolute,
                                  out string error)
        {
            string path = NormalizeMacroPath(rawPath);
            if (path.Length == 0)
                return Fail(out error, sourceName, lineNumber, "M98 has no macro path");
            if (resolveMacro == null)
                return Fail(out error, sourceName, lineNumber,
                    $"M98 calls {path} but no macro source was supplied");
            if (stack.Contains(path))
                return Fail(out error, sourceName, lineNumber,
                    $"M98 {path} is a recursive macro call");
            if (stack.Count >= MaxIncludeDepth)
                return Fail(out error, sourceName, lineNumber,
                    $"M98 nesting exceeds {MaxIncludeDepth} levels");
            if (files >= MaxMacroFiles)
                return Fail(out error, sourceName, lineNumber,
                    $"more than {MaxMacroFiles} macro files were pulled in");

            string? text = resolveMacro(path);
            if (text == null)
                return Fail(out error, sourceName, lineNumber,
                    $"M98 calls {path}, which could not be read");

            files++;
            stack.Add(path);
            // RepRapFirmware's G90/G91 state is global, so the callee inherits the
            // caller's modal state and any change it makes persists on return.
            bool ok = ParseInto(text, path, plan, resolveMacro, stack, ref files,
                                ref absolute, out error);
            stack.RemoveAt(stack.Count - 1);
            return ok;
        }

        static bool Fail(out string error, string sourceName, int line, string reason)
        {
            error = $"{sourceName} line {line}: {reason}";
            return false;
        }
    }
}
