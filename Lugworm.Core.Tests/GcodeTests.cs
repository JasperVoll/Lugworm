using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class GcodeTests
{
    static List<Vec3> Square() => new()
    { new(0,0,0), new(10,0,0), new(10,10,0), new(0,10,0), new(0,0,0) };

    static GcodeConfig BaseCfg() => new()
    {
        LayerHeight = 1.0,
        Width = 2.0,
        RamDiameter = 10.0,
        PressureCoeff = 1.0,
        PrintSpeed = 20.0
    };

    static List<List<Vec3>> AsBranch(List<Vec3> contour) => new() { contour };

    [Fact]
    public void IdentityCoordinateFrame_EmitsInputCoordinatesUnchanged()
    {
        var contour = new List<Vec3> { new(10, 20, 0), new(30, 40, 0) };
        var cfg = BaseCfg();
        cfg.CoordinateFrame = GcodeCoordinateFrame.Identity;

        var result = GcodeBuilder.Build(new[] { AsBranch(contour) }, cfg);

        Assert.Contains(result.Lines, line => line.Contains("X30.000 Y40.000"));
        Assert.DoesNotContain(result.Lines, line => line.Contains("X-40.000 Y-30.000"));
    }

    [Fact]
    public void LegacyScaraCoordinateFrame_RemainsTheDefault()
    {
        var contour = new List<Vec3> { new(10, 20, 0), new(30, 40, 0) };

        var result = GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg());

        Assert.Contains(result.Lines, line => line.Contains("X-40.000 Y-30.000"));
    }

    static int ParseFeed(string line)
    {
        var tok = line.Split(' ').First(t => t.StartsWith("F"));
        return int.Parse(tok.Substring(1));
    }

    /// <summary>The "X… Y…" token pair a Rhino-frame position is emitted as. The
    /// builder maps into the machine's own frame on the way out (MachineFrame), so a
    /// test that wants to pin a design-frame position asks for its emitted form here
    /// instead of hard-coding the reflection. Positions that are symmetric under the
    /// map - the origin - read the same either way.</summary>
    static string Xy(double rhinoX, double rhinoY)
    {
        MachineFrame.ToMachine(rhinoX, rhinoY, out double mx, out double my);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"X{mx.ToString("0.000", inv)} Y{my.ToString("0.000", inv)}";
    }

    [Fact]
    public void SingleBranch_IsContinuous_NoRetract()
    {
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()) }, BaseCfg());
        Assert.False(r.Retract);
        Assert.Equal(40.0, r.Length, 6);
        int extrudeMoves = r.Lines.Count(l => l.StartsWith("G1 X") && l.Contains(" E"));
        Assert.True(extrudeMoves >= 4);
    }

    /// <summary>Second square displaced 50mm - a far seam (&gt; SeamTol).</summary>
    static List<Vec3> FarSquare(double dz = 0.0)
        => Square().Select(p => new Vec3(p.X + 50, p.Y, p.Z + dz)).ToList();

    [Fact]
    public void LightRetraction_FarSeam_EmitsRetractMove()
    {
        var cfg = BaseCfg(); cfg.PressureOffset = 1.0; cfg.ZHop = 1.0;
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()), AsBranch(FarSquare()) }, cfg);
        Assert.True(r.Retract);
        Assert.Contains(r.Lines, l => l.Contains("E-1.00000"));
    }

    [Fact]
    public void LightRetraction_AlignedSeam_ConnectsWithoutRetract()
    {
        // stacked layers: the seam is vertically aligned -> plain connect, no retract
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg(); cfg.PressureOffset = 1.0;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        Assert.False(r.Retract);
        // one prime (start) + one retract (final) only
        Assert.Single(r.Lines.Where(l => l.Contains("E-1.00000")));
        Assert.Single(r.Lines.Where(l => l.StartsWith("G1 F") && l.Contains("E1.00000")));
        // layers joined by a plain travel move (Z=1, no E)
        Assert.Contains(r.Lines, l => l.StartsWith("G1 X") && l.Contains("Z1.000") && !l.Contains(" E"));
    }

    [Fact]
    public void FarSeam_TravelClearsPrintedMaterial_PathAware()
    {
        // a tall contour printed first: the travel corridor passes its cells, so the
        // safe hop must lift above Z5 + clearance (ZHop 1) = Z6 - not just start.Z + hop
        var high = Square().Select(p => new Vec3(p.X, p.Y, 5.0)).ToList();
        var far = FarSquare(dz: 1.0);
        var cfg = BaseCfg(); cfg.ZHop = 1.0;
        var r = GcodeBuilder.Build(new[] { AsBranch(high), AsBranch(far) }, cfg);
        Assert.Contains(r.Lines, l => l.StartsWith("G0 Z6.000"));                       // lift
        Assert.Contains(r.Lines, l => l.StartsWith("G0 " + Xy(50, 0)) && l.Contains("Z6.000"));   // travel at safe Z
        Assert.Contains(r.Lines, l => l.StartsWith("G0 " + Xy(50, 0)) && l.Contains("Z1.000"));   // descend to start
    }

    [Fact]
    public void Arcs_EmitG2OrG3()
    {
        var circ = Enumerable.Range(0, 61)
            .Select(k =>
            {
                double a = k * 6.0 * Math.PI / 180.0;
                return new Vec3(20 * Math.Cos(a), 20 * Math.Sin(a), 0.0);
            })
            .ToList();
        var cfg = BaseCfg(); cfg.UseArcs = true; cfg.ArcTol = 0.05;
        var r = GcodeBuilder.Build(new[] { AsBranch(circ) }, cfg);
        Assert.Contains(r.Lines, l => l.StartsWith("G2") || l.StartsWith("G3"));
        Assert.True(r.Arcs);
    }

    [Fact]
    public void MinLayerTime_LowersFeed()
    {
        var cfg = BaseCfg(); cfg.MinLayerTime = 100.0;
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()) }, cfg);
        var feeds = r.Lines.Where(l => l.StartsWith("G1 X") && l.Contains("F")).Select(ParseFeed).ToList();
        Assert.NotEmpty(feeds);
        Assert.True(feeds.Min() < 20 * 60);
    }

    [Fact]
    public void Header_Marlin_EmitsM900AndM204()
    {
        var cfg = BaseCfg(); cfg.LinearAdvanceK = 0.8; cfg.AccelPrint = 200; cfg.Flavor = GcodeFlavor.Marlin;
        var h = Header.Make(cfg);
        Assert.Contains("M900 K0.800", h);
        Assert.Contains("M204 P200", h);
    }

    [Fact]
    public void Header_Default_NoM900()
    {
        var h = Header.Make(BaseCfg());
        Assert.DoesNotContain("M900", h);
    }

    [Fact]
    public void Footer_LiftsThenHomesBeforeSteppersOff()
    {
        var cfg = BaseCfg(); cfg.FooterZLift = 20.0; cfg.TravelSpeed = 60.0;
        var f = Header.Footer(cfg);
        // relative Z lift clears the print, then home, then steppers off - in order
        Assert.True(f.IndexOf("G91") < f.IndexOf("G0 Z20"));
        Assert.True(f.IndexOf("G0 Z20") < f.IndexOf("G90"));
        Assert.True(f.IndexOf("G90") < f.IndexOf("G28"));
        Assert.True(f.IndexOf("G28") < f.IndexOf("M84"));
    }

    [Fact]
    public void Footer_NoLift_WhenDisabled()
    {
        var cfg = BaseCfg(); cfg.FooterZLift = 0.0;
        var f = Header.Footer(cfg);
        Assert.DoesNotContain("G91", f);
        Assert.Contains("G28", f);   // still homes
    }

    [Fact]
    public void Header_Duet_UsesM572()
    {
        var cfg = BaseCfg(); cfg.LinearAdvanceK = 0.8; cfg.Flavor = GcodeFlavor.DuetRrf;
        var h = Header.Make(cfg);
        Assert.Contains("M572 D0 S0.800", h);
        Assert.DoesNotContain("M900", h);
    }

    // ---- print start location output (hang-the-extruder point) ----
    [Fact]
    public void StartPoint_IsFirstContourStart_WithZOffset()
    {
        var cfg = BaseCfg(); cfg.ZOffset = 2.5;
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()) }, cfg);
        Assert.True(r.StartPoint.HasValue);
        Assert.Equal(0.0, r.StartPoint!.Value.X, 6);   // START of the first E-bearing move - where material first hangs
        Assert.Equal(0.0, r.StartPoint!.Value.Y, 6);
        Assert.Equal(2.5, r.StartPoint!.Value.Z, 6);   // includes ZOffset
    }

    // ---- no-retraction mode: ram never pulls back, even across far seams ----
    [Fact]
    public void NoRetraction_FarSeam_NoRamPullback_ButHopsSafely()
    {
        var cfg = BaseCfg(); cfg.PressureOffset = 1.0;
        cfg.Retraction = RetractionMode.NoRetraction;
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()), AsBranch(FarSquare(1.0)) }, cfg);
        Assert.False(r.Retract);
        // exactly one prime (start) + one retract (end) - none between the branches
        Assert.Single(r.Lines.Where(l => l.Contains("E-1.00000")));
        Assert.Single(r.Lines.Where(l => l.StartsWith("G1 F") && l.Contains("E1.00000")));
        // the far seam still travels via the safe hop (no drag through the print)
        Assert.Contains(r.Lines, l => l.StartsWith("G0 " + Xy(50, 0)) && !l.Contains(" E"));
    }

    [Fact]
    public void NoRetraction_AlignedSeam_PlainConnect()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg(); cfg.Retraction = RetractionMode.NoRetraction;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        // stacked layers still join with a single plain move, no hop
        Assert.Contains(r.Lines, l => l.StartsWith("G1 X") && l.Contains("Z1.000") && !l.Contains(" E"));
    }

    // ---- normal-retraction mode: every seam retracts, even aligned/close ones ----
    [Fact]
    public void NormalRetraction_AlignedSeam_StillRetractsAndPrimes()
    {
        // stacked layers: LightRetraction would plain-connect this seam,
        // NormalRetraction must retract + safe-hop + prime instead
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg(); cfg.PressureOffset = 1.0;
        cfg.Retraction = RetractionMode.NormalRetraction;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        Assert.True(r.Retract);
        // seam retract + final retract; initial prime + post-seam prime
        Assert.Equal(2, r.Lines.Count(l => l.Contains("E-1.00000")));
        Assert.Equal(2, r.Lines.Count(l => l.StartsWith("G1 F") && l.Contains(" E1.00000")));
        // no plain (unretracted) connect up to the next layer start
        Assert.DoesNotContain(r.Lines, l => l.StartsWith("G1 X0.000") && l.Contains("Z1.000") && !l.Contains(" E"));
    }

    [Fact]
    public void DescendingSeam_TakesSafeHop_NotDiagonalPlunge()
    {
        // seam aligned in XY but dropping 4mm (non-planar): a plain diagonal connect
        // would plunge into the printed material below - must route via the safe hop
        var high = Square().Select(p => new Vec3(p.X, p.Y, 5.0)).ToList();
        var low = Square().Select(p => new Vec3(p.X, p.Y, 1.0)).ToList();
        var cfg = BaseCfg(); cfg.PressureOffset = 1.0; cfg.ZHop = 1.0;
        var r = GcodeBuilder.Build(new[] { AsBranch(high), AsBranch(low) }, cfg);
        Assert.True(r.Retract);
        // lift above the printed Z5 + clearance, then descend to the low start
        Assert.Contains(r.Lines, l => l.StartsWith("G0 Z6.000"));
        Assert.Contains(r.Lines, l => l.StartsWith("G0 X0.000") && l.Contains("Z1.000"));
        // no plain diagonal connect from Z5 to Z1
        Assert.DoesNotContain(r.Lines, l => l.StartsWith("G1 X0.000") && l.Contains("Z1.000") && !l.Contains(" E"));
    }

    // ---- single-path auto-detect: a single branch slows ONLY the first band ----
    static List<Vec3> RisingHelix() => new()
    {
        new(0,0,0.0), new(10,0,0.5), new(10,10,1.0), new(0,10,1.5),
        new(0,0,2.0), new(10,0,2.5), new(10,10,3.0)
    };

    [Fact]
    public void SingleBranchHelix_AutoDetected_SlowsFirstBandOnly()
    {
        // no flag: single-branch input is detected as one continuous path
        var cfg = BaseCfg(); cfg.FirstLayerSpeedFactor = 0.5;   // pF 1200, band 600
        var r = GcodeBuilder.Build(new[] { AsBranch(RisingHelix()) }, cfg);
        var feeds = r.Lines.Where(l => l.StartsWith("G1 X") && l.Contains(" E")).Select(ParseFeed).ToList();
        Assert.Contains(600, feeds);    // first layer-height band (Z <= LayerHeight)
        Assert.Contains(1200, feeds);   // full print speed above the band
    }

    [Fact]
    public void MultiBranch_WholeFirstLayerStillSlowed()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg(); cfg.FirstLayerSpeedFactor = 0.5;   // flPF 600, pF 1200
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        var feeds = r.Lines.Where(l => l.StartsWith("G1 X") && l.Contains(" E")).Select(ParseFeed).ToList();
        Assert.Equal(600, feeds.First());   // first layer slowed entirely
        Assert.Equal(1200, feeds.Last());   // second layer at full speed
    }

    // ---- per-axis accel/jerk (M201/M566) softened around retraction ----
    [Fact]
    public void Header_EmitsM201AndM566_WhenSet()
    {
        var cfg = BaseCfg();
        cfg.AccelXY = 3000; cfg.AccelZ = 2000; cfg.JerkXY = 900; cfg.JerkZ = 1200;
        cfg.Flavor = GcodeFlavor.DuetRrf;
        var h = Header.Make(cfg);
        Assert.Contains("M201 X3000 Y3000 Z2000", h);
        Assert.Contains("M566 X54000 Y54000 Z72000", h);
    }

    [Fact]
    public void Header_M201M566_IncludeEAxis_WhenSet()
    {
        var cfg = BaseCfg();
        cfg.AccelXY = 3000; cfg.AccelZ = 2000; cfg.AccelE = 500;
        cfg.JerkXY = 900; cfg.JerkZ = 1200; cfg.JerkE = 300;
        cfg.Flavor = GcodeFlavor.DuetRrf;
        var h = Header.Make(cfg);
        Assert.Contains("M201 X3000 Y3000 Z2000 E500", h);
        Assert.Contains("M566 X54000 Y54000 Z72000 E18000", h);
    }

    [Fact]
    public void RetractAccel_SoftensEAxis_ForRetractBurst()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg();
        cfg.PressureOffset = 0.2;
        cfg.AccelXY = 3000; cfg.AccelE = 500; cfg.JerkE = 300;
        cfg.RetractAccelFactor = 0.5; cfg.Flavor = GcodeFlavor.DuetRrf;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        Assert.Contains("M201 X1500 Y1500 Z1500 E250", r.Lines);   // E accel halved for retract
        Assert.Contains("M566 E9000", r.Lines);                    // 300 mm/s × 0.5 × 60
    }

    [Fact]
    public void Header_Marlin_UsesM205ForJerk()
    {
        var cfg = BaseCfg(); cfg.JerkXY = 900; cfg.Flavor = GcodeFlavor.Marlin;
        var h = Header.Make(cfg);
        Assert.Contains("M205 X900 Y900 Z900", h);
        Assert.DoesNotContain("M566", h);
    }

    [Fact]
    public void JerkDialectPair_RepresentsEqualPhysicalSpeedChange()
    {
        var rrf = BaseCfg(); rrf.JerkXY = 4.0; rrf.Flavor = GcodeFlavor.DuetRrf;
        var marlin = BaseCfg(); marlin.JerkXY = 4.0; marlin.Flavor = GcodeFlavor.Marlin;

        Assert.Contains("M566 X240 Y240 Z240", Header.Make(rrf));
        Assert.Contains("M205 X4 Y4 Z4", Header.Make(marlin));
        Assert.Equal(240.0 / 60.0, 4.0, 9);
    }

    [Fact]
    public void RetractAccel_SoftensBeforeTravel_RestoresBeforePrint()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg();
        cfg.PressureOffset = 1.0; cfg.ZHop = 1.0;
        cfg.AccelXY = 3000; cfg.AccelZ = 2000; cfg.JerkXY = 900; cfg.JerkZ = 1200;
        cfg.RetractAccelFactor = 0.5; cfg.Flavor = GcodeFlavor.DuetRrf;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        Assert.Contains("M201 X1500 Y1500 Z1000", r.Lines);   // softened for travel/retract
        Assert.Contains("M566 X27000 Y27000 Z36000", r.Lines); // softened jerk in mm/min
        Assert.Contains("M201 X3000 Y3000 Z2000", r.Lines);   // restored before extruding
    }

    // ---- hop moves run at the reduced hop feedrate, travel stays at travel speed ----
    [Fact]
    public void HopSpeed_SlowsVerticalHopMoves_Only()
    {
        var cfg = BaseCfg();
        cfg.TravelSpeed = 60.0;   // tF 3600
        cfg.ZHop = 1.0;
        cfg.HopSpeed = 5.0;       // hF 300
        // two islands at Z0: the far seam lifts, travels at the safe Z, descends
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()), AsBranch(FarSquare()) }, cfg);
        // seam lift (pure Z) at hop speed
        Assert.Contains(r.Lines, l => l.StartsWith("G0 Z") && l.EndsWith("F300"));
        // descend (down to the island Z) at hop speed
        Assert.Contains(r.Lines, l => l.StartsWith("G0 " + Xy(50, 0)) && l.Contains("Z0.000") && l.EndsWith("F300"));
        // horizontal travel at the safe height still runs at travel speed
        Assert.Contains(r.Lines, l => l.StartsWith("G0 " + Xy(50, 0)) && l.Contains("Z1.000") && l.EndsWith("F3600"));
    }

    [Fact]
    public void HopSpeed_SlowsPauseAfterFloorSafetyLift()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg();
        cfg.TravelSpeed = 60.0; cfg.HopSpeed = 5.0;   // hF 300
        cfg.FloorLayers = 1; cfg.PauseAfterFloor = true; cfg.PauseTime = 1000;
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        // the planner brackets the pause with a safe lift and a hop-speed return
        Assert.Contains(r.Lines, l => l.StartsWith("G0 Z") && l.Contains("Z100.000") && l.EndsWith("F300"));
        Assert.Contains(r.Lines, l => l.StartsWith("G0 X0.000") && l.Contains("Z0.000") && l.EndsWith("F300"));
    }

    [Fact]
    public void HopSpeed_Unset_FallsBackToTravelSpeed()
    {
        var cfg = BaseCfg(); cfg.TravelSpeed = 60.0; cfg.ZHop = 1.0;   // HopSpeed default 0
        var r = GcodeBuilder.Build(new[] { AsBranch(Square()), AsBranch(FarSquare()) }, cfg);
        Assert.Contains(r.Lines, l => l.StartsWith("G0 Z") && l.EndsWith("F3600"));   // lift at travel speed
    }

    [Fact]
    public void RetractAccel_FactorOne_EmitsNoInlineAccel()
    {
        var sq = Square();
        var sq2 = sq.Select(p => new Vec3(p.X, p.Y, p.Z + 1)).ToList();
        var cfg = BaseCfg();
        cfg.AccelXY = 3000; cfg.RetractAccelFactor = 1.0;   // nothing to soften
        var r = GcodeBuilder.Build(new[] { AsBranch(sq), AsBranch(sq2) }, cfg);
        Assert.DoesNotContain(r.Lines, l => l.StartsWith("M201"));   // header only, no inline
    }

    // ---- AUD-COR-002: dropped points must drop their same-index samples --------

    static double[] PrintMoveEs(GcodeResult r) => r.Lines
        .Where(l => l.StartsWith("G1 X") && l.Contains(" E"))
        .Select(l => double.Parse(
            l.Split(' ').First(t => t.StartsWith("E")).Substring(1),
            System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();

    [Fact]
    public void DuplicatePoint_PerSegmentFlows_StayAlignedToRetainedPoints()
    {
        // second/third points coincide; the duplicate's flow sample (100) must
        // leave with it instead of poisoning the contour mean
        var contour = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 0, 0), new(20, 0, 0) };
        var flows = new List<List<List<double>>> { new() { new() { 1.0, 2.0, 100.0, 4.0 } } };
        var r = GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg(), flowTree: flows);
        var es = PrintMoveEs(r);
        Assert.Equal(2, es.Length);
        // per-segment means: (1+2)/2 = 1.5 and (2+4)/2 = 3 over equal-length segments
        Assert.Equal(2.0, es[1] / es[0], 6);
        Assert.Contains(r.Warnings, w => w.Contains("duplicate"));
    }

    [Fact]
    public void DuplicatePoint_PerSegmentHeights_StayAlignedToRetainedPoints()
    {
        var contour = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 0, 0), new(20, 0, 0) };
        var heights = new List<List<List<double>>> { new() { new() { 0.5, 0.5, 5.0, 1.0 } } };
        var r = GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg(), heightTree: heights);
        var es = PrintMoveEs(r);
        Assert.Equal(2, es.Length);
        // per-segment heights: 0.5 and (0.5+1.0)/2 = 0.75 -> E ratio 1.5
        // (3 places: emitted E words carry 5 decimals, so the ratio is quantized)
        Assert.Equal(1.5, es[1] / es[0], 3);
    }

    [Fact]
    public void NonFinitePoint_SampleDroppedWithThePoint()
    {
        var contour = new List<Vec3> { new(0, 0, 0), new(double.NaN, 0, 0), new(10, 0, 0), new(20, 0, 0) };
        var flows = new List<List<List<double>>> { new() { new() { 1.0, 100.0, 2.0, 4.0 } } };
        var r = GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg(), flowTree: flows);
        var es = PrintMoveEs(r);
        Assert.Equal(2, es.Length);
        Assert.Equal(2.0, es[1] / es[0], 6);
        Assert.Contains(r.Warnings, w => w.Contains("non-finite"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidFlowSample_RefusedBeforeProgramEmission(double invalid)
    {
        var contour = new List<Vec3> { new(0, 0, 0), new(10, 0, 0) };
        var flows = new List<List<List<double>>> { new() { new() { 1.0, invalid } } };

        Assert.Throws<ArgumentException>(
            () => GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg(), flowTree: flows));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    public void InvalidHeightSample_RefusedBeforeProgramEmission(double invalid)
    {
        var contour = new List<Vec3> { new(0, 0, 0), new(10, 0, 0) };
        var heights = new List<List<List<double>>> { new() { new() { 1.0, invalid } } };

        Assert.Throws<ArgumentException>(
            () => GcodeBuilder.Build(new[] { AsBranch(contour) }, BaseCfg(), heightTree: heights));
    }
}
