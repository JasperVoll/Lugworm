using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the press-and-hold jog state machine. The invariant under test is
/// that at most one jog is ever in flight and that queue depth is structurally zero:
/// releasing the button leaves at most the single already-sent whitelisted step in
/// motion.</summary>
public class JogRepeatPolicyTests
{
    /// <summary>Press, authorize, start - the state a repeat decision is made from.</summary>
    static JogRepeatState Sent(long generationAtCompletion = 5, JogReadiness readiness = JogReadiness.Ready,
                               long arrived = 6)
    {
        var s = JogRepeatPolicy.Press(JogRepeatState.Empty);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
        s = JogRepeatPolicy.SendStarted(s);
        s = JogRepeatPolicy.SendCompleted(s, generationAtCompletion);
        return JogRepeatPolicy.PollObserved(s, arrived, readiness);
    }

    [Fact]
    public void First_press_sends_immediately()
    {
        var s = JogRepeatPolicy.Press(JogRepeatState.Empty);
        Assert.Equal(JogRepeatPhase.Ready, s.Phase);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
        Assert.True(s.AllowsMaintenanceOverride);
    }

    [Fact]
    public void Nothing_may_be_sent_while_a_transaction_is_in_flight()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        Assert.Equal(JogRepeatPhase.InFlight, s.Phase);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // A poll arriving mid-flight does not unlock a second send.
        s = JogRepeatPolicy.PollObserved(s, 99, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));
    }

    [Fact]
    public void A_release_and_fast_repress_cannot_slip_a_second_jog_past_the_invariant()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.Release(s);
        // The running transaction must still be allowed to finish and report.
        Assert.Equal(JogRepeatPhase.InFlight, s.Phase);

        s = JogRepeatPolicy.Press(s);
        Assert.Equal(JogRepeatPhase.InFlight, s.Phase);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));
    }

    [Fact]
    public void A_repeat_waits_for_a_poll_that_started_after_the_send_completed()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.SendCompleted(s, pollGenerationAtCompletion: 5);
        Assert.Equal(JogRepeatPhase.AwaitingAck, s.Phase);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // The cycle already in flight when the send completed does NOT acknowledge it:
        // the command path and the poll loop fetch snapshots independently, so that
        // snapshot may predate the send.
        s = JogRepeatPolicy.PollObserved(s, 5, JogReadiness.Ready);
        Assert.False(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // A later generation does.
        s = JogRepeatPolicy.PollObserved(s, 6, JogReadiness.Ready);
        Assert.True(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
    }

    /// <summary>The planner gates on Idle, so the acknowledgement poll after a long step
    /// routinely reports the machine still executing that very jog. Treating that as a
    /// refusal would end every hold after one step.</summary>
    [Fact]
    public void Busy_on_the_acknowledgement_poll_waits_instead_of_terminating()
    {
        var s = Sent(readiness: JogReadiness.Busy);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        s = JogRepeatPolicy.PollObserved(s, 7, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
    }

    [Fact]
    public void A_gate_that_waiting_cannot_clear_terminates_the_repeat()
    {
        Assert.Equal(JogRepeatDecision.Terminate,
            JogRepeatPolicy.Decide(Sent(readiness: JogReadiness.Blocked)));
    }

    [Fact]
    public void Any_refusal_terminates_the_repeat_rather_than_retrying_into_a_gate()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.SendRefused(s, "jog refused - envelope unknown");
        Assert.Equal(JogRepeatPhase.Terminated, s.Phase);
        Assert.Equal(JogRepeatDecision.Terminate, JogRepeatPolicy.Decide(s));
        Assert.Equal("jog refused - envelope unknown", s.TerminationReason);

        // Only a new discrete press restarts it.
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(JogRepeatPolicy.Press(s)));
    }

    [Fact]
    public void Releasing_stops_the_repeat_and_a_completed_send_does_not_revive_it()
    {
        var s = Sent();
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));

        s = JogRepeatPolicy.Release(s);
        Assert.Equal(JogRepeatDecision.Terminate, JogRepeatPolicy.Decide(s));

        s = JogRepeatPolicy.PollObserved(s, 20, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Terminate, JogRepeatPolicy.Decide(s));
    }

    [Fact]
    public void A_send_completing_after_release_terminates_rather_than_awaiting_ack()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.Release(s);
        s = JogRepeatPolicy.SendCompleted(s, 5);
        Assert.Equal(JogRepeatPhase.Terminated, s.Phase);
        Assert.Equal("released", s.TerminationReason);
    }

    /// <summary>The paused-jog override is armed as a side effect of the refusal itself,
    /// so a held button must lose the right to it BEFORE the second step is sent -
    /// terminating after the refusal comes back would be too late.</summary>
    [Fact]
    public void An_auto_repeat_may_never_arm_or_consume_the_maintenance_override()
    {
        var s = JogRepeatPolicy.Press(JogRepeatState.Empty);
        Assert.True(s.AllowsMaintenanceOverride);

        s = JogRepeatPolicy.SendStarted(s);
        Assert.False(s.AllowsMaintenanceOverride);

        s = JogRepeatPolicy.SendCompleted(s, 5);
        s = JogRepeatPolicy.PollObserved(s, 6, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
        Assert.False(s.AllowsMaintenanceOverride);

        // A fresh discrete press restores it.
        Assert.True(JogRepeatPolicy.Press(JogRepeatPolicy.Release(s)).AllowsMaintenanceOverride);
    }

    [Fact]
    public void Paused_is_only_ready_behind_an_active_maintenance_override()
    {
        Assert.Equal(JogReadiness.Blocked, JogRepeatPolicy.ReadinessOf(MachineStatus.Paused));
        Assert.Equal(JogReadiness.Ready,
            JogRepeatPolicy.ReadinessOf(MachineStatus.Paused, maintenanceOverrideActive: true));
    }

    [Theory]
    [InlineData(MachineStatus.Idle, JogReadiness.Ready)]
    [InlineData(MachineStatus.Busy, JogReadiness.Busy)]
    [InlineData(MachineStatus.Processing, JogReadiness.Busy)]
    [InlineData(MachineStatus.Halted, JogReadiness.Blocked)]
    [InlineData(MachineStatus.Off, JogReadiness.Blocked)]
    [InlineData(MachineStatus.Disconnected, JogReadiness.Blocked)]
    [InlineData(MachineStatus.Unknown, JogReadiness.Blocked)]
    public void Readiness_mapping(MachineStatus status, JogReadiness expected)
        => Assert.Equal(expected, JogRepeatPolicy.ReadinessOf(status));

    [Fact]
    public void Out_of_order_or_duplicate_poll_arrivals_are_ignored()
    {
        var s = JogRepeatPolicy.PollObserved(JogRepeatState.Empty, 10, JogReadiness.Ready);
        s = JogRepeatPolicy.PollObserved(s, 9, JogReadiness.Blocked);
        Assert.Equal(10, s.LatestGeneration);
        Assert.Equal(JogReadiness.Ready, s.LatestReadiness);

        s = JogRepeatPolicy.PollObserved(s, 10, JogReadiness.Blocked);
        Assert.Equal(JogReadiness.Ready, s.LatestReadiness);
    }

    /// <summary>Codex code review, phase 1-2: SendCompleted -> Release -> Press -> Decide
    /// must NOT authorize another jog before the post-send poll. The previous step may
    /// still be executing, and the planner gates on Idle, so the send would be refused and
    /// would kill the fresh hold.</summary>
    [Fact]
    public void Release_then_repress_still_owes_the_outstanding_acknowledgement()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.SendCompleted(s, pollGenerationAtCompletion: 5);
        s = JogRepeatPolicy.Release(s);
        s = JogRepeatPolicy.Press(s);

        Assert.Equal(JogRepeatPhase.Ready, s.Phase);
        Assert.False(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // The poll that was already running when the send completed still does not count.
        s = JogRepeatPolicy.PollObserved(s, 5, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // A later one does, and the fresh press has its maintenance-override right back.
        s = JogRepeatPolicy.PollObserved(s, 6, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
        Assert.True(s.AllowsMaintenanceOverride);
    }

    /// <summary>A re-press while the acknowledgement poll reports the machine still
    /// executing waits rather than sending into a certain Idle refusal.</summary>
    [Fact]
    public void Release_then_repress_waits_while_the_machine_is_still_busy()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));
        s = JogRepeatPolicy.SendCompleted(s, 5);
        s = JogRepeatPolicy.PollObserved(s, 6, JogReadiness.Busy);
        s = JogRepeatPolicy.Press(JogRepeatPolicy.Release(s));

        Assert.True(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        Assert.Equal(JogRepeatDecision.Send,
            JogRepeatPolicy.Decide(JogRepeatPolicy.PollObserved(s, 7, JogReadiness.Ready)));
    }

    /// <summary>A first-ever press owes nothing - the policy must not deadlock before any
    /// poll has arrived.</summary>
    [Fact]
    public void A_first_press_sends_without_waiting_for_a_poll()
    {
        var s = JogRepeatPolicy.Press(JogRepeatState.Empty);
        Assert.True(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
    }

    /// <summary>The poll notification and the transaction outcome are marshalled to the UI
    /// thread independently, so a poll can be applied BEFORE the send outcome. The hold must
    /// not wedge: the acknowledgement is recorded against the generation current when
    /// SendCompleted is applied, so a later poll still releases the next step.</summary>
    [Fact]
    public void A_poll_applied_before_the_send_outcome_does_not_wedge_the_hold()
    {
        var s = JogRepeatPolicy.SendStarted(JogRepeatPolicy.Press(JogRepeatState.Empty));

        // The poll lands first, while the transaction is still recorded as in flight.
        s = JogRepeatPolicy.PollObserved(s, 6, JogReadiness.Ready);
        Assert.Equal(JogRepeatPhase.InFlight, s.Phase);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // Then the outcome, carrying the generation current at that moment.
        s = JogRepeatPolicy.SendCompleted(s, pollGenerationAtCompletion: 6);
        Assert.Equal(JogRepeatPhase.AwaitingAck, s.Phase);
        Assert.False(s.Acknowledged);
        Assert.Equal(JogRepeatDecision.Wait, JogRepeatPolicy.Decide(s));

        // The next poll releases it - the hold resumes rather than stalling.
        s = JogRepeatPolicy.PollObserved(s, 7, JogReadiness.Ready);
        Assert.Equal(JogRepeatDecision.Send, JogRepeatPolicy.Decide(s));
    }

    [Fact]
    public void An_untouched_state_is_terminal()
        => Assert.Equal(JogRepeatDecision.Terminate, JogRepeatPolicy.Decide(JogRepeatState.Empty));
}
