using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>4.0.0: Klipper's extended commands at the three places that lex G-code -
/// the fail-closed job parser (exact allowlist), the terminal policy (diagnostic /
/// advanced / reset by name) and the restart planner (modal state kept, no throw).</summary>
public class KlipperCommandsTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("G1 X10 Y10", false, "")]
    [InlineData("M104 S200", false, "")]
    [InlineData("N10 G1 X1", false, "")]
    [InlineData("T0", false, "")]
    [InlineData("SET_PRESSURE_ADVANCE ADVANCE=0.05", true, "SET_PRESSURE_ADVANCE")]
    [InlineData("set_velocity_limit accel=1000 ; softened", true, "SET_VELOCITY_LIMIT")]
    [InlineData("STATUS", true, "STATUS")]
    [InlineData("PAUSE", true, "PAUSE")]
    [InlineData("; SET_PRESSURE_ADVANCE in a comment", false, "")]
    [InlineData("", false, "")]
    [InlineData("G28_X", true, "G28_X")]
    public void TryGetName_RecognisesExtendedCommandsOnly(string line, bool extended, string name)
    {
        Assert.Equal(extended, KlipperCommands.TryGetName(line, out string got));
        Assert.Equal(name, got);
    }

    [Fact]
    public void Parameters_AndFormat()
    {
        var p = KlipperCommands.Parameters("SET_VELOCITY_LIMIT ACCEL=1000 square_corner_velocity=5 ; note");
        Assert.Equal("1000", p["ACCEL"]);
        Assert.Equal("5", p["SQUARE_CORNER_VELOCITY"]);
        Assert.Equal("SET_PRINT_STATS_INFO CURRENT_LAYER=3", KlipperCommands.Format(KlipperCommands.SetPrintStatsInfo, ("CURRENT_LAYER", 3)));
        Assert.Equal("SET_PRESSURE_ADVANCE ADVANCE=0.055", KlipperCommands.Format(KlipperCommands.SetPressureAdvance, ("ADVANCE", 0.0549)));
    }

    [Fact]
    public void Parser_AcceptsTheGeneratorsCommands_RefusesMacros()
    {
        var lines = new[]
        {
            "G21", "G90", "M83", "G28",
            "SET_PRESSURE_ADVANCE ADVANCE=0.05",
            "SET_VELOCITY_LIMIT ACCEL=1000 SQUARE_CORNER_VELOCITY=5",
            "SET_PRINT_STATS_INFO TOTAL_LAYER=1",
            "G1 X10 Y0 Z0.2 F1200",
            "SET_PRINT_STATS_INFO CURRENT_LAYER=1",
            "G1 X10 Y10 Z0.2 E0.3 F1200",
        };
        var prog = GcodeParser.Parse(lines, allowPerAxisHome: true);
        Assert.Equal(2, prog.Moves.Count);

        var macro = lines.Take(4).Concat(new[] { "PRINT_START EXTRUDER=210 BED=60", "G1 X10 Y0 Z0.2 F1200" });
        var ex = Assert.Throws<FormatException>(() => GcodeParser.Parse(macro, allowPerAxisHome: true));
        Assert.Contains("PRINT_START", ex.Message);
        Assert.Contains("line 5", ex.Message);
        // the terminal-only ones are not job commands either
        Assert.Throws<FormatException>(() => GcodeParser.Parse(lines.Take(4).Concat(new[] { "FIRMWARE_RESTART" }), allowPerAxisHome: true));
    }

    [Theory]
    [InlineData("STATUS", TerminalRoute.Diagnostic)]
    [InlineData("QUERY_ENDSTOPS", TerminalRoute.Diagnostic)]
    [InlineData("HELP", TerminalRoute.Diagnostic)]
    [InlineData("SET_HEATER_TEMPERATURE HEATER=extruder TARGET=200", TerminalRoute.Advanced)]
    [InlineData("SET_PRESSURE_ADVANCE ADVANCE=0.05", TerminalRoute.Advanced)]
    [InlineData("FIRMWARE_RESTART", TerminalRoute.Advanced)]
    [InlineData("CANCEL_PRINT", TerminalRoute.Advanced)]
    [InlineData("PRINT_START", TerminalRoute.Refused)]
    [InlineData("BED_MESH_CALIBRATE", TerminalRoute.Refused)]
    public void Terminal_ClassifiesExtendedCommandsByName(string text, TerminalRoute expected)
    {
        Assert.Equal(expected, TerminalPolicy.Classify(text, out var line, out string? lexError));
        Assert.Null(line);
        Assert.Null(lexError);
    }

    [Fact]
    public void Terminal_FirmwareRestartTakesTheResetRule_AndArmsLikeAnyAdvancedCommand()
    {
        var refused = TerminalPolicy.Decide("FIRMWARE_RESTART", DateTime.MinValue, T0, resetAllowed: false);
        Assert.Equal(TerminalSendDecision.Refuse, refused.Decision);
        Assert.Contains("halted", refused.Reason);
        var armed = TerminalPolicy.Decide("FIRMWARE_RESTART", DateTime.MinValue, T0, resetAllowed: true);
        Assert.Equal(TerminalSendDecision.Arm, armed.Decision);
        var sent = TerminalPolicy.Decide("FIRMWARE_RESTART", TerminalPolicy.ArmUntil(T0), T0.AddSeconds(1), resetAllowed: true);
        Assert.Equal(TerminalSendDecision.Send, sent.Decision);
        var diag = TerminalPolicy.Decide("STATUS", DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Send, diag.Decision);
        Assert.Equal(TerminalRoute.Diagnostic, diag.Route);
        // the existing M999 rule is unchanged
        Assert.True(TerminalPolicy.IsReset("RESTART"));
        Assert.False(TerminalPolicy.IsReset("STATUS"));
    }

    [Fact]
    public void Restart_KeepsKlipperModalState_AndReheatsBeforeMotion()
    {
        var src = new List<string>
        {
            "G90", "M83",
            "M140 S60", "M104 S210", "G28", "M190 S60", "M109 S210",
            "SET_PRESSURE_ADVANCE ADVANCE=0.05",
            "SET_VELOCITY_LIMIT ACCEL=1500",
            "SET_PRINT_STATS_INFO TOTAL_LAYER=2",
            "G0 X0.000 Y0.000 Z0.200 F3600",
            "; layer 1/2",
            "SET_PRINT_STATS_INFO CURRENT_LAYER=1",
            "G1 X10.000 Y0.000 Z0.200 F1200 E0.30000",
            "; layer 2/2",
            "SET_PRINT_STATS_INFO CURRENT_LAYER=2",
            "G1 X10.000 Y0.000 Z0.400 F3600",
            "G1 X0.000 Y0.000 Z0.400 F1200 E0.30000",
            "M104 S0", "M84",
        };
        var plan = GcodeRestart.Plan(src, RestartMode.ZHeight, 0.4, 0, default);
        Assert.Equal(0.4, plan.Resume.Z, 6);
        int firstMotion = plan.Lines.FindIndex(l => l.StartsWith("G0 ") || l.StartsWith("G1 "));
        int reheat = plan.Lines.IndexOf("M109 S210");
        int bedWait = plan.Lines.IndexOf("M190 S60");
        Assert.True(reheat >= 0 && bedWait >= 0 && reheat < firstMotion && bedWait < firstMotion, string.Join("|", plan.Lines));
        Assert.Single(plan.Lines.Where(l => l == "SET_PRESSURE_ADVANCE ADVANCE=0.05"));
        Assert.Single(plan.Lines.Where(l => l == "SET_VELOCITY_LIMIT ACCEL=1500"));
        Assert.Contains("G1 X0.000 Y0.000 Z0.400 F1200 E0.30000", plan.Lines);
        Assert.Contains("M84", plan.Lines);
        // a paste job (no temperatures) restores nothing thermal
        var cold = GcodeRestart.Plan(new List<string>
        {
            "G90", "M83", "G0 X0.000 Y0.000 Z1.000 F3600",
            "G1 X10.000 Y0.000 Z1.000 F1200 E0.50000",
            "G1 X10.000 Y0.000 Z2.000 F3600",
            "G1 X0.000 Y0.000 Z2.000 F1200 E0.50000",
        }, RestartMode.ZHeight, 2.0, 0, default);
        Assert.DoesNotContain(cold.Lines, l => l.StartsWith("M104") || l.StartsWith("M190"));
    }
}
