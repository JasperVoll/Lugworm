using System;

namespace Lugworm.Core.Duet
{
    /// <summary>Why a healthy-cadence wait ended. The distinction matters because the
    /// poll loop treats a cancelled delay as shutdown: an early wake must never be
    /// mistaken for one.</summary>
    public enum PollWaitOutcome
    {
        /// <summary>The configured interval elapsed - the ordinary case.</summary>
        CadenceElapsed,
        /// <summary>A command asked for an immediate poll. Continue the loop.</summary>
        WokenEarly,
        /// <summary>The session is shutting down. Leave the loop.</summary>
        Cancelled,
    }

    /// <summary>How often the session should poll. Pure: no clock, no session state.
    /// Extracted so the cadence and the "an early wake is not a shutdown" rule are
    /// testable without a board.</summary>
    /// <remarks>
    /// <para>The interval means a TARGET START-TO-START period: a configured 300 ms asks
    /// for a coordinate observation roughly every 300 ms, not for 300 ms of idling added
    /// to however long the poll's own requests took. The loop is sequential - it awaits its
    /// requests and only then waits - so requests can never overlap; <see cref="WaitAfterWork"/>
    /// just subtracts the work already done from the target and keeps a floor so the loop
    /// always yields.</para>
    /// <para>The session clamps whatever comes back to 250–2000 ms
    /// (<c>MachineSession.PollIntervalMs</c>) and <see cref="StateGate"/> still refuses
    /// snapshots older than its freshness window, so this policy can only ever make
    /// the loop poll sooner, never later than configured.</para>
    /// </remarks>
    public static class PollCadencePolicy
    {
        /// <summary>Cadence used while an operator is watching a moving machine.</summary>
        public const int FastPollMs = 250;

        /// <summary>The shortest wait the loop ever takes, so a cycle whose work already
        /// overran the target still yields the thread and still observes cancellation
        /// instead of spinning straight into the next request.</summary>
        public const int MinIdleWaitMs = 20;

        /// <summary>Turns the TARGET start-to-start interval into the wait that belongs
        /// after this cycle's work. Bounded and non-overlapping: the result is never
        /// negative, never below <see cref="MinIdleWaitMs"/>, and never above the
        /// target.</summary>
        /// <param name="targetIntervalMs">What <see cref="Decide"/> returned - the period
        /// the operator asked for, measured start to start.</param>
        /// <param name="workElapsedMs">How long this cycle has already taken, measured from
        /// the instant the poll request was issued. A slow board therefore shortens the idle
        /// instead of adding to it; the cycle can still be longer than the target, which is
        /// what <c>LiveRenderStats.ActualPollIntervalMs</c> reports.</param>
        public static int WaitAfterWork(int targetIntervalMs, double workElapsedMs)
        {
            if (targetIntervalMs <= 0) return MinIdleWaitMs;
            if (double.IsNaN(workElapsedMs) || workElapsedMs < 0) workElapsedMs = 0;
            double remaining = targetIntervalMs - workElapsedMs;
            if (remaining <= MinIdleWaitMs) return Math.Min(targetIntervalMs, MinIdleWaitMs);
            return (int)Math.Round(remaining);
        }

        /// <summary>The target start-to-start interval for the next poll.</summary>
        /// <param name="anyOwnerFocused">True while ANY registered console window on this
        /// session has focus. Owner-keyed registration is what stops two consoles on one
        /// host from blurring each other's cadence off.</param>
        /// <param name="machineActive">True when the last snapshot showed the machine doing
        /// something worth watching at speed - see <see cref="IsActive"/>.</param>
        /// <param name="configuredPollMs">The operator's configured interval.</param>
        public static int Decide(bool anyOwnerFocused, bool machineActive, int configuredPollMs)
        {
            if (configuredPollMs <= 0) return FastPollMs;
            if (!anyOwnerFocused || !machineActive) return configuredPollMs;
            return Math.Min(configuredPollMs, FastPollMs);
        }

        /// <summary>The machine states worth watching at speed. Named "active" rather than
        /// "moving" because Paused counts (the operator is working on the machine) while
        /// Off, Halted and Unknown do not.</summary>
        /// <remarks>The loop polls and only then waits, so a transition to Idle is picked
        /// up roughly one fast interval after it happens, not one configured interval:
        /// the cadence cannot linger at the slow rate once motion has been observed.</remarks>
        public static bool IsActive(MachineStatus status)
        {
            switch (status)
            {
                case MachineStatus.Processing:
                case MachineStatus.Simulating:
                case MachineStatus.Busy:
                case MachineStatus.Pausing:
                case MachineStatus.Paused:
                case MachineStatus.Resuming:
                case MachineStatus.Cancelling:
                case MachineStatus.ChangingTool:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Interprets the end of a healthy-cadence wait. The wake signal is
        /// deliberately NOT the session cancellation token: the poll loop breaks out on a
        /// cancelled delay, so sharing one would turn every "poll now" into a disconnect,
        /// and the reconnect backoff - which observes only the cancellation token - would
        /// lose its backoff entirely.</summary>
        /// <remarks>
        /// <para>The intended implementation is the semaphore's TIMED overload, whose
        /// result maps straight onto this function:</para>
        /// <code>
        /// bool woken = await _pollWake.WaitAsync(effectivePollMs, ct).ConfigureAwait(false);
        /// switch (PollCadencePolicy.Interpret(woken, ct.IsCancellationRequested)) { … }
        /// </code>
        /// <para>Do NOT race an uncancelled <c>WaitAsync()</c> against <c>Task.Delay</c>
        /// with <c>Task.WhenAny</c>: when the delay wins, the losing waiter stays queued on
        /// the semaphore and silently swallows the NEXT wake - a real lost-wakeup bug - and
        /// cancelling the loser locally is unsafe because the healthy path treats
        /// <c>OperationCanceledException</c> as shutdown. A capacity of one coalesces
        /// requests; a permit left over from the previous cycle costs at most one extra
        /// immediate poll, never a spin. Drain the permit when a new connection generation
        /// starts, and never wait on it during reconnect.</para>
        /// </remarks>
        /// <param name="wakeSignalled">A command completed and asked for an immediate poll.</param>
        /// <param name="cancellationRequested">The session is shutting down.</param>
        public static PollWaitOutcome Interpret(bool wakeSignalled, bool cancellationRequested)
        {
            // Shutdown wins: a wake that races a disconnect must still stop the loop.
            if (cancellationRequested) return PollWaitOutcome.Cancelled;
            return wakeSignalled ? PollWaitOutcome.WokenEarly : PollWaitOutcome.CadenceElapsed;
        }
    }
}
