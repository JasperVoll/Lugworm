using System;

namespace Lugworm.Core.Duet
{
    /// <summary>What the console should do next for a held jog button.</summary>
    public enum JogRepeatDecision
    {
        /// <summary>Send one whitelisted step now.</summary>
        Send,
        /// <summary>Not yet - something is still outstanding. Ask again later.</summary>
        Wait,
        /// <summary>The repeat is over: released, refused, or blocked by a gate that
        /// waiting cannot clear. The caller must stop asking until the next press.</summary>
        Terminate,
    }

    /// <summary>What the newest snapshot says about sending the next step. The
    /// distinction between <see cref="Busy"/> and <see cref="Blocked"/> is what keeps a
    /// long step from killing the repeat: <c>JogPlanner</c> gates on Idle
    /// (<c>StateGate.Refuse(requireIdle: true)</c>), so the acknowledgement poll after a
    /// 50 mm jog routinely reports the machine still executing that very jog.</summary>
    public enum JogReadiness
    {
        /// <summary>Idle - the next step may go.</summary>
        Ready,
        /// <summary>Still executing; this clears on its own. Keep waiting.</summary>
        Busy,
        /// <summary>A gate that waiting will not clear (halted, disconnected, paused
        /// without the maintenance override). End the repeat.</summary>
        Blocked,
    }

    /// <summary>Phase of one press-and-hold sequence.</summary>
    public enum JogRepeatPhase
    {
        /// <summary>No press in progress.</summary>
        Idle,
        /// <summary>Held, nothing outstanding - the next step may go.</summary>
        Ready,
        /// <summary>A gated transaction is running. Nothing else may be sent, ever.</summary>
        InFlight,
        /// <summary>The transaction succeeded; waiting for the post-send acknowledgement.</summary>
        AwaitingAck,
        /// <summary>Finished - released, refused or blocked.</summary>
        Terminated,
    }

    /// <summary>Immutable state of one press-and-hold jog sequence. Setters are private
    /// so the type stays net48-friendly, matching <see cref="PrintConsoleState"/>.</summary>
    public sealed class JogRepeatState
    {
        public JogRepeatPhase Phase { get; private set; }
        /// <summary>True while the operator holds the button down.</summary>
        public bool Held { get; private set; }
        /// <summary>True once this hold has sent at least one step, so the next one is an
        /// auto-repeat rather than the operator's own discrete press.</summary>
        public bool HasRepeated { get; private set; }
        /// <summary>The poll generation that had most recently STARTED when the last send
        /// completed. The acknowledgement is a snapshot from a LATER generation, which is
        /// exactly "a poll cycle that started after the send completed".</summary>
        /// <remarks>A generation counter, not a timestamp: <c>ObjectModelSnapshot.TakenUtc</c>
        /// is stamped when parsing finishes, not when the cycle began
        /// (<c>ObjectModel.FromRrModel</c>), so wall-clock times cannot express this
        /// causal relation - and a counter is immune to clock adjustments.</remarks>
        public long AckAfterGeneration { get; private set; }
        /// <summary>Newest poll generation whose snapshot has arrived.</summary>
        public long LatestGeneration { get; private set; }
        /// <summary>Readiness reported with <see cref="LatestGeneration"/>.</summary>
        public JogReadiness LatestReadiness { get; private set; }
        /// <summary>Why the sequence terminated; null while it is alive.</summary>
        public string? TerminationReason { get; private set; }

        JogRepeatState()
        {
            Phase = JogRepeatPhase.Idle;
            LatestReadiness = JogReadiness.Ready;
            AckAfterGeneration = long.MinValue;
            LatestGeneration = long.MinValue;
        }

        JogRepeatState(JogRepeatState src)
        {
            Phase = src.Phase;
            Held = src.Held;
            HasRepeated = src.HasRepeated;
            AckAfterGeneration = src.AckAfterGeneration;
            LatestGeneration = src.LatestGeneration;
            LatestReadiness = src.LatestReadiness;
            TerminationReason = src.TerminationReason;
        }

        public static JogRepeatState Empty { get; } = new JogRepeatState();

        /// <summary>True exactly when the next send is the operator's own discrete press
        /// rather than an auto-repeat. ONLY a discrete press may arm or consume the
        /// paused-jog maintenance override; the caller must pass this through to the jog
        /// action, because the override is armed as a side effect of the refusal itself
        /// (<c>DuetJogComponent.Jog</c>) - terminating the repeat after the refusal has
        /// already come back would be too late to stop the arming.</summary>
        public bool AllowsMaintenanceOverride => !HasRepeated;

