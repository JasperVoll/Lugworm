using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lugworm.Core.Clipping;
using Lugworm.Core.Doe;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Hardening tests from the 2026-07-14 refinement run (P2):
/// degenerate inputs produce readable errors or diagnostics, never NaN/silence.</summary>
[Collection(ExclusivePerformanceCollection.Name)]
public class RobustnessTests
{
    // D5 (2026-09-14): see ParetoTests / PerfReference for the convention -
    // wall-clock budgets below are K * this, not a fixed number of seconds.
    static readonly Lazy<double> s_referenceMs = new(PerfReference.MeasureMs);

    static List<Vec3> Square(double s, double z = 0) => new()
    {
        new(0, 0, z), new(s, 0, z), new(s, s, z), new(0, s, z), new(0, 0, z)
    };

    // ---- Infill -----------------------------------------------------------

    [Fact]
    public void ClipLines_MixedZ_KeepsPerLineZ()
    {
        var polys = new List<IReadOnlyList<Vec3>> { Square(20) };
        var lines = new List<(Vec3, Vec3)>
        {
            (new Vec3(-5, 5, 1.0), new Vec3(25, 5, 1.0)),
            (new Vec3(-5, 10, 2.0), new Vec3(25, 10, 2.0)),
        };
        var spans = Infill.ClipLines(lines, polys);
        Assert.Equal(2, spans.Count);
        var zs = spans.Select(s => s.Item1.Z).OrderBy(v => v).ToArray();
        Assert.Equal(1.0, zs[0], 9);
        Assert.Equal(2.0, zs[1], 9);   // was: both stamped with 1.0
    }

    [Fact]
    public void Zigzag_DegenerateBoundary_ReportsWhy()
    {
        var degenerate = new List<IReadOnlyList<Vec3>> { new List<Vec3> { new(0, 0, 0), new(1, 0, 0) } };
        var path = Infill.Zigzag(degenerate, 2.0, 0, 0, out string? why);
        Assert.Empty(path);
        Assert.NotNull(why);
        Assert.Contains("degenerate", why);
    }

    [Fact]
    public void Zigzag_ZeroSpacing_ReportsWhy()
    {
        var path = Infill.Zigzag(new List<IReadOnlyList<Vec3>> { Square(20) }, 0.0, 0, 0, out string? why);
        Assert.Empty(path);
        Assert.Contains("spacing", why);
    }

    [Fact]
    public void Zigzag_ValidBoundary_StillFills()
    {
        var path = Infill.Zigzag(new List<IReadOnlyList<Vec3>> { Square(20) }, 2.0, 0, 0, out string? why);
        Assert.True(path.Count > 4);
        Assert.Null(why);
    }

    // ---- Nesting ------------------------------------------------------------

    [Fact]
    public void NestingDepth_DegenerateRing_ReadableError()
    {
        var rings = new List<IReadOnlyList<Vec3>>
        {
            Square(20),
            new List<Vec3> { new(1, 1, 0), new(2, 1, 0) },   // 2 points
        };
        var ex = Assert.Throws<ArgumentException>(() => Nesting.Depth(rings, 1));
        Assert.Contains("degenerate", ex.Message);
    }

    // ---- DoE / tables --------------------------------------------------------

    [Fact]
    public void ReadCsv_MissingFile_ReadableIOException()
    {
        var ex = Assert.Throws<System.IO.IOException>(
            () => TableIo.ReadCsv(@"Z:\definitely\not\there.csv"));
        Assert.Contains("Cannot read CSV", ex.Message);
    }

    // 13 factors: the full 3^13 grid is 1.6M points; the cap must keep this fast.
    static (RsmModel model, double[] lows, double[] highs) ManyFactorsSetup()
    {
        int m = 13, n = 40;
        var names = Enumerable.Range(0, m).Select(i => "f" + i).ToArray();
        var lows = Enumerable.Repeat(0.0, m).ToArray();
        var highs = Enumerable.Repeat(1.0, m).ToArray();
        var rnd = new Random(7);
        var x = Enumerable.Range(0, n)
            .Select(_ => Enumerable.Range(0, m).Select(__ => rnd.NextDouble()).ToArray()).ToArray();
        var y = x.Select(r => r.Sum()).ToArray();
        var model = RsmFitter.Fit("sum", names, lows, highs, null, x, y, RsmModelType.Linear);
        return (model, lows, highs);
    }

