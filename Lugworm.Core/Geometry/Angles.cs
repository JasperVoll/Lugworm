using System;

namespace Lugworm.Core.Geometry
{
    /// <summary>Angle wrapping, in one place. Before the 2026-07-14 run these were
    /// re-implemented several times (ScaraKinematics, RobotPosing, RobotChecks)
    /// with identical semantics.</summary>
    public static class Angles
    {
        /// <summary>Wrap degrees to [−180, 180).</summary>
        public static double NormalizeDeg180(double deg)
        {
            double m = (deg + 180.0) % 360.0;
            if (m < 0) m += 360.0;
            return m - 180.0;
        }

    }
}
