using System.Globalization;
using System.Threading;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The one drawing rule that is not about continuity: whether a straight line
/// between two preserved observations may be drawn at all.</summary>
/// <remarks>
/// <para>The rule under test is a bounded UNCERTAINTY policy. Its only evidence is the
/// acquisition gap - how long the display stopped looking - measured against the display's
/// own observed cadence. Speed plays no part, and neither does chord/elapsed: the chord is
/// the very thing in question, so using it to judge the route would be circular.</para>
/// <para>Two acceptance conditions pull in opposite directions and both must hold. The
/// 2026-09-09 sparse-contour artifact (2 s apart on a small circle) must stay withheld, and
/// the operator's reported seam splits (row 10a.30, ordinary 250 ms samples around a
/// decelerating seam) must stop happening.</para>
/// </remarks>
public class TrailChordPolicyTests
{
    /// <summary>The 2026-09-09 dome display's cadence: a 250 ms coordinate acquisition.</summary>
    const double Cadence = 0.25;

    static bool Drawable(double chordMm, double elapsedSeconds, double nominalSeconds = Cadence)
        => TrailChordPolicy.Evaluate(chordMm, elapsedSeconds, nominalSeconds).Drawable;

    // ---- the seam regression (checklist row 10a.30) ----------------------------------

    /// <summary>THE regression this policy exists for. A 3 mm chord over an ordinary 250 ms
    /// acquisition was rejected by the old rule because a requested 40 mm/s "could have"
    /// covered 10 mm and 10 &gt; 3 * 1.25 + 1. Deceleration into the seam, or part of the
    /// interval spent stationary for a prime or a retract, explains the same 3 mm exactly as
    /// well - so the old rule split every seam it met.</summary>
    [Fact]
    public void A_short_chord_at_an_ordinary_cadence_is_drawn_however_fast_the_feed_was()
    {
        var verdict = TrailChordPolicy.Evaluate(3.0, 0.25, Cadence);
        Assert.Equal(TrailLinkDecision.Draw, verdict.Decision);
        Assert.True(verdict.Drawable);
        Assert.Null(verdict.Reason);
        // No overload takes a feed rate, so no caller can reintroduce the old inference:
        // the verdict is a function of the acquisition gap and the chord alone.
        Assert.True(Drawable(3.0, 0.25));
    }

    // ---- ordinary printing -----------------------------------------------------------

    /// <summary>A constant-speed layer at the display's own cadence. 43 mm/s for 250 ms is
    /// 10.75 mm; on a 50 mm radius the chord is 10.73 mm. Every link of an ordinary layer
    /// must be drawn or the trail fragments into hundreds of runs.</summary>
    [Fact]
    public void A_constant_speed_layer_at_the_nominal_cadence_draws_every_link()
    {
        for (int i = 0; i < 40; i++) Assert.True(Drawable(10.73, 0.25));
    }

    /// <summary>Travel decelerating from 40 mm/s to 10 mm/s across one ordinary interval:
    /// the chord is far shorter than the entry speed would suggest, which is what
    /// deceleration means. That is not evidence of a detour.</summary>
    [Fact]
    public void Travel_decelerating_across_an_ordinary_interval_is_drawn()
    {
        // 40 -> 10 mm/s over 250 ms covers about 6.25 mm, against 10 mm at a held 40 mm/s.
        Assert.True(Drawable(6.25, 0.25));
    }

    /// <summary>Stop, prime, retract, resume at a seam: the machine spent most of a 300 ms
    /// interval stationary, so the chord is nearly zero. Nothing was invented by joining
    /// two points the machine demonstrably stayed between.</summary>
    [Fact]
    public void A_dwell_at_a_seam_is_drawn()
    {
        Assert.True(Drawable(0.05, 0.3));
        Assert.True(Drawable(0.0, 0.3));
    }

