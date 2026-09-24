using System;

namespace Lugworm.Core.Duet
{
    /// <summary>What the caller must do to its timer after a scheduling decision. A command
    /// that did not change anything carries <see cref="Changed"/> false, so an already
    /// correctly scheduled pump is never re-armed for nothing.</summary>
    public readonly struct PumpCommand
    {
        /// <summary>True when the timer must be running after this command.</summary>
        public bool Running { get; }
        /// <summary>Delay before the next callback, ms. 0 is an immediate coalesced wake.</summary>
        public int DueMs { get; }
        /// <summary>Period between callbacks, ms.</summary>
        public int PeriodMs { get; }
        /// <summary>True when the caller has to reprogram the timer.</summary>
        public bool Changed { get; }

        public PumpCommand(bool running, int dueMs, int periodMs, bool changed)
        { Running = running; DueMs = dueMs; PeriodMs = periodMs; Changed = changed; }

        public static readonly PumpCommand NoChange = new PumpCommand(true, 0, 0, false);
    }

    /// <summary>The frame pump's scheduling rules, extracted from the viewport so the
    /// "moves again after a rest" path can be regression-tested without Rhino.</summary>
    /// <remarks>
    /// <para>The defect this exists for (2026-09-09, plan V4 item 2): the old
    /// <c>Wake</c> assigned the frame interval to its own field and then only reprogrammed
    /// the timer <c>if (!_pumpRunning)</c>. A pump that had settled onto the 500 ms
    /// stale-watch beat was still running, so a new telemetry sample changed the field and
    /// nothing else: the display kept ticking at 2 Hz until something stopped the pump
    /// entirely. A machine that had stood still and then moved animated at the stale-watch
    /// rate.</para>
    /// <para>The fix is that a wake compares the CURRENT period against the target and
    /// reprograms whenever they differ - with a due time of 0, so the sample that woke it
    /// reaches the very next callback rather than waiting out the old period.</para>
    /// <para>Not thread-safe: the caller owns the lock, exactly as the viewport does.</para>
    /// </remarks>
    public sealed class FramePumpScheduler
    {
        /// <summary>True while the timer is armed.</summary>
        public bool Running { get; private set; }
        /// <summary>The period the timer is armed with, ms; meaningless while stopped.</summary>
        public int IntervalMs { get; private set; }

        /// <summary>Something changed and a frame is owed soon. Starts a stopped pump and
        /// re-arms a running one whose period is not the target, always with an immediate
        /// due time so the change is not delayed by the period it replaces.</summary>
        public PumpCommand Wake(int targetIntervalMs)
        {
            int period = Clamp(targetIntervalMs);
            if (Running && IntervalMs == period) return PumpCommand.NoChange;
            Running = true;
            IntervalMs = period;
            return new PumpCommand(true, 0, period, true);
        }

        /// <summary>Settles the pump onto a period. Unlike <see cref="Wake"/> the first
        /// callback is a whole period away: this is the steady-state rate, not an event.</summary>
        public PumpCommand Set(int intervalMs)
        {
            int period = Clamp(intervalMs);
            if (Running && IntervalMs == period) return PumpCommand.NoChange;
            Running = true;
            IntervalMs = period;
            return new PumpCommand(true, period, period, true);
        }

        /// <summary>Nothing left to draw and nothing that can change on its own.</summary>
        public PumpCommand Stop()
        {
            if (!Running) return new PumpCommand(false, 0, 0, false);
            Running = false;
            return new PumpCommand(false, 0, 0, true);
        }

        static int Clamp(int intervalMs) => intervalMs < 1 ? 1 : intervalMs;
    }
}
