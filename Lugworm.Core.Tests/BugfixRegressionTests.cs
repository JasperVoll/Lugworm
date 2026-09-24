using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Arcs;
using Lugworm.Core.Doe;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Regression tests for the verified bugs fixed in the 2026-07-14
/// refinement run (P1; the phase-0 audit of 2026-07-14 is archived outside the repository).</summary>
public class BugfixRegressionTests
{
    // ---- B1: ArcWelder CLI argument construction -------------------------

    [Fact]
    public void ArcWelder_PathTolerance_IsFractionInArgs()
    {
        // ArcWelderConsole rejects --path-tolerance-percent >= 1.0; 5% must be 0.05.
        string args = ArcWelderCli.BuildArgs("in.gcode", "out.gcode", 0.05, 5.0, 1.0e6, false);
        Assert.Contains("--path-tolerance-percent=0.05", args);
        Assert.DoesNotContain("--path-tolerance-percent=5", args);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    [InlineData(150.0)]
    public void ArcWelder_InvalidTolerance_Throws(double pct)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArcWelderCli.BuildArgs("a", "b", 0.05, pct, 1.0e6, false));
    }

    [Fact]
    public void ArcWelder_InvalidResolution_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArcWelderCli.BuildArgs("a", "b", 0.0, 5.0, 1.0e6, false));
    }

    // ---- B3: Levels guards ------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    public void LevelsPlanar_NonPositiveLayerHeight_ThrowsInsteadOfHanging(double h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Levels.Planar(0, 100, 0, h));
    }

    [Fact]
    public void LevelsAdaptive_ZeroHMin_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Levels.Adaptive(0, 100, 0, 0.0, 3.0, _ => 0.0));
    }

    [Fact]
    public void LevelsAdaptive_HMaxBelowHMin_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Levels.Adaptive(0, 100, 0, 2.0, 1.0, _ => 0.5));
    }

    // ---- B4: SCARA IK never reports Valid with NaN joints ------------------

    [Theory]
    [InlineData(0.0, 458.62)]
    [InlineData(549.39, 0.0)]
    [InlineData(-5.0, 458.62)]
    public void ScaraInverse_InvalidLinkLengths_InvalidNotNaN(double l1, double l2)
    {
        var ik = ScaraKinematics.Inverse(300, 100, 50, l1, l2, 0);
        Assert.False(ik.Valid);
        Assert.False(double.IsNaN(ik.J1));
        Assert.False(double.IsNaN(ik.J3));
        Assert.Contains("link length", ik.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScaraInverse_NaNTarget_Invalid()
    {
        var ik = ScaraKinematics.Inverse(double.NaN, 100, 50, 549.39, 458.62, 0);
        Assert.False(ik.Valid);
    }

    // ---- B5: G92 X/Y/Z changes logical coordinates, not physical position --

    [Fact]
    public void GcodeParser_G92Xyz_TracksLogicalOffsetAndPhysicalPosition()
    {
        var prog = GcodeParser.Parse(new[]
        {
            "G90",
            "G1 X100 Y50 Z10 F1200",
            "G92 X0 Y0",              // logical origin moved to (100,50)
            "G91",
            "G1 X10 E1",              // relative move from the new origin
        });
        Assert.Equal(2, prog.Moves.Count);
        var before = prog.Moves[0];
        Assert.Equal(100, before.X, 9);
        Assert.Equal(50, before.Y, 9);
        Assert.Equal(10, before.Z, 9);
        Assert.Equal(new Vec3(100, 50, 10), before.Coordinates.Physical);
        Assert.Equal(new Vec3(100, 50, 10), before.Coordinates.Logical);
        Assert.Equal(new Vec3(0, 0, 0), before.Coordinates.WorkspaceOffset);

        var last = prog.Moves[^1];
        Assert.Equal(110, last.X, 9);  // physical travel continues from (100,50,10)
        Assert.Equal(50, last.Y, 9);
        Assert.Equal(10, last.Z, 9);
        Assert.Equal(new Vec3(10, 0, 10), last.Coordinates.Logical);
        Assert.Equal(new Vec3(-100, -50, 0), last.Coordinates.WorkspaceOffset);
        Assert.Equal(new Vec3(110, 50, 10), last.Coordinates.Physical);
        Assert.Equal(last.Coordinates.Physical, prog.FinalCoordinates.Physical);
    }

    [Fact]
    public void GcodeParser_G92Bare_ResetsAllAxes()
    {
        var prog = GcodeParser.Parse(new[]
        {
            "G90",
            "G1 X100 Y50 Z10",
            "G92",
            "G91",
            "G1 X5 Y5 Z5 E1",
        });
        var last = prog.Moves[^1];
        Assert.Equal(105, last.X, 9);
        Assert.Equal(55, last.Y, 9);
        Assert.Equal(15, last.Z, 9);
        Assert.Equal(new Vec3(5, 5, 5), last.Coordinates.Logical);
        Assert.Equal(new Vec3(-100, -50, -10), last.Coordinates.WorkspaceOffset);
        Assert.Equal(new Vec3(105, 55, 15), last.Coordinates.Physical);
    }

    [Fact]
    public void GcodeParser_G92E_StillTracksExtrusionDelta()
    {
        var prog = GcodeParser.Parse(new[]
        {
            "M82",
            "G1 X10 E5",
            "G92 E0",
            "G1 X20 E2",   // delta must be 2, not -3
        });
        Assert.Equal(2, prog.Moves[1].E, 9);
        Assert.True(prog.Moves[1].Extrude);
        Assert.Equal(7, prog.Moves[1].Coordinates.PhysicalE, 9);
        Assert.Equal(-5, prog.Moves[1].Coordinates.EOffset, 9);
    }

    // ---- B6: Goal range parsing --------------------------------------------

    [Theory]
    [InlineData("range=6")]
    [InlineData("range=..8")]
    [InlineData("range=6..")]
    public void GoalParse_MalformedRange_ReadableError(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => Goal.Parse(text, "resp", 0, 10));
        Assert.Contains("low..high", ex.Message);
    }

    [Fact]
    public void GoalParse_NonNumeric_ReadableError()
    {
        var ex = Assert.Throws<ArgumentException>(() => Goal.Parse("target=abc", "resp", 0, 10));
        Assert.Contains("abc", ex.Message);
    }

    [Fact]
    public void GoalParse_ValidRange_StillWorks()
    {
        var g = Goal.Parse("target=7 in 6..8 w=2", "resp", 0, 10);
        Assert.Equal(GoalKind.Target, g.Kind);
        Assert.Equal(7, g.Target, 9);
        Assert.Equal(6, g.Low, 9);
        Assert.Equal(8, g.High, 9);
        Assert.Equal(2, g.Weight, 9);
    }

    // ---- B7: MiniJson / RsmModel.FromJson on corrupt input ------------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\": [1, 2")]
    [InlineData("{\"a\": \"unterminated")]
    [InlineData("{\"a\" 1}")]
    public void RsmModel_CorruptJson_ThrowsFormatException(string json)
    {
        Assert.Throws<FormatException>(() => RsmModel.FromJson(json));
    }

    [Fact]
    public void RsmModel_JsonRoundTrip_StillWorks()
    {
        // fit a trivial model and round-trip it, guarding the happy path
        var x = new[] { new[] { -1.0 }, new[] { 0.0 }, new[] { 1.0 }, new[] { 0.5 } };
        var y = new[] { 1.0, 2.0, 3.0, 2.5 };
        var model = RsmFitter.Fit("resp", new[] { "f" }, new[] { -1.0 }, new[] { 1.0 },
                                  null, x, y, RsmModelType.Linear);
        var back = RsmModel.FromJson(model.ToJson());
        Assert.Equal(model.Predict(new[] { 0.25 }), back.Predict(new[] { 0.25 }), 6);
    }

    // ---- B8: xlsx cells without r attributes --------------------------------

    [Fact]
    public void XlsxReader_MissingCellRefs_FallBackToDocumentOrder()
    {
        // Build a minimal sheet XML the way some writers emit it: no r attributes.
        // We test via the temp-file xlsx round trip: write a normal workbook, then
        // strip the r attributes from the sheet XML inside the zip.
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "iamlab_norefs_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var wb = new XlsxWorkbook();
            var sh = wb.AddSheet("data");
            sh.Set(1, 1, "a"); sh.Set(1, 2, "b"); sh.Set(1, 3, "c");
            sh.Set(2, 1, 1.0); sh.Set(2, 2, 2.0); sh.Set(2, 3, 3.0);
            wb.Save(path);

            // strip r="..." attributes from the sheet XML
            using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite))
            using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/"));
                string xml;
                using (var r = new System.IO.StreamReader(entry.Open())) xml = r.ReadToEnd();
                xml = System.Text.RegularExpressions.Regex.Replace(xml, " r=\"[A-Z0-9]+\"", "");
                entry.Delete();
                var ne = zip.CreateEntry(entry.FullName);
                using var w = new System.IO.StreamWriter(ne.Open());
                w.Write(xml);
            }

            var grid = XlsxReader.ReadSheet(path);
            Assert.Equal("a", grid[0][0]);
            Assert.Equal("b", grid[0][1]);
            Assert.Equal("c", grid[0][2]);
            Assert.Equal("3", grid[1][2]);
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }

    // ---- B9: flow/height tree shape validation -------------------------------

    static List<List<List<Vec3>>> OneSquareBranch() => new()
    {
        new() { new() { new(0,0,0), new(10,0,0), new(10,10,0), new(0,10,0), new(0,0,0) } }
    };

    [Fact]
    public void GcodeBuilder_MismatchedHeightTree_ReadableError()
    {
        var branches = OneSquareBranch();
        var heights = new List<List<List<double>>> { new() { new() { 1.0, 1.0 } } }; // 2 values for 5 pts
        var ex = Assert.Throws<ArgumentException>(
            () => GcodeBuilder.Build(Wrap(branches), new GcodeConfig(), heightTree: Wrap(heights)));
        Assert.Contains("heightTree", ex.Message);
    }

    [Fact]
    public void GcodeBuilder_MismatchedFlowBranches_ReadableError()
    {
        var branches = OneSquareBranch();
        var flows = new List<List<List<double>>>(); // zero branches
        var ex = Assert.Throws<ArgumentException>(
            () => GcodeBuilder.Build(Wrap(branches), new GcodeConfig(), flowTree: Wrap(flows)));
        Assert.Contains("flowTree", ex.Message);
    }

    static IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> Wrap(List<List<List<Vec3>>> t)
        => t.Select(b => (IReadOnlyList<IReadOnlyList<Vec3>>)b
            .Select(c => (IReadOnlyList<Vec3>)c).ToList()).ToList();

    static IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>> Wrap(List<List<List<double>>> t)
        => t.Select(b => (IReadOnlyList<IReadOnlyList<double>>)b
            .Select(c => (IReadOnlyList<double>)c).ToList()).ToList();
}
