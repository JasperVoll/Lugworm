using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;

namespace Lugworm.Core.Slicing
{
    /// <summary>Everything one object's slicing run needs beyond the geometry itself.
    /// Mutable init-style like <see cref="BricklayerOptions"/>, and deliberately built
    /// around the existing <see cref="LayerBuildOptions"/> record rather than repeating
    /// its wall/infill/wave fields.</summary>
    public sealed class SliceOptions
    {
        /// <summary>Nominal layer spacing, mm.</summary>
        public double LayerHeight = 3.0;
        /// <summary>Height of the first section above the object's base, mm.</summary>
        public double FirstLayer = 3.0;
        /// <summary>0 planar / 1 conical.</summary>
        public int Mode = 0;
        /// <summary>Conical half-angle, degrees. Read only in mode 1.</summary>
        public double ConeAngleDeg = 20.0;
        /// <summary>Largest printable surface angle, degrees - the fixed nozzle's
        /// collision guard. Exceeding it warns, it never changes the geometry.</summary>
        public double NozzleClearDeg = 25.0;
        /// <summary>Overhang-driven variable layer heights. Honoured in mode 0 only:
        /// a conical run is already planar in its own sheared frame.</summary>
        public bool Adaptive;
        /// <summary>Adaptive bounds, mm.</summary>
        public double HMin = 1.5, HMax = 4.5;
        /// <summary>One continuous rising outer contour instead of stacked layers.
        /// Falls back to the layered build, with a warning, when the object does not
        /// present exactly one closed island on every layer.</summary>
        public bool Spiralize;
        /// <summary>Wall/infill/wave settings for every layer. <see cref="LayerBuildOptions.Level"/>,
        /// <see cref="LayerBuildOptions.ConeAngleRad"/>, <see cref="LayerBuildOptions.Cx"/> and
        /// <see cref="LayerBuildOptions.Cy"/> are the pipeline's to fill in - it builds each
        /// layer from its own copy, so the template passed in is never mutated.</summary>
        public LayerBuildOptions Build = new LayerBuildOptions();
        /// <summary>Round sharp contour corners to this radius (overfill correction), mm.
        /// 0 leaves every path as built.</summary>
        public double CornerRadius;
        /// <summary>Sectioning / linearization tolerance, mm.</summary>
        public double Tol = 0.001;
        /// <summary>Bricklayer whole-stack pass; null = off, as is a
        /// <see cref="BricklayerOptions.ShiftFraction"/> of 0.</summary>
        public BricklayerOptions? Bricklayer;
        /// <summary>Non-planar whole-stack pass; null = off, as are amplitudes of 0.</summary>
        public NonPlanarOptions? NonPlanar;
        /// <summary>Prefix every warning carries, so a multi-object solve says which
        /// object spoke. The host passes "obj[3] " or "".</summary>
        public string ObjectTag = "";
        /// <summary>The object's reference Z range, mm. The host measures it on the most
        /// precise geometry it holds (the Brep when the source was brep-like, else the
        /// mesh), which is why it is supplied rather than derived from the vertices.</summary>
        public double ZMin, ZMax;
        /// <summary>Centre of the reference bounding box in XY, mm - the conical shear
        /// axis, and supplied for the same reason as <see cref="ZMin"/>.</summary>
        public double Cx, Cy;
    }

