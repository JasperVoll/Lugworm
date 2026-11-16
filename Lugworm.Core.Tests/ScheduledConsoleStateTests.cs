using System;
using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class ScheduledConsoleStateTests
{
    static PrintConsoleState Validated(bool homed = false)
    {
        var s = PrintConsoleState.Empty("printer.local")
            .WithConnection(ConsoleConnection.Connected)
            .WithConnectionGeneration(1)
            .WithIdentity("session", "machine", null)
            .WithMachine(MachineStatus.Idle, "idle")
            .WithHomed(homed, homed, homed, homed)
            .WithGcode(3, 17)
            .WithValidation(new ValidationProjection(17, false, null, null, false))
            .WithUploaded(new UploadRecord("0:/gcodes/print.gcode", 17, 3, 1));
        return s;
    }

    [Fact]
    public void Schedule_gate_omits_the_homing_requirement_and_cancel_is_always_available()
    {
        var buttons = PrintConsoleButtons.Enabled(Validated(), true, true, false, true);
        Assert.True(buttons.Schedule);
        Assert.False(buttons.Start);
        Assert.False(buttons.ScheduleCancel);

        var scheduled = PrintConsoleReducer.StartScheduled(Validated(), new ScheduledStart(
            1, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow, "", "", "0:/gcodes/print.gcode",
            false, "printer.local", "session", 1, "machine"));
        var armed = PrintConsoleButtons.Enabled(scheduled, true, true, false, true);
        Assert.True(armed.ScheduleCancel);
        Assert.False(armed.Schedule);
        Assert.Equal("schedule", ConsoleNextAction.Decide(scheduled, armed).Id);
        Assert.Contains("Starts at", ConsoleNextAction.Decide(scheduled, armed).Label);
    }

    [Fact]
    public void Upload_is_refused_for_canvas_bytes_different_from_an_armed_schedule()
    {
        var oldJob = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G1 X1\n"), null,
                                            JobSource.ConsoleInput, DateTime.UtcNow);
        var s = Validated().WithPreparedJob(oldJob);
        var schedule = new ScheduledStart(1, DateTime.UtcNow.AddMinutes(2), DateTime.UtcNow,
                                          oldJob.Sha256, "old", "0:/gcodes/print.gcode", false,
                                          "printer.local", "session", 1, "machine");
        s = PrintConsoleReducer.StartScheduled(s, schedule);
        var newer = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G1 X2\n"), null,
                                          JobSource.ConsoleInput, DateTime.UtcNow);
        s = s.WithPreparedJob(newer);

        var b = PrintConsoleButtons.Enabled(s, true, true, false, true);
        Assert.False(b.Upload);
        Assert.True(b.ScheduleUpdate);
        Assert.Contains("Cancel or Update", b.Reasons["upload"]);
    }

    [Fact]
    public void Paused_machine_can_add_a_pause_schedule()
    {
        var s = Validated().WithMachine(MachineStatus.Paused, "paused");
        var b = PrintConsoleButtons.Enabled(s, true, true, false, true);
        Assert.True(b.PauseSchedule);
    }
}