    /// <summary>A long legitimate travel move inside one ordinary interval. The old rule
    /// drew this one for the wrong reason (the requested feed happened to match); the new
    /// rule draws it because the display never stopped watching. Speed plays no part in
    /// either direction.</summary>
    [Fact]
    public void A_long_travel_inside_one_ordinary_interval_is_drawn()
    {
        Assert.True(Drawable(60.0, 0.25));
        Assert.True(Drawable(200.0, 0.3));
    }

    // ---- the sparse-sample protection (ported acceptance condition) --------------------

    /// <summary>The 2026-09-09 artifact, kept exactly as it was stated against the old API:
    /// samples about 2 s apart draw a straight line across a small contour the machine never
    /// travelled (43 mm/s for 2 s is 86 mm of arc; on a 25 mm radius the chord is only
    /// 49.4 mm). This is an acceptance condition, not a test to delete - a short chord alone
    /// never proves the route between the samples was short.</summary>
    [Fact]
    public void The_2026_09_09_sparse_chord_across_a_small_contour_is_withheld()
    {
        var verdict = TrailChordPolicy.Evaluate(49.4, 2.0, Cadence);
        Assert.Equal(TrailLinkDecision.WithholdLine, verdict.Decision);
        Assert.NotNull(verdict.Reason);
        Assert.Contains("2 s between observations", verdict.Reason!);
        Assert.Contains("0.25 s cadence", verdict.Reason!);
        Assert.Contains("route between them is unknown", verdict.Reason!);
    }

    /// <summary>Nearby endpoints do not rule out a full contour between acquisitions.</summary>
    [Fact]
    public void A_long_gap_with_a_tiny_chord_is_withheld()
    {
        Assert.False(Drawable(0.3, 2.0));
        Assert.False(Drawable(TrailChordPolicy.DefaultMinChordMm, 30.0));
        Assert.False(Drawable(0.3, double.NaN));
    }

    /// <summary>A slow display must not normalize its own sparse shortcuts.</summary>
    [Fact]
    public void The_absolute_gap_limit_also_applies_at_a_slow_nominal_cadence()
    {
        Assert.False(Drawable(49.4, 2.0, nominalSeconds: 1.0));
        Assert.False(Drawable(49.4, 2.0, nominalSeconds: 2.0));
        Assert.False(Drawable(49.4, 3.0, nominalSeconds: 1.0));

        // Dense acquisitions retain the same bound.
        Assert.True(Drawable(49.4, 0.7, nominalSeconds: 0.05));
        Assert.False(Drawable(49.4, 0.8, nominalSeconds: 0.05));
    }

    // ---- the unknowns ------------------------------------------------------------------

    /// <summary>Unknown elapsed time withholds: uncertainty withholds, it does not assert.
    /// Withholding a line loses a drawn segment; drawing one invents a movement, so the
    /// cheaper mistake is the one uncertainty is allowed to make.</summary>
    [Fact]
    public void An_unknown_elapsed_time_withholds()
    {
        Assert.False(Drawable(50, double.NaN));
        Assert.False(Drawable(50, 0));
        Assert.False(Drawable(50, -1));
        Assert.False(Drawable(50, double.PositiveInfinity));
        Assert.Equal("elapsed time unknown", TrailChordPolicy.Evaluate(50, 0, Cadence).Reason);
    }

    /// <summary>A chord that is not a measurement is not two positions to join.</summary>
    [Fact]
    public void An_unknown_chord_withholds()
    {
        Assert.False(Drawable(double.NaN, 0.25));
        Assert.False(Drawable(double.PositiveInfinity, 0.25));
        Assert.False(Drawable(-1, 0.25));
        Assert.Equal("chord unknown", TrailChordPolicy.Evaluate(double.NaN, 0.25, Cadence).Reason);
    }

