using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Lugworm.Core.Duet;

namespace Lugworm.Ui.Console
{
    /// <summary>Modal schedule dialogs. They only return action ids and never send to a
    /// machine.</summary>
    public static class ScheduleStartDialog
    {
        public static string? Show(IWin32Window owner, PrintConsoleState state, JobTimeEstimate? estimate)
        {
            using (var form = new Form
            {
                Text = "Schedule print",
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(470, 330),
            })
            {
                // The start happens with nobody necessarily watching: material, prime and a
                // clear bed are not observable by any gate, so the operator confirms them.
                var ready = new CheckBox
                {
                    Text = "Material is loaded, the nozzle is primed and the bed is clear. The job will start without anyone pressing Start.",
                    AutoSize = false, Location = new Point(18, 228), Size = new Size(430, 42),
                };
                var at = new RadioButton { Text = "At", Location = new Point(18, 18), AutoSize = true, Checked = true };
                var inTime = new RadioButton { Text = "In", Location = new Point(18, 52), AutoSize = true };
                var picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd  HH:mm", Location = new Point(75, 14), Width = 190, Value = DateTime.Now.AddHours(1) };
                var hours = new NumericUpDown { Minimum = 0, Maximum = 23, Value = 1, Location = new Point(75, 48), Width = 65 };
                var minutes = new NumericUpDown { Minimum = 0, Maximum = 59, Location = new Point(150, 48), Width = 65 };
                var detail = new Label { AutoSize = false, Location = new Point(18, 90), Size = new Size(430, 92), ForeColor = SystemColors.ControlText };
                var reason = new Label { AutoSize = false, Location = new Point(18, 186), Size = new Size(430, 38), ForeColor = Color.DarkGoldenrod };
                var ok = new Button { Text = "Schedule", DialogResult = DialogResult.OK, Location = new Point(282, 285), Width = 85 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(374, 285), Width = 75 };
                form.Controls.AddRange(new Control[] { at, inTime, picker, hours, minutes, detail, reason, ready, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;

                DateTime Due()
                    => at.Checked ? picker.Value.ToUniversalTime()
                                  : DateTime.UtcNow.AddHours((double)hours.Value).AddMinutes((double)minutes.Value);

                void Update()
                {
                    DateTime due = Due();
                    string? refusal = PrintSchedulePolicy.RefuseArm(DateTime.UtcNow, due, state);
                    ok.Enabled = refusal == null && ready.Checked;
                    reason.Text = refusal ?? "Rhino and this document must stay open and connected. At the due time the "
                        + "normal Start gates run again: idle, homed (or a job that homes itself), the SD file re-verified.";
                    string duration = estimate != null && estimate.IsAvailable
                        ? "Estimated duration at commanded feeds: " + Duration(estimate.TotalS)
                        : "Estimated duration at commanded feeds: -";
                    string finish = estimate != null && estimate.IsAvailable
                        ? "Finish: " + due.AddSeconds(estimate.TotalS).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        : "Finish: -";
                    detail.Text = "Start: " + due.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                        + Environment.NewLine + duration + Environment.NewLine + finish + Environment.NewLine
                        + "Job: " + (state.ScheduledStart?.ShortHash ?? state.GcodeHash.ToString("x8", CultureInfo.InvariantCulture))
                        + (state.DryRun ? " (dry run)" : " (print)");
                }

                at.CheckedChanged += (s, e) => Update();
                inTime.CheckedChanged += (s, e) => Update();
                picker.ValueChanged += (s, e) => Update();
                hours.ValueChanged += (s, e) => Update();
                minutes.ValueChanged += (s, e) => Update();
                ready.CheckedChanged += (s, e) => Update();
                Update();
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                return "schedule:set:" + Due().Ticks.ToString(CultureInfo.InvariantCulture);
            }
        }

        static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "-";
            return TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        }
    }

