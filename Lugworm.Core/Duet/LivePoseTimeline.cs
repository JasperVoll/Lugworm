using System;
using System.Collections.Generic;
using System.Threading;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>One observed telemetry sample, copied out of a poll snapshot for display.
    /// Immutable; nothing here aliases the session's mutable arrays.</summary>
    /// <remarks>Times are monotonic milliseconds from the caller's clock (a Stopwatch),
    /// never wall-clock. <see cref="RequestStartMs"/> bounds the age conservatively: the
    /// machine position is at least that old, whatever the receipt time says.</remarks>
    public readonly struct LivePoseSample
    {
        /// <summary>Poll cycle sequence within the epoch; must strictly increase.</summary>
        public long Sequence { get; }
        /// <summary>Connection / identity / frame epoch. A new epoch discards the history:
        /// samples from two epochs are never interpolated across.</summary>
        public long Epoch { get; }
        /// <summary>Monotonic time the poll request started, ms.</summary>
        public double RequestStartMs { get; }
        /// <summary>Monotonic time the snapshot was received, ms.</summary>
        public double ReceiptMs { get; }
        /// <summary>Firmware-reported Cartesian position, machine frame, mm.</summary>
        public Vec3 Position { get; }
        /// <summary>False when the pose must not be shown (unhomed, unknown branch,
        /// halted, untrusted ...). An invalid sample flushes the interpolation history.</summary>
        public bool Valid { get; }
        /// <summary>Progress along the intended path, mm of arc length, when the sample is
        /// posed ON the path (2026-09-16); NaN for a measured position. Two consecutive
        /// anchored samples are interpolated along the path's own route, never along the
        /// chord between them.</summary>
        public double PathPosition { get; }

        public LivePoseSample(long sequence, long epoch, double requestStartMs, double receiptMs, Vec3 position, bool valid)
            : this(sequence, epoch, requestStartMs, receiptMs, position, valid, double.NaN) { }

        public LivePoseSample(long sequence, long epoch, double requestStartMs, double receiptMs, Vec3 position,
                              bool valid, double pathPosition)
        {
            Sequence = sequence;
            Epoch = epoch;
            RequestStartMs = requestStartMs;
            ReceiptMs = receiptMs;
            Position = position;
            Valid = valid;
            PathPosition = pathPosition;
        }

        /// <summary>True when the sample is anchored to the intended path.</summary>
        public bool OnPath => !double.IsNaN(PathPosition);

        internal bool Finite
            => Num.Finite(RequestStartMs) && Num.Finite(ReceiptMs) && Num.Finite(Position);
    }

    /// <summary>What the display should draw for one frame. An estimate, never a
    /// measurement: <see cref="Smoothed"/> says the position was interpolated between two
    /// observations with <see cref="RenderDelayMs"/> of deliberate delay.</summary>
    public readonly struct LivePoseFrame
    {
        public Vec3 Position { get; }
        /// <summary>True when interpolated between two compatible observed samples.</summary>
        public bool Smoothed { get; }
        /// <summary>True when the newest sample is older than the stale threshold.</summary>
        public bool Stale { get; }
        /// <summary>False when no pose may be drawn at all (no samples, or the newest
        /// observation is invalid).</summary>
        public bool Visible { get; }
        /// <summary>Conservative age of the newest observation, ms (from request start).</summary>
        public double AgeMs { get; }
        /// <summary>The delay the smoothing applied, ms; 0 when unsmoothed.</summary>
        public double RenderDelayMs { get; }
        /// <summary>Progress along the intended path the frame was posed at, mm; NaN when
        /// the frame comes from a measured position.</summary>
        public double PathPosition { get; }

        public LivePoseFrame(Vec3 position, bool smoothed, bool stale, bool visible, double ageMs, double renderDelayMs)
            : this(position, smoothed, stale, visible, ageMs, renderDelayMs, double.NaN) { }

        public LivePoseFrame(Vec3 position, bool smoothed, bool stale, bool visible, double ageMs,
                             double renderDelayMs, double pathPosition)
        {
            Position = position; Smoothed = smoothed; Stale = stale; Visible = visible;
            AgeMs = ageMs; RenderDelayMs = renderDelayMs; PathPosition = pathPosition;
        }

        /// <summary>True when the frame lies on the intended path.</summary>
        public bool OnPath => !double.IsNaN(PathPosition);

        public static LivePoseFrame Hidden(double ageMs)
            => new LivePoseFrame(new Vec3(0, 0, 0), false, true, false, ageMs, 0);
    }

    /// <summary>Bounded history of observed poses with delayed interpolation. Pure: the
    /// caller supplies every time; nothing here reads a clock.</summary>
    /// <remarks>
    /// <para>The display shows the position as of <c>now - delay</c>, interpolated between
    /// the two observed samples that bracket that instant. It never extrapolates past the
    /// newest sample, never bridges a gap longer than one second between MEASURED samples,
    /// never crosses an epoch, and holds the newest observation (unsmoothed) whenever no
    /// bracket exists.</para>
    /// <para>Since 2026-09-16 a sample can be anchored to the intended path
    /// (<see cref="LivePoseSample.PathPosition"/>). Between two anchored samples the frame
    /// is interpolated in PROGRESS and mapped back through <see cref="Path"/>, so the arm
    /// takes the file's own route - a Z hop goes up, across and down instead of along the
    /// chord one poll saw of it - and such a pair may be up to three seconds apart: the
    /// route between them is known, only the timing is presumed uniform. A pair with a
    /// measured sample on either side is still a straight chord under the one-second bound.</para>
    /// <para>Delay defaults to twice the recent interval p95 + 20 ms clamped to 100-800 ms
    /// (2026-09-16: one interval left a single held reading visible as a jump of most of a
    /// chord the moment the next reading arrived; two intervals cover one held reading
    /// completely). The stale threshold is <c>max(3000, 2 * p95)</c> capped at 10000 ms - the 3000 ms FLOOR
    /// is what does the work at telemetry cadence, since 2 * p95 on a 250-300 ms poll is
    /// only about 600 ms (2026-09-09: the old floor of 500 ms turned a single late 300 ms
    /// poll into a ghost for one frame, which the operator saw as the arm flashing; raising
    /// the floor to 3000 ms fixed it, and the multiplier stayed at 2). Both are display
    /// tuning, not safety facts: safety gates, calibration and logs consume observed
    /// telemetry only and never see a frame from here (ARCHI §15).</para>
    /// <para><see cref="Invalidate"/> is the third way a pose stops being drawn, next to an
    /// invalid sample and a new epoch: the newest observation exists and is finite but may
    /// not be posed (2026-09-09, the below-envelope approach coordinates). It withholds the
    /// pose and cuts interpolation without pretending the connection restarted.</para>
    /// </remarks>
    public sealed class LivePoseTimeline
    {
        public const int Capacity = 64;
        const int IntervalWindow = 16;
        const double MinDelayMs = 100, MaxDelayMs = 800, DelayMarginMs = 20;
        /// <summary>How many recent poll intervals the delay covers.</summary>
        const double DelayIntervals = 2;
        const double MinStaleMs = 3000, MaxStaleMs = 10000;
        /// <summary>Two consecutive MEASURED samples farther apart than this are never
        /// interpolated across: the display holds the earlier one and then jumps. Separate
        /// from the stale threshold, which a single long gap would otherwise widen.</summary>
        const double MaxBridgeMs = 1000;
        /// <summary>The same bound for two samples anchored to the intended path. The route
        /// between them is the file's, so a held reading or two in between (the seam
        /// excursions of 2026-09-16) does not force a hold-and-jump.</summary>
        public const double MaxPathBridgeMs = 3000;

        readonly object _lock = new object();
        readonly List<LivePoseSample> _samples = new List<LivePoseSample>(Capacity);
        readonly List<double> _intervals = new List<double>(IntervalWindow);
        long _epoch = long.MinValue;
        LivePoseSample? _latest;
        bool _invalidated;
        string? _invalidReason;
        LivePathDeviation? _path;

        /// <summary>The intended path that anchored samples refer to. Immutable once built,
        /// so the reference can be swapped whenever the job changes; anchored samples of a
        /// previous path are never mixed with it because the caller starts a new epoch.</summary>
        public LivePathDeviation? Path
        {
            get { lock (_lock) return _path; }
            set { lock (_lock) _path = value; }
        }

        /// <summary>Number of valid samples retained in the current epoch.</summary>
        public int Count { get { lock (_lock) return _samples.Count; } }

        /// <summary>The newest accepted observation (valid or not), null before the first.</summary>
        public LivePoseSample? Latest { get { lock (_lock) return _latest; } }

        /// <summary>Current epoch, or <see cref="long.MinValue"/> before the first sample.</summary>
        public long Epoch { get { lock (_lock) return _epoch; } }

        /// <summary>True while the retained history has been discarded but the newest
        /// observation is still remembered: <see cref="Evaluate"/> hides the pose until a
        /// fresh valid sample arrives.</summary>
        public bool Invalidated { get { lock (_lock) return _invalidated; } }

        /// <summary>Why the timeline was invalidated, for the info output; null when it was
        /// invalidated without a reason or is not invalidated at all.</summary>
        public string? InvalidReason { get { lock (_lock) return _invalidReason; } }

        /// <summary>Accepts one observation. Returns false for non-finite data, a sequence
        /// that does not advance, or a receipt time that goes backwards within the epoch.
        /// A different epoch clears the history first; an invalid sample flushes it.</summary>
        public bool Add(LivePoseSample sample)
        {
            if (!sample.Finite) return false;
            if (sample.ReceiptMs < sample.RequestStartMs) return false;
            lock (_lock)
            {
                if (sample.Epoch != _epoch)
                {
                    _epoch = sample.Epoch;
                    _samples.Clear();
                    _intervals.Clear();
                    _latest = null;
                    _invalidated = false;
                    _invalidReason = null;
                }
                if (_latest.HasValue)
                {
                    var last = _latest.Value;
                    if (sample.Sequence <= last.Sequence) return false;
                    if (sample.ReceiptMs < last.ReceiptMs) return false;
                }
                if (!sample.Valid)
                {
                    // Homing, E-STOP, unknown branch: flush now; the next frame hides the
                    // pose, and interpolation resumes only from fresh valid samples.
                    _samples.Clear();
                    _intervals.Clear();
                    _latest = sample;
                    _invalidated = true;
                    _invalidReason = null;
                    return true;
                }
                if (_samples.Count > 0)
                {
                    double interval = sample.ReceiptMs - _samples[_samples.Count - 1].ReceiptMs;
                    if (_intervals.Count == IntervalWindow) _intervals.RemoveAt(0);
                    _intervals.Add(interval);
                }
                if (_samples.Count == Capacity) _samples.RemoveAt(0);
                _samples.Add(sample);
                _latest = sample;
                _invalidated = false;
                _invalidReason = null;
                return true;
            }
        }

        /// <summary>Discards everything (disconnect, identity or frame change).</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _samples.Clear();
                _intervals.Clear();
                _latest = null;
                _invalidated = false;
                _invalidReason = null;
                _epoch = long.MinValue;
                // _path is deliberately kept: it belongs to the job, not to the connection.
            }
        }

        /// <summary>Withholds the pose because the newest observation may not be posed from,
        /// without forgetting it and without restarting the connection.</summary>
        /// <remarks>
        /// <para>The 2026-09-09 approach defect is the reason this is separate from
        /// <see cref="Clear"/>. Those coordinates were finite, homed and marked Valid; the
        /// firmware's own envelope is what refused them. The display must stop posing the
        /// arm and must not interpolate a path THROUGH a refused observation, but the
        /// connection did not restart and the identity did not change - so
        /// <see cref="Latest"/> and <see cref="Epoch"/> survive, and the caller can still
        /// report what it last observed and why it is not drawing it. <see cref="Clear"/>
        /// is the disconnect tool and drops both.</para>
        /// <para>Retained samples and the interval window go, so the next valid sample
        /// resumes UNSMOOTHED: an interpolation whose other end sits on the far side of the
        /// invalidation would draw a motion that was never observed.</para>
        /// </remarks>
        /// <param name="reason">Operator-language reason for the info output; optional.</param>
        public void Invalidate(string? reason = null)
        {
            lock (_lock)
            {
                _samples.Clear();
                _intervals.Clear();
                _invalidated = true;
                _invalidReason = reason;
            }
        }

        /// <summary>Recent poll interval p95, ms; 0 before two samples exist.</summary>
        public double IntervalP95Ms { get { lock (_lock) return P95(); } }

        /// <summary>The delay the automatic policy would apply now, ms.</summary>
        public double AutoDelayMs { get { lock (_lock) return AutoDelay(); } }

        /// <summary>Age beyond which the display stops smoothing and shows STALE, ms.</summary>
        public double StaleThresholdMs { get { lock (_lock) return StaleThreshold(); } }

        /// <summary>Evaluates the display pose for <paramref name="nowMs"/>.</summary>
        /// <param name="smooth">False = unsmoothed latest observation.</param>
        /// <param name="delayMs">Explicit render delay; NaN or non-positive = automatic.</param>
        public LivePoseFrame Evaluate(double nowMs, bool smooth = true, double delayMs = double.NaN)
        {
            lock (_lock)
            {
                if (!_latest.HasValue) return LivePoseFrame.Hidden(double.PositiveInfinity);
                var newest = _latest.Value;
                double age = nowMs - newest.RequestStartMs;
                if (_invalidated || _samples.Count == 0) return LivePoseFrame.Hidden(age);

                double threshold = StaleThreshold();
                bool stale = age > threshold;
                var latest = _samples[_samples.Count - 1];
                if (!smooth || stale || _samples.Count < 2)
                    return new LivePoseFrame(latest.Position, false, stale, true, age, 0, latest.PathPosition);

                double delay = double.IsNaN(delayMs) || delayMs <= 0 ? AutoDelay() : delayMs;
                double t = nowMs - delay;
                if (t >= latest.ReceiptMs)
                    return new LivePoseFrame(latest.Position, false, stale, true, age, delay, latest.PathPosition);   // never past the newest
                var oldest = _samples[0];
                if (t <= oldest.ReceiptMs)
                    return new LivePoseFrame(oldest.Position, false, stale, true, age, delay, oldest.PathPosition);

                // Bracket: the last sample at or before t and the first after it.
                int hi = 1;
                while (hi < _samples.Count && _samples[hi].ReceiptMs < t) hi++;
                var a = _samples[hi - 1];
                var b = _samples[hi];
                double span = b.ReceiptMs - a.ReceiptMs;
                bool alongPath = _path != null && a.OnPath && b.OnPath;
                if (span <= 0 || span > (alongPath ? MaxPathBridgeMs : MaxBridgeMs))
                    return new LivePoseFrame(latest.Position, false, stale, true, age, delay, latest.PathPosition);   // gap: hold, do not invent
                double u = (t - a.ReceiptMs) / span;
                if (alongPath)
                {
                    // Progress is what is interpolated; the position is the file's own route
                    // at that progress. Backwards progress cannot be commanded, so a pair
                    // that reads that way (a relocation the caller did not epoch) is held.
                    if (b.PathPosition < a.PathPosition)
                        return new LivePoseFrame(a.Position, false, stale, true, age, delay, a.PathPosition);
                    double s = a.PathPosition + (b.PathPosition - a.PathPosition) * u;
                    return new LivePoseFrame(_path!.PointAt(s), true, stale, true, age, delay, s);
                }
                var p = a.Position + (b.Position - a.Position) * u;
                return new LivePoseFrame(p, true, stale, true, age, delay);
            }
        }

        double P95()
        {
            int n = _intervals.Count;
            if (n == 0) return 0;
            var sorted = new double[n];
            _intervals.CopyTo(sorted);
            Array.Sort(sorted);
            int k = (int)Math.Ceiling(0.95 * n) - 1;
            if (k < 0) k = 0;
            if (k >= n) k = n - 1;
            return sorted[k];
        }

        double AutoDelay()
        {
            double d = DelayIntervals * P95() + DelayMarginMs;
            return d < MinDelayMs ? MinDelayMs : d > MaxDelayMs ? MaxDelayMs : d;
        }

        double StaleThreshold()
        {
            double s = Math.Max(MinStaleMs, 2 * P95());
            return s > MaxStaleMs ? MaxStaleMs : s;
        }
    }

    /// <summary>One pending display callback, ever. A timer or a telemetry event calls
    /// <see cref="Request"/>; only the first caller since the last <see cref="Consume"/>
    /// gets true and must post the UI callback, which calls <see cref="Consume"/>. There is
    /// no queue and no catch-up: a late tick draws the pose for the new time and skips the
    /// missed frames.</summary>
    public sealed class LiveDisplayPolicy : IDisposable
    {
        const int Idle = 0, Pending = 1, Disposed = 2;
        int _state = Idle;

        /// <summary>True exactly once per pending cycle; the caller then posts the callback.</summary>
        public bool Request() => Interlocked.CompareExchange(ref _state, Pending, Idle) == Idle;

        /// <summary>Called by the posted callback. True when a request was pending; a
        /// callback that finds nothing pending (superseded, or after dispose) does nothing.</summary>
        public bool Consume() => Interlocked.CompareExchange(ref _state, Idle, Pending) == Pending;

        public bool IsPending => Volatile.Read(ref _state) == Pending;
        public bool IsDisposed => Volatile.Read(ref _state) == Disposed;

        public void Dispose() => Interlocked.Exchange(ref _state, Disposed);
    }
}
