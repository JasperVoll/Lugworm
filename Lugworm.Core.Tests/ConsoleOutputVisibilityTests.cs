using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Output categories: which of the Duet Console's sockets exist, what that
/// costs, and what it means for the expire decision. The point of hiding a category is not
/// tidiness - it is that a value nobody can read must not re-solve the canvas.</summary>
public class ConsoleOutputVisibilityTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The three sockets a new console registers, plus whatever else is asked for.</summary>
    static OutputVisibility Default(params string[] extra)
        => OutputVisibility.Of(ConsoleOutputNames.Default.Concat(extra));

    static readonly OutputVisibility WithMotion = OutputVisibility.Of(
        ConsoleOutputNames.Host, ConsoleOutputNames.Status, ConsoleOutputNames.Gcode,
        ConsoleOutputNames.Pos, ConsoleOutputNames.Homed, ConsoleOutputNames.XHomed,
        ConsoleOutputNames.YHomed, ConsoleOutputNames.ZHomed, ConsoleOutputNames.BedOrigin,
        ConsoleOutputNames.ZZero);

    static PrintConsoleState Machine(double x, double y, double z)
        => PrintConsoleState.Empty("192.168.1.42").WithPosition(new Vec3(x, y, z));

    [Fact]
    public void TheDefaultSetIsHostStatusAndGcode()
    {
        Assert.Equal(new[] { "host", "status", "gcode" }, ConsoleOutputNames.Default);
        var v = Default();
        Assert.True(v.Shows(ConsoleOutputNames.Host));
        Assert.True(v.Shows(ConsoleOutputNames.Status));
        Assert.True(v.Shows(ConsoleOutputNames.Gcode));
        Assert.False(v.Shows(ConsoleOutputNames.Pos));
        Assert.False(v.Shows(ConsoleOutputNames.SpeedFactor));
        Assert.False(v.Shows(ConsoleOutputNames.Log));
        Assert.True(OutputVisibility.All.Shows(ConsoleOutputNames.Pos));
    }

    [Fact]
    public void AHiddenPositionCannotExpireTheCanvas()
    {
        var before = PrintConsoleOutputs.From(Machine(10, 20, 5), Default());
        var after = PrintConsoleOutputs.From(Machine(11, 21, 5), Default());

        Assert.Equal(before, after);
        Assert.Equal(before.GetHashCode(), after.GetHashCode());
        var verdict = PrintConsoleExpirePolicy.Decide(before, after, live: true, lastExpireUtc: null,
                                                      nowUtc: T0, minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.None, verdict);
        Assert.Equal(0, delay);
    }

    [Fact]
    public void TheSamePositionChangeExpiresOnceMotionIsShown()
    {
        var before = PrintConsoleOutputs.From(Machine(10, 20, 5), WithMotion);
        var after = PrintConsoleOutputs.From(Machine(11, 21, 5), WithMotion);

        Assert.NotEqual(before, after);
        Assert.Equal(ExpireVerdict.Now,
            PrintConsoleExpirePolicy.Decide(before, after, live: true, lastExpireUtc: null,
                                            nowUtc: T0, minIntervalMs: 1000, out _));
    }

    [Fact]
    public void ShowingACategoryRefreshesTheOutputsAtOnce()
    {
        var state = Machine(10, 20, 5);
        var compact = PrintConsoleOutputs.From(state, Default());
        var expanded = PrintConsoleOutputs.From(state, WithMotion);

        // Same machine facts, different socket set: the canvas has to fetch the new ones.
        Assert.NotEqual(compact, expanded);
        Assert.Equal(ExpireVerdict.Now,
            PrintConsoleExpirePolicy.Decide(compact, expanded, live: true, lastExpireUtc: null,
                                            nowUtc: T0, minIntervalMs: 1000, out _));
    }

    [Fact]
    public void LiveOffStillNeverExpires_WhateverIsVisible()
    {
        var before = PrintConsoleOutputs.From(Machine(10, 20, 5), WithMotion);
        var after = PrintConsoleOutputs.From(Machine(99, 99, 99), WithMotion);
        Assert.Equal(ExpireVerdict.None,
            PrintConsoleExpirePolicy.Decide(before, after, live: false, lastExpireUtc: null,
                                            nowUtc: T0, minIntervalMs: 1000, out _));
    }

    [Fact]
    public void TheCoalescingWindowStillHoldsWithAVisibleChange()
    {
        var before = PrintConsoleOutputs.From(Machine(10, 20, 5), WithMotion);
        var after = PrintConsoleOutputs.From(Machine(11, 20, 5), WithMotion);
        var verdict = PrintConsoleExpirePolicy.Decide(before, after, live: true,
                                                      lastExpireUtc: T0.AddMilliseconds(-400), nowUtc: T0,
                                                      minIntervalMs: 1000, out int delay);
        Assert.Equal(ExpireVerdict.Defer, verdict);
        Assert.Equal(600, delay);
    }

    [Fact]
    public void HiddenOutputsAreNotEvenBuilt()
    {
        var state = PrintConsoleState.Empty("host")
            .WithLog(new[] { "a", "b" }, 2)
            .WithPreparedJob(JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\nG1 X1\n"), null, JobSource.ConsoleInput, T0));

        var compact = PrintConsoleOutputs.From(state, OutputVisibility.Of(ConsoleOutputNames.Status));
        Assert.Empty(compact.Log);
        Assert.Equal("", compact.Info);
        Assert.Empty(compact.Gcode);
        // The identity is still carried, so showing the socket later is a real change.
        Assert.NotEqual("", compact.GcodeSha);

        var full = PrintConsoleOutputs.From(state, OutputVisibility.All);
        Assert.Equal(2, full.Log.Count);
        Assert.Contains("host:", full.Info, StringComparison.Ordinal);
        Assert.Equal(new[] { "G28", "G1 X1" }, full.Gcode);
    }

    [Fact]
    public void TheGcodeOutputFollowsTheEffectiveJob()
    {
        var prepared = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\n"), null, JobSource.ConsoleInput, T0);
        var active = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\nG1 X5\nG1 X10\n"), "0:/gcodes/print.gcode",
                                           JobSource.ConsoleInput, T0);
        var state = PrintConsoleState.Empty("host").WithPreparedJob(prepared);

        Assert.Equal(new[] { "G28" }, PrintConsoleOutputs.From(state, Default()).Gcode);

        // While a print runs the ACTIVE bytes win, however the input is edited meanwhile.
        var printing = state.WithActiveJob(active);
        Assert.Equal(new[] { "G28", "G1 X5", "G1 X10" }, PrintConsoleOutputs.From(printing, Default()).Gcode);

        var edited = printing.WithPreparedJob(
            JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\nG1 Y99\n"), null, JobSource.ConsoleInput, T0));
        Assert.Equal(new[] { "G28", "G1 X5", "G1 X10" }, PrintConsoleOutputs.From(edited, Default()).Gcode);
    }

    [Fact]
    public void AnExternallyStartedJobPublishesNoGcode()
    {
        var state = PrintConsoleState.Empty("host")
            .WithPreparedJob(JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\nG1 X1\n"), null, JobSource.ConsoleInput, T0))
            .WithActiveJob(JobSnapshot.External("0:/gcodes/print.gcode", T0));

        var outputs = PrintConsoleOutputs.From(state, Default());
        // Never the Console's own input in place of bytes nobody has.
        Assert.Empty(outputs.Gcode);
        Assert.Equal("", outputs.GcodeSha);
        Assert.False(state.IntendedPathAvailable);
    }

    [Fact]
    public void TheGcodeOutputComparesTheHashNotTheLines()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 5000).Select(i => "G1 X" + i)) + "\n");
        var job = JobSnapshot.FromBytes(bytes, null, JobSource.ConsoleInput, T0);
        var state = PrintConsoleState.Empty("host").WithPreparedJob(job);

        // Two projections of one job are equal, and the decoded lines are the SAME list -
        // a poll must neither re-decode 5000 lines nor look like a changed job.
        var a = PrintConsoleOutputs.From(state, Default());
        var b = PrintConsoleOutputs.From(state, Default());
        Assert.Equal(a, b);
        Assert.Same(a.Gcode, b.Gcode);

        var other = PrintConsoleOutputs.From(
            state.WithPreparedJob(JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\n"), null, JobSource.ConsoleInput, T0)),
            Default());
        Assert.NotEqual(a, other);
    }

    [Fact]
    public void ARepeatOfIdenticalBytesIsNotAChangedOutput()
    {
        var bytes = Encoding.UTF8.GetBytes("G28\nG1 X1\n");
        var first = JobSnapshot.FromBytes(bytes, null, JobSource.ConsoleInput, T0);
        var again = first.AsNewRun(T0.AddMinutes(5));
        Assert.NotEqual(first.RunId, again.RunId);

        var a = PrintConsoleOutputs.From(PrintConsoleState.Empty("h").WithPreparedJob(first), Default());
        var b = PrintConsoleOutputs.From(PrintConsoleState.Empty("h").WithPreparedJob(again), Default());
        // The SOCKET carries content, not run identity: the same bytes are the same output.
        Assert.Equal(a, b);
    }

    [Fact]
    public void JobLinesSurviveEveryLineEnding()
    {
        var crlf = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\r\nG1 X1\r\n"), null, JobSource.ConsoleInput, T0);
        Assert.Equal(new[] { "G28", "G1 X1" }, crlf.Lines());

        var noTrailingNewline = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\nG1 X1"), null, JobSource.ConsoleInput, T0);
        Assert.Equal(new[] { "G28", "G1 X1" }, noTrailingNewline.Lines());

        var blankInside = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\n\nG1 X1\n"), null, JobSource.ConsoleInput, T0);
        Assert.Equal(new[] { "G28", "", "G1 X1" }, blankInside.Lines());

        Assert.Empty(JobSnapshot.External(null, T0).Lines());
    }

    [Fact]
    public void EveryDeclaredOutputNameIsCoveredByExactlyOneCategoryName()
    {
        // The names are a contract with every saved document; a duplicate or a typo here
        // would silently drop a socket out of the equality above.
        var names = typeof(ConsoleOutputNames).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Equal(23, names.Count);   // the 22 retained sockets plus gcode
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in ConsoleOutputNames.Default) Assert.Contains(name, names);
    }
}
