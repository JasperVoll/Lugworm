using System;

namespace Lugworm.Core.Analysis
{
    /// <summary>A periodic, unit-amplitude profile: <c>u</c> in [0, 1) over one
    /// period maps to -1..+1. Shared by the wave infill pattern (rows follow it in
    /// XY, <c>Infill.WaveSpans</c>) and the non-planar infill pass (rows follow it
    /// in Z, <c>NonPlanarInfill.Apply</c>). Callers reduce <c>u</c> modulo 1 before
    /// calling; implementations must still accept any finite value.</summary>
    public interface IWaveProfile
    {
        double At(double u);
    }

    /// <summary>sin(2*pi*u): the plain wave, crest at u = 0.25, trough at 0.75.</summary>
    public sealed class SineProfile : IWaveProfile
    {
        public double At(double u) => Math.Sin(2.0 * Math.PI * u);
    }

    /// <summary>A Graph Mapper curve as a wave: <c>2*(f(u) - 0.5)</c>, so the graph's
    /// bottom edge is the trough and its top edge the crest. Ping-pong by default:
    /// <c>u -> 2u</c> for <c>u &lt; 0.5</c> and <c>2 - 2u</c> above, so ANY curve tiles
    /// C0-continuously even when <c>f(0) != f(1)</c> - one period then holds the
    /// curve forward and mirrored, which halves the apparent period of a curve that
    /// already closes on itself. <see cref="Repeat"/> plays the curve forward every
    /// period instead; when the ends differ the wave then carries a seam, which
    /// <see cref="EndsMismatch"/> exposes for the caller to warn about (Core does not
    /// warn - the component does).</summary>
    public sealed class GraphProfile : IWaveProfile
    {
        readonly GraphMap _map;

        /// <summary>Largest end-to-end difference a repeated curve may have before
        /// the seam is reported.</summary>
        public const double EndTolerance = 1e-3;

        public GraphProfile(GraphMap map, bool repeat = false)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            // a private copy: the component keeps editing its map on the canvas
            _map = map.Copy();
            Repeat = repeat;
        }

        /// <summary>False (default): ping-pong. True: the curve repeats forward.</summary>
        public bool Repeat { get; }

        /// <summary>True when <c>|f(0) - f(1)| &gt; EndTolerance</c> - a repeated wave
        /// would jump at every period boundary. Irrelevant under ping-pong.</summary>
        public bool EndsMismatch => Math.Abs(_map.Evaluate(0.0) - _map.Evaluate(1.0)) > EndTolerance;

        public double At(double u)
        {
            u -= Math.Floor(u);
            if (!Repeat) u = u < 0.5 ? 2.0 * u : 2.0 - 2.0 * u;
            // Evaluate clamps y into [0, 1], so the result is already within -1..+1
            return 2.0 * (_map.Evaluate(u) - 0.5);
        }
    }
}
