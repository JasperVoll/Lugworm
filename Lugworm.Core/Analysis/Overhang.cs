using System;
using System.Collections.Generic;
using System.Globalization;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Analysis
{
    /// <summary>Tunables for the overhang analysis.
    /// Threshold defaults are PLACEHOLDERS for extrusion-based concrete printing
    /// (open question P2 of the 2026-07 refinement notes, marker NEEDS_INPUT): printable overhang depends on
    /// the mix's early yield-stress evolution, layer time, and layer geometry.
    /// Published 3DCP wall-inclination studies typically report safe unsupported
    /// inclinations in the 15–35° range and collapse well before 60°; 25°/45° are
    /// conservative engineering defaults chosen inside that envelope - CALIBRATE
    /// them per material with a staircase-cone test print.</summary>
    public sealed class OverhangOptions
    {
        /// <summary>Warning threshold, degrees from the local build direction. NEEDS_INPUT.</summary>
        public double WarnDeg = 25.0;
        /// <summary>Failure threshold, degrees. NEEDS_INPUT.</summary>
        public double FailDeg = 45.0;
        /// <summary>Contour-point window for the local layer-plane (Newell) normal.</summary>
        public int NormalWindow = 15;
        /// <summary>Which angle the thresholds classify: the local (layer-relative) one
        /// by default, or the global (world-Z) one. Both angles are always computed.</summary>
        public OverhangReference Reference = OverhangReference.Local;
    }

    public enum OverhangClass { Safe, Warning, Critical }

    /// <summary>The build direction an overhang angle is measured from. Local: the
    /// layer's own surface normal, so the angle is the layer-on-layer stagger within the
    /// stack - what limits printability on non-planar layers. Global: world Z, so the
    /// angle is the lean against gravity - for a mix whose failure mode is the gravity
    /// load rather than the stagger. The two coincide on planar layers.</summary>
    public enum OverhangReference { Local, Global }

    /// <summary>The one definition of the overhang colours: the component paints the
    /// toolpath with them and its legend wire names them, so the viewport and the
    /// Legend component cannot disagree. Both readings sample matplotlib magma - the
    /// three classes at fixed positions, the continuous mode along the whole ramp from
    /// 0° to the critical threshold - so a class colour and the ramp colour at the same
    /// angle belong to the same family.</summary>
    public static class OverhangColors
    {
        /// <summary>Where the three classes sit on the magma ramp: deep purple, red-orange, light orange.</summary>
        public const double SafeT = 0.20, WarningT = 0.55, CriticalT = 0.85;

        public static readonly Rgb Safe = ColorSchemes.At(ColorSchemes.Magma, null, SafeT);
        public static readonly Rgb Warning = ColorSchemes.At(ColorSchemes.Magma, null, WarningT);
        public static readonly Rgb Critical = ColorSchemes.At(ColorSchemes.Magma, null, CriticalT);

        public static Rgb Of(OverhangClass c)
            => c == OverhangClass.Critical ? Critical : c == OverhangClass.Warning ? Warning : Safe;

        /// <summary>Legend title per reference: the local reading keeps the plain name,
        /// the global one says so, because the same part reads differently under each.</summary>
        public static string Title(OverhangReference reference)
            => reference == OverhangReference.Global ? "overhang (global)" : "overhang";

        /// <summary>The legend that explains an overhang analysis in class mode: three
        /// classes at the warning and critical thresholds, degrees from the local build
        /// direction (or from world Z under the global reference).</summary>
        public static LegendSpec Legend(double warnDeg, double failDeg, OverhangReference reference = OverhangReference.Local)
            => new LegendSpec(LegendType.Classes, title: Title(reference), unit: "°",
                              min: 0, max: 90, scheme: ColorSchemes.Custom,
                              colors: new[] { Safe, Warning, Critical },
                              breaks: new[] { warnDeg, Math.Max(failDeg, warnDeg) },
                              labels: new[] { "safe", "warning", "critical" }, decimals: 0);

        /// <summary>The critical threshold the continuous ramp saturates at: fail lifted
        /// to warn like the component does, and never zero (a flat domain).</summary>
        public static double ContinuousMax(double warnDeg, double failDeg)
        {
            double fail = Math.Max(failDeg, warnDeg);
            return double.IsNaN(fail) || fail <= 0 ? 1.0 : fail;
        }

        /// <summary>Colour of an angle in continuous mode: magma from 0° (dark) to the
        /// critical threshold (light), saturating above it; NaN is the grey unavailable colour.</summary>
        public static Rgb Continuous(double angleDeg, double warnDeg, double failDeg)
        {
            if (double.IsNaN(angleDeg) || double.IsInfinity(angleDeg)) return Rgb.Of(PlasmaColorScale.Unavailable);
            return ColorSchemes.At(ColorSchemes.Magma, null, Math.Max(0, angleDeg) / ContinuousMax(warnDeg, failDeg));
        }

        /// <summary>The legend for continuous mode: a magma ramp over 0..critical with the
        /// two thresholds marked, so the reader still sees where the classes would fall.</summary>
        public static LegendSpec ContinuousLegend(double warnDeg, double failDeg, OverhangReference reference = OverhangReference.Local)
        {
            double max = ContinuousMax(warnDeg, failDeg);
            return new LegendSpec(LegendType.Continuous, title: Title(reference), unit: "°",
                                  min: 0, max: max, scheme: ColorSchemes.Magma,
                                  marks: new[] { (Math.Min(warnDeg, max), "warning"), (max, "critical") },
                                  decimals: 0);
        }
    }

    /// <summary>Overhang of one toolpath segment relative to the layer below.</summary>
    public sealed class OverhangSegment
    {
        public int Layer;
        public int Contour;
        public int Segment;
        public Vec3 A, B;
        /// <summary>PRIMARY: angle between the support vector and the LOCAL layer
        /// normal (staircase offset within the layer stack). Equals the world-Z angle
        /// for planar layers; diverges for non-planar (conical) layers.</summary>
        public double AngleLayerDeg;
        /// <summary>SECONDARY: angle between the support vector and world Z
        /// (gravity-relative).</summary>
        public double AngleWorldDeg;
        /// <summary>Distance to the nearest point on the layer below, mm.</summary>
        public double SupportDist;
        /// <summary>Class of the angle the analysis was asked to classify (<see cref="OverhangOptions.Reference"/>).</summary>
        public OverhangClass Class;

        /// <summary>The angle under a reference: layer-relative (local) or world-Z (global).</summary>
        public double AngleDeg(OverhangReference reference)
            => reference == OverhangReference.Global ? AngleWorldDeg : AngleLayerDeg;
    }

    public sealed class OverhangLayerSummary
    {
        public int Layer;
        public int SegmentCount;
        public double MaxLayerDeg;
        public double MeanLayerDeg;
        public double MaxWorldDeg;
        public double PctWarning;    // % of segments >= warn (including critical)
        public double PctCritical;   // % of segments >= fail
    }

    public sealed class OverhangResult
    {
        public List<OverhangSegment> Segments = new List<OverhangSegment>();
        public List<OverhangLayerSummary> Layers = new List<OverhangLayerSummary>();
        public double WarnDeg, FailDeg;
        /// <summary>The angle the classes (and so pct_warning / pct_critical) were decided on.</summary>
        public OverhangReference Reference;

        /// <summary>Per-layer summary as CSV lines (with header). Under the global
        /// reference a leading comment line says so, because the columns keep their
        /// names while the two percentage columns then count by the world angle.</summary>
        public List<string> CsvLines()
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>(Layers.Count + 2);
            if (Reference == OverhangReference.Global)
                lines.Add("# reference: global (world Z) - pct_warning and pct_critical count by max_world_deg's angle");
            lines.Add("layer,segments,max_overhang_deg,mean_overhang_deg,max_world_deg,pct_warning,pct_critical");
            foreach (var l in Layers)
                lines.Add(string.Join(",",
                    l.Layer.ToString(inv), l.SegmentCount.ToString(inv),
                    l.MaxLayerDeg.ToString("0.0", inv), l.MeanLayerDeg.ToString("0.0", inv),
                    l.MaxWorldDeg.ToString("0.0", inv),
                    l.PctWarning.ToString("0.0", inv), l.PctCritical.ToString("0.0", inv)));
            return lines;
        }
    }

    /// <summary>Per-segment overhang analysis of a layered toolpath (planar or
    /// non-planar). Pure - geometry in, numbers out; the GH component adds the
    /// viewport colouring. The result feeds future slicing-parameter adaptation
    /// through OverhangResult (deliberately no adaptation logic here).</summary>
    public static class Overhang
    {
        /// <summary>Analyze one object's layers (layer → contours → polyline points).
        /// Layer 0 is bed-supported: all segments Safe/0°.</summary>
        public static OverhangResult Analyze(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> layers, OverhangOptions opt)
        {
            if (opt == null) opt = new OverhangOptions();
            if (double.IsNaN(opt.WarnDeg) || double.IsInfinity(opt.WarnDeg) || opt.WarnDeg < 0 || opt.WarnDeg > 90
                || double.IsNaN(opt.FailDeg) || double.IsInfinity(opt.FailDeg) || opt.FailDeg < opt.WarnDeg || opt.FailDeg > 90)
                throw new ArgumentException("overhang thresholds must be finite, ordered, and within 0..90 degrees");
            if (opt.NormalWindow < 1) throw new ArgumentException("overhang NormalWindow must be >= 1");
            var res = new OverhangResult { WarnDeg = opt.WarnDeg, FailDeg = opt.FailDeg, Reference = opt.Reference };

            SegmentGrid? reusableBelow = null;
            for (int li = 0; li < layers.Count; li++)
            {
                // spatial index over the layer below: point-vs-all-segments per segment
                // midpoint measured 3.9 s on a 30x2000 model before, ~0.3 s with the
                // grid. Cells sized to the layer gap (the expected support distance)
                // keep the shell search shallow.
                SegmentGrid? below = null;
                if (li > 0)
                {
                    double cell = 0.0;
                    var cur = FirstPoint(layers[li]);
                    var prev = FirstPoint(layers[li - 1]);
                    if (cur.HasValue && prev.HasValue)
                    {
                        double dz = Math.Abs(cur.Value.Z - prev.Value.Z);
                        if (dz > 1e-9) cell = 2.0 * dz;
                    }
                    if (reusableBelow == null) reusableBelow = new SegmentGrid(layers[li - 1], cell);
                    else reusableBelow.Rebuild(layers[li - 1], cell);
                    below = reusableBelow;
                }
                var layerSegs = new List<OverhangSegment>();
                for (int ci = 0; ci < layers[li].Count; ci++)
                {
                    var pts = layers[li][ci];
                    bool closed = pts.Count >= 3 && PolylineOps.Dist(pts[0], pts[pts.Count - 1]) < 1e-9;
                    for (int si = 0; si + 1 < pts.Count; si++)
                    {
                        var seg = new OverhangSegment
                        {
                            Layer = li,
                            Contour = ci,
                            Segment = si,
                            A = pts[si],
                            B = pts[si + 1],
                        };
                        if (li == 0)
                        {
                            seg.SupportDist = 0.0;
                            seg.Class = OverhangClass.Safe;
                        }
                        else
                        {
                            var mid = PolylineOps.Lerp(pts[si], pts[si + 1], 0.5);
                            var (q, dist) = below!.Nearest(mid);
                            var s = new Vec3(mid.X - q.X, mid.Y - q.Y, mid.Z - q.Z);
                            seg.SupportDist = dist;
                            seg.AngleWorldDeg = AngleDeg(s, new Vec3(0, 0, 1));
                            var n = LocalLayerNormal(pts, si, closed, opt.NormalWindow, s);
                            seg.AngleLayerDeg = AngleDeg(s, n);
                            double angle = seg.AngleDeg(opt.Reference);
                            seg.Class = angle >= opt.FailDeg ? OverhangClass.Critical
                                      : angle >= opt.WarnDeg ? OverhangClass.Warning
                                      : OverhangClass.Safe;
                        }
                        layerSegs.Add(seg);
                    }
                }

                var sum = new OverhangLayerSummary { Layer = li, SegmentCount = layerSegs.Count };
                if (layerSegs.Count > 0)
                {
                    double tot = 0; int warnN = 0, failN = 0;
                    foreach (var s in layerSegs)
                    {
                        sum.MaxLayerDeg = Math.Max(sum.MaxLayerDeg, s.AngleLayerDeg);
                        sum.MaxWorldDeg = Math.Max(sum.MaxWorldDeg, s.AngleWorldDeg);
                        tot += s.AngleLayerDeg;
                        if (s.Class != OverhangClass.Safe) warnN++;
                        if (s.Class == OverhangClass.Critical) failN++;
                    }
                    sum.MeanLayerDeg = tot / layerSegs.Count;
                    sum.PctWarning = 100.0 * warnN / layerSegs.Count;
                    sum.PctCritical = 100.0 * failN / layerSegs.Count;
                }
                res.Layers.Add(sum);
                res.Segments.AddRange(layerSegs);
            }
            return res;
        }

        static Vec3? FirstPoint(IReadOnlyList<IReadOnlyList<Vec3>> layer)
        {
            foreach (var c in layer) if (c != null && c.Count > 0) return c[0];
            return null;
        }

        /// <summary>Local layer-surface normal at contour point index: Newell normal of a
        /// window of contour points (the contour lies in the layer surface, so where it
        /// curves, its plane IS the layer plane). Straight windows are rank-deficient -
        /// fall back to world Z (exact for planar layers, approximate for non-planar).
        /// The sign is chosen so the normal points with the support vector (upwards
        /// through the stack).</summary>
        static Vec3 LocalLayerNormal(IReadOnlyList<Vec3> pts, int si, bool closed, int window, Vec3 towards)
        {
            int n = pts.Count - (closed ? 1 : 0);   // unique points
            if (n < 3) return Unit(towards, new Vec3(0, 0, 1));
            int half = Math.Max(1, window / 2);
            var w = new List<Vec3>(2 * half + 2);
            for (int k = -half; k <= half + 1; k++)
            {
                int idx = si + k;
                if (closed) idx = ((idx % n) + n) % n;
                else idx = Math.Max(0, Math.Min(pts.Count - 1, idx));
                w.Add(pts[idx]);
            }
            // Newell's method
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < w.Count; i++)
            {
                var c = w[i];
                var d = w[(i + 1) % w.Count];
                nx += (c.Y - d.Y) * (c.Z + d.Z);
                ny += (c.Z - d.Z) * (c.X + d.X);
                nz += (c.X - d.X) * (c.Y + d.Y);
            }
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            // rank-deficient (straight run) -> world Z
            if (len < 1e-9) return new Vec3(0, 0, 1);
            var nrm = new Vec3(nx / len, ny / len, nz / len);
            // orient along the stacking direction
            if (nrm.X * towards.X + nrm.Y * towards.Y + nrm.Z * towards.Z < 0)
                nrm = new Vec3(-nrm.X, -nrm.Y, -nrm.Z);
            return nrm;
        }

        static Vec3 Unit(Vec3 v, Vec3 fallback)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return len < 1e-12 ? fallback : new Vec3(v.X / len, v.Y / len, v.Z / len);
        }

        static double AngleDeg(Vec3 a, Vec3 b)
        {
            double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
            double lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
            if (la < 1e-12 || lb < 1e-12) return 0.0;
            double cos = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
            cos = Math.Max(-1.0, Math.Min(1.0, cos));
            return Math.Acos(cos) * 180.0 / Math.PI;
        }
    }
}
