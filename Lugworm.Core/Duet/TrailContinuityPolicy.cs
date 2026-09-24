using System;
using System.Globalization;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>What the machine was doing when an observation was taken.</summary>
    public enum TrailClass
    {
        /// <summary>Continuity is not established; never joined to a neighbour.</summary>
        Unknown,
        /// <summary>A printing move: a job is running and the extruder advanced.</summary>
        Printing,
        /// <summary>A job is running but nothing was deposited between the samples.</summary>
        Travel,
        /// <summary>Approach or footer motion around the printed layers - the pre-print
        /// descent and the lift/G28/M84 tail, which must never join the print path.</summary>
        StartupOrFooter,
        /// <summary>Paused, pausing or resuming.</summary>
        Paused,
        /// <summary>Operator motion while a job is open but not executing.</summary>
        Jogged,
    }

    /// <summary>How much an observation can be trusted. Never a reason to delete or
    /// change it: every raw value is retained whatever this says.</summary>
    public enum TrailQuality
    {
        Valid,
        /// <summary>Farther from the intended path at its own elevation than the
        /// diagnostic trigger allows. A trigger, not a verdict: the sample is preserved
        /// exactly as reported and is never projected onto the path or smoothed away.</summary>
        SuspectPathDisagreement,
        /// <summary>The distance to the previous observation over the ACTUAL elapsed time
        /// implies a speed the requested feed rate cannot explain.</summary>
        SuspectSpeed,
        /// <summary>No usable position (missing, non-numeric or duplicated axes).</summary>
        Invalid,
        /// <summary>One axis of the firmware's <c>machinePosition</c> was inconsistent with
        /// its neighbours and with the same reply's <c>userPosition</c>; the position was
        /// repaired from that witness (or, when no witness agreed, left as reported). The
        /// reason names the axis and the raw value (see <c>AxisGlitchFilter</c>), and the
        /// raw reading survives on the unmodified snapshot the repair was derived from.</summary>
        MachinePositionGlitch,
        /// <summary>Continuity with the previous observation is broken, so the two must
        /// not be joined - a new run or connection, homing, a coordinate reset, a time
        /// gap, a file-progress rollback or an out-of-order acquisition.</summary>
        Discontinuity,
    }

    /// <summary>One validated observation offered to the continuity policy. Everything the
    /// policy needs is explicit: it never reaches back into a snapshot, a session or a
    /// viewport, so the same rules run in the recorder, in the live trail and in a replay
    /// test over a recorded CSV.</summary>
    public sealed class TrailObservation
    {
        /// <summary>Process-monotonic acquisition id; 0 when the transport stamps none.</summary>
        public long AcquisitionId;
        /// <summary>When the coordinate request was issued (may be default).</summary>
        public DateTime RequestUtc;
        /// <summary>When the coordinate response arrived - the instant the observation is
        /// dated by, and the one the speed check divides by.</summary>
        public DateTime ReceivedUtc;
        /// <summary>Transport connection generation; -1 when unknown. A change breaks continuity.</summary>
        public long ConnectionGeneration = -1;
        /// <summary>Identity of the RUN, not of the file: printing the same file name twice
        /// gives two different run ids and therefore two different segments.</summary>
        public string RunId = "";
        public bool PositionValid;
        /// <summary>Machine-frame position, mm. Only meaningful when <see cref="PositionValid"/>.</summary>
        public Vec3 Position;
        public bool AllHomed;
        /// <summary>Homing is in progress; coordinates during a homing move describe a
        /// search, not a toolpath.</summary>
        public bool Homing;
        public MachineStatus Status = MachineStatus.Unknown;
        /// <summary>True while the firmware reports an open job.</summary>
        public bool JobActive;
        public int? Layer;
        public long? FilePosition;
        public double? FractionPrinted;
        public double? RequestedSpeedMms;
        public double? TopSpeedMms;
        /// <summary>True when the coordinates and the speed metadata in this observation came
        /// from separate requests, so the requested feed does not describe the whole
        /// interval.</summary>
        /// <remarks>A poll assembles one observation out of several replies: the axes reply
        /// gives the coordinates, a later <c>currentMove</c> reply gives the requested feed.
        /// The interval between two such observations can therefore contain a travel move
        /// faster than the feed the endpoint happens to report, and the mismatch says
        /// something about the METADATA rather than about continuity. When this is set, a
        /// speed mismatch may only raise <see cref="TrailQuality.SuspectSpeed"/>; it never
        /// breaks the segment. Gross discontinuity is unaffected - the absolute speed
        /// ceiling, coordinate resets, time gaps, backwards timestamps, progress rollbacks,
        /// new runs and connections, homing and relative mode all still break exactly as
        /// they do for a single-request observation. Default false, so an observation whose
        /// coordinates and feed came from one reply is judged as strictly as before.</remarks>
        public bool CompositeSpeedMetadata;
        /// <summary>Cumulative raw extrusion, mm. A rise separates a printing move from a travel.</summary>
        public double? RawExtrusion;
        /// <summary>The machine is in relative coordinate mode (G91) or the mode is unknown,
        /// so a coordinate difference is not a displacement in the machine frame.</summary>
        public bool RelativeCoordinates;
        /// <summary>Non-null when the position was repaired or flagged by the single-axis
        /// glitch filter; carried into the verdict's reason.</summary>
        public string? RepairNote;
    }

    /// <summary>What the policy concluded about one observation.</summary>
    public readonly struct TrailVerdict
    {
        /// <summary>Segment this observation belongs to. Observations with different segment
        /// ids must never be joined by a line.</summary>
        public long SegmentId { get; }
        /// <summary>True when this observation opened the segment.</summary>
        public bool SegmentStarted { get; }
        public TrailClass Classification { get; }
        public TrailQuality Quality { get; }
        /// <summary>Why the segment broke or why the quality is not Valid; null when neither.</summary>
        public string? Reason { get; }
        /// <summary>Same-elevation distance to the intended path in mm, NaN when no
        /// comparison was available. NaN is "unknown", never "on the path".</summary>
        public double PathDistanceMm { get; }
        /// <summary>Speed implied by the coordinate difference and the ACTUAL elapsed time,
        /// mm/s; NaN when it could not be computed.</summary>
        public double ImpliedSpeedMms { get; }
        /// <summary>How many observations in a row have disagreed with the intended path,
        /// including this one. A sustained tracking fault shows up here instead of being
        /// mistaken for a string of isolated glitches.</summary>
        public int ConsecutivePathDisagreements { get; }

        public TrailVerdict(long segmentId, bool segmentStarted, TrailClass classification,
                            TrailQuality quality, string? reason, double pathDistanceMm,
                            double impliedSpeedMms, int consecutivePathDisagreements)
        {
            SegmentId = segmentId; SegmentStarted = segmentStarted; Classification = classification;
            Quality = quality; Reason = reason; PathDistanceMm = pathDistanceMm;
            ImpliedSpeedMms = impliedSpeedMms; ConsecutivePathDisagreements = consecutivePathDisagreements;
        }
    }

    /// <summary>Thresholds. Every one is a diagnostic trigger, not a safety limit.</summary>
    public sealed class TrailContinuityOptions
    {
        /// <summary>Longer than this between two coordinate responses and the trail is not
        /// continuous - the machine moved through a route the log cannot recover.</summary>
        public double MaxGapSeconds = 5.0;
        /// <summary>A coordinate difference larger than this without a matching elapsed time
        /// is a coordinate reset (G92, homing, a frame change), not a movement.</summary>
        public double CoordinateResetMm = 250.0;
        /// <summary>Same-elevation distance above which an observation is marked suspect.
        /// 5 mm catches the seven operator-confirmed false positions in the 2026-09-09
        /// dome capture, whose neighbours are all inside 0.15 mm. It is a trigger for
        /// this machine and this material, not a universal tolerance.</summary>
        public double PathDisagreementMm = 5.0;
        /// <summary>Implied speed above requested * this + <see cref="SpeedFlagMarginMms"/>
        /// is flagged.</summary>
        public double SpeedFlagFactor = 1.5;
        public double SpeedFlagMarginMms = 5.0;
        /// <summary>Implied speed above requested * this + <see cref="SpeedBreakMarginMms"/>
        /// breaks the segment: the two positions cannot describe one continuous move.</summary>
        public double SpeedBreakFactor = 4.0;
        public double SpeedBreakMarginMms = 20.0;
        /// <summary>Implied speed above this breaks the segment whatever was requested
        /// (the 2026-09-02 log's 1,432 mm/s sample).</summary>
        public double SpeedCeilingMms = 1000.0;
        /// <summary>Fallback requested speed when the firmware reports none.</summary>
        public double AssumedRequestedSpeedMms = 100.0;

        public TrailContinuityOptions Copy() => (TrailContinuityOptions)MemberwiseClone();
    }

    /// <summary>The one continuity rule set for observed toolpaths: the print-log recorder,
    /// the live trail and any replay of a recorded log all segment and classify through
    /// this, so a fixture that proves a rule proves it everywhere.</summary>
    /// <remarks>
    /// <para>What it does NOT do, by design: it never edits a coordinate, never projects an
    /// observation onto the intended path, never smooths, never drops a sample. Its only
    /// outputs are a segment id, a classification and a quality flag; the caller keeps the
    /// raw values whatever those say.</para>
    /// <para>Path disagreement is a DIAGNOSTIC TRIGGER. A sample that disagrees is marked
    /// suspect and preserved, and the segment is NOT broken - breaking it would hide the
    /// excursion by turning it into two tidy runs. A sustained disagreement therefore stays
    /// visible sample by sample and in
    /// <see cref="TrailVerdict.ConsecutivePathDisagreements"/>, so a real tracking fault
    /// cannot be mistaken for a handful of isolated reporting glitches.</para>
    /// <para>Not thread-safe: one instance per consumer, called in observation order.</para>
    /// </remarks>
    public sealed class TrailContinuityPolicy
    {
        readonly TrailContinuityOptions _opt;
        IIntendedPathReference? _path;

        long _segment;
        bool _open;
        int _consecutivePathDisagreements;

        long _prevAcquisition;
        DateTime _prevReceived;
        long _prevGeneration = -1;
        string _prevRun = "";
        bool _prevValid;
        Vec3 _prevPosition;
        long? _prevFilePosition;
        double? _prevFraction;
        double? _prevExtrusion;
        bool _prevHomed;

        public TrailContinuityPolicy(TrailContinuityOptions? options = null,
                                     IIntendedPathReference? intendedPath = null)
        {
            _opt = (options ?? new TrailContinuityOptions()).Copy();
            _path = intendedPath;
        }

        /// <summary>The intended path of the ACTIVE job. Replacing it never rewrites past
        /// verdicts; it only changes what later observations are compared against. Null
        /// means "unavailable" - an externally started job, or one whose file has not been
        /// retrieved and verified - and every later comparison then reports unavailable.</summary>
        public void SetIntendedPath(IIntendedPathReference? path) => _path = path;

        public bool HasIntendedPath => _path != null;
        /// <summary>Id of the segment observations are currently joining, 0 before the first.</summary>
        public long CurrentSegment => _segment;

        /// <summary>Forgets the previous observation, so the next one starts a new segment.
        /// Used when the caller knows continuity is gone for a reason the observation does
        /// not carry (the session was replaced, the trail was cleared).</summary>
        public void Break()
        {
            _open = false;
            _prevValid = false;
            _prevRun = "";
            _prevGeneration = -1;
            _prevAcquisition = 0;
            _prevReceived = default(DateTime);
            _prevFilePosition = null;
            _prevFraction = null;
            _prevExtrusion = null;
            _consecutivePathDisagreements = 0;
        }

        public TrailVerdict Observe(TrailObservation o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));

            double implied = double.NaN;
            string? breakReason = null;

            // ---- continuity ------------------------------------------------------------
            if (!_open) breakReason = "first observation";
            else if (!string.Equals(o.RunId, _prevRun, StringComparison.Ordinal))
                breakReason = "new run";
            else if (o.ConnectionGeneration != _prevGeneration)
                breakReason = "new connection";
            else if (o.AcquisitionId > 0 && _prevAcquisition > 0 && o.AcquisitionId <= _prevAcquisition)
                breakReason = "acquisition " + o.AcquisitionId.ToString(CultureInfo.InvariantCulture)
                            + " is not newer than " + _prevAcquisition.ToString(CultureInfo.InvariantCulture);
            else if (!o.PositionValid || !_prevValid)
                breakReason = "position unavailable";
            else if (o.Homing)
                breakReason = "homing";
            else if (_prevHomed && !o.AllHomed)
                breakReason = "axes lost their homed state";
            else if (o.RelativeCoordinates)
                breakReason = "relative coordinate mode";

            double dt = double.NaN;
            if (breakReason == null && o.ReceivedUtc != default(DateTime) && _prevReceived != default(DateTime))
            {
                dt = (Utc(o.ReceivedUtc) - Utc(_prevReceived)).TotalSeconds;
                if (dt < 0) breakReason = "timestamps go backwards";
                else if (dt > _opt.MaxGapSeconds)
                    breakReason = "gap of " + dt.ToString("0.###", CultureInfo.InvariantCulture) + " s";
            }

            if (breakReason == null && o.FilePosition.HasValue && _prevFilePosition.HasValue
                && o.FilePosition.Value < _prevFilePosition.Value)
                breakReason = "file position went backwards";
            if (breakReason == null && o.FractionPrinted.HasValue && _prevFraction.HasValue
                && o.FractionPrinted.Value < _prevFraction.Value - 1e-9)
                breakReason = "printed fraction went backwards";

            double step = double.NaN;
            if (breakReason == null && o.PositionValid && _prevValid)
            {
                step = _prevPosition.DistanceTo(o.Position);
                if (step > _opt.CoordinateResetMm)
                    breakReason = "coordinate jump of " + step.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
                else if (dt > 0)
                {
                    implied = step / dt;
                    double requested = Requested(o);
                    // The absolute ceiling is physics and always breaks (the 2026-09-02 log's
                    // 1,432 mm/s sample). The comparison against the ENDPOINT's requested
                    // feed only breaks when that feed describes the whole interval: on a
                    // composite observation the coordinates and the feed came from separate
                    // replies, and the interval may legitimately contain a faster travel than
                    // the endpoint reports, so the mismatch is diagnostic - it falls through
                    // to the SuspectSpeed flag below instead of splitting a real run.
                    if (implied > _opt.SpeedCeilingMms
                        || (!o.CompositeSpeedMetadata
                            && implied > requested * _opt.SpeedBreakFactor + _opt.SpeedBreakMarginMms))
                        breakReason = "implied " + implied.ToString("0.#", CultureInfo.InvariantCulture)
                                    + " mm/s against " + requested.ToString("0.#", CultureInfo.InvariantCulture)
                                    + " mm/s requested";
                }
            }

            bool started = breakReason != null;
            if (started) { _segment++; _consecutivePathDisagreements = 0; }

            // ---- quality ---------------------------------------------------------------
            TrailQuality quality;
            string? reason = breakReason;
            if (!o.PositionValid)
            {
                quality = TrailQuality.Invalid;
                reason = "no usable position";
            }
            else if (started)
            {
                quality = TrailQuality.Discontinuity;
            }
            else
            {
                quality = TrailQuality.Valid;
                if (!double.IsNaN(implied))
                {
                    double requested = Requested(o);
                    if (implied > requested * _opt.SpeedFlagFactor + _opt.SpeedFlagMarginMms)
                    {
                        quality = TrailQuality.SuspectSpeed;
                        reason = "implied " + implied.ToString("0.#", CultureInfo.InvariantCulture)
                               + " mm/s against " + requested.ToString("0.#", CultureInfo.InvariantCulture)
                               + " mm/s requested";
                    }
                }
            }

            // ---- single-axis reply glitch ----------------------------------------------
            // Decided upstream by AxisGlitchFilter from the reply's own second witness; the
            // continuity rules above ran on the repaired position. It outranks a speed
            // flag (the raw value is what looked fast) and never breaks a segment.
            // The repaired position reaches this policy as the filter's FILTERED VIEW of the
            // poll; the reply the transport parsed keeps the coordinates the firmware sent
            // and no repair note, so raw and filtered evidence stay separable.
            if (o.RepairNote != null && o.PositionValid && !started)
            {
                quality = TrailQuality.MachinePositionGlitch;
                reason = o.RepairNote;
            }

            // ---- intended-path comparison ----------------------------------------------
            // A trigger only. It marks and counts; it never breaks the segment (that would
            // hide the excursion) and never touches the coordinates.
            double distance = double.NaN;
            bool disagrees = false;
            if (o.PositionValid && _path != null && _path.TryDistanceMm(o.Position, out double d))
            {
                distance = d;
                disagrees = d > _opt.PathDisagreementMm;
            }
            if (disagrees)
            {
                _consecutivePathDisagreements++;
                if (quality == TrailQuality.Valid)
                {
                    quality = TrailQuality.SuspectPathDisagreement;
                    reason = distance.ToString("0.###", CultureInfo.InvariantCulture)
                           + " mm from the intended path at this elevation";
                }
            }
            else if (o.PositionValid)
            {
                _consecutivePathDisagreements = 0;
            }

            var verdict = new TrailVerdict(_segment, started, Classify(o, quality), quality, reason,
                                           distance, implied, _consecutivePathDisagreements);

            // ---- carry forward ---------------------------------------------------------
            _open = true;
            _prevRun = o.RunId ?? "";
            _prevGeneration = o.ConnectionGeneration;
            if (o.AcquisitionId > 0) _prevAcquisition = o.AcquisitionId;
            if (o.ReceivedUtc != default(DateTime)) _prevReceived = o.ReceivedUtc;
            _prevValid = o.PositionValid;
            if (o.PositionValid) _prevPosition = o.Position;
            _prevFilePosition = o.FilePosition ?? _prevFilePosition;
            _prevFraction = o.FractionPrinted ?? _prevFraction;
            _prevExtrusion = o.RawExtrusion ?? _prevExtrusion;
            _prevHomed = o.AllHomed;
            return verdict;
        }

        double Requested(TrailObservation o)
        {
            double requested = o.RequestedSpeedMms ?? o.TopSpeedMms ?? _opt.AssumedRequestedSpeedMms;
            if (double.IsNaN(requested) || double.IsInfinity(requested) || requested <= 0)
                requested = _opt.AssumedRequestedSpeedMms;
            return requested;
        }

        TrailClass Classify(TrailObservation o, TrailQuality quality)
        {
            if (quality == TrailQuality.Invalid) return TrailClass.Unknown;
            if (o.Homing) return TrailClass.Unknown;
            switch (o.Status)
            {
                case MachineStatus.Paused:
                case MachineStatus.Pausing:
                case MachineStatus.Resuming:
                    return TrailClass.Paused;
                case MachineStatus.Processing:
                case MachineStatus.Simulating:
                    // Before the first layer the job is running but nothing is deposited.
                    if (!o.JobActive || !o.Layer.HasValue || o.Layer.Value <= 0)
                        return TrailClass.StartupOrFooter;
                    if (Extruding(o)) return TrailClass.Printing;
                    // The approach descent and the lift/G28/M84 footer both show as a
                    // printing job that has stopped consuming the file and stopped
                    // extruding, at the very start or the very end of it. The 2026-09-02
                    // run 2 ends with eleven such samples at Z=400: joining them to the
                    // print path would draw a movement across the finished part.
                    if (!FileAdvanced(o) && o.FractionPrinted.HasValue
                        && (o.FractionPrinted.Value >= EndOfFileFraction
                            || o.FractionPrinted.Value <= StartOfFileFraction))
                        return TrailClass.StartupOrFooter;
                    return TrailClass.Travel;
                case MachineStatus.Idle:
                case MachineStatus.Busy:
                case MachineStatus.ChangingTool:
                    return o.JobActive ? TrailClass.Jogged : TrailClass.StartupOrFooter;
                case MachineStatus.Cancelling:
                case MachineStatus.Off:
                case MachineStatus.Halted:
                    return TrailClass.StartupOrFooter;
                default:
                    return TrailClass.Unknown;
            }
        }

        /// <summary>Printed fraction at or below which a non-extruding move is the job's
        /// approach, and at or above which it is the job's footer.</summary>
        const double StartOfFileFraction = 0.01;
        const double EndOfFileFraction = 0.995;

        /// <summary>True when the extruder advanced since the previous observation. Unknown
        /// extrusion is NOT treated as printing: an unclassifiable move stays travel rather
        /// than being drawn as deposited material.</summary>
        bool Extruding(TrailObservation o)
            => o.RawExtrusion.HasValue && _prevExtrusion.HasValue
            && o.RawExtrusion.Value > _prevExtrusion.Value + 1e-9;

        /// <summary>True when the firmware consumed more of the file since the previous
        /// observation. Firmware file position runs AHEAD of physical execution, so this
        /// is a classification hint, never a location.</summary>
        bool FileAdvanced(TrailObservation o)
            => !o.FilePosition.HasValue || !_prevFilePosition.HasValue
            || o.FilePosition.Value > _prevFilePosition.Value;

        static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
