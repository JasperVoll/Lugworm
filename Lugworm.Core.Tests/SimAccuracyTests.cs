using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Simulation-accuracy tests from the 2026-07-14 run (P4): arc-swept
/// SCARA checks, trapezoidal time model, volume agreement between the builder
/// and PrintStats.</summary>
public class SimAccuracyTests
{
    // ---- arc-swept SCARA checks ------------------------------------------

    [Fact]
    public void ArcInterior_LeavingReach_IsFlagged()
    {
        // Semicircle from (900,0) to (-900,0) about the origin bulges to r=900 at
        // the top - fine. But an arc from (900,0) to (900,0.1) the LONG way around
        // with center at origin stays at r=900... instead: arc whose interior
        // exceeds reach: from (700,-500) to (700,500), center (700,0) → r=500,
        // interior bulges to x=1200 > 1008.01 max reach.
        var lines = new[]
        {
            "G90", "M83", "G28",
            "G1 X700 Y-500 Z50 F600",
            "G3 X700 Y500 I0 J500 E1",   // CCW semicircle via (1200,0) - outside reach
        };
        var prog = GcodeParser.Parse(lines);
        var lim = new RobotLimits();
        var res = RobotChecks.Run(prog, lim, GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.Contains(res.Problems, p => p.Message.Contains("Target unreachable"));
        Assert.False(res.ReachOk);
    }

    [Fact]
    public void ArcInterior_WithinLimits_NotFlagged()
    {
        var lines = new[]
        {
            "G90", "M83", "G28",
            "G1 X600 Y-100 Z50 F600",
            "G3 X600 Y100 I0 J100 E1",   // small arc bulging to x=700 - inside reach
        };
        var prog = GcodeParser.Parse(lines);
        var res = RobotChecks.Run(prog, new RobotLimits(), GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("arc interior"));
    }

    [Fact]
    public void ArcSampling_ChordTolZero_DisablesInteriorChecks()
    {
        var lines = new[]
        {
            "G90", "M83",
            "G1 X700 Y-500 Z50 F600",
            "G3 X700 Y500 I0 J500 E1",
        };
        var prog = GcodeParser.Parse(lines);
        var lim = new RobotLimits { ArcChordTol = 0 };
        var res = RobotChecks.Run(prog, lim, GcodeFlavor.DuetRrf, arcSupport: true);
        Assert.DoesNotContain(res.Problems, p => p.Message.Contains("arc interior"));
    }

    [Fact]
    public void SampleArc_RespectsChordTolerance()
    {
        // quarter circle r=100: max sagitta between consecutive samples <= tol
        var m = new ParsedMove(0, 100, 0, 600, true, true, -100, 0, g: 3, e: 1);
        var samples = RobotChecks.SampleArc(100, 0, 0, m, 0.5);
        // quarter circle r=100, tol 0.5mm: step = 2×acos(1−tol/r) ≈ 0.2 rad → 7 interior samples
        Assert.InRange(samples.Count, 5, 12);
        var all = new List<Vec3> { new(100, 0, 0) };
        all.AddRange(samples);
        all.Add(new Vec3(0, 100, 0));
        for (int i = 0; i + 1 < all.Count; i++)
        {
            var mid = new Vec3(0.5 * (all[i].X + all[i + 1].X), 0.5 * (all[i].Y + all[i + 1].Y), 0);
            double rMid = Math.Sqrt(mid.X * mid.X + mid.Y * mid.Y);
            Assert.True(100.0 - rMid <= 0.5 + 1e-6, $"sagitta {100 - rMid} > tol at {i}");
            // and every sample sits on the circle
            double rs = Math.Sqrt(all[i].X * all[i].X + all[i].Y * all[i].Y);
            Assert.Equal(100.0, rs, 6);
        }
    }

    // ---- trapezoidal time model ---------------------------------------------

    [Fact]
    public void TrapezoidTime_StraightLine_MatchesAnalytic()
    {
        // 0→v→0 over 100mm at v=20mm/s, a=100mm/s²: dAcc=dDec=2mm,
        // t = 2×(20/100) + 96/20 = 0.4 + 4.8 = 5.2 s
        var pts = new List<Vec3> { new(0, 0, 0), new(100, 0, 0) };
        var ext = new List<bool> { false, true };
        double t = PrintStatsCalc.TrapezoidTimeS(pts, ext, null, 20.0, 40.0, 100.0);
        Assert.Equal(5.2, t, 6);
    }

    [Fact]
    public void TrapezoidTime_CollinearSegments_NoSlowdownAtJoint()
    {
        var one = new List<Vec3> { new(0, 0, 0), new(100, 0, 0) };
        var two = new List<Vec3> { new(0, 0, 0), new(50, 0, 0), new(100, 0, 0) };
        var extOne = new List<bool> { false, true };
        var extTwo = new List<bool> { false, true, true };
        double tOne = PrintStatsCalc.TrapezoidTimeS(one, extOne, null, 20.0, 40.0, 100.0);
        double tTwo = PrintStatsCalc.TrapezoidTimeS(two, extTwo, null, 20.0, 40.0, 100.0);
        Assert.Equal(tOne, tTwo, 6);   // splitting a straight line must not add time
    }

    [Fact]
    public void TrapezoidTime_RightAngle_SlowerThanStraight()
    {
        var straight = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(200, 0, 0) };
        var corner = new List<Vec3> { new(0, 0, 0), new(100, 0, 0), new(100, 100, 0) };
        var ext = new List<bool> { false, true, true };
        double ts = PrintStatsCalc.TrapezoidTimeS(straight, ext, null, 20.0, 40.0, 100.0);
        double tc = PrintStatsCalc.TrapezoidTimeS(corner, ext, null, 20.0, 40.0, 100.0);
        Assert.True(tc > ts + 1e-9, $"corner {tc} not slower than straight {ts}");
    }

