using System;
using System.Collections.Generic;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Robot-specific options kept outside generic toolpath data.</summary>
    public sealed class RobotSimulationOptions
    {
        public int ScaraArmMode = 1;
        public RobotLimits? ScaraLimits;
    }

    /// <summary>Common kinematics result consumed by robot-independent adapters.</summary>
    public sealed class RobotSimulationResult
    {
        internal RobotSimulationResult(RobotProfile profile, IReadOnlyList<Vec3> toolpath)
        {
            Profile = profile;
            Toolpath = toolpath;
        }

        public RobotProfile Profile { get; }
        public IReadOnlyList<Vec3> Toolpath { get; }
        public bool KinematicsApplied { get; internal set; }
        public bool ReachOk { get; internal set; } = true;
        public List<double[]> JointsDeg { get; } = new List<double[]>();
        public List<RobotProblem> Problems { get; } = new List<RobotProblem>();
    }

    /// <summary>
    /// Small dispatch boundary over the existing, independently tested kinematics.
    /// Cartesian/toolpath-only profiles intentionally return no joint samples.
    /// </summary>
    public static class RobotSimulation
    {
        /// <summary>Program-aware simulation for parsed G-code. SCARA validation must
        /// retain G28 reseeding, calibrated-home arm mode, flavor syntax, and arc
        /// semantics; reducing a program to points loses all four.</summary>
        public static RobotSimulationResult SolveProgram(
            RobotProfile profile,
            ParsedProgram program,
            IReadOnlyList<Vec3> sampledPoints,
            GcodeFlavor flavor,
            bool arcSupport,
            RobotSimulationOptions? options = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (program == null) throw new ArgumentNullException(nameof(program));
            if (sampledPoints == null) throw new ArgumentNullException(nameof(sampledPoints));
            options ??= new RobotSimulationOptions();

            if (profile.Mechanism != RobotMechanism.Scara)
                return Solve(profile, sampledPoints, options);

            var limits = options.ScaraLimits ?? profile.CreateScaraLimits();
            var checkedPath = RobotChecks.Run(program, limits, flavor, arcSupport,
                elbow: options.ScaraArmMode);
            if (checkedPath.Iks.Count != sampledPoints.Count)
                throw new InvalidOperationException(
                    $"program-aware SCARA samples disagree: checks={checkedPath.Iks.Count}, path={sampledPoints.Count}");

            var result = new RobotSimulationResult(profile, sampledPoints)
            {
                KinematicsApplied = true,
                ReachOk = checkedPath.ReachOk,
            };
            result.Problems.AddRange(checkedPath.Problems);
            foreach (var ik in checkedPath.Iks)
            {
                result.JointsDeg.Add(ik.Valid
                    ? new[] { ik.J1Resolved, ik.J3Resolved, ik.J2 }
                    : new[] { double.NaN, double.NaN, double.NaN });
            }
            return result;
        }

        public static RobotSimulationResult Solve(
            RobotProfile profile,
            IReadOnlyList<Vec3> points,
            RobotSimulationOptions? options = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (points == null) throw new ArgumentNullException(nameof(points));
            options ??= new RobotSimulationOptions();
            var result = new RobotSimulationResult(profile, points);

            switch (profile.Mechanism)
            {
                case RobotMechanism.None:
                case RobotMechanism.Cartesian:
                    return result;

                case RobotMechanism.Scara:
                    {
                        var limits = options.ScaraLimits ?? profile.CreateScaraLimits();
                        var checkedPath = RobotChecks.RunPoints(points, limits, options.ScaraArmMode);
                        result.KinematicsApplied = true;
                        result.ReachOk = checkedPath.ReachOk;
                        result.Problems.AddRange(checkedPath.Problems);
                        foreach (var ik in checkedPath.MoveIks)
                        {
                            result.JointsDeg.Add(ik.Valid
                                ? new[] { ik.J1Resolved, ik.J3Resolved, ik.J2 }
                                : new[] { double.NaN, double.NaN, double.NaN });
                        }
                        return result;
                    }

                default:
                    throw new InvalidOperationException($"unsupported robot mechanism '{profile.Mechanism}'");
            }
        }
    }
}
