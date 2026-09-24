using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Doe/XlsxWriter.cs (XlsxWorkbook/XlsxSheet). The write half of the
/// round trip already has indirect coverage: DoeTests.TableIoTests.Xlsx_WriteRead_
/// RoundTrip exercises headers, escaped text, positive/negative decimals and
/// styled cells through XlsxReader; BugfixRegressionTests.XlsxReader_
/// MissingCellRefs_FallBackToDocumentOrder exercises a stripped-ref sheet; DoeTests.
/// ColRef_Parsing covers XlsxWorkbook.ColName/XlsxReader.ColIndex. This file adds
/// only what those miss: sheet-name sanitisation, that cells land in the sheet XML
/// in document order regardless of Set() call order, that written numbers stay
/// invariant under a German ambient culture, the NaN/Infinity fallback to an
/// inline-string cell, that a null value never creates a cell, and structural
/// checks on the merge/column-width/multi-sheet zip parts that no reader exercises
/// because Lugworm.Core.Doe.XlsxReader does not surface them.</summary>
public class XlsxWriterTests
{
    static string TempXlsx() => Path.Combine(Path.GetTempPath(), "iamlab_xlsxwriter_" + Guid.NewGuid().ToString("N") + ".xlsx");

    static string ReadZipEntry(string path, string entryName)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using var r = new StreamReader(entry.Open());
        return r.ReadToEnd();
    }

    static string FirstSheetXml(string path) => ReadZipEntry(path, "xl/worksheets/sheet1.xml");

    // ---- sheet-name sanitisation -----------------------------------------------

    [Theory]
    [InlineData("a:b\\c/d?e*f[g]h", "a_b_c_d_e_f_g_h")]
    [InlineData("Plain Name", "Plain Name")]
    public void AddSheet_SanitizesReservedCharacters(string input, string expected)
    {
        var wb = new XlsxWorkbook();
        var sh = wb.AddSheet(input);
        Assert.Equal(expected, sh.Name);
    }

    [Fact]
    public void AddSheet_TruncatesNamesLongerThan31Characters()
    {
        var wb = new XlsxWorkbook();
        var sh = wb.AddSheet(new string('A', 40));
        Assert.Equal(31, sh.Name.Length);
        Assert.Equal(new string('A', 31), sh.Name);
    }

    [Fact]
    public void AddSheet_EmptyName_FallsBackToDefault()
    {
        var wb = new XlsxWorkbook();
        var sh = wb.AddSheet("");
        Assert.Equal("Sheet", sh.Name);
    }

    // ---- document order ---------------------------------------------------------

    [Fact]
    public void Save_CellsAppearInRowThenColumnOrderRegardlessOfSetOrder()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            // deliberately out of document order
            sh.Set(2, 3, "r2c3");
            sh.Set(1, 1, "r1c1");
            sh.Set(2, 1, "r2c1");
            sh.Set(1, 2, "r1c2");
            wb.Save(path);

            string xml = FirstSheetXml(path);
            int iR1C1 = xml.IndexOf("r=\"A1\"", StringComparison.Ordinal);
            int iR1C2 = xml.IndexOf("r=\"B1\"", StringComparison.Ordinal);
            int iR2C1 = xml.IndexOf("r=\"A2\"", StringComparison.Ordinal);
            int iR2C3 = xml.IndexOf("r=\"C2\"", StringComparison.Ordinal);

            Assert.True(iR1C1 >= 0 && iR1C2 >= 0 && iR2C1 >= 0 && iR2C3 >= 0);
            Assert.True(iR1C1 < iR1C2, "A1 should come before B1 (same row, ascending column)");
            Assert.True(iR1C2 < iR2C1, "row 1 cells should come entirely before row 2 cells");
            Assert.True(iR2C1 < iR2C3, "A2 should come before C2 (same row, ascending column)");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- culture invariance -------------------------------------------------

    [Fact]
    public void Save_NumbersStayDotDecimalUnderGermanAmbientCulture()
    {
        string path = TempXlsx();
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, -7.25);
            sh.Set(1, 2, 12345.5);
            wb.Save(path);

            string xml = FirstSheetXml(path);
            Assert.Contains("<v>-7.25</v>", xml);
            Assert.Contains("<v>12345.5</v>", xml);
            Assert.DoesNotContain("-7,25", xml);
            Assert.DoesNotContain("12345,5", xml);

            var grid = XlsxReader.ReadSheet(path);
            Assert.Equal(-7.25, TableIo.ParseNum(grid[0][0])!.Value, 9);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
            try { File.Delete(path); } catch { }
        }
    }

    // D4 (4.4.0): styles.xml (font size, merge/numFmt/cellXfs counts) is machine
    // XML, not UI - it must be byte-identical under a German ambient culture. The
    // numeric-cell test above already covers <v> values; this one covers the
    // structural parts that only a custom fractional font size and a merge touch.
    [Fact]
    public void Save_StylesAndMerges_AreByteIdenticalUnderGermanAmbientCulture()
    {
        string invPath = TempXlsx();
        string dePath = TempXlsx();
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            void Build(string path)
            {
                var wb = new XlsxWorkbook();
                var sh = wb.AddSheet("data");
                sh.Set(1, 1, "title", new XFmt { Bold = true, Size = 10.5, FontColor = "1F3864" });
                sh.Set(1, 2, 42.5, XFmt.Num("0.00"));
                sh.Merge(1, 1, 1, 2);
                wb.Save(path);
            }

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Build(invPath);
            string invSheet = FirstSheetXml(invPath);
            string invStyles = ReadZipEntry(invPath, "xl/styles.xml");

            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Build(dePath);
            string deSheet = FirstSheetXml(dePath);
            string deStyles = ReadZipEntry(dePath, "xl/styles.xml");

            Assert.Equal(invSheet, deSheet);
            Assert.Equal(invStyles, deStyles);
            Assert.Contains("sz val=\"10.5\"", invStyles);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
            try { File.Delete(invPath); } catch { }
            try { File.Delete(dePath); } catch { }
        }
    }

    // ---- non-finite numeric cells -------------------------------------------

    [Fact]
    public void Save_NaNOrInfinityCell_FallsBackToAnInlineStringInsteadOfANumericValue()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, double.NaN);
            sh.Set(1, 2, double.PositiveInfinity);
            wb.Save(path);

            string xml = FirstSheetXml(path);
            // Cells whose value fails the finite-double check fall through to the
            // inline-string branch (v.ToString()), not <v>NaN</v> as a number.
            int a1 = xml.IndexOf("r=\"A1\"", StringComparison.Ordinal);
            int b1 = xml.IndexOf("r=\"B1\"", StringComparison.Ordinal);
            Assert.True(a1 >= 0 && b1 >= 0);
            int a1End = xml.IndexOf("</c>", a1, StringComparison.Ordinal);
            int b1End = xml.IndexOf("</c>", b1, StringComparison.Ordinal);
            string a1Cell = xml.Substring(a1, a1End - a1);
            string b1Cell = xml.Substring(b1, b1End - b1);

            Assert.Contains("t=\"inlineStr\"", a1Cell);
            Assert.Contains("NaN", a1Cell);
            Assert.Contains("t=\"inlineStr\"", b1Cell);
            // .NET's double.ToString() renders PositiveInfinity as the "∞" glyph,
            // not the word "Infinity" - whatever that text is, it must not have been
            // written as a bare numeric <v> (which Excel could not parse back).
            Assert.Contains(double.PositiveInfinity.ToString(CultureInfo.CurrentCulture), b1Cell);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- null values never create a cell -------------------------------------

    [Fact]
    public void Set_NullValue_DoesNotWriteACell()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, null);   // must not create a <row r="1"> at all
            sh.Set(2, 2, "kept");
            wb.Save(path);

            string xml = FirstSheetXml(path);
            Assert.DoesNotContain("r=\"1\"", xml);   // no row-1 element emitted
            Assert.DoesNotContain("r=\"A1\"", xml);

            var grid = XlsxReader.ReadSheet(path);
            Assert.Equal("kept", grid[1][1]);   // row 2 (index 1), col B (index 1)
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- merges and column widths: structural (no reader surfaces these) ----

    [Fact]
    public void Save_MergedCells_EmitsAMergeCellsRef()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, "title");
            sh.Merge(1, 1, 2, 3);
            wb.Save(path);

            string xml = FirstSheetXml(path);
            Assert.Contains("<mergeCells count=\"1\">", xml);
            Assert.Contains("<mergeCell ref=\"A1:C2\"/>", xml);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Save_ColumnWidths_EmitColsElement()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, "x");
            sh.ColWidths(10, 22.5);
            wb.Save(path);

            string xml = FirstSheetXml(path);
            Assert.Contains("<col min=\"1\" max=\"1\" width=\"10\" customWidth=\"1\"/>", xml);
            Assert.Contains("<col min=\"2\" max=\"2\" width=\"22.5\" customWidth=\"1\"/>", xml);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- multiple sheets: content-types / workbook / rels parts -------------

    [Fact]
    public void Save_MultipleSheets_AreListedInContentTypesWorkbookAndRels()
    {
        string path = TempXlsx();
        try
        {
            var wb = new XlsxWorkbook();
            wb.AddSheet("One").Set(1, 1, "a");
            wb.AddSheet("Two").Set(1, 1, "b");
            wb.Save(path);

            string contentTypes = ReadZipEntry(path, "[Content_Types].xml");
            Assert.Contains("/xl/worksheets/sheet1.xml", contentTypes);
            Assert.Contains("/xl/worksheets/sheet2.xml", contentTypes);

            string workbook = ReadZipEntry(path, "xl/workbook.xml");
            Assert.Contains("name=\"One\"", workbook);
            Assert.Contains("name=\"Two\"", workbook);

            string rels = ReadZipEntry(path, "xl/_rels/workbook.xml.rels");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(rels, "relationships/worksheet\"").Count);
            Assert.Contains("relationships/styles\"", rels);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
