using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>What the display may do with one observation.</summary>
    public enum PoseEligibility
    {
        /// <summary>Pose the arm from it and let the trail use it.</summary>
        Show,
        /// <summary>Do not pose from it and do not draw it: hold the last valid arm as
        /// held/uncertain, with the reason on the info output. Interpolation must not
        /// continue across it.</summary>
        Hold,
        /// <summary>Nothing may be drawn at all - no valid pose has ever existed here.</summary>
        Hide,
    }

    /// <summary>The answer for one observation: what the display may do, why, and whether
    /// the machine's own envelope was available to decide it.</summary>
    /// <remarks>Immutable and free of coordinates on purpose. The decision NEVER edits,
    /// clamps, projects or substitutes a value - the caller keeps the raw observation and
    /// only learns whether it may pose from it, so a rejected coordinate stays exactly as
    /// the firmware reported it in the logs, the diagnostics window and the CSV.</remarks>
    public readonly struct PoseDecision
    {
        public PoseEligibility Eligibility { get; }
        /// <summary>Why, in operator language; null only for <see cref="PoseEligibility.Show"/>
        /// with a known envelope.</summary>
        public string? Reason { get; }
        /// <summary>True when a verified machine envelope was available to check against.
        /// False means the bounds are UNKNOWN (the legacy rr_status path reports none) -
        /// unknown is reported as unknown, never treated as a pass or as a failure.</summary>
        public bool EnvelopeKnown { get; }
        /// <summary>True when the envelope check is what refused this observation - the
        /// sustained approach defect. Counted separately so it cannot be lost among ordinary
        /// holds: a real fault that persists for 15 s must stay visible and countable, and a
        /// homing hold must never be mistaken for one.</summary>
        public bool RejectedByEnvelope { get; }
        /// <summary>True only for <see cref="PoseEligibility.Show"/>.</summary>
        public bool MayPose { get { return Eligibility == PoseEligibility.Show; } }

        public PoseDecision(PoseEligibility eligibility, string? reason, bool envelopeKnown, bool rejectedByEnvelope)
        {
            Eligibility = eligibility;
            Reason = reason;
            EnvelopeKnown = envelopeKnown;
            RejectedByEnvelope = rejectedByEnvelope;
        }
    }

    /// <summary>The machine's OWN reachable box, read from the per-axis <c>min</c>/<c>max</c>
    /// the firmware puts in every <c>rr_model?key=move.axes</c> reply.</summary>
    /// <remarks>
    /// <para>Evidence (run 20260909-131323-d30110, job.gcode SHA-256
    /// 4ec8f1a21f6116d4e6068d47264f244a4f5df9350bb3c3c4921ffa0095112da8): during the
    /// approach the raw bodies reported Z down to -389.314 mm while every axis was flagged
    /// homed and <c>state.status</c> was Processing. The SAME bodies declared X
    /// <c>min:-300 max:10000</c>, Y <c>min:-10000 max:0</c> and Z <c>min:0 max:400</c>, so
    /// the excursion is 389 mm below the machine's own reported limit. The controller
    /// checked homed/halted/off and accepted the coordinates, and
    /// <c>ScaraKinematics.InverseRrf</c> solves XY then assigns <c>j2 = z - BaseH</c>, so a
    /// successful IK result never validated them either.</para>
    /// <para>Two rules follow from that evidence and are load-bearing here. First, the bed
    /// is NOT a universal Z=0 floor: this class only ever uses what the firmware reported,
    /// and a machine whose Z minimum really is negative keeps its negative travel. On this
    /// very board X and Y both have negative bounds, so "negative" and "out of bounds" are
    /// unrelated. Second, an axis the firmware said nothing usable about is UNCONSTRAINED,
    /// not a zero-height box at the origin: the legacy rr_status path leaves
    /// <see cref="AxisState.Min"/> and <see cref="AxisState.Max"/> at 0/0, and treating that
    /// as a limit would reject every coordinate on the machine.</para>
    /// </remarks>
    public sealed class MachineEnvelope
    {
        readonly Bound _x, _y, _z;
        readonly double _tolerance;

        readonly struct Bound
        {
            public bool Constrained { get; }
            public double Min { get; }
            public double Max { get; }
            public Bound(bool constrained, double min, double max) { Constrained = constrained; Min = min; Max = max; }
        }

        MachineEnvelope(Bound x, Bound y, Bound z, double tolerance)
        {
            _x = x; _y = y; _z = z; _tolerance = tolerance;
        }

        /// <summary>True when at least one position axis reported a usable min/max pair.</summary>
        public bool Known { get { return _x.Constrained || _y.Constrained || _z.Constrained; } }

        /// <summary>The tolerance this envelope was built with, mm.</summary>
        public double ToleranceMm { get { return _tolerance; } }

        /// <summary>Reads <c>move.axes</c> min/max for X, Y and Z. An axis whose pair is
        /// missing, non-finite or degenerate (max &lt;= min, or both zero as the legacy
        /// rr_status path leaves them) is UNCONSTRAINED, not zero-sized.</summary>
        public static MachineEnvelope FromAxes(IReadOnlyList<AxisState> axes, double toleranceMm)
        {
            double tol = double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm) || toleranceMm < 0
                       ? 0.0 : toleranceMm;
            return new MachineEnvelope(Read(axes, "X"), Read(axes, "Y"), Read(axes, "Z"), tol);
        }

        static Bound Read(IReadOnlyList<AxisState>? axes, string letter)
        {
            if (axes == null) return new Bound(false, 0, 0);
            AxisState? found = null;
            for (int i = 0; i < axes.Count; i++)
            {
                var a = axes[i];
                if (a != null && string.Equals(a.Letter, letter, StringComparison.OrdinalIgnoreCase))
                {
                    // A duplicated axis letter is already an invalid position upstream
                    // (PositionValidity.DuplicateAxis); take the first and let that rule decide.
                    found = a;
                    break;
                }
            }
            if (found == null) return new Bound(false, 0, 0);
            double min = found.Min, max = found.Max;
            if (double.IsNaN(min) || double.IsInfinity(min)) return new Bound(false, 0, 0);
            if (double.IsNaN(max) || double.IsInfinity(max)) return new Bound(false, 0, 0);
            // Degenerate: RRF never reports a travel of zero or negative length, so this is
            // "the firmware did not tell us", which includes the legacy 0/0 default.
            if (max <= min) return new Bound(false, 0, 0);
            return new Bound(true, min, max);
        }

        /// <summary>False when a constrained axis is outside
        /// <c>[min - tolerance, max + tolerance]</c>; the reason names the axis, the reported
        /// value and the limit it broke. An unknown envelope always returns true with a null
        /// reason - it has no evidence either way and must not invent one.</summary>
        public bool Contains(Vec3 machine, out string? reason)
        {
            reason = null;
            if (!Check(_x, machine.X, "X", out reason)) return false;
            if (!Check(_y, machine.Y, "Y", out reason)) return false;
            if (!Check(_z, machine.Z, "Z", out reason)) return false;
            return true;
        }

        bool Check(Bound b, double value, string letter, out string? reason)
        {
            reason = null;
            if (!b.Constrained) return true;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                // Not the envelope's question - the caller rejects non-finite coordinates
                // before it gets here - but never claim a non-finite value is inside.
                reason = letter + " " + F(value) + " mm is not a finite coordinate";
                return false;
            }
            if (value < b.Min - _tolerance)
            {
                reason = letter + " " + F(value) + " mm is " + F(b.Min - value)
                       + " mm below the machine minimum " + F(b.Min) + " mm";
                return false;
            }
            if (value > b.Max + _tolerance)
            {
                reason = letter + " " + F(value) + " mm is " + F(value - b.Max)
                       + " mm above the machine maximum " + F(b.Max) + " mm";
                return false;
            }
            return true;
        }

        /// <summary>The envelope in operator language, for the info output and the logs.</summary>
        public string Describe()
        {
            if (!Known) return "machine envelope unknown (the firmware reported no axis limits)";
            var sb = new StringBuilder();
            Append(sb, _x, "X");
            Append(sb, _y, "Y");
            Append(sb, _z, "Z");
            return sb.ToString();
        }

        void Append(StringBuilder sb, Bound b, string letter)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(letter).Append(' ');
            if (!b.Constrained) sb.Append("unknown");
            else sb.Append(F(b.Min)).Append(" to ").Append(F(b.Max)).Append(" mm");
        }

        static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    /// <summary>Display-side tuning for <see cref="LivePoseEligibility"/>. Not a safety
    /// gate: motion gates, calibration and the print log read observed telemetry directly
    /// and never see a decision from here (ARCHI §15).</summary>
    public sealed class PoseEligibilityOptions
    {
        /// <summary>Documented numerical tolerance on the firmware's own envelope:
        /// babystepping, a probe offset and rounding all put a legitimate coordinate
        /// slightly outside it. Small enough that a 389 mm excursion can never pass.</summary>
        public double EnvelopeToleranceMm = 2.0;
        /// <summary>False by default, and deliberately so: the job's own G28 and cancel.g
        /// unhome the axes for seconds at a time, and hiding the arm there made it flash in
        /// and out (2026-09-09). The last posed frame is held and only the trail is
        /// withheld. True restores the older hide-while-unhomed behaviour.</summary>
        public bool HideArmWhileUnhomed;
        public PoseEligibilityOptions Copy() { return (PoseEligibilityOptions)MemberwiseClone(); }
    }

    /// <summary>Decides what the display may do with one observation, and nothing else.</summary>
    /// <remarks>
    /// <para>Pure and allocation-light: it reads a snapshot and returns an answer. It never
    /// writes to the snapshot, never clamps or projects a coordinate and never substitutes
    /// one from another field - the raw observation survives the decision untouched, which
    /// is what keeps the diagnostics window, the print log and the operator's own count of
    /// rejected frames trustworthy.</para>
    /// <para>Decision order, each step earning its place:</para>
    /// <list type="number">
    /// <item><description>No snapshot at all: nothing has ever been observed, so there is no
    /// pose to hold - <see cref="PoseEligibility.Hide"/>.</description></item>
    /// <item><description>No usable position (the strict axis reader refused the reply, or
    /// X/Y/Z are not all present): hold with the parser's own reason, because the parser is
    /// the only party that can tell "absent" from "never mentioned".</description></item>
    /// <item><description>A non-finite coordinate: hold. Checked before the envelope so a
    /// NaN is never reported as an out-of-bounds excursion.</description></item>
    /// <item><description>Halted or off: hold. The firmware has stopped controlling the
    /// axes, so whatever it reports is not a pose to draw.</description></item>
    /// <item><description>Unhomed, only when <see cref="PoseEligibilityOptions.HideArmWhileUnhomed"/>
    /// asks for it.</description></item>
    /// <item><description>The envelope: a coordinate outside the firmware's OWN reported
    /// limits is held and marked <see cref="PoseDecision.RejectedByEnvelope"/>. This is the
    /// step the 2026-09-09 approach defect needed and the controller did not have.</description></item>
    /// </list>
    /// </remarks>
    public static class LivePoseEligibility
    {
        public static PoseDecision Decide(ObjectModelSnapshot? snap, PoseEligibilityOptions options)
        {
            var opt = options ?? new PoseEligibilityOptions();
            if (snap == null)
                return new PoseDecision(PoseEligibility.Hide, "no telemetry yet", false, false);

            var machine = snap.MachinePosition;
            if (!snap.PositionValid || !machine.HasValue)
            {
                string reason = snap.PositionInvalidReason ?? "no usable position";
                return new PoseDecision(PoseEligibility.Hold, reason, false, false);
            }
            var p = machine.Value;
            if (!Num.Finite(p))
                return new PoseDecision(PoseEligibility.Hold, "coordinates are not finite", false, false);

            if (snap.Status == MachineStatus.Halted)
                return new PoseDecision(PoseEligibility.Hold, "machine halted", false, false);
            if (snap.Status == MachineStatus.Off)
                return new PoseDecision(PoseEligibility.Hold, "machine off", false, false);

            if (opt.HideArmWhileUnhomed && !snap.AllHomed)
            {
                string reason = snap.Status == MachineStatus.Busy ? "homing in progress" : "axes not homed";
                return new PoseDecision(PoseEligibility.Hold, reason, false, false);
            }

            var envelope = MachineEnvelope.FromAxes(snap.Axes, opt.EnvelopeToleranceMm);
            if (envelope.Known && !envelope.Contains(p, out string? outside))
                return new PoseDecision(PoseEligibility.Hold,
                                        outside ?? "outside the machine envelope", true, true);

            return new PoseDecision(PoseEligibility.Show,
                                    envelope.Known ? null : envelope.Describe(),
                                    envelope.Known, false);
        }
    }
}
