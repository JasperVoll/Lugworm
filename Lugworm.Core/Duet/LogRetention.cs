using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lugworm.Core.Duet
{
    /// <summary>User-visible lifecycle of one job log.</summary>
    public enum LogRetentionState
    {
        /// <summary>Recording to the local staging buffer; nothing will be kept unless saved.</summary>
        TemporaryRecording,
        /// <summary>Recording, and the destination is caught up with the buffer.</summary>
        PermanentRecording,
        /// <summary>The job ended; the buffer lives for this session unless saved.</summary>
        TemporaryComplete,
        /// <summary>Bytes are being mirrored to the destination.</summary>
        Syncing,
        /// <summary>The destination could not be reached; the complete local buffer is kept.</summary>
        WaitingToSync,
        /// <summary>The destination holds a verified copy; the staging buffer is gone.</summary>
        Saved,
        /// <summary>A temporary buffer left behind by an unclean shutdown, offered at launch.</summary>
        RecoveredTemporary,
        /// <summary>The local staging itself failed (disk full, permissions). Never Saved.</summary>
        Faulted,
        /// <summary>Terminal: the buffer was removed after every writer closed.</summary>
        Discarded,
    }

    /// <summary>The file operations the coordinator needs, injected so failure, retry
    /// and crash-recovery behaviour is deterministic in tests. Paths are opaque strings
    /// joined by <see cref="Combine"/>; the production adapter maps them to the disk.
    /// Every member may throw; the coordinator turns exceptions into states.</summary>
    public interface IRetentionFileSystem
    {
        string Combine(string a, string b);
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        IReadOnlyList<string> Directories(string root);
        long Length(string path);
        void Append(string path, byte[] data);
        byte[] Read(string path, long offset, int count);
        string ReadText(string path);
        /// <summary>Temp-write-and-replace: the file is either the old or the new text.</summary>
        void WriteTextAtomic(string path, string text);
        void Move(string from, string to);
        void DeleteFile(string path);
        void DeleteDirectory(string path);
    }

    /// <summary>What the staging directory of one job records about itself. No password
    /// or authentication data, ever. Written atomically on every transition.</summary>
    public sealed class JobLogManifest
    {
        /// <summary>Version 2 adds the diagnostics stream and the run OUTCOME. Version 3
        /// adds the immutable G-code archive and the machine context a reprint has to
        /// check. Version 4 adds the three PER-RUN drop counters, so a finished run's
        /// manifest states what the recorder lost rather than leaving it to be assumed;
        /// before it, a complete-looking archive was no evidence that nothing was dropped.
        /// An older manifest still parses: its missing fields take their defaults, an
        /// unknown outcome is reported as unknown rather than as success, and a run with no
        /// archive is listed as not available for reprint.</summary>
        public const int CurrentVersion = 4;
        public const string FileName = "manifest.json";
        public const string PrintFile = "print.csv";
        public const string EventsFile = "events.log";
        /// <summary>Bounded raw-telemetry captures, one JSON object per line.</summary>
        public const string DiagnosticsFile = "diagnostics.jsonl";
        /// <summary>The run's exact G-code bytes. Written once when the run starts and
        /// never touched again: <c>print.gcode</c> on the board is overwritten by the next
        /// upload, so the remote name proves nothing about what a past run printed.</summary>
        public const string GcodeFile = "job.gcode";
        public const string Partial = ".partial";

        /// <summary>The version this manifest was written with - <see cref="CurrentVersion"/>
        /// for a new one, and whatever <see cref="Parse"/> read for an existing one. It is
        /// PRESERVED across a round trip on purpose: the drop counters default to 0, and
        /// only the version distinguishes "this run dropped nothing" from "this file is too
        /// old to say". Promoting an old manifest on rewrite would turn a silence into a
        /// claim; the coordinator raises it to the current version only when it has actually
        /// recorded the counters.</summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>True when <see cref="RowsDropped"/> and its two companions mean what
        /// they say. False for a version 1-3 manifest, where their 0 means "not recorded".</summary>
        public bool DropCountsRecorded => Version >= 4;

        public string Id { get; set; } = "";
        public string JobName { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public LogRetentionState State { get; set; }
        /// <summary>True once the operator (or the keep-all preference) promised to keep it.</summary>
        public bool Permanent { get; set; }
        /// <summary>The LOG is finished. This says nothing about how the PRINT ended: a
        /// cancelled, halted or connection-lost run finalises its log exactly like a
        /// finished one. Read <see cref="Outcome"/> for that.</summary>
        public bool Complete { get; set; }
        /// <summary>How the run itself ended - the terminal machine state or finish reason
        /// ("Idle", "Halted", "Cancelling", "ConnectionLost"). Null means unknown, which is
        /// what a version-1 manifest and an interrupted session both report; it must never
        /// be displayed as a successful print.</summary>
        public string? Outcome { get; set; }
        public string? Destination { get; set; }
        /// <summary>Directory under the destination that this job owns (collision-suffixed).</summary>
        public string? DestinationDirectory { get; set; }
        public long PrintOffset { get; set; }
        public long EventsOffset { get; set; }
        public long DiagnosticsOffset { get; set; }
        public long GcodeOffset { get; set; }
        public bool Finalized { get; set; }
        public string? LastError { get; set; }

        // ---- version 3: the immutable archive and the machine it was made on ----

        /// <summary>SHA-256 of <see cref="GcodeFile"/>, "" when no archive was written
        /// (an externally started run, or a log from before archives existed).</summary>
        public string GcodeSha256 { get; set; } = "";
        public long GcodeBytes { get; set; }
        /// <summary>Run identity of the <see cref="JobSnapshot"/> that started this run.</summary>
        public string RunId { get; set; } = "";
        public string Host { get; set; } = "";
        /// <summary>The machine identity the session expected. Never a password.</summary>
        public string Fingerprint { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string Mechanism { get; set; } = "";
        public string Transport { get; set; } = "";
        /// <summary>The run was an M37 firmware simulation - it deposited nothing.</summary>
        public bool DryRun { get; set; }

        // ---- version 4: what the recorder lost during THIS run ----

        /// <summary>CSV rows the retention worker refused because its queue was full,
        /// during this run only. Not a process-lifetime total: the service holds those and
        /// writes the difference between the run's start and its end, so a long session's
        /// earlier losses are never charged to the latest print.</summary>
        /// <remarks>Distinct from <c>overwrittenBefore</c> in <c>diagnostics.jsonl</c>.
        /// That field counts entries evicted from the bounded RING as it wrapped, which is
        /// the capture window doing its job; these three count records the worker never
        /// wrote at all, which is a hole in the evidence.</remarks>
        public long RowsDropped { get; set; }
        /// <summary>Diagnostic lines the worker refused during this run - a capture with a
        /// hole in it. Counted apart from <see cref="RowsDropped"/> because a diagnostic
        /// burst has its own, smaller share of the queue.</summary>
        public long DiagnosticsDropped { get; set; }
        /// <summary>Diagnostic lines that aged out of the in-memory session window during
        /// this run. The job's own diagnostics file still holds them; this says only that
        /// the console's after-the-fact window no longer does.</summary>
        public long SessionDiagnosticsDropped { get; set; }

        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\"version\":").Append(Version.ToString(inv));
            sb.Append(",\"id\":"); JsonText.Quote(sb, Id);
            sb.Append(",\"jobName\":"); JsonText.Quote(sb, JobName);
            sb.Append(",\"startedUtc\":"); JsonText.Quote(sb, StartedUtc.ToString("o", inv));
            sb.Append(",\"completedUtc\":");
            if (CompletedUtc.HasValue) JsonText.Quote(sb, CompletedUtc.Value.ToString("o", inv)); else sb.Append("null");
            sb.Append(",\"state\":"); JsonText.Quote(sb, State.ToString());
            sb.Append(",\"permanent\":").Append(Permanent ? "true" : "false");
            sb.Append(",\"complete\":").Append(Complete ? "true" : "false");
            sb.Append(",\"outcome\":"); if (Outcome == null) sb.Append("null"); else JsonText.Quote(sb, Outcome);
            sb.Append(",\"destination\":"); if (Destination == null) sb.Append("null"); else JsonText.Quote(sb, Destination);
            sb.Append(",\"destinationDirectory\":"); if (DestinationDirectory == null) sb.Append("null"); else JsonText.Quote(sb, DestinationDirectory);
            sb.Append(",\"printOffset\":").Append(PrintOffset.ToString(inv));
            sb.Append(",\"eventsOffset\":").Append(EventsOffset.ToString(inv));
            sb.Append(",\"diagnosticsOffset\":").Append(DiagnosticsOffset.ToString(inv));
            sb.Append(",\"gcodeOffset\":").Append(GcodeOffset.ToString(inv));
            sb.Append(",\"gcodeSha256\":"); JsonText.Quote(sb, GcodeSha256 ?? "");
            sb.Append(",\"gcodeBytes\":").Append(GcodeBytes.ToString(inv));
            sb.Append(",\"runId\":"); JsonText.Quote(sb, RunId ?? "");
            sb.Append(",\"host\":"); JsonText.Quote(sb, Host ?? "");
            sb.Append(",\"fingerprint\":"); JsonText.Quote(sb, Fingerprint ?? "");
            sb.Append(",\"profileId\":"); JsonText.Quote(sb, ProfileId ?? "");
            sb.Append(",\"mechanism\":"); JsonText.Quote(sb, Mechanism ?? "");
            sb.Append(",\"transport\":"); JsonText.Quote(sb, Transport ?? "");
            sb.Append(",\"dryRun\":").Append(DryRun ? "true" : "false");
            sb.Append(",\"rowsDropped\":").Append(RowsDropped.ToString(inv));
            sb.Append(",\"diagnosticsDropped\":").Append(DiagnosticsDropped.ToString(inv));
            sb.Append(",\"sessionDiagnosticsDropped\":").Append(SessionDiagnosticsDropped.ToString(inv));
            sb.Append(",\"finalized\":").Append(Finalized ? "true" : "false");
            sb.Append(",\"lastError\":"); if (LastError == null) sb.Append("null"); else JsonText.Quote(sb, LastError);
            sb.Append('}');
            return sb.ToString();
        }

        public static JobLogManifest Parse(string text)
        {
            var json = JsonValue.Parse(text);
            if (json.Kind != JsonKind.Object) throw new FormatException("manifest is not an object");
            int version = json.Int("version", -1);
            if (version < 1 || version > CurrentVersion)
                throw new FormatException("unsupported manifest version " + version.ToString(CultureInfo.InvariantCulture));
            string id = json.Str("id");
            if (id.Length == 0) throw new FormatException("manifest has no id");
            var m = new JobLogManifest
            {
                // Kept as read, never promoted: see the Version remark. A version 1-3 file
                // simply has no drop counters, and the zeros below then mean "not recorded".
                Version = version,
                Id = id,
                JobName = json.Str("jobName"),
                Permanent = json.Bool("permanent"),
                Complete = json.Bool("complete"),
                Finalized = json.Bool("finalized"),
                PrintOffset = (long)json.Num("printOffset"),
                EventsOffset = (long)json.Num("eventsOffset"),
                DiagnosticsOffset = (long)json.Num("diagnosticsOffset"),
                GcodeOffset = (long)json.Num("gcodeOffset"),
                GcodeSha256 = json.Str("gcodeSha256"),
                GcodeBytes = (long)json.Num("gcodeBytes"),
                RunId = json.Str("runId"),
                Host = json.Str("host"),
                Fingerprint = json.Str("fingerprint"),
                ProfileId = json.Str("profileId"),
                Mechanism = json.Str("mechanism"),
                Transport = json.Str("transport"),
                DryRun = json.Bool("dryRun"),
                RowsDropped = (long)json.Num("rowsDropped"),
                DiagnosticsDropped = (long)json.Num("diagnosticsDropped"),
                SessionDiagnosticsDropped = (long)json.Num("sessionDiagnosticsDropped"),
            };
            var outcome = json["outcome"]; m.Outcome = outcome.Kind == JsonKind.String ? outcome.AsString() : null;
            var dest = json["destination"]; m.Destination = dest.Kind == JsonKind.String ? dest.AsString() : null;
            var dir = json["destinationDirectory"]; m.DestinationDirectory = dir.Kind == JsonKind.String ? dir.AsString() : null;
            var err = json["lastError"]; m.LastError = err.Kind == JsonKind.String ? err.AsString() : null;
            if (!DateTime.TryParse(json.Str("startedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
                throw new FormatException("manifest startedUtc is not a date");
            m.StartedUtc = started;
            var completed = json["completedUtc"];
            if (completed.Kind == JsonKind.String
                && DateTime.TryParse(completed.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var done))
                m.CompletedUtc = done;
            if (!Enum.TryParse(json.Str("state"), out LogRetentionState state)) throw new FormatException("manifest state is unknown");
            m.State = state;
            if (m.PrintOffset < 0 || m.EventsOffset < 0 || m.DiagnosticsOffset < 0 || m.GcodeOffset < 0)
                throw new FormatException("manifest offsets are negative");
            if (m.GcodeBytes < 0) throw new FormatException("manifest gcodeBytes is negative");
            if (m.RowsDropped < 0 || m.DiagnosticsDropped < 0 || m.SessionDiagnosticsDropped < 0)
                throw new FormatException("manifest drop counts are negative");
            return m;
        }
    }

    /// <summary>Immutable view of one job log for the UI.</summary>
    public sealed class JobLogEntry
    {
        public string Id { get; }
        public string JobName { get; }
        public DateTime StartedUtc { get; }
        public LogRetentionState State { get; }
        public bool Permanent { get; }
        public bool Complete { get; }
        public bool Recovered { get; }
        public long StagedBytes { get; }
        public long SyncedBytes { get; }
        public string? DestinationDirectory { get; }
        public string StagingDirectory { get; }
        public string? LastError { get; }
        /// <summary>How the RUN ended, or null when unknown. <see cref="Complete"/> only
        /// says the LOG finished: a cancelled print finalises its log like a finished one,
        /// so a UI must read this before calling a print successful.</summary>
        public string? Outcome { get; }

        public JobLogEntry(string id, string jobName, DateTime startedUtc, LogRetentionState state, bool permanent,
                           bool complete, bool recovered, long stagedBytes, long syncedBytes,
                           string? destinationDirectory, string stagingDirectory, string? lastError,
                           string? outcome = null)
        {
            Id = id; JobName = jobName; StartedUtc = startedUtc; State = state; Permanent = permanent;
            Complete = complete; Recovered = recovered; StagedBytes = stagedBytes; SyncedBytes = syncedBytes;
            DestinationDirectory = destinationDirectory; StagingDirectory = stagingDirectory; LastError = lastError;
            Outcome = outcome;
        }

        /// <summary>The badge text the spec names for each state.</summary>
        public string Label
        {
            get
            {
                switch (State)
                {
                    case LogRetentionState.TemporaryRecording: return "Temporary recording";
                    case LogRetentionState.PermanentRecording: return "Permanent recording";
                    case LogRetentionState.TemporaryComplete: return "Temporary complete";
                    case LogRetentionState.Syncing: return "Syncing";
                    case LogRetentionState.WaitingToSync: return "Waiting to sync";
                    case LogRetentionState.Saved: return "Saved";
                    case LogRetentionState.RecoveredTemporary: return "Recovered temporary log";
                    case LogRetentionState.Faulted: return "Logging fault";
                    default: return "Discarded";
                }
            }
        }
    }

    /// <summary>Immutable logging status for the header badge, footer and Logs page.</summary>
    public sealed class LogRetentionStatus
    {
        public bool KeepAll { get; }
        public string? Destination { get; }
        public JobLogEntry? Current { get; }
        public IReadOnlyList<JobLogEntry> Jobs { get; }
        /// <summary>A problem that needs attention (destination unreachable, staging fault), or null.</summary>
        public string? Attention { get; }
        /// <summary>Header badge: Temporary, Recording, Syncing, Waiting to sync, Saved.</summary>
        public string Badge { get; }
        /// <summary>The last runs, newest first, for the Job page's Recent prints list.
        /// Includes runs from earlier sessions found at the destination, and runs with no
        /// archived G-code (which are visible and not reprintable).</summary>
        public IReadOnlyList<RecentPrintEntry> Recent { get; }

        public LogRetentionStatus(bool keepAll, string? destination, JobLogEntry? current,
                                  IReadOnlyList<JobLogEntry> jobs, string? attention, string badge,
                                  IReadOnlyList<RecentPrintEntry>? recent = null)
        {
            KeepAll = keepAll; Destination = destination; Current = current; Jobs = jobs;
            Attention = attention; Badge = badge ?? "";
            Recent = recent ?? Array.Empty<RecentPrintEntry>();
        }

        public static readonly LogRetentionStatus None =
            new LogRetentionStatus(false, null, null, Array.Empty<JobLogEntry>(), null, "Temporary");
    }

    /// <summary>Proof, taken WHILE a run records, that the recorder is writing where it
    /// claims to (finding D1, checklist row 10a.12).</summary>
    /// <remarks>
    /// <para>"Look in %LOCALAPPDATA%/Lugworm/PrintLogBuffer afterwards" can never
    /// establish this: a run that copied successfully DELETES its staging directory, so an
    /// empty folder is what a healthy session leaves behind and an unhealthy one leaves
    /// behind the same thing. The check therefore has to happen during the run, and its
    /// result has to be an immutable object that outlives the folder it describes.</para>
    /// <para>Cheap by construction: every field comes from what the coordinator already
    /// holds in memory, so taking one costs a lock and no file system call at all. The byte
    /// count is what this process appended and flushed to the staging file, not a stat of
    /// it.</para>
    /// </remarks>
    public sealed class LoggingHealthSnapshot
    {
        /// <summary>The log id of the run this describes.</summary>
        public string RunId { get; }
        public string StagingDirectory { get; }
        /// <summary>Full path of the run's diagnostics stream inside the staging directory.</summary>
        public string DiagnosticsPath { get; }
        /// <summary>The diagnostics file was created for this run and its staging directory
        /// has not been removed yet.</summary>
        public bool DiagnosticsPresent { get; }
        /// <summary>Bytes this process has appended to the diagnostics stream so far.</summary>
        public long DiagnosticsBytes { get; }
        /// <summary>Bytes appended to <c>print.csv</c> so far - the row stream the
        /// diagnostics exist to explain.</summary>
        public long PrintBytes { get; }
        /// <summary>Live PROCESS-LIFETIME totals, not this run's share. The per-run share is
        /// what the manifest records; these say what the session as a whole has lost.</summary>
        public long RowsDropped { get; }
        public long DiagnosticsDropped { get; }
        public long SessionDiagnosticsDropped { get; }

        public LoggingHealthSnapshot(string runId, string stagingDirectory, string diagnosticsPath,
                                     bool diagnosticsPresent, long diagnosticsBytes, long printBytes,
                                     long rowsDropped, long diagnosticsDropped, long sessionDiagnosticsDropped)
        {
            RunId = runId ?? ""; StagingDirectory = stagingDirectory ?? ""; DiagnosticsPath = diagnosticsPath ?? "";
            DiagnosticsPresent = diagnosticsPresent; DiagnosticsBytes = diagnosticsBytes; PrintBytes = printBytes;
            RowsDropped = rowsDropped; DiagnosticsDropped = diagnosticsDropped;
            SessionDiagnosticsDropped = sessionDiagnosticsDropped;
        }

        /// <summary>One line for the console's logging-health text.</summary>
        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            return "staging " + StagingDirectory
                 + ": print.csv " + PrintBytes.ToString(inv) + " B, diagnostics.jsonl "
                 + (DiagnosticsPresent ? DiagnosticsBytes.ToString(inv) + " B" : "absent")
                 + "; dropped this session " + RowsDropped.ToString(inv) + " row(s), "
                 + DiagnosticsDropped.ToString(inv) + " diagnostic line(s), "
                 + SessionDiagnosticsDropped.ToString(inv) + " aged out of the session window";
        }
    }

    /// <summary>The job-log retention state machine. Every job records to a local
    /// staging directory first - canonical until a destination copy is verified - and is
    /// mirrored append-only to `.partial` files at the chosen destination when kept.</summary>
    /// <remarks>
    /// <para>Pure with respect to time and disk: the clock, the id suffix and the file
    /// system are injected. All state lives under one lock; destination I/O runs
    /// OUTSIDE it (<see cref="SyncOnce"/> plans under the lock, performs, then records),
    /// so a stalled network share can never block a record append or a status read.
    /// The production caller appends from a dedicated thread, never from the poll loop.</para>
    /// <para>Synchronisation is idempotent: the acknowledged offset of a file is whatever
    /// length the destination `.partial` already has, so a retry after an ambiguous
    /// failure continues from the bytes that actually landed and never duplicates a
    /// row. A changed destination resets the offsets and replays the intact buffer.
    /// Finalisation renames `.partial` to the final names, verifies the lengths, and only
    /// then removes the staging buffer.</para>
    /// </remarks>
    public sealed class LogRetentionCoordinator
    {
        public const string QuarantineDirectory = "quarantine";
        public const string OwnerMarker = ".iamlab-job";
        public const int SyncChunkBytes = 1 << 20;

        sealed class Job
        {
            public JobLogManifest Manifest = new JobLogManifest();
            public string StagingDir = "";
            public bool Recovered;
            public long StagedPrint, StagedEvents, StagedDiagnostics, StagedGcode;   // bytes appended (mirrors file length)
            public bool SaveRequested;
            /// <summary>The staging directory still exists and holds this run's streams.
            /// False once finalisation removed it (or once staging failed outright), which
            /// is what makes a health snapshot honest about a run that is already saved.</summary>
            public bool StagingLive;
            /// <summary>The worker's process-lifetime drop totals as they stood when this
            /// run started. The manifest records the difference from here, never the total.</summary>
            public long BaseRowsDropped, BaseDiagnosticsDropped, BaseSessionDiagnosticsDropped;
        }

        /// <summary>A finished run found at the destination, from this session or an
        /// earlier one. Read-only: the coordinator never writes to a run it did not open.</summary>
        sealed class ArchivedRun
        {
            public JobLogManifest Manifest = new JobLogManifest();
            public string Directory = "";
        }

        readonly object _lock = new object();
        readonly IRetentionFileSystem _fs;
        readonly string _root;
        readonly Func<DateTime> _clock;
        readonly Func<string> _suffix;
        readonly List<Job> _jobs = new List<Job>();
        readonly List<ArchivedRun> _history = new List<ArchivedRun>();
        readonly List<string> _notes = new List<string>();
        Job? _current;
        bool _keepAll;
        string? _destination;
        string? _fault;
        /// <summary>The last process-lifetime drop totals the worker reported, kept so a
        /// run that starts can take them as its baseline. Only <see cref="ObserveDropTotals"/>
        /// writes them.</summary>
        long _rowsDroppedTotal, _diagnosticsDroppedTotal, _sessionDiagnosticsDroppedTotal;

        public LogRetentionCoordinator(IRetentionFileSystem fs, string stagingRoot, Func<DateTime> clock, Func<string> idSuffix)
        {
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _root = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _suffix = idSuffix ?? throw new ArgumentNullException(nameof(idSuffix));
        }

        public bool KeepAll { get { lock (_lock) return _keepAll; } }
        public string? Destination { get { lock (_lock) return _destination; } }
        /// <summary>One-line notes about recovery and quarantine, for the UI.</summary>
        public IReadOnlyList<string> Notes { get { lock (_lock) return _notes.ToArray(); } }

        // ---- preferences -----------------------------------------------------------------

        /// <summary>Keep every job from now on. Enabling promotes the CURRENT job and
        /// replays it from its first record; earlier temporary jobs stay temporary until
        /// saved one by one. A missing destination leaves the preference off - the caller
        /// asks for a folder first.</summary>
        public bool SetKeepAll(bool keepAll)
        {
            lock (_lock)
            {
                if (keepAll && _destination == null) { _keepAll = false; return false; }
                _keepAll = keepAll;
                if (keepAll && _current != null && !_current.Manifest.Permanent) PromoteLocked(_current);
                return _keepAll;
            }
        }

        /// <summary>Selects the destination. Unsaved permanent jobs replay from byte zero
        /// to the new location; nothing staged is lost. Null clears it and turns keep-all off.</summary>
        public void SetDestination(string? folder)
        {
            lock (_lock)
            {
                folder = string.IsNullOrWhiteSpace(folder) ? null : folder!.Trim();
                if (string.Equals(folder, _destination, StringComparison.Ordinal)) return;
                _destination = folder;
                if (folder == null) _keepAll = false;
                foreach (var job in _jobs)
                {
                    var m = job.Manifest;
                    if (!m.Permanent || m.Finalized || m.State == LogRetentionState.Saved || m.State == LogRetentionState.Discarded) continue;
                    m.Destination = folder;
                    m.DestinationDirectory = null;
                    m.PrintOffset = 0;
                    m.EventsOffset = 0;
                    m.LastError = null;
                    m.State = folder == null ? LogRetentionState.WaitingToSync : LogRetentionState.Syncing;
                    PersistLocked(job);
                }
            }
        }

        // ---- recording -------------------------------------------------------------------

        /// <summary>Starts a job buffer. A job still open is completed first.</summary>
        public string BeginJob(string jobName)
        {
            lock (_lock)
            {
                if (_current != null) CompleteLocked(_current);
                var now = _clock();
                string id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + _suffix();
                var job = new Job
                {
                    StagingDir = _fs.Combine(_root, id),
                    Manifest = new JobLogManifest
                    {
                        Id = id,
                        JobName = jobName ?? "",
                        StartedUtc = now,
                        Permanent = _keepAll && _destination != null,
                        Destination = _keepAll ? _destination : null,
                    },
                    // Whatever the session had already lost belongs to the runs before this
                    // one; this run's manifest counts from here.
                    BaseRowsDropped = _rowsDroppedTotal,
                    BaseDiagnosticsDropped = _diagnosticsDroppedTotal,
                    BaseSessionDiagnosticsDropped = _sessionDiagnosticsDroppedTotal,
                };
                job.Manifest.State = job.Manifest.Permanent ? LogRetentionState.Syncing : LogRetentionState.TemporaryRecording;
                try
                {
                    _fs.CreateDirectory(job.StagingDir);
                    _fs.Append(_fs.Combine(job.StagingDir, JobLogManifest.PrintFile), Array.Empty<byte>());
                    _fs.Append(_fs.Combine(job.StagingDir, JobLogManifest.EventsFile), Array.Empty<byte>());
                    _fs.Append(_fs.Combine(job.StagingDir, JobLogManifest.DiagnosticsFile), Array.Empty<byte>());
                    job.StagingLive = true;
                    PersistLocked(job);
                }
                catch (Exception ex)
                {
                    job.Manifest.State = LogRetentionState.Faulted;
                    job.Manifest.LastError = "local staging failed: " + ex.Message;
                }
                _jobs.Add(job);
                _current = job;
                return id;
            }
        }

        /// <summary>The firmware often reports the file name a poll or two after the job
        /// starts; this fills it in on the current job without starting another.</summary>
        public void NameCurrentJob(string jobName)
        {
            if (string.IsNullOrWhiteSpace(jobName)) return;
            lock (_lock)
            {
                var job = _current;
                if (job == null || string.Equals(job.Manifest.JobName, jobName, StringComparison.Ordinal)) return;
                job.Manifest.JobName = jobName;
                TryPersist(job);
            }
        }

        /// <summary>Appends one CSV row (without newline) to the current job's staged record.</summary>
        public void AppendRecord(string csvRow)
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null || job.Manifest.State == LogRetentionState.Faulted) return;
                AppendLocked(job, JobLogManifest.PrintFile, csvRow, ref job.StagedPrint);
            }
        }

        /// <summary>Appends one readable event line to the current job's events file.</summary>
        public void AppendEvent(string line)
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null || job.Manifest.State == LogRetentionState.Faulted) return;
                AppendLocked(job, JobLogManifest.EventsFile, line, ref job.StagedEvents);
            }
        }

        /// <summary>Appends one raw-telemetry diagnostic line (a JSON object) to the current
        /// job's diagnostics stream. Kept apart from the CSV so a capture can be large and
        /// bursty without disturbing the row stream or its offsets.</summary>
        public void AppendDiagnostic(string line)
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null || job.Manifest.State == LogRetentionState.Faulted) return;
                AppendLocked(job, JobLogManifest.DiagnosticsFile, line, ref job.StagedDiagnostics);
            }
        }

        /// <summary>Writes the run's exact G-code beside its log, once, and records the
        /// machine it is being run on. This is what makes a later reprint possible and
        /// verifiable: the remote file name is not evidence, because <c>print.gcode</c> is
        /// overwritten by the next upload, and two prints of one file are two runs.</summary>
        /// <param name="gcode">The exact bytes that were started, or null/empty for a run
        /// this session did not start - then only the context is recorded and the run is
        /// listed as "bytes unknown", never as reprintable.</param>
        /// <returns>True when an archive was written.</returns>
        public bool ArchiveCurrentJob(byte[]? gcode, string? runId, ReprintContext? machine, bool dryRun)
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null) return false;
                var m = job.Manifest;
                m.RunId = runId ?? "";
                if (machine != null)
                {
                    m.Host = machine.Host;
                    m.Fingerprint = machine.Fingerprint;
                    m.ProfileId = machine.ProfileId;
                    m.Mechanism = machine.Mechanism;
                    m.Transport = machine.Transport;
                }
                m.DryRun = dryRun;
                // Written ONCE. A second call for the same run records context only: the
                // archive is the bytes the run started with, and nothing may replace them.
                if (gcode == null || gcode.Length == 0 || job.StagedGcode > 0
                    || m.State == LogRetentionState.Faulted)
                {
                    TryPersist(job);
                    return false;
                }
                try
                {
                    _fs.Append(_fs.Combine(job.StagingDir, JobLogManifest.GcodeFile), gcode);
                    job.StagedGcode = gcode.LongLength;
                    m.GcodeBytes = gcode.LongLength;
                    m.GcodeSha256 = JobSnapshot.Sha256Hex(gcode);
                    if (m.Permanent && !m.Complete && m.State == LogRetentionState.PermanentRecording)
                        m.State = LogRetentionState.Syncing;
                    PersistLocked(job);
                    return true;
                }
                catch (Exception ex)
                {
                    // A failed archive must not fault the log: the row stream matters more
                    // than the reprint, and the run then simply lists as not reprintable.
                    job.StagedGcode = 0;
                    m.GcodeBytes = 0;
                    m.GcodeSha256 = "";
                    _notes.Add("job archive failed for " + m.Id + ": " + ex.Message);
                    TryPersist(job);
                    return false;
                }
            }
        }

        /// <summary>Records how the RUN ended, separately from whether the log finished.
        /// Without it a cancelled print and a completed one produce identical manifests.</summary>
        public void SetJobOutcome(string? outcome)
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null || string.IsNullOrWhiteSpace(outcome)) return;
                if (string.Equals(job.Manifest.Outcome, outcome, StringComparison.Ordinal)) return;
                job.Manifest.Outcome = outcome;
                TryPersist(job);
            }
        }

        // ---- measurable logging acceptance (finding D1, rows 10a.11 / 10a.12) -------------

        /// <summary>Reports the worker's PROCESS-LIFETIME drop totals. The coordinator keeps
        /// the value each run started from and writes the DIFFERENCE into that run's
        /// manifest, so a session that has been open all afternoon does not charge the
        /// afternoon's losses to the print that happens to end last.</summary>
        /// <remarks>
        /// <para>Called from the retention worker, cheaply and often: the manifest is
        /// rewritten only when a number actually moved, so an idle session writes nothing.
        /// A run that starts between two reports takes the LAST reported totals as its
        /// baseline, which can attribute at most one worker cycle's losses to the newer
        /// run - a bias towards over-reporting the run being watched, never towards
        /// hiding a loss.</para>
        /// <para>Totals only ever grow. A caller that resets them (a restarted service)
        /// would otherwise produce a negative delta, so the difference is clamped at zero
        /// and the baseline follows the smaller total down.</para>
        /// </remarks>
        public void ObserveDropTotals(long rows, long diagnostics, long sessionDiagnostics)
        {
            lock (_lock)
            {
                _rowsDroppedTotal = rows;
                _diagnosticsDroppedTotal = diagnostics;
                _sessionDiagnosticsDroppedTotal = sessionDiagnostics;
                var job = _current;
                if (job == null) return;
                if (rows < job.BaseRowsDropped) job.BaseRowsDropped = rows;
                if (diagnostics < job.BaseDiagnosticsDropped) job.BaseDiagnosticsDropped = diagnostics;
                if (sessionDiagnostics < job.BaseSessionDiagnosticsDropped) job.BaseSessionDiagnosticsDropped = sessionDiagnostics;
                long r = rows - job.BaseRowsDropped;
                long d = diagnostics - job.BaseDiagnosticsDropped;
                long s = sessionDiagnostics - job.BaseSessionDiagnosticsDropped;
                var m = job.Manifest;
                if (m.RowsDropped == r && m.DiagnosticsDropped == d && m.SessionDiagnosticsDropped == s
                    && m.Version == JobLogManifest.CurrentVersion) return;
                m.RowsDropped = r;
                m.DiagnosticsDropped = d;
                m.SessionDiagnosticsDropped = s;
                // The counts are now recorded, so this manifest is a version-4 statement -
                // including a recovered older one that keeps recording in this session.
                m.Version = JobLogManifest.CurrentVersion;
                if (m.State != LogRetentionState.Faulted) TryPersist(job);
            }
        }

        /// <summary>What the recorder is doing RIGHT NOW, or null when no run is recording.
        /// Costs one lock and no file system call - see <see cref="LoggingHealthSnapshot"/>
        /// for why the check cannot be made after the run instead.</summary>
        public LoggingHealthSnapshot? StagingHealth()
        {
            lock (_lock)
            {
                var job = _current;
                if (job == null) return null;
                return new LoggingHealthSnapshot(
                    job.Manifest.Id, job.StagingDir,
                    _fs.Combine(job.StagingDir, JobLogManifest.DiagnosticsFile),
                    job.StagingLive, job.StagedDiagnostics, job.StagedPrint,
                    _rowsDroppedTotal, _diagnosticsDroppedTotal, _sessionDiagnosticsDroppedTotal);
            }
        }

        void AppendLocked(Job job, string file, string text, ref long staged)
        {
            var bytes = Encoding.UTF8.GetBytes((text ?? "") + "\n");
            try
            {
                _fs.Append(_fs.Combine(job.StagingDir, file), bytes);
                staged += bytes.Length;
                // more bytes than acknowledged: a permanent job is behind again
                if (job.Manifest.Permanent && !job.Manifest.Complete && job.Manifest.State == LogRetentionState.PermanentRecording)
                    job.Manifest.State = LogRetentionState.Syncing;
            }
            catch (Exception ex)
            {
                job.Manifest.State = LogRetentionState.Faulted;
                job.Manifest.LastError = "local staging failed: " + ex.Message;
                TryPersist(job);
            }
        }

        /// <summary>The current job ended (finished, cancelled, connection lost).</summary>
        public void CompleteJob()
        {
            lock (_lock)
            {
                if (_current == null) return;
                CompleteLocked(_current);
                _current = null;
            }
        }

        void CompleteLocked(Job job)
        {
            var m = job.Manifest;
            if (m.Complete) return;
            m.Complete = true;
            m.CompletedUtc = _clock();
            if (m.State == LogRetentionState.Faulted) { TryPersist(job); return; }
            m.State = m.Permanent ? (m.Destination == null ? LogRetentionState.WaitingToSync : LogRetentionState.Syncing)
                                  : LogRetentionState.TemporaryComplete;
            TryPersist(job);
        }

        /// <summary>Promotes one job (active or complete) to permanent. Saving an active
        /// job starts synchronisation from its first record while recording continues.</summary>
        public bool SaveJob(string id)
        {
            lock (_lock)
            {
                var job = Find(id);
                if (job == null || _destination == null) return false;
                if (job.Manifest.State == LogRetentionState.Discarded || job.Manifest.State == LogRetentionState.Faulted) return false;
                PromoteLocked(job);
                return true;
            }
        }

        void PromoteLocked(Job job)
        {
            var m = job.Manifest;
            if (m.State == LogRetentionState.Saved || m.State == LogRetentionState.Discarded || m.State == LogRetentionState.Faulted) return;
            m.Permanent = true;
            job.SaveRequested = true;
            if (m.Destination == null) { m.Destination = _destination; m.DestinationDirectory = null; m.PrintOffset = 0; m.EventsOffset = 0; }
            if (m.State == LogRetentionState.RecoveredTemporary) job.Recovered = true;
            m.State = m.Destination == null ? LogRetentionState.WaitingToSync : LogRetentionState.Syncing;
            PersistLocked(job);
        }

        /// <summary>Clears the last error of every waiting job so the next
        /// <see cref="SyncOnce"/> tries again.</summary>
        public void Retry()
        {
            lock (_lock)
                foreach (var job in _jobs)
                    if (job.Manifest.State == LogRetentionState.WaitingToSync && job.Manifest.Destination != null)
                    {
                        job.Manifest.State = LogRetentionState.Syncing;
                        job.Manifest.LastError = null;
                        TryPersist(job);
                    }
        }

        /// <summary>Removes a job's buffer after explicit confirmation. Never an active job.</summary>
        public bool Discard(string id)
        {
            lock (_lock)
            {
                var job = Find(id);
                if (job == null || ReferenceEquals(job, _current)) return false;
                if (job.Manifest.State == LogRetentionState.Saved) { _jobs.Remove(job); return true; }
                try { _fs.DeleteDirectory(job.StagingDir); } catch (Exception ex) { _notes.Add("discard failed: " + ex.Message); return false; }
                job.StagingLive = false;
                job.Manifest.State = LogRetentionState.Discarded;
                _jobs.Remove(job);
                return true;
            }
        }

        // ---- synchronisation (called from a worker; destination I/O outside the lock) ------

        /// <summary>Performs one bounded synchronisation step for every job that needs
        /// one. Returns true when bytes moved or a job was finalised.</summary>
        public bool SyncOnce()
        {
            bool progressed = false;
            List<Job> candidates;
            lock (_lock)
            {
                candidates = new List<Job>();
                foreach (var job in _jobs)
                    if (job.Manifest.State == LogRetentionState.Syncing && job.Manifest.Destination != null) candidates.Add(job);
            }
            foreach (var job in candidates) progressed |= SyncJob(job);
            return progressed;
        }

        bool SyncJob(Job job)
        {
            // plan under the lock
            string stagingDir, destination, id; string? destDir;
            long stagedPrint, stagedEvents, stagedDiagnostics, stagedGcode; bool complete;
            lock (_lock)
            {
                var m = job.Manifest;
                if (m.State != LogRetentionState.Syncing || m.Destination == null) return false;
                stagingDir = job.StagingDir; destination = m.Destination; destDir = m.DestinationDirectory; id = m.Id;
                stagedPrint = job.StagedPrint; stagedEvents = job.StagedEvents;
                stagedDiagnostics = job.StagedDiagnostics; stagedGcode = job.StagedGcode; complete = m.Complete;
            }
            bool progressed = false;
            try
            {
                if (destDir == null) destDir = ClaimDestinationDirectory(destination, id);
                long newPrint = MirrorFile(stagingDir, destDir, JobLogManifest.PrintFile, stagedPrint, out bool movedPrint);
                long newEvents = MirrorFile(stagingDir, destDir, JobLogManifest.EventsFile, stagedEvents, out bool movedEvents);
                long newDiag = MirrorFile(stagingDir, destDir, JobLogManifest.DiagnosticsFile, stagedDiagnostics, out bool movedDiag);
                // The archive is a fourth stream with the same idempotent rule. A run with
                // no archived bytes mirrors nothing, so an unstarted-here job never leaves
                // an empty job.gcode behind that would look like a reprintable run.
                bool movedGcode = false;
                long newGcode = stagedGcode > 0
                    ? MirrorFile(stagingDir, destDir, JobLogManifest.GcodeFile, stagedGcode, out movedGcode) : 0;
                progressed = movedPrint || movedEvents || movedDiag || movedGcode;
                bool caughtUp = newPrint == stagedPrint && newEvents == stagedEvents && newDiag == stagedDiagnostics
                             && newGcode == stagedGcode;
                bool finalized = false;
                if (complete && caughtUp)
                {
                    Finalize(destDir, JobLogManifest.PrintFile, stagedPrint);
                    Finalize(destDir, JobLogManifest.EventsFile, stagedEvents);
                    Finalize(destDir, JobLogManifest.DiagnosticsFile, stagedDiagnostics);
                    if (stagedGcode > 0) Finalize(destDir, JobLogManifest.GcodeFile, stagedGcode);
                    finalized = true;
                }
                lock (_lock)
                {
                    var m = job.Manifest;
                    m.DestinationDirectory = destDir;
                    m.PrintOffset = newPrint;
                    m.EventsOffset = newEvents;
                    m.DiagnosticsOffset = newDiag;
                    m.GcodeOffset = newGcode;
                    m.LastError = null;
                    // more may have been appended meanwhile: stay Syncing until caught up
                    bool nowCaughtUp = newPrint == job.StagedPrint && newEvents == job.StagedEvents
                                    && newDiag == job.StagedDiagnostics && newGcode == job.StagedGcode;
                    if (finalized && nowCaughtUp && m.Complete)
                    {
                        m.Finalized = true;
                        m.State = LogRetentionState.Saved;
                        try { _fs.WriteTextAtomic(_fs.Combine(destDir, JobLogManifest.FileName), m.ToJson()); } catch (Exception) { /* the copy is verified; the manifest copy is a courtesy */ }
                        try { _fs.DeleteDirectory(stagingDir); job.StagingLive = false; }
                        catch (Exception ex) { _notes.Add("staging cleanup failed for " + id + ": " + ex.Message); }
                        progressed = true;
                    }
                    else if (m.State == LogRetentionState.Syncing)
                    {
                        if (!m.Complete && nowCaughtUp) m.State = LogRetentionState.PermanentRecording;
                        if (m.Complete && nowCaughtUp && !finalized) m.State = LogRetentionState.Syncing;   // finalise next pass
                        if (m.State != LogRetentionState.Saved) TryPersist(job);
                    }
                    if (m.State == LogRetentionState.Saved) { /* staging gone; nothing to persist */ }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    var m = job.Manifest;
                    if (m.State == LogRetentionState.Syncing)
                    {
                        m.State = LogRetentionState.WaitingToSync;
                        m.LastError = ex.Message;
                        if (destDir != null) m.DestinationDirectory = destDir;
                        TryPersist(job);
                    }
                }
            }
            return progressed;
        }

        /// <summary>The destination directory this job owns. A directory that exists but
        /// carries another job's marker (or none) is somebody else's: a deterministic
        /// suffix is chosen rather than overwriting.</summary>
        string ClaimDestinationDirectory(string destination, string id)
        {
            if (!_fs.DirectoryExists(destination)) _fs.CreateDirectory(destination);
            for (int n = 1; n < 1000; n++)
            {
                string candidate = _fs.Combine(destination, n == 1 ? id : id + "-" + n.ToString(CultureInfo.InvariantCulture));
                string marker = _fs.Combine(candidate, OwnerMarker);
                if (_fs.DirectoryExists(candidate))
                {
                    if (_fs.FileExists(marker) && string.Equals(_fs.ReadText(marker).Trim(), id, StringComparison.Ordinal))
                        return candidate;
                    continue;
                }
                _fs.CreateDirectory(candidate);
                _fs.WriteTextAtomic(marker, id);
                return candidate;
            }
            throw new InvalidOperationException("no free destination directory for " + id);
        }

        /// <summary>Appends the staged bytes beyond the acknowledged offset to the
        /// destination `.partial` file. The destination length is the truth about what
        /// landed: an ambiguous earlier failure is resolved by reading it, never by
        /// re-sending. Bounded per call.</summary>
        long MirrorFile(string stagingDir, string destDir, string file, long staged, out bool moved)
        {
            moved = false;
            string source = _fs.Combine(stagingDir, file);
            string partial = _fs.Combine(destDir, file + JobLogManifest.Partial);
            string final = _fs.Combine(destDir, file);
            if (_fs.FileExists(final) && !_fs.FileExists(partial))
            {
                long finalLength = _fs.Length(final);
                if (finalLength == staged) return staged;   // already finalised earlier
                throw new InvalidOperationException(file + " at the destination is finalised with a different length (" + finalLength + " vs " + staged + " staged)");
            }
            if (!_fs.FileExists(partial)) _fs.Append(partial, Array.Empty<byte>());   // an empty file is still a file to verify
            long destLength = _fs.Length(partial);
            if (destLength > staged)
                throw new InvalidOperationException(file + JobLogManifest.Partial + " at the destination is longer than the staged buffer (" + destLength + " vs " + staged + ")");
            long offset = destLength;   // idempotent: what is there is what was acknowledged
            long budget = SyncChunkBytes;
            while (offset < staged && budget > 0)
            {
                int count = (int)Math.Min(Math.Min(staged - offset, budget), 64 * 1024);
                var bytes = _fs.Read(source, offset, count);
                if (bytes.Length == 0) break;
                _fs.Append(partial, bytes);
                long after = _fs.Length(partial);
                if (after != offset + bytes.Length)
                    throw new InvalidOperationException(file + JobLogManifest.Partial + " did not grow as expected (" + after + " vs " + (offset + bytes.Length) + ")");
                offset = after;
                budget -= bytes.Length;
                moved = true;
            }
            return offset;
        }

        void Finalize(string destDir, string file, long staged)
        {
            string partial = _fs.Combine(destDir, file + JobLogManifest.Partial);
            string final = _fs.Combine(destDir, file);
            if (_fs.FileExists(partial))
            {
                if (_fs.FileExists(final)) _fs.DeleteFile(final);
                _fs.Move(partial, final);
            }
            long length = _fs.FileExists(final) ? _fs.Length(final) : -1;
            if (length != staged)
                throw new InvalidOperationException(file + " verification failed at the destination (" + length + " vs " + staged + " staged)");
        }

        // ---- recovery and shutdown ---------------------------------------------------------

        /// <summary>Scans the staging root for buffers left by an unclean shutdown. Intact
        /// ones become recovered jobs (temporary unless they were permanent); a corrupt
        /// manifest moves its directory to quarantine and is reported, never deleted.</summary>
        public void Recover()
        {
            lock (_lock)
            {
                IReadOnlyList<string> dirs;
                try
                {
                    if (!_fs.DirectoryExists(_root)) _fs.CreateDirectory(_root);
                    dirs = _fs.Directories(_root);
                }
                catch (Exception ex)
                {
                    _fault = "local staging unavailable: " + ex.Message;
                    return;
                }
                string quarantine = _fs.Combine(_root, QuarantineDirectory);
                foreach (string dir in dirs)
                {
                    if (string.Equals(dir, quarantine, StringComparison.Ordinal)) continue;
                    string manifestPath = _fs.Combine(dir, JobLogManifest.FileName);
                    JobLogManifest manifest;
                    try
                    {
                        if (!_fs.FileExists(manifestPath)) throw new FormatException("no manifest");
                        manifest = JobLogManifest.Parse(_fs.ReadText(manifestPath));
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (!_fs.DirectoryExists(quarantine)) _fs.CreateDirectory(quarantine);
                            _fs.Move(dir, _fs.Combine(quarantine, Leaf(dir) + "-" + _clock().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)));
                            _notes.Add("quarantined " + Leaf(dir) + ": " + ex.Message);
                        }
                        catch (Exception moveEx) { _notes.Add("could not quarantine " + Leaf(dir) + ": " + moveEx.Message); }
                        continue;
                    }
                    if (manifest.State == LogRetentionState.Saved || manifest.State == LogRetentionState.Discarded)
                    {
                        // best effort: already Saved/Discarded, so this is leftover-staging
                        // cleanup, not a state transition - a directory we cannot remove
                        // right now is simply tried again on the next recovery pass.
                        try { _fs.DeleteDirectory(dir); }
                        catch (System.IO.IOException) { }
                        catch (UnauthorizedAccessException) { }
                        continue;
                    }
                    var job = new Job { Manifest = manifest, StagingDir = dir, Recovered = true, StagingLive = true };
                    try
                    {
                        string p = _fs.Combine(dir, JobLogManifest.PrintFile);
                        string e = _fs.Combine(dir, JobLogManifest.EventsFile);
                        string d = _fs.Combine(dir, JobLogManifest.DiagnosticsFile);
                        string g = _fs.Combine(dir, JobLogManifest.GcodeFile);
                        job.StagedPrint = _fs.FileExists(p) ? _fs.Length(p) : 0;
                        job.StagedEvents = _fs.FileExists(e) ? _fs.Length(e) : 0;
                        job.StagedDiagnostics = _fs.FileExists(d) ? _fs.Length(d) : 0;
                        job.StagedGcode = _fs.FileExists(g) ? _fs.Length(g) : 0;
                        // A recovered archive that no longer matches its manifest is not an
                        // archive: a reprint must never run bytes nothing vouches for.
                        if (job.StagedGcode != manifest.GcodeBytes)
                        {
                            if (job.StagedGcode > 0)
                                _notes.Add("recovered " + manifest.Id + " with an incomplete G-code archive - not reprintable");
                            job.StagedGcode = 0;
                            manifest.GcodeBytes = 0;
                            manifest.GcodeSha256 = "";
                        }
                    }
                    catch (Exception ex) { _notes.Add("recovered " + manifest.Id + " without sizes: " + ex.Message); }
                    // whatever it was doing, it is not doing it any more
                    manifest.Complete = true;
                    manifest.CompletedUtc ??= _clock();
                    if (manifest.Permanent)
                        manifest.State = manifest.Destination == null ? LogRetentionState.WaitingToSync : LogRetentionState.Syncing;
                    else
                        manifest.State = LogRetentionState.RecoveredTemporary;
                    TryPersist(job);
                    _jobs.Add(job);
                    _notes.Add("recovered " + manifest.Id + (manifest.Permanent ? " (will resume saving)" : " (temporary - save it to keep it)"));
                }
            }
        }

        /// <summary>Clean shutdown: completed, unsaved temporary buffers are removed; an
        /// active job and anything permanent are left for recovery or completion.</summary>
        public void Shutdown()
        {
            lock (_lock)
            {
                foreach (var job in _jobs.ToArray())
                {
                    var m = job.Manifest;
                    if (ReferenceEquals(job, _current)) continue;
                    if (m.Permanent) continue;
                    if (m.State == LogRetentionState.TemporaryComplete || m.State == LogRetentionState.RecoveredTemporary)
                    {
                        try { _fs.DeleteDirectory(job.StagingDir); job.StagingLive = false; m.State = LogRetentionState.Discarded; _jobs.Remove(job); }
                        catch (Exception ex) { _notes.Add("cleanup failed for " + m.Id + ": " + ex.Message); }
                    }
                }
            }
        }

        /// <summary>Copies the CSV of a job to <paramref name="targetPath"/>: the staged one,
        /// or for a Saved job the verified copy at the destination, because finalisation
        /// deleted the staging buffer. The source is checked before the target is touched,
        /// so a job with nothing left to copy never costs the operator the file they
        /// picked.</summary>
        public void ExportCopy(string id, string targetPath)
        {
            string source; long length;
            lock (_lock)
            {
                var job = Find(id) ?? throw new InvalidOperationException("no job " + id);
                var m = job.Manifest;
                source = m.State == LogRetentionState.Saved && m.DestinationDirectory != null
                    ? _fs.Combine(m.DestinationDirectory, JobLogManifest.PrintFile)
                    : _fs.Combine(job.StagingDir, JobLogManifest.PrintFile);
                length = job.StagedPrint;   // finalisation verified the saved copy against it
            }
            if (!_fs.FileExists(source))
                throw new InvalidOperationException("the record of job " + id + " is no longer at " + source);
            if (_fs.FileExists(targetPath)) _fs.DeleteFile(targetPath);
            long offset = 0;
            _fs.Append(targetPath, Array.Empty<byte>());
            while (offset < length)
            {
                var bytes = _fs.Read(source, offset, (int)Math.Min(64 * 1024, length - offset));
                if (bytes.Length == 0) break;
                _fs.Append(targetPath, bytes);
                offset += bytes.Length;
            }
        }

        // ---- recent prints and the immutable archive -----------------------------------------

        /// <summary>Reads the destination folder for finished runs, so Recent prints
        /// survives a Rhino restart: a saved run's staging buffer is gone, its manifest and
        /// <c>job.gcode</c> are not. Destination I/O, so it runs OUTSIDE the lock and is
        /// called from the retention worker, never from a poll or a paint.</summary>
        public void IndexDestination()
        {
            string? destination;
            lock (_lock) destination = _destination;
            if (destination == null) { lock (_lock) _history.Clear(); return; }
            IReadOnlyList<string> dirs;
            try
            {
                if (!_fs.DirectoryExists(destination)) { lock (_lock) _history.Clear(); return; }
                dirs = _fs.Directories(destination);
            }
            catch (Exception ex)
            {
                lock (_lock) _notes.Add("recent prints: the destination could not be listed - " + ex.Message);
                return;
            }
            var found = new List<ArchivedRun>();
            foreach (string dir in dirs)
            {
                try
                {
                    string path = _fs.Combine(dir, JobLogManifest.FileName);
                    if (!_fs.FileExists(path)) continue;
                    var manifest = JobLogManifest.Parse(_fs.ReadText(path));
                    found.Add(new ArchivedRun { Manifest = manifest, Directory = dir });
                }
                catch (Exception) { /* a directory we cannot read is not a recent print */ }
            }
            lock (_lock) { _history.Clear(); _history.AddRange(found); }
        }

        /// <summary>The exact bytes this run printed, or null when there is no usable
        /// archive. Verification against the recorded hash is
        /// <see cref="ReprintPolicy.VerifyBytes"/>'s job, deliberately not this one's: the
        /// reader reports what is on disk and the policy decides whether it may run.</summary>
        public byte[]? ReadArchivedGcode(string id)
        {
            // Both places the bytes can be: the staging buffer while the run is still local,
            // and the destination directory once it was saved (staging is then gone). The
            // candidates are collected under the lock and READ outside it.
            var candidates = new List<(string Path, long Length)>();
            lock (_lock)
            {
                var job = Find(id);
                if (job != null)
                {
                    if (job.StagedGcode > 0)
                        candidates.Add((_fs.Combine(job.StagingDir, JobLogManifest.GcodeFile), job.StagedGcode));
                    if (job.Manifest.DestinationDirectory != null && job.Manifest.GcodeBytes > 0)
                        candidates.Add((_fs.Combine(job.Manifest.DestinationDirectory, JobLogManifest.GcodeFile), job.Manifest.GcodeBytes));
                }
                foreach (var run in _history)
                    if (string.Equals(run.Manifest.Id, id, StringComparison.Ordinal) && run.Manifest.GcodeBytes > 0)
                    { candidates.Add((_fs.Combine(run.Directory, JobLogManifest.GcodeFile), run.Manifest.GcodeBytes)); break; }
            }
            foreach (var (path, length) in candidates)
            {
                if (length <= 0 || length > int.MaxValue) continue;
                try
                {
                    if (!_fs.FileExists(path)) continue;
                    if (_fs.Length(path) != length) continue;   // changed since the run: not this job's bytes
                    var buffer = new byte[(int)length];
                    long offset = 0;
                    bool complete = true;
                    while (offset < length)
                    {
                        var chunk = _fs.Read(path, offset, (int)Math.Min(64 * 1024, length - offset));
                        if (chunk.Length == 0) { complete = false; break; }
                        Array.Copy(chunk, 0, buffer, (int)offset, chunk.Length);
                        offset += chunk.Length;
                    }
                    if (complete) return buffer;
                }
                catch (Exception ex)
                {
                    lock (_lock) _notes.Add("archive read failed for " + id + ": " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>The last runs, newest first. Runs of this session and runs found at the
        /// destination, capped. A run without an archive is listed and explains why it
        /// cannot be printed again; it is never hidden.</summary>
        public IReadOnlyList<RecentPrintEntry> RecentRuns(int cap = RecentPrints.DefaultCap)
        {
            // Snapshot under the lock, then touch the disk without it: a stalled share must
            // never block a record append or a status read.
            var pending = new List<(JobLogManifest Manifest, string Staging, string? Destination)>();
            lock (_lock)
            {
                foreach (var job in _jobs)
                    pending.Add((job.Manifest, job.StagedGcode > 0 ? job.StagingDir : "", job.Manifest.DestinationDirectory));
                foreach (var run in _history)
                {
                    bool alreadyOpen = false;
                    foreach (var job in _jobs)
                        if (string.Equals(job.Manifest.Id, run.Manifest.Id, StringComparison.Ordinal)) { alreadyOpen = true; break; }
                    if (!alreadyOpen) pending.Add((run.Manifest, "", run.Directory));
                }
            }
            var entries = new List<RecentPrintEntry>(pending.Count);
            foreach (var (manifest, staging, destination) in pending)
            {
                bool recorded = manifest.GcodeBytes > 0 && manifest.GcodeSha256.Length > 0;
                // The archive lives in the staging buffer until the run is saved and in the
                // destination directory afterwards; a saved run's staging directory is gone.
                string directory = "";
                if (recorded)
                {
                    if (Exists(staging, JobLogManifest.GcodeFile)) directory = staging;
                    else if (Exists(destination ?? "", JobLogManifest.GcodeFile)) directory = destination!;
                }
                bool present = directory.Length > 0;
                string? note = !recorded
                    ? "no G-code was archived for this run - it is listed but not available for reprint"
                    : !present
                        ? "the archived G-code of this run is no longer on disk - not available for reprint"
                        : null;
                entries.Add(new RecentPrintEntry(manifest.Id, manifest.JobName, manifest.StartedUtc, manifest.Outcome,
                                                 new ReprintContext(manifest.Host, manifest.Fingerprint, manifest.ProfileId,
                                                                    manifest.Mechanism, manifest.Transport),
                                                 manifest.DryRun, manifest.GcodeSha256, manifest.GcodeBytes,
                                                 present, note, manifest.State,
                                                 present ? directory : destination ?? staging, manifest.RunId));
            }
            return RecentPrints.Order(entries, cap);
        }

        bool Exists(string directory, string file)
        {
            // An unreachable directory is treated the same as a missing file: the
            // caller's only use of this is deciding whether a G-code archive is
            // available for reprint, and "cannot tell" must mean "not available".
            try { return directory.Length > 0 && _fs.FileExists(_fs.Combine(directory, file)); }
            catch (Exception) { return false; }
        }

        // ---- status ------------------------------------------------------------------------

        public LogRetentionStatus Snapshot()
        {
            lock (_lock)
            {
                var entries = new List<JobLogEntry>(_jobs.Count);
                JobLogEntry? current = null;
                string? attention = _fault;
                foreach (var job in _jobs)
                {
                    var m = job.Manifest;
                    var entry = new JobLogEntry(m.Id, m.JobName, m.StartedUtc, m.State, m.Permanent, m.Complete, job.Recovered,
                                                job.StagedPrint + job.StagedEvents + job.StagedDiagnostics,
                                                m.PrintOffset + m.EventsOffset + m.DiagnosticsOffset,
                                                m.DestinationDirectory, job.StagingDir, m.LastError, m.Outcome);
                    entries.Add(entry);
                    if (ReferenceEquals(job, _current)) current = entry;
                    if (attention == null && m.State == LogRetentionState.WaitingToSync)
                        attention = "waiting to sync " + m.Id + (m.LastError != null ? ": " + m.LastError : "");
                    if (attention == null && m.State == LogRetentionState.Faulted)
                        attention = "logging fault " + m.Id + (m.LastError != null ? ": " + m.LastError : "");
                }
                string badge = Badge(current, entries);
                return new LogRetentionStatus(_keepAll, _destination, current, entries, attention, badge);
            }
        }

        static string Badge(JobLogEntry? current, List<JobLogEntry> all)
        {
            var reference = current;
            if (reference == null && all.Count > 0) reference = all[all.Count - 1];
            if (reference == null) return "Temporary";
            switch (reference.State)
            {
                case LogRetentionState.PermanentRecording: return "Recording";
                case LogRetentionState.Syncing: return "Syncing";
                case LogRetentionState.WaitingToSync: return "Waiting to sync";
                case LogRetentionState.Saved: return "Saved";
                case LogRetentionState.Faulted: return "Logging fault";
                default: return "Temporary";
            }
        }

        // ---- helpers -----------------------------------------------------------------------

        Job? Find(string id)
        {
            foreach (var job in _jobs) if (string.Equals(job.Manifest.Id, id, StringComparison.Ordinal)) return job;
            return null;
        }

        void PersistLocked(Job job)
            => _fs.WriteTextAtomic(_fs.Combine(job.StagingDir, JobLogManifest.FileName), job.Manifest.ToJson());

        void TryPersist(Job job)
        {
            try { PersistLocked(job); }
            catch (Exception ex) { _notes.Add("manifest write failed for " + job.Manifest.Id + ": " + ex.Message); }
        }

        static string Leaf(string path)
        {
            int i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return i >= 0 ? path.Substring(i + 1) : path;
        }
    }
}
