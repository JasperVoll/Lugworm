using System;
using System.Globalization;
using System.Threading;

namespace Lugworm.Core.Duet
{
    public enum ScheduledStartPhase { Armed, Firing, Updating }

    /// <summary>Session-only identity of one deferred start.</summary>
    public sealed class ScheduledStart
    {
        static long _nextId;
        public long Id { get; }
        public ScheduledStartPhase Phase { get; }
        public DateTime DueUtc { get; }
        public DateTime CreatedUtc { get; }
        public string JobSha256 { get; }
        public string ShortHash { get; }
        public string RemotePath { get; }
        public bool DryRun { get; }
        public string Host { get; }
        public string SessionId { get; }
        public long ConnectionGeneration { get; }
        public string Fingerprint { get; }

        public ScheduledStart(long id, DateTime dueUtc, DateTime createdUtc, string jobSha256,
                              string shortHash, string remotePath, bool dryRun, string host,
                              string sessionId, long connectionGeneration, string fingerprint,
                              ScheduledStartPhase phase = ScheduledStartPhase.Armed)
        {
            Id = id;
            Phase = phase;
            DueUtc = Utc(dueUtc);
            CreatedUtc = Utc(createdUtc);
            JobSha256 = jobSha256 ?? "";
            ShortHash = string.IsNullOrEmpty(shortHash) ? Short(JobSha256) : shortHash;
            RemotePath = remotePath ?? "";
            DryRun = dryRun;
            Host = host ?? "";
            SessionId = sessionId ?? "";
            ConnectionGeneration = connectionGeneration;
            Fingerprint = fingerprint ?? "";
        }

        public ScheduledStart WithPhase(ScheduledStartPhase phase)
            => new ScheduledStart(Id, DueUtc, CreatedUtc, JobSha256, ShortHash, RemotePath,
                                  DryRun, Host, SessionId, ConnectionGeneration, Fingerprint, phase);

        public static ScheduledStart Create(DateTime dueUtc, DateTime createdUtc, string jobSha256,
                                            string remotePath, bool dryRun, string host,
                                            string sessionId, long connectionGeneration, string fingerprint)
            => new ScheduledStart(Interlocked.Increment(ref _nextId), dueUtc, createdUtc, jobSha256,
                                  Short(jobSha256 ?? ""), remotePath, dryRun, host, sessionId,
                                  connectionGeneration, fingerprint);

        public ScheduledStart Rebound(long id, string jobSha256, string shortHash, DateTime createdUtc)
            => new ScheduledStart(id, DueUtc, createdUtc, jobSha256, shortHash, RemotePath,
                                  DryRun, Host, SessionId, ConnectionGeneration, Fingerprint);

        static string Short(string hash)
            => hash.Length <= 8 ? hash : hash.Substring(0, 8);

        static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    public enum ScheduleDecisionKind { Wait, Fire, Missed, Cancel }

    public sealed class ScheduleDecision
    {
        public ScheduleDecisionKind Kind { get; }
        public ScheduleDecisionKind Decision => Kind;
        public string Reason { get; }

        public ScheduleDecision(ScheduleDecisionKind kind, string? reason = null)
        { Kind = kind; Reason = reason ?? ""; }

        public static ScheduleDecision Wait() => new ScheduleDecision(ScheduleDecisionKind.Wait);
        public static ScheduleDecision Fire() => new ScheduleDecision(ScheduleDecisionKind.Fire);
        public static ScheduleDecision Missed(string reason) => new ScheduleDecision(ScheduleDecisionKind.Missed, reason);
        public static ScheduleDecision Cancel(string reason) => new ScheduleDecision(ScheduleDecisionKind.Cancel, reason);
    }

    /// <summary>Pure policy for arming and firing a deferred print start.</summary>
    public static class PrintSchedulePolicy
    {
        public static readonly TimeSpan MinimumLead = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan MaxHorizon = TimeSpan.FromHours(24);
        public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(5);

