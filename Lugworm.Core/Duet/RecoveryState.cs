using System;

namespace Lugworm.Core.Duet
{
    /// <summary>What the transport was able to say about a firmware-reset send.
    /// Deliberately three-valued: a reset that never came back is NOT a success and
    /// not a plain failure either, because the board dropping its session mid-answer
    /// is the normal shape of a successful M999.</summary>
    public sealed class ResetDelivery
    {
        /// <summary>The endpoint answered and accepted the command.</summary>
        public bool Acknowledged { get; }
        /// <summary>The command was put on the wire but no usable answer came back.
        /// It may or may not have reached the board - only a later observation decides.</summary>
        public bool Uncertain => !Acknowledged;
        public string Detail { get; }

        ResetDelivery(bool acknowledged, string detail)
        {
            Acknowledged = acknowledged;
            Detail = detail ?? "";
        }

        public static ResetDelivery Confirmed(string detail) => new ResetDelivery(true, detail);
        /// <summary>Delivery is unknown: a dropped connection, a timeout, or an answer
        /// that did not acknowledge the command.</summary>
        public static ResetDelivery Unconfirmed(string detail) => new ResetDelivery(false, detail);

        public override string ToString() => (Acknowledged ? "acknowledged: " : "unconfirmed: ") + Detail;
    }

    /// <summary>Which rule lets Reset through.</summary>
    public enum ResetPath
    {
        /// <summary>Reset is refused.</summary>
        None,
        /// <summary>The connected machine reports Halted or Off - the ordinary D4 rule.</summary>
        VerifiedHalt,
        /// <summary>THIS session emergency-stopped the machine and its status is no
        /// longer readable, so the operator may deliberately send the reset blind.</summary>
        OperatorRecovery,
    }

    /// <summary>Separate, immutable facts about an emergency stop and the recovery
    /// that follows it. Nothing here equates a successful HTTP send with a verified
    /// physical stop or a verified reset: <see cref="StopAcknowledged"/> means the
    /// board answered, <see cref="HaltObserved"/> means the machine actually reported
    /// Halted/Off, and <see cref="RecoveryVerified"/> means a NEW connection to the
    /// SAME machine came back live afterwards.</summary>
    public sealed class RecoveryState
    {
        /// <summary>Nothing has happened: no stop requested, no reset in flight.</summary>
        public static readonly RecoveryState None = new RecoveryState();

        // ---- identity binding -------------------------------------------------------
        /// <summary>Canonical host the stop was requested on ("" when none).</summary>
        public string Host { get; private set; }
        /// <summary>Identity of the session object the stop belongs to. A different
        /// session (a new lease, another host, a re-created session) never inherits an
        /// operator-recovery permission.</summary>
        public string SessionId { get; private set; }
        /// <summary>Expected machine fingerprint at stop time ("" when unprobed). The
        /// operator-recovery path re-checks it, so a disconnected host does not become
        /// an unrestricted reset target.</summary>
        public string Fingerprint { get; private set; }
        /// <summary>Connection generation the stop was sent on.</summary>
        public long StopConnectionGeneration { get; private set; }
        public DateTime StopRequestedUtc { get; private set; }

        // ---- the facts --------------------------------------------------------------
        /// <summary>The operator asked for an emergency stop in this session.</summary>
        public bool StopRequested { get; private set; }
        /// <summary>The M112 / emergency_stop request was answered by the endpoint. This
        /// is an ACKNOWLEDGEMENT, never proof that the machine physically stopped.</summary>
        public bool StopAcknowledged { get; private set; }
        /// <summary>Why the stop send failed, or null. A failed send does not clear
        /// <see cref="StopRequested"/>: the operator still needs the recovery path.</summary>
        public string? StopFailure { get; private set; }
        /// <summary>A snapshot reported Halted or Off at or after the stop - the first
        /// evidence that the machine really is stopped.</summary>
        public bool HaltObserved { get; private set; }
        /// <summary>The endpoint is not answering. Set from an observed transport
        /// failure, never inferred from a button state.</summary>
        public bool TransportUnavailable { get; private set; }
        public string? TransportReason { get; private set; }

