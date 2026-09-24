using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Extrusion
{
    /// <summary>Measured pump calibration: control setting → delivery rate.
    /// Loaded from a CSV (see Resources/pump_calibration_placeholder.csv for the
    /// placeholder + measurement protocol). Settings are the pump's control value
    /// (for a speed-controlled screw pump typically an analog 0–10 V speed signal, or
    /// whatever scale you drive it with); flows are measured l/min of FRESH MORTAR.</summary>
    public sealed class PumpCalibration
    {
        /// <summary>(setting, flow l/min), sorted ascending by setting.</summary>
        public readonly List<(double Setting, double FlowLpm)> Points = new List<(double, double)>();
        /// <summary>True when the file still contains NEEDS_INPUT markers - the table
        /// is the shipped placeholder, not real measurements.</summary>
        public bool IsPlaceholder;
        /// <summary># comment lines (metadata: pump, material, date, operator).</summary>
        public readonly List<string> Metadata = new List<string>();

        public double SettingMin => Points.Count > 0 ? Points[0].Setting : 0;
        public double SettingMax => Points.Count > 0 ? Points[Points.Count - 1].Setting : 0;
        public double FlowMin => Points.Count > 0 ? Points[0].FlowLpm : 0;
        public double FlowMax => Points.Count > 0 ? Points[Points.Count - 1].FlowLpm : 0;

        /// <summary>Parse "setting,flow_lpm" lines. '#' comments and blanks are skipped;
        /// any row containing NEEDS_INPUT marks the table as placeholder and is skipped.
        /// The delimiter and number convention (English decimal point / German decimal
        /// comma) are detected once for the whole file; a mixed or ambiguous file throws
        /// <see cref="Doe.CsvFormatException"/> instead of yielding wrong numbers.</summary>
        public static PumpCalibration ParseCsv(IEnumerable<string> lines)
        {
            var cal = new PumpCalibration();
            var data = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) { cal.Metadata.Add(line); continue; }
#if NETFRAMEWORK
                if (line.IndexOf("NEEDS_INPUT", StringComparison.OrdinalIgnoreCase) >= 0)
#else
                if (line.Contains("NEEDS_INPUT", StringComparison.OrdinalIgnoreCase))
#endif
                { cal.IsPlaceholder = true; continue; }
                data.Add(line);
            }
            if (data.Count == 0) return cal;

            var det = Doe.CsvFormat.Detect(data, expectedCols: 2);
            foreach (var line in data)
            {
                var parts = Doe.TableIo.SplitCsv(line, det.Delimiter);
                if (parts.Length < 2) continue;
                double? s = Doe.CsvFormat.ParseCell(parts[0], det.Style);
                double? q = Doe.CsvFormat.ParseCell(parts[1], det.Style);
                if (s != null && q != null) cal.Points.Add((s.Value, q.Value));
            }
            cal.Points.Sort((a, b) => a.Setting.CompareTo(b.Setting));
            return cal;
        }

        /// <summary>Structural problems that make the table unusable (empty list = OK).</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (IsPlaceholder)
                problems.Add("calibration table is the NEEDS_INPUT placeholder - run the bucket test " +
                             "(docs/reference/pump-extrusion.md §Calibration protocol) and fill in measured points");
            if (Points.Count < 2)
                problems.Add($"calibration needs at least 2 measured points, found {Points.Count}");
            for (int i = 1; i < Points.Count; i++)
                if (Points[i].FlowLpm <= Points[i - 1].FlowLpm)
                    problems.Add($"flow must increase with the setting, but " +
                                 FormattableString.Invariant($"({Points[i - 1].Setting:0.###} → {Points[i - 1].FlowLpm:0.###} l/min) is followed by ") +
                                 FormattableString.Invariant($"({Points[i].Setting:0.###} → {Points[i].FlowLpm:0.###} l/min)"));
            foreach (var (s, q) in Points)
                if (q < 0) problems.Add(FormattableString.Invariant($"negative flow {q:0.###} l/min at setting {s:0.###}"));
            return problems;
        }

        /// <summary>Delivery rate at a setting (piecewise linear). Null outside the
        /// calibrated setting range - no extrapolation, ever.</summary>
        public double? FlowForSetting(double setting)
        {
            if (Points.Count < 2 || setting < SettingMin - 1e-9 || setting > SettingMax + 1e-9) return null;
            for (int i = 1; i < Points.Count; i++)
            {
                var (s0, q0) = Points[i - 1];
                var (s1, q1) = Points[i];
                if (setting <= s1 + 1e-9)
                {
                    double t = s1 - s0 < 1e-12 ? 0.0 : (setting - s0) / (s1 - s0);
                    return q0 + t * (q1 - q0);
                }
            }
            return Points[Points.Count - 1].FlowLpm;
        }

        /// <summary>Setting that delivers the requested flow (piecewise linear inverse,
        /// valid because Validate() enforces monotonically increasing flow).
        /// Null when the flow is outside the calibrated range - no extrapolation.</summary>
        public double? SettingForFlow(double flowLpm)
        {
            if (Points.Count < 2 || flowLpm < FlowMin - 1e-9 || flowLpm > FlowMax + 1e-9) return null;
            for (int i = 1; i < Points.Count; i++)
            {
                var (s0, q0) = Points[i - 1];
                var (s1, q1) = Points[i];
                if (flowLpm <= q1 + 1e-9)
                {
                    double t = q1 - q0 < 1e-12 ? 0.0 : (flowLpm - q0) / (q1 - q0);
                    return s0 + t * (s1 - s0);
                }
            }
            return Points[Points.Count - 1].Setting;
        }
    }

    /// <summary>Inputs for the pump-flow calculation. Units on every field.</summary>
    public sealed class PumpPlanInput
    {
        public double NozzleDiameter;      // mm (sanity checks only - flow comes from the layer)
        public double LayerHeight;         // mm (layer height)
        public double LayerWidth;           // mm (layer width)
        public double PrintSpeed;          // mm/s (TCP speed while extruding)
        public double FirstLayerSpeedFactor = 1.0;   // dimensionless (e.g. 0.5)
        /// <summary>Volumetric efficiency / slip factor, dimensionless. Multiplies the
        /// required flow (mortar slip, pulsation, compressibility). Default 1.0 -
        /// CALIBRATE PER MATERIAL by comparing planned vs actually printed volume.</summary>
        public double SlipFactor = 1.0;
    }

    /// <summary>Result of a pump plan: required flow(s) and, when a usable calibration
    /// table exists, the recommended pump setting(s).</summary>
    public sealed class PumpPlan
    {
        public double QRequiredLpm;        // l/min at PrintSpeed
        public double QFirstLayerLpm;      // l/min at PrintSpeed * FirstLayerSpeedFactor
        public double? Setting;            // recommended setting for QRequiredLpm (null = not derivable)
        public double? SettingFirstLayer;  // recommended setting for the first layer
        public bool Ok;                    // Setting derived and inside the calibrated range
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Summary = new List<string>();
    }

    /// <summary>Pump-mode flow model for any screw pump with a measured
    /// setting→flow table. All math is volumetric and material-agnostic:
    ///
    ///   Q_required [mm³/s] = LayerHeight[mm] - LayerWidth[mm] - PrintSpeed[mm/s] - SlipFactor
    ///   Q_required [l/min] = Q[mm³/s] - 60 / 1e6
    ///
    /// (rectangular layer cross-section - the same convention as the G-code E-factor).
    /// The setting recommendation interpolates the measured calibration table and
    /// REFUSES to answer (with a NEEDS_INPUT warning) when the table is the shipped
    /// placeholder, invalid, or the required flow falls outside its measured range.
    /// No pump performance numbers are hardcoded here.</summary>
    public static class PumpModel
    {
        /// <summary>mm³/s → l/min. 1 l = 1e6 mm³; 1 min = 60 s.</summary>
        public const double Mm3PerSecToLpm = 60.0 / 1e6;

        /// <summary>Required volumetric flow in l/min for a layer of
        /// h[mm] × w[mm] laid at v[mm/s], scaled by the slip factor.</summary>
        public static double RequiredFlowLpm(double layerHeightMm, double layerWidthMm,
                                             double printSpeedMmS, double slipFactor = 1.0)
            => layerHeightMm * layerWidthMm * printSpeedMmS * slipFactor * Mm3PerSecToLpm;

        public static PumpPlan Plan(PumpPlanInput inp, PumpCalibration? cal)
        {
            var inv = CultureInfo.InvariantCulture;
            var plan = new PumpPlan();

            if (inp.LayerHeight <= 0 || inp.LayerWidth <= 0 || inp.PrintSpeed <= 0)
            {
                plan.Warnings.Add("layer height, layer width and print speed must all be > 0 - no flow computed");
                return plan;
            }
            if (inp.SlipFactor <= 0)
                plan.Warnings.Add($"slip factor {inp.SlipFactor.ToString("0.###", inv)} is not positive - check the input");

            plan.QRequiredLpm = RequiredFlowLpm(inp.LayerHeight, inp.LayerWidth, inp.PrintSpeed, inp.SlipFactor);
            double flFactor = inp.FirstLayerSpeedFactor > 0 ? inp.FirstLayerSpeedFactor : 1.0;
            plan.QFirstLayerLpm = plan.QRequiredLpm * flFactor;

            if (inp.NozzleDiameter > 0 && inp.LayerWidth < inp.NozzleDiameter)
                plan.Warnings.Add(
                    $"layer width {inp.LayerWidth.ToString("0.#", inv)}mm is smaller than the nozzle diameter " +
                    $"{inp.NozzleDiameter.ToString("0.#", inv)}mm - extrusion-based layers are normally ≥ the nozzle bore");

            plan.Summary.Add($"Q required: {plan.QRequiredLpm.ToString("0.000", inv)} l/min " +
                             $"(layer {inp.LayerHeight.ToString("0.#", inv)}x{inp.LayerWidth.ToString("0.#", inv)}mm " +
                             $"at {inp.PrintSpeed.ToString("0.#", inv)}mm/s, slip {inp.SlipFactor.ToString("0.##", inv)})");

            // No rated-range check: the model is pump-agnostic, and the measured table's
            // flow range below is the only range a recommendation is ever made from.
            if (Math.Abs(flFactor - 1.0) > 1e-9)
                plan.Summary.Add($"Q first layer: {plan.QFirstLayerLpm.ToString("0.000", inv)} l/min " +
                                 $"(speed factor {flFactor.ToString("0.##", inv)})");

            // ---- setting recommendation strictly from the measured table ----
            if (cal == null)
            {
                plan.Warnings.Add("NEEDS_INPUT: no pump calibration table loaded - required flow is reported, " +
                                  "but no pump setting can be recommended");
                plan.Summary.Add("pump setting: n/a (no calibration table)");
                return plan;
            }
            var problems = cal.Validate();
            if (problems.Count > 0)
            {
                foreach (var p in problems) plan.Warnings.Add((cal.IsPlaceholder ? "NEEDS_INPUT: " : "") + p);
                plan.Summary.Add("pump setting: n/a (calibration unusable - see warnings)");
                return plan;
            }

            plan.Setting = cal.SettingForFlow(plan.QRequiredLpm);
            plan.SettingFirstLayer = cal.SettingForFlow(plan.QFirstLayerLpm);

            if (plan.Setting == null)
            {
                plan.Warnings.Add(
                    $"required flow {plan.QRequiredLpm.ToString("0.000", inv)} l/min is OUTSIDE the calibrated range " +
                    $"[{cal.FlowMin.ToString("0.000", inv)}, {cal.FlowMax.ToString("0.000", inv)}] l/min - " +
                    "no setting recommended; adjust print speed / layer, or extend the calibration");
                plan.Summary.Add("pump setting: n/a (out of calibrated range)");
            }
            else
            {
                plan.Ok = true;
                plan.Summary.Add($"pump setting: {plan.Setting.Value.ToString("0.00", inv)} " +
                                 $"(calibrated {cal.SettingMin.ToString("0.#", inv)}..{cal.SettingMax.ToString("0.#", inv)} " +
                                 $"→ {cal.FlowMin.ToString("0.00", inv)}..{cal.FlowMax.ToString("0.00", inv)} l/min)");
                if (plan.SettingFirstLayer != null && Math.Abs(flFactor - 1.0) > 1e-9)
                    plan.Summary.Add($"pump setting first layer: {plan.SettingFirstLayer.Value.ToString("0.00", inv)}");
                else if (plan.SettingFirstLayer == null && Math.Abs(flFactor - 1.0) > 1e-9)
                    plan.Warnings.Add("first-layer flow is outside the calibrated range - no first-layer setting recommended");
            }
            return plan;
        }
    }
}
