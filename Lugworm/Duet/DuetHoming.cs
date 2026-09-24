using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Duet
{
    /// <summary>The verified-homing sequence shared by the Jog pendant and the Print
    /// Console. G28 (or a per-axis "G28 X"/"G28 Y"/"G28 Z") is sent inside the
    /// session's gated command transaction; completion is verified by watching the
    /// machine go busy and return to Idle with the requested axes homed; and the
    /// SCARA arm configuration is established from the homeall.g park replay - or
    /// invalidated - exactly as the Jog pendant always did. Lives on the Rhino side
    /// because it needs <see cref="MachineSession"/>.</summary>
    public static class DuetHoming
    {
        /// <summary>Upper bound for the homing move and its firmware reply.</summary>
        public static readonly TimeSpan HomingTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The refusals a caller checks BEFORE opening the command
        /// transaction: no session, or the connect-time macro probe found no home
        /// macro for the requested axis. Returns the message without the "ERROR: "
        /// prefix - the caller adds it when it records the result. Null = proceed to
        /// <see cref="HomeAsync"/> (which is where the freshness/state gate runs, on
        /// the snapshot fetched under the command lock).</summary>
        public static string? PreflightRefusal(MachineSession? session, string gcode)
        {
            if (session == null) return "homing refused - not connected";
            string axis = HomeAxis(gcode);
            if (session.HomeProbeComplete && !session.HomeAvailable(axis))
                return gcode + " refused - the connect-time macro probe found no supported home macro";
            return null;
        }

        /// <summary>"all" for a bare G28, otherwise the axis letter after "G28 ".</summary>
        static string HomeAxis(string gcode) => gcode == "G28" ? "all" : gcode.Substring(4).Trim();

        /// <summary>The body of the gated homing action - run it through
        /// <c>MachineSession.RunGatedCommandAsync</c> (the components' RunGatedAction),
        /// never directly. Gates on <paramref name="snap"/> (idle; not required homed -
        /// homing is the recovery path out of an unknown state; not older than
        /// <paramref name="notBeforeUtc"/>), sends the home, verifies it, and returns
        /// the reply text incl. the arm-configuration caveat. Throws
        /// <see cref="DuetException"/> on every refusal or verification failure.</summary>
        public static async Task<string> HomeAsync(MachineSession session, ObjectModelSnapshot snap,
                                                   IMachineClient client, string gcode, RobotLimits lim,
                                                   bool cartesian, string? profileError,
                                                   DateTime? notBeforeUtc, CancellationToken ct)
        {
            string axis = HomeAxis(gcode);
            if (profileError != null)
                throw new DuetException("homing refused - " + profileError);
            string? stateGate = StateGate.Refuse(snap, requireIdle: true,
                requireHomed: false, notBeforeUtc: notBeforeUtc, activity: "homing");
            if (stateGate != null) throw new DuetException("homing refused - " + stateGate);
            bool fullHome = string.Equals(gcode, "G28", StringComparison.OrdinalIgnoreCase);
            // homeall.g is only replayed to establish the arm configuration after a
            // FULL home. Fetching and parsing it for a per-axis action would make
            // isolated bring-up depend on a macro that action never runs. A
            // Cartesian machine has no arm configuration at all, so the whole
            // macro/trigger-pose machinery is skipped for it.
            bool macroReady = false;
            if (fullHome && !cartesian)
            {
                // An unreadable or unparseable macro must NOT stop the home itself:
                // homing is the recovery path out of an unknown configuration, so it
                // stays permitted. The configuration simply remains Unknown, and the
                // gates that depend on it keep refusing until a home does establish it.
                try
                {
                    await session.PrepareHomeConfigurationAsync(client, ct).ConfigureAwait(false);
                    macroReady = true;
                }
                catch (Exception ex)
                {
                    session.InvalidateArmConfiguration(
                        "homeall.g could not be read or parsed: " + ex.Message);
                }
            }
            try
            {
                DateTime sentAt = DateTime.UtcNow;
                var verification = await VerifyHomeAsync(session, client, gcode, sentAt, ct).ConfigureAwait(false);
                // Verify only what was actually asked for. Requiring all three axes
                // after "G28 X" defeats the purpose of per-axis homing, which exists
                // precisely to bring one axis up while the others are still unhomed.
                string[] expected = fullHome
                    ? new[] { "X", "Y", "Z" }
                    : new[] { axis.ToUpperInvariant() };
                var missing = new List<string>();
                foreach (string letter in expected)
                {
                    var axisState = verification.Snapshot.Axis(letter);
                    if (axisState == null || !axisState.Homed) missing.Add(letter);
                }
                if (missing.Count > 0)
                {
                    if (!cartesian)
                        session.InvalidateArmConfiguration("homing did not home axis/axes: " + string.Join(", ", missing));
                    throw new DuetException("homing not verified - axes not homed: " + string.Join(", ", missing));
                }

                // Only a full home has the trigger pose required to replay
                // homeall.g. A per-axis attended action may leave all axes
                // marked homed, but it did not establish the full park path.
                string caveat = "";
                if (cartesian)
                {
                    // no arm configuration to establish - homed axes are the
                    // whole contract for a Cartesian machine
                }
                else if (fullHome)
                {
                    var z = verification.Snapshot.Axis("Z");
                    if (z == null) throw new DuetException("homing not verified - Z position is unavailable");
                    double j2 = z.MachinePosition - lim.BaseH;
                    var trigger = ScaraKinematics.Forward(lim.J1Min, lim.J3Min, j2,
                        lim.L1, lim.L2, lim.BaseH);
                    var verifiedTriggerPose = new Vec3(trigger.x, trigger.y, trigger.z);
                    // The machine IS homed at this point. Failing to derive the arm
                    // configuration is reported, not thrown: throwing would label a
                    // successful home a failure and hide that the axes are now homed.
                    // Configuration stays Unknown, so every configuration-sensitive
                    // action remains gated until a home does establish it.
                    // The trigger pose is built from J1Min/J3Min; the distal joint
                    // minimum fixes the branch, so the operator elbow preference has
                    // no standing here.
                    if (!macroReady)
                        caveat = " - arm configuration UNKNOWN: homeall.g was not readable";
                    else if (!session.TryEstablishArmConfigurationAfterVerifiedHome(true,
                        verifiedTriggerPose, lim, lim.HomedArmMode))
                        caveat = " - arm configuration UNKNOWN: the homeall.g park move could not be resolved safely";
                }
                else
                {
                    session.InvalidateArmConfiguration("per axis home completed, full home verification is required");
                    caveat = " - arm configuration UNKNOWN: a full G28 is required to establish it";
                }
                string reply = verification.Reply ?? "";
                return gcode + " homing verified" + (reply.Length == 0 ? "" : ": " + reply) + caveat;
            }
            // Fail-closed cleanup, then rethrow: the caller still sees the original fault
            // (including a cancellation), and the arm configuration is never left
            // claiming a branch a failed home did not establish.
            catch (Exception)
            {
                if (!cartesian) session.InvalidateArmConfiguration("homing command failed");
                throw;
            }
        }

        sealed class HomeVerification
        {
            public ObjectModelSnapshot Snapshot = new ObjectModelSnapshot();
            public string? Reply;
        }

        /// <summary>Sends the home and watches the machine: it must be seen busy after
        /// <paramref name="sentAt"/> and then return to Idle within
        /// <see cref="HomingTimeout"/>. <c>MarkInitiatedMotion</c> is armed here,
        /// immediately before the send, and nowhere else.</summary>
        static async Task<HomeVerification> VerifyHomeAsync(MachineSession session, IMachineClient client,
                                                            string gcode, DateTime sentAt, CancellationToken ct)
        {
            using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            session.MarkInitiatedMotion();
            Task<string?> replyTask = client.SendCodeAndWaitForReplyAsync(gcode, HomingTimeout, replyCts.Token);
            bool sawMotion = false;
            ObjectModelSnapshot? completed = null;
            string? reply = null;
            DateTime deadline = DateTime.UtcNow + HomingTimeout;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    if (replyTask.IsCompleted)
                    {
                        reply = await replyTask.ConfigureAwait(false);
                        ThrowIfFirmwareError(reply);
                    }
                    var observed = await client.GetSnapshotAsync(includeJob: false, ct).ConfigureAwait(false);
                    if (observed.TakenUtc >= sentAt)
                    {
                        if (observed.Status == MachineStatus.Busy || observed.Status == MachineStatus.Processing)
                            sawMotion = true;
                        else if (sawMotion && observed.Status == MachineStatus.Idle)
                        {
                            completed = observed;
                            break;
                        }
                    }
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                if (!sawMotion)
                    throw new DuetException("could not confirm - homing not verified");
                if (completed == null)
                    throw new DuetException("homing did not return to Idle - homing not verified");

                if (!replyTask.IsCompleted) replyCts.Cancel();
                try { if (reply == null) reply = await replyTask.ConfigureAwait(false); }
                // We cancelled the reply poller on the line above, so its cancellation is
                // the expected outcome and homing is already verified without a reply. The
                // filter is what keeps that distinct from the CALLER cancelling, which
                // still propagates.
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                ThrowIfFirmwareError(reply);
                return new HomeVerification { Snapshot = completed, Reply = reply };
            }
            // Cleanup, then rethrow: the original fault (cancellation included) reaches
            // the caller unchanged.
            catch (Exception)
            {
                replyCts.Cancel();
                // best effort: draining the reply poller we just cancelled - its own
                // failure would otherwise replace the fault we are propagating.
                try { await replyTask.ConfigureAwait(false); } catch (Exception) { }
                throw;
            }
        }

        static void ThrowIfFirmwareError(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return;
            string text = reply!.Trim();
#if NETFRAMEWORK
            bool error = text.StartsWith("!!", StringComparison.OrdinalIgnoreCase)
                      || text.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0
                      || text.IndexOf("error=", StringComparison.OrdinalIgnoreCase) >= 0;
#else
            bool error = text.StartsWith("!!", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("Error:", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("error=", StringComparison.OrdinalIgnoreCase);
#endif
            if (error) throw new DuetException("homing failed - firmware reply: " + text);
        }
    }
}