    /// <summary>The layer schedule for one object plus what it wants reported. Split out
    /// so a host that sections the geometry itself (a Brep, precisely) can ask for the
    /// levels first and hand the cut perimeters back to
    /// <see cref="SlicePipeline.SliceFromPerimeters"/>.</summary>
    public sealed class LevelPlan
    {
        public List<double> Levels = new List<double>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>What one object's slicing run produced. The host turns
    /// <see cref="Layers"/> and <see cref="Helix"/> into its own curve tree and raises
    /// <see cref="Warnings"/> plus the three runtime notices.</summary>
    public sealed class SliceResult
    {
        /// <summary>The tagged stack, both whole-stack passes already applied. Empty when
        /// the run produced a helix instead.</summary>
        public readonly List<LayerPaths> Layers = new List<LayerPaths>();
        /// <summary>The spiralize helix, or null when the object was sliced into layers.</summary>
        public List<Vec3>? Helix;
        /// <summary>Diagnostics for the component's `info` output, in the order they arose.</summary>
        public readonly List<string> Warnings = new List<string>();
        /// <summary>The mode actually used - what the host adds to its effective-mode set.
        /// Only meaningful on a run that returned.</summary>
        public int EffectiveMode;
        /// <summary>Layers emitted: the helix's source layer count on a spiralize run,
        /// otherwise the number of built layers.</summary>
        public int LayerCount;
        /// <summary>Which layer the run is in, for a failure message. Survives a throw:
        /// the host reads it off the <see cref="SlicePipeline.Result"/> it owns.</summary>
        public string? ActiveLayerContext;
        /// <summary>The bricklayer pass moved at least one wall loop off the nominal layer
        /// height, so the host must remark that the G-code varh group belongs on.</summary>
        public bool BricklayerVarhRemark;
        /// <summary>Set when the bricklayer pass threw and was skipped; the host raises it
        /// as a runtime warning verbatim.</summary>
        public string? BricklayerSkippedMessage;
        /// <summary>Set when the non-planar pass threw and was skipped.</summary>
        public string? NonPlanarSkippedMessage;
    }

    /// <summary>One object's slicing orchestration: levels, sectioning, the conical shear
    /// and its inverse, RDP, the spiralize helix, walls and infill per layer, and the two
    /// whole-stack passes. Moved out of `SlicerComponent.SliceObject` in 4.4.0 (D7) - it
    /// was a 300-line method inside the Grasshopper assembly, so the multi-object,
    /// adaptive and conical combinations could only ever be exercised in Rhino. The
    /// component now converts geometry, calls this, and builds the curve tree.
    ///
    /// <para>Instance, not static, for one reason: <see cref="Result"/> exists before
    /// <see cref="Slice"/> runs, so the host can still read
    /// <see cref="SliceResult.ActiveLayerContext"/> when the call throws - which is how
    /// the component names the layer in "obj[2] layer 7 geometry rejected".</para></summary>
    public sealed class SlicePipeline
    {
        /// <summary>The result this pipeline fills. Readable after a throw.</summary>
        public SliceResult Result { get; } = new SliceResult();

        /// <summary>Slices a triangle mesh. `vertices`/`faces` are the object's own mesh -
        /// unsheared; the conical shear (mode 1) happens here.</summary>
        public SliceResult Slice(IReadOnlyList<Vec3> vertices,
                                 IReadOnlyList<(int a, int b, int c)> faces,
                                 SliceOptions o)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (o == null) throw new ArgumentNullException(nameof(o));
            Result.ActiveLayerContext = "preflight";

            double ang = ConeAngleRad(o);
            double zmin = o.ZMin, zmax = o.ZMax;
            var section = vertices;
            if (o.Mode == 1)
            {
                section = Shear(vertices, ang, o.Cx, o.Cy, out double lo, out double hi);
                if (section.Count > 0) { zmin = lo; zmax = hi; }
            }

            var plan = Plan(vertices, faces, o, zmin, zmax);
            Result.Warnings.AddRange(plan.Warnings);
            var levels = plan.Levels;

            var scalar = new List<double>(section.Count);
            for (int vi = 0; vi < section.Count; vi++) scalar.Add(section[vi].Z);

            List<List<List<Vec3>>>? bucketed = null;
            if (levels.Count > 1)
            {
                bool increasing = true;
                for (int li = 1; li < levels.Count; li++)
                    if (levels[li] < levels[li - 1]) { increasing = false; break; }
                if (increasing)
                {
                    try { bucketed = Iso.PlanarContoursAtLevels(section, faces, scalar, levels, o.Tol); }
                    catch (Exception ex)
                    {
                        // deliberately broad (D6a): the section runs over caller-supplied
                        // geometry of unknown quality, and whatever it throws the answer
                        // is the same - fall back to per-layer sectioning below and say
                        // so, rather than rejecting the object
                        Result.Warnings.Add(o.ObjectTag + $"bucketed sectioning failed ({ex.Message}) - falling back to per-layer sections");
                        bucketed = null;
                    }
                }
            }

