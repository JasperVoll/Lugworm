using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Conservative planar spacing from the worst surface inclination in
    /// the chosen layer interval. Uses complete face intervals, not centroid bins
    /// or area averages. This is a geometric heuristic, not a support/stability test.</summary>
    public static class OverhangAdaptiveLevels
    {
        readonly struct FaceSpan
        {
            public readonly double Min, Max, Height;
            public readonly int Id;
            public FaceSpan(double min, double max, double height, int id)
            { Min = min; Max = max; Height = height; Id = id; }
        }

        public static List<double> Build(IReadOnlyList<Vec3> vertices,
            IReadOnlyList<(int a, int b, int c)> faces, double zmin, double zmax,
            double first, double hMin, double hMax)
        {
            if (!Num.Finite(zmin) || !Num.Finite(zmax) || zmax < zmin)
                throw new ArgumentOutOfRangeException(nameof(zmax), "Z bounds must be finite and increasing.");
            if (!Num.Finite(first)) throw new ArgumentOutOfRangeException(nameof(first));
            if (!Num.Finite(hMin) || hMin <= 0) throw new ArgumentOutOfRangeException(nameof(hMin));
            if (!Num.Finite(hMax) || hMax < hMin) throw new ArgumentOutOfRangeException(nameof(hMax));
            if (vertices == null || vertices.Count == 0)
                throw new ArgumentException("Adaptive slicing needs mesh vertices.", nameof(vertices));
            if (faces == null) throw new ArgumentNullException(nameof(faces));
            foreach (var p in vertices)
                if (!Num.Finite(p))
                    throw new ArgumentException("Mesh vertices must be finite.", nameof(vertices));

            var starts = new List<FaceSpan>(faces.Count);
            foreach (var (a, b, c) in faces)
            {
                if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
                    throw new ArgumentException("Mesh face index is outside the vertex list.", nameof(faces));
                var p = vertices[a]; var q = vertices[b]; var r = vertices[c];
                var normal = (q - p).Cross(r - p);
                double length = normal.Length;
                if (!Num.Finite(length)) throw new ArgumentException("Mesh face normal overflowed.", nameof(vertices));
                if (length <= 1e-12) continue;
                double lo = Math.Min(p.Z, Math.Min(q.Z, r.Z));
                double hi = Math.Max(p.Z, Math.Max(q.Z, r.Z));
                // Global horizontal caps are not printed walls. Interior horizontal
                // ledges are retained; near-horizontal sloping faces are never skipped.
                if (lo == hi && (Math.Abs(lo - zmin) < 1e-9 || Math.Abs(lo - zmax) < 1e-9)) continue;
                double verticality = 1.0 - Math.Min(1.0, Math.Abs(normal.Z) / length);
                starts.Add(new FaceSpan(lo, hi, hMin + (hMax - hMin) * verticality, starts.Count));
            }

            starts.Sort((a, b) => a.Min.CompareTo(b.Min));
            var ends = new List<FaceSpan>(starts);
            ends.Sort((a, b) => a.Max.CompareTo(b.Max));
            var sortedIndex = new int[starts.Count];
            for (int i = 0; i < starts.Count; i++) sortedIndex[starts[i].Id] = i;
            var active = new PrefixMinimum(starts, hMax);
            int ei = 0;
            double z = zmin + (first > 0 ? first : 0.5 * hMin);
            if (!Num.Finite(z)) throw new ArgumentOutOfRangeException(nameof(first));
            var levels = new List<double>();
            while (z < zmax)
            {
                if (levels.Count == 100000)
                    throw new InvalidOperationException("Adaptive slicing exceeds 100000 layers; increase hMin or reduce model height.");
                levels.Add(z);
                while (ei < ends.Count && ends[ei].Max <= z)
                { var f = ends[ei++]; active.Remove(sortedIndex[f.Id]); }

                double Limit(double candidate)
                {
                    double upper = Math.Min(zmax, z + candidate);
                    int lo = 0, hi = starts.Count;
                    while (lo < hi)
                    {
                        int mid = lo + (hi - lo) / 2;
                        if (starts[mid].Min < upper) lo = mid + 1;
                        else hi = mid;
                    }
                    return active.Before(lo);
                }

                // Checking a full hMax window first and blindly taking its minimum
                // made a nearby dome crown constrain even short lower layers.
                // Feasibility is monotone: as h grows, more faces participate and
                // the allowable height can only decrease. Find the largest h whose
                // OWN interval satisfies all its face limits. hMin is always feasible.
                double h = hMax;
                if (hMax > Limit(hMax))
                {
                    double lo = hMin, hi = hMax;
                    for (int iteration = 0; iteration < 40; iteration++)
                    {
                        double mid = lo + (hi - lo) * 0.5;
                        if (mid <= Limit(mid)) lo = mid;
                        else hi = mid;
                    }
                    h = lo;
                }
                double next = z + h;
                if (!Num.Finite(next) || next <= z)
                    throw new InvalidOperationException("Adaptive layer height cannot advance Z at this coordinate scale.");
                z = next;
            }
            // Match planar sectioning for a part thinner than its first section.
            if (levels.Count == 0) levels.Add(z);
            return levels;
        }

        /// <summary>Faces are ordered by minimum Z. Expired faces are removed once;
        /// a prefix query then includes exactly those intersecting a candidate slab.
        /// O(log F) updates/queries avoid rescanning the mesh for every trial height.</summary>
        sealed class PrefixMinimum
        {
            readonly double[] tree;
            readonly int count;
            readonly double empty;

            public PrefixMinimum(List<FaceSpan> faces, double empty)
            {
                count = faces.Count;
                this.empty = empty;
                tree = new double[2 * count];
                for (int i = 0; i < count; i++) tree[count + i] = faces[i].Height;
                for (int i = count - 1; i > 0; i--) tree[i] = Math.Min(tree[2 * i], tree[2 * i + 1]);
            }

            public void Remove(int index)
            {
                int i = count + index;
                tree[i] = empty;
                while ((i /= 2) > 0) tree[i] = Math.Min(tree[2 * i], tree[2 * i + 1]);
            }

            public double Before(int end)
            {
                double result = empty;
                int left = count, right = count + end;
                while (left < right)
                {
                    if ((left & 1) != 0) result = Math.Min(result, tree[left++]);
                    if ((right & 1) != 0) result = Math.Min(result, tree[--right]);
                    left /= 2; right /= 2;
                }
                return result;
            }
        }
    }
}