    public static class SchedulePauseDialog
    {
        public static string? Show(IWin32Window owner, PrintConsoleState state, JobTimeEstimate? estimate)
        {
            using (var form = new Form
            {
                Text = "Schedule pause",
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(470, 260),
            })
            {
                var inTime = new RadioButton { Text = "In", Location = new Point(18, 18), AutoSize = true, Checked = true };
                var atLayer = new RadioButton { Text = "At layer", Location = new Point(18, 52), AutoSize = true };
                var hours = new NumericUpDown { Minimum = 0, Maximum = 23, Value = 0, Location = new Point(100, 14), Width = 65 };
                var minutes = new NumericUpDown { Minimum = 1, Maximum = 59, Value = 10, Location = new Point(175, 14), Width = 65 };
                int current = state.CurrentMarkerLayer;
                int total = state.ActiveJob?.LayerMarkers.Total ?? 0;
                bool layerAvailable = state.ActiveJob != null && state.ActiveJob.LayerMarkers.CanPauseAtLayer && total > current;
                // Bounds kept ordered (min <= value <= max) even when no layer is
                // available, e.g. an external job whose firmware layer exceeds the 0 total:
                // NumericUpDown throws on a value outside its range.
                int layerMax = Math.Max(1, total);
                int layerMin = Math.Min(Math.Max(1, current + 1), layerMax);
                var layer = new NumericUpDown { Minimum = layerMin, Maximum = layerMax, Value = layerMin, Location = new Point(100, 48), Width = 65, Enabled = layerAvailable };
                var detail = new Label { AutoSize = false, Location = new Point(18, 90), Size = new Size(430, 72) };
                var reason = new Label { AutoSize = false, Location = new Point(18, 166), Size = new Size(430, 38), ForeColor = Color.DarkGoldenrod };
                var ok = new Button { Text = "Add pause", DialogResult = DialogResult.OK, Location = new Point(282, 213), Width = 85 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(374, 213), Width = 75 };
                form.Controls.AddRange(new Control[] { inTime, atLayer, hours, minutes, layer, detail, reason, ok, cancel });
                form.AcceptButton = ok; form.CancelButton = cancel;
                atLayer.Enabled = layerAvailable;

                DateTime? Due() => inTime.Checked
                    ? DateTime.UtcNow.AddHours((double)hours.Value).AddMinutes((double)minutes.Value)
                    : (DateTime?)null;

                void Update()
                {
                    PauseKind kind = atLayer.Checked ? PauseKind.AtLayer : PauseKind.AtTime;
                    DateTime? due = Due();
                    int? target = atLayer.Checked ? (int?)layer.Value : null;
                    string? refusal = PauseSchedulePolicy.RefuseAdd(kind, due, target, DateTime.UtcNow, state);
                    ok.Enabled = refusal == null;
                    reason.Text = refusal ?? (kind == PauseKind.AtLayer ? "The pause is sent at the layer marker seam." : "The pause is sent exactly when the timer runs out.");
                    string estimateText = "Estimate: -";
                    if (estimate != null && estimate.IsAvailable)
                    {
                        if (atLayer.Checked)
                        {
                            double? seconds = estimate.TimeToLayer((int)layer.Value, state.JobFilePosition,
                                                                    estimate.CorrectionFactor);
                            if (seconds.HasValue)
                                estimateText = "Estimate: about " + Duration(seconds.Value) + " ("
                                    + DateTime.UtcNow.AddSeconds(seconds.Value).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ")";
                        }
                        else
                        {
                            double seconds = Math.Max(0.0, (Due()!.Value - DateTime.UtcNow).TotalSeconds);
                            int? targetLayer = estimate.LayerAtTime(seconds, state.JobFilePosition,
                                                                     estimate.CorrectionFactor);
                            if (targetLayer.HasValue)
                                estimateText = "Estimate: about layer " + targetLayer.Value.ToString(CultureInfo.InvariantCulture)
                                    + " of " + total.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    detail.Text = kind == PauseKind.AtLayer
                        ? "Layer " + layer.Value.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + estimateText
                        : "Timer: " + Due()!.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + Environment.NewLine + estimateText;
                }

                inTime.CheckedChanged += (s, e) => Update();
                atLayer.CheckedChanged += (s, e) => Update();
                hours.ValueChanged += (s, e) => Update();
                minutes.ValueChanged += (s, e) => Update();
                layer.ValueChanged += (s, e) => Update();
                Update();
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                return inTime.Checked
                    ? "pause:add:time:" + Due()!.Value.Ticks.ToString(CultureInfo.InvariantCulture)
                    : "pause:add:layer:" + layer.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "-";
            return TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        }
    }
}
