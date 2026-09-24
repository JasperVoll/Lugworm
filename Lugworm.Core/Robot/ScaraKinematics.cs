using System;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Result of a SCARA inverse-kinematics solve. Field order follows the
    /// machine's joint naming, not numeric order: J1 = base rotation (deg),
    /// J3 = RELATIVE elbow angle θ2 between link 1 and link 2 (deg, elbow-branch
    /// dependent), J2 = prismatic carriage height (mm). The Potterbot firmware
    /// letters the vertical axis "J2", hence the unusual ordering.</summary>
    public readonly struct IkResult
    {
        public readonly double J1, J3, J2;   // resolved motor angles; J2 = Z carriage height (mm)
        public readonly double J1Raw, J1Resolved, J3Raw, J3Resolved;
        public readonly int ThetaWrapTurns, ArmMode;
        public readonly bool ThetaWrapAmbiguous;
        public readonly bool Valid;
        public readonly string Error;
        public IkResult(double j1, double j3, double j2, bool valid, string error)
            : this(j1, j3, j2, valid, error, j1, j1, 0, false, j3, j3, -1) { }

        public IkResult(double j1, double j3, double j2, bool valid, string error,
                        double j1Raw, double j1Resolved, int thetaWrapTurns,
                        bool thetaWrapAmbiguous, double j3Raw, double j3Resolved,
                        int armMode)
        {
            J1 = j1; J3 = j3; J2 = j2;
            J1Raw = j1Raw; J1Resolved = j1Resolved;
            J3Raw = j3Raw; J3Resolved = j3Resolved;
            ThetaWrapTurns = thetaWrapTurns; ArmMode = armMode;
            ThetaWrapAmbiguous = thetaWrapAmbiguous;
            Valid = valid; Error = error;
        }

        public bool Ambiguous => ThetaWrapAmbiguous;
    }

    /// <summary>SCARA inverse / forward kinematics. Two rotary links L1, L2 in XY plus
    /// a vertical J2 carriage. Direct port of the reference GCODE_Robot definition
    /// (3DPotter Potterbot Scara v4). Pure - no Rhino.</summary>
    public static class ScaraKinematics
    {
        /// <summary>Resolves a raw atan2-derived angle into the firmware's
        /// configured interval. The returned turn count is the exact whole-turn
        /// offset applied to the raw value.</summary>
        static bool TryResolveTheta(double thetaDeg, double min, double max,
                                           out double resolved, out int turns,
                                           out bool ambiguous)
        {
            resolved = thetaDeg;
            turns = 0;
            ambiguous = false;
            if (!Num.Finite(thetaDeg) || !Num.Finite(min) || !Num.Finite(max) || min > max)
                return false;

            if (resolved < min)
            {
                double offset = Math.Ceiling((min - resolved) / 360.0);
                if (offset > int.MaxValue) return false;
                turns = (int)offset;
                resolved += 360.0 * turns;
            }
            if (resolved > max)
            {
                double offset = Math.Ceiling((resolved - max) / 360.0);
                if (offset > int.MaxValue) return false;
                turns = -(int)offset;
                resolved += 360.0 * turns;
            }
            while (resolved < min) { resolved += 360.0; turns++; }
            while (resolved > max) { resolved -= 360.0; turns--; }

            double lower = Math.Ceiling((min - thetaDeg) / 360.0);
            double upper = Math.Floor((max - thetaDeg) / 360.0);
            ambiguous = upper >= lower && upper - lower + 1.0 > 1.0;
            return resolved >= min - 1e-9 && resolved <= max + 1e-9;
        }

        public static IkResult Inverse(double x, double y, double z,
                                       double l1, double l2, double baseHeight, int elbow = 0)
        {
            double j2 = z - baseHeight;
            if (!(l1 > 0) || !(l2 > 0))
                return new IkResult(0, 0, j2, false,
                    $"Invalid link lengths: L1={l1}, L2={l2} (both must be > 0)");
            if (!Num.Finite(x) || !Num.Finite(y) || !Num.Finite(z))
                return new IkResult(0, 0, j2, false, "Target contains NaN coordinates");
            double r = Math.Sqrt(x * x + y * y);
            double maxReach = l1 + l2;
            double minReach = Math.Abs(l1 - l2);

            if (r > maxReach)
                return new IkResult(0, 0, j2, false,
                    FormattableString.Invariant($"Target unreachable: distance {r:0.00}mm > max reach {maxReach:0.00}mm"));
            if (r < minReach)
                return new IkResult(0, 0, j2, false,
                    FormattableString.Invariant($"Target too close: distance {r:0.00}mm < min reach {minReach:0.00}mm"));
            if (r < 0.001)
                return new IkResult(0, 0, j2, false, "Target too close to base origin");

            double cos2 = (r * r - l1 * l1 - l2 * l2) / (2.0 * l1 * l2);
            cos2 = Math.Max(-1.0, Math.Min(1.0, cos2));
            double sin2sq = 1.0 - cos2 * cos2;
            if (sin2sq < 0) sin2sq = 0;
            double sin2 = elbow == 0 ? Math.Sqrt(sin2sq) : -Math.Sqrt(sin2sq);

            double theta2 = Math.Atan2(sin2, cos2);
            double theta1 = Math.Atan2(y, x) - Math.Atan2(l2 * sin2, l1 + l2 * cos2);
            double rawJ1 = theta1 * 180.0 / Math.PI;
            double rawJ3 = theta2 * 180.0 / Math.PI;
            TryResolveTheta(rawJ1, -180.0, 180.0,
                out double resolvedJ1, out int turns, out bool ambiguous);
            TryResolveTheta(rawJ3, -180.0, 180.0,
                out double resolvedJ3, out _, out _);

            return new IkResult(resolvedJ1, resolvedJ3, j2, true, "Solution found",
                                rawJ1, resolvedJ1, turns, ambiguous,
                                rawJ3, resolvedJ3, elbow == 0 ? 0 : 1);
        }

        /// <summary>RRF-style SCARA inverse. The preferred arm mode is tried
        /// first; if its resolved A/B angles are outside the firmware limits the
        /// other mode is tried. J1 is returned in the resolved motor domain.</summary>
        public static IkResult InverseRrf(double x, double y, double z,
                                          RobotLimits lim, int preferredArmMode)
        {
            if (lim == null) throw new ArgumentNullException(nameof(lim));
            double j2 = z - lim.BaseH;
            if (!(lim.L1 > 0) || !(lim.L2 > 0))
                return Invalid(j2, $"Invalid link lengths: L1={lim.L1}, L2={lim.L2} (both must be > 0)");
            if (!Num.Finite(x) || !Num.Finite(y) || !Num.Finite(z))
                return Invalid(j2, "Target contains NaN coordinates");

            double r = Math.Sqrt(x * x + y * y);
            double maxReach = lim.L1 + lim.L2;
            double minReach = Math.Abs(lim.L1 - lim.L2);
            if (r > maxReach)
                return Invalid(j2, FormattableString.Invariant($"Target unreachable: distance {r:0.00}mm > max reach {maxReach:0.00}mm"));
            if (r < minReach)
                return Invalid(j2, FormattableString.Invariant($"Target too close: distance {r:0.00}mm < min reach {minReach:0.00}mm"));
            if (r < 0.001)
                return Invalid(j2, "Target too close to base origin");

            double cos2 = (r * r - lim.L1 * lim.L1 - lim.L2 * lim.L2) / (2.0 * lim.L1 * lim.L2);
            cos2 = Math.Max(-1.0, Math.Min(1.0, cos2));
            double sin2sq = 1.0 - cos2 * cos2;
            if (sin2sq < 0) sin2sq = 0;

            int first = preferredArmMode == 0 ? 0 : 1;
            IkResult fallback = default;
            bool haveFallback = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int armMode = attempt == 0 ? first : 1 - first;
                double sin2 = armMode == 0 ? Math.Sqrt(sin2sq) : -Math.Sqrt(sin2sq);
                double rawJ3 = Math.Atan2(sin2, cos2) * 180.0 / Math.PI;
                double rawJ1 = (Math.Atan2(y, x) -
                    Math.Atan2(lim.L2 * sin2, lim.L1 + lim.L2 * cos2)) * 180.0 / Math.PI;

                bool aOk = TryResolveTheta(rawJ1, lim.J1Min, lim.J1Max,
                    out double resolvedJ1, out int turns, out bool ambiguous);
                bool bOk = TryResolveTheta(rawJ3, lim.J3Min, lim.J3Max,
                    out double resolvedJ3, out _, out _);
                var solution = new IkResult(resolvedJ1, resolvedJ3, j2, true,
                    aOk && bOk ? "RRF solution found" : "RRF solution (joint limits exceeded)",
                    rawJ1, resolvedJ1, turns, ambiguous, rawJ3, resolvedJ3, armMode);
                if (aOk && bOk) return solution;   // this mode keeps every joint legal
                if (!haveFallback) { fallback = solution; haveFallback = true; }  // keep the preferred branch
            }
            // Reachable geometry, but no arm mode keeps every joint inside A/B. Return
            // the preferred branch as a VALID solution: whether that is an error is a
            // JOINT-RANGE decision (CheckJoints, gated by CheckJointRanges), not an IK
            // failure. Reporting it here would break exploration mode and hide the
            // real dead-zone / mast diagnostics behind an "unreachable" message.
            return fallback;
        }

        static IkResult Invalid(double j2, string error)
            => new IkResult(0, 0, j2, false, error, 0, 0, 0, false, 0, 0, -1);

        public static (double x, double y, double z) Forward(double j1Deg, double j3Deg, double j2Height,
                                                             double l1, double l2, double baseHeight)
        {
            double j1 = j1Deg * Math.PI / 180.0;
            double j3 = j3Deg * Math.PI / 180.0;
            double x = l1 * Math.Cos(j1) + l2 * Math.Cos(j1 + j3);
            double y = l1 * Math.Sin(j1) + l2 * Math.Sin(j1 + j3);
            double z = j2Height + baseHeight;
            return (x, y, z);
        }

        /// <summary>XY of the elbow joint (end of link 1) for a given J1.</summary>
        public static (double x, double y) Elbow(double j1Deg, double l1)
        {
            double j1 = j1Deg * Math.PI / 180.0;
            return (l1 * Math.Cos(j1), l1 * Math.Sin(j1));
        }
    }
}
