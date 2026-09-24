using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class GcodeRestartTests
{
    /// <summary>Small 3-layer print: header, approach, prime, seam-connected layers,
    /// one mid-print modal change (M566), footer.</summary>
    static List<string> Sample() => new()
    {
        "; clay print header",
        "G90",
        "M83",
        "M201 X3000 Y3000",
        "G0 X0.000 Y0.000 Z1.000 F3600",
        "G1 F600 E1.00000",
        "; layer 1/3",
        "G1 X10.000 Y0.000 Z1.000 F1200 E0.50000",
        "G1 X10.000 Y10.000 Z1.000 F1200 E0.50000",
        "M566 X450 Y450",
        "; layer 2/3",
        "G1 X10.000 Y10.000 Z2.000 F3600",
        "G1 X0.000 Y10.000 Z2.000 F1200 E0.50000",
        "; layer 3/3",
        "G1 X0.000 Y10.000 Z3.000 F3600",
        "G1 X0.000 Y0.000 Z3.000 F1200 E0.50000",
        "M84",
    };

    [Fact]
    public void ZMode_CutsAtFirstExtrudingMoveAtZ_KeepsHeaderAndTail()
    {
        var plan = GcodeRestart.Plan(Sample(), RestartMode.ZHeight, 3.0, 0, default);
        // resume at the start of the first extruding Z3 move
        Assert.Equal(0.0, plan.Resume.X, 6);
        Assert.Equal(10.0, plan.Resume.Y, 6);
        Assert.Equal(3.0, plan.Resume.Z, 6);
        // original header preserved once
        Assert.Contains("G90", plan.Lines);
        Assert.Contains("M201 X3000 Y3000", plan.Lines);
        Assert.Single(plan.Lines.Where(l => l == "M201 X3000 Y3000"));
        // mid-print modal change replayed exactly once (its original line is skipped)
        Assert.Single(plan.Lines.Where(l => l == "M566 X450 Y450"));
        // safe re-approach: maxZ before cut is 3 -> +10 clearance
        Assert.Contains("G0 Z13.000 F3600", plan.Lines);
        Assert.Contains("G0 X0.000 Y10.000 Z13.000 F3600", plan.Lines);
        Assert.Contains("G0 X0.000 Y10.000 Z3.000 F3600", plan.Lines);
        // modal feed restored from the last F before the cut
        Assert.Contains("G1 F3600", plan.Lines);
        // tail kept, skipped moves gone
        Assert.Contains("G1 X0.000 Y0.000 Z3.000 F1200 E0.50000", plan.Lines);
        Assert.Contains("M84", plan.Lines);
        Assert.DoesNotContain("G1 X10.000 Y0.000 Z1.000 F1200 E0.50000", plan.Lines);
        Assert.Equal("layer 3/3", plan.LayerAtCut);
    }

    [Fact]
    public void ZMode_Prime_EmitsRelativePrimeMove()
    {
        var opt = new RestartOptions { PrimeE = 2.0, RetractSpeed = 2.0 };
        var plan = GcodeRestart.Plan(Sample(), RestartMode.ZHeight, 3.0, 0, default, opt);
        Assert.Contains("G1 E2.00000 F120", plan.Lines);
    }

    [Fact]
    public void PointMode_ResumesAfterNearestPrintedPoint()
    {
        var plan = GcodeRestart.Plan(Sample(), RestartMode.Point, 0, 0, new Vec3(10, 0, 1));
        // nearest extruded endpoint is (10,0,1); the selected move has no
        // remaining geometry and is therefore omitted from the retained tail
        Assert.Equal(10.0, plan.Resume.X, 6);
        Assert.Equal(0.0, plan.Resume.Y, 6);
        Assert.Equal(1.0, plan.Resume.Z, 6);
        Assert.Contains("G1 X10.000 Y10.000 Z1.000 F1200 E0.50000", plan.Lines); // tail
        Assert.DoesNotContain("G1 X10.000 Y0.000 Z1.000 F1200 E0.50000", plan.Lines); // skipped
    }

    [Fact]
    public void PercentMode_CutsInsideTheFile()
    {
        var source = string.Join("\n", Sample()) + "\n";
        var plan = GcodeRestart.Plan(Sample(), RestartMode.Percent, 0, 50.0, default,
            new RestartOptions
            {
                OriginalBytes = Encoding.UTF8.GetBytes(source),
                ExactByteProvenance = true,
            });
        Assert.Equal(8, plan.CutLine); // 50% byte target falls at line 8's end
        Assert.False(plan.ApproximateByteProgress);
        Assert.Equal(144.0 / 370.0 * 100.0, plan.PercentAtCut, 9);
        Assert.Contains(plan.Lines, l => l.Contains("LUGWORM RESTART"));
        Assert.Contains("M84", plan.Lines);
    }

    [Fact]
    public void PercentMode_OutOfRange_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => GcodeRestart.Plan(Sample(), RestartMode.Percent, 0, 0.0, default));
        Assert.Throws<InvalidOperationException>(() => GcodeRestart.Plan(Sample(), RestartMode.Percent, 0, 100.0, default));
    }

    [Fact]
    public void ZMode_AboveTopOfPrint_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => GcodeRestart.Plan(Sample(), RestartMode.ZHeight, 99.0, 0, default));
    }

    [Fact]
    public void AbsoluteE_SyncsCounterWithG92()
    {
        var lines = new List<string>
        {
            "G90",
            "M82",
            "G0 X0.000 Y0.000 Z1.000 F3600",
            "G1 X10.000 Y0.000 Z1.000 F1200 E1.00000",
            "G1 X10.000 Y10.000 Z1.000 F1200 E2.00000",
            "G1 X0.000 Y10.000 Z1.000 F1200 E3.00000",
        };
        var plan = GcodeRestart.Plan(lines, RestartMode.Point, 0, 0, new Vec3(10, 10, 1));
        // cut before the E3 move: absolute counter at the cut is 2.0
        Assert.Contains("G92 E2.00000", plan.Lines);
        Assert.Contains("G1 X0.000 Y10.000 Z1.000 F1200 E3.00000", plan.Lines);
    }

    [Fact]
    public void Previews_SplitAtTheCut()
    {
        var plan = GcodeRestart.Plan(Sample(), RestartMode.ZHeight, 3.0, 0, default);
        Assert.NotEmpty(plan.SkippedPolys);
        Assert.NotEmpty(plan.RemainPolys);
        // everything in the skipped preview lies below/at the cut Z; the remaining
        // preview starts at the resume point
        Assert.All(plan.SkippedPolys.SelectMany(p => p), v => Assert.True(v.Z <= 3.0 + 1e-9));
        var first = plan.RemainPolys[0][0];
        Assert.Equal(plan.Resume.X, first.X, 6);
        Assert.Equal(plan.Resume.Y, first.Y, 6);
    }

    [Fact]
    public void BuilderOutput_RoundTrips_ThroughRestart()
    {
        // real builder output: two far islands -> LightRetraction hop travel inside
        var sq = new List<Vec3> { new(0, 0, 1), new(10, 0, 1), new(10, 10, 1), new(0, 10, 1), new(0, 0, 1) };
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = new GcodeConfig { LayerHeight = 1.0, Width = 2.0, RamDiameter = 10.0, PrintSpeed = 20.0, PressureOffset = 0.5 };
        var built = GcodeBuilder.Build(new[] { new List<IReadOnlyList<Vec3>> { sq }, new List<IReadOnlyList<Vec3>> { sq2 } }, cfg);
        var all = new List<string> { "G90", "M83" };
        all.AddRange(built.Lines);
        var plan = GcodeRestart.Plan(all, RestartMode.ZHeight, 2.0, 0, default);
        Assert.Equal(2.0, plan.Resume.Z, 6);
        Assert.Contains(plan.Lines, l => l.Contains("LUGWORM RESTART"));
        // the restart file still contains the second layer's extrusion
        Assert.Contains(plan.Lines, l => l.StartsWith("G1 X") && l.Contains("Z2.000") && l.Contains(" E"));
    }
}
