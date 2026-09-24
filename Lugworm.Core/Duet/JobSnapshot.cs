using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Lugworm.Core.Duet
{
    /// <summary>Where the bytes of a job came from. Decides whether an intended path
    /// exists at all.</summary>
    public enum JobSource
    {
        /// <summary>Sliced or wired into the Console and uploaded from here: the exact
        /// bytes are known.</summary>
        ConsoleInput,
        /// <summary>A file already on the machine that was read back and verified.</summary>
        MachineFile,
        /// <summary>Started outside this session (the machine's own screen, another
        /// client). The bytes are NOT known until the file is retrieved and verified, so
        /// the intended path is unavailable - never substitute the Console's input.</summary>
        External,
    }

    /// <summary>Immutable identity of one job, shared by every consumer that needs to
    /// agree on "which print is this".</summary>
    /// <remarks>
    /// <para>A file name is not an identity: <c>print.gcode</c> is overwritten on every
    /// upload, and the same file printed twice is two runs. <see cref="RunId"/> is unique
    /// per run; <see cref="Sha256"/> identifies the content.</para>
    /// <para>The bytes are copied in and handed back only as copies, so a consumer cannot
    /// mutate the record another consumer is comparing against.</para>
    /// </remarks>
    public sealed class JobSnapshot
    {
        static long _runCounter;

        readonly byte[] _gcode;
        readonly object _linesSync = new object();
        IReadOnlyList<string>? _lines;

        /// <summary>Unique id of this RUN. Two runs of identical bytes have different ids.</summary>
        public string RunId { get; }
        /// <summary>Lower-case hex SHA-256 of the exact bytes; "" when they are unknown.</summary>
        public string Sha256 { get; }
        public long ByteCount { get; }
        /// <summary>Revision of the parsed intended path derived from these bytes. Bumped
        /// by the producer when it re-parses; consumers cache against it instead of
        /// re-parsing per poll.</summary>
        public long PathRevision { get; }
        /// <summary>Path on the machine, when known.</summary>
        public string? RemotePath { get; }
        public JobSource Source { get; }
        public DateTime CreatedUtc { get; }
        /// <summary>False when no intended path can be derived - an externally started
        /// job whose file has not been retrieved. A consumer must then report the
        /// comparison as unavailable, never fall back to an unrelated input.</summary>
        public bool IntendedPathAvailable => _gcode.Length > 0;

        JobSnapshot(string runId, byte[] gcode, string sha256, long pathRevision,
                    string? remotePath, JobSource source, DateTime createdUtc)
        {
            RunId = runId;
            _gcode = gcode;
            Sha256 = sha256;
            ByteCount = gcode.LongLength;
            PathRevision = pathRevision;
            RemotePath = remotePath;
            Source = source;
            CreatedUtc = createdUtc;
        }

        /// <summary>A copy of the exact bytes; empty when the job has none.</summary>
        public byte[] GcodeBytes()
        {
            var copy = new byte[_gcode.Length];
            Array.Copy(_gcode, copy, _gcode.Length);
            return copy;
        }

        /// <summary>The exact bytes as G-code LINES, decoded once and cached on this
        /// snapshot. What the Console publishes on its <c>gcode</c> output, so a downstream
        /// definition reads the job that is really prepared or running rather than
        /// re-deriving it from an input that may since have changed.</summary>
        /// <remarks>Lazy on purpose: an externally started job has no bytes and a hidden
        /// output is never asked, so a 2.6 MB print is decoded only when somebody looks.
        /// The result is immutable and shared - a poll that re-projects the outputs hands
        /// out the same list instance, and the expire policy compares
        /// <see cref="Sha256"/> rather than 58,000 strings.</remarks>
        public IReadOnlyList<string> Lines()
        {
            var cached = _lines;
            if (cached != null) return cached;
            lock (_linesSync)
            {
                if (_lines != null) return _lines;
                _lines = Split(_gcode);
                return _lines;
            }
        }

        bool _layerTotalKnown;
        int? _layerTotal;

        /// <summary>The job's total layer count from its own <c>; layer N/M</c> markers,
        /// scanned once and cached like <see cref="Lines"/>; null for a job without bytes
        /// or without markers.</summary>
        public int? LayerTotal()
        {
            lock (_linesSync)
            {
                if (_layerTotalKnown) return _layerTotal;
            }
            int? total = _gcode.Length == 0 ? null : Analysis.GcodeLayerMarkers.TotalLayers(Lines());
            lock (_linesSync)
            {
                _layerTotal = total;
                _layerTotalKnown = true;
                return total;
            }
        }

        static IReadOnlyList<string> Split(byte[] bytes)
        {
            if (bytes.Length == 0) return Array.Empty<string>();
            // The bytes were produced from UTF-8 text (or read back from the machine as
            // such); a byte that is not valid UTF-8 is replaced rather than throwing,
            // because refusing to show a job is worse than showing one odd character.
            string text = new UTF8Encoding(false, false).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var lines = new List<string>(Math.Max(4, text.Length / 24));
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start).TrimEnd('\r'));
            return lines;
        }

        /// <summary>True when another snapshot describes the same CONTENT (not the same run).</summary>
        public bool SameContent(JobSnapshot? other)
            => other != null && ByteCount == other.ByteCount
            && Sha256.Length > 0 && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);

        /// <summary>A run of known bytes.</summary>
        public static JobSnapshot FromBytes(byte[] gcode, string? remotePath, JobSource source,
                                            DateTime createdUtc, long pathRevision = 1)
        {
            if (gcode == null) throw new ArgumentNullException(nameof(gcode));
            var copy = new byte[gcode.Length];
            Array.Copy(gcode, copy, gcode.Length);
            return new JobSnapshot(NextRunId(createdUtc), copy, HashOf(copy), pathRevision,
                                   Normalize(remotePath), source, createdUtc);
        }

        /// <summary>A run whose bytes are not available - started outside this session.
        /// Its intended path is unavailable until the file is retrieved and verified.</summary>
        public static JobSnapshot External(string? remotePath, DateTime createdUtc)
            => new JobSnapshot(NextRunId(createdUtc), Array.Empty<byte>(), "", 0,
                               Normalize(remotePath), JobSource.External, createdUtc);

        /// <summary>The same content prepared again as a NEW run (Print again), so history
        /// and logs never merge two prints of one file.</summary>
        public JobSnapshot AsNewRun(DateTime createdUtc)
            => new JobSnapshot(NextRunId(createdUtc), _gcode, Sha256, PathRevision,
                               RemotePath, Source, createdUtc);

        /// <summary>The same run with a re-parsed intended path.</summary>
        public JobSnapshot WithPathRevision(long revision)
            => new JobSnapshot(RunId, _gcode, Sha256, revision, RemotePath, Source, CreatedUtc);

        /// <summary>The same run bound to the remote path it was uploaded to.</summary>
        public JobSnapshot WithRemotePath(string? remotePath)
            => new JobSnapshot(RunId, _gcode, Sha256, PathRevision, Normalize(remotePath), Source, CreatedUtc);

        public override string ToString()
            => RunId + " " + (RemotePath ?? "(no remote path)") + " "
             + ByteCount.ToString(CultureInfo.InvariantCulture) + " B "
             + (Sha256.Length >= 12 ? Sha256.Substring(0, 12) : "(no hash)");

        static string? Normalize(string? path)
            => string.IsNullOrWhiteSpace(path) ? null : path!.Trim();

        static string NextRunId(DateTime createdUtc)
        {
            long n = Interlocked.Increment(ref _runCounter);
            DateTime utc = createdUtc.Kind == DateTimeKind.Utc ? createdUtc : createdUtc.ToUniversalTime();
            return utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                 + "-r" + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Lower-case hex SHA-256 of these bytes, "" for none. One implementation,
        /// so a job's identity and the verification of an archived copy of it cannot
        /// drift apart.</summary>
        public static string Sha256Hex(byte[]? bytes) => HashOf(bytes ?? Array.Empty<byte>());

        static string HashOf(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// <summary>Keeps the job being PREPARED apart from the job actually PRINTING.</summary>
    /// <remarks>Editing the Console's G-code input during a print must change what a later
    /// Start would run, and nothing else: the active run keeps the snapshot it started
    /// with, so the live intended path, the job log and any deviation comparison stay
    /// bound to the bytes on the machine. Selecting an older job from history is a
    /// preparation too, never an execution.</remarks>
    public sealed class JobIdentityTracker
    {
        readonly object _sync = new object();
        JobSnapshot? _prepared;
        JobSnapshot? _active;

        /// <summary>The job a Start would run, or null when none is prepared.</summary>
        public JobSnapshot? Prepared { get { lock (_sync) return _prepared; } }
        /// <summary>The job the machine is running, or null when nothing is printing.</summary>
        public JobSnapshot? Active { get { lock (_sync) return _active; } }
        /// <summary>The job whose intended path consumers should show: the active one while
        /// a print runs, otherwise the prepared one.</summary>
        public JobSnapshot? Effective { get { lock (_sync) return _active ?? _prepared; } }

        /// <summary>New bytes wired into the Console. Never touches the active run.</summary>
        public void Prepare(JobSnapshot? job)
        {
            lock (_sync) _prepared = job;
        }

        /// <summary>This session started the prepared job. Returns the snapshot that became
        /// active, or null when nothing was prepared.</summary>
        public JobSnapshot? StartPrepared(DateTime startedUtc)
        {
            lock (_sync)
            {
                if (_prepared == null) return null;
                // A repeat print of identical bytes is a different run.
                _active = _active != null && _active.SameContent(_prepared)
                    ? _prepared.AsNewRun(startedUtc) : _prepared;
                return _active;
            }
        }

        /// <summary>The send that was to start the prepared job failed: nothing is running,
        /// the prepared job stays prepared.</summary>
        public void AbortStart()
        {
            lock (_sync) _active = null;
        }

        /// <summary>A job appeared that this session did not start. Its bytes - and so its
        /// intended path - are unavailable until the file is retrieved and verified.</summary>
        public JobSnapshot StartExternal(string? remotePath, DateTime startedUtc)
        {
            lock (_sync)
            {
                _active = JobSnapshot.External(remotePath, startedUtc);
                return _active;
            }
        }

        /// <summary>The external run's file was retrieved and verified; it now has a path.</summary>
        public JobSnapshot? ResolveExternal(byte[] gcode, string? remotePath, DateTime utc)
        {
            lock (_sync)
            {
                if (_active == null) return null;
                var resolved = JobSnapshot.FromBytes(gcode, remotePath ?? _active.RemotePath,
                                                     JobSource.MachineFile, utc);
                _active = resolved;
                return resolved;
            }
        }

        /// <summary>The run ended (finished, cancelled, connection lost). The prepared job
        /// survives: it is what a Start would run next.</summary>
        public void EndRun()
        {
            lock (_sync) _active = null;
        }
    }
}