            var buckets = bucketed;
            return Run(levels,
                       li => buckets != null && li < buckets.Count
                             ? (IReadOnlyList<IReadOnlyList<Vec3>>)buckets[li]
                             : Iso.Contour(section, faces, scalar, levels[li], o.Tol),
                       o, ang, zmin, zmax);
        }

        /// <summary>Slices from perimeters the host cut itself, one entry per level, in
        /// level order. The Brep branch: a brep-like source sections precisely on the
        /// Rhino side, and everything after the section is shared with
        /// <see cref="Slice"/>. Levels come from <see cref="PlanLevels"/>, whose warnings
        /// the host raises before these.</summary>
        public SliceResult SliceFromPerimeters(IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> perimetersPerLevel,
                                               IReadOnlyList<double> levels,
                                               SliceOptions o)
        {
            if (perimetersPerLevel == null) throw new ArgumentNullException(nameof(perimetersPerLevel));
            if (levels == null) throw new ArgumentNullException(nameof(levels));
            if (o == null) throw new ArgumentNullException(nameof(o));
            Result.ActiveLayerContext = "preflight";
            var empty = new List<IReadOnlyList<Vec3>>();
            return Run(levels,
                       li => li < perimetersPerLevel.Count ? perimetersPerLevel[li] : empty,
                       o, ConeAngleRad(o), o.ZMin, o.ZMax);
        }

        /// <summary>The layer schedule alone, for a host that must section at the levels
        /// before it can call <see cref="SliceFromPerimeters"/>. Adaptive schedules read
        /// the mesh; the fixed schedule only needs the reference Z range.</summary>
        public static LevelPlan PlanLevels(IReadOnlyList<Vec3> vertices,
                                           IReadOnlyList<(int a, int b, int c)> faces,
                                           SliceOptions o)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            if (o == null) throw new ArgumentNullException(nameof(o));
            double zmin = o.ZMin, zmax = o.ZMax;
            if (o.Mode == 1)
            {
                var sheared = Shear(vertices, ConeAngleRad(o), o.Cx, o.Cy, out double lo, out double hi);
                if (sheared.Count > 0) { zmin = lo; zmax = hi; }
            }
            return Plan(vertices, faces, o, zmin, zmax);
        }

        static double ConeAngleRad(SliceOptions o)
            => o.Mode == 1 ? o.ConeAngleDeg * Math.PI / 180.0 : 0.0;

        /// <summary>The conical space transform: z' = z + tan(angle)*r about (cx,cy).
        /// The sheared Z is rounded to single precision because the component used to run
        /// this on a duplicated Rhino mesh, whose vertices are single precision - the
        /// levels and the sections saw the rounded value, and still must.</summary>
        static List<Vec3> Shear(IReadOnlyList<Vec3> vertices, double ang, double cx, double cy,
                                out double zmin, out double zmax)
        {
            var sheared = new List<Vec3>(vertices.Count);
            zmin = double.MaxValue;
            zmax = double.MinValue;
            foreach (var v in vertices)
            {
                double shift = Conical.ConeShift(v.X, v.Y, ang, cx, cy);
                var q = new Vec3(v.X, v.Y, (float)(v.Z + shift));
                if (q.Z < zmin) zmin = q.Z;
                if (q.Z > zmax) zmax = q.Z;
                sheared.Add(q);
            }
            return sheared;
        }

