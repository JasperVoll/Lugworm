using System;

namespace Lugworm.Core.Analysis
{
    /// <summary>The interlayer-time colour scale: matplotlib Plasma from deep blue
    /// (no time elapsed since the layer below was laid) through magenta and orange to
    /// yellow (at or above the legend maximum), plus one grey for a point whose layer
    /// below could not be found. Pure RGB bytes - the UI layer turns them into colours.</summary>
    /// <remarks>Sibling of <c>DeviationColorScale</c> (Viridis, millimetres): the same
    /// contract, a different hue family so the two overlays can never be confused when
    /// both are visible. The legend maximum is chosen by the user or once per analysis,
    /// never per frame, so a colour keeps meaning the same seconds for the whole part.</remarks>
    public static class PlasmaColorScale
    {
        /// <summary>Colour of a point with no layer below it inside the search radius
        /// (first layer, bridge, overhang, or sparse log samples). Never a Plasma hue.</summary>
        public static readonly (byte R, byte G, byte B) Unavailable = (150, 150, 150);

        // matplotlib plasma sampled at t = 0.0, 0.1, ..., 1.0
        static readonly (byte R, byte G, byte B)[] Anchors =
        {
            (13, 8, 135), (65, 4, 157), (106, 0, 168), (143, 13, 164), (177, 42, 144), (204, 71, 120),
            (225, 100, 98), (242, 132, 75), (252, 166, 54), (252, 206, 37), (240, 249, 33),
        };

        /// <summary>Plasma at <paramref name="t"/> in [0, 1]; NaN and out-of-range values clamp.</summary>
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

        /// <summary>Colour for an interlayer time against a fixed legend maximum. NaN
        /// (unavailable) is grey; negative times count as zero; at or above the maximum
        /// the colour saturates.</summary>
        public static (byte R, byte G, byte B) ForSeconds(double seconds, double legendMaxS)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return Unavailable;
            if (!(legendMaxS > 0)) return Unavailable;
            return Map(seconds / legendMaxS);
        }

        /// <summary>True when the time is at or beyond the legend maximum, i.e. its colour
        /// no longer distinguishes it from a longer wait.</summary>
        public static bool Saturated(double seconds, double legendMaxS)
            => !double.IsNaN(seconds) && legendMaxS > 0 && seconds >= legendMaxS;

        static byte Lerp(byte a, byte b, double u) => (byte)Math.Round(a + (b - a) * u);
    }
}