        /// <summary>A firmware reset was sent (or attempted) in this session.</summary>
        public bool ResetRequested { get; private set; }
        /// <summary>The reset went on the wire but its delivery was never confirmed.</summary>
        public bool ResetDeliveryUncertain { get; private set; }
        public string? ResetDetail { get; private set; }
        /// <summary>Connection generation the reset was sent on; recovery counts as
        /// verified only on a LATER generation.</summary>
        public long ResetConnectionGeneration { get; private set; }
        /// <summary>A new connection to the same machine has been observed live since
        /// the reset. This - not the HTTP result - is what confirms recovery.</summary>
        public bool RecoveryVerified { get; private set; }
        /// <summary>Motion stays refused until a verified home runs after the reset.</summary>
        public bool HomingRequired { get; private set; }
        /// <summary>Deadline of the operator's explicit recovery arming;
        /// <see cref="DateTime.MinValue"/> when nothing is armed.</summary>
        public DateTime RecoveryArmedUntilUtc { get; private set; }

        /// <summary>How long an armed operator recovery stays armed (one press arms, the
        /// next press inside the window sends) - the terminal's confirm-window shape.</summary>
        public const double RecoveryArmSeconds = 8.0;

        RecoveryState()
        {
            Host = ""; SessionId = ""; Fingerprint = "";
            RecoveryArmedUntilUtc = DateTime.MinValue;
        }

        RecoveryState(RecoveryState src)
        {
            Host = src.Host; SessionId = src.SessionId; Fingerprint = src.Fingerprint;
            StopConnectionGeneration = src.StopConnectionGeneration;
            StopRequestedUtc = src.StopRequestedUtc;
            StopRequested = src.StopRequested;
            StopAcknowledged = src.StopAcknowledged;
            StopFailure = src.StopFailure;
            HaltObserved = src.HaltObserved;
            TransportUnavailable = src.TransportUnavailable;
            TransportReason = src.TransportReason;
            ResetRequested = src.ResetRequested;
            ResetDeliveryUncertain = src.ResetDeliveryUncertain;
            ResetDetail = src.ResetDetail;
            ResetConnectionGeneration = src.ResetConnectionGeneration;
            RecoveryVerified = src.RecoveryVerified;
            HomingRequired = src.HomingRequired;
            RecoveryArmedUntilUtc = src.RecoveryArmedUntilUtc;
        }

        /// <summary>True while the operator's explicit recovery arming is live.</summary>
        public bool RecoveryArmed(DateTime nowUtc) => nowUtc < RecoveryArmedUntilUtc;

        /// <summary>Nothing to show or gate on.</summary>
        public bool IsQuiet => !StopRequested && !ResetRequested && !TransportUnavailable;

        /// <summary>One line for the window and the <c>info</c> output, or null. Says
        /// what is actually known - never "reset sent" for an unconfirmed delivery, and
        /// never that a button could repair a dead network.</summary>
        public string? Notice
        {
            get
            {
                // Once the machine is back on a new connection everything before it is
                // history; only the one thing still owed - a verified home - is worth
                // saying, and it stops being said the moment it is done.
                if (ResetRequested && RecoveryVerified)
                    return HomingRequired
                        ? "the machine is back after the firmware reset - home before any motion"
                        : null;
                if (ResetRequested && !RecoveryVerified)
                {
                    if (ResetDeliveryUncertain)
                        return "firmware reset delivery is UNCONFIRMED (" + (ResetDetail ?? "no detail")
                             + ") - recovery counts only when a new connection to this machine comes back";
                    return "firmware reset sent - waiting for a new connection to this machine; home before any motion";
                }
                // A stop that could not be delivered is the most urgent thing there is:
                // it outranks even the dead transport that probably caused it.
                if (StopRequested && StopFailure != null)
                    return "E-STOP could not be delivered (" + StopFailure
                         + ") - use the machine's physical switch";
                if (TransportUnavailable)
                    return (Host.Length > 0 ? Host : "the machine") + " is not answering ("
                         + (TransportReason ?? "no detail")
                         + ") - a button cannot repair a dead connection; check power and network";
                // Requested but neither answered nor failed yet: the send is still in
                // flight, and the pending action already says so. Do not claim a failure.
                if (StopRequested && StopAcknowledged && !HaltObserved)
                    return "E-STOP acknowledged by the board, but no Halted/Off status has been observed yet -"
                         + " confirm the machine physically stopped";
                if (StopRequested && HaltObserved && !ResetRequested)
                    return "machine halted by E-STOP - press Reset to recover, then home before any motion";
                return null;
            }
        }

