using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Envelope-only validation for Cartesian Duet machines - the generic
    /// counterpart of the SCARA <see cref="RobotChecks"/> used by the jog and upload
    /// gates. A Cartesian machine has no reach annulus, no arm configuration and no
    /// mast, so the only geometric facts to enforce are the M208 box ([XMin,XMax] x
    /// [YMin,YMax] x [ZMin,ZMax]) and the feed ceiling. No IK is run and
    /// <see cref="CheckResult.Iks"/>/<see cref="CheckResult.MoveIks"/> stay empty;
    /// <see cref="CheckResult.FinalConfiguration"/> stays Unknown by construction.
    /// Interior samples of linear segments are deliberately not checked: a convex
    /// axis-aligned box cannot be crossed by a straight segment with legal endpoints.
    /// Arc interiors CAN bulge outside a box with legal endpoints, so they are
    /// sampled at the same chord tolerance the SCARA validator uses.</summary>
    public static class CartesianChecks
    {
        static void CheckPoint(CheckResult r, RobotLimits l, Vec3 p, int idx,
                               HashSet<string>? firstHits)
        {
            if (l.CheckEnvelope)
            {
                if (p.X < l.XMin - 1e-6)
                    Add(r, firstHits, "X:min", new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"X {p.X:0.0}mm is below envelope minimum {l.XMin:0.0}mm")));
                if (p.X > l.XMax + 1e-6)
                    Add(r, firstHits, "X:max", new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"X {p.X:0.0}mm exceeds envelope maximum {l.XMax:0.0}mm")));
                if (p.Y < l.YMin - 1e-6)
                    Add(r, firstHits, "Y:min", new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Y {p.Y:0.0}mm is below envelope minimum {l.YMin:0.0}mm")));
                if (p.Y > l.YMax + 1e-6)
                    Add(r, firstHits, "Y:max", new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Y {p.Y:0.0}mm exceeds envelope maximum {l.YMax:0.0}mm")));
            }
            if (p.Z < l.ZMin - 1e-6 || p.Z > l.ZMax + 1e-6)
                Add(r, firstHits, "z", new RobotProblem(p, idx, Severity.Error, FormattableString.Invariant($"Z {p.Z:0.0}mm outside [{l.ZMin:0}, {l.ZMax:0}]")));
        }

        static void Add(CheckResult r, HashSet<string>? firstHits, string key, RobotProblem problem)
        {
            r.ReachOk = false;
            if (firstHits == null || firstHits.Add(key))
                r.Problems.Add(problem);
        }

        /// <summary>Validates each point (and nothing between them - the box is
        /// convex) against the Cartesian envelope. Used by the jog gate.</summary>
        public static CheckResult RunPoints(IReadOnlyList<Vec3> pts, RobotLimits l)
        {
            var r = new CheckResult();
            if (pts == null || pts.Count == 0) return r;
            for (int i = 0; i < pts.Count; i++)
                CheckPoint(r, l, pts[i], i, firstHits: null);
            return r;
        }

        /// <summary>Validates a parsed job for a Cartesian Duet machine: every move
        /// endpoint against the envelope, arc interiors sampled at
        /// <see cref="RobotLimits.ArcChordTol"/>, and per-move feed against
        /// <see cref="RobotLimits.MaxFeed"/> as a warning (mirroring the SCARA
        /// validator's severity). The same start-pose policy as
        /// <see cref="RobotChecks.Run"/> applies: a move whose start position is not
        /// anchored (no calibrated home pose exists for a generic machine, and G28 is
        /// a teleport to an unknown position) is validated at its endpoint only,
        /// because arc geometry from an assumed start is undefined.</summary>
        public static CheckResult Run(ParsedProgram prog, RobotLimits l,
                                      bool chkSpeed = true, bool chkSyntax = true)
        {
            var r = new CheckResult();
            bool havePoint = false;
            Vec3 previousPoint = default;
            bool firstStartKnown = prog.HasFirstMoveStart
                                 && prog.HasHomeBeforeFirstMotion && prog.HomePoseKnown;
            int idx = -1;
            foreach (var m in prog.Moves)
            {
                idx++;
                var endpoint = new Vec3(m.X, m.Y, m.Z);
                var moveStart = new Vec3(m.StartX, m.StartY, m.StartZ);
                bool startKnown;
                if (!havePoint)
                {
                    havePoint = true;
                    startKnown = firstStartKnown;
                    if (startKnown) CheckPoint(r, l, moveStart, idx, firstHits: null);
                }
                else if (Math.Abs(moveStart.X - previousPoint.X) > 1e-9
                      || Math.Abs(moveStart.Y - previousPoint.Y) > 1e-9
                      || Math.Abs(moveStart.Z - previousPoint.Z) > 1e-9)
                {
                    // teleport (G28) - the post-home position of a generic machine
                    // is unknown to the parser, so the next arc's geometry is too.
                    // Known model limitation, shared with the SCARA validator: a G28
                    // (full or per-axis) with no calibrated pose leaves the parser's
                    // tracked position UNCHANGED, so this jump test cannot fire for
                    // it and an arc right after such a home is validated from the
                    // pre-home position. Endpoints are always absolute and always
                    // checked; the firmware faults an arc whose radius disagrees.
                    startKnown = prog.HomePoseKnown;
                }
                else startKnown = true;

                // First-hit dedup per move so a finely sampled arc reports each
                // violation kind once, not once per interior sample.
                var firstHits = new HashSet<string>(StringComparer.Ordinal);
                if (m.Arc && startKnown)
                    foreach (var sample in RobotChecks.SampleArc(moveStart.X, moveStart.Y,
                                 moveStart.Z, m, l.ArcChordTol))
                        CheckPoint(r, l, sample, idx, firstHits);
                CheckPoint(r, l, endpoint, idx, firstHits);
                previousPoint = endpoint;

                if (chkSpeed && m.F > 0)
                {
                    double feed = m.F / 60.0;
                    if (feed > l.MaxFeed)
                        r.Problems.Add(new RobotProblem(endpoint, idx, Severity.Warning,
                            FormattableString.Invariant($"feed {feed:0}mm/s > max {l.MaxFeed:0}mm/s")));
                }
            }

            if (chkSyntax)
            {
                var origin = new Vec3(0, 0, 0);
                if (prog.HasArcs)
                    r.Problems.Add(new RobotProblem(origin, -1, Severity.Info,
                        l.ArcChordTol > 0
                            ? FormattableString.Invariant($"arcs sampled at {l.ArcChordTol:0.##}mm chord deviation for envelope checks")
                            : "arc sampling disabled (ArcChordTol <= 0) - arcs validated at endpoints only"));
                if (!prog.HasEMode)
                    r.Problems.Add(new RobotProblem(origin, -1, Severity.Warning,
                        "no M82/M83 (E mode) found - extrusion mode is ambiguous"));
                if (!prog.HasHome && !prog.HasPartialHome)
                    r.Problems.Add(new RobotProblem(origin, -1, Severity.Info,
                        "no G28 homing found in the file"));
            }
            return r;
        }
    }
}
