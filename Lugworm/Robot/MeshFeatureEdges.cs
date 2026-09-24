using System.Collections.Generic;
using Rhino.Geometry;

namespace Lugworm.Robot
{
    /// <summary>Naked boundaries + creases sharper than a threshold angle - the
    /// silhouette overlay that gives the flat/unlit robot meshes their form.
    ///
    /// Callers must extract these from the BASE meshes and transform the cached
    /// lines per frame: recomputing <see cref="Mesh.TopologyEdges"/> on every posed
    /// duplicate is a per-tick hotspot on the ~100k-face shipped assemblies.</summary>
    static class MeshFeatureEdges
    {
        public static void Collect(Mesh m, double angleRad, List<Line> outp)
        {
            m.FaceNormals.ComputeFaceNormals();
            var te = m.TopologyEdges;
            for (int i = 0; i < te.Count; i++)
            {
                var faces = te.GetConnectedFaces(i);
                bool draw = faces.Length < 2;   // naked boundary
                if (!draw && faces.Length >= 2)
                    draw = Vector3d.VectorAngle(m.FaceNormals[faces[0]], m.FaceNormals[faces[1]]) > angleRad;
                if (draw) outp.Add(te.EdgeLine(i));
            }
        }

        public static Line[] Collect(Mesh m, double angleRad)
        {
            var lines = new List<Line>();
            Collect(m, angleRad, lines);
            return lines.ToArray();
        }
    }
}