    /// <summary>An unestablished cadence leaves the absolute gap alone in charge: it does
    /// not withhold on its own (the display simply has not measured itself yet) and it does
    /// not license a gap either.</summary>
    [Fact]
    public void An_unknown_nominal_interval_falls_back_to_the_absolute_gap()
    {
        Assert.True(Drawable(49.4, 0.25, nominalSeconds: double.NaN));
        Assert.True(Drawable(49.4, 0.7, nominalSeconds: 0));
        Assert.False(Drawable(49.4, 2.0, nominalSeconds: double.NaN));
        Assert.Contains("no established cadence",
                        TrailChordPolicy.Evaluate(49.4, 2.0, double.NaN).Reason!);
    }

    // ---- the guarantees ----------------------------------------------------------------

    /// <summary>Speed is not a parameter and cannot become one by the back door. The same
    /// chord and interval, whatever a caller might believe about the feed, gives one answer;
    /// and chord/elapsed is never consulted, so a 6 mm and a 60 mm chord over the same
    /// ordinary interval are both drawn while the same two chords over a 2 s gap are both
    /// withheld.</summary>
    [Fact]
    public void The_decision_depends_on_the_acquisition_gap_and_not_on_the_implied_speed()
    {
        // 24 mm/s and 240 mm/s implied, same interval, same verdict.
        Assert.True(Drawable(6.0, 0.25));
        Assert.True(Drawable(60.0, 0.25));
        // 3 mm/s and 30 mm/s implied, same gap, same verdict.
        Assert.False(Drawable(6.0, 2.0));
        Assert.False(Drawable(60.0, 2.0));
    }

    /// <summary>Options are copied by <see cref="TrailChordOptions.Copy"/> and honoured, so
    /// a display with a different cadence policy is configured rather than forked.</summary>
    [Fact]
    public void Options_are_honoured_and_copied()
    {
        var opt = new TrailChordOptions { MinChordMm = 10.0, SparseGapSeconds = 5.0 };
        Assert.Equal(TrailLinkDecision.Draw, TrailChordPolicy.Evaluate(9.0, 2.0, Cadence, opt).Decision);
        Assert.Equal(TrailLinkDecision.Draw, TrailChordPolicy.Evaluate(49.4, 4.0, Cadence, opt).Decision);
        Assert.Equal(TrailLinkDecision.WithholdLine, TrailChordPolicy.Evaluate(49.4, 6.0, Cadence, opt).Decision);

        var copy = opt.Copy();
        Assert.Equal(10.0, copy.MinChordMm);
        Assert.Equal(5.0, copy.SparseGapSeconds);
        copy.MinChordMm = 1.0;
        Assert.Equal(10.0, opt.MinChordMm);
    }

    /// <summary>The defaults the class documents are the defaults the options carry: the
    /// reasoning in the remarks is about the numbers actually in force.</summary>
    [Fact]
    public void The_documented_defaults_are_the_ones_in_force()
    {
        var opt = new TrailChordOptions();
        Assert.Equal(TrailChordPolicy.DefaultMinChordMm, opt.MinChordMm);
        Assert.Equal(TrailChordPolicy.DefaultSparseGapSeconds, opt.SparseGapSeconds);
        Assert.Equal(TrailChordPolicy.DefaultSparseIntervalFactor, opt.SparseIntervalFactor);
        Assert.Equal(2.0, TrailChordPolicy.DefaultMinChordMm);
        Assert.Equal(0.75, TrailChordPolicy.DefaultSparseGapSeconds);
        Assert.Equal(2.5, TrailChordPolicy.DefaultSparseIntervalFactor);
    }

    /// <summary>The reason is read by an operator and quoted in a bug report: a German
    /// desktop must not print "2,5 s" where the checklist reads "2.5 s".</summary>
    [Fact]
    public void The_reason_is_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string reason = TrailChordPolicy.Evaluate(49.4, 2.5, 0.25).Reason!;
            Assert.Contains("2.5 s between observations", reason);
            Assert.Contains("0.25 s cadence", reason);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    // ---- break accounting ---------------------------------------------------------------

