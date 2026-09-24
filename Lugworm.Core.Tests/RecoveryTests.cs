using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The 2026-09-09 dome incident, pinned. The operator pressed E-STOP, the
/// robot stopped, and afterwards Reset could not be pressed: it required connected +
/// Halted/Off + trusted + no pending action, and the emergency stop had just removed
/// every one of them. These tests cover the recovery facts, the one reset rule the
/// button / Terminal tab / transaction share, and what the transports can honestly
/// report about a reset send.
///
/// They prove software behaviour only. The separate physical proximal-axis stoppage
/// is not diagnosed here and no unit test can claim it is fixed.</summary>
public class RecoveryTests
{
    const string Host = "192.168.1.42";
    const string Session = "session-A";
    const string Print = "FAKE-BOARD-0001";
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 9, 55, 0, DateTimeKind.Utc);

    // ---- helpers ---------------------------------------------------------------

    static ResetContext Ctx(RecoveryState recovery, ConsoleConnection connection, MachineStatus status,
                            string? trust = null, string? pending = null,
                            string sessionId = Session, string fingerprint = Print,
                            string? conflict = null)
        => new ResetContext
        {
            Host = Host,
            SessionId = sessionId,
            ExpectedFingerprint = fingerprint,
            ExpectationConflict = conflict,
            Connection = connection,
            Status = status,
            TrustReason = trust,
            PendingAction = pending,
            Recovery = recovery,
        };

    static RecoveryState Stopped(DateTime? at = null, long generation = 7)
        => RecoveryReducer.StopRequested(RecoveryState.None, Host, Session, Print, generation, at ?? T0);

    static RecoveryObservation Poll(MachineStatus status, long generation, string? trust = null,
                                    ConsoleConnection connection = ConsoleConnection.Connected,
                                    bool allHomed = false)
        => new RecoveryObservation
        {
            Connection = connection,
            Status = status,
            ConnectionGeneration = generation,
            TrustReason = trust,
            AllHomed = allHomed,
        };

    // ---- (1) the facts stay separate -------------------------------------------

    [Fact]
    public void An_acknowledged_send_is_not_an_observed_halt_and_not_a_verified_reset()
    {
        var r = Stopped();
        Assert.True(r.StopRequested);
        Assert.False(r.StopAcknowledged);
        Assert.False(r.HaltObserved);
        // requested but neither answered nor failed: the send is still in flight, and
        // the notice must not claim a failure the operator would act on
        Assert.Null(r.Notice);

        r = RecoveryReducer.StopAcknowledged(r);
        Assert.True(r.StopAcknowledged);
        // The board answered. That is all it means - nothing here says the machine
        // physically stopped, and the console's notice says so in words.
        Assert.False(r.HaltObserved);
        Assert.Contains("no Halted/Off status has been observed", r.Notice);

        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Halted, 7));
        Assert.True(r.HaltObserved);
        Assert.False(r.ResetRequested);
        Assert.False(r.RecoveryVerified);
        Assert.Contains("press Reset", r.Notice);
    }

    [Fact]
    public void A_failed_stop_send_keeps_the_request_and_names_the_physical_switch()
    {
        var r = RecoveryReducer.StopFailed(Stopped(), "no route to host");
        Assert.True(r.StopRequested);
        Assert.False(r.StopAcknowledged);
        Assert.Equal("no route to host", r.StopFailure);
        Assert.Contains("physical switch", r.Notice);
        // The recovery path must still be reachable - a stop that could not be
        // delivered is exactly when the operator needs it.
        var d = ResetPolicy.Decide(Ctx(RecoveryReducer.TransportLost(r, "no route to host"),
                                       ConsoleConnection.Reconnecting, MachineStatus.Unknown), T0);
        Assert.True(d.Allowed);
        Assert.Equal(ResetPath.OperatorRecovery, d.Path);
    }

    // ---- (2) halt with live HTTP: the ordinary path is unchanged ----------------

    [Fact]
    public void Halted_machine_on_a_live_connection_takes_the_ordinary_path_without_arming()
    {
        var r = RecoveryReducer.Observed(RecoveryReducer.StopAcknowledged(Stopped()),
                                         Poll(MachineStatus.Halted, 7));
        var d = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Connected, MachineStatus.Halted), T0);
        Assert.True(d.Allowed);
        Assert.Equal(ResetPath.VerifiedHalt, d.Path);
        Assert.False(d.NeedsArming);
        Assert.Equal("Reset (M999)", d.Label);
    }

    [Fact]
    public void A_live_halted_machine_still_needs_machine_trust()
    {
        var d = ResetPolicy.Decide(Ctx(Stopped(), ConsoleConnection.Connected, MachineStatus.Halted,
                                       trust: "machine identity mismatch"), T0);
        Assert.False(d.Allowed);
        Assert.Equal("machine identity mismatch", d.Refusal);
    }

    // ---- (3) connection loss after E-Stop: the incident -------------------------

    [Fact]
    public void Connection_lost_after_this_sessions_estop_reaches_reset_by_arming_twice()
    {
        var r = RecoveryReducer.TransportLost(RecoveryReducer.StopAcknowledged(Stopped()),
                                              "The SSL connection could not be established");
        var ctx = Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Halted);

        var first = ResetPolicy.Decide(ctx, T0);
        Assert.True(first.Allowed);
        Assert.Equal(ResetPath.OperatorRecovery, first.Path);
        Assert.True(first.NeedsArming);
        Assert.Equal("Recover (M999)", first.Label);
        // The window must say what is actually wrong. Enabling a button cannot repair
        // a dead transport, and the operator has to know that before pressing again.
        Assert.Contains("is not answering", first.Notice);
        Assert.Contains("cannot repair a dead connection", first.Notice);

        r = RecoveryReducer.ArmOperatorRecovery(r, T0);
        var second = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Halted), T0.AddSeconds(2));
        Assert.True(second.Allowed);
        Assert.False(second.NeedsArming);
        Assert.Equal("Press again", second.Label);

        // and the arming expires rather than standing open
        var late = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Halted),
                                      T0.AddSeconds(RecoveryState.RecoveryArmSeconds + 1));
        Assert.True(late.NeedsArming);
    }

    [Fact]
    public void The_recovery_path_does_not_require_the_live_trust_probe()
    {
        // A reconnect sets trust back to Unknown, which is one of the gates that
        // locked the operator out. The binding recorded at stop time replaces it.
        var r = RecoveryReducer.ArmOperatorRecovery(
            RecoveryReducer.TransportLost(Stopped(), "connection interrupted"), T0);
        var d = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Unknown,
                                       trust: "machine identity could not be probed"), T0);
        Assert.True(d.Allowed);
        Assert.Equal(ResetPath.OperatorRecovery, d.Path);
    }

    [Theory]
    [InlineData("session-B", Print, "different session or host")]
    [InlineData(Session, "OTHER-BOARD", "expected machine identity changed")]
    public void The_recovery_path_keeps_the_host_and_identity_binding(string sessionId, string fingerprint,
                                                                      string expected)
    {
        var r = RecoveryReducer.TransportLost(Stopped(), "connection interrupted");
        var d = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Unknown,
                                       sessionId: sessionId, fingerprint: fingerprint), T0);
        Assert.False(d.Allowed);
        Assert.Contains(expected, d.Refusal);
    }

    [Fact]
    public void A_disconnected_host_without_an_estop_is_not_a_reset_target()
    {
        // No emergency stop of ours: an unreachable machine is just unreachable.
        var d = ResetPolicy.Decide(Ctx(RecoveryReducer.TransportLost(RecoveryState.None, "no route to host"),
                                       ConsoleConnection.Reconnecting, MachineStatus.Unknown), T0);
        Assert.False(d.Allowed);
        Assert.Contains("not connected", d.Refusal);

        // and neither is a session the operator has closed
        var closed = ResetPolicy.Decide(Ctx(RecoveryReducer.TransportLost(Stopped(), "no route to host"),
                                            ConsoleConnection.Disconnected, MachineStatus.Unknown), T0);
        Assert.False(closed.Allowed);
    }

    [Fact]
    public void An_identity_conflict_between_owners_blocks_every_path()
    {
        var r = RecoveryReducer.ArmOperatorRecovery(
            RecoveryReducer.TransportLost(Stopped(), "connection interrupted"), T0);
        var d = ResetPolicy.Decide(Ctx(r, ConsoleConnection.Reconnecting, MachineStatus.Unknown,
                                       conflict: "two components expect different machines on this host"), T0);
        Assert.False(d.Allowed);
        Assert.Contains("different machines", d.Refusal);
    }

    // ---- (4) busy queue ---------------------------------------------------------

    [Fact]
    public void A_pending_action_refuses_reset_by_naming_it()
    {
        var d = ResetPolicy.Decide(Ctx(Stopped(), ConsoleConnection.Connected, MachineStatus.Halted,
                                       pending: "uploading"), T0);
        Assert.False(d.Allowed);
        Assert.Equal("action in progress: uploading", d.Refusal);
    }

    // ---- (5) uncertain delivery -------------------------------------------------

    [Fact]
    public void An_unconfirmed_delivery_is_never_reported_as_a_completed_reset()
    {
        var r = RecoveryReducer.ResetSubmitted(
            RecoveryReducer.Observed(RecoveryReducer.StopAcknowledged(Stopped()), Poll(MachineStatus.Halted, 7)), 7);
        r = RecoveryReducer.ResetDelivered(r, ResetDelivery.Unconfirmed("the connection dropped before an answer arrived"));

        Assert.True(r.ResetRequested);
        Assert.True(r.ResetDeliveryUncertain);
        Assert.False(r.RecoveryVerified);
        Assert.True(r.HomingRequired);
        Assert.Contains("UNCONFIRMED", r.Notice);

        // The same poll generation coming back does NOT confirm it: the board would
        // have dropped its connection, so the old generation proves nothing.
        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 7));
        Assert.False(r.RecoveryVerified);
    }

    // ---- (6) reconnect confirms, and only then -----------------------------------

    [Fact]
    public void Recovery_is_verified_by_a_new_connection_generation_with_an_acceptable_identity()
    {
        var r = RecoveryReducer.ResetDelivered(
            RecoveryReducer.ResetSubmitted(RecoveryReducer.StopAcknowledged(Stopped()), 7),
            ResetDelivery.Unconfirmed("no answer"));

        // a newer generation whose identity is still refused proves nothing
        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 8, trust: "machine identity mismatch"));
        Assert.False(r.RecoveryVerified);

        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 8));
        Assert.True(r.RecoveryVerified);
        Assert.False(r.ResetDeliveryUncertain);
        // homing is still required: coming back is not the same as being homed
        Assert.True(r.HomingRequired);
        Assert.Contains("home before any motion", r.Notice);

        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 8, allHomed: true));
        Assert.False(r.HomingRequired);
        Assert.Null(r.Notice);
    }

    [Fact]
    public void An_estop_episode_closes_once_the_machine_answers_normally_again()
    {
        // Otherwise one emergency stop would authorise a blind reset for the rest of
        // the session, every time the network later hiccuped.
        var r = RecoveryReducer.Observed(RecoveryReducer.StopAcknowledged(Stopped(generation: 7)),
                                         Poll(MachineStatus.Halted, 7));
        Assert.True(r.StopRequested);
        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 8));
        Assert.False(r.StopRequested);
        Assert.True(r.IsQuiet);

        var later = ResetPolicy.Decide(Ctx(RecoveryReducer.TransportLost(r, "connection interrupted"),
                                           ConsoleConnection.Reconnecting, MachineStatus.Unknown), T0);
        Assert.False(later.Allowed);
    }

    [Fact]
    public void An_observation_clears_the_transport_fault_it_recorded()
    {
        var r = RecoveryReducer.TransportLost(Stopped(), "connection interrupted");
        Assert.True(r.TransportUnavailable);
        r = RecoveryReducer.Observed(r, Poll(MachineStatus.Halted, 8));
        Assert.False(r.TransportUnavailable);
        Assert.Null(r.TransportReason);
    }

    // ---- (7) ordinary printing can never reach Reset ------------------------------

    [Theory]
    [InlineData(MachineStatus.Processing)]
    [InlineData(MachineStatus.Simulating)]
    [InlineData(MachineStatus.Busy)]
    [InlineData(MachineStatus.Paused)]
    [InlineData(MachineStatus.Idle)]
    public void A_live_machine_that_is_not_halted_can_never_reach_reset(MachineStatus status)
    {
        foreach (var recovery in new[]
                 {
                     RecoveryState.None,
                     Stopped(),
                     RecoveryReducer.ArmOperatorRecovery(Stopped(), T0),   // armed, but the machine answers
                 })
        {
            var d = ResetPolicy.Decide(Ctx(recovery, ConsoleConnection.Connected, status), T0);
            Assert.False(d.Allowed);
            Assert.Contains("machine not halted", d.Refusal);
        }
    }

    // ---- (8) the console projection and the button --------------------------------

    static PrintConsoleState Console(RecoveryState recovery, ConsoleConnection connection,
                                     MachineStatus status, string? trust = null)
    {
        var s = PrintConsoleState.Empty(Host);
        s = PrintConsoleReducer.Reduce(s, new ConsoleFacts
        {
            Connection = connection,
            Snapshot = new ObjectModelSnapshot
            {
                Status = status,
                RawStatus = status.ToString().ToLowerInvariant(),
                TakenUtc = T0,
                AllHomed = true,
                Axes = new[]
                {
                    new AxisState { Letter = "X", MachinePosition = 1, Homed = true },
                    new AxisState { Letter = "Y", MachinePosition = 2, Homed = true },
                    new AxisState { Letter = "Z", MachinePosition = 3, Homed = true },
                },
            },
            ArmConfigurationKnown = true,
            TrustReason = trust,
            Recovery = recovery,
            SessionId = Session,
            ExpectedFingerprint = Print,
        });
        return s;
    }

    [Fact]
    public void The_button_offers_reset_after_an_estop_whose_transport_died()
    {
        var r = RecoveryReducer.TransportLost(RecoveryReducer.StopAcknowledged(Stopped()), "connection interrupted");
        var s = Console(r, ConsoleConnection.Reconnecting, MachineStatus.Halted, trust: "machine identity could not be probed");
        var b = PrintConsoleButtons.Enabled(s, hasHost: true, hasGcode: true, hasPoint: true, gotoSupported: true,
                                            nowUtc: T0);
        Assert.True(b.Reset);
        // and nothing else is: a dead transport is not a licence to move the machine
        Assert.False(b.Start); Assert.False(b.Jog); Assert.False(b.Home); Assert.False(b.Resume);
        Assert.True(b.EStop);
        Assert.Contains("is not answering", s.RecoveryNotice);
        // the dashboard's one primary action stops saying "Reconnecting..." and says Reset
        Assert.Equal(("reset", "Reset (M999)", true), ConsoleNextAction.Decide(s, b));
    }

    [Fact]
    public void A_printing_console_shows_no_reset_and_says_why()
    {
        var s = Console(RecoveryState.None, ConsoleConnection.Connected, MachineStatus.Processing);
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true, nowUtc: T0);
        Assert.False(b.Reset);
        Assert.Contains("machine not halted", b.Reasons["reset"]);
        Assert.True(b.Pause);
        Assert.True(b.Stop);
    }

    [Fact]
    public void Nothing_moves_and_no_job_resumes_until_the_reset_is_confirmed_and_homed()
    {
        var r = RecoveryReducer.ResetDelivered(
            RecoveryReducer.ResetSubmitted(RecoveryReducer.StopAcknowledged(Stopped()), 7),
            ResetDelivery.Unconfirmed("no answer"));
        var s = Console(r, ConsoleConnection.Connected, MachineStatus.Paused);
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true, nowUtc: T0);
        Assert.False(b.Resume);
        Assert.False(b.Start);
        Assert.False(b.Jog);
        Assert.False(b.GoTo);
        Assert.Contains("firmware reset is not confirmed", b.Reasons["resume"]);
        // homing is the recovery path out of an unknown configuration: the recovery
        // block never touches it (this machine is Paused, so only that refuses)
        Assert.DoesNotContain("firmware reset", b.Reasons["home"]);

        // confirmed by a new connection, but the machine is not homed yet
        var back = PrintConsoleReducer.RecoveryChanged(s,
            RecoveryReducer.Observed(r, Poll(MachineStatus.Paused, 8)));
        var bb = PrintConsoleButtons.Enabled(back, true, true, true, true, nowUtc: T0);
        Assert.False(bb.Resume);
        Assert.Contains("verified homing is required", bb.Reasons["resume"]);

        // and on an idle machine after the reset, Home is exactly what is offered
        var idle = PrintConsoleReducer.RecoveryChanged(
            Console(r, ConsoleConnection.Connected, MachineStatus.Idle),
            RecoveryReducer.Observed(r, Poll(MachineStatus.Idle, 8)));
        var bi = PrintConsoleButtons.Enabled(idle, true, true, true, true, nowUtc: T0);
        Assert.True(bi.Home);
        Assert.False(bi.Start);
        Assert.False(bi.Jog);
        Assert.Contains("verified homing is required", bi.Reasons["start"]);
    }

    [Fact]
    public void The_recovery_facts_reach_the_info_output_only_while_something_is_happening()
    {
        var quiet = Console(RecoveryState.None, ConsoleConnection.Connected, MachineStatus.Idle);
        Assert.DoesNotContain("recovery:", PrintConsoleOutputs.From(quiet).Info);

        var halted = RecoveryReducer.Observed(RecoveryReducer.StopAcknowledged(Stopped()), Poll(MachineStatus.Halted, 7));
        var loud = Console(RecoveryReducer.TransportLost(halted, "connection interrupted"),
                           ConsoleConnection.Reconnecting, MachineStatus.Halted);
        string info = PrintConsoleOutputs.From(loud).Info;
        Assert.Contains("recovery: e-stop yes, acknowledged yes, halt observed yes", info);
        Assert.Contains("transport unavailable", info);
        Assert.Contains("recovery note:", info);
    }

    // ---- (9) stale completion callbacks ------------------------------------------

    [Fact]
    public void A_completion_the_emergency_stop_superseded_cannot_clear_the_newer_pending_state()
    {
        var s = Console(RecoveryState.None, ConsoleConnection.Connected, MachineStatus.Processing);
        var jogging = PrintConsoleReducer.ActionStarted(s, "jogging");
        long jogEpoch = jogging.ActionEpoch;

        // E-STOP starts its own action while the jog is still in flight
        var stopping = PrintConsoleReducer.ActionStarted(jogging, "emergency stop (M112)");
        long stopEpoch = stopping.ActionEpoch;
        Assert.NotEqual(jogEpoch, stopEpoch);

        // the jog's completion lands late: it must change nothing at all
        var afterStale = PrintConsoleReducer.ActionFinished(stopping, "jog sent to (1, 2, 3)", null, false, jogEpoch);
        Assert.Same(stopping, afterStale);
        Assert.Equal("emergency stop (M112)", afterStale.PendingAction);
        Assert.Equal("", afterStale.LastResult);

        // the stop's own completion still lands
        var done = PrintConsoleReducer.ActionFinished(afterStale, "M112 acknowledged by the board", null, false, stopEpoch);
        Assert.Null(done.PendingAction);
        Assert.Contains("acknowledged", done.LastResult);
    }

    [Fact]
    public void A_stale_failure_cannot_overwrite_a_newer_actions_error_either()
    {
        var s = PrintConsoleReducer.ActionStarted(Console(RecoveryState.None, ConsoleConnection.Connected, MachineStatus.Idle), "uploading");
        long uploadEpoch = s.ActionEpoch;
        var reset = PrintConsoleReducer.ActionStarted(s, "reset (M999)");
        var stale = PrintConsoleReducer.ActionFinished(reset, null, "upload timed out", false, uploadEpoch);
        Assert.Same(reset, stale);
        Assert.Null(stale.LastError);
        // a caller with no action identity is still applied unconditionally
        var forced = PrintConsoleReducer.ActionFinished(reset, "done", null);
        Assert.Null(forced.PendingAction);
    }

    // ---- (10) terminal parity ------------------------------------------------------

    [Fact]
    public void Terminal_M999_follows_exactly_the_button_rule()
    {
        // printing: the button refuses, so the tab refuses with the same words
        var printing = Ctx(RecoveryState.None, ConsoleConnection.Connected, MachineStatus.Processing);
        var printingDecision = ResetPolicy.Decide(printing, T0);
        var refused = TerminalPolicy.Decide("M999", DateTime.MinValue, T0, printingDecision.Allowed,
                                            "reset (M999) refused - " + printingDecision.Refusal);
        Assert.Equal(TerminalSendDecision.Refuse, refused.Decision);
        Assert.Contains("machine not halted", refused.Reason);

        // halted: the button allows, so the tab allows - behind its own confirm arming
        var haltedDecision = ResetPolicy.Decide(Ctx(Stopped(), ConsoleConnection.Connected, MachineStatus.Halted), T0);
        var arm = TerminalPolicy.Decide("M999", DateTime.MinValue, T0, haltedDecision.Allowed);
        Assert.Equal(TerminalSendDecision.Arm, arm.Decision);
        var send = TerminalPolicy.Decide("M999", TerminalPolicy.ArmUntil(T0), T0.AddSeconds(1), haltedDecision.Allowed);
        Assert.Equal(TerminalSendDecision.Send, send.Decision);
        Assert.True(TerminalPolicy.IsReset(send.Line));

        // the recovery path opens the tab exactly when it opens the button
        var recovery = ResetPolicy.Decide(
            Ctx(RecoveryReducer.TransportLost(Stopped(), "connection interrupted"),
                ConsoleConnection.Reconnecting, MachineStatus.Unknown), T0);
        Assert.True(recovery.Allowed);
        Assert.Equal(TerminalSendDecision.Arm,
                     TerminalPolicy.Decide("M999", DateTime.MinValue, T0, recovery.Allowed).Decision);
    }

    // ---- (11) what the transports may report about a reset -------------------------

    const string ConnectOk =
        "{\"err\":0,\"sessionTimeout\":8000,\"boardType\":\"duetwifi102\",\"sessionKey\":12345,\"apiLevel\":1}";
    const string StateHalted = "{\"key\":\"state\",\"flags\":\"vn\",\"result\":{\"status\":\"halted\"}}";

    static FakeDuet HaltedBoard()
    {
        var f = new FakeDuet();
        f.On("rr_connect", FakeDuet.Json(ConnectOk));
        f.On("rr_model", FakeDuet.Json(StateHalted));
        return f;
    }

    static DuetPollClient Duet(FakeDuet fake) => new DuetPollClient(new DuetClientOptions
    {
        Host = Host,
        Handler = fake,
        RetryBaseDelayMs = 10,
    });

    [Fact]
    public async Task Duet_reset_on_a_live_board_is_acknowledged()
    {
        var fake = HaltedBoard();
        fake.On("rr_gcode", FakeDuet.Json("{\"buff\":240}"));
        using var c = Duet(fake);
        await c.ConnectAsync(CancellationToken.None);

        var delivery = await c.ResetAsync(CancellationToken.None);

        Assert.True(delivery.Acknowledged);
        Assert.False(delivery.Uncertain);
        Assert.Contains(fake.Requests, r => r.Uri.Contains("M999"));
        Assert.False(c.IsConnected);   // the session dies with the reset
    }

    [Fact]
    public async Task Duet_reset_whose_answer_never_arrives_is_uncertain_not_success()
    {
        // This is the bug: the old code caught every HttpRequestException and returned
        // as if the reset had happened. "The board resets before answering" and "the
        // endpoint is gone" are the SAME exception - neither is an observation.
        var fake = HaltedBoard();
        fake.On("rr_gcode", FakeDuet.Throw());
        using var c = Duet(fake);
        await c.ConnectAsync(CancellationToken.None);

        var delivery = await c.ResetAsync(CancellationToken.None);

        Assert.False(delivery.Acknowledged);
        Assert.True(delivery.Uncertain);
        Assert.Contains("before an answer arrived", delivery.Detail);
    }

    [Fact]
    public async Task Duet_reset_answered_by_an_expired_session_twice_is_a_refusal_not_a_reset()
    {
        var fake = HaltedBoard();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.Unauthorized),    // session expired
            FakeDuet.Status(HttpStatusCode.Unauthorized));   // and the single re-auth did not help
        using var c = Duet(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(() => c.ResetAsync(CancellationToken.None));

        // The endpoint answered. That is a refusal to be shown, never an uncertain
        // delivery the console would then wait to see confirmed.
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal(2, fake.Requests.Count(r => r.Uri.Contains("M999")));
        Assert.Equal(2, fake.Requests.Count(r => r.Uri.Contains("rr_connect")));
    }

    [Fact]
    public async Task Duet_reset_the_board_explicitly_rejects_throws_rather_than_reporting_uncertainty()
    {
        var fake = HaltedBoard();
        fake.On("rr_gcode", FakeDuet.Json("{\"err\":1}"));
        using var c = Duet(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(() => c.ResetAsync(CancellationToken.None));
        Assert.Contains("err 1", ex.Message);
    }

    [Fact]
    public async Task Duet_reset_answered_with_nothing_useful_is_uncertain()
    {
        var fake = HaltedBoard();
        fake.On("rr_gcode", FakeDuet.Json("{}"));
        using var c = Duet(fake);
        await c.ConnectAsync(CancellationToken.None);

        var delivery = await c.ResetAsync(CancellationToken.None);
        Assert.True(delivery.Uncertain);
        Assert.Contains("did not acknowledge", delivery.Detail);
    }

}
