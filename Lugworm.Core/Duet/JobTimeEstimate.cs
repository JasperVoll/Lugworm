using System;
using System.Collections.Generic;
using System.Text;
using Lugworm.Core.Analysis;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    /// <summary>Estimated elapsed time and layer lookup for one immutable job.</summary>
    public sealed class JobTimeEstimate
    {
        readonly double[] _lineSeconds;
        readonly long[] _lineStarts;
        readonly Dictionary<int, double> _layerSeconds;
        readonly bool _pathAvailable;
        readonly double _correctionFactor;

        public double TotalS { get; }
        public LayerMarkerIndex Markers { get; }
        public double CorrectionFactor => _correctionFactor;
        public bool IsAvailable => _pathAvailable;

        JobTimeEstimate(InterlayerTimeline timeline, byte[] bytes, IReadOnlyList<string> lines,
                        LayerMarkerIndex markers)
        {
            Markers = markers;
            TotalS = Math.Max(0.0, timeline.TotalS);
            _pathAvailable = bytes.Length > 0 && timeline.Count > 0;
            _lineStarts = LineStarts(bytes);
            _lineSeconds = new double[Math.Max(lines.Count, 1)];
            _layerSeconds = new Dictionary<int, double>();
            _correctionFactor = 1.0;
            BuildLineTimes(timeline);
            for (int layer = 1; layer <= markers.Total; layer++)
            {
                long offset = markers.OffsetOf(layer);
                if (offset < 0) continue;
                // The marker line carries no move, so its forward-filled time is the
                // last move before the seam.
                _layerSeconds[layer] = _lineSeconds[Math.Min(LineAt(offset), _lineSeconds.Length - 1)];
            }
        }

        JobTimeEstimate(JobTimeEstimate source, double correctionFactor)
        {
            TotalS = source.TotalS;
            Markers = source.Markers;
            _pathAvailable = source._pathAvailable;
            _lineSeconds = source._lineSeconds;
            _lineStarts = source._lineStarts;
            _layerSeconds = source._layerSeconds;
            _correctionFactor = ValidCorrection(correctionFactor);
        }

        public JobTimeEstimate WithCorrection(double correctionFactor)
            => new JobTimeEstimate(this, correctionFactor);

        /// <summary>Builds an estimate from a parsed program and its exact lines and bytes.</summary>
        public static JobTimeEstimate FromMoves(IReadOnlyList<ParsedMove> moves,
                                                IReadOnlyList<string> lines, byte[] bytes,
                                                InterlayerOptions? options = null)
        {
            if (moves == null) throw new ArgumentNullException(nameof(moves));
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var timeline = InterlayerTimeline.FromMoves(moves, lines, options);
            return new JobTimeEstimate(timeline, bytes, lines, new LayerMarkerIndex(bytes));
        }

        public static JobTimeEstimate FromTimeline(InterlayerTimeline timeline, byte[] bytes,
                                                   IReadOnlyList<string>? lines = null)
        {
            if (timeline == null) throw new ArgumentNullException(nameof(timeline));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var sourceLines = lines ?? DecodeLines(bytes);
            return new JobTimeEstimate(timeline, bytes, sourceLines, new LayerMarkerIndex(bytes));
        }

        /// <summary>Builds an estimate by parsing the exact uploaded bytes.</summary>
        public static JobTimeEstimate FromBytes(byte[] bytes, InterlayerOptions? options = null)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var lines = DecodeLines(bytes);
            var program = GcodeParser.Parse(lines, allowPerAxisHome: true);
            return FromMoves(program.Moves, lines, bytes, options);
        }

        public static JobTimeEstimate? FromSnapshot(JobSnapshot? snapshot, InterlayerOptions? options = null)
            => snapshot == null || !snapshot.IntendedPathAvailable
                ? null
                : FromBytes(snapshot.GcodeBytes(), options);

        public double? SecondsAtLayer(int layer)
            => _layerSeconds.TryGetValue(layer, out double seconds) ? seconds : (double?)null;

        /// <summary>Returns estimated elapsed time at a file byte position.</summary>
        public double? SecondsAtPosition(long filePosition)
        {
            if (!IsAvailable || filePosition < 0) return null;
            int line = LineAt(filePosition);
            return _lineSeconds[Math.Min(line, _lineSeconds.Length - 1)];
        }

        /// <summary>Returns the observed pace correction from the last three clean layers.</summary>
        public double Correction(IReadOnlyList<(int layer, double observedS)> cleanIntervals)
        {
            if (cleanIntervals == null) throw new ArgumentNullException(nameof(cleanIntervals));
            int start = Math.Max(0, cleanIntervals.Count - 3);
            double observed = 0.0;
            double estimated = 0.0;
            for (int i = start; i < cleanIntervals.Count; i++)
            {
                var interval = cleanIntervals[i];
                if (interval.observedS <= 0 || double.IsNaN(interval.observedS)
                    || double.IsInfinity(interval.observedS)) continue;
                double? end = SecondsAtLayer(interval.layer);
                double? begin = interval.layer <= 1 ? 0.0 : SecondsAtLayer(interval.layer - 1);
                if (!end.HasValue || !begin.HasValue || end.Value <= begin.Value) continue;
                observed += interval.observedS;
                estimated += end.Value - begin.Value;
            }
            if (estimated <= 0) return 1.0;
            double ratio = observed / estimated;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return 1.0;
            return Math.Max(0.5, Math.Min(3.0, ratio));
        }

        public double? TimeToLayer(int layer, long filePosition, double correction = 1.0)
        {
            double? target = SecondsAtLayer(layer);
            double? current = SecondsAtPosition(filePosition);
            if (!target.HasValue || !current.HasValue) return null;
            double factor = ValidCorrection(correction);
            return Math.Max(0.0, target.Value - current.Value) * factor;
        }

        public int? LayerAtTime(double secondsFromNow, long filePosition, double correction = 1.0)
        {
            double? current = SecondsAtPosition(filePosition);
            if (!current.HasValue || _layerSeconds.Count == 0 || double.IsNaN(secondsFromNow) || double.IsInfinity(secondsFromNow)) return null;
            double target = current.Value + Math.Max(0.0, secondsFromNow) / ValidCorrection(correction);
            int answer = 0;
            foreach (var pair in _layerSeconds)
                if (pair.Value <= target) answer = Math.Max(answer, pair.Key);
            return answer;
        }

        public int? LayerAtTime(double secondsFromStart)
        {
            if (!IsAvailable || _layerSeconds.Count == 0 || double.IsNaN(secondsFromStart) || double.IsInfinity(secondsFromStart)) return null;
            double target = Math.Max(0.0, secondsFromStart);
            int answer = 0;
            foreach (var pair in _layerSeconds)
                if (pair.Value <= target) answer = Math.Max(answer, pair.Key);
            return answer;
        }

        void BuildLineTimes(InterlayerTimeline timeline)
        {
            for (int i = 0; i < _lineSeconds.Length; i++) _lineSeconds[i] = 0.0;
            var known = new bool[_lineSeconds.Length];
            for (int i = 0; i < timeline.LineNumber.Count && i < timeline.TimeS.Count; i++)
            {
                int line = timeline.LineNumber[i];
                if (line < 1 || line > _lineSeconds.Length) continue;
                double seconds = Math.Max(0.0, timeline.TimeS[i] - timeline.TimeS[0]);
                if (!known[line - 1] || seconds > _lineSeconds[line - 1])
                    _lineSeconds[line - 1] = seconds;
                known[line - 1] = true;
            }
            double last = 0.0;
            for (int i = 0; i < _lineSeconds.Length; i++)
            {
                if (known[i]) last = _lineSeconds[i];
                else _lineSeconds[i] = last;
            }
        }

        int LineAt(long offset)
        {
            if (_lineStarts.Length == 0 || offset < _lineStarts[0]) return 0;
            int lo = 0, hi = _lineStarts.Length - 1, answer = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (_lineStarts[mid] <= offset) { answer = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return answer;
        }

        static double ValidCorrection(double correction)
            => double.IsNaN(correction) || double.IsInfinity(correction) || correction <= 0 ? 1.0 : correction;

        static long[] LineStarts(byte[] bytes)
        {
            var starts = new List<long>();
            long start = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            starts.Add(start);
            for (int i = (int)start; i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n') starts.Add(i + 1L);
            return starts.ToArray();
        }

        static IReadOnlyList<string> DecodeLines(byte[] bytes)
        {
            string text = new UTF8Encoding(false, false).GetString(bytes);
            if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text.Substring(1);
            var lines = new List<string>();
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
    }
}
