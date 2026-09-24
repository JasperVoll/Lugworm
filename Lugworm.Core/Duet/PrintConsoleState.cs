using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    /// <summary>Connection phase of the Print Console session. <see cref="None"/>
    /// means there is no session object at all (nothing to e-stop).</summary>
    public enum ConsoleConnection { None, Disconnected, Connecting, Connected, Reconnecting, Error }

    /// <summary>What the console last uploaded to the board: the remote path, the
    /// content hash and size of the bytes that went over the wire, and the upload
    /// generation counter. Start is only allowed while <see cref="Hash"/> matches
    /// the gcode currently wired into the component.</summary>
    public sealed class UploadRecord
    {
        public string Path { get; }
        public ulong Hash { get; }
        public long Size { get; }
        public long Generation { get; }

        public UploadRecord(string path, ulong hash, long size, long generation)
        {
            Path = path ?? "";
            Hash = hash;
            Size = size;
            Generation = generation;
        }
    }

    /// <summary>The result of the last solve-time validation run, held in the state so
    /// the <c>danger</c>/<c>problems</c>/<c>messages</c> outputs can be projected from
    /// it like every other output. Keyed by the gate hash it was computed for: a
    /// different hash means the projection describes gcode that is no longer wired in,
    /// and the reducer drops it.</summary>
    /// <remarks>Problem points are in the MACHINE frame - Core never learns about the
    /// Rhino frame or document units; the component converts at Emit.</remarks>
    public sealed class ValidationProjection
    {
        /// <summary>The gcode content hash this validation describes.</summary>
        public ulong GateKey { get; }
        /// <summary>True when the job must not run.</summary>
        public bool Danger { get; }
        /// <summary>Violating points, MACHINE frame.</summary>
        public IReadOnlyList<Vec3> Problems { get; }
        public IReadOnlyList<string> Messages { get; }
        /// <summary>True when the run produced motion-limit warnings. The 2.6.0 console
        /// dropped this flag (<c>var (notice, _)</c>), which rendered a warned-but-
        /// successful run as a plain remark.</summary>
        public bool Warned { get; }
        /// <summary>The validated file homes the machine before its first motion (a full
        /// G28), so Start does not require a homed machine or a known arm configuration.</summary>
        public bool HomesFirst { get; }
        /// <summary>Start of the job's first extruding move, MACHINE frame; null when the
        /// job extrudes nothing. <see cref="JobGateResult.StartPoint"/>, projected so the
        /// window can offer "Go to print start" from state alone.</summary>
        public Vec3? StartPoint { get; }

        public ValidationProjection(ulong gateKey, bool danger, IReadOnlyList<Vec3>? problems,
                                    IReadOnlyList<string>? messages, bool warned, bool homesFirst = false,
                                    Vec3? startPoint = null)
        {
            GateKey = gateKey;
            Danger = danger;
            Problems = problems ?? Array.Empty<Vec3>();
            Messages = messages ?? Array.Empty<string>();
            Warned = warned;
            HomesFirst = homesFirst;
            StartPoint = startPoint;
        }
    }

    /// <summary>Immutable view-model of the Print Console. Every mutation goes
    /// through <see cref="PrintConsoleReducer"/>, which returns a fresh instance via
    /// the <c>With…</c> copies; the setters are private so the class stays
    /// net48-compatible without a record/init shim.</summary>
    public sealed class PrintConsoleState
    {
        /// <summary>Upper bound of <see cref="LogTail"/>.</summary>
        public const int LogTailMax = 200;

        public string Host { get; private set; }
        public ConsoleConnection Connection { get; private set; }
        public bool XHomed { get; private set; }
        public bool YHomed { get; private set; }
        public bool ZHomed { get; private set; }
        public bool AllHomed { get; private set; }
        public MachineStatus MachineState { get; private set; }
        public string RawStatus { get; private set; }
        /// <summary>"" when no job is running.</summary>
        public string JobFile { get; private set; }
        /// <summary>0..1.</summary>
        public double Progress { get; private set; }
        public int? Layer { get; private set; }
        /// <summary>Total layers of the running job: its own <c>; layer N/M</c> markers, else the
        /// firmware's count, else null. Assigned on every poll, so a new or ended job clears it.</summary>
        public int? LayerCount { get; private set; }
        /// <summary>Head position in the MACHINE frame, null until the first snapshot with X/Y/Z axes.</summary>
        public Vec3? Position { get; private set; }
        public string LastReply { get; private set; }
        /// <summary>The session log entries with sequence &gt;= <see cref="LogSkip"/>, at most <see cref="LogTailMax"/> (the newest) entries.</summary>
        public IReadOnlyList<string> LogTail { get; private set; }
        /// <summary>Log sequence number (total lines ever appended to the session log)
        /// from which the tail is shown; entries before it were cleared by the user.</summary>
        public long LogSkip { get; private set; }
        /// <summary>Label of the action in flight; null when idle.</summary>
        public string? PendingAction { get; private set; }
        /// <summary>Monotonic identity of the pending action, bumped by every
        /// <see cref="PrintConsoleReducer.ActionStarted"/> - the emergency stop's
        /// included. A completion callback carrying an older epoch belongs to an action
        /// that has been superseded, and must not clear a newer action's pending state.</summary>
        public long ActionEpoch { get; private set; }
        public string LastResult { get; private set; }
        public string? LastError { get; private set; }
        public UploadRecord? Uploaded { get; private set; }
        public int GcodeLines { get; private set; }
        public ulong GcodeHash { get; private set; }
        public DateTime? SnapshotUtc { get; private set; }
        public bool ArmConfigurationKnown { get; private set; }

        // ---- 3.0.0: fields absorbed from the five consolidated components ----------

        /// <summary>Selected jog step, mm. One of <see cref="JogPlanner.StepWhitelist"/>.</summary>
        public double Step { get; private set; }
        /// <summary>Captured bed origin, MACHINE frame; null until captured.</summary>
        public Vec3? BedOrigin { get; private set; }
        /// <summary>Captured Z zero, machine Z in mm; null until captured.</summary>
        public double? ZZero { get; private set; }
        /// <summary>Deadline of the terminal's advanced-command confirmation window;
        /// <see cref="DateTime.MinValue"/> when nothing is armed.</summary>
        public DateTime TerminalConfirmUntilUtc { get; private set; }
        /// <summary>Why the session refuses state-changing commands, or null when it does
        /// not. Mirrors <c>MachineSession.TrustRefusalReason()</c>, so the window can never
        /// show an enabled button that the transaction then refuses.</summary>
        public string? TrustReason { get; private set; }
        /// <summary>True once the home-macro probe has finished. Homing is refused ONLY
        /// when the probe completed AND the axis has no macro - an incomplete or failed
        /// probe must never block homing, because homing is the recovery path out of an
        /// unknown configuration (<c>DuetHoming.PreflightRefusal</c>, ARCHI §15).</summary>
        public bool HomeProbeComplete { get; private set; }
        public bool HomeAvailableAll { get; private set; }
        public bool HomeAvailableX { get; private set; }
        public bool HomeAvailableY { get; private set; }
        public bool HomeAvailableZ { get; private set; }
        public string BoardType { get; private set; }
        public bool Legacy { get; private set; }
        public bool KinematicsProbed { get; private set; }
        public bool MotionBaselineProbed { get; private set; }
        /// <summary>True when the last completed action reported motion-limit warnings,
        /// so a successful-but-warned run renders as a warning rather than a remark.</summary>
        public bool LastActionWarned { get; private set; }
        /// <summary>Result of the last solve-time validation, or null when none applies to
        /// the currently wired gcode.</summary>
        public ValidationProjection? Validation { get; private set; }
        /// <summary>Why the control profile could not be resolved, or null. Every action
        /// that plans motion refuses while this is set.</summary>
        public string? ProfileError { get; private set; }
        /// <summary>Cartesian axes whose envelope is neither wired nor reported by the
        /// firmware. Non-empty means motion planning fails closed.</summary>
        public IReadOnlyList<string> EnvelopeUnknown { get; private set; }
        /// <summary>True when Start runs the job as an M37 firmware simulation rather than
        /// M32. Projected from the component's <c>dryRun</c> input so the window shows the
        /// mode the action will really use; the renderer never infers it.</summary>
        public bool DryRun { get; private set; }
        /// <summary>The Start control's label for the projected mode - the same wording
        /// the hidden Print component's capsule used.</summary>
        public string StartLabel => DryRun ? "Start (dry M37)" : "Start";
        /// <summary>Job-log retention facts (header badge, Logs page). Never null.</summary>
        public LogRetentionStatus Logging { get; private set; }
        /// <summary>The exact command the terminal armed for confirmation ("" when none),
        /// so the confirmation strip can show what the second press will send.</summary>
        public string TerminalArmedText { get; private set; }
        /// <summary>Layer lines of the current/last job from the retention recorder.</summary>
        public IReadOnlyList<string> JobLayers { get; private set; }
        public MachineOverrides? Overrides { get; private set; }
        public string Transport { get; private set; }
        public bool CanSimulate { get; private set; }

        // ---- E-Stop recovery (2026-09-09) -------------------------------------------

        /// <summary>Separate facts about an emergency stop and the recovery that follows
        /// it. Never null; the window, the component and the transaction all read this
        /// one projection, so none of them can invent its own idea of "recovered".</summary>
        public RecoveryState Recovery { get; private set; }
        /// <summary>Identity of the session behind this projection ("" when detached).
        /// An operator recovery is bound to it, so a re-leased or replaced session never
        /// inherits the permission.</summary>
        public string SessionId { get; private set; }
        /// <summary>The machine identity the session expects right now ("" when none was
        /// probed). Compared with the fingerprint recorded at emergency-stop time.</summary>
        public string ExpectedFingerprint { get; private set; }
        /// <summary>Non-null while owners sharing this host expect different machines.</summary>
        public string? ExpectationConflict { get; private set; }
        /// <summary>What the transport calls its firmware reset.</summary>
        public string ResetCommand => "M999";
        /// <summary>What the window shows about the recovery, or null.</summary>
        public string? RecoveryNotice => Recovery.Notice;

        /// <summary>Everything <see cref="ResetPolicy"/> reads, built from this state so
        /// the button and the transaction cannot drift apart.</summary>
        public ResetContext ResetContext() => new ResetContext
        {
            Host = Host,
            SessionId = SessionId,
            ExpectedFingerprint = ExpectedFingerprint,
            ExpectationConflict = ExpectationConflict,
            Connection = Connection,
            Status = MachineState,
            TrustReason = TrustReason,
            PendingAction = PendingAction,
            Recovery = Recovery,
            ResetCommand = ResetCommand,
        };

        // ---- 4.1: prepared job vs active job ---------------------------------------

        /// <summary>The job a Start would run: the bytes currently wired into the Console,
        /// or a job selected from history. Null when nothing is prepared.</summary>
        public JobSnapshot? PreparedJob { get; private set; }
        /// <summary>The job the machine is actually running, with the exact bytes and hash
        /// it was started from. Null when nothing is printing.</summary>
        /// <remarks>Deliberately separate from <see cref="PreparedJob"/>: editing the
        /// Console's G-code input during a print must change what a later Start would run
        /// and nothing else. Consumers that show an intended path follow
        /// <see cref="EffectiveJob"/>, so a live comparison stays bound to the bytes that
        /// are on the machine.</remarks>
        public JobSnapshot? ActiveJob { get; private set; }
        /// <summary>The job whose intended path should be shown: the active one while a
        /// print runs, otherwise the prepared one.</summary>
        public JobSnapshot? EffectiveJob => ActiveJob ?? PreparedJob;
        /// <summary>False when the running job's bytes are unknown - it was started outside
        /// this session and its file has not been retrieved and verified. An unavailable
        /// intended path must be reported as unavailable, never replaced by the Console's
        /// own input.</summary>
        public bool IntendedPathAvailable => EffectiveJob?.IntendedPathAvailable ?? false;

        // ---- 4.1: Recent prints ------------------------------------------------------

        /// <summary>The run picked from Recent prints, or null when the Console runs the
        /// G-code wired into it. Selecting a run LOADS it - it never starts anything - and
        /// the selection wins over the input until it is cleared.</summary>
        public RecentPrintEntry? SelectedRun { get; private set; }
        /// <summary>What the Validate card has to say about the selected run: the reason it
        /// cannot be reprinted, or what changed since it ran. Null when there is nothing
        /// to say.</summary>
        public string? RunSelectionNote { get; private set; }
        /// <summary>True when a past run is loaded rather than the wired G-code.</summary>
        public bool RunSelected => SelectedRun != null;
        /// <summary>The last runs, newest first. Comes from retention, which records every
        /// job; empty until the first one is observed.</summary>
        public IReadOnlyList<RecentPrintEntry> RecentRuns => Logging.Recent;

        PrintConsoleState(string host)
        {
            Host = host;
            Connection = ConsoleConnection.None;
            MachineState = MachineStatus.Unknown;
            RawStatus = "";
            JobFile = "";
            LastReply = "";
            LogTail = Array.Empty<string>();
            LastResult = "";
            BoardType = "";
            Step = JogPlanner.StepWhitelist[1];   // 1 mm
            TerminalConfirmUntilUtc = DateTime.MinValue;
            // Nothing probed yet means "no refusal known", matching the homing predicate:
            // availability only ever blocks once the probe has completed.
            HomeAvailableAll = HomeAvailableX = HomeAvailableY = HomeAvailableZ = true;
            EnvelopeUnknown = Array.Empty<string>();
            Logging = LogRetentionStatus.None;
            TerminalArmedText = "";
            JobLayers = Array.Empty<string>();
            Transport = "";
            CanSimulate = true;
            Recovery = RecoveryState.None;
            SessionId = "";
            ExpectedFingerprint = "";
        }

        PrintConsoleState(PrintConsoleState src)
        {
            Host = src.Host;
            Connection = src.Connection;
            XHomed = src.XHomed; YHomed = src.YHomed; ZHomed = src.ZHomed; AllHomed = src.AllHomed;
            MachineState = src.MachineState;
            RawStatus = src.RawStatus;
            JobFile = src.JobFile;
            Progress = src.Progress;
            Layer = src.Layer;
            LayerCount = src.LayerCount;
            Position = src.Position;
            LastReply = src.LastReply;
            LogTail = src.LogTail;
            LogSkip = src.LogSkip;
            PendingAction = src.PendingAction;
            ActionEpoch = src.ActionEpoch;
            LastResult = src.LastResult;
            LastError = src.LastError;
            Uploaded = src.Uploaded;
            GcodeLines = src.GcodeLines;
            GcodeHash = src.GcodeHash;
            SnapshotUtc = src.SnapshotUtc;
            ArmConfigurationKnown = src.ArmConfigurationKnown;
            Step = src.Step;
            BedOrigin = src.BedOrigin;
            ZZero = src.ZZero;
            TerminalConfirmUntilUtc = src.TerminalConfirmUntilUtc;
            TrustReason = src.TrustReason;
            HomeProbeComplete = src.HomeProbeComplete;
            HomeAvailableAll = src.HomeAvailableAll;
            HomeAvailableX = src.HomeAvailableX;
            HomeAvailableY = src.HomeAvailableY;
            HomeAvailableZ = src.HomeAvailableZ;
            BoardType = src.BoardType;
            Legacy = src.Legacy;
            KinematicsProbed = src.KinematicsProbed;
            MotionBaselineProbed = src.MotionBaselineProbed;
            LastActionWarned = src.LastActionWarned;
            Validation = src.Validation;
            ProfileError = src.ProfileError;
            EnvelopeUnknown = src.EnvelopeUnknown;
            DryRun = src.DryRun;
            Logging = src.Logging;
            TerminalArmedText = src.TerminalArmedText;
            JobLayers = src.JobLayers;
            Overrides = src.Overrides;
            Transport = src.Transport;
            CanSimulate = src.CanSimulate;
            Recovery = src.Recovery;
            SessionId = src.SessionId;
            ExpectedFingerprint = src.ExpectedFingerprint;
            ExpectationConflict = src.ExpectationConflict;

            PreparedJob = src.PreparedJob;
            ActiveJob = src.ActiveJob;
            SelectedRun = src.SelectedRun;
            RunSelectionNote = src.RunSelectionNote;
        }

        public static PrintConsoleState Empty(string host) => new PrintConsoleState(host ?? "");

        public PrintConsoleState WithConnection(ConsoleConnection connection) { var n = new PrintConsoleState(this); n.Connection = connection; return n; }
        public PrintConsoleState WithHomed(bool x, bool y, bool z, bool all)
        {
            var n = new PrintConsoleState(this);
            n.XHomed = x; n.YHomed = y; n.ZHomed = z; n.AllHomed = all;
            return n;
        }
        public PrintConsoleState WithMachine(MachineStatus state, string rawStatus)
        {
            var n = new PrintConsoleState(this);
            n.MachineState = state; n.RawStatus = rawStatus ?? "";
            return n;
        }
        public PrintConsoleState WithJob(string jobFile, double progress, int? layer, int? layerCount = null)
        {
            var n = new PrintConsoleState(this);
            n.JobFile = jobFile ?? ""; n.Progress = progress; n.Layer = layer; n.LayerCount = layerCount;
            return n;
        }
        /// <summary>The console's job progress line: <c>42%</c>, <c>42%  layer 12</c> when no
        /// total is known, <c>42%  layer 12/40</c> otherwise. A total below the current layer
        /// (a foreign or non-planar count) is not shown rather than contradicted.</summary>
        public static string ProgressText(double progress, int? layer, int? layerCount)
        {
            var inv = CultureInfo.InvariantCulture;
            string text = (Num.Clamp01(double.IsNaN(progress) ? 0.0 : progress) * 100.0).ToString("0", inv) + "%";
            if (!layer.HasValue) return text;
            text += "  layer " + layer.Value.ToString(inv);
            if (layerCount.HasValue && layerCount.Value >= layer.Value)
                text += "/" + layerCount.Value.ToString(inv);
            return text;
        }

        public PrintConsoleState WithPosition(Vec3? position) { var n = new PrintConsoleState(this); n.Position = position; return n; }
        public PrintConsoleState WithLastReply(string reply) { var n = new PrintConsoleState(this); n.LastReply = reply ?? ""; return n; }
        public PrintConsoleState WithLog(IReadOnlyList<string> tail, long skip)
        {
            var n = new PrintConsoleState(this);
            n.LogTail = tail ?? Array.Empty<string>(); n.LogSkip = skip;
            return n;
        }
        public PrintConsoleState WithPendingAction(string? label) { var n = new PrintConsoleState(this); n.PendingAction = label; return n; }
        /// <summary>Starts a new action identity. Every started action gets one, so a
        /// superseded action's completion can be told apart from the current one's.</summary>
        public PrintConsoleState WithNextActionEpoch() { var n = new PrintConsoleState(this); n.ActionEpoch = ActionEpoch + 1; return n; }
        public PrintConsoleState WithRecovery(RecoveryState recovery)
        { var n = new PrintConsoleState(this); n.Recovery = recovery ?? RecoveryState.None; return n; }
        public PrintConsoleState WithIdentity(string? sessionId, string? expectedFingerprint, string? expectationConflict)
        {
            var n = new PrintConsoleState(this);
            n.SessionId = sessionId ?? ""; n.ExpectedFingerprint = expectedFingerprint ?? "";
            n.ExpectationConflict = expectationConflict;
            return n;
        }
        public PrintConsoleState WithLastResult(string result) { var n = new PrintConsoleState(this); n.LastResult = result ?? ""; return n; }
        public PrintConsoleState WithLastError(string? error) { var n = new PrintConsoleState(this); n.LastError = error; return n; }
        public PrintConsoleState WithUploaded(UploadRecord? uploaded) { var n = new PrintConsoleState(this); n.Uploaded = uploaded; return n; }
        public PrintConsoleState WithGcode(int lines, ulong hash)
        {
            var n = new PrintConsoleState(this);
            n.GcodeLines = lines; n.GcodeHash = hash;
            return n;
        }
        public PrintConsoleState WithSnapshotUtc(DateTime? utc) { var n = new PrintConsoleState(this); n.SnapshotUtc = utc; return n; }
        public PrintConsoleState WithArmConfigurationKnown(bool known) { var n = new PrintConsoleState(this); n.ArmConfigurationKnown = known; return n; }
        public PrintConsoleState WithStep(double step) { var n = new PrintConsoleState(this); n.Step = step; return n; }
        public PrintConsoleState WithBedOrigin(Vec3? origin) { var n = new PrintConsoleState(this); n.BedOrigin = origin; return n; }
        public PrintConsoleState WithZZero(double? z) { var n = new PrintConsoleState(this); n.ZZero = z; return n; }
        public PrintConsoleState WithTerminalConfirmUntil(DateTime utc) { var n = new PrintConsoleState(this); n.TerminalConfirmUntilUtc = utc; return n; }
        public PrintConsoleState WithTrustReason(string? reason) { var n = new PrintConsoleState(this); n.TrustReason = reason; return n; }
        public PrintConsoleState WithHomeAvailability(bool probeComplete, bool all, bool x, bool y, bool z)
        {
            var n = new PrintConsoleState(this);
            n.HomeProbeComplete = probeComplete;
            n.HomeAvailableAll = all; n.HomeAvailableX = x; n.HomeAvailableY = y; n.HomeAvailableZ = z;
            return n;
        }
        public PrintConsoleState WithBoard(string? boardType, bool legacy, bool kinematicsProbed, bool motionBaselineProbed)
        {
            var n = new PrintConsoleState(this);
            n.BoardType = boardType ?? "";
            n.Legacy = legacy; n.KinematicsProbed = kinematicsProbed; n.MotionBaselineProbed = motionBaselineProbed;
            return n;
        }
        public PrintConsoleState WithLastActionWarned(bool warned) { var n = new PrintConsoleState(this); n.LastActionWarned = warned; return n; }
        public PrintConsoleState WithValidation(ValidationProjection? validation) { var n = new PrintConsoleState(this); n.Validation = validation; return n; }
        public PrintConsoleState WithPlanningFacts(string? profileError, IReadOnlyList<string>? envelopeUnknown)
        {
            var n = new PrintConsoleState(this);
            n.ProfileError = profileError;
            n.EnvelopeUnknown = envelopeUnknown ?? Array.Empty<string>();
            return n;
        }
        public PrintConsoleState WithDryRun(bool dryRun) { var n = new PrintConsoleState(this); n.DryRun = dryRun; return n; }
        public PrintConsoleState WithLogging(LogRetentionStatus? logging) { var n = new PrintConsoleState(this); n.Logging = logging ?? LogRetentionStatus.None; return n; }
        public PrintConsoleState WithTerminalArmedText(string text) { var n = new PrintConsoleState(this); n.TerminalArmedText = text ?? ""; return n; }
        public PrintConsoleState WithJobLayers(IReadOnlyList<string>? layers) { var n = new PrintConsoleState(this); n.JobLayers = layers ?? Array.Empty<string>(); return n; }
        public PrintConsoleState WithOverrides(MachineOverrides? overrides)
        { var n = new PrintConsoleState(this); n.Overrides = overrides; return n; }
        public PrintConsoleState WithTransport(string transport, bool canSimulate)
        {
            var n = new PrintConsoleState(this);
            n.Transport = transport ?? ""; n.CanSimulate = canSimulate;
            return n;
        }
        /// <summary>New bytes wired into the Console, or a history item selected. Never
        /// touches <see cref="ActiveJob"/>: preparing a job is not starting one.</summary>
        public PrintConsoleState WithPreparedJob(JobSnapshot? job)
        { var n = new PrintConsoleState(this); n.PreparedJob = job; return n; }
        /// <summary>Loads or clears a run picked from Recent prints. Never touches
        /// <see cref="ActiveJob"/>: an active print stays bound to its own snapshot while
        /// the operator looks through history.</summary>
        public PrintConsoleState WithSelectedRun(RecentPrintEntry? run, string? note)
        { var n = new PrintConsoleState(this); n.SelectedRun = run; n.RunSelectionNote = note; return n; }

        /// <summary>The job the machine is running; null when the run ended.</summary>
        public PrintConsoleState WithActiveJob(JobSnapshot? job)
        { var n = new PrintConsoleState(this); n.ActiveJob = job; return n; }

        /// <summary>Home macro availability for one axis, using the same predicate as
        /// <c>DuetHoming.PreflightRefusal</c>: unknown availability never refuses.</summary>
        public bool HomeRefusedFor(string axis)
        {
            if (!HomeProbeComplete) return false;
            switch (axis)
            {
                case "X": return !HomeAvailableX;
                case "Y": return !HomeAvailableY;
                case "Z": return !HomeAvailableZ;
                default: return !HomeAvailableAll;
            }
        }
    }

    /// <summary>Plain copy of what the session knows, filled in by the caller on
    /// every poll. Pure input to <see cref="PrintConsoleReducer.Reduce"/>.</summary>
    public sealed class ConsoleFacts
    {
        public ConsoleConnection Connection { get; set; }
        public ObjectModelSnapshot? Snapshot { get; set; }
        /// <summary>Overrides the snapshot's reply when set.</summary>
        public string? LastReply { get; set; }
        /// <summary>A session-level error (poll failure etc.); null keeps the previous error.</summary>
        public string? LastError { get; set; }
        /// <summary>The session log as currently retained (the session caps it, so
        /// this may be only the newest part).</summary>
        public IReadOnlyList<string> Log { get; set; } = Array.Empty<string>();
        /// <summary>Total lines ever appended to the session log. The first entry
        /// of <see cref="Log"/> has sequence <c>LogSequence - Log.Count</c>.</summary>
        public long LogSequence { get; set; }
        public bool ArmConfigurationKnown { get; set; }

        // ---- 3.0.0: session facts the window's buttons must agree with ------------

        /// <summary>Why the session refuses state-changing commands; null when it does not.
        /// Without this the console could show an enabled button that the transaction then
        /// refuses for a reason the canvas components already display.</summary>
        public string? TrustReason { get; set; }
        public bool HomeProbeComplete { get; set; }
        public bool HomeAvailableAll { get; set; } = true;
        public bool HomeAvailableX { get; set; } = true;
        public bool HomeAvailableY { get; set; } = true;
        public bool HomeAvailableZ { get; set; } = true;
        public string? BoardType { get; set; }
        public bool Legacy { get; set; }
        public bool KinematicsProbed { get; set; }
        public bool MotionBaselineProbed { get; set; }
        /// <summary>Component-side, not session-side: the unresolved control profile and
        /// the fail-closed Cartesian envelope list. They gate motion in the transaction,
        /// so the buttons have to see them too.</summary>
        public string? ProfileError { get; set; }
        public IReadOnlyList<string> EnvelopeUnknown { get; set; } = Array.Empty<string>();
        /// <summary>Job-log retention status from the retention service; null keeps the previous.</summary>
        public LogRetentionStatus? Logging { get; set; }
        /// <summary>Layer lines of the retention recorder; null keeps the previous.</summary>
        public IReadOnlyList<string>? JobLayers { get; set; }

        // ---- transport and machine overrides ---------------------------------------
        /// <summary>Transport name; null keeps the previous.</summary>
        public string? Transport { get; set; }
        public bool CanSimulate { get; set; } = true;

        // ---- E-Stop recovery (2026-09-09) --------------------------------------------
        /// <summary>The session's authoritative recovery facts; null keeps the previous.
        /// The session owns them because the recovery transaction has to re-derive its
        /// decision from the same values inside the command lock.</summary>
        public RecoveryState? Recovery { get; set; }
        /// <summary>Identity of the session; null keeps the previous.</summary>
        public string? SessionId { get; set; }
        /// <summary>The machine identity the session expects now; null keeps the previous.</summary>
        public string? ExpectedFingerprint { get; set; }
        /// <summary>Owners disagreeing about this host's machine, or null.</summary>
        public string? ExpectationConflict { get; set; }
        /// <summary>True when a session was attached and <see cref="ActiveJob"/> therefore
        /// describes the truth, including "nothing is running".</summary>
        public bool JobsObserved { get; set; }
        /// <summary>The run the machine is executing, from the session's job tracker.</summary>
        public JobSnapshot? ActiveJob { get; set; }
    }

    /// <summary>Pure state transitions of the Print Console. No side effects, no
    /// clock: every function returns a new <see cref="PrintConsoleState"/>.</summary>
    public static class PrintConsoleReducer
    {
        /// <summary>Folds a poll into the state. A null snapshot keeps the previous
        /// machine fields (homed flags, status, job, position, SnapshotUtc) and only
        /// updates the connection, reply, error, log tail and arm flag.</summary>
        public static PrintConsoleState Reduce(PrintConsoleState state, ConsoleFacts facts)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (facts == null) throw new ArgumentNullException(nameof(facts));

            var s = state.WithConnection(facts.Connection)
                         .WithArmConfigurationKnown(facts.ArmConfigurationKnown)
                         .WithTrustReason(facts.TrustReason)
                         .WithHomeAvailability(facts.HomeProbeComplete, facts.HomeAvailableAll,
                                               facts.HomeAvailableX, facts.HomeAvailableY, facts.HomeAvailableZ)
                         .WithBoard(facts.BoardType, facts.Legacy, facts.KinematicsProbed, facts.MotionBaselineProbed)
                         .WithPlanningFacts(facts.ProfileError, facts.EnvelopeUnknown);
            if (facts.Logging != null) s = s.WithLogging(facts.Logging);
            if (facts.JobLayers != null) s = s.WithJobLayers(facts.JobLayers);
            if (facts.Transport != null) s = s.WithTransport(facts.Transport, facts.CanSimulate);
            if (facts.Recovery != null) s = s.WithRecovery(facts.Recovery);
            // The session's job tracker owns the ACTIVE run: a run that ended must clear
            // here too, which a "null keeps the previous" rule could never express - hence
            // the explicit observed flag rather than a nullable field alone.
            if (facts.JobsObserved) s = s.WithActiveJob(facts.ActiveJob);
            // The identity block travels together: a conflict that resolves has to clear,
            // which a per-field "null keeps the previous" rule could never do.
            if (facts.SessionId != null)
                s = s.WithIdentity(facts.SessionId, facts.ExpectedFingerprint, facts.ExpectationConflict);

            var snap = facts.Snapshot;
            s = s.WithOverrides(snap?.Overrides ?? (snap == null ? s.Overrides : null));
            if (snap != null)
            {
                var ax = snap.Axis("X");
                var ay = snap.Axis("Y");
                var az = snap.Axis("Z");
                s = s.WithHomed(ax?.Homed ?? false, ay?.Homed ?? false, az?.Homed ?? false, snap.AllHomed)
                     .WithMachine(snap.Status, snap.RawStatus);

                var job = snap.Job;
                s = job != null
                    ? s.WithJob(job.FileName ?? "", double.IsNaN(job.FractionPrinted) ? 0.0 : Num.Clamp01(job.FractionPrinted), job.Layer,
                                s.ActiveJob?.LayerTotal() ?? job.FileLayerCount)
                    : s.WithJob("", 0.0, null, null);

                Vec3? pos = ax != null && ay != null && az != null
                    ? new Vec3(ax.MachinePosition, ay.MachinePosition, az.MachinePosition)
                    : (Vec3?)null;
                s = s.WithPosition(pos).WithSnapshotUtc(snap.TakenUtc);
            }

            var reply = facts.LastReply ?? snap?.LastReply;
            if (reply != null) s = s.WithLastReply(reply);
            if (facts.LastError != null) s = s.WithLastError(facts.LastError);

            var log = facts.Log ?? Array.Empty<string>();
            s = s.WithLog(Tail(log, facts.LogSequence, s.LogSkip), s.LogSkip);
            return s;
        }

        /// <summary>Marks an action in flight, clears the previous error and opens a new
        /// action epoch. The caller keeps the epoch of the state it produced and hands it
        /// back to <see cref="ActionFinished"/>, so a completion that an emergency stop
        /// (or a later action) has superseded can be dropped.</summary>
        public static PrintConsoleState ActionStarted(PrintConsoleState state, string label)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithPendingAction(label ?? "").WithLastError(null).WithNextActionEpoch();
        }

        /// <summary>Clears the pending action. With an error: LastError = error and
        /// LastResult = "ERROR: " + error; otherwise LastResult = result (or "").</summary>
        /// <param name="warned">True when the action reported motion-limit warnings, so a
        /// successful-but-warned run renders as a warning rather than a remark. The 2.6.0
        /// console discarded this flag.</param>
        /// <param name="epoch">The <see cref="PrintConsoleState.ActionEpoch"/> the action
        /// started under. A stale epoch means the action was superseded - typically by an
        /// emergency stop - and its completion is IGNORED rather than allowed to clear the
        /// newer action's pending state or overwrite its result. Null applies
        /// unconditionally, which is what a caller with no action identity wants.</param>
        public static PrintConsoleState ActionFinished(PrintConsoleState state, string? result, string? error,
                                                       bool warned = false, long? epoch = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (epoch.HasValue && epoch.Value != state.ActionEpoch) return state;
            var s = state.WithPendingAction(null).WithLastActionWarned(warned);
            if (!string.IsNullOrEmpty(error))
                return s.WithLastError(error).WithLastResult("ERROR: " + error);
            return s.WithLastResult(result ?? "");
        }

        /// <summary>Records a bed-origin capture (MACHINE frame).</summary>
        public static PrintConsoleState BedOriginCaptured(PrintConsoleState state, Vec3? origin)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithBedOrigin(origin);
        }

        /// <summary>Records a Z-zero capture (machine Z, mm).</summary>
        public static PrintConsoleState ZZeroCaptured(PrintConsoleState state, double? z)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithZZero(z);
        }

        /// <summary>Selects a jog step. Values outside
        /// <see cref="JogPlanner.StepWhitelist"/> are ignored - the planner would refuse
        /// them anyway, and silently keeping the old step beats showing a step the machine
        /// will not move.</summary>
        public static PrintConsoleState StepChanged(PrintConsoleState state, double step)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            foreach (double w in JogPlanner.StepWhitelist)
                if (w.Equals(step)) return state.WithStep(step);
            return state;
        }

        /// <summary>Arms the terminal's advanced-command confirmation window for the exact
        /// command text the second press will send.</summary>
        public static PrintConsoleState TerminalArmed(PrintConsoleState state, DateTime untilUtc, string armedText = "")
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithTerminalConfirmUntil(untilUtc).WithTerminalArmedText(armedText);
        }

        /// <summary>Clears the terminal's confirmation window (after a confirmed send, or
        /// when the line changes).</summary>
        public static PrintConsoleState TerminalDisarmed(PrintConsoleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithTerminalConfirmUntil(DateTime.MinValue).WithTerminalArmedText("");
        }

        /// <summary>Adopts the session's recovery facts. The session owns them - the
        /// recovery transaction re-derives its decision from the same object - and the
        /// console only projects them, so the window and the transaction agree.</summary>
        public static PrintConsoleState RecoveryChanged(PrintConsoleState state, RecoveryState recovery)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithRecovery(recovery ?? RecoveryState.None);
        }

        /// <summary>Stores a solve-time validation result. A projection whose key does not
        /// match the wired gcode is dropped rather than stored, so the outputs can never
        /// describe gcode that is no longer there.</summary>
        public static PrintConsoleState ValidationCompleted(PrintConsoleState state, ValidationProjection? validation)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (validation != null && validation.GateKey != state.GcodeHash) return state.WithValidation(null);
            return state.WithValidation(validation);
        }

        public static PrintConsoleState Uploaded(PrintConsoleState state, UploadRecord record)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (record == null) throw new ArgumentNullException(nameof(record));
            return state.WithUploaded(record);
        }

        /// <summary>New gcode wired in. A different content hash invalidates the
        /// upload record (same rule as the print-job component: changed gcode means
        /// whatever is on the board is not this file) and drops the validation
        /// projection, exactly as the component-side gate cache clears.</summary>
        public static PrintConsoleState GcodeChanged(PrintConsoleState state, int lines, ulong hash)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var s = state.WithGcode(lines, hash);
            if (hash != state.GcodeHash) s = s.WithUploaded(null).WithValidation(null);
            return s;
        }

        /// <summary>The validation policy key changed (limits, arm configuration, dry-run,
        /// arc support …) without the gcode changing. The cached verdict described a
        /// different policy, so it goes.</summary>
        public static PrintConsoleState ValidationPolicyChanged(PrintConsoleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithValidation(null);
        }

        /// <summary>Hides everything logged so far: the tail restarts at the
        /// session log's current sequence number (total lines ever appended).</summary>
        public static PrintConsoleState ClearLog(PrintConsoleState state, long logSequence)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.WithLog(Array.Empty<string>(), Math.Max(0L, logSequence));
        }

        /// <summary>Entries of the (possibly capped) session log whose sequence
        /// number is at least <paramref name="skip"/>, limited to the newest
        /// <see cref="PrintConsoleState.LogTailMax"/>.</summary>
        static IReadOnlyList<string> Tail(IReadOnlyList<string> log, long logSequence, long skip)
        {
            int count = log.Count;
            long firstSeq = logSequence - count;
            long fromSeq = Math.Max(skip, firstSeq);
            int from = fromSeq - firstSeq > count ? count : (int)(fromSeq - firstSeq);
            from = Math.Max(from, count - PrintConsoleState.LogTailMax);
            if (from >= count) return Array.Empty<string>();
            var arr = new string[count - from];
            for (int i = from; i < count; i++) arr[i - from] = log[i] ?? "";
            return arr;
        }
    }

    /// <summary>Enabled flags of the console buttons plus, per disabled action id,
    /// the first reason it is disabled (ids: connect disconnect home homex homey homez
    /// jog capture upload start pause resume stop estop reset goto gotostart send).</summary>
    public sealed class ButtonSet
    {
        public bool Connect { get; }
        public bool Home { get; }
        public bool HomeX { get; }
        public bool HomeY { get; }
        public bool HomeZ { get; }
        public bool Upload { get; }
        public bool Start { get; }
        public bool Pause { get; }
        public bool Resume { get; }
        public bool Stop { get; }
        public bool EStop { get; }
        public bool Reset { get; }
        public bool GoTo { get; }
        /// <summary>Go to the validated job's print start at Z 0 (reason id "gotostart").
        /// Same machine gates as <see cref="GoTo"/>, plus a validation that names a start.</summary>
        public bool GoToStart { get; }
        /// <summary>Jog pad and the go-to move share one gate; the reason id is "jog".</summary>
        public bool Jog { get; }
        /// <summary>Set bed origin.</summary>
        public bool Capture { get; }
        /// <summary>Terminal send. Line-level classification is <see cref="TerminalPolicy"/>'s
        /// job; this is only the session-level gate.</summary>
        public bool Send { get; }
        public bool Disconnect { get; }
        /// <summary>Local job validation. Runs <c>JobGate</c> on the wired gcode and needs
        /// neither a session nor machine trust, so it is not behind upload's gate.</summary>
        public bool Validate { get; }
        /// <summary>Pick a past run out of Recent prints, or clear the picked one. Loading
        /// bytes is not running them, so this gate is deliberately narrow: no command may
        /// be in flight, and nothing else. Whether those bytes may then be uploaded and
        /// started is decided by <see cref="Upload"/> and <see cref="Start"/>, unchanged.</summary>
        public bool History { get; }
        public IReadOnlyDictionary<string, string> Reasons { get; }

        public ButtonSet(bool connect, bool home, bool homeX, bool homeY, bool homeZ, bool upload,
                         bool start, bool pause, bool resume, bool stop, bool eStop, bool reset, bool goTo,
                         IReadOnlyDictionary<string, string> reasons,
                         bool jog = false, bool capture = false, bool send = false, bool disconnect = false,
                         bool canValidate = false,
                         bool history = false,
                         bool goToStart = false)
        {
            Connect = connect; Home = home; HomeX = homeX; HomeY = homeY; HomeZ = homeZ; Upload = upload;
            Start = start; Pause = pause; Resume = resume; Stop = stop; EStop = eStop; Reset = reset; GoTo = goTo;
            Jog = jog; Capture = capture; Send = send; Disconnect = disconnect; Validate = canValidate;
            History = history; GoToStart = goToStart;
            Reasons = reasons ?? new Dictionary<string, string>();
        }
    }

    /// <summary>The button gate. Pure: the same state always yields the same set.</summary>
    public static class PrintConsoleButtons
    {
        /// <param name="gotoSupported">False for control profiles without a go-to move (only the SCARA profile has one).</param>
        /// <param name="armConfigurationRequired">False for a Cartesian profile: such a machine never
        /// establishes an arm configuration (there is none), so requiring one refused every start.</param>
        /// <param name="terminalAdvanced">True when the line currently in the terminal box
        /// is an ADVANCED command. Diagnostics run on the non-state-changing path and stay
        /// usable while trust is diagnostic-only, so only advanced lines are trust-gated.</param>
        public static ButtonSet Enabled(PrintConsoleState s, bool hasHost, bool hasGcode, bool hasPoint, bool gotoSupported,
                                        bool armConfigurationRequired = true, bool terminalAdvanced = false, DateTime? nowUtc = null)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

            bool pending = s.PendingAction != null;
            bool connected = s.Connection == ConsoleConnection.Connected;
            bool idle = s.MachineState == MachineStatus.Idle;
            var st = s.MachineState;
            string connection = Lower(s.Connection);
            string machine = Lower(st);
            string pendingWhy = "action in progress: " + s.PendingAction;
            string notConnected = "not connected (" + connection + ")";
            string notIdle = "machine not idle (" + machine + ")";

            bool Req(string id, bool ok, string why)
            {
                if (!ok && !reasons.ContainsKey(id)) reasons[id] = why;
                return ok;
            }

            // Everything that plans motion refuses while the profile is unresolved or a
            // Cartesian envelope is unknown - the transaction fails closed on both, so the
            // buttons must too.
            string? planningBlock = s.ProfileError != null
                ? s.ProfileError
                : s.EnvelopeUnknown.Count > 0
                    ? "machine envelope unknown (" + string.Join(", ", s.EnvelopeUnknown) + ")"
                    : null;
            bool Plannable(string id) => Req(id, planningBlock == null, planningBlock ?? "");

            bool connectable = s.Connection == ConsoleConnection.None
                            || s.Connection == ConsoleConnection.Disconnected
                            || s.Connection == ConsoleConnection.Error;
            bool connect = Req("connect", !pending, pendingWhy)
                        && Req("connect", hasHost, "no host")
                        && Req("connect", connectable, "already " + connection);

            // A state-changing command the session would refuse for lack of machine trust
            // must not look available in the window - the canvas components already show
            // this reason, and the two must never disagree.
            string? trust = s.TrustReason;
            bool Trusted(string id) => Req(id, trust == null, trust ?? "");

            bool HomeGate(string id, string axis)
                => Req(id, !pending, pendingWhy) && Req(id, connected, notConnected) && Req(id, idle, notIdle)
                && Req(id, !s.HomeRefusedFor(axis), "no home macro for " + axis + " on the board")
                && Trusted(id);
            bool home = HomeGate("home", "all");
            bool homeX = HomeGate("homex", "X");
            bool homeY = HomeGate("homey", "Y");
            bool homeZ = HomeGate("homez", "Z");

            bool upload = Req("upload", !pending, pendingWhy)
                       && Req("upload", connected, notConnected)
                       && Req("upload", hasGcode, "no gcode")
                       && Trusted("upload");

            // Validation is local: JobGate is pure and runs on the wired gcode, so it must
            // not inherit upload's connection and trust gates - an operator can check a job
            // offline. Upload and Start keep every transaction gate.
            bool validate = Req("validate", !pending, pendingWhy)
                         && Req("validate", hasGcode, "no gcode")
                         && Plannable("validate");

            // After a firmware reset nothing moves until a NEW connection to the same
            // machine has been observed and a verified home has run. A job is never
            // resumed automatically, and not manually into an unrecovered machine either.
            string? recoveryBlock = s.Recovery.ResetRequested && !s.Recovery.RecoveryVerified
                ? "firmware reset is not confirmed yet - wait for the machine to come back"
                : s.Recovery.HomingRequired
                    ? "verified homing is required after the firmware reset"
                    : null;
            bool Recovered(string id) => Req(id, recoveryBlock == null, recoveryBlock ?? "");
            // D9 (2026-09-09): a validated job whose first motion is a full G28 homes the
            // machine itself. Requiring a homed machine and a known arm configuration
            // BEFORE such a job forced a second homing after every print (cancel.g and the
            // job's own footer leave the axes unhomed), which the maintainer refused. The
            // job's G28 also satisfies the post-reset homing requirement for START only;
            // jog, go-to and resume keep every gate.
            bool homesFirst = s.Validation != null && !s.Validation.Danger && s.Validation.HomesFirst;
            string? startRecoveryBlock = s.Recovery.ResetRequested && !s.Recovery.RecoveryVerified
                ? "firmware reset is not confirmed yet - wait for the machine to come back"
                : s.Recovery.HomingRequired && !homesFirst
                    ? "verified homing is required after the firmware reset"
                    : null;

            bool uploadMatches = s.Uploaded != null && s.Uploaded.Hash == s.GcodeHash;
            bool start = Req("start", !pending, pendingWhy)
                      && Req("start", connected, notConnected)
                      && Req("start", startRecoveryBlock == null, startRecoveryBlock ?? "")
                      && Req("start", idle, notIdle)
                      && Req("start", s.AllHomed || homesFirst, "not homed")
                      && Req("start", !armConfigurationRequired || s.ArmConfigurationKnown || homesFirst, "arm configuration unknown")
                      && Req("start", s.Uploaded != null, "nothing uploaded")
                      && Req("start", uploadMatches, "uploaded file does not match the current gcode")
                      && Trusted("start");

            bool printing = st == MachineStatus.Processing || st == MachineStatus.Simulating;
            bool pause = Req("pause", !pending, pendingWhy)
                      && Req("pause", connected, notConnected)
                      && Req("pause", printing, "not printing (" + machine + ")")
                      && Trusted("pause");

            bool resume = Req("resume", !pending, pendingWhy)
                       && Req("resume", connected, notConnected)
                       && Recovered("resume")
                       && Req("resume", st == MachineStatus.Paused, "not paused (" + machine + ")")
                       && Trusted("resume");

            bool stoppable = printing || st == MachineStatus.Paused || st == MachineStatus.Pausing || st == MachineStatus.Resuming;
            bool stop = Req("stop", !pending, pendingWhy)
                     && Req("stop", connected, notConnected)
                     && Req("stop", stoppable, "no job to stop (" + machine + ")")
                     && Trusted("stop");

            // E-STOP is deliberately NOT trust-gated and never behind a tab: it bypasses
            // the command queue entirely (MachineSession.EmergencyStopAsync).
            bool eStop = Req("estop", s.Connection != ConsoleConnection.None, "no session");

            // D4 still holds for the ordinary path: Halted/Off only, trust-gated, no
            // pending action. Since 2026-09-09 there is a second, narrower path - THIS
            // session's E-STOP followed by a machine whose status can no longer be read -
            // because that is exactly the state the operator was locked out of. One rule
            // for the button, the Terminal tab and the transaction: ResetPolicy.
            var resetDecision = ResetPolicy.Decide(s.ResetContext(), nowUtc ?? DateTime.UtcNow);
            bool reset = Req("reset", resetDecision.Allowed, resetDecision.Refusal ?? "");

            bool goTo = Req("goto", gotoSupported, "go-to is available for the SCARA profile only")
                     && Req("goto", !pending, pendingWhy)
                     && Req("goto", connected, notConnected)
                     && Recovered("goto")
                     && Req("goto", idle, notIdle)
                     && Req("goto", s.AllHomed, "not homed")
                     && Req("goto", s.ArmConfigurationKnown, "arm configuration unknown")
                     && Req("goto", hasPoint, "no point")
                     && Trusted("goto");

            // Go to print start: the go-to gates, then a validation that names where the
            // first layer lands. A refused job has no start worth driving to, and a job
            // without an extruding move has none at all - both are said, not guessed.
            var validation = s.Validation;
            bool goToStart = Req("gotostart", gotoSupported, "go-to is available for the SCARA profile only")
                          && Req("gotostart", !pending, pendingWhy)
                          && Req("gotostart", connected, notConnected)
                          && Recovered("gotostart")
                          && Req("gotostart", idle, notIdle)
                          && Req("gotostart", s.AllHomed, "not homed")
                          && Req("gotostart", s.ArmConfigurationKnown, "arm configuration unknown")
                          && Req("gotostart", s.GcodeLines > 0, "no gcode")
                          && Req("gotostart", validation != null, "validate the job first")
                          && Req("gotostart", validation != null && !validation.Danger, "validation refused the job")
                          && Req("gotostart", validation != null && validation.StartPoint.HasValue, "the job has no extruding move")
                          && Trusted("gotostart");

            // Jog mirrors what JogPlanner will actually accept: idle (or paused, because
            // pressing while paused is what ARMS the maintenance override - refusing here
            // would make that two-press path unreachable), homed, a known arm
            // configuration where one exists, and a plannable machine.
            bool jogState = idle || st == MachineStatus.Paused;
            bool jog = Req("jog", !pending, pendingWhy)
                    && Req("jog", connected, notConnected)
                    && Recovered("jog")
                    && Req("jog", jogState, notIdle)
                    && Req("jog", s.AllHomed, "not homed")
                    && Req("jog", !armConfigurationRequired || s.ArmConfigurationKnown, "arm configuration unknown")
                    && Plannable("jog")
                    && Trusted("jog");

            // Capture sends nothing, but it runs inside the gated transaction (which
            // trust-gates) and its body applies StateGate with requireIdle + requireHomed.
            bool capture = Req("capture", !pending, pendingWhy)
                        && Req("capture", connected, notConnected)
                        && Recovered("capture")
                        && Req("capture", idle, notIdle)
                        && Req("capture", s.AllHomed, "not homed")
                        && Req("capture", s.Position.HasValue, "no position reported yet")
                        && Trusted("capture");

            // Only advanced lines take the trusted gated transaction; diagnostics stay
            // usable while trust is diagnostic-only.
            bool send = Req("send", !pending, pendingWhy)
                     && Req("send", connected, notConnected)
                     && (!terminalAdvanced || Trusted("send"));

            bool disconnect = Req("disconnect", s.Connection != ConsoleConnection.None, "no session")
                           && Req("disconnect", s.Connection != ConsoleConnection.Disconnected, "already disconnected");

            // Recent prints: loading a run's bytes needs no machine at all - an operator
            // may look through history offline. It only waits for a command in flight, so
            // a selection cannot land between a transaction's checks and its send.
            bool history = Req("history", !pending, pendingWhy);

            return new ButtonSet(connect, home, homeX, homeY, homeZ, upload, start, pause, resume, stop, eStop, reset, goTo, reasons,
                                 jog, capture, send, disconnect, validate, history: history, goToStart: goToStart);
        }

        public static string Lower(ConsoleConnection c) => c.ToString().ToLowerInvariant();
        public static string Lower(MachineStatus m) => m.ToString().ToLowerInvariant();
    }

    /// <summary>What the component publishes on its outputs. Value equality (the log
    /// compared by sequence) is what gates canvas expiry - anything not in here can
    /// change on every poll without a redraw.</summary>
    public sealed class OutputSnapshot : IEquatable<OutputSnapshot>
    {
        public string Status { get; }
        public string State { get; }
        public double Progress { get; }
        public Vec3? Pos { get; }
        public bool Homed { get; }
        public string JobFile { get; }
        public IReadOnlyList<string> Log { get; }
        public string Info { get; }

        // ---- 3.0.0 outputs 8-19 (append-only, decision D2) ------------------------

        public string HostOut { get; }
        public bool Connected { get; }
        /// <summary>Output 10. Exposing the last firmware reply makes replies
        /// expiry-relevant: a reply change now participates in equality and can trigger a
        /// (throttled) expire. In 2.6.0 replies reached the canvas only via <c>log</c>.</summary>
        public string Reply { get; }
        public bool XHomed { get; }
        public bool YHomed { get; }
        public bool ZHomed { get; }
        /// <summary>MACHINE frame; the component converts at Emit.</summary>
        public Vec3? BedOrigin { get; }
        public double? ZZero { get; }
        /// <summary>Remote path of the last verified upload, "" when none.</summary>
        public string Uploaded { get; }
        public bool Danger { get; }
        /// <summary>MACHINE frame; the component converts at Emit.</summary>
        public IReadOnlyList<Vec3> Problems { get; }
        public IReadOnlyList<string> Messages { get; }

        // ---- machine overrides, NaN = not reported ----
        public double SpeedFactor { get; }
        public double FlowFactor { get; }

        // ---- 4.1: the effective job's G-code, and which outputs actually exist -------

        /// <summary>Lines of the EFFECTIVE job (the active run while printing, otherwise
        /// the prepared one). Empty for an externally started job whose bytes are unknown -
        /// never the Console's own input in its place.</summary>
        public IReadOnlyList<string> Gcode { get; }
        /// <summary>SHA-256 of the effective job's bytes, "" when there is none. This, not
        /// the lines, is what value equality compares: a position poll must never look like
        /// a changed job, and comparing 58,000 strings per poll would be absurd.</summary>
        public string GcodeSha { get; }
        /// <summary>Which outputs are registered on the component. Only these take part in
        /// equality, so hiding a category makes its values unable to expire the canvas.</summary>
        public OutputVisibility Visible { get; }

        public OutputSnapshot(string status, string state, double progress, Vec3? pos, bool homed,
                              string jobFile, IReadOnlyList<string> log, string info,
                              string? hostOut = null, bool connected = false, string? reply = null,
                              bool xHomed = false, bool yHomed = false, bool zHomed = false,
                              Vec3? bedOrigin = null, double? zZero = null, string? uploaded = null,
                              bool danger = false, IReadOnlyList<Vec3>? problems = null,
                              IReadOnlyList<string>? messages = null,
                              double speedFactor = double.NaN, double flowFactor = double.NaN,
                              IReadOnlyList<string>? gcode = null, string? gcodeSha = null,
                              OutputVisibility? visible = null)
        {
            Gcode = gcode ?? Array.Empty<string>();
            GcodeSha = gcodeSha ?? "";
            Visible = visible ?? OutputVisibility.All;
            SpeedFactor = speedFactor; FlowFactor = flowFactor;
            Status = status ?? "";
            State = state ?? "";
            Progress = progress;
            Pos = pos;
            Homed = homed;
            JobFile = jobFile ?? "";
            Log = log ?? Array.Empty<string>();
            Info = info ?? "";
            HostOut = hostOut ?? "";
            Connected = connected;
            Reply = reply ?? "";
            XHomed = xHomed; YHomed = yHomed; ZHomed = zHomed;
            BedOrigin = bedOrigin;
            ZZero = zZero;
            Uploaded = uploaded ?? "";
            Danger = danger;
            Problems = problems ?? Array.Empty<Vec3>();
            Messages = messages ?? Array.Empty<string>();
        }

        static bool SameVec(Vec3? a, Vec3? b)
        {
            if (a.HasValue != b.HasValue) return false;
            if (!a.HasValue) return true;
            var x = a.Value; var y = b!.Value;
            return x.X.Equals(y.X) && x.Y.Equals(y.Y) && x.Z.Equals(y.Z);
        }

        /// <summary>Value equality over the VISIBLE outputs only. An output the component
        /// does not register cannot be read by any definition, so its value must not
        /// re-solve the canvas: with Motion and Temperatures hidden, a position poll and a
        /// drifting telemetry leaves the console alone entirely. The visibility itself is part
        /// of the comparison, so showing a category refreshes the outputs at once.</summary>
        public bool Equals(OutputSnapshot? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            var v = Visible;
            if (!v.Equals(other.Visible)) return false;

            if (v.Shows(ConsoleOutputNames.Status) && !string.Equals(Status, other.Status, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.State) && !string.Equals(State, other.State, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.Progress) && !Progress.Equals(other.Progress)) return false;
            if (v.Shows(ConsoleOutputNames.Pos))
            {
                if (Pos.HasValue != other.Pos.HasValue) return false;
                if (Pos.HasValue)
                {
                    var a = Pos.Value; var b = other.Pos!.Value;
                    if (!a.X.Equals(b.X) || !a.Y.Equals(b.Y) || !a.Z.Equals(b.Z)) return false;
                }
            }
            if (v.Shows(ConsoleOutputNames.Homed) && Homed != other.Homed) return false;
            if (v.Shows(ConsoleOutputNames.JobFile) && !string.Equals(JobFile, other.JobFile, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.Info) && !string.Equals(Info, other.Info, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.Log))
            {
                if (Log.Count != other.Log.Count) return false;
                for (int i = 0; i < Log.Count; i++)
                    if (!string.Equals(Log[i], other.Log[i], StringComparison.Ordinal)) return false;
            }
            if (v.Shows(ConsoleOutputNames.Host) && !string.Equals(HostOut, other.HostOut, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.Connected) && Connected != other.Connected) return false;
            if (v.Shows(ConsoleOutputNames.Reply) && !string.Equals(Reply, other.Reply, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.XHomed) && XHomed != other.XHomed) return false;
            if (v.Shows(ConsoleOutputNames.YHomed) && YHomed != other.YHomed) return false;
            if (v.Shows(ConsoleOutputNames.ZHomed) && ZHomed != other.ZHomed) return false;
            if (v.Shows(ConsoleOutputNames.BedOrigin) && !SameVec(BedOrigin, other.BedOrigin)) return false;
            if (v.Shows(ConsoleOutputNames.ZZero))
            {
                if (ZZero.HasValue != other.ZZero.HasValue) return false;
                if (ZZero.HasValue && !ZZero.Value.Equals(other.ZZero!.Value)) return false;
            }
            if (v.Shows(ConsoleOutputNames.Uploaded) && !string.Equals(Uploaded, other.Uploaded, StringComparison.Ordinal)) return false;
            if (v.Shows(ConsoleOutputNames.Danger) && Danger != other.Danger) return false;
            if (v.Shows(ConsoleOutputNames.Problems))
            {
                if (Problems.Count != other.Problems.Count) return false;
                for (int i = 0; i < Problems.Count; i++)
                    if (!SameVec(Problems[i], other.Problems[i])) return false;
            }
            if (v.Shows(ConsoleOutputNames.Messages))
            {
                if (Messages.Count != other.Messages.Count) return false;
                for (int i = 0; i < Messages.Count; i++)
                    if (!string.Equals(Messages[i], other.Messages[i], StringComparison.Ordinal)) return false;
            }
            // double.Equals treats NaN as equal to NaN, so "not reported" is stable
            if (v.Shows(ConsoleOutputNames.SpeedFactor) && !SpeedFactor.Equals(other.SpeedFactor)) return false;
            if (v.Shows(ConsoleOutputNames.FlowFactor) && !FlowFactor.Equals(other.FlowFactor)) return false;
            // The job's identity, never its lines: the same job re-projected on every poll
            // must compare equal, and a job really does change only when its bytes do.
            if (v.Shows(ConsoleOutputNames.Gcode) && !string.Equals(GcodeSha, other.GcodeSha, StringComparison.Ordinal)) return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as OutputSnapshot);

        /// <summary>Hashes exactly the fields <see cref="Equals(OutputSnapshot?)"/>
        /// compares, so a hidden category cannot make two equal snapshots hash apart.</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                var v = Visible;
                int h = 17;
                h = h * 31 + v.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Status)) h = h * 31 + Status.GetHashCode();
                if (v.Shows(ConsoleOutputNames.State)) h = h * 31 + State.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Progress)) h = h * 31 + Progress.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Pos) && Pos.HasValue)
                {
                    var p = Pos.Value;
                    h = h * 31 + p.X.GetHashCode();
                    h = h * 31 + p.Y.GetHashCode();
                    h = h * 31 + p.Z.GetHashCode();
                }
                if (v.Shows(ConsoleOutputNames.Homed)) h = h * 31 + (Homed ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.JobFile)) h = h * 31 + JobFile.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Info)) h = h * 31 + Info.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Log))
                {
                    h = h * 31 + Log.Count;
                    for (int i = 0; i < Log.Count; i++)
                        h = h * 31 + (Log[i]?.GetHashCode() ?? 0);
                }
                if (v.Shows(ConsoleOutputNames.Host)) h = h * 31 + HostOut.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Connected)) h = h * 31 + (Connected ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.Reply)) h = h * 31 + Reply.GetHashCode();
                if (v.Shows(ConsoleOutputNames.XHomed)) h = h * 31 + (XHomed ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.YHomed)) h = h * 31 + (YHomed ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.ZHomed)) h = h * 31 + (ZHomed ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.BedOrigin) && BedOrigin.HasValue)
                {
                    var b = BedOrigin.Value;
                    h = h * 31 + b.X.GetHashCode();
                    h = h * 31 + b.Y.GetHashCode();
                    h = h * 31 + b.Z.GetHashCode();
                }
                if (v.Shows(ConsoleOutputNames.ZZero) && ZZero.HasValue) h = h * 31 + ZZero.Value.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Uploaded)) h = h * 31 + Uploaded.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Danger)) h = h * 31 + (Danger ? 1 : 0);
                if (v.Shows(ConsoleOutputNames.Problems)) h = h * 31 + Problems.Count;
                if (v.Shows(ConsoleOutputNames.Messages))
                {
                    h = h * 31 + Messages.Count;
                    for (int i = 0; i < Messages.Count; i++)
                        h = h * 31 + (Messages[i]?.GetHashCode() ?? 0);
                }
                if (v.Shows(ConsoleOutputNames.SpeedFactor)) h = h * 31 + SpeedFactor.GetHashCode();
                if (v.Shows(ConsoleOutputNames.FlowFactor)) h = h * 31 + FlowFactor.GetHashCode();
                if (v.Shows(ConsoleOutputNames.Gcode)) h = h * 31 + GcodeSha.GetHashCode();
                return h;
            }
        }
    }

    /// <summary>Projects the state onto the component outputs.</summary>
    public static class PrintConsoleOutputs
    {
        /// <param name="visible">Which outputs the component registers. Hidden ones are
        /// neither built (the <c>info</c> summary and the job's decoded lines are the
        /// expensive two) nor compared, so a console showing three outputs does not pay
        /// for twenty-seven.</param>
        public static OutputSnapshot From(PrintConsoleState s, OutputVisibility? visible = null)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var vis = visible ?? OutputVisibility.All;
            string connection = PrintConsoleButtons.Lower(s.Connection);
            string status = connection + " - " + (s.PendingAction ?? s.LastResult);
            var v = s.Validation;
            var overrides = s.Overrides;
            var job = s.EffectiveJob;
            bool wantGcode = vis.Shows(ConsoleOutputNames.Gcode);
            return new OutputSnapshot(status, s.RawStatus, s.Progress, s.Position, s.AllHomed, s.JobFile,
                                      vis.Shows(ConsoleOutputNames.Log) ? s.LogTail : Array.Empty<string>(),
                                      vis.Shows(ConsoleOutputNames.Info) ? Info(s, connection) : "",
                                      hostOut: s.Host,
                                      connected: s.Connection == ConsoleConnection.Connected,
                                      reply: s.LastReply,
                                      xHomed: s.XHomed, yHomed: s.YHomed, zHomed: s.ZHomed,
                                      bedOrigin: s.BedOrigin, zZero: s.ZZero,
                                      uploaded: s.Uploaded?.Path ?? "",
                                      danger: v?.Danger ?? false,
                                      problems: v?.Problems,
                                      messages: v?.Messages,
                                      speedFactor: Whole(overrides?.SpeedFactorPercent),
                                      flowFactor: Whole(overrides?.FlowFactorPercent),
                                      gcode: wantGcode && job != null ? job.Lines() : Array.Empty<string>(),
                                      gcodeSha: job?.Sha256 ?? "",
                                      visible: vis);
        }

        static double Whole(double? value)
            => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
               ? Math.Round(value.Value, MidpointRounding.AwayFromZero) : double.NaN;

        /// <summary>Multi-line summary built only from state fields - never from
        /// SnapshotUtc or a poll age, which would change on every poll.</summary>
        static string Info(PrintConsoleState s, string connection)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("host: ").Append(s.Host.Length > 0 ? s.Host : "-").Append('\n');
            sb.Append("connection: ").Append(connection).Append('\n');
            string machine = PrintConsoleButtons.Lower(s.MachineState);
            sb.Append("machine: ").Append(machine);
            if (s.RawStatus.Length > 0 && !string.Equals(s.RawStatus, machine, StringComparison.OrdinalIgnoreCase))
                sb.Append(" (").Append(s.RawStatus).Append(')');
            sb.Append('\n');
            sb.Append("homed: X=").Append(YesNo(s.XHomed))
              .Append(" Y=").Append(YesNo(s.YHomed))
              .Append(" Z=").Append(YesNo(s.ZHomed))
              .Append(" (all: ").Append(YesNo(s.AllHomed)).Append(")\n");
            sb.Append("job: ").Append(s.JobFile.Length > 0 ? s.JobFile : "-")
              .Append(" progress: ").Append((s.Progress * 100.0).ToString("0", inv)).Append("%\n");
            if (s.Uploaded != null)
                sb.Append("uploaded: ").Append(s.Uploaded.Path).Append(" (").Append(s.Uploaded.Size.ToString(inv)).Append(" bytes)\n");
            else
                sb.Append("uploaded: -\n");
            sb.Append("arm configuration known: ").Append(YesNo(s.ArmConfigurationKnown)).Append('\n');
            // Session diagnostics the five consolidated components used to publish. All of
            // it comes from state fields - never from SnapshotUtc or a poll age, which
            // would change `info` on every poll and expire the canvas continuously.
            sb.Append("board: ").Append(s.BoardType.Length > 0 ? s.BoardType : "-");
            if (s.Legacy) sb.Append(" (legacy API)");
            sb.Append('\n');
            sb.Append("transport: ").Append(s.Transport.Length > 0 ? s.Transport : "-").Append('\n');
            sb.Append("kinematics probed: ").Append(YesNo(s.KinematicsProbed))
              .Append("  motion baseline: ").Append(s.MotionBaselineProbed ? "verified" : "unverified").Append('\n');
            sb.Append("home macros: ");
            if (!s.HomeProbeComplete) sb.Append("not probed");
            else sb.Append("all=").Append(YesNo(s.HomeAvailableAll))
                   .Append(" X=").Append(YesNo(s.HomeAvailableX))
                   .Append(" Y=").Append(YesNo(s.HomeAvailableY))
                   .Append(" Z=").Append(YesNo(s.HomeAvailableZ));
            sb.Append('\n');
            sb.Append("trust: ").Append(s.TrustReason == null ? "ok" : s.TrustReason).Append('\n');
            // Only while something is actually going on - an idle console must not grow a
            // line that changes on every poll and expires the canvas for nothing.
            if (!s.Recovery.IsQuiet)
            {
                sb.Append("recovery: e-stop ").Append(YesNo(s.Recovery.StopRequested))
                  .Append(", acknowledged ").Append(YesNo(s.Recovery.StopAcknowledged))
                  .Append(", halt observed ").Append(YesNo(s.Recovery.HaltObserved))
                  .Append(", transport ").Append(s.Recovery.TransportUnavailable ? "unavailable" : "ok")
                  .Append(", reset ").Append(s.Recovery.ResetRequested
                      ? (s.Recovery.RecoveryVerified ? "verified"
                         : s.Recovery.ResetDeliveryUncertain ? "delivery unconfirmed" : "sent")
                      : "not requested")
                  .Append('\n');
                if (s.Recovery.Notice is string recoveryNotice)
                    sb.Append("recovery note: ").Append(recoveryNotice).Append('\n');
            }
            if (s.Validation != null)
                sb.Append("validation: ").Append(s.Validation.Danger ? "DANGER" : "ok")
                  .Append(", ").Append(s.Validation.Problems.Count.ToString(inv)).Append(" problem point(s)")
                  .Append(s.Validation.Warned ? ", warnings" : "").Append('\n');
            else
                sb.Append("validation: -\n");
            sb.Append("last error: ").Append(string.IsNullOrEmpty(s.LastError) ? "-" : s.LastError).Append('\n');
            sb.Append("last result: ").Append(s.LastResult.Length > 0 ? s.LastResult : "-");
            if (s.LastActionWarned) sb.Append("  [warnings]");
            return sb.ToString();
        }

        static string YesNo(bool b) => b ? "yes" : "no";
    }

    /// <summary>The dashboard's one state-derived primary action. A recommendation only:
    /// it names the same action id the full page uses, gated by the same
    /// <see cref="ButtonSet"/>, and nothing here ever sends a command.</summary>
    public static class ConsoleNextAction
    {
        public static (string Id, string Label, bool Enabled) Decide(PrintConsoleState s, ButtonSet b)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (b == null) throw new ArgumentNullException(nameof(b));
            // A recovery the operator has to drive outranks everything else. After this
            // session's E-STOP on a machine that stopped answering, "Reconnecting..." is
            // not the next action - Reset is, and the dashboard must say so.
            if (b.Reset && s.Recovery.StopRequested && !s.Recovery.RecoveryVerified)
                return ("reset", "Reset (" + s.ResetCommand + ")", true);
            switch (s.Connection)
            {
                case ConsoleConnection.None:
                case ConsoleConnection.Disconnected:
                case ConsoleConnection.Error:
                    return ("connect", "Connect", b.Connect);
                case ConsoleConnection.Connecting:
                case ConsoleConnection.Reconnecting:
                    return ("", s.Connection == ConsoleConnection.Connecting ? "Connecting..." : "Reconnecting...", false);
            }
            var st = s.MachineState;
            if (st == MachineStatus.Paused) return ("resume", "Resume print", b.Resume);
            if (st == MachineStatus.Processing || st == MachineStatus.Simulating) return ("tab:2", "Open Job", true);
            if (st == MachineStatus.Halted || st == MachineStatus.Off) return ("reset", "Reset (M999)", b.Reset);
            if (!s.AllHomed) return ("home", "Home all", b.Home);
            if (s.GcodeLines == 0) return ("tab:1", "Open Control", true);
            if (s.Validation == null || s.Validation.Danger) return ("validate", "Validate", b.Validate);
            if (s.Uploaded == null || s.Uploaded.Hash != s.GcodeHash) return ("upload", "Upload", b.Upload);
            return ("start", s.StartLabel, b.Start);
        }
    }

    public enum ExpireVerdict { None, Now, Defer }

    /// <summary>Decides whether a new output snapshot should expire the canvas now,
    /// never, or after <c>delayMs</c>. Callers pass the clock; nothing here reads it.</summary>
    public static class PrintConsoleExpirePolicy
    {
        public static ExpireVerdict Decide(OutputSnapshot? prev, OutputSnapshot next, bool live,
                                           DateTime? lastExpireUtc, DateTime nowUtc, int minIntervalMs,
                                           out int delayMs)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            delayMs = 0;
            if (!live) return ExpireVerdict.None;
            if (prev == null) return ExpireVerdict.Now;
            if (prev.Equals(next)) return ExpireVerdict.None;
            if (lastExpireUtc == null || minIntervalMs <= 0) return ExpireVerdict.Now;

            double elapsed = (nowUtc - lastExpireUtc.Value).TotalMilliseconds;
            if (elapsed >= minIntervalMs) return ExpireVerdict.Now;

            double remaining = Math.Ceiling(minIntervalMs - elapsed);
            if (remaining < 1) remaining = 1;
            if (remaining > minIntervalMs) remaining = minIntervalMs;
            delayMs = (int)remaining;
            return ExpireVerdict.Defer;
        }
    }
}
