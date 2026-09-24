using System;
using System.Collections.Generic;
using System.IO;
using Rhino.FileIO;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Robot
{
    /// <summary>Loads the embedded SCARA assembly (Robot_Assembly.3dm) + robot.xml once,
    /// groups link meshes by layer (and object name for the rail/slider split), applies the
    /// axial fit so mesh joints match the firmware link lengths, and poses the meshes per IK
    /// solve via <see cref="RobotPosing"/>. The pose math lives in the Rhino-free .Core; this
    /// class only does the RhinoCommon I/O and transforms.</summary>
    public sealed class RobotModel
    {
        public RobotDefinition Def { get; }
        readonly Dictionary<string, List<Mesh>> _base;   // link name -> base meshes (assembly coords, axial-fit applied)
        public IReadOnlyList<string> MissingLinks { get; }
        public string Diagnostics { get; }               // per-link brep/solid/union/face counts
        /// <summary>Load-time findings the user should see (stale base calibration,
        /// robot.xml parse problems) - surfaced by the Robot component as warnings.</summary>
        public IReadOnlyList<string> Warnings { get; }

        RobotModel(RobotDefinition def, Dictionary<string, List<Mesh>> baseMeshes, List<string> missing, string diagnostics, List<string> warnings)
        { Def = def; _base = baseMeshes; MissingLinks = missing; Diagnostics = diagnostics; Warnings = warnings; }

        static RobotModel? _default;
        static volatile bool _tried;
        static string? _loadError;
        static readonly object LoadSync = new object();

        /// <summary>Why <see cref="Default"/> is null (resource/parse failure), else null.
        /// Callers surface this instead of silently falling back to the schematic.</summary>
        public static string? LoadError => _loadError;

        /// <summary>The embedded robot, loaded once per process. Null if the resources are
        /// missing or unreadable (callers fall back to a schematic; see LoadError).</summary>
        public static RobotModel? Default
        {
            get
            {
                if (_tried) return _default;
                lock (LoadSync)
                {
                    if (_tried) return _default;
                    try { _default = LoadEmbedded(); }
                    catch (Exception ex) { _default = null; _loadError = ex.Message; }
                    _tried = true;
                }
                return _default;
            }
        }

        static Stream? Res(string name)
            => typeof(RobotModel).Assembly.GetManifestResourceStream("Lugworm.Resources." + name);

        static RobotModel LoadEmbedded()
        {
            string xml;
            using (var xs = Res("robot.xml") ?? throw new FileNotFoundException("robot.xml resource missing"))
            using (var sr = new StreamReader(xs)) xml = sr.ReadToEnd();
            var def = RobotDefinition.Parse(xml);

            byte[] bytes;
            using (var ds = Res("Robot_Assembly.3dm") ?? throw new FileNotFoundException("Robot_Assembly.3dm resource missing"))
            using (var ms = new MemoryStream()) { ds.CopyTo(ms); bytes = ms.ToArray(); }
            var file = File3dm.FromByteArray(bytes) ?? throw new InvalidDataException("could not read Robot_Assembly.3dm");

            var layerName = new Dictionary<int, string>();
            foreach (var ly in file.AllLayers) layerName[ly.Index] = ly.Name;

            // Since the 2026-07-17 sanitizing re-export, parts are world-coordinate breps
            // placed directly ON the kinematic layers (the original block instances were
            // flattened; the old import-layer duplicates are gone). CollectObject still
            // handles both forms: group by the TOP-LEVEL object's layer, recurse any
            // instances, collect the BREPS in world coords, ignore non-link layers.
            var linkLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lk in def.Links) if (!string.IsNullOrEmpty(lk.Layer)) linkLayers.Add(lk.Layer);

            var byLayer = new Dictionary<string, List<(string obj, Mesh m)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in file.Objects)
            {
                string rootLayer = layerName.TryGetValue(o.Attributes.LayerIndex, out var ln0) ? ln0 : "";
                if (!linkLayers.Contains(rootLayer)) continue;   // skip import-layer duplicates
                CollectObject(file, o, Transform.Identity, rootLayer, byLayer);
            }

            var baseMeshes = new Dictionary<string, List<Mesh>>();
            var missing = new List<string>();
            var diag = new List<string>();
            foreach (var link in def.Links)
            {
                var parts = new List<Mesh>();
                if (byLayer.TryGetValue(link.Layer, out var items))
                    foreach (var (obj, m) in items)
                    {
                        if (!string.IsNullOrEmpty(link.Object) &&
                            !string.Equals(obj, link.Object, StringComparison.OrdinalIgnoreCase))
                            continue;
                        parts.Add(m);
                    }
                if (parts.Count == 0)
                {
                    if (!string.IsNullOrEmpty(link.Layer))   // empty layer = virtual link (e.g. carriage)
                        missing.Add(link.Name + " [" + link.Layer + (link.Object != null ? "/" + link.Object : "") + "]");
                    baseMeshes[link.Name] = new List<Mesh>();
                    continue;
                }
                var (mesh, note) = CleanLink(parts);
                baseMeshes[link.Name] = mesh.Faces.Count > 0 ? new List<Mesh> { mesh } : new List<Mesh>();
                diag.Add($"{link.Name}:{note}");
            }

            ApplyAxialFit(def, baseMeshes);
            RecenterBaseOnAxis(def, baseMeshes);
            foreach (var kv in baseMeshes)
                foreach (var m in kv.Value) { m.UnifyNormals(); m.RebuildNormals(); m.Compact(); }

            var warnings = new List<string>();
            if (def.ParseWarnings.Count > 0)
                warnings.Add("robot.xml: " + string.Join("; ", def.ParseWarnings));
            if (bytes.Length != CalibratedAssemblyBytes)
                warnings.Add($"Robot_Assembly.3dm changed since the base placement was " +
                    $"calibrated ({bytes.Length} vs {CalibratedAssemblyBytes} bytes) - re-verify " +
                    $"robot.xml <BasePlacement> against the drawn robot");

            return new RobotModel(def, baseMeshes, missing, string.Join("  ", diag), warnings);
        }

        /// <summary>Fuse a link's part meshes into ONE clean mesh. The flicker is two
        /// overlapping mechanical parts (e.g. slider on rail, joint on arm) whose contact
        /// surfaces sit at the same depth - so when every part is a closed solid we boolean-
        /// union them, dissolving the shared/internal faces. Otherwise (open meshes) we just
        /// weld + drop exact duplicate faces. A sanity check guards against an empty/blown-up
        /// union so a link can never vanish. Note = "&lt;parts&gt;p/&lt;kept&gt;k/&lt;closed&gt;c[+U]/&lt;faces&gt;f".</summary>
        static (Mesh mesh, string note) CleanLink(List<Mesh> parts)
        {
            var kept = DedupeShells(parts);
            var prepped = new List<Mesh>();
            var partNotes = new List<string>();
            int closed = 0, maxFaces = 0;
            foreach (var m in kept)
            {
                var d = m.DuplicateMesh();
                d.Faces.CullDegenerateFaces();
                d.Vertices.CombineIdentical(true, true);
                if (!d.IsClosed) d.HealNakedEdges(0.5);   // zip tiny import gaps so it can union (won't cap real holes)
                d.UnifyNormals();
                d.RebuildNormals();
                if (d.Faces.Count == 0) continue;
                bool c = d.IsClosed;
                if (c) closed++;
                if (d.Faces.Count > maxFaces) maxFaces = d.Faces.Count;
                partNotes.Add((c ? "C" : "O") + d.Faces.Count);
                prepped.Add(d);
            }

            Mesh? big = null;
            bool unioned = false;
            string? unionProblem = null;
            if (prepped.Count > 1 && closed == prepped.Count)
            {
                try
                {
                    var u = Mesh.CreateBooleanUnion(prepped);
                    if (u != null && u.Length > 0)
                    {
                        var r = new Mesh();
                        foreach (var x in u) r.Append(x);
                        if (r.Faces.Count >= maxFaces / 2) { big = r; unioned = true; }  // sanity: not collapsed
                        else unionProblem = "union collapsed (kept append fallback)";
                    }
                    else unionProblem = "union returned nothing (kept append fallback)";
                }
                catch (Exception ex) { unionProblem = "union threw: " + ex.Message; }
            }
            if (big == null)
            {
                big = new Mesh();
                foreach (var m in prepped) big.Append(m);
                big.Vertices.CombineIdentical(true, true);
                RemoveDuplicateFaces(big);
            }
            big.UnifyNormals();
            big.RebuildNormals();
            big.Compact();

            string note = $"{parts.Count}p/{closed}c{(unioned ? "+U" : "")}/{big.Faces.Count}f[{string.Join(",", partNotes)}]"
                          + (unionProblem != null ? $" !{unionProblem}" : "");
            return (big, note);
        }

        // Drop whole duplicate shells: a part imported/placed twice (same shape, possibly a
        // different triangulation, so per-face dedupe can't see it). Match by geometric
        // signature - bounding box + surface area - and keep the first. Conservative: a
        // duplicate must match BOTH (within 1mm box / 1% area); genuinely distinct parts differ
        // far more, so this never drops the slider-vs-arm kind of pairing.
        static List<Mesh> DedupeShells(List<Mesh> meshes)
        {
            var kept = new List<Mesh>();
            var sigs = new List<(BoundingBox bb, double area)>();
            foreach (var m in meshes)
            {
                var bb = m.GetBoundingBox(true);
                double area = AreaMassProperties.Compute(m)?.Area ?? 0.0;
                bool dup = false;
                foreach (var (sb, sa) in sigs)
                    if (BoxMatch(sb, bb) && Math.Abs(sa - area) <= 0.01 * Math.Max(sa, area) + 0.5)
                    { dup = true; break; }
                if (!dup) { kept.Add(m); sigs.Add((bb, area)); }
            }
            return kept;
        }

        static bool BoxMatch(BoundingBox a, BoundingBox b, double tol = 1.0)
            => a.Min.DistanceTo(b.Min) <= tol && a.Max.DistanceTo(b.Max) <= tol;

        static void RemoveDuplicateFaces(Mesh m)
        {
            var seen = new HashSet<(int, int, int, int)>();
            var dup = new List<int>();
            for (int i = 0; i < m.Faces.Count; i++)
            {
                var f = m.Faces[i];
                int[] v = { f.A, f.B, f.C, f.IsQuad ? f.D : f.C };
                Array.Sort(v);
                if (!seen.Add((v[0], v[1], v[2], v[3]))) dup.Add(i);
            }
            if (dup.Count > 0) m.Faces.DeleteFaces(dup);
        }

        // Stretch each arm mesh along its axis so its joint span equals the firmware length,
        // and reconnect link2 to the firmware-distance elbow. Mirrors RobotPosing's pivots.
        static void ApplyAxialFit(RobotDefinition def, Dictionary<string, List<Mesh>> baseMeshes)
        {
            var jb = def.Joint("J1"); var je = def.Joint("J3"); var jt = def.Joint("Jtip");
            if (jb == null || je == null || jt == null) return;
            var shoulder = P(jb.Pivot); var elbowMeas = P(je.Pivot); var nozzleMeas = P(jt.Pivot);
            double L1 = def.Link("link1")?.Length ?? 0.0;
            double L2 = def.Link("link2")?.Length ?? 0.0;

            var u1 = UnitXY(elbowMeas - shoulder);
            double span1 = XYLen(elbowMeas - shoulder);
            var elbowK = new Point3d(shoulder.X + L1 * u1.X, shoulder.Y + L1 * u1.Y, elbowMeas.Z);

            if (span1 > 1e-9 && L1 > 0 && baseMeshes.TryGetValue("link1", out var l1m))
            {
                var x1 = ScaleAlong(shoulder, u1, L1 / span1);
                foreach (var m in l1m) m.Transform(x1);
            }

            var u2 = UnitXY(nozzleMeas - elbowMeas);
            double span2 = XYLen(nozzleMeas - elbowMeas);
            var reconnect = Transform.Translation(elbowK - elbowMeas);
            var x2 = (span2 > 1e-9 && L2 > 0) ? ScaleAlong(elbowK, u2, L2 / span2) : Transform.Identity;
            foreach (var name in new[] { "link2", "tool" })
                if (baseMeshes.TryGetValue(name, out var ms))
                    foreach (var m in ms) { m.Transform(reconnect); m.Transform(x2); }
        }

        // The fixed base (Fundament) is a separate ground mount; the column rotates on top of
        // it. Center its footprint on the vertical rotation axis (the J1/shoulder XY) so that
        // after the assembly->world registration (shoulder -> origin) it sits roughly centred
        // under the column and does not swing as J1 turns. The exact placement is the manual
        // calibration below (BakedBase*), applied at pose time so it can be dialed live.
        static void RecenterBaseOnAxis(RobotDefinition def, Dictionary<string, List<Mesh>> baseMeshes)
        {
            if (!baseMeshes.TryGetValue(BaseLink, out var meshes) || meshes.Count == 0) return;
            var j1 = def.Joint("J1");
            if (j1 == null) return;
            var bb = BoundingBox.Empty;
            foreach (var m in meshes) bb.Union(m.GetBoundingBox(true));
            if (!bb.IsValid) return;
            double cx = 0.5 * (bb.Min.X + bb.Max.X);
            double cy = 0.5 * (bb.Min.Y + bb.Max.Y);
            var t = Transform.Translation(j1.Pivot.X - cx, j1.Pivot.Y - cy, 0.0);
            foreach (var m in meshes) m.Transform(t);
        }

        const string BaseLink = "base";

        /// <summary>Fallback base placement for a definition that predates
        /// &lt;BasePlacement&gt; in robot.xml. The shipped robot.xml carries the values,
        /// verified against the machine on 2026-08-26 - change them THERE, next to the
        /// joints, where RobotModelTests pins them.</summary>
        public const double BakedBaseRotDeg = -180.0;
        public const double BakedBaseOffsetX = 3.0;
        // 2026-09-04: 82 -> 44, tracking the shipped robot.xml. The fallback is only ever
        // paired with the embedded assembly, so it has to describe the same machine.
        public const double BakedBaseOffsetY = 44.0;

        /// <summary>Base placement this definition declares, else the legacy fallback.
        /// Rotation (deg, CCW about the vertical J1 axis) then an XY offset (mm); the
        /// registration is a pure translation, so both read directly in the viewport.</summary>
        public (double rotDeg, double offX, double offY) BasePlacement
            => Def.BasePlacement ?? (BakedBaseRotDeg, BakedBaseOffsetX, BakedBaseOffsetY);
        /// <summary>Byte size of Robot_Assembly.3dm the baked constants were calibrated
        /// against (2026-07-13/14; re-pinned 2026-07-17 after the sanitizing re-export,
        /// which flattened block instances to world-coordinate breps - geometry
        /// verified identical per-part, so the baked placement still holds). Any edit
        /// to the .3dm changes this - LoadEmbedded then flags that the baked placement
        /// may be stale instead of silently mis-posing.</summary>
        public const long CalibratedAssemblyBytes = 1634517;

        /// <summary>Posed meshes (world coords) at the given machine joint values,
        /// with the base placement this definition declares.</summary>
        public List<(Mesh mesh, string link)> PosedMeshes(double j1, double j3, double j2)
        {
            var (rot, offX, offY) = BasePlacement;
            return PosedMeshes(j1, j3, j2, rot, offX, offY);
        }

        /// <summary>Posed meshes (world coords) at the given machine joint values.
        /// baseRotDeg/baseOffX/baseOffY place the base (Fundament) mesh: rotate about
        /// the vertical J1 axis, then shift in XY.</summary>
        public List<(Mesh mesh, string link)> PosedMeshes(double j1, double j3, double j2,
            double baseRotDeg, double baseOffX, double baseOffY)
        {
            var outp = new List<(Mesh, string)>();
            var (dj1, dj3) = DrawnJoints(j1, j3);
            var xforms = RobotPosing.LinkWorldTransforms(Def, dj1, dj3, j2);
            var baseFix = BaseFixTransform(baseRotDeg, baseOffX, baseOffY);

            foreach (var kv in _base)
            {
                if (kv.Value.Count == 0) continue;
                if (!xforms.TryGetValue(kv.Key, out var m16)) continue;
                var x = ToRhino(m16);
                if (kv.Key == BaseLink && !baseFix.Equals(Transform.Identity))
                    x = x * baseFix;   // assembly-space correction first, then registration
                foreach (var bm in kv.Value)
                {
                    var d = bm.DuplicateMesh();
                    d.Transform(x);
                    outp.Add((d, kv.Key));
                }
            }
            return outp;
        }

        /// <summary>Per-link world transforms at the given machine joint values, for a
        /// caller that draws the shared base meshes under a model transform instead of
        /// duplicating them - the live viewport's per-frame path. Same math and base
        /// placement as <see cref="PosedMeshes(double,double,double)"/>.</summary>
        public List<(string link, Transform x)> PosedLinkTransforms(double j1, double j3, double j2)
        {
            var (rot, offX, offY) = BasePlacement;
            var outp = new List<(string, Transform)>();
            var (dj1, dj3) = DrawnJoints(j1, j3);
            var xforms = RobotPosing.LinkWorldTransforms(Def, dj1, dj3, j2);
            var baseFix = BaseFixTransform(rot, offX, offY);
            foreach (var kv in _base)
            {
                if (kv.Value.Count == 0) continue;
                if (!xforms.TryGetValue(kv.Key, out var m16)) continue;
                var x = ToRhino(m16);
                if (kv.Key == BaseLink && !baseFix.Equals(Transform.Identity))
                    x = x * baseFix;
                outp.Add((kv.Key, x));
            }
            return outp;
        }

        /// <summary>The shared base meshes of one link (assembly coordinates, axial fit
        /// applied). Read-only by contract: every consumer in the process shares these
        /// instances, so a caller draws them under a transform and never modifies them.
        /// Internal so the contract stays within this assembly.</summary>
        internal IReadOnlyList<Mesh> BaseMeshesOf(string link)
            => _base.TryGetValue(link, out var meshes) ? meshes : Array.Empty<Mesh>();

        /// <summary>Cached base feature edges of one link at the given crease angle;
        /// transform per frame, never recompute.</summary>
        internal Line[] BaseFeatureEdgesOf(string link, double angleRad)
        {
            EnsureEdgeCache(angleRad);
            return _baseEdges!.TryGetValue(link, out var edges) ? edges : Array.Empty<Line>();
        }

        /// <summary>Machine joint values -&gt; the joint values that draw the same arm in
        /// the RHINO frame. The two frames differ by a reflection about y = -x (Core
        /// MachineFrame), and reflecting a planar arm is exactly the same as posing it
        /// at reflected joint values: the reflection maps a direction at angle phi to
        /// -90 - phi, so the absolute proximal angle becomes -90 - J1 and the RELATIVE
        /// elbow angle simply negates.
        ///
        /// Posing this way rather than mirroring the meshes matters: every joint centre
        /// lands in exactly the same place either way, but a mirrored mesh draws each
        /// part inside out - which is what turned the Fundament through what looked
        /// like 90 degrees in the first 1.13.0 build. Callers keep passing (and
        /// publishing) the machine's own joint values.</summary>
        internal static (double j1, double j3) DrawnJoints(double j1, double j3)
            => (-90.0 - j1, -j3);

        // feature edges of the BASE meshes, extracted once per angle and transformed
        // per scrub frame - recomputing TopologyEdges on every posed duplicate was a
        // per-tick hotspot on the ~100k-face assembly
        Dictionary<string, Line[]>? _baseEdges;
        double _baseEdgesAngle = double.NaN;

        /// <summary>Feature edges (world coords) at the given machine joint values -
        /// same crease/naked-edge definition the viewport uses, but cached on the base
        /// meshes and merely transformed per frame.</summary>
        public List<Line> PosedFeatureEdges(double j1, double j3, double j2, double angleRad)
        {
            var (rot, offX, offY) = BasePlacement;
            return PosedFeatureEdges(j1, j3, j2, angleRad, rot, offX, offY);
        }

        /// <summary>Feature edges with an explicit base placement - the re-calibration
        /// entry point, paired with the PosedMeshes overload below it. Dial values in
        /// through these, then write the result into robot.xml's &lt;BasePlacement&gt;.</summary>
        public List<Line> PosedFeatureEdges(double j1, double j3, double j2, double angleRad,
            double baseRotDeg, double baseOffX, double baseOffY)
        {
            EnsureEdgeCache(angleRad);
            var outp = new List<Line>();
            var (dj1, dj3) = DrawnJoints(j1, j3);
            var xforms = RobotPosing.LinkWorldTransforms(Def, dj1, dj3, j2);
            var baseFix = BaseFixTransform(baseRotDeg, baseOffX, baseOffY);
            foreach (var kv in _baseEdges!)
            {
                if (kv.Value.Length == 0) continue;
                if (!xforms.TryGetValue(kv.Key, out var m16)) continue;
                var x = ToRhino(m16);
                if (kv.Key == BaseLink && !baseFix.Equals(Transform.Identity))
                    x = x * baseFix;
                foreach (var l in kv.Value)
                {
                    var a = l.From; var b = l.To;
                    a.Transform(x); b.Transform(x);
                    outp.Add(new Line(a, b));
                }
            }
            return outp;
        }

        void EnsureEdgeCache(double angleRad)
        {
            if (_baseEdges != null && Math.Abs(_baseEdgesAngle - angleRad) <= 1e-12) return;
            var cache = new Dictionary<string, Line[]>();
            foreach (var kv in _base)
            {
                var lines = new List<Line>();
                foreach (var bm in kv.Value) CollectFeatureEdges(bm, angleRad, lines);
                cache[kv.Key] = lines.ToArray();
            }
            _baseEdges = cache;
            _baseEdgesAngle = angleRad;
        }

        /// <summary>Naked boundaries + creases sharper than angleRad.</summary>
        static void CollectFeatureEdges(Mesh m, double angleRad, List<Line> outp)
            => MeshFeatureEdges.Collect(m, angleRad, outp);

        Transform BaseFixTransform(double baseRotDeg, double baseOffX, double baseOffY)
        {
            var j1jt = Def.Joint("J1");
            if (j1jt == null ||
                (Math.Abs(baseRotDeg) <= 1e-9 && Math.Abs(baseOffX) <= 1e-9 && Math.Abs(baseOffY) <= 1e-9))
                return Transform.Identity;
            var pivot = new Point3d(j1jt.Pivot.X, j1jt.Pivot.Y, 0.0);
            return Transform.Translation(baseOffX, baseOffY, 0.0)
                 * Transform.Rotation(baseRotDeg * Math.PI / 180.0, Vector3d.ZAxis, pivot);
        }

        // Collect a .3dm object's MESH under rootLayer (the top-level kinematic layer),
        // recursing through block instances and accumulating their transforms so nested
        // assembly parts land in world coords. Handles mesh objects directly and meshes
        // breps/surfaces on the fly, so it works whichever the assembly stores.
        static void CollectObject(File3dm file, File3dmObject o, Transform xform,
            string rootLayer, Dictionary<string, List<(string, Mesh)>> byLayer)
        {
            var geo = o.Geometry;
            if (geo == null) return;

            if (geo is InstanceReferenceGeometry iref)
            {
                var def = file.AllInstanceDefinitions.FindId(iref.ParentIdefId);
                if (def == null) return;
                var childX = Transform.Multiply(xform, iref.Xform);
                foreach (var id in def.GetObjectIds())
                {
                    var member = file.Objects.FindId(id);
                    if (member != null) CollectObject(file, member, childX, rootLayer, byLayer);
                }
                return;
            }

            Mesh? mesh = geo is Mesh gm ? gm.DuplicateMesh() : GhConvert.AsMesh(geo);
            if (mesh == null) return;
            mesh.Transform(xform);
            if (!byLayer.TryGetValue(rootLayer, out var lst)) { lst = new List<(string, Mesh)>(); byLayer[rootLayer] = lst; }
            lst.Add((o.Attributes.Name ?? "", mesh));
        }

        // ---- helpers ----
        static Point3d P(Vec3 v) => new Point3d(v.X, v.Y, v.Z);
        static double XYLen(Vector3d v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
        static Vector3d UnitXY(Vector3d v)
        {
            double n = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return n < 1e-12 ? new Vector3d(1, 0, 0) : new Vector3d(v.X / n, v.Y / n, 0);
        }

        static Transform ScaleAlong(Point3d origin, Vector3d axisXY, double factor)
        {
            var x = axisXY; x.Unitize();
            var y = Vector3d.CrossProduct(Vector3d.ZAxis, x); y.Unitize();
            var plane = new Plane(origin, x, y);
            return Transform.Scale(plane, factor, 1.0, 1.0);
        }

        static Transform ToRhino(double[] m)
        {
            var x = Transform.Identity;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    x[i, j] = m[i * 4 + j];
            return x;
        }
    }
}
