using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Lugworm.Core.Doe;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Print-log recorder, CSV formatting and streaming writer. The recorder is
/// driven by synthetic snapshot sequences - the same shape the poll loop produces.</summary>
public class DuetPrintLogTests
{
    static readonly DateTime T0 = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);

    static ObjectModelSnapshot Snap(MachineStatus status, double seconds,
                                    double x = 0, double y = 0, double z = 0,
                                    int? layer = null, string? file = "0:/gcodes/vase.gcode",
                                    double? rawExtrusion = null)
    {
        var snap = new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString(),
            TakenUtc = T0.AddSeconds(seconds),
            RawExtrusion = rawExtrusion,
            Axes = new[]
            {
                new AxisState { Letter = "X", MachinePosition = x, Homed = true },
                new AxisState { Letter = "Y", MachinePosition = y, Homed = true },
                new AxisState { Letter = "Z", MachinePosition = z, Homed = true },
            },
            AllHomed = true,
        };
        bool printing = status == MachineStatus.Processing || status == MachineStatus.Simulating
                     || status == MachineStatus.Paused || status == MachineStatus.Pausing
                     || status == MachineStatus.Resuming;
        if (printing)
            snap.Job = new JobState { FileName = file, Layer = layer, TimePrinting = seconds };
        return snap;
    }

    static PrintLogRecorder Recorder(int everyN = 0, int maxRecords = 50000, double layerHint = 0)
    {
        var recorder = new PrintLogRecorder();
        recorder.Configure(new PrintLogOptions(true, everyN, maxRecords, layerHint));
        return recorder;
    }

    // ---- run lifecycle ----------------------------------------------------

    [Fact]
    public void Disabled_RecorderEmitsNothing()
    {
        var recorder = new PrintLogRecorder();
        recorder.Configure(new PrintLogOptions(false));

        Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1)));
        Assert.Equal(0, recorder.RecordCount);
    }

    [Fact]
    public void RunStart_EmitsJobStartThenLayerStart()
    {
        var recorder = Recorder();

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        Assert.Equal(new[] { PrintLogEventKind.JobStart, PrintLogEventKind.LayerStart },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(1, emitted[1].Layer);
        Assert.Equal(PrintLogLayerSource.Firmware, emitted[1].LayerSource);
        Assert.Equal(1, recorder.JobRun);
    }

    [Fact]
    public void FirstLayerStart_IsDeferredUntilTheLayerIsKnown()
    {
        var recorder = Recorder();

        var start = recorder.Observe(Snap(MachineStatus.Processing, 0, layer: null));
        Assert.Equal(new[] { PrintLogEventKind.JobStart }, start.Select(r => r.Kind).ToArray());
        Assert.Null(start[0].Layer);

        var resolved = recorder.Observe(Snap(MachineStatus.Processing, 3, layer: 1));

        var layerStart = Assert.Single(resolved);
        Assert.Equal(PrintLogEventKind.LayerStart, layerStart.Kind);
        Assert.Equal(1, layerStart.Layer);
        // uncertainty covers the deferral, not just one poll
        Assert.Equal(3.0, layerStart.BoundaryUncertaintySeconds!.Value, 6);
    }

    [Fact]
    public void LayerIncrease_EndsPreviousLayerAtTheLastObservedSample()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, x: 0, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 10, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 2, x: 20, z: 0.4, layer: 2));

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.LayerStart },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(1, emitted[0].Layer);
        // LayerEnd carries the PREVIOUS snapshot (last point actually seen on layer 1)
        Assert.Equal(10.0, emitted[0].Position.X, 6);
        Assert.Equal(T0.AddSeconds(1), emitted[0].TakenUtc);
        Assert.Equal(2, emitted[1].Layer);
        Assert.Equal(20.0, emitted[1].Position.X, 6);
        Assert.Equal(1.0, emitted[0].BoundaryUncertaintySeconds!.Value, 6);
    }

    [Fact]
    public void LayerJumpGreaterThanOne_EmitsOneBoundaryPairAndInventsNoLayers()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 1, layer: 5));

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.LayerStart },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(1, emitted[0].Layer);
        Assert.Equal(5, emitted[1].Layer);
        Assert.Equal(new[] { 1 }, recorder.LayerSpans().Where(s => s.Complete).Select(s => s.Layer).ToArray());
    }

    [Fact]
    public void RunEnd_ClosesFinalLayerThenJob()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Idle, 2));

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.JobEnd },
                     emitted.Select(r => r.Kind).ToArray());
        var span = Assert.Single(recorder.LayerSpans());
        Assert.True(span.Complete);
        Assert.Equal(1.0, span.DurationSeconds!.Value, 6);
    }

    [Fact]
    public void CancelledRun_MarksTheFinalSpanIncomplete()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Cancelling, 1));

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.JobEnd },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.False(Assert.Single(recorder.LayerSpans()).Complete);
    }

    // ---- Finish -----------------------------------------------------------

    [Fact]
    public void Finish_ClosesAnOpenRunAndTagsTheReason()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1));

        var emitted = recorder.Finish(PrintLogFinishReason.ConnectionLost);

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.JobEnd },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.All(emitted, r => Assert.Equal(PrintLogFinishReason.ConnectionLost, r.FinishReason));
        // the run is closed from the last snapshot actually observed
        Assert.All(emitted, r => Assert.Equal(T0.AddSeconds(1), r.TakenUtc));
        Assert.False(Assert.Single(recorder.LayerSpans()).Complete);
    }

    [Fact]
    public void Finish_WithNoOpenRun_IsANoOp()
    {
        var recorder = Recorder();

        Assert.Empty(recorder.Finish(PrintLogFinishReason.SessionStopped));
        Assert.Empty(recorder.Finish(PrintLogFinishReason.LoggingDisabled));
        Assert.Equal(0, recorder.RecordCount);
    }

    [Fact]
    public void OrdinaryRecords_CarryNoFinishReason()
    {
        var recorder = Recorder(everyN: 1);
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1));

        Assert.All(recorder.Snapshot(), r => Assert.Null(r.FinishReason));
    }

    // ---- run identity -----------------------------------------------------

    [Fact]
    public void RunSurvivesAMissingJobFragmentAndALaterNullFileName()
    {
        var recorder = Recorder();
        // first printing snapshot carries no job fragment at all
        var first = Snap(MachineStatus.Processing, 0, layer: 1);
        first.Job = null;
        recorder.Observe(first);

        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1, file: "0:/gcodes/vase.gcode"));
        recorder.Observe(Snap(MachineStatus.Processing, 2, x: 10, layer: 1, file: null));

        Assert.Equal(1, recorder.JobRun);
        Assert.DoesNotContain(recorder.Snapshot(), r => r.Kind == PrintLogEventKind.JobEnd);
    }

    [Fact]
    public void TwoDifferingNonEmptyFileNames_SplitTheRunIntoFourRecords()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1, file: "0:/gcodes/a.gcode"));

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 1, layer: 1, file: "0:/gcodes/b.gcode"));

        Assert.Equal(new[]
        {
            PrintLogEventKind.LayerEnd, PrintLogEventKind.JobEnd,
            PrintLogEventKind.JobStart, PrintLogEventKind.LayerStart,
        }, emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(new[] { 1, 1, 2, 2 }, emitted.Select(r => r.JobRun).ToArray());
        Assert.Equal("0:/gcodes/a.gcode", emitted[1].JobFile);
        Assert.Equal("0:/gcodes/b.gcode", emitted[2].JobFile);
        Assert.Equal(2, recorder.JobRun);
    }

    // ---- interval sampling ------------------------------------------------

    [Fact]
    public void IntervalRecords_AppearEveryNPollsAndResetAtBoundaries()
    {
        var recorder = Recorder(everyN: 2);
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1)));
        var interval = Assert.Single(recorder.Observe(Snap(MachineStatus.Processing, 2, x: 10, layer: 1)));
        Assert.Equal(PrintLogEventKind.Interval, interval.Kind);
        Assert.Equal(1, interval.Layer);

        // boundary resets the counter: the next poll after it must not emit
        recorder.Observe(Snap(MachineStatus.Processing, 3, x: 15, layer: 2));
        Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, 4, x: 20, layer: 2)));
    }

    [Fact]
    public void SampleEveryNPollsZero_EmitsBoundariesOnly()
    {
        var recorder = Recorder(everyN: 0);
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        for (int i = 1; i <= 6; i++)
            Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, i, x: i * 5, layer: 1)));

        Assert.DoesNotContain(recorder.Snapshot(), r => r.Kind == PrintLogEventKind.Interval);
    }

    // ---- derived speed ----------------------------------------------------

    [Fact]
    public void DerivedSpeed_IsDistanceOverElapsedTime()
    {
        var recorder = Recorder(everyN: 1);
        recorder.Observe(Snap(MachineStatus.Processing, 0, x: 0, layer: 1));

        var record = Assert.Single(recorder.Observe(Snap(MachineStatus.Processing, 2, x: 60, layer: 1)));

        Assert.Equal(30.0, record.SpeedObserved!.Value, 6);   // 60 mm over 2 s
        Assert.Equal(2.0, record.DeltaSeconds!.Value, 6);
    }

    [Fact]
    public void DerivedSpeed_IsNullAcrossALongGap()
    {
        var recorder = Recorder(everyN: 1);
        recorder.Observe(Snap(MachineStatus.Processing, 0, x: 0, layer: 1));

        // a reconnect hole must not be reported as a very slow move
        var record = Assert.Single(recorder.Observe(Snap(MachineStatus.Processing, 120, x: 60, layer: 1)));

        Assert.Null(record.SpeedObserved);
    }

    [Fact]
    public void DerivedSpeed_IsNullWhenTimeDidNotAdvance()
    {
        var recorder = Recorder(everyN: 1);
        recorder.Observe(Snap(MachineStatus.Processing, 5, x: 0, layer: 1));

        var record = Assert.Single(recorder.Observe(Snap(MachineStatus.Processing, 5, x: 60, layer: 1)));

        Assert.Null(record.SpeedObserved);
        Assert.Null(record.DeltaSeconds);
    }

    // ---- Z-step fallback --------------------------------------------------

    [Fact]
    public void ZStepFallback_OpensTheFirstLayerAtRunStart()
    {
        // The first physical layer never produces a rise, so it can only be opened at
        // run start. Without that the whole first layer is lost and every later layer
        // is numbered one low.
        var recorder = Recorder(layerHint: 0.4);

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: null, rawExtrusion: 1));

        Assert.Equal(new[] { PrintLogEventKind.JobStart, PrintLogEventKind.LayerStart },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(1, emitted[1].Layer);
        Assert.Equal(PrintLogLayerSource.ZStep, emitted[1].LayerSource);
    }

    [Fact]
    public void ZStepFallback_FirstLayerCollectsIntervalSamples()
    {
        var recorder = Recorder(everyN: 1, layerHint: 0.4);
        recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: null, rawExtrusion: 1));

        var interval = Assert.Single(
            recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, z: 0.4, layer: null, rawExtrusion: 2)));

        Assert.Equal(PrintLogEventKind.Interval, interval.Kind);
        Assert.Equal(1, interval.Layer);
    }

    [Fact]
    public void ZStepFallback_FirstRiseOpensLayerTwo()
    {
        var recorder = Recorder(layerHint: 0.4);
        recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: null, rawExtrusion: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, z: 0.4, layer: null, rawExtrusion: 2));

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 2, z: 0.8, layer: null, rawExtrusion: 3));

        Assert.Equal(new[] { PrintLogEventKind.LayerEnd, PrintLogEventKind.LayerStart },
                     emitted.Select(r => r.Kind).ToArray());
        Assert.Equal(1, emitted[0].Layer);
        Assert.Equal(2, emitted[1].Layer);
        Assert.Equal(PrintLogLayerSource.ZStep, emitted[1].LayerSource);
    }

    [Fact]
    public void ZStepFallback_IsInertWithoutAHint()
    {
        var recorder = Recorder(layerHint: 0);

        // no hint => no layer is ever opened, so JobStart stands alone
        var start = recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: null, rawExtrusion: 1));
        Assert.Equal(new[] { PrintLogEventKind.JobStart }, start.Select(r => r.Kind).ToArray());

        Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, 1, z: 0.8, layer: null, rawExtrusion: 2)));
        Assert.DoesNotContain(recorder.Snapshot(), r => r.LayerSource == PrintLogLayerSource.ZStep);
    }

    [Fact]
    public void ZStepFallback_IgnoresARiseWithoutExtrusion()
    {
        var recorder = Recorder(layerHint: 0.4);
        recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: null, rawExtrusion: 1));

        // z-hop / travel: height rose but nothing was extruded
        Assert.Empty(recorder.Observe(Snap(MachineStatus.Processing, 1, z: 2.0, layer: null, rawExtrusion: 1)));
    }

    [Fact]
    public void FirmwareLayer_TakesPrecedenceOverTheHint()
    {
        var recorder = Recorder(layerHint: 0.4);
        recorder.Observe(Snap(MachineStatus.Processing, 0, z: 0.4, layer: 7, rawExtrusion: 1));

        Assert.All(recorder.Snapshot(),
                   r => Assert.NotEqual(PrintLogLayerSource.ZStep, r.LayerSource));
        Assert.Equal(7, recorder.Snapshot().Last().Layer);
    }

    // ---- ring buffer ------------------------------------------------------

    [Fact]
    public void RingBuffer_DropsOldestAndFlagsTruncationWithoutLosingTheNewest()
    {
        var recorder = Recorder(everyN: 1, maxRecords: 4);
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));
        for (int i = 1; i <= 8; i++)
            recorder.Observe(Snap(MachineStatus.Processing, i, x: i * 5, layer: 1));

        var records = recorder.Snapshot();
        Assert.Equal(4, records.Count);
        Assert.True(recorder.Truncated);
        // sequence numbers keep counting, so the survivors are demonstrably the newest
        Assert.Equal(records.Select(r => r.Seq).OrderBy(v => v).ToArray(), records.Select(r => r.Seq).ToArray());
        Assert.Equal(recorder.Snapshot().Max(r => r.Seq), records.Last().Seq);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var recorder = Recorder(everyN: 1);
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 5, layer: 1));

        recorder.Reset();

        Assert.Equal(0, recorder.RecordCount);
        Assert.Equal(0, recorder.JobRun);
        Assert.Empty(recorder.LayerSpans());
        Assert.False(recorder.Truncated);
    }

    // ---- terminal state --------------------------------------------------

    [Fact]
    public void CancelledRun_RecordsTheTerminalMachineState()
    {
        // Position and time on the run-end rows come from the last PRINTING poll, so
        // without terminal_status a cancel is byte-identical to a clean finish.
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Cancelling, 1));

        Assert.All(emitted, r => Assert.Equal("Cancelling", r.TerminalStatus));
        Assert.All(emitted, r => Assert.Equal("Processing", r.Status));   // the sampled poll
        Assert.All(emitted, r => Assert.Null(r.FinishReason));
    }

    [Fact]
    public void StatusColumns_UseParsedNamesOnALegacyBoard()
    {
        // The legacy API reports protocol letters ("P", "I"). Writing those into the
        // CSV would make terminal_status board-specific and break any filter written
        // against it - the Potterbot is exactly such a board.
        var recorder = Recorder();
        var printing = Snap(MachineStatus.Processing, 0, layer: 1);
        printing.RawStatus = "P";
        recorder.Observe(printing);

        var idle = Snap(MachineStatus.Idle, 1);
        idle.RawStatus = "I";
        var emitted = recorder.Observe(idle);

        Assert.All(emitted, r => Assert.Equal("Idle", r.TerminalStatus));
        Assert.All(emitted, r => Assert.Equal("Processing", r.Status));
        Assert.DoesNotContain(recorder.Snapshot(), r => r.Status == "P" || r.TerminalStatus == "I");
    }

    [Fact]
    public void CompletedRun_IsDistinguishableFromACancelledOne()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1));

        var emitted = recorder.Observe(Snap(MachineStatus.Idle, 1));

        Assert.All(emitted, r => Assert.Equal("Idle", r.TerminalStatus));
        Assert.True(Assert.Single(recorder.LayerSpans()).Complete);
    }

    [Fact]
    public void FileNameSplit_ReportsNoTerminalStatusBecauseTheMachineKeepsPrinting()
    {
        var recorder = Recorder();
        recorder.Observe(Snap(MachineStatus.Processing, 0, layer: 1, file: "0:/gcodes/a.gcode"));

        var emitted = recorder.Observe(Snap(MachineStatus.Processing, 1, layer: 1, file: "0:/gcodes/b.gcode"));

        Assert.All(emitted, r => Assert.Null(r.TerminalStatus));
    }

    [Fact]
    public void Finish_ReportsTheRealGapNotAnExactBoundary()
    {
        var recorder = new PrintLogRecorder();
        recorder.Configure(new PrintLogOptions(true));
        // the observation is stamped well in the past, as after a 60 s blackout
        var stale = Snap(MachineStatus.Processing, 0, layer: 1);
        stale.TakenUtc = DateTime.UtcNow.AddSeconds(-90);
        recorder.Observe(stale);

        var emitted = recorder.Finish(PrintLogFinishReason.ConnectionLost);

        Assert.All(emitted, r => Assert.NotNull(r.BoundaryUncertaintySeconds));
        Assert.All(emitted, r => Assert.True(r.BoundaryUncertaintySeconds!.Value > 60,
            "a finish after a blackout must not claim an exact boundary"));
    }

    // ---- CSV --------------------------------------------------------------

    [Fact]
    public void Csv_RoundTripsThroughTheProjectCsvReader()
    {
        var recorder = Recorder(everyN: 1, layerHint: 0);
        recorder.Observe(Snap(MachineStatus.Processing, 0, x: 1.5, y: 2.5, z: 0.4, layer: 1));
        recorder.Observe(Snap(MachineStatus.Processing, 1, x: 11.5, y: 2.5, z: 0.4, layer: 1));
        recorder.Finish(PrintLogFinishReason.SessionStopped);

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                             "printlog_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using (var writer = new PrintLogCsvWriter(path))
            {
                writer.Append(recorder.Snapshot());
                writer.Flush();
                Assert.False(writer.Disabled);
                Assert.Equal(recorder.RecordCount, writer.RowsWritten);
            }

            var table = TableIo.ReadCsv(path);
            Assert.Equal(recorder.RecordCount, table.Rows.Length);
            Assert.Equal(PrintLogCsv.Header().Split(','), table.Headers);

            int kind = Array.IndexOf(table.Headers, "kind");
            int layer = Array.IndexOf(table.Headers, "layer");
            int x = Array.IndexOf(table.Headers, "x");
            int finish = Array.IndexOf(table.Headers, "finish_reason");
            Assert.Equal("JobStart", table.Rows[0][kind]);
            Assert.Equal("1", table.Rows[1][layer]);
            Assert.Equal(1.5, TableIo.ParseNum(table.Rows[1][x])!.Value, 6);
            Assert.Equal("", table.Rows[0][finish]);
            Assert.Equal("SessionStopped", table.Rows[table.Rows.Length - 1][finish]);
            int terminal = Array.IndexOf(table.Headers, "terminal_status");
            Assert.True(terminal >= 0, "terminal_status column must be present");
            Assert.Equal("", table.Rows[0][terminal]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Csv_UsesInvariantNumbersRegardlessOfTheAmbientCulture()
    {
        var record = new PrintLogRecord
        {
            Seq = 1,
            Kind = PrintLogEventKind.Interval,
            Position = new Core.Geometry.Vec3(1.5, -2.25, 0.125),
            SpeedObserved = 30.5,
            Status = "Processing",
        };

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");   // decimal comma
            string row = PrintLogCsv.Row(record);

            Assert.Contains("1.5,-2.25,0.125", row);
            Assert.Contains("30.5", row);
            Assert.DoesNotContain("1,5", row);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Csv_QuotesAFileNameContainingASeparator()
    {
        var record = new PrintLogRecord
        {
            Seq = 1,
            Kind = PrintLogEventKind.JobStart,
            JobFile = "0:/gcodes/vase,v2.gcode",
            Status = "Processing",
        };

        Assert.Contains("\"0:/gcodes/vase,v2.gcode\"", PrintLogCsv.Row(record));
    }

    // ---- writer -----------------------------------------------------------

    [Fact]
    public void Writer_WritesTheHeaderOnceAndAppendsToAnExistingFile()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                             "printlog_" + Guid.NewGuid().ToString("N") + ".csv");
        var record = new PrintLogRecord { Seq = 1, Kind = PrintLogEventKind.Interval, Status = "Processing" };
        try
        {
            using (var first = new PrintLogCsvWriter(path)) first.Append(new[] { record });
            using (var second = new PrintLogCsvWriter(path)) second.Append(new[] { record });

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Equal(PrintLogCsv.Header(), lines[0]);
            Assert.Single(lines.Where(l => l == PrintLogCsv.Header()));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Writer_GoesInertInsteadOfThrowingOnAnUnusablePath()
    {
        // a directory path can never be opened as a file
        string path = System.IO.Path.GetTempPath();

        using var writer = new PrintLogCsvWriter(path);
        var exception = Record.Exception(() =>
        {
            writer.Append(new[] { new PrintLogRecord { Seq = 1, Kind = PrintLogEventKind.Interval } });
            writer.Flush();
        });

        Assert.Null(exception);
        Assert.True(writer.Disabled);
        Assert.False(string.IsNullOrEmpty(writer.LastError));
        Assert.Equal(0, writer.RowsWritten);
    }

    [Fact]
    public void Writer_KeepsTheFileReadableWhileItIsOpen()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                             "printlog_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            using var writer = new PrintLogCsvWriter(path);
            writer.Append(new[] { new PrintLogRecord { Seq = 1, Kind = PrintLogEventKind.LayerEnd } });

            // LayerEnd forces a flush, so an inspector must see the row mid-print
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            Assert.Contains("LayerEnd", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- readable projections (final test round 5.20) ----------------------

    [Fact]
    public void LayerLine_IsOneReadableRow_NotAKeyValueDump()
    {
        var span = new PrintLogLayerSpan(12, T0.AddSeconds(151), T0.AddSeconds(193.4), 42.4, 18.26, 12, true);
        string line = PrintLogText.LayerLine(span);
        Assert.StartsWith("layer   12  10:02:31 -> 10:03:13", line);
        Assert.Contains("42.4 s", line);
        Assert.Contains("18.3 mm/s", line);
        Assert.EndsWith("12 samples", line);
        Assert.DoesNotContain("=", line);
        Assert.DoesNotContain("T10:", line);
    }

    [Fact]
    public void LayerLine_MarksOpenAndCutShortSpans()
    {
        var open = new PrintLogLayerSpan(3, T0, null, null, null, 2, false);
        var cut = new PrintLogLayerSpan(4, T0, T0.AddSeconds(5), 5.0, 9.0, 3, false);
        Assert.EndsWith("(open)", PrintLogText.LayerLine(open));
        Assert.Contains("->    ...  ", PrintLogText.LayerLine(open));
        Assert.EndsWith("(cut short)", PrintLogText.LayerLine(cut));
    }

    [Fact]
    public void RunSummary_CountsLayersAndAveragesTheRun()
    {
        var spans = new List<PrintLogLayerSpan>
        {
            new PrintLogLayerSpan(1, T0, T0.AddSeconds(40), 40.0, 17.0, 8, true),
            new PrintLogLayerSpan(2, T0.AddSeconds(40), T0.AddSeconds(85), 45.0, 19.0, 9, true),
            new PrintLogLayerSpan(3, T0.AddSeconds(85), null, null, null, 1, false),
        };
        Assert.Equal("layers: 3 (2 complete), printing time 1 min 25 s, mean layer 42.5 s, mean speed 18.0 mm/s",
                     PrintLogText.RunSummary(spans));
        Assert.Equal("layers: none recorded", PrintLogText.RunSummary(new List<PrintLogLayerSpan>()));

        // two runs with an idle hour between them: printing time is layer time, not wall time
        var twoRuns = new List<PrintLogLayerSpan>
        {
            new PrintLogLayerSpan(1, T0, T0.AddSeconds(40), 40.0, 17.0, 8, true),
            new PrintLogLayerSpan(1, T0.AddSeconds(3600), T0.AddSeconds(3645), 45.0, 19.0, 9, true),
        };
        Assert.StartsWith("layers: 2 (2 complete), printing time 1 min 25 s", PrintLogText.RunSummary(twoRuns));
    }

    [Fact]
    public void LayerLineAndRunSummary_AreCultureInvariant()
    {
        var span = new PrintLogLayerSpan(7, T0, T0.AddSeconds(61.5), 61.5, 12.25, 4, true);
        var spans = new List<PrintLogLayerSpan> { span };
        string lineInv = PrintLogText.LayerLine(span), sumInv = PrintLogText.RunSummary(spans);
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(lineInv, PrintLogText.LayerLine(span));
            Assert.Equal(sumInv, PrintLogText.RunSummary(spans));
        }
        finally { CultureInfo.CurrentCulture = old; }
        Assert.Contains("1 min 2 s", lineInv);
        Assert.Contains("12.3 mm/s", lineInv);
        Assert.Contains("12.3 mm/s", sumInv);
    }
}
