using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Forward posing of the SCARA assembly: turns IK joint values
    /// (J1, J3 deg; J2 mm) into a world 4x4 transform per link, so the .3dm meshes can be
    /// drawn at the scrub pose. Pure - no Rhino; matrices are row-major double[16].
    ///
    /// Model: revolute joints rotate about vertical Z; the prismatic lift translates along
    /// Z. Meshes live in assembly coords; a registration R maps the J1 shoulder axis to the
    /// IK/world origin so the posed nozzle tracks the toolpath. Joint angle deltas are taken
    /// from the home directions implied by the measured pivots (so the math is independent
    /// of any rounding in the stored home values). Kinematic link lengths come from the
    /// firmware config (link.Length), and the elbow/nozzle pivots are placed at those
    /// distances along the measured home directions (matching the mesh axial-fit).</summary>
    public static class RobotPosing
    {
        /// <summary>World 4x4 (row-major double[16]) per link name, at pose (j1,j3,j2).</summary>
        public static Dictionary<string, double[]> LinkWorldTransforms(
            RobotDefinition def, double j1, double j3, double j2)
        {
            var k = Kinematics.From(def);
            var asm = AssemblyFrames(def, k, j1, j3, j2);
            var R = k.Registration;
            var world = new Dictionary<string, double[]>(asm.Count);
            foreach (var kv in asm) world[kv.Key] = Mul(R, kv.Value);
            return world;
        }

        /// <summary>World position of the nozzle tip at pose (j1,j3,j2). Equals
        /// <see cref="ScaraKinematics.Forward"/> with the firmware link lengths.</summary>
        public static Vec3 ToolTipWorld(RobotDefinition def, double j1, double j3, double j2)
        {
            var k = Kinematics.From(def);
            var asm = AssemblyFrames(def, k, j1, j3, j2);
            var toolName = ToolLinkName(def);
            var T = Mul(k.Registration, asm.TryGetValue(toolName, out var t) ? t : Identity());
            return TransformPoint(T, k.NozzleK);
        }

        // ---- internals ----------------------------------------------------

        static Dictionary<string, double[]> AssemblyFrames(
            RobotDefinition def, Kinematics k, double j1, double j3, double j2)
        {
            var frames = new Dictionary<string, double[]>();
            // base link(s): every link with no inbound joint stays at identity (assembly pose)
            foreach (var ln in def.Links) frames[ln.Name] = Identity();

            foreach (var jt in def.Joints)
            {
                var parent = frames.TryGetValue(jt.Parent, out var pf) ? pf : Identity();
                double[] child;
                if (jt.Kind == JointKind.Fixed)
                {
                    child = parent; // rigidly attached to parent
                }
                else if (jt.Kind == JointKind.Prismatic)
                {
                    double d = ValueFor(jt, j1, j3, j2) - jt.Home;          // mm
                    var axisW = TransformVector(parent, jt.Axis);
                    child = Mul(Translation(axisW.X * d, axisW.Y * d, axisW.Z * d), parent);
                }
                else // revolute
                {
                    double homeDeg = HomeAngle(jt, k);
                    double deltaRad = (ValueFor(jt, j1, j3, j2) - homeDeg) * Math.PI / 180.0;
                    var pivotHome = PivotHome(jt, k);
                    var pivotW = TransformPoint(parent, pivotHome);
                    var axisW = Unit(TransformVector(parent, jt.Axis));
                    child = Mul(RotateAboutAxis(axisW, deltaRad, pivotW), parent);
                }
                frames[jt.Child] = child;
            }
            return frames;
        }

        static double ValueFor(RobotJoint jt, double j1, double j3, double j2) => jt.Name switch
        {
            "J1" => j1,
            "J3" => j3,
            "J2" => j2,
            _ => jt.Home, // unknown driven joint -> stays at home
        };

        // The home angle / pivot for the two revolutes come from the measured directions so
        // the posed nozzle is exactly FK(j1,j3). J1 pivots at the measured shoulder; J3
        // pivots at the firmware-distance elbow (elbow_k), matching the mesh axial-fit.
        static double HomeAngle(RobotJoint jt, Kinematics k) => jt.Name switch
        {
            "J1" => k.H1Deg,
            "J3" => k.H3Deg,
            _ => jt.Home,
        };

        static Vec3 PivotHome(RobotJoint jt, Kinematics k) => jt.Name switch
        {
            "J1" => k.Shoulder,
            "J3" => k.ElbowK,
            _ => jt.Pivot,
        };

        static string ToolLinkName(RobotDefinition def)
        {
            var jtip = def.Joint("Jtip");
            return jtip != null ? jtip.Child : "tool";
        }

        /// <summary>Derived kinematic data: home directions, firmware-distance pivots, and
        /// the assembly->world registration.</summary>
        sealed class Kinematics
        {
            public Vec3 Shoulder, ElbowK, NozzleK;
            public double H1Deg, H3Deg;
            public double[] Registration = Identity();

            public static Kinematics From(RobotDefinition def)
            {
                var k = new Kinematics();
                var j1 = def.Joint("J1"); var j3 = def.Joint("J3"); var jt = def.Joint("Jtip");
                var l1 = def.Link("link1"); var l2 = def.Link("link2");
                var shoulder = j1?.Pivot ?? default;
                var elbowMeas = j3?.Pivot ?? default;
                var nozzleMeas = jt?.Pivot ?? default;
                double L1 = l1?.Length ?? 0.0;
                double L2 = l2?.Length ?? 0.0;

                var u1 = UnitXY(Sub(elbowMeas, shoulder));
                k.H1Deg = Math.Atan2(u1.Y, u1.X) * 180.0 / Math.PI;
                k.Shoulder = shoulder;
                k.ElbowK = new Vec3(shoulder.X + L1 * u1.X, shoulder.Y + L1 * u1.Y, elbowMeas.Z);

                var u2 = UnitXY(Sub(nozzleMeas, elbowMeas));
                double ang2 = Math.Atan2(u2.Y, u2.X) * 180.0 / Math.PI;
                k.H3Deg = Norm180(ang2 - k.H1Deg);
                k.NozzleK = new Vec3(k.ElbowK.X + L2 * u2.X, k.ElbowK.Y + L2 * u2.Y, nozzleMeas.Z);

                // registration: J1 shoulder axis -> world origin, optional yaw, + Base offset
                var toOrigin = Translation(-shoulder.X, -shoulder.Y, 0.0);
                var yaw = RotateAboutAxis(new Vec3(0, 0, 1), def.BaseYaw * Math.PI / 180.0, default);
                var baseT = Translation(def.Base.X, def.Base.Y, def.Base.Z);
                k.Registration = Mul(baseT, Mul(yaw, toOrigin));
                return k;
            }
        }

        // ---- vector helpers ----
        static Vec3 Sub(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        static Vec3 UnitXY(Vec3 v)
        {
            double n = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return n < 1e-12 ? new Vec3(1, 0, 0) : new Vec3(v.X / n, v.Y / n, 0);
        }
        static Vec3 Unit(Vec3 v)
        {
            double n = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return n < 1e-12 ? new Vec3(0, 0, 1) : new Vec3(v.X / n, v.Y / n, v.Z / n);
        }
        static double Norm180(double deg) => Angles.NormalizeDeg180(deg);

        // ---- 4x4 (row-major double[16]) ----
        public static double[] Identity() => new double[]
        {
            1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1
        };

        public static double[] Translation(double x, double y, double z)
        {
            var m = Identity(); m[3] = x; m[7] = y; m[11] = z; return m;
        }

        /// <summary>Rotation by <paramref name="ang"/> rad about unit <paramref name="axis"/>
        /// through <paramref name="pivot"/>.</summary>
        static double[] RotateAboutAxis(Vec3 axis, double ang, Vec3 pivot)
        {
            double c = Math.Cos(ang), s = Math.Sin(ang), t = 1 - c;
            double ax = axis.X, ay = axis.Y, az = axis.Z;
            var rot = new double[]
            {
                t*ax*ax + c,    t*ax*ay - s*az, t*ax*az + s*ay, 0,
                t*ax*ay + s*az, t*ay*ay + c,    t*ay*az - s*ax, 0,
                t*ax*az - s*ay, t*ay*az + s*ax, t*az*az + c,    0,
                0,              0,              0,              1,
            };
            // T(pivot) * R * T(-pivot)
            return Mul(Translation(pivot.X, pivot.Y, pivot.Z),
                       Mul(rot, Translation(-pivot.X, -pivot.Y, -pivot.Z)));
        }

        static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) sum += a[i * 4 + k] * b[k * 4 + j];
                    r[i * 4 + j] = sum;
                }
            return r;
        }

        static Vec3 TransformPoint(double[] m, Vec3 v)
        {
            double x = m[0] * v.X + m[1] * v.Y + m[2] * v.Z + m[3];
            double y = m[4] * v.X + m[5] * v.Y + m[6] * v.Z + m[7];
            double z = m[8] * v.X + m[9] * v.Y + m[10] * v.Z + m[11];
            return new Vec3(x, y, z);
        }

        static Vec3 TransformVector(double[] m, Vec3 v)
        {
            double x = m[0] * v.X + m[1] * v.Y + m[2] * v.Z;
            double y = m[4] * v.X + m[5] * v.Y + m[6] * v.Z;
            double z = m[8] * v.X + m[9] * v.Y + m[10] * v.Z;
            return new Vec3(x, y, z);
        }
    }
}
