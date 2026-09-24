using System;
using System.Collections.Generic;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Options for <see cref="NonPlanarInfill.Apply"/>.</summary>
    public sealed class NonPlanarOptions
    {
        /// <summary>Nominal layer height, mm. Required as the amplitude bound for a
        /// single-layer or completely flat stack; multi-layer stacks use actual gaps.</summary>
        public double LayerHeight;
        /// <summary>Peak Z excursion, mm. 0 disables the pass.</summary>
        public double Amplitude;
        /// <summary>Wave period along <see cref="DirectionDeg"/>, mm.</summary>
        public double Wavelength;
        /// <summary>Direction the wave travels, degrees CCW from +X. Normally the infill
        /// angle, so the wave runs along the rows rather than across them.</summary>
        public double DirectionDeg;
        /// <summary>Segment length the infill is resampled to before modulation, mm.
        /// The wave is only as smooth as this.</summary>
        public double Resolution = 1.0;
        /// <summary>Phase advance per layer, in turns. 0.5 puts the next layer's trough
        /// over this layer's crest, which is what interlocks them.</summary>
        public double PhasePerLayer = 0.5;
        /// <summary>Layers over which the amplitude ramps in at the bottom and out at the
        /// top. The first and last layer are always flat.</summary>
        public int RampLayers = 1;
        /// <summary>Largest amplitude, as a fraction of the layer height, that keeps two
        /// consecutive layers from intersecting.</summary>
        public double MaxStepFraction = 0.4;
        /// <summary>Sine, or a Graph Mapper curve. Required.</summary>
        public IWaveProfile? Profile;
        /// <summary>Peak wall Z excursion, mm. 0 leaves walls unchanged.</summary>
        public double WallAmplitude;
        /// <summary>Phase advance per wall loop, in turns.</summary>
        public double LoopPhaseTurns = 0.25;
        /// <summary>Whether wall index 0 is modulated.</summary>
        public bool IncludeOuterWall;
    }

    /// <summary>Wave-modulated ("non-planar") paths: selected infill and wall paths are
    /// resampled and displaced in Z by a periodic profile along one planar direction.
    /// Open sections are never touched.
    ///
    /// <para>The amplitude ramps in over the first <see cref="NonPlanarOptions.RampLayers"/>
    /// layers and out over the last, because this slicer has no solid top/bottom layers to
    /// taper against; the first and last layer are left exactly flat.</para>
    ///
    /// <para><b>No extrusion correction belongs here.</b> A tilted segment is longer than
    /// its XY shadow, but <c>GcodeBuilder</c> already measures every segment with
    /// <see cref="PolylineOps.Dist"/>, which is 3-D, so E already scales with the true
    /// length. Multiplying E by a segment-length ratio here would double-count it.</para>
    ///
    /// <para>Clean-room implementation; the idea comes from the GPL-3.0 "Bricklayers +
    /// NonPlanar infill" post-processor, no code from it is used - see
    /// THIRD-PARTY-NOTICES.md.</para></summary>
    public static class NonPlanarInfill
    {
        /// <summary>Upper bound on the thinning tolerance, mm. Well below what a printer
        /// resolves, so thinning cannot be seen in the part, but enough to drop the samples
        /// a long wavelength never needed.</summary>
        internal const double MaxThinTolerance = 0.02;

        /// <summary>Modulates selected infill and wall paths in place. Refuses - rather
        /// than silently clamping - amplitudes that could make two layers intersect.</summary>
        public static void Apply(IList<LayerPaths> stack, NonPlanarOptions o)
        {
            if (stack == null) throw new ArgumentNullException(nameof(stack));
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (!Num.Finite(o.LayerHeight) || o.LayerHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"non-planar infill needs a finite layer height > 0 (got {o.LayerHeight})");
            if (!Num.Finite(o.Amplitude) || o.Amplitude < 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"amplitude must be finite and >= 0 (got {o.Amplitude})");
            if (!Num.Finite(o.WallAmplitude) || o.WallAmplitude < 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"wall amplitude must be finite and >= 0 (got {o.WallAmplitude})");
            if (!Num.Finite(o.LoopPhaseTurns))
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"loop phase must be finite (got {o.LoopPhaseTurns})");
            bool infillActive = o.Amplitude > 0.0;
            bool wallsActive = o.WallAmplitude > 0.0;
            if (!infillActive && !wallsActive) return;
            if (!Num.Finite(o.Wavelength) || o.Wavelength <= 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"wavelength must be finite and > 0 (got {o.Wavelength})");
            if (!Num.Finite(o.Resolution) || o.Resolution <= 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"resolution must be finite and > 0 (got {o.Resolution})");
            if (!Num.Finite(o.MaxStepFraction) || o.MaxStepFraction <= 0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"maxStepFraction must be finite and > 0 (got {o.MaxStepFraction})");
            if (o.Profile == null)
                throw new ArgumentNullException(nameof(o), "non-planar modulation needs a wave profile");

            // The same rule for both amplitudes - what must not intersect is one path on two
            // consecutive layers - but the walls' gap is measured after any shift a stack
            // pass has applied: Bricklayer ramps a loop from flat to shifted and back, so at
            // those boundaries the real gap between the loop's two layers is half a layer.
            double limit = ActualAmplitudeLimit(stack, o, walls: false);
            if (infillActive && o.Amplitude > limit)
                throw new ArgumentOutOfRangeException(nameof(o),
                    FormattableString.Invariant($"amplitude {o.Amplitude:0.###} mm exceeds the {limit:0.###} mm limit from the actual layer gap ") +
                    FormattableString.Invariant($"and ramp ({o.MaxStepFraction:0.##} step fraction) - consecutive layers would intersect. ") +
                    "Lower the amplitude or raise the layer height.");
            if (wallsActive)
            {
                double wallLimit = ActualAmplitudeLimit(stack, o, walls: true);
                if (o.WallAmplitude > wallLimit)
                    throw new ArgumentOutOfRangeException(nameof(o),
                        FormattableString.Invariant($"wall amplitude {o.WallAmplitude:0.###} mm exceeds the {wallLimit:0.###} mm limit from the actual ") +
                        FormattableString.Invariant($"wall gap (layer gap less any bricklayer shift change) and ramp ({o.MaxStepFraction:0.##} step ") +
                        "fraction) - consecutive layers would intersect. Lower the wall amplitude, the bricklayer shift " +
                        "or raise the layer height.");
            }

            double rad = o.DirectionDeg * Math.PI / 180.0;
            double dx = Math.Cos(rad), dy = Math.Sin(rad);
            int n = stack.Count;

            for (int li = 0; li < n; li++)
            {
                var layer = stack[li];
                if (layer == null || layer.Paths.Count == 0) continue;
                double ramp = Ramp(li, n, o.RampLayers);
                if (ramp <= 0.0) continue;
                double infillAmplitude = o.Amplitude * ramp;
                double wallAmplitude = o.WallAmplitude * ramp;
                double layerPhase = o.PhasePerLayer * li;

                if (infillAmplitude > 0.0)
                    foreach (var path in layer.Paths)
                        if (path != null && path.Role == PathRole.Infill)
                            Modulate(path, infillAmplitude, layerPhase, o, dx, dy);

                if (wallAmplitude > 0.0)
                    foreach (var path in layer.Paths)
                        if (path != null && path.Role == PathRole.Wall
                            && (path.WallIndex != 0 || o.IncludeOuterWall)
                            && (path.Closed || PolylineOps.IsClosed(path.Points, 1e-3)))
                            Modulate(path, wallAmplitude,
                                     layerPhase + o.LoopPhaseTurns * Math.Max(0, path.WallIndex), o, dx, dy);
            }
        }

        static void Modulate(TaggedPath path, double amplitude, double phase,
                             NonPlanarOptions o, double dx, double dy)
        {
            var src = path.Points;
            if (src == null || src.Count < 2) return;
            bool closed = path.Closed || PolylineOps.IsClosed(src, 1e-3);
            path.Points = Thin(Samples(src, amplitude, phase, o, dx, dy, closed),
                               ThinTolerance(amplitude), closed);
            path.Closed = closed;
        }

        /// <summary>The modulated path at the full sampling
        /// <see cref="NonPlanarOptions.Resolution"/>, before thinning: one vertex per point
        /// the profile was evaluated at. Closure, when the source was closed, is exact.</summary>
        internal static List<Vec3> Samples(IReadOnlyList<Vec3> src, double amplitude, double phase,
                                           NonPlanarOptions o, double dx, double dy, bool closed)
        {
            var dense = Resample.ByLength(src, o.Resolution);
            var outp = new List<Vec3>(dense.Count);
            for (int i = 0; i < dense.Count; i++)
            {
                var p = dense[i];
                double s = p.X * dx + p.Y * dy;
                outp.Add(new Vec3(p.X, p.Y,
                    p.Z + amplitude * o.Profile!.At(s / o.Wavelength + phase)));
            }
            if (closed && outp.Count > 1) outp[outp.Count - 1] = outp[0];
            return outp;
        }

        /// <summary>Thinning tolerance for one amplitude, mm: a fiftieth of the wave, capped
        /// at <see cref="MaxThinTolerance"/>. It bounds the deviation of the thinned path, it
        /// is not a sampling step - the profile is still evaluated every
        /// <see cref="NonPlanarOptions.Resolution"/> mm and thinning only drops the vertices
        /// the profile did not bend.</summary>
        internal static double ThinTolerance(double amplitude)
            => Math.Min(MaxThinTolerance, amplitude / 50.0);

        /// <summary>Douglas-Peucker thinning of an already modulated path, measured in 3-D:
        /// <see cref="Rdp.SimplifyIndices"/> scores points with
        /// <see cref="PolylineOps.PointSegDist"/>, which is a 3-D point-to-segment distance,
        /// so the wave's Z excursion is what decides.
        ///
        /// <para>A closed loop is thinned as an OPEN polyline running from its seam back to
        /// itself, deliberately not through <see cref="Rdp.Simplify"/>: that entry point
        /// re-starts a ring at the vertex farthest from its centroid, which would move the
        /// seam the slicer placed. Here index 0 and the last index are pinned, so the seam
        /// and the exact closure both survive. A loop that would thin below three distinct
        /// vertices is returned unthinned rather than destroyed.</para></summary>
        internal static List<Vec3> Thin(List<Vec3> pts, double tol, bool closed)
        {
            if (pts == null || pts.Count < 3 || !(tol > 0.0)) return pts!;
            var keep = Rdp.SimplifyIndices(pts, tol);
            if (keep.Count >= pts.Count) return pts;
            if (closed && keep.Count < 4) return pts;
            var outp = new List<Vec3>(keep.Count);
            foreach (int i in keep) outp.Add(pts[i]);
            if (closed) outp[outp.Count - 1] = outp[0];
            return outp;
        }

        /// <summary>Amplitude scale for one layer: 0 at the very first and very last
        /// layer, rising linearly across <paramref name="ramp"/> layers at each end and
        /// 1 in between. <paramref name="ramp"/> &lt;= 0 leaves every layer at full
        /// amplitude except that there is then nothing to ramp.</summary>
        internal static double Ramp(int index, int count, int ramp)
        {
            if (ramp <= 0) return 1.0;
            if (count <= 1) return 0.0;
            // clamp so a ramp longer than half the object still meets in the middle
            int r = Math.Min(ramp, count / 2);
            if (r <= 0) return 0.0;
            double up = (double)index / r;
            double down = (double)(count - 1 - index) / r;
            double f = Math.Min(1.0, Math.Min(up, down));
            return f < 0.0 ? 0.0 : f;
        }

        /// <summary>Largest amplitude that keeps one path clear of itself on the next layer,
        /// over every adjacent pair, with the ramp factors of both layers. For walls the pair's
        /// gap is the real one (<see cref="WallGap"/>); for infill it is the level gap.</summary>
        static double ActualAmplitudeLimit(IList<LayerPaths> stack, NonPlanarOptions o, bool walls)
        {
            int n = stack.Count;
            if (n <= 1) return o.MaxStepFraction * o.LayerHeight;
            double limit = double.PositiveInfinity;
            bool anyActivePair = false;
            for (int i = 0; i + 1 < n; i++)
            {
                double gap = stack[i + 1].Level - stack[i].Level;
                if (!(gap > 0.0) || !Num.Finite(gap))
                    throw new ArgumentOutOfRangeException(nameof(stack),
                        $"non-planar infill needs finite, increasing layer levels (gap {i + 1}-{i + 2}: {gap})");
                if (walls)
                {
                    gap = WallGap(stack[i], stack[i + 1], gap, o);
                    if (gap <= 0.0)
                        throw new ArgumentOutOfRangeException(nameof(stack),
                            $"the wall loops of layers {i + 1} and {i + 2} already meet after the bricklayer shift " +
                            "- no room for a wall wave. Lower the bricklayer shift or raise the layer height.");
                }
                double factors = Ramp(i, n, o.RampLayers) + Ramp(i + 1, n, o.RampLayers);
                if (factors <= 0.0) continue;
                anyActivePair = true;
                limit = Math.Min(limit, 2.0 * o.MaxStepFraction * gap / factors);
            }
            // A completely flat ramp is still subject to the documented nominal bound;
            // it must not turn an absurd amplitude into an accepted configuration.
            return anyActivePair ? limit : o.MaxStepFraction * o.LayerHeight;
        }

        /// <summary>Smallest vertical gap between a wall loop this pass will modulate on
        /// <paramref name="below"/> and the loop of the same wall index on <paramref name="above"/>,
        /// after the shift a stack pass has already applied to each (<see cref="TaggedPath.ZShift"/>).
        /// Loops are matched by wall index, so on a multi-island layer the worst combination
        /// counts - conservative, never optimistic. Never more than the level gap.</summary>
        static double WallGap(LayerPaths below, LayerPaths above, double levelGap, NonPlanarOptions o)
        {
            var maxBelow = new Dictionary<int, double>();
            var minAbove = new Dictionary<int, double>();
            foreach (var p in below.Paths)
                if (ModulatedWall(p, o))
                    maxBelow[p.WallIndex] = maxBelow.TryGetValue(p.WallIndex, out double s) ? Math.Max(s, p.ZShift) : p.ZShift;
            foreach (var p in above.Paths)
                if (ModulatedWall(p, o))
                    minAbove[p.WallIndex] = minAbove.TryGetValue(p.WallIndex, out double s) ? Math.Min(s, p.ZShift) : p.ZShift;
            double gap = levelGap;
            foreach (var pair in maxBelow)
                if (minAbove.TryGetValue(pair.Key, out double shiftAbove))
                    gap = Math.Min(gap, levelGap + shiftAbove - pair.Value);
            return gap;
        }

        static bool ModulatedWall(TaggedPath p, NonPlanarOptions o)
            => p != null && p.Role == PathRole.Wall && (p.WallIndex != 0 || o.IncludeOuterWall);
    }
}
