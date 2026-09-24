using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The print-log recorder driving the shared continuity policy, and the schema-2
/// CSV columns that carry its verdicts. The acceptance cases from the plan's section 2 -
/// ordinary curves, missing coordinates, homing, same-name repeat prints, pause/jog/resume,
/// reconnect, cancelled jobs and changed G-code - are covered here at the recorder, where
/// the poll loop actually calls it.</summary>
public class TelemetryRecorderSchemaTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 9, 0, 0, DateTimeKind.Utc);
    static long _acq = 1;

    static ObjectModelSnapshot Snap(MachineStatus status, double seconds,
                                    double x = 0, double y = 0, double z = 0,
                                    int? layer = 1, string? file = "0:/gcodes/print.gcode",
                                    double? rawExtrusion = null, long filePosition = 0,
                                    bool positionValid = true, long generation = 1,
                                    double? requested = 43)
    {
        var axes = new List<AxisState>();
        if (positionValid)
        {
            axes.Add(new AxisState { Letter = "X", MachinePosition = x, UserPosition = x, Homed = true });
            axes.Add(new AxisState { Letter = "Y", MachinePosition = y, UserPosition = y, Homed = true });
            axes.Add(new AxisState { Letter = "Z", MachinePosition = z, UserPosition = z, Homed = true });
        }
        var snap = new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString(),
            Axes = axes,
            AllHomed = positionValid,
            RawExtrusion = rawExtrusion,
            RequestedSpeed = requested,
            Acquisition = new AcquisitionStamp
            {
                AcquisitionId = _acq++,
                RequestStartedUtc = T0.AddSeconds(seconds - 0.05),
                ResponseReceivedUtc = T0.AddSeconds(seconds),
                ConnectionGeneration = generation,
                Composite = true,
            },
        };
        snap.TakenUtc = T0.AddSeconds(seconds);
        if (!positionValid)
        {
            snap.PositionValidity = PositionValidity.MissingAxis;
            snap.PositionInvalidReason = "axis 'Y' is not reported";
        }
        bool printing = status == MachineStatus.Processing || status == MachineStatus.Simulating
                     || status == MachineStatus.Paused || status == MachineStatus.Pausing
                     || status == MachineStatus.Resuming;
        if (printing)
            snap.Job = new JobState
            {
                FileName = file,
                Layer = layer,
                TimePrinting = seconds,
                FilePosition = filePosition,
                FileSize = 1000000,
                FractionPrinted = filePosition / 1000000.0,
            };
        return snap;
    }

    static PrintLogRecorder Recorder()
    {
        var r = new PrintLogRecorder();
        r.Configure(new PrintLogOptions(true, 1, 50000, 0));
        return r;
    }

    // ---- ordinary curve ------------------------------------------------------------

    [Fact]
    public void OrdinaryCurve_IsOneSegmentOfPrintingRows()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        for (int i = 1; i <= 6; i++)
            recorder.Observe(Snap(MachineStatus.Processing, i * 0.4, i * 10, 0, 5,
                                  rawExtrusion: 1 + i * 0.1, filePosition: 1000 + i * 100));

        var rows = recorder.Snapshot().Where(r => r.Kind == PrintLogEventKind.Interval).ToArray();
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(r => r.SegmentId).Distinct());
        Assert.All(rows, r => Assert.Equal(TrailQuality.Valid, r.Quality));
        Assert.All(rows, r => Assert.Equal(TrailClass.Printing, r.Classification));
        Assert.All(rows, r => Assert.True(r.PositionValid));
        Assert.All(rows, r => Assert.True(r.AcquisitionId > 0));
        Assert.All(rows, r => Assert.True(r.Composite));
    }

    // ---- missing coordinates -------------------------------------------------------

    [Fact]
    public void MissingCoordinates_AreRecordedAsUnusableRatherThanAsTheOrigin()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 100, 100, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 0, 0, 0, rawExtrusion: 1.1,
                              filePosition: 1100, positionValid: false));

        var bad = recorder.Snapshot().Last();
        Assert.False(bad.PositionValid);
        Assert.Equal(TrailQuality.Invalid, bad.Quality);
        Assert.Equal("axis 'Y' is not reported", bad.PositionInvalidReason);

        // and the CSV writes no coordinates at all for it
        string row = PrintLogCsv.Row(bad);
        var cells = PrintLogCsvReaderProbe.Split(row);
        Assert.Equal("", cells[10]);
        Assert.Equal("", cells[11]);
        Assert.Equal("", cells[12]);
    }

    [Fact]
    public void AnUnusableObservation_BreaksTheSegmentOnBothSides()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Processing, 0.8, 0, 0, 0, rawExtrusion: 1.2,
                              filePosition: 1200, positionValid: false));
        recorder.Observe(Snap(MachineStatus.Processing, 1.2, 30, 0, 5, rawExtrusion: 1.3, filePosition: 1300));

        var rows = recorder.Snapshot();
        long before = rows.First(r => r.Seq == rows.First(x => x.PositionValid && x.Kind == PrintLogEventKind.Interval).Seq).SegmentId;
        var invalid = rows.First(r => !r.PositionValid);
        var after = rows.Last();
        Assert.NotEqual(before, invalid.SegmentId);
        Assert.NotEqual(invalid.SegmentId, after.SegmentId);
    }

    // ---- homing --------------------------------------------------------------------

    [Fact]
    public void HomingSplitsTheTrail()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        var homing = Snap(MachineStatus.Busy, 0.4, 0, 0, 0, layer: null, file: null);
        homing.AllHomed = false;
        recorder.Observe(homing);
        recorder.Observe(Snap(MachineStatus.Processing, 0.8, 20, 0, 5, rawExtrusion: 1.2, filePosition: 1200));

        var rows = recorder.Snapshot();
        // Busy ends the run; the restart is a new run and therefore a new segment.
        Assert.True(rows.Select(r => r.SegmentId).Distinct().Count() >= 2);
    }

    // ---- same-name repeat prints ---------------------------------------------------

    [Fact]
    public void TheSameFilePrintedTwice_GetsTwoRunIdsAndTwoSegments()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Idle, 0.8));
        recorder.Observe(Snap(MachineStatus.Processing, 1.2, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 1.6, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));

        var rows = recorder.Snapshot();
        var runIds = rows.Where(r => r.RunId.Length > 0).Select(r => r.RunId).Distinct().ToArray();
        Assert.Equal(2, runIds.Length);
        Assert.All(runIds, id => Assert.Contains("print.gcode", id, StringComparison.Ordinal));
        // and no segment carries rows from both runs
        foreach (var group in rows.Where(r => r.RunId.Length > 0).GroupBy(r => r.SegmentId))
            Assert.Single(group.Select(r => r.RunId).Distinct());
    }

    [Fact]
    public void ChangedGcodeMidRun_StartsANewRunAndSegment()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, file: "0:/gcodes/a.gcode",
                              rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, file: "0:/gcodes/b.gcode",
                              rawExtrusion: 1.1, filePosition: 1100));

        var rows = recorder.Snapshot();
        Assert.Contains(rows, r => r.JobFile == "0:/gcodes/a.gcode");
        Assert.Contains(rows, r => r.JobFile == "0:/gcodes/b.gcode");
        var a = rows.Last(r => r.JobFile == "0:/gcodes/a.gcode");
        var b = rows.Last(r => r.JobFile == "0:/gcodes/b.gcode");
        Assert.NotEqual(a.SegmentId, b.SegmentId);
        Assert.NotEqual(a.RunId, b.RunId);
    }

    // ---- pause / jog / resume ------------------------------------------------------

    [Fact]
    public void PauseAndResume_AreClassifiedAndNotDrawnAsPrinting()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Paused, 0.4, 10, 0, 5, rawExtrusion: 1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Resuming, 0.8, 10, 0, 5, rawExtrusion: 1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Processing, 1.2, 20, 0, 5, rawExtrusion: 1.2, filePosition: 1200));

        var rows = recorder.Snapshot();
        Assert.Contains(rows, r => r.Classification == TrailClass.Paused);
        Assert.Contains(rows, r => r.Classification == TrailClass.Printing);
    }

    // ---- reconnect -----------------------------------------------------------------

    [Fact]
    public void ANewConnectionGeneration_SplitsTheSegment()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Processing, 0.8, 20, 0, 5, rawExtrusion: 1.2,
                              filePosition: 1200, generation: 2));

        var rows = recorder.Snapshot();
        var last = rows.Last();
        Assert.Equal(TrailQuality.Discontinuity, last.Quality);
        Assert.Contains("new connection", last.QualityReason!, StringComparison.Ordinal);
    }

    // ---- cancelled job -------------------------------------------------------------

    [Fact]
    public void ACancelledRun_RecordsItsTerminalState()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Halted, 0.8));

        var end = recorder.Snapshot().Last(r => r.Kind == PrintLogEventKind.JobEnd);
        Assert.Equal("Halted", end.TerminalStatus);
    }

    // ---- the CSV schema ------------------------------------------------------------

    [Fact]
    public void Schema2Header_KeepsTheSchema1ColumnsInPlace()
    {
        var cells = PrintLogCsvReaderProbe.Split(PrintLogCsv.Header());
        string[] schema1 =
        {
            "seq","kind","job_run","job_file","layer","layer_source","utc_iso","local_iso",
            "job_elapsed_s","dt_s","x","y","z","speed_observed_mms","speed_requested_mms",
            "speed_top_mms","speed_factor_pct","raw_extrusion_mm","fraction_printed",
            "file_position","status","boundary_uncertainty_s","finish_reason","terminal_status",
        };
        for (int i = 0; i < schema1.Length; i++) Assert.Equal(schema1[i], cells[i]);
        Assert.Equal("log_schema", cells[schema1.Length]);
        Assert.Contains("run_id", cells);
        Assert.Contains("segment_id", cells);
        Assert.Contains("quality", cells);
        Assert.Contains("classification", cells);
        Assert.Contains("path_distance_mm", cells);
        Assert.Contains("position_valid", cells);
    }

    [Fact]
    public void Schema2Rows_RoundTripThroughTheReader()
    {
        var recorder = Recorder();
        recorder.SetIntendedPath(null);
        recorder.Observe(Snap(MachineStatus.Processing, 0, 0, 0, 5, rawExtrusion: 1, filePosition: 1000));
        recorder.Observe(Snap(MachineStatus.Processing, 0.4, 10, 0, 5, rawExtrusion: 1.1, filePosition: 1100));
        recorder.Observe(Snap(MachineStatus.Processing, 0.8, 0, 0, 0, rawExtrusion: 1.2,
                              filePosition: 1200, positionValid: false));

        var original = recorder.Snapshot();
        var lines = new List<string> { PrintLogCsv.Header() };
        foreach (var r in original) lines.Add(PrintLogCsv.Row(r));

        var parsed = PrintLogCsvReader.Parse(lines);
        Assert.Equal(0, parsed.SkippedRows);
        Assert.Equal(original.Count, parsed.Records.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(PrintLogCsv.SchemaVersion, parsed.Records[i].Schema);
            Assert.Equal(original[i].SegmentId, parsed.Records[i].SegmentId);
            Assert.Equal(original[i].RunId, parsed.Records[i].RunId);
            Assert.Equal(original[i].Quality, parsed.Records[i].Quality);
            Assert.Equal(original[i].Classification, parsed.Records[i].Classification);
            Assert.Equal(original[i].PositionValid, parsed.Records[i].PositionValid);
            Assert.Equal(original[i].AcquisitionId, parsed.Records[i].AcquisitionId);
            Assert.Equal(original[i].Composite, parsed.Records[i].Composite);
        }
        // the unusable observation survives the round trip instead of being skipped
        var invalid = parsed.Records.Single(r => !r.PositionValid);
        Assert.Equal("axis 'Y' is not reported", invalid.PositionInvalidReason);
    }

    [Fact]
    public void ASchema1FileStillImports()
    {
        // The dome fixture is a real schema-1 log: no continuity columns at all.
        var log = TelemetryReplayFixtures.LoadLog("dome-20260909", "print.csv");
        Assert.Equal(248, log.Count);
        Assert.All(log, r => Assert.Equal(1, r.Schema));
        Assert.All(log, r => Assert.True(r.PositionValid));   // schema 1 declares nothing
        Assert.All(log, r => Assert.Equal("", r.RunId));
        Assert.All(log, r => Assert.Equal(0, r.SegmentId));
        Assert.All(log, r => Assert.Null(r.PathDistanceMm));
    }

    [Fact]
    public void ARowWithNoCoordinatesAndNoDeclaration_IsStillMalformed()
    {
        var lines = new List<string>
        {
            "seq,kind,x,y,z,status",
            "1,Interval,,,,Processing",
        };
        var parsed = PrintLogCsvReader.Parse(lines);
        Assert.Empty(parsed.Records);
        Assert.Equal(1, parsed.SkippedRows);
    }
}

/// <summary>Test-only access to the reader's RFC-4180 cell split.</summary>
static class PrintLogCsvReaderProbe
{
    public static List<string> Split(string line)
    {
        var method = typeof(PrintLogCsvReader).GetMethod("SplitRow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, new object[] { line })!;
    }
}
