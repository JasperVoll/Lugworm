using System;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class PrintScheduleTests
{
    static readonly DateTime T0 = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    static PrintConsoleState Ready()
    {
        var s = PrintConsoleState.Empty("printer.local")
            .WithConnection(ConsoleConnection.Connected)
            .WithConnectionGeneration(7)
            .WithIdentity("session-1", "machine-1", null)
            .WithMachine(MachineStatus.Idle, "idle")
            .WithGcode(12, 0x1234UL)
            .WithValidation(new ValidationProjection(0x1234UL, false, null, null, false))
            .WithUploaded(new UploadRecord("0:/gcodes/print.gcode", 0x1234UL, 42, 1));
        return s;
    }

    static ScheduledStart Schedule()
        => new ScheduledStart(11, T0.AddMinutes(10), T0, "job-sha", "job-sha", "0:/gcodes/print.gcode",
                              false, "printer.local", "session-1", 7, "machine-1");

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(86400, false)]
    [InlineData(86401, true)]
    public void Arm_horizon_has_frozen_boundaries(int seconds, bool refused)
    {
        string? reason = PrintSchedulePolicy.RefuseTime(T0, T0.AddSeconds(seconds));
        Assert.Equal(refused, reason != null);
    }

    [Fact]
    public void Arm_requires_validation_upload_and_idle_but_not_homing()
    {
        var s = Ready().WithHomed(false, false, false, false);
        Assert.Null(PrintSchedulePolicy.RefuseArm(T0, T0.AddMinutes(2), s));
        Assert.NotNull(PrintSchedulePolicy.RefuseArm(T0, T0.AddMinutes(2), s.WithMachine(MachineStatus.Processing, "processing")));
        Assert.NotNull(PrintSchedulePolicy.RefuseArm(T0, T0.AddMinutes(2), s.WithValidation(null)));
        Assert.NotNull(PrintSchedulePolicy.RefuseArm(T0, T0.AddMinutes(2), s.WithUploaded(null)));
    }

    [Fact]
    public void Decide_fires_at_the_due_time_and_misses_only_after_five_minutes()
    {
        var schedule = Schedule();
        var state = Ready();
        Assert.Equal(ScheduleDecisionKind.Wait, PrintSchedulePolicy.Decide(T0, schedule, state).Kind);
        Assert.Equal(ScheduleDecisionKind.Fire, PrintSchedulePolicy.Decide(schedule.DueUtc, schedule, state).Kind);
        Assert.Equal(ScheduleDecisionKind.Fire, PrintSchedulePolicy.Decide(schedule.DueUtc.AddMinutes(5), schedule, state).Kind);
        Assert.Equal(ScheduleDecisionKind.Missed, PrintSchedulePolicy.Decide(schedule.DueUtc.AddMinutes(5).AddSeconds(1), schedule, state).Kind);
    }

    [Fact]
    public void Identity_and_connection_changes_cancel_without_rearming()
    {
        var schedule = Schedule();
        var state = PrintConsoleReducer.StartScheduled(Ready(), schedule);
        Assert.Equal("connection lost", PrintSchedulePolicy.CancelReason(schedule,
            state.WithConnection(ConsoleConnection.Reconnecting)));
        Assert.Equal("connection changed", PrintSchedulePolicy.CancelReason(schedule,
            state.WithConnectionGeneration(8)));
        Assert.Equal("machine identity changed", PrintSchedulePolicy.CancelReason(schedule,
            state.WithIdentity("session-1", "other-machine", null)));

        var folded = PrintConsoleReducer.Reduce(state, new ConsoleFacts
        {
            Connection = ConsoleConnection.Connected,
            ConnectionGeneration = 7,
            SessionId = "session-1",
            ExpectedFingerprint = "machine-1",
        });
        Assert.Same(schedule, folded.ScheduledStart);
    }

    [Theory]
    [InlineData(MachineStatus.Busy, false)]          // the operator homes after scheduling
    [InlineData(MachineStatus.ChangingTool, false)]
    [InlineData(MachineStatus.Processing, true)]     // another job started
    [InlineData(MachineStatus.Simulating, true)]
    [InlineData(MachineStatus.Paused, true)]
    [InlineData(MachineStatus.Halted, true)]
    public void Armed_schedule_cancels_only_when_a_job_runs_or_the_machine_stops(MachineStatus status, bool cancels)
    {
        var schedule = Schedule();
        var state = PrintConsoleReducer.StartScheduled(Ready(), schedule).WithMachine(status, "x");
        Assert.Equal(cancels, PrintSchedulePolicy.CancelReason(schedule, state) != null);
    }

    [Fact]
    public void Firing_schedule_is_not_cancelled_by_its_own_start_transition()
    {
        var armed = PrintConsoleReducer.StartScheduled(Ready(), Schedule());
        Assert.True(PrintConsoleReducer.TryClaimStart(armed, 11, out var firing));
        var printing = firing.WithMachine(MachineStatus.Processing, "processing");
        Assert.Null(PrintSchedulePolicy.CancelReason(firing.ScheduledStart!, printing));
        var folded = PrintConsoleReducer.Reduce(printing, new ConsoleFacts
        {
            Connection = ConsoleConnection.Connected,
            ConnectionGeneration = 7,
            SessionId = "session-1",
            ExpectedFingerprint = "machine-1",
        });
        Assert.Equal(ScheduledStartPhase.Firing, folded.ScheduledStart!.Phase);
    }

    [Fact]
    public void Start_claim_is_idempotent_and_stale_ids_do_nothing()
    {
        var state = PrintConsoleReducer.StartScheduled(Ready(), Schedule());
        Assert.True(PrintConsoleReducer.TryClaimStart(state, 11, out var firing));
        Assert.Equal(ScheduledStartPhase.Firing, firing.ScheduledStart!.Phase);
        Assert.False(PrintConsoleReducer.TryClaimStart(firing, 11, out var again));
        Assert.Same(firing.ScheduledStart, again.ScheduledStart);
        Assert.False(PrintConsoleReducer.TryClaimStart(state, 99, out var stale));
        Assert.Same(state.ScheduledStart, stale.ScheduledStart);
    }

    [Fact]
    public void Successful_update_rearms_with_a_new_id_and_keeps_due_time()
    {
        var old = Schedule();
        var state = PrintConsoleReducer.ScheduleUpdating(
            PrintConsoleReducer.StartScheduled(Ready(), old));
        var updated = new ScheduledStart(12, old.DueUtc, T0.AddMinutes(1), "new-sha", "new-sha",
                                         old.RemotePath, false, old.Host, old.SessionId,
                                         old.ConnectionGeneration, old.Fingerprint);
        var rearmed = PrintConsoleReducer.ScheduleRearmed(state, updated);
        Assert.Equal(ScheduledStartPhase.Armed, rearmed.ScheduledStart!.Phase);
        Assert.Equal(12, rearmed.ScheduledStart.Id);
        Assert.Equal(old.DueUtc, rearmed.ScheduledStart.DueUtc);
        Assert.Equal("new-sha", rearmed.ScheduledStart.JobSha256);
    }

    [Fact]
    public void Update_refuses_firing_and_cancels_when_the_due_time_is_reached()
    {
        var old = Schedule();
        var firing = PrintConsoleReducer.TryClaimStart(
            PrintConsoleReducer.StartScheduled(Ready(), old), old.Id);
        Assert.Equal("scheduled start is firing", PrintSchedulePolicy.RefuseUpdate(T0, firing.ScheduledStart!, Ready()));

        var updating = PrintConsoleReducer.ScheduleUpdating(PrintConsoleReducer.StartScheduled(Ready(), old));
        Assert.Contains("due time", PrintSchedulePolicy.RefuseUpdate(old.DueUtc, updating.ScheduledStart!, Ready()));
        Assert.Null(PrintConsoleReducer.ScheduleUpdateFailed(updating, "upload failed").ScheduledStart);
    }
}
