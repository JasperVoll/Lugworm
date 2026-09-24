using System;
using System.Collections.Generic;
using System.IO;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The lab board's homeall.g was rewritten on 2026-08-25 to delegate to the
/// per-axis macros with `M98 P"…"`. The reader rejected that outright, so every G28
/// left the tracked arm configuration Unknown and every configuration-sensitive
/// action stayed refused - jog, validate, upload, start - with no way to clear it.
/// These run against the board's real files, copied into fixtures/ as provenance
/// (the board copy lives outside the repository), exactly like potterbot-config.g.</summary>
public class HomeMacroIncludeTests
{
    static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    /// <summary>The board's /sys as the reader sees it, keyed by canonical path.</summary>
    static Func<string, string?> BoardMacros()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HomeMacroReader.NormalizeMacroPath("0:/sys/homez.g")] = Fixture("potterbot-homez.g"),
            [HomeMacroReader.NormalizeMacroPath("0:/sys/homeproximal.g")] = Fixture("potterbot-homeproximal.g"),
            [HomeMacroReader.NormalizeMacroPath("0:/sys/homedistal.g")] = Fixture("potterbot-homedistal.g"),
        };
        return path => files.TryGetValue(path, out var text) ? text : null;
    }

    /// <summary>The board's own homeall.g, whichever shape it is in. It has called
    /// `M98 P"0:/sys/homez.g"` since 2026-08-25, so it does not parse at all without
    /// include support - the whole point of this fix.</summary>
    [Fact]
    public void LiveHomeAll_ParsesThroughItsM98Calls()
    {
        Assert.True(HomeMacroReader.TryParse(Fixture("potterbot-homeall.g"), BoardMacros(),
            out var plan, out string error), error);
        Assert.Equal("", error);
        Assert.True(plan.HasTrigger);

        // what survives the last homing trigger is homeall.g's own park move
        var points = plan.Replay(new Vec3(-32.983, -263.535, 400));
        Assert.Single(points);
        Assert.Equal(200, points[0].X, 3);
        Assert.Equal(-500, points[0].Y, 3);
        Assert.Equal(400, points[0].Z, 3);
    }

    /// <summary>The fully delegated shape the board also ships from time to time: no
    /// homing move of its own, every axis handed to a callee, so the G1 H1/H2 trigger
    /// exists ONLY inside an include. Nothing here can work unless the calls are
    /// followed and the callees' modal state carries back.</summary>
    [Fact]
    public void FullyDelegatedHomeAll_FindsTheTriggerInsideACallee()
    {
        const string delegated =
            ";Home all\n" +
            "M98 P\"0:/sys/homez.g\"           ; Z to its high-end switch\n" +
            "M98 P\"0:/sys/homeproximal.g\"    ; proximal arm\n" +
            "M98 P\"0:/sys/homedistal.g\"      ; distal arm\n" +
            "\n" +
            ";Park - legal only now that all three axes are homed\n" +
            "G90\n" +
            "G0 X200 Y-500 F4000\n";

        Assert.Equal(new[] { "/sys/homez.g", "/sys/homeproximal.g", "/sys/homedistal.g" },
                     HomeMacroReader.ExtractIncludes(delegated));
        Assert.True(HomeMacroReader.TryParse(delegated, BoardMacros(),
            out var plan, out string error), error);
        Assert.True(plan.HasTrigger);

        var points = plan.Replay(new Vec3(-32.983, -263.535, 400));
        Assert.Single(points);
        Assert.Equal(200, points[0].X, 3);
        Assert.Equal(-500, points[0].Y, 3);
    }

    /// <summary>End to end, the way DuetJogComponent runs it after a verified full
    /// G28: seed at the endstop-determined arm mode, start from the joint-minimum
    /// trigger pose, walk the parsed park move, and require a KNOWN configuration.
    /// This is the assertion that would have caught the reported bug.</summary>
    [Fact]
    public void VerifiedHomeReplay_EstablishesAKnownArmConfiguration()
    {
        Assert.True(HomeMacroReader.TryParse(Fixture("potterbot-homeall.g"), BoardMacros(),
            out var plan, out _));

        var lim = new RobotLimits { ReachMin = 235.0 };
        var trigger = ScaraKinematics.Forward(lim.J1Min, lim.J3Min, 400.0 - lim.BaseH,
                                              lim.L1, lim.L2, lim.BaseH);
        var triggerPose = new Vec3(trigger.x, trigger.y, trigger.z);

        var points = plan.Replay(triggerPose);
        points.Insert(0, triggerPose);
        var check = RobotChecks.RunPoints(points, lim, ArmConfiguration.Seed(lim.HomedArmMode));

        Assert.True(check.ReachOk, string.Join("; ", check.Problems));
        Assert.True(check.FinalConfiguration.Known);
        Assert.Equal(lim.HomedArmMode, check.FinalConfiguration.ArmMode);
    }

    [Fact]
    public void M98WithoutAMacroSource_FailsClosed()
    {
        // the legacy overload has no way to fetch the callee, so it must refuse
        // rather than parse a macro whose motion it cannot see
        Assert.False(HomeMacroReader.TryParse(Fixture("potterbot-homeall.g"),
            out _, out string error));
        Assert.Contains("M98", error);
    }

    [Fact]
    public void UnreachableInclude_FailsClosed()
    {
        Assert.False(HomeMacroReader.TryParse(Fixture("potterbot-homeall.g"),
            _ => null, out _, out string error));
        Assert.Contains("could not be read", error);
    }

    [Fact]
    public void RecursiveInclude_FailsClosed()
    {
        string self = "G1 H1 X-10 F600\nM98 P\"0:/sys/loop.g\"\n";
        Assert.False(HomeMacroReader.TryParse(self, _ => self, out _, out string error));
        Assert.Contains("recursive", error);
    }

    [Fact]
    public void ModalStatePersistsAcrossAnInclude()
    {
        // RepRapFirmware's G90/G91 state is global: a callee that leaves relative
        // mode leaves the caller in relative mode too.
        string root = "M98 P\"0:/sys/child.g\"\nG1 X10 Y-20 F4000\n";
        string child = "G91\nG1 H1 X-10 F600\n";
        Assert.True(HomeMacroReader.TryParse(root, _ => child, out var plan, out string error), error);
        var points = plan.Replay(new Vec3(100, -200, 400));
        Assert.Equal(110, points[^1].X, 3);    // relative, not absolute X10
        Assert.Equal(-220, points[^1].Y, 3);
    }

    [Theory]
    [InlineData("M400\nG1 H1 X-10 F600\nG90\nG0 X5 Y-5 F1000\n")]
    [InlineData("G1 H1 X-10 F600\nG4 P500\nG90\nG0 X5 Y-5 F1000\n")]
    public void InertCommandsAreTolerated(string macro)
    {
        Assert.True(HomeMacroReader.TryParse(macro, out var plan, out string error), error);
        var points = plan.Replay(new Vec3(0, 0, 400));
        Assert.Equal(5, points[^1].X, 3);
        Assert.Equal(-5, points[^1].Y, 3);
    }

    [Theory]
    [InlineData("M18\nG1 H1 X-10 F600\n")]          // disables motors - not inert
    [InlineData("G1 H1 X-10 F600\nG2 X5 Y5 I1 J1\n")]   // arcs are not modelled here
    public void UnmodelledCommandsStillFailClosed(string macro)
    {
        Assert.False(HomeMacroReader.TryParse(macro, out _, out string error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("0:/sys/homez.g", "/sys/homez.g")]
    [InlineData("homez.g", "/sys/homez.g")]
    [InlineData("/sys/HomeZ.g", "/sys/homez.g")]
    [InlineData("0:\\sys\\homez.g", "/sys/homez.g")]
    public void MacroPathsCanonicalise(string raw, string expected)
        => Assert.Equal(expected, HomeMacroReader.NormalizeMacroPath(raw));

    [Fact]
    public void IncludesAreDiscoverableForPreFetching()
    {
        // MachineSession crawls this list to download the tree before parsing
        Assert.Contains("/sys/homez.g",
            HomeMacroReader.ExtractIncludes(Fixture("potterbot-homeall.g")));
    }
}
