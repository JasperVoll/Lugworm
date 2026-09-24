using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests
{
    /// <summary>The approach defect of run 20260909-131323-d30110: for 15 s the raw
    /// <c>rr_model?key=move.axes</c> bodies reported Z down to -389.314 mm while every axis
    /// was flagged homed, <c>state.status</c> was Processing and the SAME bodies declared
    /// the machine's own Z envelope as <c>min:0 max:400</c>. The display posed the arm from
    /// those coordinates because the only checks were homed/halted/off, and
    /// <c>ScaraKinematics.InverseRrf</c> solves XY and assigns <c>j2 = z - BaseH</c>, so a
    /// successful IK result never validated them.</summary>
    /// <remarks>
    /// <para>These tests pin the decision, not a repair. Nothing here may edit, clamp or
    /// substitute a coordinate: the eligibility answer is only "may the display pose the arm
    /// from this observation, and if not, why not", and a refused observation must stay
    /// countable so a sustained real fault cannot vanish from the operator's view.</para>
    /// <para>The envelope comes from the firmware's own per-axis min/max, never from an
    /// assumed print bed at Z=0: on this machine X is <c>min:-300 max:10000</c> and Y is
    /// <c>min:-10000 max:0</c>, so both a negative X and a negative Y are perfectly legal
    /// coordinates. When the bounds are not reported at all - the legacy rr_status path
    /// carries none - the answer is "unknown", which is neither a pass nor a failure.</para>
    /// </remarks>
    public class PoseEligibilityTests
    {
        static AxisState Ax(string letter, double machine, double min, double max, bool homed = true)
            => new AxisState { Letter = letter, MachinePosition = machine, UserPosition = machine, Homed = homed, Min = min, Max = max };

        /// <summary>A snapshot shaped exactly like the run's bodies: the Potterbot's real
        /// envelope, every axis homed, status Processing.</summary>
        static ObjectModelSnapshot Snap(double x, double y, double z,
                                        MachineStatus status = MachineStatus.Processing,
                                        bool homed = true)
        {
            return new ObjectModelSnapshot
            {
                Status = status,
                AllHomed = homed,
                Axes = new[]
                {
                    Ax("X", x, -300, 10000, homed),
                    Ax("Y", y, -10000, 0, homed),
                    Ax("Z", z, 0, 400, homed),
                },
            };
        }

        // ---- envelope reading ----------------------------------------------------------

        [Fact]
        public void Envelope_reads_the_machines_own_per_axis_min_and_max()
        {
            var e = MachineEnvelope.FromAxes(Snap(0, -100, 10).Axes, 2.0);
            Assert.True(e.Known);
            // acquisition 6424: the last coordinate before the excursion, Z exactly at max
            Assert.True(e.Contains(new Vec3(195.959, -498.675, 400), out string? ok));
            Assert.Null(ok);
            // acquisition 6487: 389 mm below the firmware's own Z minimum
            Assert.False(e.Contains(new Vec3(336.877, -298.976, -389.314), out string? why));
            Assert.NotNull(why);
            Assert.Contains("Z", why!, StringComparison.Ordinal);
            Assert.Contains("-389.314", why!, StringComparison.Ordinal);
            Assert.Contains("0", why!, StringComparison.Ordinal);
        }

        [Fact]
        public void Envelope_is_unknown_when_the_firmware_reports_no_bounds()
        {
            // legacy rr_status: AxisState.Min/Max are never assigned, so they are 0/0.
            var axes = new List<AxisState>
            {
                Ax("X", 100, 0, 0), Ax("Y", -100, 0, 0), Ax("Z", 5, 0, 0),
            };
            var e = MachineEnvelope.FromAxes(axes, 2.0);
            Assert.False(e.Known);
            // unknown is reported as unknown: it must not refuse and must not silently pass
            Assert.True(e.Contains(new Vec3(100, -100, -9999), out string? why));
            Assert.Null(why);
            Assert.Contains("unknown", e.Describe(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_degenerate_or_non_finite_axis_pair_is_unconstrained_not_zero_sized()
        {
            // max <= min is not an envelope of zero height around the origin
            var partly = new List<AxisState>
            {
                Ax("X", 100, 50, 50),                                      // degenerate
                Ax("Y", -100, double.NaN, 0),                              // non-finite
                Ax("Z", 5, 0, 400),                                        // the only real pair
            };
            var e = MachineEnvelope.FromAxes(partly, 2.0);
            Assert.True(e.Known);                                           // Z alone is enough
            Assert.True(e.Contains(new Vec3(-99999, 99999, 5), out _));     // X and Y unconstrained
            Assert.False(e.Contains(new Vec3(0, 0, -50), out string? why));
            Assert.Contains("Z", why!, StringComparison.Ordinal);
        }

        [Fact]
        public void The_tolerance_forgives_babystepping_but_never_the_excursion()
        {
            var e = MachineEnvelope.FromAxes(Snap(0, -100, 10).Axes, 2.0);
            Assert.True(e.Contains(new Vec3(0, -100, -1.9), out _));        // probe offset / babystep
            Assert.False(e.Contains(new Vec3(0, -100, -2.1), out _));
            Assert.True(e.Contains(new Vec3(0, -100, 401.9), out _));
            Assert.False(e.Contains(new Vec3(0, -100, 402.1), out _));
        }

        [Fact]
        public void A_negative_coordinate_inside_a_negative_axis_minimum_is_legal()
        {
            // X min is -300 and Y max is 0 on this machine: "negative" is not "out of bounds".
            var e = MachineEnvelope.FromAxes(Snap(0, 0, 0).Axes, 2.0);
            Assert.True(e.Contains(new Vec3(-299, -9999, 0), out _));
            Assert.False(e.Contains(new Vec3(-303, -9999, 0), out string? why));
            Assert.Contains("X", why!, StringComparison.Ordinal);
        }

        // ---- the decision order --------------------------------------------------------

        [Fact]
        public void No_telemetry_hides_everything()
        {
            var d = LivePoseEligibility.Decide(null, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hide, d.Eligibility);
            Assert.False(d.MayPose);
            Assert.False(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
            Assert.Equal("no telemetry yet", d.Reason);
        }

        [Fact]
        public void An_unusable_position_holds_with_the_parsers_own_reason()
        {
            var snap = new ObjectModelSnapshot
            {
                Status = MachineStatus.Processing,
                AllHomed = true,
                Axes = new[] { Ax("X", 1, -300, 10000), Ax("Y", -1, -10000, 0) },
            };
            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, d.Eligibility);
            Assert.False(d.RejectedByEnvelope);
            Assert.Equal(snap.PositionInvalidReason, d.Reason);
            Assert.Contains("Z", d.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void A_non_finite_coordinate_holds_and_is_not_blamed_on_the_envelope()
        {
            var snap = Snap(1, -1, 1);
            snap.Axes[2].MachinePosition = double.NaN;
            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, d.Eligibility);
            Assert.False(d.RejectedByEnvelope);
            Assert.Equal("coordinates are not finite", d.Reason);
        }

        [Fact]
        public void Halted_and_off_hold_before_the_envelope_is_consulted()
        {
            var halted = LivePoseEligibility.Decide(Snap(0, -100, -389.314, MachineStatus.Halted),
                                                    new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, halted.Eligibility);
            Assert.Equal("machine halted", halted.Reason);
            Assert.False(halted.RejectedByEnvelope);

            var off = LivePoseEligibility.Decide(Snap(0, -100, 5, MachineStatus.Off),
                                                 new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, off.Eligibility);
            Assert.Equal("machine off", off.Reason);
        }

        [Fact]
        public void Unhomed_holds_only_when_the_option_asks_and_names_homing_while_busy()
        {
            var opts = new PoseEligibilityOptions { HideArmWhileUnhomed = true };
            var busy = LivePoseEligibility.Decide(Snap(0, -100, 5, MachineStatus.Busy, homed: false), opts);
            Assert.Equal(PoseEligibility.Hold, busy.Eligibility);
            Assert.Equal("homing in progress", busy.Reason);

            var idle = LivePoseEligibility.Decide(Snap(0, -100, 5, MachineStatus.Idle, homed: false), opts);
            Assert.Equal(PoseEligibility.Hold, idle.Eligibility);
            Assert.Equal("axes not homed", idle.Reason);

            // default: the job's own G28 and cancel.g unhome for seconds; the arm must not flash
            var kept = LivePoseEligibility.Decide(Snap(0, -100, 5, MachineStatus.Busy, homed: false),
                                                  new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, kept.Eligibility);
            Assert.True(kept.MayPose);
        }

        [Fact]
        public void The_approach_coordinate_is_refused_by_the_envelope_and_counted_as_such()
        {
            // acquisition 6487, verbatim from the raw body
            var d = LivePoseEligibility.Decide(Snap(336.877, -298.976, -389.314), new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, d.Eligibility);
            Assert.False(d.MayPose);
            Assert.True(d.EnvelopeKnown);
            Assert.True(d.RejectedByEnvelope);
            Assert.Contains("Z", d.Reason!, StringComparison.Ordinal);
            Assert.Contains("-389.314", d.Reason!, StringComparison.Ordinal);
        }

        [Fact]
        public void A_refused_observation_is_never_edited()
        {
            var snap = Snap(336.877, -298.976, -389.314);
            LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(-389.314, snap.Axis("Z")!.MachinePosition, 9);
            Assert.Equal(336.877, snap.Axis("X")!.MachinePosition, 9);
            Assert.Equal(-298.976, snap.Axis("Y")!.MachinePosition, 9);
            Assert.Equal(0, snap.Axis("Z")!.Min, 9);
            Assert.Equal(400, snap.Axis("Z")!.Max, 9);
        }

        [Fact]
        public void An_in_envelope_homed_observation_shows_with_a_known_envelope()
        {
            var d = LivePoseEligibility.Decide(Snap(195.959, -498.675, 400), new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.True(d.MayPose);
            Assert.True(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
            Assert.Null(d.Reason);
        }

        [Fact]
        public void Unknown_bounds_show_and_say_so_rather_than_rejecting()
        {
            var snap = new ObjectModelSnapshot
            {
                Status = MachineStatus.Processing,
                AllHomed = true,
                Axes = new[] { Ax("X", 100, 0, 0), Ax("Y", -100, 0, 0), Ax("Z", -50, 0, 0) },
            };
            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.False(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
            Assert.NotNull(d.Reason);
            Assert.Contains("unknown", d.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Options_copy_is_independent()
        {
            var a = new PoseEligibilityOptions { EnvelopeToleranceMm = 5.5, HideArmWhileUnhomed = true };
            var b = a.Copy();
            a.EnvelopeToleranceMm = 1.0;
            a.HideArmWhileUnhomed = false;
            Assert.Equal(5.5, b.EnvelopeToleranceMm, 9);
            Assert.True(b.HideArmWhileUnhomed);
        }

        [Fact]
        public void A_negative_but_in_envelope_axis_is_shown_not_refused()
        {
            // A machine whose Z really does travel below zero (min -50). The rule is the
            // firmware's own limit, never an assumed bed at Z=0.
            var snap = new ObjectModelSnapshot
            {
                Status = MachineStatus.Processing,
                AllHomed = true,
                Axes = new[] { Ax("X", -250, -300, 10000), Ax("Y", -9000, -10000, 0), Ax("Z", -40, -50, 400) },
            };
            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.True(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
        }
    }
}
