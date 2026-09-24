using System;
using System.Threading;
using Lugworm.Core.Duet;

namespace Lugworm.Duet
{
    /// <summary>Projects one shared <see cref="MachineSession"/> onto an immutable
    /// <see cref="PrintConsoleState"/> for the Print Console. The session stays the
    /// single source of truth: on every <see cref="MachineSession.Changed"/> its facts are
    /// copied out, folded by <see cref="PrintConsoleReducer"/> under one lock and
    /// published through <see cref="StateChanged"/> (raised on the calling thread -
    /// poll, command or UI; the component forwards it to the form, which coalesces).
    ///
    /// Canvas re-solves are a separate, rate-limited concern: after every change
    /// <see cref="PrintConsoleExpirePolicy"/> compares the OUTPUT projection of the
    /// new state with what the canvas last received and asks for an expire
    /// (<see cref="ExpireRequested"/>, always on the UI thread) at most once per
    /// <see cref="ExpireMinIntervalMs"/>, trailing edge, and only while <see cref="Live"/>.
    /// Form-only fields (layer, last reply, snapshot time / poll age) never expire because
    /// they are not part of <see cref="OutputSnapshot"/>; per-axis homed flags, the upload
    /// record and the arm-configuration flag reach the <c>info</c> output and expire once per
    /// action that flips them, never per poll.
    /// Rhino only - no WinForms; the component is the only place that touches the form.</summary>
    public sealed class PrintConsoleProjector : IDisposable
    {
        /// <summary>Minimum spacing of expire requests = the session's poll interval
        /// (<see cref="MachineSession.PollIntervalMs"/> default), so a live console costs
        /// at most one re-solve per poll.</summary>
        public const int ExpireMinIntervalMs = 1000;

        readonly object _lock = new object();           // state, session, expire bookkeeping
        readonly object _publishLock = new object();    // StateChanged delivery order
        readonly Timer _timer;
        PrintConsoleState _state;
        MachineSession? _session;
        OutputSnapshot? _emitted;                       // outputs the canvas holds (or was asked to fetch)
        DateTime? _lastExpireUtc;
        OutputVisibility _visible = OutputVisibility.All;
        bool _live;
        bool _disposed;
        int _generation;                                // bumped by Dispose: queued callbacks become no-ops
        long _version;                                  // stamped under _lock
        long _publishedVersion;                         // under _publishLock
        int _pollHandlerFaultLogged;                    // 1 once a PollCompleted handler fault was logged

