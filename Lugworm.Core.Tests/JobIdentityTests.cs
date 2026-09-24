using System;
using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The immutable job snapshot and the prepared-vs-active distinction. A file name
/// is not an identity: <c>print.gcode</c> is overwritten on every upload, and the same file
/// printed twice is two runs that must never share a log, a segment or a history entry.</summary>
public class JobIdentityTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
    static byte[] Gcode(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ASnapshotCarriesTheExactBytesAndTheirHash()
    {
        var bytes = Gcode("G21\nG90\nG1 X10 Y10 Z1 E0.5\n");
        var job = JobSnapshot.FromBytes(bytes, "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);

        Assert.Equal(bytes.Length, job.ByteCount);
        Assert.Equal(bytes, job.GcodeBytes());
        Assert.Equal(64, job.Sha256.Length);
        Assert.True(job.IntendedPathAvailable);
        Assert.Equal("0:/gcodes/print.gcode", job.RemotePath);
    }

    [Fact]
    public void TheBytesAreCopiedIn_SoALaterMutationCannotRewriteHistory()
    {
        var bytes = Gcode("G1 X1\n");
        var job = JobSnapshot.FromBytes(bytes, null, JobSource.ConsoleInput, T0);
        bytes[0] = (byte)'X';
        Assert.Equal((byte)'G', job.GcodeBytes()[0]);
    }

    [Fact]
    public void TheSameContentPreparedTwice_HasOneHashAndTwoRunIds()
    {
        var bytes = Gcode("G1 X1\n");
        var a = JobSnapshot.FromBytes(bytes, "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        var b = JobSnapshot.FromBytes(bytes, "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.True(a.SameContent(b));
        Assert.NotEqual(a.RunId, b.RunId);
    }

    [Fact]
    public void AnExternallyStartedJob_HasNoIntendedPath()
    {
        var job = JobSnapshot.External("0:/gcodes/print.gcode", T0);
        Assert.False(job.IntendedPathAvailable);
        Assert.Equal("", job.Sha256);
        Assert.Empty(job.GcodeBytes());
        Assert.Equal(JobSource.External, job.Source);
    }

    [Fact]
    public void EditingTheConsoleInputDuringAPrint_DoesNotSwitchTheActiveJob()
    {
        var tracker = new JobIdentityTracker();
        var first = JobSnapshot.FromBytes(Gcode("G1 X1\n"), "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        tracker.Prepare(first);
        var active = tracker.StartPrepared(T0);
        Assert.NotNull(active);

        // the operator re-slices while the machine is printing
        var second = JobSnapshot.FromBytes(Gcode("G1 X2\n"), "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        tracker.Prepare(second);

        Assert.Same(first, tracker.Active);
        Assert.Same(second, tracker.Prepared);
        Assert.Same(first, tracker.Effective);       // the live comparison follows the machine
        Assert.NotEqual(tracker.Active!.Sha256, tracker.Prepared!.Sha256);
    }

    [Fact]
    public void PrintingTheSameFileAgain_IsANewRun()
    {
        var tracker = new JobIdentityTracker();
        var job = JobSnapshot.FromBytes(Gcode("G1 X1\n"), "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        tracker.Prepare(job);
        var first = tracker.StartPrepared(T0)!;
        var second = tracker.StartPrepared(T0.AddMinutes(5))!;

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public void AnEndedRunLeavesThePreparedJobAlone()
    {
        var tracker = new JobIdentityTracker();
        var job = JobSnapshot.FromBytes(Gcode("G1 X1\n"), null, JobSource.ConsoleInput, T0);
        tracker.Prepare(job);
        tracker.StartPrepared(T0);
        tracker.EndRun();

        Assert.Null(tracker.Active);
        Assert.Same(job, tracker.Prepared);
        Assert.Same(job, tracker.Effective);
    }

    [Fact]
    public void AnExternalRunCanBeResolvedOnceItsFileIsRetrieved()
    {
        var tracker = new JobIdentityTracker();
        tracker.Prepare(JobSnapshot.FromBytes(Gcode("G1 X99\n"), null, JobSource.ConsoleInput, T0));
        var external = tracker.StartExternal("0:/gcodes/foreign.gcode", T0);
        Assert.False(external.IntendedPathAvailable);
        // the Console's own input must NOT stand in for the running job's path
        Assert.False(tracker.Effective!.IntendedPathAvailable);

        var resolved = tracker.ResolveExternal(Gcode("G1 X5\n"), "0:/gcodes/foreign.gcode", T0.AddSeconds(3))!;
        Assert.True(resolved.IntendedPathAvailable);
        Assert.Equal(JobSource.MachineFile, resolved.Source);
        Assert.Equal("0:/gcodes/foreign.gcode", resolved.RemotePath);
    }

    [Fact]
    public void TheConsoleStateKeepsPreparedAndActiveApart()
    {
        var state = PrintConsoleState.Empty("printer.local");
        Assert.Null(state.PreparedJob);
        Assert.Null(state.ActiveJob);
        Assert.False(state.IntendedPathAvailable);

        var prepared = JobSnapshot.FromBytes(Gcode("G1 X1\n"), null, JobSource.ConsoleInput, T0);
        var active = JobSnapshot.FromBytes(Gcode("G1 X2\n"), null, JobSource.ConsoleInput, T0);
        var s = state.WithPreparedJob(prepared).WithActiveJob(active);

        Assert.Same(active, s.EffectiveJob);
        Assert.True(s.IntendedPathAvailable);
        // and the copies carry both through every other With… call
        var later = s.WithLastResult("ok").WithPendingAction("Start");
        Assert.Same(prepared, later.PreparedJob);
        Assert.Same(active, later.ActiveJob);

        var external = s.WithActiveJob(JobSnapshot.External("0:/gcodes/x.gcode", T0));
        Assert.False(external.IntendedPathAvailable);
    }

    [Fact]
    public void PathRevisionTracksAReparseWithoutChangingTheRun()
    {
        var job = JobSnapshot.FromBytes(Gcode("G1 X1\n"), null, JobSource.ConsoleInput, T0);
        var reparsed = job.WithPathRevision(7);
        Assert.Equal(job.RunId, reparsed.RunId);
        Assert.Equal(job.Sha256, reparsed.Sha256);
        Assert.Equal(7, reparsed.PathRevision);
    }
}
