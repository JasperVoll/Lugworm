using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>When the session may keep the tracked arm configuration through an event
/// that would otherwise forget it: a transport reconnect, and the job's own G28.</summary>
public class ArmConfigurationCarryTests
{
    static readonly ArmConfiguration Homed = new ArmConfiguration(1, 0);

    static ObjectModelSnapshot Snap(MachineStatus status = MachineStatus.Idle, bool homed = true,
                                    double x = 200, double y = -500, double z = 300,
                                    bool withZ = true, string? job = null)
    {
        var axes = new List<AxisState>
        {
            new AxisState { Letter = "X", MachinePosition = x, Homed = homed },
            new AxisState { Letter = "Y", MachinePosition = y, Homed = homed },
        };
        if (withZ) axes.Add(new AxisState { Letter = "Z", MachinePosition = z, Homed = homed });
        return new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            TakenUtc = DateTime.UtcNow,
            AllHomed = homed,
            Axes = axes,
            Job = job == null ? null : new JobState { FileName = job },
        };
    }

    [Fact]
    public void Reconnect_to_an_unmoved_homed_idle_machine_keeps_the_configuration()
    {
        var carry = ArmConfigurationCarry.CaptureAtLoss(Homed, Snap());

        Assert.NotNull(carry);
        Assert.Null(ArmConfigurationCarry.ReconnectRefusal(carry!, Snap(), trustRefusal: null));
        Assert.Equal(Homed.ArmMode, carry!.Configuration.ArmMode);
        Assert.Equal(Homed.ThetaTurns, carry.Configuration.ThetaTurns);
    }

    public static IEnumerable<object?[]> AfterReconnect => new[]
    {
        // name, snapshot after the reconnect, trust refusal, expected reason fragment (null = kept)
        new object?[] { "within tolerance", Snap(x: 200.04), null, null },
        new object?[] { "X moved", Snap(x: 200.06), null, "moved" },
        new object?[] { "Y moved", Snap(y: -499.9), null, "moved" },
        new object?[] { "Z moved", Snap(z: 330), null, "moved" },
        new object?[] { "unhomed (reset)", Snap(homed: false), null, "unhomed" },
        new object?[] { "busy", Snap(status: MachineStatus.Busy), null, "idle" },
        new object?[] { "identity", Snap(), "fingerprint mismatch", "identity" },
        new object?[] { "position unreadable", Snap(withZ: false), null, "position" },
    };

    [Theory]
    [MemberData(nameof(AfterReconnect))]
    public void Reconnect_keeps_the_configuration_only_when_nothing_can_have_moved_the_arm(
        string name, ObjectModelSnapshot after, string? trustRefusal, string? expected)
    {
        var carry = ArmConfigurationCarry.CaptureAtLoss(Homed, Snap())!;

        string? refusal = ArmConfigurationCarry.ReconnectRefusal(carry, after, trustRefusal);

        if (expected == null) Assert.True(refusal == null, name + ": " + refusal);
        else Assert.Contains(expected, refusal ?? "<kept>");
    }

    public static IEnumerable<object?[]> AtLoss => new[]
    {
        new object?[] { "unknown configuration", ArmConfiguration.Unknown, Snap() },
        new object?[] { "no snapshot", Homed, null },
        new object?[] { "job running", Homed, Snap(status: MachineStatus.Processing) },
        new object?[] { "moving", Homed, Snap(status: MachineStatus.Busy) },
        new object?[] { "unhomed", Homed, Snap(homed: false) },
        new object?[] { "position unreadable", Homed, Snap(withZ: false) },
    };

    [Theory]
    [MemberData(nameof(AtLoss))]
    public void Nothing_is_captured_when_the_last_poll_cannot_anchor_the_arm(
        string name, ArmConfiguration configuration, ObjectModelSnapshot? last)
        => Assert.True(ArmConfigurationCarry.CaptureAtLoss(configuration, last) == null, name);

    [Theory]
    [InlineData(MachineStatus.Processing, MachineStatus.Idle, true, true)]
    [InlineData(MachineStatus.Processing, MachineStatus.Idle, false, false)]   // unhomed at the end
    [InlineData(MachineStatus.Simulating, MachineStatus.Idle, true, false)]    // dry run moved nothing
    [InlineData(MachineStatus.Paused, MachineStatus.Idle, true, false)]
    [InlineData(MachineStatus.Cancelling, MachineStatus.Idle, true, false)]    // cancel.g homes
    [InlineData(MachineStatus.Processing, MachineStatus.Busy, true, false)]
    [InlineData(MachineStatus.Processing, MachineStatus.Halted, true, false)]
    public void Only_a_print_that_ran_to_idle_on_a_homed_machine_counts_as_completed(
        MachineStatus previous, MachineStatus current, bool homed, bool completed)
        => Assert.Equal(completed,
            ArmConfigurationCarry.JobCompleted(previous, Snap(status: current, homed: homed)));

    [Theory]
    [InlineData(MachineStatus.Processing, "0:/gcodes/a.gcode", "0:/gcodes/a.gcode", null, true)]
    [InlineData(MachineStatus.Processing, "0:/gcodes/A.GCODE ", "0:/gcodes/a.gcode", null, true)]
    [InlineData(MachineStatus.Processing, "0:/gcodes/b.gcode", "0:/gcodes/a.gcode", null, false)]
    [InlineData(MachineStatus.Processing, "0:/gcodes/a.gcode", null, null, false)]
    [InlineData(MachineStatus.Processing, "0:/gcodes/a.gcode", "0:/gcodes/a.gcode", "identity", false)]
    [InlineData(MachineStatus.Paused, "0:/gcodes/a.gcode", "0:/gcodes/a.gcode", null, false)]
    [InlineData(MachineStatus.Idle, null, "0:/gcodes/a.gcode", null, false)]
    public void A_job_survives_a_reconnect_only_while_the_same_job_still_prints(
        MachineStatus status, string? file, string? initiated, string? trustRefusal, bool kept)
        => Assert.Equal(kept,
            ArmConfigurationCarry.JobStillRunning(Snap(status: status, job: file), initiated, trustRefusal));
}
