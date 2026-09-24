using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>Poll-thread print log state machine. It consumes snapshots, never
    /// performs I/O, and returns only the records emitted by the current call.</summary>
    public sealed class PrintLogRecorder
    {
        // A reconnect gap must not turn into a misleadingly slow movement. The
        // session's durable-loss threshold is deliberately much longer than this.
        const double MaxDerivedSpeedGapSeconds = 10.0;

        readonly object _sync = new object();
        readonly List<PrintLogRecord> _records = new List<PrintLogRecord>();
        readonly List<PrintLogLayerSpan> _completedSpans = new List<PrintLogLayerSpan>();
        readonly TrailContinuityPolicy _continuity;
        PrintLogOptions _options = new PrintLogOptions();
        SpanState? _openSpan;
        ObjectModelSnapshot? _lastSnapshot;
        /// <summary>Verdict of the most recently OBSERVED poll.</summary>
        TrailVerdict _lastVerdict;
        /// <summary>Verdict stamped on the rows being emitted right now. It differs from
        /// <see cref="_lastVerdict"/> while a run is being closed: those rows are dated by
        /// the previous poll and belong to the previous run and segment.</summary>
        TrailVerdict _emitVerdict;
        string _runId = "";
        string _currentRunKey = "";
        string? _jobFile;
        DateTime _runStartUtc;
        int? _currentLayer;
        PrintLogLayerSource _currentLayerSource;
        int _fallbackLayer;
        int _pollsSinceBoundary;
        double? _lastSpeed;
        double? _lastDeltaSeconds;
        double? _lastExtrusion;
        bool _runOpen;
        int _jobRun;
        long _nextSequence = 1;
        long _version;
        bool _truncated;

        sealed class SpanState
        {
            public readonly int Layer;
            public readonly DateTime StartUtc;
            public DateTime? EndUtc;
            public int SampleCount;
            public double SpeedSum;
            public int SpeedCount;

            public SpanState(int layer, DateTime startUtc)
            {
                Layer = layer;
                StartUtc = startUtc;
            }

            public void AddSample(double? speed)
            {
                SampleCount++;
                if (speed.HasValue && !double.IsNaN(speed.Value) && !double.IsInfinity(speed.Value))
                {
                    SpeedSum += speed.Value;
                    SpeedCount++;
                }
            }

            public PrintLogLayerSpan ToSpan(bool complete)
            {
                double? duration = EndUtc.HasValue
                    ? Math.Max(0.0, (EndUtc.Value - StartUtc).TotalSeconds)
                    : (double?)null;
                double? mean = SpeedCount > 0 ? SpeedSum / SpeedCount : (double?)null;
                return new PrintLogLayerSpan(Layer, StartUtc, EndUtc, duration, mean,
                                              SampleCount, complete);
            }
        }

        public PrintLogRecorder() : this(null) { }

        /// <summary>The recorder segments and classifies through the shared
        /// <see cref="TrailContinuityPolicy"/>, so the CSV, the live trail and a replay of
        /// the CSV all agree about what is one continuous observed run.</summary>
        /// <param name="options">Continuity thresholds; the defaults when null.</param>
        public PrintLogRecorder(TrailContinuityOptions? options)
        {
            _continuity = new TrailContinuityPolicy(options);
        }

        /// <summary>The intended path of the ACTIVE job, used only to flag observations
        /// that disagree with it. Null means unavailable (an externally started job, or a
        /// job whose file was never verified) and every comparison then reports unknown.
        /// The recorder never projects an observation onto this path.</summary>
        public void SetIntendedPath(IIntendedPathReference? path)
        {
            lock (_sync) _continuity.SetIntendedPath(path);
        }

        /// <summary>Identity of the run being recorded. Two prints of the same file get
        /// different ids, so their rows never share a segment.</summary>
        public void SetRunId(string? runId)
        {
            lock (_sync) _runId = runId ?? "";
        }

        /// <summary>Continuity verdict of the LAST observed poll - including the polls no
        /// row was emitted for. The session's diagnostic window reads it to decide whether
        /// to capture the raw telemetry around this observation.</summary>
        public TrailVerdict LastVerdict
        {
            get { lock (_sync) return _lastVerdict; }
        }

        /// <summary>Run key the next emitted row will carry; "" while nothing is printing.</summary>
        public string CurrentRunId
        {
            get { lock (_sync) return _currentRunKey; }
        }

        public bool Active
        {
            get { lock (_sync) return _options.Enabled; }
        }

        public int RecordCount
        {
            get { lock (_sync) return _records.Count; }
        }

        public long Version
        {
            get { lock (_sync) return _version; }
        }

        public int JobRun
        {
            get { lock (_sync) return _jobRun; }
        }

        public bool Truncated
        {
            get { lock (_sync) return _truncated; }
        }

        public void Configure(PrintLogOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            lock (_sync)
            {
                _options = options.Copy();
                _options.SampleEveryNPolls = Math.Max(0, _options.SampleEveryNPolls);
                _options.MaxRecords = Math.Max(1, _options.MaxRecords);
                if (!(_options.LayerHeightHint > 0) || double.IsNaN(_options.LayerHeightHint)
                    || double.IsInfinity(_options.LayerHeightHint))
                    _options.LayerHeightHint = 0;
                TrimToLimit();
            }
        }

        public IReadOnlyList<PrintLogRecord> Observe(ObjectModelSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (_sync)
            {
                var emitted = new List<PrintLogRecord>();
                if (!_options.Enabled) return emitted;

                // The run key is resolved BEFORE the policy sees the observation, so the
                // first sample of a new run already lands in a new segment. It carries the
                // recorder's own run counter, which is why printing the same file twice
                // produces two run ids and never one joined trail.
                string incomingRunKey = ResolveRunKey(snapshot);
                // Continuity is evaluated on EVERY observed poll, including the ones no row
                // is emitted for. Segmenting only the exported rows would join two samples
                // across a break the recorder saw but did not write down.
                _lastVerdict = _continuity.Observe(ToObservation(snapshot, incomingRunKey));
                // Rows that CLOSE the previous run are dated by the previous poll and must
                // keep that run's identity; only rows describing this observation take the
                // incoming one.
                string closingRunKey = _currentRunKey;
                TrailVerdict closingVerdict = _emitVerdict;

                if (!IsPrinting(snapshot.Status))
                {
                    if (_runOpen)
                    {
                        double? uncertainty = Gap(_lastSnapshot, snapshot);
                        _currentRunKey = closingRunKey; _emitVerdict = closingVerdict;
                        // The terminal snapshot supplies the machine state that ended the
                        // run. Without it a cancel and a clean finish produce identical
                        // rows, because position and time come from the last PRINTING poll.
                        EndRun(emitted, _lastSnapshot ?? snapshot, uncertainty,
                               snapshot.Status == MachineStatus.Idle, null,
                               snapshot.Status.ToString());
                    }
                    _currentRunKey = incomingRunKey; _emitVerdict = _lastVerdict;
                    return emitted;
                }

                if (!_runOpen)
                {
                    _currentRunKey = incomingRunKey; _emitVerdict = _lastVerdict;
                    StartRun(emitted, snapshot);
                    return emitted;
                }

                string? incomingFile = NonEmpty(snapshot.Job?.FileName);
                if (_jobFile != null && incomingFile != null
                    && !string.Equals(_jobFile, incomingFile, StringComparison.Ordinal))
                {
                    double? uncertainty = Gap(_lastSnapshot, snapshot);
                    _currentRunKey = closingRunKey; _emitVerdict = closingVerdict;
                    // The machine keeps printing here - the run ended because the file
                    // changed, so there is no terminal machine state to report.
                    EndRun(emitted, _lastSnapshot ?? snapshot, uncertainty, false, null, null);
                    _currentRunKey = incomingRunKey; _emitVerdict = _lastVerdict;
                    StartRun(emitted, snapshot);
                    return emitted;
                }
                _currentRunKey = incomingRunKey; _emitVerdict = _lastVerdict;
                if (_jobFile == null && incomingFile != null) _jobFile = incomingFile;

                double? deltaSeconds;
                double? observedSpeed = DerivedSpeed(_lastSnapshot, snapshot, out deltaSeconds);
                bool layerBoundary = false;
                int resolvedLayer;
                PrintLogLayerSource resolvedSource;
                if (TryResolveLayer(snapshot, out resolvedLayer, out resolvedSource))
                {
                    if (!_currentLayer.HasValue)
                    {
                        BeginLayer(emitted, snapshot, resolvedLayer, resolvedSource,
                                   observedSpeed, deltaSeconds, GapFromRunStart(snapshot));
                        layerBoundary = true;
                    }
                    else if (_currentLayer.Value != resolvedLayer)
                    {
                        EndLayer(emitted, _lastSnapshot ?? snapshot, _lastSpeed, _lastDeltaSeconds,
                                 Gap(_lastSnapshot, snapshot), true, null, null);
                        BeginLayer(emitted, snapshot, resolvedLayer, resolvedSource,
                                   observedSpeed, deltaSeconds, Gap(_lastSnapshot, snapshot));
                        layerBoundary = true;
                    }
                }

                if (_currentLayer.HasValue)
                {
                    if (!layerBoundary)
                    {
                        _pollsSinceBoundary++;
                        if (_options.SampleEveryNPolls > 0
                            && _pollsSinceBoundary >= _options.SampleEveryNPolls)
                        {
                            Emit(emitted, snapshot, PrintLogEventKind.Interval,
                                 _currentLayer, _currentLayerSource, observedSpeed, deltaSeconds, null, null);
                            _pollsSinceBoundary = 0;
                        }
                    }
                    if (!layerBoundary && _openSpan != null)
                        _openSpan.AddSample(observedSpeed);
                }

                SaveObservation(snapshot, observedSpeed, deltaSeconds);
                return emitted;
            }
        }

        public IReadOnlyList<PrintLogRecord> Finish(PrintLogFinishReason reason)
        {
            lock (_sync)
            {
                var emitted = new List<PrintLogRecord>();
                // _lastSnapshot is always set while a run is open; the fallback only
                // guards against a future call order that opens a run without observing.
                // Uncertainty is the real gap since the last observation: a ConnectionLost
                // finish lands 60+ s after it, and reporting 0 would claim an exact
                // boundary the log cannot possibly know.
                if (_runOpen)
                {
                    double? uncertainty = _lastSnapshot == null
                        ? (double?)null
                        : Math.Max(0.0, (DateTime.UtcNow - Utc(_lastSnapshot.TakenUtc)).TotalSeconds);
                    EndRun(emitted, _lastSnapshot
                           ?? new ObjectModelSnapshot { TakenUtc = DateTime.UtcNow },
                           uncertainty, false, reason, null);
                }
                return emitted;
            }
        }

        public IReadOnlyList<PrintLogRecord> Snapshot()
        {
            lock (_sync) return new List<PrintLogRecord>(_records);
        }

        public IReadOnlyList<PrintLogLayerSpan> LayerSpans()
        {
            lock (_sync)
            {
                var spans = new List<PrintLogLayerSpan>(_completedSpans);
                if (_openSpan != null) spans.Add(_openSpan.ToSpan(false));
                return spans;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _records.Clear();
                _completedSpans.Clear();
                _openSpan = null;
                _lastSnapshot = null;
                _jobFile = null;
                _runStartUtc = default(DateTime);
                _currentLayer = null;
                _currentLayerSource = PrintLogLayerSource.None;
                _fallbackLayer = 0;
                _pollsSinceBoundary = 0;
                _lastSpeed = null;
                _lastDeltaSeconds = null;
                _lastExtrusion = null;
                _runOpen = false;
                _jobRun = 0;
                _nextSequence = 1;
                _version = 0;
                _truncated = false;
                _currentRunKey = "";
                _lastVerdict = default(TrailVerdict);
                _emitVerdict = default(TrailVerdict);
                _continuity.Break();
            }
        }

        void StartRun(List<PrintLogRecord> emitted, ObjectModelSnapshot snapshot)
        {
            _runOpen = true;
            _jobRun++;
            _jobFile = NonEmpty(snapshot.Job?.FileName);
            _runStartUtc = Utc(snapshot.TakenUtc);
            _currentLayer = null;
            _currentLayerSource = PrintLogLayerSource.None;
            _fallbackLayer = 0;
            _pollsSinceBoundary = 0;
            _lastSpeed = null;
            _lastDeltaSeconds = null;
            SetExtrusionBaseline(snapshot);

            int layer;
            PrintLogLayerSource source;
            bool hasLayer = TryResolveLayer(snapshot, out layer, out source);
            Emit(emitted, snapshot, PrintLogEventKind.JobStart,
                 hasLayer ? (int?)layer : null, hasLayer ? source : PrintLogLayerSource.None,
                 null, null, 0, null);
            if (hasLayer)
                BeginLayer(emitted, snapshot, layer, source, null, null, 0);
            else if (_options.LayerHeightHint > 0)
                // Z-step detection needs a RISE, and the first physical layer never
                // produces one. Without opening layer 1 here the entire first layer
                // would yield no span and no interval records, and every later layer
                // would be numbered one low.
                BeginLayer(emitted, snapshot, 1, PrintLogLayerSource.ZStep, null, null, 0);
            SaveObservation(snapshot, null, null);
        }

        void BeginLayer(List<PrintLogRecord> emitted, ObjectModelSnapshot snapshot, int layer,
                        PrintLogLayerSource source, double? speed, double? delta,
                        double? uncertainty)
        {
            _currentLayer = layer;
            _currentLayerSource = source;
            _fallbackLayer = layer;
            _pollsSinceBoundary = 0;
            _openSpan = new SpanState(layer, Utc(snapshot.TakenUtc));
            _openSpan.AddSample(speed);
            Emit(emitted, snapshot, PrintLogEventKind.LayerStart, layer, source,
                 speed, delta, uncertainty, null);
        }

        void EndLayer(List<PrintLogRecord> emitted, ObjectModelSnapshot snapshot,
                      double? speed, double? delta, double? uncertainty,
                      bool complete, PrintLogFinishReason? reason, string? terminalStatus)
        {
            if (!_currentLayer.HasValue) return;
            Emit(emitted, snapshot, PrintLogEventKind.LayerEnd, _currentLayer,
                 _currentLayerSource, speed, delta, uncertainty, reason, terminalStatus);
            if (_openSpan != null)
            {
                _openSpan.EndUtc = Utc(snapshot.TakenUtc);
                _completedSpans.Add(_openSpan.ToSpan(complete && !reason.HasValue));
                _openSpan = null;
            }
            _currentLayer = null;
            _currentLayerSource = PrintLogLayerSource.None;
            _pollsSinceBoundary = 0;
        }

        void EndRun(List<PrintLogRecord> emitted, ObjectModelSnapshot snapshot,
                    double? uncertainty, bool complete, PrintLogFinishReason? reason,
                    string? terminalStatus)
        {
            if (!_runOpen) return;
            int? terminalLayer = _currentLayer;
            PrintLogLayerSource terminalSource = _currentLayerSource;
            EndLayer(emitted, snapshot, _lastSpeed, _lastDeltaSeconds, uncertainty, complete,
                     reason, terminalStatus);
            Emit(emitted, snapshot, PrintLogEventKind.JobEnd, terminalLayer,
                 terminalSource, _lastSpeed, _lastDeltaSeconds, uncertainty, reason,
                 terminalStatus);

            _runOpen = false;
            _lastSnapshot = null;
            _jobFile = null;
            _currentLayer = null;
            _currentLayerSource = PrintLogLayerSource.None;
            _pollsSinceBoundary = 0;
            _lastSpeed = null;
            _lastDeltaSeconds = null;
            _lastExtrusion = null;
        }

        bool TryResolveLayer(ObjectModelSnapshot snapshot, out int layer,
                             out PrintLogLayerSource source)
        {
            if (snapshot.Job != null && snapshot.Job.Layer.HasValue)
            {
                layer = snapshot.Job.Layer.Value;
                source = PrintLogLayerSource.Firmware;
                _fallbackLayer = layer;
                return true;
            }

            layer = 0;
            source = PrintLogLayerSource.None;
            if (!(_options.LayerHeightHint > 0) || _lastSnapshot == null)
                return false;

            Vec3 current = Position(snapshot, out bool currentHasPosition);
            Vec3 previous = Position(_lastSnapshot, out bool previousHasPosition);
            if (!currentHasPosition || !previousHasPosition || current.Z - previous.Z <= _options.LayerHeightHint * 0.5)
                return false;
            if (!snapshot.RawExtrusion.HasValue || !_lastExtrusion.HasValue
                || snapshot.RawExtrusion.Value <= _lastExtrusion.Value + 1e-9)
                return false;

            _fallbackLayer = _currentLayer.HasValue ? _currentLayer.Value + 1 : _fallbackLayer + 1;
            layer = _fallbackLayer;
            source = PrintLogLayerSource.ZStep;
            return true;
        }

        void SaveObservation(ObjectModelSnapshot snapshot, double? speed, double? delta)
        {
            _lastSnapshot = snapshot;
            _lastSpeed = speed;
            _lastDeltaSeconds = delta;
            SetExtrusionBaseline(snapshot);
        }

        /// <summary>Baseline for the Z-step layer fallback, which only accepts a
        /// rise that came with actual extrusion.</summary>
        void SetExtrusionBaseline(ObjectModelSnapshot snapshot)
        {
            _lastExtrusion = snapshot.RawExtrusion;
        }

        double? DerivedSpeed(ObjectModelSnapshot? previous, ObjectModelSnapshot current,
                             out double? deltaSeconds)
        {
            deltaSeconds = null;
            if (previous == null) return null;
            double dt = (Utc(current.TakenUtc) - Utc(previous.TakenUtc)).TotalSeconds;
            deltaSeconds = dt > 0 ? dt : (double?)null;
            if (!(dt > 0) || dt > MaxDerivedSpeedGapSeconds) return null;
            Vec3 p = Position(current, out bool currentHasPosition);
            Vec3 old = Position(previous, out bool previousHasPosition);
            if (!currentHasPosition || !previousHasPosition) return null;
            return old.DistanceTo(p) / dt;
        }

        /// <summary>Status columns carry the PARSED state name, never
        /// <see cref="ObjectModelSnapshot.RawStatus"/>: the legacy API reports protocol
        /// letters ("I", "P") while RRF3 reports words, so a raw value would make the
        /// same CSV column mean different things on different boards and break any
        /// filter written against it.</summary>
        void Emit(List<PrintLogRecord> emitted, ObjectModelSnapshot snapshot,
                  PrintLogEventKind kind, int? layer, PrintLogLayerSource source,
                  double? speed, double? delta, double? uncertainty,
                  PrintLogFinishReason? reason, string? terminalStatus = null)
        {
            var record = new PrintLogRecord(kind, _jobRun, _jobFile, layer, source,
                                            Utc(snapshot.TakenUtc), snapshot.Job?.TimePrinting,
                                            delta, Position(snapshot, out _), speed,
                                            snapshot.RequestedSpeed, snapshot.TopSpeed,
                                            snapshot.SpeedFactorPercent, snapshot.RawExtrusion,
                                            snapshot.Job?.FractionPrinted,
                                            snapshot.Job == null ? (long?)null : snapshot.Job.FilePosition,
                                            snapshot.Status.ToString(), uncertainty, reason,
                                            terminalStatus)
            {
                Seq = _nextSequence++,
                RunId = _currentRunKey,
                SegmentId = _emitVerdict.SegmentId,
                Quality = _emitVerdict.Quality,
                Classification = _emitVerdict.Classification,
                QualityReason = _emitVerdict.Reason,
                PathDistanceMm = double.IsNaN(_emitVerdict.PathDistanceMm) ? (double?)null : _emitVerdict.PathDistanceMm,
                ImpliedSpeedMms = double.IsNaN(_emitVerdict.ImpliedSpeedMms) ? (double?)null : _emitVerdict.ImpliedSpeedMms,
                AcquisitionId = snapshot.AcquisitionId,
                PositionValid = snapshot.PositionValid,
                PositionInvalidReason = snapshot.PositionInvalidReason,
                Composite = snapshot.CompositeObservation,
            };
            _records.Add(record);
            emitted.Add(record);
            _version++;
            TrimToLimit();
        }

        void TrimToLimit()
        {
            int limit = Math.Max(1, _options.MaxRecords);
            while (_records.Count > limit)
            {
                _records.RemoveAt(0);
                _truncated = true;
            }
        }

        static bool IsPrinting(MachineStatus status)
            => status == MachineStatus.Processing || status == MachineStatus.Simulating
            || status == MachineStatus.Paused || status == MachineStatus.Pausing
            || status == MachineStatus.Resuming;

        static string? NonEmpty(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        static double? Gap(ObjectModelSnapshot? previous, ObjectModelSnapshot current)
        {
            if (previous == null) return 0;
            double seconds = (Utc(current.TakenUtc) - Utc(previous.TakenUtc)).TotalSeconds;
            return seconds > 0 ? seconds : 0;
        }

        double GapFromRunStart(ObjectModelSnapshot snapshot)
        {
            double seconds = (Utc(snapshot.TakenUtc) - _runStartUtc).TotalSeconds;
            return seconds > 0 ? seconds : 0;
        }

        /// <summary>Machine position of a snapshot. <paramref name="complete"/> is false
        /// when the snapshot has no usable position; the returned vector is then the
        /// coordinates that WERE reported, and the row that carries it is marked
        /// position_valid=0 so nothing downstream reads it as a measurement.</summary>
        static Vec3 Position(ObjectModelSnapshot snapshot, out bool complete)
        {
            var x = snapshot.Axis("X");
            var y = snapshot.Axis("Y");
            var z = snapshot.Axis("Z");
            complete = snapshot.PositionValid && x != null && y != null && z != null;
            return new Vec3(x?.MachinePosition ?? 0, y?.MachinePosition ?? 0,
                            z?.MachinePosition ?? 0);
        }

        /// <summary>Identity of the run this observation belongs to. Empty while nothing is
        /// printing, so a gap between runs always breaks continuity. The run counter is the
        /// one the NEXT emitted record will carry, which is what makes a repeat print of
        /// the same file a different run rather than a continuation of the old one.</summary>
        string ResolveRunKey(ObjectModelSnapshot snapshot)
        {
            if (!IsPrinting(snapshot.Status)) return "";
            string? incoming = NonEmpty(snapshot.Job?.FileName);
            int run = _jobRun;
            if (!_runOpen
                || (_jobFile != null && incoming != null
                    && !string.Equals(_jobFile, incoming, StringComparison.Ordinal)))
                run++;
            string name = _runId.Length > 0 ? _runId : (incoming ?? _jobFile ?? "");
            return name + "#" + run.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Maps a snapshot onto the shared continuity model.</summary>
        TrailObservation ToObservation(ObjectModelSnapshot snapshot, string runKey)
        {
            var stamp = snapshot.Acquisition;
            var machine = snapshot.MachinePosition;
            return new TrailObservation
            {
                AcquisitionId = snapshot.AcquisitionId,
                RequestUtc = stamp?.RequestStartedUtc ?? default(DateTime),
                ReceivedUtc = Utc(snapshot.TakenUtc),
                ConnectionGeneration = snapshot.ConnectionGeneration,
                RunId = runKey,
                PositionValid = machine.HasValue,
                Position = machine ?? default(Vec3),
                AllHomed = snapshot.AllHomed,
                // A busy machine whose axes are not all homed is running a homing move;
                // its coordinates describe a search, not a toolpath.
                Homing = snapshot.Status == MachineStatus.Busy && !snapshot.AllHomed,
                Status = snapshot.Status,
                JobActive = snapshot.Job != null,
                Layer = snapshot.Job?.Layer,
                FilePosition = snapshot.Job?.FilePosition,
                FractionPrinted = snapshot.Job?.FractionPrinted,
                RequestedSpeedMms = snapshot.RequestedSpeed,
                TopSpeedMms = snapshot.TopSpeed,
                RawExtrusion = snapshot.RawExtrusion,
                CompositeSpeedMetadata = snapshot.CompositeObservation,
                RepairNote = snapshot.PositionRepair,
            };
        }
    }
}
