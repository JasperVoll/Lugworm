using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Doe;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Hand-derived fixtures from the plan: A, B, C trade one objective against
/// the other; B has the greatest D. No third-party generator was used.</summary>
public class ParetoSelectionTests
{
    static ParetoSelectionState ConfirmationFixture()
    {
        var point = new ParetoPoint { X = new[] { 20.0 }, D = new[] { 0.6, 0.6 }, DOverall = 0.6,
            Predicted = new[] { 7.8, 2.1 }, PiHalf = new[] { 0.4, 0.9 },
            PiLo = new[] { 7.5, 1.5 }, PiHi = new[] { 8.3, 3.3 }, Leverage = 0.4 };
        return ParetoSelection.Select(ParetoSelectionState.Empty, Front(point.ToBranch()), 0);
    }

    [Fact]
    public void Confirm_ReportsInsideOrOutsidePerResponse()
    {
        var s = ParetoSelection.Confirm(ConfirmationFixture(), new[] { 8.25, 1.3 });
        string report = ParetoSelection.ConfirmationReport(s);
        Assert.Contains("inside PI; measured 8.25, predicted 7.8, interval [7.5, 8.3]", report);
        Assert.Contains("outside PI; measured 1.3, predicted 2.1, interval [1.5, 3.3]", report);
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(report, ParetoSelection.ConfirmationReport(s)); }
        finally { CultureInfo.CurrentCulture = previous; }
        var reset = ParetoSelection.Reset(s); Assert.Null(reset.ConfirmValues);
    }

    [Fact]
    public void Confirm_SurvivesArchiveRoundTrip()
    {
        var s = ParetoSelection.Confirm(ConfirmationFixture(), new[] { 8.25, 1.3 });
        Assert.True(ParetoArchive.TryRead(ParetoArchive.Write(s, null, null), out var copy, out _, out _, out var warning));
        Assert.Null(warning); Assert.Equal(s.ConfirmValues, copy.ConfirmValues);
        Assert.Equal(ParetoSelection.ConfirmationReport(s), ParetoSelection.ConfirmationReport(copy));
    }

    [Fact]
    public void Confirm_MissingIntervalIsReportedNotGuessed()
    {
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, Front(B), 0);
        s = ParetoSelection.Confirm(s, new[] { 7.8, 2.1 });
        Assert.Contains("PI unavailable, not checked", ParetoSelection.ConfirmationReport(s));
        Assert.DoesNotContain("inside PI", ParetoSelection.ConfirmationReport(s));
        Assert.Throws<ArgumentException>(() => ParetoSelection.Confirm(s, new[] { 1.0 }));
    }

    [Fact]
    public void Snapshot_ReadsReliabilityColumns_AndOldArchivesStillLoad()
    {
        var point = new ParetoPoint { X = new[] { 20.0 }, D = new[] { 0.6, 0.6 }, DOverall = 0.6,
            Predicted = new[] { 7.8, 2.1 }, PiHalf = new[] { 0.4, 0.9 },
            PiLo = new[] { 7.4, 1.2 }, PiHi = new[] { 8.2, 3.0 }, Leverage = 0.8, ExtrapolationFlag = true };
        var front = Front(point.ToBranch()); var recipe = front.Rows[0];
        Assert.Equal(0.4, recipe.Reliability!.PiHalf[0]); Assert.True(recipe.Reliability.ExtrapolationFlag);
        var state = ParetoSelection.Select(ParetoSelectionState.Empty, front, 0);
        var archive = ParetoArchive.Write(state, front, null);
        Assert.True(ParetoArchive.TryRead(archive, out var read, out var displayed, out _, out var warning));
        Assert.Null(warning); Assert.Equal(front.Hash, displayed!.Hash);
        Assert.Equal(recipe.ReliabilityStatus, read.Recipe!.ReliabilityStatus);
        var old = "{\"version\":1,\"front\":{\"factorNames\":[\"feed\"],\"objectiveNames\":[\"a\",\"b\"],\"rows\":[[20,0.6,0.6,0.6]]}}";
        Assert.True(ParetoArchive.TryRead(old, out _, out var oldFront, out _, out warning));
        Assert.Null(warning); Assert.Null(oldFront!.Rows[0].Reliability);
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); Assert.Equal(archive, ParetoArchive.Write(state, front, null)); Assert.Contains("7.8 ± 0.4", recipe.ReliabilityStatus); }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    static readonly string[] Factors = { "feed" };
    static readonly string[] Objectives = { "width desirability", "speed desirability" };
    static readonly double S18 = Math.Sqrt(0.18);

    static IReadOnlyList<double> A => new[] { 10.0, 0.2, 0.9, S18 };
    static IReadOnlyList<double> B => new[] { 20.0, 0.6, 0.6, 0.6 };
    static IReadOnlyList<double> C => new[] { 30.0, 0.9, 0.2, S18 };

    static ParetoFrontSnapshot Front(params IReadOnlyList<double>[] rows)
    {
        Assert.True(ParetoFrontSnapshot.TryParse(rows, Factors, Objectives, out var f, out string? err), err);
        return f!;
    }

    [Fact]
    public void Parser_rejects_malformed_trees_without_dropping_branches()
    {
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A, new[] { 20.0, 0.6, 0.6 } }, Factors, Objectives, out _, out string? e1));
        Assert.Contains("branch 1", e1);
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A, new[] { double.NaN, 0.6, 0.6, 0.6 } }, Factors, Objectives, out _, out string? e2));
        Assert.Contains("not finite", e2);
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { new[] { 20.0, 1.2, 0.6, 0.6 } }, Factors, Objectives, out _, out string? e3));
        Assert.Contains("outside [0, 1]", e3);
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A }, Factors, new[] { "a", "a" }, out _, out string? e4));
        Assert.Contains("not unique", e4);
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A }, Factors, Array.Empty<string>(), out _, out string? e5));
        Assert.Contains("no active objective", e5);
        Assert.False(ParetoFrontSnapshot.TryParse(Array.Empty<IReadOnlyList<double>>(), Factors, Objectives, out _, out _));
        // a count/order mismatch of the names against the arity is a malformed front, not a guess
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A }, new[] { "feed", "speed" }, Objectives, out _, out string? e6));
        Assert.Contains("expected 5", e6);
    }

    [Fact]
    public void Identity_ignores_branch_order_and_negative_zero_but_notices_D_and_names()
    {
        var abc = Front(A, B, C);
        var cab = Front(C, A, B);
        Assert.Equal(abc.Hash, cab.Hash);
        Assert.Equal(16, abc.Hash.Length);
        var negZero = Front(new[] { -0.0, 0.2, 0.9, S18 });
        var posZero = Front(new[] { 0.0, 0.2, 0.9, S18 });
        Assert.Equal(negZero.Hash, posZero.Hash);
        // D-only change (different weights) changes identity although x and d did not
        var reweighted = Front(A, new[] { 20.0, 0.6, 0.6, 0.55 }, C);
        Assert.NotEqual(abc.Hash, reweighted.Hash);
        Assert.NotEqual(abc.Hash, Front(A, B, C, B).Hash);   // multiset, not set
        Assert.False(ParetoFrontSnapshot.TryParse(new[] { A, B, C }, new[] { "speed" }, Objectives, out var renamed, out _) && renamed!.Hash == abc.Hash);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(abc.Hash, Front(A, B, C).Hash);
            Assert.Equal(abc.Rows[1].Key, Front(A, B, C).Rows[1].Key);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Select_serialize_reopen_without_front_emits_exactly_the_stored_recipe()
    {
        var front = Front(A, B, C);
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, front, 1);
        Assert.True(s.Current);
        Assert.Equal(new[] { 20.0 }, s.Recipe!.X);
        Assert.Equal(new[] { 0.6, 0.6 }, s.Recipe.Di);
        Assert.Equal(0.6, s.Recipe.D);

        string json = ParetoArchive.Write(s, front, new[] { 20.0 });
        Assert.True(ParetoArchive.TryRead(json, out var restored, out var displayed, out var optimum, out string? warning));
        Assert.Null(warning);
        Assert.NotNull(displayed);
        Assert.Equal(front.Hash, displayed!.Hash);
        Assert.Equal(new[] { 20.0 }, optimum);
        // first solve with no front: cached recipe, explicit status, current = false
        var observed = ParetoSelection.Observe(restored, null, null, null, out _);
        Assert.False(observed.Current);
        Assert.Equal(new[] { 20.0 }, observed.Recipe!.X);
        Assert.Equal(new[] { 0.6, 0.6 }, observed.Recipe.Di);
        Assert.Equal(0.6, observed.Recipe.D);
        Assert.Contains("current front unavailable", observed.Status);
    }

    [Fact]
    public void Reordering_preserves_the_selection_and_a_replaced_point_makes_it_stale()
    {
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, Front(A, B, C), 1);
        var reordered = ParetoSelection.Observe(s, Front(C, A, B), null, null, out _);
        Assert.True(reordered.Current);
        Assert.Contains("row 2", reordered.Status);

        var replaced = Front(A, new[] { 21.0, 0.61, 0.59, Math.Sqrt(0.3599) }, C);
        var stale = ParetoSelection.Observe(s, replaced, null, null, out _);
        Assert.False(stale.Current);
        Assert.Equal(new[] { 20.0 }, stale.Recipe!.X);          // stored numbers untouched
        Assert.Equal(0.6, stale.Recipe.D);
        Assert.Contains("no longer on it", stale.Status);
        Assert.Equal(-1, stale.CandidateIndex);
        // explicit reselection on the new front is what accepts it
        var accepted = ParetoSelection.Select(stale, replaced, 1);
        Assert.True(accepted.Current);
        Assert.Equal(new[] { 21.0 }, accepted.Recipe!.X);
    }

    [Fact]
    public void Perturbation_within_tolerance_reports_a_candidate_but_still_needs_acceptance()
    {
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, Front(A, B, C), 1);
        var perturbed = Front(A, new[] { 20.0 + 1e-12, 0.6, 0.6, 0.6 + 1e-12 }, C);
        var observed = ParetoSelection.Observe(s, perturbed, null, null, out _);
        Assert.False(observed.Current);
        Assert.Equal(1, observed.CandidateIndex);
        Assert.Contains("still matches row 1", observed.Status);
        Assert.Equal(20.0, observed.Recipe!.X[0]);   // saved numbers remain untouched
        Assert.True(ParetoSelection.Near(20.0, 20.0 + 1e-12));
        Assert.False(ParetoSelection.Near(20.0, 20.0 + 1e-6));
        // and a duplicate of the stored recipe is reported ambiguous, never silently picked
        var duplicated = Front(A, B, B, C);
        var amb = ParetoSelection.Observe(s, duplicated, null, null, out _);
        Assert.False(amb.Current);
        Assert.Contains("ambiguous", amb.Status);
    }

    [Fact]
    public void Headless_key_wins_over_index_and_never_falls_back()
    {
        var front = Front(A, B, C);
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, front, 1);
        string key = s.Recipe!.Key;
        Assert.StartsWith("iamps1:" + front.Hash + ":", key);

        var byKey = ParetoSelection.Observe(ParetoSelectionState.Empty, front, key, 0, out string? remark);
        Assert.True(byKey.Current);
        Assert.Equal(new[] { 20.0 }, byKey.Recipe!.X);
        Assert.Contains("selectionIndex ignored", remark);

        // a key from another front: refused, and NOT replaced by the index
        var other = Front(A, C);
        var refused = ParetoSelection.Observe(s, other, key, 0, out _);
        Assert.False(refused.Current);
        Assert.Contains("different front", refused.Status);
        Assert.Equal(new[] { 20.0 }, refused.Recipe!.X);
        // garbage key: no recipe chosen
        var garbage = ParetoSelection.Observe(ParetoSelectionState.Empty, front, "not-a-key", null, out _);
        Assert.Null(garbage.Recipe);
        Assert.Contains("not a key", garbage.Status);
        // a key without a live front cannot be applied
        var noFront = ParetoSelection.Observe(ParetoSelectionState.Empty, null, key, null, out _);
        Assert.Null(noFront.Recipe);
        Assert.Contains("none is wired", noFront.Status);
    }

    [Fact]
    public void Unchanged_index_is_not_a_standing_order_on_a_new_front()
    {
        var front = Front(A, B, C);
        var s = ParetoSelection.Observe(ParetoSelectionState.Empty, front, null, 2, out _);
        Assert.True(s.Current);
        Assert.Equal(new[] { 30.0 }, s.Recipe!.X);
        // same index again on the same front: nothing to redo
        var again = ParetoSelection.Observe(s, front, null, 2, out _);
        Assert.True(again.Current);
        Assert.Equal(new[] { 30.0 }, again.Recipe!.X);
        // the front changes: the retained recipe is flagged, the ordinal is NOT re-applied
        var changed = Front(C, B, A, new[] { 40.0, 0.95, 0.1, Math.Sqrt(0.095) });
        var flagged = ParetoSelection.Observe(s, changed, null, 2, out _);
        Assert.False(flagged.Current);
        Assert.Equal(new[] { 30.0 }, flagged.Recipe!.X);   // still C, not the new row 2 (A)
        Assert.Contains("front changed", flagged.Status);
        Assert.Equal(0, flagged.CandidateIndex);
        // an explicit new request on the new front is honoured
        var fresh = ParetoSelection.Observe(flagged, changed, null, 3, out _);
        Assert.True(fresh.Current);
        Assert.Equal(new[] { 40.0 }, fresh.Recipe!.X);
        // out of range: the request is refused with its reason; the kept recipe is still
        // the one on the live front, so it stays current - no fallback to another point
        var oor = ParetoSelection.Observe(fresh, changed, null, 9, out _);
        Assert.True(oor.Current);
        Assert.Equal(new[] { 40.0 }, oor.Recipe!.X);
        Assert.Contains("out of range", oor.Status);
        // the same stale ordinal again on this front is still not applied
        var stale = ParetoSelection.Observe(fresh, changed, null, 3, out _);
        Assert.True(stale.Current);
        Assert.Equal(new[] { 40.0 }, stale.Recipe!.X);
    }

    [Fact]
    public void Changed_names_and_empty_front_keep_the_recipe_with_a_truthful_status()
    {
        var s = ParetoSelection.Select(ParetoSelectionState.Empty, Front(A, B, C), 1);
        Assert.True(ParetoFrontSnapshot.TryParse(new[] { A, B, C }, new[] { "speed" }, Objectives, out var renamed, out _));
        var st = ParetoSelection.Observe(s, renamed, null, null, out _);
        Assert.False(st.Current);
        Assert.Contains("names changed", st.Status);
        Assert.Equal(new[] { 20.0 }, st.Recipe!.X);
        var none = ParetoSelection.Observe(s, null, null, null, out _);
        Assert.False(none.Current);
        Assert.Equal(0.6, none.Recipe!.D);
        var reset = ParetoSelection.Reset(none);
        Assert.Null(reset.Recipe);
        Assert.Equal("no selection", reset.Status);
    }

    [Fact]
    public void Optimum_marker_and_best_D_are_distinct_facts()
    {
        var front = Front(A, B, C);
        Assert.Equal(1, front.IndexOfSettings(new[] { 20.0 }));
        Assert.Equal(-1, front.IndexOfSettings(new[] { 25.0 }));   // off-front recommendation (factor priors)
        Assert.Equal(-1, front.IndexOfSettings(new[] { 20.0, 1.0 }));
        Assert.Equal(-2, Front(A, B, B).IndexOfSettings(new[] { 20.0 }));
        Assert.Equal(1, front.IndexOfBestD());
        // k > 2: zero-D extremes stay selectable and every objective is a row
        var objectives3 = new[] { "a", "b", "c" };
        var rows = new IReadOnlyList<double>[]
        {
            new[] { 1.0, 1, 0, 0, 0 }, new[] { 2.0, 0, 1, 0, 0 }, new[] { 3.0, 0, 0, 1, 0 }, new[] { 4.0, 0.5, 0.5, 0.5, 0.5 },
        };
        Assert.True(ParetoFrontSnapshot.TryParse(rows, Factors, objectives3, out var f3, out string? err), err);
        var picked = ParetoSelection.Select(ParetoSelectionState.Empty, f3!, 0);
        Assert.True(picked.Current);
        Assert.Equal(0.0, picked.Recipe!.D);
        Assert.Equal(3, f3!.IndexOfBestD());
        Assert.Equal(3, f3.ObjectiveNames.Count);
    }

    [Fact]
    public void Weighted_best_is_the_aggregation_applied_after_the_front()
    {
        // A = (0.2, 0.9), B = (0.6, 0.6), C = (0.9, 0.2). Equal weights: geometric means
        // sqrt(0.18) = 0.424, 0.6, 0.424 -> B. Weights (1, 4): A scores
        // exp((ln 0.2 + 4 ln 0.9) / 5) = 0.666, B 0.6, C exp((ln 0.9 + 4 ln 0.2) / 5) = 0.270
        // -> A; (4, 1) mirrors to C. The marker moves across the front while the front
        // itself is untouched - this is the weighting step AFTER the non-dominated set.
        var front = Front(A, B, C);
        Assert.Equal(1, front.IndexOfWeightedBest(new[] { 1.0, 1.0 }, out double sB, out string? p1));
        Assert.Null(p1);
        Assert.Equal(0.6, sB, 9);
        Assert.Equal(0, front.IndexOfWeightedBest(new[] { 1.0, 4.0 }, out double sA, out _));
        Assert.Equal(Math.Exp((Math.Log(0.2) + 4 * Math.Log(0.9)) / 5), sA, 9);
        Assert.Equal(2, front.IndexOfWeightedBest(new[] { 4.0, 1.0 }, out _, out _));
        // a zero weight drops that axis: (1, 0) ranks by d0 alone -> C
        Assert.Equal(2, front.IndexOfWeightedBest(new[] { 1.0, 0.0 }, out double sC, out _));
        Assert.Equal(0.9, sC, 9);
        Assert.Equal(front.Hash, Front(A, B, C).Hash);   // nothing about the front changed
        // refused with a reason, never guessed: wrong count, negative, all zero, nothing wired
        Assert.Equal(-1, front.IndexOfWeightedBest(new[] { 1.0 }, out _, out string? e1));
        Assert.Contains("2 objectives", e1);
        Assert.Equal(-1, front.IndexOfWeightedBest(new[] { 1.0, -1.0 }, out _, out string? e2));
        Assert.Contains("from 0 up", e2);
        Assert.Equal(-1, front.IndexOfWeightedBest(new[] { 0.0, 0.0 }, out _, out string? e3));
        Assert.Contains("every weight is 0", e3);
        Assert.Equal(-1, front.IndexOfWeightedBest(null, out _, out _));
        // a zero d on a weighted axis annihilates the score exactly like the optimizer's D,
        // so a front of such recipes has no weighted best and says so; dropping the axis
        // with a zero weight brings the extreme back
        var zeros = Front(new[] { 1.0, 1.0, 0.0, 0.0 }, new[] { 2.0, 0.0, 1.0, 0.0 });
        Assert.Equal(-1, zeros.IndexOfWeightedBest(new[] { 1.0, 1.0 }, out _, out string? e4));
        Assert.Contains("scores 0", e4);
        Assert.Equal(0, zeros.IndexOfWeightedBest(new[] { 1.0, 0.0 }, out _, out _));
        Assert.Equal(1, zeros.IndexOfWeightedBest(new[] { 0.0, 1.0 }, out _, out _));
    }

    [Fact]
    public void Archive_rejects_garbage_and_future_versions_and_keeps_what_it_can()
    {
        Assert.False(ParetoArchive.TryRead("", out _, out _, out _, out _));
        Assert.False(ParetoArchive.TryRead("{ nope", out _, out _, out _, out string? w1));
        Assert.Contains("not readable", w1);
        Assert.False(ParetoArchive.TryRead("{\"version\":9}", out _, out _, out _, out string? w2));
        Assert.Contains("not supported", w2);
        // a malformed stored recipe is dropped with a warning; the archive still reads
        string partial = "{\"version\":1,\"status\":\"x\",\"consumed\":\"\",\"recipe\":{\"factorNames\":[\"feed\"],\"objectiveNames\":[\"a\"],\"x\":[1.0],\"di\":[0.5,0.5],\"D\":0.5},\"front\":null,\"optimum\":null}";
        Assert.True(ParetoArchive.TryRead(partial, out var st, out var front, out var opt, out string? w3));
        Assert.Null(st.Recipe);
        Assert.Null(front);
        Assert.Null(opt);
        Assert.Contains("malformed", w3);
        // an oversized payload is refused before parsing
        Assert.False(ParetoArchive.TryRead(new string('x', ParetoArchive.MaxBytes + 1), out _, out _, out _, out string? w4));
        Assert.Contains("MiB", w4);
    }
}