        // ---- copies ------------------------------------------------------------------

        RecoveryState Copy() => new RecoveryState(this);

        internal RecoveryState WithStop(string host, string sessionId, string fingerprint,
                                        long generation, DateTime nowUtc)
        {
            var n = Copy();
            n.Host = host ?? ""; n.SessionId = sessionId ?? ""; n.Fingerprint = fingerprint ?? "";
            n.StopConnectionGeneration = generation;
            n.StopRequestedUtc = nowUtc;
            n.StopRequested = true;
            n.StopAcknowledged = false;
            n.StopFailure = null;
            n.HaltObserved = false;
            // a new stop supersedes any earlier recovery of this session
            n.ResetRequested = false; n.ResetDeliveryUncertain = false; n.ResetDetail = null;
            n.RecoveryVerified = false; n.RecoveryArmedUntilUtc = DateTime.MinValue;
            return n;
        }

        internal RecoveryState WithStopAcknowledged() { var n = Copy(); n.StopAcknowledged = true; n.StopFailure = null; return n; }
        internal RecoveryState WithStopFailure(string reason) { var n = Copy(); n.StopAcknowledged = false; n.StopFailure = reason; return n; }
        internal RecoveryState WithHaltObserved() { var n = Copy(); n.HaltObserved = true; return n; }
        internal RecoveryState WithTransport(bool unavailable, string? reason)
        { var n = Copy(); n.TransportUnavailable = unavailable; n.TransportReason = unavailable ? reason : null; return n; }
        internal RecoveryState WithResetRequested(long generation)
        {
            var n = Copy();
            n.ResetRequested = true; n.ResetConnectionGeneration = generation;
            n.RecoveryVerified = false; n.HomingRequired = true;
            n.RecoveryArmedUntilUtc = DateTime.MinValue;
            return n;
        }
        internal RecoveryState WithResetDelivery(bool uncertain, string? detail)
        { var n = Copy(); n.ResetDeliveryUncertain = uncertain; n.ResetDetail = detail; return n; }
        internal RecoveryState WithRecoveryVerified()
        { var n = Copy(); n.RecoveryVerified = true; n.ResetDeliveryUncertain = false; return n; }
        internal RecoveryState WithHomingRequired(bool required) { var n = Copy(); n.HomingRequired = required; return n; }
        internal RecoveryState WithRecoveryArmedUntil(DateTime untilUtc)
        { var n = Copy(); n.RecoveryArmedUntilUtc = untilUtc; return n; }
    }

    /// <summary>One completed observation of the machine, as the poll loop sees it.
    /// Pure input to <see cref="RecoveryReducer.Observed"/>.</summary>
    public sealed class RecoveryObservation
    {
        public ConsoleConnection Connection { get; set; } = ConsoleConnection.None;
        public MachineStatus Status { get; set; } = MachineStatus.Unknown;
        /// <summary>Connection generation the observation was taken on.</summary>
        public long ConnectionGeneration { get; set; }
        /// <summary>Why state-changing actions are refused, or null. A reset counts as
        /// recovered only when the identity of the NEW connection is acceptable again.</summary>
        public string? TrustReason { get; set; }
        public bool AllHomed { get; set; }
    }

    /// <summary>Pure transitions of <see cref="RecoveryState"/>. No clock, no side
    /// effects: the session applies these at the moments they describe, and the same
    /// functions drive the tests.</summary>
    public static class RecoveryReducer
    {
        /// <summary>The operator asked for an emergency stop. Records the host, the
        /// session and the expected machine identity the recovery is bound to.</summary>
        public static RecoveryState StopRequested(RecoveryState state, string host, string sessionId,
                                                  string fingerprint, long generation, DateTime nowUtc)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithStop(host, sessionId, fingerprint, generation, nowUtc);
        }

        /// <summary>The endpoint answered the stop request. An acknowledgement, not a
        /// verified physical stop - <see cref="Observed"/> supplies that.</summary>
        public static RecoveryState StopAcknowledged(RecoveryState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.StopRequested ? state.WithStopAcknowledged() : state;
        }

