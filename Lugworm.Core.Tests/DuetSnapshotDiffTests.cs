using System;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the snapshot comparison that drives every Duet component's
/// re-solve. The motion telemetry added in 1.10.0 must stay out of it: it changes
/// on virtually every poll, and routing it into a change would put the canvas back
/// into the redraw storm fixed in 1.7.0.</summary>
public class DuetSnapshotDiffTests
{
    static ObjectModelSnapshot Snap(MachineStatus status = MachineStatus.Idle,
                                    double x = 10, bool homed = true,
                                    string? reply = null, JobState? job = null)
        => new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString(),
            TakenUtc = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc),
            AllHomed = homed,
            LastReply = reply,
            Job = job,
            Axes = new[]
            {
                new AxisState { Letter = "X", MachinePosition = x, UserPosition = x, Homed = homed },
            },
        };

    // ---- characterization: behavior that existed before the print log ----

    [Fact]
    public void NoPreviousSnapshot_ReportsStatusAndJob()
        => Assert.Equal(SnapshotDelta.Status | SnapshotDelta.Job,
                        ObjectModelDiff.Compare(null, Snap()));

    [Fact]
    public void IdenticalSnapshots_ReportNoChange()
        => Assert.Equal(SnapshotDelta.None, ObjectModelDiff.Compare(Snap(), Snap()));

    [Fact]
    public void MovedAxis_ReportsStatus()
        => Assert.Equal(SnapshotDelta.Status,
                        ObjectModelDiff.Compare(Snap(x: 10), Snap(x: 11)));

    [Fact]
    public void ChangedStatusOrHoming_ReportsStatus()
    {
        Assert.Equal(SnapshotDelta.Status,
                     ObjectModelDiff.Compare(Snap(MachineStatus.Idle), Snap(MachineStatus.Processing)));
        Assert.Equal(SnapshotDelta.Status,
                     ObjectModelDiff.Compare(Snap(homed: true), Snap(homed: false)));
    }

    [Fact]
    public void ChangedJob_ReportsJob()
    {
        var before = Snap(job: new JobState { FileName = "a.gcode", FilePosition = 10, Layer = 1 });
        var after = Snap(job: new JobState { FileName = "a.gcode", FilePosition = 20, Layer = 2 });

        Assert.Equal(SnapshotDelta.Job, ObjectModelDiff.Compare(before, after));
    }

    [Fact]
    public void ChangedReply_ReportsReply()
        => Assert.Equal(SnapshotDelta.Reply,
                        ObjectModelDiff.Compare(Snap(reply: "ok"), Snap(reply: "T:20")));

    // ---- the 1.10.0 regression guard ----

    [Fact]
    public void MotionTelemetryAlone_IsNotAChange()
    {
        var before = Snap();
        before.RequestedSpeed = 20;
        before.TopSpeed = 25;
        before.SpeedFactorPercent = 100;
        before.RawExtrusion = 128.75;

        var after = Snap();
        after.RequestedSpeed = 31.5;      // changes on essentially every poll
        after.TopSpeed = 40.0;
        after.SpeedFactorPercent = 125.0;
        after.RawExtrusion = 130.25;

        Assert.Equal(SnapshotDelta.None, ObjectModelDiff.Compare(before, after));
    }

    [Fact]
    public void MotionTelemetryDoesNotMaskARealChange()
    {
        var before = Snap(x: 10);
        before.TopSpeed = 25;
        var after = Snap(x: 11);
        after.TopSpeed = 25;

        Assert.Equal(SnapshotDelta.Status, ObjectModelDiff.Compare(before, after));
    }

    // ---- the plugin-side mapping this enum feeds ----

    [Fact]
    public void SnapshotDeltaAndSessionChangeKind_AreNotBitCompatible()
    {
        // MachineSession must map these per flag; SessionChangeKind starts at
        // Connection = 1, so a numeric cast would relabel every change.
        Assert.Equal(1, (int)SnapshotDelta.Status);
        Assert.Equal(2, (int)SnapshotDelta.Job);
        Assert.Equal(4, (int)SnapshotDelta.Reply);
    }
}
