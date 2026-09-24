using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>RRF object-model machine states (state.status).</summary>
    public enum MachineStatus
    {
        Disconnected, Starting, Updating, Off, Halted, Pausing, Paused,
        Resuming, Cancelling, Processing, Simulating, Busy, ChangingTool, Idle, Unknown
    }

    /// <summary>One entry of move.axes - only the fields the components need.</summary>
    public sealed class AxisState
    {
        public string Letter = "";
        public double MachinePosition;
        public double UserPosition;
        public bool Homed;
        public double Min, Max;
    }

    /// <summary>Subset of the job key while a file is printing/simulating.</summary>
    public sealed class JobState
    {
        public string? FileName;
        public long FilePosition;
        public long FileSize;
        /// <summary>0..1; filePosition/size when the firmware gives no better figure.</summary>
        public double FractionPrinted;
        public int? Layer;
        /// <summary>The firmware's own layer count for the file (<c>job.file.numLayers</c>),
        /// or null when it reports none (0 or absent, and always on the legacy path).</summary>
        public int? FileLayerCount;
        public double? TimePrinting;   // seconds
    }

    /// <summary>One coherent status poll. TakenUtc lets consumers refuse to act on
    /// stale data (the jog gate hard-refuses snapshots older than a few seconds).</summary>
    public sealed class ObjectModelSnapshot
    {
        public MachineStatus Status = MachineStatus.Unknown;
        public string RawStatus = "";
        public IReadOnlyList<AxisState> Axes = Array.Empty<AxisState>();
        public JobState? Job;
        /// <summary>Requested head speed in mm/s, when reported by the firmware.</summary>
        public double? RequestedSpeed;
        /// <summary>Firmware top speed in mm/s, when reported by the firmware.</summary>
        public double? TopSpeed;
        /// <summary>Speed factor as a percentage (100 = unmodified).</summary>
        public double? SpeedFactorPercent;
        /// <summary>Unmodified cumulative extrusion position in mm.</summary>
        public double? RawExtrusion;
        public bool AllHomed;
        /// <summary>Instant this observation is dated by. Since 4.1 this is the moment the
        /// COORDINATE response was received whenever the transport stamps the acquisition
        /// (<see cref="Acquisition"/>); it used to be - and still is, for a transport or a
        /// test that supplies no stamp - the moment parsing finished, which on the modern
        /// rr_model path lands after the cosmetic job/thermal reads. Existing consumers
        /// (staleness gates, log timestamps, derived speed) keep using it unchanged.</summary>
        public DateTime TakenUtc;
        public string? LastReply;
        /// <summary>Identity and timing of the acquisition that produced this snapshot,
        /// or null when the transport supplied none.</summary>
        public AcquisitionStamp? Acquisition;
        /// <summary>Process-monotonic acquisition id; 0 when unstamped. A consumer must
        /// never let a smaller id overwrite a larger one it already published.</summary>
        public long AcquisitionId => Acquisition?.AcquisitionId ?? 0;
        /// <summary>Connection generation of the acquisition; -1 when unknown.</summary>
        public long ConnectionGeneration => Acquisition?.ConnectionGeneration ?? -1;
        /// <summary>True when state, axes and job came from separate requests or from
        /// stitched pages, so the parts may describe different instants.</summary>
        public bool CompositeObservation => Acquisition?.Composite ?? false;
        PositionValidity? _positionValidity;

        /// <summary>Whether X/Y/Z form a usable position, and why not when they do not.</summary>
        /// <remarks>The parsers set this explicitly, because only they can tell "the field
        /// was absent" from "the axis was never mentioned". A snapshot assembled by hand or
        /// by another transport derives it from <see cref="Axes"/>: the axis list is then
        /// the only evidence there is, and an entry in it was, by construction, given a
        /// real coordinate.</remarks>
        public PositionValidity PositionValidity
        {
            get => _positionValidity ?? DeriveValidity(out _);
            set => _positionValidity = value;
        }
        string? _positionInvalidReason;
        /// <summary>Human-readable reason behind a non-valid <see cref="PositionValidity"/>.</summary>
        public string? PositionInvalidReason
        {
            get
            {
                if (_positionInvalidReason != null) return _positionInvalidReason;
                if (_positionValidity.HasValue) return null;
                DeriveValidity(out string? reason);
                return reason;
            }
            set => _positionInvalidReason = value;
        }
        public bool PositionValid => PositionValidity == PositionValidity.Valid;
        /// <summary>Non-null when <see cref="AxisGlitchFilter"/> replaced one axis of the
        /// reported machine position with the same reply's user position, or left a
        /// single-axis disagreement in place; names the axis and both raw values. The raw
        /// reply itself stays in the diagnostics window.</summary>
        /// <remarks>Carried only by the filtered COPY the filter returns (see
        /// <see cref="WithPositionRepair"/>): the snapshot the transport produced is never
        /// written to, so it keeps null here and the coordinates the firmware reported.</remarks>
        public string? PositionRepair;
        /// <summary>The raw coordinate reply this snapshot was parsed from, when the
        /// transport exposes it; paired at parse time so a snapshot the glitch filter
        /// holds for one poll still meets its own body in the diagnostics window.</summary>
        public RawBodyProbe? RawProbe;

        PositionValidity DeriveValidity(out string? reason)
        {
            var letters = new List<string>(Axes.Count);
            foreach (var a in Axes) letters.Add(a.Letter);
            return AxisReader.Validate(letters, letters, out reason);
        }

        /// <summary>Machine-frame head position, or null when <see cref="PositionValid"/>
        /// is false. Never fabricates a coordinate for an axis the machine did not report.</summary>
        public Vec3? MachinePosition
        {
            get
            {
                if (!PositionValid) return null;
                var x = Axis("X"); var y = Axis("Y"); var z = Axis("Z");
                if (x == null || y == null || z == null) return null;
                return new Vec3(x.MachinePosition, y.MachinePosition, z.MachinePosition);
            }
        }

        /// <summary>User-frame head position, or null when <see cref="PositionValid"/> is false.</summary>
        public Vec3? UserPosition
        {
            get
            {
                if (!PositionValid) return null;
                var x = Axis("X"); var y = Axis("Y"); var z = Axis("Z");
                if (x == null || y == null || z == null) return null;
                return new Vec3(x.UserPosition, y.UserPosition, z.UserPosition);
            }
        }
        /// <summary>M220/M221 overrides; null when the transport reports neither.</summary>
        public Machine.MachineOverrides? Overrides;

        public AxisState? Axis(string letter)
        {
            foreach (var a in Axes)
                if (string.Equals(a.Letter, letter, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>A COPY of this snapshot carrying <paramref name="repair"/> in
        /// <see cref="PositionRepair"/> and, when <paramref name="correctedAxis"/> is given,
        /// that axis's <see cref="AxisState.MachinePosition"/> replaced by
        /// <paramref name="correctedMachinePosition"/>. This snapshot is left exactly as the
        /// transport produced it.</summary>
        /// <remarks>A snapshot is published to consumers as soon as it is polled, while
        /// <see cref="AxisGlitchFilter"/> can only judge it one poll later; repairing it in
        /// place would rewrite state other code already holds and would destroy the raw
        /// telemetry the false-position investigation compares against. The axis list is
        /// rebuilt entry by entry so the copy's axes are independent too; the acquisition
        /// stamp, the job, the overrides and the raw probe are shared by reference, as
        /// nothing writes to them after parsing.</remarks>
        public ObjectModelSnapshot WithPositionRepair(string repair, string? correctedAxis = null,
                                                     double correctedMachinePosition = 0)
        {
            var axes = new AxisState[Axes.Count];
            for (int i = 0; i < Axes.Count; i++)
            {
                var a = Axes[i];
                bool corrected = correctedAxis != null
                              && string.Equals(a.Letter, correctedAxis, StringComparison.OrdinalIgnoreCase);
                axes[i] = new AxisState
                {
                    Letter = a.Letter,
                    MachinePosition = corrected ? correctedMachinePosition : a.MachinePosition,
                    UserPosition = a.UserPosition,
                    Homed = a.Homed,
                    Min = a.Min,
                    Max = a.Max,
                };
            }
            // MemberwiseClone, not a field-by-field copy: it carries the PRIVATE validity
            // backing fields across, so a copy of a snapshot whose parser set the validity
            // explicitly keeps that verdict instead of re-deriving it from the axis list.
            var copy = (ObjectModelSnapshot)MemberwiseClone();
            copy.Axes = axes;
            copy.PositionRepair = repair;
            return copy;
        }
    }

    /// <summary>Maps rr_model JSON fragments onto the DTO subset. Tolerant: missing
    /// fields keep defaults (RRF omits/renames fields between versions); only a
    /// missing top-level result is an error (the client raises that earlier).</summary>
    public static class ObjectModelParser
    {
        public static MachineStatus ParseStatus(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "disconnected": return MachineStatus.Disconnected;
                case "starting": return MachineStatus.Starting;
                case "updating": return MachineStatus.Updating;
                case "off": return MachineStatus.Off;
                case "halted": return MachineStatus.Halted;
                case "pausing": return MachineStatus.Pausing;
                case "paused": return MachineStatus.Paused;
                case "resuming": return MachineStatus.Resuming;
                case "cancelling": return MachineStatus.Cancelling;
                case "processing": return MachineStatus.Processing;
                case "simulating": return MachineStatus.Simulating;
                case "busy": return MachineStatus.Busy;
                case "changingtool": return MachineStatus.ChangingTool;
                case "idle": return MachineStatus.Idle;
                default: return MachineStatus.Unknown;
            }
        }

        /// <summary>Single-letter machine states of the legacy rr_status API
        /// (RRF &lt; 3.2 - the Potterbot ships RRF 3.1.1 "3DPotter Edition").</summary>
        public static MachineStatus ParseStatusLetter(string s)
        {
            switch ((s ?? "").Trim())
            {
                case "C": return MachineStatus.Starting;      // reading config
                case "F": return MachineStatus.Updating;      // flashing firmware
                case "O": return MachineStatus.Off;
                case "H": return MachineStatus.Halted;
                case "D": return MachineStatus.Pausing;       // decelerating
                case "S": return MachineStatus.Paused;        // stopped
                case "R": return MachineStatus.Resuming;
                case "P": return MachineStatus.Processing;    // printing
                case "M": return MachineStatus.Simulating;
                case "B": return MachineStatus.Busy;
                case "T": return MachineStatus.ChangingTool;
                case "I": return MachineStatus.Idle;
                default: return MachineStatus.Unknown;
            }
        }

        /// <summary>Builds a snapshot from a legacy rr_status?type=3 response
        /// (status letter, coords.machine/xyz, coords.axesHomed, fractionPrinted).
        /// <paramref name="fileName"/> comes from a separate rr_fileinfo call and
        /// may be null. Tolerant of missing fields like the rr_model path.</summary>
        public static ObjectModelSnapshot FromRrStatus(JsonValue status, string? fileName,
                                                      AcquisitionStamp? acquisition = null)
        {
            var snap = new ObjectModelSnapshot
            {
                RawStatus = status.Str("status", ""),
                Acquisition = acquisition,
                TakenUtc = Dated(acquisition),
            };
            snap.Status = ParseStatusLetter(snap.RawStatus);

            var machine = status.At("coords.machine");
            var user = status.At("coords.xyz");
            var homed = status.At("coords.axesHomed");
            string letters = "XYZ";
            var list = new List<AxisState>();
            var reported = new List<string>();
            var accepted = new List<string>();
            bool allHomed = homed.Count >= 3;
            for (int i = 0; i < 3 && i < machine.Count; i++)
            {
                string letter = letters[i].ToString();
                reported.Add(letter);
                bool h = homed[i].AsInt() == 1;
                if (!h) allHomed = false;
                // Strict: the coordinate must be a finite JSON number. A missing entry,
                // a string, a boolean or an object is a protocol change, not a position
                // at the machine origin, so the axis is dropped and the snapshot reports
                // an invalid position instead of fabricating one.
                var machineValue = machine[i];
                var userValue = i < user.Count ? user[i] : machineValue;
                if (!AxisReader.IsCoordinate(machineValue)) continue;
                if (!AxisReader.IsCoordinate(userValue)) userValue = machineValue;
                accepted.Add(letter);
                list.Add(new AxisState
                {
                    Letter = letter,
                    MachinePosition = machineValue.AsNumber(),
                    UserPosition = userValue.AsNumber(),
                    Homed = h,
                });
            }
            snap.Axes = list;
            snap.PositionValidity = AxisReader.Validate(reported, accepted, out string? reason);
            snap.PositionInvalidReason = reason;
            // unchanged rule: three homed position axes were reported. Whether their
            // coordinates were readable is a separate fact (PositionValidity).
            snap.AllHomed = allHomed && reported.Count == 3;
            snap.RequestedSpeed = NumberOrNull(status.At("speeds.requested"));
            snap.TopSpeed = NumberOrNull(status.At("speeds.top"));
            snap.SpeedFactorPercent = NumberOrNull(status.At("params.speedFactor"));
            double? flowFactor = NumberOrNull(status.At("params.extrFactors[0]"));
            if (snap.SpeedFactorPercent.HasValue || flowFactor.HasValue)
                snap.Overrides = new Machine.MachineOverrides(snap.SpeedFactorPercent, flowFactor);
            snap.RawExtrusion = NumberOrNull(status.At("extrRaw[0]"));

            bool jobActive = snap.Status == MachineStatus.Processing
                          || snap.Status == MachineStatus.Simulating
                          || snap.Status == MachineStatus.Paused
                          || snap.Status == MachineStatus.Pausing
                          || snap.Status == MachineStatus.Resuming;
            if (jobActive && !status.At("fractionPrinted").IsNull)
            {
                snap.Job = new JobState
                {
                    FileName = fileName,
                    FilePosition = status.At("filePosition").IsNull ? 0L : (long)status.Num("filePosition"),
                    FractionPrinted = Math.Min(1.0, Math.Max(0.0, status.Num("fractionPrinted") / 100.0)),
                    Layer = status.At("currentLayer").IsNull ? (int?)null : status.Int("currentLayer"),
                    TimePrinting = status.At("printDuration").IsNull ? (double?)null : status.Num("printDuration"),
                };
            }
            return snap;
        }

        /// <summary>Builds a snapshot from the three per-key rr_model results
        /// (state, move.axes and - while a job runs - job).</summary>
        public static ObjectModelSnapshot FromRrModel(JsonValue state, JsonValue axes, JsonValue? job,
                                                     JsonValue? moveCurrent = null,
                                                     JsonValue? moveSpeedFactor = null,
                                                     AcquisitionStamp? acquisition = null,
                                                     JsonValue? moveExtruders = null)
        {
            var snap = new ObjectModelSnapshot
            {
                RawStatus = state.Str("status", ""),
                Acquisition = acquisition,
                TakenUtc = Dated(acquisition),
            };
            snap.Status = ParseStatus(snap.RawStatus);

            var list = new List<AxisState>();
            var reported = new List<string>();
            var accepted = new List<string>();
            bool allHomed = true;
            bool anyVisible = false;
            for (int i = 0; i < axes.Count; i++)
            {
                var a = axes[i];
                string letter = a.Str("letter", "");
                reported.Add(letter);
                var machineValue = a.At("machinePosition");
                var userValue = a.At("userPosition");
                bool homedFlag = a.Bool("homed");
                // hidden/param axes (visible=false) don't gate homing. Homing readiness is
                // read from every REPORTED entry, independent of whether its coordinate is
                // readable, so this rule keeps the meaning it always had.
                bool visible = a.At("visible").IsNull || a.Bool("visible", true);
                if (visible) { anyVisible = true; if (!homedFlag) allHomed = false; }

                // Strict coordinate rule (2026-09-09 telemetry review): a position axis
                // must carry finite JSON NUMBERS. JsonValue.Num maps a missing field, a
                // string or an object to 0 and a boolean to 0/1, so without this check a
                // response that dropped machinePosition became a valid-looking sample at
                // the machine origin. A rejected position axis is left out of Axes
                // entirely - every consumer of Axis("X") already fails closed on null.
                bool numeric = AxisReader.IsCoordinate(machineValue)
                            && (userValue.IsNull || AxisReader.IsCoordinate(userValue));
                if (!numeric && IsPositionAxis(letter)) continue;
                var ax = new AxisState
                {
                    Letter = letter,
                    MachinePosition = machineValue.AsNumber(),
                    UserPosition = userValue.IsNull ? machineValue.AsNumber() : userValue.AsNumber(),
                    Homed = homedFlag,
                    Min = a.Num("min"),
                    Max = a.Num("max"),
                };
                // defense in depth behind the parser's own non-finite rejection
                // (AUD-SAF-003): axis data that gates motion must be finite
                if (double.IsNaN(ax.MachinePosition) || double.IsInfinity(ax.MachinePosition)
                    || double.IsNaN(ax.Min) || double.IsInfinity(ax.Min)
                    || double.IsNaN(ax.Max) || double.IsInfinity(ax.Max))
                    throw new FormatException($"non-finite axis data for '{ax.Letter}'");
                if (numeric) accepted.Add(letter);
                list.Add(ax);
            }
            snap.Axes = list;
            snap.PositionValidity = AxisReader.Validate(reported, accepted, out string? reason);
            snap.PositionInvalidReason = reason;
            // Unchanged rule: every VISIBLE reported axis is homed. Homing readiness and
            // position validity stay separate facts - a board may report a homed axis set
            // whose coordinates this poll could not read - and consumers that need a
            // position ask for PositionValid / MachinePosition, which fail closed.
            snap.AllHomed = anyVisible && allHomed;

            if (moveCurrent != null && !moveCurrent.IsNull)
            {
                snap.RequestedSpeed = NumberOrNull(moveCurrent.At("requestedSpeed"));
                snap.TopSpeed = NumberOrNull(moveCurrent.At("topSpeed"));
            }
            if (moveSpeedFactor != null && !moveSpeedFactor.IsNull)
            {
                var factor = moveSpeedFactor.At("speedFactor");
                if (factor.IsNull) factor = moveSpeedFactor;
                double? factorValue = NumberOrNull(factor);
                if (factorValue.HasValue)
                    snap.SpeedFactorPercent = factorValue.Value * 100.0;
            }
            double? flowFactor = null;
            if (moveExtruders != null && !moveExtruders.IsNull && moveExtruders.Count > 0)
            {
                var factor = moveExtruders[0].At("factor");
                if (factor.IsNull) factor = moveExtruders[0].At("extrusionFactor");
                double? factorValue = NumberOrNull(factor);
                if (factorValue.HasValue) flowFactor = factorValue.Value * 100.0;
            }
            if (snap.SpeedFactorPercent.HasValue || flowFactor.HasValue)
                snap.Overrides = new Machine.MachineOverrides(snap.SpeedFactorPercent, flowFactor);

            if (job != null && !job.IsNull && !job.At("file").IsNull
                && job.Str("file.fileName", "").Length > 0)
            {
                var j = new JobState
                {
                    FileName = job.Str("file.fileName"),
                    FilePosition = (long)job.Num("filePosition"),
                    FileSize = (long)job.Num("file.size"),
                    TimePrinting = job.At("duration").IsNull ? (double?)null : job.Num("duration"),
                    Layer = job.At("layer").IsNull ? (int?)null : job.Int("layer"),
                };
                double? numLayers = NumberOrNull(job.At("file.numLayers"));
                if (numLayers.HasValue && numLayers.Value >= 1 && numLayers.Value <= int.MaxValue)
                    j.FileLayerCount = (int)numLayers.Value;
                j.FractionPrinted = j.FileSize > 0
                    ? Math.Min(1.0, Math.Max(0.0, (double)j.FilePosition / j.FileSize)) : 0.0;
                snap.Job = j;
            }
            if (job != null && !job.IsNull)
                snap.RawExtrusion = NumberOrNull(job.At("rawExtrusion"));
            return snap;
        }

        static double? NumberOrNull(JsonValue value)
            => value == null || value.IsNull ? (double?)null : value.AsNumber();

        static bool IsPositionAxis(string letter)
            => string.Equals(letter, "X", StringComparison.OrdinalIgnoreCase)
            || string.Equals(letter, "Y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(letter, "Z", StringComparison.OrdinalIgnoreCase);

        /// <summary>The instant the snapshot is dated by: the coordinate response's
        /// arrival when the transport stamped it, the parse time otherwise.</summary>
        static DateTime Dated(AcquisitionStamp? stamp)
            => stamp != null && stamp.ResponseReceivedUtc != default(DateTime)
               ? stamp.ResponseReceivedUtc : DateTime.UtcNow;
    }
}
