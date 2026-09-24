using System;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    public enum PrintLogEventKind
    {
        JobStart,
        LayerStart,
        Interval,
        LayerEnd,
        JobEnd,
    }

    public enum PrintLogLayerSource
    {
        None,
        Firmware,
        ZStep,
    }

    public enum PrintLogFinishReason
    {
        SessionStopped,
        ConnectionLost,
        LoggingDisabled,
    }

    /// <summary>One timestamped print-log row. Fields are assigned once by the
    /// recorder and then treated as immutable by the file and UI consumers.</summary>
    public sealed class PrintLogRecord
    {
        public long Seq;
        public PrintLogEventKind Kind;
        public int JobRun;
        public string? JobFile;
        public int? Layer;
        public PrintLogLayerSource LayerSource;
        public DateTime TakenUtc;
        public double? JobElapsedSeconds;
        public double? DeltaSeconds;
        public Vec3 Position;
        public double? SpeedObserved;
        public double? SpeedRequested;
        public double? SpeedTop;
        public double? SpeedFactorPercent;
        public double? RawExtrusion;
        public double? FractionPrinted;
        public long? FilePosition;
        public string Status = "";
        public double? BoundaryUncertaintySeconds;
        public PrintLogFinishReason? FinishReason;
        /// <summary>Machine state that ended the run, on the run-end records only.
        /// Position and time on those rows come from the last PRINTING poll, so
        /// without this a cancel and a clean finish would be indistinguishable.</summary>
        public string? TerminalStatus;

        // ---- schema 2 (2026-09-09 telemetry integrity) -----------------------------

        /// <summary>CSV schema this record was read from or will be written as. 1 is a
        /// pre-continuity log: its quality and segment columns simply do not exist, and
        /// nothing may be inferred from their absence.</summary>
        public int Schema = PrintLogCsv.SchemaVersion;
        /// <summary>Identity of the RUN, not of the file. Printing the same file twice
        /// gives two run ids, so the two prints never share a segment or a history entry.
        /// Empty on an imported schema-1 log.</summary>
        public string RunId = "";
        /// <summary>Continuous-observation segment. Rows with different segment ids must
        /// never be joined by a line.</summary>
        public long SegmentId;
        public TrailQuality Quality = TrailQuality.Valid;
        public TrailClass Classification = TrailClass.Unknown;
        /// <summary>Why the quality is not Valid, or why the segment broke.</summary>
        public string? QualityReason;
        /// <summary>Same-elevation distance to the intended path, mm; null when no
        /// comparison was available. Null is "unknown", never "on the path".</summary>
        public double? PathDistanceMm;
        /// <summary>Speed implied by the coordinate difference over the actual elapsed
        /// time, mm/s; null when it could not be computed.</summary>
        public double? ImpliedSpeedMms;
        public long AcquisitionId;
        /// <summary>False when this row carries no usable position; the coordinate cells
        /// then hold whatever the machine did report, and empty for what it did not.</summary>
        public bool PositionValid = true;
        /// <summary>Why the position is unusable.</summary>
        public string? PositionInvalidReason;
        /// <summary>The observation was assembled from separate requests or stitched
        /// pages, so its parts may describe different instants.</summary>
        public bool Composite;

        public PrintLogRecord() { }

        internal PrintLogRecord(PrintLogEventKind kind, int jobRun, string? jobFile, int? layer,
                                PrintLogLayerSource layerSource, DateTime takenUtc,
                                double? jobElapsedSeconds, double? deltaSeconds, Vec3 position,
                                double? speedObserved, double? speedRequested, double? speedTop,
                                double? speedFactorPercent, double? rawExtrusion,
                                double? fractionPrinted, long? filePosition, string status,
                                double? boundaryUncertaintySeconds, PrintLogFinishReason? finishReason,
                                string? terminalStatus = null)
        {
            Kind = kind;
            JobRun = jobRun;
            JobFile = jobFile;
            Layer = layer;
            LayerSource = layerSource;
            TakenUtc = takenUtc;
            JobElapsedSeconds = jobElapsedSeconds;
            DeltaSeconds = deltaSeconds;
            Position = position;
            SpeedObserved = speedObserved;
            SpeedRequested = speedRequested;
            SpeedTop = speedTop;
            SpeedFactorPercent = speedFactorPercent;
            RawExtrusion = rawExtrusion;
            FractionPrinted = fractionPrinted;
            FilePosition = filePosition;
            Status = status ?? "";
            BoundaryUncertaintySeconds = boundaryUncertaintySeconds;
            FinishReason = finishReason;
            TerminalStatus = terminalStatus;
        }
    }

    /// <summary>Summary of one detected layer. Completed spans have both time
    /// endpoints; an interrupted final span is explicitly incomplete.</summary>
    public sealed class PrintLogLayerSpan
    {
        public readonly int Layer;
        public readonly DateTime StartUtc;
        public readonly DateTime? EndUtc;
        public readonly double? DurationSeconds;
        public readonly double? MeanSpeed;
        public readonly int SampleCount;
        public readonly bool Complete;

        internal PrintLogLayerSpan(int layer, DateTime startUtc, DateTime? endUtc,
                                   double? durationSeconds, double? meanSpeed,
                                   int sampleCount, bool complete)
        {
            Layer = layer;
            StartUtc = startUtc;
            EndUtc = endUtc;
            DurationSeconds = durationSeconds;
            MeanSpeed = meanSpeed;
            SampleCount = sampleCount;
            Complete = complete;
        }
    }

    public sealed class PrintLogOptions
    {
        public bool Enabled;
        public int SampleEveryNPolls = 5;
        public int MaxRecords = 50000;
        public double LayerHeightHint;

        public PrintLogOptions() { }

        public PrintLogOptions(bool enabled, int sampleEveryNPolls = 5,
                               int maxRecords = 50000, double layerHeightHint = 0)
        {
            Enabled = enabled;
            SampleEveryNPolls = sampleEveryNPolls;
            MaxRecords = maxRecords;
            LayerHeightHint = layerHeightHint;
        }

        internal PrintLogOptions Copy()
            => new PrintLogOptions(Enabled, SampleEveryNPolls, MaxRecords, LayerHeightHint);
    }

    /// <summary>Shared CSV representation for the streaming writer and component
    /// text output. All values are invariant-culture and ordinary records leave
    /// the finish-reason column empty.</summary>
    public static class PrintLogCsv
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Schema of the columns this writer emits. It is written into every row
        /// (the <c>log_schema</c> column) rather than into a banner line, because the
        /// reader takes the first non-blank line as the header and a banner would break
        /// every existing importer. Schema 2 appends the continuity, quality and identity
        /// columns after the schema-1 ones, so the original column order is untouched and
        /// a schema-1 file still imports.</summary>
        public const int SchemaVersion = 2;

        public static string Header()
            => "seq,kind,job_run,job_file,layer,layer_source,utc_iso,local_iso,job_elapsed_s,dt_s,"
             + "x,y,z,speed_observed_mms,speed_requested_mms,speed_top_mms,speed_factor_pct,"
             + "raw_extrusion_mm,fraction_printed,file_position,status,boundary_uncertainty_s,"
             + "finish_reason,terminal_status,"
             + "log_schema,run_id,segment_id,quality,classification,quality_reason,"
             + "path_distance_mm,implied_speed_mms,acquisition_id,position_valid,"
             + "position_invalid_reason,composite";

        public static string Row(PrintLogRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string[] cells =
            {
                record.Seq.ToString(Invariant),
                record.Kind.ToString(),
                record.JobRun.ToString(Invariant),
                record.JobFile ?? "",
                Format(record.Layer),
                record.LayerSource.ToString(),
                UtcIso(record.TakenUtc),
                LocalIso(record.TakenUtc),
                Format(record.JobElapsedSeconds),
                Format(record.DeltaSeconds),
                // An unusable position writes no coordinates at all. A zero here would be
                // exactly the fabrication the strict axis rule removed upstream.
                record.PositionValid ? Format(record.Position.X) : "",
                record.PositionValid ? Format(record.Position.Y) : "",
                record.PositionValid ? Format(record.Position.Z) : "",
                Format(record.SpeedObserved),
                Format(record.SpeedRequested),
                Format(record.SpeedTop),
                Format(record.SpeedFactorPercent),
                Format(record.RawExtrusion),
                Format(record.FractionPrinted),
                Format(record.FilePosition),
                record.Status ?? "",
                Format(record.BoundaryUncertaintySeconds),
                record.FinishReason.HasValue ? record.FinishReason.Value.ToString() : "",
                record.TerminalStatus ?? "",
                SchemaVersion.ToString(Invariant),
                record.RunId ?? "",
                record.SegmentId.ToString(Invariant),
                record.Quality.ToString(),
                record.Classification.ToString(),
                record.QualityReason ?? "",
                Format(record.PathDistanceMm),
                Format(record.ImpliedSpeedMms),
                record.AcquisitionId == 0 ? "" : record.AcquisitionId.ToString(Invariant),
                record.PositionValid ? "1" : "0",
                record.PositionInvalidReason ?? "",
                record.Composite ? "1" : "0",
            };

            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Quote(cells[i]));
            }
            return sb.ToString();
        }

        static string Format(int? value)
            => value.HasValue ? value.Value.ToString(Invariant) : "";

        static string Format(long? value)
            => value.HasValue ? value.Value.ToString(Invariant) : "";

        static string Format(double? value)
            => value.HasValue ? Format(value.Value) : "";

        static string Format(double value)
            => value.ToString("0.###############", Invariant);

        static string UtcIso(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Invariant);
        }

        static string LocalIso(DateTime value)
            => value.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", Invariant);

        static string Quote(string value)
#if NETFRAMEWORK
            => value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0
#else
            => value.Contains(',') || value.Contains('"') || value.Contains('\n')
#endif
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
    }
}
