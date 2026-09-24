using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Firmware motion limits: config.g baseline, job scan, and the comparison that
/// stops a job silently re-raising acceleration past what the machine was configured for.
///
/// Units are the recurring hazard here. Over the G-code interface `M203` and `M566` are
/// mm/min while `M201`/`M204` are mm/s², so a missed divide-by-60 is a 60x false alarm on
/// every axis. Each command gets its own conversion test for that reason.</summary>
public class DuetMotionLimitTests
{
    static IEnumerable<string> L(params string[] lines) => lines;

    static FirmwareMotionLimits Baseline(params string[] lines)
        => FirmwareMotionLimits.ParseConfig(lines);

    // ---- unit conversion: one test per command ----------------------------

    [Fact]
    public void M203_IsMmPerMinute_ConvertedToMmPerSecond()
    {
        var c = Baseline("M203 X3600 Y3600 Z1500 E75");

        Assert.Equal(60.0, c.X.MaxSpeed!.Value, 6);      // 3600 mm/min
        Assert.Equal(60.0, c.Y.MaxSpeed!.Value, 6);
        Assert.Equal(25.0, c.Z.MaxSpeed!.Value, 6);
        Assert.Equal(1.25, c.E.MaxSpeed!.Value, 6);
    }

    /// <summary>RepRapFirmware, GCodes2.cpp case 203: "Units are mm/sec if S1 is given,
    /// else mm/min". Dividing unconditionally read `M203 S1 X100` as 1.67 mm/s - a 60x
    /// understatement that let a job raising the speed ceiling past the configured limit
    /// pass the comparison without a warning.</summary>
    [Fact]
    public void M203_WithS1_IsMmPerSecond_NotDivided()
    {
        var c = Baseline("M203 S1 X100 Y100 Z25 E1.25");

        Assert.Equal(100.0, c.X.MaxSpeed!.Value, 6);
        Assert.Equal(100.0, c.Y.MaxSpeed!.Value, 6);
        Assert.Equal(25.0, c.Z.MaxSpeed!.Value, 6);
        Assert.Equal(1.25, c.E.MaxSpeed!.Value, 6);
    }

    /// <summary>Only S1 selects mm/sec; S0 - and any other S value - keeps mm/min, which
    /// mirrors the firmware's own `gb.Seen('S') &amp;&amp; gb.GetIValue() == 1` test.</summary>
    [Fact]
    public void M203_WithS0_StaysMmPerMinute()
    {
        var c = Baseline("M203 S0 X3600");
        Assert.Equal(60.0, c.X.MaxSpeed!.Value, 6);
    }

    /// <summary>The job scanner must normalise identically to the config scanner, or the
    /// two sides of the comparison are in different units.</summary>
    [Fact]
    public void ScanJob_M203WithS1_IsMmPerSecond()
    {
        var demand = JobMotionDemand.ScanJob(L("G90", "M203 S1 X100", "G1 X10 Y0 F600"));
        Assert.Equal(100.0, demand.X.MaxSpeed!.Value, 6);
    }

    [Fact]
    public void M566_IsMmPerMinute_ConvertedToMmPerSecond()
    {
        var c = Baseline("M566 X800 Y800 Z600 E60");

        Assert.Equal(800.0 / 60.0, c.X.Jerk!.Value, 6);
        Assert.Equal(10.0, c.Z.Jerk!.Value, 6);          // 600 mm/min
        Assert.Equal(1.0, c.E.Jerk!.Value, 6);           // 60 mm/min
    }

    [Fact]
    public void M201_IsAlreadyMmPerSecondSquared_AndNotDividedBy60()
    {
        var c = Baseline("M201 X800 Y800 Z500 E100");

        Assert.Equal(800.0, c.X.Acceleration!.Value, 6);
        Assert.Equal(500.0, c.Z.Acceleration!.Value, 6);
        Assert.Equal(100.0, c.E.Acceleration!.Value, 6);
    }

    [Fact]
    public void M204_IsAlreadyMmPerSecondSquared()
    {
        var c = Baseline("M204 P500 T900");

        Assert.Equal(500.0, c.PrintAccel!.Value, 6);
        Assert.Equal(900.0, c.TravelAccel!.Value, 6);
    }

    // ---- the real machine file --------------------------------------------

