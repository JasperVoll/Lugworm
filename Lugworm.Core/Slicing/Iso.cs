using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Mesh iso-contours via marching triangles over a per-vertex scalar.
    /// Used to turn a (geodesic or coordinate) scalar field into layer rings.
    /// Port of clay_core_v2 §7. Mesh = vertices V + triangle indices F.</summary>
    public static class Iso
    {
        /// <summary>Extract polylines where scalar == level. Returns list of point-lists.</summary>
        public static List<List<Vec3>> Contour(IReadOnlyList<Vec3> V,
                                               IReadOnlyList<(int a, int b, int c)> F,
                                               IReadOnlyList<double> scalar,
                                               double level, double tol = 1e-6)
        {
            var segs = new List<(Vec3, Vec3)>();
            foreach (var tri in F)
            {
                int[] idx = { tri.a, tri.b, tri.c };
                double[] vals = { scalar[idx[0]] - level, scalar[idx[1]] - level, scalar[idx[2]] - level };
                var crossing = new List<Vec3>(2);
                for (int a = 0; a < 3; a++)
                {
                    int b = (a + 1) % 3;
                    double va = vals[a], vb = vals[b];
                    if ((va <= 0 && 0 < vb) || (vb <= 0 && 0 < va))
                    {
                        double t = va / (va - vb);
                        // snap near-vertex crossings onto the vertex: when the level passes
                        // (numerically almost) through a vertex, all incident triangles then
                        // produce bit-identical endpoints, so stitching cannot fragment there.
                        // 1e-5 (was 1e-7): geodesic fields carry ~1e-6 solver noise relative
                        // to the level, which produced micro-scrap 2-point rings.
                        if (t < 1e-5) t = 0.0;
                        else if (t > 1.0 - 1e-5) t = 1.0;
                        Vec3 pa = V[idx[a]], pb = V[idx[b]];
                        crossing.Add(new Vec3(pa.X + t * (pb.X - pa.X),
                                              pa.Y + t * (pb.Y - pa.Y),
                                              pa.Z + t * (pb.Z - pa.Z)));
                    }
                }
                // a level passing exactly through a vertex makes both edge-crossings of a
                // triangle collapse onto that vertex - a zero-length segment that would
                // stitch into a spurious degenerate "ring". Skip those.
                if (crossing.Count == 2 && Dist2Sq(crossing[0], crossing[1]) > 1e-18)
                    segs.Add((crossing[0], crossing[1]));
            }
            return JoinSegments(segs, tol < 1e-5 ? 1e-5 : tol);
        }

        static List<List<Vec3>> ContourFaces(IReadOnlyList<Vec3> V,
                                              IReadOnlyList<(int a, int b, int c)> F,
                                              IReadOnlyList<double> scalar,
                                              IReadOnlyList<int> faces,
                                              double level, double tol)
        {
            var segs = new List<(Vec3, Vec3)>(faces.Count);
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var tri = F[faces[fi]];
                int[] idx = { tri.a, tri.b, tri.c };
                double[] vals = { scalar[idx[0]] - level, scalar[idx[1]] - level, scalar[idx[2]] - level };
                var crossing = new List<Vec3>(2);
                for (int a = 0; a < 3; a++)
                {
                    int b = (a + 1) % 3;
                    double va = vals[a], vb = vals[b];
                    if ((va <= 0 && 0 < vb) || (vb <= 0 && 0 < va))
                    {
                        double t = va / (va - vb);
                        if (t < 1e-5) t = 0.0;
                        else if (t > 1.0 - 1e-5) t = 1.0;
                        Vec3 pa = V[idx[a]], pb = V[idx[b]];
                        crossing.Add(new Vec3(pa.X + t * (pb.X - pa.X),
                                              pa.Y + t * (pb.Y - pa.Y),
                                              pa.Z + t * (pb.Z - pa.Z)));
                    }
                }
                if (crossing.Count == 2 && Dist2Sq(crossing[0], crossing[1]) > 1e-18)
                    segs.Add((crossing[0], crossing[1]));
            }
            return JoinSegments(segs, tol < 1e-5 ? 1e-5 : tol);
        }

        /// <summary>Extract planar contours while visiting only faces whose scalar
        /// interval contains the requested level.</summary>
        public static List<List<List<Vec3>>> PlanarContoursAtLevels(
            IReadOnlyList<Vec3> V,
            IReadOnlyList<(int a, int b, int c)> F,
            IReadOnlyList<double> scalar,
            IReadOnlyList<double> levels,
            double tol = 1e-6)
        {
            var result = new List<List<List<Vec3>>>(levels.Count);
            if (levels.Count == 0 || F.Count == 0) return result;
            var enter = new List<(double value, int face)>(F.Count);
            var leave = new List<(double value, int face)>(F.Count);
            for (int fi = 0; fi < F.Count; fi++)
            {
                var t = F[fi];
                double a = scalar[t.a], b = scalar[t.b], c = scalar[t.c];
                double lo = Math.Min(a, Math.Min(b, c));
                double hi = Math.Max(a, Math.Max(b, c));
                if (hi > lo) { enter.Add((lo, fi)); leave.Add((hi, fi)); }
            }
            enter.Sort((x, y) => x.value.CompareTo(y.value));
            leave.Sort((x, y) => x.value.CompareTo(y.value));
            var active = new List<int>();
            var positions = new int[F.Count];
            for (int i = 0; i < positions.Length; i++) positions[i] = -1;
            int add = 0, remove = 0;
            for (int li = 0; li < levels.Count; li++)
            {
                double level = levels[li];
                while (add < enter.Count && enter[add].value <= level)
                {
                    int face = enter[add++].face;
                    if (positions[face] < 0) { positions[face] = active.Count; active.Add(face); }
                }
                while (remove < leave.Count && leave[remove].value <= level)
                {
                    int face = leave[remove++].face;
                    int pos = positions[face];
                    if (pos < 0) continue;
                    int last = active[active.Count - 1];
                    active[pos] = last;
                    positions[last] = pos;
                    active.RemoveAt(active.Count - 1);
                    positions[face] = -1;
                }
                result.Add(active.Count == 0
                    ? new List<List<Vec3>>()
                    : ContourFaces(V, F, scalar, active, level, tol));
            }
            return result;
        }

        /// <summary>Stitch unordered segments into polylines by endpoint matching.</summary>
        static List<List<Vec3>> JoinSegments(IReadOnlyList<(Vec3 a, Vec3 b)> segs, double tol = 1e-5)
        {
            (long, long, long) Key(Vec3 p)
                => ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));

            var adj = new Dictionary<(long, long, long), List<(int si, Vec3 other)>>();
            void Add((long, long, long) k, int si, Vec3 other)
            {
                if (!adj.TryGetValue(k, out var lst)) { lst = new List<(int, Vec3)>(); adj[k] = lst; }
                lst.Add((si, other));
            }
            for (int si = 0; si < segs.Count; si++)
            {
                Add(Key(segs[si].a), si, segs[si].b);
                Add(Key(segs[si].b), si, segs[si].a);
            }

            var used = new bool[segs.Count];
            var polylines = new List<List<Vec3>>();
            for (int start = 0; start < segs.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var chain = new LinkedList<Vec3>();
                chain.AddLast(segs[start].a);
                chain.AddLast(segs[start].b);

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    var tail = chain.Last!.Value;
                    if (adj.TryGetValue(Key(tail), out var cand))
                        foreach (var (si, other) in cand)
                            if (!used[si]) { used[si] = true; chain.AddLast(other); changed = true; break; }
                }
                changed = true;
                while (changed)
                {
                    changed = false;
                    var head = chain.First!.Value;
                    if (adj.TryGetValue(Key(head), out var cand))
                        foreach (var (si, other) in cand)
                            if (!used[si]) { used[si] = true; chain.AddFirst(other); changed = true; break; }
                }
                polylines.Add(new List<Vec3>(chain));
            }
            return polylines;
        }

        /// <summary>Non-planar layer rings at scalar levels start, start+step, ...</summary>
        public static List<List<List<Vec3>>> Layers(IReadOnlyList<Vec3> V,
                                                    IReadOnlyList<(int a, int b, int c)> F,
                                                    IReadOnlyList<double> scalar,
                                                    double start, double step,
                                                    double? smin = null, double? smax = null)
        {
            if (!(step > 0.0) || double.IsNaN(step) || double.IsInfinity(step))
                throw new ArgumentException("iso layer step must be finite and > 0", nameof(step));
            double lo = smin ?? Min(scalar);
            double hi = smax ?? Max(scalar);
            // guard band: a level within solver noise of the field max grazes the top
            // boundary tangentially and shatters into tiny scraps - never a usable layer
            double guard = 1e-6 * Math.Max(hi - lo, 1.0);
            var levels = new List<double>();
            double first = lo + start;
            if (double.IsNaN(first) || double.IsInfinity(first)) return new List<List<List<Vec3>>>();
            for (double lv = first; lv < hi - guard && levels.Count < 1000000; lv += step)
            {
                levels.Add(lv);
                if (double.IsInfinity(lv + step)) break;
            }

            var layers = new List<List<List<Vec3>>>();
            foreach (var lv in levels)
            {
                var rings = Contour(V, F, scalar, lv);
                // drop point-count degenerates AND micro-scrap rings (total length below
                // any printable feature - leftovers of near-vertex level crossings)
                rings.RemoveAll(r => r.Count < 2 || TotalLen(r) < 1e-3);
                if (rings.Count > 0) layers.Add(rings);
            }
            return layers;
        }

        static double TotalLen(List<Vec3> r)
        {
            double s = 0;
            for (int i = 0; i + 1 < r.Count; i++) s += Math.Sqrt(Dist2Sq(r[i], r[i + 1]));
            return s;
        }

        static double Min(IReadOnlyList<double> s) { double m = double.MaxValue; foreach (var v in s) if (v < m) m = v; return m; }
        static double Max(IReadOnlyList<double> s) { double m = double.MinValue; foreach (var v in s) if (v > m) m = v; return m; }
        static double Dist2Sq(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
