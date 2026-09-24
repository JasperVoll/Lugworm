using System;
using System.Collections.Generic;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    public sealed class FirmwareLimitFinding
    {
        public Severity Severity;
        public string Message = "";
        public override string ToString() => Message;
    }

    /// <summary>Cross-checks the configured software model against the limits and
    /// kinematic values reported by the firmware. Legacy rr_status polls leave
    /// Min/Max at 0 - those axes are unverified and are skipped here.</summary>
    public static class FirmwareLimitCheck
    {
        const double Tolerance = 0.5;

        public static List<FirmwareLimitFinding> CompareFindings(ObjectModelSnapshot snap, RobotLimits lim)
        {
            var findings = new List<FirmwareLimitFinding>();
            if (snap == null || lim == null) return findings;

            var z = snap.Axis("Z");
            if (z != null && z.Max > z.Min)
            {
                if (lim.ZMax > z.Max + Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Error,
                        Message = FormattableString.Invariant($"configured build height ZMax {lim.ZMax:0}mm exceeds the firmware's M208 Z limit {z.Max:0}mm - the board will clip or fault those moves")
                    });
                else if (z.Max > lim.ZMax + Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Info,
                        Message = FormattableString.Invariant($"firmware allows Z up to {z.Max:0}mm but ZMax is configured at {lim.ZMax:0}mm - tall prints are refused unnecessarily (informational)")
                    });
            }

            foreach (var axis in new[] { "X", "Y" })
            {
                var firmware = snap.Axis(axis);
                if (firmware == null || firmware.Max <= firmware.Min) continue;

                double softwareMin = axis == "X" ? lim.XMin : lim.YMin;
                double softwareMax = axis == "X" ? lim.XMax : lim.YMax;
                if (!lim.CheckEnvelope) continue;

                if (softwareMin < firmware.Min - Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Error,
                        Message = FormattableString.Invariant($"configured {axis} minimum {softwareMin:0}mm is below firmware M208 {axis} minimum {firmware.Min:0}mm - the board will clip or fault those moves")
                    });
                else if (firmware.Min < softwareMin - Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Info,
                        Message = FormattableString.Invariant($"firmware allows {axis} down to {firmware.Min:0}mm but {axis}Min is configured at {softwareMin:0}mm - the software is more restrictive (informational)")
                    });

                if (softwareMax > firmware.Max + Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Error,
                        Message = FormattableString.Invariant($"configured {axis} maximum {softwareMax:0}mm exceeds firmware M208 {axis} maximum {firmware.Max:0}mm - the board will clip or fault those moves")
                    });
                else if (firmware.Max > softwareMax + Tolerance)
                    findings.Add(new FirmwareLimitFinding
                    {
                        Severity = Severity.Info,
                        Message = FormattableString.Invariant($"firmware allows {axis} up to {firmware.Max:0}mm but {axis}Max is configured at {softwareMax:0}mm - the software is more restrictive (informational)")
                    });
            }
            return findings;
        }

        public static List<FirmwareLimitFinding> CompareKinematics(M669Parameters firmware, RobotLimits lim)
        {
            var findings = new List<FirmwareLimitFinding>();
            if (firmware == null || lim == null) return findings;

            AddMismatch(findings, "P/L1", firmware.P, lim.L1, "mm");
            AddMismatch(findings, "D/L2", firmware.D, lim.L2, "mm");
            AddMismatch(findings, "A/J1Min", firmware.AMin, lim.J1Min, "°");
            AddMismatch(findings, "A/J1Max", firmware.AMax, lim.J1Max, "°");
            AddMismatch(findings, "B/J3Min", firmware.BMin, lim.J3Min, "°");
            AddMismatch(findings, "B/J3Max", firmware.BMax, lim.J3Max, "°");
            // Only compare R when the firmware actually reported one. An unset R is
            // absent from the M669 report and from the object model; treating that
            // absence as 0 would flag a mismatch against every non-zero ReachMin and
            // block upload on a board whose kinematics are in fact correct.
            if (firmware.HasR)
            {
                double softwareReachMin = lim.ReachMin > 0 ? lim.ReachMin : Math.Abs(lim.L1 - lim.L2);
                AddMismatch(findings, "R/ReachMin", firmware.R, softwareReachMin, "mm");
            }
            return findings;
        }

        /// <summary>Compares the limits a job sets, and the per-axis speeds it
        /// requests, against the immutable config.g baseline. Motion-limit
        /// findings are never Error severity: configured raises are warnings,
        /// while feedrates above an intact M203 are informational because RRF
        /// clips them without changing the toolpath geometry.</summary>
        public static List<FirmwareLimitFinding> CompareMotion(FirmwareMotionLimits baseline,
                                                                JobMotionDemand job,
                                                                LiveMotionState? live)
        {
            var findings = new List<FirmwareLimitFinding>();
            if (baseline == null || job == null) return findings;

            CompareJobAxis(findings, "X", baseline.X, job.X);
            CompareJobAxis(findings, "Y", baseline.Y, job.Y);
            CompareJobAxis(findings, "Z", baseline.Z, job.Z);
            CompareJobAxis(findings, "E", baseline.E, job.E);
            CompareJobAcceleration(findings, "P", baseline.PrintAccel, job.PrintAccel);
            CompareJobAcceleration(findings, "T", baseline.TravelAccel, job.TravelAccel);

            CompareLiveAxis(findings, "X", baseline.X, live?.X);
            CompareLiveAxis(findings, "Y", baseline.Y, live?.Y);
            CompareLiveAxis(findings, "Z", baseline.Z, live?.Z);
            CompareLiveAxis(findings, "E", baseline.E, live?.E);
            if (live == null)
            {
                findings.Add(new FirmwareLimitFinding
                {
                    Severity = Severity.Info,
                    Message = "live motion state unverified - could not read M203/M201/M566"
                });
            }
            return findings;
        }

        static void CompareJobAxis(List<FirmwareLimitFinding> findings, string axis,
                                    FirmwareAxisLimits baseline, FirmwareAxisLimits job)
        {
            if (job.MaxSpeed.HasValue && baseline.MaxSpeed.HasValue
                && job.MaxSpeed.Value > baseline.MaxSpeed.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"job sets M203 {axis}{job.MaxSpeed.Value:0.###} mm/s but config.g configures {baseline.MaxSpeed.Value:0.###} mm/s - this machine's maximum speed was reduced deliberately"));

            if (job.Acceleration.HasValue && baseline.Acceleration.HasValue
                && job.Acceleration.Value > baseline.Acceleration.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"job sets M201 {axis}{job.Acceleration.Value:0.###} mm/s^2 but config.g configures {baseline.Acceleration.Value:0.###} mm/s^2 - this machine's acceleration was reduced deliberately"));

            if (job.Jerk.HasValue && baseline.Jerk.HasValue
                && job.Jerk.Value > baseline.Jerk.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"job sets M566/M205 {axis}{job.Jerk.Value:0.###} mm/s but config.g configures {baseline.Jerk.Value:0.###} mm/s - this machine's jerk was reduced deliberately"));

            if (job.EffectiveSpeed.HasValue && baseline.MaxSpeed.HasValue
                && job.EffectiveSpeed.Value > baseline.MaxSpeed.Value + Tolerance
                && (!job.MaxSpeed.HasValue || job.MaxSpeed.Value <= baseline.MaxSpeed.Value + Tolerance))
                AddMotionFinding(findings, Severity.Info,
                    FormattableString.Invariant($"requested {axis} speed {job.EffectiveSpeed.Value:0.###} mm/s exceeds the configured M203 {axis}{baseline.MaxSpeed.Value:0.###} mm/s - the board will clip this axis, affecting timing only, not geometry"));
        }

        static void CompareJobAcceleration(List<FirmwareLimitFinding> findings, string mode,
                                           double? baseline, double? job)
        {
            if (!baseline.HasValue || !job.HasValue || job.Value <= baseline.Value + Tolerance) return;
            findings.Add(new FirmwareLimitFinding
            {
                Severity = Severity.Warning,
                Message = FormattableString.Invariant($"job sets M204 {mode}{job.Value:0.###} mm/s^2 but config.g configures {baseline.Value:0.###} mm/s^2 - this machine's {ModeName(mode)} acceleration was reduced deliberately")
            });
        }

        static string ModeName(string mode) => mode == "P" ? "print" : "travel";

        static void CompareLiveAxis(List<FirmwareLimitFinding> findings, string axis,
                                    FirmwareAxisLimits baseline, FirmwareAxisLimits? live)
        {
            if (live == null) return;
            if (live.MaxSpeed.HasValue && baseline.MaxSpeed.HasValue
                && live.MaxSpeed.Value > baseline.MaxSpeed.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"live M203 {axis}{live.MaxSpeed.Value:0.###} mm/s is above config.g's {axis}{baseline.MaxSpeed.Value:0.###} mm/s - an earlier job or operator change raised this machine's maximum speed"));
            if (live.Acceleration.HasValue && baseline.Acceleration.HasValue
                && live.Acceleration.Value > baseline.Acceleration.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"live M201 {axis}{live.Acceleration.Value:0.###} mm/s^2 is above config.g's {axis}{baseline.Acceleration.Value:0.###} mm/s^2 - an earlier job or operator change raised this machine's acceleration"));
            if (live.Jerk.HasValue && baseline.Jerk.HasValue
                && live.Jerk.Value > baseline.Jerk.Value + Tolerance)
                AddMotionFinding(findings, Severity.Warning,
                    FormattableString.Invariant($"live M566 {axis}{live.Jerk.Value:0.###} mm/s is above config.g's {axis}{baseline.Jerk.Value:0.###} mm/s - an earlier job or operator change raised this machine's jerk"));
        }

        static void AddMotionFinding(List<FirmwareLimitFinding> findings, Severity severity, string message)
            => findings.Add(new FirmwareLimitFinding { Severity = severity, Message = message });

        static void AddMismatch(List<FirmwareLimitFinding> findings, string name,
                                double firmwareValue, double softwareValue, string unit)
        {
            if (Math.Abs(firmwareValue - softwareValue) <= Tolerance) return;
            findings.Add(new FirmwareLimitFinding
            {
                Severity = Severity.Error,
                Message = FormattableString.Invariant($"firmware M669 {name.Split('/')[0]} is {firmwareValue:0.###}{unit}, software {name.Split('/')[1]} is {softwareValue:0.###}{unit} - kinematics mismatch")
            });
        }

        /// <summary>Compatibility projection for existing UI callers. New code
        /// should use <see cref="CompareFindings"/> to retain severity.</summary>
        public static List<string> Compare(ObjectModelSnapshot snap, RobotLimits lim)
        {
            var result = new List<string>();
            foreach (var finding in CompareFindings(snap, lim)) result.Add(finding.Message);
            return result;
        }
    }
}
