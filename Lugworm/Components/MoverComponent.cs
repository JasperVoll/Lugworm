using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;
using Lugworm.Core.Paths;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Places a contour tree at the global print centre (one point: the part's
    /// bottom-centre lands there) and sets the seam per layer. Light + live (no compute
    /// button). Port of ClayMover2.py.</summary>
    public class MoverComponent : IamComponentBase
    {
        public MoverComponent()
            : base("Place", "Place",
                   "Place contours on the bed and set each layer's seam.",
                   "Lugworm", "2 Place")
        { }

        public override Guid ComponentGuid => new Guid("b1a2c3d4-0002-4a2b-9c3d-aa0000000002");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.Mover();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("contours", "C", "Layer contour tree from Slice, or equivalent curves in Rhino document units", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("printCenter", "Pt", "Part bottom-center position in Rhino document units, overriding the Print Bed default when wired", "core", PType.Point),
            new ParamSpec("zOffset", "Z", "Additional vertical shift after placement, mm", "core", PType.Number).Hint(-10, 50, 0, 1),
            new ParamSpec("move", "Mv", "Additional XYZ translation vector in mm after placement", "core", PType.Vector),
            new ParamSpec("seamMode", "S", "0 = keep, 1 = align (default), 2 = stagger, 3 = nearest, 4 = guide, 5 = track", "core", PType.Integer),
            new ParamSpec("seamAngle", "A", "Direction for mode 1, or the step per layer for mode 2, deg", "seam", PType.Number).Hint(0, 360, 0, 0),
            new ParamSpec("seamPoint", "P", "Target in Rhino document units for nearest seams, or the first layer in track mode", "seam", PType.Point),
            new ParamSpec("seamCurve", "Gc", "Guide curve at the placed part in Rhino document units, required for seamMode 4", "seam", PType.Curve),
            // appended last: promoting 'bed' out of its old toggle group must not move
            // the sockets a saved document already wired (Grasshopper restores by index)
            new ParamSpec("bed", "B", "Print Bed definition text supplying the default printCenter", "core", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("seam", "Seam direction / target inputs"),
        };

        // 1: 'bed' left its toggle group for the always-visible end of the input list.
        // Nothing was removed, so a saved document only needs its sockets put back in
        // declaration order - no bed settings to migrate.
        protected override int InputContractVersion => 1;

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("seamMode", "Seam mode: create value list",
             new[] { ("Keep", "0"), ("Align", "1"), ("Stagger", "2"), ("Nearest", "3"),
                     ("Guide curve", "4"), ("Track below", "5") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("moved", "M", "Placed contour tree in Rhino document units, preserving the input branch structure", GH_ParamAccess.tree);
            pManager.AddPointParameter("seams", "S", "Start point per contour in Rhino document units", GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "i", "Placement and seam settings or warnings", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public GH_Structure<GH_Curve> Moved = new();
            public GH_Structure<GH_Point> Seams = new();
            public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var tree = GetCurveTree(DA, "contours");
            var res = new Result();
            if (tree == null || tree.IsEmpty) { res.Info = "no contours"; return res; }

            // Print-centre precedence, in order: an explicitly wired printCenter, then
            // a wired Print Bed definition, then the historical lab literal.
            //
            // printCenter stays because it is a PLACEMENT operation - where this one
            // part stands - not bed configuration. The bed owns the default; this input
            // moves a single part off it.
            //
            // The bed's PLATE centre is deliberately not used here. A part's bottom-centre
            // and the plate's centre are different quantities - (339,-193) vs (468,-441)
            // on this machine - so falling back to the plate centre would silently
            // relocate every part in every saved document.
            // document-unit adapter (AUD-COR-001): the lab literal, bed print centre,
            // zOffset and move are physical mm; the curves live in document units, so
            // the mm quantities convert into document space for the translation
            double unitScale = DocUnits.ScaleToMm();
            var printCenter = DocUnits.FromMm(new Point3d(339, -193, 0), unitScale);
            BedDefinition? bed = null;
            try { bed = BedDefinitionText.Resolve(GetString(DA, "bed", "")); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Print bed: " + ex.Message); }
            if (bed != null) printCenter = DocUnits.FromMm(GhConvert.ToPt(bed.PrintCenter), unitScale);
            if (IsWired("printCenter")) printCenter = GetPoint(DA, "printCenter", printCenter);
            double zOffset = GetNumber(DA, "zOffset", 0.0) / unitScale;
            int seamMode = GetInt(DA, "seamMode", 1);   // Align - the lab default
            if (seamMode < 0 || seamMode > 5)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Invalid seam mode {seamMode}. Supported modes are 0 to 5");
                res.Info = $"invalid seam mode {seamMode} (expected 0 to 5)";
                Message = $"invalid seam {seamMode}";
                return res;
            }
            var mode = (SeamMode)seamMode;
            double seamAngle = GetNumber(DA, "seamAngle", 0.0);
            Point3d seamPoint = GetPoint(DA, "seamPoint", Point3d.Unset);
            Curve? seamCurve = GetCurve(DA, "seamCurve");
            if (seamMode == 4 && seamCurve == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Seam mode 4 (guide) needs the seamCurve input from the seam toggle group. Seams were kept as they are");

            // overall bounding box for centering / dropping
            var bb = BoundingBox.Empty;
            foreach (var branch in tree.Branches)
                foreach (var goo in branch)
                    if (goo?.Value != null) bb.Union(goo.Value.GetBoundingBox(true));
            if (!bb.IsValid) { res.Info = "no valid curves"; return res; }

            double cx = 0.5 * (bb.Min.X + bb.Max.X);
            double cy = 0.5 * (bb.Min.Y + bb.Max.Y);
            var move = GetVector(DA, "move", Vector3d.Zero) / unitScale;   // labelled mm
            // printCenter positions the part: its bottom-centre (bbox centre XY, base Z)
            // lands on printCenter. zOffset and the move vector are optional nudges added on
            // top, so printCenter alone fully positions the part. (Core.Placement, tested.)
            var off = Core.Geometry.Placement.Offset(
                new Vec3(cx, cy, 0.0), bb.Min.Z,
                new Vec3(printCenter.X, printCenter.Y, printCenter.Z),
                zOffset, new Vec3(move.X, move.Y, move.Z));
            var xform = Transform.Translation(off.X, off.Y, off.Z);

            // mode 5 (track): each closed contour's seam lands at the closest point to
            // the SAME contour's seam on the previous layer - per (object, contour)
            // lineage, so an outer wall never tracks an inner wall, a hole ring, or
            // another object (kills seam wander without cross-contour contamination).
            var prevSeams = new Dictionary<(int obj, int contour), Point3d>();
            var prevOpenStarts = new Dictionary<(int obj, int open), Vec3>();

            bool SetSeam(Curve crv, int li, Point3d trackTarget, Vec3? nearestOpenTarget)
            {
                if (!crv.IsClosed || mode == SeamMode.Keep) return false;
                var amp = AreaMassProperties.Compute(crv);
                var cen = amp != null ? amp.Centroid : crv.GetBoundingBox(true).Center;
                Vec3? guideClosestPoint = null;
                if (seamCurve != null && seamCurve.ClosestPoint(cen, out double tg))
                {
                    // closest point of the guide to THIS layer (guide is world-space,
                    // evaluated near the contour's centroid height)
                    var guidePoint = seamCurve.PointAt(tg);
                    guideClosestPoint = new Vec3(guidePoint.X, guidePoint.Y, guidePoint.Z);
                }

                var nearestTarget = mode == SeamMode.Nearest && nearestOpenTarget.HasValue
                    ? nearestOpenTarget
                    : seamPoint.IsValid ? new Vec3(seamPoint.X, seamPoint.Y, seamPoint.Z) : null;
                var target = SeamPlacer.TargetFor(
                    mode,
                    new Vec3(cen.X, cen.Y, cen.Z),
                    crv.GetBoundingBox(true).Diagonal.Length,
                    seamAngle,
                    li,
                    nearestTarget,
                    guideClosestPoint,
                    trackTarget.IsValid ? new Vec3(trackTarget.X, trackTarget.Y, trackTarget.Z) : null);
                if (!target.HasValue) return false;

                var targetPoint = new Point3d(target.Value.X, target.Value.Y, target.Value.Z);
                if (!targetPoint.IsValid) return false;
                if (!crv.ClosestPoint(targetPoint, out double t)) return false;
                crv.ChangeClosedCurveSeam(t);
                return true;
            }

            int n = 0, nOpen = 0, openFlipped = 0, closed = 0, seamApplied = 0, li = 0;
            foreach (var branch in tree.Branches)
            {
                var path = tree.Paths[li];
                int objectLineage = path.Length > 1 ? path[0] : 0;
                int layerIndex = path.Length > 0 ? path[path.Length - 1] : li;
                int ci = -1;
                var curves = new List<Curve>();
                foreach (var goo in branch)
                {
                    if (goo?.Value == null) continue;
                    var curve = goo.Value.DuplicateCurve();
                    curve.Transform(xform);
                    curves.Add(curve);
                }

                var closedIndices = new List<int>();
                var openIndices = new List<int>();
                for (int i = 0; i < curves.Count; i++)
                {
                    if (curves[i].IsClosed) closedIndices.Add(i);
                    else openIndices.Add(i);
                }

                double EndpointDistance(Curve wall, Point3d endpoint)
                {
                    if (!wall.ClosestPoint(endpoint, out double t)) return double.PositiveInfinity;
                    return endpoint.DistanceTo(wall.PointAt(t));
                }

                var layerBox = BoundingBox.Empty;
                foreach (var curve in curves) layerBox.Union(curve.GetBoundingBox(true));
                Point3d layerCentroid;
                if (closedIndices.Count > 0)
                {
                    var amp = AreaMassProperties.Compute(curves[closedIndices[0]]);
                    layerCentroid = amp != null ? amp.Centroid : curves[closedIndices[0]].GetBoundingBox(true).Center;
                }
                else layerCentroid = layerBox.Center;

                Vec3? guideTarget = null;
                if (seamCurve != null && seamCurve.ClosestPoint(layerCentroid, out double guideParameter))
                {
                    var guidePoint = seamCurve.PointAt(guideParameter);
                    guideTarget = new Vec3(guidePoint.X, guidePoint.Y, guidePoint.Z);
                }
                Point3d outerTrack = Point3d.Unset;
                if (closedIndices.Count > 0
                    && prevSeams.TryGetValue((objectLineage, closedIndices[0]), out var trackedOuter))
                    outerTrack = trackedOuter;
                if (!outerTrack.IsValid && seamPoint.IsValid) outerTrack = seamPoint;
                Vec3? layerTarget = SeamPlacer.TargetFor(
                    mode,
                    new Vec3(layerCentroid.X, layerCentroid.Y, layerCentroid.Z),
                    layerBox.IsValid ? layerBox.Diagonal.Length : 0.0,
                    seamAngle,
                    layerIndex,
                    seamPoint.IsValid ? new Vec3(seamPoint.X, seamPoint.Y, seamPoint.Z) : null,
                    guideTarget,
                    outerTrack.IsValid ? new Vec3(outerTrack.X, outerTrack.Y, outerTrack.Z) : null);

                var reverseOpen = new Dictionary<int, bool>();
                var openSeamTargets = new Dictionary<int, Vec3>();
                var endpointDistances = new Dictionary<(int closed, int open), (double start, double end)>();
                foreach (int closedIndex in closedIndices)
                    foreach (int openIndex in openIndices)
                        endpointDistances[(closedIndex, openIndex)] = (
                            EndpointDistance(curves[closedIndex], curves[openIndex].PointAtStart),
                            EndpointDistance(curves[closedIndex], curves[openIndex].PointAtEnd));

                for (int openOrdinal = 0; mode != SeamMode.Keep && openOrdinal < openIndices.Count; openOrdinal++)
                {
                    int openIndex = openIndices[openOrdinal];
                    var open = curves[openIndex];
                    double startDistance = double.PositiveInfinity;
                    double endDistance = double.PositiveInfinity;
                    foreach (int closedIndex in closedIndices)
                    {
                        var wallPair = endpointDistances[(closedIndex, openIndex)];
                        startDistance = Math.Min(startDistance, wallPair.start);
                        endDistance = Math.Min(endDistance, wallPair.end);
                    }
                    double layerWidth = Math.Min(startDistance, endDistance);
                    if (double.IsNaN(layerWidth) || double.IsInfinity(layerWidth) || layerWidth <= 1e-9)
                        layerWidth = 1e-6;
                    var start = new Vec3(open.PointAtStart.X, open.PointAtStart.Y, open.PointAtStart.Z);
                    var end = new Vec3(open.PointAtEnd.X, open.PointAtEnd.Y, open.PointAtEnd.Z);
                    var openKey = (objectLineage, openOrdinal);
                    Vec3? previousStart = prevOpenStarts.TryGetValue(openKey, out var prior) ? prior : null;
                    var orientation = SeamPlacer.OrientOpenPath(
                        start, end, startDistance, endDistance, layerTarget, previousStart,
                        new Vec3(layerCentroid.X, layerCentroid.Y, layerCentroid.Z), layerWidth);
                    reverseOpen[openIndex] = orientation.Reverse;
                    openSeamTargets[openIndex] = orientation.Start;
                    prevOpenStarts[openKey] = orientation.Start;
                    if (orientation.Reverse) openFlipped++;
                }

                Vec3? primaryOpenStart = openIndices.Count > 0
                    && openSeamTargets.TryGetValue(openIndices[0], out var primaryStart)
                    ? primaryStart : null;

                for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
                {
                    ci++;
                    var d = curves[curveIndex];
                    if (reverseOpen.TryGetValue(curveIndex, out bool flipOpen) && flipOpen)
                        d.Reverse();
                    if (!d.IsClosed) nOpen++;
                    else closed++;
                    var key = (objectLineage, ci);
                    Point3d track = prevSeams.TryGetValue(key, out var ps) ? ps
                                  : seamPoint.IsValid ? seamPoint : Point3d.Unset;
                    Vec3? nearestOpenTarget = mode == SeamMode.Nearest ? primaryOpenStart : null;
                    if (SetSeam(d, layerIndex, track, nearestOpenTarget)) seamApplied++;
                    res.Moved.Append(new GH_Curve(d), path);
                    var sp = d.PointAtStart;
                    res.Seams.Append(new GH_Point(sp), path);
                    if (d.IsClosed) prevSeams[key] = sp;   // this contour's seam feeds the NEXT layer's same contour
                    n++;
                }
                li++;
            }

            string[] modes = { "keep", "align", "stagger", "nearest", "guide", "track" };
            res.Info = $"curves: {n} ({nOpen} open/non-planar)  dx={off.X:0.00} dy={off.Y:0.00} dz={off.Z:0.00}  " +
                       $"seam={modes[seamMode]}  closed:{closed}  applied:{seamApplied}  " +
                       $"open flipped: {openFlipped}/{nOpen}";
            Message = seamMode == 0
                ? $"keep - {n} curves"
                : $"{modes[seamMode]} - {seamApplied}/{closed} closed";
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataTree(0, r.Moved);
            DA.SetDataTree(1, r.Seams);
            DA.SetData(2, r.Info);
        }
    }
}