        public PrintConsoleProjector(PrintConsoleState initial)
        {
            _state = initial ?? throw new ArgumentNullException(nameof(initial));
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Current projection; readable from any thread.</summary>
        public PrintConsoleState State { get { lock (_lock) return _state; } }

        /// <summary>The attached session, null when detached.</summary>
        public MachineSession? Session { get { lock (_lock) return _session; } }

        /// <summary>Forwarded from the session for EVERY completed poll, including one
        /// whose snapshot is unchanged - the press-and-hold jog needs proof that an
        /// acknowledgement poll ran, which <see cref="StateChanged"/> cannot give because
        /// the session suppresses no-change notifications.</summary>
        public event Action<long, ObjectModelSnapshot>? PollCompleted;

        void RaisePollCompleted(long generation, ObjectModelSnapshot snapshot)
        {
            if (snapshot.Overrides != null) Transition(null, foldFacts: true, adopt: false);
            var handler = PollCompleted;
            if (handler == null) return;
            try { handler(generation, snapshot); }
            catch (Exception ex)
            {
                // A repeat must never break the poll loop, which is why the session never
                // sees this, but a handler that throws on every poll stops the held jog
                // unnoticed: logged once per projector.
                if (Interlocked.Exchange(ref _pollHandlerFaultLogged, 1) != 0) return;
                // best effort: the session log is the only sink, and this is a catch path
                try { Session?.AppendLog("console: poll handler failed: " + MachineSession.Flatten(ex)); }
                catch (Exception) { }
            }
        }

        /// <summary>Which output sockets the component registers. Only their values take
        /// part in the change decision, so hiding Motion and Temperatures means a position
        /// poll and drifting telemetry never expire the canvas at all - and the projection
        /// stops building the values nobody asked for.</summary>
        public OutputVisibility Visible
        {
            get { lock (_lock) return _visible; }
            set
            {
                lock (_lock)
                {
                    if (_disposed || value == null || _visible.Equals(value)) return;
                    _visible = value;
                }
            }
        }

        /// <summary>While false no change ever asks for a re-solve; the outputs
        /// refresh on the next ordinary solve only.</summary>
        public bool Live
        {
            get { lock (_lock) return _live; }
            set
            {
                lock (_lock)
                {
                    if (_disposed || _live == value) return;
                    _live = value;
                    if (!value) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
        }

        /// <summary>Raised after every state transition, on the calling thread.</summary>
        public event Action<PrintConsoleState>? StateChanged;

        /// <summary>Raised on the UI thread when the canvas outputs should refresh.</summary>
        public event Action? ExpireRequested;

        /// <summary>Subscribes to the session and folds its facts once (event path:
        /// the fold runs the expire decision like any other change).</summary>
        public void Attach(MachineSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            MachineSession? previous = null;
            bool subscribe = false;
            lock (_lock)
            {
                if (_disposed) return;
                if (!ReferenceEquals(_session, session)) { previous = _session; _session = session; subscribe = true; }
            }
            if (previous != null) { previous.Changed -= OnSessionChanged; previous.PollCompleted -= RaisePollCompleted; }
            if (subscribe) { session.Changed += OnSessionChanged; session.PollCompleted += RaisePollCompleted; }
            Transition(null, foldFacts: true, adopt: false);
        }

        /// <summary>Unsubscribes; the projection keeps its last machine facts and log
        /// tail but reports <see cref="ConsoleConnection.None"/> - the console has let
        /// go of the session, so nothing it shows is live any more.</summary>
        public void Detach()
        {
            MachineSession? previous;
            lock (_lock) { previous = _session; _session = null; }
            if (previous == null) return;
            previous.Changed -= OnSessionChanged;
            previous.PollCompleted -= RaisePollCompleted;
            Transition(s => s.WithConnection(ConsoleConnection.None), foldFacts: false, adopt: false);
        }

        /// <summary>The solve path: folds the session facts once (when attached) plus
        /// an optional solve-time step, then adopts the resulting outputs as what the
        /// canvas holds - the caller emits <see cref="State"/> right after, so no expire
        /// is requested and a pending deferred one is dropped.</summary>
        public void Refresh(Func<PrintConsoleState, PrintConsoleState>? solveStep = null)
            => Transition(solveStep, foldFacts: true, adopt: true);

        /// <summary>The event path for action / clear-log transitions: folds the
        /// freshest session facts (when attached), applies the step and runs the expire
        /// decision. Returns the state it produced, so a caller that started an action can
        /// keep its <see cref="PrintConsoleState.ActionEpoch"/> and hand it back on
        /// completion.</summary>
        public PrintConsoleState Apply(Func<PrintConsoleState, PrintConsoleState> step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            return Transition(step, foldFacts: true, adopt: false);
        }

        public void Dispose()
        {
            MachineSession? session;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _generation++;
                session = _session;
                _session = null;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            if (session != null)
            {
                session.Changed -= OnSessionChanged;
                // Without this the session keeps a reference to a disposed projector and
                // goes on raising poll notifications into it.
                session.PollCompleted -= RaisePollCompleted;
            }
            _timer.Dispose();
        }

        // ---- the single transition -------------------------------------------------

        void OnSessionChanged(SessionChange change) => Transition(null, foldFacts: true, adopt: false);

        PrintConsoleState Transition(Func<PrintConsoleState, PrintConsoleState>? step, bool foldFacts, bool adopt)
        {
            PrintConsoleState next;
            long version;
            lock (_lock)
            {
                if (_disposed) return _state;
                next = _state;
                if (foldFacts && _session != null) next = PrintConsoleReducer.Reduce(next, Collect(_session));
                if (step != null) next = step(next);
                _state = next;
                version = ++_version;
                if (adopt)
                {
                    _emitted = PrintConsoleOutputs.From(next, _visible);
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            Publish(next, version);
            if (!adopt) Decide();
            return next;
        }

        /// <summary>Delivers states in version order: a transition that lost the race
        /// to a newer one is dropped, never delivered late over the newer state.</summary>
        void Publish(PrintConsoleState state, long version)
        {
            var handler = StateChanged;
            if (handler == null) return;
            lock (_publishLock)
            {
                if (version <= _publishedVersion) return;
                _publishedVersion = version;
                handler(state);
            }
        }

        /// <summary>Runs the expire policy against the CURRENT state (not the one a
        /// particular transition produced): a deferred decision re-evaluated later
        /// therefore always sees the newest outputs - the trailing edge is never lost.</summary>
        void Decide()
        {
            bool request = false;
            int generation = 0;
            lock (_lock)
            {
                if (_disposed) return;
                // with live off the policy answers None without looking at the outputs, so
                // the snapshot (info text, log tail) is not built on every poll for nothing
                var outputs = _live || _emitted == null ? PrintConsoleOutputs.From(_state, _visible) : _emitted;
                var now = DateTime.UtcNow;
                var verdict = PrintConsoleExpirePolicy.Decide(_emitted, outputs, _live, _lastExpireUtc, now,
                                                              ExpireMinIntervalMs, out int delayMs);
                switch (verdict)
                {
                    case ExpireVerdict.Now:
                        _emitted = outputs;
                        _lastExpireUtc = now;
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        request = true;
                        generation = _generation;
                        break;
                    case ExpireVerdict.Defer:
                        // restart, never stack: one timer, the latest deadline wins
                        _timer.Change(delayMs, Timeout.Infinite);
                        break;
                }
            }
            if (!request) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                lock (_lock) { if (_disposed || generation != _generation) return; }
                ExpireRequested?.Invoke();
            }));
        }

        void OnTimer(object? state) => Decide();

        // ---- facts -------------------------------------------------------------------

        static ConsoleFacts Collect(MachineSession session)
        {
            var log = session.LogSnapshot(out long sequence);
            return new ConsoleFacts
            {
                Connection = Map(session.State),
                Snapshot = session.Latest,
                Transport = session.ControlKind.ToString(),
                CanSimulate = session.Capabilities.Simulate,
                LastReply = session.LastReply,
                LastError = session.LastError,
                Log = log,
                LogSequence = sequence,
                ArmConfigurationKnown = session.ArmConfiguration.Known,
                // 3.0.0: the facts the window's buttons must agree with. TrustRefusalReason
                // is what stops the console offering a button the transaction would refuse;
                // the home-macro availability feeds the DuetHoming preflight predicate.
                TrustReason = session.TrustRefusalReason(),
                HomeProbeComplete = session.HomeProbeComplete,
                HomeAvailableAll = session.HomeAvailable("all"),
                HomeAvailableX = session.HomeAvailable("X"),
                HomeAvailableY = session.HomeAvailable("Y"),
                HomeAvailableZ = session.HomeAvailable("Z"),
                BoardType = session.BoardType,
                Legacy = session.Legacy,
                KinematicsProbed = session.KinematicsProbed,
                MotionBaselineProbed = session.MotionBaselineProbed,
                // 3.0.0 logging workspace: retention status is process-wide; layer lines
                // belong to this session's retained job
                Logging = LogRetentionService.Instance.Status,
                JobLayers = session.RetentionLayerLines(),
                // E-Stop recovery: the session owns these facts because the recovery
                // transaction re-derives its decision from the same values inside the
                // command lock. The console only projects them.
                Recovery = session.Recovery,
                SessionId = session.SessionId,
                ExpectedFingerprint = session.ExpectedFingerprint?.Value ?? "",
                ExpectationConflict = session.ExpectationConflict,
                // Job identity: the session's tracker is the truth about what is RUNNING.
                // The prepared job stays the component's, because it exists before any
                // session does.
                JobsObserved = true,
                ActiveJob = session.Jobs.Active,
            };
        }

        static ConsoleConnection Map(DuetConnState state) => state switch
        {
            DuetConnState.Connecting => ConsoleConnection.Connecting,
            DuetConnState.Connected => ConsoleConnection.Connected,
            DuetConnState.Reconnecting => ConsoleConnection.Reconnecting,
            DuetConnState.Error => ConsoleConnection.Error,
            _ => ConsoleConnection.Disconnected,
        };
    }
}
