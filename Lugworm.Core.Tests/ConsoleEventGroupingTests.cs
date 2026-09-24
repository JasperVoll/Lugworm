using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class ConsoleEventGroupingTests
{
    static string Line(string time, string message) => "[" + time + "] " + message;

    [Fact]
    public void Split_ParsesTimestampAndMessage()
    {
        var (time, message) = ConsoleEventGrouping.Split("[09:48:26] arm configuration unknown: an axis is unhomed");
        Assert.Equal("09:48:26", time);
        Assert.Equal("arm configuration unknown: an axis is unhomed", message);
    }

    [Fact]
    public void Split_LineWithoutTimestampPassesThrough()
    {
        var (time, message) = ConsoleEventGrouping.Split("no bracket here");
        Assert.Equal("", time);
        Assert.Equal("no bracket here", message);
    }

    [Fact]
    public void Split_EmptyAndNullAreSafe()
    {
        Assert.Equal(("", ""), ConsoleEventGrouping.Split(""));
        Assert.Equal(("", ""), ConsoleEventGrouping.Split(null!));
    }

    [Fact]
    public void Group_EmptyLogReturnsEmptyList()
    {
        Assert.Empty(ConsoleEventGrouping.Group(new List<string>()));
        Assert.Empty(ConsoleEventGrouping.Group(null!));
    }

    [Fact]
    public void Group_TwentyOneRepeatsCollapseToOneLineWithCountAndTimeRange()
    {
        // The 2026-09-09 dome startup logged this exact line 21 times before homing.
        var lines = new List<string>();
        for (int i = 0; i < 21; i++)
            lines.Add(Line($"09:48:{26 + i:00}", "arm configuration unknown: an axis is unhomed"));

        var grouped = ConsoleEventGrouping.Group(lines);

        Assert.Single(grouped);
        var g = grouped[0];
        Assert.Equal(21, g.Count);
        Assert.Equal("09:48:26", g.FirstTime);
        Assert.Equal("09:48:46", g.LastTime);
        Assert.Equal(21, g.RawLines.Count);
        Assert.Equal(ConsoleEventCategory.Motion, g.Category);
        string display = g.Display();
        Assert.Contains("x21", display);
        Assert.Contains("09:48:26", display);
        Assert.Contains("09:48:46", display);
        Assert.Contains("arm configuration unknown: an axis is unhomed", display);
    }

    [Fact]
    public void Group_SingleLineDisplaysWithoutACount()
    {
        var grouped = ConsoleEventGrouping.Group(new[] { Line("10:00:00", "connected to 192.168.1.50 (Duet 3, api 2)") });
        Assert.Single(grouped);
        Assert.Equal(1, grouped[0].Count);
        Assert.DoesNotContain("x1)", grouped[0].Display());
        Assert.StartsWith("[10:00:00] connected", grouped[0].Display());
    }

    [Fact]
    public void Group_InterleavedDifferentMessagesDoNotCollapse()
    {
        var lines = new List<string>
        {
            Line("09:00:00", "connected to 192.168.1.50 (Duet 3, api 2)"),
            Line("09:00:01", "arm configuration unknown: an axis is unhomed"),
            Line("09:00:02", "connected to 192.168.1.50 (Duet 3, api 2)"),
            Line("09:00:03", "arm configuration unknown: an axis is unhomed"),
        };

        var grouped = ConsoleEventGrouping.Group(lines);

        Assert.Equal(4, grouped.Count);
        Assert.All(grouped, g => Assert.Equal(1, g.Count));
    }

    [Fact]
    public void Group_OnlyConsecutiveRunsCollapse()
    {
        var lines = new List<string>
        {
            Line("09:00:00", "repeat me"),
            Line("09:00:01", "repeat me"),
            Line("09:00:02", "something else"),
            Line("09:00:03", "repeat me"),
        };

        var grouped = ConsoleEventGrouping.Group(lines);

        Assert.Equal(3, grouped.Count);
        Assert.Equal(2, grouped[0].Count);   // the first two "repeat me" lines
        Assert.Equal(1, grouped[1].Count);   // "something else"
        Assert.Equal(1, grouped[2].Count);   // the later, non-consecutive "repeat me"
    }

    [Theory]
    [InlineData("connect failed: timeout", ConsoleEventCategory.Errors)]
    [InlineData("!! M112 EMERGENCY STOP", ConsoleEventCategory.Errors)]
    [InlineData("MACHINE IDENTITY CONFLICT: expected X, got Y", ConsoleEventCategory.Errors)]
    [InlineData("connected to 192.168.1.50 (Duet 3, api 2)", ConsoleEventCategory.Connection)]
    [InlineData("reconnected", ConsoleEventCategory.Connection)]
    [InlineData("machine identity trusted by the operator: Duet3 @ 192.168.1.50", ConsoleEventCategory.Connection)]
    [InlineData("print log suppressed: disk full", ConsoleEventCategory.Job)]
    [InlineData("validation passed: 128 points checked", ConsoleEventCategory.Job)]
    [InlineData("arm configuration unknown: an axis is unhomed", ConsoleEventCategory.Motion)]
    [InlineData("firmware reset (M999 / FIRMWARE_RESTART)", ConsoleEventCategory.Motion)]
    [InlineData("motion baseline verified from M669", ConsoleEventCategory.Motion)]
    [InlineData("firmware (during 503 backoff): ok", ConsoleEventCategory.Firmware)]
    [InlineData("temperature history seeded from the machine (3 series)", ConsoleEventCategory.All)]
    public void Classify_AssignsExpectedCategory(string message, ConsoleEventCategory expected)
        => Assert.Equal(expected, ConsoleEventGrouping.Classify(message));

    [Fact]
    public void Classify_EmptyMessageIsAll()
        => Assert.Equal(ConsoleEventCategory.All, ConsoleEventGrouping.Classify(""));

    [Fact]
    public void Matches_AllFilterShowsEveryCategory()
    {
        foreach (ConsoleEventCategory category in new[]
                 { ConsoleEventCategory.All, ConsoleEventCategory.Connection, ConsoleEventCategory.Job,
                   ConsoleEventCategory.Motion, ConsoleEventCategory.Firmware, ConsoleEventCategory.Errors })
            Assert.True(ConsoleEventGrouping.Matches(ConsoleEventCategory.All, category));
    }

    [Fact]
    public void Matches_SpecificFilterOnlyShowsItsOwnCategory()
    {
        Assert.True(ConsoleEventGrouping.Matches(ConsoleEventCategory.Motion, ConsoleEventCategory.Motion));
        Assert.False(ConsoleEventGrouping.Matches(ConsoleEventCategory.Motion, ConsoleEventCategory.Connection));
        Assert.False(ConsoleEventGrouping.Matches(ConsoleEventCategory.Errors, ConsoleEventCategory.All));
    }

    [Fact]
    public void Group_ThenFilter_PicksOnlyMatchingLines()
    {
        var lines = new List<string>
        {
            Line("09:00:00", "connected to 192.168.1.50 (Duet 3, api 2)"),
            Line("09:00:01", "arm configuration unknown: an axis is unhomed"),
            Line("09:00:02", "arm configuration unknown: an axis is unhomed"),
            Line("09:00:03", "connect failed: timeout"),
        };

        var grouped = ConsoleEventGrouping.Group(lines);
        var motionOnly = grouped.Where(g => ConsoleEventGrouping.Matches(ConsoleEventCategory.Motion, g.Category)).ToList();
        var errorsOnly = grouped.Where(g => ConsoleEventGrouping.Matches(ConsoleEventCategory.Errors, g.Category)).ToList();

        Assert.Single(motionOnly);
        Assert.Equal(2, motionOnly[0].Count);
        Assert.Single(errorsOnly);
        Assert.Contains("connect failed", errorsOnly[0].Message);
    }
}
