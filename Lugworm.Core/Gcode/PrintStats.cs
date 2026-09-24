using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Gcode
{
    /// <summary>Toolpath / G-code statistics: lengths, extruded volume &amp; mass,
    /// kinematic time estimate, per-layer breakdown. Feed-aware when fed from parsed
    /// G-code (accelerations are not modelled - the estimate is a lower bound).</summary>
    public sealed class PrintStatsResult
    {
        public int Moves, Layers;
        public double TotalLenMm, ExtrudeLenMm, TravelLenMm;
        public double VolumeCm3, MassG;
        /// <summary>True when the volume came from the G-code E values (exact ram
        /// model) rather than the geometric w×h fallback.</summary>
        public bool VolumeFromE;
        public double TimeMin;
        /// <summary>Trapezoidal-profile estimate (0 when no acceleration was supplied).
        /// TimeMin stays the kinematic lower bound; this adds accel/decel ramps with a
        /// cos-angle junction-speed model, so it is the more realistic number.</summary>
        public double TimeAccelMin;
        /// <summary>Time with the robot's joint-speed ceilings applied (0 when no
        /// kinematic profile was supplied). Reported alongside the Cartesian numbers
        /// rather than replacing them, so the robot's cost is visible, not hidden.</summary>
        public double JointLimitedTimeMin;
        /// <summary>Segments whose feed the joint limits actually reduced.</summary>
        public int JointLimitedSegments;
        public double ZMinMm, ZMaxMm;
        public readonly List<(double z, double lenMm, double timeMin)> PerLayer = new List<(double, double, double)>();

        public string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("══ Print statistics ══");
            sb.AppendLine(FormattableString.Invariant($"moves            {Moves}"));
            sb.AppendLine(FormattableString.Invariant($"layers           {Layers}   (Z {F(ZMinMm)} … {F(ZMaxMm)} mm)"));
            sb.AppendLine(FormattableString.Invariant($"path length      {F(TotalLenMm / 1000)} m   (extrude {F(ExtrudeLenMm / 1000)} m, travel {F(TravelLenMm / 1000)} m)"));
            sb.AppendLine(FormattableString.Invariant($"extruded volume  {F(VolumeCm3)} cm³   ≈ {F(MassG)} g   ({(VolumeFromE ? "from E values, ram model" : "geometric w×h")})"));
            sb.AppendLine(TimeAccelMin > 0
                ? $"time estimate    {F(TimeAccelMin)} min   (trapezoidal accel model; kinematic lower bound {F(TimeMin)} min)"
                : $"time estimate    {F(TimeMin)} min   (no acceleration model - lower bound)");
            if (JointLimitedTimeMin > 0 && JointLimitedSegments > 0)
            {
                double baseline = TimeAccelMin > 0 ? TimeAccelMin : TimeMin;
                sb.AppendLine(FormattableString.Invariant($"robot-limited    {F(JointLimitedTimeMin)} min   ({JointLimitedSegments} segment(s) capped by joint speed; {(baseline > 1e-9 ? (JointLimitedTimeMin / baseline - 1.0) * 100.0 : 0):+0.0;-0.0;0.0}% vs Cartesian)"));
            }
            if (PerLayer.Count > 0 && PerLayer.Count <= 60)
            {
                sb.AppendLine(FormattableString.Invariant($"{"layer Z (mm)",12}{"length (mm)",14}{"time (min)",12}"));
                foreach (var (z, len, t) in PerLayer)
                    sb.AppendLine(FormattableString.Invariant($"{F(z),12}{F(len),14}{F(t),12}"));
            }
            return sb.ToString();
        }

        static string F(double v) => v.ToString("0.0#", CultureInfo.InvariantCulture);
    }

    public static class PrintStatsCalc
    {
        /// <summary>Stats from parsed G-code (per-move feeds honoured). When the file
        /// carries E values and <paramref name="ramDiameterMm"/> &gt; 0, the extruded
        /// volume is computed EXACTLY from ΣE⁺ - ram area - the same ram model the
        /// G-code generator uses, so both components report the same volume.</summary>
        public static PrintStatsResult FromMoves(IReadOnlyList<ParsedMove> moves,
            double layerWidthMm, double layerHeightMm, double densityGCm3 = 2.0,
            double defaultFeedMmS = 25.0, double ramDiameterMm = 0, double accelMmS2 = 0,
            IReadOnlyList<int>? layerIds = null, JointSpeedLimits? jointLimits = null,
            double? defaultTravelFeedMmS = null)
        {
            var r = new PrintStatsResult { Moves = moves.Count };
            if (moves.Count == 0) return r;
            var layerLen = new SortedDictionary<double, (double len, double time)>();
            var accelPts = new List<Vec3>();
            var accelExt = new List<bool>();
            var accelFeeds = new List<double>();
            double timeS = 0.0;
            bool limiting = jointLimits != null && jointLimits.Active;
            double jointTimeS = 0.0;
            r.ZMinMm = double.MaxValue; r.ZMaxMm = double.MinValue;
            for (int mi = 0; mi < moves.Count; mi++)
            {
                var parsed = moves[mi];
                var p0 = new Vec3(parsed.StartX, parsed.StartY, parsed.StartZ);
                var p1 = new Vec3(parsed.X, parsed.Y, parsed.Z);
                Move move = Move.Line(p0, p1);
                if (parsed.Arc)
                {
                    double cx = p0.X + parsed.I, cy = p0.Y + parsed.J;
                    double radius = Math.Sqrt((p0.X - cx) * (p0.X - cx) + (p0.Y - cy) * (p0.Y - cy));
                    if (radius > 1e-9 && !double.IsNaN(radius))
                        move = Move.Arc(p0, p1, cx, cy, parsed.G == 2, radius);
                }
                double len = ArcTessellation.Length(move);
                if (len < 1e-9) continue;
                // a travel without an F word takes the caller's travel feed when it gave one
                double feed = parsed.F > 0 ? parsed.F / 60.0
                    : (parsed.Extrude ? defaultFeedMmS : defaultTravelFeedMmS ?? defaultFeedMmS * 2.0);
                double t = len / Math.Max(feed, 0.1);
                timeS += t;
                if (limiting)
                {
                    // Joint samples include the start plus one endpoint per parsed move.
                    // This keeps the first commanded segment inside the robot model.
                    double capped = jointLimits!.CapFeed(mi, mi + 1, len, feed);
                    if (capped < feed - 1e-9) r.JointLimitedSegments++;
                    jointTimeS += len / Math.Max(capped, 0.1);
                    feed = capped;
                }
                r.TotalLenMm += len;
                r.ZMinMm = Math.Min(r.ZMinMm, Math.Min(p0.Z, p1.Z));
                r.ZMaxMm = Math.Max(r.ZMaxMm, Math.Max(p0.Z, p1.Z));
                if (parsed.Extrude)
                {
                    r.ExtrudeLenMm += len;
                    double zKey = layerIds != null && mi < layerIds.Count
                        ? layerIds[mi] : Math.Round(p1.Z, 2);
                    layerLen.TryGetValue(zKey, out var cur);
                    layerLen[zKey] = (cur.len + len, cur.time + t / 60.0);
                }
                else r.TravelLenMm += len;

                if (accelMmS2 > 0)
                {
                    var poly = ArcTessellation.ToPolyline(move, 0.05, 4096);
                    if (accelPts.Count == 0) { accelPts.Add(poly[0]); accelExt.Add(false); accelFeeds.Add(feed); }
                    for (int pi = 1; pi < poly.Count; pi++)
                    { accelPts.Add(poly[pi]); accelExt.Add(parsed.Extrude); accelFeeds.Add(feed); }
                }
            }
            r.TimeMin = timeS / 60.0;
            if (accelMmS2 > 0 && accelPts.Count > 1)
                r.TimeAccelMin = TrapezoidTimeS(accelPts, accelExt, accelFeeds,
                                                defaultFeedMmS, defaultFeedMmS * 2.0, accelMmS2) / 60.0;
            if (limiting)
                r.JointLimitedTimeMin = accelMmS2 > 0 && accelPts.Count > 1
                    ? TrapezoidTimeS(accelPts, accelExt, accelFeeds,
                                     defaultFeedMmS, defaultFeedMmS * 2.0, accelMmS2) / 60.0
                    : jointTimeS / 60.0;
            r.Layers = layerLen.Count;
            foreach (var kv in layerLen) r.PerLayer.Add((kv.Key, kv.Value.len, kv.Value.time));
            double w = Math.Max(layerWidthMm, 1e-3), h = Math.Max(layerHeightMm, 1e-3);
            r.VolumeCm3 = r.ExtrudeLenMm * w * h / 1000.0;
            r.MassG = r.VolumeCm3 * densityGCm3;
            double ePlus = moves.Sum(m => Math.Max(0, m.E));
            if (ramDiameterMm > 0 && ePlus > 1e-9)
            {
                double ramArea = Math.PI * ramDiameterMm * ramDiameterMm / 4.0;   // mm²
                r.VolumeCm3 = ePlus * ramArea / 1000.0;
                r.MassG = r.VolumeCm3 * densityGCm3;
                r.VolumeFromE = true;
            }
            return r;
        }

        /// <param name="ext">ext[k] = segment ending at point k extrudes</param>
        /// <param name="travelFeedMmS">feed for non-extruding segments (0 → default)</param>
        public static PrintStatsResult FromPoints(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> ext,
            double layerWidthMm, double layerHeightMm, double densityGCm3 = 2.0,
            double defaultFeedMmS = 25.0, double travelFeedMmS = 0,
            IReadOnlyList<double>? feedsMmS = null, double accelMmS2 = 0,
            IReadOnlyList<int>? layerIds = null, JointSpeedLimits? jointLimits = null)
        {
            var r = new PrintStatsResult { Moves = pts.Count };
            if (pts.Count < 2) return r;
            if (travelFeedMmS <= 0) travelFeedMmS = defaultFeedMmS * 2;

            var layerLen = new SortedDictionary<double, (double len, double time)>();
            double timeS = 0;
            bool limiting = jointLimits != null && jointLimits.Active;
            double jointTimeS = 0.0;
            List<double>? jointFeeds = limiting
                ? Enumerable.Repeat(0.0, pts.Count).ToList() : null;
            r.ZMinMm = double.MaxValue; r.ZMaxMm = double.MinValue;
            foreach (var p in pts) { r.ZMinMm = Math.Min(r.ZMinMm, p.Z); r.ZMaxMm = Math.Max(r.ZMaxMm, p.Z); }

            for (int k = 1; k < pts.Count; k++)
            {
                double dx = pts[k].X - pts[k - 1].X, dy = pts[k].Y - pts[k - 1].Y, dz = pts[k].Z - pts[k - 1].Z;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < 1e-9) continue;
                bool e = k < ext.Count && ext[k];
                double feed = feedsMmS != null && k < feedsMmS.Count && feedsMmS[k] > 0
                    ? feedsMmS[k]
                    : e ? defaultFeedMmS : travelFeedMmS;
                double t = d / Math.Max(feed, 0.1);
                timeS += t;
                if (limiting)
                {
                    double capped = jointLimits!.CapFeed(k - 1, k, d, feed);
                    if (capped < feed - 1e-9) r.JointLimitedSegments++;
                    jointTimeS += d / Math.Max(capped, 0.1);
                    jointFeeds![k] = capped;
                }
                r.TotalLenMm += d;
                if (e)
                {
                    r.ExtrudeLenMm += d;
                    double zKey = layerIds != null && k < layerIds.Count
                        ? layerIds[k] : Math.Round(pts[k].Z, 2);
                    layerLen.TryGetValue(zKey, out var cur);
                    layerLen[zKey] = (cur.len + d, cur.time + t / 60.0);
                }
                else r.TravelLenMm += d;
            }

            r.TimeMin = timeS / 60.0;
            if (accelMmS2 > 0)
                r.TimeAccelMin = TrapezoidTimeS(pts, ext, feedsMmS, defaultFeedMmS, travelFeedMmS, accelMmS2) / 60.0;
            if (limiting)
                r.JointLimitedTimeMin = accelMmS2 > 0
                    ? TrapezoidTimeS(pts, ext, jointFeeds, defaultFeedMmS, travelFeedMmS, accelMmS2) / 60.0
                    : jointTimeS / 60.0;
            r.Layers = layerLen.Count;
            foreach (var kv in layerLen) r.PerLayer.Add((kv.Key, kv.Value.len, kv.Value.time));
            // geometric fallback: w×h rectangular cross-section - the same layer model the
            // G-code generator's E calculation uses, so the two components agree
            double w = Math.Max(layerWidthMm, 1e-3), h = Math.Max(layerHeightMm, 1e-3);
            r.VolumeCm3 = r.ExtrudeLenMm * w * h / 1000.0;
            r.MassG = r.VolumeCm3 * densityGCm3;
            return r;
        }

        /// <summary>Trapezoidal per-segment time (seconds). Junction-speed
        /// simplification: the carry-over speed between two segments is
        /// min(v_k, v_k+1) - max(0, cos θ) - full speed through straight joints,
        /// full stop at reversals/right angles. First entry / last exit are 0.
        /// Segments too short to reach the target speed get a triangular profile.</summary>
        internal static double TrapezoidTimeS(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> ext,
            IReadOnlyList<double>? feedsMmS, double defaultFeedMmS, double travelFeedMmS, double a)
        {
            double timeS = 0;
            foreach (double t in TrapezoidSegmentTimesS(pts, ext, feedsMmS, defaultFeedMmS, travelFeedMmS, a))
                timeS += t;
            return timeS;
        }

        /// <summary>Per-segment trapezoidal times (seconds), segment k running from
        /// pts[k] to pts[k+1]. The same model as <see cref="TrapezoidTimeS"/>, which is
        /// its sum; exposed so a timeline can place every point on the clock instead of
        /// only knowing the total.</summary>
        public static double[] TrapezoidSegmentTimesS(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> ext,
            IReadOnlyList<double>? feedsMmS, double defaultFeedMmS, double travelFeedMmS, double a)
        {
            int nSeg = pts.Count - 1;
            if (nSeg < 1) return Array.Empty<double>();
            var segTimes = new double[nSeg];
            var len = new double[nSeg];
            var dir = new Vec3[nSeg];
            var vTgt = new double[nSeg];
            for (int k = 0; k < nSeg; k++)
            {
                double dx = pts[k + 1].X - pts[k].X, dy = pts[k + 1].Y - pts[k].Y, dz = pts[k + 1].Z - pts[k].Z;
                len[k] = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                dir[k] = len[k] > 1e-12 ? new Vec3(dx / len[k], dy / len[k], dz / len[k]) : new Vec3(0, 0, 0);
                bool e = k + 1 < ext.Count && ext[k + 1];
                vTgt[k] = feedsMmS != null && k + 1 < feedsMmS.Count && feedsMmS[k + 1] > 0
                    ? feedsMmS[k + 1]
                    : e ? defaultFeedMmS : travelFeedMmS;
                vTgt[k] = Math.Max(vTgt[k], 0.1);
            }

            // junction speeds: vj[k] = carry-over speed entering segment k
            var vj = new double[nSeg + 1];
            vj[0] = 0; vj[nSeg] = 0;
            for (int k = 1; k < nSeg; k++)
            {
                double cos = dir[k - 1].X * dir[k].X + dir[k - 1].Y * dir[k].Y + dir[k - 1].Z * dir[k].Z;
                vj[k] = Math.Min(vTgt[k - 1], vTgt[k]) * Math.Max(0.0, cos);
            }
            // forward/backward passes so ve/vx are actually attainable within a×d
            for (int k = 0; k < nSeg; k++)
                vj[k + 1] = Math.Min(vj[k + 1], Math.Sqrt(vj[k] * vj[k] + 2 * a * len[k]));
            for (int k = nSeg - 1; k >= 0; k--)
                vj[k] = Math.Min(vj[k], Math.Sqrt(vj[k + 1] * vj[k + 1] + 2 * a * len[k]));

            for (int k = 0; k < nSeg; k++)
            {
                double d = len[k];
                if (d < 1e-9) continue;
                double v = vTgt[k], ve = Math.Min(vj[k], v), vx = Math.Min(vj[k + 1], v);
                double dAcc = (v * v - ve * ve) / (2 * a);
                double dDec = (v * v - vx * vx) / (2 * a);
                if (dAcc + dDec <= d)
                {
                    segTimes[k] = (v - ve) / a + (v - vx) / a + (d - dAcc - dDec) / v;
                }
                else
                {
                    // triangular: peak speed the distance allows
                    double vp = Math.Sqrt(Math.Max((2 * a * d + ve * ve + vx * vx) / 2.0, Math.Max(ve * ve, vx * vx)));
                    segTimes[k] = (vp - ve) / a + (vp - vx) / a;
                }
            }
            return segTimes;
        }
    }
}
