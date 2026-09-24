using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Slicing
{
    /// <summary>Options for <see cref="Bricklayer.Apply"/>. Defaults are the plan's:
    /// half a layer height, a 50% side-by-side gate, the visible wall left flat, and
    /// one flat layer at each end of the object.</summary>
    public sealed class BricklayerOptions
    {
        /// <summary>Nominal layer height, mm. Used when the stack has only one layer;
        /// multi-layer stacks derive each shift from their actual level gaps.</summary>
        public double LayerHeight;
        /// <summary>Layer width, mm - the adjacency distance gate. The Slice component
        /// passes its `spacing`, which is both the wall offset and the layer width. The
        /// gate is actually tested at <see cref="Bricklayer.AdjacencyGate"/>, one layer
        /// plus the offset pipeline's working precision.</summary>
        public double LayerWidth;
        /// <summary>Shift as a fraction of <see cref="LayerHeight"/> (0.5 = the running
        /// bond; 0 disables the pass).</summary>
        public double ShiftFraction = 0.5;
        /// <summary>How much of the shorter loop must run alongside its neighbour before
        /// the two count as adjacent. 0.5 = "more than half the time".</summary>
        public double OverlapFraction = 0.5;
        /// <summary>False (default): the outermost wall of every component stays flat, so
        /// the visible surface is unaffected. True: the component's colouring is flipped
        /// and the outermost wall is the one that rises.</summary>
        public bool ShiftOuter = false;
        /// <summary>Layers left flat at each end of the object, so the print starts flat
        /// on the bed and ends flat.</summary>
        public int RampLayers = 1;

        /// <summary>Largest number of loop pairs one layer may be tested for. A layer with
        /// more islands than this is reported and left flat rather than left to run for
        /// minutes - the adjacency test is quadratic in the loop count.</summary>
        public int MaxPairsPerLayer = 20000;
    }

    /// <summary>What <see cref="Bricklayer.Apply"/> did, and every case it deliberately
    /// refused. Never thrown away by the caller: the Slice component surfaces the notes
    /// as remarks so a layer that silently stayed flat is visible.</summary>
    public sealed class BricklayerReport
    {
        public int LayersProcessed;
        public int LayersShifted;
        public int PathsShifted;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>Interlocking ("brick") walls: within one layer, adjacent wall loops are
    /// two-coloured and every other colour rises by half a layer height, so the vertical
    /// seam between two columns of layers becomes a running bond instead of a continuous
    /// delamination plane.
    ///
    /// <para>Two loops count as adjacent only when BOTH gates pass: their XY distance is
    /// within one layer width (to the tolerance <see cref="AdjacencyGate"/> explains), AND
    /// more than <see cref="BricklayerOptions.OverlapFraction"/> of the shorter loop runs
    /// that close to the other. The distance gate alone would bond two rings that merely
    /// touch at a tangent point; the overlap gate alone would bond two rings on opposite
    /// sides of the part.</para>
    ///
    /// <para>Conical layers (slice mode 1) need no special handling even though the plan
    /// asked for the shift to happen in the sheared frame. The inverse conical map is
    /// <c>z = level - tan(a)*r</c>: it subtracts a function of the radius only, and leaves
    /// XY untouched, so raising the sheared level by <c>d</c> and raising the mapped z by
    /// <c>d</c> give the same points. The chord refinement in
    /// <see cref="Conical.InverseContourRefined"/> is likewise driven by r alone, so it
    /// inserts the same intermediate points either way. Applying the shift after the map -
    /// which is what this class does, uniformly for both slice modes - is therefore exactly
    /// equivalent, and avoids keeping the whole conical stack in the sheared frame.
    /// <c>BricklayerTests.ConicalShift_EqualsShearedFrameShift</c> pins that equivalence.</para>
    ///
    /// <para>This is geometry only. A shifted loop's gap to the layer below is 1.5x the
    /// layer height on the ramp-in layer and 0.5x on the ramp-out layer; compensating the
    /// extrusion for that is the G-code generator's `varh` group, and the Slice component
    /// warns when it is asked for bricklayer output without it.</para>
    ///
    /// <para>Clean-room implementation. The idea is Roman Tenger's / GeekDetour's
    /// "BrickLayers" post-processors (GPL-3.0); no code from them is used or ported - see
    /// THIRD-PARTY-NOTICES.md.</para></summary>
    public static class Bricklayer
    {
        /// <summary>The distance the adjacency gate is actually tested at, mm: one layer
        /// width plus the offset pipeline's own working precision.
        ///
        /// <para><b>Why it cannot be one layer exactly</b> (4.4.0, D10). Wall loops arrive
        /// from Clipper, which works on a 0.1 um integer grid
        /// (<c>ClipperConvert.Precision</c> = 4), and <c>Offset.Polygons</c> then thins
        /// every ring with Douglas-Peucker at <c>min(0.02, spacing/200)</c> mm, which pulls
        /// a curved ring's chords inside the true offset arc. On a round part both effects
        /// land the measured wall-to-wall gap OUTSIDE the nominal spacing: 1.0e-4 mm on a
        /// 48-gon at spacing 3 (the grid quantum alone), rising to 1.3e-2 mm on a 256-gon
        /// at spacing 6, where the thinning dominates. The gate's old epsilon was relative
        /// (1e-9), so on a cylinder, cone or vase no two loops ever counted as adjacent and
        /// the pass reported "0 wall loop(s) raised" on every eligible layer - while the
        /// hand-built rectangle fixtures, whose offsets are exact, passed throughout.</para>
        ///
        /// <para>2% of the layer width clears the thinning tolerance by at least 4x at every
        /// layer width (b/50 against min(0.02, b/200) plus the grid quantum), and the 1e-3 mm
        /// floor covers the quantum alone for a layer so small that 2% would not. It is a
        /// tolerance, not a loosening: the overlap-fraction gate - the one that keeps rings
        /// on opposite sides of a part apart - is untouched, and loops one and a half layers
        /// apart stay non-adjacent by a wide margin. It is also strictly greater than the
        /// layer width, which is what lets <see cref="SegmentGrid.Nearest"/>'s strictly-closer
        /// cap accept two walls offset by exactly one layer.</para></summary>
        internal static double AdjacencyGate(double layerWidth)
            => layerWidth + Math.Max(1e-3, 0.02 * layerWidth);

        /// <summary>Shifts alternate adjacent wall loops in place. Infill and open
        /// sections are never touched. Returns what happened; throws only on options
        /// that cannot mean anything.</summary>
        public static BricklayerReport Apply(IList<LayerPaths> stack, BricklayerOptions o)
        {
            if (stack == null) throw new ArgumentNullException(nameof(stack));
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (!(o.LayerHeight > 0) || double.IsNaN(o.LayerHeight) || double.IsInfinity(o.LayerHeight))
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"bricklayer needs a finite layer height > 0 (got {o.LayerHeight})");
            if (!(o.LayerWidth > 0) || double.IsNaN(o.LayerWidth) || double.IsInfinity(o.LayerWidth))
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"bricklayer needs a finite layer width > 0 (got {o.LayerWidth})");
            if (double.IsNaN(o.ShiftFraction) || double.IsInfinity(o.ShiftFraction)
                || o.ShiftFraction < 0.0 || o.ShiftFraction > 1.0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"shift fraction must be finite and within [0, 1] (got {o.ShiftFraction})");
            if (o.OverlapFraction < 0.0 || o.OverlapFraction >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(o),
                    $"overlap fraction must be within [0, 1) (got {o.OverlapFraction})");

            var report = new BricklayerReport();
            if (o.ShiftFraction == 0.0) return report;   // an explicit no-op, not an error

            int n = stack.Count;
            var gaps = LayerGaps(stack, o.LayerHeight);
            double sampleStep = Math.Min(o.LayerWidth * 0.5, 1.0);
            int ramp = Math.Max(0, o.RampLayers);

            for (int li = 0; li < n; li++)
            {
                // flat at both ends: the print must sit flat on the bed and finish flat
                if (li < ramp || li >= n - ramp) continue;
                var layer = stack[li];
                if (layer == null || layer.Paths.Count == 0) continue;
                report.LayersProcessed++;

                var walls = new List<int>();
                for (int i = 0; i < layer.Paths.Count; i++)
                {
                    var p = layer.Paths[i];
                    if (p != null && p.Role == PathRole.Wall && p.Points != null && p.Points.Count >= 2)
                        walls.Add(i);
                }
                if (walls.Count < 2) continue;

                long pairs = (long)walls.Count * (walls.Count - 1) / 2;
                if (pairs > o.MaxPairsPerLayer)
                {
                    report.Notes.Add($"layer {li + 1}: {walls.Count} wall loops ({pairs} pairs) exceeds the " +
                                     $"{o.MaxPairsPerLayer}-pair budget - left flat");
                    continue;
                }

                int shifted = ShiftLayer(layer, walls, o, o.ShiftFraction * gaps[li], sampleStep, li, report);
                if (shifted > 0) { report.LayersShifted++; report.PathsShifted += shifted; }
            }
            return report;
        }

        /// <summary>Two-colours one layer's wall loops and raises colour 1. Returns how
        /// many paths moved.</summary>
        static int ShiftLayer(LayerPaths layer, List<int> walls, BricklayerOptions o,
                              double dz, double sampleStep, int layerIndex, BricklayerReport report)
        {
            int m = walls.Count;
            var pts = new List<Vec3>[m];
            var xy = new List<Vec3>[m];
            var grids = new SegmentGrid[m];
            var boxes = new double[m][];   // xmin, ymin, xmax, ymax
            var lengths = new double[m];
            for (int k = 0; k < m; k++)
            {
                pts[k] = layer.Paths[walls[k]].Points;
                xy[k] = ProjectXy(pts[k]);
                boxes[k] = BoxOf(pts[k]);
                lengths[k] = PolylineOps.ContourLength(xy[k]);
            }

            // adjacency graph, both gates
            double gate = AdjacencyGate(o.LayerWidth);
            var adj = new List<int>[m];
            for (int k = 0; k < m; k++) adj[k] = new List<int>();
            for (int a = 0; a < m; a++)
                for (int b = a + 1; b < m; b++)
                {
                    if (!BoxesWithin(boxes[a], boxes[b], gate)) continue;
                    // sample the shorter loop against the longer one's grid: the fraction
                    // is "how much of the shorter loop runs alongside", which is what the
                    // brief asks for and is symmetric enough to two-colour on
                    int s = lengths[a] <= lengths[b] ? a : b, t = s == a ? b : a;
                    if (grids[t] == null) grids[t] = new SegmentGrid(new[] { (IReadOnlyList<Vec3>)xy[t] });
                    if (!Adjacent(xy[s], grids[t], gate, o.OverlapFraction, sampleStep)) continue;
                    adj[a].Add(b);
                    adj[b].Add(a);
                }

            // BFS two-colouring, one component at a time, entered at its outermost loop
            var order = new int[m];
            for (int k = 0; k < m; k++) order[k] = k;
            Array.Sort(order, (x, y) => layer.Paths[walls[x]].WallIndex.CompareTo(layer.Paths[walls[y]].WallIndex));

            var colour = new int[m];
            for (int k = 0; k < m; k++) colour[k] = -1;
            var queue = new Queue<int>();
            var member = new List<int>();
            int moved = 0;

            for (int oi = 0; oi < m; oi++)
            {
                int start = order[oi];
                if (colour[start] >= 0) continue;
                if (adj[start].Count == 0) { colour[start] = 0; continue; }   // nothing to interlock with

                member.Clear();
                queue.Clear();
                colour[start] = 0;
                queue.Enqueue(start);
                member.Add(start);
                bool bipartite = true;
                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    foreach (int v in adj[u])
                    {
                        if (colour[v] < 0)
                        {
                            colour[v] = 1 - colour[u];
                            member.Add(v);
                            queue.Enqueue(v);
                        }
                        else if (colour[v] == colour[u]) bipartite = false;
                    }
                }

                if (!bipartite)
                {
                    // an odd cycle - three mutually adjacent loops. Any two-colouring puts
                    // two shifted loops side by side, which is worse than not shifting.
                    foreach (int k in member) colour[k] = 0;
                    report.Notes.Add($"layer {layerIndex + 1}: {member.Count} wall loops form a non-bipartite " +
                                     "adjacency group (odd cycle) - left flat");
                    continue;
                }
                // colour 0 belongs to the component's outermost loop, because the BFS was
                // entered there; ShiftOuter asks for the opposite
                if (o.ShiftOuter)
                    foreach (int k in member) colour[k] = 1 - colour[k];
            }

            for (int k = 0; k < m; k++)
            {
                if (colour[k] != 1) continue;
                var path = layer.Paths[walls[k]];
                var src = path.Points;
                var lifted = new List<Vec3>(src.Count);
                for (int i = 0; i < src.Count; i++) lifted.Add(new Vec3(src[i].X, src[i].Y, src[i].Z + dz));
                path.Points = lifted;
                path.ZShift += dz;   // the non-planar wall guard reads the real gap from this
                moved++;
            }
            return moved;
        }

        /// <summary>The overlap gate: what fraction of <paramref name="sample"/> runs
        /// within <paramref name="gate"/> - <see cref="AdjacencyGate"/> of the layer width -
        /// of the other loop.</summary>
        static bool Adjacent(List<Vec3> sample, SegmentGrid other, double gate,
                             double overlapFraction, double sampleStep)
        {
            var probes = Resample.ByLength(sample, sampleStep);
            if (probes.Count == 0) return false;
            // SegmentGrid.Nearest only accepts a candidate STRICTLY closer than its cap,
            // which is one reason AdjacencyGate stays strictly above the layer width:
            // querying at exactly one layer would reject the commonest case of all, two
            // walls offset by exactly one layer.
            int hit = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                // every wall loop of a layer sits at one z, so the grid's 3-D distance IS
                // the XY distance here; the cap makes a far loop cost nothing
                var near = other.Nearest(probes[i], gate);
                if (near.dist <= gate) hit++;
            }
            return (double)hit / probes.Count > overlapFraction;
        }

        static double[] BoxOf(List<Vec3> pts)
        {
            double xmin = double.MaxValue, ymin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.X < xmin) xmin = p.X;
                if (p.X > xmax) xmax = p.X;
                if (p.Y < ymin) ymin = p.Y;
                if (p.Y > ymax) ymax = p.Y;
            }
            return new[] { xmin, ymin, xmax, ymax };
        }

        static List<Vec3> ProjectXy(List<Vec3> pts)
        {
            var projected = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++) projected.Add(new Vec3(pts[i].X, pts[i].Y, 0.0));
            return projected;
        }

        static double[] LayerGaps(IList<LayerPaths> stack, double fallback)
        {
            int n = stack.Count;
            var gaps = new double[n];
            if (n == 0) return gaps;
            if (n == 1) { gaps[0] = fallback; return gaps; }
            for (int i = 0; i + 1 < n; i++)
            {
                double gap = stack[i + 1].Level - stack[i].Level;
                if (!(gap > 0.0) || double.IsNaN(gap) || double.IsInfinity(gap))
                    throw new ArgumentOutOfRangeException(nameof(stack),
                        $"bricklayer needs finite, increasing layer levels (gap {i + 1}-{i + 2}: {gap})");
                gaps[i] = gap;
            }
            gaps[n - 1] = gaps[n - 2];
            return gaps;
        }

        static bool BoxesWithin(double[] a, double[] b, double slack)
            => a[0] - slack <= b[2] && b[0] - slack <= a[2]
            && a[1] - slack <= b[3] && b[1] - slack <= a[3];
    }
}
