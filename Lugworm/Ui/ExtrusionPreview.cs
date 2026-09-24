using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;

namespace Lugworm.Ui
{
    /// <summary>Extruded-material layer preview in the clean arctic look: near-white
    /// clay with soft porcelain shading so the layers read 3-D against the light bed.
    /// Robot-independent: shared by the Robot Simulation component (with or without
    /// kinematics) and the live machine view.</summary>
    public static class ExtrusionPreview
    {
        /// <summary>Arctic layer colour (porcelain white, slightly warm).</summary>
        public static readonly Color LayerColor = Color.FromArgb(243, 241, 236);
        public const int DefaultMaxPoints = 30000;
        public const int DefaultMaxFaces = 600000;
        /// <summary>Vertices per profile ring. Sixteen keep an oval round under smooth
        /// normals; more only spends face budget the corners need.</summary>
        const int ProfileSides = 16;
        /// <summary>Share of the rings the face budget allows that the path points may
        /// take; the rest is what round corners get once a print fills the budget.</summary>
        const double PointShareOfRings = 0.8;
        static readonly DisplayMaterial _layerMaterial = new DisplayMaterial(LayerColor)
        {
            // matte porcelain: no highlight at all, and the shadow side lifted by a
            // strong emission so only a faint light/shadow gradient is left to say
            // the layers are round
            Specular = Color.Black,
            Shine = 0.0,
            Emission = Color.FromArgb(82, 81, 78),
            IsTwoSided = true,
            BackDiffuse = LayerColor,
            BackSpecular = Color.Black,
        };

        /// <summary>Faintly lit two-sided material - just enough light/shadow gradient
        /// to keep the layers' roundness readable when everything in the scene is
        /// white, with no specular highlight.</summary>
        public static DisplayMaterial LayerMaterial() => _layerMaterial;

        /// <summary>Fixed world light the layer shading is baked against: high, from
        /// the front left, so the top of every layer is brightest and a wall's outer
        /// flank reads a shade darker whatever the viewport's own lights do.</summary>
        static readonly Vector3d BakedLight = BakedLightDirection();
        /// <summary>How far the shadow side drops below the lit side, 0 to 1.</summary>
        const double ShadeDepth = 0.22;

        static Vector3d BakedLightDirection()
        {
            var v = new Vector3d(-0.35, -0.45, 0.82);
            v.Unitize();
            return v;
        }

        /// <summary>Bake the light/shadow gradient into vertex colours so the layer
        /// looks the same in Wireframe, Shaded and Arctic: those modes light a
        /// DisplayMaterial differently (Wireframe barely, Arctic to white), false
        /// colours they all draw as given. Half-Lambert against the fixed light, so
        /// the gradient wraps softly round the oval with no hard terminator.</summary>
        public static void BakeShading(Mesh mesh)
        {
            if (mesh.Normals.Count != mesh.Vertices.Count) mesh.Normals.ComputeNormals();
            mesh.VertexColors.Clear();
            int n = mesh.Vertices.Count;
            for (int i = 0; i < n; i++)
            {
                var normal = new Vector3d(mesh.Normals[i]);
                double wrap = 0.5 + 0.5 * (normal * BakedLight);
                double k = 1.0 - ShadeDepth * (1.0 - wrap);
                mesh.VertexColors.Add(Color.FromArgb(
                    (int)(LayerColor.R * k), (int)(LayerColor.G * k), (int)(LayerColor.B * k)));
            }
        }

        /// <summary>Draw a layer mesh: baked colours as they are, a mesh without them
        /// under the material.</summary>
        public static void Draw(DisplayPipeline display, Mesh mesh)
        {
            if (mesh.VertexColors.Count == mesh.Vertices.Count) display.DrawMeshFalseColors(mesh);
            else display.DrawMeshShaded(mesh, _layerMaterial);
        }

        /// <summary>Chord tolerance for sampling curved toolpaths into layer points, mm:
        /// 2 % of the layer width, clamped to 0.05 to 0.5. Sampling at the layer width
        /// itself turned every small-radius arc into a few straight edges.</summary>
        public static double ChordTolerance(double widthMm)
            => Math.Max(0.05, Math.Min(0.5, 0.02 * Math.Max(0.0, widthMm)));

