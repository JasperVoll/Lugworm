using System;
using System.Collections.Generic;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    public sealed class JobGateResult
    {
        public bool Allowed;
        public int Errors, Warnings;
        public int Moves;
        public ulong Hash;
        public CheckResult Checks = new CheckResult();
        public ArmConfiguration InitialConfiguration = ArmConfiguration.Unknown;
        /// <summary>The file's first motion is a full G28: the job homes the machine itself
        /// before any move, so a Start needs neither a homed machine nor a known arm
        /// configuration beforehand (2026-09-09 maintainer decision D9).</summary>
        public bool HomesFirst;
        /// <summary>Where the print begins: the start of the job's first extruding move,
        /// in the MACHINE frame the job was validated in (null when nothing extrudes). The
        /// console's "Go to print start" travels there at Z 0, so an operator sees where
        /// the first layer lands before the job runs. It is the parser's own start point,
        /// so it can never disagree with what the checks validated.</summary>
        public Vec3? StartPoint;
        public string Summary = "";
    }

    /// <summary>The upload gate: parses the job and re-runs the machine's check set -
    /// the full SCARA validation by default, or the Cartesian envelope checks when the
    /// robot definition selects a Cartesian Duet machine (mechanism parameter).
    /// Lives in Core (not the UI) and is re-run inside the print component itself so
    /// the gate cannot be bypassed by leaving a wire unconnected. Allowed only when
    /// the program has moves and zero blocking problems. Mast-clearance findings
    /// block by default (<paramref name="mastAsError"/>): they are the only guard
    /// against link 2 sweeping into the physical column, which the TCP dead-zone
    /// check cannot see. <paramref name="blockOnWarnings"/> additionally refuses
    /// jobs with ANY warning (overspeed, jerk, singularity proximity).</summary>
    public static class JobGate
    {
        public static JobGateResult Validate(IReadOnlyList<string> gcodeLines, RobotLimits lim,
                                             int elbow = 0, bool arcSupport = true,
                                             bool blockOnWarnings = false, bool mastAsError = true,
                                             ArmConfiguration? initialConfiguration = null,
                                             RobotMechanism mechanism = RobotMechanism.Scara)
        {
            var res = new JobGateResult();
            res.InitialConfiguration = initialConfiguration.HasValue && initialConfiguration.Value.Known
                ? initialConfiguration.Value
                : ArmConfiguration.Seed(elbow);
            string joined = string.Join("\n", gcodeLines);
            string configurationKey = initialConfiguration.HasValue && initialConfiguration.Value.Known
                ? $"|cfg:{initialConfiguration.Value.ArmMode}:{initialConfiguration.Value.ThetaTurns}"
                : "|cfg:unknown";
            res.Hash = ContentHash.Fnv1a(joined + "|" + ContentHash.LimitsKey(lim, elbow, 0, arcSupport)
                                         + "|" + blockOnWarnings + "|" + mastAsError + configurationKey
                                         + "|mech:" + (int)mechanism);

            ParsedProgram prog;
            try
            {
                prog = GcodeParser.Parse(gcodeLines, lim.CalibratedHomePose,
                    allowPerAxisHome: mechanism == RobotMechanism.Cartesian);
            }
            catch (Exception ex)
            {
                res.Allowed = false;
                res.Errors = 1;
                res.Summary = "G-code parse failed: " + ex.Message;
                return res;
            }

            res.Moves = prog.Moves.Count;
            res.HomesFirst = prog.HasHomeBeforeFirstMotion && !prog.HasPartialHome;
            foreach (var move in prog.Moves)
            {
                if (!move.Extrude) continue;
                res.StartPoint = new Vec3(move.StartX, move.StartY, move.StartZ);
                break;
            }
            if (res.Moves == 0)
            {
                res.Allowed = false;
                res.Summary = "no motion commands found - nothing to print";
                return res;
            }

            res.Checks = mechanism == RobotMechanism.Cartesian
                ? CartesianChecks.Run(prog, lim)
                : RobotChecks.Run(prog, lim, GcodeFlavor.DuetRrf, arcSupport, elbow: elbow,
                                  initialConfiguration: initialConfiguration);
            foreach (var p in res.Checks.Problems)
            {
                bool gateError = p.Sev == Severity.Error
                              || (mastAsError && p.Kind == ProblemKind.MastClearance);
                if (gateError) res.Errors++;
                else if (p.Sev == Severity.Warning) res.Warnings++;
            }

            res.Allowed = res.Errors == 0 && (!blockOnWarnings || res.Warnings == 0);
            res.Summary = res.Errors > 0
                ? $"DANGER: {res.Errors} limit violation(s) over {res.Moves} moves - upload refused"
                : res.Allowed
                    ? $"OK - {res.Moves} moves, {res.Warnings} warning(s)"
                    : $"{res.Warnings} warning(s) over {res.Moves} moves - blocked by policy";
            return res;
        }
    }
}
