using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Geometry;

namespace Lugworm.Validate
{
    /// <summary>Validation verdict for one input object. Errors block slicing of that
    /// object; warnings do not. Fixes list what was repaired automatically.</summary>
    public sealed class InputReport
    {
        public int Index;
        public string TypeName = "";
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
        public readonly List<string> Fixes = new();
        public bool Ok => Errors.Count == 0;

        public string Describe()
        {
            var parts = new List<string>();
            foreach (var e in Errors) parts.Add($"obj[{Index}] ({TypeName}) REJECTED: {e}");
            foreach (var w in Warnings) parts.Add($"obj[{Index}] ({TypeName}) warning: {w}");
            foreach (var f in Fixes) parts.Add($"obj[{Index}] ({TypeName}) fixed: {f}");
            return string.Join("\n", parts);
        }
    }

    /// <summary>One validated, slicer-ready input object.</summary>
    public sealed class PreparedInput
    {
        public int Index;                    // position in the raw input list
        public GeometryBase Geometry = null!; // original (mesh path may substitute repaired mesh)
        public Mesh? Mesh;                   // triangulated mesh (always set when Ok)
        public Brep? Brep;                   // non-null when the source was a Brep/Surface/Extrusion
        public InputReport Report = null!;
        public bool Ok => Report.Ok && Mesh != null;
    }

    /// <summary>Input validation layer for the Slicer (Phase 1). Converts each incoming
    /// object to a slicer-ready mesh (+Brep where available), runs Rhino-side and pure
    /// Core-side topology checks, applies safe automatic repairs (normal unification),
    /// and produces a human-readable report per object - a rejection is never a silent
    /// null. Pure decisions live in Core.MeshChecks (unit-tested); this class only
    /// bridges RhinoCommon.</summary>
    public static class GeometryInput
    {
        /// <summary>Validate + prepare a list of raw geometry inputs.
        /// <paramref name="mp"/> controls Brep→mesh conversion (null = Rhino default,
        /// matching the previous behaviour). <paramref name="scaleToMm"/> converts
        /// document-unit geometry into Core's mm frame on duplicated copies
        /// (AUD-COR-001); the caller's geometry is never mutated.</summary>
        public static List<PreparedInput> Prepare(IReadOnlyList<GeometryBase?> geos, MeshingParameters? mp,
                                                  double scaleToMm = 1.0)
        {
            var outp = new List<PreparedInput>();
            for (int i = 0; i < geos.Count; i++)
                outp.Add(PrepareOne(geos[i], i, mp, scaleToMm));
            return outp;
        }

        static PreparedInput PrepareOne(GeometryBase? geo, int index, MeshingParameters? mp, double scaleToMm)
        {
            var rep = new InputReport { Index = index, TypeName = geo?.GetType().Name ?? "null" };
            var prep = new PreparedInput { Index = index, Report = rep, Geometry = geo! };

            if (geo == null)
            {
                rep.Errors.Add("input is null (empty tree item or unsupported type reached the component)");
                return prep;
            }

            // ---- convert to Brep where applicable ----
            Mesh? mesh = geo is Mesh m ? m.DuplicateMesh() : null;
            if (mesh != null) DocUnits.ToMmInPlace(mesh, scaleToMm);
            Brep? brep = null;
            if (mesh == null)
            {
                if (geo is Brep b) brep = DocUnits.IsUnity(scaleToMm) ? b : b.DuplicateBrep();
                else if (geo is Surface s) brep = s.ToBrep();
                else if (geo is Extrusion e) brep = e.ToBrep();
                if (brep == null)
                {
                    rep.Errors.Add($"unsupported geometry type '{rep.TypeName}' - expected Mesh, Brep, Surface or Extrusion");
                    return prep;
                }
                if (!DocUnits.IsUnity(scaleToMm) && !ReferenceEquals(brep, geo))
                    DocUnits.ToMmInPlace(brep, scaleToMm);
                if (!brep.IsValid)
                    rep.Warnings.Add("Brep reports IsValid=false - meshing may fail or produce artefacts");
                if (!brep.IsSolid)
                    rep.Warnings.Add("Brep is not a closed solid - planar sections may be open curves");

                var pieces = Mesh.CreateFromBrep(brep, mp ?? MeshingParameters.Default);
                if (pieces == null || pieces.Length == 0)
                {
                    rep.Errors.Add("meshing the Brep produced no mesh (check the geometry or relax the meshing parameters)");
                    return prep;
                }
                mesh = new Mesh();
                foreach (var piece in pieces) mesh.Append(piece);
            }

            // ---- normalize the mesh like the previous pipeline did ----
            mesh.Faces.ConvertQuadsToTriangles();
            mesh.Vertices.CombineIdentical(true, true);

            if (!mesh.IsValid)
                rep.Warnings.Add("Rhino reports the mesh as invalid (Mesh.IsValid=false)");
            if (mesh.Faces.Count == 0)
            {
                rep.Errors.Add("mesh has no faces after triangulation");
                return prep;
            }

            // ---- pure topology checks (Core, unit-tested) ----
            var (V, F) = GhConvert.MeshVF(mesh);
            var check = MeshChecks.Analyze(V, F);

            if (check.NonManifoldEdgeCount > 0)
                rep.Errors.Add($"{check.NonManifoldEdgeCount} non-manifold edge(s) - repair the mesh (e.g. Rhino _MeshRepair) and retry");

            if (!check.IsClosed)
                rep.Warnings.Add($"mesh is open: {check.NakedEdgeCount} naked edge(s) in {check.BoundaryLoopCount} boundary loop(s) - sections may be open");

            if (check.OrientationConflicts > 0)
            {
                // safe auto-repair: unify normals (Rhino-side, matches viewport expectations)
                int flips = mesh.UnifyNormals();
                if (flips > 0)
                {
                    rep.Fixes.Add($"unified face normals ({flips} face(s) flipped)");
                    (V, F) = GhConvert.MeshVF(mesh);
                    check = MeshChecks.Analyze(V, F);
                }
                if (check.OrientationConflicts > 0)
                    rep.Warnings.Add($"{check.OrientationConflicts} edge(s) still have inconsistent face orientation after UnifyNormals");
            }

            if (check.DegenerateFaces.Count > 0)
                rep.Warnings.Add($"{check.DegenerateFaces.Count} degenerate (zero-area) face(s) - ignored by slicing, but they degrade heat-method quality");

            if (check.ComponentCount > 1)
                rep.Warnings.Add($"mesh has {check.ComponentCount} disconnected components - consider splitting into separate inputs (each prints as part of this object)");

            mesh.Normals.ComputeNormals();
            prep.Mesh = mesh;
            prep.Brep = brep;
            return prep;
        }
    }
}
