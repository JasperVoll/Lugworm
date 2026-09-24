using System;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>What to do with the LINE between two preserved observations.</summary>
    public enum TrailLinkDecision
    {
        /// <summary>Draw the straight segment: nothing known about the interval argues
        /// against it.</summary>
        Draw,
        /// <summary>Withhold the line. Both observations are still drawn as points and both
        /// keep their reported coordinates exactly; only the claim "the machine went
        /// straight from here to there" is not made.</summary>
        WithholdLine,
    }

    /// <summary>The decision for one link, with the reason it was taken.</summary>
    /// <remarks>The reason exists so a break in the live trail can be ATTRIBUTED in the
    /// diagnostics block instead of guessed at - see <see cref="TrailBreakCounters"/>.
    /// An operator reporting "there are splits at the seams" must be answerable with the
    /// count and the wording of what actually withheld the lines.</remarks>
    public readonly struct TrailLinkVerdict
    {
        public TrailLinkDecision Decision { get; }

        /// <summary>Why the line was withheld; null when it is drawn.</summary>
        public string? Reason { get; }

        /// <summary>Convenience for the caller's <c>if</c>: true when the line may be drawn.</summary>
        public bool Drawable
        {
            get { return Decision == TrailLinkDecision.Draw; }
        }

        public TrailLinkVerdict(TrailLinkDecision decision, string? reason)
        {
            Decision = decision;
            Reason = reason;
        }
    }

    /// <summary>Thresholds for <see cref="TrailChordPolicy"/>. Every one is about
    /// ACQUISITION, never about motion: they describe when the display stopped looking long
    /// enough that the route between two samples became unknown.</summary>
    public sealed class TrailChordOptions
    {
        /// <summary>Retained for source compatibility; endpoint distance never exempts a
        /// link from acquisition-time uncertainty.</summary>
        public double MinChordMm = TrailChordPolicy.DefaultMinChordMm;

        /// <summary>Absolute acquisition gap above which the route between two samples is
        /// unknown, whatever the display's cadence happens to be.</summary>
        public double SparseGapSeconds = TrailChordPolicy.DefaultSparseGapSeconds;

        /// <summary>Retained for source compatibility; cadence does not relax the absolute
        /// acquisition gap limit.</summary>
        public double SparseIntervalFactor = TrailChordPolicy.DefaultSparseIntervalFactor;

        public TrailChordOptions Copy()
        {
            return (TrailChordOptions)MemberwiseClone();
        }
    }

    /// <summary>May a straight line between two observed positions be drawn at all?</summary>
    /// <remarks>
    /// <para>Separate from <see cref="TrailContinuityPolicy"/> on purpose. Continuity asks
    /// "do these two observations belong to the same run"; this asks the narrower DRAWING
    /// question "would a straight segment between them assert a route that was never
    /// observed". A drawing gap is not a continuity break: the two samples still belong to
    /// the same run, the display simply declines to guess how the machine got between
    /// them.</para>
    /// <para><see cref="Evaluate"/> uses the ACQUISITION GAP - the actual time between the two
    /// coordinate responses, measured against an absolute acquisition gap limit. When the
    /// display looked away for longer than that limit, anything could
    /// have happened in between and the line is withheld (the 2026-09-09 dome capture's
    /// exported rows are a median 1.94 s apart, which drew visible chords straight across a
    /// circular contour). A slow nominal cadence does not make that route known.</para>
    /// <para>SPEED PLAYS NO PART, and this is the whole point of the 2026-09-09 rewrite. The
    /// previous rule compared <c>requestedSpeed * elapsedTime</c> with the chord and withheld
    /// the line when the machine "could have" travelled materially farther. Requested speed
    /// is not distance actually travelled: a 3 mm chord over 0.25 s at a requested 40 mm/s
    /// was rejected because 10 &gt; 3 * 1.25 + 1, even though deceleration into a seam, or
    /// part of the interval spent stationary for a prime or a retract, explains that
    /// observation exactly as well as a curved detour does. That false rejection is the
    /// operator's reported "there are also splits at the seams" (checklist row 10a.30).</para>
    /// <para>Nor may <c>chord / elapsed</c> be used as independent evidence about the route:
    /// that is circular, because the chord is the very thing in question. A short chord
    /// yields a low implied speed, and a low implied speed would then be read as proof that
    /// the route was short - which is the conclusion, not the evidence. So the chord's only
    /// role in the timing check is validation. Nearby endpoints can hide a complete contour.</para>
    /// <para>Raw data is preserved unconditionally. Nothing here edits, clamps, projects,
    /// smooths or deletes a measured coordinate; the single available outcome is to withhold
    /// a line between two points that both stay exactly as reported.</para>
    /// </remarks>
    public static class TrailChordPolicy
    {
        /// <summary>Legacy option default; no longer used to bypass uncertainty.</summary>
        public const double DefaultMinChordMm = 2.0;

        /// <summary>0.75 s: three ordinary 250 ms acquisitions. A display that misses two
        /// consecutive samples has stopped observing rather than sampled coarsely, and the
        /// 2026-09-09 sparse rows (median 1.94 s) sit far above it.</summary>
        public const double DefaultSparseGapSeconds = 0.75;

        /// <summary>Legacy option default; no longer used to relax the absolute limit.</summary>
        public const double DefaultSparseIntervalFactor = 2.5;

        /// <summary>One shared instance for the null-options path: this runs once per drawn
        /// link, so allocating a fresh options object per call would be pure garbage. Private
        /// and never handed out, so nothing can mutate it out from under a caller.</summary>
        static readonly TrailChordOptions Defaults = new TrailChordOptions();

        /// <summary>Decides whether the line between two preserved observations may be drawn.</summary>
        /// <param name="chordMm">Straight-line distance between the two preserved
        /// observations. Validated as finite and nonnegative; never divided by time.</param>
        /// <param name="elapsedSeconds">ACTUAL time between the two coordinate responses -
        /// not the configured poll interval, and not a publication timestamp. Unknown or
        /// non-positive withholds: uncertainty withholds, it does not assert.</param>
        /// <param name="nominalIntervalSeconds">The display's observed acquisition cadence
        /// (for example the recent interval p50 or p95), or NaN / non-positive when it is not
        /// established. Used only in the diagnostic reason.</param>
        /// <param name="options">Thresholds; null uses the documented defaults.</param>
        public static TrailLinkVerdict Evaluate(double chordMm, double elapsedSeconds,
                                                double nominalIntervalSeconds,
                                                TrailChordOptions? options = null)
        {
            TrailChordOptions opt = options ?? Defaults;

            // 1. A chord that is not a number is not a measurement. Refusing to draw is the
            //    only honest answer: the caller has no two positions to join.
            if (double.IsNaN(chordMm) || double.IsInfinity(chordMm) || chordMm < 0)
                return Withhold("chord unknown");

            // 3. Without the actual interval there is no evidence either way, and the
            //    asymmetry is deliberate: withholding a line loses a drawn segment, drawing
            //    one invents a movement. Uncertainty must cost the cheaper of the two.
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds <= 0)
                return Withhold("elapsed time unknown");

            // Slow acquisition must not normalize an unobserved contour into a line.
            double threshold = opt.SparseGapSeconds;
            if (double.IsNaN(threshold) || double.IsInfinity(threshold) || threshold <= 0)
                threshold = DefaultSparseGapSeconds;
            bool cadenceKnown = !double.IsNaN(nominalIntervalSeconds)
                                && !double.IsInfinity(nominalIntervalSeconds)
                                && nominalIntervalSeconds > 0;

            if (elapsedSeconds > threshold)
            {
                var inv = CultureInfo.InvariantCulture;
                string cadence = cadenceKnown
                    ? " at a " + nominalIntervalSeconds.ToString("0.###", inv) + " s cadence"
                    : " with no established cadence";
                return Withhold(elapsedSeconds.ToString("0.###", inv) + " s between observations"
                                + cadence + " - the route between them is unknown");
            }

            // Within the display's acquisition uncertainty budget. This is not proof of
            // the physical route, only permission to render a sampled link.
            return Draw();
        }

        static TrailLinkVerdict Draw()
        {
            return new TrailLinkVerdict(TrailLinkDecision.Draw, null);
        }

        static TrailLinkVerdict Withhold(string reason)
        {
            return new TrailLinkVerdict(TrailLinkDecision.WithholdLine, reason);
        }
    }
}