        public static string? RefuseArm(DateTime nowUtc, DateTime dueUtc, PrintConsoleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            string? time = RefuseTime(nowUtc, dueUtc);
            if (time != null) return time;
            if (state.ScheduledStart != null) return "a scheduled start is already armed";
            if (state.Connection != ConsoleConnection.Connected) return "not connected";
            if (state.MachineState != MachineStatus.Idle) return "machine not idle";
            if (state.PendingAction != null) return "action in progress: " + state.PendingAction;
            if (state.GcodeLines <= 0) return "no gcode";
            if (state.Validation == null || state.Validation.Danger) return "validate the job first";
            if (state.Uploaded == null || state.Uploaded.Hash != state.GcodeHash)
                return "the job is not verified-uploaded";
            if (state.StaleInput != null) return PrintConsoleState.StaleInputReason(state.StaleInput);
            if (state.TrustReason != null) return state.TrustReason;
            if (!state.Recovery.IsQuiet) return "machine recovery is not complete";
            return null;
        }

        public static string? RefuseTime(DateTime nowUtc, DateTime dueUtc)
        {
            double lead = (dueUtc.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds;
            if (lead < 0) return "start time is in the past";
            if (lead < MinimumLead.TotalSeconds) return "start time must be at least 60 seconds ahead";
            if (lead > MaxHorizon.TotalSeconds) return "start time is more than 24 hours ahead";
            return null;
        }

        public static ScheduleDecision Decide(DateTime nowUtc, ScheduledStart schedule, PrintConsoleState state)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            if (state == null) throw new ArgumentNullException(nameof(state));
            string? cancel = CancelReason(schedule, state);
            if (cancel != null) return ScheduleDecision.Cancel(cancel);
            if (schedule.Phase != ScheduledStartPhase.Armed) return ScheduleDecision.Wait();
            double late = (nowUtc.ToUniversalTime() - schedule.DueUtc).TotalSeconds;
            if (late < 0) return ScheduleDecision.Wait();
            if (late > MissedAfter.TotalSeconds) return ScheduleDecision.Missed("scheduled start missed by more than 5 minutes");
            return ScheduleDecision.Fire();
        }

        /// <summary>Returns a fact-triggered cancellation without considering the clock. A
        /// Firing schedule is owned by its start transaction, whose own machine transition
        /// (Idle to Processing) must not read as "another job started".</summary>
        public static string? CancelReason(ScheduledStart schedule, PrintConsoleState state)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (schedule.Phase == ScheduledStartPhase.Firing) return null;
            if (state.Connection != ConsoleConnection.Connected) return "connection lost";
            if (!Same(schedule.Host, state.Host)) return "host changed";
            if (schedule.SessionId.Length > 0 && !string.Equals(schedule.SessionId, state.SessionId, StringComparison.Ordinal))
                return "session changed";
            if (schedule.ConnectionGeneration >= 0 && schedule.ConnectionGeneration != state.ConnectionGeneration)
                return "connection changed";
            if (schedule.Fingerprint.Length > 0 && !string.Equals(schedule.Fingerprint, state.ExpectedFingerprint, StringComparison.Ordinal))
                return "machine identity changed";
            if (StartsOrStopsAJob(state.MachineState))
                return "machine left idle state (" + state.MachineState.ToString().ToLowerInvariant() + ")";
            if (!state.Recovery.IsQuiet) return "machine recovery changed";
            return null;
        }

        /// <summary>The states that mean a job ran or the machine stopped after arming. Busy,
        /// ChangingTool and Unknown do not cancel: the operator may home after scheduling
        /// (Busy while G28 runs), and the fire-time StateGate still requires Idle.</summary>
        static bool StartsOrStopsAJob(MachineStatus st)
            => st == MachineStatus.Processing || st == MachineStatus.Simulating
            || st == MachineStatus.Pausing || st == MachineStatus.Paused || st == MachineStatus.Resuming
            || st == MachineStatus.Cancelling || st == MachineStatus.Halted || st == MachineStatus.Off;

        static bool Same(string expected, string actual)
            => expected.Length == 0 || string.Equals(expected, actual ?? "", StringComparison.OrdinalIgnoreCase);

        /// <summary>Checks the conditions that must still hold before an updating
        /// schedule may be promoted to a new armed generation.</summary>
        public static string? RefuseUpdate(DateTime nowUtc, ScheduledStart schedule, PrintConsoleState state)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (schedule.Phase == ScheduledStartPhase.Firing) return "scheduled start is firing";
            if (schedule.Phase != ScheduledStartPhase.Updating) return "schedule update is not in progress";
            string? time = RefuseTime(nowUtc, schedule.DueUtc);
            if (time != null) return "scheduled start due time reached during update";
            return RefuseArm(nowUtc, schedule.DueUtc, state.WithScheduledStart(null));
        }
    }
}