        /// <summary>True once a poll that started after the last send completed has
        /// arrived. Vacuously true before the first send of a hold.</summary>
        public bool Acknowledged
            => AckAfterGeneration == long.MinValue || LatestGeneration > AckAfterGeneration;

        internal JogRepeatState With(Action<JogRepeatState> mutate)
        {
            var n = new JogRepeatState(this);
            mutate(n);
            return n;
        }

        internal void SetPhase(JogRepeatPhase p) { Phase = p; }
        internal void SetHeld(bool h) { Held = h; }
        internal void SetHasRepeated(bool r) { HasRepeated = r; }
        internal void SetAckAfterGeneration(long g) { AckAfterGeneration = g; }
        internal void SetLatestGeneration(long g) { LatestGeneration = g; }
        internal void SetLatestReadiness(JogReadiness r) { LatestReadiness = r; }
        internal void SetTerminationReason(string? r) { TerminationReason = r; }
    }

    /// <summary>Press-and-hold jog coalescing. Pure - the caller supplies every event;
    /// nothing here reads a clock or talks to a session.</summary>
    /// <remarks>
    /// <para>The invariant is <b>at most one jog in flight, ever</b>. Queue depth is
    /// structurally zero: a step is only ever emitted out of
    /// <see cref="JogRepeatPhase.Ready"/>, and <see cref="SendStarted"/> is the only exit
    /// from it, so releasing the button leaves at most the single already-sent
    /// whitelisted step in motion. A release followed immediately by a fresh press
    /// cannot defeat this: <see cref="Press"/> is ignored while a transaction is in
    /// flight.</para>
    /// <para>The acknowledgement is strictly <b>post-send</b>: a repeat needs a poll
    /// cycle that <i>started</i> after the previous transaction <i>completed</i>. The
    /// transaction's own pre-send snapshot does not satisfy it - the healthy poll loop
    /// and the command path fetch snapshots independently, the latter under the command
    /// lock, so the two overlap. Neither does the planner's <c>notBeforeUtc</c>, which is
    /// stamped before the command is queued and, in any case, means "refuse snapshots
    /// older than this command" (<see cref="StateGate"/>) rather than "do not send before
    /// this time" - it is a freshness bound, not a pacing deadline, and this policy
    /// deliberately does not reinterpret it.</para>
    /// <para>The condition is "a poll started later", not "a poll that observed motion":
    /// a 0.1 mm step can begin and finish between two polls, so requiring an observed
    /// non-idle snapshot would stall the repeat forever. That is safe because the planner
    /// emits the ABSOLUTE validated target rather than a relative delta, so a duplicate
    /// press on a stale snapshot re-sends the same target instead of walking the machine
    /// (<c>JogPlanner.Plan</c>).</para>
    /// </remarks>
    public static class JogRepeatPolicy
    {
        /// <summary>The operator pressed the button down. Ignored while a transaction is
        /// in flight, so a fast release/re-press cannot slip a second jog past the
        /// one-in-flight invariant.</summary>
        public static JogRepeatState Press(JogRepeatState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Phase == JogRepeatPhase.InFlight)
                return state.With(n => n.SetHeld(true));   // resume the hold; still no second send
            // AckAfterGeneration is deliberately NOT cleared: an acknowledgement still
            // outstanding from the previous hold carries over, so release/re-press cannot
            // be used to jump the post-send poll.
            return state.With(n =>
            {
                n.SetPhase(JogRepeatPhase.Ready);
                n.SetHeld(true);
                n.SetHasRepeated(false);
                n.SetTerminationReason(null);
            });
        }

        /// <summary>The operator let go. Any step already sent stays in motion; nothing
        /// further is emitted.</summary>
        public static JogRepeatState Release(JogRepeatState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.With(n =>
            {
                n.SetHeld(false);
                // A transaction already running must still finish and report; only the
                // repeat stops.
                if (n.Phase != JogRepeatPhase.InFlight)
                {
                    n.SetPhase(JogRepeatPhase.Terminated);
                    if (n.TerminationReason == null) n.SetTerminationReason("released");
                }
            });
        }

        /// <summary>The caller has begun a gated transaction for the step this policy
        /// authorized. Nothing else may be sent until it completes.</summary>
        public static JogRepeatState SendStarted(JogRepeatState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.With(n =>
            {
                n.SetPhase(JogRepeatPhase.InFlight);
                // From the second step onwards this is an auto-repeat, which loses the
                // right to arm or consume the paused-jog maintenance override.
                n.SetHasRepeated(true);
            });
        }

