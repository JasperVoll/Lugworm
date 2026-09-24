using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Gcode
{
    /// <summary>Header / footer generation. Port of clay_core_v2 §10, made
    /// flavor-aware: Marlin uses M900 (linear advance); Duet/RRF uses M572; Klipper
    /// uses SET_PRESSURE_ADVANCE and SET_VELOCITY_LIMIT.</summary>
    public static class Header
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Klipper has no per-axis M201/M205 and no separate print accel: one
        /// SET_VELOCITY_LIMIT ACCEL is the toolhead's acceleration (M204 writes the same
        /// field), so the print accel wins when set and the XY limit otherwise. Z keeps
        /// printer.cfg's max_z_accel. Null when nothing is set. Shared with the
        /// builder's inline re-issue.</summary>
        public static string? KlipperVelocityLimit(GcodeConfig c, double factor)
        {
            var args = new List<(string, double)>();
            double? accel = c.AccelPrint ?? c.AccelXY;
            if (accel.HasValue) args.Add(("ACCEL", System.Math.Round(accel.Value * factor)));
            if (c.JerkXY.HasValue)
                args.Add(("SQUARE_CORNER_VELOCITY", System.Math.Round(c.JerkDialect(c.JerkXY.Value * factor), 2)));
            return args.Count == 0 ? null : KlipperCommands.Format(KlipperCommands.SetVelocityLimit, args.ToArray());
        }

        public static string Make(GcodeConfig c)
        {
            var h = new List<string>
            {
                "; --- BEGIN HEADER ---",
                "; PRINTSPEED=" + c.PrintSpeed.ToString(Inv),
                "; TRAVELSPEED=" + c.TravelSpeed.ToString(Inv),
                "; LAYERHEIGHT=" + c.LayerHeight.ToString(Inv),
                "; LINEWIDTH=" + c.Width.ToString(Inv),
            };
            h.Add("; RAMDIAMETER=" + c.RamDiameter.ToString(Inv));
            h.Add("; PRESSURECOEFF=" + c.PressureCoeff.ToString(Inv));
            h.Add("; FLAVOR=" + c.Flavor);
            h.Add("G21 ; mm");
            h.Add("G90 ; absolute XYZ");

            h.Add("G28 ; home all axes");
            h.Add(c.RelativeE ? "M83 ; relative E" : "M82 ; absolute E");

            bool klipper = c.Flavor == GcodeFlavor.Klipper;
            if (c.AccelPrint.HasValue && !klipper)
            {
                int ap = (int)System.Math.Round(c.AccelPrint.Value);
                int at = (int)System.Math.Round(c.AccelTravel ?? c.AccelPrint.Value);
                h.Add(string.Format(Inv, "M204 P{0} T{1} ; accel (clay: keep low/constant)", ap, at));
            }
            if (klipper)
            {
                // Klipper: one SET_VELOCITY_LIMIT replaces M201 + M204 + M205 (M204 and ACCEL
                // are the same toolhead field, so the print accel is folded in rather than
                // overwritten); the E axis has no per-axis ceiling and Z keeps printer.cfg's
                // max_z_accel, so those inputs are dropped with a note
                string? limit = KlipperVelocityLimit(c, 1.0);
                if (limit != null) h.Add(limit + " ; accel (print accel when set, else the XY limit) / square-corner velocity");
                if (c.AccelTravel.HasValue) h.Add("; Klipper has one acceleration for print and travel - accelTravel not emitted");
                if (c.AccelZ.HasValue) h.Add("; Klipper keeps printer.cfg max_z_accel - accelZ not emitted");
                if (c.AccelE.HasValue || c.JerkE.HasValue) h.Add("; Klipper has no per-axis E accel/jerk limit - accelE/jerkE not emitted");
            }
            else
            {
                // Per-axis accel (M201) + instantaneous-speed-change / jerk (M566 on RRF,
                // M205 on Marlin). Softening these tames the current spike a SCARA arm sees at
                // every direction change (100:1 reduction => big joint torque per mm/s^2).
                if (c.AccelXY.HasValue || c.AccelE.HasValue)
                {
                    string s = "M201";
                    if (c.AccelXY.HasValue)
                        s += string.Format(Inv, " X{0} Y{0} Z{1}",
                            (int)System.Math.Round(c.AccelXY.Value),
                            (int)System.Math.Round(c.AccelZ ?? c.AccelXY.Value));
                    if (c.AccelE.HasValue)
                        s += string.Format(Inv, " E{0}", (int)System.Math.Round(c.AccelE.Value));
                    h.Add(s + " ; max accel (SCARA + ram: soften)");
                }
                if (c.JerkXY.HasValue || c.JerkE.HasValue)
                {
                    bool duet = c.Flavor == GcodeFlavor.DuetRrf;
                    string s = duet ? "M566" : "M205";
                    if (c.JerkXY.HasValue)
                        s += string.Format(Inv, " X{0} Y{0} Z{1}",
                            (int)System.Math.Round(c.JerkDialect(c.JerkXY.Value)),
                            (int)System.Math.Round(c.JerkDialect(c.JerkZ ?? c.JerkXY.Value)));
                    if (c.JerkE.HasValue)
                        s += string.Format(Inv, " E{0}", (int)System.Math.Round(c.JerkDialect(c.JerkE.Value)));
                    h.Add(s + (duet ? " ; max instant speed change, mm/min (SCARA + ram: soften)"
                                    : " ; jerk, mm/s (SCARA + ram: soften)"));
                }
            }
            if (c.LinearAdvanceK.HasValue)
            {
                if (c.Flavor == GcodeFlavor.DuetRrf)
                    h.Add(string.Format(Inv, "M572 D0 S{0:0.000} ; pressure advance (clay: calibrate!)", c.LinearAdvanceK.Value));
                else if (klipper)
                    h.Add(KlipperCommands.Format(KlipperCommands.SetPressureAdvance, ("ADVANCE", c.LinearAdvanceK.Value)) + " ; pressure advance (calibrate!)");
                else
                    h.Add(string.Format(Inv, "M900 K{0:0.000} ; linear advance (clay: calibrate!)", c.LinearAdvanceK.Value));
            }
            h.Add("G92 E0");
            h.Add("; --- END HEADER ---");
            return string.Join("\n", h) + "\n";
        }

        public static string Footer(GcodeConfig c)
        {
            var f = new List<string> { "; --- BEGIN FOOTER ---", "G92 E0" };
            if (c.FooterZLift > 1e-9)
            {
                // relative lift so it clears the print regardless of the final Z; use the
                // hop speed when set, else the travel speed
                double liftSpeed = c.HopSpeed > 1e-9 ? c.HopSpeed : c.TravelSpeed;
                int lf = System.Math.Max(1, (int)System.Math.Round(liftSpeed * 60));
                f.Add("G91 ; relative");
                f.Add(string.Format(Inv, "G0 Z{0} F{1} ; lift clear of the print before homing",
                                    c.FooterZLift.ToString("0.###", Inv), lf));
                f.Add("G90 ; absolute");
            }
            f.Add("G28 ; home all axes");
            // Duet/RRF: M84 clears the homed flags, forcing a re-home after every print (row 5.8).
            if (c.Flavor != GcodeFlavor.DuetRrf) f.Add("M84 ; steppers off");
            f.Add("; --- END FOOTER ---");
            return string.Join("\n", f) + "\n";
        }
    }
}
