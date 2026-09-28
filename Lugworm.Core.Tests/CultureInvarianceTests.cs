using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Analysis;
using Lugworm.Core.Calibration;
using Lugworm.Core.Doe;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>AUD-CULT-001 / AUD-TEST-001: every machine/program serialization must be
/// byte-identical under a German ambient culture (decimal comma, dot thousands
/// separator) and under the invariant culture. Each case runs the same serialization
/// under both cultures and compares line-for-line. The print-log CSV counterpart
/// already lives in DuetPrintLogTests; bed-definition round-trips in
/// BedDefinitionTests.</summary>
public class CultureInvarianceTests
{
    static readonly CultureInfo DeDe = new("de-DE");

    static T RunUnder<T>(CultureInfo culture, Func<T> f)
    {
        var c0 = CultureInfo.CurrentCulture;
        try
        {
            // CurrentCulture only: the UI culture would swap in .NET Framework's localised
            // exception suffix ("Parametername: o"), which is BCL text, not the invariance under test
            CultureInfo.CurrentCulture = culture;
            return f();
        }
        finally
        {
            CultureInfo.CurrentCulture = c0;
        }
    }

    static void AssertSameUnderDeDe<T>(Func<T> serialize, Action<T, T> compare)
    {
        var invariant = RunUnder(CultureInfo.InvariantCulture, serialize);
        var german = RunUnder(DeDe, serialize);
        compare(invariant, german);
    }

