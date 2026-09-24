using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Deterministic in-memory file system for the retention coordinator. Paths are
/// forward-slash strings; a predicate can make writes to chosen paths fail (a vanished
/// network share) or make an append land but then throw (an ambiguous failure).</summary>
sealed class FakeFileSystem : IRetentionFileSystem
{
    public readonly Dictionary<string, List<byte>> Files = new(StringComparer.Ordinal);
    public readonly HashSet<string> Dirs = new(StringComparer.Ordinal);
    public Func<string, bool> FailWrite = _ => false;
    public Func<string, bool> AmbiguousAppend = _ => false;
    public int Appends;

    public string Combine(string a, string b) => a.TrimEnd('/') + "/" + b;
    public bool FileExists(string path) => Files.ContainsKey(path);
    public bool DirectoryExists(string path) => Dirs.Contains(path);
    public void CreateDirectory(string path)
    {
        if (FailWrite(path)) throw new System.IO.IOException("cannot create " + path);
        var parts = path.Split('/');
        string p = "";
        for (int i = 0; i < parts.Length; i++)
        {
            p = i == 0 ? parts[0] : p + "/" + parts[i];
            if (p.Trim('/').Length > 0) Dirs.Add(p);   // keeps a UNC prefix intact
        }
    }
    public IReadOnlyList<string> Directories(string root)
        => Dirs.Where(d => d.StartsWith(root + "/", StringComparison.Ordinal) && !d.Substring(root.Length + 1).Contains('/'))
               .OrderBy(d => d, StringComparer.Ordinal).ToList();
    public long Length(string path)
    {
        if (FailWrite(path)) throw new System.IO.IOException("cannot reach " + path);
        return Files.TryGetValue(path, out var f) ? f.Count : throw new System.IO.FileNotFoundException(path);
    }
    public void Append(string path, byte[] data)
    {
        if (FailWrite(path)) throw new System.IO.IOException("cannot write " + path);
        Appends++;
        if (!Files.TryGetValue(path, out var f)) Files[path] = f = new List<byte>();
        f.AddRange(data);
        if (AmbiguousAppend(path)) throw new System.IO.IOException("connection reset after write to " + path);
    }
    public byte[] Read(string path, long offset, int count)
    {
        var f = Files[path];
        int n = (int)Math.Max(0, Math.Min(count, f.Count - offset));
        return f.GetRange((int)offset, n).ToArray();
    }
    public string ReadText(string path) => Encoding.UTF8.GetString(Files[path].ToArray());
    public void WriteTextAtomic(string path, string text)
    {
        if (FailWrite(path)) throw new System.IO.IOException("cannot write " + path);
        Files[path] = new List<byte>(Encoding.UTF8.GetBytes(text));
    }
    public void Move(string from, string to)
    {
        if (FailWrite(to)) throw new System.IO.IOException("cannot move to " + to);
        if (Files.TryGetValue(from, out var f)) { Files.Remove(from); Files[to] = f; return; }
        if (Dirs.Contains(from))
        {
            foreach (var d in Dirs.Where(d => d == from || d.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            { Dirs.Remove(d); Dirs.Add(to + d.Substring(from.Length)); }
            foreach (var kv in Files.Where(kv => kv.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            { Files.Remove(kv.Key); Files[to + kv.Key.Substring(from.Length)] = kv.Value; }
            return;
        }
        throw new System.IO.FileNotFoundException(from);
    }
    public void DeleteFile(string path) { Files.Remove(path); }
    public void DeleteDirectory(string path)
    {
        if (FailWrite(path)) throw new System.IO.IOException("cannot delete " + path);
        foreach (var d in Dirs.Where(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) Dirs.Remove(d);
        foreach (var k in Files.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) Files.Remove(k);
    }

    public string Text(string path) => Files.TryGetValue(path, out var f) ? Encoding.UTF8.GetString(f.ToArray()) : "";
}

public class LogRetentionTests
{
    const string Root = "C:/staging";
    const string Dest = "//nas/prints";
    DateTime _now = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc);
    int _suffix;

    LogRetentionCoordinator New(FakeFileSystem fs)
        => new LogRetentionCoordinator(fs, Root, () => _now, () => (++_suffix).ToString("x6", CultureInfo.InvariantCulture));

    static JobLogEntry Only(LogRetentionCoordinator c) => Assert.Single(c.Snapshot().Jobs);

    [Fact]
    public void Every_job_records_temporarily_and_stays_temporary_until_saved()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.Recover();
        string id = c.BeginJob("vase.gcode");
        Assert.Equal(LogRetentionState.TemporaryRecording, Only(c).State);
        Assert.Equal("Temporary", c.Snapshot().Badge);
        c.AppendRecord("row1");
        c.AppendEvent("started");
        Assert.Equal("row1\n", fs.Text(Root + "/" + id + "/print.csv"));
        Assert.Equal("started\n", fs.Text(Root + "/" + id + "/events.log"));
        var manifest = JobLogManifest.Parse(fs.Text(Root + "/" + id + "/manifest.json"));
        Assert.False(manifest.Permanent);
        Assert.DoesNotContain("password", manifest.ToJson(), StringComparison.OrdinalIgnoreCase);

        c.CompleteJob();
        Assert.Equal(LogRetentionState.TemporaryComplete, Only(c).State);
        Assert.False(c.SyncOnce());   // nothing is kept without a decision
        Assert.DoesNotContain(Dest, fs.Dirs);

        // clean shutdown removes exactly this kind of buffer
        c.Shutdown();
        Assert.Empty(c.Snapshot().Jobs);
        Assert.DoesNotContain(Root + "/" + id, fs.Dirs);
    }

    [Fact]
    public void Keep_all_needs_a_destination_and_promotes_the_active_job_from_its_first_record()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        Assert.False(c.SetKeepAll(true));   // no folder yet: stays off
        Assert.False(c.KeepAll);

        string id = c.BeginJob("wall.gcode");
        c.AppendRecord("r1"); c.AppendRecord("r2");
        c.SetDestination(Dest);
        Assert.True(c.SetKeepAll(true));
        Assert.Equal(LogRetentionState.Syncing, Only(c).State);
        Assert.True(c.SyncOnce());
        // the records captured BEFORE the toggle are at the destination
        Assert.Equal("r1\nr2\n", fs.Text(Dest + "/" + id + "/print.csv.partial"));
        Assert.Equal(LogRetentionState.PermanentRecording, Only(c).State);
        Assert.Equal("Recording", c.Snapshot().Badge);

        c.AppendRecord("r3");
        Assert.Equal(LogRetentionState.Syncing, Only(c).State);
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        var job = Only(c);
        Assert.Equal(LogRetentionState.Saved, job.State);
        Assert.Equal("r1\nr2\nr3\n", fs.Text(Dest + "/" + id + "/print.csv"));
        Assert.False(fs.FileExists(Dest + "/" + id + "/print.csv.partial"));
        Assert.DoesNotContain(Root + "/" + id, fs.Dirs);   // staging removed only after verification
        Assert.Equal("Saved", c.Snapshot().Badge);
        // a new job under keep-all is permanent from the start
        string next = c.BeginJob("second.gcode");
        Assert.True(c.Snapshot().Current!.Permanent);
        Assert.NotEqual(id, next);
    }

    [Fact]
    public void Network_loss_keeps_every_local_record_and_retry_resumes_without_duplicate_rows()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("job.gcode");
        c.AppendRecord("a");
        Assert.True(c.SyncOnce());

        // the share disappears: the append lands but the connection resets afterwards
        fs.AmbiguousAppend = p => p.StartsWith(Dest, StringComparison.Ordinal);
        c.AppendRecord("b");
        Assert.False(c.SyncOnce());
        var waiting = Only(c);
        Assert.Equal(LogRetentionState.WaitingToSync, waiting.State);
        Assert.Contains("connection reset", waiting.LastError);
        Assert.Equal("Waiting to sync", c.Snapshot().Badge);
        Assert.NotNull(c.Snapshot().Attention);

        // local recording continues regardless
        fs.FailWrite = p => p.StartsWith(Dest, StringComparison.Ordinal);
        fs.AmbiguousAppend = _ => false;
        c.AppendRecord("c");
        Assert.Equal("a\nb\nc\n", fs.Text(Root + "/" + id + "/print.csv"));
        c.Retry();
        Assert.False(c.SyncOnce());   // still gone
        Assert.Equal(LogRetentionState.WaitingToSync, Only(c).State);

        // share is back: resume from what actually landed - "b" is not sent twice
        fs.FailWrite = _ => false;
        c.Retry();
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(c).State);
        Assert.Equal("a\nb\nc\n", fs.Text(Dest + "/" + id + "/print.csv"));
    }

