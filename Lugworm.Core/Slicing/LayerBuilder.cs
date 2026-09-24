using System;
using System.Collections.Generic;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;

namespace Lugworm.Core.Slicing
{
    /// <summary>Options for <see cref="LayerBuilder"/>'s tagged and flattening builds -
    /// replaces the eight positional overloads that used to exist (H2), which differed
    /// only in the presence of wallSpacing/infillSpacing/waveProfile/wave and would have
    /// doubled again for every future option.</summary>
    public sealed class LayerBuildOptions
    {
        /// <summary>Z the walls/infill are built at. Planar: the layer's own Z. Conical:
        /// the level the section was cut at in the sheared frame (see
        /// <see cref="LayerBuilder.BuildConicalTagged(IReadOnlyList{IReadOnlyList{Vec3}}, LayerBuildOptions)"/>).</summary>
        public double Level;
        /// <summary>Number of wall loops, 0 = outermost. At least one wall is always built.</summary>
        public int WallCount = 1;
        /// <summary>0 none / 1 zigzag / 2 concentric / 3 spiral / 4 fermat / 5 wave-sine / 6 wave-graph.</summary>
        public int Infill = 0;
        /// <summary>Offset between wall loops and from the last wall to the infill boundary, mm.</summary>
        public double WallSpacing;
        /// <summary>Pitch within the infill, mm. Left at its default (0 or NaN), it is
        /// treated as equal to <see cref="WallSpacing"/> - the rule the removed
        /// single-spacing overloads applied by passing the same value twice.</summary>
        public double InfillSpacing = double.NaN;
        /// <summary>Infill/zigzag/wave direction, degrees.</summary>
        public double AngleDeg;
        /// <summary>Wave profile for infill modes 5 and 6; ignored otherwise. A null profile
        /// on a wave mode falls back to a plain sine rather than dropping the infill.</summary>
        public IWaveProfile? WaveProfile;
        /// <summary>Wave amplitude/period options for infill modes 5 and 6; ignored otherwise.</summary>
        public Infill.WaveOptions? Wave;
        /// <summary>Conical shear angle, radians. 0 = planar - with this, BuildConicalTagged
        /// delegates straight to BuildTagged.</summary>
        public double ConeAngleRad;
        /// <summary>Conical shear centre X, mm.</summary>
        public double Cx;
        /// <summary>Conical shear centre Y, mm.</summary>
        public double Cy;

        /// <summary>InfillSpacing resolved against <see cref="WallSpacing"/> per the rule
        /// documented on <see cref="InfillSpacing"/>.</summary>
        internal double ResolvedInfillSpacing
            => double.IsNaN(InfillSpacing) || InfillSpacing == 0.0 ? WallSpacing : InfillSpacing;
    }

    /// <summary>One printed layer from its section perimeters: walls (inward offsets)
    /// then infill, ordered for printing (port of clay_core_v2 _build_layer). Moved out
    /// of the Slice component in 2.6.0 (final test round 3.1.8) so the conical mapping
    /// of walls and infill is covered by Core tests.</summary>
    public static class LayerBuilder
    {
        /// <summary>Walls + infill for one planar layer, flattened to plain point lists;
        /// every emitted point lies at <see cref="LayerBuildOptions.Level"/>.</summary>
        public static List<List<Vec3>> Build(IReadOnlyList<IReadOnlyList<Vec3>> perims, LayerBuildOptions options)
            => BuildTagged(perims, options).Flatten();

        /// <summary>Tagged walls + infill for one planar layer.</summary>
        public static LayerPaths BuildTagged(IReadOnlyList<IReadOnlyList<Vec3>> perims, LayerBuildOptions options)
        {
            double z = options.Level;
            int wallCount = options.WallCount;
            int infill = options.Infill;
            double wallSpacing = options.WallSpacing;
            double infillSpacing = options.ResolvedInfillSpacing;
            double angle = options.AngleDeg;
            IWaveProfile? waveProfile = options.WaveProfile;
            Infill.WaveOptions? wave = options.Wave;

            // nesting-aware orientation: outers CCW, holes CW. Forcing everything CCW
            // (old behaviour) made Clipper2 offset holes the wrong way and let infill
            // fill hole interiors (refinement note P4 of 2026-07 - fixed).
            // Partition FIRST: only closed rings may enter Orient/offset/infill -
            // Orient would fabricate a closing edge on an open section, which then
            // grows fake walls AND still ships as an open path (duplicate output).
            var closedPerims = new List<IReadOnlyList<Vec3>>();
            var result = new LayerPaths { Level = z };
            foreach (var p in perims)
            {
                if (p == null || p.Count < 2) continue;
                if (PolylineOps.IsClosed(p, 1e-3)) closedPerims.Add(p);
                else result.Paths.Add(Tag(new List<Vec3>(p), PathRole.OpenSection, -1));
            }
            var clean = Nesting.Orient(closedPerims);
            if (clean.Count == 0) return result;
            for (int i = 0; i < clean.Count; i++) clean[i] = AnchorRing(clean[i]);
            foreach (var path in clean) result.Paths.Add(Tag(path, PathRole.Wall, 0));

            var inner = (IReadOnlyList<IReadOnlyList<Vec3>>)clean;
            for (int w = 1; w < Math.Max(1, wallCount); w++)
            {
                var next = Offset.Polygons(inner, -Math.Abs(wallSpacing), z);
                next.RemoveAll(r => r.Count < 3);
                if (next.Count == 0) break;
                for (int i = 0; i < next.Count; i++) next[i] = AnchorRing(next[i]);
                foreach (var path in next) result.Paths.Add(Tag(path, PathRole.Wall, w));
                inner = next;
            }
            if (infill != 0)
            {
                var region = Offset.Polygons(inner, -Math.Abs(wallSpacing), z);
                region.RemoveAll(r => r.Count < 3);
                if (region.Count > 0)
                {
                    var lastWall = result.Paths[result.Paths.Count - 1].Points;
                    Vec3? anchor = lastWall[lastWall.Count - 1];
                    if (infill == 1)
                    {
                        var spans = Infill.ZigzagSpans(region, infillSpacing, angle, z, out _, anchor);
                        foreach (var path in spans) result.Paths.Add(Tag(path, PathRole.Infill, -1));
                    }
                    else if (infill == 2)
                        AddTagged(result, Chain.RingPaths(Offset.ConcentricRings(region, infillSpacing, z), anchor), PathRole.Infill);
                    else if (infill == 3)
                    {
                        var paths = Spiral.InwardPaths(Offset.ConcentricRings(region, infillSpacing, z), anchor,
                                                       out string? diagnostic);
                        result.Diagnostic = diagnostic;
                        AddTagged(result, paths, PathRole.Infill);
                    }
                    else if (infill == 4)
                    {
                        var paths = Fermat.Paths(Offset.ConcentricRings(region, infillSpacing, z), infillSpacing,
                                                 anchor, out string? diagnostic);
                        result.Diagnostic = diagnostic;
                        AddTagged(result, paths, PathRole.Infill);
                    }
                    else if (infill == 5 || infill == 6)
                    {
                        var spans = Infill.WaveSpans(region, infillSpacing, angle, z,
                                                     waveProfile ?? new SineProfile(),
                                                     wave ?? new Infill.WaveOptions(), out _, anchor);
                        foreach (var path in spans) result.Paths.Add(Tag(path, PathRole.Infill, -1));
                    }
                }
            }
            return result;
        }

