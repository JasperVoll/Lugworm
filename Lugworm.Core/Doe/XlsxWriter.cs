using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Lugworm.Core.Doe
{
    /// <summary>Cell formatting: interned into the styles part on save.</summary>
    public struct XFmt
    {
        public bool Bold, Italic;
        public string? FontColor;   // "FFFFFF" (RGB, no alpha)
        public string? Fill;        // "1F3864"
        public string? NumFmt;      // "0.0000"
        public double Size;         // 0 = default 10
        public bool Border;

        public static readonly XFmt Default = new XFmt();
        public static readonly XFmt Title = new XFmt { Bold = true, Size = 12, FontColor = "1F3864" };
        public static readonly XFmt Header = new XFmt { Bold = true, FontColor = "FFFFFF", Fill = "1F3864", Border = true };
        public static readonly XFmt Sub = new XFmt { Bold = true, Fill = "D6E4F7", Border = true };
        public static readonly XFmt Note = new XFmt { Italic = true, Size = 9 };
        public static XFmt Num(string fmt, string? fill = null, bool bold = false)
            => new XFmt { NumFmt = fmt, Fill = fill, Bold = bold, Border = true };
        public static XFmt Text(string? fill = null, bool bold = false)
            => new XFmt { Fill = fill, Bold = bold, Border = true };
    }

    public sealed class XlsxSheet
    {
        public string Name;
        internal readonly Dictionary<long, (object val, XFmt fmt)> Cells = new Dictionary<long, (object, XFmt)>();
        internal readonly Dictionary<int, double> Widths = new Dictionary<int, double>();
        internal readonly List<(int r1, int c1, int r2, int c2)> Merges = new List<(int, int, int, int)>();

        internal XlsxSheet(string name) { Name = name; }

        /// <summary>Set a cell (1-based row/col). Value: string / double / int / null.</summary>
        public void Set(int r, int c, object? val, XFmt fmt = default)
        { if (val != null) Cells[Key(r, c)] = (val, fmt); }

        public void ColWidth(int c, double w) => Widths[c] = w;
        public void ColWidths(params double[] w) { for (int i = 0; i < w.Length; i++) Widths[i + 1] = w[i]; }
        public void Merge(int r1, int c1, int r2, int c2) => Merges.Add((r1, c1, r2, c2));

        internal static long Key(int r, int c) => (long)r << 20 | (uint)c;
    }

    /// <summary>Minimal self-contained .xlsx writer (SpreadsheetML + zip): no external
    /// dependencies, inline strings, interned styles. Enough for styled DoE plans and
    /// analysis reports; not a general Excel library.</summary>
    public sealed class XlsxWorkbook
    {
        readonly List<XlsxSheet> _sheets = new List<XlsxSheet>();

        public XlsxSheet AddSheet(string name)
        {
            var s = new XlsxSheet(SanitizeSheetName(name));
            _sheets.Add(s);
            return s;
        }

        static string SanitizeSheetName(string n)
        {
            foreach (var ch in new[] { ':', '\\', '/', '?', '*', '[', ']' }) n = n.Replace(ch, '_');
            return n.Length > 31 ? n.Substring(0, 31) : n.Length == 0 ? "Sheet" : n;
        }

        public void Save(string path)
        {
            // style interning
            var fonts = new List<string> { FontXml(new XFmt()) };
            var fills = new List<string> { "<fill><patternFill patternType=\"none\"/></fill>",
                                           "<fill><patternFill patternType=\"gray125\"/></fill>" };
            var numFmts = new List<(int id, string fmt)>();
            var xfs = new List<string> { "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>" };
            var xfIndex = new Dictionary<string, int>();

            int StyleId(XFmt f)
            {
                string fKey = FontXml(f);
                int fontId = fonts.IndexOf(fKey);
                if (fontId < 0) { fonts.Add(fKey); fontId = fonts.Count - 1; }
                int fillId = 0;
                if (f.Fill != null)
                {
                    string fill = $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{f.Fill}\"/></patternFill></fill>";
                    fillId = fills.IndexOf(fill);
                    if (fillId < 0) { fills.Add(fill); fillId = fills.Count - 1; }
                }
                int numId = 0;
                if (f.NumFmt != null)
                {
                    var hit = numFmts.FirstOrDefault(nf => nf.fmt == f.NumFmt);
                    if (hit.fmt == null) { numId = 164 + numFmts.Count; numFmts.Add((numId, f.NumFmt)); }
                    else numId = hit.id;
                }
                int borderId = f.Border ? 1 : 0;
                string key = $"{fontId}|{fillId}|{numId}|{borderId}";
                if (xfIndex.TryGetValue(key, out int id)) return id;
                xfs.Add($"<xf numFmtId=\"{numId}\" fontId=\"{fontId}\" fillId=\"{fillId}\" borderId=\"{borderId}\" " +
                        $"applyNumberFormat=\"{(numId > 0 ? 1 : 0)}\" applyFont=\"1\" applyFill=\"{(fillId > 0 ? 1 : 0)}\" applyBorder=\"{borderId}\"" +
                        "><alignment vertical=\"center\"/></xf>");
                xfIndex[key] = xfs.Count - 1;
                return xfs.Count - 1;
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            void Entry(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            // sheets first (styles interned on the way)
            var sheetXmls = new List<string>();
            foreach (var sh in _sheets)
            {
                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                if (sh.Widths.Count > 0)
                {
                    sb.Append("<cols>");
                    foreach (var kv in sh.Widths.OrderBy(k => k.Key))
                        sb.Append(FormattableString.Invariant($"<col min=\"{kv.Key}\" max=\"{kv.Key}\" width=\"{kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>"));
                    sb.Append("</cols>");
                }
                sb.Append("<sheetData>");
                foreach (var rowGroup in sh.Cells.GroupBy(kv => (int)(kv.Key >> 20)).OrderBy(g => g.Key))
                {
                    sb.Append(FormattableString.Invariant($"<row r=\"{rowGroup.Key}\">"));
                    foreach (var kv in rowGroup.OrderBy(kv => (int)(kv.Key & 0xFFFFF)))
                    {
                        int c = (int)(kv.Key & 0xFFFFF);
                        string cellRef = ColName(c) + rowGroup.Key;
                        int s = StyleId(kv.Value.fmt);
                        var v = kv.Value.val;
                        if (v is double dd && !double.IsNaN(dd) && !double.IsInfinity(dd))
                            sb.Append(FormattableString.Invariant($"<c r=\"{cellRef}\" s=\"{s}\"><v>{dd.ToString("R", CultureInfo.InvariantCulture)}</v></c>"));
                        else if (v is int ii)
                            sb.Append(FormattableString.Invariant($"<c r=\"{cellRef}\" s=\"{s}\"><v>{ii}</v></c>"));
                        else
                            sb.Append(FormattableString.Invariant($"<c r=\"{cellRef}\" s=\"{s}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(v.ToString() ?? "")}</t></is></c>"));
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData>");
                if (sh.Merges.Count > 0)
                {
                    sb.Append(FormattableString.Invariant($"<mergeCells count=\"{sh.Merges.Count}\">"));
                    foreach (var (r1, c1, r2, c2) in sh.Merges)
                        sb.Append(FormattableString.Invariant($"<mergeCell ref=\"{ColName(c1)}{r1}:{ColName(c2)}{r2}\"/>"));
                    sb.Append("</mergeCells>");
                }
                sb.Append("</worksheet>");
                sheetXmls.Add(sb.ToString());
            }

            Entry("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                string.Concat(Enumerable.Range(1, _sheets.Count).Select(i =>
                    $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>");

            Entry("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            Entry("xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(_sheets.Select((s, i) =>
                    $"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>");

            Entry("xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(_sheets.Select((s, i) =>
                    $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{_sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>");

            var styles = new StringBuilder();
            styles.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            styles.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (numFmts.Count > 0)
            {
                styles.Append(FormattableString.Invariant($"<numFmts count=\"{numFmts.Count}\">"));
                foreach (var (id, fmt) in numFmts)
                    styles.Append(FormattableString.Invariant($"<numFmt numFmtId=\"{id}\" formatCode=\"{Esc(fmt)}\"/>"));
                styles.Append("</numFmts>");
            }
            styles.Append(FormattableString.Invariant($"<fonts count=\"{fonts.Count}\">")).Append(string.Concat(fonts)).Append("</fonts>");
            styles.Append(FormattableString.Invariant($"<fills count=\"{fills.Count}\">")).Append(string.Concat(fills)).Append("</fills>");
            styles.Append("<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
                "<border><left style=\"thin\"/><right style=\"thin\"/><top style=\"thin\"/><bottom style=\"thin\"/><diagonal/></border></borders>");
            styles.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
            styles.Append(FormattableString.Invariant($"<cellXfs count=\"{xfs.Count}\">")).Append(string.Concat(xfs)).Append("</cellXfs>");
            styles.Append("</styleSheet>");
            Entry("xl/styles.xml", styles.ToString());

            for (int i = 0; i < sheetXmls.Count; i++)
                Entry($"xl/worksheets/sheet{i + 1}.xml", sheetXmls[i]);
        }

        static string FontXml(XFmt f)
        {
            double size = f.Size > 0 ? f.Size : 10;
            var sb = new StringBuilder("<font>");
            if (f.Bold) sb.Append("<b/>");
            if (f.Italic) sb.Append("<i/>");
            sb.Append(FormattableString.Invariant($"<sz val=\"{size.ToString("0.#", CultureInfo.InvariantCulture)}\"/>"));
            if (f.FontColor != null) sb.Append(FormattableString.Invariant($"<color rgb=\"FF{f.FontColor}\"/>"));
            sb.Append("<name val=\"Calibri\"/></font>");
            return sb.ToString();
        }

        internal static string ColName(int c)
        {
            var s = "";
            while (c > 0) { c--; s = (char)('A' + c % 26) + s; c /= 26; }
            return s;
        }

        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