        public sealed class Cache
        {
            internal object? Key;
            internal int MaxIdx = -1;
            internal Mesh? Mesh;
            internal int BuiltPoints;
            internal bool Decimated;
            internal int LastBuiltIdx = -1;
            internal double Width, Height;
            internal int MaxPoints, MaxFaces;
            public void Clear()
            {
                Key = null;
                MaxIdx = -1;
                Mesh = null;
                BuiltPoints = 0;
                Decimated = false;
                LastBuiltIdx = -1;
            }
        }

        /// <summary>Build the layer mesh for a toolpath, clipped to maxIdx. Returns
        /// null when nothing extrudes.</summary>
        public static Mesh? Build(List<Vec3> pts, List<bool> ext, int maxIdx, double widthMm, double heightMm)
            => Build(pts, ext, maxIdx, widthMm, heightMm, DefaultMaxPoints, DefaultMaxFaces);

        public static Mesh? Build(List<Vec3> pts, List<bool> ext, int maxIdx, double widthMm, double heightMm,
                                  int maxPoints, int maxFaces)
            => BuildVisiblePrefix(pts, ext, maxIdx, widthMm, heightMm, maxPoints, maxFaces);

        static Mesh? BuildVisiblePrefix(List<Vec3> pts, List<bool> ext, int maxIdx,
                                        double widthMm, double heightMm, int maxPoints, int maxFaces)
        {
            var em = new Mesh();
            if (maxPoints < 2) maxPoints = 2;
            if (maxFaces < 16) maxFaces = 16;
            var runs = ExtrusionHistory.VisibleRuns(pts, ext, maxIdx);
            int total = 0;
            foreach (var run in runs) total += run.Count;
            int pointBudget = EffectivePointBudget(maxPoints, maxFaces);
            var clippedRuns = new List<IReadOnlyList<Vec3>>(runs.Count);
            int used = 0;
            foreach (var run in runs)
            {
                int runBudget = total <= pointBudget
                    ? run.Count
                    : Math.Max(2, (int)(pointBudget * (double)run.Count / total));
                var clipped = runBudget == run.Count ? run : Decimate(run, runBudget);
                clippedRuns.Add(clipped);
                used += clipped.Count;
            }
            // Round corners spend whatever rings the points leave. A print too large
            // for them at any ladder step falls back to mitred corners rather than to
            // a mesh over budget.
            double joinStep = LayerFrames.FitJoinStep(clippedRuns, RingBudget(maxFaces) - used);
            foreach (var run in clippedRuns)
            {
                var b = BuildLayer(run, widthMm, heightMm, null, joinStep);
                if (b != null) em.Append(b);
            }
            if (em.Faces.Count == 0) return null;
            // normals again on the assembled mesh (Append does not promise to keep
            // them), then the mode-independent shading on top
            em.Normals.ComputeNormals();
            BakeShading(em);
            return em;
        }

        public static Mesh? BuildCached(Cache cache, object key, List<Vec3> pts, List<bool> ext,
                                        int maxIdx, double widthMm, double heightMm,
                                        int maxPoints = DefaultMaxPoints, int maxFaces = DefaultMaxFaces)
        {
            maxPoints = Math.Max(2, maxPoints);
            maxFaces = Math.Max(16, maxFaces);
            int pointBudget = EffectivePointBudget(maxPoints, maxFaces);
            bool sameGeometry = Equals(cache.Key, key) && cache.Width == widthMm && cache.Height == heightMm
                && cache.MaxPoints == maxPoints && cache.MaxFaces == maxFaces;
            if (cache.Mesh != null && sameGeometry && cache.MaxIdx == maxIdx)
                return cache.Mesh;
            if (cache.Mesh != null && sameGeometry)
            {
                if (cache.Decimated)
                {
                    // hysteresis in BOTH directions: a backwards scrub must also
                    // trigger a rebuild or the mesh stays frozen at the old maxIdx
                    int rebuildStep = Math.Max(1, (int)Math.Ceiling(pts.Count * 0.02));
                    if (Math.Abs(maxIdx - cache.LastBuiltIdx) < rebuildStep)
                    {
                        cache.MaxIdx = maxIdx;
                        return cache.Mesh;
                    }
                }
            }
            // Rebuild the visible prefix as maximal extrusion runs. Appending a
            // separately swept/capped delta on each scrub tick leaves duplicate end
            // caps and disconnected topology at every slider position, which makes a
            // continuous print look like many extrusion instances.
            var mesh = Build(pts, ext, maxIdx, widthMm, heightMm, maxPoints, maxFaces);
            cache.Key = key; cache.MaxIdx = maxIdx; cache.Mesh = mesh;
            cache.Width = widthMm; cache.Height = heightMm;
            cache.MaxPoints = maxPoints; cache.MaxFaces = maxFaces;
            cache.BuiltPoints = CountRunPoints(pts, ext, maxIdx);
            cache.Decimated = cache.BuiltPoints > pointBudget;
            cache.LastBuiltIdx = maxIdx;
            return mesh;
        }

