using System;
using System.Collections.Generic;
using System.Text;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Duet
{
    /// <summary>Byte offsets and strict validity for the layer markers in one job.</summary>
    public sealed class LayerMarkerIndex
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        readonly List<Entry> _entries = new List<Entry>();
        readonly Dictionary<int, long> _firstOffset = new Dictionary<int, long>();

        public int Total { get; }
        public int Count => _entries.Count;
        public bool CanPauseAtLayer { get; }
        public string Reason { get; }

        readonly struct Entry
        {
            public readonly long Offset;
            public readonly int Layer;
            public readonly int Total;

            public Entry(long offset, int layer, int total)
            { Offset = offset; Layer = layer; Total = total; }
        }

        public LayerMarkerIndex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            Scan(bytes);
            Total = _entries.Count == 0 ? 0 : _entries[0].Total;
            CanPauseAtLayer = Validate(out string reason);
            Reason = reason;
        }

        public static LayerMarkerIndex FromBytes(byte[] bytes) => new LayerMarkerIndex(bytes);

        /// <summary>Returns the marker byte offset, or -1 when that layer is absent.</summary>
        public long OffsetOf(int layer)
            => _firstOffset.TryGetValue(layer, out long offset) ? offset : -1L;

        /// <summary>Returns the last marker at or before the file position.</summary>
        public int LayerAt(long filePosition)
        {
            if (filePosition < 0 || _entries.Count == 0) return 0;
            int lo = 0, hi = _entries.Count - 1, answer = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_entries[mid].Offset <= filePosition)
                {
                    answer = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return answer < 0 ? 0 : _entries[answer].Layer;
        }

        void Scan(byte[] bytes)
        {
            long lineStart = 0;
            for (int i = 0; i <= bytes.Length; i++)
            {
                if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
                int length = checked((int)(i - lineStart));
                if (length > 0 && bytes[i - 1] == (byte)'\r') length--;
                // Only comment lines can be markers: skip decoding the moves.
                if (length > 0 && bytes[lineStart] == (byte)';')
                {
                    string line = Utf8.GetString(bytes, checked((int)lineStart), length);
                    if (LayerMarker.TryParseExact(line, out int layer, out int total))
                    {
                        _entries.Add(new Entry(lineStart, layer, total));
                        if (!_firstOffset.ContainsKey(layer)) _firstOffset[layer] = lineStart;
                    }
                }
                lineStart = i + 1L;
            }
        }

        bool Validate(out string reason)
        {
            if (_entries.Count == 0) { reason = "no strict layer markers"; return false; }
            int total = _entries[0].Total;
            if (total < 2) { reason = "layer pauses require at least two layers"; return false; }
            if (_entries.Count != total)
            {
                reason = "layer markers are incomplete or duplicated";
                return false;
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Total != total || entry.Layer != i + 1)
                {
                    reason = "layer markers must be 1..M in strict order with one total";
                    return false;
                }
            }
            reason = "";
            return true;
        }
    }
}
