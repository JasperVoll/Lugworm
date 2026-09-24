using System;
using System.Collections.Generic;
using System.Globalization;
using static System.FormattableString;
using Lugworm.Core.Arcs;
using Lugworm.Core.Clipping;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Gcode
{
    /// <summary>Curve-tree -> Marlin/Duet G-code (clay, ram/piston). Port of
    /// clay_core_v2 §9 build_gcode_v2. Seam handling follows RetractionMode:
    /// NoRetraction connects everything into one continuous path; LightRetraction
    /// connects vertically-aligned / close seams and retracts only on distant jumps;
    /// NormalRetraction retracts at every seam. Retracting travels lift to a
    /// path-aware collision-safe height (max printed Z along the travel corridor
    /// + ZHop clearance). All numeric formatting uses InvariantCulture
    /// (decimals must be dots).</summary>
    public static class GcodeBuilder
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F3(double v) => v.ToString("0.000", Inv);
        static string F5(double v) => v.ToString("0.00000", Inv);

        static List<Move> LineMoves(IReadOnlyList<Vec3> pts)
        {
            var m = new List<Move>(pts.Count - 1);
            for (int i = 0; i < pts.Count - 1; i++) m.Add(Move.Line(pts[i], pts[i + 1]));
            return m;
        }

        /// <summary>Flow/height trees must be exactly parallel to the toolpath tree
        /// (same branch, contour and point counts) - anything else used to throw a raw
        /// IndexOutOfRange deep in the build or silently mismatch values.</summary>
        static void ValidateParallelTree(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? tree,
            IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> branches, string name)
        {
            if (tree == null) return;
            if (tree.Count != branches.Count)
                throw new ArgumentException(
                    $"{name} has {tree.Count} branches but the toolpath has {branches.Count} - the trees must be parallel.");
            for (int bi = 0; bi < branches.Count; bi++)
            {
                if (tree[bi].Count != branches[bi].Count)
                    throw new ArgumentException(
                        $"{name} branch {bi} has {tree[bi].Count} contours but the toolpath has {branches[bi].Count}.");
                for (int ci = 0; ci < branches[bi].Count; ci++)
                {
                    var contour = branches[bi][ci];
                    if (contour != null && contour.Count >= 2 && tree[bi][ci].Count != contour.Count)
                        throw new ArgumentException(
                            $"{name} branch {bi} contour {ci} has {tree[bi][ci].Count} values for {contour.Count} points.");
                    if (contour != null && contour.Count >= 2)
                    {
                        for (int vi = 0; vi < tree[bi][ci].Count; vi++)
                        {
                            double value = tree[bi][ci][vi];
                            if (!(value > 0) || double.IsInfinity(value))
                                throw new ArgumentException(
                                    $"{name} branch {bi} contour {ci} value {vi} must be finite and > 0.", name);
                        }
                    }
                }
            }
        }

        public static GcodeResult Build(
            IReadOnlyList<IReadOnlyList<IReadOnlyList<Vec3>>> branchesIn,
            GcodeConfig c,
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? flowTree = null,
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? heightTree = null,
            IReadOnlyList<int>? objectIds = null)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (branchesIn == null) throw new ArgumentNullException(nameof(branchesIn));
            if (objectIds != null && objectIds.Count != branchesIn.Count)
                throw new ArgumentException("Object IDs must match the input branch count.", nameof(objectIds));
            c.Validate();
            ValidateParallelTree(flowTree, branchesIn, "flowTree");
            ValidateParallelTree(heightTree, branchesIn, "heightTree");

            // ---- clean empties (drop contours with < 2 points) ----
            var branches = new List<List<IReadOnlyList<Vec3>>>();
            var flows = new List<List<IReadOnlyList<double>>>();
            var hgts = new List<List<IReadOnlyList<double>?>>();
            var ids = new List<int>();
            var warnings = new List<string>();
            for (int bi = 0; bi < branchesIn.Count; bi++)
            {
                var cs = new List<IReadOnlyList<Vec3>>();
                var fs = new List<IReadOnlyList<double>>();
                var hh = new List<IReadOnlyList<double>?>();
                var br = branchesIn[bi];
                for (int ci = 0; ci < br.Count; ci++)
                {
                    var contour = br[ci];
                    if (contour != null && contour.Count >= 2)
                    {
                        // a dropped point takes its same-index flow/height samples with
                        // it, so the parallel arrays stay aligned with retained points
                        var srcFlow = flowTree?[bi][ci];
                        var srcHgt = heightTree?[bi][ci];
                        var normalized = new List<Vec3>(contour.Count);
                        var keptFlow = srcFlow != null ? new List<double>(srcFlow.Count) : null;
                        var keptHgt = srcHgt != null ? new List<double>(srcHgt.Count) : null;
                        for (int pi = 0; pi < contour.Count; pi++)
                        {
                            var p = contour[pi];
                            if (double.IsNaN(p.X) || double.IsInfinity(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.Y)
                                || double.IsNaN(p.Z) || double.IsInfinity(p.Z))
                            { warnings.Add($"branch {bi} contour {ci}: dropped non-finite point"); continue; }
                            if (normalized.Count == 0 || PolylineOps.Dist(normalized[normalized.Count - 1], p) > 1e-9)
                            {
                                normalized.Add(p);
                                keptFlow?.Add(srcFlow![pi]);
                                keptHgt?.Add(srcHgt![pi]);
                            }
                            else warnings.Add($"branch {bi} contour {ci}: dropped duplicate point");
                        }
                        if (normalized.Count >= 2)
                        {
                            cs.Add(normalized);
                            if (keptFlow != null) fs.Add(keptFlow);
                            else { var ones = new double[normalized.Count]; for (int k = 0; k < ones.Length; k++) ones[k] = 1.0; fs.Add(ones); }
                            hh.Add(keptHgt);
                        }
                        else warnings.Add($"branch {bi} contour {ci}: dropped degenerate contour");
                    }
                }
                if (cs.Count > 0) { branches.Add(cs); flows.Add(fs); hgts.Add(hh); ids.Add(objectIds?[bi] ?? 0); }
            }
            if (c.AlternateInfill)
            {
                var seen = new Dictionary<int, int>();
                for (int bi = 0; bi < branches.Count; bi++)
                {
                    int objectId = ids[bi];
                    int layerInObject = seen.TryGetValue(objectId, out var prior) ? prior : 0;
                    seen[objectId] = layerInObject + 1;
                    if ((layerInObject & 1) == 0) continue;
                    var cs = branches[bi];
                    int firstOpen = -1;
                    for (int ci = 0; ci < cs.Count; ci++)
                        if (!PolylineOps.IsClosed(cs[ci])) { firstOpen = ci; break; }
                    if (firstOpen < 0 || firstOpen == 0) continue;
                    // The slicer contract is closed walls followed by open infill.
                    // Leave mixed/ambiguous user trees untouched rather than moving
                    // an arbitrary contour across a layer boundary.
                    bool ordered = true;
                    for (int ci = 0; ci < firstOpen; ci++) ordered &= PolylineOps.IsClosed(cs[ci]);
                    for (int ci = firstOpen; ci < cs.Count; ci++) ordered &= !PolylineOps.IsClosed(cs[ci]);
                    if (!ordered) continue;
                    var order = new List<int>();
                    for (int ci = cs.Count - 1; ci >= firstOpen; ci--) order.Add(ci);
                    for (int ci = firstOpen - 1; ci >= 0; ci--) order.Add(ci);
                    var newCs = new List<IReadOnlyList<Vec3>>(cs.Count);
                    var newFs = new List<IReadOnlyList<double>>(cs.Count);
                    var newHs = new List<IReadOnlyList<double>?>(cs.Count);
                    foreach (int old in order)
                    {
                        var points = new List<Vec3>(cs[old]);
                        var flow = new List<double>(flows[bi][old]);
                        var hgt = hgts[bi][old] == null ? null : new List<double>(hgts[bi][old]!);
                        if (!PolylineOps.IsClosed(points)) { points.Reverse(); flow.Reverse(); hgt?.Reverse(); }
                        newCs.Add(points); newFs.Add(flow); newHs.Add(hgt);
                    }
                    branches[bi] = newCs; flows[bi] = newFs; hgts[bi] = newHs;
                }
            }
            // NoRetraction connects every seam; LightRetraction connects seams that
            // align vertically / are very close and retracts only on distant jumps;
            // NormalRetraction retracts at every seam regardless of distance.
            bool connectAll = c.Retraction == RetractionMode.NoRetraction;
            bool retractEverySeam = c.Retraction == RetractionMode.NormalRetraction;
            double seamTol = c.SeamTol > 1e-9 ? c.SeamTol : 2.0 * c.Width;
            bool anyRetractSeam = false;

            // E per mm of XY travel for a layer of height h (mm): the ram model from the
            // original GCODE_Essential_Overview cluster. h is the NOMINAL layer height
            // unless a heightTree supplies measured local heights (variable-height
            // slices); PressureCoeff / flow multipliers stay reserved for MATERIAL
            // calibration and multiply on top.
            double EFactorH(double h) =>
                RamExtrusion.RamTravelPerPathMm(c.Width, h, c.RamDiameter, c.PressureCoeff);

            int pF = Math.Max(1, (int)Math.Round(c.PrintSpeed * 60));
            int tF = Math.Max(1, (int)Math.Round(c.TravelSpeed * 60));
            int rF = Math.Max(1, (int)Math.Round(c.RetractSpeed * 60));
            // feedrate for the pure-Z hop lift/descend moves; falls back to travel speed
            int hF = c.HopSpeed > 1e-9 ? Math.Max(1, (int)Math.Round(c.HopSpeed * 60)) : tF;
            int flPF = Math.Max(1, (int)Math.Round(c.PrintSpeed * c.FirstLayerSpeedFactor * 60));
            double zoff = c.ZOffset;
            // retraction length: ram pull-back
            double offset = Math.Max(0.0, c.PressureOffset);
            double prime = offset + Math.Max(0.0, c.PrimeExtra);
            // clearance above the printed material during travels (path-aware safe hop)
            double hop = c.ZHop > 1e-9 ? c.ZHop : c.LayerHeight;
            double coast = Math.Max(0.0, c.Coast);
            double leadIn = Math.Max(0.0, c.LeadIn);
            double leadBoost = Math.Max(1.0, c.LeadInBoost);
            double dwell = Math.Max(0.0, c.Dwell);

            var L = new List<string>();
            var printPolys = new List<List<Vec3>>();
            var travelPolys = new List<List<Vec3>>();
            Vec3? firstActualPrint = null;
            double totLen = 0.0, totVol = 0.0, totTime = 0.0, eAccum = 0.0, extrudedE = 0.0;
            bool relE = c.RelativeE;

            // ---- path-aware travel clearance ----
            // Coarse XY grid of the highest nozzle Z printed so far. A travel lifts to
            // the max printed Z along its own corridor + hop clearance instead of a
            // fixed global hop, so non-planar / complex prints are cleared without
            // manually cranking ZHop.
            double cell = Math.Max(4.0 * c.Width, 10.0);
            var zGrid = new Dictionary<(int cx, int cy), double>();
            void GridAddPoint(double x, double y, double z)
            {
                var k = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
                zGrid[k] = zGrid.TryGetValue(k, out var zc) ? Math.Max(zc, z) : z;
            }
            void GridAddPoly(List<Vec3> poly)
            {
                for (int i = 0; i < poly.Count; i++)
                {
                    GridAddPoint(poly[i].X, poly[i].Y, poly[i].Z);
                    if (i == 0) continue;
                    // sample long segments so mid-span cells are not missed
                    var a = poly[i - 1]; var b = poly[i];
                    double len = PolylineOps.Dist(a, b);
                    int n = (int)(len / (cell * 0.5));
                    for (int s = 1; s <= n; s++)
                    {
                        double t = (double)s / (n + 1);
                        GridAddPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                    }
                }
            }
            // safe Z for a travel a→b: max printed Z in the 3×3 cells along the
            // corridor + clearance, never below either endpoint
            double SafeTravelZ(Vec3 a, Vec3 b)
            {
                double best = double.NegativeInfinity;
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                int n = Math.Max(1, (int)Math.Ceiling(len / (cell * 0.5)));
                for (int i = 0; i <= n; i++)
                {
                    double t = (double)i / n;
                    int cx = (int)Math.Floor((a.X + dx * t) / cell);
                    int cy = (int)Math.Floor((a.Y + dy * t) / cell);
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                            if (zGrid.TryGetValue((cx + ox, cy + oy), out var z) && z > best) best = z;
                }
                double safe = best > double.NegativeInfinity ? best + hop : Math.Max(a.Z, b.Z) + hop;
                return Math.Max(safe, Math.Max(a.Z, b.Z));
            }
            // seam "close enough to just connect": aligned vertically or nearby in XY,
            // and not descending - a dropping connect would plunge diagonally into the
            // material printed below (non-planar layers), so it must take the safe hop
            bool SeamClose(Vec3 from, Vec3 to)
            {
                double dx = to.X - from.X, dy = to.Y - from.Y;
                return Math.Sqrt(dx * dx + dy * dy) <= seamTol
                       && to.Z >= from.Z - 1.5 * c.LayerHeight;
            }

            // net E: what passed the nozzle - a retract and its prime cancel, the initial prime stays
            double EmitE(double de) { extrudedE += de; if (relE) return de; eAccum += de; return eAccum; }

            // The whole builder works in the Rhino design frame - that is what the
            // toolpath, the previews and the startPoint output are. Only the emitted
            // X/Y words cross into the machine's own Cartesian frame, so the mapping
            // lives here, in the four emitters and nowhere else. See MachineFrame.
            string XY(double x, double y)
            {
                double mx = x, my = y;
                if (c.CoordinateFrame == GcodeCoordinateFrame.LegacyScara)
                    MachineFrame.ToMachine(x, y, out mx, out my);
                return $"X{F3(mx)} Y{F3(my)}";
            }
            void G1Print(Vec3 from, Vec3 p, double de, int feed)
            {
                // material starts at the move's START point - that is where the layer
                // first hangs over the nozzle, matching the startPoint output contract
                firstActualPrint ??= new Vec3(from.X, from.Y, from.Z + zoff);
                L.Add(Invariant($"G1 {XY(p.X, p.Y)} Z{F3(p.Z + zoff)} F{feed} E{F5(EmitE(de))}"));
            }
            void G2G3(Move mv, double de, int feed, bool withE = true)
            {
                if (withE) firstActualPrint ??= new Vec3(mv.P0.X, mv.P0.Y, mv.P0.Z + zoff);
                double i = mv.Cx - mv.P0.X, j = mv.Cy - mv.P0.Y;
                int arcCode = mv.Cw ? 2 : 3;
                if (c.CoordinateFrame == GcodeCoordinateFrame.LegacyScara)
                {
                    MachineFrame.ToMachineArcOffset(i, j, out i, out j);
                    // the legacy frame map is a reflection, so it reverses arc sense
                    arcCode = MachineFrame.FlipArcCode(arcCode);
                }
                string code = arcCode == 2 ? "G2" : "G3";
                string head = Invariant($"{code} {XY(mv.P1.X, mv.P1.Y)} Z{F3(mv.P1.Z + zoff)} I{F3(i)} J{F3(j)} F{feed}");
                L.Add(withE ? $"{head} E{F5(EmitE(de))}" : head);
            }
            void G1Move(Vec3 p, int feed)
                => L.Add(Invariant($"G1 {XY(p.X, p.Y)} Z{F3(p.Z + zoff)} F{feed}"));
            void G0(double x, double y, double z, int feed)
                => L.Add(Invariant($"G0 {XY(x, y)} Z{F3(z + zoff)} F{feed}"));
            void EOnly(double de) { if (Math.Abs(de) < 1e-9) return; L.Add(Invariant($"G1 F{rF} E{F5(EmitE(de))}")); }

            // ---- per-axis accel (M201) / jerk (M566 RRF, M205 Marlin) around travel ----
            // Includes the E (ram) axis: on a 100:1 SCARA the retract/prime E move is the
            // biggest step-rate burst, so capping M201/M566 E is what stops the Duet 2
            // hiccuping ("pause") at each retract.
            bool hasAccel = c.AccelXY.HasValue || c.JerkXY.HasValue
                            || c.AccelE.HasValue || c.JerkE.HasValue;
            double accelFactor = Math.Max(0.0, Math.Min(1.0, c.RetractAccelFactor));
            void EmitAccel(double factor)
            {
                if (c.Flavor == GcodeFlavor.Klipper)
                {
                    // one SET_VELOCITY_LIMIT carries both ceilings; no per-axis E limit exists
                    string? limit = Header.KlipperVelocityLimit(c, factor);
                    if (limit != null) L.Add(limit);
                    return;
                }
                if (c.AccelXY.HasValue || c.AccelE.HasValue)
                {
                    string s = "M201";
                    if (c.AccelXY.HasValue)
                    {
                        int ax = (int)Math.Round(c.AccelXY.Value * factor);
                        int az = (int)Math.Round((c.AccelZ ?? c.AccelXY.Value) * factor);
                        s += Invariant($" X{ax} Y{ax} Z{az}");
                    }
                    if (c.AccelE.HasValue) s += Invariant($" E{(int)Math.Round(c.AccelE.Value * factor)}");
                    L.Add(s);
                }
                if (c.JerkXY.HasValue || c.JerkE.HasValue)
                {
                    string s = c.Flavor == GcodeFlavor.DuetRrf ? "M566" : "M205";
                    if (c.JerkXY.HasValue)
                    {
                        int jx = (int)Math.Round(c.JerkDialect(c.JerkXY.Value * factor));
                        int jz = (int)Math.Round(c.JerkDialect((c.JerkZ ?? c.JerkXY.Value) * factor));
                        s += Invariant($" X{jx} Y{jx} Z{jz}");
                    }
                    if (c.JerkE.HasValue) s += Invariant($" E{(int)Math.Round(c.JerkDialect(c.JerkE.Value * factor))}");
                    L.Add(s);
                }
            }
            // Soften accel/jerk for the retraction + travel moves, restore to the base
            // (header) limits before extruding again. No-ops when the group is off or the
            // factor is 1.0; the state guard skips re-emitting an already-active setting.
            int accelState = 0;   // 0 = base limits active (header), 1 = softened
            void SoftAccel() { if (hasAccel && accelFactor < 1.0 && accelState != 1) { EmitAccel(accelFactor); accelState = 1; } }
            void RestoreAccel() { if (hasAccel && accelFactor < 1.0 && accelState != 0) { EmitAccel(1.0); accelState = 0; } }
            double DoRetract() { if (offset > 0) { EOnly(-offset); return offset / Math.Max(c.RetractSpeed, 1e-9); } return 0.0; }
            double DoPrime()
            {
                if (prime > 0)
                {
                    EOnly(prime);
                    if (dwell > 0) L.Add(Invariant($"G4 P{(int)Math.Round(dwell)}"));
                    return prime / Math.Max(c.RetractSpeed, 1e-9);
                }
                return 0.0;
            }
            double FlowAt(double s, double baseMul)
            {
                double f = baseMul;
                if (leadIn > 1e-9 && s < leadIn) { double frac = s / leadIn; f *= leadBoost + (1.0 - leadBoost) * frac; }
                return f;
            }

            Vec3 ExtrudeContour(IReadOnlyList<Vec3> pts, double flowmul, IReadOnlyList<double>? flows,
                                IReadOnlyList<double>? heights, bool applyCoast, int feed,
                                int slowFeed = -1, double slowZmax = double.NaN)
            {
                double clen = PolylineOps.ContourLength(pts);
                double stopAt = applyCoast ? Math.Max(0.0, clen - coast) : clen;
                var ppoly = new List<Vec3>();
                void AppendPrintedMove(Move move)
                {
                    foreach (var point in ArcTessellation.ToPolyline(move, c.ArcTol))
                    {
                        if (ppoly.Count == 0 || PolylineOps.Dist(ppoly[ppoly.Count - 1], point) > 1e-9)
                            ppoly.Add(point);
                    }
                }
                var moves = c.UseArcs ? ArcFit.FitArcs(pts, c.ArcTol) : LineMoves(pts);
                // per-SEGMENT values apply on line moves, where moves map 1:1 to point
                // pairs; arc moves span many points and use the per-contour mean instead.
                // heights feed the h of the E-kinematics (geometry); flows stay a pure
                // material-calibration multiplier on top.
                bool perSegF = flows != null && !c.UseArcs
                               && flows.Count == pts.Count && moves.Count == pts.Count - 1;
                bool perSegH = heights != null && !c.UseArcs
                               && heights.Count == pts.Count && moves.Count == pts.Count - 1;
                double hContour = c.LayerHeight;
                if (heights != null && heights.Count > 0)
                { double s = 0; foreach (var h in heights) s += h; hContour = s / heights.Count; }
                int mi = -1;
                double acc = 0.0;
                foreach (var mv in moves)
                {
                    mi++;
                    double seg = ArcFit.ArcLengthOf(mv);
                    if (seg <= 1e-9) continue;
                    double baseMul = perSegF ? 0.5 * (flows![mi] + flows[mi + 1]) : flowmul;
                    double hSeg = perSegH ? 0.5 * (heights![mi] + heights[mi + 1]) : hContour;
                    double mul = FlowAt(acc + 0.5 * seg, baseMul);
                    double ef = EFactorH(hSeg) * mul;
                    // single-path prints: slow only the segments inside the first layer-height Z band
                    int mvFeed = feed;
                    if (slowFeed >= 0 && !double.IsNaN(slowZmax)
                        && 0.5 * (mv.P0.Z + mv.P1.Z) <= slowZmax + 1e-9)
                        mvFeed = slowFeed;
                    if (acc + seg <= stopAt + 1e-9)
                    {
                        double de = seg * ef;
                        if (mv.Kind == MoveKind.Arc) G2G3(mv, de, mvFeed); else G1Print(mv.P0, mv.P1, de, mvFeed);
                        totLen += seg;
                        totVol += RamExtrusion.DepositedVolumeMm3(seg, c.Width, hSeg);
                        totTime += seg / (mvFeed / 60.0);
                        AppendPrintedMove(mv);
                    }
                    else if (acc >= stopAt - 1e-9)
                    {
                        if (mv.Kind == MoveKind.Arc) G2G3(mv, 0, mvFeed, withE: false);
                        else G1Move(mv.P1, mvFeed);
                        totTime += seg / (mvFeed / 60.0);
                    }
                    else
                    {
                        double t = (stopAt - acc) / seg;
                        var mid = mv.PointAt(t);
                        var printedPart = mv.Portion(0, t);
                        if (printedPart.Kind == MoveKind.Arc) G2G3(printedPart, (seg * t) * ef, mvFeed);
                        else G1Print(mv.P0, mid, (seg * t) * ef, mvFeed);
                        var tail = mv.Portion(t, 1);
                        if (tail.Kind == MoveKind.Arc) G2G3(tail, 0, mvFeed, withE: false);
                        else G1Move(mv.P1, mvFeed);
                        totLen += seg * t;
                        totVol += RamExtrusion.DepositedVolumeMm3(seg * t, c.Width, hSeg);
                        totTime += seg / (mvFeed / 60.0);
                        AppendPrintedMove(printedPart);
                    }
                    acc += seg;
                }
                if (ppoly.Count >= 2)
                {
                    printPolys.Add(ppoly);
                    GridAddPoly(ppoly);
                }
                return pts[pts.Count - 1];
            }

            bool skirtEmitted = false;
            Vec3? EmitSkirt(IReadOnlyList<IReadOnlyList<Vec3>> firstLayer)
            {
                if (!c.Skirt || skirtEmitted) return null;
                var polys = new List<IReadOnlyList<Vec3>>();
                foreach (var ct in firstLayer) if (PolylineOps.IsClosed(ct)) polys.Add(ct);
                if (polys.Count == 0) return null;
                // orient by nesting so hole rings (CW) shrink away under the outward
                // offset; keep only CCW results - the skirt is the OUTER loop(s) only,
                // never a ring inside a part hole
                var oriented = new List<IReadOnlyList<Vec3>>();
                foreach (var r in Nesting.Orient(polys)) oriented.Add(r);
                var rings = Offset.Polygons(oriented, Math.Abs(c.SkirtOffset), polys[0][0].Z);
                rings.RemoveAll(r => PolylineOps.SignedAreaXy(r) <= 0);
                Vec3? end = null;
                foreach (var r in rings)
                {
                    if (r.Count < 3) continue;
                    var closed = new List<Vec3>(r) { r[0] };
                    if (end == null)
                    {
                        // the skirt is the FIRST printed material: it owns the initial
                        // approach and prime - extruding unprimed starves the layer
                        SoftAccel();
                        G0(r[0].X, r[0].Y, r[0].Z + hop, tF);
                        G0(r[0].X, r[0].Y, r[0].Z, hF);
                        totTime += DoPrime();
                    }
                    else SeamTravel(end.Value, r[0]);
                    RestoreAccel();
                    end = ExtrudeContour(closed, 1.0, null, null, false, flPF);
                }
                skirtEmitted = true;
                return end;
            }

            // ---- main loop ----
            Vec3? prevEnd = null;
            int nlayers = branches.Count;
            // single-path input (spiral/helix or lone layer) is auto-detected: slow only
            // the first layer-height band instead of treating the whole branch as the
            // "first layer". For a flat single layer the band covers everything, so the
            // output is unchanged.
            bool singlePath = branches.Count == 1;
            double slowBandZmax = branches.Count > 0 ? branches[0][0][0].Z + c.LayerHeight : 0.0;

            if (c.Flavor == GcodeFlavor.Klipper && nlayers > 0)
                L.Add(KlipperCommands.Format(KlipperCommands.SetPrintStatsInfo, ("TOTAL_LAYER", nlayers)));

            // seam transition. Close seams: plain connect, no retraction (except in
            // NormalRetraction, which retracts at every seam). Far (or descending)
            // seams: path-aware safe-hop travel, with retract + prime in Light/Normal
            // retraction, without them in NoRetraction (the path stays fully
            // connected, but the nozzle still must not drag through printed material).
            void SeamTravel(Vec3 from, Vec3 to)
            {
                double d = PolylineOps.Dist(from, to);
                if (d < 1e-9) return;   // shared endpoint (chained contours), nothing to do
                SoftAccel();
                if (!retractEverySeam && SeamClose(from, to))
                {
                    travelPolys.Add(new List<Vec3> { from, to });
                    G1Move(to, tF);
                    totTime += d / c.TravelSpeed;
                    return;
                }
                if (!connectAll)
                {
                    anyRetractSeam = true;
                    totTime += DoRetract();
                }
                double safeZ = SafeTravelZ(from, to);
                double hopSpd = c.HopSpeed > 1e-9 ? c.HopSpeed : c.TravelSpeed;
                if (safeZ > from.Z + 1e-9) L.Add(Invariant($"G0 Z{F3(safeZ + zoff)} F{hF}"));   // lift at hop speed
                G0(to.X, to.Y, safeZ, tF);
                if (safeZ > to.Z + 1e-9) G0(to.X, to.Y, to.Z, hF);   // descend at hop speed
                travelPolys.Add(new List<Vec3>
                {
                    from,
                    new Vec3(from.X, from.Y, safeZ),
                    new Vec3(to.X, to.Y, safeZ),
                    to,
                });
                totTime += (safeZ - from.Z + (safeZ - to.Z)) / hopSpd
                         + PolylineOps.Dist(new Vec3(from.X, from.Y, 0), new Vec3(to.X, to.Y, 0)) / c.TravelSpeed;
                if (!connectAll) totTime += DoPrime();
            }

            for (int bi = 0; bi < branches.Count; bi++)
            {
                var contours = branches[bi];
                bool isFirstLayer = bi == 0;
                // a single-path branch is not one big "first layer"; print it at full
                // speed and let ExtrudeContour slow only the first-band segments
                int feed = (!singlePath && isFirstLayer) ? flPF : pF;

                if (c.MinLayerTime > 0)
                {
                    double est = 0.0;
                    foreach (var ct in contours) est += PolylineOps.ContourLength(ct);
                    est /= Math.Max(c.PrintSpeed, 1e-6);
                    if (est > 0 && est < c.MinLayerTime)
                    {
                        feed = Math.Max(1, (int)Math.Round(feed * est / c.MinLayerTime));
                    }
                }

                Vec3 start = contours[0][0];
                Vec3? skirtEnd = null;
                if (isFirstLayer && c.Skirt) skirtEnd = EmitSkirt(contours);

                if (c.PauseAfterFloor && c.FloorLayers > 0 && bi == c.FloorLayers)
                {
                    L.Add(Invariant($"; ///// PAUSE AFTER FLOOR {c.PauseTime} ms /////"));
                    var pauseFrom = prevEnd ?? start;
                    double pauseZ = Math.Max(pauseFrom.Z + 100.0, SafeTravelZ(pauseFrom, pauseFrom));
                    SoftAccel();
                    if (!connectAll)
                    {
                        anyRetractSeam = true;
                        totTime += DoRetract();
                    }
                    L.Add(Invariant($"G0 Z{F3(pauseZ + zoff)} F{hF}"));
                    L.Add(Invariant($"G4 P{c.PauseTime}"));
                    L.Add(Invariant($"G0 Z{F3(pauseFrom.Z + zoff)} F{hF}"));
                    travelPolys.Add(new List<Vec3> { pauseFrom,
                        new Vec3(pauseFrom.X, pauseFrom.Y, pauseZ), pauseFrom });
                    totTime += (pauseZ - pauseFrom.Z) * 2.0 / Math.Max(c.HopSpeed > 1e-9 ? c.HopSpeed : c.TravelSpeed, 1e-9);
                    if (!connectAll) totTime += DoPrime();
                }

                if (bi == 0)
                {
                    if (skirtEnd.HasValue)
                    {
                        // the skirt already approached and primed - travel from its
                        // end to the part like any other seam, occupancy-aware
                        SeamTravel(skirtEnd.Value, start);
                    }
                    else
                    {
                        // initial approach: nothing is printed yet - hop above the start
                        SoftAccel();
                        G0(start.X, start.Y, start.Z + hop, tF);
                        G0(start.X, start.Y, start.Z, hF);   // descend at hop speed
                        totTime += DoPrime();
                    }
                }
                else if (prevEnd.HasValue) SeamTravel(prevEnd.Value, start);

                RestoreAccel();   // extrude at the base (header) accel limits
                L.Add(Invariant($"; layer {bi + 1}/{nlayers}"));
                if (c.Flavor == GcodeFlavor.Klipper)
                    L.Add(KlipperCommands.Format(KlipperCommands.SetPrintStatsInfo, ("CURRENT_LAYER", bi + 1)));
                for (int ci = 0; ci < contours.Count; ci++)
                {
                    var pts = contours[ci];
                    bool isLast = ci == contours.Count - 1;
                    double fmul = 1.0;
                    var fl = flows[bi][ci];
                    if (fl != null && fl.Count > 0)
                    {
                        double sum = 0.0; foreach (var v in fl) sum += v; fmul = sum / fl.Count;
                    }
                    if (ci > 0 && prevEnd.HasValue)
                    {
                        SeamTravel(prevEnd.Value, pts[0]);
                        RestoreAccel();
                    }
                    // first-band slow: never speed a segment past the (possibly
                    // MinLayerTime-reduced) branch feed
                    prevEnd = singlePath
                        ? ExtrudeContour(pts, fmul, fl, hgts[bi][ci], isLast, feed, Math.Min(flPF, feed), slowBandZmax)
                        : ExtrudeContour(pts, fmul, fl, hgts[bi][ci], isLast, feed);
                }
            }

            // single final retract so the ram is unloaded at the end of the print
            if (branches.Count > 0) { SoftAccel(); totTime += DoRetract(); }

            // integrates the local layer height when a heightTree is present; without one
            // this equals the classic totLen - LayerHeight - Width
            double volumeCm3 = totVol / 1000.0;
            // The previews must show what the machine is COMMANDED to do, not the raw
            // sliced geometry: every emitter writes `Z + zoff`, and StartPoint is already
            // documented as "as commanded, incl. ZOffset". Collecting the polylines at the
            // geometry Z made the Grasshopper preview disagree with its own G-code by
            // exactly ZOffset - the toolpath drew at the layer height while the printer ran
            // above it. Applied once, here, so no collection site can miss it.
            if (Math.Abs(zoff) > 1e-12)
            {
                static void Raise(List<List<Vec3>> polys, double dz)
                {
                    foreach (var poly in polys)
                        for (int i = 0; i < poly.Count; i++)
                            poly[i] = new Vec3(poly[i].X, poly[i].Y, poly[i].Z + dz);
                }
                Raise(printPolys, zoff);
                Raise(travelPolys, zoff);
            }

            return new GcodeResult
            {
                Lines = L,
                PrintPolys = printPolys,
                TravelPolys = travelPolys,
                Length = totLen,
                Volume = volumeCm3,
                TimeMin = totTime / 60.0,
                Retract = anyRetractSeam,
                Arcs = c.UseArcs,
                StartPoint = firstActualPrint,
                Warnings = warnings,
            };
        }
    }
}
