using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Arcs;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Analysis
{
    /// <summary>Where the clock of a timeline came from.</summary>
    public enum InterlayerTimeSource
    {
        /// <summary>Per-move feeds (plus the optional accel model): the print speed the file asks for.</summary>
        Estimated,
        /// <summary>Print-log records anchored by RRF file position; estimated pace between anchors.</summary>
        LogFilePosition,
        /// <summary>Print-log layer spans; estimated pace inside each layer.</summary>
        LogLayerSpans,
        /// <summary>No G-code: the log's own sparse position samples are the path.</summary>
        LogOnly,
    }

    public sealed class InterlayerOptions
    {
        /// <summary>Layer height, mm. 0 = the file's <c>; LAYERHEIGHT=</c> header, else the
        /// median Z step of the extruding moves.</summary>
        public double LayerHeightMm;
        /// <summary>Feed for extruding moves that carry no F word, mm/s.</summary>
        public double PrintFeedMmS = 25.0;
        /// <summary>Feed for travel moves that carry no F word, mm/s.</summary>
        public double TravelFeedMmS = 50.0;
        /// <summary>XY acceleration for the trapezoidal model, mm/s² (0 = kinematic lower bound).</summary>
        public double AccelMmS2;
        public double ArcMaxDeviationMm = 0.05;
    }

    /// <summary>Layer markers a slicer leaves as comments. Lugworm writes
    /// <c>; layer N/M</c>; <c>;LAYER:N</c> (Cura, 0-based) and <c>;LAYER_CHANGE</c>
    /// (PrusaSlicer) are accepted too, so a foreign file scrolls by its own layers.</summary>
    public static class GcodeLayerMarkers
    {
        /// <summary>1-based source line → 1-based layer number, ascending by line.</summary>
        public static SortedList<int, int> Scan(IReadOnlyList<string> lines)
        {
            var markers = new SortedList<int, int>();
            if (lines == null) return markers;
            int changeCounter = 0;
            int min = int.MaxValue;
            for (int i = 0; i < lines.Count; i++)
            {
                string raw = lines[i];
                if (raw == null) continue;
                string s = raw.Trim();
                if (s.Length < 6 || s[0] != ';') continue;
                s = s.Substring(1).TrimStart();
                if (s.Length < 5 || !s.StartsWith("layer", StringComparison.OrdinalIgnoreCase)) continue;
                string rest = s.Substring(5).TrimStart();
                if (rest.StartsWith("_change", StringComparison.OrdinalIgnoreCase))
                {
                    markers[i + 1] = ++changeCounter;
                    min = Math.Min(min, 1);
                    continue;
                }
                if (rest.StartsWith(":", StringComparison.Ordinal)) rest = rest.Substring(1).TrimStart();
                int p = 0;
                while (p < rest.Length && char.IsDigit(rest[p])) p++;
                if (p == 0) continue;   // "; layer height 3" and friends
                if (!int.TryParse(rest.Substring(0, p), NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer)) continue;
                markers[i + 1] = layer;
                min = Math.Min(min, layer);
            }
            if (markers.Count > 0 && min == 0)
            {
                // Cura counts from 0; the timeline counts from 1 like the firmware and Lugworm
                var shifted = new SortedList<int, int>(markers.Count);
                foreach (var kv in markers) shifted[kv.Key] = kv.Value + 1;
                return shifted;
            }
            return markers;
        }

        /// <summary>The M of the first Lugworm <c>; layer N/M</c> marker: the job's total layer
        /// count as the generator wrote it. Null for G-code without one (foreign slicers,
        /// hand-written files); "; layer height 3" and friends do not count.</summary>
        public static int? TotalLayers(IReadOnlyList<string> lines)
        {
            if (lines == null) return null;
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                string s = raw.Trim();
                if (s.Length < 6 || s[0] != ';') continue;
                s = s.Substring(1).TrimStart();
                if (!s.StartsWith("layer ", StringComparison.OrdinalIgnoreCase)) continue;
                string counts = s.Substring("layer ".Length).Trim();
                int slash = counts.IndexOf('/');
                if (slash <= 0) continue;
                if (int.TryParse(counts.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    && int.TryParse(counts.Substring(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int m)
                    && n >= 1 && m >= n)
                    return m;
            }
            return null;
        }

        /// <summary>Value of a <c>; KEY=value</c> header line (Lugworm's own header block),
        /// searched in the first 64 lines. Null when absent or not a finite number.</summary>
        public static double? HeaderNumber(IReadOnlyList<string> lines, string key)
        {
            if (lines == null || string.IsNullOrEmpty(key)) return null;
            int limit = Math.Min(lines.Count, 64);
            for (int i = 0; i < limit; i++)
            {
                string s = (lines[i] ?? "").Trim();
                if (s.Length == 0 || s[0] != ';') continue;
                s = s.Substring(1).Trim();
                int eq = s.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(s.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                string v = s.Substring(eq + 1).Trim();
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
                return null;
            }
            return null;
        }
    }

    /// <summary>A toolpath with every sample placed on the clock: the moment its layer
    /// was (log) or will be (estimate) laid. Parallel lists; <c>Extrude[k]</c> says
    /// whether the segment ending at point k lays material, like every other Lugworm
    /// path list. Layers are 1-based.</summary>
    public sealed class InterlayerTimeline
    {
        public readonly List<Vec3> Points = new List<Vec3>();
        public readonly List<bool> Extrude = new List<bool>();
        public readonly List<int> Layer = new List<int>();
        public readonly List<double> TimeS = new List<double>();
        /// <summary>1-based G-code line of the move that ends at the point (log: record seq);
        /// 0 for the initial position before any command, -1 unknown.</summary>
        public readonly List<int> LineNumber = new List<int>();
        public double LayerHeightMm = 1.0;
        public int LayerCount;
        public InterlayerTimeSource Source = InterlayerTimeSource.Estimated;
        /// <summary>"markers", "z-step" or "log".</summary>
        public string LayerSource = "";
        public string Note = "";
        /// <summary>Header <c>; LINEWIDTH=</c> when the file carries one.</summary>
        public double? HeaderWidthMm;
        public double? HeaderLayerHeightMm;

        public int Count => Points.Count;

        public double TotalS => TimeS.Count > 0 ? TimeS[TimeS.Count - 1] - TimeS[0] : 0.0;

        /// <summary>Timeline of a parsed program at its commanded feeds. <paramref name="rawLines"/>
        /// (the same lines the parser saw) supplies layer markers and the header; without
        /// them layers come from the Z steps of the extruding moves.</summary>
        public static InterlayerTimeline FromMoves(IReadOnlyList<ParsedMove> moves, IReadOnlyList<string>? rawLines,
                                                  InterlayerOptions? options = null)
        {
            if (moves == null) throw new ArgumentNullException(nameof(moves));
            var o = options ?? new InterlayerOptions();
            var tl = new InterlayerTimeline();
            if (rawLines != null)
            {
                tl.HeaderWidthMm = GcodeLayerMarkers.HeaderNumber(rawLines, "LINEWIDTH");
                tl.HeaderLayerHeightMm = GcodeLayerMarkers.HeaderNumber(rawLines, "LAYERHEIGHT");
            }
            var feeds = new List<double>();
            var waits = new List<double>();
            var dwellLines = new SortedList<int, double>();
            if (rawLines != null)
                for (int i = 0; i < rawLines.Count; i++)
                {
                    string executable = GcodeText.Strip(rawLines[i]);
                    // Filter before lexing: the parser also accepts bare G28 axis
                    // words, which are not numeric words for the shared lexer.
                    if (!System.Text.RegularExpressions.Regex.IsMatch(executable,
                        @"^(?:N\d+\s*)?G0*4(?=\s|[PS*]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    double seconds = 0, milliseconds = 0;
                    foreach (var word in GcodeText.Lex(executable, i + 1).Words)
                    {
                        if (word.Letter == 'S') seconds = word.Value;
                        if (word.Letter == 'P') milliseconds = word.Value;
                    }
                    double delay = seconds > 0 ? seconds : milliseconds / 1000.0;
                    if (delay > 0) dwellLines[i + 1] = delay;
                }
            int dwellIndex = 0;
            foreach (var parsed in moves)
            {
                var p0 = new Vec3(parsed.StartX, parsed.StartY, parsed.StartZ);
                var p1 = new Vec3(parsed.X, parsed.Y, parsed.Z);
                Move move = Move.Line(p0, p1);
                if (parsed.Arc)
                {
                    double cx = p0.X + parsed.I, cy = p0.Y + parsed.J;
                    double r = Math.Sqrt((p0.X - cx) * (p0.X - cx) + (p0.Y - cy) * (p0.Y - cy));
                    if (r > 1e-9 && !double.IsNaN(r)) move = Move.Arc(p0, p1, cx, cy, parsed.G == 2, r);
                }
                var poly = ArcTessellation.ToPolyline(move, o.ArcMaxDeviationMm, 4096);
                double feed = parsed.F > 0 ? parsed.F / 60.0 : 0.0;
                if (tl.Points.Count == 0)
                {
                    tl.Points.Add(poly[0]); tl.Extrude.Add(false); feeds.Add(0.0); waits.Add(0.0);
                    tl.LineNumber.Add(0);
                }
                while (dwellIndex < dwellLines.Count && dwellLines.Keys[dwellIndex] < parsed.LineNumber)
                {
                    tl.Points.Add(tl.Points[tl.Points.Count - 1]); tl.Extrude.Add(false); feeds.Add(0.0);
                    waits.Add(dwellLines.Values[dwellIndex]); tl.LineNumber.Add(dwellLines.Keys[dwellIndex]);
                    dwellIndex++;
                }
                for (int i = 1; i < poly.Count; i++)
                {
                    tl.Points.Add(poly[i]); tl.Extrude.Add(parsed.Extrude); feeds.Add(feed); waits.Add(0.0);
                    tl.LineNumber.Add(parsed.LineNumber);
                }
            }
            if (tl.Points.Count == 0) return tl;
            while (dwellIndex < dwellLines.Count)
            {
                tl.Points.Add(tl.Points[tl.Points.Count - 1]); tl.Extrude.Add(false); feeds.Add(0.0);
                waits.Add(dwellLines.Values[dwellIndex]); tl.LineNumber.Add(dwellLines.Keys[dwellIndex]);
                dwellIndex++;
            }

            // layer height: option > header > auto
            double h = o.LayerHeightMm > 0 ? o.LayerHeightMm
                     : tl.HeaderLayerHeightMm.HasValue && tl.HeaderLayerHeightMm.Value > 0 ? tl.HeaderLayerHeightMm.Value
                     : 0.0;
            string hNote = o.LayerHeightMm > 0 ? "input" : h > 0 ? "header" : "auto";
            if (h <= 0)
            {
                h = AutoLayerHeight(tl.Points, tl.Extrude);
                if (h <= 0) { h = 1.0; hNote = "unknown (set height)"; }
            }
            tl.LayerHeightMm = h;

            // layers: markers > z-step
            var markers = rawLines != null ? GcodeLayerMarkers.Scan(rawLines) : new SortedList<int, int>();
            if (markers.Count > 0)
            {
                tl.LayerSource = "markers";
                var lineKeys = markers.Keys;
                int mi = 0;
                int current = markers.Values[0];
                for (int k = 0; k < tl.Points.Count; k++)
                {
                    int line = tl.LineNumber[k];
                    while (mi < lineKeys.Count && lineKeys[mi] <= line) { current = markers.Values[mi]; mi++; }
                    tl.Layer.Add(Math.Max(1, current));
                }
            }
            else
            {
                tl.LayerSource = "z-step";
                AssignLayersByZ(tl.Points, tl.Extrude, h, tl.Layer);
            }
            int maxLayer = 0;
            foreach (int l in tl.Layer) maxLayer = Math.Max(maxLayer, l);
            tl.LayerCount = maxLayer;

            // clock: commanded feeds, optionally the trapezoidal accel model
            tl.TimeS.Add(0.0);
            if (o.AccelMmS2 > 0 && tl.Points.Count > 1)
            {
                var seg = PrintStatsCalc.TrapezoidSegmentTimesS(tl.Points, tl.Extrude, feeds,
                    Math.Max(o.PrintFeedMmS, 0.1), Math.Max(o.TravelFeedMmS, 0.1), o.AccelMmS2);
                for (int k = 0; k < seg.Length; k++) tl.TimeS.Add(tl.TimeS[k] + seg[k] + waits[k + 1]);
            }
            else
            {
                for (int k = 1; k < tl.Points.Count; k++)
                {
                    double d = tl.Points[k].DistanceTo(tl.Points[k - 1]);
                    double f = feeds[k] > 0 ? feeds[k] : tl.Extrude[k] ? o.PrintFeedMmS : o.TravelFeedMmS;
                    tl.TimeS.Add(tl.TimeS[k - 1] + d / Math.Max(f, 0.1) + waits[k]);
                }
            }
            tl.Source = InterlayerTimeSource.Estimated;
            tl.Note = FormattableString.Invariant(
                $"{moves.Count} moves, {tl.Points.Count} samples, {tl.LayerCount} layers ({tl.LayerSource}), layer height {h:0.##} mm ({hNote}), estimated {Seconds(tl.TotalS)}")
                + (o.AccelMmS2 > 0 ? FormattableString.Invariant($" with accel {o.AccelMmS2:0} mm/s²") : " at commanded feeds (no accel model)")
                + (dwellLines.Count > 0 ? "; explicit G4 dwells included" : "");
            return tl;
        }

        /// <summary>Timeline from the print log alone: the recorded positions are the
        /// path (sparse - one sample per boundary plus every Nth poll) and the recorded
        /// clock is the clock. The job run with the most positioned samples is used.</summary>
        public static InterlayerTimeline FromLog(IReadOnlyList<PrintLogRecord> records, InterlayerOptions? options = null)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            var o = options ?? new InterlayerOptions();
            var tl = new InterlayerTimeline { Source = InterlayerTimeSource.LogOnly, LayerSource = "log" };
            var run = SelectRun(records, out int jobRun, out string? jobFile);
            if (run.Count == 0) { tl.Note = "print log carries no positioned samples"; return tl; }
            var times = RelativeTimes(run, out string clockNote);

            double h = o.LayerHeightMm;
            int fallbackLayer = 1;
            double? z0 = null;
            for (int i = 0; i < run.Count; i++)
            {
                var r = run[i];
                tl.Points.Add(r.Position);
                tl.Extrude.Add(i > 0);
                tl.TimeS.Add(times[i]);
                tl.LineNumber.Add((int)Math.Min(int.MaxValue, Math.Max(0, r.Seq)));
                int layer;
                if (r.Layer.HasValue && r.Layer.Value > 0) layer = r.Layer.Value;
                else if (h > 0)
                {
                    if (!z0.HasValue) z0 = r.Position.Z;
                    layer = Math.Max(fallbackLayer, (int)Math.Round((r.Position.Z - z0.Value) / h) + 1);
                }
                else layer = fallbackLayer;
                fallbackLayer = Math.Max(fallbackLayer, layer);
                tl.Layer.Add(layer);
            }
            int maxLayer = 0;
            foreach (int l in tl.Layer) maxLayer = Math.Max(maxLayer, l);
            tl.LayerCount = maxLayer;
            if (h <= 0) h = AutoLayerHeightFromLayers(tl.Points, tl.Layer);
            tl.LayerHeightMm = h > 0 ? h : 1.0;
            tl.Note = FormattableString.Invariant(
                $"{run.Count} log samples of job run {jobRun}{(jobFile != null ? " (" + jobFile + ")" : "")}, {tl.LayerCount} layers, {clockNote}, {Seconds(tl.TotalS)}");
            return tl;
        }

        /// <summary>Replace the estimated clock by the print log's. Prefers RRF file
        /// positions (needs <paramref name="lineByteEnds"/> from
        /// <see cref="GcodeText.LineByteEnds"/>), else the log's layer spans. Inside an
        /// anchor interval the estimated pace still orders the samples. Returns a note
        /// describing what was matched; the timeline is untouched when nothing was.</summary>
        public string CalibrateWithLog(IReadOnlyList<PrintLogRecord> records, long[]? lineByteEnds)
        {
            if (records == null) throw new ArgumentNullException(nameof(records));
            if (Points.Count < 2) return "no path to calibrate";
            var run = SelectRun(records, out int jobRun, out string? jobFile);
            if (run.Count == 0) return "print log carries no positioned samples - estimated clock kept";
            double[] times;
            string clockNote;
            try { times = RelativeTimes(run, out clockNote); }
            catch (FormatException ex) { return ex.Message + " - estimated clock kept"; }
            string runNote = FormattableString.Invariant($"job run {jobRun}{(jobFile != null ? " (" + jobFile + ")" : "")}, {clockNote}");

            if (lineByteEnds != null && lineByteEnds.Length > 0)
            {
                string? fp = CalibrateByFilePosition(run, times, lineByteEnds);
                if (fp != null) return fp + "; " + runNote;
            }
            string? spans = CalibrateByLayerSpans(run, times);
            if (spans != null) return spans + "; " + runNote;
            return "print log has no file positions and no layer spans - estimated clock kept";
        }

        string? CalibrateByFilePosition(List<PrintLogRecord> run, double[] times, long[] lineByteEnds)
        {
            // byte offset of every sample = end of the line of the move that produced it
            var offsets = new long[Points.Count];
            long last = 0;
            for (int k = 0; k < Points.Count; k++)
            {
                int line = LineNumber[k];
                if (line >= 1 && line <= lineByteEnds.Length) last = lineByteEnds[line - 1];
                offsets[k] = last;
            }
            // anchors: strictly increasing file positions; a plateau (pause) keeps both
            // the first and the last time seen at that sample
            var anchorIdx = new List<int>();
            var anchorFirst = new List<double>();
            var anchorLast = new List<double>();
            long lastPos = -1;
            for (int i = 0; i < run.Count; i++)
            {
                long? pos = run[i].FilePosition;
                if (!pos.HasValue || pos.Value < lastPos) continue;
                int idx = LastIndexAtOrBefore(offsets, pos.Value);
                if (idx < 0) continue;
                if (anchorIdx.Count > 0 && anchorIdx[anchorIdx.Count - 1] == idx)
                {
                    anchorLast[anchorLast.Count - 1] = times[i];
                }
                else
                {
                    anchorIdx.Add(idx); anchorFirst.Add(times[i]); anchorLast.Add(times[i]);
                }
                lastPos = pos.Value;
            }
            if (anchorIdx.Count < 2) return null;

            var est = new List<double>(TimeS);
            var mapped = new double[Points.Count];
            // before the first anchor: estimated pace backwards
            int a0 = anchorIdx[0];
            for (int k = 0; k <= a0; k++) mapped[k] = anchorFirst[0] - (est[a0] - est[k]);
            for (int ai = 0; ai + 1 < anchorIdx.Count; ai++)
            {
                int a = anchorIdx[ai], b = anchorIdx[ai + 1];
                double ta = anchorLast[ai], tb = anchorFirst[ai + 1];
                double span = est[b] - est[a];
                for (int k = a + 1; k <= b; k++)
                {
                    double f = span > 1e-9 ? (est[k] - est[a]) / span : (double)(k - a) / (b - a);
                    mapped[k] = ta + f * (tb - ta);
                }
            }
            int an = anchorIdx[anchorIdx.Count - 1];
            for (int k = an + 1; k < Points.Count; k++) mapped[k] = anchorLast[anchorLast.Count - 1] + (est[k] - est[an]);
            for (int k = 0; k < Points.Count; k++) TimeS[k] = mapped[k];
            Source = InterlayerTimeSource.LogFilePosition;
            return FormattableString.Invariant(
                $"clock from the print log's file positions ({anchorIdx.Count} anchors, {Seconds(TotalS)}; a file position leads the executed move by the planner's look-ahead)");
        }

        string? CalibrateByLayerSpans(List<PrintLogRecord> run, double[] times)
        {
            var spanStart = new SortedDictionary<int, double>();
            var spanEnd = new SortedDictionary<int, double>();
            for (int i = 0; i < run.Count; i++)
            {
                var r = run[i];
                if (!r.Layer.HasValue || r.Layer.Value <= 0) continue;
                int l = r.Layer.Value;
                if (!spanStart.ContainsKey(l) || r.Kind == PrintLogEventKind.LayerStart) spanStart[l] = Math.Min(spanStart.TryGetValue(l, out var s0) ? s0 : double.MaxValue, times[i]);
                if (!spanEnd.ContainsKey(l) || times[i] > spanEnd[l]) spanEnd[l] = times[i];
            }
            if (spanStart.Count == 0) return null;

            // contiguous index range of every timeline layer
            var first = new SortedDictionary<int, int>();
            var lastIdx = new SortedDictionary<int, int>();
            for (int k = 0; k < Points.Count; k++)
            {
                int l = Layer[k];
                if (!first.ContainsKey(l)) first[l] = k;
                lastIdx[l] = k;
            }
            var est = new List<double>(TimeS);
            var mapped = new double[Points.Count];
            int matched = 0;
            var anchors = new SortedList<int, double>();
            foreach (var kv in first)
            {
                int l = kv.Key, a = kv.Value, b = lastIdx[l];
                bool has = spanStart.TryGetValue(l, out double sL) & spanEnd.TryGetValue(l, out double eL)
                    && eL > sL && b > a;
                if (has)
                {
                    // Out-of-order layer labels cannot make the print clock run backwards.
                    if (anchors.Count > 0 && (a <= anchors.Keys[anchors.Count - 1]
                        || sL < anchors.Values[anchors.Count - 1])) continue;
                    matched++;
                    anchors[a] = sL;
                    anchors[b] = eL;
                }
            }
            if (matched == 0) return null;
            // Preserve estimated pace outside coverage; inside gaps, fit that pace to
            // the two measured boundaries. This also handles logs started mid-print.
            int firstAnchor = anchors.Keys[0];
            for (int k = 0; k <= firstAnchor; k++)
                mapped[k] = anchors.Values[0] - (est[firstAnchor] - est[k]);
            for (int i = 1; i < anchors.Count; i++)
            {
                int a = anchors.Keys[i - 1], b = anchors.Keys[i];
                double span = est[b] - est[a];
                for (int k = a + 1; k <= b; k++)
                {
                    double f = span > 1e-9 ? (est[k] - est[a]) / span : (double)(k - a) / (b - a);
                    mapped[k] = anchors.Values[i - 1] + f * (anchors.Values[i] - anchors.Values[i - 1]);
                }
            }
            int lastAnchor = anchors.Keys[anchors.Count - 1];
            for (int k = lastAnchor + 1; k < Points.Count; k++)
                mapped[k] = anchors.Values[anchors.Count - 1] + est[k] - est[lastAnchor];
            for (int k = 0; k < Points.Count; k++) TimeS[k] = mapped[k];
            Source = InterlayerTimeSource.LogLayerSpans;
            return FormattableString.Invariant(
                $"clock from the print log's layer spans ({matched} of {first.Count} layers matched, {Seconds(TotalS)}; ±1 poll per boundary)");
        }

        static int LastIndexAtOrBefore(long[] sorted, long value)
        {
            int lo = 0, hi = sorted.Length - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= value) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>The run with the most positioned samples; records ordered by sequence.</summary>
        internal static List<PrintLogRecord> SelectRun(IReadOnlyList<PrintLogRecord> records, out int jobRun, out string? jobFile)
        {
            var byRun = new Dictionary<int, List<PrintLogRecord>>();
            foreach (var r in records)
            {
                if (r == null) continue;
                if (!Num.Finite(r.Position)) continue;
                if (!byRun.TryGetValue(r.JobRun, out var list)) { list = new List<PrintLogRecord>(); byRun[r.JobRun] = list; }
                list.Add(r);
            }
            jobRun = 0; jobFile = null;
            List<PrintLogRecord>? best = null;
            foreach (var kv in byRun)
                if (best == null || kv.Value.Count > best.Count || (kv.Value.Count == best.Count && kv.Key > jobRun))
                { best = kv.Value; jobRun = kv.Key; }
            if (best == null) return new List<PrintLogRecord>();
            best.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            foreach (var r in best) if (!string.IsNullOrEmpty(r.JobFile)) { jobFile = r.JobFile; break; }
            return best;
        }

        /// <summary>Seconds since the run's first sample: the job clock when every record
        /// carries one, else the wall clock.</summary>
        internal static double[] RelativeTimes(List<PrintLogRecord> run, out string note)
        {
            var t = new double[run.Count];
            bool allElapsed = run.Count > 0;
            double previous = double.NegativeInfinity;
            foreach (var r in run)
            {
                double value = r.JobElapsedSeconds ?? double.NaN;
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value < previous)
                { allElapsed = false; break; }
                previous = value;
            }
            if (allElapsed)
            {
                double t0 = run[0].JobElapsedSeconds!.Value;
                for (int i = 0; i < run.Count; i++) t[i] = run[i].JobElapsedSeconds!.Value - t0;
                note = "job clock";
                return t;
            }
            DateTime u0 = run[0].TakenUtc;
            for (int i = 0; i < run.Count; i++)
            {
                if (run[i].TakenUtc == DateTime.MinValue || (i > 0 && run[i].TakenUtc < run[i - 1].TakenUtc))
                    throw new FormatException("print log has no complete, nondecreasing clock");
                t[i] = (run[i].TakenUtc - u0).TotalSeconds;
            }
            note = "wall clock";
            return t;
        }

        /// <summary>Median of the distinct Z steps of the extruding samples; 0 when the
        /// path has fewer than two distinct extruding heights (single layer, or a spiral
        /// whose Z rises continuously - set the height explicitly there).</summary>
        public static double AutoLayerHeight(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> ext)
        {
            var zs = new SortedSet<double>();
            for (int k = 0; k < pts.Count; k++)
                if (k < ext.Count && ext[k]) zs.Add(Math.Round(pts[k].Z, 3));
            if (zs.Count < 2) return 0.0;
            var diffs = new List<double>();
            double prev = double.NaN;
            foreach (double z in zs)
            {
                if (!double.IsNaN(prev) && z - prev > 1e-3) diffs.Add(z - prev);
                prev = z;
            }
            if (diffs.Count == 0) return 0.0;
            diffs.Sort();
            double med = diffs[diffs.Count / 2];
            // a spiral's steps are the sampling pitch, not a layer - refuse to guess
            return med >= 0.05 ? med : 0.0;
        }

        static double AutoLayerHeightFromLayers(IReadOnlyList<Vec3> pts, IReadOnlyList<int> layers)
        {
            var firstZ = new SortedDictionary<int, double>();
            for (int k = 0; k < pts.Count; k++)
                if (!firstZ.ContainsKey(layers[k])) firstZ[layers[k]] = pts[k].Z;
            var diffs = new List<double>();
            double prev = double.NaN;
            foreach (var kv in firstZ)
            {
                if (!double.IsNaN(prev) && Math.Abs(kv.Value - prev) > 1e-3) diffs.Add(Math.Abs(kv.Value - prev));
                prev = kv.Value;
            }
            if (diffs.Count == 0) return 0.0;
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }

        /// <summary>Layer = 1 + round((z − z0)/h) of the extruding samples, never
        /// decreasing; travel samples take the layer they leave. A spiral gets a layer
        /// boundary every h of rise, which is what "the layer below" means there.</summary>
        static void AssignLayersByZ(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> ext, double h, List<int> layers)
        {
            int current = 1;
            double? z0 = null;
            for (int k = 0; k < pts.Count; k++)
            {
                if (ext[k])
                {
                    if (!z0.HasValue) z0 = pts[k].Z;
                    int band = (int)Math.Round((pts[k].Z - z0.Value) / h) + 1;
                    if (band > current) current = band;
                }
                layers.Add(current);
            }
        }

        internal static string Seconds(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
            double v = Math.Max(0.0, s);
            if (v < 60.0) return v.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            int m = (int)(v / 60.0);
            return m.ToString(CultureInfo.InvariantCulture) + " min " + (v - 60.0 * m).ToString("0", CultureInfo.InvariantCulture) + " s";
        }
    }

    public sealed class InterlayerLayerStats
    {
        public int Layer;
        public double ZMm;
        public double StartS, EndS;
        public double DurationS => EndS - StartS;
        public int Samples, Available, TooFast, TooSlow;
        public double MinS = double.NaN, MeanS = double.NaN, MaxS = double.NaN;
        /// <summary>"ok", "fast", "slow", "fast+slow" or "n/a" (no layer below found).</summary>
        public string Verdict = "n/a";
    }

    public sealed class InterlayerResult
    {
        /// <summary>Per timeline sample: seconds since the nearest extruded point in the layer below
        /// was laid; NaN when none was found inside the search radius.</summary>
        public double[] SecondsBelow = Array.Empty<double>();
        public readonly List<InterlayerLayerStats> Layers = new List<InterlayerLayerStats>();
        public double RadiusMm, LayerHeightMm, MinThresholdS, MaxThresholdS;
        public int Available, Unavailable, TooFast, TooSlow;
        public double MinObservedS = double.NaN, MeanObservedS = double.NaN, MaxObservedS = double.NaN, P95S = double.NaN;
        public string SourceNote = "";

        /// <summary>Legend maximum for a fixed colour scale: 1.5× the slow threshold when
        /// one is set (so the threshold sits inside the ramp), else the 95th percentile of
        /// the observed times, else one minute.</summary>
        public double SuggestLegendMaxS()
        {
            if (MaxThresholdS > 0) return MaxThresholdS * 1.5;
            if (!double.IsNaN(P95S) && P95S > 0) return P95S;
            if (!double.IsNaN(MaxObservedS) && MaxObservedS > 0) return MaxObservedS;
            return 60.0;
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("══ Interlayer time ══");
            sb.AppendLine("clock            " + SourceNote);
            sb.AppendLine(FormattableString.Invariant($"layer height     {LayerHeightMm:0.##} mm   search radius {RadiusMm:0.##} mm"));
            sb.AppendLine(FormattableString.Invariant($"thresholds       too fast < {(MinThresholdS > 0 ? MinThresholdS.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "off")}   too slow > {(MaxThresholdS > 0 ? MaxThresholdS.ToString("0.#", CultureInfo.InvariantCulture) + " s" : "off")}"));
            sb.AppendLine(FormattableString.Invariant($"samples          {Available + Unavailable}   ({Available} with a layer below, {Unavailable} without - first layer, bridges, overhangs)"));
            if (Available > 0)
                sb.AppendLine(FormattableString.Invariant($"time below       min {F(MinObservedS)}   mean {F(MeanObservedS)}   p95 {F(P95S)}   max {F(MaxObservedS)}"));
            sb.AppendLine(FormattableString.Invariant($"out of range     {TooFast} too fast   {TooSlow} too slow"));
            if (Layers.Count > 0 && Layers.Count <= 120)
            {
                sb.AppendLine(FormattableString.Invariant($"{"layer",6}{"Z (mm)",10}{"start",12}{"duration",12}{"min",9}{"mean",9}{"max",9}  verdict"));
                foreach (var l in Layers)
                    sb.AppendLine(FormattableString.Invariant(
                        $"{l.Layer,6}{l.ZMm,10:0.##}{Clock(l.StartS),12}{Clock(l.DurationS),12}{F(l.MinS),9}{F(l.MeanS),9}{F(l.MaxS),9}  {l.Verdict}"));
            }
            return sb.ToString();
        }

        static string F(double s) => double.IsNaN(s) ? "-" : s.ToString("0.#", CultureInfo.InvariantCulture);

        static string Clock(double s)
        {
            if (double.IsNaN(s)) return "-";
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1
                ? FormattableString.Invariant($"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
                : FormattableString.Invariant($"{t.Minutes}:{t.Seconds:00}");
        }
    }

    /// <summary>Interlayer time = for every extruded sample, how long ago the nearest point
    /// directly below it (Z − 0.5h … Z − 1.5h, within the search radius in XY) was
    /// laid. This is the local time-between-layers that governs adhesion: a spiral
    /// gets its revolution time, a seam-per-layer print gets the walk time around the
    /// contour, and a long travel or pause shows up exactly where the clay had time
    /// to skin over. Thresholds only classify; the colour scale reports the number.</summary>
    public static class InterlayerAnalysis
    {
        public static InterlayerResult Compute(InterlayerTimeline tl, double radiusMm, double minS, double maxS)
        {
            if (tl == null) throw new ArgumentNullException(nameof(tl));
            var res = new InterlayerResult
            {
                RadiusMm = radiusMm,
                LayerHeightMm = tl.LayerHeightMm,
                MinThresholdS = Math.Max(0, minS),
                MaxThresholdS = Math.Max(0, maxS),
                SourceNote = tl.Note,
            };
            int n = tl.Count;
            res.SecondsBelow = new double[n];
            for (int k = 0; k < n; k++) res.SecondsBelow[k] = double.NaN;
            if (n == 0) return res;
            double h = tl.LayerHeightMm > 0 ? tl.LayerHeightMm : 1.0;
            double cell = radiusMm > 1e-6 ? radiusMm : Math.Max(h, 1.0);

            // extruded samples: every point that starts or ends an extruding segment
            var onLayer = new bool[n];
            for (int k = 0; k < n; k++)
                onLayer[k] = (k < tl.Extrude.Count && tl.Extrude[k]) || (k + 1 < n && tl.Extrude[k + 1]);

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            for (int k = 0; k < n; k++)
            {
                if (!onLayer[k]) continue;
                var p = tl.Points[k];
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
            }
            if (minX == double.MaxValue) return res;

            var grid = new Dictionary<long, List<int>>();
            for (int k = 0; k < n; k++)
            {
                if (!onLayer[k]) continue;
                long key = Key(tl.Points[k], minX, minY, minZ, cell, h);
                if (!grid.TryGetValue(key, out var bucket)) { bucket = new List<int>(); grid[key] = bucket; }
                bucket.Add(k);
            }

            var values = new List<double>();
            for (int k = 0; k < n; k++)
            {
                if (!onLayer[k]) continue;
                var p = tl.Points[k];
                double tp = tl.TimeS[k];
                int ix = (int)Math.Floor((p.X - minX) / cell), iy = (int)Math.Floor((p.Y - minY) / cell);
                int bandLo = (int)Math.Floor((p.Z - 1.5 * h - minZ) / h), bandHi = (int)Math.Floor((p.Z - 0.5 * h - minZ) / h);
                double best = double.MaxValue; int bestIdx = -1;
                for (int band = bandLo; band <= bandHi; band++)
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (!grid.TryGetValue(Pack(ix + dx, iy + dy, band), out var bucket)) continue;
                            foreach (int q in bucket)
                            {
                                if (tl.TimeS[q] > tp) continue;   // laid later than this sample: not below it yet
                                var pq = tl.Points[q];
                                double dz = p.Z - pq.Z;
                                if (dz < 0.5 * h || dz > 1.5 * h) continue;
                                double ddx = p.X - pq.X, ddy = p.Y - pq.Y;
                                double d2 = ddx * ddx + ddy * ddy;
                                // ties (a closed contour's seam is laid twice at one XY)
                                // go to the most recent layer: that is the surface the
                                // new one lands on
                                if (d2 < best - TieMm2 || (Math.Abs(d2 - best) <= TieMm2 && tl.TimeS[q] > tl.TimeS[bestIdx]))
                                { best = d2; bestIdx = q; }
                            }
                        }
                if (bestIdx >= 0 && best <= cell * cell)
                {
                    double v = tp - tl.TimeS[bestIdx];
                    res.SecondsBelow[k] = v;
                    values.Add(v);
                }
            }

            // per-layer statistics
            var byLayer = new SortedDictionary<int, InterlayerLayerStats>();
            for (int k = 0; k < n; k++)
            {
                int l = tl.Layer[k];
                if (!byLayer.TryGetValue(l, out var s))
                {
                    s = new InterlayerLayerStats { Layer = l, ZMm = double.NaN, StartS = tl.TimeS[k], EndS = tl.TimeS[k] };
                    byLayer[l] = s;
                }
                s.StartS = Math.Min(s.StartS, tl.TimeS[k]);
                s.EndS = Math.Max(s.EndS, tl.TimeS[k]);
                if (!onLayer[k]) continue;
                if (double.IsNaN(s.ZMm)) s.ZMm = tl.Points[k].Z;
                s.Samples++;
                double v = res.SecondsBelow[k];
                if (double.IsNaN(v)) continue;
                s.Available++;
                s.MinS = double.IsNaN(s.MinS) ? v : Math.Min(s.MinS, v);
                s.MaxS = double.IsNaN(s.MaxS) ? v : Math.Max(s.MaxS, v);
                s.MeanS = double.IsNaN(s.MeanS) ? v : s.MeanS + v;
                if (res.MinThresholdS > 0 && v < res.MinThresholdS) { s.TooFast++; res.TooFast++; }
                if (res.MaxThresholdS > 0 && v > res.MaxThresholdS) { s.TooSlow++; res.TooSlow++; }
            }
            foreach (var s in byLayer.Values)
            {
                if (s.Available > 0) s.MeanS /= s.Available;
                if (double.IsNaN(s.ZMm)) s.ZMm = 0;
                s.Verdict = s.Available == 0 ? "n/a"
                          : s.TooFast > 0 && s.TooSlow > 0 ? "fast+slow"
                          : s.TooFast > 0 ? "fast"
                          : s.TooSlow > 0 ? "slow" : "ok";
                res.Layers.Add(s);
            }

            res.Available = values.Count;
            int layerSamples = 0;
            for (int k = 0; k < n; k++) if (onLayer[k]) layerSamples++;
            res.Unavailable = layerSamples - values.Count;
            if (values.Count > 0)
            {
                values.Sort();
                res.MinObservedS = values[0];
                res.MaxObservedS = values[values.Count - 1];
                double sum = 0; foreach (double v in values) sum += v;
                res.MeanObservedS = sum / values.Count;
                res.P95S = values[Math.Min(values.Count - 1, (int)Math.Floor(0.95 * (values.Count - 1)))];
            }
            return res;
        }

        const double TieMm2 = 1e-6;   // (1 µm)²

        static long Key(Vec3 p, double minX, double minY, double minZ, double cell, double h)
            => Pack((int)Math.Floor((p.X - minX) / cell), (int)Math.Floor((p.Y - minY) / cell),
                    (int)Math.Floor((p.Z - minZ) / h));

        static long Pack(int ix, int iy, int band)
            => ((long)(ix & 0x1FFFFF) << 42) | ((long)(iy & 0x1FFFFF) << 21) | (long)(band & 0x1FFFFF);
    }

    /// <summary>How a false-colour preview tells one layer from the next. The
    /// colour scale answers "how many seconds"; the shade only answers "which
    /// layer", so it is display-only and never reaches the emitted colours.</summary>
    public enum LayerShadeMode
    {
        /// <summary>Every layer keeps its scale colour.</summary>
        None,
        /// <summary>Every other layer is tinted down, so a stack of similar times
        /// still reads as separate layers.</summary>
        Alternate,
        /// <summary>A dark-to-bright ramp from the lowest layer to the highest.</summary>
        HeightRamp,
    }

    /// <summary>Layer tints for an unlit false-colour preview. Pure numbers - the UI
    /// layer multiplies its own colours by these factors, so the scale stays the one
    /// definition of what a colour means.</summary>
    public static class LayerShading
    {
        /// <summary>Tint of an odd layer in <see cref="LayerShadeMode.Alternate"/>.</summary>
        public const double AlternateTint = 0.82;
        /// <summary>Tint of the lowest layer in <see cref="LayerShadeMode.HeightRamp"/>.</summary>
        public const double RampFloor = 0.70;

        /// <summary>Luminance factor for a layer, given its zero-based index from the
        /// bottom and the number of layers drawn. Out-of-range indices clamp; a single
        /// layer (or none) is never tinted.</summary>
        public static double Factor(int layer, int layerCount, LayerShadeMode mode)
        {
            switch (mode)
            {
                case LayerShadeMode.Alternate:
                    return (layer & 1) == 0 ? 1.0 : AlternateTint;
                case LayerShadeMode.HeightRamp:
                    if (layerCount <= 1) return 1.0;
                    double t = (double)layer / (layerCount - 1);
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    return RampFloor + (1.0 - RampFloor) * t;
                default:
                    return 1.0;
            }
        }

        /// <summary>The colour scaled by a luminance factor, rounded and clamped to
        /// the byte range. A non-finite factor leaves the colour alone.</summary>
        public static (byte R, byte G, byte B) Apply((byte R, byte G, byte B) rgb, double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor)) factor = 1.0;
            return (Channel(rgb.R, factor), Channel(rgb.G, factor), Channel(rgb.B, factor));
        }

        static byte Channel(byte value, double factor)
        {
            double v = Math.Round(value * factor, MidpointRounding.AwayFromZero);
            return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
        }

        /// <summary>Fake headlight for an unlit false-colour mesh: a face pointing
        /// straight up keeps its colour, a vertical wall drops to 0.75, so the layer
        /// still reads as a solid. <paramref name="nz"/> is the vertical component of
        /// a unit face (or vertex) normal.</summary>
        public static double FaceShade(double nz)
        {
            if (double.IsNaN(nz)) nz = 0;
            return 0.75 + 0.25 * Math.Max(0, nz);
        }
    }
}