    [Fact]
    public void ParseConfig_ReadsTheRealPotterbotConfig()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "potterbot-config.g");
        Assert.True(File.Exists(path), "fixture missing: " + path);

        var c = FirmwareMotionLimits.ParseConfig(File.ReadAllLines(path));

        // M203 X3600 Y3600 Z1500 E75  ->  mm/s
        Assert.Equal(60.0, c.X.MaxSpeed!.Value, 6);
        Assert.Equal(60.0, c.Y.MaxSpeed!.Value, 6);
        Assert.Equal(25.0, c.Z.MaxSpeed!.Value, 6);
        Assert.Equal(1.25, c.E.MaxSpeed!.Value, 6);
        // M201 X800 Y800 Z500 E100  ->  mm/s^2, unconverted
        Assert.Equal(800.0, c.X.Acceleration!.Value, 6);
        Assert.Equal(500.0, c.Z.Acceleration!.Value, 6);
        // M566 X800 Y800 Z600 E60  ->  mm/s
        Assert.Equal(800.0 / 60.0, c.X.Jerk!.Value, 6);
        Assert.Equal(10.0, c.Z.Jerk!.Value, 6);
        Assert.True(c.HasAnyLimit);
    }

    [Fact]
    public void ParseConfig_IgnoresCommentsAndAcceptsCompactWords()
    {
        var c = Baseline(
            "; M203 X9000 Y9000   <- vendor original, commented out",
            "M203X3600Y3600");

        Assert.Equal(60.0, c.X.MaxSpeed!.Value, 6);
        Assert.Equal(60.0, c.Y.MaxSpeed!.Value, 6);
    }

    [Fact]
    public void ParseConfig_LastDirectiveWins_AsTheFirmwareExecutesTheFile()
    {
        var c = Baseline(
            "M201 X3000",
            "M201 X800");

        Assert.Equal(800.0, c.X.Acceleration!.Value, 6);
    }

    [Fact]
    public void ParseConfig_AbsentLimitsAreNullNotZero()
    {
        var c = Baseline("G90", "M550 P\"Scara\"");

        Assert.Null(c.X.MaxSpeed);
        Assert.Null(c.X.Acceleration);
        Assert.Null(c.X.Jerk);
        Assert.False(c.HasAnyLimit);
    }

    // ---- job scan ---------------------------------------------------------

    [Fact]
    public void ScanJob_TakesThePeak_NotTheHeaderValue()
    {
        // a job may raise a limit mid-file; the peak is what the machine ever sees
        var job = JobMotionDemand.ScanJob(L(
            "M201 X800",
            "G1 X10 F1200",
            "M201 X3000",
            "G1 X20 F1200"));

        Assert.Equal(3000.0, job.X.Acceleration!.Value, 6);
    }

    [Fact]
    public void ScanJob_CapturesJobIssuedM203()
    {
        // raising M203 removes the firmware's own clipping - the one motion command
        // that turns a would-be-clipped move into real overspeed
        var job = JobMotionDemand.ScanJob(L("M203 X9000"));

        Assert.Equal(150.0, job.X.MaxSpeed!.Value, 6);
    }

    [Fact]
    public void ScanJob_M205IsMmPerSecond_WhileM566IsMmPerMinute()
    {
        var viaM566 = JobMotionDemand.ScanJob(L("M566 X600"));
        var viaM205 = JobMotionDemand.ScanJob(L("M205 X10"));

        Assert.Equal(10.0, viaM566.X.Jerk!.Value, 6);   // 600 mm/min
        Assert.Equal(10.0, viaM205.X.Jerk!.Value, 6);   // already mm/s
    }

    [Fact]
    public void ScanJob_M204S_SetsBothPrintAndTravel()
    {
        var job = JobMotionDemand.ScanJob(L("M204 S750"));

        Assert.Equal(750.0, job.PrintAccel!.Value, 6);
        Assert.Equal(750.0, job.TravelAccel!.Value, 6);
    }

    [Fact]
    public void ScanJob_IgnoresUnmodelledCommandsInsteadOfThrowing()
    {
        // deliberately unlike GcodeParser, which fails closed: a motion scan must
        // tolerate any file it is handed, including one it cannot fully model
        var exception = Record.Exception(() => JobMotionDemand.ScanJob(L(
            "M9999 Q1 ; nonsense",
            "T0",
            "M201 X900")));

        Assert.Null(exception);
        Assert.Equal(900.0, JobMotionDemand.ScanJob(L("M9999 Q1", "M201 X900")).X.Acceleration!.Value, 6);
    }

    // ---- per-axis speed decomposition -------------------------------------

    [Fact]
    public void LinearMove_PureXY_DoesNotRegisterAgainstZorE()
    {
        // F is the vector feedrate; charging it to Z would false-alarm on every travel
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X0 Y0", "G1 X100 Y0 F3600"));

        Assert.Equal(60.0, job.X.EffectiveSpeed!.Value, 6);
        Assert.Null(job.Z.EffectiveSpeed);
        Assert.Null(job.E.EffectiveSpeed);
    }

    [Fact]
    public void LinearMove_DiagonalSplitsFeedByAxisShare()
    {
        // 3-4-5: dx=30, dz=40, |d|=50 at F3000 (50 mm/s) -> X 30, Z 40
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X0 Z0", "G1 X30 Z40 F3000"));

        Assert.Equal(30.0, job.X.EffectiveSpeed!.Value, 6);
        Assert.Equal(40.0, job.Z.EffectiveSpeed!.Value, 6);
    }

    [Fact]
    public void LinearMove_RelativeModeMatchesAbsolute()
    {
        var abs = JobMotionDemand.ScanJob(L("G90", "G1 X0 Y0", "G1 X30 Y40 F3000"));
        var rel = JobMotionDemand.ScanJob(L("G90", "G1 X0 Y0", "G91", "G1 X30 Y40 F3000"));

        Assert.Equal(abs.X.EffectiveSpeed!.Value, rel.X.EffectiveSpeed!.Value, 6);
        Assert.Equal(abs.Y.EffectiveSpeed!.Value, rel.Y.EffectiveSpeed!.Value, 6);
    }

    [Fact]
    public void ZeroLengthMove_DoesNotDivideByZero()
    {
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y10", "G1 X10 Y10 F3000"));

        foreach (string axis in new[] { "X", "Y", "Z", "E" })
        {
            double? v = job.Axis(axis).EffectiveSpeed;
            Assert.True(!v.HasValue || (!double.IsNaN(v.Value) && !double.IsInfinity(v.Value)),
                        axis + " produced " + v);
        }
    }

    [Fact]
    public void ExtruderOnlyMove_ChargesTheFeedToE()
    {
        // a retraction has no XYZ length, so F applies to E
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X0 Y0", "G1 E-1 F600"));

        Assert.Equal(10.0, job.E.EffectiveSpeed!.Value, 6);
    }

    // ---- arcs -------------------------------------------------------------

    [Fact]
    public void QuarterArc_ReachesFullFeedOnTheAxisItsTangentSweepsThrough()
    {
        // start at (10,0) about origin, CCW quarter to (0,10): the tangent passes
        // through -Y then -X..., so at least one axis must see the full feed
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y0", "G3 X0 Y10 I-10 J0 F3000"));

        double peak = Math.Max(job.X.EffectiveSpeed ?? 0, job.Y.EffectiveSpeed ?? 0);
        Assert.Equal(50.0, peak, 3);
    }

    [Fact]
    public void FullCircle_RegistersRealSpeedDespiteZeroEndpointDelta()
    {
        // the endpoint delta is zero, so a linear formula would score this as no motion
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y0", "G2 X10 Y0 I-10 J0 F3000"));

        Assert.True((job.X.EffectiveSpeed ?? 0) > 1.0, "X speed was " + job.X.EffectiveSpeed);
        Assert.True((job.Y.EffectiveSpeed ?? 0) > 1.0, "Y speed was " + job.Y.EffectiveSpeed);
    }

    [Fact]
    public void PlanarArc_IsNotScaledDown()
    {
        // pathLength3D == planarArcLength, so the helical factor must degenerate to 1
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y0", "G2 X10 Y0 I-10 J0 F3000"));

        double peak = Math.Max(job.X.EffectiveSpeed ?? 0, job.Y.EffectiveSpeed ?? 0);
        Assert.Equal(50.0, peak, 3);
    }

    [Fact]
    public void Helix_ScalesXYandZByThe3DPathLength()
    {
        // full circle r=10 (planar length 20*pi ~ 62.83) climbing dz -> XY must be
        // scaled by planarArc/path3D, not left at the planar value
        var planar = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y0 Z0", "G2 X10 Y0 Z0 I-10 J0 F3000"));
        var helix = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y0 Z0", "G2 X10 Y0 Z60 I-10 J0 F3000"));

        double planarPeak = Math.Max(planar.X.EffectiveSpeed ?? 0, planar.Y.EffectiveSpeed ?? 0);
        double helixPeak = Math.Max(helix.X.EffectiveSpeed ?? 0, helix.Y.EffectiveSpeed ?? 0);

        Assert.True(helixPeak < planarPeak,
            $"helical XY ({helixPeak}) must be scaled below the planar case ({planarPeak})");
        Assert.True((helix.Z.EffectiveSpeed ?? 0) > 0, "helical Z must register");
    }

    // ---- comparison -------------------------------------------------------

    [Fact]
    public void JobAccelAboveBaseline_Warns()
    {
        var baseline = Baseline("M201 X800 Y800");
        var job = JobMotionDemand.ScanJob(L("M201 X3000"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.Contains(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void JobAccelAtOrBelowBaseline_IsSilent()
    {
        var baseline = Baseline("M201 X800 Y800");
        var job = JobMotionDemand.ScanJob(L("M201 X400"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void JobRaisingM203_Warns_BecauseItRemovesTheFirmwaresClipping()
    {
        var baseline = Baseline("M203 X3600");
        var job = JobMotionDemand.ScanJob(L("M203 X9000"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.Contains(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void FeedAboveAnIntactM203_IsInfoNotWarning()
    {
        // the board clips this: the print runs slower than estimated, but all axes
        // scale together so extrusion per mm - and therefore geometry - is unaffected
        var baseline = Baseline("M203 X3600 Y3600");
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X0 Y0", "G1 X500 Y0 F9000"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.Contains(findings, f => f.Severity == Severity.Info);
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void CompareMotion_NeverEmitsError()
    {
        // the gate refuses on Error; this check is advisory by design
        var baseline = Baseline("M203 X3600", "M201 X800", "M566 X800");
        var job = JobMotionDemand.ScanJob(L("M203 X9000", "M201 X3000", "M566 X3000",
                                            "G90", "G1 X0 Y0", "G1 X500 Y0 F9000"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
    }

    [Fact]
    public void NullBaselineValues_AreSkippedNotTreatedAsZero()
    {
        var baseline = Baseline("G90");                       // nothing configured
        var job = JobMotionDemand.ScanJob(L("M201 X3000"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    // ---- live in-force state ----------------------------------------------

    [Fact]
    public void InheritedRaisedState_WarnsEvenWhenTheJobSetsNoLimits()
    {
        // an earlier job left M201 raised and nothing restores it; a later job that
        // mentions no limits at all would still run at the raised value
        var baseline = Baseline("M201 X800");
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y10 F1200"));
        var live = new LiveMotionState();
        live.X.Acceleration = 3000.0;

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, live);

        Assert.Contains(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void LiveStateMatchingBaseline_IsSilent()
    {
        var baseline = Baseline("M201 X800");
        var job = JobMotionDemand.ScanJob(L("G90", "G1 X10 Y10 F1200"));
        var live = new LiveMotionState();
        live.X.Acceleration = 800.0;

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, live);

        Assert.DoesNotContain(findings, f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void NullLiveState_DoesNotThrow_AndDoesNotRefuse()
    {
        var baseline = Baseline("M201 X800");
        var job = JobMotionDemand.ScanJob(L("M201 X400"));

        var findings = FirmwareLimitCheck.CompareMotion(baseline, job, null);

        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
    }

    // ---- reply parsing ----------------------------------------------------

    [Fact]
    public void ParseReply_ReadsTheRealRrfWording()
    {
        var parsed = FirmwareMotionLimits.ParseReply(
            "Max speeds (mm/min): X3600.0, Y3600.0, Z1500.0, E75.0",
            MotionReplyKind.MaxSpeed);

        Assert.Equal(60.0, parsed.X.MaxSpeed!.Value, 6);
        Assert.Equal(25.0, parsed.Z.MaxSpeed!.Value, 6);
    }

    [Fact]
    public void ParseReply_UnrecognisedText_YieldsNoValuesRatherThanZeros()
    {
        var parsed = FirmwareMotionLimits.ParseReply("ok", MotionReplyKind.MaxSpeed);

        Assert.Null(parsed.X.MaxSpeed);
        Assert.False(parsed.HasAnyLimit);
    }

    // ---- the parser change that makes the M203 path reachable -------------

    [Fact]
    public void GcodeParser_AcceptsM203_SoTheWarningPathIsReachable()
    {
        // before 1.11.0 the fail-closed allowlist omitted M203, so a job containing it
        // died at robot validation with a generic "unmodelled command" error and never
        // reached the motion check at all
        var exception = Record.Exception(() => GcodeParser.Parse(new[]
        {
            "G28",
            "M203 X9000",
            "G1 X10 Y-10 Z5 F1200",
        }, null));

        Assert.Null(exception);
    }
}
