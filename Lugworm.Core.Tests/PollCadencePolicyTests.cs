using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the focused fast-poll decision and the rule that an early wake is
/// never a shutdown - the poll loop leaves on a cancelled delay, so confusing the two
/// would turn every "poll now" into a disconnect.</summary>
public class PollCadencePolicyTests
{
    [Fact]
    public void Fast_cadence_needs_both_focus_and_an_active_machine()
    {
        Assert.Equal(250, PollCadencePolicy.Decide(anyOwnerFocused: true, machineActive: true, configuredPollMs: 1000));
        Assert.Equal(1000, PollCadencePolicy.Decide(anyOwnerFocused: true, machineActive: false, configuredPollMs: 1000));
        Assert.Equal(1000, PollCadencePolicy.Decide(anyOwnerFocused: false, machineActive: true, configuredPollMs: 1000));
        Assert.Equal(1000, PollCadencePolicy.Decide(anyOwnerFocused: false, machineActive: false, configuredPollMs: 1000));
    }

    /// <summary>The policy may only ever poll SOONER than configured, never later.</summary>
    [Fact]
    public void A_configured_interval_faster_than_the_fast_rate_is_never_slowed_down()
    {
        Assert.Equal(250, PollCadencePolicy.Decide(true, true, 250));
        Assert.Equal(250, PollCadencePolicy.Decide(true, false, 250));

        // The session clamps to 250..2000, but a caller passing a smaller value must not
        // be pushed back up to 250.
        Assert.Equal(100, PollCadencePolicy.Decide(true, true, 100));
    }

    [Fact]
    public void A_nonpositive_interval_falls_back_to_the_fast_rate()
    {
        Assert.Equal(250, PollCadencePolicy.Decide(false, false, 0));
        Assert.Equal(250, PollCadencePolicy.Decide(false, false, -1));
    }

    [Theory]
    [InlineData(MachineStatus.Processing, true)]
    [InlineData(MachineStatus.Simulating, true)]
    [InlineData(MachineStatus.Busy, true)]
    [InlineData(MachineStatus.Pausing, true)]
    [InlineData(MachineStatus.Paused, true)]
    [InlineData(MachineStatus.Resuming, true)]
    [InlineData(MachineStatus.Cancelling, true)]
    [InlineData(MachineStatus.ChangingTool, true)]
    [InlineData(MachineStatus.Idle, false)]
    [InlineData(MachineStatus.Off, false)]
    [InlineData(MachineStatus.Halted, false)]
    [InlineData(MachineStatus.Unknown, false)]
    [InlineData(MachineStatus.Disconnected, false)]
    public void Active_states(MachineStatus status, bool active)
        => Assert.Equal(active, PollCadencePolicy.IsActive(status));

    [Fact]
    public void An_early_wake_is_not_a_shutdown()
    {
        Assert.Equal(PollWaitOutcome.WokenEarly,
            PollCadencePolicy.Interpret(wakeSignalled: true, cancellationRequested: false));
        Assert.Equal(PollWaitOutcome.CadenceElapsed,
            PollCadencePolicy.Interpret(wakeSignalled: false, cancellationRequested: false));
    }

    /// <summary>A wake racing a disconnect must still stop the loop.</summary>
    [Fact]
    public void Shutdown_wins_over_a_wake()
    {
        Assert.Equal(PollWaitOutcome.Cancelled,
            PollCadencePolicy.Interpret(wakeSignalled: true, cancellationRequested: true));
        Assert.Equal(PollWaitOutcome.Cancelled,
            PollCadencePolicy.Interpret(wakeSignalled: false, cancellationRequested: true));
    }

    /// <summary>The configured interval is a target START-TO-START period. Before this,
    /// 300 ms meant "idle 300 ms after however long the poll took", so a 200 ms cycle
    /// produced a 500 ms observation spacing and the operator's number described nothing
    /// they could see.</summary>
    [Fact]
    public void The_interval_is_spent_start_to_start_not_added_to_the_work()
    {
        Assert.Equal(300, PollCadencePolicy.WaitAfterWork(300, 0));
        Assert.Equal(100, PollCadencePolicy.WaitAfterWork(300, 200));
        Assert.Equal(150, PollCadencePolicy.WaitAfterWork(300, 149.6));
    }

    /// <summary>A cycle whose work already overran the target still yields: the loop must
    /// observe cancellation and must not spin straight into the next request.</summary>
    [Fact]
    public void An_overrunning_cycle_still_waits_the_floor()
    {
        Assert.Equal(PollCadencePolicy.MinIdleWaitMs, PollCadencePolicy.WaitAfterWork(300, 300));
        Assert.Equal(PollCadencePolicy.MinIdleWaitMs, PollCadencePolicy.WaitAfterWork(300, 4000));
        Assert.Equal(PollCadencePolicy.MinIdleWaitMs, PollCadencePolicy.WaitAfterWork(300, 285));
        // an unreadable elapsed time is treated as "no work done", never as a negative wait
        Assert.Equal(300, PollCadencePolicy.WaitAfterWork(300, double.NaN));
        Assert.Equal(300, PollCadencePolicy.WaitAfterWork(300, -5));
    }

    /// <summary>The wait is never longer than the target, and a target below the floor
    /// collapses to the target rather than stretching it.</summary>
    [Fact]
    public void The_wait_is_bounded_by_the_target()
    {
        Assert.Equal(10, PollCadencePolicy.WaitAfterWork(10, 50));
        Assert.Equal(PollCadencePolicy.MinIdleWaitMs, PollCadencePolicy.WaitAfterWork(0, 0));
        for (int work = 0; work <= 400; work += 7)
        {
            int wait = PollCadencePolicy.WaitAfterWork(300, work);
            Assert.InRange(wait, PollCadencePolicy.MinIdleWaitMs, 300);
        }
    }
}
