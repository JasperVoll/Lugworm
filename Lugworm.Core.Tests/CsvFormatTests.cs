using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lugworm.Core.Doe;
using Lugworm.Core.Extrusion;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>File-level English/German CSV detection (release-readiness spec §5.2).
/// One consistent convention per file is accepted; mixed or ambiguous files are
/// refused with an actionable CsvFormatException instead of silently mis-parsed.</summary>
public class CsvFormatTests
{
    static string Temp(params string[] lines)
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        File.WriteAllLines(p, lines);
        return p;
    }

    static void WithFile(string[] lines, Action<DataTableLite> check)
    {
        var p = Temp(lines);
        try { check(TableIo.ReadCsv(p)); }
        finally { try { File.Delete(p); } catch { } }
    }

    static void ExpectRefusal(params string[] lines)
    {
        var p = Temp(lines);
        try { Assert.Throws<CsvFormatException>(() => TableIo.ReadCsv(p)); }
        finally { try { File.Delete(p); } catch { } }
    }

    [Fact]
    public void EnglishCommaDelimited_Parses()
        => WithFile(new[] { "a,b", "1.5,2", "0.25,3" }, t =>
        {
            Assert.Equal(CsvNumberStyle.English, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 0.25 }, t.NumericColumn(0));
            Assert.Equal(new[] { 2.0, 3.0 }, t.NumericColumn(1));
        });

    [Fact]
    public void EnglishSemicolonDelimited_Parses()
        => WithFile(new[] { "a;b", "1.5;2", "0.25;3" }, t =>
        {
            Assert.Equal(CsvNumberStyle.English, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 0.25 }, t.NumericColumn(0));
        });

    [Fact]
    public void GermanSemicolonDelimited_Parses()
        => WithFile(new[] { "a;b", "1,5;2", "0,25;3" }, t =>
        {
            Assert.Equal(CsvNumberStyle.German, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 0.25 }, t.NumericColumn(0));
            Assert.Equal(new[] { 2.0, 3.0 }, t.NumericColumn(1));
        });

    [Fact]
    public void GermanTabDelimited_Parses()
        => WithFile(new[] { "a\tb", "1,5\t2", "0,25\t3" }, t =>
        {
            Assert.Equal(CsvNumberStyle.German, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 0.25 }, t.NumericColumn(0));
        });

    [Fact]
    public void NeutralIntegersCommaDelimited_Parse()
        => WithFile(new[] { "a,b", "1,2", "3,4" }, t =>
        {
            Assert.Equal(CsvNumberStyle.Neutral, t.NumberStyle);
            Assert.Equal(new[] { 1.0, 3.0 }, t.NumericColumn(0));
            Assert.Equal(new[] { 2.0, 4.0 }, t.NumericColumn(1));
        });

    [Fact]
    public void QuotedGermanDecimalInCommaFile_Parses()
        => WithFile(new[] { "a,b", "\"1,5\",2", "\"0,25\",3" }, t =>
        {
            Assert.Equal(CsvNumberStyle.German, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 0.25 }, t.NumericColumn(0));
        });

    [Fact]
    public void MixedDecimalConventions_Refused()
        => ExpectRefusal("a;b", "1,5;2", "0.25;3");

    [Fact]
    public void ThousandsSeparators_Refused()
        => ExpectRefusal("a;b", "1.234,5;2", "3,5;4");

    [Fact]
    public void GermanDecimalsSplitByCommaDelimiter_Refused()
        // the classic trap: German numbers exported with a comma delimiter -
        // "1,5,2,5" must never be read as four integer columns
        => ExpectRefusal("setting,flow", "1,5,2,5", "3,0,4,5");

    [Fact]
    public void InconsistentColumnCounts_Refused()
        => ExpectRefusal("a;b", "1;2", "1;2;3");

    [Theory]
    [InlineData("a;b", "\"1.5;2")]
    [InlineData("a;b", "1.5\";2")]
    [InlineData("a;b", "\"1.5\"oops;2")]
    [InlineData("a;b", "abc,\"quoted\";2")]
    public void MalformedQuoting_Refused(string header, string row)
        => ExpectRefusal(header, row);

    [Fact]
    public void ExponentOverflow_RefusedInsteadOfBecomingInfinity()
        => ExpectRefusal("a;b", "1e999;2");

    [Fact]
    public void ParseCell_ExponentOverflow_IsNotANumber()
        => Assert.Null(CsvFormat.ParseCell("1e999", CsvNumberStyle.English));

    [Fact]
    public void BomEmptyCellAndTrailingDelimiter_Parse()
        => WithFile(new[] { "\uFEFFa;b;", "1,5;;", "2,5;3;" }, t =>
        {
            Assert.Equal(new[] { "a", "b", "" }, t.Headers);
            Assert.Equal(CsvNumberStyle.German, t.NumberStyle);
            Assert.Equal(1.5, t.NumericColumn(0)[0], 9);
            Assert.True(double.IsNaN(t.NumericColumn(1)[0]));
        });

    [Fact]
    public void AmbiguousSingleColumnCommaNumber_Refused()
        => ExpectRefusal("value", "1,234");

    [Fact]
    public void RoundTrip_WriteCsvThenRead()
    {
        var p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".csv");
        try
        {
            TableIo.WriteCsv(p, new[] { "x", "y" }, new[] { new[] { "1.5", "hello" }, new[] { "2.25", "wo,rld" } });
            var t = TableIo.ReadCsv(p);
            Assert.Equal(CsvNumberStyle.English, t.NumberStyle);
            Assert.Equal(new[] { 1.5, 2.25 }, t.NumericColumn(0));
            Assert.Equal("wo,rld", t.Rows[1][1]);
        }
        finally { try { File.Delete(p); } catch { } }
    }

    // ---- pump calibration CSV ----

    [Fact]
    public void PumpCalibration_GermanSemicolon_Parses()
    {
        var cal = PumpCalibration.ParseCsv(new[] { "# meta", "setting;flow_lpm", "2,0;1,5", "4,0;2,5" });
        Assert.Equal(2, cal.Points.Count);
        Assert.Equal(2.0, cal.Points[0].Setting, 9);
        Assert.Equal(1.5, cal.Points[0].FlowLpm, 9);
        Assert.Equal(2.5, cal.Points[1].FlowLpm, 9);
    }

    [Fact]
    public void PumpCalibration_EnglishComma_Parses()
    {
        var cal = PumpCalibration.ParseCsv(new[] { "setting,flow_lpm", "2.0,1.5", "4.0,2.5" });
        Assert.Equal(2, cal.Points.Count);
        Assert.Equal(1.5, cal.Points[0].FlowLpm, 9);
    }

    [Fact]
    public void PumpCalibration_MixedConventions_Refused()
        => Assert.Throws<CsvFormatException>(
            () => PumpCalibration.ParseCsv(new[] { "2,0;1,5", "4.0;2.5" }));

    [Fact]
    public void PumpCalibration_GermanCommaDelimited_Refused()
        => Assert.Throws<CsvFormatException>(
            () => PumpCalibration.ParseCsv(new[] { "2,0,1,5", "4,0,2,5" }));

    [Fact]
    public void PumpCalibration_ExponentOverflow_Refused()
        => Assert.Throws<CsvFormatException>(
            () => PumpCalibration.ParseCsv(new[] { "setting;flow_lpm", "2;1e999" }));

    [Fact]
    public void PumpCalibration_PlaceholderStillDetected()
    {
        var cal = PumpCalibration.ParseCsv(new[] { "# placeholder", "1,NEEDS_INPUT", "2,NEEDS_INPUT" });
        Assert.True(cal.IsPlaceholder);
        Assert.Empty(cal.Points);
    }

    [Fact]
    public void RefusalNamesTheLine()
    {
        var p = Temp("a;b", "1,5;2", "0.25;3");
        try
        {
            var ex = Assert.Throws<CsvFormatException>(() => TableIo.ReadCsv(p));
            Assert.True(ex.Line > 0);
            Assert.Contains("English", ex.Message);
        }
        finally { try { File.Delete(p); } catch { } }
    }
}
