using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Pins the pure state core of the Print Console: the reducer that folds
/// session facts into an immutable state, the button gate, and the output
/// snapshot whose equality decides whether the canvas gets expired at all.</summary>
public class PrintConsoleStateTests
{
    const string Host = "192.168.1.10";
    const string JobPath = "0:/gcodes/print.gcode";
    static readonly DateTime T0 = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    static ObjectModelSnapshot Snap(MachineStatus status = MachineStatus.Idle,
                                    bool x = true, bool y = true, bool z = true,
                                    JobState? job = null, string? reply = null,
                                    DateTime? taken = null)
        => new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            TakenUtc = taken ?? T0,
            AllHomed = x && y && z,
            LastReply = reply,
            Job = job,
            Axes = new[]
            {
                new AxisState { Letter = "X", MachinePosition = 1, Homed = x },
                new AxisState { Letter = "Y", MachinePosition = 2, Homed = y },
                new AxisState { Letter = "Z", MachinePosition = 3, Homed = z },
            },
        };

    static ConsoleFacts Facts(ConsoleConnection connection, ObjectModelSnapshot? snap = null,
                              IReadOnlyList<string>? log = null, string? reply = null,
                              string? error = null, bool arm = true, long? seq = null)
        => new ConsoleFacts
        {
            Connection = connection,
            Snapshot = snap,
            Log = log ?? Array.Empty<string>(),
            LogSequence = seq ?? (log?.Count ?? 0),
            LastReply = reply,
            LastError = error,
            ArmConfigurationKnown = arm,
        };

    /// <summary>Connected, idle, all axes homed, arm configuration known, gcode
    /// uploaded with the matching hash - every Start precondition satisfied.</summary>
    static PrintConsoleState Ready()
    {
        var s = PrintConsoleState.Empty(Host);
        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap()));
        s = PrintConsoleReducer.GcodeChanged(s, 120, 0xABCD);
        s = PrintConsoleReducer.Uploaded(s, new UploadRecord(JobPath, 0xABCD, 4096, 1));
        s = PrintConsoleReducer.ActionFinished(s, "uploaded " + JobPath, null);
        return s;
    }

    // ---- (1) connection sequence + per-axis homed flags ----

    [Fact]
    public void Facts_sequence_tracks_connection_and_per_axis_homed_flags()
    {
        var s = PrintConsoleState.Empty(Host);
        Assert.Equal(Host, s.Host);
        Assert.Equal(ConsoleConnection.None, s.Connection);
        Assert.Null(s.SnapshotUtc);

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Disconnected));
        Assert.Equal(ConsoleConnection.Disconnected, s.Connection);

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connecting));
        Assert.Equal(ConsoleConnection.Connecting, s.Connection);
        Assert.Equal(MachineStatus.Unknown, s.MachineState);

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(x: true, y: true, z: false)));
        Assert.Equal(ConsoleConnection.Connected, s.Connection);
        Assert.True(s.XHomed);
        Assert.True(s.YHomed);
        Assert.False(s.ZHomed);
        Assert.False(s.AllHomed);
        Assert.Equal(MachineStatus.Idle, s.MachineState);
        Assert.Equal("idle", s.RawStatus);
        Assert.Equal(T0, s.SnapshotUtc);
        Assert.True(s.ArmConfigurationKnown);

        Assert.NotNull(s.Position);
        Assert.Equal(1, s.Position!.Value.X);
        Assert.Equal(2, s.Position.Value.Y);
        Assert.Equal(3, s.Position.Value.Z);
    }

    [Fact]
    public void Null_snapshot_keeps_machine_fields_but_sets_connection()
    {
        var job = new JobState { FileName = JobPath, FractionPrinted = 0.5, Layer = 3 };
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(MachineStatus.Processing, job: job)));

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Reconnecting, snap: null));

        Assert.Equal(ConsoleConnection.Reconnecting, s.Connection);
        Assert.Equal(MachineStatus.Processing, s.MachineState);
        Assert.Equal(JobPath, s.JobFile);
        Assert.Equal(0.5, s.Progress);
        Assert.Equal(3, s.Layer);
        Assert.True(s.XHomed && s.YHomed && s.ZHomed && s.AllHomed);
        Assert.Equal(T0, s.SnapshotUtc);
    }

    /// <summary>4.4.0 review: the shared Num.Clamp01 now propagates NaN, so the reducer
    /// keeps its own NaN-to-zero guard on the job fraction - a firmware poll without a
    /// usable fraction reads as "no progress", never as NaN in the console.</summary>
    [Fact]
    public void NaN_job_fraction_reads_as_zero_progress()
    {
        var job = new JobState { FileName = JobPath, FractionPrinted = double.NaN, Layer = 1 };
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(MachineStatus.Processing, job: job)));

        Assert.Equal(0.0, s.Progress);
        Assert.Equal(1, s.Layer);
    }

    // ---- (2) job fields ----

    [Fact]
    public void Job_facts_map_file_progress_and_layer_and_null_job_resets_them()
    {
        var job = new JobState { FileName = JobPath, FractionPrinted = 0.25, Layer = 7 };
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(MachineStatus.Processing, job: job)));

        Assert.Equal(JobPath, s.JobFile);
        Assert.Equal(0.25, s.Progress);
        Assert.Equal(7, s.Layer);

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(MachineStatus.Idle)));
        Assert.Equal("", s.JobFile);
        Assert.Equal(0.0, s.Progress);
        Assert.Null(s.Layer);
    }

    [Fact]
    public void Progress_is_clamped_to_unit_interval()
    {
        var job = new JobState { FileName = JobPath, FractionPrinted = 1.7 };
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(MachineStatus.Processing, job: job)));
        Assert.Equal(1.0, s.Progress);
    }

    // ---- (3) reply + log tail ----

    [Fact]
    public void Log_tail_keeps_last_200_and_clear_log_skips_older_entries()
    {
        var log = Enumerable.Range(0, 250).Select(i => "line " + i).ToList();
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(), log: log, reply: "ok"));

        Assert.Equal("ok", s.LastReply);
        Assert.Equal(200, s.LogTail.Count);
        Assert.Equal("line 50", s.LogTail[0]);
        Assert.Equal("line 249", s.LogTail[199]);

        s = PrintConsoleReducer.ClearLog(s, log.Count);
        Assert.Empty(s.LogTail);
        Assert.Equal(250L, s.LogSkip);

        var log2 = log.Concat(Enumerable.Range(250, 5).Select(i => "line " + i)).ToList();
        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(), log: log2));
        Assert.Equal(5, s.LogTail.Count);
        Assert.Equal("line 250", s.LogTail[0]);
        Assert.Equal("line 254", s.LogTail[4]);
    }

    [Fact]
    public void Log_tail_survives_the_sessions_capped_log_via_sequence_numbers()
    {
        // The session keeps only its last 200 lines; 1000 have been appended so far.
        var log = Enumerable.Range(800, 200).Select(i => "line " + i).ToList();
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(), log: log, seq: 1000));
        Assert.Equal(200, s.LogTail.Count);
        Assert.Equal("line 800", s.LogTail[0]);

        s = PrintConsoleReducer.ClearLog(s, 1000);
        Assert.Empty(s.LogTail);
        Assert.Equal(1000L, s.LogSkip);

        // Five more lines arrived; the session dropped its five oldest.
        var log2 = Enumerable.Range(805, 200).Select(i => "line " + i).ToList();
        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(), log: log2, seq: 1005));
        Assert.Equal(5, s.LogTail.Count);
        Assert.Equal("line 1000", s.LogTail[0]);
        Assert.Equal("line 1004", s.LogTail[4]);

        // Skip beyond the retained window (session restarted with a shorter log) shows nothing.
        var shortLog = new[] { "a", "b" };
        var t = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(), log: shortLog, seq: 2));
        Assert.Empty(t.LogTail);
    }

    [Fact]
    public void Reply_falls_back_to_snapshot_reply_and_error_is_sticky_until_action_starts()
    {
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host),
            Facts(ConsoleConnection.Connected, Snap(reply: "from-snapshot"), reply: null, error: "poll failed"));
        Assert.Equal("from-snapshot", s.LastReply);
        Assert.Equal("poll failed", s.LastError);

        s = PrintConsoleReducer.Reduce(s, Facts(ConsoleConnection.Connected, Snap(), reply: null, error: null));
        Assert.Equal("poll failed", s.LastError);

        s = PrintConsoleReducer.ActionStarted(s, "home");
        Assert.Null(s.LastError);
    }

    // ---- (4) actions ----

    [Fact]
    public void ActionStarted_sets_pending_and_ActionFinished_reports_result_or_error()
    {
        var s = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host), Facts(ConsoleConnection.Connected, Snap()));

        s = PrintConsoleReducer.ActionStarted(s, "upload");
        Assert.Equal("upload", s.PendingAction);
        Assert.Null(s.LastError);

        s = PrintConsoleReducer.ActionFinished(s, "uploaded " + JobPath, null);
        Assert.Null(s.PendingAction);
        Assert.Equal("uploaded " + JobPath, s.LastResult);
        Assert.Null(s.LastError);

        s = PrintConsoleReducer.ActionStarted(s, "start");
        Assert.Equal("start", s.PendingAction);
        s = PrintConsoleReducer.ActionFinished(s, null, "HTTP 500");
        Assert.Null(s.PendingAction);
        Assert.Equal("HTTP 500", s.LastError);
        Assert.StartsWith("ERROR:", s.LastResult);
        Assert.Contains("HTTP 500", s.LastResult);
    }

    // ---- (5) upload record vs gcode hash ----

    [Fact]
    public void GcodeChanged_clears_upload_only_when_hash_differs()
    {
        var s = PrintConsoleReducer.GcodeChanged(PrintConsoleState.Empty(Host), 100, 0xABCD);
        Assert.Equal(100, s.GcodeLines);
        Assert.Equal(0xABCDUL, s.GcodeHash);

        var rec = new UploadRecord(JobPath, 0xABCD, 4096, 1);
        s = PrintConsoleReducer.Uploaded(s, rec);
        Assert.Same(rec, s.Uploaded);
        Assert.Equal(JobPath, rec.Path);
        Assert.Equal(4096, rec.Size);
        Assert.Equal(1, rec.Generation);

        var same = PrintConsoleReducer.GcodeChanged(s, 100, 0xABCD);
        Assert.Same(rec, same.Uploaded);

        var other = PrintConsoleReducer.GcodeChanged(s, 101, 0xBEEF);
        Assert.Null(other.Uploaded);
        Assert.Equal(101, other.GcodeLines);
        Assert.Equal(0xBEEFUL, other.GcodeHash);
    }

    // ---- (6) buttons ----

    [Fact]
    public void Buttons_ready_state_enables_start_not_pause()
    {
        var b = PrintConsoleButtons.Enabled(Ready(), hasHost: true, hasGcode: true, hasPoint: true, gotoSupported: true);
        Assert.True(b.Start);
        Assert.False(b.Pause);
        Assert.True(b.Home);
        Assert.True(b.HomeX);
        Assert.True(b.HomeY);
        Assert.True(b.HomeZ);
        Assert.True(b.Upload);
        Assert.True(b.GoTo);
        Assert.True(b.EStop);
        Assert.False(b.Connect);
        Assert.False(b.Resume);
        Assert.False(b.Stop);
        Assert.False(b.Reset);
        Assert.False(b.Reasons.ContainsKey("start"));
        Assert.True(b.Reasons.ContainsKey("pause"));
        Assert.True(b.Reasons.ContainsKey("connect"));
    }

    [Fact]
    public void Buttons_processing_enables_pause_and_stop_not_start_or_home()
    {
        var s = Ready().WithMachine(MachineStatus.Processing, "processing");
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true);
        Assert.True(b.Pause);
        Assert.True(b.Stop);
        Assert.False(b.Start);
        Assert.False(b.Home);
        Assert.False(b.HomeX);
        Assert.False(b.GoTo);
        Assert.True(b.Upload);
        Assert.True(b.EStop);
        Assert.False(b.Resume);
        Assert.False(b.Reset);
        Assert.True(b.Reasons.ContainsKey("start"));
    }

    [Fact]
    public void Buttons_paused_enables_resume_and_stop()
    {
        var s = Ready().WithMachine(MachineStatus.Paused, "paused");
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true);
        Assert.True(b.Resume);
        Assert.True(b.Stop);
        Assert.False(b.Pause);
        Assert.False(b.Start);
    }

    [Theory]
    [InlineData(MachineStatus.Halted)]
    [InlineData(MachineStatus.Off)]
    public void Buttons_halted_or_off_enables_reset(MachineStatus st)
    {
        var s = Ready().WithMachine(st, st.ToString().ToLowerInvariant());
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true);
        Assert.True(b.Reset);
        Assert.False(b.Start);
        Assert.False(b.Home);
        Assert.True(b.EStop);
    }

    [Fact]
    public void Buttons_no_session_disables_everything_including_estop()
    {
        var b = PrintConsoleButtons.Enabled(PrintConsoleState.Empty(Host), hasHost: false, hasGcode: true, hasPoint: true, gotoSupported: true);
        Assert.False(b.Connect);
        Assert.False(b.Home); Assert.False(b.HomeX); Assert.False(b.HomeY); Assert.False(b.HomeZ);
        Assert.False(b.Upload); Assert.False(b.Start); Assert.False(b.Pause); Assert.False(b.Resume);
        Assert.False(b.Stop); Assert.False(b.EStop); Assert.False(b.Reset); Assert.False(b.GoTo);
        // 3.0.0 ids absorbed from the five consolidated components.
        Assert.False(b.Jog); Assert.False(b.Capture); Assert.False(b.Send); Assert.False(b.Disconnect);
        Assert.Equal(18, b.Reasons.Count);   // 17 + gotostart (2026-09-16)
        Assert.False(string.IsNullOrEmpty(b.Reasons["estop"]));
        foreach (string id in new[] { "jog", "capture", "send", "disconnect", "gotostart" })
            Assert.False(string.IsNullOrEmpty(b.Reasons[id]));
    }

    [Fact]
    public void Buttons_connect_needs_host_and_a_connectable_state()
    {
        var none = PrintConsoleState.Empty(Host);
        Assert.True(PrintConsoleButtons.Enabled(none, hasHost: true, hasGcode: false, hasPoint: false, gotoSupported: true).Connect);

        var error = none.WithConnection(ConsoleConnection.Error);
        Assert.True(PrintConsoleButtons.Enabled(error, true, false, false, true).Connect);
        Assert.False(PrintConsoleButtons.Enabled(error, false, false, false, true).Connect);

        var disconnected = none.WithConnection(ConsoleConnection.Disconnected);
        Assert.True(PrintConsoleButtons.Enabled(disconnected, true, false, false, true).Connect);

        var connecting = none.WithConnection(ConsoleConnection.Connecting);
        Assert.False(PrintConsoleButtons.Enabled(connecting, true, false, false, true).Connect);
        Assert.True(PrintConsoleButtons.Enabled(connecting, true, false, false, true).EStop);

        Assert.False(PrintConsoleButtons.Enabled(Ready(), true, true, true, true).Connect);
    }

    [Fact]
    public void Buttons_pending_action_disables_everything_except_estop()
    {
        var s = PrintConsoleReducer.ActionStarted(Ready(), "upload");
        var b = PrintConsoleButtons.Enabled(s, true, true, true, true);
        Assert.True(b.EStop);
        Assert.False(b.Connect); Assert.False(b.Home); Assert.False(b.HomeX); Assert.False(b.HomeY);
        Assert.False(b.HomeZ); Assert.False(b.Upload); Assert.False(b.Start); Assert.False(b.Pause);
        Assert.False(b.Resume); Assert.False(b.Stop); Assert.False(b.Reset); Assert.False(b.GoTo);
        Assert.Contains("upload", b.Reasons["start"]);
    }

    [Fact]
    public void Buttons_start_requires_matching_upload_homed_and_arm_configuration()
    {
        var ready = Ready();

        var stale = ready.WithUploaded(new UploadRecord(JobPath, 0x1234, 4096, 1));
        Assert.False(PrintConsoleButtons.Enabled(stale, true, true, true, true).Start);

        var none = ready.WithUploaded(null);
        Assert.False(PrintConsoleButtons.Enabled(none, true, true, true, true).Start);

        var unhomed = PrintConsoleReducer.Reduce(ready, Facts(ConsoleConnection.Connected, Snap(z: false)));
        var bu = PrintConsoleButtons.Enabled(unhomed, true, true, true, true);
        Assert.False(bu.Start);
        Assert.False(bu.GoTo);
        Assert.True(bu.Home);

        var noArm = PrintConsoleReducer.Reduce(ready, Facts(ConsoleConnection.Connected, Snap(), arm: false));
        var ba = PrintConsoleButtons.Enabled(noArm, true, true, true, true);
        Assert.False(ba.Start);
        Assert.False(ba.GoTo);
        Assert.True(ba.Home);

        Assert.False(PrintConsoleButtons.Enabled(ready, true, hasGcode: false, hasPoint: true, gotoSupported: true).Upload);
        Assert.False(PrintConsoleButtons.Enabled(ready, true, true, hasPoint: false, gotoSupported: true).GoTo);
    }

    [Fact]
    public void Buttons_start_accepts_an_unhomed_machine_and_an_unknown_arm_when_the_job_homes_first()
    {
        // D9 (2026-09-09): a validated job whose first motion is a full G28 homes the
        // machine itself; cancel.g and the job footer leave the axes unhomed, and the
        // maintainer refused a forced second homing before every print.
        var ready = Ready();
        var homesFirst = new ValidationProjection(ready.GcodeHash, false, null, null, false, homesFirst: true);

        var unhomed = PrintConsoleReducer.Reduce(ready, Facts(ConsoleConnection.Connected, Snap(z: false)))
                                         .WithValidation(homesFirst);
        var bu = PrintConsoleButtons.Enabled(unhomed, true, true, true, true);
        Assert.True(bu.Start);
        Assert.False(bu.GoTo);      // go-to keeps its homing gate
        Assert.False(bu.Jog);       // so does jogging

        var noArm = PrintConsoleReducer.Reduce(ready, Facts(ConsoleConnection.Connected, Snap(), arm: false))
                                       .WithValidation(homesFirst);
        Assert.True(PrintConsoleButtons.Enabled(noArm, true, true, true, true).Start);

        // a job that does NOT home first, or a refused one, keeps both requirements
        var plain = unhomed.WithValidation(new ValidationProjection(ready.GcodeHash, false, null, null, false));
        Assert.False(PrintConsoleButtons.Enabled(plain, true, true, true, true).Start);
        var refused = unhomed.WithValidation(new ValidationProjection(ready.GcodeHash, true, null, null, false, homesFirst: true));
        Assert.False(PrintConsoleButtons.Enabled(refused, true, true, true, true).Start);
    }

    [Fact]
    public void Buttons_gotostart_needs_a_validation_that_names_a_start()
    {
        var ready = Ready();
        var none = PrintConsoleButtons.Enabled(ready, hasHost: true, hasGcode: true, hasPoint: false, gotoSupported: true);
        Assert.False(none.GoToStart);
        Assert.Equal("validate the job first", none.Reasons["gotostart"]);

        var noStart = ready.WithValidation(new ValidationProjection(ready.GcodeHash, false, null, null, false));
        var b0 = PrintConsoleButtons.Enabled(noStart, true, true, false, true);
        Assert.False(b0.GoToStart);
        Assert.Equal("the job has no extruding move", b0.Reasons["gotostart"]);

        var refused = ready.WithValidation(new ValidationProjection(ready.GcodeHash, true, null, null, false,
                                                                    startPoint: new Vec3(600, 0, 5)));
        var b1 = PrintConsoleButtons.Enabled(refused, true, true, false, true);
        Assert.False(b1.GoToStart);
        Assert.Equal("validation refused the job", b1.Reasons["gotostart"]);

        var ok = ready.WithValidation(new ValidationProjection(ready.GcodeHash, false, null, null, false,
                                                               startPoint: new Vec3(600, 0, 5)));
        var b2 = PrintConsoleButtons.Enabled(ok, true, true, false, true);
        Assert.True(b2.GoToStart);
        Assert.False(b2.Reasons.ContainsKey("gotostart"));
        Assert.False(b2.GoTo);   // the point go-to still wants a selected point
    }

    [Fact]
    public void Buttons_gotostart_keeps_the_goto_machine_gates()
    {
        var ready = Ready();
        var ok = ready.WithValidation(new ValidationProjection(ready.GcodeHash, false, null, null, false,
                                                               startPoint: new Vec3(600, 0, 5)));
        var cartesian = PrintConsoleButtons.Enabled(ok, true, true, false, gotoSupported: false);
        Assert.False(cartesian.GoToStart);
        Assert.Equal("go-to is available for the SCARA profile only", cartesian.Reasons["gotostart"]);

        var processing = PrintConsoleButtons.Enabled(ok.WithMachine(MachineStatus.Processing, "processing"), true, true, false, true);
        Assert.False(processing.GoToStart);
        Assert.StartsWith("machine not idle", processing.Reasons["gotostart"]);

        var unhomed = PrintConsoleButtons.Enabled(
            PrintConsoleReducer.Reduce(ok, Facts(ConsoleConnection.Connected, Snap(z: false))), true, true, false, true);
        Assert.False(unhomed.GoToStart);
        Assert.Equal("not homed", unhomed.Reasons["gotostart"]);
    }

    [Fact]
    public void Buttons_goto_requires_a_profile_that_supports_it()
    {
        var b = PrintConsoleButtons.Enabled(Ready(), hasHost: true, hasGcode: true, hasPoint: true, gotoSupported: false);
        Assert.False(b.GoTo);
        Assert.Equal("go-to is available for the SCARA profile only", b.Reasons["goto"]);
        Assert.True(b.Start);
        Assert.True(b.Home);
    }

    // ---- (7) output snapshot equality ----

    [Fact]
    public void Outputs_ignore_layer_and_snapshot_time()
    {
        var a = Ready();
        var o = PrintConsoleOutputs.From(a);

        Assert.Equal(o, PrintConsoleOutputs.From(a.WithJob(a.JobFile, a.Progress, 99)));
        Assert.Equal(o, PrintConsoleOutputs.From(a.WithSnapshotUtc(T0.AddSeconds(5))));

        Assert.NotEqual(o, PrintConsoleOutputs.From(a.WithMachine(MachineStatus.Processing, "processing")));
        Assert.NotEqual(o, PrintConsoleOutputs.From(a.WithJob(a.JobFile, 0.5, a.Layer)));
        Assert.NotEqual(o, PrintConsoleOutputs.From(a.WithLog(new[] { "one more line" }, a.LogSkip)));
        Assert.NotEqual(o, PrintConsoleOutputs.From(PrintConsoleReducer.ActionStarted(a, "home")));
    }

    /// <summary>3.0.0 behaviour change: `reply` became output 10, so a reply change IS
    /// an output change. In 2.6.0 replies reached the canvas only via `log` and were
    /// deliberately excluded from the snapshot. The 1/s expiry throttle is what keeps
    /// this affordable - a chatty board must not re-solve the canvas per reply.</summary>
    [Fact]
    public void Outputs_include_the_reply_and_a_reply_change_expires_under_the_throttle()
    {
        var a = Ready();
        var o = PrintConsoleOutputs.From(a);
        var next = PrintConsoleOutputs.From(a.WithLastReply("ok T:24.1"));

        Assert.Equal("", o.Reply);
        Assert.Equal("ok T:24.1", next.Reply);
        Assert.NotEqual(o, next);

        // Inside the throttle window the expire is deferred, not dropped …
        var verdict = PrintConsoleExpirePolicy.Decide(o, next, live: true, lastExpireUtc: T0,
                                                      nowUtc: T0.AddMilliseconds(400), minIntervalMs: 1000,
                                                      out int delayMs);
        Assert.Equal(ExpireVerdict.Defer, verdict);
        Assert.InRange(delayMs, 1, 1000);

        // … and once the window has passed it fires.
        Assert.Equal(ExpireVerdict.Now,
            PrintConsoleExpirePolicy.Decide(o, next, live: true, lastExpireUtc: T0,
                                            nowUtc: T0.AddMilliseconds(1200), minIntervalMs: 1000, out _));

        // With `live` off nothing expires, reply or not.
        Assert.Equal(ExpireVerdict.None,
            PrintConsoleExpirePolicy.Decide(o, next, live: false, lastExpireUtc: T0,
                                            nowUtc: T0.AddMilliseconds(1200), minIntervalMs: 1000, out _));
    }

    [Fact]
    public void Outputs_expose_status_line_and_info_without_timestamps()
    {
        var a = Ready().WithSnapshotUtc(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var o = PrintConsoleOutputs.From(a);

        Assert.Equal("connected - uploaded " + JobPath, o.Status);
        Assert.Equal("idle", o.State);
        Assert.True(o.Homed);
        Assert.Equal("", o.JobFile);
        Assert.NotNull(o.Pos);
        Assert.Equal(0.0, o.Progress);
        Assert.Contains(Host, o.Info);
        Assert.Contains(JobPath, o.Info);
        Assert.Contains("4096", o.Info);
        Assert.DoesNotContain("2031", o.Info);

        var pending = PrintConsoleOutputs.From(PrintConsoleReducer.ActionStarted(a, "homing"));
        Assert.Equal("connected - homing", pending.Status);
    }

    [Fact]
    public void Buttons_start_on_a_cartesian_profile_does_not_wait_for_an_arm_configuration()
    {
        // a Cartesian machine never establishes one (DuetHoming: "no arm configuration at all"),
        // so the SCARA requirement refused every Cartesian start (Codex re-review 2026-09-03)
        var noArm = PrintConsoleReducer.Reduce(Ready(), Facts(ConsoleConnection.Connected, Snap(), arm: false));
        Assert.False(PrintConsoleButtons.Enabled(noArm, true, true, true, false).Start);
        Assert.True(PrintConsoleButtons.Enabled(noArm, true, true, true, false, armConfigurationRequired: false).Start);
        // the SCARA path is unchanged
        Assert.False(PrintConsoleButtons.Enabled(noArm, true, true, true, true, armConfigurationRequired: true).Start);
    }

    // ---- (8) 3.0.0: session facts the window's buttons must agree with ----

    /// <summary>The window must never show an enabled button that the transaction then
    /// refuses for a reason the canvas components already display.</summary>
    [Fact]
    public void Buttons_a_trust_refusal_disables_every_state_changing_action()
    {
        var facts = Facts(ConsoleConnection.Connected, Snap());
        facts.TrustReason = "machine identity not confirmed - trust this machine first";
        var s = PrintConsoleReducer.Reduce(Ready(), facts);

        var b = PrintConsoleButtons.Enabled(s, hasHost: true, hasGcode: true, hasPoint: true, gotoSupported: true);
        Assert.False(b.Home); Assert.False(b.HomeX); Assert.False(b.Upload); Assert.False(b.Start);
        Assert.False(b.Pause); Assert.False(b.Resume); Assert.False(b.Stop); Assert.False(b.GoTo);
        Assert.False(b.Jog);
        Assert.Equal(facts.TrustReason, b.Reasons["start"]);

        // Capture and Reset both run inside RunGatedCommandAsync, which trust-gates
        // (MachineSession.cs), so they are refused too - the button must say so.
        Assert.False(b.Capture);
        Assert.False(b.Reset);

        // E-STOP is the one exception: it bypasses the command queue entirely
        // (MachineSession.EmergencyStopAsync), so trust can never disable it.
        Assert.True(b.EStop);
    }

    /// <summary>Homing is the recovery path out of an unknown configuration, so an
    /// incomplete or failed macro probe must never block it - only a COMPLETED probe that
    /// found no macro does (DuetHoming.PreflightRefusal, ARCHI §15).</summary>
    [Fact]
    public void Buttons_home_is_refused_only_when_the_probe_completed_and_found_no_macro()
    {
        // Probe not finished: availability is unknown, homing stays offered.
        var unprobed = Facts(ConsoleConnection.Connected, Snap());
        unprobed.HomeProbeComplete = false;
        unprobed.HomeAvailableAll = unprobed.HomeAvailableX = false;
        var s = PrintConsoleReducer.Reduce(Ready(), unprobed);
        Assert.True(PrintConsoleButtons.Enabled(s, true, true, true, true).Home);
        Assert.True(PrintConsoleButtons.Enabled(s, true, true, true, true).HomeX);

        // Probe finished and X has no macro: only X is refused.
        var probed = Facts(ConsoleConnection.Connected, Snap());
        probed.HomeProbeComplete = true;
        probed.HomeAvailableX = false;
        var t = PrintConsoleReducer.Reduce(Ready(), probed);
        var b = PrintConsoleButtons.Enabled(t, true, true, true, true);
        Assert.False(b.HomeX);
        Assert.Contains("X", b.Reasons["homex"]);
        Assert.True(b.HomeY); Assert.True(b.HomeZ); Assert.True(b.Home);
    }

    [Fact]
    public void Facts_carry_the_board_diagnostics_into_info()
    {
        var facts = Facts(ConsoleConnection.Connected, Snap());
        facts.BoardType = "Duet 2 WiFi";
        facts.Legacy = true;
        facts.KinematicsProbed = true;
        facts.MotionBaselineProbed = false;
        var s = PrintConsoleReducer.Reduce(Ready(), facts);

        Assert.Equal("Duet 2 WiFi", s.BoardType);
        string info = PrintConsoleOutputs.From(s).Info;
        Assert.Contains("Duet 2 WiFi", info);
        Assert.Contains("legacy API", info);
        Assert.Contains("motion baseline: unverified", info);
    }

    // ---- (9) 3.0.0: the validation projection behind outputs 17-19 ----

    [Fact]
    public void Validation_projects_onto_danger_problems_and_messages()
    {
        var s = Ready();
        var v = new ValidationProjection(s.GcodeHash, danger: true,
                                         problems: new[] { new Vec3(1, 2, 3) },
                                         messages: new[] { "outside the envelope" }, warned: true);
        s = PrintConsoleReducer.ValidationCompleted(s, v);

        var o = PrintConsoleOutputs.From(s);
        Assert.True(o.Danger);
        Assert.Single(o.Problems);
        Assert.Equal("outside the envelope", Assert.Single(o.Messages));
        Assert.Contains("DANGER", o.Info);
    }

    /// <summary>A verdict computed for other gcode must never describe what is wired in
    /// now - the same rule as the component-side gate cache.</summary>
    [Fact]
    public void Validation_is_dropped_when_the_gcode_changes()
    {
        var s = Ready();
        s = PrintConsoleReducer.ValidationCompleted(s,
            new ValidationProjection(s.GcodeHash, true, new[] { new Vec3(1, 2, 3) }, new[] { "bad" }, false));
        Assert.NotNull(s.Validation);

        s = PrintConsoleReducer.GcodeChanged(s, 130, 0xBEEF);
        Assert.Null(s.Validation);

        var o = PrintConsoleOutputs.From(s);
        Assert.False(o.Danger);
        Assert.Empty(o.Problems);
        Assert.Empty(o.Messages);
    }

    [Fact]
    public void Validation_for_a_foreign_key_is_refused_rather_than_stored()
    {
        var s = Ready();
        s = PrintConsoleReducer.ValidationCompleted(s,
            new ValidationProjection(s.GcodeHash ^ 0xFF, true, null, new[] { "stale" }, false));
        Assert.Null(s.Validation);
    }

    [Fact]
    public void Validation_is_dropped_when_the_policy_key_changes()
    {
        var s = Ready();
        s = PrintConsoleReducer.ValidationCompleted(s,
            new ValidationProjection(s.GcodeHash, false, null, new[] { "ok" }, false));
        Assert.NotNull(s.Validation);
        Assert.Null(PrintConsoleReducer.ValidationPolicyChanged(s).Validation);
    }

    /// <summary>The 2.6.0 console dropped the motion-limit warned flag (`var (notice, _)`),
    /// so a successful-but-warned run rendered as a plain remark.</summary>
    [Fact]
    public void A_warned_action_is_recorded_as_warned()
    {
        var s = PrintConsoleReducer.ActionFinished(Ready(), "uploaded", null, warned: true);
        Assert.True(s.LastActionWarned);
        Assert.Contains("[warnings]", PrintConsoleOutputs.From(s).Info);

        Assert.False(PrintConsoleReducer.ActionFinished(s, "uploaded", null).LastActionWarned);
    }

    // ---- (10) 3.0.0: captures, step selection and terminal arming ----

    [Fact]
    public void Captures_and_step_selection_round_trip_into_the_outputs()
    {
        var s = PrintConsoleReducer.BedOriginCaptured(Ready(), new Vec3(10, 20, 0));
        s = PrintConsoleReducer.ZZeroCaptured(s, 4.5);

        var o = PrintConsoleOutputs.From(s);
        Assert.Equal(new Vec3(10, 20, 0), o.BedOrigin);
        Assert.Equal(4.5, o.ZZero);
    }

    [Fact]
    public void Only_whitelisted_jog_steps_are_accepted()
    {
        var s = Ready();
        Assert.Equal(1.0, s.Step);

        s = PrintConsoleReducer.StepChanged(s, 10.0);
        Assert.Equal(10.0, s.Step);

        // The planner would refuse anything else, so the old step survives.
        Assert.Equal(10.0, PrintConsoleReducer.StepChanged(s, 7.5).Step);
    }

    [Fact]
    public void Terminal_arming_round_trips()
    {
        var until = T0.AddSeconds(5);
        var s = PrintConsoleReducer.TerminalArmed(Ready(), until);
        Assert.Equal(until, s.TerminalConfirmUntilUtc);
        Assert.Equal(DateTime.MinValue, PrintConsoleReducer.TerminalDisarmed(s).TerminalConfirmUntilUtc);
    }

    // ---- 3.0.0 M3 parity: local validation, projected dry-run mode ----

    [Fact]
    public void Validate_is_available_offline_while_upload_is_not()
    {
        // JobGate is pure: a disconnected operator can still check a job. Upload keeps
        // the connection gate; Validate must not die with it.
        var offline = PrintConsoleState.Empty(Host).WithConnection(ConsoleConnection.Disconnected);
        var b = PrintConsoleButtons.Enabled(offline, hasHost: true, hasGcode: true, hasPoint: false, gotoSupported: true);
        Assert.True(b.Validate);
        Assert.False(b.Upload);
        Assert.False(b.Reasons.ContainsKey("validate"));
        Assert.Equal("not connected (disconnected)", b.Reasons["upload"]);

        // Untrusted machine: upload refuses, validation still runs (it sends nothing).
        var untrusted = Ready().WithTrustReason("machine identity mismatch");
        var bu = PrintConsoleButtons.Enabled(untrusted, true, true, true, true);
        Assert.True(bu.Validate);
        Assert.False(bu.Upload);
    }

    [Fact]
    public void Validate_refuses_without_gcode_while_pending_and_on_an_unresolved_profile()
    {
        var none = PrintConsoleButtons.Enabled(Ready(), true, hasGcode: false, hasPoint: true, gotoSupported: true);
        Assert.False(none.Validate);
        Assert.Equal("no gcode", none.Reasons["validate"]);

        var pending = PrintConsoleButtons.Enabled(PrintConsoleReducer.ActionStarted(Ready(), "uploading"), true, true, true, true);
        Assert.False(pending.Validate);
        Assert.StartsWith("action in progress", pending.Reasons["validate"]);

        // The gate fails closed on the same planning blocks as the transaction.
        var noProfile = PrintConsoleButtons.Enabled(Ready().WithPlanningFacts("robot profile unresolved", null), true, true, true, true);
        Assert.False(noProfile.Validate);
        Assert.Equal("robot profile unresolved", noProfile.Reasons["validate"]);
        var noEnvelope = PrintConsoleButtons.Enabled(Ready().WithPlanningFacts(null, new[] { "xMin" }), true, true, true, true);
        Assert.False(noEnvelope.Validate);
        Assert.Contains("envelope unknown", noEnvelope.Reasons["validate"]);
    }

    [Fact]
    public void Start_label_follows_the_projected_dry_run_mode()
    {
        var s = Ready();
        Assert.False(s.DryRun);
        Assert.Equal("Start", s.StartLabel);
        var dry = s.WithDryRun(true);
        Assert.True(dry.DryRun);
        Assert.Equal("Start (dry M37)", dry.StartLabel);
        // The flag is display projection only: it does not touch the output snapshot,
        // so toggling it never expires the canvas by itself.
        Assert.Equal(PrintConsoleOutputs.From(s), PrintConsoleOutputs.From(dry));
        // and it survives the reducer's poll fold like every other projected fact
        Assert.True(PrintConsoleReducer.Reduce(dry, Facts(ConsoleConnection.Connected, Snap())).DryRun);
    }

    [Fact]
    public void Next_action_walks_connect_home_validate_upload_start_and_never_bypasses_a_gate()
    {
        var none = PrintConsoleState.Empty(Host);
        var b0 = PrintConsoleButtons.Enabled(none, hasHost: false, hasGcode: true, hasPoint: false, gotoSupported: true);
        Assert.Equal(("connect", "Connect", false), ConsoleNextAction.Decide(none, b0));   // no host: recommended but gated

        var unhomed = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host), Facts(ConsoleConnection.Connected, Snap(x: false)));
        unhomed = PrintConsoleReducer.GcodeChanged(unhomed, 10, 0xABCD);
        var bu = PrintConsoleButtons.Enabled(unhomed, true, true, false, true);
        Assert.Equal(("home", "Home all", true), ConsoleNextAction.Decide(unhomed, bu));

        var homed = PrintConsoleReducer.Reduce(PrintConsoleState.Empty(Host), Facts(ConsoleConnection.Connected, Snap()));
        homed = PrintConsoleReducer.GcodeChanged(homed, 10, 0xABCD);
        var bh = PrintConsoleButtons.Enabled(homed, true, true, false, true);
        Assert.Equal(("validate", "Validate", true), ConsoleNextAction.Decide(homed, bh));

        var validated = PrintConsoleReducer.ValidationCompleted(homed, new ValidationProjection(0xABCD, false, null, null, false));
        Assert.Equal("upload", ConsoleNextAction.Decide(validated, PrintConsoleButtons.Enabled(validated, true, true, false, true)).Id);
        var refused = PrintConsoleReducer.ValidationCompleted(homed, new ValidationProjection(0xABCD, true, null, null, false));
        Assert.Equal("validate", ConsoleNextAction.Decide(refused, PrintConsoleButtons.Enabled(refused, true, true, false, true)).Id);

        var ready = Ready();
        var br = PrintConsoleButtons.Enabled(ready, true, true, true, true);
        Assert.Equal("validate", ConsoleNextAction.Decide(ready, br).Id);   // Ready() has no validation projection yet
        var complete = PrintConsoleReducer.ValidationCompleted(ready, new ValidationProjection(0xABCD, false, null, null, false));
        Assert.Equal(("start", "Start", true), ConsoleNextAction.Decide(complete, PrintConsoleButtons.Enabled(complete, true, true, true, true)));
        var dry = complete.WithDryRun(true);
        Assert.Equal("Start (dry M37)", ConsoleNextAction.Decide(dry, PrintConsoleButtons.Enabled(dry, true, true, true, true)).Label);

        var paused = ready.WithMachine(MachineStatus.Paused, "paused");
        Assert.Equal(("resume", "Resume print", true), ConsoleNextAction.Decide(paused, PrintConsoleButtons.Enabled(paused, true, true, true, true)));
        var printing = ready.WithMachine(MachineStatus.Processing, "processing");
        Assert.Equal("tab:2", ConsoleNextAction.Decide(printing, PrintConsoleButtons.Enabled(printing, true, true, true, true)).Id);
        var halted = ready.WithMachine(MachineStatus.Halted, "halted");
        Assert.Equal(("reset", "Reset (M999)", true), ConsoleNextAction.Decide(halted, PrintConsoleButtons.Enabled(halted, true, true, true, true)));
        // untrusted: the recommendation is still Home, and it is gated exactly like the page control
        var untrusted = unhomed.WithTrustReason("identity mismatch");
        Assert.Equal(("home", "Home all", false), ConsoleNextAction.Decide(untrusted, PrintConsoleButtons.Enabled(untrusted, true, true, false, true)));
    }

    [Fact]
    public void Terminal_arming_keeps_the_exact_command_for_the_confirmation_strip()
    {
        var s = PrintConsoleReducer.TerminalArmed(Ready(), T0.AddSeconds(5), "G1 X10");
        Assert.Equal("G1 X10", s.TerminalArmedText);
        Assert.Equal("", PrintConsoleReducer.TerminalDisarmed(s).TerminalArmedText);
    }

    [Fact]
    public void Outputs_expose_the_appended_3_0_0_sockets()
    {
        var o = PrintConsoleOutputs.From(Ready());
        Assert.Equal(Host, o.HostOut);
        Assert.True(o.Connected);
        Assert.True(o.XHomed); Assert.True(o.YHomed); Assert.True(o.ZHomed);
        Assert.Equal(JobPath, o.Uploaded);
    }
}
