using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Lugworm.Core.Slicing;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>D7 (4.4.0): the per-object slicing orchestration moved out of
/// `SlicerComponent.SliceObject` into `Core/Slicing/SlicePipeline.cs`.
///
/// <para><b>How the extraction is pinned.</b> The orchestration used to live inside the
/// Grasshopper assembly, which these tests cannot load, so there was no Core-only way to
/// run the OLD code. <see cref="ReferenceCompose"/> is therefore a transcription of
/// `SliceObject`'s Core steps in `SliceObject`'s own order, written off the component
/// source before the extraction, and <see cref="Golden"/> records what it produced for
/// three Core-reproducible meshes at that moment. The golden digests are the "before"
/// record; the equality tests assert the extracted pipeline reproduces the composition
/// exactly, for the plain build and for the conical, corner-rounding, bricklayer,
/// non-planar and spiralize combinations on top of it.</para></summary>
public class SlicePipelineTests
{
    // ---------------------------------------------------------------- meshes

    /// <summary>Closed solid of revolution: radius profile r(z) on nv rows between z=0
    /// and z=h, capped flat at both ends. nv is deliberately coprime with the layer
    /// schedule so no level lands exactly on a vertex row.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Revolve(Func<double, double> radius, double h,
                                                           int nu = 48, int nv = 13)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        for (int j = 0; j <= nv; j++)
        {
            double z = h * j / nv;
            double r = Math.Max(radius(z), 1e-3);
            for (int i = 0; i < nu; i++)
            {
                double th = 2 * Math.PI * i / nu;
                V.Add(new Vec3(r * Math.Cos(th), r * Math.Sin(th), z));
            }
        }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = j * nu + i, b = j * nu + (i + 1) % nu;
                int c = (j + 1) * nu + i, d = (j + 1) * nu + (i + 1) % nu;
                F.Add((a, b, d)); F.Add((a, d, c));
            }
        int c0 = V.Count; V.Add(new Vec3(0, 0, 0));
        int c1 = V.Count; V.Add(new Vec3(0, 0, h));
        for (int i = 0; i < nu; i++)
        {
            F.Add((c0, (i + 1) % nu, i));
            F.Add((c1, nv * nu + i, nv * nu + (i + 1) % nu));
        }
        return (V, F);
    }

    static (List<Vec3> V, List<(int, int, int)> F) Cylinder(double r = 20, double h = 30)
        => Revolve(_ => r, h);

    static (List<Vec3> V, List<(int, int, int)> F) Cone(double rBase = 24, double rTop = 6, double h = 30)
        => Revolve(z => rBase + (rTop - rBase) * (z / h), h);

    /// <summary>Closed axis-aligned box, two triangles per face.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Box(double sx = 40, double sy = 30, double sz = 20)
    {
        double x0 = -0.5 * sx, x1 = 0.5 * sx, y0 = -0.5 * sy, y1 = 0.5 * sy;
        var V = new List<Vec3>
        {
            new(x0, y0, 0), new(x1, y0, 0), new(x1, y1, 0), new(x0, y1, 0),
            new(x0, y0, sz), new(x1, y0, sz), new(x1, y1, sz), new(x0, y1, sz),
        };
        var F = new List<(int, int, int)>
        {
            (0, 2, 1), (0, 3, 2),           // bottom
            (4, 5, 6), (4, 6, 7),           // top
            (0, 1, 5), (0, 5, 4),           // -Y
            (1, 2, 6), (1, 6, 5),           // +X
            (2, 3, 7), (2, 7, 6),           // +Y
            (3, 0, 4), (3, 4, 7),           // -X
        };
        return (V, F);
    }

    /// <summary>Closed tube: outer and inner walls joined by an annular cap at each end.
    /// Every level cuts two rings, so the layer carries a hole.</summary>
    static (List<Vec3> V, List<(int, int, int)> F) Tube(double rOuter = 30, double rInner = 6,
                                                        double h = 30, int nu = 48, int nv = 13)
    {
        var V = new List<Vec3>();
        var F = new List<(int, int, int)>();
        int outer0 = 0;
        for (int j = 0; j <= nv; j++)
        {
            double z = h * j / nv;
            for (int i = 0; i < nu; i++)
            {
                double th = 2 * Math.PI * i / nu;
                V.Add(new Vec3(rOuter * Math.Cos(th), rOuter * Math.Sin(th), z));
            }
        }
        int inner0 = V.Count;
        for (int j = 0; j <= nv; j++)
        {
            double z = h * j / nv;
            for (int i = 0; i < nu; i++)
            {
                double th = 2 * Math.PI * i / nu;
                V.Add(new Vec3(rInner * Math.Cos(th), rInner * Math.Sin(th), z));
            }
        }
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = outer0 + j * nu + i, b = outer0 + j * nu + (i + 1) % nu;
                int c = outer0 + (j + 1) * nu + i, d = outer0 + (j + 1) * nu + (i + 1) % nu;
                F.Add((a, b, d)); F.Add((a, d, c));
                int e = inner0 + j * nu + i, f = inner0 + j * nu + (i + 1) % nu;
                int g = inner0 + (j + 1) * nu + i, k = inner0 + (j + 1) * nu + (i + 1) % nu;
                F.Add((e, k, f)); F.Add((e, g, k));
            }
        for (int i = 0; i < nu; i++)
        {
            int ao = outer0 + i, bo = outer0 + (i + 1) % nu;
            int ai = inner0 + i, bi = inner0 + (i + 1) % nu;
            F.Add((ao, ai, bi)); F.Add((ao, bi, bo));
            int to = outer0 + nv * nu + i, tp = outer0 + nv * nu + (i + 1) % nu;
            int ti = inner0 + nv * nu + i, tq = inner0 + nv * nu + (i + 1) % nu;
            F.Add((to, tq, ti)); F.Add((to, tp, tq));
        }
        return (V, F);
    }

    // ------------------------------------------------ the reference composition

    /// <summary>The inputs `SliceObject` read off its `SliceParams`, named the same way.</summary>
    sealed class RefParams
    {
        public double LayerHeight = 3.0, FirstLayer = 3.0, Tol = 0.001, CornerRadius = 0.0;
        public int Mode = 0, WallCount = 2, InfillMode = 1;
        public double WallSpacing = 5.0, InfillSpacing = 5.0, ZigAngle = 0.0;
        public bool Adaptive = false, Spiralize = false;
        public double HMin = 1.0, HMax = 4.5;
        public double ConeAngleDeg = 20.0, NozzleClear = 25.0;
        public IWaveProfile? WaveProfile = null;
        public Infill.WaveOptions? WaveOptions = null;
        public BricklayerOptions? Brick = null;
        public NonPlanarOptions? NonPlanar = null;
        public string Tag = "";
    }

    /// <summary>Everything `SliceObject` pushed into the component's result.</summary>
    sealed class RefOutcome
    {
        public List<LayerPaths> Layers = new();
        public List<Vec3>? Helix;
        public List<string> Warnings = new();
        public int LayerCount;
        public int EffectiveMode;
        public bool BricklayerVarhRemark;
        public string? BricklayerSkippedMessage;
        public string? NonPlanarSkippedMessage;
    }

    /// <summary>`SlicerComponent.SliceObject`'s Core steps, in `SliceObject`'s order,
    /// for a mesh source. Written off the component before the extraction and frozen:
    /// nothing in here may be "improved" - it is the record of the old behaviour.</summary>
    static RefOutcome ReferenceCompose(IReadOnlyList<Vec3> V, IReadOnlyList<(int a, int b, int c)> F,
                                       RefParams p)
    {
        var outcome = new RefOutcome();
        var warn = outcome.Warnings;
        string tag = p.Tag;

        // reference bbox: the component reads it off Rhino; for a mesh source that is
        // the vertex bounding box
        double zmin = double.MaxValue, zmax = double.MinValue;
        double xlo = double.MaxValue, xhi = double.MinValue, ylo = double.MaxValue, yhi = double.MinValue;
        foreach (var v in V)
        {
            if (v.X < xlo) xlo = v.X;
            if (v.X > xhi) xhi = v.X;
            if (v.Y < ylo) ylo = v.Y;
            if (v.Y > yhi) yhi = v.Y;
            if (v.Z < zmin) zmin = v.Z;
            if (v.Z > zmax) zmax = v.Z;
        }
        double cx = 0.5 * (xlo + xhi), cy = 0.5 * (ylo + yhi);
        double ang = p.Mode == 1 ? p.ConeAngleDeg * Math.PI / 180.0 : 0.0;

        IReadOnlyList<Vec3> sec = V;
        if (p.Mode == 1)
        {
            var cm = new List<Vec3>(V.Count);
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var v in V)
            {
                double sh = Conical.ConeShift(v.X, v.Y, ang, cx, cy);
                var q = new Vec3(v.X, v.Y, (float)(v.Z + sh));
                if (q.Z < lo) lo = q.Z;
                if (q.Z > hi) hi = q.Z;
                cm.Add(q);
            }
            sec = cm;
            zmin = lo; zmax = hi;
        }

        List<double> levels;
        if (p.Adaptive && p.Mode == 0)
        {
            levels = OverhangAdaptiveLevels.Build(V, F, zmin, zmax, p.FirstLayer, p.HMin, p.HMax);
            double hLo = double.MaxValue, hHi = 0.0;
            for (int li = 1; li < levels.Count; li++)
            {
                double h = levels[li] - levels[li - 1];
                if (h < hLo) hLo = h;
                if (h > hHi) hHi = h;
            }
            warn.Add(levels.Count > 1
                ? tag + FormattableString.Invariant($"adaptive overhang: {levels.Count} layers, layer height {hLo:0.##}..{hHi:0.##} mm (hMin {p.HMin:0.##}, hMax {p.HMax:0.##}); first gap {levels[1] - levels[0]:0.##} mm -> last gap {levels[levels.Count - 1] - levels[levels.Count - 2]:0.##} mm; enable G-code varh for matching extrusion")
                : tag + "adaptive: object thinner than one layer");
        }
        else levels = Levels.Planar(zmin, zmax, p.FirstLayer, p.LayerHeight);

        var scalar = new List<double>(sec.Count);
        for (int vi = 0; vi < sec.Count; vi++) scalar.Add(sec[vi].Z);
        List<List<List<Vec3>>>? bucketed = null;
        if (levels.Count > 1)
        {
            bool increasing = true;
            for (int li = 1; li < levels.Count; li++)
                if (levels[li] < levels[li - 1]) { increasing = false; break; }
            if (increasing)
            {
                try { bucketed = Iso.PlanarContoursAtLevels(sec, F, scalar, levels, p.Tol); }
                catch (Exception ex)
                {
                    warn.Add(tag + $"bucketed sectioning failed ({ex.Message}) - falling back to per-layer sections");
                    bucketed = null;
                }
            }
        }

        var layerPolys = new List<List<List<Vec3>>>();
        for (int li = 0; li < levels.Count; li++)
        {
            double z = levels[li];
            var layer = new List<List<Vec3>>();
            try
            {
                var perims = bucketed != null && li < bucketed.Count
                    ? bucketed[li]
                    : Iso.Contour(sec, F, scalar, z, p.Tol);
                foreach (var pl in perims)
                {
                    IReadOnlyList<Vec3> pp = p.Mode == 1 ? Conical.InverseContour(pl, ang, cx, cy) : pl;
                    var s = Rdp.Simplify(pp, p.Tol);
                    if (s.Count >= 2) layer.Add(s);
                }
            }
            catch (Exception ex)
            {
                warn.Add(tag + FormattableString.Invariant($"layer {li + 1} (z={z:0.###}) skipped: {ex.Message}"));
                layer.Clear();
            }
            layerPolys.Add(layer);
        }

        if (p.Mode == 1)
        {
            double worst = 0.0;
            foreach (var lp in layerPolys)
                foreach (var pl in lp) worst = Math.Max(worst, Conical.LocalSlopeDeg(pl));
            if (worst > p.NozzleClear)
                warn.Add(tag + FormattableString.Invariant($"conical surface angle {worst:0} deg exceeds nozzle clearance {p.NozzleClear:0} deg -> collision risk; reduce coneAngle"));
        }

        if (p.Spiralize)
        {
            int single = 0;
            foreach (var lp in layerPolys)
                if (lp.Count == 1 && lp[0].Count >= 3 && PolylineOps.IsClosed(lp[0], 1e-3)) single++;
            if (single >= 2 && single == layerPolys.Count)
            {
                bool adaptiveLevels = p.Adaptive && p.Mode == 0;
                var contours = new List<IReadOnlyList<Vec3>>(layerPolys.Count);
                foreach (var lp in layerPolys) contours.Add(lp[0]);
                var helix = Helix.Build(contours, levels, p.LayerHeight, adaptiveLevels);
                if (helix.Count >= 2)
                {
                    outcome.Helix = helix;
                    outcome.EffectiveMode = p.Mode;
                    warn.Add(tag + $"spiralize: 1 helix from {single} layers (arc-length Z)");
                    if (p.WallCount > 1 || p.InfillMode != 0)
                        warn.Add(tag + $"spiralize emits the outer contour only - wallCount {p.WallCount} and infill are ignored (one continuous path cannot carry inner walls)");
                    outcome.LayerCount = single;
                    return outcome;
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

        int nl = 0;
        outcome.EffectiveMode = p.Mode;
        var taggedLayers = outcome.Layers;
        bool infillFallbackReported = false;
        var buildOptions = new LayerBuildOptions
        {
            WallCount = p.WallCount,
            Infill = p.InfillMode,
            WallSpacing = p.WallSpacing,
            InfillSpacing = p.InfillSpacing,
            AngleDeg = p.ZigAngle,
            WaveProfile = p.WaveProfile,
            Wave = p.WaveOptions,
            ConeAngleRad = ang,
            Cx = cx,
            Cy = cy,
        };
        for (int i = 0; i < layerPolys.Count; i++)
        {
            var perims = layerPolys[i];
            if (perims.Count == 0) continue;
            double z = perims[0].Count > 0 ? perims[0][0].Z : 0.0;
            LayerPaths tagged;
            try
            {
                if (p.Mode == 1)
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
                    var cc = p.CornerRadius > 1e-6
                        ? PolylineOps.RoundCorners(c, p.CornerRadius, PolylineOps.IsClosed(c))
                        : c;
                    if (cc.Count < 2) tagged.Paths.RemoveAt(j);
                    else
                    {
                        path.Points = cc;
                        path.Closed = PolylineOps.IsClosed(cc);
                    }
                }
            }
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

        if (p.Brick != null && p.Brick.ShiftFraction > 0)
        {
            try
            {
                var report = Bricklayer.Apply(taggedLayers, p.Brick);
                warn.Add(tag + $"bricklayer: {report.PathsShifted} wall loop(s) raised " +
                               (p.Adaptive ? FormattableString.Invariant($"by {p.Brick.ShiftFraction:0.##} x each local layer gap on ")
                                           : FormattableString.Invariant($"{p.Brick.ShiftFraction * p.LayerHeight:0.###} mm on ")) +
                               $"{report.LayersShifted}/" +
                               $"{report.LayersProcessed} eligible layers");
                foreach (var note in report.Notes) warn.Add(tag + "bricklayer: " + note);
                if (report.PathsShifted > 0) outcome.BricklayerVarhRemark = true;
            }
            catch (Exception ex)
            {
                outcome.BricklayerSkippedMessage = $"bricklayer skipped: {ex.Message}";
                warn.Add(tag + $"bricklayer skipped: {ex.Message}");
            }
        }
        if (p.NonPlanar != null && (p.NonPlanar.Amplitude > 0 || p.NonPlanar.WallAmplitude > 0))
        {
            try
            {
                NonPlanarInfill.Apply(taggedLayers, p.NonPlanar);
                warn.Add(tag + FormattableString.Invariant($"non-planar: infill +/-{p.NonPlanar.Amplitude:0.###} mm, walls +/-{p.NonPlanar.WallAmplitude:0.###} mm, ") +
                               FormattableString.Invariant($"{p.NonPlanar.Wavelength:0.#} mm period, {p.NonPlanar.PhasePerLayer:0.##} turn/layer, ") +
                               FormattableString.Invariant($"{p.NonPlanar.LoopPhaseTurns:0.##} turn/loop, {p.NonPlanar.RampLayers} ramp layer(s)"));
            }
            catch (Exception ex)
            {
                outcome.NonPlanarSkippedMessage = $"non-planar modulation skipped: {ex.Message}";
                warn.Add(tag + $"non-planar modulation skipped: {ex.Message}");
            }
        }
        outcome.LayerCount = nl;
        return outcome;
    }

    // ---------------------------------------------------------------- digests

    /// <summary>Every tagged layer's level and role/wall-index/point-count/first-point,
    /// invariant-culture so the pin does not move with the ambient culture.</summary>
    static string Signature(IReadOnlyList<LayerPaths> layers)
    {
        var sb = new StringBuilder();
        foreach (var layer in layers)
        {
            sb.Append(layer.Index).Append('@')
              .Append(layer.Level.ToString("0.####", CultureInfo.InvariantCulture)).Append(':');
            foreach (var path in layer.Paths)
            {
                sb.Append(' ').Append((int)path.Role).Append('/').Append(path.WallIndex)
                  .Append('/').Append(path.Points.Count).Append(path.Closed ? "c" : "o");
                if (path.Points.Count > 0)
                {
                    var q = path.Points[0];
                    sb.Append('(').Append(q.X.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                      .Append(q.Y.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                      .Append(q.Z.ToString("0.####", CultureInfo.InvariantCulture)).Append(')');
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string Golden(IReadOnlyList<LayerPaths> layers)
    {
        int paths = 0, points = 0;
        foreach (var layer in layers)
        {
            paths += layer.Paths.Count;
            foreach (var path in layer.Paths) points += path.Points.Count;
        }
        ulong hash = 14695981039346656037UL;
        foreach (char c in Signature(layers))
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return string.Format(CultureInfo.InvariantCulture, "layers={0} paths={1} points={2} sig={3:x16}",
                             layers.Count, paths, points, hash);
    }

    // ------------------------------------------------------- the frozen record

    // Captured from ReferenceCompose before SlicePipeline existed - the "before" half
    // of the pin. A change here means the slicing composition changed, not the test.
    const string GoldenCylinder = "layers=9 paths=27 points=1014 sig=dad044f4eab94cf5";
    const string GoldenBox = "layers=6 paths=18 points=96 sig=ce05d7bf55d0a256";
    // 5.0.0 re-recorded the tube, deliberately: zigzag rows next to the bore now join along
    // the bore's offset ring instead of starting a new path (99 -> 81 paths; the extra
    // points are the ring's arc vertices). Was "layers=9 paths=99 points=2237 sig=df9d55a18389ec4e".
    const string GoldenTube = "layers=9 paths=81 points=2563 sig=52ba5d87a51a5729";

    static RefParams PlainParams() => new RefParams
    {
        LayerHeight = 3.0,
        FirstLayer = 3.0,
        WallCount = 2,
        InfillMode = 1,
        WallSpacing = 5.0,
        InfillSpacing = 5.0,
        ZigAngle = 0.0,
        Tol = 0.001,
        Tag = "obj[0] ",
    };

    /// <summary>D10, at the level the Rhino tester reads: a five-wall cylinder sliced
    /// with the standard settings, layer width = wallDistance exactly as the component
    /// passes it, must interlock on every eligible layer. Two of the five loops rise
    /// (the chain outer-1-2-3-4 two-coloured from the flat outer wall), so 7 eligible
    /// layers raise 14 loops. Before the adjacency gate carried a tolerance for the
    /// offset pipeline's working precision this line read "6 wall loop(s) raised ... on
    /// 6/7 eligible layers" on this mesh, and nothing at all on a clean 48-gon
    /// (<c>BricklayerTests.CylinderStack_ShiftsEveryOtherWall_OnEveryEligibleLayer</c>).</summary>
    [Fact]
    public void Bricklayer_OnAFiveWallCylinder_RaisesLoopsOnEveryEligibleLayer()
    {
        var (v, f) = Cylinder();
        var p = PlainParams();
        p.WallCount = 5;
        p.Brick = new BricklayerOptions
        {
            LayerHeight = p.LayerHeight,
            LayerWidth = p.WallSpacing,
            ShiftFraction = 0.5,
            OverlapFraction = 0.5,
            RampLayers = 1,
        };

        var result = new SlicePipeline().Slice(v, f, ToOptions(v, p));

        var line = result.Warnings.Find(w => w.Contains("bricklayer:"));
        Assert.NotNull(line);
        Assert.Contains("14 wall loop(s) raised 1.5 mm on 7/7 eligible layers", line);
        Assert.True(result.BricklayerVarhRemark, "no wall loop was raised");
    }

    [Fact]
    public void Golden_ClosedCylinder_MatchesTheRecordedComposition()
    {
        var (v, f) = Cylinder();
        Assert.Equal(GoldenCylinder, Golden(ReferenceCompose(v, f, PlainParams()).Layers));
    }

    [Fact]
    public void Golden_Box_MatchesTheRecordedComposition()
    {
        var (v, f) = Box();
        Assert.Equal(GoldenBox, Golden(ReferenceCompose(v, f, PlainParams()).Layers));
    }

    [Fact]
    public void Golden_Tube_MatchesTheRecordedComposition()
    {
        var (v, f) = Tube();
        Assert.Equal(GoldenTube, Golden(ReferenceCompose(v, f, PlainParams()).Layers));
    }

    // --------------------------------------------- the extracted pipeline equals it

    /// <summary>The same run, expressed against the extracted pipeline. The reference
    /// bounding box is the host's: for a mesh source it is the vertex bounding box, so
    /// that is what the test measures.</summary>
    static SliceOptions ToOptions(IReadOnlyList<Vec3> V, RefParams p)
    {
        double xlo = double.MaxValue, xhi = double.MinValue;
        double ylo = double.MaxValue, yhi = double.MinValue;
        double zlo = double.MaxValue, zhi = double.MinValue;
        foreach (var v in V)
        {
            if (v.X < xlo) xlo = v.X;
            if (v.X > xhi) xhi = v.X;
            if (v.Y < ylo) ylo = v.Y;
            if (v.Y > yhi) yhi = v.Y;
            if (v.Z < zlo) zlo = v.Z;
            if (v.Z > zhi) zhi = v.Z;
        }
        return new SliceOptions
        {
            LayerHeight = p.LayerHeight,
            FirstLayer = p.FirstLayer,
            Mode = p.Mode,
            ConeAngleDeg = p.ConeAngleDeg,
            NozzleClearDeg = p.NozzleClear,
            Adaptive = p.Adaptive,
            HMin = p.HMin,
            HMax = p.HMax,
            Spiralize = p.Spiralize,
            CornerRadius = p.CornerRadius,
            Tol = p.Tol,
            ObjectTag = p.Tag,
            ZMin = zlo,
            ZMax = zhi,
            Cx = 0.5 * (xlo + xhi),
            Cy = 0.5 * (ylo + yhi),
            Build = new LayerBuildOptions
            {
                WallCount = p.WallCount,
                Infill = p.InfillMode,
                WallSpacing = p.WallSpacing,
                InfillSpacing = p.InfillSpacing,
                AngleDeg = p.ZigAngle,
                WaveProfile = p.WaveProfile,
                Wave = p.WaveOptions,
            },
            Bricklayer = p.Brick,
            NonPlanar = p.NonPlanar,
        };
    }

    /// <summary>Runs the composition and the pipeline over the same mesh and the same
    /// settings and asserts they agree on every tagged path, on the helix, on the
    /// warnings and on the layer count.</summary>
    static void AssertPipelineMatchesComposition(IReadOnlyList<Vec3> V,
                                                 IReadOnlyList<(int a, int b, int c)> F,
                                                 RefParams p)
    {
        var expected = ReferenceCompose(V, F, p);
        var actual = new SlicePipeline().Slice(V, F, ToOptions(V, p));

        Assert.Equal(Signature(expected.Layers), Signature(actual.Layers));
        Assert.Equal(expected.Warnings, actual.Warnings);
        Assert.Equal(expected.LayerCount, actual.LayerCount);
        Assert.Equal(expected.EffectiveMode, actual.EffectiveMode);
        Assert.Equal(expected.BricklayerVarhRemark, actual.BricklayerVarhRemark);
        Assert.Equal(expected.BricklayerSkippedMessage, actual.BricklayerSkippedMessage);
        Assert.Equal(expected.NonPlanarSkippedMessage, actual.NonPlanarSkippedMessage);
        if (expected.Helix == null) Assert.Null(actual.Helix);
        else
        {
            Assert.NotNull(actual.Helix);
            Assert.Equal(expected.Helix.Count, actual.Helix!.Count);
            for (int i = 0; i < expected.Helix.Count; i++)
            {
                Assert.Equal(expected.Helix[i].X, actual.Helix[i].X, 12);
                Assert.Equal(expected.Helix[i].Y, actual.Helix[i].Y, 12);
                Assert.Equal(expected.Helix[i].Z, actual.Helix[i].Z, 12);
            }
        }
    }

    public static IEnumerable<object[]> Meshes()
    {
        yield return new object[] { "cylinder" };
        yield return new object[] { "box" };
        yield return new object[] { "tube" };
    }

    static (List<Vec3> V, List<(int, int, int)> F) MeshNamed(string name) => name switch
    {
        "cylinder" => Cylinder(),
        "box" => Box(),
        "tube" => Tube(),
        "cone" => Cone(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown test mesh"),
    };

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_PlainBuild_EqualsTheComposition(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        AssertPipelineMatchesComposition(v, f, PlainParams());
    }

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_Conical_EqualsTheComposition(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        var p = PlainParams();
        p.Mode = 1;
        p.ConeAngleDeg = 15.0;
        p.NozzleClear = 25.0;
        AssertPipelineMatchesComposition(v, f, p);
    }

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_AdaptiveWithRoundedCorners_EqualsTheComposition(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        var p = PlainParams();
        p.Adaptive = true;
        p.HMin = 1.0;
        p.HMax = 4.5;
        p.CornerRadius = 1.5;
        AssertPipelineMatchesComposition(v, f, p);
    }

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_BothStackPasses_EqualTheComposition(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        var p = PlainParams();
        p.WallCount = 3;
        p.Brick = new BricklayerOptions
        {
            LayerHeight = p.LayerHeight,
            LayerWidth = p.WallSpacing,
            ShiftFraction = 0.5,
            OverlapFraction = 0.5,
            RampLayers = 1,
        };
        p.NonPlanar = new NonPlanarOptions
        {
            LayerHeight = p.LayerHeight,
            Amplitude = 0.4,
            Wavelength = 20.0,
            DirectionDeg = p.ZigAngle,
            Resolution = 1.0,
            PhasePerLayer = 0.5,
            RampLayers = 1,
            Profile = new SineProfile(),
            WallAmplitude = 0.3,
            LoopPhaseTurns = 0.25,
        };
        AssertPipelineMatchesComposition(v, f, p);
    }

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_Spiralize_EqualsTheComposition(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        var p = PlainParams();
        p.Spiralize = true;
        AssertPipelineMatchesComposition(v, f, p);
    }

    /// <summary>The Brep entry point cuts nothing itself: handed the perimeters the mesh
    /// path would have produced, at the same levels, it must reach the same stack.</summary>
    [Theory]
    [MemberData(nameof(Meshes))]
    public void Pipeline_FromPerimeters_EqualsTheMeshPath(string mesh)
    {
        var (v, f) = MeshNamed(mesh);
        var p = PlainParams();
        var options = ToOptions(v, p);

        var plan = SlicePipeline.PlanLevels(v, f, options);
        var scalar = new List<double>(v.Count);
        foreach (var q in v) scalar.Add(q.Z);
        var cut = Iso.PlanarContoursAtLevels(v, f, scalar, plan.Levels, p.Tol);
        var perimeters = new List<IReadOnlyList<IReadOnlyList<Vec3>>>();
        foreach (var level in cut) perimeters.Add(level);

        var fromMesh = new SlicePipeline().Slice(v, f, ToOptions(v, p));
        var fromPerimeters = new SlicePipeline().SliceFromPerimeters(perimeters, plan.Levels, options);

        Assert.Empty(plan.Warnings);
        Assert.Equal(Signature(fromMesh.Layers), Signature(fromPerimeters.Layers));
        Assert.Equal(fromMesh.Warnings, fromPerimeters.Warnings);
        Assert.Equal(fromMesh.LayerCount, fromPerimeters.LayerCount);
    }

    // ------------------------------- combinations that were Rhino-only before D7

    /// <summary>Adaptive plus bricklayer on a cone in conical mode - three groups that
    /// could only ever be tried together in Rhino. Two things are pinned. The conical
    /// run ignores the adaptive flag on purpose: a conical object is already planar in
    /// its own sheared frame, so the schedule is the fixed one taken in that frame, and
    /// the levels come out evenly spaced rather than following the cone's slope. And the
    /// bricklayer pass still runs on the mapped stack, raising loops off their layer.</summary>
    [Fact]
    public void AdaptiveWithBricklayer_OnAConeInConicalMode_UsesTheShearedPlanarSchedule()
    {
        var (v, f) = Cone();
        var p = PlainParams();
        p.Mode = 1;
        p.ConeAngleDeg = 15.0;
        p.NozzleClear = 60.0;          // a warning here would only add noise
        p.Adaptive = true;             // deliberately ignored in mode 1
        p.HMin = 1.0;
        p.HMax = 4.5;
        p.WallCount = 3;
        p.Brick = new BricklayerOptions
        {
            LayerHeight = p.LayerHeight,
            // The component passes LayerWidth = wallDistance, and since D10 that is enough
            // on a round part: the adjacency gate carries a tolerance for the offset
            // pipeline's working precision (Bricklayer.AdjacencyGate), which is what the
            // measured wall gap here needs. Before D10 this row had to inflate the layer
            // by 2% for the pass to interlock anything at all.
            LayerWidth = p.WallSpacing,
            ShiftFraction = 0.5,
            OverlapFraction = 0.5,
            RampLayers = 1,
        };
        var options = ToOptions(v, p);

        var plan = SlicePipeline.PlanLevels(v, f, options);
        var result = new SlicePipeline().Slice(v, f, options);

        // the schedule is the fixed one in the sheared frame, not an adaptive one
        Assert.Empty(plan.Warnings);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("adaptive"));
        Assert.True(plan.Levels.Count > 4, $"only {plan.Levels.Count} levels");
        for (int i = 1; i < plan.Levels.Count; i++)
            Assert.Equal(p.LayerHeight, plan.Levels[i] - plan.Levels[i - 1], 9);

        // and the object really is conical: a layer's own points are not all at one Z
        Assert.True(result.Layers.Count > 4, $"only {result.Layers.Count} layers");
        var mid = result.Layers[result.Layers.Count / 2];
        double zLo = double.MaxValue, zHi = double.MinValue;
        foreach (var path in mid.Paths)
            foreach (var q in path.Points)
            {
                if (q.Z < zLo) zLo = q.Z;
                if (q.Z > zHi) zHi = q.Z;
            }
        Assert.True(zHi - zLo > 0.5, $"conical layer spans only {zHi - zLo:0.###} mm in Z");

        // the bricklayer pass ran on the mapped stack
        Assert.Contains(result.Warnings, w => w.Contains("bricklayer:"));
        Assert.True(result.BricklayerVarhRemark, "no wall loop was raised");
        int shifted = 0;
        foreach (var layer in result.Layers)
            foreach (var path in layer.Paths)
                if (Math.Abs(path.ZShift) > 1e-9) shifted++;
        Assert.True(shifted > 0, "no path recorded a Z shift");
    }

    /// <summary>Spiralize on a cylinder stack: one turn per level, Z never drops, and
    /// each turn starts where the one below ended.</summary>
    [Fact]
    public void Spiralize_OnACylinderStack_RisesMonotonically_OneTurnPerLevel()
    {
        var (v, f) = Cylinder();
        var p = PlainParams();
        p.Spiralize = true;
        var options = ToOptions(v, p);

        var plan = SlicePipeline.PlanLevels(v, f, options);
        var result = new SlicePipeline().Slice(v, f, options);

        var helix = result.Helix;
        Assert.NotNull(helix);
        Assert.Empty(result.Layers);
        Assert.Equal(plan.Levels.Count, result.LayerCount);
        Assert.Contains(result.Warnings, w => w.Contains($"spiralize: 1 helix from {plan.Levels.Count} layers"));

        for (int i = 1; i < helix!.Count; i++)
            Assert.True(helix[i].Z >= helix[i - 1].Z - 1e-9,
                $"helix dropped from {helix[i - 1].Z:0.####} to {helix[i].Z:0.####} at point {i}");

        // one turn per level: each turn's first point sits exactly on its level. A turn
        // ends on the next level too (it closes where the next one opens), so the start
        // is the LAST index carrying that Z, not the first.
        var turnStart = new List<int>();
        int at = 0;
        foreach (double level in plan.Levels)
        {
            while (at < helix.Count && helix[at].Z < level - 1e-12) at++;
            Assert.True(at < helix.Count, $"no helix point reaches level {level:0.###}");
            while (at + 1 < helix.Count && helix[at + 1].Z <= level + 1e-12) at++;
            Assert.Equal(level, helix[at].Z, 9);
            turnStart.Add(at);
            at++;
        }
        Assert.Equal(plan.Levels.Count, turnStart.Count);
        Assert.Equal(0, turnStart[0]);

        // seam continuity: a turn starts where the previous one ended, in XY too
        for (int t = 1; t < turnStart.Count; t++)
        {
            int k = turnStart[t];
            double jump = PolylineOps.Dist(helix[k - 1], helix[k]);
            Assert.True(jump < 0.5,
                $"turn {t} starts {jump:0.###} mm away from where turn {t - 1} ended");
        }

        // the last turn closes on the top level plus one layer height
        Assert.Equal(plan.Levels[plan.Levels.Count - 1] + p.LayerHeight, helix[helix.Count - 1].Z, 9);
    }
}
