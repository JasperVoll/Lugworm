using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class InterlayerTimeTests
{
    // ---- fixtures ----------------------------------------------------------------

    /// <summary>Two 10 mm square layers at 10 mm/s (F600), layer height 2 mm, Lugworm
    /// markers and header. Each side takes 1 s, so a layer takes 4 s of extrusion.</summary>
    static List<string> TwoSquareLayers(bool markers = true, bool header = true)
    {
        var l = new List<string>();
        if (header)
        {
            l.Add("; --- BEGIN HEADER ---");
            l.Add("; PRINTSPEED=10");
            l.Add("; LAYERHEIGHT=2");
            l.Add("; LINEWIDTH=8");
            l.Add("; --- END HEADER ---");
        }
        l.Add("G21");
        l.Add("G90");
        l.Add("M83");
        l.Add("G0 X0 Y0 Z0 F3000");
        for (int layer = 1; layer <= 2; layer++)
        {
            double z = (layer - 1) * 2.0;
            if (markers) l.Add($"; layer {layer}/2");
            if (layer > 1) l.Add(FormattableString.Invariant($"G1 Z{z:0.###} F600"));
            l.Add(FormattableString.Invariant($"G1 X10 Y0 Z{z:0.###} F600 E1"));
            l.Add(FormattableString.Invariant($"G1 X10 Y10 Z{z:0.###} F600 E1"));
            l.Add(FormattableString.Invariant($"G1 X0 Y10 Z{z:0.###} F600 E1"));
            l.Add(FormattableString.Invariant($"G1 X0 Y0 Z{z:0.###} F600 E1"));
        }
        return l;
    }

    static InterlayerTimeline Timeline(List<string> lines, InterlayerOptions? o = null)
        => InterlayerTimeline.FromMoves(GcodeParser.Parse(lines, allowPerAxisHome: true).Moves, lines, o);

    static PrintLogRecord Rec(long seq, PrintLogEventKind kind, int? layer, double x, double y, double z,
                              double elapsed, long? filePos = null, int run = 1, string status = "printing")
        => new PrintLogRecord
        {
            Seq = seq,
            Kind = kind,
            JobRun = run,
            JobFile = "0:/gcodes/part.gcode",
            Layer = layer,
            LayerSource = layer.HasValue ? PrintLogLayerSource.Firmware : PrintLogLayerSource.None,
            TakenUtc = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc).AddSeconds(elapsed),
            JobElapsedSeconds = elapsed,
            Position = new Vec3(x, y, z),
            FilePosition = filePos,
            Status = status,
        };

    // ---- colour scale ------------------------------------------------------------

    [Fact]
    public void Plasma_endpoints_grey_and_saturation()
    {
        Assert.Equal(((byte)13, (byte)8, (byte)135), PlasmaColorScale.Map(0));
        Assert.Equal(((byte)240, (byte)249, (byte)33), PlasmaColorScale.Map(1));
        Assert.Equal(PlasmaColorScale.Map(0.5), PlasmaColorScale.ForSeconds(30, 60));
        Assert.Equal(PlasmaColorScale.Map(1), PlasmaColorScale.ForSeconds(600, 60));
        Assert.Equal(PlasmaColorScale.Unavailable, PlasmaColorScale.ForSeconds(double.NaN, 60));
        Assert.Equal(PlasmaColorScale.Unavailable, PlasmaColorScale.ForSeconds(5, 0));
        Assert.True(PlasmaColorScale.Saturated(60, 60));
        Assert.False(PlasmaColorScale.Saturated(59.9, 60));
        Assert.False(PlasmaColorScale.Saturated(double.NaN, 60));
        // plasma is not viridis: the two overlays must stay distinguishable
        Assert.NotEqual(DeviationColorScale.Map(0), PlasmaColorScale.Map(0));
        Assert.NotEqual(DeviationColorScale.Map(1), PlasmaColorScale.Map(1));
    }

    // ---- layer markers -----------------------------------------------------------

    [Fact]
    public void Markers_iamlab_cura_and_prusa_forms_are_read_and_noise_is_not()
    {
        var iam = GcodeLayerMarkers.Scan(new[] { "; layer height 3", "G1 X1", "; layer 1/26", "G1 X2", ";  LAYER 2/26" });
        Assert.Equal(new[] { 3, 5 }, iam.Keys.ToArray());
        Assert.Equal(new[] { 1, 2 }, iam.Values.ToArray());

        var cura = GcodeLayerMarkers.Scan(new[] { ";LAYER_COUNT:2", ";LAYER:0", "G1 X1", ";LAYER:1" });
        Assert.Equal(new[] { 1, 2 }, cura.Values.ToArray());   // 0-based shifted to 1-based

        var prusa = GcodeLayerMarkers.Scan(new[] { ";LAYER_CHANGE", ";Z:0.2", "G1 X1", ";LAYER_CHANGE" });
        Assert.Equal(new[] { 1, 4 }, prusa.Keys.ToArray());
        Assert.Equal(new[] { 1, 2 }, prusa.Values.ToArray());

        Assert.Empty(GcodeLayerMarkers.Scan(new[] { "G1 X1 ; layer 3", "; layers: 3" }));
        Assert.Equal(3.0, GcodeLayerMarkers.HeaderNumber(new[] { "; LAYERHEIGHT=3", "G1" }, "layerheight"));
        Assert.Null(GcodeLayerMarkers.HeaderNumber(new[] { "; LAYERHEIGHT=abc" }, "LAYERHEIGHT"));
        Assert.Null(GcodeLayerMarkers.HeaderNumber(new[] { "G1 X1" }, "LAYERHEIGHT"));
    }

    // ---- timeline from moves -----------------------------------------------------

    [Fact]
    public void Timeline_from_gcode_uses_markers_header_and_commanded_feeds()
    {
        var tl = Timeline(TwoSquareLayers());
        Assert.Equal("markers", tl.LayerSource);
        Assert.Equal(2, tl.LayerCount);
        Assert.Equal(2.0, tl.LayerHeightMm);
        Assert.Equal(8.0, tl.HeaderWidthMm);
        Assert.Equal(InterlayerTimeSource.Estimated, tl.Source);
        // 8 sides of 1 s plus the 0.2 s Z move; the G0 approach has zero length
        Assert.InRange(tl.TotalS, 8.19, 8.21);
        for (int k = 1; k < tl.Count; k++) Assert.True(tl.TimeS[k] >= tl.TimeS[k - 1]);
        Assert.Equal(1, tl.Layer[0]);
        Assert.Equal(2, tl.Layer[tl.Count - 1]);
    }

    [Fact]
    public void Timeline_without_markers_or_header_falls_back_to_z_steps()
    {
        var tl = Timeline(TwoSquareLayers(markers: false, header: false));
        Assert.Equal("z-step", tl.LayerSource);
        Assert.Equal(2, tl.LayerCount);
        Assert.Equal(2.0, tl.LayerHeightMm);
        Assert.Null(tl.HeaderWidthMm);
        // the travel that lifts to layer 2 is still "layer 1" until the first layer of 2
        int firstLayer2 = tl.Layer.IndexOf(2);
        Assert.True(firstLayer2 > 0 && tl.Extrude[firstLayer2]);
    }

    [Fact]
    public void Explicit_layer_height_wins_over_the_header()
    {
        var tl = Timeline(TwoSquareLayers(), new InterlayerOptions { LayerHeightMm = 1.0 });
        Assert.Equal(1.0, tl.LayerHeightMm);
        Assert.Contains("(input)", tl.Note);
    }

    [Fact]
    public void Accel_model_only_ever_slows_the_clock()
    {
        var lower = Timeline(TwoSquareLayers());
        var accel = Timeline(TwoSquareLayers(), new InterlayerOptions { AccelMmS2 = 200 });
        Assert.Equal(lower.Count, accel.Count);
        Assert.True(accel.TotalS > lower.TotalS);
        for (int k = 1; k < accel.Count; k++) Assert.True(accel.TimeS[k] >= accel.TimeS[k - 1]);
        Assert.Contains("accel", accel.Note);
    }

    // ---- analysis ----------------------------------------------------------------

    [Fact]
    public void Layer_two_sees_the_time_since_layer_one_passed_below_and_layer_one_has_nothing_below()
    {
        var tl = Timeline(TwoSquareLayers());
        var res = InterlayerAnalysis.Compute(tl, radiusMm: 8.0, minS: 0, maxS: 0);
        Assert.Equal(tl.Count, res.SecondsBelow.Length);
        int seam = tl.Layer.IndexOf(2);   // the Z move lands right above where layer 1 just ended
        for (int k = 0; k < tl.Count; k++)
        {
            bool onLayer = tl.Extrude[k] || (k + 1 < tl.Count && tl.Extrude[k + 1]);
            if (!onLayer) { Assert.True(double.IsNaN(res.SecondsBelow[k])); continue; }
            if (tl.Layer[k] == 1) Assert.True(double.IsNaN(res.SecondsBelow[k]));
            else if (k == seam) Assert.InRange(res.SecondsBelow[k], 0.19, 0.21);   // just the Z move
            else Assert.InRange(res.SecondsBelow[k], 4.19, 4.21);   // one lap plus the Z move
        }
        Assert.Equal(2, res.Layers.Count);
        Assert.Equal("n/a", res.Layers[0].Verdict);
        Assert.Equal("ok", res.Layers[1].Verdict);
        Assert.InRange(res.Layers[1].MaxS, 4.19, 4.21);
        Assert.InRange(res.Layers[1].MinS, 0.19, 0.21);
        Assert.InRange(res.Layers[0].DurationS, 3.99, 4.01);
        Assert.Equal(0.0, res.Layers[0].ZMm);
        Assert.Equal(2.0, res.Layers[1].ZMm);
        Assert.True(res.Available > 0 && res.Unavailable > 0);
        Assert.Contains("Interlayer time", res.Report());
    }

    [Fact]
    public void Thresholds_classify_without_touching_the_values()
    {
        var tl = Timeline(TwoSquareLayers());
        var fast = InterlayerAnalysis.Compute(tl, 8.0, minS: 10, maxS: 0);
        var slow = InterlayerAnalysis.Compute(tl, 8.0, minS: 0, maxS: 3);
        var free = InterlayerAnalysis.Compute(tl, 8.0, 0, 0);
        Assert.Equal("fast", fast.Layers[1].Verdict);
        Assert.Equal("slow", slow.Layers[1].Verdict);   // the lap points; the seam point is fast, not slow
        Assert.True(fast.TooFast > 0 && fast.TooSlow == 0);
        Assert.True(slow.TooSlow > 0 && slow.TooFast == 0);
        Assert.Equal(free.SecondsBelow, fast.SecondsBelow);
        Assert.Equal(free.SecondsBelow, slow.SecondsBelow);
        Assert.Equal(4.5, slow.SuggestLegendMaxS());
        Assert.InRange(free.SuggestLegendMaxS(), 4.19, 4.21);   // p95 of the observed times
    }

    [Fact]
    public void Search_radius_bounds_what_counts_as_below()
    {
        // layer 2 shifted 5 mm sideways: inside an 8 mm radius, outside a 2 mm one
        var lines = TwoSquareLayers();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith("G1 X") && lines[i].Contains("Z2"))
                lines[i] = lines[i].Replace("X10", "X15").Replace("X0", "X5");
        var tl = Timeline(lines);
        var near = InterlayerAnalysis.Compute(tl, 8.0, 0, 0);
        var tight = InterlayerAnalysis.Compute(tl, 2.0, 0, 0);
        Assert.True(near.Layers[1].Available > 1);
        // only the Z move's landing point (still at layer 1's XY) has a layer within 2 mm
        Assert.Equal(1, tight.Layers[1].Available);
        Assert.Equal(near.Layers[1].Samples, tight.Layers[1].Samples);
    }

    [Fact]
    public void Continuous_spiral_gets_its_revolution_time()
    {
        // a helix of pitch 2 mm, radius 10, 20 segments per turn, 3 turns at 10 mm/s
        var l = new List<string> { "G21", "G90", "M83", "G0 X10 Y0 Z0 F3000" };
        int perTurn = 20, turns = 3;
        for (int i = 1; i <= perTurn * turns; i++)
        {
            double a = 2 * Math.PI * i / perTurn;
            l.Add(FormattableString.Invariant($"G1 X{10 * Math.Cos(a):0.###} Y{10 * Math.Sin(a):0.###} Z{2.0 * i / perTurn:0.###} F600 E1"));
        }
        var tl = Timeline(l, new InterlayerOptions { LayerHeightMm = 2.0 });
        Assert.Equal("z-step", tl.LayerSource);
        Assert.True(tl.LayerCount >= 3);
        var res = InterlayerAnalysis.Compute(tl, 4.0, 0, 0);
        double lapS = perTurn * (2 * 10 * Math.Sin(Math.PI / perTurn)) / 10.0;   // chord length per segment / feed
        var found = res.SecondsBelow.Where(v => !double.IsNaN(v)).ToList();
        Assert.True(found.Count > perTurn);
        foreach (double v in found) Assert.InRange(v, lapS * 0.9, lapS * 1.1);
    }

    // ---- print log CSV -----------------------------------------------------------

    [Fact]
    public void Print_log_csv_round_trips_through_the_reader()
    {
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.JobStart, null, 0, 0, 0, 0),
            Rec(2, PrintLogEventKind.LayerStart, 1, 1.5, -2.25, 0, 0.5, 120),
            Rec(3, PrintLogEventKind.Interval, 1, 5, 5, 0, 2.5, 400, status: "printing, \"busy\""),
            Rec(4, PrintLogEventKind.LayerEnd, 1, 0, 0, 0, 4.5, 700),
        };
        records[3].FinishReason = PrintLogFinishReason.SessionStopped;
        records[3].TerminalStatus = "idle";
        var lines = new List<string> { PrintLogCsv.Header() };
        foreach (var r in records) lines.Add(PrintLogCsv.Row(r));

        var read = PrintLogCsvReader.Parse(lines);
        Assert.Equal(0, read.SkippedRows);
        Assert.Equal(4, read.Records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            var a = records[i]; var b = read.Records[i];
            Assert.Equal(a.Seq, b.Seq);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Layer, b.Layer);
            Assert.Equal(a.LayerSource, b.LayerSource);
            Assert.Equal(a.Position.X, b.Position.X, 9);
            Assert.Equal(a.Position.Y, b.Position.Y, 9);
            Assert.Equal(a.JobElapsedSeconds, b.JobElapsedSeconds);
            Assert.Equal(a.FilePosition, b.FilePosition);
            Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.JobFile, b.JobFile);
            Assert.Equal(a.TakenUtc, b.TakenUtc);
            Assert.Equal(DateTimeKind.Utc, b.TakenUtc.Kind);
        }
        Assert.Equal(PrintLogFinishReason.SessionStopped, read.Records[3].FinishReason);
        Assert.Equal("idle", read.Records[3].TerminalStatus);
    }

    [Fact]
    public void Print_log_reader_tolerates_reordered_columns_and_skips_bad_rows()
    {
        var lines = new List<string>
        {
            "﻿layer,z,y,x,job_elapsed_s,extra",
            "1,0,2,1,10,foo",
            "1,abc,2,1,11,bar",
            "2,2,2,1,20,baz",
        };
        var read = PrintLogCsvReader.Parse(lines);
        Assert.Equal(1, read.SkippedRows);
        Assert.Equal(2, read.Records.Count);
        Assert.Equal(new Vec3(1, 2, 0).Z, read.Records[0].Position.Z);
        Assert.Equal(2, read.Records[1].Layer);
        Assert.Contains("skipped", read.Note);
        Assert.Throws<FormatException>(() => PrintLogCsvReader.Parse(new[] { "seq,kind", "1,Interval" }));
        Assert.Throws<FormatException>(() => PrintLogCsvReader.Parse(new[] { "", "  " }));
    }

    // ---- log-only timeline ----------------------------------------------------------

    [Fact]
    public void Log_only_timeline_uses_the_run_with_the_most_samples_and_its_job_clock()
    {
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.LayerStart, 1, 0, 0, 0, 100, run: 1),
            Rec(2, PrintLogEventKind.LayerEnd, 1, 10, 0, 0, 101, run: 1),
            Rec(3, PrintLogEventKind.LayerStart, 1, 0, 0, 0, 0, run: 2),
            Rec(4, PrintLogEventKind.Interval, 1, 10, 0, 0, 1, run: 2),
            Rec(5, PrintLogEventKind.Interval, 1, 10, 10, 0, 2, run: 2),
            Rec(6, PrintLogEventKind.LayerStart, 2, 0, 0, 2, 3, run: 2),
            Rec(7, PrintLogEventKind.Interval, 2, 10, 0, 2, 40, run: 2),
            Rec(8, PrintLogEventKind.Interval, 2, 10, 10, 2, 41, run: 2),
        };
        var tl = InterlayerTimeline.FromLog(records);
        Assert.Equal(InterlayerTimeSource.LogOnly, tl.Source);
        Assert.Equal(6, tl.Count);
        Assert.Equal(2, tl.LayerCount);
        Assert.Equal(2.0, tl.LayerHeightMm);
        Assert.Equal(0.0, tl.TimeS[0]);
        Assert.Equal(41.0, tl.TimeS[5]);
        Assert.Contains("job run 2", tl.Note);
        Assert.Contains("job clock", tl.Note);

        var res = InterlayerAnalysis.Compute(tl, 5.0, 0, 0);
        // (10,0,2) at 40 s sits above (10,0,0) laid at 1 s
        int idx = tl.Points.FindIndex(p => p.Z == 2 && p.X == 10 && p.Y == 0);
        Assert.Equal(39.0, res.SecondsBelow[idx], 6);
    }

    [Fact]
    public void Log_only_timeline_without_layer_numbers_uses_the_height_hint_or_one_layer()
    {
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.Interval, null, 0, 0, 0, 0),
            Rec(2, PrintLogEventKind.Interval, null, 10, 0, 0, 1),
            Rec(3, PrintLogEventKind.Interval, null, 10, 0, 3, 2),
        };
        Assert.Equal(1, InterlayerTimeline.FromLog(records).LayerCount);
        Assert.Equal(2, InterlayerTimeline.FromLog(records, new InterlayerOptions { LayerHeightMm = 3 }).LayerCount);
        Assert.Contains("no positioned samples", InterlayerTimeline.FromLog(new List<PrintLogRecord>()).Note);
    }

    // ---- calibration by the log ---------------------------------------------------

    [Fact]
    public void Layer_spans_from_the_log_rescale_each_layers_estimated_clock()
    {
        var tl = Timeline(TwoSquareLayers());
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.LayerStart, 1, 0, 0, 0, 0),
            Rec(2, PrintLogEventKind.LayerEnd, 1, 0, 0, 0, 40),
            Rec(3, PrintLogEventKind.LayerStart, 2, 0, 0, 2, 40),
            Rec(4, PrintLogEventKind.LayerEnd, 2, 0, 0, 2, 80),
        };
        string note = tl.CalibrateWithLog(records, lineByteEnds: null);
        Assert.Equal(InterlayerTimeSource.LogLayerSpans, tl.Source);
        Assert.Contains("2 of 2 layers matched", note);
        Assert.InRange(tl.TotalS, 79.9, 80.1);
        for (int k = 1; k < tl.Count; k++) Assert.True(tl.TimeS[k] >= tl.TimeS[k - 1]);
        var res = InterlayerAnalysis.Compute(tl, 8.0, 0, 0);
        // a lap now takes 40 s: every corner sits 40 s above its layer, the seam 0 s
        Assert.InRange(res.Layers[1].MaxS, 39.9, 40.1);
        Assert.InRange(res.Layers[1].MinS, 0, 0.1);
        Assert.InRange(res.Layers[0].DurationS, 39.9, 40.1);
    }

    [Fact]
    public void Layer_spans_missing_from_the_log_keep_the_estimated_pace_continuously()
    {
        var tl = Timeline(TwoSquareLayers());
        double estLayer2 = tl.TimeS[tl.Count - 1] - tl.TimeS[tl.Layer.IndexOf(2)];
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.LayerStart, 1, 0, 0, 0, 0),
            Rec(2, PrintLogEventKind.LayerEnd, 1, 0, 0, 0, 40),
        };
        string note = tl.CalibrateWithLog(records, null);
        Assert.Contains("1 of 2 layers matched", note);
        int a = tl.Layer.IndexOf(2);
        Assert.InRange(tl.TimeS[a], 40.1, 40.3);   // 40 s at the log's layer end, plus the 0.2 s Z move at estimated pace
        Assert.InRange(tl.TimeS[tl.Count - 1] - tl.TimeS[a], estLayer2 - 1e-6, estLayer2 + 1e-6);
    }

    [Fact]
    public void File_positions_from_the_log_anchor_the_clock_to_the_moves()
    {
        var lines = TwoSquareLayers();
        var tl = Timeline(lines);
        long[] ends = GcodeText.LineByteEnds(lines, null);
        int lastLine1 = lines.FindLastIndex(s => s.Contains("Z0 F600 E1")) + 1;   // 1-based
        int lastLine2 = lines.Count;
        var records = new List<PrintLogRecord>
        {
            Rec(1, PrintLogEventKind.LayerStart, 1, 0, 0, 0, 0, filePos: ends[lines.FindIndex(s => s.StartsWith("G0 "))]),
            Rec(2, PrintLogEventKind.LayerEnd, 1, 0, 0, 0, 30, filePos: ends[lastLine1 - 1]),
            Rec(3, PrintLogEventKind.LayerEnd, 2, 0, 0, 2, 60, filePos: ends[lastLine2 - 1]),
        };
        string note = tl.CalibrateWithLog(records, ends);
        Assert.Equal(InterlayerTimeSource.LogFilePosition, tl.Source);
        Assert.Contains("3 anchors", note);
        int endOfLayer1 = tl.LineNumber.LastIndexOf(lastLine1);
        Assert.InRange(tl.TimeS[endOfLayer1], 29.9, 30.1);
        Assert.InRange(tl.TimeS[tl.Count - 1], 59.9, 60.1);
        for (int k = 1; k < tl.Count; k++) Assert.True(tl.TimeS[k] >= tl.TimeS[k - 1]);

        // without byte offsets the same log falls back to its layer spans
        var tl2 = Timeline(lines);
        tl2.CalibrateWithLog(records, null);
        Assert.Equal(InterlayerTimeSource.LogLayerSpans, tl2.Source);

        // a log without positions, layers or clock leaves the estimate alone
        var tl3 = Timeline(lines);
        var estimate = new List<double>(tl3.TimeS);
        string kept = tl3.CalibrateWithLog(new List<PrintLogRecord> { Rec(1, PrintLogEventKind.Interval, null, 0, 0, 0, 5) }, ends);
        Assert.Equal(InterlayerTimeSource.Estimated, tl3.Source);
        Assert.Equal(estimate, tl3.TimeS);
        Assert.Contains("estimated clock kept", kept);
    }

    [Fact]
    public void Line_byte_ends_count_terminators_exactly_or_approximately()
    {
        var lines = new List<string> { "G1 X1", "G1 X2", "" };
        var approx = GcodeText.LineByteEnds(lines, null);
        Assert.Equal(new long[] { 6, 12, 13 }, approx);
        byte[] crlf = System.Text.Encoding.UTF8.GetBytes("﻿G1 X1\r\nG1 X2\r\n");
        var exact = GcodeText.LineByteEnds(lines, crlf);
        Assert.Equal(new long[] { 10, 17, 17 }, exact);
    }

    // ---- culture ------------------------------------------------------------------

    [Fact]
    public void A_single_layer_sample_cannot_replace_the_estimated_duration()
    {
        var tl = Timeline(TwoSquareLayers());
        var estimate = tl.TimeS.ToArray();
        tl.CalibrateWithLog(new[] { Rec(1, PrintLogEventKind.Interval, 1, 0, 0, 0, 10) }, null);
        Assert.Equal(InterlayerTimeSource.Estimated, tl.Source);
        Assert.Equal(estimate, tl.TimeS);
    }

    [Fact]
    public void Logging_started_on_layer_two_preserves_the_earlier_estimated_pace()
    {
        var tl = Timeline(TwoSquareLayers());
        int start = tl.Layer.IndexOf(2);
        double approach = tl.TimeS[start] - tl.TimeS[0];
        tl.CalibrateWithLog(new[]
        {
            Rec(1, PrintLogEventKind.LayerStart, 2, 0, 0, 2, 50),
            Rec(2, PrintLogEventKind.LayerEnd, 2, 0, 0, 2, 90),
        }, null);
        for (int k = 1; k < tl.Count; k++) Assert.True(tl.TimeS[k] >= tl.TimeS[k - 1]);
        Assert.Equal(approach, tl.TimeS[start] - tl.TimeS[0], 6);
        Assert.Equal(40, tl.TimeS[tl.Count - 1] - tl.TimeS[start], 6);
    }

    [Fact]
    public void Missing_log_clocks_leave_estimates_intact_and_cannot_create_a_log_timeline()
    {
        var records = PrintLogCsvReader.Parse(new[] { "x,y,z,layer", "0,0,0,1", "10,0,0,1" }).Records;
        var tl = Timeline(TwoSquareLayers());
        var estimate = tl.TimeS.ToArray();
        string note = tl.CalibrateWithLog(records, null);
        Assert.Equal(estimate, tl.TimeS);
        Assert.Contains("clock", note);
        Assert.Throws<FormatException>(() => InterlayerTimeline.FromLog(records));
    }

    [Fact]
    public void A_reset_job_clock_uses_valid_wall_timestamps()
    {
        var a = Rec(1, PrintLogEventKind.Interval, 1, 0, 0, 0, 10);
        var b = Rec(2, PrintLogEventKind.Interval, 1, 10, 0, 0, 20);
        b.JobElapsedSeconds = 0;
        var tl = InterlayerTimeline.FromLog(new[] { a, b });
        Assert.Equal(10, tl.TotalS);
        Assert.Contains("wall clock", tl.Note);
    }

    [Fact]
    public void Line_byte_ends_handle_cr_only_and_mixed_terminators()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("G1 X1\rG1 X2\r\nG1 X3\nG1 X4");
        Assert.Equal(new long[] { 6, 13, 19, 24 },
            GcodeText.LineByteEnds(new[] { "G1 X1", "G1 X2", "G1 X3", "G1 X4" }, bytes));
    }

    [Theory]
    [InlineData("G4 S30", 0)]
    [InlineData("N42 G04 P30000", 0)]
    [InlineData("G4 S30", 200)]
    public void Explicit_dwell_between_layers_is_included_in_the_clock(string dwell, double accel)
    {
        var lines = TwoSquareLayers();
        var options = new InterlayerOptions { AccelMmS2 = accel };
        double estimate = Timeline(lines, options).TotalS;
        lines.Insert(lines.IndexOf("; layer 2/2"), dwell);
        var tl = Timeline(lines, options);
        Assert.Equal(estimate + 30, tl.TotalS, 6);
        var result = InterlayerAnalysis.Compute(tl, 8, 0, 0);
        int seam = tl.Layer.IndexOf(2);
        Assert.InRange(result.SecondsBelow[seam], 30, 31);
    }

    [Fact]
    public void Startup_and_trailing_dwells_keep_source_lines_and_times_in_order()
    {
        var lines = TwoSquareLayers();
        double estimate = Timeline(lines).TotalS;
        lines.Insert(lines.FindIndex(s => s.StartsWith("G0 ")), "G4 S5");
        lines.Add("G4 P2000");
        var tl = Timeline(lines);
        Assert.Equal(estimate + 7, tl.TotalS, 6);
        for (int k = 1; k < tl.Count; k++)
        {
            Assert.True(tl.LineNumber[k] >= tl.LineNumber[k - 1]);
            Assert.True(tl.TimeS[k] >= tl.TimeS[k - 1]);
        }
    }

    [Fact]
    public void Reader_and_report_are_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Thread.CurrentThread.CurrentCulture = CultureInfo.CurrentCulture;
            var lines = new List<string> { PrintLogCsv.Header(), PrintLogCsv.Row(Rec(1, PrintLogEventKind.Interval, 1, 1.5, 2.5, 0, 3.25)) };
            var read = PrintLogCsvReader.Parse(lines);
            Assert.Equal(1.5, read.Records[0].Position.X);
            Assert.Equal(3.25, read.Records[0].JobElapsedSeconds);
            var tl = Timeline(TwoSquareLayers());
            string report = InterlayerAnalysis.Compute(tl, 8.0, 0, 3).Report();
            Assert.DoesNotContain("4,2", report);
            Assert.Contains("layer height     2 mm", report);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    // ---- layer shading (display only) --------------------------------------------

    [Fact]
    public void Shade_none_leaves_every_layer_at_full_colour()
    {
        for (int layer = 0; layer < 5; layer++)
            Assert.Equal(1.0, LayerShading.Factor(layer, 5, LayerShadeMode.None));
    }

    [Fact]
    public void Shade_alternate_tints_odd_zero_based_layers()
    {
        Assert.Equal(1.0, LayerShading.Factor(0, 10, LayerShadeMode.Alternate));
        Assert.Equal(LayerShading.AlternateTint, LayerShading.Factor(1, 10, LayerShadeMode.Alternate));
        Assert.Equal(1.0, LayerShading.Factor(2, 10, LayerShadeMode.Alternate));
        Assert.Equal(LayerShading.AlternateTint, LayerShading.Factor(9, 10, LayerShadeMode.Alternate));
        // the parity does not depend on how many layers there are
        Assert.Equal(LayerShading.AlternateTint, LayerShading.Factor(1, 0, LayerShadeMode.Alternate));
        Assert.Equal(LayerShading.AlternateTint, LayerShading.Factor(1, 1, LayerShadeMode.Alternate));
    }

    [Fact]
    public void Shade_height_ramp_runs_from_the_floor_at_the_bottom_to_full_at_the_top()
    {
        Assert.Equal(LayerShading.RampFloor, LayerShading.Factor(0, 5, LayerShadeMode.HeightRamp), 12);
        Assert.Equal(1.0, LayerShading.Factor(4, 5, LayerShadeMode.HeightRamp), 12);
        Assert.Equal(0.85, LayerShading.Factor(2, 5, LayerShadeMode.HeightRamp), 12);
        // monotone bottom to top
        double previous = -1;
        for (int layer = 0; layer < 5; layer++)
        {
            double f = LayerShading.Factor(layer, 5, LayerShadeMode.HeightRamp);
            Assert.True(f > previous);
            previous = f;
        }
        // out-of-range indices clamp into the ramp instead of over/under-shooting
        Assert.Equal(LayerShading.RampFloor, LayerShading.Factor(-3, 5, LayerShadeMode.HeightRamp), 12);
        Assert.Equal(1.0, LayerShading.Factor(9, 5, LayerShadeMode.HeightRamp), 12);
    }

    [Fact]
    public void Shade_of_a_single_layer_or_none_is_never_a_ramp()
    {
        Assert.Equal(1.0, LayerShading.Factor(0, 1, LayerShadeMode.HeightRamp));
        Assert.Equal(1.0, LayerShading.Factor(0, 0, LayerShadeMode.HeightRamp));
        Assert.Equal(1.0, LayerShading.Factor(0, -4, LayerShadeMode.HeightRamp));
    }

    [Fact]
    public void Apply_rounds_and_clamps_each_channel()
    {
        Assert.Equal(((byte)100, (byte)200, (byte)0), LayerShading.Apply(((byte)100, (byte)200, (byte)0), 1.0));
        Assert.Equal(((byte)50, (byte)100, (byte)0), LayerShading.Apply(((byte)100, (byte)200, (byte)0), 0.5));
        // 101 * 0.5 = 50.5 rounds away from zero, not to even
        Assert.Equal((byte)51, LayerShading.Apply(((byte)101, (byte)0, (byte)0), 0.5).R);
        // brightening clamps at the top of the byte range, darkening at the bottom
        Assert.Equal(((byte)255, (byte)255, (byte)255), LayerShading.Apply(((byte)200, (byte)128, (byte)255), 4.0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), LayerShading.Apply(((byte)200, (byte)128, (byte)255), -1.0));
        Assert.Equal(((byte)0, (byte)0, (byte)0), LayerShading.Apply(((byte)200, (byte)128, (byte)255), 0.0));
        // a non-finite factor leaves the colour alone rather than blanking it
        Assert.Equal(((byte)200, (byte)128, (byte)255), LayerShading.Apply(((byte)200, (byte)128, (byte)255), double.NaN));
        Assert.Equal(((byte)200, (byte)128, (byte)255), LayerShading.Apply(((byte)200, (byte)128, (byte)255), double.PositiveInfinity));
    }

    [Fact]
    public void FaceShade_lights_a_flat_top_fully_and_never_drops_below_the_floor()
    {
        Assert.Equal(1.0, LayerShading.FaceShade(1.0), 12);
        Assert.Equal(0.75, LayerShading.FaceShade(0.0), 12);
        Assert.Equal(0.75, LayerShading.FaceShade(-1.0), 12);      // downward faces do not darken further
        Assert.Equal(0.875, LayerShading.FaceShade(0.5), 12);
        Assert.Equal(0.75, LayerShading.FaceShade(double.NaN), 12);
        for (double nz = -1.0; nz <= 1.0; nz += 0.05)
            Assert.InRange(LayerShading.FaceShade(nz), 0.75, 1.0);
    }

    [Fact]
    public void Layer_shade_never_touches_the_scale_colour_itself()
    {
        // the shade says WHICH layer; PlasmaColorScale still says HOW MANY seconds
        var scale = PlasmaColorScale.ForSeconds(30, 120);
        var shaded = LayerShading.Apply(scale, LayerShading.Factor(1, 4, LayerShadeMode.Alternate));
        Assert.NotEqual(scale, shaded);
        Assert.Equal(scale, PlasmaColorScale.ForSeconds(30, 120));
    }

    // ---- the legend wire the component emits --------------------------------------

    /// <summary>The spec InterlayerTimeComponent builds for its Plasma scale.</summary>
    static LegendSpec InterlayerLegend(double legendMaxS, double minT, double maxT)
    {
        var marks = new List<(double Value, string Label)>();
        if (minT > 0) marks.Add((minT, "fast"));
        if (maxT > 0) marks.Add((maxT, "slow"));
        return new LegendSpec(LegendType.Continuous, title: "interlayer time - estimated from feeds",
                              unit: "s", min: 0, max: legendMaxS, scheme: ColorSchemes.Plasma,
                              marks: marks,
                              unavailable: (Rgb.Of(PlasmaColorScale.Unavailable), "no layer below"),
                              decimals: 0);
    }

    [Fact]
    public void Interlayer_legend_wire_carries_the_plasma_scale_its_maximum_and_both_thresholds()
    {
        var wire = InterlayerLegend(180, 20, 120).Serialize();
        Assert.StartsWith(LegendSpec.Prefix + LegendSpec.Version + ":", wire);
        var spec = LegendSpec.Parse(wire);

        Assert.Equal(LegendType.Continuous, spec.Type);
        Assert.Equal(ColorSchemes.Plasma, spec.Scheme);
        Assert.Equal("s", spec.Unit);
        Assert.Equal(0, spec.Min);
        Assert.Equal(180, spec.Max);
        Assert.Equal(0, spec.Decimals);
        Assert.Contains("interlayer time", spec.Title);
        Assert.Equal(new[] { (20.0, "fast"), (120.0, "slow") }, spec.Marks.ToArray());
        Assert.NotNull(spec.Unavailable);
        Assert.Equal(Rgb.Of(PlasmaColorScale.Unavailable), spec.Unavailable!.Value.Color);
        Assert.Equal("no layer below", spec.Unavailable!.Value.Label);
    }

    [Fact]
    public void Interlayer_legend_colours_are_the_colours_the_viewport_paints()
    {
        var spec = LegendSpec.Parse(InterlayerLegend(120, 0, 0).Serialize());
        foreach (double seconds in new[] { 0.0, 1.0, 30.0, 60.0, 119.0, 120.0, 400.0 })
            Assert.Equal(Rgb.Of(PlasmaColorScale.ForSeconds(seconds, 120)), spec.ColorFor(seconds));
        // no layer below is the one colour that is never a Plasma hue
        Assert.Equal(Rgb.Of(PlasmaColorScale.Unavailable), spec.ColorFor(double.NaN));
    }

    [Fact]
    public void Interlayer_legend_omits_a_threshold_that_is_switched_off()
    {
        Assert.Empty(LegendSpec.Parse(InterlayerLegend(120, 0, 0).Serialize()).Marks);
        Assert.Equal(new[] { (90.0, "slow") },
                     LegendSpec.Parse(InterlayerLegend(120, 0, 90).Serialize()).Marks.ToArray());
        Assert.Equal(new[] { (5.0, "fast") },
                     LegendSpec.Parse(InterlayerLegend(120, 5, 0).Serialize()).Marks.ToArray());
    }
}
