using System.Globalization;
using System.Threading;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The live display's measured counters and the one drawing rule that is not
/// about continuity: whether a straight line between two observations may be drawn at
/// all. Both exist so the Rhino profiling row reads numbers instead of impressions.</summary>
public class LiveRenderStatsTests
{
    static LiveRenderStats Sample(long redraws = 4, long callbacks = 4, int frameMs = 33)
        => new LiveRenderStats(pollRequests: 120, lastRoundTripMs: 41.4, meanRoundTripMs: 38.9,
                               actualPollIntervalMs: 306.2, uiCallbackWaitMsP95: 7.2,
                               frameBuildMsP50: 1.26, frameBuildMsP95: 4.78, redraws: redraws,
                               frameCallbacks: callbacks, ghSolves: 0, parseCount: 1,
                               parseCacheHits: 9, trailPoints: 812, trailSegments: 3,
                               droppedSamples: 2, suspectSamples: 7, frameIntervalMs: frameMs);

    [Fact]
    public void Fps_follows_the_frame_budget_and_a_stopped_pump_is_zero()
    {
        Assert.Equal(30.0, LiveRenderStats.Fps(33), 0);
        Assert.Equal(15.0, LiveRenderStats.Fps(66), 0);
        Assert.Equal(0.0, LiveRenderStats.Fps(0));
        Assert.Equal(0.0, LiveRenderStats.Fps(-5));
    }

    /// <summary>A mean over nothing is 0, not NaN: this is printed into a diagnostics
    /// block an operator reads, and "NaN" there reads as a fault that is not there.</summary>
    [Fact]
    public void Mean_of_an_empty_sample_is_zero()
    {
        Assert.Equal(0.0, LiveRenderStats.Mean(0, 0));
        Assert.Equal(0.0, LiveRenderStats.Mean(-1, 100));
        Assert.Equal(0.0, LiveRenderStats.Mean(4, double.NaN));
        Assert.Equal(25.0, LiveRenderStats.Mean(4, 100));
    }

    /// <summary>The plan's redraw budget as a fact: at most one Rhino redraw per frame
    /// callback. Two publications in one tick used to redraw twice.</summary>
    [Fact]
    public void The_redraw_budget_is_one_per_frame_callback()
    {
        Assert.True(Sample(redraws: 4, callbacks: 4).WithinRedrawBudget);
        Assert.True(Sample(redraws: 1, callbacks: 4).WithinRedrawBudget);
        Assert.False(Sample(redraws: 5, callbacks: 4).WithinRedrawBudget);
    }

    [Fact]
    public void Describe_reports_every_counter_the_plan_asks_for()
    {
        string text = Sample().Describe();
        Assert.Contains("polls: 120", text);
        Assert.Contains("round trip last/mean 41/39 ms", text);
        Assert.Contains("actual interval 306 ms", text);
        Assert.Contains("budget 30 fps", text);
        Assert.Contains("build p50/p95 1.3/4.8 ms", text);
        Assert.Contains("ui callback wait p95 7 ms", text);
        Assert.Contains("redraws 4/4", text);
        Assert.Contains("solves: 0 telemetry-triggered", text);
        Assert.Contains("path parses 1", text);
        Assert.Contains("cache hits 9", text);
        Assert.Contains("812 points in 3 segment(s)", text);
        Assert.Contains("7 suspect (kept raw)", text);
        Assert.Contains("2 without a position", text);
        Assert.Equal(4, Sample().Lines().Count);
    }

    /// <summary>The text is a diagnostics block, not a locale-dependent one: a German
    /// document must not print "1,3" where a test and a bug report read "1.3".</summary>
    [Fact]
    public void Describe_is_culture_invariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("build p50/p95 1.3/4.8 ms", Sample().Describe());
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void Empty_stats_describe_without_throwing()
    {
        string text = LiveRenderStats.Empty.Describe();
        Assert.Contains("polls: 0", text);
        Assert.Contains("budget 0 fps", text);
    }
}
