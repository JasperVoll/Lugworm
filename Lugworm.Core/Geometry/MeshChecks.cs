using System;
using System.Collections.Generic;
using System.Text;

namespace Lugworm.Core.Geometry
{
    /// <summary>Result of <see cref="MeshChecks.Analyze"/> on an indexed triangle mesh.
    /// All counts refer to the V/F soup as given (after any upstream welding).</summary>
    public sealed class MeshCheckReport
    {
        public int VertexCount;
        public int FaceCount;
        /// <summary>Faces with area below the degeneracy threshold.</summary>
        public List<int> DegenerateFaces = new List<int>();
        /// <summary>Vertices referenced by no face (would create singular Laplacian rows).</summary>
        public List<int> UnreferencedVertices = new List<int>();
        /// <summary>Edges used by exactly one face (naked / boundary edges).</summary>
        public int NakedEdgeCount;
        /// <summary>Edges used by three or more faces.</summary>
        public int NonManifoldEdgeCount;
        /// <summary>Closed boundary loops stitched from the naked edges.</summary>
        public int BoundaryLoopCount;
        /// <summary>Interior edges whose two faces traverse them in the SAME direction
        /// (i.e. the faces disagree about orientation).</summary>
        public int OrientationConflicts;
        /// <summary>Edge-connected face components.</summary>
        public int ComponentCount;
        /// <summary>Face count per component, largest first.</summary>
        public List<int> ComponentSizes = new List<int>();
        /// <summary>Component index per face (parallel to F).</summary>
        public int[] FaceComponent = Array.Empty<int>();

        public bool IsClosed => NakedEdgeCount == 0;
        public bool IsEdgeManifold => NonManifoldEdgeCount == 0;
        public bool IsOrientationConsistent => OrientationConflicts == 0;

        /// <summary>Blocking problems for slicing (as opposed to warnings).</summary>
        public bool HasErrors => FaceCount == 0 || NonManifoldEdgeCount > 0;

        /// <summary>Human-readable one-line-per-finding description. Empty list = clean.</summary>
        public List<string> Describe()
        {
            var msgs = new List<string>();
            if (FaceCount == 0) { msgs.Add("mesh has no faces"); return msgs; }
            if (NonManifoldEdgeCount > 0)
                msgs.Add($"{NonManifoldEdgeCount} non-manifold edge(s) (an edge shared by 3+ faces) - slicing would be ambiguous there");
            if (NakedEdgeCount > 0)
                msgs.Add($"mesh is open: {NakedEdgeCount} naked edge(s) in {BoundaryLoopCount} boundary loop(s) - sections there may be open curves");
            if (OrientationConflicts > 0)
                msgs.Add($"{OrientationConflicts} edge(s) with inconsistent face orientation (flipped normals)");
            if (DegenerateFaces.Count > 0)
                msgs.Add($"{DegenerateFaces.Count} degenerate (zero-area) face(s) - ignored, but they degrade heat-method quality");
            if (UnreferencedVertices.Count > 0)
                msgs.Add($"{UnreferencedVertices.Count} unreferenced vertex/vertices (used by no face)");
            if (ComponentCount > 1)
                msgs.Add($"mesh has {ComponentCount} disconnected components (face counts: {string.Join(", ", ComponentSizes)})");
            return msgs;
        }
    }

    /// <summary>Pure topology / quality checks and repairs on an indexed triangle mesh
    /// (V + F soup, same representation the heat method uses). No Rhino dependency, so
    /// everything here is unit-testable; the plugin layer wraps this for user reporting.</summary>
    public static class MeshChecks
    {
        const double DegenerateArea = 1e-12;

        static (int, int) UndirectedKey(int a, int b) => a < b ? (a, b) : (b, a);

        static double FaceArea(IReadOnlyList<Vec3> V, (int a, int b, int c) f)
        {
            Vec3 p = V[f.a], q = V[f.b], r = V[f.c];
            double ux = q.X - p.X, uy = q.Y - p.Y, uz = q.Z - p.Z;
            double vx = r.X - p.X, vy = r.Y - p.Y, vz = r.Z - p.Z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        /// <summary>Full topology and quality analysis. O(V + F).</summary>
        public static MeshCheckReport Analyze(IReadOnlyList<Vec3> V, IReadOnlyList<(int a, int b, int c)> F)
        {
            var rep = new MeshCheckReport { VertexCount = V.Count, FaceCount = F.Count };
            if (F.Count == 0) return rep;

            // edge -> list of (face index, traversed a->b as stored in the face)
            var edges = new Dictionary<(int, int), List<(int face, bool forward)>>();
            var referenced = new bool[V.Count];

            for (int fi = 0; fi < F.Count; fi++)
            {
                var f = F[fi];
                referenced[f.a] = referenced[f.b] = referenced[f.c] = true;
                if (FaceArea(V, f) < DegenerateArea) rep.DegenerateFaces.Add(fi);

                foreach (var (u, v) in new[] { (f.a, f.b), (f.b, f.c), (f.c, f.a) })
                {
                    var key = UndirectedKey(u, v);
                    if (!edges.TryGetValue(key, out var lst)) { lst = new List<(int, bool)>(2); edges[key] = lst; }
                    lst.Add((fi, u < v));
                }
            }

            for (int i = 0; i < V.Count; i++) if (!referenced[i]) rep.UnreferencedVertices.Add(i);

            // edge classification + orientation consistency
            var nakedEdges = new List<(int, int)>();
            foreach (var kv in edges)
            {
                int n = kv.Value.Count;
                if (n == 1) { rep.NakedEdgeCount++; nakedEdges.Add(kv.Key); }
                else if (n > 2) rep.NonManifoldEdgeCount++;
                else if (kv.Value[0].forward == kv.Value[1].forward)
                    rep.OrientationConflicts++;   // both faces traverse the edge the same way
            }

            rep.BoundaryLoopCount = CountBoundaryLoops(nakedEdges);

            // connected components over faces (shared edge = connected)
            var parent = new int[F.Count];
            for (int i = 0; i < F.Count; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int x, int y) { int rx = Find(x), ry = Find(y); if (rx != ry) parent[rx] = ry; }
            foreach (var kv in edges)
                for (int i = 1; i < kv.Value.Count; i++)
                    Union(kv.Value[0].face, kv.Value[i].face);

            var compIds = new Dictionary<int, int>();
            rep.FaceComponent = new int[F.Count];
            var sizes = new List<int>();
            for (int fi = 0; fi < F.Count; fi++)
            {
                int root = Find(fi);
                if (!compIds.TryGetValue(root, out int id)) { id = compIds.Count; compIds[root] = id; sizes.Add(0); }
                rep.FaceComponent[fi] = id;
                sizes[id]++;
            }
            rep.ComponentCount = compIds.Count;
            sizes.Sort((x, y) => y.CompareTo(x));
            rep.ComponentSizes = sizes;
            return rep;
        }

        static int CountBoundaryLoops(List<(int a, int b)> nakedEdges)
        {
            if (nakedEdges.Count == 0) return 0;
            // union-find over the vertices of naked edges: each connected chain/loop counts once
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
                while (p != x) { x = p; p = parent[x]; }
                return x;
            }
            void Union(int x, int y)
            {
                int rx = Find(x), ry = Find(y);
                if (rx != ry) parent[rx] = ry;
            }
            foreach (var (a, b) in nakedEdges) Union(a, b);
            var roots = new HashSet<int>();
            foreach (var (a, b) in nakedEdges) { roots.Add(Find(a)); roots.Add(Find(b)); }
            return roots.Count;
        }