    [Fact]
    public void TrapezoidTime_AlwaysAboveKinematicLowerBound()
    {
        var pts = new List<Vec3>();
        for (int i = 0; i <= 20; i++)
            pts.Add(new Vec3(50 * Math.Cos(i * 0.3), 50 * Math.Sin(i * 0.3), i * 0.1));
        var ext = pts.Select(_ => true).ToList();
        var r = PrintStatsCalc.FromPoints(pts, ext, 4, 2, 2.0, 25.0, 60.0, null, 500.0);
        Assert.True(r.TimeAccelMin >= r.TimeMin - 1e-9);
        Assert.True(r.TimeAccelMin > 0);
    }

    // ---- volume agreement: builder vs PrintStats (incl. varh) ----------------

    static List<List<List<Vec3>>> TwoSquareLayers(double h)
    {
        List<Vec3> Sq(double z) => new()
        { new(0, 0, z), new(50, 0, z), new(50, 50, z), new(0, 50, z), new(0, 0, z) };
        return new List<List<List<Vec3>>> { new() { Sq(h) }, new() { Sq(2 * h) } };
    }

    static IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> Wrap(List<List<List<Vec3>>> t)
        => t.Select(b => (IReadOnlyList<IReadOnlyList<Vec3>>)b
            .Select(c => (IReadOnlyList<Vec3>)c).ToList()).ToList();

    [Fact]
    public void Volume_BuilderVsPrintStats_Agree()
    {
        var cfg = new GcodeConfig { LayerHeight = 2.0, Width = 4.0, RamDiameter = 94.0 };
        var res = GcodeBuilder.Build(Wrap(TwoSquareLayers(2.0)), cfg);
        var prog = GcodeParser.Parse(res.Lines);
        var stats = PrintStatsCalc.FromMoves(prog.Moves, cfg.Width, cfg.LayerHeight,
                                             ramDiameterMm: cfg.RamDiameter);
        Assert.True(stats.VolumeFromE);
        // same ram model on both sides - within E-word rounding (3 decimals per move)
        Assert.Equal(res.Volume, stats.VolumeCm3, 1);
    }

    [Fact]
    public void Volume_VariableHeights_ScaleTheAgreement()
    {
        var cfg = new GcodeConfig { LayerHeight = 2.0, Width = 4.0, RamDiameter = 94.0 };
        var branches = Wrap(TwoSquareLayers(2.0));

        // heightTree with layer 2 at half height → less volume than nominal
        var heights = branches.Select((b, bi) => (IReadOnlyList<IReadOnlyList<double>>)b
            .Select(c => (IReadOnlyList<double>)c.Select(_ => bi == 0 ? 2.0 : 1.0).ToList())
            .ToList()).ToList();

        var nominal = GcodeBuilder.Build(branches, cfg);
        var varh = GcodeBuilder.Build(branches, cfg, heightTree: heights);
        Assert.True(varh.Volume < nominal.Volume - 1e-6);

        var progV = GcodeParser.Parse(varh.Lines);
        var statsV = PrintStatsCalc.FromMoves(progV.Moves, cfg.Width, cfg.LayerHeight,
                                              ramDiameterMm: cfg.RamDiameter);
        Assert.Equal(varh.Volume, statsV.VolumeCm3, 1);   // E-based stats track varh exactly
    }
}
