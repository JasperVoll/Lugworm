using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>Reads the print-log CSV that <see cref="PrintLogCsv"/> writes (the
    /// Duet Live <c>log</c> output, the streaming <c>printlog_*.csv</c>, or a retained
    /// job log) back into <see cref="PrintLogRecord"/>s. Columns are matched by
    /// header name so a file with extra or reordered columns still reads; the three
    /// position columns are required. Invariant culture by construction - the writer
    /// never emits anything else - so a decimal comma is a malformed cell, not a
    /// convention to detect.</summary>
    public static class PrintLogCsvReader
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Result
        {
            public readonly List<PrintLogRecord> Records = new List<PrintLogRecord>();
            public int SkippedRows;
            public string Note = "";
        }

        /// <summary>Parse header + rows. Throws <see cref="FormatException"/> when the
        /// header is missing or lacks the position columns; a malformed row is skipped
        /// and counted rather than failing the whole file.</summary>
        public static Result Parse(IReadOnlyList<string> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            var result = new Result();
            int headerIndex = -1;
            for (int i = 0; i < lines.Count; i++)
                if (!string.IsNullOrWhiteSpace(lines[i])) { headerIndex = i; break; }
            if (headerIndex < 0) throw new FormatException("print log is empty");

            var header = SplitRow(lines[headerIndex].TrimStart('﻿'));
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                string name = header[i].Trim();
                if (name.Length > 0 && !col.ContainsKey(name)) col[name] = i;
            }
            if (!col.ContainsKey("x") || !col.ContainsKey("y") || !col.ContainsKey("z"))
                throw new FormatException("print log header lacks the x, y, z columns - is this the print-log CSV?");

            for (int i = headerIndex + 1; i < lines.Count; i++)
            {
                string raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                List<string> cells;
                try { cells = SplitRow(raw); }
                catch (FormatException) { result.SkippedRows++; continue; }
                var record = new PrintLogRecord();
                // Schema 1 has no schema column; nothing may be inferred from the absence
                // of the continuity fields, so they keep their neutral defaults.
                long? schema = Int64(cells, col, "log_schema");
                record.Schema = schema.HasValue ? (int)schema.Value : 1;
                string? validCell = Text(cells, col, "position_valid");
                bool declaredInvalid = validCell != null && (validCell == "0" || validCell.Equals("false", StringComparison.OrdinalIgnoreCase));
                double? x = Num(cells, col, "x"), y = Num(cells, col, "y"), z = Num(cells, col, "z");
                if (!x.HasValue || !y.HasValue || !z.HasValue)
                {
                    // A schema-2 row that says so carries no coordinates on purpose; it is
                    // evidence of an unusable observation and must survive the round trip.
                    // A row without that declaration is malformed, as before.
                    if (!declaredInvalid) { result.SkippedRows++; continue; }
                    record.Position = new Vec3(x ?? 0, y ?? 0, z ?? 0);
                }
                else record.Position = new Vec3(x.Value, y.Value, z.Value);
                record.PositionValid = !declaredInvalid;
                record.Seq = Int64(cells, col, "seq") ?? i;
                record.Kind = Enum(cells, col, "kind", PrintLogEventKind.Interval);
                record.JobRun = (int)(Int64(cells, col, "job_run") ?? 1);
                record.JobFile = Text(cells, col, "job_file");
                long? layer = Int64(cells, col, "layer");
                record.Layer = layer.HasValue ? (int?)(int)layer.Value : null;
                record.LayerSource = Enum(cells, col, "layer_source", PrintLogLayerSource.None);
                record.TakenUtc = Utc(cells, col, "utc_iso") ?? Local(cells, col, "local_iso") ?? DateTime.MinValue;
                record.JobElapsedSeconds = Num(cells, col, "job_elapsed_s");
                record.DeltaSeconds = Num(cells, col, "dt_s");
                record.SpeedObserved = Num(cells, col, "speed_observed_mms");
                record.SpeedRequested = Num(cells, col, "speed_requested_mms");
                record.SpeedTop = Num(cells, col, "speed_top_mms");
                record.SpeedFactorPercent = Num(cells, col, "speed_factor_pct");
                record.RawExtrusion = Num(cells, col, "raw_extrusion_mm");
                record.FractionPrinted = Num(cells, col, "fraction_printed");
                record.FilePosition = Int64(cells, col, "file_position");
                record.Status = Text(cells, col, "status") ?? "";
                record.BoundaryUncertaintySeconds = Num(cells, col, "boundary_uncertainty_s");
                string? finish = Text(cells, col, "finish_reason");
                record.FinishReason = finish != null && System.Enum.TryParse(finish, true, out PrintLogFinishReason fr)
                    ? (PrintLogFinishReason?)fr : null;
                record.TerminalStatus = Text(cells, col, "terminal_status");
                record.RunId = Text(cells, col, "run_id") ?? "";
                record.SegmentId = Int64(cells, col, "segment_id") ?? 0;
                record.Quality = Enum(cells, col, "quality", TrailQuality.Valid);
                record.Classification = Enum(cells, col, "classification", TrailClass.Unknown);
                record.QualityReason = Text(cells, col, "quality_reason");
                record.PathDistanceMm = Num(cells, col, "path_distance_mm");
                record.ImpliedSpeedMms = Num(cells, col, "implied_speed_mms");
                record.AcquisitionId = Int64(cells, col, "acquisition_id") ?? 0;
                record.PositionInvalidReason = Text(cells, col, "position_invalid_reason");
                string? composite = Text(cells, col, "composite");
                record.Composite = composite != null
                    && (composite == "1" || composite.Equals("true", StringComparison.OrdinalIgnoreCase));
                result.Records.Add(record);
            }
            if (result.SkippedRows > 0)
                result.Note = result.SkippedRows.ToString(Inv) + " malformed row(s) skipped";
            return result;
        }

        static string? Text(List<string> cells, Dictionary<string, int> col, string name)
        {
            if (!col.TryGetValue(name, out int i) || i >= cells.Count) return null;
            string s = cells[i];
            return s.Length == 0 ? null : s;
        }

        static double? Num(List<string> cells, Dictionary<string, int> col, string name)
        {
            string? s = Text(cells, col, name);
            if (s == null) return null;
            return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out double v)
                   && !double.IsNaN(v) && !double.IsInfinity(v) ? (double?)v : null;
        }

        static long? Int64(List<string> cells, Dictionary<string, int> col, string name)
        {
            string? s = Text(cells, col, name);
            if (s == null) return null;
            return long.TryParse(s.Trim(), NumberStyles.Integer, Inv, out long v) ? (long?)v : null;
        }

        static T Enum<T>(List<string> cells, Dictionary<string, int> col, string name, T fallback) where T : struct
        {
            string? s = Text(cells, col, name);
            return s != null && System.Enum.TryParse(s.Trim(), true, out T v) ? v : fallback;
        }

        static DateTime? Utc(List<string> cells, Dictionary<string, int> col, string name)
        {
            string? s = Text(cells, col, name);
            if (s == null) return null;
            return DateTime.TryParse(s.Trim(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? (DateTime?)DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        }

        static DateTime? Local(List<string> cells, Dictionary<string, int> col, string name)
        {
            string? s = Text(cells, col, name);
            if (s == null) return null;
            return DateTime.TryParse(s.Trim(), Inv, DateTimeStyles.AdjustToUniversal, out var t)
                ? (DateTime?)DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        }

        /// <summary>RFC-4180 style cell split matching <see cref="PrintLogCsv.Row"/>'s
        /// quoting: a quoted cell may hold commas, newlines and doubled quotes.</summary>
        internal static List<string> SplitRow(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"')
                {
                    if (sb.Length != 0) throw new FormatException("quote inside an unquoted cell");
                    quoted = true;
                }
                else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            if (quoted) throw new FormatException("unterminated quoted cell");
            cells.Add(sb.ToString());
            return cells;
        }
    }
}