        /// <summary>Profile rings the face budget allows.</summary>
        static int RingBudget(int maxFaces) => Math.Max(16, maxFaces) / ProfileSides;

        static int EffectivePointBudget(int maxPoints, int maxFaces)
            => Math.Max(2, Math.Min(Math.Max(2, maxPoints), (int)(RingBudget(maxFaces) * PointShareOfRings)));

        static int CountRunPoints(List<Vec3> pts, List<bool> ext, int maxIdx)
        {
            int total = 0;
            foreach (var run in ExtrusionHistory.VisibleRuns(pts, ext, maxIdx)) total += run.Count;
            return total;
        }

        static List<Vec3> Decimate(IReadOnlyList<Vec3> path, int budget)
            => PreviewReduction.Reduce(path, budget);

        public static List<Vec3> DecimatePreview(IReadOnlyList<Vec3> path, int budget)
            => Decimate(path, budget);

        /// <summary>Layer mesh: an oval cross-section (width w, height h) piped
        /// continuously along the run. One profile ring per vertex where the path
        /// barely turns, mitred to the bisector; a fan of rings where it corners, so
        /// the layer's outer side sweeps round every corner, including sharp ones,
        /// instead of creasing or stopping (<see cref="LayerFrames"/>). Closed runs
        /// (rings) are stitched seamlessly; open ends get flat caps.</summary>
        public static Mesh? BuildLayer(IReadOnlyList<Vec3> pts, double w, double h)
            => BuildLayer(pts, w, h, null, LayerFrames.DefaultJoinStepRad);

        /// <summary>Same layer with one colour per input point applied as vertex colours
        /// (every profile ring takes its point's colour, each end cap its ring's), so
        /// <c>DrawMeshFalseColors</c> paints the sweep by a per-sample quantity.</summary>
        public static Mesh? BuildLayer(IReadOnlyList<Vec3> pts, double w, double h, IReadOnlyList<Color>? colors)
            => BuildLayer(pts, w, h, colors, LayerFrames.DefaultJoinStepRad);

