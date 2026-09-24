using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Extrusion
{
    /// <summary>Slope-dependent extrusion compensation for conical / non-planar
    /// slices printed with a fixed VERTICAL nozzle: on an inclined layer stack the
    /// layer is deposited onto a slanted support, and practice (e.g. the ZHAW RotBot
    /// slicer4rtn, whose authors mark their factor as empirical) shows the flow needs
    /// a calibrated boost with inclination. This produces a per-point FLOW MULTIPLIER
    /// tree (the material-calibration channel per decision D025 of the archived
    /// decision log - geometry-driven thickness is
    /// varh's job, this is deliberately separate):
    ///
    ///   m(slope) = 1 + (kRef − 1) - min(slope, refDeg) / refDeg
    ///
    /// slope = angle between the local support vector (point → nearest segment of the
    /// layer below) and the vertical; 0° = flat planar stack ⇒ m = 1 everywhere.
    /// kRef = multiplier AT refDeg - a measured value (NEEDS_INPUT: default 1.0 makes
    /// this a no-op; calibrate with an inclined-wall test print). Layer 0 gets 1.0.</summary>
    public static class SlopeFlow
    {
        public static List<List<List<double>>> Multipliers(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> branches,
            double kRef, double refDeg = 45.0,
            IReadOnlyList<int>? objectIds = null,
            double maxDistance = double.PositiveInfinity)
        {
            // NaN fails every comparison, so guard with the negated form (AUD-COR-003)
            if (!(refDeg > 1e-6) || double.IsInfinity(refDeg))
                throw new ArgumentException("reference slope must be a finite angle > 0°", nameof(refDeg));
            if (!(kRef > 0) || double.IsInfinity(kRef))
                throw new ArgumentException("reference multiplier must be a finite value > 0 " +
                                            "(a negative or zero multiplier would command reverse extrusion)", nameof(kRef));
            if (double.IsNaN(maxDistance) || !(maxDistance > 0))
                throw new ArgumentException("max support distance must be > 0", nameof(maxDistance));
            var outp = new List<List<List<double>>>(branches.Count);
            SegmentGrid? grid = null;
            for (int bi = 0; bi < branches.Count; bi++)
            {
                var branchOut = new List<List<double>>(branches[bi].Count);
                bool newObject = bi == 0 || (objectIds != null && bi < objectIds.Count
                                              && objectIds[bi] != objectIds[bi - 1]);
                if (newObject || Math.Abs(kRef - 1.0) < 1e-12)
                {
                    foreach (var contour in branches[bi])
                        branchOut.Add(Ones(contour.Count));
                    outp.Add(branchOut);
                    continue;
                }

                if (grid == null) grid = new SegmentGrid(branches[bi - 1]);
                else grid.Rebuild(branches[bi - 1]);
                foreach (var contour in branches[bi])
                {
                    var ms = new List<double>(contour.Count);
                    foreach (var p in contour)
                    {
                        var (q, dist) = grid.Nearest(p, maxDistance);
                        double m = 1.0;
                        if (dist > 1e-9 && dist < double.MaxValue)
                        {
                            var s = p - q;
                            double cos = Math.Abs(s.Z) / s.Length;                    // vs vertical
                            double slope = Math.Acos(Math.Max(-1, Math.Min(1, cos))) * 180.0 / Math.PI;
                            m = 1.0 + (kRef - 1.0) * Math.Min(slope, refDeg) / refDeg;
                        }
                        ms.Add(m);
                    }
                    branchOut.Add(ms);
                }
                outp.Add(branchOut);
            }
            return outp;
        }

        static List<double> Ones(int n)
        {
            var l = new List<double>(n);
            for (int i = 0; i < n; i++) l.Add(1.0);
            return l;
        }
    }
}
