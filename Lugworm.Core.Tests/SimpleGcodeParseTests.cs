using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Everyday G-code through the Robot Simulation path: slicer status lines are
/// accepted, refusals stay FormatException (the component reports them), and a G-code
/// text that lands on the robot socket yields a bounded message, never the whole file.</summary>
public class SimpleGcodeParseTests
{
    static readonly string[] CuraStart =
    {
        ";FLAVOR:Marlin", "M105", "M82 ;absolute extrusion mode",
        "G28 ;Home", "G92 E0", "G1 F2400 E-5", ";LAYER_COUNT:2", ";LAYER:0", "M107", "G0 F3600 X10 Y10 Z0.3",
        "G1 F1200 X20 Y10 E0.5", "G1 X20 Y20 E1.0", "M73 P50", "M117 Printing...", "T0", "M2",
    };

    [Fact]
    public void SlicerStatusLinesAndProgramEndParse()
    {
        var program = GcodeParser.Parse(CuraStart);
        // the E-only retract, the travel and the two extruding moves
        Assert.Equal(4, program.Moves.Count);
        Assert.True(program.Moves[3].Extrude);
    }

    [Fact]
    public void RefusalsStayFormatExceptions()
    {
        // M30 deletes a card file on Marlin; a card-level command never belongs in a job
        Assert.Throws<FormatException>(() => GcodeParser.Parse(new[] { "G28", "M30" }));
        // Klipper macros are not self-contained
        Assert.Throws<FormatException>(() => GcodeParser.Parse(new[] { "START_PRINT" }));
        // and a value the machine cannot execute
        Assert.Throws<FormatException>(() => GcodeParser.Parse(new[] { "G1 X1e400" }));
    }

    [Fact]
    public void SimulationOfEverydayGcodeNeverThrows()
    {
        var potterbot = RobotCatalog.Get("potterbot-scara-v4");
        var home = potterbot.CreateScaraLimits().CalibratedHomePose;
        foreach (var lines in new[]
        {
            CuraStart,
            new[] { "G1 X10 Y10 Z1 F600", "G1 X20 E1" },
            new[] { "G28", "G1 F1500", "G1 X500 Y-300 Z10", "G1 Z5", "G1 E2", "G1 X510" },
            new[] { "G28", "G91", "G1 X5 Y5 Z1 F600", "G1 X10 E1", "G90" },
            new[] { "G28" },
            new[] { "", "   ", "; only comments" },
            new[] { "N1 G28*18", "N2 G1 X500 Y-300 Z10 F600*99" },
            new[] { "G28", "G1 X5000 Y5000 Z10 F600", "G1 X5010 E1" },
        })
        {
            foreach (var (profile, seed) in new[] { (RobotCatalog.Get("none"), (Vec3?)null), (potterbot, (Vec3?)home) })
            {
                var program = GcodeParser.Parse(lines, seed);
                var pts = new List<Vec3>();
                if (program.Moves.Count > 0) pts.Add(new Vec3(program.Moves[0].StartX, program.Moves[0].StartY, program.Moves[0].StartZ));
                foreach (var m in program.Moves) pts.Add(new Vec3(m.X, m.Y, m.Z));
                var result = profile.Mechanism == RobotMechanism.Scara
                    ? RobotSimulation.SolveProgram(profile, program, pts, GcodeFlavor.DuetRrf, arcSupport: true, new RobotSimulationOptions { ScaraArmMode = 1 })
                    : RobotSimulation.Solve(profile, pts);
                Assert.Equal(profile.Mechanism == RobotMechanism.Scara ? pts.Count : 0, result.JointsDeg.Count);
            }
        }
    }

    [Fact]
    public void GcodeOnTheRobotSocketGivesABoundedMessage()
    {
        string gcode = string.Join("\n", Enumerable.Repeat("G1 X10 Y10 Z1 F600 E1", 500));
        var ex = Assert.Throws<ArgumentException>(() => RobotCatalog.Resolve(gcode));
        Assert.True(ex.Message.Length < 200, ex.Message);
        Assert.DoesNotContain("\n", ex.Message);
        Assert.Contains("Robot Definition", ex.Message);
        var unknown = Assert.Throws<ArgumentException>(() => RobotCatalog.Get(new string('x', 400)));
        Assert.True(unknown.Message.Length < 120);
    }
}
