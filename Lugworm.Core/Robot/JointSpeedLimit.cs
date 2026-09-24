using System;
using System.Collections.Generic;

namespace Lugworm.Core.Robot
{
    /// <summary>
    /// Per-sample joint values plus the machine's speed ceilings, supplied to the
    /// print-time estimator so a Cartesian feed the robot cannot actually deliver is
    /// capped to one it can.
    ///
    /// <para><b>Cardinality:</b> <see cref="JointsDeg"/> must be index-for-index with
    /// the points/moves the estimator walks. Both come from
    /// <c>RobotSimulation.Solve</c>, which already produces exactly that.</para>
    ///
    /// <para><b>Units wart, inherited deliberately:</b> <see cref="MaxJointSpeedMm"/>
    /// is one scalar compared against °/s for the rotary joints and mm/s for the
    /// prismatic one, exactly as <c>RobotChecks.Run</c> has always done. Reusing the
    /// shipped convention beats silently redefining what the number means; splitting
    /// it is a separate change with its own migration.</para>
    /// </summary>
    public sealed class JointSpeedLimits
    {
        public JointSpeedLimits(IReadOnlyList<double[]> jointsDeg, double maxJointSpeed, double maxFeedMmS)
        {
            JointsDeg = jointsDeg ?? throw new ArgumentNullException(nameof(jointsDeg));
            MaxJointSpeedMm = maxJointSpeed;
            MaxFeedMmS = maxFeedMmS;
        }

        public IReadOnlyList<double[]> JointsDeg { get; }
        public double MaxJointSpeedMm { get; }
        public double MaxFeedMmS { get; }

        public bool Active => JointsDeg.Count > 1 && (MaxJointSpeedMm > 0 || MaxFeedMmS > 0);

        /// <summary>
        /// Largest feed (mm/s) the robot can sustain over the segment from sample
        /// <paramref name="i0"/> to <paramref name="i1"/>, never above
        /// <paramref name="requestedFeedMmS"/>.
        ///
        /// <para>Inverts the relationship <c>RobotChecks</c> already checks against:
        /// <c>joint_speed = (Δjoint / Δcartesian) - feed</c>. Near a singularity a
        /// small Cartesian step demands a large Δjoint, the cap collapses, and the
        /// segment correctly takes longer - the effect a Cartesian-only estimate
        /// misses entirely.</para>
        /// </summary>
        public double CapFeed(int i0, int i1, double distMm, double requestedFeedMmS)
        {
            double capped = requestedFeedMmS;
            if (MaxFeedMmS > 0 && capped > MaxFeedMmS) capped = MaxFeedMmS;
            if (MaxJointSpeedMm <= 0 || distMm <= 1e-9) return capped;
            if (i0 < 0 || i1 < 0 || i0 >= JointsDeg.Count || i1 >= JointsDeg.Count) return capped;

            double[] a = JointsDeg[i0], b = JointsDeg[i1];
            if (a == null || b == null) return capped;
            int n = Math.Min(a.Length, b.Length);
            for (int k = 0; k < n; k++)
            {
                double delta = Math.Abs(b[k] - a[k]);
                // An unreachable sample carries NaN. Skipping it leaves the Cartesian
                // estimate in place rather than poisoning the whole total.
                if (double.IsNaN(delta) || double.IsInfinity(delta) || delta <= 1e-12) continue;
                double allowed = MaxJointSpeedMm * distMm / delta;
                if (allowed < capped) capped = allowed;
            }
            return capped > 1e-6 ? capped : 1e-6;
        }
    }
}