        /// <summary>The stop send failed. The request stays recorded: the operator needs
        /// the recovery path exactly when the transport died.</summary>
        public static RecoveryState StopFailed(RecoveryState state, string reason)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.StopRequested ? state.WithStopFailure(reason ?? "no detail") : state;
        }

        /// <summary>Folds one completed observation. Sets the halt fact from a real
        /// Halted/Off status, clears the transport fault, and verifies recovery only on
        /// a LATER connection generation whose identity is acceptable.</summary>
        public static RecoveryState Observed(RecoveryState state, RecoveryObservation o)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (o == null) throw new ArgumentNullException(nameof(o));
            var s = state;
            if (s.TransportUnavailable) s = s.WithTransport(false, null);
            if (s.StopRequested && !s.HaltObserved
                && (o.Status == MachineStatus.Halted || o.Status == MachineStatus.Off))
                s = s.WithHaltObserved();
            if (s.ResetRequested && !s.RecoveryVerified
                && o.Connection == ConsoleConnection.Connected
                && o.ConnectionGeneration > s.ResetConnectionGeneration
                && o.TrustReason == null
                && o.Status != MachineStatus.Unknown)
                s = s.WithRecoveryVerified();
            if (s.HomingRequired && o.AllHomed && o.ConnectionGeneration > s.ResetConnectionGeneration)
                s = s.WithHomingRequired(false);

            // Close the episode once it is genuinely over. An emergency stop must not
            // authorise a blind reset for the rest of the session: a stop that has been
            // recovered from - by our reset, or by the machine simply coming back on a
            // new connection and answering normally - is history.
            bool liveAgain = o.Connection == ConsoleConnection.Connected
                          && o.Status != MachineStatus.Halted && o.Status != MachineStatus.Off
                          && o.Status != MachineStatus.Unknown;
            if (s.ResetRequested && s.RecoveryVerified && !s.HomingRequired) return RecoveryState.None;
            if (s.StopRequested && !s.ResetRequested && liveAgain
                && o.ConnectionGeneration > s.StopConnectionGeneration)
                return RecoveryState.None;
            return s;
        }

        /// <summary>The endpoint stopped answering. <paramref name="reason"/> is the
        /// transport's own words; the window shows it rather than a generic refusal.</summary>
        public static RecoveryState TransportLost(RecoveryState state, string reason)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithTransport(true, reason ?? "no detail");
        }

        /// <summary>A firmware reset has been submitted on <paramref name="generation"/>.
        /// Homing is required from here on, and a job is never resumed automatically.</summary>
        public static RecoveryState ResetSubmitted(RecoveryState state, long generation)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithResetRequested(generation);
        }

        /// <summary>Records what the transport could actually say about the send.</summary>
        public static RecoveryState ResetDelivered(RecoveryState state, ResetDelivery delivery)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (delivery == null) throw new ArgumentNullException(nameof(delivery));
            return state.WithResetDelivery(delivery.Uncertain, delivery.Detail);
        }

        /// <summary>Arms the operator's explicit recovery action (first press).</summary>
        public static RecoveryState ArmOperatorRecovery(RecoveryState state, DateTime nowUtc)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithRecoveryArmedUntil(nowUtc.AddSeconds(RecoveryState.RecoveryArmSeconds));
        }

    }

    /// <summary>Everything the reset rule reads, so the window and the transaction can
    /// answer from the same inputs.</summary>
    public sealed class ResetContext
    {
        public string Host { get; set; } = "";
        /// <summary>Identity of the live session, matched against
        /// <see cref="RecoveryState.SessionId"/>.</summary>
        public string SessionId { get; set; } = "";
        /// <summary>The expectation the session holds NOW, matched against
        /// <see cref="RecoveryState.Fingerprint"/> ("" when none was ever probed).</summary>
        public string ExpectedFingerprint { get; set; } = "";
        /// <summary>Non-null while the owners sharing this host disagree about which
        /// machine it is. Overrides every path - fail closed.</summary>
        public string? ExpectationConflict { get; set; }
        public ConsoleConnection Connection { get; set; } = ConsoleConnection.None;
        public MachineStatus Status { get; set; } = MachineStatus.Unknown;
        /// <summary>Why the session refuses state-changing commands, or null.</summary>
        public string? TrustReason { get; set; }
        /// <summary>Label of the action in flight, or null.</summary>
        public string? PendingAction { get; set; }
        public RecoveryState Recovery { get; set; } = RecoveryState.None;
        /// <summary>Wording of the transport's reset command, for the labels.</summary>
        public string ResetCommand { get; set; } = "M999";
    }

    /// <summary>The outcome of the reset rule.</summary>
    public sealed class ResetDecision
    {
        public bool Allowed { get; }
        public ResetPath Path { get; }
        /// <summary>True when the operator has to press once to arm and once to send.
        /// Only the blind operator-recovery path arms; a verified halt does not.</summary>
        public bool NeedsArming { get; }
        /// <summary>Why it is refused, or null.</summary>
        public string? Refusal { get; }
        /// <summary>What the window must show whether or not the button is enabled.</summary>
        public string? Notice { get; }
        public string Label { get; }

        public ResetDecision(bool allowed, ResetPath path, bool needsArming, string? refusal,
                             string? notice, string label)
        {
            Allowed = allowed; Path = path; NeedsArming = needsArming;
            Refusal = refusal; Notice = notice; Label = label ?? "Reset";
        }
    }

    /// <summary>The one reset rule. The console button, the Terminal tab's
    /// <c>M999</c> and the session's recovery transaction all decide here, so the tab
    /// cannot be a bypass and the window can never offer what the transaction refuses.</summary>
    public static class ResetPolicy
    {
        /// <param name="nowUtc">Clock for the operator-recovery arming window only.</param>
        public static ResetDecision Decide(ResetContext ctx, DateTime nowUtc)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var r = ctx.Recovery ?? RecoveryState.None;
            string command = string.IsNullOrEmpty(ctx.ResetCommand) ? "Reset" : ctx.ResetCommand;
            string plainLabel = "Reset (" + command + ")";
            string? notice = r.Notice;

            ResetDecision Refuse(string why)
                => new ResetDecision(false, ResetPath.None, false, why, notice, plainLabel);

            if (ctx.PendingAction != null)
                return Refuse("action in progress: " + ctx.PendingAction);
            if (ctx.ExpectationConflict != null)
                return Refuse(ctx.ExpectationConflict);

            bool halted = ctx.Status == MachineStatus.Halted || ctx.Status == MachineStatus.Off;
            bool connected = ctx.Connection == ConsoleConnection.Connected;

            // ---- path A: the ordinary D4 rule, unchanged ----------------------------
            if (connected && halted)
            {
                if (ctx.TrustReason != null) return Refuse(ctx.TrustReason);
                return new ResetDecision(true, ResetPath.VerifiedHalt, false, null, notice, plainLabel);
            }

            // ---- path B: this session's E-STOP, with the status no longer readable ---
            // The machine cannot confirm it is halted because nothing is answering. That
            // is exactly the case the operator has to be able to act on, so the LIVE
            // trust probe is not required here - but the binding recorded at stop time
            // is, or every disconnected host would become a reset target.
            bool sessionAlive = ctx.Connection != ConsoleConnection.None
                             && ctx.Connection != ConsoleConnection.Disconnected;
            bool statusUnavailable = !connected || r.TransportUnavailable;
            if (r.StopRequested && !r.RecoveryVerified && sessionAlive && statusUnavailable)
            {
                if (!string.Equals(r.SessionId, ctx.SessionId, StringComparison.Ordinal)
                    || !string.Equals(r.Host, ctx.Host, StringComparison.Ordinal))
                    return Refuse("the emergency stop belongs to a different session or host");
                if (!string.Equals(r.Fingerprint, ctx.ExpectedFingerprint, StringComparison.Ordinal))
                    return Refuse("the expected machine identity changed since the emergency stop");
                bool armed = r.RecoveryArmed(nowUtc);
                // Short enough for the footer button's fixed width; the notice and the
                // button's own tooltip carry the explanation.
                return new ResetDecision(true, ResetPath.OperatorRecovery, !armed, null, notice,
                                         armed ? "Press again" : "Recover (" + command + ")");
            }

            if (!connected)
                return Refuse("not connected (" + PrintConsoleButtons.Lower(ctx.Connection) + ")");
            return Refuse("machine not halted (" + PrintConsoleButtons.Lower(ctx.Status) + ")");
        }
    }
}