        /// <summary><paramref name="joinStepRad"/> is the angle one round-join segment
        /// spans, +infinity for mitred corners only.</summary>
        public static Mesh? BuildLayer(IReadOnlyList<Vec3> pts, double w, double h, IReadOnlyList<Color>? colors,
                                      double joinStepRad)
        {
            if (pts.Count < 2 || w <= 1e-6 || h <= 1e-6 ||
                double.IsNaN(w) || double.IsNaN(h) || double.IsInfinity(w) || double.IsInfinity(h)) return null;
            var keep = DedupeIndices(pts);
            if (keep.Count < 2) return null;
            bool closed = keep.Count > 3 && Dist(pts[keep[0]], pts[keep[keep.Count - 1]]) < 1e-6;
            // A closed colored run can revisit the same location at a later time.
            // Keep separate seam rings so the last sample retains its own colour.
            bool splitSeam = closed && colors != null && colors.Count > 0;
            if (closed && !splitSeam) keep.RemoveAt(keep.Count - 1);
            int n = keep.Count;
            if (n < 2) return null;
            var path = new List<Vec3>(n);
            foreach (int i in keep) path.Add(pts[i]);

            var frames = LayerFrames.Build(path, closed, splitSeam, joinStepRad);
            int rings = frames.Count;
            if (rings < 2) return null;
            var profile = OvalProfile(w, h);
            int m = profile.Count;
            var mesh = new Mesh();
            foreach (var frame in frames)
            {
                var c = GhConvert.ToPt(frame.Centre);
                var side = new Vector3d(frame.Side.X, frame.Side.Y, frame.Side.Z);
                foreach (var (py, pz) in profile)
                    mesh.Vertices.Add(c + side * py + Vector3d.ZAxis * pz);
            }

            int Quad(int ring, int k) => ring * m + k;
            bool wrap = closed && !splitSeam;
            int last = wrap ? rings : rings - 1;
            for (int i = 0; i < last; i++)
            {
                int j = (i + 1) % rings;
                for (int k = 0; k < m; k++)
                {
                    int k2 = (k + 1) % m;
                    mesh.Faces.AddFace(Quad(i, k), Quad(j, k), Quad(j, k2), Quad(i, k2));
                }
            }
            var capRings = new List<int>(2);
            if (!closed)
            {
                // flat end caps (triangle fans about the ring centroid)
                foreach (var (ring, flip) in new[] { (0, true), (rings - 1, false) })
                {
                    var centroid = Point3d.Origin;
                    for (int k = 0; k < m; k++) centroid += mesh.Vertices[Quad(ring, k)];
                    centroid /= m;
                    int ci = mesh.Vertices.Count;
                    mesh.Vertices.Add(centroid);
                    capRings.Add(ring);
                    for (int k = 0; k < m; k++)
                    {
                        int k2 = (k + 1) % m;
                        if (flip) mesh.Faces.AddFace(ci, Quad(ring, k), Quad(ring, k2));
                        else mesh.Faces.AddFace(ci, Quad(ring, k2), Quad(ring, k));
                    }
                }
            }
            if (colors != null && colors.Count > 0)
            {
                // vertex order is rings first, then the cap centroids
                foreach (var frame in frames)
                {
                    var col = colors[Math.Min(keep[frame.Source], colors.Count - 1)];
                    for (int k = 0; k < m; k++) mesh.VertexColors.Add(col);
                }
                foreach (int ring in capRings)
                    mesh.VertexColors.Add(colors[Math.Min(keep[frames[ring].Source], colors.Count - 1)]);
            }
            mesh.Normals.ComputeNormals();
            return mesh;
        }

        /// <summary>Elliptical cross-section (y = lateral, z = vertical, centred on the
        /// path.) Counter-clockwise seen along the travel direction.</summary>
        static List<(double y, double z)> OvalProfile(double w, double h)
        {
            double a = w * 0.5, b = h * 0.5;
            var pts = new List<(double, double)>(ProfileSides);
            for (int i = 0; i < ProfileSides; i++)
            {
                double ang = 2 * Math.PI * i / ProfileSides;
                pts.Add((a * Math.Cos(ang), b * Math.Sin(ang)));
            }
            return pts;
        }

        /// <summary>Indices of the points that survive coincident-point removal.</summary>
        static List<int> DedupeIndices(IReadOnlyList<Vec3> pts)
        {
            var keep = new List<int> { 0 };
            for (int i = 1; i < pts.Count; i++)
                if (Dist(pts[i], pts[keep[keep.Count - 1]]) > 1e-6) keep.Add(i);
            return keep;
        }

        static double Dist(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Median layer-to-layer Z step of the toolpath = the layer height.</summary>
        public static double AutoLayerHeight(List<Vec3> pts, double fallback)
            => AutoLayerHeight(pts, fallback, null);

        public static double AutoLayerHeight(List<Vec3> pts, double fallback, IReadOnlyList<int>? layerIds)
        {
            if (pts.Count < 2 || layerIds == null || layerIds.Count != pts.Count)
                return Math.Max(fallback, 0.1);
            var firstZ = new List<double>();
            int lastLayer = int.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (layerIds[i] == lastLayer) continue;
                lastLayer = layerIds[i];
                firstZ.Add(pts[i].Z);
            }
            var diffs = new List<double>();
            for (int i = 1; i < firstZ.Count; i++)
            {
                double d = Math.Abs(firstZ[i] - firstZ[i - 1]);
                if (d > 1e-3) diffs.Add(d);
            }
            if (diffs.Count == 0) return Math.Max(fallback, 0.1);
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }
    }
}
