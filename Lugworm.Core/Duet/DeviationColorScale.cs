using System;

namespace Lugworm.Core.Duet
{
    /// <summary>The observed-versus-intended deviation colour scale: Viridis from purple
    /// (no deviation) to yellow (at or above the legend maximum), plus one grey for an
    /// unavailable comparison. Pure RGB bytes - the UI layer turns them into colours.</summary>
    /// <remarks>The legend maximum is fixed by the user, never auto-rescaled per frame:
    /// a colour must keep meaning the same millimetres for the whole trail, or the
    /// history would silently recolour itself as the print goes on. Distances above the
    /// maximum saturate to the top colour and are reported as saturated.</remarks>
    public static class DeviationColorScale
    {
        /// <summary>Colour of an observation whose comparison could not be made
        /// (no intended path, invalid telemetry, exhausted budget). Never a Viridis hue,
        /// so it can never be mistaken for "zero deviation".</summary>
        public static readonly (byte R, byte G, byte B) Unavailable = (150, 150, 150);

        /// <summary>Default legend maximum, mm.</summary>
        public const double DefaultLegendMm = 5.0;

        // matplotlib viridis sampled at t = 0.0, 0.1, ..., 1.0
        static readonly (byte R, byte G, byte B)[] Anchors =
        {
            (68, 1, 84), (72, 35, 116), (64, 67, 135), (52, 94, 141), (41, 120, 142), (32, 144, 140),
            (34, 167, 132), (68, 190, 112), (121, 209, 81), (189, 222, 38), (253, 231, 37),
        };

        /// <summary>Viridis at <paramref name="t"/> in [0, 1]; NaN and out-of-range values clamp.</summary>
        public static (byte R, byte G, byte B) Map(double t)
        {
            if (double.IsNaN(t) || t <= 0) return Anchors[0];
            if (t >= 1) return Anchors[Anchors.Length - 1];
            double x = t * (Anchors.Length - 1);
            int i = (int)Math.Floor(x);
            if (i >= Anchors.Length - 1) return Anchors[Anchors.Length - 1];
            double u = x - i;
            var a = Anchors[i];
            var b = Anchors[i + 1];
            return (Lerp(a.R, b.R, u), Lerp(a.G, b.G, u), Lerp(a.B, b.B, u));
        }

        /// <summary>Colour for a nearest-path distance against a fixed legend maximum.
        /// NaN (unavailable) is grey; negative distances count as zero; at or above the
        /// maximum the colour saturates.</summary>
        public static (byte R, byte G, byte B) ForDistance(double distanceMm, double legendMaxMm)
        {
            if (double.IsNaN(distanceMm) || double.IsInfinity(distanceMm)) return Unavailable;
            if (!(legendMaxMm > 0)) return Unavailable;
            return Map(distanceMm / legendMaxMm);
        }

        /// <summary>True when the distance is at or beyond the legend maximum, i.e. its
        /// colour no longer distinguishes it from a larger deviation.</summary>
        public static bool Saturated(double distanceMm, double legendMaxMm)
            => !double.IsNaN(distanceMm) && legendMaxMm > 0 && distanceMm >= legendMaxMm;

        static byte Lerp(byte a, byte b, double u) => (byte)Math.Round(a + (b - a) * u);
    }
}
