using System;
using System.Collections.Generic;
using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class PauseScheduleTests
{
    static readonly DateTime T0 = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    static JobSnapshot Run()
    {
        string text = "G21\nG90\nM83\nG0 X0 Y0 Z0 F600\n; layer 1/3\nG1 X10 Y0 Z0 F600 E1\n; layer 2/3\nG1 X20 Y0 Z0 F600 E1\n; layer 3/3\nG1 X30 Y0 Z0 F600 E1\n";
        return JobSnapshot.FromBytes(Encoding.UTF8.GetBytes(text), "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
    }

    static PauseScheduleFacts Facts(JobSnapshot run, MachineStatus status = MachineStatus.Processing,
                                    int currentLayer = 0, IReadOnlyList<ScheduledPause>? pauses = null)
        => new PauseScheduleFacts
        {
            Status = status,
            Run = run,
            RunId = run.RunId,
            CurrentLayer = currentLayer,
            Pauses = pauses ?? Array.Empty<ScheduledPause>(),
        };

    [Fact]
    public void Layer_add_refuses_missing_duplicate_and_out_of_range_layers()
    {
        var run = Run();
        var first = ScheduledPause.AtLayer(1, 2, run.RunId, T0);
        Assert.Null(PauseSchedulePolicy.RefuseAdd(PauseKind.AtLayer, 2, T0, Facts(run)));
        Assert.Equal("pause layer must be after the current layer",
            PauseSchedulePolicy.RefuseAdd(PauseKind.AtLayer, 2, T0, Facts(run, currentLayer: 2)));
        Assert.Equal("pause layer is beyond the job total",
            PauseSchedulePolicy.RefuseAdd(PauseKind.AtLayer, 4, T0, Facts(run)));
        Assert.Equal("that layer pause already exists",
            PauseSchedulePolicy.RefuseAdd(PauseKind.AtLayer, 2, T0, Facts(run, pauses: new[] { first })));
    }

    [Fact]
    public void Adding_beyond_the_pending_limit_is_refused()
    {
        var run = Run();
        var full = new List<ScheduledPause>();
        for (int i = 0; i < PauseSchedulePolicy.MaxPending; i++)
            full.Add(ScheduledPause.AtTime(100 + i, T0.AddMinutes(10 + i), run.RunId, T0));
        Assert.NotNull(PauseSchedulePolicy.RefuseAdd(PauseKind.AtTime, T0.AddHours(1), T0, Facts(run, pauses: full)));
        full.RemoveAt(0);
        Assert.Null(PauseSchedulePolicy.RefuseAdd(PauseKind.AtTime, T0.AddHours(1), T0, Facts(run, pauses: full)));
    }

    [Fact]
    public void Layer_evaluation_claims_one_item_and_barrier_waits_for_resume()
    {
        var run = Run();
        var first = ScheduledPause.AtLayer(1, 1, run.RunId, T0);
        var second = ScheduledPause.AtLayer(2, 2, run.RunId, T0);
        var due = PauseSchedulePolicy.Evaluate(new[] { first, second }, run.RunId, run,
                                               MachineStatus.Processing, run.LayerMarkers.OffsetOf(2), T0);
        Assert.Same(first, due.ToSend);

        var claimed = PauseSchedulePolicy.Evaluate(new PauseScheduleState(
            new[] { first.WithPhase(ScheduledPausePhase.Sending), second },
            PauseSchedulerPhase.AwaitingPause, T0), run.RunId, run,
            MachineStatus.Processing, run.LayerMarkers.OffsetOf(2), T0.AddSeconds(1));
        Assert.Null(claimed.ToSend);

        var observed = PauseSchedulePolicy.Evaluate(new PauseScheduleState(
            new[] { second }, PauseSchedulerPhase.AwaitingPause, T0), run.RunId, run,
            MachineStatus.Paused, run.LayerMarkers.OffsetOf(2), T0.AddSeconds(2));
        Assert.Equal(PauseSchedulerPhase.AwaitingResume, observed.Phase);
        var resumed = PauseSchedulePolicy.Evaluate(new PauseScheduleState(
            new[] { second }, observed.Phase, T0.AddSeconds(2)), run.RunId, run,
            MachineStatus.Processing, run.LayerMarkers.OffsetOf(2), T0.AddSeconds(3));
        Assert.Equal(PauseSchedulerPhase.Ready, resumed.Phase);
        Assert.Same(second, PauseSchedulePolicy.Evaluate(new[] { second }, run.RunId, run,
            MachineStatus.Processing, run.LayerMarkers.OffsetOf(2), T0.AddSeconds(3)).ToSend);
    }

    [Fact]
    public void Accepted_pause_timeout_returns_to_ready_without_restoring_the_item()
    {
        var run = Run();
        var state = new PauseScheduleState(Array.Empty<ScheduledPause>(),
                                           PauseSchedulerPhase.AwaitingPause, T0);
        var result = PauseSchedulePolicy.Evaluate(state, run.RunId, run, MachineStatus.Processing,
                                                  run.LayerMarkers.OffsetOf(1), T0.AddSeconds(10));
        Assert.Equal(PauseSchedulerPhase.Ready, result.Phase);
        Assert.Contains("not observed", result.Notes);
    }

    [Theory]
    [InlineData(MachineStatus.Paused)]
    [InlineData(MachineStatus.Pausing)]
    public void Timed_pause_due_while_paused_is_consumed_without_a_second_send(MachineStatus status)
    {
        var run = Run();
        var pause = ScheduledPause.AtTime(4, T0.AddMinutes(1), run.RunId, T0);
        var result = PauseSchedulePolicy.Evaluate(new[] { pause }, run.RunId, run,
                                                  status, 0, T0.AddMinutes(2));
        Assert.Null(result.ToSend);
        Assert.Same(pause, result.ConsumedWithoutSend);
        Assert.Equal("already paused", result.Notes);
    }

    [Fact]
    public void Layer_crossings_keep_only_fully_observed_uninterrupted_intervals()
    {
        var c = new LayerCrossings("run");
        c.Observe(2, MachineStatus.Processing, false, T0);                  // attached mid-layer 2
        c.Observe(3, MachineStatus.Processing, false, T0.AddSeconds(10));   // start of 3 unobserved before: no interval
        c.Observe(3, MachineStatus.Processing, false, T0.AddSeconds(15));
        c.Observe(4, MachineStatus.Processing, false, T0.AddSeconds(30));   // clean: layer 3 took 20 s
        c.Observe(4, MachineStatus.Paused, false, T0.AddSeconds(40));
        c.Observe(5, MachineStatus.Processing, false, T0.AddSeconds(90));   // paused inside: dropped
        c.Observe(5, MachineStatus.Processing, true, T0.AddSeconds(95));
        c.Observe(6, MachineStatus.Processing, false, T0.AddSeconds(120));  // reconnect inside: dropped
        c.Observe(8, MachineStatus.Processing, false, T0.AddSeconds(130));  // two markers in one poll: dropped
        c.Observe(9, MachineStatus.Processing, false, T0.AddSeconds(155));  // clean: 25 s
        Assert.Equal(new[] { (4, 20.0), (9, 25.0) }, c.CleanIntervals);
    }

    [Fact]
    public void Claiming_a_pause_succeeds_once_and_reconnect_keeps_the_run_identity()
    {
        var run = Run();
        var pause = ScheduledPause.AtTime(5, T0.AddMinutes(1), run.RunId, T0);
        Assert.True(PauseSchedulePolicy.TryClaimPause(new[] { pause }, 5, out var claimed));
        Assert.Equal(ScheduledPausePhase.Sending, claimed[0].Phase);
        Assert.False(PauseSchedulePolicy.TryClaimPause(claimed, 5, out _));
        var late = PauseSchedulePolicy.Evaluate(new[] { pause }, run.RunId, run,
                                                MachineStatus.Processing, 0, T0.AddMinutes(2));
        Assert.Same(pause, late.ToSend);
    }
}
