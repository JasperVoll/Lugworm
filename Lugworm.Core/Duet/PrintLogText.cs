using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lugworm.Core.Duet
{
    /// <summary>Human-readable projections of the print log for the Duet Live outputs
    /// (final test round 5.20: the `layers` lines were key=value dumps with 7-digit ISO
    /// timestamps and 15-digit doubles). The CSV stays the machine record; this is what
    /// an operator reads on a panel. Invariant culture, fixed layout, one decimal.</summary>
    public static class PrintLogText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>One line per layer span: number, clock start and end (HH:mm:ss),
        /// duration, mean observed speed, sample count, and an `(open)` / `(cut short)`
        /// marker instead of a boolean. `localTime` converts the UTC stamps for display;
        /// the CSV keeps both UTC and local ISO columns.</summary>
        public static string LayerLine(PrintLogLayerSpan span, bool localTime = false)
        {
            if (span == null) throw new ArgumentNullException(nameof(span));
            var sb = new StringBuilder(96);
            sb.Append("layer ").Append(span.Layer.ToString(Inv).PadLeft(4));
            sb.Append("  ").Append(Clock(span.StartUtc, localTime));
            sb.Append(" -> ").Append(span.EndUtc.HasValue ? Clock(span.EndUtc.Value, localTime) : "   ...  ");
            sb.Append("  ").Append(Seconds(span.DurationSeconds).PadLeft(8));
            sb.Append("  ").Append(Speed(span.MeanSpeed).PadLeft(10));
            sb.Append("  ").Append(span.SampleCount.ToString(Inv).PadLeft(4)).Append(" samples");
            if (!span.EndUtc.HasValue) sb.Append("  (open)");
            else if (!span.Complete) sb.Append("  (cut short)");
            return sb.ToString();
        }

        /// <summary>Run summary for the `logInfo` output: layer count, completed layers,
        /// printing time as the sum of the closed layer durations (the recorder keeps the
        /// spans of every run, so first-to-last would count the idle gap between two
        /// prints), mean layer time and the mean of the per-layer mean speeds.</summary>
        public static string RunSummary(IReadOnlyList<PrintLogLayerSpan> spans)
        {
            if (spans == null || spans.Count == 0) return "layers: none recorded";
            int complete = 0, durCount = 0, speedCount = 0;
            double durSum = 0, speedSum = 0;
            foreach (var s in spans)
            {
                if (s.Complete) complete++;
                if (s.DurationSeconds.HasValue) { durSum += s.DurationSeconds.Value; durCount++; }
                if (s.MeanSpeed.HasValue) { speedSum += s.MeanSpeed.Value; speedCount++; }
            }
            var sb = new StringBuilder(96);
            sb.Append("layers: ").Append(spans.Count.ToString(Inv));
            sb.Append(" (").Append(complete.ToString(Inv)).Append(" complete)");
            sb.Append(", printing time ").Append(Seconds(durSum));
            if (durCount > 0) sb.Append(", mean layer ").Append(Seconds(durSum / durCount));
            if (speedCount > 0) sb.Append(", mean speed ").Append(Speed(speedSum / speedCount));
            return sb.ToString();
        }

        static string Clock(DateTime utc, bool localTime)
            => (localTime ? utc.ToLocalTime() : utc).ToString("HH:mm:ss", Inv);

        static string Seconds(double? s)
        {
            if (!s.HasValue || double.IsNaN(s.Value) || double.IsInfinity(s.Value)) return "-";
            double v = Math.Max(0.0, s.Value);
            if (v < 60.0) return v.ToString("0.0", Inv) + " s";
            int m = (int)(v / 60.0);
            return m.ToString(Inv) + " min " + (v - 60.0 * m).ToString("0", Inv) + " s";
        }

        static string Speed(double? mms)
            => mms.HasValue && !double.IsNaN(mms.Value) && !double.IsInfinity(mms.Value)
               ? mms.Value.ToString("0.0", Inv) + " mm/s" : "- mm/s";
    }
}
