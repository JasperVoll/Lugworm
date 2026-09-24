using System.Globalization;
using System.Threading;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The measurement block the 2026-09-09 review asked for: every rate reported as
/// the separate thing it is, and a clamped request said out loud instead of printed as if it
/// had been honoured.</summary>
public class LiveDisplayDetailTests
{
    static LiveDisplayDetail Sample() => new LiveDisplayDetail
    {
        RequestedPollIntervalMs = 50,
        EffectivePollTargetMs = 250,
        ClampNote = "requested 50 ms clamped to 250 ms (the session's supported range is 250-2000 ms)",
        CoordinateAcquisitions = 538,
        DuplicateCallbacks = 12,
        CoordinateIntervalMsP50 = 250.035,
        CoordinateIntervalMsP95 = 259.359,
        CoordinateRoundTripMsP95 = 19.885,
        FullPollElapsedMsP95 = 84.2,
        PublicationAgeMsP95 = 96.4,
        OverlayIntervalMsP95 = 104.0,
        ParsesInWindow = 1,
        CacheHitsInWindow = 940,
        RedrawsInWindow = 220,
        FrameCallbacksInWindow = 900,
        ObservationsHeld = 65,
        RejectedByEnvelope = 65,
        EnvelopeKnown = true,
        BreakReasons = "continuity 2, sparse sample 4",
        RetentionNote = "1200 of 5000 point budget, nothing compacted or discarded",
    };

    /// <summary>A 14 ms coordinate round trip is not a 14 ms poll, and 250 ms of measured
    /// cadence is not the 50 ms that was asked for. All four numbers have to be readable
    /// side by side or the row cannot be argued from evidence.</summary>
    [Fact]
    public void Every_rate_is_reported_as_the_separate_thing_it_is()
    {
        string text = string.Join("\n", Sample().Lines());
        Assert.Contains("requested 50 ms, effective target 250 ms", text);
        Assert.Contains("interval p50/p95 250/259 ms", text);
        Assert.Contains("coordinate round trip p95 20 ms", text);
        Assert.Contains("full poll p95 84 ms", text);
        Assert.Contains("publication age p95 96 ms", text);
    }

    [Fact]
    public void A_clamped_request_is_named_rather_than_silently_honoured()
    {
        Assert.Contains("clamped to 250 ms", string.Join("\n", Sample().Lines()));
        var unclamped = Sample();
        unclamped.ClampNote = null;
        Assert.DoesNotContain("clamped", string.Join("\n", unclamped.Lines()));
    }

    /// <summary>Repeated callbacks are counted, not averaged away: the glitch filter releases
    /// a held sample together with its successor, and that pair is one acquisition.</summary>
    [Fact]
    public void Acquisitions_and_repeated_callbacks_are_counted_apart()
    {
        Assert.Contains("acquisitions 538 (12 repeated callbacks)", string.Join("\n", Sample().Lines()));
    }

    [Fact]
    public void Counters_are_window_deltas_and_envelope_ignorance_is_stated()
    {
        string text = string.Join("\n", Sample().Lines());
        Assert.Contains("window: parses 1, cache hits 940, redraws 220/900 callbacks", text);
        Assert.Contains("65 outside the reported envelope, envelope known", text);
        var unknown = Sample();
        unknown.EnvelopeKnown = false;
        Assert.Contains("envelope UNKNOWN - not checked", string.Join("\n", unknown.Lines()));
    }

    [Fact]
    public void An_empty_detail_block_describes_without_throwing()
    {
        string text = string.Join("\n", new LiveDisplayDetail().Lines());
        Assert.Contains("breaks: none", text);
        Assert.Contains("envelope UNKNOWN", text);
    }

    /// <summary>The detail block is appended to the original lines, never woven into them: an
    /// existing reader of this text keeps working.</summary>
    [Fact]
    public void The_detail_block_extends_the_original_stats_text()
    {
        var stats = new LiveRenderStats(pollRequests: 120, lastRoundTripMs: 41.4, meanRoundTripMs: 38.9,
                                        actualPollIntervalMs: 306.2, uiCallbackWaitMsP95: 7.2,
                                        frameBuildMsP50: 1.26, frameBuildMsP95: 4.78, redraws: 4,
                                        frameCallbacks: 4, ghSolves: 0, parseCount: 1, parseCacheHits: 9,
                                        trailPoints: 812, trailSegments: 3, droppedSamples: 2,
                                        suspectSamples: 7, frameIntervalMs: 33, detail: Sample());
        var lines = stats.Lines();
        Assert.Equal(9, lines.Count);
        Assert.StartsWith("polls: 120", lines[0]);
        Assert.StartsWith("cadence: requested 50 ms", lines[4]);
        Assert.Null(LiveRenderStats.Empty.Detail);
        Assert.Equal(4, LiveRenderStats.Empty.Lines().Count);
    }

    /// <summary>A comma decimal separator in the operator's locale must not reach a
    /// diagnostics line another tool parses.</summary>
    [Fact]
    public void The_block_is_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("interval p50/p95 250/259 ms", string.Join("\n", Sample().Lines()));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }
}
