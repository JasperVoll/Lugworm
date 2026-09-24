using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Robot;
using Lugworm.Duet;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Base for the "7 Print" components. Provides: session lookup by host
    /// string, live-update subscription with UI-thread marshaling, and the one
    /// safety-critical convention of the whole group - NOTHING is ever sent to the
    /// machine from a solve. Solves only read <c>Session.Latest</c>; every send
    /// starts in an on-canvas capsule press and runs on a worker task.</summary>
    public abstract class MachineComponentBase : IamComponentBase, IButtonRowHost
    {
        protected MachineComponentBase(string name, string nick, string desc, string sub)
            : base(name, nick, desc, "Lugworm", sub) { }

        /// <summary>Host as read during the last solve - what button presses act on.</summary>
        protected string HostInput = "";
        readonly Guid _owner = Guid.NewGuid();
        string _leasedHost = "";
        protected MachineSession? Session
        {
            get
            {
                string key = MachineSessionManager.CanonicalHost(HostInput);
                if (_leasedHost.Length > 0 && key != _leasedHost) ReleaseLease();
                var session = MachineSessionManager.Peek(HostInput);
                if (session != null) EnsureLease(HostInput, "");
                return session;
            }
        }

        string? _busy;
        string _actionHost = "";
        protected string? LastActionResult;
        int _expirePending;
        MachineSession? _subscribed;
        SessionChangeKind _subscriptionMask = SessionChangeKind.All;

        protected bool Busy => _busy != null;
        protected string? BusyLabel => _busy;
        /// <summary>Clears the busy marker from a subclass worker's UI-thread callback.</summary>
        protected void ClearBusy() => _busy = null;

        protected ButtonDef ActionButton(string id, string label, GH_Palette palette,
                                         bool enabled, string? refusal = null)
        {
            // the refusal reason lives in the hover tooltip only - appended to the
            // label it forced every row to size to its longest disabled state
            return new ButtonDef(id, label, palette, enabled, refusal);
        }

        /// <summary>Plugin-facing spelling of <see cref="GateSeverity"/>: the name every
        /// machine component's <c>ActionGates()</c> override already uses. Same members,
        /// same order, so the two convert by value.</summary>
        protected enum GateReport { None = 0, Remark = 1, Warning = 2 }

        /// <summary>Plugin-facing spelling of <see cref="MachineActionGate"/>. The gate
        /// SET and the severity policy live in <see cref="ActionGatePolicy"/> (Core);
        /// this carries them across the plugin boundary unchanged.</summary>
        protected readonly struct ActionGate
        {
            public readonly string Actions;
            public readonly string? Reason;
            public readonly GateReport Level;

            public ActionGate(string actions, string? reason, GateReport level)
            { Actions = actions; Reason = reason; Level = level; }

            internal ActionGate(MachineActionGate gate)
                : this(gate.Actions, gate.Reason, (GateReport)(int)gate.Level) { }

            internal MachineActionGate ToCore()
                => new MachineActionGate(Actions, Reason, (GateSeverity)(int)Level);
        }

        protected virtual IEnumerable<ActionGate> ActionGates()
            => Array.Empty<ActionGate>();

        /// <summary>Everything the gate policy needs, read once from the live session.
        /// The session, and only the session, is the plugin's business here.</summary>
        MachineActionState GateState()
        {
            var session = Session;
            return new MachineActionState
            {
                Busy = Busy,
                HasSession = session != null,
                Connected = session != null && session.State == DuetConnState.Connected,
                ConnectionState = session != null ? session.State.ToString().ToLowerInvariant() : "",
                TrustRefusalReason = session?.TrustRefusalReason(),
                ArmConfigurationKnown = session != null && session.ArmConfiguration.Known,
                Snapshot = session?.Latest,
            };
        }

        protected void ReportActionGates()
        {
            var gates = new List<MachineActionGate>();
            foreach (var gate in ActionGates()) gates.Add(gate.ToCore());
            foreach (var message in ActionGatePolicy.Report(gates))
                AddRuntimeMessage(message.Level == GateSeverity.Warning
                    ? GH_RuntimeMessageLevel.Warning : GH_RuntimeMessageLevel.Remark, message.Text);
        }

        protected ActionGate ConnectedActionGate(string actions)
            => new ActionGate(ActionGatePolicy.ConnectionGate(GateState(), actions));

        protected ActionGate StateActionGate(string actions, bool requireIdle = true,
                                             bool allowPaused = false, bool requireHomed = true,
                                             DateTime? notBeforeUtc = null,
                                             string activity = "this action")
            => new ActionGate(ActionGatePolicy.MachineStateGate(GateState(), actions,
                requireIdle: requireIdle, allowPaused: allowPaused, requireHomed: requireHomed,
                notBeforeUtc: notBeforeUtc, activity: activity));

        protected ActionGate ArmConfigurationActionGate(string actions,
                                                         string activity = "this action")
            => new ActionGate(ActionGatePolicy.ArmConfigurationGate(GateState(), actions, activity));

        // Kept as a named helper so capsule state and the transaction-side gate
        // describe the same refusal, while the transaction still re-checks it
        // under the session command lock.
        protected string? ActionRefusal(bool requireIdle = true, bool allowPaused = false,
                                        bool requireHomed = true, DateTime? notBeforeUtc = null,
                                        string activity = "this action")
            => StateActionGate("", requireIdle, allowPaused, requireHomed,
                               notBeforeUtc, activity).Reason;

        protected string? ConnectedRefusal()
            => ConnectedActionGate("").Reason;

        protected string? ArmConfigurationRefusal(string activity = "this action")
            => ArmConfigurationActionGate("", activity).Reason;

        protected void SetActionLifecycle(string operation, string phase)
        {
            LastActionResult = $"{operation} @ {HostInput}: {phase}";
        }

        static string ActionContext(string operation, string host)
            => $"{operation} @ {(host.Length == 0 ? "<no host>" : host)}";

        /// <summary>Peeks, never leases: this overload is also reached from worker threads,
        /// where the leasing <see cref="Session"/> getter could re-register a removed
        /// component as an owner and keep its session polling for good.</summary>
        protected string ActionFailure(string operation, Exception ex)
            => ActionFailure(operation, ex, MachineSessionManager.Peek(HostInput));

        /// <param name="session">The session the action ran on, captured on the UI thread
        /// before the worker started.</param>
        protected string ActionFailure(string operation, Exception ex, MachineSession? session)
        {
            string actionHost = _actionHost.Length == 0 ? HostInput : _actionHost;
            string state = session == null
                ? "disconnected"
                : session.Latest?.RawStatus ?? session.State.ToString();
            return $"ERROR: {ActionContext(operation, actionHost)} failed: {MachineSession.Flatten(ex)} " +
                   $"(state {state}). Next: check the gate and status, then retry";
        }

        protected void BeginAction(string operation)
        {
            _actionHost = HostInput;
            LastActionWarned = false;   // severity belongs to the action about to run
            _busy = "accepted: " + operation;
            SetActionLifecycle(operation, "queued and accepted");
            Grasshopper.Instances.RedrawCanvas();
        }

        protected void MarkExecuting(string operation)
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_busy != null) _busy = "executing: " + operation;
                SetActionLifecycle(operation, "executing");
                if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                else Grasshopper.Instances.RedrawCanvas();
            }));
        }

        protected MachineSession LeaseSession(string host, string password,
                                           Core.Duet.MachineFingerprint? expectedFingerprint = null)
        {
            HostInput = (host ?? "").Trim();
            var session = MachineSessionManager.Acquire(HostInput, password, _owner, expectedFingerprint);
            _leasedHost = MachineSessionManager.CanonicalHost(HostInput);
            return session;
        }

        void EnsureLease(string host, string password)
        {
            string key = MachineSessionManager.CanonicalHost(host);
            if (key.Length == 0) return;
            // Matching the cached host is NOT sufficient proof of ownership. Disconnect
            // calls CloseHost, which removes the entry and drops every owner while each
            // surviving component still holds _leasedHost. On the next Connect a fresh
            // entry is created, and a host-only check would return early here and leave
            // those components unregistered - so the Connect component would look like
            // the sole owner and its Release could stop a session others are still using.
            // Re-acquire whenever this owner is absent from the current entry.
            if (key == _leasedHost && MachineSessionManager.HasOwner(key, _owner)) return;
            if (_leasedHost.Length > 0 && _leasedHost != key) ReleaseLease();
            MachineSessionManager.Acquire(host, password, _owner);
            _leasedHost = key;
        }

        protected void ReleaseLease()
        {
            if (_leasedHost.Length == 0) return;
            MachineSessionManager.Release(_leasedHost, _owner);
            _leasedHost = "";
        }

        public abstract IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows { get; }
        public abstract void OnCapsulePressed(string id);

        // ---- live updates -> UI thread -------------------------------------

        protected void SubscribeTo(MachineSession? session, SessionChangeKind mask = SessionChangeKind.All)
        {
            if (ReferenceEquals(_subscribed, session) && _subscriptionMask == mask) return;
            if (_subscribed != null) _subscribed.Changed -= OnSessionChanged;
            _subscribed = session;
            _subscriptionMask = mask;
            if (session != null) session.Changed += OnSessionChanged;
        }

        /// <summary>Minimum spacing of poll-driven re-solves, ms; 0 = every change, which
        /// is the unchanged behaviour of every machine component. Live View raises it to
        /// 1000 in its automatic-output mode so a 250 ms poll cannot solve the canvas four
        /// times a second. Trailing edge: a change inside the window is not lost, it is
        /// deferred to the end of the window.</summary>
        protected virtual int ExpireMinIntervalMs => 0;

        DateTime _lastPollExpireUtc = DateTime.MinValue;
        Timer? _deferredExpire;
        long _telemetrySolves;

        /// <summary>How many Grasshopper solves this component's TELEMETRY subscription has
        /// caused. Live View reports it so the plan's "zero telemetry-triggered GH solves
        /// with automatic outputs off" can be read off a running Rhino instead of assumed.</summary>
        protected long TelemetrySolves => Interlocked.Read(ref _telemetrySolves);

        void OnSessionChanged(SessionChange change)
        {
            if ((change.Kind & _subscriptionMask) == 0) return;
            // poll-thread event: coalesce bursts, then expire on the UI thread only
            if (Interlocked.CompareExchange(ref _expirePending, 1, 0) != 0) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                Interlocked.Exchange(ref _expirePending, 0);
                if (Locked || OnPingDocument() == null) return;
                int min = ExpireMinIntervalMs;
                if (min > 0)
                {
                    double since = (DateTime.UtcNow - _lastPollExpireUtc).TotalMilliseconds;
                    if (since < min)
                    {
                        // restart, never stack: one timer, the latest deadline wins
                        _deferredExpire ??= new Timer(_ => Rhino.RhinoApp.InvokeOnUiThread((Action)DeferredExpire),
                                                      null, Timeout.Infinite, Timeout.Infinite);
                        _deferredExpire.Change(Math.Max(1, (int)Math.Ceiling(min - since)), Timeout.Infinite);
                        return;
                    }
                    _lastPollExpireUtc = DateTime.UtcNow;
                }
                Interlocked.Increment(ref _telemetrySolves);
                ExpireSolution(true);
            }));
        }

        void DeferredExpire()
        {
            if (_subscribed == null || Locked || OnPingDocument() == null) return;
            _lastPollExpireUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _telemetrySolves);
            ExpireSolution(true);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            SubscribeTo(null);
            _deferredExpire?.Dispose();
            _deferredExpire = null;
            ReleaseLease();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close)
            {
                SubscribeTo(null);
                _deferredExpire?.Dispose();
                _deferredExpire = null;
                ReleaseLease();
            }
            base.DocumentContextChanged(document, context);
        }

        // ---- capsule press -> async send ------------------------------------

        /// <summary>Runs one machine command off the UI thread (serialized per
        /// session); the outcome lands in <see cref="LastActionResult"/> and the
        /// component re-solves. Never blocks the canvas, never throws.</summary>
        protected void RunAction(string busyLabel, Func<IMachineClient, CancellationToken, Task<string>> action,
                                 bool stateChanging = true)
        {
            var session = Session;
            if (session == null)
            {
                LastActionResult = ActionFailure(busyLabel,
                    new InvalidOperationException("not connected. Press Connect"));
                ExpireFromUi();
                return;
            }
            BeginAction(busyLabel);
            Task.Run(async () =>
            {
                string result;
                try
                {
                    MarkExecuting(busyLabel);
                    string reply = await session.RunCommandAsync(action, stateChanging).ConfigureAwait(false);
                    result = string.IsNullOrEmpty(reply)
                        ? $"{ActionContext(busyLabel, _actionHost)}: confirmed by Duet"
                        : $"{ActionContext(busyLabel, _actionHost)}: confirmed by Duet, {reply}";
                }
                catch (Exception ex)
                {
                    result = ActionFailure(busyLabel, ex, session);
                }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _busy = null;
                    LastActionResult = result;
                    if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                    else Grasshopper.Instances.RedrawCanvas();
                }));
            });
        }

        /// <summary>Runs a state-changing action with a fresh snapshot obtained
        /// inside the session command transaction.</summary>
        protected void RunGatedAction(string busyLabel, bool includeJob,
            Func<ObjectModelSnapshot, IMachineClient, CancellationToken, Task<string>> action)
        {
            var session = Session;
            if (session == null)
            {
                LastActionResult = ActionFailure(busyLabel,
                    new InvalidOperationException("not connected. Press Connect"));
                ExpireFromUi();
                return;
            }
            BeginAction(busyLabel);
            Task.Run(async () =>
            {
                string result;
                try
                {
                    MarkExecuting(busyLabel);
                    string reply = await session.RunGatedCommandAsync(includeJob, action).ConfigureAwait(false);
                    result = string.IsNullOrEmpty(reply)
                        ? $"{ActionContext(busyLabel, _actionHost)}: confirmed by Duet"
                        : $"{ActionContext(busyLabel, _actionHost)}: confirmed by Duet, {reply}";
                }
                catch (Exception ex) { result = ActionFailure(busyLabel, ex, session); }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    _busy = null;
                    LastActionResult = result;
                    if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                    else Grasshopper.Instances.RedrawCanvas();
                }));
            });
        }

        /// <summary>E-stop path: no session-state gate, no command queue.</summary>
        protected void RunEmergencyStop()
        {
            var session = Session;
            if (session == null)
            {
                LastActionResult = ActionFailure("emergency stop",
                    new InvalidOperationException("no session. Use the machine's physical emergency stop"));
                ExpireFromUi();
                return;
            }
            BeginAction("emergency stop (M112)");
            Task.Run(async () =>
            {
                string result;
                try
                {
                    MarkExecuting("emergency stop (M112)");
                    await session.EmergencyStopAsync().ConfigureAwait(false);
                    result = $"{ActionContext("emergency stop (M112)", _actionHost)}: " +
                             "confirmed by Duet, M112 sent. Press Reset (M999) to recover";
                }
                catch (Exception ex) { result = ActionFailure("emergency stop (M112)", ex, session) + ". Use the physical switch"; }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    // Clear _busy exactly as RunAction/RunGatedAction do; leaving it set
                    // makes ConnectedRefusal() report "another action is executing"
                    // forever, permanently disabling every Home and jog button.
                    _busy = null;
                    LastActionResult = result;
                    if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                    else Grasshopper.Instances.RedrawCanvas();
                }));
            });
        }

        /// <summary>Expire from a UI-thread context (button handlers).</summary>
        protected void ExpireFromUi()
        {
            if (OnPingDocument() != null && !Locked) ExpireSolution(true);
            else Grasshopper.Instances.RedrawCanvas();
        }

        // ---- shared emit helpers ---------------------------------------------

        /// <summary>Set when an action succeeded but carried a warning the operator must
        /// see. Severity cannot be recovered from the message text: RunGatedAction wraps a
        /// successful reply as "&lt;action&gt; @ &lt;host&gt; - confirmed by Duet: &lt;reply&gt;",
        /// so an embedded "WARNING: ..." never reaches the front of the string and the
        /// prefix test below demoted every non-blocking motion-limit warning to a grey
        /// remark - which is the one thing that warning exists to avoid.</summary>
        protected bool LastActionWarned;

        /// <summary>Surfaces the last action outcome as a runtime message.</summary>
        protected void ReportLastAction()
        {
            if (string.IsNullOrEmpty(LastActionResult)) return;
            GH_RuntimeMessageLevel level =
                LastActionResult!.StartsWith("ERROR", StringComparison.Ordinal)
                    ? GH_RuntimeMessageLevel.Error
                    : LastActionWarned || LastActionResult.StartsWith("WARNING", StringComparison.Ordinal)
                        ? GH_RuntimeMessageLevel.Warning
                        : GH_RuntimeMessageLevel.Remark;
            AddRuntimeMessage(
                level,
                LastActionResult);
        }

        /// <summary>Read-only text for the `info` output. Peeks, never leases: a component
        /// that has no business owning the session (Live View with enable off) must not
        /// keep it alive just by describing it. Every other caller has already leased
        /// through <see cref="Session"/> earlier in the same solve.</summary>
        protected string SessionInfo(RobotMechanism mechanism = RobotMechanism.Scara)
        {
            var s = MachineSessionManager.Peek(HostInput);
            if (s == null) return $"no session for host '{HostInput}'. Press Connect";
            var parts = new List<string> { $"host {s.Host}", $"state {s.State}" };
            if (s.State == DuetConnState.Connecting)
                parts.Add("connection lifecycle: executing");
            else if (s.State == DuetConnState.Reconnecting)
                parts.Add("reconnect lifecycle: executing");
            else if (s.State == DuetConnState.Connected)
                parts.Add("connection lifecycle: confirmed");
            else if (s.State == DuetConnState.Disconnected)
                parts.Add("connection lifecycle: disconnected");
            if (s.BoardType != null) parts.Add("board " + s.BoardType);
            if (s.Legacy)
            {
                parts.Add("legacy rr_status API (RRF < 3.2)");
                parts.Add(s.LegacyM208Probed ? "M208 limits: verified" : "M208 limits: unverified");
            }
            // A Cartesian profile has no SCARA M669 contract, so "M669 kinematics:
            // unverified" would be permanent noise there; the SCARA-board mismatch
            // is surfaced by ReportFirmwareFindings instead.
            if (mechanism != RobotMechanism.Cartesian)
                parts.Add(s.KinematicsProbed ? "M669 kinematics: verified" : "M669 kinematics: unverified");
            parts.Add(s.MotionBaselineProbed ? "motion baseline: verified from config.g" : "motion baseline: unverified");
            var snap = s.Latest;
            if (snap != null)
                parts.Add($"status {snap.RawStatus}, poll age {(DateTime.UtcNow - snap.TakenUtc).TotalSeconds:0.0}s");
            if (s.LastError != null) parts.Add("last error: " + s.LastError);
            if (Busy) parts.Add("busy: " + BusyLabel);
            if (!string.IsNullOrEmpty(LastActionResult)) parts.Add("last action: " + LastActionResult);
            return string.Join("\n", parts);
        }

        protected void ReportFirmwareFindings(RobotLimits lim,
                                              RobotMechanism mechanism = RobotMechanism.Scara)
        {
            var session = Session;
            if (session == null || session.State != DuetConnState.Connected) return;
            if (session.Legacy && !session.LegacyM208Probed)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Machine axis limits could not be verified from the firmware, so check the limit inputs before moving");
            if (mechanism == RobotMechanism.Cartesian)
            {
                // A Cartesian profile has no SCARA M669 contract to verify. But a
                // board whose move.kinematics DID parse as SCARA is very likely not
                // the machine this profile describes - say so instead of gating.
                if (session.KinematicsProbed && session.Kinematics != null)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "The firmware reports SCARA (M669) kinematics but the robot definition selects a "
                        + "Cartesian profile. Check that the definition matches the connected machine");
            }
            else if (!session.KinematicsProbed)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Firmware M669 kinematics are unverified: "
                    + (session.KinematicsProbeDetail ?? "the probe at connect time did not return a readable reply")
                    + ". Reconnect to retry.");
            var snap = session.Latest;
            if (snap == null) return;
            foreach (var finding in FirmwareLimitCheck.CompareFindings(snap, lim))
            {
                var level = finding.Severity == Severity.Error ? GH_RuntimeMessageLevel.Error
                    : finding.Severity == Severity.Info ? GH_RuntimeMessageLevel.Remark
                    : GH_RuntimeMessageLevel.Warning;
                AddRuntimeMessage(level, finding.Message);
            }
            if (mechanism != RobotMechanism.Cartesian
                && session.KinematicsProbed && session.Kinematics != null)
                foreach (var finding in FirmwareLimitCheck.CompareKinematics(session.Kinematics, lim))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, finding.Message);
        }

        /// <summary>Resolves the optional robot-definition input a Duet control
        /// component carries since 2.2.0. Empty (unwired) keeps the shipped
        /// Potterbot SCARA profile - the pre-2.2.0 behaviour, so saved documents
        /// are unchanged. A profile that does not advertise Duet control returns
        /// null with an error so callers fail closed instead of gating on the
        /// wrong machine model.</summary>
        protected static RobotProfile? ResolveControlProfile(string text, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text)) return RobotCatalog.Get("potterbot-scara-v4");
            RobotProfile profile;
            try { profile = RobotCatalog.Resolve(text); }
            catch (Exception ex) { error = ex.Message; return null; }
            if (profile.ControlKind != RobotControlKind.Duet)
            {
                error = $"robot profile '{profile.Id}' does not advertise machine control. Use a Duet profile";
                return null;
            }
            return profile;
        }

        protected virtual RobotControlKind ControlKind => RobotControlKind.Duet;
    }
}