        /// <summary>The transaction completed successfully.</summary>
        /// <param name="pollGenerationAtCompletion">The poll generation that had most
        /// recently STARTED at this moment. The next repeat waits for a later one.</param>
        public static JogRepeatState SendCompleted(JogRepeatState state, long pollGenerationAtCompletion)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.With(n =>
            {
                n.SetAckAfterGeneration(pollGenerationAtCompletion);
                if (n.Phase == JogRepeatPhase.InFlight)
                {
                    n.SetPhase(n.Held ? JogRepeatPhase.AwaitingAck : JogRepeatPhase.Terminated);
                    if (!n.Held && n.TerminationReason == null) n.SetTerminationReason("released");
                }
            });
        }

        /// <summary>The transaction was refused (a gate, a planner refusal, a transport
        /// error). Terminates the repeat: an auto-retry would hammer a closed gate, and a
        /// refusal that arms the maintenance override must be answered by a human press.</summary>
        public static JogRepeatState SendRefused(JogRepeatState state, string reason)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.With(n =>
            {
                n.SetPhase(JogRepeatPhase.Terminated);
                n.SetTerminationReason(reason ?? "refused");
            });
        }

        /// <summary>A poll cycle of generation <paramref name="generation"/> delivered a
        /// snapshot. Must be called for EVERY completed poll, including one whose contents
        /// are identical to the previous snapshot: the session's <c>Changed</c> event
        /// suppresses <c>SessionChangeKind.None</c>, so it alone cannot prove that an
        /// acknowledgement poll happened.</summary>
        public static JogRepeatState PollObserved(JogRepeatState state, long generation, JogReadiness readiness)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.LatestGeneration != long.MinValue && generation <= state.LatestGeneration) return state;
            return state.With(n =>
            {
                n.SetLatestGeneration(generation);
                n.SetLatestReadiness(readiness);
            });
        }

        /// <summary>Whether another step may go out now.</summary>
        public static JogRepeatDecision Decide(JogRepeatState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            switch (state.Phase)
            {
                case JogRepeatPhase.Idle:
                case JogRepeatPhase.Terminated:
                    return JogRepeatDecision.Terminate;

                case JogRepeatPhase.InFlight:
                    // The one-in-flight invariant. Nothing is queued behind this.
                    return JogRepeatDecision.Wait;

                case JogRepeatPhase.Ready:
                    if (!state.Held) return JogRepeatDecision.Terminate;
                    // A hold that has never sent anything has nothing to acknowledge. But a
                    // release and immediate re-press must NOT skip the outstanding
                    // acknowledgement of the step the previous hold already sent - the
                    // machine may still be executing it, and sending again would earn a
                    // guaranteed Idle refusal that kills the new hold.
                    if (!state.Acknowledged) return JogRepeatDecision.Wait;
                    return state.LatestReadiness == JogReadiness.Busy
                        ? JogRepeatDecision.Wait
                        : JogRepeatDecision.Send;

                case JogRepeatPhase.AwaitingAck:
                    if (!state.Held) return JogRepeatDecision.Terminate;
                    if (!state.Acknowledged) return JogRepeatDecision.Wait;
                    switch (state.LatestReadiness)
                    {
                        // Still executing the previous step: waiting clears this, and
                        // sending now would earn an Idle refusal that kills the hold.
                        case JogReadiness.Busy: return JogRepeatDecision.Wait;
                        case JogReadiness.Blocked: return JogRepeatDecision.Terminate;
                        default: return JogRepeatDecision.Send;
                    }

                default:
                    return JogRepeatDecision.Wait;
            }
        }

        /// <summary>Maps a machine status onto the readiness the repeat needs.</summary>
        /// <param name="maintenanceOverrideActive">True only while a discrete press has
        /// armed the paused-jog maintenance override; an auto-repeat never sets it.</param>
        public static JogReadiness ReadinessOf(MachineStatus status, bool maintenanceOverrideActive = false)
        {
            switch (status)
            {
                case MachineStatus.Idle:
                    return JogReadiness.Ready;
                case MachineStatus.Busy:
                case MachineStatus.Processing:
                case MachineStatus.Simulating:
                case MachineStatus.Resuming:
                case MachineStatus.Pausing:
                case MachineStatus.Cancelling:
                case MachineStatus.ChangingTool:
                    return JogReadiness.Busy;
                case MachineStatus.Paused:
                    // Jogging while paused is the maintenance path, and it is reachable
                    // only behind the two-press override - which a held button may not use.
                    return maintenanceOverrideActive ? JogReadiness.Ready : JogReadiness.Blocked;
                default:
                    return JogReadiness.Blocked;
            }
        }
    }
}