        static LevelPlan Plan(IReadOnlyList<Vec3> vertices, IReadOnlyList<(int a, int b, int c)> faces,
                              SliceOptions o, double zmin, double zmax)
        {
            var plan = new LevelPlan();
            if (o.Adaptive && o.Mode == 0)
            {
                var levels = OverhangAdaptiveLevels.Build(vertices, faces, zmin, zmax,
                                                          o.FirstLayer, o.HMin, o.HMax);
                plan.Levels = levels;
                double hLo = double.MaxValue, hHi = 0.0;
                for (int li = 1; li < levels.Count; li++)
                {
                    double h = levels[li] - levels[li - 1];
                    if (h < hLo) hLo = h;
                    if (h > hHi) hHi = h;
                }
                plan.Warnings.Add(levels.Count > 1
                    ? o.ObjectTag + FormattableString.Invariant($"adaptive overhang: {levels.Count} layers, layer height {hLo:0.##}..{hHi:0.##} mm (hMin {o.HMin:0.##}, hMax {o.HMax:0.##}); first gap {levels[1] - levels[0]:0.##} mm -> last gap {levels[levels.Count - 1] - levels[levels.Count - 2]:0.##} mm; enable G-code varh for matching extrusion")
                    : o.ObjectTag + "adaptive: object thinner than one layer");
            }
            else plan.Levels = Levels.Planar(zmin, zmax, o.FirstLayer, o.LayerHeight);
            return plan;
        }

