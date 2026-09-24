using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the expire throttle of the Print Console: an unchanged output
/// never expires the canvas, a changed one expires at most once per interval and
/// otherwise asks the caller to defer by the remaining time.</summary>
public class PrintConsoleExpirePolicyTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    static OutputSnapshot Out(string status = "connected - idle", double progress = 0)
        => new OutputSnapshot(status, "idle", progress, null, true, "", Array.Empty<string>(), "info");

    [Fact]
    public void Equal_outputs_never_expire()
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(), live: true, lastExpireUtc: null, nowUtc: T0, minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.None, v);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void Not_live_never_expires_even_when_changed()
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(progress: 0.5), live: false, lastExpireUtc: null, nowUtc: T0, minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.None, v);
        Assert.Equal(0, delay);

        v = PrintConsoleExpirePolicy.Decide(null, Out(), live: false, lastExpireUtc: null, nowUtc: T0, minIntervalMs: 1000, out delay);
        Assert.Equal(ExpireVerdict.None, v);
    }

    [Fact]
    public void First_output_expires_now()
    {
        var v = PrintConsoleExpirePolicy.Decide(null, Out(), live: true, lastExpireUtc: null, nowUtc: T0, minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.Now, v);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void Changed_after_interval_expires_now()
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(progress: 0.5), live: true,
            lastExpireUtc: T0, nowUtc: T0.AddMilliseconds(1500), minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.Now, v);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void Changed_within_interval_defers_by_remaining_time()
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(progress: 0.5), live: true,
            lastExpireUtc: T0, nowUtc: T0.AddMilliseconds(300), minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.Defer, v);
        Assert.Equal(700, delay);
    }

    [Fact]
    public void Changed_with_no_previous_expire_expires_now()
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(progress: 0.5), live: true,
            lastExpireUtc: null, nowUtc: T0, minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.Now, v);
        Assert.Equal(0, delay);
    }

    [Theory]
    [InlineData(1000, ExpireVerdict.Now, 0)]
    [InlineData(999, ExpireVerdict.Defer, 1)]
    [InlineData(0, ExpireVerdict.Defer, 1000)]
    public void Interval_boundary(int elapsedMs, ExpireVerdict expected, int expectedDelay)
    {
        var v = PrintConsoleExpirePolicy.Decide(Out(), Out(progress: 0.5), live: true,
            lastExpireUtc: T0, nowUtc: T0.AddMilliseconds(elapsedMs), minIntervalMs: 1000, out int delay);
        Assert.Equal(expected, v);
        Assert.Equal(expectedDelay, delay);
    }

    [Fact]
    public void Output_equality_compares_log_by_sequence()
    {
        var a = new OutputSnapshot("s", "idle", 0, null, true, "", new[] { "l1", "l2" }, "i");
        var b = new OutputSnapshot("s", "idle", 0, null, true, "", new List<string> { "l1", "l2" }, "i");
        var c = new OutputSnapshot("s", "idle", 0, null, true, "", new[] { "l1" }, "i");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.False(a.Equals(null));
    }
}
