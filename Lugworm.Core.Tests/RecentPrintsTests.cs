using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Recent prints and the immutable G-code archive. The feature exists because a
/// remote file name proves nothing: <c>print.gcode</c> is overwritten by the next upload,
/// so a repeat print has to run bytes that were kept and can be verified - or be refused.</summary>
public class RecentPrintsTests
{
    const string Root = "C:/staging";
    const string Dest = "//nas/prints";
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 10, 0, 0, DateTimeKind.Utc);

    DateTime _now = T0;
    int _suffix;

    LogRetentionCoordinator New(FakeFileSystem fs)
        => new LogRetentionCoordinator(fs, Root, () => _now, () => (++_suffix).ToString("x6", CultureInfo.InvariantCulture));

    static byte[] Gcode(string text) => Encoding.UTF8.GetBytes(text);

    static readonly ReprintContext Potterbot =
        new ReprintContext("192.168.1.42", "fp-aaaa", "potterbot-scara-v4", "Scara", "Duet");

    static RecentPrintEntry Entry(string id, DateTime started, string name = "vase.gcode",
                                  string sha = "abc", long bytes = 10, bool archive = true,
                                  ReprintContext? machine = null, string? outcome = "Idle")
        => new RecentPrintEntry(id, name, started, outcome, machine ?? Potterbot, false, sha, bytes,
                                archive, archive ? null : "no G-code was archived for this run",
                                LogRetentionState.Saved, Dest + "/" + id);

    // ---- the archive -----------------------------------------------------------------

    [Fact]
    public void TheRunsBytesAreArchivedBesideItsLogAndRecordedInTheManifest()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("vase.gcode");
        var bytes = Gcode("G28\nG1 X10 Y10 E1\n");

        Assert.True(c.ArchiveCurrentJob(bytes, "20260909-100000-r1", Potterbot, dryRun: false));

        Assert.Equal(bytes, fs.Files[Root + "/" + id + "/job.gcode"].ToArray());
        var manifest = JobLogManifest.Parse(fs.Text(Root + "/" + id + "/manifest.json"));
        // version 3 added this archive; version 4 added the per-run drop counters
        Assert.Equal(4, JobLogManifest.CurrentVersion);
        Assert.Equal(JobSnapshot.Sha256Hex(bytes), manifest.GcodeSha256);
        Assert.Equal(bytes.Length, manifest.GcodeBytes);
        Assert.Equal("20260909-100000-r1", manifest.RunId);
        Assert.Equal("192.168.1.42", manifest.Host);
        Assert.Equal("fp-aaaa", manifest.Fingerprint);
        Assert.Equal("potterbot-scara-v4", manifest.ProfileId);
        Assert.Equal("Scara", manifest.Mechanism);
        Assert.Equal("Duet", manifest.Transport);
        Assert.False(manifest.DryRun);
        Assert.DoesNotContain("password", manifest.ToJson(), StringComparison.OrdinalIgnoreCase);

        var read = c.ReadArchivedGcode(id);
        Assert.Equal(bytes, read);
        var run = Assert.Single(c.RecentRuns());
        Assert.True(run.ArchiveAvailable);
        Assert.Null(ReprintPolicy.VerifyBytes(run, read));
    }

    [Fact]
    public void TheArchiveIsWrittenOnceAndACallWithNoBytesRecordsTheContextOnly()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("vase.gcode");
        var first = Gcode("G28\nG1 X1\n");

        Assert.True(c.ArchiveCurrentJob(first, "r1", Potterbot, false));
        // Whatever arrives later, the run's own bytes stand: the archive is evidence.
        Assert.False(c.ArchiveCurrentJob(Gcode("G28\nG1 X999\n"), "r1", Potterbot, false));
        Assert.Equal(first, c.ReadArchivedGcode(id));

        // A run this session did not start records its machine and no bytes at all.
        c.CompleteJob();
        string external = c.BeginJob("someone-elses.gcode");
        Assert.False(c.ArchiveCurrentJob(null, "", Potterbot, false));
        Assert.False(fs.FileExists(Root + "/" + external + "/job.gcode"));
        var run = c.RecentRuns().Single(r => r.Id == external);
        Assert.False(run.ArchiveAvailable);
        Assert.Contains("not available for reprint", run.ArchiveNote);
        Assert.Null(c.ReadArchivedGcode(external));
    }

    [Fact]
    public void TheArchiveIsMirroredAndFinalisedLikeThePrintLog()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("vase.gcode");
        var bytes = Gcode("G28\nG1 X10\nG1 X20\n");
        c.ArchiveCurrentJob(bytes, "r1", Potterbot, false);
        c.AppendRecord("row1");
        c.CompleteJob();

        for (int i = 0; i < 5 && c.Snapshot().Jobs.Single().State != LogRetentionState.Saved; i++) c.SyncOnce();
        Assert.Equal(LogRetentionState.Saved, c.Snapshot().Jobs.Single().State);

        string dir = Dest + "/" + id;
        Assert.Equal(bytes, fs.Files[dir + "/job.gcode"].ToArray());
        Assert.False(fs.FileExists(dir + "/job.gcode.partial"));
        Assert.False(fs.DirectoryExists(Root + "/" + id));   // staging is gone
        // The bytes are read back from the destination once the staging buffer went.
        Assert.Equal(bytes, c.ReadArchivedGcode(id));
    }

    [Fact]
    public void ARunWithoutBytesLeavesNoEmptyArchiveAtTheDestination()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("external.gcode");
        c.ArchiveCurrentJob(null, "", Potterbot, false);
        c.AppendRecord("row1");
        c.CompleteJob();
        for (int i = 0; i < 5 && c.Snapshot().Jobs.Single().State != LogRetentionState.Saved; i++) c.SyncOnce();

        // An empty job.gcode would read as a reprintable run with nothing in it.
        Assert.False(fs.FileExists(Dest + "/" + id + "/job.gcode"));
        Assert.False(fs.FileExists(Dest + "/" + id + "/job.gcode.partial"));
        Assert.True(fs.FileExists(Dest + "/" + id + "/print.csv"));
    }

    [Fact]
    public void RecoveryDropsAnArchiveThatNoLongerMatchesItsManifest()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("vase.gcode");
        c.ArchiveCurrentJob(Gcode("G28\nG1 X10\n"), "r1", Potterbot, false);
        c.AppendRecord("row1");

        // Crash, then a truncated archive on the disk that comes back.
        fs.Files[Root + "/" + id + "/job.gcode"].RemoveRange(0, 4);
        var next = New(fs);
        next.Recover();

        var run = Assert.Single(next.RecentRuns());
        Assert.False(run.ArchiveAvailable);
        Assert.Null(next.ReadArchivedGcode(id));
    }

    [Fact]
    public void HistorySurvivesARestartByReadingTheDestination()
    {
        var fs = new FakeFileSystem();
        var first = New(fs);
        first.Recover();
        first.SetDestination(Dest);
        first.SetKeepAll(true);
        string id = first.BeginJob("vase.gcode");
        var bytes = Gcode("G28\nG1 X10\n");
        first.ArchiveCurrentJob(bytes, "r1", Potterbot, false);
        first.AppendRecord("row1");
        first.CompleteJob();
        for (int i = 0; i < 5 && first.Snapshot().Jobs.Single().State != LogRetentionState.Saved; i++) first.SyncOnce();

        // A new process: nothing is staged any more, and the list must not be empty.
        var second = New(fs);
        second.Recover();
        second.SetDestination(Dest);
        Assert.Empty(second.RecentRuns());
        second.IndexDestination();

        var run = Assert.Single(second.RecentRuns());
        Assert.Equal(id, run.Id);
        Assert.Equal("vase.gcode", run.JobName);
        Assert.True(run.ArchiveAvailable);
        Assert.Equal(bytes, second.ReadArchivedGcode(id));
        Assert.Null(ReprintPolicy.RefuseReprint(run, Potterbot));
    }

    [Fact]
    public void DiscardingABufferMakesTheArchiveReportItselfGoneRatherThanReprint()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("vase.gcode");
        c.ArchiveCurrentJob(Gcode("G28\nG1 X10\n"), "r1", Potterbot, false);
        c.CompleteJob();
        var before = Assert.Single(c.RecentRuns());
        Assert.True(before.ArchiveAvailable);

        Assert.True(c.Discard(id));

        Assert.Empty(c.RecentRuns());
        Assert.Null(c.ReadArchivedGcode(id));
        // The console keeps the entry it selected; the reprint must refuse on the bytes.
        Assert.Contains("could not be read", ReprintPolicy.VerifyBytes(before, null));
    }

    [Fact]
    public void AVersion2ManifestStillParsesAndListsAsNotReprintable()
    {
        // The logs of every print made before this feature existed: they stay readable and
        // stay listed, and they say plainly that their bytes were never kept.
        const string old = "{\"version\":2,\"id\":\"20260902-140800-abc123\",\"jobName\":\"dome.gcode\","
                         + "\"startedUtc\":\"2026-09-02T14:08:00.0000000Z\",\"completedUtc\":null,"
                         + "\"state\":\"Saved\",\"permanent\":true,\"complete\":true,\"outcome\":\"Halted\","
                         + "\"destination\":null,\"destinationDirectory\":null,\"printOffset\":10,"
                         + "\"eventsOffset\":2,\"diagnosticsOffset\":0,\"finalized\":true,\"lastError\":null}";
        var manifest = JobLogManifest.Parse(old);

        Assert.Equal("20260902-140800-abc123", manifest.Id);
        Assert.Equal("Halted", manifest.Outcome);
        Assert.Equal("", manifest.GcodeSha256);
        Assert.Equal(0, manifest.GcodeBytes);
        Assert.Equal("", manifest.Host);
        Assert.False(manifest.DryRun);

        var entry = Entry(manifest.Id, manifest.StartedUtc, manifest.JobName, sha: "", bytes: 0,
                          archive: false, outcome: manifest.Outcome);
        Assert.NotNull(ReprintPolicy.RefuseSelection(entry));
        Assert.NotNull(RecentPrints.Find(RecentPrints.Order(new[] { entry }), manifest.Id));
    }

    // ---- the list ---------------------------------------------------------------------

    [Fact]
    public void TheListIsNewestFirstAndCapped()
    {
        var entries = Enumerable.Range(0, 14)
            .Select(i => Entry("id" + i.ToString("00", CultureInfo.InvariantCulture), T0.AddMinutes(i)))
            .ToList();

        var ordered = RecentPrints.Order(entries);
        Assert.Equal(RecentPrints.DefaultCap, ordered.Count);
        Assert.Equal(10, ordered.Count);
        Assert.Equal("id13", ordered[0].Id);
        Assert.Equal("id04", ordered[9].Id);
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i - 1].StartedUtc >= ordered[i].StartedUtc);
    }

    [Fact]
    public void TwoRunsOfTheSameFileWithTheSameNameStayDistinct()
    {
        var a = Entry("run-a", T0, name: "vase.gcode", sha: "hash-1");
        var b = Entry("run-b", T0.AddMinutes(30), name: "vase.gcode", sha: "hash-1");

        var ordered = RecentPrints.Order(new[] { a, b });
        Assert.Equal(2, ordered.Count);
        Assert.Equal("run-b", ordered[0].Id);
        // Selection is by run id, never by name: the same name is not the same run.
        Assert.Same(a, RecentPrints.Find(ordered, "run-a"));
        Assert.Same(b, RecentPrints.Find(ordered, "run-b"));
        Assert.Null(RecentPrints.Find(ordered, "vase.gcode"));
    }

    [Fact]
    public void AnEntryWithoutAnArchiveIsListedAndExplainsItself()
    {
        var metadataOnly = Entry("old-run", T0, archive: false, sha: "", bytes: 0);
        var ordered = RecentPrints.Order(new[] { metadataOnly, Entry("fresh", T0.AddMinutes(1)) });

        Assert.Equal(2, ordered.Count);                        // visible, not hidden
        Assert.NotNull(RecentPrints.Find(ordered, "old-run"));
        string? refusal = ReprintPolicy.RefuseSelection(metadataOnly);
        Assert.NotNull(refusal);
        Assert.Contains("no G-code was archived", refusal);
    }

    [Fact]
    public void AnUnknownOutcomeIsNeverReportedAsSuccess()
    {
        var unknown = Entry("run", T0, outcome: null);
        Assert.Equal("outcome unknown", unknown.OutcomeText);
        Assert.Equal("Halted", Entry("run2", T0, outcome: "Halted").Outcome);
    }

    // ---- the reprint gates --------------------------------------------------------------

    [Fact]
    public void AHashMismatchRefusesTheReprint()
    {
        var bytes = Gcode("G28\nG1 X10\n");
        var run = Entry("run", T0, sha: JobSnapshot.Sha256Hex(bytes), bytes: bytes.Length);

        Assert.Null(ReprintPolicy.VerifyBytes(run, bytes));

        var tampered = Gcode("G28\nG1 X99\n");
        string? refusal = ReprintPolicy.VerifyBytes(run, tampered);
        Assert.NotNull(refusal);
        Assert.Contains("does not match the hash recorded", refusal);
    }

    [Fact]
    public void AMissingOrEmptyArchiveRefusesTheReprint()
    {
        var run = Entry("run", T0, sha: "abc", bytes: 12);
        Assert.Contains("could not be read", ReprintPolicy.VerifyBytes(run, null));
        Assert.Contains("is empty", ReprintPolicy.VerifyBytes(run, Array.Empty<byte>()));
        Assert.Contains("bytes, not the", ReprintPolicy.VerifyBytes(run, Gcode("G28\n")));
    }

    [Fact]
    public void AChangedMechanismOrTransportRefuses_AChangedMachineOnlyWarns()
    {
        var run = Entry("run", T0);

        var cartesian = new ReprintContext("192.168.1.42", "fp-aaaa", "generic-duet-cartesian", "Cartesian", "Duet");
        string? refusal = ReprintPolicy.RefuseReprint(run, cartesian);
        Assert.NotNull(refusal);
        Assert.Contains("not interchangeable", refusal);

        var klipper = new ReprintContext("192.168.1.42", "fp-aaaa", "potterbot-scara-v4", "Scara", "Klipper");
        Assert.Contains("dialect differs", ReprintPolicy.RefuseReprint(run, klipper));

        // A second identical machine is ordinary: allowed, with what changed spelled out.
        var otherMachine = new ReprintContext("192.168.1.77", "fp-bbbb", "potterbot-scara-v4", "Scara", "Duet");
        Assert.Null(ReprintPolicy.RefuseReprint(run, otherMachine));
        string? warning = ReprintPolicy.Warn(run, otherMachine);
        Assert.NotNull(warning);
        Assert.Contains("192.168.1.42", warning);
        Assert.Contains("machine identity has changed", warning);

        Assert.Null(ReprintPolicy.Warn(run, Potterbot));
    }

    [Fact]
    public void ADryRunIsSaidToBeOneBeforeItIsPrintedAgain()
    {
        var dry = new RecentPrintEntry("run", "vase.gcode", T0, "Idle", Potterbot, true, "abc", 10,
                                       true, null, LogRetentionState.Saved, Dest + "/run");
        Assert.Null(ReprintPolicy.RefuseReprint(dry, Potterbot));
        Assert.Contains("dry run", ReprintPolicy.Warn(dry, Potterbot));
    }

    // ---- selection is not execution -------------------------------------------------------

    [Fact]
    public void SelectingARunPreparesItAndLeavesTheActivePrintAlone()
    {
        var active = JobSnapshot.FromBytes(Gcode("G28\nG1 X1\n"), "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        var tracker = new JobIdentityTracker();
        tracker.Prepare(active);
        tracker.StartPrepared(T0);

        var history = JobSnapshot.FromBytes(Gcode("G28\nG1 Y50\n"), null, JobSource.MachineFile, T0.AddHours(1));
        tracker.Prepare(history);   // a selection is a preparation, never a start

        Assert.Same(active, tracker.Active);
        Assert.Same(history, tracker.Prepared);
        Assert.Same(active, tracker.Effective);

        var run = Entry("run", T0);
        var state = PrintConsoleState.Empty("192.168.1.42")
            .WithActiveJob(active)
            .WithPreparedJob(history)
            .WithSelectedRun(run, null);

        Assert.True(state.RunSelected);
        Assert.Same(active, state.ActiveJob);
        Assert.Same(active, state.EffectiveJob);       // the machine still runs its own bytes
        Assert.Same(history, state.PreparedJob);

        var cleared = state.WithSelectedRun(null, null);
        Assert.False(cleared.RunSelected);
        Assert.Same(active, cleared.ActiveJob);        // clearing a selection stops nothing
    }

    [Fact]
    public void TheHistoryGateOnlyWaitsForACommandInFlight()
    {
        var idle = PrintConsoleState.Empty("192.168.1.42");
        var buttons = PrintConsoleButtons.Enabled(idle, hasHost: true, hasGcode: false, hasPoint: false,
                                                  gotoSupported: true, nowUtc: T0);
        // Offline history browsing is deliberate: loading bytes reaches no machine.
        Assert.True(buttons.History);

        var busy = PrintConsoleReducer.ActionStarted(idle, "uploading print.gcode");
        var busyButtons = PrintConsoleButtons.Enabled(busy, hasHost: true, hasGcode: true, hasPoint: false,
                                                      gotoSupported: true, nowUtc: T0);
        Assert.False(busyButtons.History);
        Assert.Contains("action in progress", busyButtons.Reasons["history"]);
    }

    [Fact]
    public void RecentRunsReachTheConsoleStateThroughRetention()
    {
        var runs = new[] { Entry("a", T0), Entry("b", T0.AddMinutes(5)) };
        var status = new LogRetentionStatus(false, Dest, null, Array.Empty<JobLogEntry>(), null, "Saved",
                                            RecentPrints.Order(runs));
        var state = PrintConsoleState.Empty("host").WithLogging(status);

        Assert.Equal(2, state.RecentRuns.Count);
        Assert.Equal("b", state.RecentRuns[0].Id);
        Assert.Empty(PrintConsoleState.Empty("host").RecentRuns);
    }
}
