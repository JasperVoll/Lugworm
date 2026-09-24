using System;

namespace Lugworm.Core.Duet
{
    /// <summary>The one machine-state freshness gate every motion-causing action
    /// must pass - jog, go-to, home, print start. Pure so it stays unit-tested.
    /// The poll loop deliberately keeps the last snapshot (stale-stamped) through
    /// reconnects, so acting on <c>Status</c>/<c>AllHomed</c> without checking
    /// <c>TakenUtc</c> means acting on a machine whose real state is unknown.</summary>
    public static class StateGate
    {
        /// <summary>Snapshots older than this are treated as unknown state. The poll
        /// cadence must stay below this (see MachineSession.PollIntervalMs) or a fresh
        /// snapshot can age out between polls and gate every action.</summary>
        public const double DefaultMaxAgeSeconds = 3.0;

        /// <summary>Returns null when the snapshot is fresh and the machine state
        /// allows the action, otherwise the refusal reason.
        /// <paramref name="notBeforeUtc"/>: refuse snapshots taken before the last
        /// command was sent - the position/status they report predates that command.</summary>
        public static string? Refuse(ObjectModelSnapshot? snap,
                                     double maxAgeS = DefaultMaxAgeSeconds,
                                     bool requireIdle = true,
                                     bool allowPaused = false,
                                     bool requireHomed = true,
                                     DateTime? notBeforeUtc = null,
                                     string activity = "this action")
        {
            if (snap == null)
                return "no machine status available - connect first";

            double age = (DateTime.UtcNow - snap.TakenUtc).TotalSeconds;
            if (age > maxAgeS)
                return FormattableString.Invariant($"machine status is stale ({age:0.0}s old > {maxAgeS:0.0}s) - wait for a fresh poll");

            if (notBeforeUtc.HasValue && snap.TakenUtc < notBeforeUtc.Value)
                return "machine status predates the last command - wait for a fresh poll";

            if (requireIdle)
            {
                bool ok = snap.Status == MachineStatus.Idle
                       || (allowPaused && snap.Status == MachineStatus.Paused);
                if (!ok)
                    return allowPaused
                        ? $"machine is '{snap.RawStatus}' - {activity} allowed only when idle or paused"
                        : $"machine is '{snap.RawStatus}' - {activity} allowed only when idle";
            }

            if (requireHomed && !snap.AllHomed)
                return "axes are not homed - home the machine first";

            return null;
        }

        /// <summary>The job-control gate (pause / resume / cancel): the snapshot must
        /// be fresh, and the machine must be in a status the action is valid in -
        /// the caller decides <paramref name="allowedStatus"/> from the snapshot it
        /// holds (e.g. Paused for resume). Idle and homed are deliberately NOT
        /// required: a running or paused job is exactly the state these actions
        /// address. Shared by the Print component and the Print Console.</summary>
        public static string? RefuseJobControl(ObjectModelSnapshot? snap, string activity, bool allowedStatus)
        {
            string? freshness = Refuse(snap, requireIdle: false,
                requireHomed: false, activity: activity);
            if (freshness != null) return freshness;
            if (!allowedStatus)
                return $"machine is '{snap!.RawStatus}' - {activity} is not valid in this state";
            return null;
        }
    }
}
