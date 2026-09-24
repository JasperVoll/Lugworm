using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The retention coordinator's third stream (raw-telemetry captures) and the run
/// outcome, plus the rule that Console-configured retention needs nothing from the Live
/// component.</summary>
public class RetentionDiagnosticsTests
{
    const string Root = "C:/staging";
    const string Dest = "//nas/prints";
    DateTime _now = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
    int _suffix;

    LogRetentionCoordinator New(FakeFileSystem fs)
        => new LogRetentionCoordinator(fs, Root, () => _now,
                                       () => (++_suffix).ToString("x6", CultureInfo.InvariantCulture));

    [Fact]
    public void DiagnosticLinesGoToTheirOwnStagedStream()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("print.gcode");
        c.AppendRecord("row1");
        c.AppendDiagnostic("{\"capture\":\"SuspectPathDisagreement\"}");
        c.AppendDiagnostic("{\"acq\":42}");

        Assert.Equal("row1\n", fs.Text(Root + "/" + id + "/print.csv"));
        Assert.Equal("{\"capture\":\"SuspectPathDisagreement\"}\n{\"acq\":42}\n",
                     fs.Text(Root + "/" + id + "/diagnostics.jsonl"));
    }

    [Fact]
    public void DiagnosticsAreMirroredAndFinalisedLikeTheOtherStreams()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("print.gcode");
        c.AppendRecord("row1");
        c.AppendEvent("started");
        c.AppendDiagnostic("{\"acq\":1}");
        c.CompleteJob();
        while (c.SyncOnce()) { }

        var job = Assert.Single(c.Snapshot().Jobs);
        Assert.Equal(LogRetentionState.Saved, job.State);
        string dir = job.DestinationDirectory!;
        Assert.Equal("{\"acq\":1}\n", fs.Text(dir + "/diagnostics.jsonl"));
        Assert.False(fs.FileExists(dir + "/diagnostics.jsonl.partial"));
        Assert.Equal("row1\n", fs.Text(dir + "/print.csv"));
        var manifest = JobLogManifest.Parse(fs.Text(dir + "/manifest.json"));
        Assert.Equal("{\"acq\":1}\n".Length, manifest.DiagnosticsOffset);
    }

    [Fact]
    public void ADiagnosticBurstDoesNotFinaliseTheJobBeforeItLands()
    {
        // The capture arrives after the last CSV row; finalising on the CSV alone would
        // publish a job whose diagnostics file is still short.
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        c.BeginJob("print.gcode");
        c.AppendRecord("row1");
        c.SyncOnce();
        c.AppendDiagnostic("{\"acq\":1}");
        c.CompleteJob();
        while (c.SyncOnce()) { }

        var job = Assert.Single(c.Snapshot().Jobs);
        Assert.Equal(LogRetentionState.Saved, job.State);
        Assert.Equal("{\"acq\":1}\n", fs.Text(job.DestinationDirectory + "/diagnostics.jsonl"));
    }

    [Fact]
    public void CompleteMeansTheLogFinished_TheOutcomeSaysHowTheRunEnded()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("print.gcode");
        c.AppendRecord("row1");
        c.SetJobOutcome("Halted");
        c.CompleteJob();

        var manifest = JobLogManifest.Parse(fs.Text(Root + "/" + id + "/manifest.json"));
        Assert.True(manifest.Complete);
        Assert.Equal("Halted", manifest.Outcome);
        Assert.Equal("Halted", Assert.Single(c.Snapshot().Jobs).Outcome);
    }

    [Fact]
    public void AnUnknownOutcomeStaysNull_NeverASuccessfulPrint()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("print.gcode");
        c.CompleteJob();
        var manifest = JobLogManifest.Parse(fs.Text(Root + "/" + id + "/manifest.json"));
        Assert.True(manifest.Complete);
        Assert.Null(manifest.Outcome);
    }

    [Fact]
    public void AVersion1ManifestStillParsesAndKnowsNothingAboutTheNewFields()
    {
        // The 2026-09-09 dome capture's own manifest.
        var manifest = JobLogManifest.Parse(
            System.IO.File.ReadAllText(TelemetryReplayFixtures.Path("dome-20260909", "manifest.json")));
        Assert.Equal("20260909-074734-c1a776", manifest.Id);
        Assert.True(manifest.Complete);
        Assert.Null(manifest.Outcome);
        Assert.Equal(0, manifest.DiagnosticsOffset);
        // and it round-trips through the version-2 writer
        var again = JobLogManifest.Parse(manifest.ToJson());
        Assert.Equal(manifest.PrintOffset, again.PrintOffset);
        Assert.Null(again.Outcome);
    }

    [Fact]
    public void RecoveryPicksUpTheDiagnosticsLengthToo()
    {
        var fs = new FakeFileSystem();
        var first = New(fs);
        first.Recover();
        string id = first.BeginJob("print.gcode");
        first.AppendRecord("row1");
        first.AppendDiagnostic("{\"acq\":1}");
        // no CompleteJob: an unclean shutdown

        var second = New(fs);
        second.Recover();
        var job = Assert.Single(second.Snapshot().Jobs);
        Assert.Equal(id, job.Id);
        Assert.True(job.Recovered);
        Assert.Equal("row1\n".Length + "{\"acq\":1}\n".Length, job.StagedBytes);
    }

    /// <summary>The Live component owns no logging. The always-on session recorder the
    /// Console configures records a whole job and routes it to retention with no owner
    /// registration of any kind, which is what "Console logs persist with Live absent"
    /// means at the Core level.</summary>
    [Fact]
    public void SessionRetentionRecordsAWholeJobWithNoLoggingOwner()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();

        // exactly how MachineSession configures its retention recorder
        var recorder = new PrintLogRecorder();
        recorder.Configure(new PrintLogOptions(true, 5, 50000, 0));

        var routed = new List<PrintLogRecord>();
        var T0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i <= 12; i++)
        {
            var snap = Printing(T0.AddSeconds(i * 0.4), i * 10, 1 + i * 0.1, 1000 + i * 100);
            routed.AddRange(recorder.Observe(snap));
        }
        routed.AddRange(recorder.Observe(new ObjectModelSnapshot
        {
            Status = MachineStatus.Idle,
            RawStatus = "Idle",
            TakenUtc = T0.AddSeconds(6),
        }));

        string? jobName = null;
        foreach (var record in routed)
        {
            if (record.Kind == PrintLogEventKind.JobStart)
            {
                jobName = record.JobFile;
                c.BeginJob(record.JobFile ?? "");
                c.AppendRecord(PrintLogCsv.Header());
            }
            else if (!string.IsNullOrWhiteSpace(record.JobFile) && record.JobFile != jobName)
            {
                jobName = record.JobFile;
                c.NameCurrentJob(record.JobFile!);
            }
            c.AppendRecord(PrintLogCsv.Row(record));
            if (record.Kind == PrintLogEventKind.JobEnd)
            {
                c.SetJobOutcome(record.TerminalStatus ?? "Unknown");
                c.CompleteJob();
            }
        }

        var job = Assert.Single(c.Snapshot().Jobs);
        Assert.Equal("0:/gcodes/print.gcode", job.JobName);
        Assert.True(job.Complete);
        Assert.Equal("Idle", job.Outcome);

        string csv = fs.Text(Root + "/" + job.Id + "/print.csv");
        var parsed = PrintLogCsvReader.Parse(csv.Split('\n'));
        Assert.Equal(0, parsed.SkippedRows);
        Assert.Contains(parsed.Records, r => r.Kind == PrintLogEventKind.JobStart);
        Assert.Contains(parsed.Records, r => r.Kind == PrintLogEventKind.JobEnd);
        Assert.All(parsed.Records, r => Assert.Equal(PrintLogCsv.SchemaVersion, r.Schema));
        Assert.All(parsed.Records, r => Assert.NotEqual("", r.RunId));
    }

    static ObjectModelSnapshot Printing(DateTime utc, double x, double extrusion, long filePosition)
    {
        var snap = new ObjectModelSnapshot
        {
            Status = MachineStatus.Processing,
            RawStatus = "Processing",
            Axes = new[]
            {
                new AxisState { Letter = "X", MachinePosition = x, Homed = true },
                new AxisState { Letter = "Y", MachinePosition = 0, Homed = true },
                new AxisState { Letter = "Z", MachinePosition = 5, Homed = true },
            },
            AllHomed = true,
            RawExtrusion = extrusion,
            RequestedSpeed = 43,
            TakenUtc = utc,
            Job = new JobState
            {
                FileName = "0:/gcodes/print.gcode",
                Layer = 2,
                FilePosition = filePosition,
                FileSize = 1000000,
                FractionPrinted = filePosition / 1000000.0,
                TimePrinting = 1,
            },
        };
        return snap;
    }
}
