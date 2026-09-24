using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lugworm.Core.Gcode
{
    /// <summary>One word from a G-code line. The lexer deliberately keeps the
    /// original numeric text so callers can distinguish a malformed number from a
    /// missing parameter instead of silently reusing modal state.</summary>
    public readonly struct GcodeWord
    {
        public readonly char Letter;
        public readonly string Text;
        public readonly double Value;

        public GcodeWord(char letter, string text, double value)
        { Letter = letter; Text = text; Value = value; }
    }

    /// <summary>Tokenized executable line. N-numbers and checksums are accepted but
    /// are not executable words.</summary>
    public sealed class GcodeLine
    {
        public int LineNumber { get; }
        public char CommandLetter { get; }
        public int CommandNumber { get; }
        public string Command => CommandLetter == '\0'
            ? ""
            : CommandLetter + CommandNumber.ToString(CultureInfo.InvariantCulture);
        public IReadOnlyList<GcodeWord> Words { get; }

        internal GcodeLine(int lineNumber, char commandLetter, int commandNumber,
                           IReadOnlyList<GcodeWord> words)
        {
            LineNumber = lineNumber;
            CommandLetter = commandLetter;
            CommandNumber = commandNumber;
            Words = words;
        }
    }

    /// <summary>Shared G-code text primitives. The parser is intentionally small,
    /// but it is a real word lexer: compact words (G1X10), zero-padded commands,
    /// N-numbers, checksums and comments are handled consistently by every safety
    /// boundary that consumes G-code.</summary>
    public static class GcodeText
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string F3(double v) => v.ToString("0.000", Inv);
        public static string F5(double v) => v.ToString("0.00000", Inv);

        /// <summary>Line without ';' or parenthesized comments, trimmed.</summary>
        public static string Strip(string line)
        {
            if (line == null) return "";
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);

            var sb = new StringBuilder(line.Length);
            int commentDepth = 0;
            foreach (char c in line)
            {
                if (c == '(') { commentDepth++; continue; }
                if (c == ')' && commentDepth > 0) { commentDepth--; continue; }
                if (commentDepth == 0) sb.Append(c);
            }
            if (commentDepth != 0)
                throw new FormatException("unterminated parenthesized G-code comment");
            return sb.ToString().Trim();
        }

        /// <summary>True for a display / host message line (M117, M118), whose payload
        /// is free text rather than G-code words. An optional N-number may precede the
        /// command; comments are stripped first.</summary>
        public static bool IsFreeTextMessage(string raw)
        {
            string line = Strip(raw);
            int p = 0;
            if (p < line.Length && (line[p] == 'N' || line[p] == 'n'))
            {
                int q = p + 1;
                while (q < line.Length && char.IsDigit(line[q])) q++;
                if (q > p + 1) { p = q; while (p < line.Length && char.IsWhiteSpace(line[p])) p++; }
            }
            if (p >= line.Length || (line[p] != 'M' && line[p] != 'm')) return false;
            int start = ++p;
            while (p < line.Length && char.IsDigit(line[p])) p++;
            if (p == start || (p < line.Length && !char.IsWhiteSpace(line[p]))) return false;
            string code = line.Substring(start, p - start);
            return code == "117" || code == "118";
        }

        /// <summary>Tokenize one line. Numeric failures include the source line
        /// number and are never converted into a modal-value reuse.</summary>
        public static GcodeLine Lex(string raw, int lineNumber = 1)
        {
            string line = Strip(raw);
            if (line.Length == 0)
                return new GcodeLine(lineNumber, '\0', 0, Array.Empty<GcodeWord>());

            int star = line.IndexOf('*');
            if (star >= 0)
            {
                string checksum = line.Substring(star + 1).Trim();
                if (checksum.Length == 0 || !int.TryParse(checksum, NumberStyles.Integer, Inv, out _))
                    throw Error(lineNumber, "invalid checksum");
                line = line.Substring(0, star).TrimEnd();
            }

            var words = new List<GcodeWord>();
            int p = 0;
            while (p < line.Length)
            {
                while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
                if (p >= line.Length) break;
                if (!char.IsLetter(line[p]))
                    throw Error(lineNumber, "expected a word letter near '" + line.Substring(p) + "'");

                char letter = char.ToUpperInvariant(line[p++]);
                int start = p;
                if (p < line.Length && (line[p] == '+' || line[p] == '-')) p++;
                int digits = 0;
                while (p < line.Length && char.IsDigit(line[p])) { p++; digits++; }
                if (p < line.Length && line[p] == '.')
                {
                    p++;
                    while (p < line.Length && char.IsDigit(line[p])) { p++; digits++; }
                }
                if (digits == 0)
                    throw Error(lineNumber, $"word {letter} has no numeric value");
                if (p < line.Length && (line[p] == 'e' || line[p] == 'E'))
                {
                    p++;
                    if (p < line.Length && (line[p] == '+' || line[p] == '-')) p++;
                    int expDigits = 0;
                    while (p < line.Length && char.IsDigit(line[p])) { p++; expDigits++; }
                    if (expDigits == 0)
                        throw Error(lineNumber, $"word {letter} has an invalid exponent");
                }

                string text = line.Substring(start, p - start);
                if (!double.TryParse(text, NumberStyles.Float, Inv, out double value)
                    || double.IsNaN(value) || double.IsInfinity(value))
                    throw Error(lineNumber, $"word {letter}{text} is not a finite number");
                words.Add(new GcodeWord(letter, text, value));
            }

            int commandIndex = 0;
            while (commandIndex < words.Count && words[commandIndex].Letter == 'N') commandIndex++;
            if (commandIndex >= words.Count)
                return new GcodeLine(lineNumber, '\0', 0, words);

            var commandWord = words[commandIndex];
            if (commandWord.Letter != 'G' && commandWord.Letter != 'M' && commandWord.Letter != 'T')
                throw Error(lineNumber, $"word {commandWord.Letter} is not an executable command");
            double rounded = Math.Round(commandWord.Value);
            if (Math.Abs(commandWord.Value - rounded) > 1e-9 || rounded < 0 || rounded > int.MaxValue)
                throw Error(lineNumber, $"command {commandWord.Letter}{commandWord.Text} is not an integer code");

            return new GcodeLine(lineNumber, commandWord.Letter, (int)rounded, words);
        }

        static FormatException Error(int line, string message)
            => new FormatException($"line {line}: {message}");

        /// <summary>Byte offset just past the end of every line (including its line
        /// terminator), the frame RRF's <c>job.filePosition</c> and the restart
        /// planner's percent mode use. With <paramref name="exactBytes"/> the offsets
        /// are read off the real file (BOM skipped, any terminator); without them the
        /// text is assumed re-serialized as UTF-8 with LF, which is approximate for a
        /// file that was CRLF on disk.</summary>
        public static long[] LineByteEnds(IReadOnlyList<string> lines, byte[]? exactBytes)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            var ends = new long[lines.Count];
            if (exactBytes != null)
            {
                int p = exactBytes.Length >= 3 && exactBytes[0] == 0xef && exactBytes[1] == 0xbb && exactBytes[2] == 0xbf ? 3 : 0;
                for (int i = 0; i < lines.Count; i++)
                {
                    while (p < exactBytes.Length && exactBytes[p] != (byte)'\n' && exactBytes[p] != (byte)'\r') p++;
                    if (p < exactBytes.Length && exactBytes[p++] == (byte)'\r'
                        && p < exactBytes.Length && exactBytes[p] == (byte)'\n') p++;
                    ends[i] = p;
                }
                return ends;
            }
            long p2 = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                p2 += System.Text.Encoding.UTF8.GetByteCount(lines[i] ?? "") + 1;
                ends[i] = p2;
            }
            return ends;
        }

        /// <summary>Parameter words after the command. N-numbers are omitted.
        /// This compatibility helper is used by restart planning; safety parsing
        /// uses <see cref="Lex"/> directly so malformed values throw.</summary>
        public static IEnumerable<(char w, double v)> Words(string s)
        {
            var line = Lex(s);
            bool commandSeen = false;
            foreach (var word in line.Words)
            {
                if (!commandSeen && (word.Letter == line.CommandLetter || word.Letter == 'N'))
                {
                    if (word.Letter == line.CommandLetter) commandSeen = true;
                    continue;
                }
                if (word.Letter != 'N') yield return (word.Letter, word.Value);
            }
        }
    }
}
