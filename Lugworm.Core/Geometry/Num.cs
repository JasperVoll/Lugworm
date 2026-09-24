using System;

namespace Lugworm.Core.Geometry
{
    /// <summary>Shared small numeric helpers (finiteness checks, clamping) used across
    /// Core - the one definition each call site should share instead of a private copy.
    /// Public rather than internal: the plugin project has no InternalsVisibleTo grant
    /// onto Core, and its own copies (Ui/SettingsIo, Ui/GraphMapEditor) call this too.</summary>
    public static class Num
    {
        /// <summary>True when <paramref name="v"/> is neither NaN nor +/-Infinity.</summary>
        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>True when every component of <paramref name="p"/> is finite.</summary>
        public static bool Finite(Vec3 p) => Finite(p.X) && Finite(p.Y) && Finite(p.Z);

        /// <summary>Clamps <paramref name="v"/> to [<paramref name="lo"/>, <paramref name="hi"/>].</summary>
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Clamps <paramref name="v"/> to [0, 1]. NaN passes through unchanged, as
        /// every private copy this replaced did (GraphMap, SeamAnchor, PolylineOps, the
        /// graph editor): an invalid input must stay visibly invalid rather than become a
        /// valid endpoint. A caller that wants NaN read as 0 guards it itself
        /// (PrintConsoleReducer does, for the job fraction).</summary>
        public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
