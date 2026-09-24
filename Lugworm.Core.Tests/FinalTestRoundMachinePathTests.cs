using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Final test round 2.6.0, machine-path rows 5.8 and 5.23.</summary>
public class FinalTestRoundMachinePathTests
{
    static GcodeConfig BaseCfg() => new()
    {
        LayerHeight = 1.0,
        Width = 2.0,
        RamDiameter = 10.0,
        PressureCoeff = 1.0,
        PrintSpeed = 20.0
    };

    static List<string> Job(string? planeWord)
    {
        var l = new List<string> { "G90", "M83", "G21" };
        if (planeWord != null) l.Add(planeWord);
        l.AddRange(new[]
        {
            "G28",
            "G0 X0.000 Y0.000 Z1.000 F3600",
            "; layer 1/2",
            "G1 X10.000 Y0.000 Z1.000 F1200 E0.50000",
            "; layer 2/2",
            "G1 X10.000 Y0.000 Z2.000 F3600",
            "G1 X0.000 Y0.000 Z2.000 F1200 E0.50000",
        });
        return l;
    }

    [Fact]
    public void Row523_RestartOfJobWithoutPlaneWord_EmitsNoPlaneWord()
    {
        var plan = GcodeRestart.Plan(Job(null), RestartMode.ZHeight, 2.0, 0, default);
        Assert.DoesNotContain(plan.Lines, l => l.StartsWith("G17") || l.StartsWith("G18") || l.StartsWith("G19"));
    }

    [Fact]
    public void Row523_RestartOfJobWithG18_RestoresG18()
    {
        var plan = GcodeRestart.Plan(Job("G18"), RestartMode.ZHeight, 2.0, 0, default);
        Assert.Contains(plan.Lines, l => l.StartsWith("G18"));
    }

    [Fact]
    public void Row523_ParserAcceptsBarePlaneWords()
    {
        var prog = GcodeParser.Parse(new[] { "G21", "G90", "M83", "G17", "G18", "G19", "G28", "G1 X1 Y0 Z0 F600" });
        Assert.NotNull(prog);
    }

    [Fact]
    public void Row58_DuetFooter_HomesButDoesNotDisableSteppers()
    {
        var cfg = BaseCfg(); cfg.Flavor = GcodeFlavor.DuetRrf;
        var f = Header.Footer(cfg);
        Assert.Contains("G28", f);
        Assert.DoesNotContain("M84", f);
    }

    [Fact]
    public void Row58_MarlinFooter_StillDisablesSteppers()
    {
        var cfg = BaseCfg(); cfg.Flavor = GcodeFlavor.Marlin;
        var f = Header.Footer(cfg);
        Assert.Contains("G28", f);
        Assert.Contains("M84", f);
    }
}
