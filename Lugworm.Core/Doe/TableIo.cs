using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Lugworm.Core.Doe
{
    /// <summary>Header + string cell grid, as read from a linked .csv or .xlsx table.</summary>
    public sealed class DataTableLite
    {
        public string[] Headers = Array.Empty<string>();
        public string[][] Rows = Array.Empty<string[]>();
        /// <summary>File-level number convention detected on read (spec §5.2).
        /// XLSX cells are XML-invariant, so the xlsx path is always English.</summary>
        public CsvNumberStyle NumberStyle = CsvNumberStyle.English;

        public int ColumnIndex(string name)
            => Array.FindIndex(Headers, h => string.Equals(h.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>One cell under this table's detected convention; null when blank
        /// or non-numeric.</summary>
        public double? ParseCell(string s) => CsvFormat.ParseCell(s, NumberStyle);

        /// <summary>Column as doubles; unparseable/empty cells → NaN.</summary>
        public double[] NumericColumn(int i)
            => Rows.Select(r => i < r.Length ? ParseCell(r[i]) ?? double.NaN : double.NaN).ToArray();

        public double[]? FeasibilityColumn()
        {
            int column = ColumnIndex("feasible");
            if (column < 0) return null;
            return Rows.Select((row, i) =>
            {
                string text = column < row.Length ? row[column] : "";
                if (string.IsNullOrWhiteSpace(text)) return 1.0;
                double? value = ParseCell(text);
                if (value != 0 && value != 1)
                    throw new ArgumentException(FormattableString.Invariant($"feasible at row {i + 1} must be 0, 1 or blank"));
                return value.Value;
            }).ToArray();
        }

        /// <summary>True if ≥ 60 % of the non-empty cells in column i parse as numbers.</summary>
        public bool IsNumeric(int i)
        {
            int n = 0, ok = 0;
            foreach (var r in Rows)
            {
                if (i >= r.Length || string.IsNullOrWhiteSpace(r[i])) continue;
                n++;
                if (ParseCell(r[i]) != null) ok++;
            }
            return n > 0 && ok >= 0.6 * n;
        }
    }

    /// <summary>Reads linked measurement tables (.csv with , ; or tab separators and
    /// decimal comma tolerance, or .xlsx via the built-in reader) and writes CSV.</summary>
    public static class TableIo
    {
        public static DataTableLite Read(string path, string? sheet = null)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".xlsx" || ext == ".xlsm")
                return FromGrid(XlsxReader.ReadSheet(path, sheet));
            return ReadCsv(path);
        }

        static DataTableLite FromGrid(string[][] grid)
        {
            // header = the WIDEST row among the first 20: exported plans carry a title
            // and a 4-column factor-summary block above the run table - the run-table
            // header (Run + factors + x_* + responses) is always the widest
            int start = -1, best = 1;
            for (int i = 0; i < Math.Min(grid.Length, 20); i++)
            {
                int n = grid[i].Count(c => !string.IsNullOrWhiteSpace(c));
                if (n > best) { best = n; start = i; }
            }
            if (start < 0) return new DataTableLite();
            return new DataTableLite
            {
                Headers = grid[start].Select(h => h.Trim()).ToArray(),
                Rows = grid.Skip(start + 1)
                           .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
                           .ToArray(),
            };
        }

        public static DataTableLite ReadCsv(string path)
        {
            string[] raw;
            try { raw = File.ReadAllLines(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new IOException($"Cannot read CSV '{path}': {ex.Message}", ex);
            }
            var lines = raw.Where(l => l.Trim().Length > 0).ToArray();
            if (lines.Length == 0) return new DataTableLite();
            // file-level delimiter + number-convention detection (spec §5.2); throws
            // CsvFormatException on mixed/ambiguous files instead of guessing per cell
            var det = CsvFormat.Detect(lines);
            var rows = lines.Select(l => SplitCsv(l, det.Delimiter)).ToArray();
            return new DataTableLite
            {
                Headers = rows[0].Select(h => h.Trim()).ToArray(),
                Rows = rows.Skip(1).ToArray(),
                NumberStyle = det.Style,
            };
        }

        internal static string[] SplitCsv(string line, char sep)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == sep) { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells.ToArray();
        }

        /// <summary>Invariant first, then decimal-comma fallback ("1,25" → 1.25).</summary>
        public static double? ParseNum(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
#if NETFRAMEWORK
            if (s.Contains(",") && !s.Contains(".") &&
#else
            if (s.Contains(',') && !s.Contains('.') &&
#endif
                double.TryParse(s.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        public static void WriteCsv(string path, string[] headers, IEnumerable<string[]> rows, char sep = ',')
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep.ToString(), headers.Select(h => Quote(h, sep))));
            foreach (var r in rows)
                sb.AppendLine(string.Join(sep.ToString(), r.Select(c => Quote(c, sep))));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        static string Quote(string s, char sep)
#if NETFRAMEWORK
            => s.IndexOf(sep) >= 0 || s.Contains("\"") || s.Contains("\n")
#else
            => s.Contains(sep) || s.Contains('"') || s.Contains('\n')
#endif
               ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Minimal .xlsx reader: first (or named) worksheet to a string grid.
    /// Handles shared strings, inline strings and cached formula values.</summary>
    public static class XlsxReader
    {
        public static string[][] ReadSheet(string path, string? sheetName = null)
        {
            if (string.IsNullOrWhiteSpace(sheetName)) sheetName = null;   // "" = first sheet
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            string ReadEntry(string name)
            {
                var e = zip.GetEntry(name) ?? throw new FileNotFoundException($"'{name}' missing in {path}");
                using var r = new StreamReader(e.Open());
                return r.ReadToEnd();
            }

            // workbook: sheet name → r:id
            var wbDoc = LoadXml(ReadEntry("xl/workbook.xml"));
            string? rid = null;
            foreach (XmlElement sh in wbDoc.GetElementsByTagName("sheet"))
            {
                if (sheetName == null || string.Equals(sh.GetAttribute("name"), sheetName, StringComparison.OrdinalIgnoreCase))
                {
                    rid = sh.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    if (string.IsNullOrEmpty(rid)) rid = sh.GetAttribute("r:id");
                    break;
                }
            }
            if (rid == null) throw new ArgumentException($"Sheet '{sheetName}' not found in {path}");

            // rels: r:id → target
            var relDoc = LoadXml(ReadEntry("xl/_rels/workbook.xml.rels"));
            string? target = null;
            foreach (XmlElement rel in relDoc.GetElementsByTagName("Relationship"))
                if (rel.GetAttribute("Id") == rid) { target = rel.GetAttribute("Target"); break; }
            if (target == null) throw new InvalidDataException("Worksheet relationship missing.");
            string sheetPath = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;

            // shared strings (optional)
            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") != null)
            {
                var ssDoc = LoadXml(ReadEntry("xl/sharedStrings.xml"));
                foreach (XmlElement si in ssDoc.DocumentElement!.GetElementsByTagName("si"))
                {
                    var sb = new StringBuilder();
                    foreach (XmlElement t in si.GetElementsByTagName("t")) sb.Append(t.InnerText);
                    shared.Add(sb.ToString());
                }
            }

            var doc = LoadXml(ReadEntry(sheetPath));
            var rows = new List<(int r, List<(int c, string v)> cells)>();
            int maxCol = 0;
            int lastRow = 0;
            foreach (XmlElement row in doc.GetElementsByTagName("row"))
            {
                // Some writers omit the r attributes; fall back to document order
                // instead of shredding everything into row 1 / column 1.
                string rAttr = row.GetAttribute("r");
                int rIdx = int.TryParse(rAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ri)
                    ? ri : lastRow + 1;
                lastRow = rIdx;
                var cells = new List<(int, string)>();
                int lastCol = 0;
                foreach (XmlElement c in row.GetElementsByTagName("c"))
                {
                    string cRef = c.GetAttribute("r");
                    int col = cRef.Length > 0 ? ColIndex(cRef) : lastCol + 1;
                    lastCol = col;
                    string t = c.GetAttribute("t");
                    string val = "";
                    if (t == "inlineStr")
                    {
                        var isEl = c.GetElementsByTagName("is");
                        if (isEl.Count > 0) val = isEl[0]!.InnerText;
                    }
                    else
                    {
                        var v = c.GetElementsByTagName("v");
                        if (v.Count > 0)
                        {
                            val = v[0]!.InnerText;
                            if (t == "s" && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                                         out int si) && si < shared.Count) val = shared[si];
                            else if (t == "b") val = val == "1" ? "TRUE" : "FALSE";
                        }
                    }
                    cells.Add((col, val));
                    maxCol = Math.Max(maxCol, col);
                }
                rows.Add((rIdx, cells));
            }

            int maxRow = rows.Count == 0 ? 0 : rows.Max(r => r.r);
            var grid = new string[maxRow][];
            for (int i = 0; i < maxRow; i++)
                grid[i] = Enumerable.Repeat("", maxCol).ToArray();
            foreach (var (r, cells) in rows)
                foreach (var (c, v) in cells)
                    grid[r - 1][c - 1] = v;
            return grid;
        }

        static XmlDocument LoadXml(string xml)
        {
            // XLSX worksheet XML never needs external entities or a DTD. Parsing through
            // an XmlReader with DtdProcessing.Prohibit and no XmlResolver closes the
            // XXE/SSRF vector a crafted .xlsx could otherwise open (CA3075) - unlike
            // XmlDocument.LoadXml(string), which has no settings overload for this.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument();
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                doc.Load(reader);
            return doc;
        }

        /// <summary>"BC12" → 55 (1-based column).</summary>
        internal static int ColIndex(string cellRef)
        {
            int col = 0;
            foreach (char ch in cellRef)
            {
                if (char.IsDigit(ch)) break;
                col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return Math.Max(col, 1);
        }
    }
}