    [Fact]
    public void Changing_the_destination_replays_the_whole_buffer_and_collisions_get_a_suffix()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("job.gcode");
        c.AppendRecord("a"); c.AppendRecord("b");
        Assert.True(c.SyncOnce());
        Assert.Equal("a\nb\n", fs.Text(Dest + "/" + id + "/print.csv.partial"));

        // somebody else owns the same directory name at the new place
        const string other = "//nas/archive";
        fs.CreateDirectory(other + "/" + id);
        fs.WriteTextAtomic(other + "/" + id + "/" + LogRetentionCoordinator.OwnerMarker, "someone-else");
        c.SetDestination(other);
        Assert.Equal(0, Only(c).SyncedBytes);
        Assert.True(c.SyncOnce());
        Assert.Equal("a\nb\n", fs.Text(other + "/" + id + "-2/print.csv.partial"));
        Assert.Equal("someone-else", fs.Text(other + "/" + id + "/" + LogRetentionCoordinator.OwnerMarker));
        Assert.Equal(other + "/" + id + "-2", Only(c).DestinationDirectory);
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(c).State);
        Assert.Equal("a\nb\n", fs.Text(other + "/" + id + "-2/print.csv"));
    }

    [Fact]
    public void Save_this_job_promotes_a_temporary_active_or_complete_job()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        string id = c.BeginJob("job.gcode");
        c.AppendRecord("a");
        Assert.True(c.SaveJob(id));
        Assert.Equal(LogRetentionState.Syncing, Only(c).State);
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.PermanentRecording, Only(c).State);
        c.AppendRecord("b");
        c.CompleteJob();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(c).State);
        Assert.Equal("a\nb\n", fs.Text(Dest + "/" + id + "/print.csv"));

        // a completed temporary job saved later
        string second = c.BeginJob("second.gcode");
        c.AppendRecord("x");
        c.CompleteJob();
        Assert.Equal(LogRetentionState.TemporaryComplete, c.Snapshot().Jobs.Last().State);
        Assert.True(c.SaveJob(second));
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, c.Snapshot().Jobs.Last().State);
        Assert.Equal("x\n", fs.Text(Dest + "/" + second + "/print.csv"));
        // saving needs somewhere to save to
        var d = New(new FakeFileSystem());
        string third = d.BeginJob("third.gcode");
        Assert.False(d.SaveJob(third));
    }

    [Fact]
    public void Crash_recovery_offers_leftover_buffers_and_quarantines_a_corrupt_manifest()
    {
        var fs = new FakeFileSystem();
        var first = New(fs);
        first.SetDestination(Dest);
        string temp = first.BeginJob("temp.gcode");
        first.AppendRecord("t");
        first.CompleteJob();
        first.SetKeepAll(true);
        string perm = first.BeginJob("perm.gcode");
        first.AppendRecord("p");
        // process dies here: no CompleteJob, no Shutdown, no sync

        fs.CreateDirectory(Root + "/broken-1");
        fs.WriteTextAtomic(Root + "/broken-1/manifest.json", "{ not json");
        fs.WriteTextAtomic(Root + "/broken-1/print.csv", "precious\n");

        var next = New(fs);
        next.SetDestination(Dest);
        next.Recover();
        var jobs = next.Snapshot().Jobs;
        Assert.Equal(2, jobs.Count);
        var recoveredTemp = jobs.Single(j => j.Id == temp);
        Assert.Equal(LogRetentionState.RecoveredTemporary, recoveredTemp.State);
        Assert.True(recoveredTemp.Recovered);
        var recoveredPerm = jobs.Single(j => j.Id == perm);
        Assert.Equal(LogRetentionState.Syncing, recoveredPerm.State);
        Assert.True(recoveredPerm.Complete);
        // the corrupt one is moved aside intact, never deleted
        Assert.DoesNotContain(Root + "/broken-1", fs.Dirs);
        string quarantined = fs.Dirs.Single(d => d.StartsWith(Root + "/quarantine/broken-1", StringComparison.Ordinal));
        Assert.Equal("precious\n", fs.Text(quarantined + "/print.csv"));
        Assert.Contains(next.Notes, n => n.StartsWith("quarantined broken-1", StringComparison.Ordinal));

        // the permanent one resumes saving; the temporary one goes on the next clean shutdown
        Assert.True(next.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, next.Snapshot().Jobs.Single(j => j.Id == perm).State);
        Assert.Equal("p\n", fs.Text(Dest + "/" + perm + "/print.csv"));
        next.Shutdown();
        Assert.All(next.Snapshot().Jobs, j => Assert.Equal(LogRetentionState.Saved, j.State));   // only the saved one is listed
        Assert.DoesNotContain(Root + "/" + temp, fs.Dirs);
        Assert.Contains(quarantined, fs.Dirs);
    }

    [Fact]
    public void A_recovered_temporary_log_can_be_saved()
    {
        var fs = new FakeFileSystem();
        var first = New(fs);
        string id = first.BeginJob("temp.gcode");
        first.AppendRecord("t");
        var next = New(fs);
        next.Recover();
        Assert.Equal(LogRetentionState.RecoveredTemporary, Only(next).State);
        next.SetDestination(Dest);
        Assert.True(next.SaveJob(id));
        Assert.True(next.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(next).State);
        Assert.Equal("t\n", fs.Text(Dest + "/" + id + "/print.csv"));
    }

    [Fact]
    public void Staging_failure_is_a_fault_that_never_reports_saved()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("job.gcode");
        c.AppendRecord("a");
        fs.FailWrite = p => p.StartsWith(Root, StringComparison.Ordinal) && p.EndsWith("print.csv", StringComparison.Ordinal);
        c.AppendRecord("b");   // disk full
        var job = Only(c);
        Assert.Equal(LogRetentionState.Faulted, job.State);
        Assert.Contains("local staging failed", job.LastError);
        Assert.Equal("Logging fault", c.Snapshot().Badge);
        c.CompleteJob();
        Assert.False(c.SyncOnce());
        Assert.NotEqual(LogRetentionState.Saved, Only(c).State);
        Assert.False(c.SaveJob(id));
    }

    [Fact]
    public void Finalisation_verifies_the_destination_before_the_staging_buffer_goes()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        string id = c.BeginJob("job.gcode");
        c.AppendRecord("a");
        c.CompleteJob();
        // the rename cannot happen: the job must not be reported Saved and staging must stay
        fs.FailWrite = p => p == Dest + "/" + id + "/print.csv";
        Assert.True(c.SyncOnce());   // bytes moved to .partial
        Assert.Equal(LogRetentionState.WaitingToSync, Only(c).State);
        Assert.Contains(Root + "/" + id, fs.Dirs);
        fs.FailWrite = _ => false;
        c.Retry();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(c).State);
        Assert.DoesNotContain(Root + "/" + id, fs.Dirs);
        Assert.Equal("a\n", fs.Text(Dest + "/" + id + "/print.csv"));
        Assert.Equal(id, JobLogManifest.Parse(fs.Text(Dest + "/" + id + "/manifest.json")).Id);
    }

    [Fact]
    public void Discard_is_explicit_and_never_the_active_job()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        string a = c.BeginJob("a.gcode");
        Assert.False(c.Discard(a));
        c.CompleteJob();
        Assert.Contains(Root + "/" + a, fs.Dirs);
        Assert.True(c.Discard(a));
        Assert.DoesNotContain(Root + "/" + a, fs.Dirs);
        Assert.Empty(c.Snapshot().Jobs);
    }

    [Fact]
    public void Export_copy_writes_the_staged_csv_elsewhere()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        string id = c.BeginJob("a.gcode");
        c.AppendRecord("h"); c.AppendRecord("1");
        c.ExportCopy(id, "C:/out/copy.csv");
        Assert.Equal("h\n1\n", fs.Text("C:/out/copy.csv"));
    }

    [Fact]
    public void Manifest_and_settings_round_trip_invariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var m = new JobLogManifest
            {
                Id = "20260907-100000-0001ab",
                JobName = "Wand \"A\"\\1.gcode",
                StartedUtc = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc),
                State = LogRetentionState.Syncing,
                Permanent = true,
                Destination = @"\\nas\prints",
                DestinationDirectory = @"\\nas\prints\x",
                PrintOffset = 123456789012,
                EventsOffset = 7,
                LastError = "n/a",
            };
            var back = JobLogManifest.Parse(m.ToJson());
            Assert.Equal(m.Id, back.Id); Assert.Equal(m.JobName, back.JobName); Assert.Equal(m.StartedUtc, back.StartedUtc);
            Assert.Equal(m.State, back.State); Assert.Equal(m.Destination, back.Destination);
            Assert.Equal(m.PrintOffset, back.PrintOffset); Assert.Equal(m.EventsOffset, back.EventsOffset);
            Assert.Throws<FormatException>(() => JobLogManifest.Parse("{\"version\":99,\"id\":\"x\"}"));
            Assert.Throws<FormatException>(() => JobLogManifest.Parse("nope"));

            var s = new ConsoleSettings { KeepAllJobLogs = true, Destination = @"\\nas\prints", ReducedMotion = true, WindowBounds = new[] { 10, 20, 960, 700 } };
            var sb = ConsoleSettings.Parse(s.ToJson());
            Assert.True(sb.KeepAllJobLogs); Assert.Equal(s.Destination, sb.Destination); Assert.True(sb.ReducedMotion);
            Assert.Equal(new[] { 10, 20, 960, 700 }, sb.WindowBounds);
            Assert.Contains("\"windowBounds\":[10,20,960,700]", s.ToJson());   // invariant numbers
            Assert.Contains("\"printOffset\":123456789012", m.ToJson());
            // keep-all without a destination is impossible and is corrected on read
            Assert.False(ConsoleSettings.Parse("{\"version\":1,\"keepAllJobLogs\":true}").KeepAllJobLogs);
            var v2 = ConsoleSettings.Parse("{\"version\":2,\"reducedMotion\":true,\"presets\":[{\"name\":\"PLA\",\"hotendC\":205}]}");
            Assert.True(v2.ReducedMotion); // obsolete presets are silently ignored
            Assert.False(ConsoleSettings.TryParse("garbage", out var fallback, out string? error));
            Assert.False(fallback.KeepAllJobLogs); Assert.Null(fallback.Destination); Assert.NotNull(error);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    // ---- D1: measurable logging acceptance (checklist rows 10a.11, 10a.12) ----------------

    /// <summary>A manifest written before version 4 says nothing about dropped records.
    /// Its zeros must therefore read as "not recorded", and the ONLY thing that tells a
    /// reader which it is looking at is the version the manifest was written with - so
    /// Parse has to preserve that version rather than silently promoting it.</summary>
    [Fact]
    public void A_version_3_manifest_still_parses_and_proves_nothing_about_drops()
    {
        const string v3 = "{\"version\":3,\"id\":\"20260909-130420-2b732c\",\"jobName\":\"halt.gcode\","
                        + "\"startedUtc\":\"2026-09-09T13:04:20.0000000Z\",\"completedUtc\":null,\"state\":\"Saved\","
                        + "\"permanent\":true,\"complete\":true,\"outcome\":\"Halted\",\"printOffset\":42,"
                        + "\"gcodeSha256\":\"abc\",\"gcodeBytes\":7,\"mechanism\":\"Scara\",\"finalized\":true}";
        var m = JobLogManifest.Parse(v3);
        Assert.Equal(3, m.Version);
        Assert.False(m.DropCountsRecorded);   // 0 here means "not recorded"
        Assert.Equal(0, m.RowsDropped);
        Assert.Equal(0, m.DiagnosticsDropped);
        Assert.Equal(0, m.SessionDiagnosticsDropped);
        Assert.Equal("Halted", m.Outcome);
        // the older version survives a round trip: promoting it would turn "unknown" into
        // a proven zero, which is exactly the claim the 2026-09-09 run could not support
        Assert.Equal(3, JobLogManifest.Parse(m.ToJson()).Version);
        Assert.False(JobLogManifest.Parse(m.ToJson()).DropCountsRecorded);
    }

    [Fact]
    public void A_version_4_manifest_round_trips_the_three_drop_counters()
    {
        var m = new JobLogManifest
        {
            Id = "20260909-130420-2b732c",
            StartedUtc = new DateTime(2026, 9, 9, 13, 4, 20, DateTimeKind.Utc),
            State = LogRetentionState.Saved,
            RowsDropped = 12,
            DiagnosticsDropped = 3400,
            SessionDiagnosticsDropped = 7,
        };
        Assert.Equal(4, JobLogManifest.CurrentVersion);
        Assert.Equal(4, m.Version);
        Assert.Contains("\"rowsDropped\":12", m.ToJson());
        Assert.Contains("\"diagnosticsDropped\":3400", m.ToJson());
        Assert.Contains("\"sessionDiagnosticsDropped\":7", m.ToJson());
        var back = JobLogManifest.Parse(m.ToJson());
        Assert.True(back.DropCountsRecorded);
        Assert.Equal(12, back.RowsDropped);
        Assert.Equal(3400, back.DiagnosticsDropped);
        Assert.Equal(7, back.SessionDiagnosticsDropped);
    }

    /// <summary>The service's counters are process-lifetime totals; a manifest must carry
    /// the DELTA of its own run. Otherwise a long session attributes every earlier drop to
    /// the last print, and the last print looks worse the longer Rhino has been open.</summary>
    [Fact]
    public void Each_run_records_its_own_drop_delta_not_the_session_total()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);

        c.ObserveDropTotals(0, 0, 0);
        string first = c.BeginJob("a.gcode");
        c.AppendRecord("r1");
        c.ObserveDropTotals(3, 1, 0);      // this run lost 3 rows and 1 diagnostic line
        c.CompleteJob();
        Assert.True(c.SyncOnce());

        string second = c.BeginJob("b.gcode");
        c.AppendRecord("r2");
        c.ObserveDropTotals(7, 4, 2);      // session totals; this run's share is 4 / 3 / 2
        c.CompleteJob();
        Assert.True(c.SyncOnce());

        var a = JobLogManifest.Parse(fs.Text(Dest + "/" + first + "/manifest.json"));
        var b = JobLogManifest.Parse(fs.Text(Dest + "/" + second + "/manifest.json"));
        Assert.True(a.DropCountsRecorded);
        Assert.Equal(3, a.RowsDropped); Assert.Equal(1, a.DiagnosticsDropped); Assert.Equal(0, a.SessionDiagnosticsDropped);
        Assert.Equal(4, b.RowsDropped); Assert.Equal(3, b.DiagnosticsDropped); Assert.Equal(2, b.SessionDiagnosticsDropped);
    }

    /// <summary>Row 10a.12: "look in the staging folder afterwards" can never prove that a
    /// run recorded anything, because a successful final copy REMOVES the staging folder.
    /// The snapshot is therefore taken while the run records, and stays readable after.</summary>
    [Fact]
    public void The_staging_snapshot_is_taken_while_recording_and_survives_finalisation()
    {
        var fs = new FakeFileSystem();
        var c = New(fs);
        c.SetDestination(Dest);
        c.SetKeepAll(true);
        Assert.Null(c.StagingHealth());   // nothing is recording yet

        c.ObserveDropTotals(2, 5, 1);
        string id = c.BeginJob("halt.gcode");
        c.AppendRecord("r1");
        c.AppendDiagnostic("{\"reply\":1}");
        c.AppendDiagnostic("{\"reply\":2}");

        var health = c.StagingHealth();
        Assert.NotNull(health);
        Assert.Equal(id, health!.RunId);
        Assert.Equal(Root + "/" + id, health.StagingDirectory);
        Assert.Equal(Root + "/" + id + "/diagnostics.jsonl", health.DiagnosticsPath);
        Assert.True(health.DiagnosticsPresent);
        Assert.Equal(fs.Text(Root + "/" + id + "/diagnostics.jsonl").Length, health.DiagnosticsBytes);
        Assert.True(health.DiagnosticsBytes > 0);
        Assert.Equal(2, health.RowsDropped);
        Assert.Equal(5, health.DiagnosticsDropped);
        Assert.Equal(1, health.SessionDiagnosticsDropped);
        Assert.Contains("diagnostics.jsonl", health.Describe());
        long bytes = health.DiagnosticsBytes;

        c.CompleteJob();
        Assert.True(c.SyncOnce());
        Assert.Equal(LogRetentionState.Saved, Only(c).State);
        Assert.DoesNotContain(Root + "/" + id, fs.Dirs);   // staging is gone, as designed
        Assert.Null(c.StagingHealth());                    // nothing is recording any more
        // the snapshot taken during the run is immutable and still says what was written
        Assert.True(health.DiagnosticsPresent);
        Assert.Equal(bytes, health.DiagnosticsBytes);
        Assert.Equal(bytes, fs.Text(Dest + "/" + id + "/diagnostics.jsonl").Length);
    }
}
