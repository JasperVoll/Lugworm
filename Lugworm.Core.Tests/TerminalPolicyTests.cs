using System;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the terminal's classification and confirm-arming rules. The two
/// lists decide what a text box is allowed to send to a machine, so they are pinned
/// literally against the 2.6.0 Terminal component they were moved from - a silent
/// drift here widens what the board will accept.</summary>
public class TerminalPolicyTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("M20")]
    [InlineData("M105")]
    [InlineData("M114")]
    [InlineData("M115")]
    [InlineData("M119")]
    [InlineData("M122")]
    [InlineData("M408")]
    [InlineData("M503")]
    public void Diagnostic_allowlist_is_exactly_the_2_6_0_set(string line)
    {
        Assert.Equal(TerminalRoute.Diagnostic, TerminalPolicy.Classify(line, out _, out _));
    }

    [Theory]
    [InlineData("G0 X10")]
    [InlineData("G1 X10")]
    [InlineData("G2 X10 Y10 I5 J0")]
    [InlineData("G3 X10 Y10 I5 J0")]
    [InlineData("G28")]
    [InlineData("G53")]
    [InlineData("G90")]
    [InlineData("G91")]
    [InlineData("G92 X0")]
    [InlineData("M32")]
    [InlineData("M37")]
    [InlineData("M999")]
    [InlineData("M201 X500")]
    [InlineData("M204 P500")]
    [InlineData("M205")]
    [InlineData("M566 X60")]
    [InlineData("M84")]
    [InlineData("M220 S100")]
    [InlineData("M221 S100")]
    [InlineData("M104 S0")]
    [InlineData("M109 S0")]
    [InlineData("M140 S0")]
    [InlineData("M190 S0")]
    [InlineData("M106")]
    [InlineData("M107")]
    [InlineData("M302")]
    public void Advanced_set_is_exactly_the_2_6_0_set(string line)
    {
        Assert.Equal(TerminalRoute.Advanced, TerminalPolicy.Classify(line, out _, out _));
    }

    [Theory]
    [InlineData("M0")]          // unconditional stop - never was on either list
    [InlineData("M112")]        // emergency stop belongs to the E-STOP path, not the box
    [InlineData("G4 P100")]
    [InlineData("T0")]
    public void Anything_off_both_lists_is_refused(string line)
    {
        Assert.Equal(TerminalRoute.Refused, TerminalPolicy.Classify(line, out _, out _));
        var v = TerminalPolicy.Decide(line, DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Refuse, v.Decision);
        Assert.Equal("command is not on the diagnostic allowlist", v.Reason);
    }

    /// <summary>The lexer is a stricter gate than either list, and it runs first: a word
    /// without a numeric value never becomes a GcodeLine at all. This is unchanged 2.6.0
    /// behaviour - the Terminal component lexed identically - and it means M32 is
    /// effectively unreachable from the box, because every real M32 carries a filename.
    /// Fail-closed, so it is pinned rather than fixed here.</summary>
    [Theory]
    [InlineData("M32 file.g")]
    [InlineData("M291 P\"hi\"")]
    [InlineData("hello")]
    public void A_line_the_lexer_rejects_never_reaches_either_list(string line)
    {
        Assert.Equal(TerminalRoute.Refused, TerminalPolicy.Classify(line, out var parsed, out string? lexError));
        Assert.Null(parsed);
        Assert.NotNull(lexError);

        var v = TerminalPolicy.Decide(line, DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Refuse, v.Decision);
        Assert.StartsWith("line rejected: ", v.Reason);
    }

    [Fact]
    public void Empty_and_unlexable_lines_are_refused_with_their_own_reasons()
    {
        Assert.Equal("enter one G-code line", TerminalPolicy.Decide("   ", DateTime.MinValue, T0).Reason);

        var bad = TerminalPolicy.Decide("G1 X(", DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Refuse, bad.Decision);
        Assert.StartsWith("line rejected: ", bad.Reason);
    }

    [Fact]
    public void Diagnostics_send_immediately_without_arming()
    {
        var v = TerminalPolicy.Decide("M115", DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Send, v.Decision);
        Assert.Equal(TerminalRoute.Diagnostic, v.Route);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void Advanced_needs_two_presses_inside_a_five_second_window()
    {
        // First press arms and sends nothing.
        var first = TerminalPolicy.Decide("G1 X10", DateTime.MinValue, T0);
        Assert.Equal(TerminalSendDecision.Arm, first.Decision);
        Assert.Equal(TerminalRoute.Advanced, first.Route);

        var until = TerminalPolicy.ArmUntil(T0);
        Assert.Equal(T0.AddSeconds(5), until);

        // Second press inside the window sends.
        Assert.Equal(TerminalSendDecision.Send,
            TerminalPolicy.Decide("G1 X10", until, T0.AddSeconds(2)).Decision);

        // After it lapses the next press only re-arms.
        Assert.Equal(TerminalSendDecision.Arm,
            TerminalPolicy.Decide("G1 X10", until, T0.AddSeconds(6)).Decision);
    }

    [Fact]
    public void Console_m999_obeys_the_d4_halted_or_off_rule()
    {
        var armed = TerminalPolicy.ArmUntil(T0);

        // The console passes the D4 predicate; without it the Terminal tab would be a
        // raw bypass around the console's stricter Reset button.
        var refused = TerminalPolicy.Decide("M999", armed, T0.AddSeconds(1), resetAllowed: false);
        Assert.Equal(TerminalSendDecision.Refuse, refused.Decision);
        Assert.Contains("halted", refused.Reason!);

        // Halted/Off - allowed, still behind the confirm arming.
        Assert.Equal(TerminalSendDecision.Send,
            TerminalPolicy.Decide("M999", armed, T0.AddSeconds(1), resetAllowed: true).Decision);

        // The hidden Terminal component keeps its unchanged 2.6.0 behaviour (default true).
        Assert.Equal(TerminalSendDecision.Send,
            TerminalPolicy.Decide("M999", armed, T0.AddSeconds(1)).Decision);
    }

    [Fact]
    public void Reset_is_recognised_only_for_m999()
    {
        TerminalPolicy.Classify("M999", out var m999, out _);
        TerminalPolicy.Classify("M998", out var m998, out _);
        Assert.True(TerminalPolicy.IsReset(m999));
        Assert.False(TerminalPolicy.IsReset(m998));
    }

    [Fact]
    public void Confirm_window_is_a_half_open_interval()
    {
        var until = TerminalPolicy.ArmUntil(T0);
        Assert.True(TerminalPolicy.IsConfirmArmed(until, T0.AddSeconds(4.999)));
        Assert.False(TerminalPolicy.IsConfirmArmed(until, until));
        Assert.False(TerminalPolicy.IsConfirmArmed(DateTime.MinValue, T0));
    }
}
