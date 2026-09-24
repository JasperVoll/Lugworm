using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    /// <summary>One requested relative jog. Exactly one axis may be non-zero.</summary>
    public sealed class JogRequest
    {
        public double Dx, Dy, Dz;
        public double FeedMmS = 20.0;
    }

    /// <summary>Outcome of planning a jog: either refusal (with the reason) or the
    /// G-code to send plus the validated absolute target.</summary>
    public sealed class JogPlan
    {
        public bool Ok;
        public string? RefusalReason;
        public string Gcode = "";
        public double TargetX, TargetY, TargetZ;
        public ArmConfiguration FinalConfiguration = ArmConfiguration.Unknown;
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>The jog safety core - pure and unit-tested, deliberately outside the
    /// UI so no component can jog without passing it. Validates machine state
    /// (fresh snapshot, homed, idle) and the ABSOLUTE target (current machine
    /// position + delta) against the full SCARA check set before emitting G-code.</summary>
    public static class JogPlanner
    {
        /// <summary>Allowed jog increments (mm). Anything else is refused.</summary>
        public static readonly double[] StepWhitelist = { 0.1, 1.0, 10.0, 50.0 };

        public static JogPlan Plan(ObjectModelSnapshot? snap, JogRequest req, RobotLimits lim,
                                   int elbow = 0, double maxFeedMmS = 50.0, double maxSnapshotAgeS = 3.0,
                                   DateTime? notBeforeUtc = null, bool maintenanceOverride = false,
                                   ArmConfiguration? sessionConfiguration = null,
                                   RobotMechanism mechanism = RobotMechanism.Scara)
        {
            var plan = new JogPlan();

            string? gate = StateGate.Refuse(snap, maxSnapshotAgeS, requireIdle: true,
                allowPaused: maintenanceOverride, requireHomed: true, notBeforeUtc: notBeforeUtc, activity: "jogging");
            if (gate != null) return Refuse(plan, gate);

            var ax = snap!.Axis("X"); var ay = snap.Axis("Y"); var az = snap.Axis("Z");
            if (ax == null || ay == null || az == null)
                return Refuse(plan, "object model is missing X/Y/Z axes");
            if (!Num.Finite(ax.MachinePosition) || !Num.Finite(ay.MachinePosition) || !Num.Finite(az.MachinePosition))
                return Refuse(plan, "machine position is not finite");

            // exactly one axis per press, step from the whitelist
            int moving = (IsNonZero(req.Dx) ? 1 : 0) + (IsNonZero(req.Dy) ? 1 : 0) + (IsNonZero(req.Dz) ? 1 : 0);
            if (moving == 0) return Refuse(plan, "zero jog distance");
            if (moving > 1) return Refuse(plan, "jog one axis at a time");
            double step = Math.Abs(IsNonZero(req.Dx) ? req.Dx : IsNonZero(req.Dy) ? req.Dy : req.Dz);
            if (!InWhitelist(step))
                return Refuse(plan, $"step {step.ToString("0.###", CultureInfo.InvariantCulture)}mm is not in " +
                                    $"the allowed set ({string.Join(", ", StepWhitelist)})");

            if (!(req.FeedMmS > 0))
                return Refuse(plan, "feed must be > 0");
            double feedCap = Math.Min(maxFeedMmS, lim.MaxFeed);
            if (req.FeedMmS > feedCap + 1e-9)
                return Refuse(plan, FormattableString.Invariant($"feed {req.FeedMmS:0.#}mm/s exceeds the cap {feedCap:0.#}mm/s"));

            // validate the ABSOLUTE machine-frame target with the full check set,
            // plus the swept segment from the current position (a legal-endpoint
            // chord can still cross the dead-zone annulus or sweep the mast)
            plan.TargetX = ax.MachinePosition + req.Dx;
            plan.TargetY = ay.MachinePosition + req.Dy;
            plan.TargetZ = az.MachinePosition + req.Dz;
            var current = new Vec3(ax.MachinePosition, ay.MachinePosition, az.MachinePosition);
            if (mechanism == RobotMechanism.Cartesian)
            {
                // Cartesian machine: the envelope box is the whole geometric truth.
                // No IK, no arm configuration, no dead zone, no mast - and a single
                // straight jog cannot leave a convex box between legal endpoints.
                var cart = CartesianChecks.RunPoints(
                    new[] { current, new Vec3(plan.TargetX, plan.TargetY, plan.TargetZ) }, lim);
                foreach (var p in cart.Problems)
                {
                    if (p.Sev == Severity.Error)
                        return Refuse(plan, "target rejected: " + p.Message);
                    plan.Warnings.Add(p.Message);
                }
            }
            else
            {
                var seed = sessionConfiguration.HasValue
                    ? sessionConfiguration.Value
                    : ArmConfiguration.Seed(elbow);
                var check = RobotChecks.RunPoints(
                    new[] { current, new Vec3(plan.TargetX, plan.TargetY, plan.TargetZ) }, lim, seed);
                plan.FinalConfiguration = check.FinalConfiguration;
                foreach (var p in check.Problems)
                {
                    if (p.Sev == Severity.Error)
                        return Refuse(plan, "target rejected: " + p.Message);
                    if (p.Kind == ProblemKind.MastClearance)
                        return Refuse(plan, "target rejected: mast clearance is a blocking safety finding");
                    plan.Warnings.Add(p.Message);
                }

                // singularity proximity (RunPoints validates limits only; Run's warning
                // belongs here too - a jog INTO a singular region makes J1 race)
                double reachMax = lim.ReachMax > 0 ? lim.ReachMax : lim.L1 + lim.L2;
                double reachMin = lim.ReachMin > 0 ? lim.ReachMin : Math.Abs(lim.L1 - lim.L2);
                double r = Math.Sqrt(plan.TargetX * plan.TargetX + plan.TargetY * plan.TargetY);
                if (r > reachMax * 0.98)
                    plan.Warnings.Add(FormattableString.Invariant($"near max reach ({r:0.0}/{reachMax:0.0}mm) - singular, J1 races"));
                else if (r < reachMin * 1.02)
                    plan.Warnings.Add(FormattableString.Invariant($"near dead-zone ({r:0.0}/{reachMin:0.0}mm) - inner singularity, joints race"));
            }

            // ABSOLUTE machine-frame move (G53 strips work offsets), bracketed to
            // restore the caller's modal state. Emitting the validated absolute
            // target - not the relative delta - makes a duplicate press on a stale
            // snapshot idempotent: it re-sends the SAME target instead of walking
            // the machine past the envelope one unvalidated delta at a time.
            string d = FormatAbsAxis(req, plan);
            string f = FormatFeed(req.FeedMmS);
            plan.Gcode = $"M120\nG90\nG53 G1 {d} F{f}\nM121";
            plan.Ok = true;
            return plan;
        }

        static string FormatAbsAxis(JogRequest req, JogPlan plan)
        {
            var inv = CultureInfo.InvariantCulture;
            if (IsNonZero(req.Dx)) return "X" + plan.TargetX.ToString("0.###", inv);
            if (IsNonZero(req.Dy)) return "Y" + plan.TargetY.ToString("0.###", inv);
            return "Z" + plan.TargetZ.ToString("0.###", inv);
        }

        /// <summary>mm/s → mm/min, floored at F1 so a sub-1 mm/s feed can never
        /// round down to the undefined F0.</summary>
        static string FormatFeed(double feedMmS)
            => Math.Max(1.0, feedMmS * 60.0).ToString("0", CultureInfo.InvariantCulture);

        static bool IsNonZero(double v) => Math.Abs(v) > 1e-9;

        static bool InWhitelist(double step)
        {
            foreach (double w in StepWhitelist)
                if (Math.Abs(step - w) < 1e-9) return true;
            return false;
        }

        static JogPlan Refuse(JogPlan plan, string reason)
        {
            plan.Ok = false;
            plan.RefusalReason = reason;
            return plan;
        }

        /// <summary>Plans an absolute multi-waypoint move (calibration go-to-point):
        /// same machine-state gates as a jog, every waypoint validated against the
        /// full SCARA check set, then emitted as G90 G1 moves.</summary>
        public static JogPlan PlanPath(ObjectModelSnapshot? snap, IReadOnlyList<Vec3> waypoints,
                                   double feedMmS, RobotLimits lim, int elbow = 0,
                                   double maxFeedMmS = 50.0, double maxSnapshotAgeS = 3.0,
                                   DateTime? notBeforeUtc = null,
                                   ArmConfiguration? sessionConfiguration = null)
        {
            var plan = new JogPlan();
            if (waypoints == null || waypoints.Count == 0)
                return Refuse(plan, "no waypoints");
            string? gate = StateGate.Refuse(snap, maxSnapshotAgeS, requireIdle: true,
                allowPaused: false, requireHomed: true, notBeforeUtc: notBeforeUtc, activity: "moves");
            if (gate != null) return Refuse(plan, gate);
            if (!(feedMmS > 0))
                return Refuse(plan, "feed must be > 0");
            double feedCap = Math.Min(maxFeedMmS, lim.MaxFeed);
            if (feedMmS > feedCap + 1e-9)
                return Refuse(plan, FormattableString.Invariant($"feed {feedMmS:0.#}mm/s exceeds the cap {feedCap:0.#}mm/s"));

            var seed = sessionConfiguration.HasValue
                ? sessionConfiguration.Value
                : ArmConfiguration.Seed(elbow);

            // swept segments: current position → wp0, then each wp_i → wp_{i+1} -
            // the travel between legal waypoints can still chord through the
            // dead-zone annulus or sweep link 2 across the mast
            var axP = snap!.Axis("X"); var ayP = snap.Axis("Y"); var azP = snap.Axis("Z");
            if (axP == null || ayP == null || azP == null)
                return Refuse(plan, "object model is missing X/Y/Z axes");
            if (!Num.Finite(axP.MachinePosition) || !Num.Finite(ayP.MachinePosition) || !Num.Finite(azP.MachinePosition))
                return Refuse(plan, "machine position is not finite");
            var prev = new Vec3(axP.MachinePosition, ayP.MachinePosition, azP.MachinePosition);
            var walk = new List<Vec3>(waypoints.Count + 1) { prev };
            for (int i = 0; i < waypoints.Count; i++) walk.Add(waypoints[i]);
            var check = RobotChecks.RunPoints(walk, lim, seed);
            plan.FinalConfiguration = check.FinalConfiguration;

            foreach (var p in check.Problems)
            {
                if (p.Sev == Severity.Error)
                    return Refuse(plan, $"waypoint {p.Index} rejected: {p.Message}");
                if (p.Kind == ProblemKind.MastClearance)
                    return Refuse(plan, $"waypoint {p.Index} rejected: mast clearance is a blocking safety finding");
                plan.Warnings.Add(p.Message);
            }

            var inv = CultureInfo.InvariantCulture;
            string f = FormatFeed(feedMmS);
            var sb = new System.Text.StringBuilder("M120\nG90");
            foreach (var w in waypoints)
                sb.Append('\n').Append(FormattableString.Invariant(
                    $"G53 G1 X{w.X.ToString("0.###", inv)} Y{w.Y.ToString("0.###", inv)} Z{w.Z.ToString("0.###", inv)} F{f}"));
            sb.Append("\nM121");
            plan.Gcode = sb.ToString();
            var last = waypoints[waypoints.Count - 1];
            plan.TargetX = last.X; plan.TargetY = last.Y; plan.TargetZ = last.Z;
            plan.Ok = true;
            return plan;
        }
    }
}
