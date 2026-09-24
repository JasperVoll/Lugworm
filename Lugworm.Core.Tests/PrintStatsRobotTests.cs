using System;
using System.Collections.Generic;
using System.Threading;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests
{
    public class PrintStatsRobotTests
    {
        // A straight 100 mm run in four 25 mm steps, all extruding at 25 mm/s.
        static (List<Vec3> pts, List<bool> ext) Path()
        {
            var pts = new List<Vec3>();
            var ext = new List<bool> { false };
            for (int i = 0; i <= 4; i++) pts.Add(new Vec3(500 + i * 25.0, -300, 10));
            for (int i = 0; i < 4; i++) ext.Add(true);
            return (pts, ext);
        }

        /// <summary>Joints that barely move: the machine is nowhere near a limit, so the
        /// estimate must be untouched.</summary>
        static List<double[]> SlowJoints(int n)
        {
            var j = new List<double[]>();
            for (int i = 0; i < n; i++) j.Add(new[] { i * 0.5, i * 0.25, 0.0 });
            return j;
        }

        /// <summary>Joints racing 40 deg per 25 mm step - the singularity signature.</summary>
        static List<double[]> FastJoints(int n)
        {
            var j = new List<double[]>();
            for (int i = 0; i < n; i++) j.Add(new[] { i * 40.0, 0.0, 0.0 });
            return j;
        }

        [Fact]
        public void NullJointLimitsLeavesEstimateUnchanged()
        {
            var (pts, ext) = Path();
            var baseline = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25);
            var withNull = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: null);

            Assert.Equal(baseline.TimeMin, withNull.TimeMin, 9);
            Assert.Equal(0, withNull.JointLimitedTimeMin);
            Assert.Equal(0, withNull.JointLimitedSegments);
        }

        [Fact]
        public void SlowJointsDoNotChangeTheEstimate()
        {
            var (pts, ext) = Path();
            var limits = new JointSpeedLimits(SlowJoints(pts.Count), maxJointSpeed: 120, maxFeedMmS: 150);
            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: limits);

            Assert.Equal(0, r.JointLimitedSegments);
            Assert.Equal(r.TimeMin, r.JointLimitedTimeMin, 9);
        }

        [Fact]
        public void RacingJointsSlowThePrintDown()
        {
            var (pts, ext) = Path();
            var limits = new JointSpeedLimits(FastJoints(pts.Count), maxJointSpeed: 120, maxFeedMmS: 150);
            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: limits);

            // 40 deg over 25 mm at 25 mm/s demands 40 deg/s... under the 120 limit, so
            // raise the demand until it binds: the cap is 120 * 25 / 40 = 75 mm/s > 25,
            // so this configuration must NOT bind.
            Assert.Equal(0, r.JointLimitedSegments);

            // Now a limit low enough to bind.
            var tight = new JointSpeedLimits(FastJoints(pts.Count), maxJointSpeed: 20, maxFeedMmS: 150);
            var rt = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: tight);

            Assert.Equal(4, rt.JointLimitedSegments);
            Assert.True(rt.JointLimitedTimeMin > rt.TimeMin,
                $"expected robot-limited {rt.JointLimitedTimeMin} > cartesian {rt.TimeMin}");
            // cap = 20 * 25 / 40 = 12.5 mm/s over 100 mm = 8 s
            Assert.Equal(8.0 / 60.0, rt.JointLimitedTimeMin, 6);

            // D4 (4.4.0): the "robot-limited" line carries a custom "+0.0;-0.0;0.0"
            // percent format - the one most likely to leak a decimal comma. Report()
            // is GH panel text, not UI, so it must stay dot-decimal under German.
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                string invariant = rt.Report();
                Assert.Contains("robot-limited", invariant);
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string german = rt.Report();
                Assert.Equal(invariant, german);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }

        [Fact]
        public void MaxFeedAloneCapsTheEstimate()
        {
            var (pts, ext) = Path();
            // Requested 25 mm/s, machine ceiling 10 mm/s, joints irrelevant.
            var limits = new JointSpeedLimits(SlowJoints(pts.Count), maxJointSpeed: 0, maxFeedMmS: 10);
            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: limits);

            Assert.Equal(4, r.JointLimitedSegments);
            Assert.Equal(10.0 / 60.0, r.JointLimitedTimeMin, 6);   // 100 mm / 10 mm/s = 10 s
        }

        [Fact]
        public void AccelerationAndJointLimitsComposeIntoOneEstimate()
        {
            var (pts, ext) = Path();
            var limits = new JointSpeedLimits(FastJoints(pts.Count), maxJointSpeed: 20, maxFeedMmS: 150);
            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 50, layerIds: null, jointLimits: limits);

            Assert.True(r.JointLimitedTimeMin >= r.TimeAccelMin,
                $"combined robot/accel time {r.JointLimitedTimeMin} must not undercut accel time {r.TimeAccelMin}");
            Assert.True(r.JointLimitedTimeMin > 8.0 / 60.0,
                "acceleration ramps must add time to the constant-speed robot-limited estimate");
        }

        [Fact]
        public void ZeroLimitsAreInactive()
        {
            var (pts, ext) = Path();
            var limits = new JointSpeedLimits(SlowJoints(pts.Count), maxJointSpeed: 0, maxFeedMmS: 0);
            Assert.False(limits.Active);

            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: limits);
            Assert.Equal(0, r.JointLimitedTimeMin);
        }

        [Fact]
        public void UnreachableSamplesAreSkippedNotPropagated()
        {
            var (pts, ext) = Path();
            var joints = SlowJoints(pts.Count);
            joints[2] = new[] { double.NaN, double.NaN, double.NaN };
            var limits = new JointSpeedLimits(joints, maxJointSpeed: 120, maxFeedMmS: 150);

            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: limits);

            Assert.False(double.IsNaN(r.JointLimitedTimeMin));
            Assert.True(r.JointLimitedTimeMin > 0);
        }

        [Fact]
        public void CapFeedNeverExceedsTheRequest()
        {
            var limits = new JointSpeedLimits(SlowJoints(4), maxJointSpeed: 100000, maxFeedMmS: 100000);
            Assert.Equal(25.0, limits.CapFeed(0, 1, 25.0, 25.0), 9);
        }

        [Fact]
        public void CapFeedIgnoresOutOfRangeIndices()
        {
            var limits = new JointSpeedLimits(SlowJoints(3), maxJointSpeed: 1, maxFeedMmS: 0);
            Assert.Equal(25.0, limits.CapFeed(-1, 0, 25.0, 25.0), 9);
            Assert.Equal(25.0, limits.CapFeed(0, 99, 25.0, 25.0), 9);
        }

        [Fact]
        public void CapFeedIsBoundedBelowSoTimeStaysFinite()
        {
            var joints = new List<double[]> { new[] { 0.0 }, new[] { 1e12 } };
            var limits = new JointSpeedLimits(joints, maxJointSpeed: 1, maxFeedMmS: 0);
            double capped = limits.CapFeed(0, 1, 1.0, 25.0);
            Assert.True(capped > 0, "cap must stay positive so the time sum stays finite");
            Assert.False(double.IsInfinity(1.0 / capped));
        }

        [Fact]
        public void FromMovesHonoursJointLimits()
        {
            // Parsed rather than hand-built: ParsedMove's public constructor sets the
            // start equal to the end, which would make every segment zero-length.
            var gcode = new[]
            {
                "G21", "G90", "G1 X500 Y-300 Z10 F1500",
                "G1 X525 Y-300 Z10 E1 F1500",
                "G1 X550 Y-300 Z10 E2 F1500",
                "G1 X575 Y-300 Z10 E3 F1500",
                "G1 X600 Y-300 Z10 E4 F1500",
            };
            var prog = GcodeParser.Parse(gcode);
            Assert.True(prog.Moves.Count >= 4);

            var loose = PrintStatsCalc.FromMoves(prog.Moves, 10, 3, 2.0, 25);
            Assert.Equal(0, loose.JointLimitedTimeMin);

            var joints = new List<double[]>();
            for (int i = 0; i <= prog.Moves.Count; i++) joints.Add(new[] { i * 40.0, 0.0, 0.0 });
            var tight = PrintStatsCalc.FromMoves(prog.Moves, 10, 3, 2.0, 25, 0, 0, null,
                new JointSpeedLimits(joints, maxJointSpeed: 20, maxFeedMmS: 150));

            Assert.True(tight.JointLimitedSegments > 0);
            Assert.True(tight.JointLimitedTimeMin > tight.TimeMin);
        }

        [Fact]
        public void ReportMentionsTheRobotLimitOnlyWhenItApplied()
        {
            var (pts, ext) = Path();
            var plain = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25);
            Assert.DoesNotContain("robot-limited", plain.Report());

            var tight = new JointSpeedLimits(FastJoints(pts.Count), maxJointSpeed: 20, maxFeedMmS: 150);
            var r = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: tight);
            Assert.Contains("robot-limited", r.Report());

            var loose = new JointSpeedLimits(SlowJoints(pts.Count), maxJointSpeed: 120, maxFeedMmS: 150);
            var unchanged = PrintStatsCalc.FromPoints(pts, ext, 10, 3, 2.0, 25,
                travelFeedMmS: 0, feedsMmS: null, accelMmS2: 0, layerIds: null, jointLimits: loose);
            Assert.DoesNotContain("robot-limited", unchanged.Report());
        }

        /// <summary>The shipped SCARA profile must actually carry usable ceilings -
        /// otherwise the component silently falls back to the Cartesian estimate.</summary>
        [Fact]
        public void ScaraProfileSuppliesSpeedCeilings()
        {
            var limits = RobotCatalog.Get("potterbot-scara-v4").CreateScaraLimits();
            Assert.True(limits.MaxJointSpeed > 0);
            Assert.True(limits.MaxFeed > 0);
        }
    }
}