    /// <summary>Counting is per reason, and a drawing gap is never folded into a continuity
    /// break: that separation is the whole reason the counters exist.</summary>
    [Fact]
    public void Counters_count_each_reason_separately()
    {
        var counters = new TrailBreakCounters();
        Assert.Equal(0, counters.Total);
        Assert.Equal(0, counters.Get(TrailBreakReason.SparseSample));

        counters.Count(TrailBreakReason.SparseSample);
        counters.Count(TrailBreakReason.SparseSample);
        counters.Count(TrailBreakReason.ContinuityVerdict);

        Assert.Equal(2, counters.Get(TrailBreakReason.SparseSample));
        Assert.Equal(1, counters.Get(TrailBreakReason.ContinuityVerdict));
        Assert.Equal(0, counters.Get(TrailBreakReason.Pruning));
        Assert.Equal(3, counters.Total);
    }

    [Fact]
    public void Reset_clears_every_reason()
    {
        var counters = new TrailBreakCounters();
        counters.Count(TrailBreakReason.Pruning);
        counters.Count(TrailBreakReason.PoseIneligible);
        counters.Reset();
        Assert.Equal(0, counters.Total);
        Assert.Equal(new[] { "none" }, counters.Lines());
    }

    /// <summary>An empty block says "none" rather than printing nothing: a missing line
    /// reads as a missing measurement, not as a clean run.</summary>
    [Fact]
    public void Describe_on_an_empty_set_says_none()
    {
        Assert.Equal("none", new TrailBreakCounters().Describe());
        Assert.Equal(new[] { "none" }, new TrailBreakCounters().Lines());
    }

    /// <summary>Terms come out in enum order whatever order the breaks happened in, so two
    /// diagnostics blocks from two runs can be compared line by line. Reasons that never
    /// fired print nothing.</summary>
    [Fact]
    public void Describe_lists_only_non_zero_reasons_in_enum_order()
    {
        var counters = new TrailBreakCounters();
        counters.Count(TrailBreakReason.Pruning);
        counters.Count(TrailBreakReason.SparseSample);
        counters.Count(TrailBreakReason.ContinuityVerdict);
        counters.Count(TrailBreakReason.SparseSample);
        counters.Count(TrailBreakReason.FrameOrConfiguration);

        Assert.Equal(new[] { "continuity 1", "sparse sample 2", "frame or configuration 1", "pruning 1" },
                     counters.Lines());
        Assert.Equal("continuity 1, sparse sample 2, frame or configuration 1, pruning 1",
                     counters.Describe());
    }

    /// <summary>Every reason has a word for it, so a new enum member cannot silently vanish
    /// from the diagnostics block.</summary>
    [Fact]
    public void Every_reason_has_a_name_and_counts()
    {
        foreach (TrailBreakReason reason in System.Enum.GetValues(typeof(TrailBreakReason)))
        {
            var counters = new TrailBreakCounters();
            counters.Count(reason);
            Assert.Equal(1, counters.Get(reason));
            Assert.Equal(1, counters.Total);
            var lines = counters.Lines();
            Assert.Single(lines);
            Assert.NotEqual("none", lines[0]);
            Assert.EndsWith(" 1", lines[0]);
        }
    }

    /// <summary>Diagnostics must never be the thing that stops a print being drawn: an
    /// unrecognised reason is ignored, not thrown.</summary>
    [Fact]
    public void An_unrecognised_reason_is_ignored_rather_than_thrown()
    {
        var counters = new TrailBreakCounters();
        counters.Count((TrailBreakReason)999);
        Assert.Equal(0, counters.Total);
        Assert.Equal(0, counters.Get((TrailBreakReason)999));
    }

    /// <summary>The counts are printed into a diagnostics block an operator reads.</summary>
    [Fact]
    public void Describe_is_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var counters = new TrailBreakCounters();
            for (int i = 0; i < 1234; i++) counters.Count(TrailBreakReason.SparseSample);
            Assert.Equal("sparse sample 1234", counters.Describe());
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
