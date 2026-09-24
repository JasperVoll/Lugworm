using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Duet
{
    public enum DuetConnState { Disconnected, Connecting, Connected, Reconnecting, Error }

    [Flags]
    public enum SessionChangeKind
    {
        None = 0,
        Connection = 1,
        Status = 2,
        Job = 4,
        Reply = 8,
        // All predates print-log updates and deliberately keeps its value at 15.
        All = 15,
        PrintLog = 16,
    }

    public enum PrintLogOwnership
    {
        None,
        Owner,
        Secondary,
    }

    public sealed class SessionChange
    {
        public readonly SessionChangeKind Kind;
        public readonly ObjectModelSnapshot? Snapshot;
        public SessionChange(SessionChangeKind kind, ObjectModelSnapshot? snapshot)
        { Kind = kind; Snapshot = snapshot; }
    }

    /// <summary>One shared, serialized Duet connection per canonical host. The poll
    /// loop owns reconnect/disconnect; commands obtain the same lock before taking
    /// a client reference, so a reconnect cannot invalidate a command mid-acquire.</summary>
    public sealed class MachineSession : IDisposable
    {
        public string Host { get; }
        string _password;
        int _pollMs = 1000;
        int _pollRequestedMs = 1000;
        IMachineClient? _client;
        CancellationTokenSource? _cts;
        Task? _loop;
        readonly SemaphoreSlim _cmdLock = new SemaphoreSlim(1, 1);
        readonly object _lifeLock = new object();
        readonly object _logLock = new object();
        readonly object _configurationLock = new object();
        readonly object _printLogLock = new object();
        readonly List<string> _log = new List<string>();
        long _logSeq;   // total lines ever appended (the list itself is capped at LogCap)
        readonly PrintLogRecorder _printLog = new PrintLogRecorder();
        /// <summary>3.0.0 job-log retention: a second, always-on recorder whose records
        /// feed the staging buffer of <see cref="LogRetentionService"/> for EVERY job,
        /// independently of the Duet Live component's own configurable log.</summary>
        readonly PrintLogRecorder _retentionLog = new PrintLogRecorder();
        readonly object _retentionLock = new object();
        string? _retentionJobName;
        readonly List<PrintLogRegistration> _printLogOwners = new List<PrintLogRegistration>();
        readonly List<PrintLogRecord> _printLogPending = new List<PrintLogRecord>();
        const int LogCap = 200;
        public const int PrintLogConnectionLossSeconds = 60;
        volatile ObjectModelSnapshot? _latest;
        /// <summary>Acquisition id of the snapshot currently published in <see cref="_latest"/>.
        /// The poll loop and a gated command both produce snapshots; without this an
        /// acquisition that STARTED earlier but finished later could replace a newer one
        /// and hand every consumer a position from the past.</summary>
        long _publishedAcquisition;
        volatile DuetConnState _state = DuetConnState.Disconnected;
        volatile string? _lastError;
        volatile string? _lastReply;
        int _pollCount;
        /// <summary>Monotonic poll-cycle counter, incremented as each cycle STARTS. It is
        /// the causal identity a caller needs to say "a poll that began after my command
        /// completed": ObjectModelSnapshot.TakenUtc is stamped when parsing finishes, so a
        /// timestamp cannot express that, and a counter is immune to clock adjustments.</summary>
        long _pollGeneration;
        /// <summary>Wakes ONLY the healthy-cadence wait. Deliberately not the session
        /// cancellation token: the poll loop leaves on a cancelled delay, so sharing one
        /// would turn every "poll now" into a disconnect and would strip the reconnect
        /// backoff, which observes the token alone.</summary>
        readonly SemaphoreSlim _pollWake = new SemaphoreSlim(0, 1);
        readonly object _focusLock = new object();
        readonly HashSet<object> _focusOwners = new HashSet<object>();
        bool _restartRequested;
        bool _closing;
        Task? _disposeTask;
        /// <summary>The reconnect failure last written to the log: one that repeats on every
        /// backoff round is logged once, a different one again.</summary>
        string? _lastReconnectFailure;
        /// <summary>Set once the release of a held poll, respectively the retention
        /// recorder, threw. Both run on the poll path, so each fault is logged once per
        /// session, and one source cannot hide the other.</summary>
        int _heldPollFaultLogged;
        int _recordingFaultLogged;
        ArmConfiguration _armConfiguration = ArmConfiguration.Unknown;
        HomeMacroPlan? _pendingHomeMacro;
        bool _homeInProgress;
        string? _initiatedJob;
        long _initiatedStatusTransitionUntilTicks;
        PrintLogSink? _printLogSink;
        bool _printLogFileOpen;
        string? _printLogPath;
        string? _printLogError;
        string? _lastLoggedPrintLogError;
        string? _printLogFolder;
        string _printLogPrefix = "printlog";
        string? _printLogPendingFolder;
        string _printLogPendingPrefix = "printlog";
        DateTime _printLogPendingStartUtc;
        int _printLogPendingPolls;
        int _printLogFailureCount;
        DateTime? _reconnectingSinceUtc;
        bool _printLogConnectionLossFinished;

        sealed class PrintLogRegistration
        {
            public readonly Guid Owner;
            public PrintLogOptions Options;
            public string? Folder;
            public string Prefix;

            public PrintLogRegistration(Guid owner, PrintLogOptions options,
                                        string? folder, string prefix)
            {
                Owner = owner;
                Options = options;
                Folder = folder;
                Prefix = prefix;
            }
        }

        public MachineSession(string host, string password)
        {
            Host = host; _password = password;
            _retentionLog.Configure(new PrintLogOptions(true, 5, 50000, 0));
        }

        /// <summary>The transport this session speaks: Duet RRF HTTP.</summary>
        public RobotControlKind ControlKind => RobotControlKind.Duet;

        /// <summary>What the connected transport can do; the Duet capabilities until a
        /// client exists, so button gating never has to null-check.</summary>
        public MachineCapabilities Capabilities => _client?.Capabilities ?? MachineCapabilities.For(ControlKind);

        // ---- machine identity trust (AUD-SAF-004) ---------------------------
        MachineFingerprint? _expectedFingerprint;
        volatile MachineTrust _trust = MachineTrust.Unknown;
        readonly ConcurrentDictionary<Guid, MachineFingerprint> _persistedExpectations =
            new ConcurrentDictionary<Guid, MachineFingerprint>();
        volatile string? _expectationConflict;

        /// <summary>The identity probed on the current connection (null before a probe).</summary>
        public MachineFingerprint? CurrentFingerprint => _client?.Fingerprint;
        public MachineFingerprint? ExpectedFingerprint => _expectedFingerprint;
        public MachineTrust Trust => _trust;
        public string? FingerprintProbeDetail => _client?.FingerprintProbeDetail;

        /// <summary>Installs the expectation persisted in the .gh document. Re-evaluates
        /// immediately when already connected.</summary>
        public void SetExpectedFingerprint(MachineFingerprint? expected)
        {
            _expectedFingerprint = expected;
            var client = _client;
            if (client != null && _state == DuetConnState.Connected) EvaluateTrust(client);
        }

        /// <summary>Installs one owner's persisted expectation. Every owner sharing this
        /// host must agree: equal expectations collapse to one consensus, disagreement
        /// fails closed as an explicit conflict - never last-writer-wins (AUD-SAF-004).</summary>
        public void InstallPersistedExpectation(Guid owner, MachineFingerprint expected)
        {
            _persistedExpectations[owner] = expected;
            RecomputeExpectationConsensus();
        }

        /// <summary>Drops an owner's expectation when its lease ends - a removed
        /// component or closed document must stop constraining the shared session.</summary>
        public void ForgetPersistedExpectation(Guid owner)
        {
            if (_persistedExpectations.TryRemove(owner, out _)) RecomputeExpectationConsensus();
        }

        /// <summary>Non-null while the owners sharing this host persist conflicting
        /// machine identities; overrides every trust verdict until resolved.</summary>
        public string? ExpectationConflict => _expectationConflict;

        void RecomputeExpectationConsensus()
        {
            MachineFingerprint? consensus =
                MachineTrustEvaluator.Consensus(_persistedExpectations.Values, out string? conflict);
            bool changed = !string.Equals(conflict, _expectationConflict, StringComparison.Ordinal);
            _expectationConflict = conflict;
            if (conflict != null)
            {
                if (changed)
                {
                    AppendLog("MACHINE IDENTITY CONFLICT: " + conflict);
                    RaiseChanged(SessionChangeKind.Status);
                }
                return;
            }
            if (consensus != null) SetExpectedFingerprint(consensus);
            else if (changed) RaiseChanged(SessionChangeKind.Status);
        }

        /// <summary>The operator's explicit acceptance of the connected machine: the
        /// probed fingerprint becomes the new expectation.</summary>
        public void TrustConnectedMachine()
        {
            var current = CurrentFingerprint;
            if (current == null) return;
            // explicit acceptance overrides every persisted copy; the Connect
            // components re-adopt the new expectation on their next solve
            _persistedExpectations.Clear();
            _expectationConflict = null;
            _expectedFingerprint = current;
            _trust = MachineTrust.Trusted;
            AppendLog($"machine identity trusted by the operator: {current.Display}");
            RaiseChanged(SessionChangeKind.Status);
        }

        // ---- E-Stop recovery (2026-09-09) ------------------------------------------

        /// <summary>Identity of this session object. An operator recovery permission is
        /// bound to it, so a re-leased or re-created session never inherits one.</summary>
        public string SessionId { get; } = Guid.NewGuid().ToString("N");

        readonly object _recoveryLock = new object();
        RecoveryState _recovery = RecoveryState.None;
        long _actionEpoch;
        /// <summary>Cancels commands that are queued on, or running inside, the command
        /// lock. Replaced (never re-armed) after each cancellation, so the commands issued
        /// AFTER a recovery are not born cancelled.</summary>
        CancellationTokenSource _commandAbort = new CancellationTokenSource();

        /// <summary>The session's authoritative recovery facts. The console projects
        /// them; the recovery transaction re-derives its decision from them inside the
        /// command lock, so the window can never offer what the transaction refuses.</summary>
        public RecoveryState Recovery { get { lock (_recoveryLock) return _recovery; } }

        /// <summary>Monotonic identity of the newest started action. An emergency stop
        /// advances it, which is how a superseded command's completion is recognised.</summary>
        public long ActionEpoch => Interlocked.Read(ref _actionEpoch);

        void UpdateRecovery(Func<RecoveryState, RecoveryState> step)
        {
            RecoveryState before, after;
            lock (_recoveryLock)
            {
                before = _recovery;
                after = step(before) ?? before;
                _recovery = after;
            }
            if (!ReferenceEquals(before, after)) RaiseChanged(SessionChangeKind.Status);
        }

        /// <summary>Cancels every command queued on or running inside the command lock and
        /// opens a new action epoch. An HTTP request already on the wire cannot be
        /// recalled, but its continuation stops, its completion is recognised as stale,
        /// and nothing that was still waiting for the lock ever dispatches.</summary>
        public long CancelQueuedCommands(string reason)
        {
            CancellationTokenSource previous;
            lock (_lifeLock)
            {
                previous = _commandAbort;
                _commandAbort = new CancellationTokenSource();
            }
            // best effort: a previous abort source that some other path already disposed
            // has, by definition, already cancelled everything linked to it.
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            // Deliberately NOT disposed: linked sources created from its token are still
            // alive in the commands being cancelled, and disposing a parent out from under
            // them is exactly the race this method exists to end. It holds no timer.
            long epoch = Interlocked.Increment(ref _actionEpoch);
            AppendLog("cancelling any queued or in-flight machine command - " + reason);
            return epoch;
        }

        /// <summary>Records the operator's emergency stop and clears the command queue
        /// BEFORE the M112 goes out, so a motion command that was waiting its turn cannot
        /// dispatch into a machine that is being stopped. Returns the new action epoch.</summary>
        public long BeginEmergencyStop()
        {
            long epoch = CancelQueuedCommands("emergency stop");
            // Exactly what ResetContext will report later, so the binding compares like
            // with like: an unprobed machine records "" and the recovery is then bound to
            // the session and the host alone - never to some other identity's value.
            string fingerprint = _expectedFingerprint?.Value ?? "";
            UpdateRecovery(r => RecoveryReducer.StopRequested(r, Host, SessionId, fingerprint,
                                                              ConnectionGeneration, DateTime.UtcNow));
            return epoch;
        }

        /// <summary>Null when state-changing actions may proceed; otherwise the
        /// operator-actionable refusal. Checked at presentation time AND inside every
        /// command transaction.</summary>
        public string? TrustRefusalReason()
            => _expectationConflict
               ?? MachineTrustEvaluator.RefusalReason(_trust, _expectedFingerprint, CurrentFingerprint);

        void EvaluateTrust(IMachineClient client)
        {
            var trust = MachineTrustEvaluator.Evaluate(_expectedFingerprint, client.Fingerprint);
            if (trust == MachineTrust.FirstConnection)
            {
                // a user-initiated first connection establishes the baseline
                _expectedFingerprint = client.Fingerprint;
                AppendLog($"machine identity recorded: {client.Fingerprint!.Display}");
            }
            else if (trust == MachineTrust.Mismatch)
                AppendLog($"MACHINE IDENTITY MISMATCH: expected {_expectedFingerprint?.Display}, " +
                          $"connected {client.Fingerprint?.Display} - state-changing actions are blocked");
            else if (trust == MachineTrust.Unavailable)
                AppendLog("machine identity could not be probed (" +
                          (client.FingerprintProbeDetail ?? "no detail") + ") - state-changing actions are blocked");
            _trust = trust;
            RaiseChanged(SessionChangeKind.Status);
        }

        public ObjectModelSnapshot? Latest => _latest;

        /// <summary>The one publication gate for <see cref="Latest"/>.</summary>
        /// <remarks>
        /// <para>Two producers exist: the poll loop and <see cref="RunGatedCommandAsync"/>,
        /// which takes its own fresh snapshot inside the command lock. They are not
        /// ordered against each other, so an acquisition that started earlier could
        /// complete later and overwrite a newer one - every consumer would then be handed
        /// a position from the past with no way to notice.</para>
        /// <para>Publication is therefore monotonic in the acquisition id. A refused
        /// snapshot is not discarded: the caller still acts on it (a gate must decide on
        /// the state it actually read) and the recorders still see their own poll-local
        /// copy; it simply does not become the published one.</para>
        /// <para>A snapshot with no acquisition id - a transport that stamps none, or a
        /// hand-built one in a test - publishes unconditionally, exactly as before.</para>
        /// </remarks>
        bool TryPublishLatest(ObjectModelSnapshot snapshot)
        {
            long id = snapshot.AcquisitionId;
            if (id <= 0) { _latest = snapshot; return true; }
            while (true)
            {
                long seen = Interlocked.Read(ref _publishedAcquisition);
                if (id <= seen) return false;
                if (Interlocked.CompareExchange(ref _publishedAcquisition, id, seen) == seen)
                {
                    _latest = snapshot;
                    return true;
                }
            }
        }

        public DuetConnState State => _state;
        public string? LastError => _lastError;
        public string? LastReply => _lastReply;
        public string? BoardType => _client?.BoardType;
        public bool Legacy => _client?.Legacy ?? false;
        public bool LegacyM208Probed => _client?.LegacyM208Probed ?? false;
        public bool KinematicsProbed => _client?.KinematicsProbed ?? false;
        public string? KinematicsProbeDetail => _client?.KinematicsProbeDetail;
        public M669Parameters? Kinematics => _client?.Kinematics;
        public FirmwareMotionLimits? MotionBaseline => _client?.MotionBaseline;
        public bool MotionBaselineProbed => _client?.MotionBaselineProbed ?? false;
        public bool HomeProbeComplete => _client?.HomeProbeComplete ?? false;
        public bool HomeAvailable(string axis) => _client?.HomeAvailable(axis) ?? true;
        public long ConnectionGeneration => _client?.ConnectionGeneration ?? 0;
        /// <summary>Identity of the run the always-on retention recorder is logging; "" when
        /// nothing is printing. Live uses it so its drawn runs split exactly where the job
        /// log's do, and so two prints of one file never join into a single trail.</summary>
        public string RetentionRunId
        {
            get { lock (_retentionLock) return _retentionLog.CurrentRunId; }
        }
        /// <summary>The job identities this session knows about: the one being prepared and
        /// the one actually printing. Editing the Console input during a print changes only
        /// the prepared job, so the active run keeps the bytes it started with.</summary>
        public JobIdentityTracker Jobs { get; } = new JobIdentityTracker();

        // ---- job archive context (retention region; set by the Console before a Start) ----
        volatile ReprintContext? _jobContext;
        volatile bool _jobDryRun;

        /// <summary>The machine and profile the next run is made on, recorded beside its
        /// log so a later reprint can say what has changed since. The Console sets it; the
        /// session only carries it into the archive.</summary>
        public void SetJobArchiveContext(ReprintContext? machine, bool dryRun)
        { _jobContext = machine; _jobDryRun = dryRun; }

        public PrintLogRecorder PrintLog => _printLog;
        public string? PrintLogPath
        {
            get { lock (_printLogLock) return _printLogPath; }
        }
        public string? PrintLogError
        {
            get
            {
                lock (_printLogLock)
                    return _printLogError ?? _printLogSink?.LastError;
            }
        }
        /// <summary>Rows the sink discarded because its queue was full. Non-zero means
        /// the CSV is NOT a complete record of the run.</summary>
        public long PrintLogRowsDropped
        {
            get { lock (_printLogLock) return _printLogSink?.RowsDropped ?? 0; }
        }
        public ArmConfiguration ArmConfiguration
        {
            get { lock (_configurationLock) return _armConfiguration; }
        }
        public event Action<SessionChange>? Changed;

        /// <summary>Raised after EVERY completed poll cycle, with the generation that
        /// cycle started as. Unlike <see cref="Changed"/> - which suppresses
        /// <see cref="SessionChangeKind.None"/> - this fires even when the snapshot is
        /// byte-identical to the previous one, so a caller can prove that an
        /// acknowledgement poll actually happened.</summary>
        public event Action<long, ObjectModelSnapshot>? PollCompleted;

        /// <summary>The newest poll generation to have STARTED.</summary>
        public long PollGeneration => Interlocked.Read(ref _pollGeneration);

        long _lastPollRoundTripTicks;
        /// <summary>Request start to snapshot receipt of the most recent completed poll,
        /// ms. The live display uses it to bound a sample's age conservatively - the
        /// reported position is at least that old - without pretending to know when the
        /// firmware actually sampled it.</summary>
        public double LastPollRoundTripMs
            => Interlocked.Read(ref _lastPollRoundTripTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        long _completedPollWorkTicks, _completedPollWorkGeneration;
        /// <summary>Latest completed cycle's work, including replies, diagnostics,
        /// logging and synchronous subscribers; excludes the cadence wait. Read by the
        /// next poll callback, so the current callback's own cost is included.</summary>
        public double LastCompletedPollWorkMs
            => Interlocked.Read(ref _completedPollWorkTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        public long CompletedPollWorkGeneration => Interlocked.Read(ref _completedPollWorkGeneration);

        void RecordCompletedPollWork(long generation, long started)
        {
            Interlocked.Exchange(ref _completedPollWorkTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Exchange(ref _completedPollWorkGeneration, generation);
        }

        /// <summary>Milliseconds since a <see cref="System.Diagnostics.Stopwatch"/> timestamp.
        /// Used by the poll loop to spend the configured interval start to start rather than
        /// adding it to the cycle's own work.</summary>
        static double ElapsedMsSince(long timestamp)
            => (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp) * 1000.0
             / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>Asks the healthy poll loop to poll now instead of waiting out the rest
        /// of the current interval. Only ever shortens a wait - it starts no send path and
        /// cannot affect the reconnect backoff. Safe to call from any thread; a capacity of
        /// one coalesces bursts, and a leftover permit costs at most one extra poll.</summary>
        public void RequestPollSoon()
        {
            try { if (_pollWake.CurrentCount == 0) _pollWake.Release(); }
            catch (SemaphoreFullException) { /* already signalled - nothing to do */ }
            catch (ObjectDisposedException) { /* session is going away */ }
        }

        /// <summary>Registers a console window as focused. Focus is owner-keyed so two
        /// consoles on one host cannot blur each other's fast cadence off: the fast rate
        /// applies while ANY registered owner has focus.</summary>
        public void RegisterFocusOwner(object owner)
        {
            if (owner == null) return;
            bool changed;
            lock (_focusLock) changed = _focusOwners.Add(owner);
            if (changed) RequestPollSoon();
        }

        /// <summary>Clears a focus registration (blur, hide, host rebind, dispose,
        /// disconnect). Unknown owners are ignored, so callers may clear defensively.</summary>
        public void ClearFocusOwner(object owner)
        {
            if (owner == null) return;
            lock (_focusLock) _focusOwners.Remove(owner);
        }

        /// <summary>True while at least one registered console window has focus.</summary>
        public bool AnyOwnerFocused
        {
            get { lock (_focusLock) return _focusOwners.Count > 0; }
        }
        // Cap below StateGate's freshness window (3 s) so at least one poll always
        // lands inside it; a 4 s cadence let the newest snapshot age out between
        // polls and gate every Home/jog action.
        /// <summary>The shortest and longest interval the session will actually run at.
        /// Public because a display that reports a cadence has to be able to say that the
        /// number the operator configured was clamped, rather than printing the request as
        /// if it had been honoured (2026-09-09: "50 ms" was neither reached nor reported).</summary>
        public const int MinPollIntervalMs = 250, MaxPollIntervalMs = 2000;
        public int PollIntervalMs
        {
            get => _pollMs;
            set
            {
                _pollRequestedMs = value;
                _pollMs = Math.Max(MinPollIntervalMs, Math.Min(MaxPollIntervalMs, value));
            }
        }
        /// <summary>The interval as CONFIGURED, before the clamp above. Kept so the clamp is
        /// reportable; it is never used to schedule anything.</summary>
        public int RequestedPollIntervalMs => _pollRequestedMs;
        /// <summary>Non-null when the configured interval was clamped, naming both numbers.</summary>
        public string? PollClampNote
            => _pollRequestedMs == _pollMs ? null
             : "requested " + _pollRequestedMs.ToString(CultureInfo.InvariantCulture)
               + " ms clamped to " + _pollMs.ToString(CultureInfo.InvariantCulture)
               + " ms (the session's supported range is "
               + MinPollIntervalMs.ToString(CultureInfo.InvariantCulture) + "-"
               + MaxPollIntervalMs.ToString(CultureInfo.InvariantCulture) + " ms)";
        public void SetPassword(string password) => _password = password;

        public PrintLogOwnership ConfigurePrintLog(Guid owner, PrintLogOptions options,
                                                   string? folder, string prefix)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            bool notify = false;
            string? message = null;
            PrintLogOwnership ownership = PrintLogOwnership.None;
            lock (_printLogLock)
            {
                int index = FindPrintLogOwnerLocked(owner);
                if (!options.Enabled)
                {
                    if (index >= 0)
                    {
                        _printLogOwners.RemoveAt(index);
                        if (_printLogOwners.Count == 0)
                        {
                            notify = FinishAndDisablePrintLogLocked(
                                PrintLogFinishReason.LoggingDisabled, out message);
                        }
                        else
                        {
                            ApplyActivePrintLogLocked();
                        }
                    }
                }
                else
                {
                    var registration = new PrintLogRegistration(
                        owner, CopyPrintLogOptions(options), NormalizeFolder(folder),
                        NormalizePrintLogPrefix(prefix));
                    if (index >= 0) _printLogOwners[index] = registration;
                    else _printLogOwners.Add(registration);
                    ApplyActivePrintLogLocked();
                    ownership = _printLogOwners[0].Owner == owner
                        ? PrintLogOwnership.Owner
                        : PrintLogOwnership.Secondary;
                }
            }

            PublishPrintLogMessage(message);
            if (notify) RaiseChanged(SessionChangeKind.PrintLog);
            return ownership;
        }

        public void ReleasePrintLog(Guid owner)
        {
            bool notify = false;
            string? message = null;
            lock (_printLogLock)
            {
                int index = FindPrintLogOwnerLocked(owner);
                if (index < 0) return;
                _printLogOwners.RemoveAt(index);
                if (_printLogOwners.Count == 0)
                {
                    notify = FinishAndDisablePrintLogLocked(
                        PrintLogFinishReason.LoggingDisabled, out message);
                }
                else
                {
                    ApplyActivePrintLogLocked();
                }
            }

            PublishPrintLogMessage(message);
            if (notify) RaiseChanged(SessionChangeKind.PrintLog);
        }

        public void InvalidateArmConfiguration(string? reason = null, bool preservePendingHome = false)
        {
            bool changed;
            lock (_configurationLock)
            {
                changed = _armConfiguration.Known || _pendingHomeMacro != null;
                _armConfiguration = ArmConfiguration.Unknown;
                if (!preservePendingHome)
                {
                    _pendingHomeMacro = null;
                    _homeInProgress = false;
                }
            }
            if (reason != null) AppendLog("arm configuration unknown: " + reason);
            if (changed) RaiseChanged(SessionChangeKind.Status);
        }

        public void SetArmConfiguration(ArmConfiguration configuration)
        {
            lock (_configurationLock)
            {
                _armConfiguration = configuration.Known
                    ? configuration
                    : ArmConfiguration.Unknown;
            }
            RaiseChanged(SessionChangeKind.Status);
        }

        public void MarkInitiatedJob(string remotePath)
        {
            lock (_configurationLock) _initiatedJob = remotePath;
            MarkInitiatedStatusTransition();
        }

        /// <summary>True when this session submitted the start of <paramref name="file"/>.</summary>
        bool InitiatedJobMatches(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            string? initiated;
            lock (_configurationLock) initiated = _initiatedJob;
            return initiated != null && string.Equals(file!.Trim(), initiated.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public void ClearInitiatedJob()
        {
            lock (_configurationLock) _initiatedJob = null;
            InvalidateArmConfiguration("job ended or was cancelled");
        }

        const string HomeAllMacroPath = "0:/sys/homeall.g";

        /// <summary>Reads the live macro immediately before a G28 action. This
        /// does not establish a configuration: Phase 4 must supply the verified
        /// endstop signal first.
        ///
        /// homeall.g may delegate the per-axis work with `M98 P"0:/sys/homeX.g"`
        /// - the lab board was rewritten that way on 2026-08-25 - so the whole
        /// include tree is downloaded first and the parse follows the calls. A
        /// macro that cannot be fetched fails the read: skipping it would hide
        /// motion, and the homing trigger itself now lives inside the callees.</summary>
        public async Task PrepareHomeConfigurationAsync(IMachineClient client, CancellationToken ct)
        {
            try
            {
                string rootText = await DownloadMacroTextAsync(client, HomeAllMacroPath, ct)
                    .ConfigureAwait(false);
                var macros = await DownloadIncludedMacrosAsync(client, rootText, ct)
                    .ConfigureAwait(false);
                if (!HomeMacroReader.TryParse(rootText,
                        path => macros.TryGetValue(path, out var included) ? included : null,
                        out var plan, out string error))
                    throw new DuetException(error);
                lock (_configurationLock)
                {
                    _pendingHomeMacro = plan;
                    _homeInProgress = true;
                    _armConfiguration = ArmConfiguration.Unknown;
                }
            }
            catch (DuetException)
            {
                InvalidateArmConfiguration("homeall.g unreadable or unsupported");
                throw;
            }
            catch (Exception ex)
            {
                InvalidateArmConfiguration("homeall.g unreadable or unsupported: " + Flatten(ex));
                throw new DuetException("home refused - live homeall.g could not be read safely: " + Flatten(ex), ex);
            }
        }

        static async Task<string> DownloadMacroTextAsync(IMachineClient client, string path,
                                                         CancellationToken ct)
        {
            byte[] bytes = await client.DownloadAsync(path, ct).ConfigureAwait(false);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                     throwOnInvalidBytes: true).GetString(bytes);
        }

        /// <summary>Breadth-first fetch of every macro the home tree calls, keyed by
        /// the reader's canonical path. Bounded by HomeMacroReader.MaxMacroFiles so a
        /// cyclic or runaway set cannot spin the downloader.</summary>
        static async Task<Dictionary<string, string>> DownloadIncludedMacrosAsync(
            IMachineClient client, string rootText, CancellationToken ct)
        {
            var macros = new Dictionary<string, string>(StringComparer.Ordinal);
            var pending = new Queue<string>(HomeMacroReader.ExtractIncludes(rootText));
            while (pending.Count > 0)
            {
                string path = pending.Dequeue();
                if (macros.ContainsKey(path)) continue;
                if (macros.Count >= HomeMacroReader.MaxMacroFiles)
                    throw new DuetException(
                        $"homeall.g pulls in more than {HomeMacroReader.MaxMacroFiles} macro files");
                string text = await DownloadMacroTextAsync(client, "0:" + path, ct)
                    .ConfigureAwait(false);
                macros[path] = text;
                foreach (string nested in HomeMacroReader.ExtractIncludes(text))
                    pending.Enqueue(nested);
            }
            return macros;
        }

        /// <summary>Replays the prepared home macro after verified endstop completion
        /// and establishes the tracked arm configuration. The caller supplies the
        /// machine's endstop-determined arm mode, not an operator preference.</summary>
        public bool TryEstablishArmConfigurationAfterVerifiedHome(bool verifiedHome,
                                                                  Vec3 verifiedTriggerPose,
                                                                  RobotLimits limits,
                                                                  int homedArmMode)
        {
            HomeMacroPlan? macro;
            lock (_configurationLock) macro = _pendingHomeMacro;
            // Phase 4: currently no verified-home signal is available, so this
            // branch is never entered by the shipped call site.
            if (!verifiedHome)
            {
                lock (_configurationLock)
                {
                    _armConfiguration = ArmConfiguration.Unknown;
                    _homeInProgress = true;
                }
                AppendLog("arm configuration unknown: verified home signal is not available");
                RaiseChanged(SessionChangeKind.Status);
                return false;
            }
            if (macro == null)
            {
                InvalidateArmConfiguration("homeall.g was not prepared");
                return false;
            }

            var points = macro.Replay(verifiedTriggerPose);
            points.Insert(0, verifiedTriggerPose);
            var check = RobotChecks.RunPoints(points, limits,
                ArmConfiguration.Seed(homedArmMode));
            if (!check.ReachOk || !check.FinalConfiguration.Known)
            {
                InvalidateArmConfiguration("homeall.g park replay is not safely resolvable");
                return false;
            }
            SetArmConfiguration(check.FinalConfiguration);
            lock (_configurationLock) _homeInProgress = false;
            return true;
        }

        public void Start()
        {
            lock (_lifeLock)
            {
                if (_closing) return;
                if (_loop != null && !_loop.IsCompleted)
                {
                    if (_cts != null && _cts.IsCancellationRequested) _restartRequested = true;
                    return;
                }
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _restartRequested = false;
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token), token);
            }
        }

        public void Stop()
        {
            lock (_lifeLock)
            {
                _restartRequested = false;
                _cts?.Cancel();
            }
        }

        async Task RunAsync(CancellationToken ct)
        {
            _reconnectingSinceUtc = null;
            _lastReconnectFailure = null;
            _printLogConnectionLossFinished = false;
            InvalidateArmConfiguration("connection started");
            _glitchFilter.Reset();
            SetState(DuetConnState.Connecting, null);
            IMachineClient client;
            try { client = MachineClientFactory.Create(ControlKind, Host, _password); }
            catch (Exception ex)
            {
                SetState(DuetConnState.Error, Flatten(ex));
                AppendLog("connect failed: " + Flatten(ex));
                return;
            }
            _client = client;
            client.OpaqueMotionIssued += OnOpaqueMotionIssued;
            AttachDiagnostics(client);
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                await client.BeginReplyPollingAsync(ct).ConfigureAwait(false);
                InvalidateArmConfiguration("connected, home verification is required");
                AppendLog($"connected to {Host} ({client.BoardType ?? "unknown board"}, api {client.ApiLevel})");
                if (!client.MotionBaselineProbed)
                    AppendLog("motion baseline unverified - " + client.Capabilities.MotionBaselineSource + " could not be read; motion-limit checks are skipped");
                else
                {
                    AppendLog("motion baseline verified from " + client.Capabilities.MotionBaselineSource);
                    foreach (string warning in client.MotionBaseline!.Warnings)
                        AppendLog("motion baseline note: " + warning);
                }
                EvaluateTrust(client);
                SetState(DuetConnState.Connected, null);
                int backoffMs = 1000;
                DrainPollWake();
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Stamp the generation as the cycle STARTS: a caller waiting for
                        // "a poll that began after my command completed" compares against
                        // the value it read at completion time.
                        long generation = Interlocked.Increment(ref _pollGeneration);
                        long pollStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        var previous = _latest;
                        var lastStatus = previous?.Status ?? MachineStatus.Unknown;
                        bool includeJob = lastStatus == MachineStatus.Processing || lastStatus == MachineStatus.Simulating
                                       || lastStatus == MachineStatus.Paused || lastStatus == MachineStatus.Pausing
                                       || lastStatus == MachineStatus.Resuming || lastStatus == MachineStatus.Cancelling
                                       || (_pollCount++ % 5) == 0;
                        var snap = await client.GetSnapshotAsync(includeJob, ct).ConfigureAwait(false);
                        lock (_diagnosticsLock) { snap.RawProbe = _lastCoordinateBody; _lastCoordinateBody = null; }
                        Interlocked.Exchange(ref _lastPollRoundTripTicks,
                            System.Diagnostics.Stopwatch.GetTimestamp() - pollStarted);
                        _reconnectingSinceUtc = null;
                        _printLogConnectionLossFinished = false;
                        string? reply = await client.FetchNewReplyAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(reply)) { _lastReply = reply; AppendLog(reply!); }
                        string? drained = client.TakeDrainedReply();
                        if (!string.IsNullOrEmpty(drained)) { _lastReply = drained; AppendLog("firmware (during 503 backoff): " + drained); }
                        snap.LastReply = _lastReply;
                        // A gated command may have published a NEWER acquisition while this
                        // poll was in flight; the gate keeps that one. The recorders below
                        // still observe this poll-local snapshot in poll order.
                        bool published = TryPublishLatest(snap);
                        backoffMs = 1000;
                        ObserveSnapshot(previous, snap);
                        // Position consumers see a poll only once the single-axis reply
                        // glitch rule has run on it: a candidate waits for its successor,
                        // everything else passes at once (AxisGlitchFilter). The retention
                        // recorder goes first so the diagnostic record carries the poll's
                        // continuity verdict and a suspect one arms the capture.
                        bool printLogChanged = false;
                        var released = _glitchFilter.Push(snap);
                        for (int r = 0; r < released.Count; r++)
                        {
                            var observed = released[r].Snapshot;
                            ObserveRetention(observed);
                            ObserveDiagnostics(observed);
                            printLogChanged |= SafePrintLog(() => ObservePrintLog(observed));
                        }
                        if (!published)
                        {
                            // Report the poll, but never as a position: publishing this
                            // snapshot would move every consumer backwards in time.
                            RaiseChanged(printLogChanged ? SessionChangeKind.PrintLog : SessionChangeKind.None);
                            SetState(DuetConnState.Connected, null);
                            RecordCompletedPollWork(generation, pollStarted);
                            int wait = PollCadencePolicy.WaitAfterWork(
                                PollCadencePolicy.Decide(AnyOwnerFocused,
                                    PollCadencePolicy.IsActive(snap.Status), _pollMs),
                                ElapsedMsSince(pollStarted));
                            bool awake = await _pollWake.WaitAsync(wait, ct).ConfigureAwait(false);
                            if (PollCadencePolicy.Interpret(awake, ct.IsCancellationRequested) == PollWaitOutcome.Cancelled)
                                break;
                            continue;
                        }
                        var change = Compare(previous, snap);
                        if (printLogChanged) change |= SessionChangeKind.PrintLog;
                        // If the previous snapshot aged past the freshness window before
                        // this one replaced it, the UI may have latched a disabled button
                        // layout (StateGate refuses stale state). A fresh but content-
                        // identical poll yields Compare==None and nothing re-enables the
                        // buttons - force a Status relayout so freshness recovery lands.
                        if (previous != null &&
                            (snap.TakenUtc - previous.TakenUtc).TotalSeconds > StateGate.DefaultMaxAgeSeconds)
                            change |= SessionChangeKind.Status;
                        RaiseChanged(change);
                        // Fires even when Compare said nothing changed - RaiseChanged
                        // suppresses SessionChangeKind.None, so it cannot prove a poll ran.
                        for (int r = 0; r < released.Count; r++)
                            RaisePollCompleted(generation, released[r].Snapshot);
                        SetState(DuetConnState.Connected, null);

                        // The configured interval is a TARGET START-TO-START period, not an
                        // idle added after the cycle's work: at 300 ms the operator asked for
                        // a coordinate observation about every 300 ms, and a poll whose
                        // requests took 200 ms therefore waits 100, not 300. Requests still
                        // cannot overlap - this loop awaits them and only then waits - and
                        // WaitAfterWork keeps a floor so the loop always yields.
                        RecordCompletedPollWork(generation, pollStarted);
                        int effective = PollCadencePolicy.WaitAfterWork(
                            PollCadencePolicy.Decide(AnyOwnerFocused,
                                PollCadencePolicy.IsActive(snap.Status), _pollMs),
                            ElapsedMsSince(pollStarted));
                        // The semaphore's TIMED overload, never Task.WhenAny against a
                        // Task.Delay: there the losing waiter stays queued and silently
                        // swallows the next wake, and cancelling the loser is unsafe
                        // because this catch treats OperationCanceledException as shutdown.
                        bool woken = await _pollWake.WaitAsync(effective, ct).ConfigureAwait(false);
                        if (PollCadencePolicy.Interpret(woken, ct.IsCancellationRequested) == PollWaitOutcome.Cancelled)
                            break;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        bool printLogChanged = SafePrintLog(FinishPrintLogAfterConnectionLoss);
                        if (printLogChanged) RaiseChanged(SessionChangeKind.PrintLog);
                        client.EndReplyPolling();
                        // The exact transport failure, and a capture of the polls around it.
                        // The 2026-09-09 capture recorded only "connection interrupted",
                        // which is why that reconnect could not be ruled in or out.
                        FlushGlitchFilter();
                        NoteDiagnostic("transport failure: " + ex.GetType().FullName + ": " + Flatten(ex), trigger: true);
                        InvalidateArmConfiguration("connection interrupted");
                        // The machine's status is now unreadable. Record WHY, so the
                        // console can say what actually failed instead of leaving Reset
                        // greyed out with "not connected" (the 2026-09-09 incident).
                        UpdateRecovery(r => RecoveryReducer.TransportLost(r, Flatten(ex)));
                        _trust = MachineTrust.Unknown;   // reconnect must reprobe identity (AUD-SAF-004)
                        SetState(DuetConnState.Reconnecting, Flatten(ex));
                        try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                        backoffMs = Math.Min(backoffMs * 2, 10000);
                        try
                        {
                            InvalidateArmConfiguration("reconnecting");
                            await client.ConnectAsync(ct).ConfigureAwait(false);
                            await client.BeginReplyPollingAsync(ct).ConfigureAwait(false);
                            InvalidateArmConfiguration("reconnected, home verification is required");
                            EvaluateTrust(client);
                            AppendLog("reconnected");
                            _lastReconnectFailure = null;
                            NoteDiagnostic("reconnect completed on connection generation "
                                + client.ConnectionGeneration.ToString(CultureInfo.InvariantCulture));
                            if (!client.MotionBaselineProbed)
                                AppendLog("motion baseline unverified - " + client.Capabilities.MotionBaselineSource + " could not be read; motion-limit checks are skipped");
                            else
                            {
                                AppendLog("motion baseline verified from " + client.Capabilities.MotionBaselineSource);
                                foreach (string warning in client.MotionBaseline!.Warnings)
                                    AppendLog("motion baseline note: " + warning);
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        // best effort: one reconnect attempt. Broad because the failure can
                        // come from the transport, the identity probe or the baseline read.
                        // The next poll reports a different error (not connected), so the
                        // real reason (a 401, a refused identity) is logged here, once per
                        // distinct reason rather than on every backoff round.
                        catch (Exception reconnectEx)
                        {
                            string reason = Flatten(reconnectEx);
                            if (!string.Equals(reason, _lastReconnectFailure, StringComparison.Ordinal))
                            {
                                _lastReconnectFailure = reason;
                                AppendLog("reconnect failed: " + reason);
                            }
                        }
                    }
                }
            }
            // Cancellation IS the ordinary way this loop ends (Stop/Dispose), so it leaves
            // the state alone; anything else is a fault and is reported as one.
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetState(DuetConnState.Error, Flatten(ex));
                AppendLog("connect failed: " + Flatten(ex));
            }
            finally
            {
                FinishRetention(PrintLogFinishReason.SessionStopped);
                bool printLogChanged = SafePrintLog(
                    () => FinishPrintLog(PrintLogFinishReason.SessionStopped));
                if (printLogChanged) RaiseChanged(SessionChangeKind.PrintLog);
                SafePrintLog(() => { DisposePrintLogWriter(); return false; });
                FlushDiagnostics();
                client.EndReplyPolling();
                client.OpaqueMotionIssued -= OnOpaqueMotionIssued;
                DetachDiagnostics(client);
                InvalidateArmConfiguration("disconnected");
                _trust = MachineTrust.Unknown;
                await CloseClientAsync(client).ConfigureAwait(false);
                lock (_lifeLock)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                    if (!_closing) SetState(DuetConnState.Disconnected, _lastError);
                    if (_restartRequested && !_closing)
                    {
                        _restartRequested = false;
                        _cts?.Dispose();
                        _cts = new CancellationTokenSource();
                        var token = _cts.Token;
                        _loop = Task.Run(() => RunAsync(token), token);
                    }
                }
                client.Dispose();
            }
        }

        void OnOpaqueMotionIssued()
        {
            InvalidateArmConfiguration("opaque firmware macro issued");
        }

        /// <summary>Seconds we allow between accepting a command and seeing the machine
        /// actually leave Idle. Only this gap is time-bounded; once motion is observed
        /// the window is kept alive by the motion itself, so a long jog cannot outlive
        /// it, and a command that never moves cannot hold authorization open for long.</summary>
        const int InitiatedPendingSeconds = 30;

        /// <summary>Opens the authorization window. The firmware keeps moving after
        /// rr_gcode is acknowledged - a jog returns on acceptance, not on completion -
        /// so a command's status edges keep arriving after the HTTP call returns.</summary>
        void MarkInitiatedStatusTransition()
            => Interlocked.Exchange(ref _initiatedStatusTransitionUntilTicks,
                DateTime.UtcNow.AddSeconds(InitiatedPendingSeconds).Ticks);

        bool InitiatedWindowLive()
            => Interlocked.Read(ref _initiatedStatusTransitionUntilTicks) >= DateTime.UtcNow.Ticks;

        /// <summary>1 once the machine has been observed non-idle since the command
        /// began, which is what distinguishes "our motion is still running" from
        /// "our command never moved anything".</summary>
        int _initiatedMotionSeen;

        /// <summary>Call IMMEDIATELY before submitting a command that will move the
        /// machine - and only then. Authorization must mean "we submitted motion", not
        /// "a wrapper is running": the generic command wrappers also run for uploads,
        /// directory listings and validations that never move anything, and they run
        /// even when the action refuses before sending. Arming there let an external
        /// DWC move that happened to fall inside the window pass as ours.</summary>
        public void MarkInitiatedMotion()
        {
            Volatile.Write(ref _initiatedMotionSeen, 0);
            MarkInitiatedStatusTransition();
        }

        /// <summary>Peek, never consume: every edge belonging to an initiated command is
        /// authorised, not just the first one - but only the edges a command of ours
        /// actually produces.
        ///
        /// An open window is NOT blanket permission. While one of our prints runs, the
        /// window is kept alive by the motion itself, and authorising every edge inside
        /// it would let an external DWC pause or cancel pass as ours - and cancel.g runs
        /// G28, physically moving the machine while the tracked arm configuration stayed
        /// "known". So outside an in-flight command only two shapes are accepted: the
        /// machine leaving Idle (our motion starting) and the machine returning to Idle
        /// (our motion completing). Pausing / Paused / Resuming / Cancelling / Halted
        /// arriving on their own are foreign by definition and invalidate.</summary>
        bool InitiatedStatusTransitionAuthorized(ObjectModelSnapshot? previous,
                                                 ObjectModelSnapshot current)
        {
            if (!InitiatedWindowLive()) return false;
            if (current.Status == MachineStatus.Idle) return true;
            return previous != null && previous.Status == MachineStatus.Idle;
        }

        /// <summary>Runs on EVERY snapshot, not only on a status change. Closing the
        /// window only from the transition path left it open after a command whose
        /// motion had already finished (verified homing waits for Idle inside the
        /// action), so a subsequent external DWC move would have both of its edges
        /// authorised and the tracked configuration would go stale.</summary>
        void ObserveInitiatedMotion(ObjectModelSnapshot current)
        {
            if (current.Status != MachineStatus.Idle)
            {
                Volatile.Write(ref _initiatedMotionSeen, 1);
                // Keep our own motion authorised for as long as it actually runs - a jog
                // or print outlives any fixed window - but never arm it for motion we did
                // not start. Holding the window open is safe only because
                // InitiatedStatusTransitionAuthorized still refuses pause/cancel/halt
                // edges; the window buys the completing return-to-Idle, nothing more.
                if (InitiatedWindowLive()) MarkInitiatedStatusTransition();
            }
            else if (Volatile.Read(ref _initiatedMotionSeen) == 1)
            {
                Interlocked.Exchange(ref _initiatedStatusTransitionUntilTicks, 0);
                Volatile.Write(ref _initiatedMotionSeen, 0);
            }
        }

        // ---- bounded raw-telemetry diagnostic window (2026-09-09) -------------------

        readonly object _diagnosticsLock = new object();
        RawTelemetryWindow? _diagnostics;
        RawBodyProbe? _lastCoordinateBody;
        /// <summary>Repairs the single-axis machinePosition glitch the 2026-09-09 capture
        /// localized to the firmware reply (Core, poll thread only). Position consumers -
        /// retention, diagnostics, the print log, PollCompleted - see a poll only after it
        /// ran; status facts see every poll at once.</summary>
        readonly AxisGlitchFilter _glitchFilter = new AxisGlitchFilter();
        public AxisGlitchFilter GlitchFilter => _glitchFilter;
        long _diagnosticPoll;

        /// <summary>The window, created on first use. Its sink hands the finished capture to
        /// the retention worker, which is the only thread that writes to disk; the poll
        /// loop only ever enqueues.</summary>
        RawTelemetryWindow Diagnostics
        {
            get
            {
                lock (_diagnosticsLock)
                {
                    return _diagnostics ??= new RawTelemetryWindow(capture =>
                    {
                        var service = LogRetentionService.Instance;
                        foreach (string line in capture.ToJsonLines()) service.AppendDiagnostic(line);
                    });
                }
            }
        }

        /// <summary>Subscribes to the transport's pre-parse body seam. Only a transport that
        /// offers it participates; the window still records the parsed observation for the
        /// others, so the stages that ARE available can still be lined up.</summary>
        void AttachDiagnostics(IMachineClient client)
        {
            if (client is DuetPollClient duet) duet.RawBodyObserved += OnRawBody;
        }

        void DetachDiagnostics(IMachineClient client)
        {
            if (client is DuetPollClient duet) duet.RawBodyObserved -= OnRawBody;
        }

        void OnRawBody(RawBodyProbe probe)
        {
            // Only the request that carries coordinates dates the observation; the job,
            // thermal and file-info reads around it must not move its timestamps.
            if (probe.Endpoint.IndexOf("move.axes", StringComparison.Ordinal) < 0
                && probe.Endpoint.IndexOf("rr_status", StringComparison.Ordinal) < 0) return;
            lock (_diagnosticsLock) _lastCoordinateBody = probe;
        }

        /// <summary>Records one raw observation and, when the continuity policy called it
        /// suspect, arms a capture of the polls around it. Runs on the poll thread and does
        /// no I/O.</summary>
        void ObserveDiagnostics(ObjectModelSnapshot snapshot)
        {
            try
            {
                var window = Diagnostics;
                RawBodyProbe? probe = snapshot.RawProbe;
                var stamp = snapshot.Acquisition;
                var arm = _armConfiguration;
                var record = new RawTelemetryRecord
                {
                    AcquisitionId = snapshot.AcquisitionId,
                    PollId = Interlocked.Increment(ref _diagnosticPoll),
                    ConnectionGeneration = snapshot.ConnectionGeneration,
                    RequestUtc = probe?.RequestUtc ?? stamp?.RequestStartedUtc ?? default(DateTime),
                    ReceivedUtc = probe?.ReceivedUtc ?? stamp?.ResponseReceivedUtc ?? snapshot.TakenUtc,
                    Endpoint = probe?.Endpoint ?? "",
                    ApiMode = probe?.ApiMode ?? (Legacy ? "rr_status" : "rr_model"),
                    Body = probe?.Body ?? "",
                    Page = probe?.Page ?? 1,
                    PositionValidity = snapshot.PositionValidity.ToString(),
                    PositionInvalidReason = snapshot.PositionInvalidReason,
                    MachinePosition = snapshot.MachinePosition,
                    UserPosition = snapshot.UserPosition,
                    AllHomed = snapshot.AllHomed,
                    HomedFlags = HomedFlags(snapshot),
                    MachineStatus = snapshot.Status.ToString(),
                    JobFile = snapshot.Job?.FileName,
                    FilePosition = snapshot.Job?.FilePosition,
                    FractionPrinted = snapshot.Job?.FractionPrinted,
                    RequestedSpeedMms = snapshot.RequestedSpeed,
                    TopSpeedMms = snapshot.TopSpeed,
                    ArmMode = arm.Known ? arm.ArmMode : -1,
                    ArmKnown = arm.Known,
                };
                TrailQuality quality;
                lock (_retentionLock)
                {
                    var verdict = _retentionLog.LastVerdict;
                    quality = verdict.Quality;
                    record.Quality = quality.ToString();
                    record.SegmentId = verdict.SegmentId;
                    record.RunId = _retentionLog.CurrentRunId;
                }
                lock (_diagnosticsLock)
                {
                    window.Add(record);
                    if (quality == TrailQuality.SuspectPathDisagreement || quality == TrailQuality.SuspectSpeed
                        || quality == TrailQuality.Invalid || quality == TrailQuality.Discontinuity)
                        window.Trigger(quality.ToString(), record.ReceivedUtc);
                }
            }
            catch (Exception) { /* diagnostics must never break polling */ }
        }

        /// <summary>A poll the glitch filter was holding gets no successor (transport
        /// failure): release it unrepaired to every position consumer, in order.</summary>
        void FlushGlitchFilter()
        {
            try
            {
                var released = _glitchFilter.Flush();
                for (int r = 0; r < released.Count; r++)
                {
                    var observed = released[r].Snapshot;
                    ObserveRetention(observed);
                    ObserveDiagnostics(observed);
                    SafePrintLog(() => ObservePrintLog(observed));
                }
            }
            // never into the poll loop's failure path, but not silently either: the held
            // poll it could not release is missing from the job log and the trail
            catch (Exception ex) { NoteSwallowed("releasing a held poll", ex, ref _heldPollFaultLogged); }
        }

        /// <summary>One log line for a fault a poll-path catch has to swallow, at most once
        /// per session: these run on every poll, and a persistent fault must neither flood
        /// the log nor pass unnoticed.</summary>
        void NoteSwallowed(string what, Exception ex, ref int logged)
        {
            if (Interlocked.Exchange(ref logged, 1) != 0) return;
            // best effort: this is itself a catch path, and the log is the only sink left
            try { AppendLog("job log: " + what + " failed: " + Flatten(ex) + ". Later failures in this session are not logged again"); }
            catch (Exception) { }
        }

        /// <summary>Records a non-poll event - a transport exception, a reconnect
        /// completion, an operator request - into the window and, for a transport failure,
        /// arms a capture around it.</summary>
        public void NoteDiagnostic(string note, bool trigger = false)
        {
            try
            {
                var window = Diagnostics;
                var record = new RawTelemetryRecord
                {
                    PollId = Interlocked.Increment(ref _diagnosticPoll),
                    ConnectionGeneration = ConnectionGeneration,
                    ReceivedUtc = DateTime.UtcNow,
                    ApiMode = Legacy ? "rr_status" : "rr_model",
                    Note = note,
                };
                lock (_diagnosticsLock)
                {
                    window.Add(record);
                    if (trigger) window.Trigger(note, record.ReceivedUtc);
                }
            }
            // best effort: a diagnostics note is evidence, never a reason to fail the
            // caller - this runs on the poll and command paths.
            catch (Exception) { }
        }

        /// <summary>Hands an armed but unfinished capture to the worker, so a job or session
        /// that ends inside the post-trigger window still keeps the evidence it has.</summary>
        void FlushDiagnostics()
        {
            try { lock (_diagnosticsLock) _diagnostics?.Flush(); }
            // best effort: the flush runs on job-end and teardown paths; a capture that
            // cannot be written is evidence lost, never a reason to fail the shutdown.
            catch (Exception) { }
        }

        static string HomedFlags(ObjectModelSnapshot snapshot)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var axis in snapshot.Axes)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(axis.Letter).Append(axis.Homed ? "+" : "-");
            }
            return sb.ToString();
        }

        void ObserveSnapshot(ObjectModelSnapshot? previous, ObjectModelSnapshot current)
        {
            if (!current.AllHomed)
            {
                bool preserve;
                lock (_configurationLock) preserve = _homeInProgress;
                InvalidateArmConfiguration("an axis is unhomed", preservePendingHome: preserve);
            }

            string? initiated;
            lock (_configurationLock) initiated = _initiatedJob;
            var job = current.Job;
            string fileName = job?.FileName ?? "";
            if (job != null && (initiated == null || fileName.Trim().Length == 0))
                InvalidateArmConfiguration("foreign or uninitiated job detected");
            else if (job != null && initiated != null
                     && !string.Equals(fileName.Trim(), initiated.Trim(), StringComparison.OrdinalIgnoreCase))
                InvalidateArmConfiguration("foreign or uninitiated job detected");

            if (previous != null && previous.Status != current.Status)
            {
                if (!InitiatedStatusTransitionAuthorized(previous, current))
                    InvalidateArmConfiguration("uninitiated firmware status transition");
            }
            // AFTER the transition check, so the completing Busy->Idle edge is still
            // authorised by the window it then closes.
            ObserveInitiatedMotion(current);
            ObserveRecovery(current);
        }

        /// <summary>Folds one completed poll into the recovery facts: a real Halted/Off
        /// status is what turns "E-STOP acknowledged" into "halt observed", and a live
        /// machine on a NEW connection generation with an acceptable identity is what
        /// turns "reset sent" into "recovery verified". Never the HTTP result.</summary>
        void ObserveRecovery(ObjectModelSnapshot current)
        {
            var before = Recovery;
            if (before.IsQuiet && !before.HomingRequired) return;
            var observation = new RecoveryObservation
            {
                Connection = _state == DuetConnState.Connected ? ConsoleConnection.Connected : ConsoleConnection.Reconnecting,
                Status = current.Status,
                ConnectionGeneration = ConnectionGeneration,
                TrustReason = TrustRefusalReason(),
                AllHomed = current.AllHomed,
            };
            UpdateRecovery(r => RecoveryReducer.Observed(r, observation));
        }

        int FindPrintLogOwnerLocked(Guid owner)
        {
            for (int i = 0; i < _printLogOwners.Count; i++)
                if (_printLogOwners[i].Owner == owner) return i;
            return -1;
        }

        void ApplyActivePrintLogLocked()
        {
            if (_printLogOwners.Count == 0)
            {
                _printLog.Configure(new PrintLogOptions { Enabled = false });
                _printLogFolder = null;
                _printLogPrefix = "printlog";
                return;
            }

            var active = _printLogOwners[0];
            _printLogFolder = active.Folder;
            _printLogPrefix = active.Prefix;
            _printLog.Configure(CopyPrintLogOptions(active.Options));
        }

        static PrintLogOptions CopyPrintLogOptions(PrintLogOptions options)
            => new PrintLogOptions
            {
                Enabled = options.Enabled,
                SampleEveryNPolls = options.SampleEveryNPolls,
                MaxRecords = options.MaxRecords,
                LayerHeightHint = options.LayerHeightHint,
            };

        static string? NormalizeFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return folder!.Trim();
        }

        static string NormalizePrintLogPrefix(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return "printlog";
            return prefix!.Trim();
        }

        bool ObservePrintLog(ObjectModelSnapshot snapshot)
        {
            bool emitted = false;
            string? message = null;
            lock (_printLogLock)
            {
                if (!_printLog.Active)
                {
                    // The sink is asynchronous, so an open/append/close failure can
                    // surface after the run was finished or logging was switched off.
                    // Returning early here would drop that error on the floor forever.
                    message = CapturePrintLogErrorLocked();
                    if (message != null)
                    {
                        PublishPrintLogMessage(message);
                        return true;
                    }
                    return false;
                }
                try
                {
                    bool pendingAtStart = _printLogPending.Count > 0;
                    if (pendingAtStart) _printLogPendingPolls++;
                    var records = _printLog.Observe(snapshot);
                    bool recordHasName = false;
                    foreach (var record in records)
                    {
                        if (!string.IsNullOrWhiteSpace(record.JobFile))
                        {
                            recordHasName = true;
                            break;
                        }
                    }
                    if (pendingAtStart && _printLogPendingPolls >= 10 && !recordHasName)
                        OpenPendingPrintLogWriterLocked(null);

                    RoutePrintLogRecordsLocked(records);
                    emitted = records.Count > 0;

                    // File-name adoption may not itself emit a record when interval
                    // sampling is disabled. The snapshot still supplies the trigger.
                    if (_printLogPending.Count > 0 && records.Count == 0)
                    {
                        string? adopted = NonEmptyPrintLogFile(snapshot.Job?.FileName);
                        if (adopted != null) OpenPendingPrintLogWriterLocked(adopted);
                        else if (_printLogPendingPolls >= 10)
                            OpenPendingPrintLogWriterLocked(null);
                    }
                    _printLogFailureCount = 0;
                    message = CapturePrintLogErrorLocked();
                }
                catch (Exception ex)
                {
                    message = HandlePrintLogFailureLocked(ex);
                }
            }

            PublishPrintLogMessage(message);
            // A poll that emitted nothing can still have produced a NEW sink error, and
            // the component only re-solves when a change is raised - otherwise the
            // failure would sit unseen on logInfo until something else moved.
            return emitted || message != null;
        }

        bool FinishPrintLog(PrintLogFinishReason reason)
        {
            bool emitted;
            string? message;
            lock (_printLogLock)
            {
                emitted = FinishPrintLogLocked(reason, out message);
            }
            PublishPrintLogMessage(message);
            return emitted;
        }

        /// <summary>Last line of defence for the print log. The methods below already
        /// guard their own bodies, but an escape from here would be caught by the poll
        /// loop's generic handler, which invalidates the arm configuration and enters
        /// reconnect backoff - a logging bug must never be able to do that.</summary>
        bool SafePrintLog(Func<bool> action)
        {
            try { return action(); }
            catch (Exception ex)
            {
                // best effort: this IS the reporting path - if the log sink itself throws
                // there is nowhere left to say so, and rethrowing would defeat the guard.
                try { AppendLog("print log suppressed: " + Flatten(ex)); } catch (Exception) { }
                return false;
            }
        }

        bool FinishPrintLogAfterConnectionLoss()
        {
            DateTime now = DateTime.UtcNow;
            if (!_reconnectingSinceUtc.HasValue)
            {
                _reconnectingSinceUtc = now;
                _printLogConnectionLossFinished = false;
                return false;
            }
            if (_printLogConnectionLossFinished
                || (now - _reconnectingSinceUtc.Value).TotalSeconds < PrintLogConnectionLossSeconds)
                return false;

            _printLogConnectionLossFinished = true;
            FinishRetention(PrintLogFinishReason.ConnectionLost);
            return FinishPrintLog(PrintLogFinishReason.ConnectionLost);
        }

        // ---- 3.0.0 job-log retention (every job, staged locally, kept on request) ----

        /// <summary>Feeds the retention recorder; records only ever enqueue to the
        /// service worker, so a stalled destination cannot touch this loop.</summary>
        void ObserveRetention(ObjectModelSnapshot snapshot)
        {
            try
            {
                IReadOnlyList<PrintLogRecord> records;
                lock (_retentionLock) records = _retentionLog.Observe(snapshot);
                RouteRetention(records);
            }
            // logging must never break polling, but a recorder that keeps failing would end
            // the always-on job log without a word: say so, once per session
            catch (Exception ex) { NoteSwallowed("recording", ex, ref _recordingFaultLogged); }
        }

        void RouteRetention(IReadOnlyList<PrintLogRecord> records)
        {
            if (records.Count == 0) return;
            var service = LogRetentionService.Instance;
            foreach (var record in records)
            {
                if (record.Kind == PrintLogEventKind.JobStart)
                {
                    _retentionJobName = record.JobFile;
                    service.BeginJob(record.JobFile ?? "");
                    service.AppendRecord(PrintLogCsv.Header());
                    service.AppendEvent("job started" + (record.JobFile != null ? ": " + record.JobFile : ""));
                    // Job identity and the immutable archive. A run this session started is
                    // already ACTIVE (the Start transaction promoted the prepared snapshot);
                    // anything else appeared from the machine's own screen or another client
                    // and its bytes are unknown - never the Console's input in their place.
                    // The poll can see the job start before the M32 reply returns: a job
                    // this session INITIATED for the same file is its prepared snapshot,
                    // not an external run (2026-09-09 13:17 run recorded a console start
                    // as "bytes unknown").
                    var active = Jobs.Active
                        ?? (InitiatedJobMatches(record.JobFile) ? Jobs.StartPrepared(DateTime.UtcNow) : null)
                        ?? Jobs.StartExternal(record.JobFile, DateTime.UtcNow);
                    byte[]? bytes = active.IntendedPathAvailable ? active.GcodeBytes() : null;
                    // Enqueued: the archive is written by the retention worker, never here
                    // in the poll loop.
                    service.ArchiveJobGcode(bytes, active.RunId, _jobContext, _jobDryRun);
                    if (bytes == null)
                        service.AppendEvent("g-code bytes unknown for this run - it was not started from this console, "
                            + "so Recent prints lists it without a reprint");
                }
                else if (!string.IsNullOrWhiteSpace(record.JobFile)
                         && !string.Equals(record.JobFile, _retentionJobName, StringComparison.Ordinal))
                {
                    _retentionJobName = record.JobFile;
                    service.NameJob(record.JobFile!);
                }
                service.AppendRecord(PrintLogCsv.Row(record));
                if (record.Kind == PrintLogEventKind.JobEnd)
                {
                    service.AppendEvent("job ended" + (record.TerminalStatus != null ? " (" + record.TerminalStatus + ")" : "")
                        + (record.FinishReason.HasValue ? " - " + record.FinishReason.Value : ""));
                    // manifest.complete only means the LOG finished. The outcome is what
                    // says whether the PRINT did: the 2026-09-09 run was halted by M112 and
                    // still finalised its log exactly like a completed one.
                    string outcome = record.TerminalStatus
                                  ?? (record.FinishReason.HasValue ? record.FinishReason.Value.ToString() : "Unknown");
                    service.SetJobOutcome(outcome);
                    // An armed capture that has not filled its post window is still evidence.
                    FlushDiagnostics();
                    service.CompleteJob();
                    // The run is over: the ACTIVE job goes, the PREPARED one survives - it
                    // is what a later Start would run.
                    Jobs.EndRun();
                    _retentionJobName = null;
                }
            }
        }

        /// <summary>Readable layer lines of the current/last retained job, newest last.</summary>
        public IReadOnlyList<string> RetentionLayerLines()
        {
            IReadOnlyList<PrintLogLayerSpan> spans;
            lock (_retentionLock) spans = _retentionLog.LayerSpans();
            var lines = new List<string>(spans.Count);
            foreach (var span in spans) lines.Add(PrintLogText.LayerLine(span, localTime: true));
            return lines;
        }

        void FinishRetention(PrintLogFinishReason reason)
        {
            try
            {
                IReadOnlyList<PrintLogRecord> records;
                lock (_retentionLock) records = _retentionLog.Finish(reason);
                RouteRetention(records);
            }
            catch (Exception ex)
            {
                // Once per job end, not per poll: say so in the session log rather than
                // leaving the operator to discover the truncated run in the buffer. The
                // caller is still never failed - this runs on the poll and teardown paths.
                try { AppendLog("job log: the run could not be closed cleanly - " + Flatten(ex)); }
                catch (Exception) { /* best effort: the log sink itself is gone */ }
            }
        }

        bool FinishPrintLogLocked(PrintLogFinishReason reason, out string? message)
        {
            message = null;
            try
            {
                var records = _printLog.Finish(reason);
                RoutePrintLogRecordsLocked(records);
                message = CapturePrintLogErrorLocked();
                return records.Count > 0 || message != null;
            }
            catch (Exception ex)
            {
                message = HandlePrintLogFailureLocked(ex);
                return false;
            }
        }

        bool FinishAndDisablePrintLogLocked(PrintLogFinishReason reason, out string? message)
        {
            bool emitted = FinishPrintLogLocked(reason, out message);
            try { _printLog.Configure(new PrintLogOptions { Enabled = false }); }
            catch (Exception ex)
            {
                string? extra = HandlePrintLogFailureLocked(ex);
                if (message == null) message = extra;
            }
            ClosePrintLogWriterLocked();
            _printLogPending.Clear();
            _printLogPendingPolls = 0;
            return emitted;
        }

        string? HandlePrintLogFailureLocked(Exception ex)
        {
            _printLogFailureCount++;
            string detail = Flatten(ex);
            string error = "print log failed: " + detail;
            bool repeated = _printLogFailureCount >= 2;
            if (repeated)
            {
                DisablePrintLogLocked();
                error += " (logging disabled after repeated failure)";
            }
            return PublishPrintLogErrorLocked(error);
        }

        void DisablePrintLogLocked()
        {
            try { _printLog.Configure(new PrintLogOptions { Enabled = false }); }
            // best effort: the writer is closed below either way, and the caller is
            // already turning the print log off - there is nothing left to salvage.
            catch (Exception) { }
            ClosePrintLogWriterLocked();
            _printLogPending.Clear();
            _printLogPendingPolls = 0;
        }

        string? CapturePrintLogErrorLocked()
        {
            if (_printLogSink?.LastError != null)
                _printLogError = _printLogSink.LastError;
            return _printLogError == null ? null : PublishPrintLogErrorLocked(_printLogError);
        }

        string? PublishPrintLogErrorLocked(string error)
        {
            _printLogError = error;
            if (string.Equals(_lastLoggedPrintLogError, error, StringComparison.Ordinal)) return null;
            _lastLoggedPrintLogError = error;
            return "print log: " + error;
        }

        void PublishPrintLogMessage(string? message)
        {
            if (!string.IsNullOrEmpty(message)) AppendLog(message!);
        }

        void RoutePrintLogRecordsLocked(IReadOnlyList<PrintLogRecord> records)
        {
            for (int i = 0; i < records.Count; i++)
                RoutePrintLogRecordLocked(records[i]);
        }

        // Every method below only mutates in-memory state and enqueues sink commands.
        // No file handle is touched on the poll thread and no disk I/O happens under
        // _printLogLock - PrintLogSink owns both.
        void RoutePrintLogRecordLocked(PrintLogRecord record)
        {
            if (record.Kind == PrintLogEventKind.JobStart)
            {
                // A correctly formed split is JobEnd followed by JobStart, but
                // force-close any stale deferred state before starting the next run.
                if (_printLogPending.Count > 0)
                {
                    OpenPendingPrintLogWriterLocked(null);
                    ClosePrintLogWriterLocked();
                }
                if (_printLogFileOpen) ClosePrintLogWriterLocked();
                if (_printLogFolder == null) return;

                _printLogPendingFolder = _printLogFolder;
                _printLogPendingPrefix = _printLogPrefix;
                _printLogPendingStartUtc = record.TakenUtc;
                _printLogPendingPolls = 0;
                _printLogPending.Add(record);
                string? name = NonEmptyPrintLogFile(record.JobFile);
                if (name != null) OpenPendingPrintLogWriterLocked(name);
                return;
            }

            if (_printLogPending.Count > 0)
            {
                if (!_printLogFileOpen)
                {
                    string? name = NonEmptyPrintLogFile(record.JobFile);
                    if (name != null) OpenPendingPrintLogWriterLocked(name);
                }
                if (!_printLogFileOpen)
                {
                    _printLogPending.Add(record);
                    if (record.Kind == PrintLogEventKind.JobEnd)
                    {
                        OpenPendingPrintLogWriterLocked(null);
                        ClosePrintLogWriterLocked();
                    }
                    return;
                }

                AppendPrintLogRecordLocked(record);
                if (record.Kind == PrintLogEventKind.JobEnd)
                    ClosePrintLogWriterLocked();
                return;
            }

            if (!_printLogFileOpen) return;
            AppendPrintLogRecordLocked(record);
            if (record.Kind == PrintLogEventKind.JobEnd)
                ClosePrintLogWriterLocked();
        }

        void OpenPendingPrintLogWriterLocked(string? jobFile)
        {
            if (_printLogPending.Count == 0 || _printLogPendingFolder == null) return;
            string path = BuildPrintLogPath(_printLogPendingFolder, _printLogPendingPrefix,
                                            _printLogPendingStartUtc, jobFile);
            var sink = EnsurePrintLogSinkLocked();
            sink.Open(path);
            sink.Append(_printLogPending);
            _printLogFileOpen = true;
            _printLogPath = path;
            _printLogPending.Clear();
            _printLogPendingFolder = null;
            _printLogPendingPolls = 0;
            // Deliberately no CapturePrintLogErrorLocked() here: it returns a one-shot
            // message and swallowing it would mark the error as already reported while
            // nothing ever reported it. The outward operation captures exactly once.
        }

        PrintLogSink EnsurePrintLogSinkLocked()
            => _printLogSink ?? (_printLogSink = new PrintLogSink());

        void AppendPrintLogRecordLocked(PrintLogRecord record)
        {
            if (!_printLogFileOpen || _printLogSink == null) return;
            _printLogSink.Append(record);
        }

        void ClosePrintLogWriterLocked()
        {
            if (!_printLogFileOpen) return;
            _printLogFileOpen = false;
            _printLogSink?.Close();
        }

        static string? NonEmptyPrintLogFile(string? fileName)
            => string.IsNullOrWhiteSpace(fileName) ? null : fileName;

        static string BuildPrintLogPath(string folder, string prefix, DateTime startUtc,
                                        string? jobFile)
        {
            string safePrefix = SanitizePrintLogPart(prefix, "printlog");
            string safeJob = SanitizePrintLogPart(jobFile, "unknown-job");
            string stamp = startUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss",
                                                            CultureInfo.InvariantCulture);
            return Path.Combine(folder, safePrefix + "_" + stamp + "_" + safeJob + ".csv");
        }

        static string SanitizePrintLogPart(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            char[] invalid = Path.GetInvalidFileNameChars();
            string text = value!.Trim();
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 || c == '/' || c == '\\')
                    sb.Append('_');
                else sb.Append(c);
            }
            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? fallback : result;
        }

        void DisposePrintLogWriter()
        {
            PrintLogSink? sink;
            lock (_printLogLock)
            {
                ClosePrintLogWriterLocked();
                _printLogPending.Clear();
                _printLogPendingPolls = 0;
                sink = _printLogSink;
                _printLogSink = null;
            }
            // Disposed outside the lock: it drains the queue and joins the pump thread
            // (bounded), and nothing else may be made to wait on that behind the lock.
            sink?.Dispose();
        }

        async Task CloseClientAsync(IMachineClient client)
        {
            // Disconnect is authenticated and is serialized behind any command
            // already in flight. It is intentionally best effort during teardown -
            // bounded, so a stuck command can delay but never hang it (AUD-SAF-002);
            // on timeout the polite disconnect is skipped and the board's own idle
            // session expiry cleans up.
            bool acquired;
            // best effort: the semaphore is disposed out from under a teardown race
            // (ObjectDisposedException) - there is then no lock to take and no client
            // left to disconnect politely.
            try { acquired = await _cmdLock.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            if (!acquired) return;
            try { await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            // best effort: see the note above - the board expires an abandoned session
            // on its own, so a failed goodbye must not fail teardown.
            catch (Exception) { }
            finally { _cmdLock.Release(); }
        }

        void SetState(DuetConnState state, string? error)
        {
            bool changed = _state != state || _lastError != error;
            _state = state; _lastError = error;
            if (changed) RaiseChanged(SessionChangeKind.Connection);
        }

        void DrainPollWake()
        {
            try { while (_pollWake.CurrentCount > 0 && _pollWake.Wait(0)) { } }
            // best effort: the session is going away, so a leftover permit on a disposed
            // semaphore can no longer cause the extra poll this drain exists to prevent.
            catch (ObjectDisposedException) { }
        }

        void RaisePollCompleted(long generation, ObjectModelSnapshot snapshot)
        {
            if (_closing) return;
            var handlers = PollCompleted;
            if (handlers == null) return;
            foreach (Action<long, ObjectModelSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(generation, snapshot); }
                catch (Exception ex) { AppendLog("poll subscriber failed: " + Flatten(ex)); }
            }
        }

        void RaiseChanged(SessionChangeKind kind, ObjectModelSnapshot? snapshot = null)
        {
            if (_closing) return;
            if (kind == SessionChangeKind.None) return;
            var handlers = Changed;
            if (handlers == null) return;
            var change = new SessionChange(kind, snapshot ?? _latest);
            foreach (Action<SessionChange> handler in handlers.GetInvocationList())
            {
                try { handler(change); } catch (Exception ex) { AppendLog("subscriber failed: " + Flatten(ex)); }
            }
        }

        static SessionChangeKind Compare(ObjectModelSnapshot? oldSnap, ObjectModelSnapshot snap)
        {
            SnapshotDelta delta = ObjectModelDiff.Compare(oldSnap, snap);
            SessionChangeKind kind = SessionChangeKind.None;
            if (delta.HasFlag(SnapshotDelta.Status)) kind |= SessionChangeKind.Status;
            if (delta.HasFlag(SnapshotDelta.Job)) kind |= SessionChangeKind.Job;
            if (delta.HasFlag(SnapshotDelta.Reply)) kind |= SessionChangeKind.Reply;
            return kind;
        }

        public async Task<string> RunCommandAsync(Func<IMachineClient, CancellationToken, Task<string>> action,
                                                  bool stateChanging = true)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            CancellationToken sessionCt;
            CancellationToken abort;
            lock (_lifeLock)
            {
                // _closing before _cts: disposal disposes the token source, and reading it
                // then would put ObjectDisposedException in front of the operator
                if (_closing) throw NotConnected();
                sessionCt = _cts?.Token ?? CancellationToken.None; abort = _commandAbort.Token;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCt, abort);
            var ct = linked.Token;
            try { await _cmdLock.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Aborted(abort, sessionCt)) { throw Cancelled(); }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested) { throw NotConnected(); }
            try
            {
                var client = _client;
                if (_closing || client == null || _state == DuetConnState.Disconnected || _state == DuetConnState.Error)
                    throw NotConnected();
                // identity trust is re-checked inside the serialized transaction so no
                // check-then-send race can slip past a mismatch (AUD-SAF-004)
                if (stateChanging && TrustRefusalReason() is string trustReason)
                    throw new InvalidOperationException(trustReason);
                string reply = await action(client, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply)) { _lastReply = reply; AppendLog(reply); }
                RaiseChanged(SessionChangeKind.Reply);
                RequestPollSoon();   // success only; only ever shortens the next wait
                return reply;
            }
            catch (OperationCanceledException) when (Aborted(abort, sessionCt)) { throw Cancelled(); }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested) { throw NotConnected(); }
            finally { _cmdLock.Release(); }
        }

        /// <summary>True when the emergency stop cancelled this command rather than the
        /// session shutting down - the two must not report the same thing.</summary>
        static bool Aborted(CancellationToken abort, CancellationToken sessionCt)
            => abort.IsCancellationRequested && !sessionCt.IsCancellationRequested;

        static InvalidOperationException Cancelled()
            => new InvalidOperationException("command cancelled by the emergency stop - it was not sent");

        /// <summary>A command on a stopped or disposed session: the bare cancellation
        /// ("The operation was canceled.") says nothing the operator can act on.</summary>
        static InvalidOperationException NotConnected()
            => new InvalidOperationException("not connected - press Connect first");

        public async Task<string> RunGatedCommandAsync(bool includeJob,
            Func<ObjectModelSnapshot, IMachineClient, CancellationToken, Task<string>> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            CancellationToken sessionCt;
            CancellationToken abort;
            lock (_lifeLock)
            {
                if (_closing) throw NotConnected();   // before _cts, as in RunCommandAsync
                sessionCt = _cts?.Token ?? CancellationToken.None; abort = _commandAbort.Token;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCt, abort);
            var ct = linked.Token;
            try { await _cmdLock.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (Aborted(abort, sessionCt)) { throw Cancelled(); }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested) { throw NotConnected(); }
            try
            {
                var client = _client;
                if (_closing || client == null || _state == DuetConnState.Disconnected || _state == DuetConnState.Error)
                    throw NotConnected();
                if (TrustRefusalReason() is string trustReason)
                    throw new InvalidOperationException(trustReason);
                var previous = _latest;
                var snap = await client.GetSnapshotAsync(includeJob, ct).ConfigureAwait(false);
                snap.LastReply = _lastReply;
                // The gate below always decides on THIS snapshot - a command must act on
                // the state it actually read - but it only becomes the published one when
                // it is newer than what a concurrent poll already published.
                bool published = TryPublishLatest(snap);
                string reply = await action(snap, client, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply)) { _lastReply = reply; AppendLog(reply); }
                snap.LastReply = _lastReply;
                if (published) RaiseChanged(Compare(previous, snap) | SessionChangeKind.Reply, snap);
                else RaiseChanged(SessionChangeKind.Reply);
                // Success only, and only ever shortens the next wait: the effect of an
                // accepted command should reach the UI on the next poll, not up to a full
                // interval later.
                RequestPollSoon();
                return reply;
            }
            catch (OperationCanceledException) when (Aborted(abort, sessionCt)) { throw Cancelled(); }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested) { throw NotConnected(); }
            finally { _cmdLock.Release(); }
        }

        public async Task EmergencyStopAsync()
        {
            var client = _client;
            if (client == null)
            {
                // The operator asked for a stop we cannot deliver. Record that as a
                // FAILED stop rather than leaving the recovery facts silent - the
                // console's notice is the only thing that will say "use the switch".
                if (Recovery.StopRequested)
                    UpdateRecovery(r => RecoveryReducer.StopFailed(r, "there is no connection to " + Host));
                throw new InvalidOperationException("no session, so M112 cannot be sent. Use the machine's physical switch");
            }
            // The queue is cleared and the stop recorded before anything goes out, so a
            // motion command that was only waiting its turn can never dispatch into a
            // machine that is being stopped. A caller that already pressed the console's
            // E-STOP has done this; any other caller gets it here.
            if (!Recovery.StopRequested) BeginEmergencyStop();
            AppendLog("!! M112 EMERGENCY STOP");
            try
            {
                await client.EmergencyStopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                UpdateRecovery(r => RecoveryReducer.StopFailed(r, Flatten(ex)));
                throw;
            }
            // An acknowledgement, NOT a verified physical stop: only an observed
            // Halted/Off status supplies that, and ObserveRecovery records it.
            UpdateRecovery(RecoveryReducer.StopAcknowledged);
            RaiseChanged(SessionChangeKind.Reply);
        }

        public async Task ResetAsync(bool maintenanceOverride = false)
        {
            await RunGatedCommandAsync(false, async (snap, client, ct) =>
            {
                bool halted = snap.Status == MachineStatus.Halted || snap.Status == MachineStatus.Off;
                // The armed override exists for RRF's M999. Klipper's FIRMWARE_RESTART aborts a
                // running print and drops the MCU, so the transport's capability decides
                // whether any override is honoured - fail closed, never by transport name.
                if (!halted && maintenanceOverride && !client.Capabilities.MaintenanceReset)
                    throw new InvalidOperationException("firmware reset refused - " + client.Capabilities.Name
                        + " allows it only while the machine is halted/off (no maintenance override)");
                if (!halted && !maintenanceOverride)
                    throw new InvalidOperationException("M999 refused - machine must be halted/off or an armed maintenance override is required");
                AppendLog("firmware reset (M999)");
                MarkInitiatedMotion();
                UpdateRecovery(r => RecoveryReducer.ResetSubmitted(r, ConnectionGeneration));
                var delivery = await client.ResetAsync(ct).ConfigureAwait(false);
                UpdateRecovery(r => RecoveryReducer.ResetDelivered(r, delivery));
                DiscardStateAfterReset();
                return DescribeDelivery(delivery);
            }).ConfigureAwait(false);
        }

        /// <summary>What the operator is told about a reset send. A send whose answer
        /// never arrived must not read as "reset done": the machine coming back on a new
        /// connection is the only confirmation, and the console says so.</summary>
        static string DescribeDelivery(ResetDelivery delivery)
            => delivery.Acknowledged
                ? "firmware reset sent (" + delivery.Detail + ") - reconnect and home before the next move"
                : "firmware reset DELIVERY UNCONFIRMED (" + delivery.Detail
                  + "). It may or may not have reached the board. Recovery counts only when the machine comes back";

        /// <summary>Everything the machine told us before the reset is now void: the
        /// board reboots, the identity must be re-probed on the new connection, the arm
        /// configuration is unknown, and no job is ever resumed on our behalf.</summary>
        void DiscardStateAfterReset()
        {
            _latest = null;
            _trust = MachineTrust.Unknown;
            ClearInitiatedJob();
            InvalidateArmConfiguration("firmware reset - verified homing is required before any motion");
            RequestPollSoon();
        }

        /// <summary>How long the recovery transaction waits for the ordinary command queue
        /// before refusing. Bounded on purpose: a recovery that can be blocked forever by a
        /// stuck motion command is not a recovery.</summary>
        public static readonly TimeSpan RecoveryQueueWait = TimeSpan.FromSeconds(5);

        /// <summary>The console's firmware reset: a bounded recovery transaction, decided
        /// by the SAME <see cref="ResetPolicy"/> the window's button and the Terminal tab
        /// use, with its own prerequisite full object-model poll.
        ///
        /// It is independent of the ordinary motion queue in the ways that matter: it
        /// waits only <see cref="RecoveryQueueWait"/> for the command lock instead of
        /// queueing behind whatever is stuck, and it does not require a readable machine
        /// status when THIS session's emergency stop is what silenced it. What it does not
        /// relax is the host and identity binding - the recovery is bound to the session,
        /// the host and the machine identity recorded at stop time.</summary>
        /// <param name="operatorRecovery">The operator has explicitly armed the blind
        /// recovery (the second press). Ignored while the machine's status is readable.</param>
        public async Task<string> RunRecoveryResetAsync(bool operatorRecovery)
        {
            CancellationToken sessionCt;
            lock (_lifeLock)
            {
                // before _cts, as in RunCommandAsync: a disposed session has no token source
                if (_closing) throw new InvalidOperationException("reset refused: " + NotConnected().Message);
                sessionCt = _cts?.Token ?? CancellationToken.None;
            }

            var decision = ResetPolicy.Decide(ResetContext(null), DateTime.UtcNow);
            if (!decision.Allowed) throw new InvalidOperationException("reset refused: " + decision.Refusal);
            if (decision.Path == ResetPath.OperatorRecovery && !operatorRecovery)
                throw new InvalidOperationException("reset refused: the machine's status is unreadable. "
                    + "Press Reset again to send the recovery reset anyway");

            bool acquired;
            try { acquired = await _cmdLock.WaitAsync(RecoveryQueueWait, sessionCt).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new InvalidOperationException("reset refused: the session is closing"); }
            if (!acquired)
                throw new InvalidOperationException("reset refused: the machine command queue did not clear within "
                    + RecoveryQueueWait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + " s. Press E-stop first, which cancels it");
            try
            {
                var client = _client;
                if (_closing || client == null)
                    throw new InvalidOperationException("no connection to " + Host
                        + ". The reset cannot be delivered. Press Connect first");

                // Prerequisite: a full object-model poll of our own. Success re-decides
                // against the FRESH status, so a machine that came back live in the
                // meantime is treated as connected-and-not-halted, not as a blind
                // recovery. Failure is recorded as a transport fault - the honest reason
                // the operator sees - and only the armed operator recovery goes on.
                MachineStatus observed = MachineStatus.Unknown;
                bool statusReadable = false;
                try
                {
                    var snap = await client.GetSnapshotAsync(true, sessionCt).ConfigureAwait(false);
                    observed = snap.Status;
                    statusReadable = true;
                    var poll = new RecoveryObservation
                    {
                        Connection = ConsoleConnection.Connected,
                        Status = snap.Status,
                        ConnectionGeneration = ConnectionGeneration,
                        TrustReason = TrustRefusalReason(),
                        AllHomed = snap.AllHomed,
                    };
                    UpdateRecovery(r => RecoveryReducer.Observed(r, poll));
                }
                catch (OperationCanceledException) when (sessionCt.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    string why = Flatten(ex);
                    UpdateRecovery(r => RecoveryReducer.TransportLost(r, why));
                    AppendLog("reset preflight poll failed - " + why);
                }

                var confirmed = ResetPolicy.Decide(ResetContext(statusReadable ? observed : (MachineStatus?)null),
                                                   DateTime.UtcNow);
                if (!confirmed.Allowed) throw new InvalidOperationException("reset refused: " + confirmed.Refusal);
                if (confirmed.Path == ResetPath.OperatorRecovery && !operatorRecovery)
                    throw new InvalidOperationException("reset refused: the machine's status is unreadable. "
                        + "Press Reset again to send the recovery reset anyway");

                AppendLog(confirmed.Path == ResetPath.OperatorRecovery
                    ? "recovery reset (" + client.Capabilities.Name + ") - the machine's status is unreadable, sending anyway"
                    : "firmware reset (" + client.Capabilities.Name + ") - machine reported halted/off");
                MarkInitiatedMotion();
                UpdateRecovery(r => RecoveryReducer.ResetSubmitted(r, ConnectionGeneration));
                ResetDelivery delivery;
                try
                {
                    delivery = await client.ResetAsync(sessionCt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The endpoint answered and refused (an auth failure, an error status).
                    // Nothing was reset; say so, and leave the recovery facts standing so
                    // the operator can act again.
                    string why = Flatten(ex);
                    UpdateRecovery(r => RecoveryReducer.ResetDelivered(r, ResetDelivery.Unconfirmed(why)));
                    DiscardStateAfterReset();
                    throw new InvalidOperationException("the machine refused the reset - " + why);
                }
                UpdateRecovery(r => RecoveryReducer.ResetDelivered(r, delivery));
                DiscardStateAfterReset();
                string text = DescribeDelivery(delivery);
                AppendLog(text);
                return text;
            }
            finally { _cmdLock.Release(); }
        }

        /// <summary>The session's own view of the reset rule's inputs. Same shape the
        /// console builds from its projected state, so the two cannot drift.</summary>
        /// <param name="status">The status just polled, or null when nothing answered.</param>
        public ResetContext ResetContext(MachineStatus? status)
            => new ResetContext
            {
                Host = Host,
                SessionId = SessionId,
                ExpectedFingerprint = _expectedFingerprint?.Value ?? "",
                ExpectationConflict = ExpectationConflict,
                Connection = _state switch
                {
                    DuetConnState.Connected => ConsoleConnection.Connected,
                    DuetConnState.Connecting => ConsoleConnection.Connecting,
                    DuetConnState.Reconnecting => ConsoleConnection.Reconnecting,
                    DuetConnState.Error => ConsoleConnection.Error,
                    _ => ConsoleConnection.Disconnected,
                },
                Status = status ?? _latest?.Status ?? MachineStatus.Unknown,
                TrustReason = TrustRefusalReason(),
                PendingAction = null,   // the command lock is the transaction's queue gate
                Recovery = Recovery,
                ResetCommand = "M999",
            };

        /// <summary>Arms the operator's blind recovery (the first Reset press while the
        /// machine's status is unreadable). Pure state: nothing is sent.</summary>
        public void ArmOperatorRecovery()
            => UpdateRecovery(r => RecoveryReducer.ArmOperatorRecovery(r, DateTime.UtcNow));

        public void AppendLog(string line)
        {
            lock (_logLock)
            {
                _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
                _logSeq++;
                if (_log.Count > LogCap) _log.RemoveRange(0, _log.Count - LogCap);
            }
            // the readable events of the job's retained log (ignored while no job is open)
            if (_retentionJobName != null || _retentionLog.Active)
                LogRetentionService.Instance.AppendEvent(DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
        }
        /// <summary>Total lines ever appended: the first retained entry of
        /// <see cref="LogSnapshot()"/> has sequence <c>LogSequence - Count</c>. Lets a
        /// viewer address log lines stably although the list itself is capped.</summary>
        public long LogSequence { get { lock (_logLock) return _logSeq; } }
        public List<string> LogSnapshot() { lock (_logLock) return new List<string>(_log); }
        /// <summary>Log copy and its sequence number taken under one lock, so the pair is consistent.</summary>
        public List<string> LogSnapshot(out long sequence)
        {
            lock (_logLock) { sequence = _logSeq; return new List<string>(_log); }
        }
        public static string Flatten(Exception ex)
        {
            if (ex is AggregateException agg && agg.InnerException != null) ex = agg.InnerException;
            var msg = ex.Message; if (ex.InnerException != null && !msg.Contains(ex.InnerException.Message)) msg += " (" + ex.InnerException.Message + ")"; return msg;
        }

        public Task DisposeAsync()
        {
            lock (_lifeLock)
            {
                if (_disposeTask != null) return _disposeTask;
                _closing = true; _restartRequested = false; _cts?.Cancel();
                _disposeTask = DisposeCoreAsync();
                return _disposeTask;
            }
        }

        async Task DisposeCoreAsync()
        {
            Task? loop;
            lock (_lifeLock) loop = _loop;
            if (loop != null && (!Task.CurrentId.HasValue || loop.Id != Task.CurrentId.Value))
            {
                // best effort: the loop is being cancelled, so OperationCanceledException
                // is the expected outcome and any other fault it ended with was already
                // reported through SetState/AppendLog before it unwound.
                try { await loop.ConfigureAwait(false); } catch (Exception) { }
            }
            // A command not honoring its cancellation token should finish before the
            // client and semaphore are disposed; this prevents ObjectDisposed races.
            // Bounded (AUD-SAF-002): every command path runs under its own request or
            // transfer timeout now, so a holder that outlives this wait is already
            // failing - disposing anyway is the lesser risk than hanging teardown.
            try
            {
                if (await _cmdLock.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
                    _cmdLock.Release();
            }
            // best effort: a second disposal path already disposed the semaphore
            // (ObjectDisposedException); disposal continues either way, as above.
            catch (ObjectDisposedException) { }
            bool printLogChanged = SafePrintLog(
                () => FinishPrintLog(PrintLogFinishReason.SessionStopped));
            if (printLogChanged) RaiseChanged(SessionChangeKind.PrintLog);
            SafePrintLog(() => { DisposePrintLogWriter(); return false; });
            lock (_printLogLock)
            {
                _printLogOwners.Clear();
                _printLog.Configure(new PrintLogOptions { Enabled = false });
                _printLogFolder = null;
                _printLogPrefix = "printlog";
            }
            _cts?.Dispose();
            _state = DuetConnState.Disconnected;
            _client = null;
            NotifyClosed();
        }

        /// <summary>The closing guard in RaiseChanged keeps every subscriber from hearing
        /// the final state, so a console that did not press Disconnect kept showing
        /// Connected until its next solve. One explicit notification once the state has
        /// settled; subscribers only read facts from it.</summary>
        void NotifyClosed()
        {
            var handlers = Changed;
            if (handlers == null) return;
            var change = new SessionChange(SessionChangeKind.Connection, _latest);
            foreach (Action<SessionChange> handler in handlers.GetInvocationList())
            {
                // best effort: a subscriber fault after disposal has nowhere to go but the log
                try { handler(change); } catch (Exception ex) { AppendLog("subscriber failed: " + Flatten(ex)); }
            }
        }

        public void Dispose() { _ = DisposeAsync(); }
    }
}
