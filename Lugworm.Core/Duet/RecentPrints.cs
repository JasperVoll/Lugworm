using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>The machine and profile a run was made on, or is about to be made on.
    /// Recorded with every archived job so a reprint can say what has changed since.</summary>
    public sealed class ReprintContext
    {
        public string Host { get; }
        /// <summary>The machine identity the session expected, "" when none was recorded.</summary>
        public string Fingerprint { get; }
        public string ProfileId { get; }
        /// <summary>Kinematics: a job planned for a SCARA is not a job for a Cartesian.</summary>
        public string Mechanism { get; }
        /// <summary>Transport identifier recorded with the archived job.</summary>
        public string Transport { get; }

        public ReprintContext(string? host, string? fingerprint, string? profileId,
                              string? mechanism, string? transport)
        {
            Host = host ?? "";
            Fingerprint = fingerprint ?? "";
            ProfileId = profileId ?? "";
            Mechanism = mechanism ?? "";
            Transport = transport ?? "";
        }

        public static readonly ReprintContext Unknown = new ReprintContext("", "", "", "", "");
    }

    /// <summary>One past run as the Job page lists it: name, time, outcome, machine - plus
    /// whether its exact bytes are still on disk, which is the only thing that makes a
    /// reprint possible.</summary>
    /// <remarks>A remote file name is not an identity (<c>print.gcode</c> is overwritten on
    /// every upload) and neither is a job name (two runs can share one). An entry is
    /// identified by <see cref="Id"/> - the retention log id of that run - and its content
    /// by <see cref="GcodeSha256"/>.</remarks>
    public sealed class RecentPrintEntry
    {
        public string Id { get; }
        /// <summary>The <see cref="JobSnapshot.RunId"/> this run was started with, "" when
        /// it was started outside this session or recorded before job archives existed.</summary>
        public string RunId { get; }
        public string JobName { get; }
        public DateTime StartedUtc { get; }
        /// <summary>How the RUN ended, null when unknown. Never render null as success:
        /// a version-1 manifest and an interrupted session both report it.</summary>
        public string? Outcome { get; }
        public ReprintContext Machine { get; }
        public bool DryRun { get; }
        /// <summary>SHA-256 of the archived bytes, "" when no archive was written.</summary>
        public string GcodeSha256 { get; }
        public long GcodeBytes { get; }
        /// <summary>True when this run recorded an immutable copy of its G-code AND that
        /// copy is still where the manifest says it is.</summary>
        public bool ArchiveAvailable { get; }
        /// <summary>Why the archive cannot be used, or null when it can. Shown on the row,
        /// so an old metadata-only log is visible and explains itself instead of vanishing.</summary>
        public string? ArchiveNote { get; }
        public LogRetentionState State { get; }
        /// <summary>Directory holding this run's files. Opaque to Core.</summary>
        public string Directory { get; }

        public RecentPrintEntry(string id, string? jobName, DateTime startedUtc, string? outcome,
                                ReprintContext? machine, bool dryRun, string? gcodeSha256, long gcodeBytes,
                                bool archiveAvailable, string? archiveNote, LogRetentionState state,
                                string? directory, string? runId = null)
        {
            Id = id ?? "";
            RunId = runId ?? "";
            JobName = string.IsNullOrWhiteSpace(jobName) ? "(unnamed job)" : jobName!;
            StartedUtc = startedUtc;
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome;
            Machine = machine ?? ReprintContext.Unknown;
            DryRun = dryRun;
            GcodeSha256 = gcodeSha256 ?? "";
            GcodeBytes = gcodeBytes;
            ArchiveAvailable = archiveAvailable;
            ArchiveNote = archiveNote;
            State = state;
            Directory = directory ?? "";
        }

        /// <summary>Outcome as a person reads it. "complete" of a log is not a finished
        /// print, so an unrecorded outcome says "unknown", never "ok".</summary>
        public string OutcomeText => Outcome ?? "outcome unknown";

        /// <summary>Short machine description for the row: host plus whatever identity was
        /// recorded with the run.</summary>
        public string MachineText
        {
            get
            {
                string host = Machine.Host.Length > 0 ? Machine.Host : "unknown host";
                string id = Machine.Fingerprint.Length > 0 ? Machine.Fingerprint
                          : Machine.ProfileId.Length > 0 ? Machine.ProfileId : "";
                return id.Length > 0 ? host + " (" + Short(id) + ")" : host;
            }
        }

        public string ShortHash => Short(GcodeSha256);

        static string Short(string value)
            => value.Length > 12 ? value.Substring(0, 12) : value;

        public override string ToString()
            => Id + " " + JobName + " " + StartedUtc.ToString("o", CultureInfo.InvariantCulture)
             + " " + OutcomeText + (ArchiveAvailable ? " [archived " + ShortHash + "]" : " [no archive]");
    }

    /// <summary>Builds the Recent prints list. Pure: ordering and the cap only.</summary>
    public static class RecentPrints
    {
        /// <summary>How many runs the Job page lists.</summary>
        public const int DefaultCap = 10;

        /// <summary>Newest first, at most <paramref name="cap"/>, one row per run id.
        /// Two runs of the same file with the same name are two rows; they differ by id
        /// (and usually by start time), never by name.</summary>
        public static IReadOnlyList<RecentPrintEntry> Order(IEnumerable<RecentPrintEntry>? entries, int cap = DefaultCap)
        {
            var all = new List<RecentPrintEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e == null || e.Id.Length == 0) continue;
                    if (!seen.Add(e.Id)) continue;
                    all.Add(e);
                }
            all.Sort((a, b) =>
            {
                int byTime = b.StartedUtc.CompareTo(a.StartedUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(b.Id, a.Id);
            });
            if (cap > 0 && all.Count > cap) all.RemoveRange(cap, all.Count - cap);
            return all;
        }

        /// <summary>The entry with this id, or null. Selection is by id, never by name.</summary>
        public static RecentPrintEntry? Find(IReadOnlyList<RecentPrintEntry>? entries, string? id)
        {
            if (entries == null || string.IsNullOrEmpty(id)) return null;
            foreach (var e in entries) if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
            return null;
        }
    }

    /// <summary>Whether a past run may be prepared again, and what has changed since it
    /// ran. Pure and total: every refusal is a sentence the operator can act on.</summary>
    /// <remarks>Reprinting is NOT a new command. A prepared history job takes exactly the
    /// existing Validate -> verified Upload -> explicit Start path, with <c>JobSafety</c>,
    /// <c>JobGate</c> and the upload identity proof unchanged. This policy only decides
    /// whether those bytes may be prepared at all.</remarks>
    public static class ReprintPolicy
    {
        /// <summary>Why this run cannot be loaded for a reprint, or null when it can.
        /// A run without an archive stays VISIBLE in the list - it just cannot be run.</summary>
        public static string? RefuseSelection(RecentPrintEntry? entry)
        {
            if (entry == null) return "no run selected";
            if (!entry.ArchiveAvailable)
                return entry.ArchiveNote
                    ?? "this run has no archived G-code, so it cannot be printed again - "
                     + "its log is still readable on the Logs page";
            if (entry.GcodeSha256.Length == 0)
                return "this run recorded no G-code hash, so its archive cannot be verified";
            if (entry.GcodeBytes <= 0) return "this run's archived G-code is empty";
            return null;
        }

        /// <summary>Why the SELECTED run must not be prepared for THIS machine, or null.
        /// Refuses only on facts that change what the G-code means: a different kinematic
        /// mechanism or a different transport dialect. A different host or a different
        /// machine identity is a warning, because moving a job to a second identical
        /// machine is ordinary and the operator can see it.</summary>
        public static string? RefuseReprint(RecentPrintEntry? entry, ReprintContext? now)
        {
            string? selection = RefuseSelection(entry);
            if (selection != null) return selection;
            if (now == null) return null;
            var was = entry!.Machine;
            if (was.Mechanism.Length > 0 && now.Mechanism.Length > 0
                && !string.Equals(was.Mechanism, now.Mechanism, StringComparison.OrdinalIgnoreCase))
                return "this run was made on a " + was.Mechanism.ToLowerInvariant() + " machine and this profile is "
                     + now.Mechanism.ToLowerInvariant() + " - the G-code is not interchangeable";
            if (was.Transport.Length > 0 && now.Transport.Length > 0
                && !string.Equals(was.Transport, now.Transport, StringComparison.OrdinalIgnoreCase))
                return "this run was made through " + was.Transport + " and this session speaks "
                     + now.Transport + " - the G-code dialect differs";
            return null;
        }

        /// <summary>What changed since the run that the operator should see before starting
        /// it again, or null. Never a refusal.</summary>
        public static string? Warn(RecentPrintEntry? entry, ReprintContext? now)
        {
            if (entry == null || now == null) return null;
            var was = entry.Machine;
            var notes = new List<string>();
            if (was.Host.Length > 0 && now.Host.Length > 0
                && !string.Equals(was.Host, now.Host, StringComparison.OrdinalIgnoreCase))
                notes.Add("it ran on " + was.Host);
            if (was.Fingerprint.Length > 0 && now.Fingerprint.Length > 0
                && !string.Equals(was.Fingerprint, now.Fingerprint, StringComparison.Ordinal))
                notes.Add("the machine identity has changed since");
            if (was.ProfileId.Length > 0 && now.ProfileId.Length > 0
                && !string.Equals(was.ProfileId, now.ProfileId, StringComparison.OrdinalIgnoreCase))
                notes.Add("it was planned with the " + was.ProfileId + " profile");
            if (entry.DryRun) notes.Add("it was a dry run (M37), so it never deposited material");
            if (notes.Count == 0) return null;
            return "check before starting: " + string.Join("; ", notes);
        }

        /// <summary>Verifies bytes read back from the archive against what the run recorded.
        /// A mismatch refuses: the point of the archive is that the reprint is the SAME job,
        /// and a file that no longer hashes to its record is not evidence of anything.</summary>
        public static string? VerifyBytes(RecentPrintEntry? entry, byte[]? bytes)
        {
            if (entry == null) return "no run selected";
            if (bytes == null) return "the archived G-code of " + entry.Id + " could not be read";
            if (bytes.Length == 0) return "the archived G-code of " + entry.Id + " is empty";
            if (entry.GcodeBytes > 0 && bytes.LongLength != entry.GcodeBytes)
                return "the archived G-code of " + entry.Id + " is "
                     + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes, not the "
                     + entry.GcodeBytes.ToString(CultureInfo.InvariantCulture) + " bytes recorded for the run";
            string actual = JobSnapshot.Sha256Hex(bytes);
            if (entry.GcodeSha256.Length > 0 && !string.Equals(actual, entry.GcodeSha256, StringComparison.Ordinal))
                return "the archived G-code of " + entry.Id + " does not match the hash recorded for the run ("
                     + Short(actual) + " instead of " + entry.ShortHash + ") - refusing to print it";
            return null;
        }

        static string Short(string value) => value.Length > 12 ? value.Substring(0, 12) : value;
    }
}
