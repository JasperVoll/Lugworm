using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading;
using Rhino;
using Rhino.Geometry;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Robot;

namespace Lugworm.Duet
{
    /// <summary>What the live viewport needs from a solve. Immutable; replaced whole.</summary>
    /// <remarks>The intended path is named, not carried: <see cref="Path"/> says which job's
    /// bytes to compare against and the controller resolves them through
    /// <see cref="ParsedJobCache"/>, so a solve never parses anything and two Live Views on
    /// one job parse it once between them.</remarks>
    public sealed class LiveRenderConfig
    {
        public RobotLimits Limits { get; }
        public int ElbowFallback { get; }
        public bool Cartesian { get; }
        public double BaseZ { get; }
        public bool HideArmWhileUnhomed { get; }
        /// <summary>Which intended path to show, and where it came from.</summary>
        public IntendedPathRequest Path { get; }
        public bool Smooth { get; }
        public bool DeviationOn { get; }
        public double DeviationLegendMm { get; }

        public LiveRenderConfig(RobotLimits limits, int elbowFallback, bool cartesian, double baseZ,
                                bool hideArmWhileUnhomed, IntendedPathRequest path,
                                bool smooth, bool deviationOn, double deviationLegendMm)
        {
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));
            ElbowFallback = elbowFallback; Cartesian = cartesian; BaseZ = baseZ;
            HideArmWhileUnhomed = hideArmWhileUnhomed;
            Path = path ?? IntendedPathRequest.None;
            Smooth = smooth; DeviationOn = deviationOn; DeviationLegendMm = deviationLegendMm;
        }

        /// <summary>The part of the configuration that changes what a pose means. A change
        /// starts a new interpolation epoch; anything else is applied without a flush.</summary>
        internal string FrameKey => string.Format(CultureInfo.InvariantCulture,
            "{0:R}|{1:R}|{2:R}|{3:R}|{4:R}|{5}|{6}|{7}|{8:R}",
            Limits.L1, Limits.L2, Limits.BaseH, Limits.ReachMin, Limits.ReachMax,
            Cartesian ? 1 : 0, ElbowFallback, HideArmWhileUnhomed ? 1 : 0, BaseZ);
    }

    /// <summary>One continuous run of observed positions, as the continuity policy decided
    /// it. Handed to the component for its <c>trail</c> output; never smoothed, never
    /// joined across a segment the policy broke.</summary>
    public sealed class LiveTrailSegment
    {
        public long SegmentId { get; }
        /// <summary>Observed positions in order, RHINO frame, mm. One point is a legitimate
        /// segment: it says a position was observed and nothing may be drawn from it.</summary>
        public IReadOnlyList<Point3d> Points { get; }
        /// <summary>How many of these observations the continuity policy flagged. They are
        /// in the list with their raw coordinates - this is a count, not a removal.</summary>
        public int SuspectCount { get; }

        public LiveTrailSegment(long segmentId, IReadOnlyList<Point3d> points, int suspectCount)
        { SegmentId = segmentId; Points = points; SuspectCount = suspectCount; }
    }

    /// <summary>The observed trail as the component publishes it.</summary>
    public sealed class LiveTrailSnapshot
    {
        public IReadOnlyList<LiveTrailSegment> Segments { get; }
        public int PointCount { get; }
        public long SuspectSamples { get; }
        public long DroppedSamples { get; }
        /// <summary>What bounded retention has kept, compacted and discarded. The trail
        /// output is the display's data, so a reduction in it is reported rather than made
        /// silently (plan V4 item 4).</summary>
        public string RetentionNote { get; }
        /// <summary>Why drawn runs ended, by reason - a continuity break and a drawing gap
        /// are counted apart.</summary>
        public string BreakReasons { get; }

        public LiveTrailSnapshot(IReadOnlyList<LiveTrailSegment> segments, int pointCount,
                                 long suspectSamples, long droppedSamples,
                                 string? retentionNote = null, string? breakReasons = null)
        {
            Segments = segments; PointCount = pointCount; SuspectSamples = suspectSamples;
            DroppedSamples = droppedSamples; RetentionNote = retentionNote ?? "";
            BreakReasons = breakReasons ?? "";
        }

        public static readonly LiveTrailSnapshot Empty =
            new LiveTrailSnapshot(Array.Empty<LiveTrailSegment>(), 0, 0, 0);
    }

    /// <summary>Owns the live robot display for one Live View component: a bounded pose
    /// timeline fed at telemetry cadence, a frame pump that only ever posts one coalesced UI
    /// callback, and a display conduit that repaints Rhino viewports without a Grasshopper
    /// solve, canvas invalidation or refresh.</summary>
    /// <remarks>
    /// <para>Three rates stay independent: the session polls the machine; this controller
    /// animates between observed samples with a deliberate delay
    /// (<see cref="LivePoseTimeline"/>); the component publishes GH outputs on its own
    /// terms. Nothing here calls ExpireSolution.</para>
    /// <para>Since 2026-09-16 the arm presumes the machine executes its file: a reading on
    /// the intended path is progress, the arm is posed ON the path there and moves along the
    /// file's own route between readings (<see cref="LivePathFollower"/>). A reading off
    /// the path is held rather than drawn; only after it has persisted for 1.5 s is the
    /// measured position shown instead. The trail, the outputs and the logs never see any
    /// of this - they consume observations.</para>
    /// <para>The animation is an estimated, delayed representation of firmware-reported
    /// position. Safety gates, calibration, print logs, the observed trail and the numeric
    /// outputs never read a frame from here; they read observed telemetry (ARCHI §15).</para>
    /// <para>One redraw per UI tick, at most: the pose and the overlays are both published
    /// before a single <see cref="Redraw"/>. Frames are compared by geometry alone - there is
    /// no diagnostic text in a frame to invalidate a stationary one.</para>
    /// <para>Threads: the poll thread adds samples and trail points; the timer thread only
    /// asks the policy for a callback; every frame, IK, geometry, publish and redraw runs on
    /// the Rhino UI thread; G-code parsing runs on a thread-pool thread inside
    /// <see cref="ParsedJobCache"/>. Mutable trail state is guarded by one lock that the
    /// paint callback never takes (it reads published immutable snapshots).</para>
    /// </remarks>
    public sealed class LiveViewportController : IDisposable
    {
        /// <summary>The default frame budget: 30 fps. Detail is raised only when it is
        /// affordable, so this is a ceiling on work, not a promise of smoothness.</summary>
        public const int FrameIntervalMs = 33;
        const int DegradedIntervalMs = 66;
        const int HeavilyDegradedIntervalMs = 100;
        /// <summary>The slow beat that only exists to notice a pose going stale. Once it is
        /// stale and nothing changes, the pump stops entirely.</summary>
        const int StaleWatchIntervalMs = 500;
        /// <summary>How often the overlays may be republished. 2026-09-09: this was 500 ms,
        /// which at 40 mm/s is 20 mm of motion before a newly observed point could appear.
        /// The rebuild is now incremental (see <see cref="PublishOverlaysIfDue"/>), so the
        /// bound can be the plan's 100 ms publication-latency target instead of the cost of
        /// re-copying and re-colouring every run.</summary>
        const double OverlayMinIntervalMs = 100;
        /// <summary>The trail's point budget. Beyond it older stretches are simplified in
        /// steps (<see cref="TrailRetention"/>); points are discarded only past
        /// <see cref="TrailRetention.HardCapFactor"/> times this, and that is reported.</summary>
        const int TrailLimit = 5000;
        const double TrailDecimationMm = 0.5;
        const double FeatureAngleRad = 0.55;
        const int CostWindow = 32;
        /// <summary>Window for the cadence and latency percentiles. Larger than the frame
        /// window because a poll is an order of magnitude slower than a frame.</summary>
        const int MetricWindow = 64;
        const double DegradeAtMs = 12.0, HeavyDegradeAtMs = 25.0, RecoverBelowMs = 6.0;

        sealed class TrailRun
        {
            public long SegmentId;
            public readonly List<Point3d> Points = new List<Point3d>();
            public readonly List<float> Deviation = new List<float>();
            public readonly List<bool> Ambiguous = new List<bool>();
            public int Suspect;
            /// <summary>Which stretches of this run retention has simplified, and how far.</summary>
            public readonly TrailRetention.RunState Retention = new TrailRetention.RunState();
            /// <summary>What was handed to the conduit last time and how much of this run it
            /// covered. A growing run extends these instead of rebuilding them, which is what
            /// took the whole trail's copy and recolour out of the 2 Hz overlay path.</summary>
            public Point3d[]? PublishedPoints;
            public Color[]? PublishedColors;
            public bool[]? PublishedAmbiguous;
            public int PublishedCount;
            public bool PublishedColoured;
            public double PublishedLegendMm;

            public void InvalidatePublished()
            {
                PublishedPoints = null; PublishedColors = null; PublishedAmbiguous = null;
                PublishedCount = 0; PublishedColoured = false; PublishedLegendMm = double.NaN;
            }
        }

        /// <summary>A reading set aside: its display sample and what the trail needs to
        /// append it later.</summary>
        sealed class HeldReading
        {
            public LivePoseSample Sample;
            public Vec3 Machine;
            public DateTime TakenUtc;
            public long SegmentId;
            public TrailQuality Quality;
            public LivePathDeviationResult Comparison;
        }

        readonly object _lock = new object();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly LivePoseTimeline _timeline = new LivePoseTimeline();
        /// <summary>The shared rule set for what may be joined into one drawn run. Called
        /// from the poll thread only, which is the one thread <see cref="OnPollCompleted"/>
        /// runs on.</summary>
        readonly TrailContinuityPolicy _continuity = new TrailContinuityPolicy();
        /// <summary>Turns readings into progress along the intended path; the ARM is posed
        /// from its decisions, the trail and the outputs from the observations (2026-09-16).
        /// Replaced whole when the path changes; every use is under <see cref="_lock"/>.</summary>
        LivePathFollower _follower = new LivePathFollower(null);
        /// <summary>Readings the follower has not posed from yet: off the path for less than
        /// the persistence time, or an advance waiting for the next reading to confirm it.
        /// A confirmed deviation animates and draws the excursion from where it began; a
        /// corroborated advance is posed and drawn at its own time; anything else is dropped
        /// from the display, never from the logs. Guarded by <see cref="_lock"/>.</summary>
        readonly List<HeldReading> _pendingSamples = new List<HeldReading>();
        const int PendingSampleLimit = 16;
        long _heldSamples, _deferredAdvances, _rejectedAdvances, _deviationEpisodes, _relocations;
        /// <summary>The session is between a transport failure and its reconnect. The last
        /// pose is held through it instead of hidden.</summary>
        volatile bool _reconnecting;
        readonly LiveDisplayPolicy _policy = new LiveDisplayPolicy();
        /// <summary>Why drawn runs actually ended. A false break used to be an impression;
        /// this makes it an attributable count (plan V3 item 1).</summary>
        readonly TrailBreakCounters _breaks = new TrailBreakCounters();
        /// <summary>The frame pump's scheduling rules, extracted so the "moves again after a
        /// rest" path has a regression that does not need Rhino.</summary>
        readonly FramePumpScheduler _pump = new FramePumpScheduler();
        readonly LiveRobotConduit _conduit;
        readonly Timer _timer;
        readonly Func<bool> _visible;

        volatile LiveRenderConfig? _config;
        string _frameKey = "";
        int _configRevision;
        MachineSession? _session;
        volatile bool _connected;
        volatile bool _disposed;
        int _lifetime;   // bumped by Dispose: queued callbacks become no-ops

        // epoch: connection generation, frame configuration, arm branch, job, and the
        // session's RETENTION RUN id. The run id is what the old key was missing: printing
        // the same file twice kept the same key, so a same-filename restart could leave old
        // interpolation samples usable across a boundary that is not continuous at all
        // (plan V1 item 5).
        long _epoch;
        (long conn, int cfg, int arm, string job, string run) _epochKey = (-1, -1, -1, "", "");
        int _armMode;
        string? _lastSampleReason;
        /// <summary>The last frame that posed the arm. Held through an unsolvable
        /// observation so the arm never blanks for one frame (2026-09-09).</summary>
        LiveRobotFrame? _lastPosed;

        // trail (telemetry cadence), guarded by _lock
        readonly List<TrailRun> _runs = new List<TrailRun>();
        int _trailPoints;
        bool _trailDirty;
        string _trailJob = "";
        LivePathDeviation? _deviation;
        int _deviationUnavailable, _deviationAmbiguous, _deviationSaturated;
        /// <summary>Bounds of the observed trail, maintained as points are appended. The
        /// overlay used to union every point of every run on every publication; compaction
        /// and pruning only ever REMOVE points, so a cached box stays valid (it can only be
        /// too large, never too small).</summary>
        BoundingBox _observedBounds = BoundingBox.Empty;

        // Intended path, resolved from the shared cache and never parsed on this thread.
        // Written by the solve thread and by the parse callback, read by the UI tick: plain
        // reference assignments, marked volatile so a tick cannot see a half-updated pair.
        volatile string _pathKey = "";
        volatile ParsedJobPath? _path;
        volatile IReadOnlyList<Point3d[]> _context = Array.Empty<Point3d[]>();
        /// <summary>The job's non-extruding moves - travel, seams, coast and the layer
        /// transitions - drawn separately from the printed geometry (plan V2).</summary>
        volatile IReadOnlyList<Point3d[]> _travel = Array.Empty<Point3d[]>();
        /// <summary>Bounds of the intended path, measured once when a path is adopted.</summary>
        BoundingBox _contextBounds = BoundingBox.Empty;

        // the previous accepted observation, for the sparse-sample drawing rule (poll thread)
        bool _havePrevious;
        Vec3 _previousMachine;
        DateTime _previousUtc;

        // telemetry counters (poll thread, guarded by _lock)
        long _pollRequests, _droppedSamples, _suspectSamples;
        double _roundTripSum, _lastRoundTripMs;
        double _pollIntervalSum; long _pollIntervals; double _lastPollAtMs = double.NaN;

        // ---- honest cadence and latency measurement (poll thread, guarded by _lock) ------
        // The old RecordPoll counted CALLBACKS and timed them on the local clock. The glitch
        // filter releases a held sample together with its successor, so one poll could
        // deliver two callbacks microseconds apart and fabricate a near-zero poll interval.
        // Acquisitions are therefore deduplicated by id and timed by their own stamps.
        long _lastAcquisitionId;
        long _lastAcquisitionConnection = -1, _lastPollWorkGeneration;
        DateTime _lastAcquisitionUtc;
        long _acquisitions, _duplicateCallbacks;
        readonly double[] _coordIntervals = new double[MetricWindow];
        readonly double[] _coordRoundTrips = new double[MetricWindow];
        readonly double[] _fullPollMs = new double[MetricWindow];
        int _coordN, _rttN, _fullPollN;
        int _requestedPollMs, _effectivePollTargetMs;
        string? _pollClampNote;

        // publication latency: when the oldest not-yet-published observation was accepted
        double _pendingSinceMs = double.NaN;
        readonly double[] _publicationAge = new double[MetricWindow];
        readonly double[] _overlayGap = new double[MetricWindow];
        int _pubN, _overlayN;

        // display validity (plan V1): held observations are preserved and counted, never
        // deleted, so a sustained real fault cannot vanish into a tidy trail
        long _observationsHeld, _rejectedByEnvelope;
        bool _envelopeKnown;

        // bounded retention accounting
        long _prunedPoints, _compactedPoints;

        // measurement-window baselines, taken when this display attaches: the shared parse
        // cache's counters are process-wide statics and a lifetime total says nothing about
        // this window (plan V4 item 1)
        long _parseBase, _cacheHitBase, _redrawBase, _callbackBase;

        // pump + statistics (UI thread)
        int _pumpIntervalMs;
        bool _pumpRunning;
        int _settled;
        double _lastOverlayMs = double.NegativeInfinity;
        LiveRobotFrame? _published;
        readonly double[] _tickCost = new double[CostWindow];
        readonly double[] _tickGap = new double[CostWindow];
        readonly double[] _uiWait = new double[CostWindow];
        int _tickN;
        long _frameCallbacks, _redraws;
        double _lastTickMs = double.NaN;
        double _lowCostSinceMs = double.NaN;
        double _requestedAtMs = double.NaN;
        /// <summary>Written by the UI tick, read by the poll thread's <see cref="Wake"/> to
        /// pick a target rate. A plain reference read of an immutable string.</summary>
        volatile string _degraded = "";
        string? _fault;

        public LiveViewportController(Func<bool> visible, Func<double> scaleToMm)
        {
            _visible = visible ?? throw new ArgumentNullException(nameof(visible));
            _conduit = new LiveRobotConduit(visible, scaleToMm);
            _conduit.Enabled = true;
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            _pumpIntervalMs = FrameIntervalMs;
            // An unfilled percentile slot is UNKNOWN, not zero: a half-filled window of
            // zeroes would report a 0 ms cadence and be believed.
            Blank(_coordIntervals); Blank(_coordRoundTrips); Blank(_fullPollMs);
            Blank(_publicationAge); Blank(_overlayGap);
            TakeWindowBaseline();
        }

        static void Blank(double[] ring)
        {
            for (int i = 0; i < ring.Length; i++) ring[i] = double.NaN;
        }

        static void Push(double[] ring, ref int count, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return;
            ring[count % ring.Length] = value;
            count++;
        }

        /// <summary>Starts a fresh measurement window. The parse-cache counters are shared
        /// process-wide statics, so only a delta against these baselines describes what THIS
        /// display did.</summary>
        void TakeWindowBaseline()
        {
            _parseBase = ParsedJobCache.ParseCount;
            _cacheHitBase = ParsedJobCache.CacheHits;
            _redrawBase = Interlocked.Read(ref _redraws);
            _callbackBase = Interlocked.Read(ref _frameCallbacks);
        }

        /// <summary>Everything drawn, mm, for the component clipping box.</summary>
        public BoundingBox Bounds => _conduit.Bounds;

        // ---- configuration and attachment (UI thread) --------------------------------

        public void Configure(LiveRenderConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_disposed) return;
            string frameKey = config.FrameKey;
            bool frameChanged = !string.Equals(frameKey, _frameKey, StringComparison.Ordinal);
            _config = config;
            if (frameChanged)
            {
                _frameKey = frameKey;
                Interlocked.Increment(ref _configRevision);
                // A new frame is a fresh start: a display fault is not permanent any more.
                // One that persists is contained again on its next tick.
                _fault = null;
            }
            ResolveIntendedPath(config);
            if (frameChanged) lock (_lock) _trailDirty = true;
            Wake();
        }

        /// <summary>Points the display at the intended path of the job it was given. Parsing
        /// happens once per job revision, in <see cref="ParsedJobCache"/>, off this thread;
        /// a job whose bytes are unknown leaves the path null and draws nothing.</summary>
        void ResolveIntendedPath(LiveRenderConfig config)
        {
            string key = config.Path.Key;
            bool keyChanged = !string.Equals(key, _pathKey, StringComparison.Ordinal);
            var entry = key.Length == 0 ? null : ParsedJobCache.Lookup(key);
            if (!keyChanged && ReferenceEquals(entry, _path)) return;

            _pathKey = key;
            Adopt(entry);
            if (entry == null && config.Path.Parseable)
                ParsedJobCache.Request(config.Path, OnPathParsed);
        }

        /// <summary>The background parse finished. Runs on the parse thread, so it publishes
        /// the result and wakes the pump; it never solves Grasshopper.</summary>
        void OnPathParsed(ParsedJobPath entry)
        {
            if (_disposed || entry == null) return;
            // A job that has been superseded while its file was parsing is dropped: the
            // display must never show the path of a print that is no longer the one running.
            if (!string.Equals(entry.Key, _pathKey, StringComparison.Ordinal)) return;
            Adopt(entry);
            Wake();
        }

        void Adopt(ParsedJobPath? entry)
        {
            _path = entry;
            bool usable = entry != null && entry.Error == null;
            _context = usable ? entry!.ContextRuns : Array.Empty<Point3d[]>();
            _travel = usable ? entry!.TravelRuns : Array.Empty<Point3d[]>();
            var box = BoundingBox.Empty;
            foreach (var run in _context) foreach (var p in run) box.Union(p);
            foreach (var run in _travel) foreach (var p in run) box.Union(p);
            lock (_lock)
            {
                // A replaced intended path resets the comparison: old observations are never
                // recoloured against a different job. The index itself was built on the parse
                // thread, so this is a reference assignment, not work.
                _deviation = entry != null && entry.HasGeometry ? entry.Deviation : null;
                _follower = new LivePathFollower(_deviation);
                _pendingSamples.Clear();
                _timeline.Path = _deviation;
                foreach (var run in _runs)
                {
                    for (int i = 0; i < run.Deviation.Count; i++) { run.Deviation[i] = float.NaN; run.Ambiguous[i] = false; }
                    run.InvalidatePublished();   // every colour is now against a different job
                }
                _deviationUnavailable = _deviationAmbiguous = _deviationSaturated = 0;
                // Measured once here, on the thread that adopts the path, instead of on every
                // overlay publication.
                _contextBounds = box;
                _trailDirty = true;
            }
        }

        public void Attach(MachineSession? session)
        {
            if (_disposed) return;
            var previous = _session;
            if (ReferenceEquals(previous, session)) return;
            if (previous != null)
            {
                previous.PollCompleted -= OnPollCompleted;
                previous.Changed -= OnSessionChanged;
            }
            _session = session;
            // A new attachment restarts the display, so toggling `enable` recovers from a
            // display fault (the controller itself lives until the component goes).
            _fault = null;
            if (session != null)
            {
                session.PollCompleted += OnPollCompleted;
                session.Changed += OnSessionChanged;
                _connected = session.State == DuetConnState.Connected;
                _reconnecting = session.State == DuetConnState.Reconnecting;
            }
            else { _connected = false; _reconnecting = false; }
            _timeline.Clear();
            _continuity.Break();
            lock (_lock)
            {
                ClearTrailLocked();
                _havePrevious = false;
                _breaks.Reset();
                _follower.Reset();
                _pendingSamples.Clear();
                _heldSamples = _deferredAdvances = _rejectedAdvances = _deviationEpisodes = _relocations = 0;
                _lastAcquisitionId = 0;
                _lastAcquisitionConnection = -1;
                _lastPollWorkGeneration = 0;
                _lastAcquisitionUtc = default(DateTime);
                _acquisitions = _duplicateCallbacks = 0;
                _observationsHeld = _rejectedByEnvelope = 0;
                _prunedPoints = _compactedPoints = 0;
                _coordN = _rttN = _fullPollN = _pubN = _overlayN = 0;
                Blank(_coordIntervals); Blank(_coordRoundTrips); Blank(_fullPollMs);
                Blank(_publicationAge); Blank(_overlayGap);
                _pendingSinceMs = double.NaN;
                TakeWindowBaseline();
            }
            Wake();
        }

        // ---- telemetry (poll thread) ---------------------------------------------------

        void OnSessionChanged(SessionChange change)
        {
            if (_disposed) return;
            if ((change.Kind & SessionChangeKind.Connection) == 0) return;
            var session = _session;
            var state = session == null ? DuetConnState.Disconnected : session.State;
            bool connected = state == DuetConnState.Connected;
            _reconnecting = state == DuetConnState.Reconnecting;
            _connected = connected;
            // A closed or failed session hides the arm at once, including a reconnect that
            // gives up. A transport retry does not: the machine is most likely still
            // executing its file, and the operator saw the arm vanish for the length of a
            // 12 s reconnect during a layer approach (run 20260916-124703-99cbd9). The last
            // pose is held until the reconnect delivers a fresh reading or the session
            // gives up.
            if (!connected && !_reconnecting) { _timeline.Clear(); _lastPosed = null; }
            Wake();
        }

        void OnPollCompleted(long generation, ObjectModelSnapshot snap)
        {
            if (_disposed) return;
            var cfg = _config;
            var session = _session;
            if (cfg == null || session == null) return;
            try
            {
                if (!RecordCadence(session, snap)) return;
                // The shared continuity policy decides what may be joined. It runs on EVERY
                // poll, including the ones with no usable position, because an observation
                // that is skipped still has to break the trail rather than be stitched over.
                var verdict = _continuity.Observe(ToObservation(snap, session));

                // The DISPLAY validity decision, taken BEFORE the timeline and before IK.
                // A successful IK solve cannot validate telemetry: InverseRrf solves XY and
                // assigns j2 = z - BaseH, so the 2026-09-09 approach replies - Z down to
                // about -390 mm, every axis flagged homed, while the same reply reports the
                // machine's own Z envelope as 0-400 - solved happily and were animated as
                // motion (plan V1). The coordinates are preserved and counted, never
                // clamped, projected or replaced by an intended point.
                var decision = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions
                {
                    HideArmWhileUnhomed = cfg.HideArmWhileUnhomed,
                });
                if (decision.Eligibility != PoseEligibility.Show)
                {
                    // The arm is HELD, not blanked: the job's own G28 and cancel.g unhome the
                    // axes for seconds at a time and the operator asked for a persistent arm
                    // (2026-09-09). What changes is that the timeline is invalidated, so
                    // nothing is interpolated ACROSS the boundary and the held pose is drawn
                    // as uncertain with its reason on `info`.
                    bool missing = !snap.PositionValid || !snap.MachinePosition.HasValue;
                    lock (_lock)
                    {
                        _lastSampleReason = decision.Reason;
                        _follower.Reset();
                        _pendingSamples.Clear();
                        _havePrevious = false;
                        _observationsHeld++;
                        if (decision.RejectedByEnvelope) _rejectedByEnvelope++;
                        if (missing) _droppedSamples++;
                        _envelopeKnown = decision.EnvelopeKnown;
                        _breaks.Count(missing ? TrailBreakReason.InvalidObservation
                                              : TrailBreakReason.PoseIneligible);
                    }
                    _timeline.Invalidate(decision.Reason);
                    _continuity.Break(); // recovery must start a new drawn run as well
                    Wake();
                    return;
                }
                var machine = snap.MachinePosition!.Value;
                int armMode = cfg.Cartesian ? 0
                    : session.ArmConfiguration.Known ? session.ArmConfiguration.ArmMode : cfg.ElbowFallback;
                string job = snap.Job?.FileName ?? "";
                var key = (session.ConnectionGeneration, Volatile.Read(ref _configRevision), armMode, job,
                           session.RetentionRunId ?? "");

                long epoch;
                lock (_lock)
                {
                    _envelopeKnown = decision.EnvelopeKnown;
                    if (!key.Equals(_epochKey))
                    {
                        bool frameChanged = key.Item2 != _epochKey.cfg || key.Item3 != _epochKey.arm;
                        _epochKey = key;
                        _follower.Reset();
                        _pendingSamples.Clear();
                        _epoch++;
                        _armMode = armMode;
                        if (!string.Equals(job, _trailJob, StringComparison.Ordinal))
                        {
                            // a new job: the old trail belongs to a different print
                            ClearTrailLocked();
                            _trailJob = job;
                        }
                        else StartRunLocked(verdict.SegmentId);   // reconnect, home, branch change: split, keep
                        _breaks.Count(frameChanged ? TrailBreakReason.FrameOrConfiguration
                                                   : TrailBreakReason.RunOrConnection);
                        _havePrevious = false;
                    }
                    else if (verdict.SegmentStarted)
                    {
                        _follower.Reset();
                        _pendingSamples.Clear();
                        _epoch++; // the pose must not interpolate across a trail boundary
                        StartRunLocked(verdict.SegmentId);
                        _breaks.Count(TrailBreakReason.ContinuityVerdict);
                        _havePrevious = false;
                    }
                    epoch = _epoch;
                    _lastSampleReason = null;
                    if (verdict.Quality != TrailQuality.Valid && verdict.Quality != TrailQuality.Discontinuity)
                        _suspectSamples++;
                }
                // One whole-path query per acquisition: it colours the trail and seeds the
                // follower, which otherwise searches a window of progress around the last
                // fix. This gate is downstream of raw logging and continuity on purpose.
                LivePathDeviation? reference;
                lock (_lock) reference = _deviation;
                var comparison = reference?.Query(machine) ?? LivePathDeviationResult.Unavailable();
                double now = _clock.Elapsed.TotalMilliseconds;
                double roundTrip = session.LastPollRoundTripMs;
                if (double.IsNaN(roundTrip) || roundTrip < 0) roundTrip = 0;
                // The sample is dated by its own acquisition stamp: a poll the glitch filter
                // held for its successor arrives here one interval late, and must not be
                // timed as if it had just been received.
                double receipt = now, requestStart = now - roundTrip;
                var stamp = snap.Acquisition;
                if (stamp != null && stamp.ResponseReceivedUtc != default(DateTime))
                {
                    double ageMs = (DateTime.UtcNow - stamp.ResponseReceivedUtc).TotalMilliseconds;
                    if (ageMs >= 0 && ageMs < 60000)
                    {
                        receipt = now - ageMs;
                        double inFlight = (stamp.ResponseReceivedUtc - stamp.RequestStartedUtc).TotalMilliseconds;
                        requestStart = receipt - (inFlight > 0 && inFlight < 60000 ? inFlight : roundTrip);
                    }
                }
                long sequence = snap.AcquisitionId > 0 ? snap.AcquisitionId : generation;

                // The arm is posed from the follower's decision (2026-09-16): on the path at
                // the located progress, held through a brief off-path reading, or at the
                // measured position once a deviation has persisted. The trail below keeps
                // consuming the observation itself.
                bool executing = snap.AllHomed && (snap.Status == MachineStatus.Processing
                    || snap.Status == MachineStatus.Simulating);
                var measured = new LivePoseSample(sequence, epoch, requestStart, receipt, machine, true);
                bool printing = AppendsTrail(snap.Status);
                LivePathFollowResult follow;
                lock (_lock)
                {
                    if (!ReferenceEquals(reference, _deviation)) comparison = LivePathDeviationResult.Unavailable();
                    bool wasDeviating = _follower.Mode == LivePathFollowMode.Deviating;
                    bool wasDeferred = _follower.Mode == LivePathFollowMode.Deferred;
                    follow = _follower.Observe(machine, snap.TakenUtc, executing, comparison);
                    if (wasDeferred && _pendingSamples.Count > 0)
                    {
                        var deferred = _pendingSamples[_pendingSamples.Count - 1];
                        _pendingSamples.RemoveAt(_pendingSamples.Count - 1);
                        if (follow.HasDeferred)
                        {
                            // The advance was real: posed at its own time, before this
                            // reading, and drawn in the trail.
                            var d = deferred.Sample;
                            _timeline.Add(new LivePoseSample(d.Sequence, epoch, d.RequestStartMs, d.ReceiptMs,
                                                             follow.DeferredPoint, true, follow.DeferredPathPosition));
                            if (printing)
                                AppendObservation(deferred.Machine, deferred.TakenUtc, deferred.SegmentId,
                                                  deferred.Quality, cfg, deferred.Comparison);
                        }
                        // Otherwise it was a queued move's target or a glitch on a later
                        // pass: not posed, not drawn, kept in the logs.
                        else _rejectedAdvances++;
                    }
                    switch (follow.Mode)
                    {
                        case LivePathFollowMode.Pending:
                            _heldSamples++;
                            _lastSampleReason = "reading off the intended path for under "
                                + LivePathFollower.PersistenceSeconds.ToString("0.#", CultureInfo.InvariantCulture)
                                + " s: arm held on the path, reading kept in the raw logs";
                            HoldLocked(measured, machine, snap.TakenUtc, verdict, comparison);
                            break;
                        case LivePathFollowMode.Deferred:
                            _deferredAdvances++;
                            _lastSampleReason = "reading far ahead on the intended path: held until the next reading confirms it";
                            HoldLocked(measured, machine, snap.TakenUtc, verdict, comparison);
                            break;
                        case LivePathFollowMode.Deviating:
                            if (!wasDeviating)
                            {
                                _deviationEpisodes++;
                                // The excursion is animated and drawn from where it began,
                                // and the trail draws no shortcut across it.
                                _havePrevious = false;
                                StartRunLocked(verdict.SegmentId);
                                _breaks.Count(TrailBreakReason.SustainedDeviation);
                                foreach (var held in _pendingSamples)
                                {
                                    _timeline.Add(held.Sample);
                                    if (printing)
                                        AppendObservation(held.Machine, held.TakenUtc, held.SegmentId,
                                                          held.Quality, cfg, held.Comparison);
                                }
                            }
                            break;
                        case LivePathFollowMode.OnPath:
                            if (follow.Relocated)
                            {
                                // Found elsewhere on the path: the arm jumps there rather
                                // than racing along everything in between.
                                _relocations++;
                                epoch = ++_epoch;
                                _havePrevious = false;
                                StartRunLocked(verdict.SegmentId);
                                _breaks.Count(TrailBreakReason.SustainedDeviation);
                            }
                            break;
                    }
                    if (follow.Mode != LivePathFollowMode.Pending && follow.Mode != LivePathFollowMode.Deferred)
                        _pendingSamples.Clear();
                }
                if (follow.Mode == LivePathFollowMode.Pending || follow.Mode == LivePathFollowMode.Deferred)
                {
                    // Nothing is posed from this reading yet; the display keeps following
                    // the path from the last fix. The timeline is NOT invalidated: that hid
                    // the arm for every seam glitch (2026-09-16).
                    Wake();
                    return;
                }
                _timeline.Add(follow.Mode == LivePathFollowMode.OnPath
                    ? new LivePoseSample(sequence, epoch, requestStart, receipt, follow.Point, true, follow.PathPosition)
                    : new LivePoseSample(sequence, epoch, requestStart, receipt, machine, true));

                if (printing)
                    AppendObservation(machine, snap.TakenUtc, verdict.SegmentId, verdict.Quality, cfg, comparison);
                else
                    lock (_lock) _havePrevious = false;
                Wake();
            }
            catch (Exception ex)
            {
                // A display fault must never reach the poll loop.
                _fault ??= FaultText(ex);
            }
        }

        /// <summary>What stopped the display, and the way back: Attach clears the fault.</summary>
        static string FaultText(Exception ex)
            => "live display stopped: " + ex.GetType().Name + ": " + ex.Message
             + ". Set enable to false and back to true to restart it";

        /// <summary>Counts what the poll actually cost, keeping apart the three rates the
        /// 2026-09-09 review found conflated: the coordinate ACQUISITION cadence (from the
        /// acquisition's own stamps, deduplicated by id), the coordinate request's round trip
        /// alone, and the whole poll cycle's elapsed time. A 14 ms axes round trip is not a
        /// 14 ms poll, and neither is the configured interval.</summary>
        /// <remarks>The old version counted CALLBACKS on the local clock.
        /// <see cref="AxisGlitchFilter"/> releases a held sample together with its successor,
        /// so one poll could deliver two callbacks microseconds apart and fabricate a
        /// near-zero poll interval. Repeated acquisition ids are counted instead.</remarks>
        /// <returns>False when this callback repeated an acquisition already counted.</returns>
        bool RecordCadence(MachineSession session, ObjectModelSnapshot snap)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            double fullPoll = session.LastPollRoundTripMs;
            if (double.IsNaN(fullPoll) || fullPoll < 0) fullPoll = 0;
            var stamp = snap.Acquisition;
            long id = snap.AcquisitionId;
            double coordinateRtt = stamp == null ? double.NaN : stamp.RoundTripSeconds * 1000.0;
            DateTime taken = stamp != null && stamp.ResponseReceivedUtc != default(DateTime)
                ? stamp.ResponseReceivedUtc : snap.TakenUtc;
            int requested = session.RequestedPollIntervalMs;
            string? clamp = session.PollClampNote;
            int target = PollCadencePolicy.Decide(session.AnyOwnerFocused,
                PollCadencePolicy.IsActive(snap.Status), session.PollIntervalMs);
            bool fresh;
            lock (_lock)
            {
                _pollRequests++;
                _lastRoundTripMs = fullPoll;
                _roundTripSum += fullPoll;
                if (!double.IsNaN(_lastPollAtMs))
                {
                    _pollIntervalSum += now - _lastPollAtMs;
                    _pollIntervals++;
                }
                _lastPollAtMs = now;
                long workGeneration = session.CompletedPollWorkGeneration;
                if (workGeneration > _lastPollWorkGeneration)
                {
                    Push(_fullPollMs, ref _fullPollN, session.LastCompletedPollWorkMs);
                    _lastPollWorkGeneration = workGeneration;
                }
                // Acquisition ids may restart when the transport reconnects.
                if (_lastAcquisitionConnection != session.ConnectionGeneration)
                {
                    _lastAcquisitionConnection = session.ConnectionGeneration;
                    _lastAcquisitionId = 0;
                    _lastAcquisitionUtc = default(DateTime);
                }
                fresh = id <= 0 || id > _lastAcquisitionId;
                if (!fresh) _duplicateCallbacks++;
                else
                {
                    _acquisitions++;
                    Push(_coordRoundTrips, ref _rttN, coordinateRtt);
                    if (taken != default(DateTime) && _lastAcquisitionUtc != default(DateTime))
                        Push(_coordIntervals, ref _coordN,
                             (Utc(taken) - Utc(_lastAcquisitionUtc)).TotalMilliseconds);
                    if (id > 0) _lastAcquisitionId = id;
                    if (taken != default(DateTime)) _lastAcquisitionUtc = taken;
                }
                _requestedPollMs = requested;
                _pollClampNote = clamp;
                _effectivePollTargetMs = target;
            }
            return fresh;
        }

        /// <summary>Maps a poll onto the shared continuity model. The run identity is the
        /// session's retention run id, so the trail splits exactly where the job log does
        /// and printing the same file twice never joins into one drawn run.</summary>
        TrailObservation ToObservation(ObjectModelSnapshot snap, MachineSession session)
        {
            var machine = snap.MachinePosition;
            return new TrailObservation
            {
                AcquisitionId = snap.AcquisitionId,
                RequestUtc = snap.Acquisition?.RequestStartedUtc ?? default(DateTime),
                ReceivedUtc = snap.TakenUtc,
                ConnectionGeneration = session.ConnectionGeneration,
                RunId = session.RetentionRunId,
                PositionValid = machine.HasValue,
                Position = machine ?? default(Vec3),
                AllHomed = snap.AllHomed,
                Homing = snap.Status == MachineStatus.Busy && !snap.AllHomed,
                Status = snap.Status,
                JobActive = snap.Job != null,
                Layer = snap.Job?.Layer,
                FilePosition = snap.Job?.FilePosition,
                FractionPrinted = snap.Job?.FractionPrinted,
                RequestedSpeedMms = snap.RequestedSpeed,
                TopSpeedMms = snap.TopSpeed,
                RawExtrusion = snap.RawExtrusion,
                RepairNote = snap.PositionRepair,
                // The modern rr_model path issues state, axes and job sequentially, so the
                // requested feed in this snapshot may describe a different instant than its
                // coordinates. That makes a speed mismatch diagnostic, not a discontinuity
                // (plan V3 item 5); gross resets and the ceiling still break unconditionally.
                CompositeSpeedMetadata = snap.CompositeObservation,
            };
        }

        /// <summary>Whether an observation joins the trail. Only while the file executes:
        /// 5.0.0 took Pausing, Paused and Resuming out, because the pause macro's lift and park
        /// (+34 mm Z on 2026-09-23) and the move back are not the print. The run id, and with
        /// it the trail epoch, does not change on a pause, so nothing is cleared either.</summary>
        static bool AppendsTrail(MachineStatus status)
            => status == MachineStatus.Processing || status == MachineStatus.Simulating;

        /// <summary>Sets a reading aside for the follower's decision. Bounded; the oldest
        /// held reading goes first. Called under <see cref="_lock"/>.</summary>
        void HoldLocked(LivePoseSample sample, Vec3 machine, DateTime takenUtc, TrailVerdict verdict,
                        LivePathDeviationResult comparison)
        {
            if (_pendingSamples.Count >= PendingSampleLimit) _pendingSamples.RemoveAt(0);
            _pendingSamples.Add(new HeldReading
            {
                Sample = sample, Machine = machine, TakenUtc = takenUtc,
                SegmentId = verdict.SegmentId, Quality = verdict.Quality, Comparison = comparison,
            });
        }

        /// <summary>Appends one observed position. A reading the follower is still holding
        /// (off the path for under the persistence time) does not get here; it stays in the
        /// raw logs. Never appends an interpolated frame or a point projected onto the
        /// intended path - the trail is measurement, the arm is the estimate.</summary>
        void AppendObservation(Vec3 machine, DateTime taken, long segmentId, TrailQuality quality,
                               LiveRenderConfig cfg, LivePathDeviationResult comparison)
        {
            var shown = ToRhino(machine, cfg.Cartesian);
            float deviation = float.NaN;
            bool ambiguous = false;
            if (cfg.DeviationOn && comparison.Available)
            {
                deviation = (float)comparison.DistanceMm;
                ambiguous = comparison.Ambiguous;
            }

            lock (_lock)
            {
                // Whether a straight line between two preserved observations may be DRAWN.
                // 2026-09-09: this used to compare requested speed times elapsed time with
                // the chord, which is not distance actually travelled - a 3 mm chord over
                // 250 ms at a requested 40 mm/s was rejected even though decelerating into a
                // seam explains it exactly as well, and that is the seam fragmentation the
                // operator reported (row 10a.30). The rule is now about the ACQUISITION GAP
                // alone: a sparse sample leaves the route unknown, so the LINE is withheld
                // while both observations stay exactly where they were reported. This is a
                // drawing gap, counted apart from a continuity break.
                // An unstamped observation is not evidence either way, so the rule stays out
                // of it: the continuity policy already refuses to join what it cannot time.
                if (_havePrevious && taken != default(DateTime) && _previousUtc != default(DateTime))
                {
                    double elapsed = (Utc(taken) - Utc(_previousUtc)).TotalSeconds;
                    double chord = _previousMachine.DistanceTo(machine);
                    double nominalSeconds = Percentile(_coordIntervals, 0.5) / 1000.0;
                    var link = TrailChordPolicy.Evaluate(chord, elapsed, nominalSeconds);
                    if (!link.Drawable)
                    {
                        StartRunLocked(segmentId);
                        _breaks.Count(TrailBreakReason.SparseSample);
                    }
                }
                _havePrevious = true;
                _previousMachine = machine;
                _previousUtc = taken;

                if (_runs.Count == 0) _runs.Add(new TrailRun { SegmentId = segmentId });
                var run = _runs[_runs.Count - 1];
                if (run.Points.Count > 0 && run.Points[run.Points.Count - 1].DistanceTo(shown) <= TrailDecimationMm) return;
                run.Points.Add(shown);
                run.Deviation.Add(deviation);
                run.Ambiguous.Add(ambiguous);
                if (quality == TrailQuality.SuspectPathDisagreement
                    || quality == TrailQuality.SuspectSpeed) run.Suspect++;
                _trailPoints++;
                _observedBounds.Union(shown);
                if (cfg.DeviationOn)
                {
                    if (float.IsNaN(deviation)) _deviationUnavailable++;
                    else
                    {
                        if (ambiguous) _deviationAmbiguous++;
                        if (DeviationColorScale.Saturated(deviation, cfg.DeviationLegendMm)) _deviationSaturated++;
                    }
                }
                EnforceRetentionLocked();
                // The overlay publication latency is measured from the FIRST observation that
                // is still waiting to be drawn, not from the newest one.
                if (double.IsNaN(_pendingSinceMs)) _pendingSinceMs = _clock.Elapsed.TotalMilliseconds;
                _trailDirty = true;
            }
        }

        /// <summary>Bounded retention. 5.0.0: past the budget, older stretches are simplified
        /// one ladder step at a time (<see cref="TrailRetention"/>), the live run's newest
        /// points excepted, so a long print keeps its first layers with a stated error instead
        /// of losing them. Only past the hard cap are the oldest points discarded. Both are
        /// counted and reported through <c>info</c>.</summary>
        void EnforceRetentionLocked()
        {
            if (_trailPoints <= TrailLimit) return;
            var states = new List<TrailRetention.RunState>(_runs.Count);
            for (int i = 0; i < _runs.Count; i++)
            {
                var state = _runs[i].Retention;
                state.Count = _runs[i].Points.Count;
                state.IsLive = i == _runs.Count - 1;
                states.Add(state);
            }
            while (TrailRetention.Next(states, TrailLimit) is TrailRetention.Step step)
            {
                var run = _runs[step.RunIndex];
                int n = step.End - step.Start;
                var source = new List<Vec3>(n);
                for (int k = step.Start; k < step.End; k++)
                {
                    var p = run.Points[k];
                    source.Add(new Vec3(p.X, p.Y, p.Z));
                }
                var kept = Rdp.SimplifyIndices(source, step.ToleranceMm);
                int removed = n - kept.Count;
                if (removed > 0)
                {
                    var points = new List<Point3d>(kept.Count);
                    var deviation = new List<float>(kept.Count);
                    var ambiguous = new List<bool>(kept.Count);
                    foreach (int k in kept)
                    {
                        points.Add(run.Points[step.Start + k]);
                        deviation.Add(run.Deviation[step.Start + k]);
                        ambiguous.Add(run.Ambiguous[step.Start + k]);
                    }
                    run.Points.RemoveRange(step.Start, n); run.Points.InsertRange(step.Start, points);
                    run.Deviation.RemoveRange(step.Start, n); run.Deviation.InsertRange(step.Start, deviation);
                    run.Ambiguous.RemoveRange(step.Start, n); run.Ambiguous.InsertRange(step.Start, ambiguous);
                    run.InvalidatePublished();
                    _compactedPoints += removed;
                    _trailPoints -= removed;
                }
                TrailRetention.Commit(states[step.RunIndex], step, removed);
            }
            long hardCap = (long)TrailLimit * TrailRetention.HardCapFactor;
            while (_trailPoints > hardCap && _runs.Count > 0)
            {
                var oldest = _runs[0];
                int drop = (int)Math.Min(oldest.Points.Count, _trailPoints - hardCap);
                oldest.Points.RemoveRange(0, drop); oldest.Deviation.RemoveRange(0, drop); oldest.Ambiguous.RemoveRange(0, drop);
                TrailRetention.DropFront(oldest.Retention, drop);
                oldest.InvalidatePublished();
                _trailPoints -= drop;
                _prunedPoints += drop;
                if (oldest.Points.Count == 0) { _runs.RemoveAt(0); _breaks.Count(TrailBreakReason.Pruning); }
            }
        }

        /// <summary>What retention is currently keeping, in the operator's words. Called
        /// under <see cref="_lock"/>.</summary>
        string RetentionNoteLocked()
        {
            var inv = CultureInfo.InvariantCulture;
            if (_compactedPoints == 0 && _prunedPoints == 0)
                return _trailPoints.ToString(inv) + " of " + TrailLimit.ToString(inv)
                     + " point budget, nothing compacted or discarded";
            double bound = 0;
            foreach (var run in _runs) bound = Math.Max(bound, run.Retention.MaxBoundMm);
            string note = _compactedPoints.ToString(inv) + " point(s) compacted, the oldest trail within "
                        + bound.ToString("0.##", inv) + " mm of what was observed";
            if (_prunedPoints > 0)
                note += ", " + _prunedPoints.ToString(inv) + " discarded beyond the "
                      + ((long)TrailLimit * TrailRetention.HardCapFactor).ToString(inv) + " point cap";
            return note;
        }

        static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        void StartRunLocked(long segmentId)
        {
            if (_runs.Count == 0 || _runs[_runs.Count - 1].Points.Count > 0)
                _runs.Add(new TrailRun { SegmentId = segmentId });
            else
                _runs[_runs.Count - 1].SegmentId = segmentId;
            _trailDirty = true;
        }

        void ClearTrailLocked()
        {
            _runs.Clear();
            _trailPoints = 0;
            _deviationUnavailable = _deviationAmbiguous = _deviationSaturated = 0;
            _observedBounds = BoundingBox.Empty;
            _prunedPoints = _compactedPoints = 0;
            _pendingSinceMs = double.NaN;
            _trailDirty = true;
        }

        static Point3d ToRhino(Vec3 machine, bool cartesian)
        {
            if (cartesian) return new Point3d(machine.X, machine.Y, machine.Z);
            MachineFrame.ToRhino(machine.X, machine.Y, out double rx, out double ry);
            return new Point3d(rx, ry, machine.Z);
        }

        // ---- outputs the component publishes (any thread) --------------------------------

        /// <summary>The filtered observed trail, one branch per continuous segment. Points are the
        /// positions the machine reported, in the Rhino frame, never interpolated and never
        /// projected onto the intended path.</summary>
        public LiveTrailSnapshot TrailSnapshot()
        {
            lock (_lock)
            {
                var segments = new List<LiveTrailSegment>(_runs.Count);
                foreach (var run in _runs)
                {
                    if (run.Points.Count == 0) continue;
                    segments.Add(new LiveTrailSegment(run.SegmentId, run.Points.ToArray(), run.Suspect));
                }
                return new LiveTrailSnapshot(segments, _trailPoints, _suspectSamples, _droppedSamples,
                                             RetentionNoteLocked(), _breaks.Describe());
            }
        }

        /// <summary>Everything measured about this display, for the component's <c>info</c>
        /// output and the owed Rhino profiling row.</summary>
        public LiveRenderStats Stats(long ghSolves)
        {
            long polls, dropped, suspect, intervals;
            double roundTripSum, lastRoundTrip, intervalSum;
            int points, segments;
            var detail = new LiveDisplayDetail();
            lock (_lock)
            {
                polls = _pollRequests; dropped = _droppedSamples; suspect = _suspectSamples;
                roundTripSum = _roundTripSum; lastRoundTrip = _lastRoundTripMs;
                intervalSum = _pollIntervalSum; intervals = _pollIntervals;
                points = _trailPoints;
                segments = 0;
                foreach (var run in _runs) if (run.Points.Count > 0) segments++;

                detail.RequestedPollIntervalMs = _requestedPollMs;
                detail.EffectivePollTargetMs = _effectivePollTargetMs;
                detail.ClampNote = _pollClampNote;
                detail.CoordinateAcquisitions = _acquisitions;
                detail.DuplicateCallbacks = _duplicateCallbacks;
                detail.CoordinateIntervalMsP50 = Percentile(_coordIntervals, 0.5);
                detail.CoordinateIntervalMsP95 = Percentile(_coordIntervals, 0.95);
                detail.CoordinateRoundTripMsP95 = Percentile(_coordRoundTrips, 0.95);
                detail.FullPollElapsedMsP95 = Percentile(_fullPollMs, 0.95);
                detail.PublicationAgeMsP95 = Percentile(_publicationAge, 0.95);
                detail.OverlayIntervalMsP95 = Percentile(_overlayGap, 0.95);
                detail.ObservationsHeld = _observationsHeld;
                detail.RejectedByEnvelope = _rejectedByEnvelope;
                detail.EnvelopeKnown = _envelopeKnown;
                detail.BreakReasons = _breaks.Describe();
                detail.RetentionNote = RetentionNoteLocked();
                detail.ParsesInWindow = ParsedJobCache.ParseCount - _parseBase;
                detail.CacheHitsInWindow = ParsedJobCache.CacheHits - _cacheHitBase;
                detail.RedrawsInWindow = Interlocked.Read(ref _redraws) - _redrawBase;
                detail.FrameCallbacksInWindow = Interlocked.Read(ref _frameCallbacks) - _callbackBase;
            }
            return new LiveRenderStats(
                pollRequests: polls,
                lastRoundTripMs: lastRoundTrip,
                meanRoundTripMs: LiveRenderStats.Mean(polls, roundTripSum),
                actualPollIntervalMs: LiveRenderStats.Mean(intervals, intervalSum),
                uiCallbackWaitMsP95: Percentile(_uiWait, 0.95),
                frameBuildMsP50: Percentile(_tickCost, 0.5),
                frameBuildMsP95: Percentile(_tickCost, 0.95),
                redraws: Interlocked.Read(ref _redraws),
                frameCallbacks: Interlocked.Read(ref _frameCallbacks),
                ghSolves: ghSolves,
                parseCount: ParsedJobCache.ParseCount,
                parseCacheHits: ParsedJobCache.CacheHits,
                trailPoints: points,
                trailSegments: segments,
                droppedSamples: dropped,
                suspectSamples: suspect,
                frameIntervalMs: TargetInterval(),
                detail: detail);
        }

        /// <summary>Content identity of the lines the current intended path was parsed from,
        /// or null when nothing is parsed. Lets a component tell a <c>gcode</c> wire that
        /// carries the same file from one that carries a different one.</summary>
        public string? IntendedPathLineIdentity => _path?.LineIdentity;

        /// <summary>What the display can say about the intended path right now.</summary>
        public string IntendedPathNote
        {
            get
            {
                var cfg = _config;
                if (cfg == null) return "intended path: not configured yet";
                var request = cfg.Path;
                if (request.Origin == IntendedPathOrigin.Unavailable || request.Origin == IntendedPathOrigin.None)
                    return request.Note;
                var entry = _path;
                if (entry == null) return request.Note + " - parsing";
                if (entry.Error != null) return request.Note + " - rejected: " + entry.Error;
                if (!entry.HasGeometry) return request.Note + " - no moves";
                return request.Note + " - " + entry.MoveCount.ToString(CultureInfo.InvariantCulture)
                     + " moves, " + entry.SampleCount.ToString(CultureInfo.InvariantCulture) + " samples, "
                     + _context.Count.ToString(CultureInfo.InvariantCulture) + " printing and "
                     + _travel.Count.ToString(CultureInfo.InvariantCulture) + " travel run(s)"
                     + (entry.DetailNote == null ? "" : " - " + entry.DetailNote);
            }
        }

        // ---- the pump ------------------------------------------------------------------

        /// <summary>Something changed: make sure a frame gets drawn soon. Restarts a stopped
        /// pump AND re-arms a running one that is still on the slow stale-watch beat.</summary>
        /// <remarks>2026-09-09 (plan V4 item 2): this used to assign the frame interval to
        /// its own field and reprogram the timer only <c>if (!_pumpRunning)</c>. A pump that
        /// had settled onto the 500 ms stale watch was still running, so a machine that had
        /// stood still and then moved animated at 2 Hz while <c>info</c> claimed 30 fps. The
        /// decision now lives in <see cref="FramePumpScheduler"/>, which has a regression for
        /// exactly that sequence.</remarks>
        void Wake()
        {
            if (_disposed) return;
            lock (_lock)
            {
                _settled = 0;
                ApplyPumpLocked(_pump.Wake(TargetInterval()));
            }
        }

        void SetPump(int intervalMs)
        {
            lock (_lock) ApplyPumpLocked(_pump.Set(intervalMs));
        }

        void StopPump()
        {
            lock (_lock) ApplyPumpLocked(_pump.Stop());
        }

        /// <summary>Programs the real timer from a scheduling decision. Called under
        /// <see cref="_lock"/>; a command that changed nothing never touches the timer.</summary>
        void ApplyPumpLocked(PumpCommand command)
        {
            if (!command.Changed) return;
            _pumpRunning = command.Running;
            if (command.Running) _pumpIntervalMs = command.PeriodMs;
            try
            {
                if (command.Running) _timer.Change(command.DueMs, command.PeriodMs);
                else _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            // best effort: Dispose raced this scheduling decision. A disposed timer is
            // already stopped, which is what every command reaching here wants next.
            catch (ObjectDisposedException) { }
        }

        /// <summary>Timer thread: never draws. At most one callback is ever pending; a
        /// stalled UI simply skips ticks and draws the pose for the time it wakes at.</summary>
        void OnTimer(object? state)
        {
            if (_disposed) return;
            if (!_policy.Request()) return;
            _requestedAtMs = _clock.Elapsed.TotalMilliseconds;
            int lifetime = _lifetime;
            try
            {
                RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    if (lifetime != _lifetime) return;
                    UiTick();
                }));
            }
            // best effort: marshalling to the UI thread is the only thing in this try, and
            // Rhino does not document what it throws while the host is closing. The
            // outstanding request is released so the pump is not wedged, and the next tick
            // retries; a display fault reaches the operator through _fault in UiTick.
            catch (Exception)
            {
                _policy.Consume();
            }
        }

        void UiTick()
        {
            if (!_policy.Consume() || _disposed) return;
            double t0 = _clock.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref _frameCallbacks);
            bool dirty = false;
            try
            {
                var cfg = _config;
                if (cfg == null) { StopPump(); return; }
                if (_fault != null)
                {
                    dirty |= PublishFrame(LiveRobotFrame.Hidden(_fault));
                    StopPump();
                    return;
                }
                if (!_visible())
                {
                    // Nothing to draw into; the next sample or solve wakes the pump.
                    StopPump();
                    return;
                }
                if (!_connected)
                {
                    var held = _reconnecting ? _lastPosed : null;
                    dirty |= PublishFrame(held != null
                        ? HeldFrame(held, "pose held: reconnecting")
                        : LiveRobotFrame.Hidden(_session == null ? "" : "live view: disconnected"));
                    dirty |= PublishOverlaysIfDue(t0, cfg, force: true);
                    StopPump();
                    return;
                }

                var frame = _timeline.Evaluate(t0, cfg.Smooth);
                var render = BuildFrame(frame, cfg);
                bool changed = PublishFrame(render);
                changed |= PublishOverlaysIfDue(t0, cfg, force: false);
                dirty |= changed;

                if (!frame.Visible)
                {
                    // Nothing is drawn and nothing about it can change on its own.
                    StopPump();
                }
                else if (frame.Stale)
                {
                    // The ghost is already showing. No further state changes without a new
                    // sample, and a new sample wakes the pump: stop rather than idle-repaint.
                    if (changed) SetPump(TargetInterval()); else StopPump();
                }
                else if (!changed && !frame.Smoothed)
                {
                    // At rest but not yet stale: the only thing left to notice is the pose
                    // going stale, which needs a slow beat, not a frame rate.
                    if (++_settled >= 3) SetPump(StaleWatchIntervalMs);
                }
                else
                {
                    _settled = 0;
                    SetPump(TargetInterval());
                }
            }
            catch (Exception ex)
            {
                // Contain the fault: disable this renderer, keep machine control alive.
                _fault = FaultText(ex);
                // best effort: this IS the containment path. _fault is already set and is
                // what the operator reads; publishing the hidden frame is the tidy-up, and
                // a second fault inside it must not escape into the poll loop.
                try { dirty |= PublishFrame(LiveRobotFrame.Hidden(_fault)); } catch (Exception) { }
                StopPump();
            }
            finally
            {
                // One redraw per UI tick, whatever changed in it. Pose and overlays used to
                // ask for one each, so a tick that moved both repainted Rhino twice.
                if (dirty) Redraw();
                RecordCost(t0, _clock.Elapsed.TotalMilliseconds);
            }
        }

        LiveRobotFrame BuildFrame(LivePoseFrame frame, LiveRenderConfig cfg)
        {
            if (!frame.Visible)
            {
                string why = _lastSampleReason ?? _timeline.InvalidReason
                          ?? (_timeline.Latest.HasValue ? "no valid pose" : "no telemetry yet");
                // The telemetry cannot be posed from - unhomed, halted, no position, or
                // outside the machine's own reported envelope. The last VALID arm is held and
                // drawn as uncertain (the ghost) with the reason on `info`, which is what the
                // operator asked for on 2026-09-09; it is hidden only when no valid pose has
                // ever existed. Nothing is clamped and nothing is substituted.
                var lastValid = _lastPosed;
                if (lastValid != null) return HeldFrame(lastValid, "pose held (uncertain): " + why);
                return LiveRobotFrame.Hidden("arm hidden: " + why);
            }

            var pos = frame.Position;
            bool smoothed = frame.Smoothed;
            string reason = "";
            int armMode; lock (_lock) armMode = _armMode;

            var nozzle = ToRhino(pos, cfg.Cartesian);

            if (cfg.Cartesian)
            {
                var b = new BoundingBox(nozzle, nozzle);
                // Recorded as the last VALID pose too, so a Cartesian machine holds its tool
                // position through an ineligible observation instead of vanishing.
                return _lastPosed = new LiveRobotFrame(Array.Empty<(Mesh, Transform)>(), Array.Empty<(Line[], Transform)>(),
                                                       Array.Empty<Line>(), nozzle, true, smoothed, frame.Stale, reason, b);
            }

            // IK on the interpolated Cartesian position with the observed branch. An
            // intermediate that does not solve falls back to the newest observation.
            var ik = ScaraKinematics.InverseRrf(pos.X, pos.Y, pos.Z, cfg.Limits, armMode);
            if (!ik.Valid && smoothed && _timeline.Latest is LivePoseSample latest)
            {
                pos = latest.Position;
                nozzle = ToRhino(pos, cfg.Cartesian);
                smoothed = false;
                ik = ScaraKinematics.InverseRrf(pos.X, pos.Y, pos.Z, cfg.Limits, armMode);
                reason = "smoothing off: intermediate pose invalid";
            }
            if (!ik.Valid)
            {
                // An unsolvable observation (a reply glitch the filter could not repair, a
                // point outside the annulus) holds the last posed arm instead of blanking
                // it for one frame; the reason reaches `info`.
                var held = _lastPosed;
                if (held != null)
                    return new LiveRobotFrame(held.Links, held.Edges, held.Schematic, nozzle, true, false, frame.Stale,
                                              "pose held: " + ik.Error, held.Bounds);
                return new LiveRobotFrame(Array.Empty<(Mesh, Transform)>(), Array.Empty<(Line[], Transform)>(),
                                          Array.Empty<Line>(), nozzle, false, false, frame.Stale,
                                          "arm hidden: " + ik.Error, new BoundingBox(nozzle, nozzle));
            }

            var model = RobotModel.Default;
            var bounds = new BoundingBox(nozzle, nozzle);
            bounds.Union(new Point3d(0, 0, cfg.BaseZ));
            if (model != null)
            {
                var links = new List<(Mesh, Transform)>();
                var edges = new List<(Line[], Transform)>();
                foreach (var (link, x) in model.PosedLinkTransforms(ik.J1, ik.J3, ik.J2))
                {
                    foreach (var mesh in model.BaseMeshesOf(link))
                    {
                        links.Add((mesh, x));
                        bounds.Union(BaseBounds(mesh, x));
                    }
                    var e = model.BaseFeatureEdgesOf(link, FeatureAngleRad);
                    if (e.Length > 0) edges.Add((e, x));
                }
                return _lastPosed = new LiveRobotFrame(links, edges, Array.Empty<Line>(), nozzle, true, smoothed, frame.Stale,
                                                       reason, bounds);
            }
            var (mex, mey) = ScaraKinematics.Elbow(ik.J1, cfg.Limits.L1);
            MachineFrame.ToRhino(mex, mey, out double ex, out double ey);
            var basePt = new Point3d(0, 0, cfg.BaseZ);
            var carriage = new Point3d(0, 0, pos.Z);
            var elbowPt = new Point3d(ex, ey, pos.Z);
            var schematic = new[] { new Line(basePt, carriage), new Line(carriage, elbowPt), new Line(elbowPt, nozzle) };
            foreach (var l in schematic) { bounds.Union(l.From); bounds.Union(l.To); }
            return _lastPosed = new LiveRobotFrame(Array.Empty<(Mesh, Transform)>(), Array.Empty<(Line[], Transform)>(),
                                                   schematic, nozzle, true, smoothed, frame.Stale, reason, bounds);
        }

        /// <summary>The last posed arm again, drawn exactly as it was, with the reason for
        /// the hold on <c>info</c>.</summary>
        static LiveRobotFrame HeldFrame(LiveRobotFrame last, string reason)
            => new LiveRobotFrame(last.Links, last.Edges, last.Schematic, last.Nozzle, true, false, true,
                                  reason, last.Bounds);

        /// <summary>The posed bounding box of one shared base mesh. <c>Mesh.GetBoundingBox</c>
        /// with a transform walks every vertex, and these assemblies run to roughly 100,000
        /// faces, so calling it per link per frame was pure per-frame cost. The untransformed
        /// box is measured once per mesh instance and only its corners are transformed after
        /// that; the base meshes are shared and never modified, so one measurement holds.</summary>
        BoundingBox BaseBounds(Mesh mesh, Transform x)
        {
            if (!_meshBounds.TryGetValue(mesh, out var box))
            {
                box = mesh.GetBoundingBox(false);
                _meshBounds[mesh] = box;
            }
            if (x.Equals(Transform.Identity)) return box;
            var posed = new BoundingBox(box.Min, box.Max);
            posed.Transform(x);
            return posed;
        }

        readonly Dictionary<Mesh, BoundingBox> _meshBounds = new Dictionary<Mesh, BoundingBox>();

        /// <summary>Publishes the pose when it actually differs. Returns whether a redraw is
        /// owed; the redraw itself happens once, at the end of the tick.</summary>
        bool PublishFrame(LiveRobotFrame render)
        {
            var prev = _published;
            if (prev != null && SameFrame(prev, render)) return false;
            _published = render;
            _conduit.Publish(render);
            return true;
        }

        /// <summary>Frame equality by GEOMETRY and appearance only. The old comparison
        /// included the on-screen label, whose sample-age text changed every tick, so a
        /// motionless machine repainted Rhino at the frame rate for no visible reason.</summary>
        static bool SameFrame(LiveRobotFrame a, LiveRobotFrame b)
        {
            if (a.ArmVisible != b.ArmVisible || a.Stale != b.Stale) return false;
            if (a.Nozzle.HasValue != b.Nozzle.HasValue) return false;
            if (a.Nozzle.HasValue && a.Nozzle.Value.DistanceTo(b.Nozzle!.Value) > 1e-9) return false;
            if (a.Links.Count != b.Links.Count) return false;
            for (int i = 0; i < a.Links.Count; i++)
                if (!ReferenceEquals(a.Links[i].mesh, b.Links[i].mesh) || !a.Links[i].x.Equals(b.Links[i].x)) return false;
            if (a.Schematic.Count != b.Schematic.Count) return false;
            for (int i = 0; i < a.Schematic.Count; i++)
                if (a.Schematic[i].From.DistanceTo(b.Schematic[i].From) > 1e-9
                    || a.Schematic[i].To.DistanceTo(b.Schematic[i].To) > 1e-9) return false;
            return true;
        }

        /// <summary>Trail and intended path, at most every <see cref="OverlayMinIntervalMs"/>
        /// while changing: bounded overlay work, never in the frame path proper. Returns
        /// whether a redraw is owed.</summary>
        /// <remarks>2026-09-09 (plan V4 item 3): this used to copy every point of every run
        /// and recolour all of them under the poll-shared lock, twice a second. A run now
        /// keeps the arrays it was last published with and only the new tail is written into
        /// a grown copy, the intended-path bounds are measured once when the path is adopted,
        /// and the observed bounds are maintained as points arrive. That is what makes a
        /// 100 ms publication interval affordable instead of 500 ms.</remarks>
        bool PublishOverlaysIfDue(double now, LiveRenderConfig cfg, bool force)
        {
            bool dirty;
            lock (_lock) dirty = _trailDirty;
            if (!force && !dirty) return false;
            if (!force && now - _lastOverlayMs < OverlayMinIntervalMs) { SetPump(TargetInterval()); return false; }
            double gap = double.IsNegativeInfinity(_lastOverlayMs) ? double.NaN : now - _lastOverlayMs;
            _lastOverlayMs = now;

            LiveTrailState state;
            var context = _context;
            var travel = _travel;
            lock (_lock)
            {
                _trailDirty = false;
                Push(_overlayGap, ref _overlayN, gap);
                if (!double.IsNaN(_pendingSinceMs))
                {
                    Push(_publicationAge, ref _pubN, now - _pendingSinceMs);
                    _pendingSinceMs = double.NaN;
                }
                bool coloured = cfg.DeviationOn && _deviation != null;
                var runs = new List<Point3d[]>(_runs.Count);
                var colors = new List<Color[]?>(_runs.Count);
                var ambiguous = new List<bool[]?>(_runs.Count);
                var isolated = new List<Point3d>();
                foreach (var run in _runs)
                {
                    int n = run.Points.Count;
                    if (n == 0) continue;
                    if (n == 1)
                    {
                        // One observation on its own: shown where it was reported, with no
                        // line to a neighbour it may not have travelled to in a straight move.
                        isolated.Add(run.Points[0]);
                        continue;
                    }
                    bool reusable = run.PublishedPoints != null && run.PublishedCount <= n
                                 && run.PublishedColoured == coloured
                                 && (!coloured || run.PublishedLegendMm.Equals(cfg.DeviationLegendMm));
                    if (reusable && run.PublishedCount == n)
                    {
                        runs.Add(run.PublishedPoints!);
                        colors.Add(run.PublishedColors);
                        ambiguous.Add(run.PublishedAmbiguous);
                        continue;
                    }
                    int from = reusable ? run.PublishedCount : 0;
                    var pts = new Point3d[n];
                    if (from > 0) Array.Copy(run.PublishedPoints!, pts, from);
                    for (int i = from; i < n; i++) pts[i] = run.Points[i];
                    Color[]? c = null;
                    bool[]? amb = null;
                    if (coloured)
                    {
                        c = new Color[n];
                        amb = new bool[n];
                        if (from > 0 && run.PublishedColors != null && run.PublishedAmbiguous != null)
                        {
                            Array.Copy(run.PublishedColors, c, from);
                            Array.Copy(run.PublishedAmbiguous, amb, from);
                        }
                        for (int i = from; i < n; i++)
                        {
                            var (r, g, b) = DeviationColorScale.ForDistance(run.Deviation[i], cfg.DeviationLegendMm);
                            c[i] = Color.FromArgb(r, g, b);
                            amb[i] = run.Ambiguous[i];
                        }
                    }
                    run.PublishedPoints = pts;
                    run.PublishedColors = c;
                    run.PublishedAmbiguous = amb;
                    run.PublishedCount = n;
                    run.PublishedColoured = coloured;
                    run.PublishedLegendMm = cfg.DeviationLegendMm;
                    runs.Add(pts); colors.Add(c); ambiguous.Add(amb);
                }
                var bounds = BoundingBox.Empty;
                if (_observedBounds.IsValid) bounds.Union(_observedBounds);
                if (_contextBounds.IsValid) bounds.Union(_contextBounds);
                foreach (var p in isolated) bounds.Union(p);
                string note = "";
                if (cfg.DeviationOn)
                {
                    note = _deviation == null
                        ? "deviation: no intended path for this job"
                        : "nearest path distance from the controller reported position, not independent metrology. "
                          + _deviationAmbiguous.ToString(CultureInfo.InvariantCulture) + " ambiguous, "
                          + _deviationUnavailable.ToString(CultureInfo.InvariantCulture) + " unavailable, "
                          + _deviationSaturated.ToString(CultureInfo.InvariantCulture) + " above range";
                }
                state = new LiveTrailState(runs, colors, ambiguous, isolated, context,
                                           coloured, cfg.DeviationLegendMm, note, bounds, travel);
            }
            _conduit.PublishTrail(state);
            return true;
        }

        void Redraw()
        {
            Interlocked.Increment(ref _redraws);
            var doc = RhinoDoc.ActiveDoc;
            doc?.Views.Redraw();   // Rhino viewports only; never the Grasshopper canvas
        }

        // ---- cost accounting and degradation (UI thread) --------------------------------

        void RecordCost(double t0, double t1)
        {
            int i = _tickN % CostWindow;
            _tickCost[i] = t1 - t0;
            _tickGap[i] = double.IsNaN(_lastTickMs) ? double.NaN : t0 - _lastTickMs;
            _uiWait[i] = double.IsNaN(_requestedAtMs) ? double.NaN : Math.Max(0, t0 - _requestedAtMs);
            _lastTickMs = t0;
            _tickN++;
            if (_tickN < CostWindow) return;
            double p95 = Percentile(_tickCost, 0.95);
            if (p95 > HeavyDegradeAtMs) { _degraded = "10 fps (frame work p95 above 25 ms)"; _lowCostSinceMs = double.NaN; }
            else if (p95 > DegradeAtMs) { if (_degraded.Length == 0 || _degraded.StartsWith("10", StringComparison.Ordinal)) _degraded = "15 fps (frame work p95 above 12 ms)"; _lowCostSinceMs = double.NaN; }
            else if (_degraded.Length > 0)
            {
                // recover slowly: two seconds of headroom before stepping back up
                if (double.IsNaN(_lowCostSinceMs)) _lowCostSinceMs = t1;
                else if (t1 - _lowCostSinceMs > 2000 && p95 < RecoverBelowMs) { _degraded = ""; _lowCostSinceMs = double.NaN; }
            }
        }

        int TargetInterval()
            => _degraded.StartsWith("10", StringComparison.Ordinal) ? HeavilyDegradedIntervalMs
             : _degraded.StartsWith("15", StringComparison.Ordinal) ? DegradedIntervalMs
             : FrameIntervalMs;

        static double Percentile(double[] values, double p)
        {
            var sorted = new List<double>(values.Length);
            foreach (double v in values) if (!double.IsNaN(v)) sorted.Add(v);
            if (sorted.Count == 0) return 0;
            sorted.Sort();
            int k = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (k < 0) k = 0;
            if (k >= sorted.Count) k = sorted.Count - 1;
            return sorted[k];
        }

        /// <summary>What the arm is currently posed from. Called under <see cref="_lock"/>.</summary>
        string ArmSourceTextLocked()
        {
            if (_deviation == null) return "measured position, no intended path to follow";
            switch (_follower.Mode)
            {
                case LivePathFollowMode.OnPath:
                    return "following the intended path, progress from the reported position";
                case LivePathFollowMode.Pending:
                    return "held on the intended path, the reported position is off it";
                case LivePathFollowMode.Deferred:
                    return "held on the intended path, a reading further ahead awaits the next one";
                case LivePathFollowMode.Deviating:
                    return "measured position, off the intended path for "
                         + LivePathFollower.PersistenceSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s or more";
                default:
                    return "measured position, the file is not executing";
            }
        }

        /// <summary>Human-readable state for the component info output and the console.
        /// Wall-clock measurements, not deterministic facts: reported, never promised.</summary>
        public string StatusText
        {
            get
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                var cfg = _config;
                if (_fault != null) return _fault;
                sb.Append("live display: ").Append(!_connected ? "disconnected" : _pumpRunning ? "animating" : "at rest");
                sb.Append(", target ").Append(TargetInterval() == FrameIntervalMs ? "30 fps" : _degraded);
                if (_tickN >= CostWindow)
                    sb.Append(", frame work p50/p95 ").Append(Percentile(_tickCost, 0.5).ToString("0.0", inv))
                      .Append('/').Append(Percentile(_tickCost, 0.95).ToString("0.0", inv)).Append(" ms, frame interval p95 ")
                      .Append(Percentile(_tickGap, 0.95).ToString("0", inv)).Append(" ms");
                sb.Append('\n');
                double p95 = _timeline.IntervalP95Ms;
                sb.Append("telemetry: interval p95 ").Append(p95.ToString("0", inv)).Append(" ms, delay ")
                  .Append(_timeline.AutoDelayMs.ToString("0", inv)).Append(" ms, stale after ")
                  .Append(_timeline.StaleThresholdMs.ToString("0", inv)).Append(" ms");
                var latest = _timeline.Latest;
                if (latest.HasValue)
                    sb.Append(", sample age ").Append((_clock.Elapsed.TotalMilliseconds - latest.Value.RequestStartMs).ToString("0", inv)).Append(" ms");
                // A stale pose is held and drawn unchanged (2026-09-09: the operator wants
                // the arm persistent); this line is where staleness is reported.
                var published = _published;
                if (published != null && published.Stale) sb.Append(" - STALE (last pose held, drawing unchanged)");
                if (published != null && published.Reason.Length > 0) sb.Append(" - ").Append(published.Reason);
                sb.Append('\n');
                lock (_lock)
                {
                    // What the arm is posed from, what the display refused to pose from,
                    // and why runs actually ended.
                    sb.Append("arm: ").Append(ArmSourceTextLocked())
                      .Append("; ").Append(_heldSamples.ToString(inv)).Append(" reading(s) held off the path, ")
                      .Append(_deferredAdvances.ToString(inv)).Append(" advance(s) deferred of which ")
                      .Append(_rejectedAdvances.ToString(inv)).Append(" dropped, ")
                      .Append(_deviationEpisodes.ToString(inv)).Append(" sustained deviation(s), ")
                      .Append(_relocations.ToString(inv)).Append(" relocation(s); off-path tolerance ")
                      .Append(LivePathFollower.ToleranceMm.ToString("0.#", inv)).Append(" mm for ")
                      .Append(LivePathFollower.PersistenceSeconds.ToString("0.#", inv))
                      .Append(" s, print and travel alike; the trail and the logs keep every reading\n");
                    // Both are counts of preserved observations, never of deleted ones.
                    sb.Append("validity: ").Append(_observationsHeld.ToString(inv))
                      .Append(" observation(s) held, ").Append(_rejectedByEnvelope.ToString(inv))
                      .Append(" outside the machine's reported envelope, envelope ")
                      .Append(_envelopeKnown ? "known" : "UNKNOWN - not checked")
                      .Append("\nbreaks: ").Append(_breaks.Describe())
                      .Append("\nretention: ").Append(RetentionNoteLocked()).Append('\n');
                }
                var glitches = _session?.GlitchFilter;
                if (glitches != null && glitches.Candidates > 0)
                    sb.Append("reply glitches: ").Append(glitches.Repaired.ToString(inv))
                      .Append(" repaired from the reply's userPosition, ").Append(glitches.Suspect.ToString(inv))
                      .Append(" left as reported, ").Append(glitches.Unresolved.ToString(inv)).Append(" unresolved\n");
                sb.Append("smoothing: ").Append(cfg != null && cfg.Smooth ? "on (delayed interpolation along the intended path between readings, about two poll intervals behind, an estimate rather than a measurement, never part of the trail)" : "off (latest observation)");
                if (cfg != null && cfg.DeviationOn)
                {
                    sb.Append("\ndeviation overlay: legend 0-").Append(cfg.DeviationLegendMm.ToString("0.#", inv)).Append(" mm");
                    lock (_lock)
                        sb.Append(_deviation == null ? " - no intended path" : ", " + _deviationAmbiguous.ToString(inv) + " ambiguous / "
                            + _deviationUnavailable.ToString(inv) + " unavailable / " + _deviationSaturated.ToString(inv) + " above range");
                }
                return sb.ToString();
            }
        }

        // ---- shutdown -------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _lifetime);
            var session = _session;
            _session = null;
            if (session != null)
            {
                session.PollCompleted -= OnPollCompleted;
                session.Changed -= OnSessionChanged;
            }
            // best effort: a second Dispose, or one racing ApplyPumpLocked - the timer is
            // stopped either way and is disposed on the next line.
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { }
            _timer.Dispose();
            _policy.Dispose();
            _conduit.Enabled = false;
            _conduit.ReleaseResources();
            _timeline.Clear();
            _meshBounds.Clear();
            lock (_lock) ClearTrailLocked();
            Redraw();
        }
    }
}
