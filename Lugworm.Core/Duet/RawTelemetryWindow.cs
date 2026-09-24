using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>The untouched response body of one status/coordinate request, handed out
    /// by the transport BEFORE parsing. Carries no bound of its own: the subscriber
    /// (the session's <see cref="RawTelemetryWindow"/>) owns the truncation and the ring,
    /// so the transport never accumulates diagnostics nobody reads.</summary>
    public sealed class RawBodyProbe
    {
        public string Endpoint { get; }
        public string ApiMode { get; }
        public string Body { get; }
        public DateTime RequestUtc { get; }
        public DateTime ReceivedUtc { get; }
        public long ConnectionGeneration { get; }
        /// <summary>Page index within a stitched rr_model array; 1 for a single response.</summary>
        public int Page { get; }

        public RawBodyProbe(string endpoint, string apiMode, string body, DateTime requestUtc,
                            DateTime receivedUtc, long connectionGeneration, int page)
        {
            Endpoint = endpoint ?? "";
            ApiMode = apiMode ?? "";
            Body = body ?? "";
            RequestUtc = requestUtc;
            ReceivedUtc = receivedUtc;
            ConnectionGeneration = connectionGeneration;
            Page = page;
        }
    }

    /// <summary>One raw per-poll observation, as close to the wire as the client can offer.</summary>
    /// <remarks>The 2026-09-09 dome capture could not be localized because the HTTP bodies
    /// were gone by the time the CSV showed the excursion. This record exists so the next
    /// occurrence carries the response text, the endpoint, both request timestamps and the
    /// ids needed to line the stages up: response -> parsed snapshot -> recorder -> live.</remarks>
    public sealed class RawTelemetryRecord
    {
        public long AcquisitionId;
        public long PollId;
        public long ConnectionGeneration = -1;
        public DateTime RequestUtc;
        public DateTime ReceivedUtc;
        /// <summary>Request path, e.g. "rr_model?key=move.axes&amp;flags=vnd4".</summary>
        public string Endpoint = "";
        /// <summary>Which RRF protocol shape produced it: "rr_model" or "rr_status".</summary>
        public string ApiMode = "";
        /// <summary>Response body, truncated to the window's cap.</summary>
        public string Body = "";
        /// <summary>Length of the body BEFORE truncation, characters.</summary>
        public int BodyLength;
        public bool BodyTruncated;
        /// <summary>Page index within a stitched rr_model array (1 for a single page).</summary>
        public int Page = 1;

        // ---- what the parse made of it ------------------------------------------------
        public string PositionValidity = "";
        public string? PositionInvalidReason;
        public Vec3? MachinePosition;
        public Vec3? UserPosition;
        public bool AllHomed;
        public string HomedFlags = "";
        public string? MachineStatus;
        public string? JobFile;
        public long? FilePosition;
        public double? FractionPrinted;
        public double? RequestedSpeedMms;
        public double? TopSpeedMms;
        /// <summary>Arm configuration at this poll, -1 when unknown.</summary>
        public int ArmMode = -1;
        public bool ArmKnown;
        /// <summary>Continuity verdict, when one had been reached for this observation.</summary>
        public string? Quality;
        public long SegmentId;
        public string? RunId;
        /// <summary>Free text for a non-poll event: a transport exception, a reconnect
        /// completion, an explicit operator capture request.</summary>
        public string? Note;

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            Num(sb, "acq", AcquisitionId, first: true);
            Num(sb, "poll", PollId);
            Num(sb, "gen", ConnectionGeneration);
            Time(sb, "req", RequestUtc);
            Time(sb, "rcv", ReceivedUtc);
            Str(sb, "endpoint", Endpoint);
            Str(sb, "api", ApiMode);
            Num(sb, "page", Page);
            Num(sb, "bodyLen", BodyLength);
            Bool(sb, "bodyTruncated", BodyTruncated);
            Str(sb, "body", Body);
            Str(sb, "positionValidity", PositionValidity);
            Str(sb, "positionInvalidReason", PositionInvalidReason);
            Point(sb, "machine", MachinePosition);
            Point(sb, "user", UserPosition);
            Bool(sb, "allHomed", AllHomed);
            Str(sb, "homed", HomedFlags);
            Str(sb, "status", MachineStatus);
            Str(sb, "jobFile", JobFile);
            Num(sb, "filePosition", FilePosition);
            Real(sb, "fractionPrinted", FractionPrinted);
            Real(sb, "requestedSpeed", RequestedSpeedMms);
            Real(sb, "topSpeed", TopSpeedMms);
            Num(sb, "armMode", ArmMode);
            Bool(sb, "armKnown", ArmKnown);
            Str(sb, "quality", Quality);
            Num(sb, "segment", SegmentId);
            Str(sb, "runId", RunId);
            Str(sb, "note", Note);
            sb.Append('}');
            return sb.ToString();
        }

        static void Comma(StringBuilder sb, bool first) { if (!first) sb.Append(','); }
        static void Num(StringBuilder sb, string key, long value, bool first = false)
        { Comma(sb, first); sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture)); }
        static void Num(StringBuilder sb, string key, long? value)
        { if (!value.HasValue) return; Num(sb, key, value.Value); }
        static void Real(StringBuilder sb, string key, double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return;
            sb.Append(",\"").Append(key).Append("\":").Append(value.Value.ToString("0.##########", CultureInfo.InvariantCulture));
        }
        static void Bool(StringBuilder sb, string key, bool value)
        { sb.Append(",\"").Append(key).Append("\":").Append(value ? "true" : "false"); }
        static void Str(StringBuilder sb, string key, string? value)
        {
            if (value == null) return;
            sb.Append(",\"").Append(key).Append("\":");
            JsonText.Quote(sb, value);
        }
        static void Time(StringBuilder sb, string key, DateTime value)
        {
            if (value == default(DateTime)) return;
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            Str(sb, key, utc.ToString("o", CultureInfo.InvariantCulture));
        }
        static void Point(StringBuilder sb, string key, Vec3? value)
        {
            if (!value.HasValue) return;
            var v = value.Value;
            sb.Append(",\"").Append(key).Append("\":[")
              .Append(v.X.ToString("0.##########", CultureInfo.InvariantCulture)).Append(',')
              .Append(v.Y.ToString("0.##########", CultureInfo.InvariantCulture)).Append(',')
              .Append(v.Z.ToString("0.##########", CultureInfo.InvariantCulture)).Append(']');
        }
    }

    /// <summary>A finished diagnostic capture: the records around one trigger.</summary>
    public sealed class RawTelemetryCapture
    {
        public string Reason { get; }
        public DateTime TriggeredUtc { get; }
        public IReadOnlyList<RawTelemetryRecord> Records { get; }
        /// <summary>Records that rolled out of the ring before the trigger fired, so a
        /// short window is never mistaken for a quiet machine.</summary>
        public long OverwrittenBefore { get; }

        public RawTelemetryCapture(string reason, DateTime triggeredUtc,
                                   IReadOnlyList<RawTelemetryRecord> records, long overwrittenBefore)
        {
            Reason = reason ?? "";
            TriggeredUtc = triggeredUtc;
            Records = records ?? Array.Empty<RawTelemetryRecord>();
            OverwrittenBefore = overwrittenBefore;
        }

        /// <summary>One JSON object per line: a header, then every record in order.</summary>
        public IReadOnlyList<string> ToJsonLines()
        {
            var lines = new List<string>(Records.Count + 1);
            var head = new StringBuilder();
            head.Append("{\"capture\":");
            JsonText.Quote(head, Reason);
            head.Append(",\"triggeredUtc\":");
            JsonText.Quote(head, (TriggeredUtc.Kind == DateTimeKind.Utc ? TriggeredUtc : TriggeredUtc.ToUniversalTime())
                .ToString("o", CultureInfo.InvariantCulture));
            head.Append(",\"records\":").Append(Records.Count.ToString(CultureInfo.InvariantCulture));
            head.Append(",\"overwrittenBefore\":").Append(OverwrittenBefore.ToString(CultureInfo.InvariantCulture));
            head.Append('}');
            lines.Add(head.ToString());
            foreach (var r in Records) lines.Add(r.ToJson());
            return lines;
        }
    }

    /// <summary>Bounded ring of the most recent raw per-poll observations, with a
    /// pre/post trigger window.</summary>
    /// <remarks>
    /// <para>Every poll is offered, not every fifth: the dome spikes sit between exported
    /// CSV rows, so a sampled ring would have missed the neighbours that prove the
    /// excursion is isolated. Boundedness comes from the ring size and the per-body
    /// character cap, both fixed at construction, so the memory cost cannot grow with the
    /// length of a print.</para>
    /// <para><see cref="Trigger"/> copies the ring as the PRE window and then collects the
    /// next <c>post</c> records; the capture is handed to the sink when the post window
    /// fills, or by <see cref="Flush"/> when the job or session ends first. The sink must
    /// not block - the production one enqueues to the retention worker - because
    /// <see cref="Add"/> runs on the poll thread.</para>
    /// <para>Not thread-safe against concurrent Add; guard it with the caller's own lock
    /// when two producers exist.</para>
    /// </remarks>
    public sealed class RawTelemetryWindow
    {
        public const int DefaultCapacity = 200;
        public const int DefaultMaxBodyChars = 4096;
        public const int DefaultPostTriggerRecords = 40;

        readonly int _capacity;
        readonly int _maxBodyChars;
        readonly int _postTrigger;
        readonly Queue<RawTelemetryRecord> _ring;
        readonly Action<RawTelemetryCapture>? _sink;

        List<RawTelemetryRecord>? _pending;
        string _pendingReason = "";
        DateTime _pendingUtc;
        long _pendingOverwritten;
        int _pendingRemaining;
        long _overwritten;
        long _captures;

        public RawTelemetryWindow(Action<RawTelemetryCapture>? sink = null,
                                  int capacity = DefaultCapacity,
                                  int maxBodyChars = DefaultMaxBodyChars,
                                  int postTriggerRecords = DefaultPostTriggerRecords)
        {
            _capacity = Math.Max(1, capacity);
            _maxBodyChars = Math.Max(0, maxBodyChars);
            _postTrigger = Math.Max(0, postTriggerRecords);
            _ring = new Queue<RawTelemetryRecord>(_capacity);
            _sink = sink;
        }

        public int Capacity => _capacity;
        public int MaxBodyChars => _maxBodyChars;
        public int Count => _ring.Count;
        /// <summary>Records dropped off the back of the ring without ever being captured.</summary>
        public long Overwritten => _overwritten;
        /// <summary>Captures handed to the sink.</summary>
        public long Captures => _captures;
        public bool CapturePending => _pending != null;

        public void Add(RawTelemetryRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Body != null && record.Body.Length > _maxBodyChars)
            {
                record.BodyLength = record.Body.Length;
                record.Body = _maxBodyChars > 0 ? record.Body.Substring(0, _maxBodyChars) : "";
                record.BodyTruncated = true;
            }
            else if (record.BodyLength == 0)
            {
                record.BodyLength = record.Body?.Length ?? 0;
            }

            _ring.Enqueue(record);
            while (_ring.Count > _capacity) { _ring.Dequeue(); _overwritten++; }

            if (_pending == null) return;
            _pending.Add(record);
            if (--_pendingRemaining <= 0) Complete();
        }

        /// <summary>Arms a capture around the records seen so far. A second trigger while
        /// one is pending only extends the post window - the interesting stretch stays one
        /// capture instead of becoming a storm of overlapping ones.</summary>
        public void Trigger(string reason, DateTime utc)
        {
            if (_pending != null)
            {
                _pendingRemaining = Math.Max(_pendingRemaining, _postTrigger);
                if (!string.IsNullOrEmpty(reason) && _pendingReason.IndexOf(reason, StringComparison.Ordinal) < 0)
                    _pendingReason = _pendingReason + "; " + reason;
                return;
            }
            _pending = new List<RawTelemetryRecord>(_ring);
            _pendingReason = reason ?? "";
            _pendingUtc = utc;
            _pendingOverwritten = _overwritten;
            _pendingRemaining = _postTrigger;
            if (_pendingRemaining <= 0) Complete();
        }

        /// <summary>Hands an incomplete capture to the sink - the job ended or the session
        /// is closing before the post window filled. A truncated window is still evidence.</summary>
        public void Flush()
        {
            if (_pending != null) Complete();
        }

        /// <summary>The current ring contents, oldest first. For tests and for an explicit
        /// operator request that wants the window without arming a post capture.</summary>
        public IReadOnlyList<RawTelemetryRecord> Snapshot() => new List<RawTelemetryRecord>(_ring);

        void Complete()
        {
            var capture = new RawTelemetryCapture(_pendingReason, _pendingUtc, _pending!, _pendingOverwritten);
            _pending = null;
            _pendingReason = "";
            _pendingRemaining = 0;
            _captures++;
            if (_sink != null)
            {
                try { _sink(capture); }
                catch (Exception) { /* diagnostics must never break the poll loop */ }
            }
        }
    }
}
