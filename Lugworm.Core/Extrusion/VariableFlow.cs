using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Extrusion
{
    /// <summary>Per-point LOCAL LAYER HEIGHTS for slices whose real layer height varies
    /// (adaptive planar layers and conical slices) - refinement note
    /// P5 of 2026-07. The heights feed straight into the h of the ram extrusion model from the
    /// original GCODE_Essential_Overview cluster (E = L*h*w / (π(d/2)²) * k) via
    /// GcodeBuilder's heightTree input. The FLOW MULTIPLIER channel (flowTree /
    /// PressureCoeff) is deliberately untouched - it stays reserved for material
    /// calibration (more/less viscous mixes), not geometry.
    ///
    /// Horizontal layers with explicit object identity use vertical Z spacing.
    /// Other paths use distance to the nearest segment of the layer below.</summary>
    public static class VariableFlow
    {
        /// <summary>Local-height tree parallel to <paramref name="branches"/>
        /// (branch → contour → per-point, mm). Rules:
        /// - layer 0: h_nominal everywhere (first-layer height above the bed is not
        ///   derivable from the toolpath alone - nominal behaviour retained);
        /// - planar consecutive layers with the same explicit object ID use delta Z;
        /// - otherwise a branch whose MEDIAN local height exceeds 2×h_nominal is treated as the
        ///   base layer of a new object (sequential multi-object printing) → h_nominal;
        /// - otherwise height = clamp(h_local, minMul×h_nominal, maxMul×h_nominal).</summary>
        public static List<List<List<double>>> LocalHeights(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> branches,
            double nominalH, double minMul = 0.25, double maxMul = 2.5,
            IReadOnlyList<int>? objectIds = null)
        {
            // NaN fails every comparison, so guard with the negated form (AUD-COR-003)
            if (!(nominalH > 1e-9) || double.IsInfinity(nominalH))
                throw new ArgumentException("nominal layer height must be a finite value > 0", nameof(nominalH));
            if (!(minMul > 0) || double.IsInfinity(minMul))
                throw new ArgumentException("min height multiplier must be a finite value > 0", nameof(minMul));
            if (!(maxMul > 0) || double.IsInfinity(maxMul))
                throw new ArgumentException("max height multiplier must be a finite value > 0", nameof(maxMul));
            if (maxMul < minMul)
                throw new ArgumentException("max height multiplier must be >= min height multiplier", nameof(maxMul));
            double hMin = minMul * nominalH, hMax = maxMul * nominalH;

            var outp = new List<List<List<double>>>(branches.Count);
            SegmentGrid? grid = null;
            for (int bi = 0; bi < branches.Count; bi++)
            {
                var branchOut = new List<List<double>>(branches[bi].Count);
                bool newObject = bi == 0 || (objectIds != null && bi < objectIds.Count
                                              && objectIds[bi] != objectIds[bi - 1]);
                if (newObject)
                {
                    foreach (var contour in branches[bi])
                        branchOut.Add(Filled(contour.Count, nominalH));
                    outp.Add(branchOut);
                    continue;
                }

                // The Grasshopper adapter supplies object IDs from tree paths. With
                // that explicit boundary, lateral movement of a planar overhang must
                // not inflate layer height to the diagonal support distance, nor may
                // a valid large Z step be mistaken for the start of another object.
                // Without IDs retain the legacy spatial discontinuity heuristic.
                if (objectIds != null && bi < objectIds.Count
                    && TryPlanarZ(branches[bi - 1], out double belowZ)
                    && TryPlanarZ(branches[bi], out double currentZ) && currentZ > belowZ)
                {
                    double h = Math.Max(hMin, Math.Min(hMax, currentZ - belowZ));
                    foreach (var contour in branches[bi]) branchOut.Add(Filled(contour.Count, h));
                    outp.Add(branchOut);
                    continue;
                }

                // raw local heights for every point of this branch - spatial grid over
                // the layer below instead of scanning all of its segments per point;
                // cells sized to the expected answer (~nominalH) keep the shell search
                // at radius 1
                if (grid == null) grid = new SegmentGrid(branches[bi - 1], cellSize: 2.0 * nominalH);
                else grid.Rebuild(branches[bi - 1]);
                var rawPerContour = new List<double[]>(branches[bi].Count);
                var all = new List<double>();
                foreach (var contour in branches[bi])
                {
                    var raw = new double[contour.Count];
                    for (int k = 0; k < contour.Count; k++)
                    {
                        var hit = grid.Nearest(contour[k], 2.0 * nominalH);
                        raw[k] = hit.dist == double.MaxValue ? 2.0 * nominalH + 1e-9 : hit.dist;
                        all.Add(raw[k]);
                    }
                    rawPerContour.Add(raw);
                }

                // discontinuity guard: sequential multi-object trees put another object's
                // last layer "below" this object's first - huge distances everywhere.
                double median = all.Count > 0 ? SelectKth(all, all.Count / 2) : nominalH;
                bool baseLayer = median > 2.0 * nominalH;

                for (int ci = 0; ci < rawPerContour.Count; ci++)
                {
                    var raw = rawPerContour[ci];
                    var hs = new List<double>(raw.Length);
                    for (int k = 0; k < raw.Length; k++)
                        hs.Add(baseLayer ? nominalH
                             : Math.Max(hMin, Math.Min(hMax, raw[k])));
                    branchOut.Add(hs);
                }
                outp.Add(branchOut);
            }
            return outp;
        }

        static double SelectKth(List<double> values, int k)
        {
            int left = 0, right = values.Count - 1;
            while (left < right)
            {
                int i = left, j = right;
                double pivot = values[left + (right - left) / 2];
                while (i <= j)
                {
                    while (values[i] < pivot) i++;
                    while (values[j] > pivot) j--;
                    if (i <= j)
                    {
                        double t = values[i]; values[i] = values[j]; values[j] = t;
                        i++; j--;
                    }
                }
                if (k <= j) right = j;
                else if (k >= i) left = i;
                else break;
            }
            return values[k];
        }

        static bool TryPlanarZ(IReadOnlyList<IReadOnlyList<Vec3>> branch, out double z)
        {
            z = 0;
            bool found = false;
            foreach (var contour in branch)
                foreach (var p in contour)
                {
                    if (double.IsNaN(p.Z) || double.IsInfinity(p.Z)) return false;
                    if (!found) { z = p.Z; found = true; }
                    else if (Math.Abs(p.Z - z) > 1e-7) return false;
                }
            return found;
        }

        static List<double> Filled(int n, double v)
        {
            var l = new List<double>(n);
            for (int i = 0; i < n; i++) l.Add(v);
            return l;
        }

    }
}
