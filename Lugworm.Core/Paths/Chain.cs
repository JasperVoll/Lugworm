using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Paths
{
    public static class Chain
    {
        /// <summary>Each ring as an independent extrusion path. The handoff is
        /// intentionally not inserted into the returned points; the G-code layer
        /// sees the path boundary and emits a safe travel.</summary>
        public static List<List<Vec3>> RingPaths(IReadOnlyList<IReadOnlyList<Vec3>> rings,
                                                  Vec3? anchor = null)
        {
            var clean = new List<List<Vec3>>();
            foreach (var r in rings)
            {
                if (r == null || r.Count < 3 || !PolylineOps.IsClosed(r, 1e-3)) continue;
                var ring = new List<Vec3>(r);
                if (!PolylineOps.IsClosed(ring, 1e-6)) ring.Add(ring[0]);
                clean.Add(ring);
            }
            var result = new List<List<Vec3>>(clean.Count);
            Vec3? last = anchor;
            foreach (var sourceRing in clean)
            {
                var ring = sourceRing;
                if (last.HasValue)
                {
                    var hit = SeamAnchor.ClosestOnRing(ring, last.Value);
                    ring = SeamAnchor.SplitAt(ring, hit.SegmentIndex, hit.T);
                }
                result.Add(ring);
                last = ring[ring.Count - 1];
            }
            return result;
        }

        /// <summary>Chain nested rings into ONE open polyline. Each ring is entered at
        /// the point nearest the previous ring's exit (or `anchor` for the first), so
        /// every ring-to-ring connection is a short, bounded hop. Rings are de-closed
        /// (trailing duplicate dropped) then re-closed individually.
        /// <para>Walks <paramref name="rings"/> in ARRIVAL order, not containment order
        /// (H8 leftover from D1: unlike <see cref="Spiral.InwardPaths"/>, this does not
        /// route through <see cref="Spiral.NestedChain"/>). Kept internal - its only
        /// caller is the test-only <see cref="Spiral.Connected"/>.</para></summary>
        internal static List<Vec3> Rings(IReadOnlyList<IReadOnlyList<Vec3>> rings, Vec3? anchor = null)
        {
            var clean = new List<List<Vec3>>();
            foreach (var r in rings)
            {
                if (r.Count < 3) continue;
                if (PolylineOps.IsClosed(r))
                {
                    var declosed = new List<Vec3>(r.Count - 1);
                    for (int k = 0; k < r.Count - 1; k++) declosed.Add(r[k]);
                    clean.Add(declosed);
                }
                else clean.Add(new List<Vec3>(r));
            }
            var path = new List<Vec3>();
            if (clean.Count == 0) return path;
            Vec3? last = anchor;
            foreach (var r in clean)
            {
                List<Vec3> seq;
                if (last.HasValue)
                {
                    var hit = SeamAnchor.ClosestOnRing(r, last.Value);
                    seq = SeamAnchor.SplitAt(r, hit.SegmentIndex, hit.T);
                }
                else seq = SeamAnchor.SplitAt(r, 0, 0.0);
                path.AddRange(seq);
                last = path[path.Count - 1];
            }
            return path;
        }
    }
}
