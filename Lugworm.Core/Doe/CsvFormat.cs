using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lugworm.Core.Doe
{
    /// <summary>The one number convention a CSV file was detected to use. Detection is
    /// file-level (release-readiness spec §5.2): a file is English, German or neutral
    /// as a whole - never guessed cell by cell - and anything mixed or ambiguous is
    /// refused with a <see cref="CsvFormatException"/> instead of silently mis-read.</summary>
    public enum CsvNumberStyle
    {
        /// <summary>Invariant decimal point ("1.5"); delimiter comma, semicolon or tab.</summary>
        English,
        /// <summary>Decimal comma ("1,5"); delimiter semicolon or tab (or quoted commas).</summary>
        German,
        /// <summary>Integer/blank-only numeric evidence - no decimal convention needed.</summary>
        Neutral,
    }

    /// <summary>A CSV file whose delimiter/number layout is mixed, inconsistent or
    /// ambiguous. <see cref="Line"/> is 1-based within the file's non-empty lines
    /// (0 = whole file). The message tells the user what to fix.</summary>
    public sealed class CsvFormatException : FormatException
    {
        public int Line { get; }
        public CsvFormatException(string message, int line = 0) : base(message) { Line = line; }
    }

    /// <summary>File-level delimiter + number-convention detection shared by the RSM
    /// table reader and the pump-calibration reader. Independent of the Windows/Rhino
    /// culture by construction: only the file's own bytes decide.</summary>
    public static class CsvFormat
    {
        public readonly struct Detection
        {
            public readonly char Delimiter;
            public readonly CsvNumberStyle Style;
            public Detection(char delimiter, CsvNumberStyle style) { Delimiter = delimiter; Style = style; }
        }

        // number-like: only digits, signs, separators and an optional exponent - a cell
        // matching this MUST parse under the detected style, or the file is refused
        static readonly Regex NumberLike = new(@"^[+-]?[0-9.,]+([eE][+-]?[0-9]+)?$", RegexOptions.Compiled);
        static readonly Regex EnglishDecimal = new(@"^[+-]?(\d+\.\d*|\.\d+)([eE][+-]?\d+)?$", RegexOptions.Compiled);
        static readonly Regex GermanDecimal = new(@"^[+-]?\d*,\d+([eE][+-]?\d+)?$", RegexOptions.Compiled);
        static readonly Regex PlainInteger = new(@"^[+-]?\d+([eE][+-]?\d+)?$", RegexOptions.Compiled);

        static readonly char[] CandidateDelimiters = { '\t', ';', ',' };

        /// <summary>Detects delimiter and number style over ALL non-empty lines.
        /// <paramref name="expectedCols"/> pins the required column count (the pump
        /// table's 2); null lets the file's own consistency decide. Throws
        /// <see cref="CsvFormatException"/> on mixed conventions, inconsistent column
        /// counts, or number-like cells that fit no convention.</summary>
        public static Detection Detect(IReadOnlyList<string> lines, int? expectedCols = null)
        {
            if (lines == null || lines.Count == 0)
                return new Detection(',', CsvNumberStyle.Neutral);

            var evaluated = new List<Candidate>();
            foreach (var d in CandidateDelimiters)
                evaluated.Add(Evaluate(lines, d, expectedCols));

            // best usable candidate: most columns, then most decimal evidence, then
            // the candidate-order priority (tab, semicolon, comma)
            Candidate? best = null;
            foreach (var c in evaluated)
            {
                if (!c.StructurallyValid || c.Conflicted) continue;
                if (best == null
                    || c.Columns > best.Value.Columns
                    || (c.Columns == best.Value.Columns && c.DecimalEvidence > best.Value.DecimalEvidence))
                    best = c;
            }

            if (best != null && (expectedCols != null || best.Value.Columns > 1))
            {
                ValidateQuoting(lines, best.Value.Delimiter);
                Verify(lines, best.Value);
                return new Detection(best.Value.Delimiter, best.Value.Style);
            }

            // a multi-column candidate existed but carried both conventions
            var conflicted = evaluated.FirstOrDefault(c => c.StructurallyValid && c.Conflicted && c.Columns > 1);
            if (conflicted.Conflicted)
            {
                ValidateQuoting(lines, conflicted.Delimiter);
                throw new CsvFormatException(conflicted.ConflictMessage!, conflicted.ConflictLine);
            }

            // a delimiter occurs but the counts disagree row to row
            foreach (var c in evaluated)
                if (!c.StructurallyValid && c.MaxColumns > 1)
                {
                    ValidateQuoting(lines, c.Delimiter);
                    throw new CsvFormatException(
                        expectedCols != null
                            ? $"'{Show(c.Delimiter)}'-delimited rows have {c.MinColumns}..{c.MaxColumns} columns " +
                              $"where {expectedCols} are expected - export one consistent English or German CSV"
                            : $"inconsistent column counts ({c.MinColumns}..{c.MaxColumns}) for the " +
                              $"'{Show(c.Delimiter)}' delimiter - export one consistent English or German CSV",
                        c.FirstInconsistentLine);
                }

            // no delimiter at all: a single-column file; classify the whole lines
            var single = Evaluate(lines, ',', expectedCols: null);
            if (single.Conflicted)
                throw new CsvFormatException(single.ConflictMessage!, single.ConflictLine);
            var det = new Detection(',', single.Style);
            ValidateQuoting(lines, ',');
            Verify(lines, new Candidate(',', true, 1, 1, 1, 0, single.Style, false, null, 0, 0));
            return det;
        }

        /// <summary>Parses one cell under the file's detected convention; null for
        /// blanks and non-numeric text (callers map that to NaN, exactly as before).</summary>
        public static double? ParseCell(string s, CsvNumberStyle style)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            switch (style)
            {
                case CsvNumberStyle.German:
                    if (s.Contains('.')) return null;   // thousands separators are never guessed
                    return double.TryParse(s.Replace(',', '.'), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out double g)
                           && !double.IsNaN(g) && !double.IsInfinity(g) ? g : null;
                default:
                    if (s.Contains(',')) return null;
                    return double.TryParse(s, NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out double v)
                           && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
            }
        }

        static void ValidateQuoting(IReadOnlyList<string> lines, char delimiter)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                string line = lines[li];
                bool quoted = false, fieldHasContent = false, quoteClosed = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (quoted)
                    {
                        if (c != '"') continue;
                        if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                        quoted = false;
                        quoteClosed = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        if (fieldHasContent || quoteClosed)
                            throw MalformedQuote(li, "a quote starts inside an unquoted field");
                        quoted = true;
                    }
                    else if (c == delimiter)
                    {
                        fieldHasContent = false;
                        quoteClosed = false;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        if (quoteClosed)
                            throw MalformedQuote(li, "text follows a closing quote before the delimiter");
                        fieldHasContent = true;
                    }
                }
                if (quoted) throw MalformedQuote(li, "a quoted field is not closed");
            }

            static CsvFormatException MalformedQuote(int zeroBasedLine, string detail)
                => new CsvFormatException(
                    $"malformed CSV quoting on line {zeroBasedLine + 1}: {detail} - " +
                    "export one consistently quoted English or German CSV",
                    zeroBasedLine + 1);
        }

        readonly struct Candidate
        {
            public readonly char Delimiter;
            public readonly bool StructurallyValid;
            public readonly int Columns, MinColumns, MaxColumns, FirstInconsistentLine;
            public readonly CsvNumberStyle Style;
            public readonly bool Conflicted;
            public readonly string? ConflictMessage;
            public readonly int ConflictLine, DecimalEvidence;

            public Candidate(char delimiter, bool valid, int cols, int minCols, int maxCols,
                             int firstInconsistent, CsvNumberStyle style, bool conflicted,
                             string? conflictMessage, int conflictLine, int decimalEvidence)
            {
                Delimiter = delimiter; StructurallyValid = valid; Columns = cols;
                MinColumns = minCols; MaxColumns = maxCols; FirstInconsistentLine = firstInconsistent;
                Style = style; Conflicted = conflicted; ConflictMessage = conflictMessage;
                ConflictLine = conflictLine; DecimalEvidence = decimalEvidence;
            }
        }

        static Candidate Evaluate(IReadOnlyList<string> lines, char delimiter, int? expectedCols)
        {
            int cols = -1, minCols = int.MaxValue, maxCols = 0, firstInconsistent = 0;
            bool consistent = true;
            int english = 0, german = 0;
            int englishLine = 0, germanLine = 0;
            string englishCell = "", germanCell = "";

            for (int li = 0; li < lines.Count; li++)
            {
                var cells = TableIo.SplitCsv(lines[li], delimiter);
                minCols = Math.Min(minCols, cells.Length);
                maxCols = Math.Max(maxCols, cells.Length);
                if (cols < 0) cols = cells.Length;
                else if (cells.Length != cols && consistent) { consistent = false; firstInconsistent = li + 1; }

                foreach (var raw in cells)
                {
                    var cell = raw.Trim();
                    if (cell.Length == 0 || !cell.Any(char.IsDigit)) continue;
                    if (PlainInteger.IsMatch(cell)) continue;   // neutral evidence
                    if (EnglishDecimal.IsMatch(cell)) { english++; if (englishLine == 0) { englishLine = li + 1; englishCell = cell; } }
                    else if (GermanDecimal.IsMatch(cell)) { german++; if (germanLine == 0) { germanLine = li + 1; germanCell = cell; } }
                }
            }

            bool valid = consistent && (expectedCols == null || cols == expectedCols.Value);
            bool conflicted = english > 0 && german > 0;
            string? msg = null;
            int conflictLine = 0;
            if (conflicted)
            {
                bool englishLater = englishLine >= germanLine;
                conflictLine = Math.Max(englishLine, germanLine);
                msg = englishLater
                    ? $"mixed decimal conventions: '{englishCell}' (line {englishLine}) looks English while " +
                      $"'{germanCell}' (line {germanLine}) looks German - export one consistent English or German CSV"
                    : $"mixed decimal conventions: '{germanCell}' (line {germanLine}) looks German while " +
                      $"'{englishCell}' (line {englishLine}) looks English - export one consistent English or German CSV";
            }
            var style = english > 0 ? CsvNumberStyle.English
                      : german > 0 ? CsvNumberStyle.German
                      : CsvNumberStyle.Neutral;
            return new Candidate(delimiter, valid, cols < 0 ? 0 : cols, minCols, maxCols,
                                 firstInconsistent, style, conflicted, msg, conflictLine, english + german);
        }

        /// <summary>Every number-like cell of the chosen layout must parse under the
        /// chosen style - "1,5,2,5" or "1.234,5" is a refusal, never a silent value.</summary>
        static void Verify(IReadOnlyList<string> lines, Candidate chosen)
        {
            for (int li = 0; li < lines.Count; li++)
            {
                var cells = chosen.Columns <= 1
                    ? new[] { lines[li] }
                    : TableIo.SplitCsv(lines[li], chosen.Delimiter);
                foreach (var raw in cells)
                {
                    var cell = raw.Trim();
                    if (cell.Length == 0 || !cell.Any(char.IsDigit)) continue;
                    if (NumberLike.IsMatch(cell) && ParseCell(cell, chosen.Style) == null)
                        throw new CsvFormatException(
                            $"'{cell}' (line {li + 1}) fits neither the detected " +
                            $"{chosen.Style} number convention nor a plain integer - " +
                            "export one consistent English or German CSV without thousands separators",
                            li + 1);
                }
            }
        }

        static string Show(char d) => d == '\t' ? "\\t" : d.ToString();
    }
}
