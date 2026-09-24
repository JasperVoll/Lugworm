using System;
using System.Globalization;
using System.Threading;

namespace Lugworm.Core.Duet
{
    /// <summary>Why a snapshot carries no usable X/Y/Z position. Anything other than
    /// <see cref="Valid"/> means the coordinates must not be treated as a measurement:
    /// no trail point, no derived speed, no path comparison, no motion gate.</summary>
    /// <remarks>The 2026-09-09 dome incident is the reason this exists as an explicit
    /// state rather than a silent zero. <see cref="JsonValue.AsNumber"/> maps a missing,
    /// string or object field to its default and a boolean to 0/1, so before this a
    /// response that simply omitted <c>machinePosition</c> produced a perfectly
    /// well-formed sample at the machine origin.</remarks>
    public enum PositionValidity
    {
        /// <summary>X, Y and Z were each reported exactly once as finite JSON numbers.</summary>
        Valid,
        /// <summary>No axis data at all (no array, or an empty one).</summary>
        Unreported,
        /// <summary>At least one of X/Y/Z is absent from the reported axes.</summary>
        MissingAxis,
        /// <summary>An X/Y/Z coordinate was present but not a JSON number - a string,
        /// a boolean, an object or an explicit null.</summary>
        NotNumeric,
        /// <summary>The same axis letter was reported more than once, so which entry
        /// describes the head is undecidable.</summary>
        DuplicateAxis,
    }

    /// <summary>Timing and identity of ONE acquisition, stamped by the transport.
    /// The timestamps belong to the request that carried the COORDINATES, not to the
    /// completion of the surrounding parse or of any cosmetic metadata fetched
    /// afterwards.</summary>
    public sealed class AcquisitionStamp
    {
        /// <summary>Process-monotonic id. Strictly increasing; a consumer that sees a
        /// smaller id than the one it already published is looking at an older
        /// acquisition and must not let it overwrite the newer one.</summary>
        public long AcquisitionId;
        /// <summary>When the coordinate request was issued.</summary>
        public DateTime RequestStartedUtc;
        /// <summary>When the coordinate response was received (before parsing, before
        /// any follow-up metadata request).</summary>
        public DateTime ResponseReceivedUtc;
        /// <summary>Transport connection generation this acquisition belongs to; -1 when
        /// the transport does not report one.</summary>
        public long ConnectionGeneration = -1;
        /// <summary>True when state, axes and job did NOT come from one response - the
        /// modern rr_model path issues them sequentially, so the parts can describe
        /// different instants. A legacy rr_status poll is one atomic response.</summary>
        public bool Composite;
        /// <summary>Pages the coordinate array was stitched from (rr_model "next"
        /// continuation). More than one page means the array itself is composite.</summary>
        public int CoordinatePages = 1;

        /// <summary>Round-trip of the coordinate request in seconds, or NaN when the
        /// stamps are not both set.</summary>
        public double RoundTripSeconds
            => RequestStartedUtc == default(DateTime) || ResponseReceivedUtc == default(DateTime)
               ? double.NaN
               : Math.Max(0.0, (ResponseReceivedUtc - RequestStartedUtc).TotalSeconds);

        public AcquisitionStamp Copy() => new AcquisitionStamp
        {
            AcquisitionId = AcquisitionId,
            RequestStartedUtc = RequestStartedUtc,
            ResponseReceivedUtc = ResponseReceivedUtc,
            ConnectionGeneration = ConnectionGeneration,
            Composite = Composite,
            CoordinatePages = CoordinatePages,
        };
    }

    /// <summary>The one source of acquisition ids in the process. Ids are never reused
    /// and never reset, so they stay comparable across reconnects and across the two
    /// snapshot producers (the poll loop and a gated command).</summary>
    public static class AcquisitionCounter
    {
        static long _next;
        public static long Next() => Interlocked.Increment(ref _next);
        /// <summary>The last id handed out; 0 before the first acquisition.</summary>
        public static long Current => Interlocked.Read(ref _next);
    }

    /// <summary>Strict reading of the axis fields that make up a position. Separate from
    /// <see cref="ObjectModelParser"/> so the rule can be tested on its own and so the
    /// lenient <see cref="JsonValue"/> accessors stay available for the cosmetic fields
    /// (job names, durations, temperatures) where a default really is harmless.</summary>
    public static class AxisReader
    {
        /// <summary>The three axes a position needs, in the order a report must contain them.</summary>
        public static readonly string[] Required = { "X", "Y", "Z" };

        /// <summary>True when <paramref name="value"/> is a JSON number with a finite
        /// value. A boolean is NOT a number here: RRF never reports a coordinate as
        /// true/false, and accepting it would turn a protocol change into a position of
        /// 0 or 1 mm.</summary>
        public static bool IsCoordinate(JsonValue? value)
            => value != null && value.Kind == JsonKind.Number
               && !double.IsNaN(value.AsNumber()) && !double.IsInfinity(value.AsNumber());

        /// <summary>Validates a completed axis list. Returns <see cref="PositionValidity.Valid"/>
        /// only when X, Y and Z each appear exactly once.</summary>
        /// <param name="letters">Axis letters in report order, including the ones whose
        /// coordinates were rejected - the caller passes what the response CONTAINED so
        /// a duplicate can still be detected after a rejected entry was dropped.</param>
        /// <param name="accepted">Letters whose machine and user coordinates were both
        /// accepted as finite numbers.</param>
        public static PositionValidity Validate(System.Collections.Generic.IReadOnlyList<string> letters,
                                                System.Collections.Generic.IReadOnlyCollection<string> accepted,
                                                out string? reason)
        {
            reason = null;
            if (letters == null || letters.Count == 0)
            {
                reason = "no axes reported";
                return PositionValidity.Unreported;
            }
            for (int i = 0; i < Required.Length; i++)
            {
                string want = Required[i];
                int seen = 0;
                foreach (string letter in letters)
                    if (string.Equals(letter, want, StringComparison.OrdinalIgnoreCase)) seen++;
                if (seen > 1)
                {
                    reason = "axis '" + want + "' reported " + seen.ToString(CultureInfo.InvariantCulture) + " times";
                    return PositionValidity.DuplicateAxis;
                }
            }
            foreach (string want in Required)
            {
                bool present = false;
                foreach (string letter in letters)
                    if (string.Equals(letter, want, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                if (!present)
                {
                    reason = "axis '" + want + "' is not reported";
                    return PositionValidity.MissingAxis;
                }
                bool ok = false;
                foreach (string letter in accepted)
                    if (string.Equals(letter, want, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                if (!ok)
                {
                    reason = "axis '" + want + "' has no numeric coordinate";
                    return PositionValidity.NotNumeric;
                }
            }
            return PositionValidity.Valid;
        }
    }
}