        static TaggedPath Tag(List<Vec3> points, PathRole role, int wallIndex)
            => new TaggedPath
            {
                Points = points,
                Role = role,
                WallIndex = wallIndex,
                Closed = PolylineOps.IsClosed(points, 1e-3),
            };

        static void AddTagged(LayerPaths result, IEnumerable<List<Vec3>> paths, PathRole role)
        {
            foreach (var path in paths) result.Paths.Add(Tag(path, role, -1));
        }

        /// <summary>The ring re-started at the exact point where the +X ray from its own area
        /// centroid crosses it (the farthest crossing on a concave ring; the closest point to
        /// the centroid when the ray misses). Per ring, so neither the section input order nor
        /// a hole arriving first can move another ring's seam.</summary>
        static List<Vec3> AnchorRing(IReadOnlyList<Vec3> ring)
        {
            var origin = SeamAnchor.Centroid(ring);
            var hit = SeamAnchor.RayHit(ring, origin, 0.0)
                   ?? SeamAnchor.ClosestOnRing(ring, origin);
            return SeamAnchor.SplitAt(ring, hit.SegmentIndex, hit.T);
        }

        /// <summary>Walls + infill for one conical layer (slice mode 1), flattened to plain
        /// point lists. `perims` are the printed (inverse-mapped) section rings and
        /// <see cref="LayerBuildOptions.Level"/> is the planar height they were cut at in
        /// the sheared frame z' = z + tan(coneAngle)*r about (Cx,Cy) - see
        /// <see cref="Conical"/>.</summary>
        public static List<List<Vec3>> BuildConical(IReadOnlyList<IReadOnlyList<Vec3>> perims, LayerBuildOptions options)
            => BuildConicalTagged(perims, options).Flatten();

        /// <summary>Tagged walls + infill for one conical layer. Walls and infill are 2-D
        /// operations, so they are built in the sheared frame, where the layer IS planar at
        /// <see cref="LayerBuildOptions.Level"/>, and every emitted curve gets the same
        /// inverse map the perimeters received: z = level - tan(coneAngle)*r. Built at the
        /// outer ring's own z instead, the inner walls and the infill of a round object sat
        /// flat and conical mode was indistinguishable from planar (final test round 3.1.8).
        /// XY is untouched by the map, so the perimeters come back bit-for-bit up to one
        /// rounding of z; straight edges get intermediate points where the printed move would
        /// otherwise leave the cone by more than <see cref="ConeChordTol"/>.
        /// <see cref="LayerBuildOptions.ConeAngleRad"/> == 0 delegates straight to
        /// <see cref="BuildTagged"/>.</summary>
        public static LayerPaths BuildConicalTagged(IReadOnlyList<IReadOnlyList<Vec3>> perims, LayerBuildOptions options)
        {
            if (options.ConeAngleRad == 0.0)
                return BuildTagged(perims, options);
            var sheared = new List<IReadOnlyList<Vec3>>(perims.Count);
            foreach (var p in perims)
            {
                if (p == null) continue;
                var s = new List<Vec3>(p.Count);
                foreach (var q in p) s.Add(Conical.ForwardZ(q, options.ConeAngleRad, options.Cx, options.Cy));
                sheared.Add(s);
            }
            var built = BuildTagged(sheared, options);
            for (int i = 0; i < built.Paths.Count; i++)
            {
                var tagged = built.Paths[i];
                tagged.Points = Conical.InverseContourRefined(tagged.Points, options.ConeAngleRad, options.Cx, options.Cy, ConeChordTol);
                tagged.Closed = PolylineOps.IsClosed(tagged.Points, 1e-3);
            }
            return built;
        }

        /// <summary>Largest sag, mm, a straight printed move may have below the conical
        /// layer surface before an intermediate point is inserted (two orders below a
        /// clay layer; a ring around the axis never needs one).</summary>
        public const double ConeChordTol = 0.05;
    }
}
