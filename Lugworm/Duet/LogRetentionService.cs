using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Lugworm.Core.Duet;

namespace Lugworm.Duet
{
    /// <summary>The production <see cref="IRetentionFileSystem"/>: plain disk I/O with an
    /// atomic text write (temp file + replace). Any exception propagates; the coordinator
    /// turns it into a state.</summary>
    public sealed class DiskRetentionFileSystem : IRetentionFileSystem
    {
        public string Combine(string a, string b) => Path.Combine(a, b);
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public IReadOnlyList<string> Directories(string root) => Directory.GetDirectories(root);
        public long Length(string path) => new FileInfo(path).Length;

        public void Append(string path, byte[] data)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                if (data.Length > 0) stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }
        }

        public byte[] Read(string path, long offset, int count)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (offset >= stream.Length) return Array.Empty<byte>();
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == buffer.Length) return buffer;
                var trimmed = new byte[read];
                Array.Copy(buffer, trimmed, read);
                return trimmed;
            }
        }

        public string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

        public void WriteTextAtomic(string path, string text)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            // best effort: removing the scratch file after a failed replace. The real
            // fault is already on its way out of this finally; a leftover .tmp- file is
            // the lesser problem and must not mask it.
            finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch (Exception) { } } }
        }

        public void Move(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, true);
    }

    /// <summary>Process-wide job-log retention: one staging root under %LOCALAPPDATA%,
    /// one coordinator, one worker thread that performs every staging append and every
    /// destination synchronisation, and the console settings file. Sessions only
    /// enqueue; the poll loop never touches a disk here (the same rule as
    /// <see cref="PrintLogSink"/>). Nothing in this class calls WinForms or Skia.</summary>
    public sealed class LogRetentionService : IDisposable
    {
        public const string ProductFolder = "Lugworm";
        /// <summary>The %LOCALAPPDATA% folder before the 5.0.0 rename, moved once on first use.</summary>
        public const string LegacyProductFolder = "IAMLabSlicer";
        public const string BufferFolder = "PrintLogBuffer";
        public const string SettingsFile = "console-settings.json";
        const int QueueCapacity = 20000;
        /// <summary>Diagnostic lines get their own, smaller share of the queue. A capture
        /// arrives as a burst of a few hundred lines; without a separate budget a stalled
        /// destination would let one burst crowd out the CSV rows, and a capture matters
        /// less than the row stream it is explaining.</summary>
        const int DiagnosticQueueCapacity = 5000;
        const int SyncIntervalMs = 1000;
        /// <summary>Diagnostic lines kept in memory for the session, so a capture stays
        /// readable after the job log closed - the 2026-09-09 halt ended its log at M112
        /// and took the evidence about the transport with it.</summary>
        const int SessionDiagnosticCap = 4000;

        static readonly object InstanceLock = new object();
        static LogRetentionService? _instance;

        /// <summary>The shared service, created on first use (recovery runs then).</summary>
        public static LogRetentionService Instance
        {
            get
            {
                lock (InstanceLock)
                {
                    if (_instance == null)
                    {
                        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        string root = Path.Combine(local, ProductFolder);
                        // 5.0.0 rename: a machine that logged under the old product folder keeps
                        // its buffers and archives - the folder is moved once, and when the move
                        // is refused (a file open elsewhere, no permission) the old folder stays
                        // in use rather than starting a second, empty history
                        string legacy = Path.Combine(local, LegacyProductFolder);
                        if (!Directory.Exists(root) && Directory.Exists(legacy))
                        {
                            try { Directory.Move(legacy, root); }
                            catch (IOException) { root = legacy; }
                            catch (UnauthorizedAccessException) { root = legacy; }
                        }
                        _instance = new LogRetentionService(new DiskRetentionFileSystem(), root);
                    }
                    return _instance;
                }
            }
        }

        enum Op { Begin, Name, Record, Event, Complete, Diagnostic, Outcome, Archive, ReadArchive, Index }
        readonly struct DropTotals
        {
            public readonly long Rows, Diagnostics, SessionDiagnostics;
            public DropTotals(long rows, long diagnostics, long sessionDiagnostics)
            { Rows = rows; Diagnostics = diagnostics; SessionDiagnostics = sessionDiagnostics; }
        }
        readonly struct Command
        {
            public readonly Op Kind; public readonly string Text; public readonly object? Payload;
            public readonly DropTotals Drops;
            public Command(Op kind, string text, object? payload = null, DropTotals drops = default)
            { Kind = kind; Text = text; Payload = payload; Drops = drops; }
        }

        /// <summary>One run's G-code and the machine it is being run on, queued for the
        /// worker. The bytes are the caller's copy; nothing here mutates them.</summary>
        sealed class ArchiveRequest
        {
            public byte[]? Gcode;
            public string RunId = "";
            public ReprintContext? Machine;
            public bool DryRun;
        }

        sealed class ArchiveRead
        {
            public string Id = "";
            public Action<byte[]?>? Completed;
        }

        readonly object _sync = new object();
        readonly Queue<Command> _queue = new Queue<Command>();
        readonly IRetentionFileSystem _fs;
        readonly string _root;
        readonly string _settingsPath;
        readonly LogRetentionCoordinator _coordinator;
        readonly Thread _thread;
        ConsoleSettings _settings = new ConsoleSettings();
        string? _settingsWarning;
        volatile bool _stopping;
        long _dropped;
        long _diagnosticsDropped;
        int _queuedDiagnostics;
        readonly Queue<string> _sessionDiagnostics = new Queue<string>();
        long _sessionDiagnosticsDropped;
        LogRetentionStatus _status = LogRetentionStatus.None;
        /// <summary>The Recent prints projection, rebuilt on the worker thread only.</summary>
        IReadOnlyList<RecentPrintEntry> _recent = Array.Empty<RecentPrintEntry>();

        /// <summary>Raised on the worker thread after the status changed; subscribers
        /// marshal to their own thread.</summary>
        public event Action<LogRetentionStatus>? StatusChanged;

        public LogRetentionService(IRetentionFileSystem fs, string root)
        {
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _settingsPath = _fs.Combine(root, SettingsFile);
            LoadSettings();
            _coordinator = new LogRetentionCoordinator(_fs, _fs.Combine(root, BufferFolder), () => DateTime.UtcNow,
                () => Guid.NewGuid().ToString("N").Substring(0, 6));
            _coordinator.SetDestination(_settings.Destination);
            _coordinator.SetKeepAll(_settings.KeepAllJobLogs);
            try { _coordinator.Recover(); } catch (Exception ex) { _settingsWarning ??= "recovery failed: " + ex.Message; }
            _status = _coordinator.Snapshot();
            _thread = new Thread(Pump) { IsBackground = true, Name = "Lugworm job-log retention" };
            _thread.Start();
            // Recent prints survives a restart because it is on disk: read the destination
            // once at startup, on the worker.
            RefreshRecentPrints();
        }

        public LogRetentionStatus Status { get { lock (_sync) return _status; } }
        public ConsoleSettings Settings { get { lock (_sync) return _settings.Copy(); } }
        public long RowsDropped => Interlocked.Read(ref _dropped);
        /// <summary>Diagnostic lines dropped because the worker was behind. Counted apart
        /// from <see cref="RowsDropped"/>: a capture with a hole in it must say so rather
        /// than read as a quiet stretch of polling.</summary>
        public long DiagnosticsDropped => Interlocked.Read(ref _diagnosticsDropped);
        /// <summary>Diagnostic lines that aged out of the in-memory session window.</summary>
        public long SessionDiagnosticsDropped { get { lock (_sync) return _sessionDiagnosticsDropped; } }

        /// <summary>What the recorder is doing right now, or null when no run is recording
        /// (finding D1, checklist row 10a.12). Readable from any thread and from the UI: it
        /// is one lock over in-memory state, with no file system call, so it can be asked
        /// WHILE a print records - which is the only time the answer exists, because a
        /// successful final copy deletes the staging folder it describes.</summary>
        public LoggingHealthSnapshot? LoggingHealth() => _coordinator.StagingHealth();

        // ---- producers (any thread; never block) --------------------------------------------

        public void BeginJob(string jobName) => Enqueue(new Command(Op.Begin, jobName ?? ""));
        public void NameJob(string jobName) => Enqueue(new Command(Op.Name, jobName ?? ""));
        public void AppendRecord(string csvRow) => Enqueue(new Command(Op.Record, csvRow ?? ""));
        public void AppendEvent(string line) => Enqueue(new Command(Op.Event, line ?? ""));
        public void CompleteJob() => Enqueue(new Command(Op.Complete, ""));
        /// <summary>How the RUN ended, recorded in the manifest apart from log completeness.</summary>
        public void SetJobOutcome(string outcome) => Enqueue(new Command(Op.Outcome, outcome ?? ""));

        /// <summary>Archives the exact bytes of the run that just started, beside its log,
        /// together with the machine and profile it runs on. Enqueued like every other
        /// record: the poll loop that noticed the job start never touches the disk.
        /// Null or empty bytes record the context only - an externally started run is
        /// listed with its bytes unknown, never with somebody else's G-code.</summary>
        public void ArchiveJobGcode(byte[]? gcode, string? runId, ReprintContext? machine, bool dryRun)
            => Enqueue(new Command(Op.Archive, "", new ArchiveRequest
            { Gcode = gcode, RunId = runId ?? "", Machine = machine, DryRun = dryRun }));

        /// <summary>Reads one run's archived G-code on the worker thread and hands it back
        /// through <paramref name="completed"/> (called on the worker; the caller marshals).
        /// Null means there is nothing usable to print again.</summary>
        public void ReadArchivedGcode(string id, Action<byte[]?> completed)
        {
            if (string.IsNullOrEmpty(id)) { completed?.Invoke(null); return; }
            Enqueue(new Command(Op.ReadArchive, id, new ArchiveRead { Id = id, Completed = completed }));
        }

        /// <summary>Re-reads the destination folder so Recent prints picks up runs from
        /// earlier sessions. Queued, because it lists a folder that may be a network share.</summary>
        public void RefreshRecentPrints() => Enqueue(new Command(Op.Index, ""));

        /// <summary>One raw-telemetry diagnostic line. Kept in a session-level window as
        /// well as in the job's diagnostics file, so evidence about a transport failure
        /// survives the job log that closed with it.</summary>
        public void AppendDiagnostic(string line)
        {
            string text = line ?? "";
            lock (_sync)
            {
                _sessionDiagnostics.Enqueue(text);
                while (_sessionDiagnostics.Count > SessionDiagnosticCap)
                { _sessionDiagnostics.Dequeue(); _sessionDiagnosticsDropped++; }
                Enqueue(new Command(Op.Diagnostic, text));
            }
        }

        /// <summary>The session's diagnostic window, oldest first. Available after the job
        /// log closed; bounded by <see cref="SessionDiagnosticCap"/>.</summary>
        public IReadOnlyList<string> SessionDiagnostics()
        {
            lock (_sync) return new List<string>(_sessionDiagnostics);
        }

        void Enqueue(Command command)
        {
            lock (_sync)
            {
                if (_stopping) return;
                if (command.Kind == Op.Diagnostic)
                {
                    // A diagnostic burst must never push back on the poll loop, and must
                    // never starve the row stream it exists to explain.
                    if (_queuedDiagnostics >= DiagnosticQueueCapacity || _queue.Count >= QueueCapacity)
                    {
                        Interlocked.Increment(ref _diagnosticsDropped);
                        return;
                    }
                    _queuedDiagnostics++;
                }
                else if (_queue.Count >= QueueCapacity && command.Kind == Op.Record)
                {
                    Interlocked.Increment(ref _dropped);   // never push back on the poll loop
                    return;
                }
                if (command.Kind == Op.Begin || command.Kind == Op.Complete)
                    command = new Command(command.Kind, command.Text, command.Payload, SnapshotDropsLocked());
                _queue.Enqueue(command);
                Monitor.Pulse(_sync);
            }
        }

        // ---- operator actions (UI thread; quick, then the worker syncs) ----------------------

        public bool SetKeepAll(bool keepAll)
        {
            bool applied = _coordinator.SetKeepAll(keepAll);
            lock (_sync) { _settings.KeepAllJobLogs = applied; }
            SaveSettings();
            Publish();
            return applied;
        }

        public void SetDestination(string? folder)
        {
            _coordinator.SetDestination(folder);
            lock (_sync)
            {
                _settings.Destination = string.IsNullOrWhiteSpace(folder) ? null : folder;
                if (_settings.Destination == null) _settings.KeepAllJobLogs = false;
            }
            SaveSettings();
            Publish();
            // A different folder holds different past runs.
            RefreshRecentPrints();
        }

        public bool SaveJob(string id) { bool ok = _coordinator.SaveJob(id); Publish(); return ok; }
        public void Retry() { _coordinator.Retry(); Publish(); }
        /// <summary>Removing a buffer must not silently invalidate a Recent prints
        /// selection: the list is rebuilt, and the removed run's archive then reports
        /// itself as gone rather than disappearing behind a still-armed reprint.</summary>
        public bool Discard(string id) { bool ok = _coordinator.Discard(id); Publish(); RefreshRecentPrints(); return ok; }
        public void ExportCopy(string id, string targetPath) { _coordinator.ExportCopy(id, targetPath); }

        public void SetReducedMotion(bool reduced)
        {
            lock (_sync) _settings.ReducedMotion = reduced;
            SaveSettings();
        }

        public void SetWindowBounds(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            lock (_sync) _settings.WindowBounds = new[] { x, y, width, height };
            SaveSettings();
        }

        /// <summary>Clean shutdown: completed, unsaved temporary buffers go; the worker
        /// finishes what is queued. Called from the host's closing event.</summary>
        public void Shutdown()
        {
            lock (_sync) { _stopping = true; Monitor.PulseAll(_sync); }
            if (!_thread.Join(3000)) return;
            // best effort: Rhino is closing, so there is no surface left to report on and
            // no later run to repair anything - the coordinator's own manifest on disk is
            // what the next session recovers from.
            try { _coordinator.CompleteJob(); _coordinator.Shutdown(); } catch (Exception) { }
        }

        public void Dispose() => Shutdown();

        // ---- worker ---------------------------------------------------------------------------

        void Pump()
        {
            var pending = new List<Command>();
            while (true)
            {
                pending.Clear();
                DropTotals batchDrops;
                lock (_sync)
                {
                    if (_queue.Count == 0 && !_stopping) Monitor.Wait(_sync, SyncIntervalMs);
                    while (_queue.Count > 0)
                    {
                        var next = _queue.Dequeue();
                        if (next.Kind == Op.Diagnostic && _queuedDiagnostics > 0) _queuedDiagnostics--;
                        pending.Add(next);
                    }
                    if (_stopping && pending.Count == 0) return;
                    batchDrops = SnapshotDropsLocked();
                }
                bool changed = false;
                foreach (var command in pending)
                {
                    // Use producer-time boundaries. Reading live totals here would charge
                    // a delayed run for losses from later commands, or swallow its losses
                    // into its opening baseline before Begin has been processed.
                    if (command.Kind == Op.Begin || command.Kind == Op.Complete) ObserveDrops(command.Drops);
                    try
                    {
                        switch (command.Kind)
                        {
                            case Op.Begin: _coordinator.BeginJob(command.Text); changed = true; break;
                            case Op.Name: _coordinator.NameCurrentJob(command.Text); break;
                            case Op.Record: _coordinator.AppendRecord(command.Text); break;
                            case Op.Event: _coordinator.AppendEvent(command.Text); break;
                            case Op.Diagnostic: _coordinator.AppendDiagnostic(command.Text); break;
                            case Op.Outcome: _coordinator.SetJobOutcome(command.Text); changed = true; break;
                            case Op.Complete: _coordinator.CompleteJob(); changed = true; break;
                            case Op.Archive:
                                if (command.Payload is ArchiveRequest request)
                                    _coordinator.ArchiveCurrentJob(request.Gcode, request.RunId, request.Machine, request.DryRun);
                                changed = true;
                                break;
                            case Op.ReadArchive:
                                if (command.Payload is ArchiveRead read)
                                {
                                    byte[]? bytes = null;
                                    try { bytes = _coordinator.ReadArchivedGcode(read.Id); }
                                    // best effort: the waiter is always answered, with null
                                    // when the read failed. A consumer that throws on its own
                                    // callback must not kill the retention worker thread.
                                    finally { try { read.Completed?.Invoke(bytes); } catch (Exception) { } }
                                }
                                break;
                            case Op.Index: _coordinator.IndexDestination(); changed = true; break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The coordinator reports its own faults through states; this is
                        // reached only when one escaped that. The worker goes on, and the
                        // first such fault rides on the status attention line, where the
                        // console shows it, instead of vanishing (once: it can repeat per
                        // command).
                        lock (_sync)
                            _settingsWarning ??= "job log worker: " + command.Kind.ToString().ToLowerInvariant()
                                               + " failed: " + ex.Message;
                    }
                }
                // A run that is still going gets its manifest kept current, so an
                // interrupted session still leaves the losses it knew about on disk.
                ObserveDrops(batchDrops);
                // best effort: the coordinator records a failed sync as a note on the
                // status snapshot read a few lines below, which is what the console shows.
                try { if (_coordinator.SyncOnce()) changed = true; } catch (Exception) { }
                // The recent-prints projection touches the disk (does this run's archive
                // still exist?), so it is rebuilt HERE, on the worker, and only when
                // something moved - never on the UI thread inside Publish().
                if (changed)
                {
                    try
                    {
                        var recent = _coordinator.RecentRuns();
                        lock (_sync) _recent = recent;
                    }
                    // best effort: Recent prints touches the destination folder, which can
                    // vanish mid-session. The previous projection stays on screen and the
                    // next pass over a reachable folder replaces it.
                    catch (Exception) { }
                }
                var status = WithSettingsWarning(_coordinator.Snapshot());
                bool differs;
                lock (_sync) { differs = !SameStatus(_status, status); _status = status; }
                if (changed || differs)
                {
                    var handler = StatusChanged;
                    // best effort: one console window that throws while rendering a status
                    // must not stop the retention worker or the other windows.
                    if (handler != null)
                        foreach (Action<LogRetentionStatus> one in handler.GetInvocationList())
                        {
                            try { one(status); } catch (Exception) { }
                        }
                }
            }
        }

        /// <summary>Hands the coordinator this process's drop totals. The properties above
        /// stay lifetime totals - other callers read them that way - and the coordinator
        /// keeps each run's baseline, so what lands in a manifest is the run's own share.
        /// Worker thread only.</summary>
        DropTotals SnapshotDropsLocked()
            => new DropTotals(_dropped, _diagnosticsDropped, _sessionDiagnosticsDropped);

        void ObserveDrops(DropTotals totals)
        {
            try { _coordinator.ObserveDropTotals(totals.Rows, totals.Diagnostics, totals.SessionDiagnostics); }
            catch (Exception) { /* a manifest write failure is recorded as a coordinator note */ }
        }

        void Publish()
        {
            var status = WithSettingsWarning(_coordinator.Snapshot());
            lock (_sync) _status = status;
            var handler = StatusChanged;
            // best effort: as in Pump - a subscriber's render fault stays with it.
            if (handler != null) { try { handler(status); } catch (Exception) { } }
        }

        /// <summary>A settings-file problem (unreadable, unsaved, recovery failed) rides on the
        /// status's attention line when the coordinator has nothing more urgent; before this
        /// the warning was recorded and never shown anywhere.</summary>
        LogRetentionStatus WithSettingsWarning(LogRetentionStatus status)
        {
            string? warning;
            IReadOnlyList<RecentPrintEntry> recent;
            lock (_sync) { warning = _settingsWarning; recent = _recent; }
            if (warning != null && status.Attention != null) warning = null;
            return new LogRetentionStatus(status.KeepAll, status.Destination, status.Current, status.Jobs,
                                          warning ?? status.Attention, status.Badge, recent);
        }

        static bool SameStatus(LogRetentionStatus a, LogRetentionStatus b)
        {
            if (a.KeepAll != b.KeepAll || !string.Equals(a.Destination, b.Destination, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Badge, b.Badge, StringComparison.Ordinal) || !string.Equals(a.Attention, b.Attention, StringComparison.Ordinal)) return false;
            if (a.Recent.Count != b.Recent.Count) return false;
            for (int i = 0; i < a.Recent.Count; i++)
            {
                var x = a.Recent[i]; var y = b.Recent[i];
                if (!string.Equals(x.Id, y.Id, StringComparison.Ordinal) || x.ArchiveAvailable != y.ArchiveAvailable
                    || !string.Equals(x.Outcome, y.Outcome, StringComparison.Ordinal)
                    || !string.Equals(x.JobName, y.JobName, StringComparison.Ordinal)) return false;
            }
            if (a.Jobs.Count != b.Jobs.Count) return false;
            for (int i = 0; i < a.Jobs.Count; i++)
            {
                var x = a.Jobs[i]; var y = b.Jobs[i];
                if (x.Id != y.Id || x.State != y.State || x.StagedBytes != y.StagedBytes || x.SyncedBytes != y.SyncedBytes
                    || !string.Equals(x.LastError, y.LastError, StringComparison.Ordinal) || x.JobName != y.JobName) return false;
            }
            return true;
        }

        // ---- settings ----------------------------------------------------------------------------

        void LoadSettings()
        {
            try
            {
                if (!_fs.DirectoryExists(_root)) _fs.CreateDirectory(_root);
                if (!_fs.FileExists(_settingsPath)) { _settings = new ConsoleSettings(); return; }
                string text = _fs.ReadText(_settingsPath);
                if (!ConsoleSettings.TryParse(text, out var parsed, out string? error))
                {
                    _settings = new ConsoleSettings();
                    _settingsWarning = "console settings could not be read (" + error + ") - permanent logging is off until you choose a folder again";
                    return;
                }
                _settings = parsed;
            }
            catch (Exception ex)
            {
                _settings = new ConsoleSettings();
                _settingsWarning = "console settings could not be read (" + ex.Message + ") - permanent logging is off until you choose a folder again";
            }
        }

        void SaveSettings()
        {
            ConsoleSettings copy;
            lock (_sync) copy = _settings.Copy();
            try
            {
                if (!_fs.DirectoryExists(_root)) _fs.CreateDirectory(_root);
                _fs.WriteTextAtomic(_settingsPath, copy.ToJson());
            }
            catch (Exception ex)
            {
                lock (_sync) _settingsWarning ??= "console settings could not be saved: " + ex.Message;
            }
        }

        /// <summary>Human-readable summary for diagnostics. The drop counters here are
        /// PROCESS-LIFETIME totals; the per-run share of each finished run is in its own
        /// manifest. While a run records, the staging line below states where its streams
        /// are being written and how much has landed - the check that has to be made during
        /// the run, because finalisation removes the folder it names.</summary>
        public string Describe()
        {
            var s = Status;
            var inv = CultureInfo.InvariantCulture;
            var health = LoggingHealth();
            return "job logs: " + s.Badge + (s.KeepAll ? " (keep all)" : " (temporary)")
                 + (s.Destination != null ? " -> " + s.Destination : "")
                 + ", " + s.Jobs.Count.ToString(inv) + " job(s) this session"
                 + (RowsDropped > 0 ? ", " + RowsDropped.ToString(inv) + " row(s) dropped" : "")
                 + (DiagnosticsDropped > 0 ? ", " + DiagnosticsDropped.ToString(inv) + " diagnostic line(s) dropped" : "")
                 + (health != null ? "\n" + health.Describe() : "\nnot recording - no staging snapshot to take");
        }
    }
}
