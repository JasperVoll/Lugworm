using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Kinematic self-check: validates that the mesh-posing FK chain
    /// (<see cref="RobotPosing"/>, driven by the robot.xml definition) lands the
    /// nozzle exactly where the analytic SCARA forward kinematics
    /// (<see cref="ScaraKinematics.Forward"/>, driven by the firmware link lengths
    /// stored in the same robot.xml) says it must be.
    ///
    /// The expected TCP positions are DERIVED from the definition file itself:
    /// L1 = link1.Length, L2 = link2.Length (the firmware M669 P/D values quoted in
    /// robot.xml), and nozzle-Z == J2 (the bed-contact calibration robot.xml encodes
    /// by construction: J2.home equals the Jtip pivot Z). No hand-invented numbers.</summary>
    public static class RobotSelfCheck
    {
        /// <summary>Joint-space poses to verify: home, axis-aligned, mid-workspace,
        /// and the firmware joint-range extremes (from the definition when present).</summary>
        public static List<(double j1, double j3, double j2)> TestPoses(RobotDefinition def)
        {
            var poses = new List<(double, double, double)>
            {
                (0.0, 0.0, 300.0),        // arm straight along +X
                (45.0, 30.0, 100.0),
                (-90.0, -120.0, 500.0),
                (30.0, 90.0, 800.0),
                (-135.0, 151.0, 10.0),
            };
            var j1 = def.Joint("J1"); var j3 = def.Joint("J3"); var j2 = def.Joint("J2");
            if (j1 != null && j3 != null && j2 != null)
            {
                poses.Add((j1.Home, j3.Home, j2.Home));            // modeled home pose
                poses.Add((j1.Min, j3.Min, j2.Min));               // joint-range extremes
                poses.Add((j1.Max, j3.Max, j2.Max));
            }
            return poses;
        }

        /// <summary>Runs the FK comparison. Returns failure messages; empty list = PASS.
        /// <paramref name="tol"/> is the max allowed TCP deviation in mm.</summary>
        public static List<string> Run(RobotDefinition def, double tol = 1e-6)
        {
            var failures = new List<string>();
            var l1 = def.Link("link1");
            var l2 = def.Link("link2");
            if (l1 == null || l2 == null || l1.Length <= 0 || l2.Length <= 0)
            {
                failures.Add("definition has no link1/link2 lengths - cannot derive expected TCP positions");
                return failures;
            }
            double L1 = l1.Length, L2 = l2.Length;

            foreach (var (j1, j3, j2) in TestPoses(def))
            {
                Vec3 posed;
                try
                {
                    posed = RobotPosing.ToolTipWorld(def, j1, j3, j2);
                }
                catch (Exception ex)
                {
                    failures.Add(FormattableString.Invariant($"pose (J1={j1:0.##}, J3={j3:0.##}, J2={j2:0.##}): posing threw {ex.Message}"));
                    continue;
                }
                var (ex2, ey, ez) = ScaraKinematics.Forward(j1, j3, j2, L1, L2, 0.0);
                double dx = posed.X - ex2, dy = posed.Y - ey, dz = posed.Z - ez;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > tol)
                    failures.Add(
                        FormattableString.Invariant($"pose (J1={j1:0.##}°, J3={j3:0.##}°, J2={j2:0.##}mm): posed nozzle ") +
                        FormattableString.Invariant($"({posed.X:0.####}, {posed.Y:0.####}, {posed.Z:0.####}) deviates {d:0.######}mm ") +
                        FormattableString.Invariant($"from analytic FK ({ex2:0.####}, {ey:0.####}, {ez:0.####})"));
            }
            return failures;
        }
    }
}