        /// <summary>Makes face orientation consistent per component by BFS flood-fill from
        /// the first face of each component, flipping faces that disagree with their
        /// neighbour across a shared edge. Returns the new face list and how many faces
        /// were flipped. Non-manifold edges are skipped (orientation is ambiguous there).
        /// Note: this makes orientation CONSISTENT, not necessarily outward - outwardness
        /// needs geometry (volume sign), which callers can apply per component if needed.</summary>
        public static (List<(int a, int b, int c)> F, int flipped) UnifyOrientation(
            IReadOnlyList<(int a, int b, int c)> F)
        {
            var result = new List<(int, int, int)>(F.Count);
            foreach (var f in F) result.Add(f);
            if (F.Count == 0) return (result, 0);

            // adjacency: undirected edge -> faces
            var edges = new Dictionary<(int, int), List<int>>();
            for (int fi = 0; fi < F.Count; fi++)
            {
                var f = F[fi];
                foreach (var (u, v) in new[] { (f.a, f.b), (f.b, f.c), (f.c, f.a) })
                {
                    var key = UndirectedKey(u, v);
                    if (!edges.TryGetValue(key, out var lst)) { lst = new List<int>(2); edges[key] = lst; }
                    lst.Add(fi);
                }
            }

            static IEnumerable<(int u, int v)> Directed((int a, int b, int c) f)
            { yield return (f.a, f.b); yield return (f.b, f.c); yield return (f.c, f.a); }

            var visited = new bool[F.Count];
            int flipped = 0;
            for (int seed = 0; seed < F.Count; seed++)
            {
                if (visited[seed]) continue;
                visited[seed] = true;
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    var f = result[fi];
                    foreach (var (u, v) in Directed(f))
                    {
                        var key = UndirectedKey(u, v);
                        var lst = edges[key];
                        if (lst.Count != 2) continue;   // naked or non-manifold: skip
                        int other = lst[0] == fi ? lst[1] : lst[0];
                        if (visited[other]) continue;
                        visited[other] = true;
                        // consistent orientation = the neighbour traverses the shared edge
                        // in the OPPOSITE direction (v -> u)
                        bool consistent = false;
                        foreach (var (ou, ov) in Directed(result[other]))
                            if (ou == v && ov == u) { consistent = true; break; }
                        if (!consistent)
                        {
                            var o = result[other];
                            result[other] = (o.Item1, o.Item3, o.Item2);
                            flipped++;
                        }
                        queue.Enqueue(other);
                    }
                }
            }
            return (result, flipped);
        }

        /// <summary>Splits the face list into edge-connected components (using a
        /// previously computed report). Vertices are re-indexed per component.
        /// Returns per component: (V, F, map from local to original vertex index).</summary>
        public static List<(List<Vec3> V, List<(int a, int b, int c)> F, List<int> toOriginal)> SplitComponents(
            IReadOnlyList<Vec3> V, IReadOnlyList<(int a, int b, int c)> F, MeshCheckReport report)
        {
            var outp = new List<(List<Vec3>, List<(int, int, int)>, List<int>)>();
            if (report.ComponentCount <= 0) return outp;
            for (int c = 0; c < report.ComponentCount; c++)
            {
                var map = new Dictionary<int, int>();
                var cv = new List<Vec3>();
                var cf = new List<(int, int, int)>();
                var toOrig = new List<int>();
                int Local(int orig)
                {
                    if (!map.TryGetValue(orig, out int l))
                    { l = cv.Count; map[orig] = l; cv.Add(V[orig]); toOrig.Add(orig); }
                    return l;
                }
                for (int fi = 0; fi < F.Count; fi++)
                {
                    if (report.FaceComponent[fi] != c) continue;
                    var f = F[fi];
                    cf.Add((Local(f.a), Local(f.b), Local(f.c)));
                }
                outp.Add((cv, cf, toOrig));
            }
            return outp;
        }
    }
}
