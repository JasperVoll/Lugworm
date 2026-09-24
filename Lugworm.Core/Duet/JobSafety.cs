using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Machine;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    /// <summary>The upload/start gates shared by every component that commits a
    /// G-code job to the Duet - the Print component and the Print Console run the
    /// SAME path through here, so a gate cannot drift between them. Pure: no Rhino,
    /// no session object. The session's tracked arm configuration is passed in as a
    /// value, which is what keeps the checks unit-testable.</summary>
    public static class JobSafety
    {
        /// <summary>SD-card path for a job name: characters outside letters, digits,
        /// '_' and '-' become '_'; an empty (or null) name falls back to "print".</summary>
        public static string RemotePath(string? jobName)
        {
            var sb = new StringBuilder();
            foreach (char c in (jobName ?? "").Trim())
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            string name = sb.Length == 0 ? "print" : sb.ToString();
            return "0:/gcodes/" + name + ".gcode";
        }

        /// <summary>Refuses (by throwing <see cref="DuetException"/>) unless the firmware
        /// the job is about to run on is the one it was validated against: profile
        /// resolvable, M208 envelope / M669 kinematics probed and matching
        /// <paramref name="lim"/>, and - for the SCARA - the session's tracked arm
        /// configuration (<paramref name="sessionConfiguration"/>, null when there is no
        /// session) known and equal to the one the job was validated in
        /// (<paramref name="validatedConfiguration"/>). A Cartesian machine has no arm
        /// configuration; there the board must NOT report SCARA kinematics.
        /// Runs synchronously against already-probed client state - it never sends.</summary>
        public static void EnsureFirmwareSafe(ObjectModelSnapshot snap, IMachineClient client,
                                              RobotLimits lim, RobotMechanism mechanism,
                                              ArmConfiguration? sessionConfiguration,
                                              ArmConfiguration validatedConfiguration,
                                              string? profileError,
                                              bool jobHomesFirst = false)
        {
            if (profileError != null)
                throw new DuetException("upload/start refused - " + profileError);
            // A transport that cannot vouch for its kinematics family refuses under every
            // profile, before any other check.
            if (client.KinematicsRefusal != null)
                throw new DuetException("upload/start refused - " + client.KinematicsRefusal);
            if (mechanism == RobotMechanism.Cartesian)
            {
                // No arm configuration and no SCARA M669 contract exist for a
                // Cartesian machine. What CAN be verified: the M208 envelope the
                // job was validated against, and that the board is not secretly a
                // SCARA (a parseable SCARA M669 on a "generic Cartesian" profile
                // means the definition does not match the connected machine).
                if (sessionConfiguration == null)
                    throw new DuetException("upload/start refused - session disappeared");
                if (client.Legacy && !client.LegacyM208Probed)
                    throw new DuetException("firmware M208 limits are unverified on this legacy board - upload/start refused");
                if (client.KinematicsProbed && client.Kinematics != null)
                    throw new DuetException("the firmware reports SCARA (M669) kinematics but the robot "
                        + "definition selects a Cartesian profile - upload/start refused; check the Robot Definition");
                foreach (var axis in new[] { "X", "Y", "Z" })
                {
                    var a = snap.Axis(axis);
                    if (a == null || a.Max <= a.Min)
                        throw new DuetException($"firmware M208 {axis} limits are unverified - upload/start refused");
                }
                foreach (var finding in FirmwareLimitCheck.CompareFindings(snap, lim))
                    if (finding.Severity == Severity.Error)
                        throw new DuetException("firmware limit mismatch - " + finding.Message);
                return;
            }
            // D9 (2026-09-09): a job whose first motion is a full G28 establishes its own
            // configuration - the validator walked it from the homed arm mode - so the
            // session's tracked configuration is not a precondition. Every other job still
            // needs the verified configuration it was validated against.
            if (!jobHomesFirst)
            {
                if (sessionConfiguration == null || !sessionConfiguration.Value.Known)
                    throw new DuetException("arm configuration is Unknown - complete a verified home before upload/start");
                var current = sessionConfiguration.Value;
                if (!validatedConfiguration.Known
                    || current.ArmMode != validatedConfiguration.ArmMode
                    || current.ThetaTurns != validatedConfiguration.ThetaTurns)
                    throw new DuetException("arm configuration changed since validation - validate again");
            }
            if (client.Legacy && !client.LegacyM208Probed)
                throw new DuetException("firmware M208 limits are unverified on this legacy board - upload/start refused");
            if (!client.KinematicsProbed || client.Kinematics == null)
                throw new DuetException(client.Legacy
                    ? "firmware kinematics are unverified - the connect-time M669 query returned no parseable reply; upload/start refused"
                    : "firmware kinematics are unverified - move.kinematics was not readable from the object model; check that the board reports SCARA kinematics, then reconnect. Upload/start refused");
            if (client.Legacy)
            {
                foreach (var axis in new[] { "X", "Y", "Z" })
                {
                    var a = snap.Axis(axis);
                    if (a == null || a.Max <= a.Min)
                        throw new DuetException($"firmware M208 {axis} limits are unverified - upload/start refused");
                }
            }
            foreach (var finding in FirmwareLimitCheck.CompareFindings(snap, lim))
                if (finding.Severity == Severity.Error)
                    throw new DuetException("firmware limit mismatch - " + finding.Message);
            foreach (var finding in FirmwareLimitCheck.CompareKinematics(client.Kinematics, lim))
                if (finding.Severity == Severity.Error)
                    throw new DuetException("firmware kinematics mismatch - " + finding.Message);
        }

        /// <summary>Compares the job's motion demand (feeds, accelerations, jerk) with
        /// the config.g baseline the client probed at connect time and the live M203/M201
        /// state. Returns the notice text to append to the action result ("" when there
        /// is nothing to say) and whether a WARNING was raised - the caller records the
        /// warning severity, because the action still succeeds and no prefix test on the
        /// wrapped reply can see it. Throws on an Error finding, and on a Warning when
        /// <paramref name="blockWarnings"/> is set.</summary>
        public static async Task<(string notice, bool warned)> CheckMotionAtGateAsync(
            IMachineClient client, IReadOnlyList<string> lines, bool blockWarnings, CancellationToken ct)
        {
            if (!client.MotionBaselineProbed || client.MotionBaseline == null)
                return ("INFO: motion baseline unverified - config.g could not be read; motion-limit checks are skipped", false);

            JobMotionDemand demand = JobMotionDemand.ScanJob(lines);
            LiveMotionState? live = await client.QueryLiveMotionStateAsync(ct).ConfigureAwait(false);
            List<FirmwareLimitFinding> findings = FirmwareLimitCheck.CompareMotion(
                client.MotionBaseline, demand, live);
            var notices = new List<string>();
            bool warned = false;
            foreach (FirmwareLimitFinding finding in findings)
            {
                // CompareMotion is specified never to emit Error. Refuse rather than drop one if
                // that ever changes - silently ignoring an Error in a machine-safety path is how a
                // guard stops guarding.
                if (finding.Severity == Severity.Error)
                    throw new DuetException("motion-limit error - " + finding.Message);
                if (finding.Severity == Severity.Warning)
                {
                    if (blockWarnings)
                        throw new DuetException("motion-limit warning - " + finding.Message + " (upload refused by blockWarnings)");
                    notices.Add("WARNING: " + finding.Message);
                    warned = true;
                }
                else if (finding.Severity == Severity.Info)
                    notices.Add("INFO: " + finding.Message);
            }
            if (notices.Count == 0) return ("", warned);
            return (string.Join(" | ", notices), warned);
        }
    }
}