    /// <summary>4.4.0 review follow-up to D4: the diagnostics that D7 moved into Core
    /// (adaptive layer heights, the conical clearance, the bricklayer shift, the
    /// non-planar amplitudes) format doubles, so they must read the same under de-DE.
    /// One planar run carries the adaptive/bricklayer/non-planar lines, one conical run
    /// with a zero nozzle clearance forces the collision line.</summary>
    /// <summary>Closed cylinder of radius r and height h: nv rings of nu vertices, a
    /// centre vertex on each cap. Local so this file still compiles inside
    /// tests/FrameworkTests, which links it without SlicePipelineTests.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) ClosedCylinder(double r = 20, double h = 30, int nu = 48, int nv = 13)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        for (int j = 0; j < nv; j++)
        {
            double z = h * j / (nv - 1);
            for (int i = 0; i < nu; i++)
            {
                double a = 2 * Math.PI * i / nu;
                V.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
            }
        }
        for (int j = 0; j < nv - 1; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = j * nu + i, b = j * nu + (i + 1) % nu, c = a + nu, d = b + nu;
                F.Add((a, b, d));
                F.Add((a, d, c));
            }
        int c0 = V.Count; V.Add(new Vec3(0, 0, 0));
        int c1 = V.Count; V.Add(new Vec3(0, 0, h));
        for (int i = 0; i < nu; i++)
        {
            F.Add((c0, (i + 1) % nu, i));
            F.Add((c1, (nv - 1) * nu + i, (nv - 1) * nu + (i + 1) % nu));
        }
        return (V, F);
    }

    [Fact]
    public void SlicePipelineWarnings_IdenticalUnderGermanCulture()
    {
        var (v, f) = ClosedCylinder();
        double zlo = v.Min(p => p.Z), zhi = v.Max(p => p.Z);
        double cx = 0.5 * (v.Min(p => p.X) + v.Max(p => p.X));
        double cy = 0.5 * (v.Min(p => p.Y) + v.Max(p => p.Y));

        List<string> Run(int mode, bool adaptive, double nozzleClearDeg, double amplitude = 0.4)
        {
            var o = new SliceOptions
            {
                LayerHeight = 3.0,
                FirstLayer = 3.0,
                Mode = mode,
                ConeAngleDeg = 15.0,
                NozzleClearDeg = nozzleClearDeg,
                Adaptive = adaptive,
                HMin = 1.0,
                HMax = 4.5,
                Tol = 0.001,
                ObjectTag = "obj[0] ",
                ZMin = zlo,
                ZMax = zhi,
                Cx = cx,
                Cy = cy,
                Build = new LayerBuildOptions
                {
                    WallCount = 3,
                    Infill = 1,
                    WallSpacing = 5.0,
                    InfillSpacing = 5.0,
                    AngleDeg = 0.0,
                },
                Bricklayer = new BricklayerOptions
                {
                    LayerHeight = 3.0,
                    LayerWidth = 5.0,
                    ShiftFraction = 0.5,
                    OverlapFraction = 0.5,
                    RampLayers = 1,
                },
                NonPlanar = new NonPlanarOptions
                {
                    LayerHeight = 3.0,
                    Amplitude = amplitude,
                    Wavelength = 20.0,
                    DirectionDeg = 0.0,
                    Resolution = 1.0,
                    PhasePerLayer = 0.5,
                    RampLayers = 1,
                    Profile = new SineProfile(),
                },
            };
            return new SlicePipeline().Slice(v, f, o).Warnings;
        }

        var planarInv = RunUnder(CultureInfo.InvariantCulture, () => Run(0, true, 25.0));
        var planarDe = RunUnder(DeDe, () => Run(0, true, 25.0));
        Assert.Equal(planarInv, planarDe);
        Assert.Contains(planarDe, w => w.Contains("adaptive overhang"));
        Assert.Contains(planarDe, w => w.Contains("bricklayer:"));
        Assert.Contains(planarDe, w => w.Contains("non-planar:"));

        var conicalInv = RunUnder(CultureInfo.InvariantCulture, () => Run(1, false, 0.0));
        var conicalDe = RunUnder(DeDe, () => Run(1, false, 0.0));
        Assert.Equal(conicalInv, conicalDe);
        Assert.Contains(conicalDe, w => w.Contains("conical surface angle"));

        // an amplitude the guard refuses: the refusal names both amplitude and limit
        var refusedInv = RunUnder(CultureInfo.InvariantCulture, () => Run(0, false, 25.0, amplitude: 50.0));
        var refusedDe = RunUnder(DeDe, () => Run(0, false, 25.0, amplitude: 50.0));
        Assert.Equal(refusedInv, refusedDe);
        Assert.Contains(refusedDe, w => w.Contains("non-planar modulation skipped") && w.Contains("exceeds the"));

        foreach (var line in planarDe.Concat(conicalDe).Concat(refusedDe))
            Assert.DoesNotMatch(@"\d,\d", line);
    }

    /// <summary>Same rule for the problem text Core formats from doubles: a jog feed
    /// refusal, SCARA and Cartesian envelope errors, an IK reach error, a specimen
    /// description, a prediction band and Vec3 must read the same under de-DE and
    /// never carry a decimal comma.</summary>
    [Fact]
    public void ProblemText_IdenticalUnderGermanCulture()
    {
        static ObjectModelSnapshot Snap() => new()
        {
            Status = MachineStatus.Idle,
            RawStatus = "idle",
            TakenUtc = DateTime.UtcNow,
            AllHomed = true,
            Axes = new List<AxisState>
            {
                new() { Letter = "X", MachinePosition = 600, Homed = true },
                new() { Letter = "Y", MachinePosition = 0, Homed = true },
                new() { Letter = "Z", MachinePosition = 100, Homed = true },
            },
        };
        static List<string> Messages()
        {
            var lim = new RobotLimits { ReachMin = 235.0 };
            var scara = RobotChecks.RunPoints(new[] { new Vec3(1200.5, 0, 450.5) }, lim);
            var cartesian = CartesianChecks.RunPoints(new[] { new Vec3(-300.5, 0.5, -0.5) }, lim);
            var jog = JogPlanner.Plan(Snap(), new JogRequest { Dx = 1.0, FeedMmS = 75.5 }, lim);
            var ik = ScaraKinematics.Inverse(1200.5, 0, 100, lim.L1, lim.L2, lim.BaseH);
            var all = new List<string>
            {
                new Vec3(1.5, 2.25, 3.125).ToString(),
                new PredictionBand(1.5, 0.25, 2.75).ToString(),
                new PredictionBand(1.5, double.NaN, double.NaN).ToString(),
                new TestSpecimen("EN 196-1", "bending", 40.5, 40.5, 160.25, span: 100.5).Describe(),
                new TestSpecimen("EN 12390", "compression", 150.5, 0, 300.25, round: true).Describe(),
                jog.RefusalReason ?? "",
                ik.Error,
            };
            all.AddRange(scara.Problems.Select(p => p.Message));
            all.AddRange(cartesian.Problems.Select(p => p.Message));
            return all;
        }

        var inv = RunUnder(CultureInfo.InvariantCulture, Messages);
        var de = RunUnder(DeDe, Messages);
        Assert.Equal(inv, de);
        // Vec3 separates its components with a bare comma, so it is pinned exactly
        // instead of going through the decimal-comma scan below
        Assert.Equal("(1.5,2.25,3.125)", de[0]);
        Assert.Contains(de, m => m.Contains("exceeds the cap"));
        Assert.Contains(de, m => m.Contains("Target unreachable"));
        Assert.Contains(de, m => m.Contains("envelope minimum"));
        Assert.Contains(de, m => m.Contains("max reach"));
        foreach (var m in de.Skip(1)) Assert.DoesNotMatch(@"\d,\d", m);
    }

    static List<List<Vec3>> Square(double z, double dx = 0) => new()
    {
        new() { new(dx, 0, z), new(dx + 10.5, 0, z), new(dx + 10.5, 10.5, z), new(dx, 10.5, z), new(dx, 0, z) }
    };

    static GcodeConfig Cfg(GcodeFlavor flavor) => new()
    {
        LayerHeight = 1.25,
        Width = 2.5,
        RamDiameter = 10.0,
        PressureCoeff = 1.1,
        PrintSpeed = 22.5,
        TravelSpeed = 61.5,
        PressureOffset = 1.5,
        ZHop = 1.5,
        Flavor = flavor,
        AccelXY = 3000,
        AccelE = 500,
        JerkXY = 15.5,
        JerkE = 5.5,
        RetractAccelFactor = 0.5,
        FloorLayers = 1,
        PauseAfterFloor = true,
        PauseTime = 1500
    };

    void GcodeIsCultureInvariant(GcodeFlavor flavor)
    {
        AssertSameUnderDeDe(() =>
        {
            var cfg = Cfg(flavor);
            var r = GcodeBuilder.Build(
                new[] { Square(0), Square(1.25), Square(2.5, dx: 60) }, cfg);
            var all = new List<string> { Header.Make(cfg), Header.Footer(cfg) };
            all.AddRange(r.Lines);
            return all;
        },
        (inv, de) => Assert.Equal(inv, de));
    }

    [Fact]
    public void MarlinGcode_IdenticalUnderGermanCulture() => GcodeIsCultureInvariant(GcodeFlavor.Marlin);

    [Fact]
    public void RrfGcode_IdenticalUnderGermanCulture() => GcodeIsCultureInvariant(GcodeFlavor.DuetRrf);

    [Fact]
    public void KlipperGcode_IdenticalUnderGermanCulture() => GcodeIsCultureInvariant(GcodeFlavor.Klipper);

    [Fact]
    public void RestartPlan_IdenticalUnderGermanCulture()
    {
        var source = new List<string>
        {
            "G90", "M83", "G1 X0 Y0 Z0.5 F1350",
            "G1 X10.5 Y0 Z0.5 E1.23456 F1350",
            "G1 X10.5 Y10.5 Z0.5 E1.23456 F1350",
            "G1 X10.5 Y10.5 Z1.75 E0.5 F1350",
            "G1 X0 Y10.5 Z1.75 E1.23456 F1350",
        };
        AssertSameUnderDeDe(
            () => GcodeRestart.Plan(source, RestartMode.ZHeight, 1.75, 0, default,
                                    new RestartOptions { TravelSpeed = 61.5, PrimeE = 0.75 }).Lines,
            (inv, de) => Assert.Equal(inv, de));
    }

    [Fact]
    public void DuetJsonNumbers_ParseIdenticallyUnderGermanCulture()
    {
        const string json = "{\"pos\":[12.5,-33.25,0.75],\"speed\":1.5e2,\"neg\":-2.5e-3}";
        var inv = RunUnder(CultureInfo.InvariantCulture, () => JsonValue.Parse(json));
        var de = RunUnder(DeDe, () => JsonValue.Parse(json));
        static double[] Nums(JsonValue n) => new[]
        {
            n["pos"][0].AsNumber(), n["pos"][1].AsNumber(), n["pos"][2].AsNumber(),
            n["speed"].AsNumber(), n["neg"].AsNumber(),
        };
        Assert.Equal(Nums(inv), Nums(de));
        Assert.Equal(12.5, de["pos"][0].AsNumber());
    }
}
