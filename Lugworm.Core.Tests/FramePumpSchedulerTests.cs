using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The live viewport's frame pump, and the bounded retention decision it uses.
/// Both were only reachable through Rhino before, which is why the 2026-09-09 review could
/// not find a regression covering the wake-up defect.</summary>
public class FramePumpSchedulerTests
{
    const int Frame = 33, StaleWatch = 500;

    /// <summary>A fake timer: records what it was actually programmed with, so "the field
    /// changed but the timer did not" is visible instead of invisible.</summary>
    sealed class FakeTimer
    {
        public readonly List<(int due, int period)> Programmed = new List<(int, int)>();
        public bool Running;
        public void Apply(PumpCommand c)
        {
            if (!c.Changed) return;
            Running = c.Running;
            if (c.Running) Programmed.Add((c.DueMs, c.PeriodMs));
        }
    }

    /// <summary>THE regression (plan V4 item 2). The machine rests, the pump settles onto the
    /// 500 ms stale watch, then a new sample arrives. The old Wake assigned the frame
    /// interval to its own field and reprogrammed the timer only when the pump was stopped,
    /// so a running stale-watch pump kept ticking at 2 Hz while claiming 30 fps.</summary>
    [Fact]
    public void Waking_a_settled_pump_reprograms_the_timer_immediately()
    {
        var timer = new FakeTimer();
        var pump = new FramePumpScheduler();
        timer.Apply(pump.Wake(Frame));
        timer.Apply(pump.Set(StaleWatch));
        Assert.Equal(StaleWatch, pump.IntervalMs);
        timer.Programmed.Clear();

        timer.Apply(pump.Wake(Frame));

        Assert.Equal(Frame, pump.IntervalMs);
        Assert.True(pump.Running);
        // due 0: the sample that woke the pump reaches the next callback, it does not wait
        // out the 500 ms period it replaced.
        Assert.Equal(new[] { (0, Frame) }, timer.Programmed);
    }

    /// <summary>The old code, reproduced, so the difference is a fact and not a claim.</summary>
    [Fact]
    public void The_old_wake_rule_would_have_left_the_stale_watch_period_in_place()
    {
        var timer = new FakeTimer();
        bool running = false;
        int intervalMs = Frame;
        void OldWake()
        {
            if (intervalMs == StaleWatch) intervalMs = Frame;
            if (!running) { running = true; timer.Programmed.Add((0, intervalMs)); }
        }
        void OldSet(int ms)
        {
            if (running && intervalMs == ms) return;
            intervalMs = ms; running = true; timer.Programmed.Add((ms, ms));
        }

        OldWake();
        OldSet(StaleWatch);
        timer.Programmed.Clear();
        OldWake();

        Assert.Equal(Frame, intervalMs);              // the field moved ...
        Assert.Empty(timer.Programmed);               // ... and the timer never did
    }

    [Fact]
    public void Waking_a_pump_already_at_the_target_rate_does_not_re_arm_it()
    {
        var timer = new FakeTimer();
        var pump = new FramePumpScheduler();
        timer.Apply(pump.Wake(Frame));
        timer.Programmed.Clear();

        var again = pump.Wake(Frame);

        Assert.False(again.Changed);
        Assert.Empty(timer.Programmed);
    }

    [Fact]
    public void A_degraded_target_re_arms_a_running_pump()
    {
        var pump = new FramePumpScheduler();
        pump.Wake(Frame);
        var degraded = pump.Wake(100);
        Assert.True(degraded.Changed);
        Assert.Equal(0, degraded.DueMs);
        Assert.Equal(100, degraded.PeriodMs);
    }

    [Fact]
    public void Set_arms_the_first_callback_a_whole_period_away()
    {
        var pump = new FramePumpScheduler();
        var command = pump.Set(Frame);
        Assert.True(command.Changed);
        Assert.Equal(Frame, command.DueMs);
        Assert.Equal(Frame, command.PeriodMs);
    }

    [Fact]
    public void Stopping_twice_only_changes_the_timer_once()
    {
        var pump = new FramePumpScheduler();
        pump.Wake(Frame);
        Assert.True(pump.Stop().Changed);
        Assert.False(pump.Stop().Changed);
        Assert.False(pump.Running);
    }

    [Fact]
    public void A_stopped_pump_starts_again_on_the_next_wake()
    {
        var pump = new FramePumpScheduler();
        pump.Wake(Frame);
        pump.Stop();
        var command = pump.Wake(Frame);
        Assert.True(command.Changed);
        Assert.True(command.Running);
        Assert.Equal(0, command.DueMs);
    }

    // ---- bounded retention -------------------------------------------------------------

    /// <summary>Compacting an older run must keep its shape within the stated tolerance and
    /// must keep its ends, so an early layer survives a long print as geometry with a known
    /// error instead of being discarded as the oldest points.</summary>
    [Fact]
    public void Compaction_keeps_the_ends_and_stays_inside_the_tolerance()
    {
        var line = new List<Vec3>();
        for (int i = 0; i <= 100; i++) line.Add(new Vec3(i, 0, 0));
        line[50] = new Vec3(50, 0.05, 0);   // well inside the tolerance

        var kept = Rdp.SimplifyIndices(line, 0.2);

        Assert.Equal(0, kept[0]);
        Assert.Equal(line.Count - 1, kept[kept.Count - 1]);
        Assert.True(kept.Count < line.Count);
        Assert.DoesNotContain(50, kept);
    }

    /// <summary>A corner is never cut: the indices come back in order and the feature that
    /// exceeds the tolerance is retained.</summary>
    [Fact]
    public void Compaction_retains_a_corner_and_returns_ordered_indices()
    {
        var corner = new List<Vec3>
        {
            new Vec3(0, 0, 0), new Vec3(5, 0, 0), new Vec3(10, 0, 0),
            new Vec3(10, 5, 0), new Vec3(10, 10, 0),
        };

        var kept = Rdp.SimplifyIndices(corner, 0.2);

        Assert.Contains(2, kept);
        for (int i = 1; i < kept.Count; i++) Assert.True(kept[i] > kept[i - 1]);
    }

    [Fact]
    public void Compaction_of_a_short_run_or_a_zero_tolerance_keeps_everything()
    {
        var two = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0) };
        Assert.Equal(new[] { 0, 1 }, Rdp.SimplifyIndices(two, 5.0));
        var five = new List<Vec3>
        {
            new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(2, 0, 0), new Vec3(3, 0, 0), new Vec3(4, 0, 0),
        };
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Rdp.SimplifyIndices(five, 0));
        Assert.Empty(Rdp.SimplifyIndices(new List<Vec3>(), 1.0));
    }
}
