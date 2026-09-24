using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Interlayer time: for every extruded sample, how long ago the layer directly
    /// below it was laid - the local time-between-layers that governs adhesion and
    /// collapse in clay. The clock is the G-code's commanded feeds (optionally with the
    /// accel model) or, when a print log is wired, the log's real timestamps. The
    /// toolpath (lines) or the extruded structure (layers) is painted in Plasma, scrolled
    /// by layer, with user thresholds for "too fast" (layer below not stiff yet) and
    /// "too slow" (layer below skinned over). Analysis lives in Core
    /// (<see cref="InterlayerAnalysis"/>); this component only feeds it and draws.</summary>
    public class InterlayerTimeComponent : IamComponentBase
    {
        public InterlayerTimeComponent()
            : base("Interlayer Time", "Interlayer",
                   "Supply G-code or a print log to measure the delay between printing each layer and the layer below.",
                   "Lugworm", "5 Analyze")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0031-4bfb-9c3d-aa0000000031");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Interlayer();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code as one string or a list of lines. Optional when a print log is wired, since the log's own samples become the path", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("gcodePath", "Gp", "Optional G-code file path, read in the background. With a print log its exact bytes anchor the log's file positions to the moves", "core", PType.Text),
            new ParamSpec("log", "Lg", "Print log CSV lines from a retained job log. When wired, the log's clock replaces the estimate", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("logPath", "Lp", "Optional print log CSV file path, printlog_*.csv or a retained job log", "core", PType.Text),
            new ParamSpec("layer", "L", "Highest visible layer, counted from 1 (0 = all), limiting both preview and output trees", "core", PType.Integer).Hint(0, 500, 0, 0),
            new ParamSpec("isolate", "Is", "Show only the top visible layer instead of everything up to it", "core", PType.Boolean).HintBool(false),
            new ParamSpec("minTime", "Tn", "Minimum acceptable interlayer time in s, calibrated for the material (0 = no lower warning)", "core", PType.Number).Hint(0, 600, 0, 0),
            new ParamSpec("maxTime", "Tx", "Maximum acceptable interlayer time in s, calibrated for the material (0 = no upper warning)", "core", PType.Number).Hint(0, 3600, 120, 0),
            new ParamSpec("legendMax", "Cm", "Color scale maximum, s (0 = auto: 1.5 x maxTime, else the 95th percentile). Fixed per analysis so a color keeps its meaning", "core", PType.Number).Hint(0, 3600, 0, 0),
            new ParamSpec("width", "W", "Layer width, mm (0 = the file's LINEWIDTH header, else 10)", "geometry", PType.Number).Hint(0, 30, 0, 1),
            new ParamSpec("height", "H", "Layer height, mm (0 = the file's LAYERHEIGHT header, else the median Z step)", "geometry", PType.Number).Hint(0, 10, 0, 2),
            new ParamSpec("radius", "R", "Search radius for the layer below, mm (0 = layer width, or 3 x the sample spacing for log only paths)", "geometry", PType.Number).Hint(0, 50, 0, 1),
            new ParamSpec("printFeed", "Pf", "Print feed where a move has no F word, mm/s", "feeds", PType.Number).Hint(1, 200, 25, 0),
            new ParamSpec("travelFeed", "Tf", "Travel feed where a move has no F word, mm/s", "feeds", PType.Number).Hint(5, 500, 50, 0),
            new ParamSpec("accel", "a", "XY acceleration, mm/s² (0 = commanded feeds only). Adds the trapezoidal ramp model of Print Stats to the estimated clock", "feeds", PType.Number).Hint(0, 5000, 0, 0),
            new ParamSpec("display", "D", "0 = toolpath lines, 1 = extruded layers as a vertex colored mesh", "display", PType.Integer).Hint(0, 1, 0, 0),
            new ParamSpec("shade", "Sh", "Tell adjacent layers apart: 0 = none, 1 = alternate layers tinted, 2 = a bottom to top ramp. Preview only, the colors output stays the unshaded scale color", "display", PType.Integer).Hint(0, 2, 1, 0),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("geometry", "Layer geometry inputs (width, height, radius)"),
            ("feeds", "Feed override inputs (estimated clock)"),
            ("display", "Display inputs (lines/layers, layer shading)"),
        };

        protected override IReadOnlyList<(string param, string label, (string label, string val)[] items)> ValueLists => new[]
        {
            ("display", "Display", new[] { ("lines (toolpath)", "0"), ("layers (extruded structure)", "1") }),
            ("shade", "Shade", new[] { ("none", "0"), ("alternate layers", "1"), ("height ramp", "2") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("report", "T", "Readable report of the whole print: clock source, thresholds, totals and a table per layer. Connect a panel", GH_ParamAccess.item);
            pManager.AddNumberParameter("perLayer", "pL", "Data for the visible layers, one branch per layer: layer, Z mm, start s, duration s, min s, mean s, max s, too fast samples, too slow samples", GH_ParamAccess.tree);
            pManager.AddNumberParameter("times", "t", "Interlayer time per path vertex, s. Branches match the path output, cut to the visible layers (-1 = no layer below inside the radius)", GH_ParamAccess.tree);
            pManager.AddCurveParameter("path", "P", "Extrusion runs as polylines, one branch per layer and run, document units, cut to the visible layers", GH_ParamAccess.tree);
            pManager.AddColourParameter("colors", "Cl", "Plasma color per path vertex, branches cut like the path output, for a custom preview. The unshaded scale color, whatever shade draws", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("layers", "L", "Layer count of the whole print, not of the visible cut", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
            pManager.AddTextParameter("legend", "Le", "Legend definition text for Legend, matching this analysis's time scale and warning thresholds", GH_ParamAccess.item);
        }

        // ---- cached state ------------------------------------------------------------

        sealed class LayerRuns
        {
            public int Layer;
            public readonly List<int[]> Runs = new();
        }

        /// <summary>Everything that depends on the path and the clock - the expensive part.</summary>
        sealed class Analysis
        {
            public string Key = "";
            public InterlayerTimeline Timeline = new();
            public InterlayerResult Result = new();
            public readonly List<LayerRuns> Layers = new();
            public double LayerWidthMm, LayerHeightMm;
            public string Info = "";
            public readonly List<(GH_RuntimeMessageLevel level, string text)> Messages = new();
            public GH_Structure<GH_Number> PerLayer = new();
            public GH_Structure<GH_Number> Times = new();
            public GH_Structure<GH_Curve> Path = new();
        }

        const int Buckets = 32;

        /// <summary>Everything that depends on the legend and the display mode - cheap to rebuild.</summary>
        sealed class LayerDraw
        {
            public readonly List<Line>[] Thin = new List<Line>[Buckets + 1];
            public readonly List<Line>[] Thick = new List<Line>[Buckets + 1];
            /// <summary>This layer's bucket colours with its shade factor already
            /// applied, so a frame only picks a colour out of the array.</summary>
            public readonly Color[] BucketColors = new Color[Buckets + 1];
            public Mesh? LayerMesh;
            public BoundingBox Box = BoundingBox.Empty;
            public LayerDraw(double shade)
            {
                for (int b = 0; b <= Buckets; b++)
                {
                    Thin[b] = new List<Line>(); Thick[b] = new List<Line>();
                    BucketColors[b] = Shaded(BucketColor(b), shade);
                }
            }
        }

        sealed class DrawSet : IDisposable
        {
            public string Key = "";
            public double LegendMaxS;
            /// <summary>The legend wire for this scale - it depends on the legend
            /// maximum, so it lives with the draw set that maximum keys.</summary>
            public string Legend = "";
            public readonly SortedDictionary<int, LayerDraw> Layers = new();
            public GH_Structure<GH_Colour> Colors = new();

            public void Dispose()
            {
                foreach (var layer in Layers.Values) layer.LayerMesh?.Dispose();
                Layers.Clear();
            }
        }

        sealed class Result
        {
            public Analysis Analysis = new();
            public DrawSet Draw = new();
        }

        Analysis? _analysis;
        DrawSet? _draw;
        int _visibleTop;
        bool _isolate;
        int _display;
        double _unitScale = 1.0;

        sealed class SourceRead
        {
            public byte[]? Bytes;
            public List<string> Lines = new();
        }

        // background file reads (AUD-PERF-001): keyed by canonical path + file identity
        readonly BackgroundFileWork<SourceRead> _gcodeReader = new("gcodePath read");
        readonly BackgroundFileWork<SourceRead> _logReader = new("logPath read");

        public override void RemovedFromDocument(GH_Document document)
        {
            _gcodeReader.CancelAndDetach();
            _logReader.CancelAndDetach();
            ClearAnalysis();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close)
            {
                _gcodeReader.CancelAndDetach(); _logReader.CancelAndDetach();
                ClearAnalysis();
            }
            base.DocumentContextChanged(document, context);
        }

        // ---- solve -----------------------------------------------------------------------

        protected override object? Compute(IGH_DataAccess DA)
        {
            _unitScale = DocUnits.ScaleToMm();
            string gcodePath = GetString(DA, "gcodePath", "").Trim();
            string logPath = GetString(DA, "logPath", "").Trim();
            int layerTop = GetInt(DA, "layer", 0);
            bool isolate = GetBool(DA, "isolate", false);
            double minT = Math.Max(0, GetNumber(DA, "minTime", 0));
            double maxT = Math.Max(0, GetNumber(DA, "maxTime", 120));
            double legendIn = Math.Max(0, GetNumber(DA, "legendMax", 0));
            double width = Math.Max(0, GetNumber(DA, "width", 0));
            double height = Math.Max(0, GetNumber(DA, "height", 0));
            double radius = Math.Max(0, GetNumber(DA, "radius", 0));
            double printFeed = GetNumber(DA, "printFeed", 25.0);
            double travelFeed = GetNumber(DA, "travelFeed", 50.0);
            double accel = Math.Max(0, GetNumber(DA, "accel", 0));
            int display = Math.Max(0, Math.Min(1, GetInt(DA, "display", 0)));
            var shade = (LayerShadeMode)Math.Max(0, Math.Min(2, GetInt(DA, "shade", (int)LayerShadeMode.Alternate)));

            bool pending = false;
            var gcodeRead = ReadSource(DA, "gcode", gcodePath, _gcodeReader, ref pending, out string gcodeSourceNote);
            var logRead = ReadSource(DA, "log", logPath, _logReader, ref pending, out string logSourceNote);
            if (pending)
            {
                // last good outputs replay while a read is in flight
                return _analysis != null && _draw != null ? new Result { Analysis = _analysis, Draw = _draw } : null;
            }
            if (gcodeRead == null && logRead == null)
            {
                if (gcodeSourceNote.Length > 0 || logSourceNote.Length > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, (gcodeSourceNote + " " + logSourceNote).Trim());
                else
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Wire G-code (gcode or gcodePath), a print log (log or logPath), or both");
                ClearAnalysis();
                return null;
            }
            if (gcodeSourceNote.Length > 0) AddRuntimeMessage(GH_RuntimeMessageLevel.Error, gcodeSourceNote);
            if (logSourceNote.Length > 0) AddRuntimeMessage(GH_RuntimeMessageLevel.Error, logSourceNote);

            string key = string.Join("|",
                SourceKey(gcodeRead),
                logRead != null ? ContentHash.Fnv1aLines(logRead.Lines).ToString("X16", CultureInfo.InvariantCulture) : "-",
                R(width), R(height), R(radius), R(printFeed), R(travelFeed), R(accel), R(minT), R(maxT), R(_unitScale));
            if (_analysis == null || _analysis.Key != key)
            {
                _draw?.Dispose(); _draw = null;
                _analysis = BuildAnalysis(key, gcodeRead, logRead, width, height, radius, printFeed, travelFeed, accel, minT, maxT);
                if (_analysis == null) { Message = ""; return null; }
            }
            foreach (var (level, text) in _analysis.Messages) AddRuntimeMessage(level, text);

            double legendMax = legendIn > 0 ? legendIn : _analysis.Result.SuggestLegendMaxS();
            // NOTE: 'layer' is deliberately absent from both keys - scrolling must cut
            // the trees and the preview without recomputing anything.
            string drawKey = string.Join("|", key, R(legendMax), display.ToString(CultureInfo.InvariantCulture),
                                         ((int)shade).ToString(CultureInfo.InvariantCulture), R(_unitScale));
            if (_draw == null || _draw.Key != drawKey)
            {
                _draw?.Dispose(); _draw = null;
                _draw = BuildDraw(_analysis, drawKey, legendMax, display, shade, minT, maxT);
            }

            int count = _analysis.Timeline.LayerCount;
            _visibleTop = layerTop <= 0 ? count : Math.Min(layerTop, count);
            _isolate = isolate;
            _display = display;
            Message = FormattableString.Invariant($"L {_visibleTop}/{count} - ≤{legendMax:0} s");
            return new Result { Analysis = _analysis, Draw = _draw };
        }

        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        static string SourceKey(SourceRead? source)
        {
            if (source == null) return "-";
            if (source.Bytes == null) return "text:" + ContentHash.Fnv1aLines(source.Lines).ToString("X16", CultureInfo.InvariantCulture);
            // CRLF, BOM and trailing newlines affect log file-position anchors even
            // when the decoded G-code lines are identical.
            using var hash = System.Security.Cryptography.SHA256.Create();
            return "bytes:" + BitConverter.ToString(hash.ComputeHash(source.Bytes));
        }

        void ClearAnalysis()
        {
            _draw?.Dispose(); _draw = null;
            _analysis = null;
            Message = "";
        }

        /// <summary>Text-list input or file path (background read). Returns null when the
        /// source is absent or unreadable; <paramref name="pending"/> is set while a read
        /// is in flight; <paramref name="error"/> carries an unreadable path's reason.</summary>
        SourceRead? ReadSource(IGH_DataAccess DA, string listParam, string path, BackgroundFileWork<SourceRead> reader,
                               ref bool pending, out string error)
        {
            error = "";
            if (path.Length > 0)
            {
                string readKey;
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists)
                    {
                        reader.CancelAndDetach();
                        error = listParam + "Path could not be read: file not found";
                        return null;
                    }
                    readKey = FormattableString.Invariant($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                }
                catch (Exception ex)
                {
                    reader.CancelAndDetach();
                    error = listParam + "Path could not be read: " + ex.Message;
                    return null;
                }
                var got = reader.ResultFor(readKey);
                if (got != null) return got;
                if (reader.ErrorFor(readKey) is string readError)
                {
                    error = readError;
                    reader.ForgetError();
                    return null;
                }
                string p = path;
                reader.Start(this, readKey, ct =>
                {
                    byte[] bytes = File.ReadAllBytes(p);
                    string text = new System.Text.UTF8Encoding(false, false).GetString(bytes).TrimStart('﻿');
                    var ls = new List<string>(text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
                    TrimTrailingEmpty(ls);
                    return new SourceRead { Bytes = bytes, Lines = ls };
                });
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Loading " + path + " …");
                pending = true;
                return null;
            }
            reader.CancelAndDetach();
            var raw = GetStringList(DA, listParam);
            List<string> lines = raw.Count == 1
                ? new List<string>(raw[0].Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
                : new List<string>(raw);
            TrimTrailingEmpty(lines);
            return lines.Count == 0 ? null : new SourceRead { Lines = lines };
        }

        static void TrimTrailingEmpty(List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);
        }

        Analysis? BuildAnalysis(string key, SourceRead? gcode, SourceRead? log, double width, double height, double radius,
                                double printFeed, double travelFeed, double accel, double minT, double maxT)
        {
            var a = new Analysis { Key = key };
            var opt = new InterlayerOptions
            {
                LayerHeightMm = height,
                PrintFeedMmS = printFeed,
                TravelFeedMmS = travelFeed,
                AccelMmS2 = accel,
            };

            List<PrintLogRecord>? records = null;
            string logNote = "";
            if (log != null)
            {
                try
                {
                    var parsed = PrintLogCsvReader.Parse(log.Lines);
                    records = parsed.Records;
                    logNote = parsed.Note;
                }
                catch (FormatException ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Print log: " + ex.Message);
                    if (gcode == null) return null;
                    a.Messages.Add((GH_RuntimeMessageLevel.Warning, "Print log ignored: " + ex.Message));
                }
            }

            InterlayerTimeline tl;
            string clockNote = "";
            if (gcode != null)
            {
                ParsedProgram prog;
                try { prog = GcodeParser.Parse(gcode.Lines, allowPerAxisHome: true); }
                catch (FormatException ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "G-code: " + ex.Message);
                    return null;
                }
                if (prog.Moves.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No moves parsed");
                    return null;
                }
                tl = InterlayerTimeline.FromMoves(prog.Moves, gcode.Lines, opt);
                if (records != null)
                {
                    long[] ends = GcodeText.LineByteEnds(gcode.Lines, gcode.Bytes);
                    clockNote = tl.CalibrateWithLog(records, ends);
                    if (gcode.Bytes == null && tl.Source == InterlayerTimeSource.LogFilePosition)
                        clockNote += ". Byte offsets reconstructed from the text. Wire gcodePath for exact anchors";
                }
            }
            else
            {
                try { tl = InterlayerTimeline.FromLog(records!, opt); }
                catch (FormatException ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Print log: " + ex.Message);
                    return null;
                }
                if (tl.Count < 2)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Print log: " + tl.Note);
                    return null;
                }
                a.Messages.Add((GH_RuntimeMessageLevel.Remark,
                    "Log only path: the recorded samples are sparse, boundaries plus every Nth poll. Wire the G-code for the real toolpath"));
            }
            if (tl.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Empty path");
                return null;
            }

            a.LayerWidthMm = width > 0 ? width : tl.HeaderWidthMm.HasValue && tl.HeaderWidthMm.Value > 0 ? tl.HeaderWidthMm.Value : 10.0;
            a.LayerHeightMm = height > 0 ? height : tl.LayerHeightMm;
            double r = radius > 0 ? radius
                     : tl.Source == InterlayerTimeSource.LogOnly ? Math.Max(a.LayerWidthMm, 3.0 * MedianSpacing(tl))
                     : a.LayerWidthMm;
            a.Timeline = tl;
            a.Result = InterlayerAnalysis.Compute(tl, r, minT, maxT);

            // extrusion runs per layer (a run = consecutive extruding segments on one layer)
            LayerRuns? current = null;
            List<int>? run = null;
            for (int k = 1; k < tl.Count; k++)
            {
                bool ext = tl.Extrude[k];
                int layer = tl.Layer[k];
                if (ext && run != null && current != null && current.Layer == layer)
                {
                    run.Add(k);
                    continue;
                }
                if (run != null) { current!.Runs.Add(run.ToArray()); run = null; }
                if (!ext) continue;
                if (current == null || current.Layer != layer)
                {
                    current = null;
                    foreach (var lr in a.Layers) if (lr.Layer == layer) { current = lr; break; }
                    if (current == null) { current = new LayerRuns { Layer = layer }; a.Layers.Add(current); }
                }
                run = new List<int> { k - 1, k };
            }
            if (run != null && current != null) current.Runs.Add(run.ToArray());
            a.Layers.Sort((x, y) => x.Layer.CompareTo(y.Layer));

            // outputs that do not depend on the legend
            foreach (var lr in a.Layers)
            {
                for (int ri = 0; ri < lr.Runs.Count; ri++)
                {
                    var path = new GH_Path(lr.Layer, ri);
                    var idx = lr.Runs[ri];
                    var pts = new List<Vec3>(idx.Length);
                    foreach (int k in idx)
                    {
                        pts.Add(tl.Points[k]);
                        double v = a.Result.SecondsBelow[k];
                        a.Times.Append(new GH_Number(double.IsNaN(v) ? -1.0 : v), path);
                    }
                    a.Path.Append(new GH_Curve(GhConvert.PtsToCurve(pts, _unitScale)), path);
                }
            }
            foreach (var s in a.Result.Layers)
            {
                var path = new GH_Path(s.Layer);
                a.PerLayer.Append(new GH_Number(s.Layer), path);
                a.PerLayer.Append(new GH_Number(s.ZMm), path);
                a.PerLayer.Append(new GH_Number(s.StartS), path);
                a.PerLayer.Append(new GH_Number(s.DurationS), path);
                a.PerLayer.Append(new GH_Number(double.IsNaN(s.MinS) ? -1 : s.MinS), path);
                a.PerLayer.Append(new GH_Number(double.IsNaN(s.MeanS) ? -1 : s.MeanS), path);
                a.PerLayer.Append(new GH_Number(double.IsNaN(s.MaxS) ? -1 : s.MaxS), path);
                a.PerLayer.Append(new GH_Number(s.TooFast), path);
                a.PerLayer.Append(new GH_Number(s.TooSlow), path);
            }

            var res = a.Result;
            a.Info = tl.Note
                   + (clockNote.Length > 0 ? " - " + clockNote : "")
                   + (logNote.Length > 0 ? " - log: " + logNote : "")
                   + FormattableString.Invariant($" - radius {r:0.#} mm - {res.Available} samples with a layer below, {res.Unavailable} without")
                   + FormattableString.Invariant($" - too fast {res.TooFast} - too slow {res.TooSlow}");
            if (res.TooSlow > 0)
                a.Messages.Add((GH_RuntimeMessageLevel.Warning,
                    FormattableString.Invariant($"{res.TooSlow} sample(s) waited longer than {maxT:0.#} s on the layer below. Adhesion risk")));
            if (res.TooFast > 0)
                a.Messages.Add((GH_RuntimeMessageLevel.Warning,
                    FormattableString.Invariant($"{res.TooFast} sample(s) landed within {minT:0.#} s on the layer below. It may not carry the load")));
            if (tl.LayerSource == "z-step" && tl.Note.Contains("unknown"))
                a.Messages.Add((GH_RuntimeMessageLevel.Warning, "Layer height could not be derived. Wire height from the geometry group"));
            return a;
        }

        static double MedianSpacing(InterlayerTimeline tl)
        {
            var d = new List<double>(tl.Count);
            for (int k = 1; k < tl.Count; k++)
            {
                double s = tl.Points[k].DistanceTo(tl.Points[k - 1]);
                if (s > 1e-6) d.Add(s);
            }
            if (d.Count == 0) return 1.0;
            d.Sort();
            return d[d.Count / 2];
        }

        DrawSet BuildDraw(Analysis a, string key, double legendMaxS, int display, LayerShadeMode shade,
                          double minT, double maxT)
        {
            var ds = new DrawSet { Key = key, LegendMaxS = legendMaxS, Legend = LegendWire(a, legendMaxS, minT, maxT) };
            var tl = a.Timeline;
            var below = a.Result.SecondsBelow;
            Transform toDoc = DocUnits.FromMmTransform(_unitScale);
            bool unity = DocUnits.IsUnity(_unitScale);

            int totalRunPoints = 0;
            foreach (var lr in a.Layers) foreach (var run in lr.Runs) totalRunPoints += run.Length;
            int budget = ExtrusionPreview.DefaultMaxPoints;

            for (int li = 0; li < a.Layers.Count; li++)
            {
                var lr = a.Layers[li];
                // the shade says "which layer", the colour says "how many seconds";
                // the emitted colours below stay unshaded because they are data
                double shadeFactor = LayerShading.Factor(li, a.Layers.Count, shade);
                var ld = new LayerDraw(shadeFactor);
                Mesh? extruded = display == 1 ? new Mesh() : null;
                for (int ri = 0; ri < lr.Runs.Count; ri++)
                {
                    var idx = lr.Runs[ri];
                    var path = new GH_Path(lr.Layer, ri);
                    Point3d prev = DocUnits.FromMm(GhConvert.ToPt(tl.Points[idx[0]]), _unitScale);
                    ld.Box.Union(prev);
                    for (int j = 0; j < idx.Length; j++)
                    {
                        int k = idx[j];
                        double v = below[k];
                        var (cr, cg, cb) = PlasmaColorScale.ForSeconds(v, legendMaxS);
                        ds.Colors.Append(new GH_Colour(Color.FromArgb(cr, cg, cb)), path);
                        if (j == 0) continue;
                        Point3d p = DocUnits.FromMm(GhConvert.ToPt(tl.Points[k]), _unitScale);
                        ld.Box.Union(p);
                        var line = new Line(prev, p);
                        bool flagged = !double.IsNaN(v) && ((minT > 0 && v < minT) || (maxT > 0 && v > maxT));
                        (flagged ? ld.Thick : ld.Thin)[Bucket(v, legendMaxS)].Add(line);
                        prev = p;
                    }
                    if (extruded != null)
                    {
                        int runBudget = totalRunPoints <= budget ? idx.Length
                            : Math.Max(2, (int)((double)budget * idx.Length / totalRunPoints));
                        int[] use = runBudget >= idx.Length ? idx : Decimate(idx, runBudget);
                        var pts = new List<Vec3>(use.Length);
                        var cols = new List<Color>(use.Length);
                        foreach (int k in use)
                        {
                            pts.Add(tl.Points[k]);
                            var (cr, cg, cb) = PlasmaColorScale.ForSeconds(below[k], legendMaxS);
                            cols.Add(Color.FromArgb(cr, cg, cb));
                        }
                        using var piece = ExtrusionPreview.BuildLayer(pts, a.LayerWidthMm, a.LayerHeightMm, cols);
                        if (piece != null) { ShadeLayer(piece, shadeFactor); extruded.Append(piece); }
                    }
                }
                if (extruded != null && extruded.Faces.Count > 0)
                {
                    if (!unity) extruded.Transform(toDoc);
                    ld.LayerMesh = extruded;
                    ld.Box.Union(extruded.GetBoundingBox(false));
                }
                else extruded?.Dispose();
                ds.Layers[lr.Layer] = ld;
            }
            return ds;
        }

        static int[] Decimate(int[] idx, int budget)
        {
            if (idx.Length <= budget) return idx;
            var result = new int[budget];
            double step = (double)(idx.Length - 1) / (budget - 1);
            for (int i = 0; i < budget; i++) result[i] = idx[(int)Math.Round(i * step)];
            return result;
        }

        static int Bucket(double seconds, double legendMaxS)
        {
            if (double.IsNaN(seconds) || !(legendMaxS > 0)) return Buckets;   // grey
            int b = (int)Math.Floor(Math.Max(0, seconds) / legendMaxS * Buckets);
            return Math.Max(0, Math.Min(Buckets - 1, b));
        }

        static Color BucketColor(int b)
        {
            if (b >= Buckets)
            {
                var u = PlasmaColorScale.Unavailable;
                return Color.FromArgb(u.R, u.G, u.B);
            }
            var (r, g, bl) = PlasmaColorScale.Map((b + 0.5) / Buckets);
            return Color.FromArgb(r, g, bl);
        }

        static Color Shaded(Color c, double factor)
        {
            if (factor >= 1.0) return c;
            var (r, g, b) = LayerShading.Apply((c.R, c.G, c.B), factor);
            return Color.FromArgb(r, g, b);
        }

        /// <summary>Layer tint plus a fake headlight on the layer's own normals -
        /// <c>DrawMeshFalseColors</c> is unlit, so without this a coloured layer reads
        /// as a flat silhouette instead of a solid.</summary>
        static void ShadeLayer(Mesh mesh, double layerFactor)
        {
            int n = mesh.VertexColors.Count;
            if (n == 0) return;
            if (mesh.Normals.Count != mesh.Vertices.Count) mesh.Normals.ComputeNormals();
            bool lit = mesh.Normals.Count >= n;
            for (int i = 0; i < n; i++)
            {
                double f = layerFactor * (lit ? LayerShading.FaceShade(mesh.Normals[i].Z) : 1.0);
                var c = mesh.VertexColors[i];
                var (r, g, b) = LayerShading.Apply((c.R, c.G, c.B), f);
                mesh.VertexColors[i] = Color.FromArgb(r, g, b);
            }
        }

        static string SourceLabel(InterlayerTimeSource source) => source switch
        {
            InterlayerTimeSource.Estimated => "estimated from feeds",
            InterlayerTimeSource.LogFilePosition => "print log (file positions)",
            InterlayerTimeSource.LogLayerSpans => "print log (layer spans)",
            _ => "print log samples",
        };

        /// <summary>The scale this component painted, as the wire Legend reads -
        /// built from the same Core scale, so the viewport and the legend cannot disagree.</summary>
        static string LegendWire(Analysis a, double legendMaxS, double minT, double maxT)
        {
            var marks = new List<(double Value, string Label)>(2);
            if (minT > 0) marks.Add((minT, "fast"));
            if (maxT > 0) marks.Add((maxT, "slow"));
            return new LegendSpec(LegendType.Continuous,
                                  title: "interlayer time - " + SourceLabel(a.Timeline.Source),
                                  unit: "s", min: 0, max: legendMaxS, scheme: ColorSchemes.Plasma,
                                  marks: marks,
                                  unavailable: (Rgb.Of(PlasmaColorScale.Unavailable), "no layer below"),
                                  decimals: 0).Serialize();
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Analysis.Result.Report());
            DA.SetDataTree(1, Cut(r.Analysis.PerLayer));
            DA.SetDataTree(2, Cut(r.Analysis.Times));
            DA.SetDataTree(3, Cut(r.Analysis.Path));
            DA.SetDataTree(4, Cut(r.Draw.Colors));
            DA.SetData(5, r.Analysis.Timeline.LayerCount);
            DA.SetData(6, r.Analysis.Info);
            DA.SetData(7, r.Draw.Legend);
        }

        /// <summary>The branches whose layer (the first path index) the scroll leaves
        /// visible. A cheap filter over the cached trees - the branches themselves are
        /// reused, so scrolling never touches the analysis.</summary>
        GH_Structure<T> Cut<T>(GH_Structure<T> tree) where T : IGH_Goo
        {
            bool cut = false;
            for (int i = 0; i < tree.PathCount && !cut; i++)
                cut = tree.Paths[i].Length > 0 && !Visible(tree.Paths[i][0]);
            if (!cut) return tree;
            var visible = new GH_Structure<T>();
            for (int i = 0; i < tree.PathCount; i++)
            {
                var path = tree.Paths[i];
                if (path.Length > 0 && !Visible(path[0])) continue;
                visible.AppendRange(tree.Branches[i], path);
            }
            return visible;
        }

        // ---- viewport ----------------------------------------------------------------------

        bool Visible(int layer) => _isolate ? layer == _visibleTop : layer <= _visibleTop;

        public override BoundingBox ClippingBox
        {
            get
            {
                if (_draw == null) return base.ClippingBox;
                var box = BoundingBox.Empty;
                foreach (var kv in _draw.Layers) if (Visible(kv.Key)) box.Union(kv.Value.Box);
                return box.IsValid ? box : base.ClippingBox;
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            if (_draw == null || Hidden || Locked || _display != 1) return;
            foreach (var kv in _draw.Layers)
            {
                if (!Visible(kv.Key) || kv.Value.LayerMesh == null) continue;
                args.Display.DrawMeshFalseColors(kv.Value.LayerMesh);
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (_draw == null || Hidden || Locked) return;
            if (_display == 0)
            {
                foreach (var kv in _draw.Layers)
                {
                    if (!Visible(kv.Key)) continue;
                    var ld = kv.Value;
                    for (int b = 0; b <= Buckets; b++)
                    {
                        // one batched call per bucket per layer: the bucket says how many
                        // seconds, the layer's own copy of the colour says which layer
                        if (ld.Thin[b].Count > 0) args.Display.DrawLines(ld.Thin[b], ld.BucketColors[b], 1);
                        if (ld.Thick[b].Count > 0) args.Display.DrawLines(ld.Thick[b], ld.BucketColors[b], 3);
                    }
                }
            }
        }
    }
}
