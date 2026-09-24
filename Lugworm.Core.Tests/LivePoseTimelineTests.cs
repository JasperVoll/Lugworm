using System;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class LivePoseTimelineTests
{
    static LivePoseSample Sample(long sequence, double time, double x, long epoch = 1, bool valid = true)
        => new LivePoseSample(sequence, epoch, time, time, new Vec3(x, 0, 0), valid);

    [Fact]
    public void Delayed_midpoint_does_not_modify_observation_or_extrapolate()
    {
        var t = new LivePoseTimeline();
        t.Add(Sample(1, 0, 0)); t.Add(Sample(2, 100, 10));
        var frame = t.Evaluate(150, true, 100);
        Assert.Equal(5, frame.Position.X, 9);
        Assert.True(frame.Smoothed);
        Assert.Equal(10, t.Latest!.Value.Position.X);
        Assert.Equal(10, t.Evaluate(250, true, 100).Position.X);
    }

    [Fact]
    public void Invalid_epoch_gap_and_stale_samples_never_bridge()
    {
        // Stale after max(3000, 2 * p95) ms capped at 10000 since 2026-09-09: one late poll
        // must not turn the pose stale for a frame (the operator saw the arm flash at
        // telemetry cadence). 3000 ms is the floor that does the work at this cadence.
        var t = new LivePoseTimeline();
        t.Add(Sample(1, 0, 0)); t.Add(Sample(2, 100, 10));
        Assert.False(t.Evaluate(1200).Stale);
        Assert.True(t.Evaluate(4200).Stale);
        t.Add(Sample(3, 4250, 100));
        Assert.False(t.Evaluate(4250, true, 100).Smoothed);   // a 4150 ms gap is never bridged
        t.Add(Sample(4, 4300, 200, valid: false));
        Assert.False(t.Evaluate(4300).Visible);
        t.Add(Sample(5, 4400, 300, 2));
        Assert.Equal(1, t.Count);
        Assert.Equal(300, t.Evaluate(4400).Position.X);
    }

    [Fact]
    public void History_is_bounded_and_rejects_backwards_duplicate_nonfinite_data()
    {
        var t = new LivePoseTimeline();
        for (int i = 1; i <= 100; i++) Assert.True(t.Add(Sample(i, i * 10, 7)));
        Assert.Equal(64, t.Count);
        Assert.False(t.Add(Sample(100, 1001, 8)));
        Assert.False(t.Add(Sample(101, 999, 8)));
        Assert.False(t.Add(Sample(101, 1001, double.NaN)));
        Assert.False(t.Evaluate(1050).Stale);
    }

    [Fact]
    public void Invalidate_hides_the_pose_without_forgetting_the_latest_or_the_epoch()
    {
        // The approach defect (2026-09-09, run 20260909-131323-d30110): a below-envelope
        // observation must stop the display posing the arm AND stop interpolation running
        // through it, without pretending the connection restarted. Clear() is the tool for
        // a disconnect; this is the tool for "the newest observation may not be posed".
        var t = new LivePoseTimeline();
        t.Add(Sample(1, 0, 0));
        t.Add(Sample(2, 100, 10));
        Assert.True(t.Evaluate(150, true, 100).Smoothed);

        t.Invalidate("Z -389.314 mm is 389.314 mm below the machine minimum 0 mm");
        Assert.True(t.Invalidated);
        Assert.Equal("Z -389.314 mm is 389.314 mm below the machine minimum 0 mm", t.InvalidReason);
        Assert.False(t.Evaluate(150, true, 100).Visible);
        Assert.Equal(0, t.Count);
        // Latest and the epoch survive: the caller still knows what it last observed and a
        // resumed sample of the same epoch is not treated as a new connection.
        Assert.Equal(10, t.Latest!.Value.Position.X);
        Assert.Equal(1, t.Epoch);
    }

    [Fact]
    public void A_sample_after_Invalidate_resumes_unsmoothed_and_never_bridges_the_gap()
    {
        var t = new LivePoseTimeline();
        t.Add(Sample(1, 0, 0));
        t.Add(Sample(2, 100, 10));
        t.Invalidate("below the machine envelope");

        Assert.True(t.Add(Sample(3, 200, 500)));   // fresh valid observation, same epoch
        Assert.False(t.Invalidated);
        Assert.Null(t.InvalidReason);
        var first = t.Evaluate(250, true, 100);
        Assert.True(first.Visible);
        Assert.False(first.Smoothed);              // one fresh sample: held, never blended with 10
        Assert.Equal(500, first.Position.X);
        Assert.Equal(1, t.Count);
        Assert.Equal(1, t.Epoch);                  // still the same epoch, not a reconnect

        t.Add(Sample(4, 300, 600));
        var second = t.Evaluate(350, true, 100);
        Assert.True(second.Smoothed);              // two fresh samples: interpolation resumes
        Assert.Equal(550, second.Position.X, 9);
    }

    [Fact]
    public void Invalidate_and_Clear_are_different_tools()
    {
        var t = new LivePoseTimeline();
        t.Add(Sample(1, 0, 0));
        t.Invalidate();
        Assert.True(t.Invalidated);
        Assert.Null(t.InvalidReason);              // a reason is optional
        Assert.NotNull(t.Latest);

        t.Clear();
        Assert.False(t.Invalidated);
        Assert.Null(t.Latest);
        Assert.Equal(long.MinValue, t.Epoch);
    }

    [Fact]
    public void Display_policy_is_one_pending_callback_and_no_catchup()
    {
        var p = new LiveDisplayPolicy();
        Assert.True(p.Request());
        for (int i = 0; i < 100; i++) Assert.False(p.Request());
        Assert.True(p.Consume());
        Assert.False(p.Consume());
        p.Dispose();
        Assert.False(p.Request());
    }
}