    [Fact]
    public void Desirability_ManyFactors_FindsGoodRegionUnderCap()
    {
        // Functional half of the split (D5): always runs, regardless of the
        // Category=Perf filter.
        var (model, lows, highs) = ManyFactorsSetup();
        var res = DesirabilityOptimizer.Optimize(new[] { model }, new[] { Goal.Parse("max", "sum", 0, 13) },
                                        lows: lows, highs: highs);
        Assert.Contains("cap", res.Notes);            // sampled fallback was used + noted
        Assert.True(res.DOverall > 0.5);              // still finds a good region
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Desirability_ManyFactors_CompletesUnderCap()
    {
        // D5: budget = 270 * reference - see ParetoTests for the convention.
        var (model, lows, highs) = ManyFactorsSetup();
        var sw = Stopwatch.StartNew();
        DesirabilityOptimizer.Optimize(new[] { model }, new[] { Goal.Parse("max", "sum", 0, 13) },
                                        lows: lows, highs: highs);
        sw.Stop();
        double budgetMs = 270 * s_referenceMs.Value;
        Assert.True(sw.Elapsed.TotalMilliseconds < budgetMs,
            $"took {sw.Elapsed.TotalMilliseconds:0}ms (budget {budgetMs:0}ms = 270x reference {s_referenceMs.Value:0}ms)");
    }

    // ---- robot.xml parsing ----------------------------------------------------

    [Fact]
    public void RobotDefinition_MalformedNumeric_RecordedNotSilent()
    {
        string xml = """
            <Robot name="test">
              <Base x="banana" y="0" z="0" />
              <Reach min="235" max="1008" />
              <Links>
                <Link name="l1" layer="L1" length="oops" />
              </Links>
              <Joints>
                <Joint name="j1" type="revolute" parent="l1" child="l1" axis="0 0" home="0" />
              </Joints>
            </Robot>
            """;
        var def = RobotDefinition.Parse(xml);
        Assert.True(def.ParseWarnings.Count >= 3);
        Assert.Contains(def.ParseWarnings, w => w.Contains("banana"));
        Assert.Contains(def.ParseWarnings, w => w.Contains("oops"));
        Assert.Contains(def.ParseWarnings, w => w.Contains("axis"));
        Assert.Equal(0.0, def.Base.X, 9);            // defaulted, not garbage
    }

    [Fact]
    public void RobotDefinition_NonFiniteNumeric_RecordedAndDefaulted()
    {
        const string xml = """
            <Robot name="test">
              <Base x="1e999" y="0" z="0" />
              <Reach min="0" max="1e999" />
            </Robot>
            """;

        var def = RobotDefinition.Parse(xml);

        Assert.Equal(0.0, def.Base.X);
        Assert.Equal(0.0, def.ReachMax);
        Assert.Contains(def.ParseWarnings, w => w.Contains("1e999"));
    }

    [Fact]
    public void RobotDefinition_NonFiniteCalibratedHomePose_IsDisabled()
    {
        const string xml = """
            <Robot name="test">
              <HomePose calibrated="true" x="1e999" y="0" z="0" />
            </Robot>
            """;

        var def = RobotDefinition.Parse(xml);

        Assert.False(def.HomePoseCalibrated);
        Assert.Null(def.CalibratedHomePose);
        Assert.Contains(def.ParseWarnings, w => w.Contains("finite"));
    }

    [Fact]
    public void RobotDefinition_WellFormed_ParsesCalibratedHomePose()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "robot.xml");
        var def = RobotDefinition.Parse(System.IO.File.ReadAllText(path));
        Assert.Empty(def.ParseWarnings);
        Assert.True(def.HomePosePresent);
        Assert.True(def.HomePoseCalibrated);
        Assert.True(def.CalibratedHomePose.HasValue);
        Assert.Equal(200.0, def.CalibratedHomePose!.Value.X, 9);
        Assert.Equal(-500.0, def.CalibratedHomePose!.Value.Y, 9);
        Assert.Equal(400.0, def.CalibratedHomePose!.Value.Z, 9);
    }
}
