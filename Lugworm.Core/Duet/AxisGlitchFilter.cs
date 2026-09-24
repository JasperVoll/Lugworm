using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>What the filter decided about one snapshot.</summary>
    public enum GlitchVerdict
    {
        /// <summary>Nothing to repair: the reply was self-consistent, or the disagreement
        /// was the ordinary "userPosition leads during a long move" and the machine value
        /// agreed with its neighbours.</summary>
        None,
        /// <summary>One axis of <c>machinePosition</c> was off its neighbours while the same
        /// reply's <c>userPosition</c> sat on them; that axis now carries the user value.</summary>
        Repaired,
        /// <summary>One axis of <c>machinePosition</c> was off its neighbours and no witness
        /// in the same reply agreed with them either. Left untouched and flagged.</summary>
        SuspectUnrepaired,
        /// <summary>A candidate that never got a successor (disconnect, flush): released
        /// untouched because the rule could not run.</summary>
        Unresolved,
    }

    /// <summary>One snapshot released by the filter, in acquisition order: the filtered
    /// view to use, paired with the reply exactly as it was polled.</summary>
    /// <remarks>The filter never writes into what it was handed, so raw and filtered
    /// telemetry stay comparable after the fact - which is the whole point of keeping the
    /// pair. The two properties are the same object whenever nothing was corrected.</remarks>
    public sealed class GlitchResolution
    {
        /// <summary>The snapshot the position consumers must use: a corrected copy when
        /// <see cref="WasCorrected"/> is true, otherwise the pushed snapshot itself.</summary>
        public ObjectModelSnapshot Snapshot { get; }
        /// <summary>The snapshot exactly as it was pushed. It still carries the coordinates
        /// the firmware reported and a null
        /// <see cref="ObjectModelSnapshot.PositionRepair"/>.</summary>
        public ObjectModelSnapshot RawSnapshot { get; }
        /// <summary>True when <see cref="Snapshot"/> is a corrected copy of
        /// <see cref="RawSnapshot"/> rather than that object itself - the verdict was
        /// <see cref="GlitchVerdict.Repaired"/> or
        /// <see cref="GlitchVerdict.SuspectUnrepaired"/>.</summary>
        public bool WasCorrected => !ReferenceEquals(Snapshot, RawSnapshot);
        public GlitchVerdict Verdict { get; }
        public string? Axis { get; }
        public double RawValue { get; }
        public double UsedValue { get; }
        public string? Note { get; }

        public GlitchResolution(ObjectModelSnapshot snapshot, ObjectModelSnapshot rawSnapshot,
                                GlitchVerdict verdict, string? axis,
                                double rawValue, double usedValue, string? note)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            RawSnapshot = rawSnapshot ?? throw new ArgumentNullException(nameof(rawSnapshot));
            Verdict = verdict; Axis = axis; RawValue = rawValue; UsedValue = usedValue; Note = note;
        }
    }

    public sealed class AxisGlitchOptions
    {
        /// <summary>A candidate needs exactly one axis whose machine and user values differ
        /// by more than this, mm.</summary>
        public double DisagreementMm = 4.0;
        /// <summary>... while the other position axes agree within this, mm.</summary>
        public double AgreementMm = 1.0;
        /// <summary>The machine value must be at least this far from the midpoint of its
        /// neighbours to count as a glitch, mm.</summary>
        public double NeighbourResidualMm = 5.0;
        /// <summary>... and the user value within this of that midpoint to be trusted as
        /// the repair, mm.</summary>
        public double WitnessResidualMm = 2.5;
        /// <summary>Neighbours farther apart in time than this do not bracket a candidate.</summary>
        public double MaxNeighbourGapSeconds = 2.0;
        public AxisGlitchOptions Copy() => (AxisGlitchOptions)MemberwiseClone();
    }

    /// <summary>Repairs the single-axis <c>machinePosition</c> glitch the 2026-09-09 dome
    /// capture localized to the firmware reply, and nothing else.</summary>
    /// <remarks>
    /// <para>Evidence (raw <c>rr_model?key=move.axes</c> bodies, 1132 polls at 250 ms):
    /// in 6 % of replies ONE of X or Y carried a <c>machinePosition</c> 15-53 mm off the
    /// contour while the other axis was exact and the same reply's <c>userPosition</c> for
    /// the bad axis sat on the contour (acquisition 412: X machine 199.144, X user 229.483,
    /// neighbours 224.7 and 234.4). A SCARA cannot report one Cartesian axis right and the
    /// other wrong from one consistent pair of joint angles, and the operator confirmed the
    /// arm never made these excursions: the field is inconsistent inside the reply.</para>
    /// <para>The rule needs one poll of look-ahead, because <c>userPosition</c> also leads
    /// <c>machinePosition</c> legitimately during a long move (it is the end of the current
    /// move: during the startup travel it sat at the destination for seconds). A glitch
    /// returns on the next poll; a travel move does not. So only a snapshot in which exactly
    /// one axis disagrees is HELD until its successor arrives; every other snapshot passes
    /// through at once. A held snapshot is repaired only when its machine value is off the
    /// midpoint of its neighbours AND the user value of the same reply is on it - the
    /// firmware's own second witness, never an interpolation, never the intended path. The
    /// raw value stays in the note, the CSV quality column and the diagnostics window.</para>
    /// <para>A repair is a new snapshot, never a write into the one that was pushed: the
    /// session publishes a poll the moment it arrives and this rule can only judge it one
    /// poll later, so repairing in place would rewrite state the rest of the program already
    /// holds and would leave no raw reading to compare the filtered one against. Every
    /// released <see cref="GlitchResolution"/> therefore carries both
    /// (<see cref="GlitchResolution.Snapshot"/>, <see cref="GlitchResolution.RawSnapshot"/>).</para>
    /// <para>Pure and single-threaded by contract: the poll loop owns it.</para>
    /// </remarks>
    public sealed class AxisGlitchFilter
    {
        readonly AxisGlitchOptions _opt;
        readonly List<GlitchResolution> _out = new List<GlitchResolution>(2);
        ObjectModelSnapshot? _previous;    // last released snapshot with a valid position
        ObjectModelSnapshot? _held;
        string? _heldAxis;

        public int Candidates { get; private set; }
        public int Repaired { get; private set; }
        public int Suspect { get; private set; }
        public int Unresolved { get; private set; }
        /// <summary>True while a snapshot is waiting for its successor.</summary>
        public bool Holding => _held != null;

        public AxisGlitchFilter(AxisGlitchOptions? options = null)
        {
            _opt = options?.Copy() ?? new AxisGlitchOptions();
        }

        /// <summary>Offers the next poll. Returns the snapshots released by it, in order:
        /// a previously held candidate first (resolved against this one), then this one
        /// unless it is held itself.</summary>
        public IReadOnlyList<GlitchResolution> Push(ObjectModelSnapshot snap)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            _out.Clear();
            var machine = snap.MachinePosition;
            var user = snap.UserPosition;

            if (_held != null)
            {
                Resolve(_held, _heldAxis!, snap);
                _held = null; _heldAxis = null;
            }

            string? axis = machine.HasValue && user.HasValue ? DisagreeingAxis(machine.Value, user.Value) : null;
            bool bracketable = axis != null && _previous != null && WithinGap(_previous, snap);
            if (bracketable)
            {
                Candidates++;
                _held = snap; _heldAxis = axis;
                return _out;
            }
            Release(snap, GlitchVerdict.None, null, 0, 0, null);
            return _out;
        }

        /// <summary>Releases a held candidate without a successor (disconnect, end of run).</summary>
        public IReadOnlyList<GlitchResolution> Flush()
        {
            _out.Clear();
            if (_held != null)
            {
                Unresolved++;
                Release(_held, GlitchVerdict.Unresolved, _heldAxis, 0, 0,
                        "single-axis machine/user disagreement on " + _heldAxis + " could not be checked - no successor");
                _held = null; _heldAxis = null;
            }
            return _out;
        }

        /// <summary>Forgets everything (new connection).</summary>
        public void Reset()
        {
            _held = null; _heldAxis = null; _previous = null;
        }

        void Resolve(ObjectModelSnapshot held, string axis, ObjectModelSnapshot next)
        {
            var prev = _previous;
            var nextMachine = next.MachinePosition;
            if (prev == null || !nextMachine.HasValue || !WithinGap(held, next) || !prev.MachinePosition.HasValue)
            {
                Unresolved++;
                Release(held, GlitchVerdict.Unresolved, axis, 0, 0,
                        "single-axis machine/user disagreement on " + axis + " could not be checked - no bracketing neighbour");
                return;
            }
            double p = Component(prev.MachinePosition.Value, axis);
            double n = Component(nextMachine.Value, axis);
            double midpoint = (p + n) / 2.0;
            var heldAxis = held.Axis(axis);
            if (heldAxis == null)
            {
                Release(held, GlitchVerdict.None, null, 0, 0, null);
                return;
            }
            double raw = heldAxis.MachinePosition;
            double witness = heldAxis.UserPosition;
            double residualMachine = Math.Abs(raw - midpoint);
            double residualWitness = Math.Abs(witness - midpoint);
            if (residualMachine <= _opt.NeighbourResidualMm)
            {
                // The machine value sits between its neighbours: the disagreement was the
                // user position leading a long move. Nothing to repair.
                Release(held, GlitchVerdict.None, null, 0, 0, null);
                return;
            }
            if (residualWitness <= _opt.WitnessResidualMm)
            {
                string note = axis + " machinePosition " + F(raw) + " replaced by userPosition " + F(witness)
                            + " from the same reply (neighbours " + F(p) + " and " + F(n)
                            + "; single-axis glitch in the firmware reply)";
                Repaired++;
                Release(held.WithPositionRepair(note, axis, witness),
                        GlitchVerdict.Repaired, axis, raw, witness, note, held);
                return;
            }
            Suspect++;
            string why = axis + " machinePosition " + F(raw) + " is " + F(residualMachine)
                       + " mm off its neighbours and userPosition " + F(witness) + " does not agree either - left as reported";
            Release(held.WithPositionRepair(why), GlitchVerdict.SuspectUnrepaired, axis, raw, raw, why, held);
        }

        /// <summary>Releases one resolution. <paramref name="asPolled"/> is the snapshot the
        /// caller pushed, given only when <paramref name="filtered"/> is a corrected copy of
        /// it. The neighbour the NEXT candidate is bracketed against is the filtered view, the
        /// same value the former in-place repair left behind - so the decisions the tests pin
        /// are unchanged by no longer mutating.</summary>
        void Release(ObjectModelSnapshot filtered, GlitchVerdict verdict, string? axis, double raw, double used,
                     string? note, ObjectModelSnapshot? asPolled = null)
        {
            _out.Add(new GlitchResolution(filtered, asPolled ?? filtered, verdict, axis, raw, used, note));
            if (filtered.MachinePosition.HasValue) _previous = filtered;
        }

        string? DisagreeingAxis(Vec3 machine, Vec3 user)
        {
            double dx = Math.Abs(machine.X - user.X);
            double dy = Math.Abs(machine.Y - user.Y);
            double dz = Math.Abs(machine.Z - user.Z);
            int far = 0; string? which = null;
            if (dx > _opt.DisagreementMm) { far++; which = "X"; }
            if (dy > _opt.DisagreementMm) { far++; which = "Y"; }
            if (dz > _opt.DisagreementMm) { far++; which = "Z"; }
            if (far != 1) return null;
            // the other two must agree; a whole-position lead (long travel) is not a glitch
            if (which != "X" && dx > _opt.AgreementMm) return null;
            if (which != "Y" && dy > _opt.AgreementMm) return null;
            if (which != "Z" && dz > _opt.AgreementMm) return null;
            return which;
        }

        bool WithinGap(ObjectModelSnapshot a, ObjectModelSnapshot b)
        {
            DateTime ta = Utc(a), tb = Utc(b);
            if (ta == default(DateTime) || tb == default(DateTime)) return true;
            double gap = Math.Abs((tb - ta).TotalSeconds);
            return gap <= _opt.MaxNeighbourGapSeconds;
        }

        static DateTime Utc(ObjectModelSnapshot s)
        {
            var stamp = s.Acquisition;
            if (stamp != null && stamp.ResponseReceivedUtc != default(DateTime)) return stamp.ResponseReceivedUtc;
            return s.TakenUtc;
        }

        static double Component(Vec3 v, string axis)
            => axis == "X" ? v.X : axis == "Y" ? v.Y : v.Z;

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