        /// <summary>Everything downstream of the section, shared by both entry points.</summary>
        SliceResult Run(IReadOnlyList<double> levels,
                        Func<int, IReadOnlyList<IReadOnlyList<Vec3>>> perimetersAt,
                        SliceOptions o, double ang, double zmin, double zmax)
        {
            string tag = o.ObjectTag;
            var warn = Result.Warnings;

            var layerPolys = new List<List<List<Vec3>>>(levels.Count);
            for (int li = 0; li < levels.Count; li++)
            {
                Result.ActiveLayerContext = $"layer {li + 1}";
                double z = levels[li];
                // one bad layer must degrade to a warning, never reject the object
                var layer = new List<List<Vec3>>();
                try
                {
                    var perims = perimetersAt(li);
                    foreach (var pl in perims)
                    {
                        IReadOnlyList<Vec3> pp = o.Mode == 1 ? Conical.InverseContour(pl, ang, o.Cx, o.Cy) : pl;
                        var s = Rdp.Simplify(pp, o.Tol);
                        if (s.Count >= 2) layer.Add(s);   // degenerate slivers are skipped
                    }
                }
                // deliberately broad (D6a): the perimeter source is a caller-supplied
                // delegate over caller-supplied geometry, so no exception set is known;
                // every one of them means this layer, and only this layer, is lost
                catch (Exception ex)
                {
                    warn.Add(tag + FormattableString.Invariant($"layer {li + 1} (z={z:0.###}) skipped: {ex.Message}"));
                    layer.Clear();
                }
                layerPolys.Add(layer);
            }

            if (o.Mode == 1)
            {
                double worst = 0.0;
                foreach (var lp in layerPolys)
                    foreach (var pl in lp) worst = Math.Max(worst, Conical.LocalSlopeDeg(pl));
                if (worst > o.NozzleClearDeg)
                    warn.Add(tag + FormattableString.Invariant($"conical surface angle {worst:0} deg exceeds nozzle clearance {o.NozzleClearDeg:0} deg -> collision risk; reduce coneAngle"));
            }

            // spiralize -> one continuous helix, Z by arc-length fraction
            if (o.Spiralize)
            {
                int single = 0;
                foreach (var lp in layerPolys)
                    if (lp.Count == 1 && lp[0].Count >= 3 && PolylineOps.IsClosed(lp[0], 1e-3)) single++;
                if (single >= 2 && single == layerPolys.Count)
                {
                    // the helix follows the levels the layers were actually cut at: an
                    // adaptive schedule must not be flattened back to the fixed layer
                    // height here (final test round 3.1.7 - planar levels reproduce
                    // the previous helix exactly)
                    var baseLevels = levels.Count > 0
                        ? levels
                        : (IReadOnlyList<double>)Levels.Planar(zmin, zmax, o.FirstLayer, o.LayerHeight);
                    bool adaptiveLevels = o.Adaptive && o.Mode == 0;
                    var contours = new List<IReadOnlyList<Vec3>>(layerPolys.Count);
                    foreach (var lp in layerPolys) contours.Add(lp[0]);
                    var helix = Helix.Build(contours, baseLevels, o.LayerHeight, adaptiveLevels);
                    if (helix.Count >= 2)
                    {
                        Result.Helix = helix;
                        Result.EffectiveMode = o.Mode;
                        warn.Add(tag + $"spiralize: 1 helix from {single} layers (arc-length Z)");
                        if (o.Build.WallCount > 1 || o.Build.Infill != 0)
                            warn.Add(tag + $"spiralize emits the outer contour only - wallCount {o.Build.WallCount} and infill are ignored (one continuous path cannot carry inner walls)");
                        Result.LayerCount = single;
                        return Result;
                    }
                }
                int badLayer = -1;
                for (int i = 0; i < layerPolys.Count; i++)
                    if (!(layerPolys[i].Count == 1 && layerPolys[i][0].Count >= 3 && PolylineOps.IsClosed(layerPolys[i][0], 1e-3))) { badLayer = i; break; }
                if (badLayer >= 0)
                    warn.Add(tag + $"spiralize needs exactly one closed island per layer - found {layerPolys[badLayer].Count} on layer {badLayer}; sliced normally instead");
                else
                    warn.Add(tag + $"spiralize needs at least 2 layers - found {layerPolys.Count}; sliced normally instead");
            }

            // layered output: walls + infill, one entry per layer
            int nl = 0;
            Result.EffectiveMode = o.Mode;
            var taggedLayers = Result.Layers;
            bool infillFallbackReported = false;
            var buildOptions = new LayerBuildOptions
            {
                WallCount = o.Build.WallCount,
                Infill = o.Build.Infill,
                WallSpacing = o.Build.WallSpacing,
                InfillSpacing = o.Build.InfillSpacing,
                AngleDeg = o.Build.AngleDeg,
                WaveProfile = o.Build.WaveProfile,
                Wave = o.Build.Wave,
                ConeAngleRad = ang,
                Cx = o.Cx,
                Cy = o.Cy,
            };
            for (int i = 0; i < layerPolys.Count; i++)
            {
                Result.ActiveLayerContext = $"layer {i + 1}";
                var perims = layerPolys[i];
                if (perims.Count == 0) continue;
                double z = perims[0].Count > 0 ? perims[0][0].Z : 0.0;
                // build the layer's final path list BEFORE appending anything, so a
                // failure can fall back to the raw perimeters without duplicates -
                // one bad layer degrades to perimeters-only, never rejects the object
                LayerPaths tagged;
                try
                {
                    // mode 1: walls and infill are 2-D operations, so they are built in
                    // the sheared frame (where the layer is planar at its cut level)
                    // and mapped back like the perimeters. Built at the ring's own z
                    // they sat flat at the outer ring's height, which made conical
                    // mode look like a no-op on round objects (final test round 3.1.8)
                    if (o.Mode == 1)
                    {
                        buildOptions.Level = i < levels.Count ? levels[i] : z;
                        tagged = LayerBuilder.BuildConicalTagged(perims, buildOptions);
                    }
                    else
                    {
                        buildOptions.Level = z;
                        tagged = LayerBuilder.BuildTagged(perims, buildOptions);
                    }
                    tagged.Index = i;
                    if (!infillFallbackReported && tagged.Diagnostic != null)
                    {
                        warn.Add(tag + $"layer {i + 1}: {tagged.Diagnostic}");
                        infillFallbackReported = true;
                    }
                    for (int j = tagged.Paths.Count - 1; j >= 0; j--)
                    {
                        var path = tagged.Paths[j];
                        var c = path.Points;
                        var cc = o.CornerRadius > 1e-6
                            ? PolylineOps.RoundCorners(c, o.CornerRadius, PolylineOps.IsClosed(c))
                            : c;
                        if (cc.Count < 2) tagged.Paths.RemoveAt(j);
                        else
                        {
                            path.Points = cc;
                            path.Closed = PolylineOps.IsClosed(cc);
                        }
                    }
                }
                // deliberately broad (D6a): offsetting and filling run Clipper2 over a
                // section this slicer did not author; any failure degrades this layer to
                // its perimeters rather than losing the object
                catch (Exception ex)
                {
                    warn.Add(tag + $"layer {i + 1}: walls/infill failed ({ex.Message}) - emitting perimeters only");
                    tagged = new LayerPaths { Level = z, Index = i };
                    foreach (var pl in perims)
                    {
                        if (pl.Count < 2) continue;
                        bool closed = PolylineOps.IsClosed(pl, 1e-3);
                        tagged.Paths.Add(new TaggedPath
                        {
                            Points = new List<Vec3>(pl),
                            Role = closed ? PathRole.Wall : PathRole.OpenSection,
                            WallIndex = closed ? 0 : -1,
                            Closed = closed,
                        });
                    }
                }
                taggedLayers.Add(tagged);
                nl++;
            }

            // Bricklayer and the non-planar infill pass operate on the whole tagged stack
            // here, before anything is flattened into curves: both need to see every layer
            // (the ramps) and both need the wall/infill roles. With both off this is a
            // no-op and the flattened output is exactly the pre-tagging curve output.
            if (o.Bricklayer != null && o.Bricklayer.ShiftFraction > 0)
            {
                try
                {
                    var report = Bricklayer.Apply(taggedLayers, o.Bricklayer);
                    warn.Add(tag + $"bricklayer: {report.PathsShifted} wall loop(s) raised " +
                                   (o.Adaptive ? FormattableString.Invariant($"by {o.Bricklayer.ShiftFraction:0.##} x each local layer gap on ")
                                               : FormattableString.Invariant($"{o.Bricklayer.ShiftFraction * o.LayerHeight:0.###} mm on ")) +
                                   $"{report.LayersShifted}/" +
                                   $"{report.LayersProcessed} eligible layers");
                    foreach (var note in report.Notes) warn.Add(tag + "bricklayer: " + note);
                    if (report.PathsShifted > 0) Result.BricklayerVarhRemark = true;
                }
                // deliberately broad (D6a): the pass is optional, so any failure in it
                // must cost the pass and nothing else - the stack below it is still good
                catch (Exception ex)
                {
                    Result.BricklayerSkippedMessage = $"bricklayer skipped: {ex.Message}";
                    warn.Add(tag + $"bricklayer skipped: {ex.Message}");
                }
            }
            if (o.NonPlanar != null && (o.NonPlanar.Amplitude > 0 || o.NonPlanar.WallAmplitude > 0))
            {
                try
                {
                    NonPlanarInfill.Apply(taggedLayers, o.NonPlanar);
                    warn.Add(tag + FormattableString.Invariant($"non-planar: infill +/-{o.NonPlanar.Amplitude:0.###} mm, walls +/-{o.NonPlanar.WallAmplitude:0.###} mm, ") +
                                   FormattableString.Invariant($"{o.NonPlanar.Wavelength:0.#} mm period, {o.NonPlanar.PhasePerLayer:0.##} turn/layer, ") +
                                   FormattableString.Invariant($"{o.NonPlanar.LoopPhaseTurns:0.##} turn/loop, {o.NonPlanar.RampLayers} ramp layer(s)"));
                }
                // deliberately broad (D6a): optional pass, same rule as the bricklayer -
                // and NonPlanarInfill.Apply throws by design on an unsafe amplitude
                catch (Exception ex)
                {
                    Result.NonPlanarSkippedMessage = $"non-planar modulation skipped: {ex.Message}";
                    warn.Add(tag + $"non-planar modulation skipped: {ex.Message}");
                }
            }
            Result.LayerCount = nl;
            return Result;
        }
    }
}
