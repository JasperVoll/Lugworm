using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Robot
{
    public enum Severity { Info, Warning, Error }
    public enum ProblemKind { Other, MastClearance, SelfCollision, ConfigFlip }

    public readonly struct RobotProblem
    {
        public readonly Vec3 At;
        public readonly int Index;
        public readonly Severity Sev;
        public readonly string Message;
        public readonly ProblemKind Kind;
        public RobotProblem(Vec3 at, int index, Severity sev, string message, ProblemKind kind = ProblemKind.Other)
        { At = at; Index = index; Sev = sev; Message = message; Kind = kind; }
        public override string ToString() => Index >= 0 ? $"[{Sev}] move {Index}: {Message}" : $"[{Sev}] {Message}";
    }

    public sealed class RobotLimits
    {
        public double L1 = 549.06, L2 = 457.23, BaseH = 0.0;
        public double ReachMax = 0.0, ReachMin = 235.0;
        public double ZMax = 400.0;
        /// <summary>Lower Z bound. The SCARA validator keeps its long-standing
        /// hard-coded Z >= 0 check; this field is read only by the Cartesian
        /// envelope path (<see cref="CartesianChecks"/>), where a generic machine's
        /// M208 Z minimum may legitimately be non-zero.</summary>
        public double ZMin = 0.0;
        // Cartesian envelope in machine coordinates - mirrors the firmware
        // M208 X/Y box. The reach annulus alone is not the workspace.
        public double XMin = -300.0, XMax = 10000.0;
        public double YMin = -10000.0, YMax = 0.0;
        public bool CheckEnvelope = true;
        public double MaxFeed = 150.0, MaxJointSpeed = 120.0, JerkThresh = 5.0;
        public double J1Min = -41.1, J1Max = 319.1, J3Min = -151.2, J3Max = 151.2;
        public double J2Min = 0.0, J2Max = 400.0;
        public bool CheckJointRanges = true;
        public double ArcChordTol = 0.5;
        public double MastClearance = 150.0;
        /// <summary>Calibrated Cartesian position after the machine's full
        /// homeall.g macro. Null is intentionally not a usable pose when the
        /// contract is required by a hardware gate.</summary>
        public Vec3? CalibratedHomePose;
        /// <summary>Arm mode after a verified full G28, established by the homing
        /// endstops rather than an operator preference. The lab Potterbot was measured
        /// in mode 1 (bring-up of 2026-08-25, Step 11: M114 after a full G28 reported
        /// X:200 Y:-500 Z:400 with joints -18.489 / -116.054; the lab memo is archived
        /// outside the repository).</summary>
        public int HomedArmMode = 1;
        public bool RequireCalibratedHomePose;
    }

    public sealed class CheckResult
    {
        public bool ReachOk = true;
        public readonly List<RobotProblem> Problems = new List<RobotProblem>();
        // This list is a sampled path, not a move list. For RunPoints it is
        // index-for-index with the input points; for Run it is index-for-index
        // with ToolpathSampler.SampleMoves(...).
        public readonly List<IkResult> Iks = new List<IkResult>();

        /// <summary>True when Iks[0] is a placeholder for a start point that was NOT
        /// validated (no G28 anchors the pose). It exists only to keep Iks aligned with
        /// the sampled toolpath for rendering; it carries no measured joint values, so
        /// anything publishing joint angles must skip it.</summary>
        public bool FirstSampleUnvalidated;

        /// <summary>One IK per parsed move (its endpoint pose) for Run, or per input
        /// point for RunPoints. This is what the public J1/J3/J2 outputs have always
        /// published. Deliberately SEPARATE from <see cref="Iks"/>: arcs are now
        /// validated along their swept path, so Iks gained interior samples, and
        /// publishing those would silently change the output list length of a shipped
        /// component for every saved definition.</summary>
        public readonly List<IkResult> MoveIks = new List<IkResult>();

        public ArmConfiguration FinalConfiguration = ArmConfiguration.Unknown;
    }

    public static class RobotChecks
    {
        const int ContinuitySampleBudget = 512;
        const double ContinuityEpsilon = 1e-9;

        readonly struct PathSample
        {
            public readonly double T;
            public readonly Vec3 Point;
            public readonly bool StoreIk;
            public PathSample(double t, Vec3 point, bool storeIk)
            { T = t; Point = point; StoreIk = storeIk; }
        }

        readonly struct PointEvaluation
        {
            public readonly IkResult Ik;
            public readonly CheckResult Local;
            public PointEvaluation(IkResult ik, CheckResult local)
            { Ik = ik; Local = local; }
        }

        /// <summary>Interior points of the same parametric arc used by the
        /// validator and the UI sampler. The endpoint is deliberately omitted
        /// because callers validate it as the move endpoint.</summary>
        public static List<Vec3> SampleArc(double px, double py, double pz, ParsedMove m, double tol)
        {
            var result = new List<Vec3>();
            foreach (var sample in BuildArcPath(px, py, pz, m, tol, includeEndpoint: false))
                result.Add(sample.Point);
            return result;
        }

        /// <summary>Returns the shared rendered path for one parsed move. A
        /// linear move contributes its endpoint; an arc contributes the same
        /// chord-tolerance samples plus its exact endpoint.</summary>
        public static List<Vec3> SampleMovePath(ParsedMove m, double tol)
        {
            var path = new List<Vec3>();
            path.Add(new Vec3(m.StartX, m.StartY, m.StartZ));
            if (m.Arc)
            {
                foreach (var sample in BuildArcPath(m.StartX, m.StartY, m.StartZ, m, tol, includeEndpoint: false))
                    path.Add(sample.Point);
            }
            path.Add(new Vec3(m.X, m.Y, m.Z));
            return path;
        }

        static List<PathSample> BuildArcPath(double px, double py, double pz, ParsedMove m,
                                             double tol, bool includeEndpoint)
        {
            var result = new List<PathSample>();
            double cx = px + m.I, cy = py + m.J;
            double r = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (r < 1e-9 || tol <= 0 || double.IsNaN(r) || double.IsInfinity(r))
            {
                if (includeEndpoint) result.Add(new PathSample(1.0, new Vec3(m.X, m.Y, m.Z), true));
                return result;
            }

            double a0 = Math.Atan2(py - cy, px - cx);
            double a1 = Math.Atan2(m.Y - cy, m.X - cx);
            bool cw = m.G == 2;
            double sweep = a1 - a0;
            if (cw) { if (sweep >= -1e-12) sweep -= 2.0 * Math.PI; }
            else if (sweep <= 1e-12) sweep += 2.0 * Math.PI;

            double maxStep = 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, 1.0 - tol / r)));
            if (maxStep < 1e-4) maxStep = 1e-4;
            int n = Math.Min(ContinuitySampleBudget,
                Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / maxStep)));
            for (int k = 1; k < n; k++)
            {
                double t = (double)k / n;
                result.Add(new PathSample(t, ArcPointAt(px, py, pz, m, t), true));
            }
            if (includeEndpoint)
                result.Add(new PathSample(1.0, new Vec3(m.X, m.Y, m.Z), true));
            return result;
        }

        static Vec3 ArcPointAt(double px, double py, double pz, ParsedMove m, double t)
        {
            double cx = px + m.I, cy = py + m.J;
            double r = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            double a0 = Math.Atan2(py - cy, px - cx);
            double a1 = Math.Atan2(m.Y - cy, m.X - cx);
            double sweep = a1 - a0;
            if (m.G == 2) { if (sweep >= -1e-12) sweep -= 2.0 * Math.PI; }
            else if (sweep <= 1e-12) sweep += 2.0 * Math.PI;
            double a = a0 + sweep * t;
            return new Vec3(cx + r * Math.Cos(a), cy + r * Math.Sin(a), pz + (m.Z - pz) * t);
        }

        static List<PathSample> BuildLinearPath(Vec3 from, Vec3 to, double stepMm, bool includeEndpoint)
        {
            var result = new List<PathSample>();
            double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9 || stepMm <= 0)
            {
                if (includeEndpoint) result.Add(new PathSample(1.0, to, true));
                return result;
            }

            int n = Math.Min(ContinuitySampleBudget,
                Math.Max(1, (int)Math.Ceiling(len / stepMm)));
            for (int k = 1; k <= n; k++)
            {
                if (k == n && !includeEndpoint) break;
                double t = (double)k / n;
                result.Add(new PathSample(t,
                    new Vec3(from.X + dx * t, from.Y + dy * t, from.Z + dz * t), k == n));
            }
            return result;
        }

        static double ReachMin(RobotLimits l) => l.ReachMin > 0 ? l.ReachMin : Math.Abs(l.L1 - l.L2);
        static double ReachMax(RobotLimits l) => l.ReachMax > 0 ? l.ReachMax : l.L1 + l.L2;

        static void CheckJoints(CheckResult r, RobotLimits l, IkResult ik, Vec3 at, int idx)
        {
            if (!l.CheckJointRanges || !ik.Valid) return;
            if (ik.J1Resolved < l.J1Min - 1e-6 || ik.J1Resolved > l.J1Max + 1e-6)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(at, idx, Severity.Error, FormattableString.Invariant($"J1 {ik.J1Resolved:0.0}° outside joint range [{l.J1Min:0.0}, {l.J1Max:0.0}]°"))); }
            if (ik.J3Resolved < l.J3Min - 1e-6 || ik.J3Resolved > l.J3Max + 1e-6)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(at, idx, Severity.Error, FormattableString.Invariant($"J3 {ik.J3Resolved:0.0}° outside joint range [{l.J3Min:0.0}, {l.J3Max:0.0}]°"))); }
            if (ik.J2 < l.J2Min - 1e-6 || ik.J2 > l.J2Max + 1e-6)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(at, idx, Severity.Error, FormattableString.Invariant($"J2 carriage {ik.J2:0.0}mm outside travel [{l.J2Min:0.0}, {l.J2Max:0.0}]mm"))); }
        }

        static void CheckMast(CheckResult r, RobotLimits l, IkResult ik, Vec3 at, int idx)
        {
            if (l.MastClearance <= 0 || !ik.Valid) return;
            var (ex, ey) = ScaraKinematics.Elbow(ik.J1Resolved, l.L1);
            double d = PolylineOps.PointSegDist(new Vec3(0, 0, 0), new Vec3(ex, ey, 0), new Vec3(at.X, at.Y, 0));
            if (d < l.MastClearance)
                r.Problems.Add(new RobotProblem(at, idx, Severity.Warning,
                    FormattableString.Invariant($"link 2 sweeps {d:0.0}mm from the mast axis (< {l.MastClearance:0.0}mm clearance)"), ProblemKind.MastClearance));
        }

        static void CheckPointCore(CheckResult r, RobotLimits l, Vec3 p, int idx, IkResult ik,
                                   bool checkEnvelope)
        {
            if (checkEnvelope && l.CheckEnvelope)
            {
                if (p.X < l.XMin - 1e-6)
                { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"X {p.X:0.0}mm is below envelope minimum {l.XMin:0.0}mm"))); }
                if (p.X > l.XMax + 1e-6)
                { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"X {p.X:0.0}mm exceeds envelope maximum {l.XMax:0.0}mm"))); }
                if (p.Y < l.YMin - 1e-6)
                { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Y {p.Y:0.0}mm is below envelope minimum {l.YMin:0.0}mm"))); }
                if (p.Y > l.YMax + 1e-6)
                { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Y {p.Y:0.0}mm exceeds envelope maximum {l.YMax:0.0}mm"))); }
            }

            // Geometric position checks run independently of IK validity: r and Z are
            // facts about the target, not the solution. R=235 dead-zone / max reach must
            // report clearly even when the elbow solution is itself out of range.
            double rxy = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            if (rxy < ReachMin(l) - 1e-6) { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"inside dead-zone: r {rxy:0.0}mm < min radius {ReachMin(l):0.0}mm"))); }
            if (rxy > ReachMax(l) + 1e-6) { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"beyond configured max reach: r {rxy:0.0}mm > {ReachMax(l):0.0}mm"))); }
            if (p.Z < -1e-6 || p.Z > l.ZMax) { r.ReachOk = false; r.Problems.Add(new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Z {p.Z:0.0}mm outside [0, {l.ZMax:0}]"))); }

            if (!ik.Valid)
            {
                // no usable IK - report it, but only once the geometric facts above are
                // out. Joint/mast checks need a valid solution, so stop here.
                r.ReachOk = false;
                r.Problems.Add(new RobotProblem(p, idx, Severity.Error, ik.Error));
                return;
            }
            CheckJoints(r, l, ik, p, idx);
            CheckMast(r, l, ik, p, idx);
        }

        static string ProblemKey(RobotProblem problem)
        {
            if (problem.Kind == ProblemKind.MastClearance) return "mast";
            if (problem.Kind == ProblemKind.ConfigFlip) return "config";
            string message = problem.Message;
            if (message.Contains("below envelope minimum")) return message[0] + ":min";
            if (message.Contains("exceeds envelope maximum")) return message[0] + ":max";
            if (message.Contains("inside dead-zone")) return "reach:min";
            if (message.Contains("beyond configured max reach")) return "reach:max";
            if (message.StartsWith("Z ", StringComparison.Ordinal)) return "z";
            if (message.Contains("J1 ") && message.Contains("joint range")) return "joint:J1";
            if (message.Contains("J3 ") && message.Contains("joint range")) return "joint:J3";
            if (message.Contains("J2 carriage")) return "joint:J2";
            if (message.Contains("Target ")) return "ik";
            return problem.Kind + ":" + message;
        }

        static PointEvaluation EvaluatePoint(RobotLimits l, Vec3 p, int idx,
                                             ArmConfiguration state, bool checkReach,
                                             bool checkEnvelope)
        {
            var ik = ScaraKinematics.InverseRrf(p.X, p.Y, p.Z, l,
                state.Known ? state.ArmMode : 0);
            var local = new CheckResult();
            if (checkReach) CheckPointCore(local, l, p, idx, ik, checkEnvelope);
            return new PointEvaluation(ik, local);
        }

        static void MergePoint(CheckResult r, PointEvaluation evaluation, bool storeIk,
                               HashSet<string>? firstHits)
        {
            foreach (var problem in evaluation.Local.Problems)
            {
                if (firstHits == null || firstHits.Add(ProblemKey(problem)))
                    r.Problems.Add(problem);
                if (problem.Sev == Severity.Error) r.ReachOk = false;
            }
            if (storeIk) r.Iks.Add(evaluation.Ik);
        }

        static bool IsCoarse(IkResult previous, IkResult current, out int nCont, out double continuous)
        {
            nCont = 0;
            continuous = current.J1Resolved;
            if (!previous.Valid || !current.Valid) return false;
            nCont = (int)Math.Round((previous.J1Resolved - current.J1Raw) / 360.0,
                MidpointRounding.AwayFromZero);
            continuous = current.J1Raw + 360.0 * nCont;
            return Math.Abs(continuous - previous.J1Resolved) >= 180.0;
        }

        static void AddConfigFlip(CheckResult r, Vec3 at, int idx,
                                  IkResult previous, IkResult current, int nCont,
                                  HashSet<string>? firstHits)
        {
            r.ReachOk = false;
            var problem = new RobotProblem(at, idx, Severity.Error,
                FormattableString.Invariant($"physical arm configuration discontinuity at ({at.X:0.###}, {at.Y:0.###}, {at.Z:0.###})mm: ") +
                $"ArmMode {previous.ArmMode} -> {current.ArmMode}, " +
                FormattableString.Invariant($"J1 {previous.J1Resolved:0.0}° -> raw {current.J1Raw:0.0}° / resolved {current.J1Resolved:0.0}° ") +
                $"(continuous turn {nCont}, firmware turn {current.ThetaWrapTurns}); machine would physically reconfigure the arm",
                ProblemKind.ConfigFlip);
            if (firstHits == null || firstHits.Add(ProblemKey(problem)))
                r.Problems.Add(problem);
        }

        static void AddUnresolvable(CheckResult r, Vec3 at, int idx)
        {
            r.ReachOk = false;
            r.Problems.Add(new RobotProblem(at, idx, Severity.Error,
                "J1 continuity unresolvable", ProblemKind.Other));
        }

        static ArmConfiguration CommitPoint(CheckResult r, PointEvaluation evaluation,
                                            Vec3 at, int idx,
                                            IkResult previous, bool hasPrevious, bool storeIk,
                                            HashSet<string>? firstHits, bool suppressConfigFlip,
                                            out IkResult current)
        {
            current = evaluation.Ik;
            MergePoint(r, evaluation, storeIk, firstHits);
            if (hasPrevious && current.Valid && previous.Valid && !suppressConfigFlip)
            {
                IsCoarse(previous, current, out int nCont, out _);
                if (current.ArmMode != previous.ArmMode || nCont != current.ThetaWrapTurns)
                    AddConfigFlip(r, at, idx, previous, current, nCont, firstHits);
            }
            return current.Valid
                ? new ArmConfiguration(current.ArmMode, current.ThetaWrapTurns)
                : ArmConfiguration.Unknown;
        }

        static ArmConfiguration ProcessPath(CheckResult r, RobotLimits l,
                                            IReadOnlyList<PathSample> samples, int idx,
                                            ArmConfiguration state, IkResult previous,
                                            bool hasPrevious, bool checkReach,
                                            HashSet<string>? firstHits,
                                            Func<double, Vec3> pointAt,
                                            bool envelopeOnAllSamples,
            out IkResult lastIk)
        {
            lastIk = previous;
            double previousT = 0.0;
            int budget = 0;

            for (int sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
            {
                PathSample target = samples[sampleIndex];
                PointEvaluation evaluation = EvaluatePoint(l, target.Point, idx, state, checkReach,
                    envelopeOnAllSamples || target.StoreIk);
                budget++;
                bool unresolved = false;

                // Refine in parameter space. The accepted midpoint becomes the
                // predecessor for the target, so the target is retried against
                // the physically ordered state rather than a fixed elbow seed.
                while (hasPrevious && evaluation.Ik.Valid && lastIk.Valid &&
                       IsCoarse(lastIk, evaluation.Ik, out _, out _) &&
                       target.T - previousT > ContinuityEpsilon)
                {
                    if (budget >= ContinuitySampleBudget)
                    {
                        unresolved = true;
                        break;
                    }

                    double midT = previousT + (target.T - previousT) * 0.5;
                    if (midT - previousT <= ContinuityEpsilon || target.T - midT <= ContinuityEpsilon)
                    {
                        unresolved = true;
                        break;
                    }
                    // The callback is linear for G0/G1 and angle/helical for
                    // G2/G3, matching the base sampler's parameterisation.
                    Vec3 mid = pointAt(midT);
                    PointEvaluation midpoint = EvaluatePoint(l, mid, idx, state, checkReach,
                        envelopeOnAllSamples);
                    budget++;
                    state = CommitPoint(r, midpoint, mid, idx, lastIk, hasPrevious,
                        storeIk: false, firstHits, suppressConfigFlip: false, out lastIk);
                    hasPrevious = lastIk.Valid;
                    previousT = midT;
                    if (budget >= ContinuitySampleBudget)
                    {
                        unresolved = true;
                        break;
                    }
                    evaluation = EvaluatePoint(l, target.Point, idx, state, checkReach,
                        envelopeOnAllSamples || target.StoreIk);
                    budget++;
                }

                if (unresolved) AddUnresolvable(r, target.Point, idx);
                state = CommitPoint(r, evaluation, target.Point, idx, lastIk,
                    hasPrevious, target.StoreIk, firstHits, unresolved, out lastIk);
                hasPrevious = lastIk.Valid;
                previousT = target.T;
            }
            return state;
        }

        /// <summary>Checks one endpoint using the incoming ordered arm state.
        /// The elbow argument used by older callers is intentionally absent:
        /// state is now the seed and the returned state is the next seed.</summary>
        public static ArmConfiguration CheckPoint(CheckResult r, RobotLimits l, Vec3 p, int idx,
                                                  ArmConfiguration incoming, IkResult previousIk,
                                                  out IkResult ik, bool storeIk = true,
                                                  HashSet<string>? firstHits = null)
        {
            var evaluation = EvaluatePoint(l, p, idx, incoming, checkReach: true,
                checkEnvelope: true);
            return CommitPoint(r, evaluation, p, idx, previousIk, previousIk.Valid,
                storeIk, firstHits, suppressConfigFlip: false, out ik);
        }

        /// <summary>Checks the interior in physical order and returns the state
        /// reached by the last interior sample. Envelope checks deliberately do
        /// not occur here: a convex box cannot be crossed by a straight segment
        /// whose endpoints passed CheckPoint.</summary>
        public static ArmConfiguration CheckSegmentInterior(CheckResult r, RobotLimits l,
                                                            Vec3 from, Vec3 to, int idx,
                                                            ArmConfiguration incoming,
                                                            IkResult previousIk,
                                                            out IkResult outgoingIk,
                                                            double stepMm = 5.0,
                                                            HashSet<string>? firstHits = null)
        {
            var samples = BuildLinearPath(from, to, stepMm, includeEndpoint: false);
            if (samples.Count == 0)
            {
                outgoingIk = previousIk;
                return incoming;
            }
            return ProcessPath(r, l, samples, idx, incoming, previousIk, previousIk.Valid,
                checkReach: true, firstHits,
                t => new Vec3(from.X + (to.X - from.X) * t,
                              from.Y + (to.Y - from.Y) * t,
                              from.Z + (to.Z - from.Z) * t),
                envelopeOnAllSamples: false,
                out outgoingIk);
        }

        /// <summary>Compatibility wrapper for preview-only callers. New safety
        /// callers must use the stateful overload above.</summary>
        public static void CheckSegmentInterior(CheckResult r, RobotLimits l, Vec3 from, Vec3 to,
                                                int idx, int elbow = 0, double stepMm = 5.0)
        {
            var state = ArmConfiguration.Seed(elbow);
            var previous = ScaraKinematics.InverseRrf(from.X, from.Y, from.Z, l, state.ArmMode);
            CheckSegmentInterior(r, l, from, to, idx, state, previous,
                out _, stepMm, firstHits: null);
        }

        public static CheckResult RunPoints(IReadOnlyList<Vec3> pts, RobotLimits l, int elbow = 0)
            => RunPoints(pts, l, ArmConfiguration.Seed(elbow));

        public static CheckResult RunPoints(IReadOnlyList<Vec3> pts, RobotLimits l,
                                            ArmConfiguration seed)
        {
            var r = new CheckResult();
            if (pts == null || pts.Count == 0) return r;

            ArmConfiguration state = seed.Known ? seed : ArmConfiguration.Seed(0);
            var first = EvaluatePoint(l, pts[0], 0, state, checkReach: true,
                checkEnvelope: true);
            state = CommitPoint(r, first, pts[0], 0, default, false,
                storeIk: true, firstHits: null, suppressConfigFlip: false, out IkResult previous);
            bool hasPrevious = previous.Valid;
            r.MoveIks.Add(previous);        // one entry per INPUT point for RunPoints
            Vec3 from = pts[0];
            for (int i = 1; i < pts.Count; i++)
            {
                var samples = BuildLinearPath(from, pts[i], 5.0, includeEndpoint: true);
                state = ProcessPath(r, l, samples, i, state, previous, hasPrevious,
                    checkReach: true, firstHits: null,
                    t => new Vec3(from.X + (pts[i].X - from.X) * t,
                                  from.Y + (pts[i].Y - from.Y) * t,
                                  from.Z + (pts[i].Z - from.Z) * t),
                    envelopeOnAllSamples: false,
                    out previous);
                hasPrevious = previous.Valid;
                r.MoveIks.Add(previous);
                from = pts[i];
            }
            r.FinalConfiguration = state;
            return r;
        }

        static double SegmentLength(double px, double py, double pz, ParsedMove m)
        {
            if (!m.Arc) return Math.Sqrt((m.X - px) * (m.X - px) + (m.Y - py) * (m.Y - py) + (m.Z - pz) * (m.Z - pz));
            double cx = px + m.I, cy = py + m.J, rad = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (rad < 1e-9) return 0;
            double a0 = Math.Atan2(py - cy, px - cx), a1 = Math.Atan2(m.Y - cy, m.X - cx), sweep = a1 - a0;
            if (m.G == 2) { if (sweep >= 0) sweep -= 2 * Math.PI; } else if (sweep <= 0) sweep += 2 * Math.PI;
            double xy = Math.Abs(sweep) * rad, dz = m.Z - pz;
            return Math.Sqrt(xy * xy + dz * dz);
        }

        static ArmConfiguration SeedConfiguration(RobotLimits l, int elbow, bool homeAnchored,
                                                   ArmConfiguration? initialConfiguration = null)
        {
            if (initialConfiguration.HasValue && initialConfiguration.Value.Known)
                return initialConfiguration.Value;
            return homeAnchored
                ? ArmConfiguration.Seed(l.HomedArmMode)
                : ArmConfiguration.Seed(elbow);
        }

        public static CheckResult Run(ParsedProgram prog, RobotLimits l, GcodeFlavor flavor, bool arcSupport,
                                      bool chkReach = true, bool chkSing = true, bool chkSpeed = true,
                                      bool chkSyntax = true, int elbow = 0,
                                      ArmConfiguration? initialConfiguration = null)
        {
            var r = new CheckResult();
            if (l.RequireCalibratedHomePose && !l.CalibratedHomePose.HasValue)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(default, -1, Severity.Error, "start position unknown - calibrated post-homeall Cartesian pose is missing")); }
            if (l.RequireCalibratedHomePose && !prog.HasHomeBeforeFirstMotion)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(default, -1, Severity.Error, "missing G28 before first motion - start position is unknown")); }
            if (l.RequireCalibratedHomePose && prog.HasHome && !prog.HomePoseKnown)
            { r.ReachOk = false; r.Problems.Add(new RobotProblem(default, -1, Severity.Error, "start position unknown - G28 has no calibrated post-homeall pose")); }

            ArmConfiguration state = SeedConfiguration(l, elbow,
                prog.HasHomeBeforeFirstMotion && prog.HomePoseKnown, initialConfiguration);
            IkResult previous = default;
            bool hasPrevious = false;
            Vec3 previousPoint = default;
            bool havePoint = false;
            int idx = -1;

            // The parser fills an unknown physical start with (0,0,0). Validating that
            // assumed pose (or a path from it) invents dead-zone / reach errors at the
            // origin, so the start point and its segment are checked only when the pose
            // is actually known - a calibrated post-home pose. Endpoints are always
            // checked below. Mirrors the pre-3.x havePrev guard.
            bool firstStartKnown = prog.HasFirstMoveStart
                                 && prog.HasHomeBeforeFirstMotion && prog.HomePoseKnown;

            foreach (var m in prog.Moves)
            {
                idx++;
                Vec3 endpoint = new Vec3(m.X, m.Y, m.Z);
                Vec3 moveStart = new Vec3(m.StartX, m.StartY, m.StartZ);
                bool startKnown = havePoint ? prog.HomePoseKnown : firstStartKnown;
                if (!havePoint)
                {
                    havePoint = true;
                    previousPoint = moveStart;
                    if (startKnown)
                    {
                        var start = EvaluatePoint(l, moveStart, idx, state, chkReach,
                            checkEnvelope: true);
                        state = CommitPoint(r, start, moveStart, idx, default, false,
                            storeIk: true, firstHits: null, suppressConfigFlip: false, out previous);
                        hasPrevious = previous.Valid;
                    }
                    else
                    {
                        hasPrevious = false;   // start pose unknown - endpoint only
                        // ToolpathSampler always emits the first move's start point, so
                        // Iks must carry a slot for it to stay index-for-index (see the
                        // CheckResult.Iks contract). Skipping the slot silently shifted
                        // every later index by one and made the renderer's count guard
                        // fail on any G-code without a homing header.
                        r.Iks.Add(new IkResult(0, 0, 0, false,
                            "start pose unknown - no home in the program, so this point was not validated"));
                        r.FirstSampleUnvalidated = true;
                    }
                }
                else if (Math.Abs(moveStart.X - previousPoint.X) > 1e-9
                      || Math.Abs(moveStart.Y - previousPoint.Y) > 1e-9
                      || Math.Abs(moveStart.Z - previousPoint.Z) > 1e-9)
                {
                    // G28 is a physical teleport. The macro reader/session owns
                    // its verified pose; the offline walk resets continuity at
                    // the parser's physical post-home start.
                    state = SeedConfiguration(l, elbow, prog.HomePoseKnown);
                    previousPoint = moveStart;
                    if (startKnown)
                    {
                        var start = EvaluatePoint(l, moveStart, idx, state, chkReach,
                            checkEnvelope: true);
                        state = CommitPoint(r, start, moveStart, idx, default, false,
                            storeIk: false, firstHits: null, suppressConfigFlip: false, out previous);
                        hasPrevious = previous.Valid;
                    }
                    else hasPrevious = false;   // post-home pose unknown - endpoint only
                }

                bool moveHadPrevious = hasPrevious;
                IkResult movePrevious = previous;
                double dist = moveHadPrevious ? SegmentLength(moveStart.X, moveStart.Y, moveStart.Z, m) : 0.0;
                // First-hit dedup per move so a long segment (linear OR arc) reports
                // each violation kind once, not once per interior sample.
                HashSet<string>? firstHits = chkReach
                    ? new HashSet<string>(StringComparer.Ordinal) : null;
                List<PathSample> samples;
                if (!hasPrevious)
                    // start pose unknown: validate only where the move ends, never a
                    // path from an assumed origin (arc geometry is undefined too).
                    samples = new List<PathSample> { new PathSample(1.0, endpoint, true) };
                else if (m.Arc)
                    samples = BuildArcPath(moveStart.X, moveStart.Y, moveStart.Z, m, l.ArcChordTol, includeEndpoint: true);
                else
                    samples = BuildLinearPath(moveStart, endpoint, 5.0, includeEndpoint: true);

                state = ProcessPath(r, l, samples, idx, state, previous, hasPrevious,
                    checkReach: chkReach, firstHits,
                    m.Arc
                        ? new Func<double, Vec3>(t => ArcPointAt(moveStart.X, moveStart.Y, moveStart.Z, m, t))
                        : new Func<double, Vec3>(t => new Vec3(
                            moveStart.X + (m.X - moveStart.X) * t,
                            moveStart.Y + (m.Y - moveStart.Y) * t,
                            moveStart.Z + (m.Z - moveStart.Z) * t)),
                    envelopeOnAllSamples: m.Arc,
                    out previous);
                hasPrevious = previous.Valid;
                // The move's endpoint pose - one entry per parsed move, which is the
                // cardinality the J1/J3/J2 outputs have always had.
                r.MoveIks.Add(previous);
                previousPoint = new Vec3(m.X, m.Y, m.Z);

                double rxy = Math.Sqrt(m.X * m.X + m.Y * m.Y);
                if (chkSing && previous.Valid)
                {
                    if (rxy > ReachMax(l) * .98) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"near max reach ({rxy:0.0}/{ReachMax(l):0.0}mm) - singular, J1 races")));
                    else if (rxy < ReachMin(l) * 1.02) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"near dead-zone ({rxy:0.0}/{ReachMin(l):0.0}mm) - inner singularity, joints race")));
                    if (moveHadPrevious && movePrevious.Valid && dist > 1e-6)
                    {
                        double d1 = Math.Abs(previous.J1Resolved - movePrevious.J1Resolved);
                        double d3 = Math.Abs(previous.J3Resolved - movePrevious.J3Resolved);
                        double d2 = Math.Abs(previous.J2 - movePrevious.J2);
                        if (d1 / dist > l.JerkThresh) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"joint jerk: ΔJ1 {d1:0.0}° over {dist:0.0}mm = {d1 / dist:0.0}°/mm")));
                        if (d3 / dist > l.JerkThresh) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"joint jerk: ΔJ3 {d3:0.0}° over {dist:0.0}mm = {d3 / dist:0.0}°/mm")));
                        if (d2 / dist > l.JerkThresh) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"joint jerk: ΔJ2 {d2:0.0}mm over {dist:0.0}mm = {d2 / dist:0.0}mm/mm")));
                    }
                }
                if (chkSpeed && previous.Valid && m.F > 0)
                {
                    double feed = m.F / 60.0;
                    if (feed > l.MaxFeed) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"feed {feed:0}mm/s > max {l.MaxFeed:0}mm/s")));
                    if (moveHadPrevious && movePrevious.Valid && dist > 1e-6)
                    {
                        double d1 = Math.Abs(previous.J1Resolved - movePrevious.J1Resolved);
                        double d3 = Math.Abs(previous.J3Resolved - movePrevious.J3Resolved);
                        double d2 = Math.Abs(previous.J2 - movePrevious.J2);
                        double s1 = d1 / dist * feed, s3 = d3 / dist * feed, s2 = d2 / dist * feed;
                        if (s1 > l.MaxJointSpeed) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"J1 speed {s1:0}°/s > max {l.MaxJointSpeed:0}°/s")));
                        if (s3 > l.MaxJointSpeed) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"J3 speed {s3:0}°/s > max {l.MaxJointSpeed:0}°/s")));
                        if (s2 > l.MaxJointSpeed) r.Problems.Add(new RobotProblem(new Vec3(m.X, m.Y, m.Z), idx, Severity.Warning, FormattableString.Invariant($"J2 speed {s2:0}mm/s > max {l.MaxJointSpeed:0}mm/s")));
                    }
                }
            }

            r.FinalConfiguration = state;
            if (chkSyntax)
            {
                var origin = new Vec3(0, 0, 0);
                if (prog.HasArcs && flavor == GcodeFlavor.Marlin && !arcSupport) r.Problems.Add(new RobotProblem(origin, -1, Severity.Warning, "G2/G3 arcs present - ensure the Marlin build has ARC_SUPPORT enabled"));
                if (prog.HasArcs) r.Problems.Add(new RobotProblem(origin, -1, Severity.Info, l.ArcChordTol > 0 ? FormattableString.Invariant($"arcs sampled at {l.ArcChordTol:0.##}mm chord deviation for reach/joint checks") : "arc sampling disabled (ArcChordTol <= 0) - arcs validated at endpoints only"));
                if (!prog.HasEMode) r.Problems.Add(new RobotProblem(origin, -1, Severity.Warning, "no M82/M83 (E mode) found - extrusion mode is ambiguous"));
                if (!prog.HasHome) r.Problems.Add(new RobotProblem(origin, -1, l.RequireCalibratedHomePose ? Severity.Error : Severity.Info, "no G28 homing found in the file"));
            }
            return r;
        }
    }
}
