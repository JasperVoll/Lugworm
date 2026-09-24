using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Lugworm.Core.Geometry;

namespace Lugworm.Convert
{
    /// <summary>RhinoCommon &lt;-&gt; Core marshalling + mesh/brep sectioning.
    /// This is the ONLY place the engine touches Rhino - the Core library itself
    /// has no Rhino dependency (keeps it GH2-portable). Ports the adapter helpers
    /// from ClaySlicer2.py / ClayGcode2.py.</summary>
    public static class GhConvert
    {
        public static Vec3 ToVec(Point3d p) => new Vec3(p.X, p.Y, p.Z);
        public static Point3d ToPt(Vec3 v) => new Point3d(v.X, v.Y, v.Z);

        /// <summary>Curve -> point list. TryGetPolyline -> ToPolyline -> DivideByCount.
        /// <paramref name="scaleToMm"/> converts a document-unit curve to Core's mm
        /// frame first (AUD-COR-001); <paramref name="tol"/> is always mm.</summary>
        public static List<Vec3> CurveToPts(Curve? crv, double tol, double scaleToMm = 1.0)
        {
            var outp = new List<Vec3>();
            if (crv == null) return outp;
            if (!DocUnits.IsUnity(scaleToMm))
            {
                var dup = crv.DuplicateCurve();
                dup.Transform(DocUnits.ToMmTransform(scaleToMm));
                crv = dup;
            }
            if (crv.TryGetPolyline(out Polyline pl) && pl != null && pl.Count >= 2)
            {
                foreach (var p in pl) outp.Add(ToVec(p));
                return outp;
            }
            var pc = crv.ToPolyline(tol, 0.0, 0.0, 0.0);
            if (pc != null)
            {
                var pl2 = pc.ToPolyline();
                if (pl2 != null && pl2.Count >= 2)
                {
                    foreach (var p in pl2) outp.Add(ToVec(p));
                    return outp;
                }
            }
            int n = System.Math.Max(2, (int)(crv.GetLength() / System.Math.Max(tol, 0.1)));
            var ts = crv.DivideByCount(n, true);
            if (ts != null)
                foreach (var t in ts) outp.Add(ToVec(crv.PointAt(t)));
            return outp;
        }

        /// <summary>mm points -> document-unit polyline curve (divide by
        /// <paramref name="scaleToMm"/> on the way out, AUD-COR-001).</summary>
        public static PolylineCurve PtsToCurve(IReadOnlyList<Vec3> pts, double scaleToMm = 1.0)
        {
            var poly = new Polyline(pts.Count);
            if (DocUnits.IsUnity(scaleToMm))
                foreach (var p in pts) poly.Add(p.X, p.Y, p.Z);
            else
                foreach (var p in pts) poly.Add(p.X / scaleToMm, p.Y / scaleToMm, p.Z / scaleToMm);
            return poly.ToPolylineCurve();
        }

        /// <summary>Mesh/Brep/Surface/Extrusion -> a clean triangulated mesh, or null.</summary>
        public static Mesh? AsMesh(GeometryBase geo)
        {
            Mesh m;
            if (geo is Mesh mesh) m = mesh.DuplicateMesh();
            else
            {
                Brep? brep = geo as Brep;
                if (brep == null && geo is Surface srf) brep = srf.ToBrep();
                if (brep == null && geo is Extrusion ext) brep = ext.ToBrep();
                if (brep == null) return null;
                var ms = Mesh.CreateFromBrep(brep, MeshingParameters.Default);
                if (ms == null || ms.Length == 0) return null;
                m = new Mesh();
                foreach (var s in ms) m.Append(s);
            }
            m.Faces.ConvertQuadsToTriangles();
            m.Vertices.CombineIdentical(true, true);
            m.Normals.ComputeNormals();
            return m;
        }

        public static (List<Vec3> V, List<(int, int, int)> F) MeshVF(Mesh mesh)
        {
            var V = new List<Vec3>(mesh.Vertices.Count);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                var p = mesh.Vertices[i];
                V.Add(new Vec3(p.X, p.Y, p.Z));
            }
            var F = new List<(int, int, int)>(mesh.Faces.Count);
            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                var f = mesh.Faces[i];
                if (f.IsTriangle) F.Add((f.A, f.B, f.C));
                else { F.Add((f.A, f.B, f.C)); F.Add((f.A, f.C, f.D)); }
            }
            return (V, F);
        }

        /// <summary>Planar section at height z -> list of closed point-loops.</summary>
        public static List<List<Vec3>> Section(Mesh? mesh, Brep? brep, double z, double tol)
        {
            var plane = new Plane(new Point3d(0, 0, z), Vector3d.ZAxis);
            var crvs = new List<Curve>();
            if (mesh != null)
            {
                var pls = Rhino.Geometry.Intersect.Intersection.MeshPlane(mesh, plane);
                if (pls != null)
                    foreach (var pl in pls)
                        if (pl != null && pl.Count >= 2) crvs.Add(new PolylineCurve(pl));
            }
            if (brep != null)
            {
                if (Rhino.Geometry.Intersect.Intersection.BrepPlane(brep, plane, tol, out Curve[] outCrvs, out _)
                    && outCrvs != null)
                    foreach (var c in outCrvs) if (c != null) crvs.Add(c);
            }
            if (crvs.Count > 0)
            {
                var joined = Curve.JoinCurves(crvs, tol);
                if (joined != null && joined.Length > 0) crvs = new List<Curve>(joined);
            }
            var outp = new List<List<Vec3>>();
            foreach (var c in crvs)
            {
                var pts = CurveToPts(c, tol);
                if (pts.Count >= 2)
                {
                    if (pts.Count >= 3 && !PolylineOps.IsClosed(pts, 1e-6)
                        && PolylineOps.Dist(pts[0], pts[pts.Count - 1]) <= Math.Max(tol, 1e-3))
                        pts.Add(pts[0]);
                    outp.Add(pts);
                }
            }
            return outp;
        }
    }
}
