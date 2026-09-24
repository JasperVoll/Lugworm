using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;
using Lugworm.Core.Paths;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Shared toolpath/G-code viewer and optional robot simulator. Slicing
    /// and extrusion preview remain available for None/Cartesian definitions.</summary>
    public sealed class RobotSimulationComponent : IamComponentBase
    {
        public RobotSimulationComponent()
            : base("Robot Simulation", "Robot Sim",
                   "Preview supplied G-code or toolpath curves with an optional robot model, without moving the printer.",
                   "Lugworm", "4 Robot")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0026-4bab-9c3d-aa0000000026");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Robot();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("robot", "R", "Robot Definition. Empty or none shows the toolpath and extrusion without a robot model", "core", PType.Text),
            new ParamSpec("gcode", "G", "G-code as one string or a list of lines", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("toolpath", "C", "Toolpath curve tree in Rhino document units, used only when gcode is empty", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("sim", "S", "Scrub from 0 to 1 through the accumulated print", "core", PType.Number).Hint(0, 1, 1, 3),
            new ParamSpec("showRobot", "R?", "Show the selected robot model when available", "core", PType.Boolean).HintBool(true),
            new ParamSpec("showExtrusion", "E?", "Show deposited material as a continuous extrusion mesh", "core", PType.Boolean).HintBool(true),
            new ParamSpec("width", "W", "Layer width in mm for the extrusion preview", "core", PType.Number).Hint(0.5, 20, 10, 1),
            new ParamSpec("height", "H", "Extrusion or layer height, mm (0 = infer from the layer metadata)", "core", PType.Number).Hint(0, 10, 0, 2),
            new ParamSpec("bed", "B", "Print Bed definition text for the plate preview (unwired = no plate)", "core", PType.Text),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("joints", "J", "Joint values, one branch per joint in profile order, with rotary axes in deg and linear axes in mm", GH_ParamAccess.tree);
            pManager.AddCurveParameter("arm", "A", "Robot arm curve at the selected simulation position, in Rhino document units", GH_ParamAccess.item);
            pManager.AddCurveParameter("tcp", "T", "Displayed nozzle path in Rhino document units", GH_ParamAccess.item);
            pManager.AddCurveParameter("outline", "O", "Print Bed outline in Rhino document units", GH_ParamAccess.item);
            pManager.AddBooleanParameter("reachOK", "Ok", "True when all targets pass the selected robot's reach checks, or when no kinematics are selected", GH_ParamAccess.item);
            pManager.AddPointParameter("problems", "P", "Flagged target positions in Rhino document units", GH_ParamAccess.list);
            pManager.AddTextParameter("messages", "M", "Validation messages", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Profile and preview diagnostics", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public GH_Structure<GH_Number> Joints = new();
            public Curve? Arm, Tcp, Bed;
            public bool ReachOk = true;
            public List<Point3d> Problems = new();
            public List<string> Messages = new();
            public string Info = "";
        }

        /// <summary>Everything a solve derives from the toolpath SOURCE, kept between solves
        /// and reused whenever the source content and the options that shape it are the
        /// same.</summary>
        /// <remarks>Parsing a 58,000-line file, sampling it and running IK over every sample
        /// used to happen on every solve, including the ones where only the <c>sim</c> scrub
        /// slider had moved - and a Console G-code output that republished identical bytes on
        /// a position poll would have rebuilt all of it at telemetry cadence. The key is the
        /// content, so identical bytes cost nothing whatever the wire does.</remarks>
        sealed class SimState
        {
            public string Key = "";
            public List<Vec3> Solve = new List<Vec3>();
            public List<Vec3> Draw = new List<Vec3>();
            public List<bool> Extruding = new List<bool>();
            public List<int>? LayerIds;
            public RobotSimulationResult? Simulation;
            public string SourceInfo = "";
        }

        SimState? _simState;

        readonly BedPreview _bed = new BedPreview();
        readonly ExtrusionPreview.Cache _extrusionCache = new ExtrusionPreview.Cache();
        readonly List<Mesh> _robotMeshes = new List<Mesh>();
        readonly List<Line> _robotEdges = new List<Line>();   // silhouette + crease edges for form
        Mesh? _extrusionMesh;
        readonly List<Polyline> _printedRuns = new List<Polyline>();
        readonly List<Polyline> _travelRuns = new List<Polyline>();
        BoundingBox _clip = BoundingBox.Empty;
        bool _showRobot, _showExtrusion;
        // The robot draws as a ghost (RobotPreview): translucent blue with depth
        // writing off, feature edges on top. Robot and Live View still draw flat.
        // Deposition vs. travel. Blue keeps the deposited path reading as the material
        // the other components already draw in RoyalBlue; travel is a warmer, thinner
        // line so it stays legible over the near-white bed without competing with it.
        static readonly Color PrintedColor = Color.RoyalBlue;
        static readonly Color TravelColor = Color.FromArgb(214, 148, 62);       // ochre - rapid, no material
        const double FeatureAngleRad = 0.55;                                   // ~31 deg crease threshold

        protected override void BeforeSolveInstance()
        {
            _robotMeshes.Clear();
            _robotEdges.Clear();
            _extrusionMesh = null;
            _printedRuns.Clear();
            _travelRuns.Clear();
            _clip = BoundingBox.Empty;
            base.BeforeSolveInstance();
        }

        // document→mm factor of the current solve (AUD-COR-001). All simulation and
        // preview state stays mm; drawing and outputs divide by it on the way out.
        double _unitScale = 1.0;

        protected override object? Compute(IGH_DataAccess DA)
        {
            try { return ComputeCore(DA); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the preview state was cleared in BeforeSolveInstance, so a failed solve
                // draws nothing rather than a half-built scene
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Simulation failed: " + ex.Message);
                return null;
            }
        }

        object? ComputeCore(IGH_DataAccess DA)
        {
            _unitScale = DocUnits.ScaleToMm();
            RobotProfile profile;
            try { profile = RobotCatalog.Resolve(GetString(DA, "robot", "none")); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return null; }

            var lines = GetStringList(DA, "gcode");
            string gcode = lines.Count == 1 ? lines[0] : string.Join("\n", lines);
            var tree = GetCurveTree(DA, "toolpath");
            bool haveGcode = !string.IsNullOrWhiteSpace(gcode);
            bool haveToolpath = tree != null && !tree.IsEmpty;
            if (!haveGcode && !haveToolpath)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Connect G-code or a toolpath");
                return null;
            }
            if (haveGcode && haveToolpath)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "G-code selected, toolpath ignored");

            double width = Math.Max(0.01, GetNumber(DA, "width", 10.0));

            var options = new RobotSimulationOptions();
            if (profile.Mechanism == RobotMechanism.Scara)
                options.ScaraArmMode = profile.CreateScaraLimits().HomedArmMode;

            // Everything the source-derived state depends on besides the source itself. The
            // scrub slider is deliberately NOT in it: moving it must not re-parse a file.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string optionKey = profile.Id + "|" + options.ScaraArmMode.ToString(inv)
                             + "|" + width.ToString("R", inv) + "|" + _unitScale.ToString("R", inv);

            SimState? state = null;
            var cached = _simState;
            if (haveGcode)
            {
                string key = optionKey + "|g" + ContentHash.Fnv1a(gcode).ToString("x16", inv);
                if (cached != null && string.Equals(cached.Key, key, StringComparison.Ordinal)) state = cached;
                else state = BuildSimState(key, profile, options, gcode, null, width);
            }
            else
            {
                // A curve tree has no content hash of its own, so it is sampled first and the
                // SAMPLES are hashed; that skips the inverse kinematics, which is the
                // expensive half, when nothing upstream actually moved.
                List<Vec3> sampledPoints;
                List<bool> sampledExtruding;
                List<int>? sampledLayers;
                try
                {
                    var sampled = ToolpathSampler.SampleWithMetadata(tree!, ExtrusionPreview.ChordTolerance(width),
                                                                     scaleToMm: _unitScale);
                    sampledPoints = sampled.pts;
                    sampledExtruding = sampled.ext;
                    sampledLayers = sampled.layerIds;
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Toolpath parse failed: " + ex.Message);
                    return null;
                }
                if (sampledPoints.Count == 0) return null;
                string key = optionKey + "|t" + ContentHash.Fnv1a(sampledPoints, sampledExtruding).ToString("x16", inv);
                if (cached != null && string.Equals(cached.Key, key, StringComparison.Ordinal)) state = cached;
                else state = BuildSimState(key, profile, options, null,
                                           (sampledPoints, sampledExtruding, sampledLayers), width);
            }
            if (state == null) return null;
            _simState = state;

            var drawPoints = state.Draw;
            var extruding = state.Extruding;
            var layerIds = state.LayerIds;
            string sourceInfo = state.SourceInfo;
            var simulation = state.Simulation!;
            var result = new Result { ReachOk = simulation.ReachOk };
            int jointCount = simulation.JointsDeg.Count == 0 ? 0 : simulation.JointsDeg.Max(row => row.Length);
            for (int joint = 0; joint < jointCount; joint++)
            {
                var path = new GH_Path(joint);
                foreach (var row in simulation.JointsDeg)
                    result.Joints.Append(new GH_Number(joint < row.Length ? row[joint] : double.NaN), path);
            }

            foreach (var problem in simulation.Problems)
            {
                Vec3 p = profile.GcodeCoordinateFrame == GcodeCoordinateFrame.LegacyScara
                    ? MachineFrame.ToRhino(problem.At) : problem.At;
                result.Problems.Add(DocUnits.FromMm(GhConvert.ToPt(p), _unitScale));
                result.Messages.Add(problem.ToString());
            }
            if (!simulation.ReachOk)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"{profile.DisplayName}: one or more targets are outside robot limits");

            result.Tcp = drawPoints.Count >= 2 ? GhConvert.PtsToCurve(drawPoints, _unitScale) : null;
            if (drawPoints.Count >= 2) _clip.Union(GhConvert.PtsToCurve(drawPoints).GetBoundingBox(false));

            double sim = Math.Max(0, Math.Min(1, GetNumber(DA, "sim", 1.0)));
            int scrub = Math.Max(0, Math.Min(drawPoints.Count - 1,
                (int)Math.Round(sim * (drawPoints.Count - 1))));
            if (scrub >= 1)
            {
                // Split the walked prefix into deposition and travel so the two read
                // differently. Same maximal-run logic the layer mesh uses, so a run drawn
                // here and a layer built there cannot disagree about what extrudes.
                foreach (var run in ExtrusionHistory.VisibleRuns(drawPoints, extruding, scrub))
                    _printedRuns.Add(new Polyline(run.Select(GhConvert.ToPt)));
                foreach (var run in ExtrusionHistory.TravelRuns(drawPoints, extruding, scrub))
                    _travelRuns.Add(new Polyline(run.Select(GhConvert.ToPt)));
            }

            _showRobot = GetBool(DA, "showRobot", true) && profile.HasKinematics;
            _showExtrusion = GetBool(DA, "showExtrusion", true);
            if (_showExtrusion)
            {
                double height = GetNumber(DA, "height", 0.0);
                if (height <= 1e-6) height = ExtrusionPreview.AutoLayerHeight(drawPoints, 3.5, layerIds);
                string cacheKey = profile.Id + "|" + ContentHash.Fnv1a(drawPoints, extruding);
                _extrusionMesh = ExtrusionPreview.BuildCached(_extrusionCache, cacheKey,
                    drawPoints, extruding, scrub, width, height);
                if (_extrusionMesh != null) _clip.Union(_extrusionMesh.GetBoundingBox(false));
            }

            BuildBed(DA, profile, options, out Curve? bedCurve);
            result.Bed = bedCurve;

            if (_showRobot && scrub < simulation.JointsDeg.Count)
            {
                result.Arm = BuildRobotPose(profile, simulation.JointsDeg[scrub], drawPoints[scrub]);
                if (result.Arm != null && !DocUnits.IsUnity(_unitScale))
                    result.Arm.Transform(DocUnits.FromMmTransform(_unitScale));
            }

            result.Info = $"{sourceInfo} - {profile.DisplayName} - kinematics:{simulation.KinematicsApplied} - " +
                          $"reachOK:{simulation.ReachOk} - scrub:{scrub}/{drawPoints.Count - 1}";
            Message = profile.Mechanism == RobotMechanism.None ? "Toolpath" : profile.ModelKey;
            return result;
        }

        /// <summary>Parses or adopts the source, converts frames and runs the kinematics
        /// once. Returns null and reports when the source cannot be read.</summary>
        /// <param name="gcode">The G-code to parse, or null when sampled curves are given.</param>
        /// <param name="sampled">Already sampled curve data, or null for the G-code path.</param>
        SimState? BuildSimState(string key, RobotProfile profile, RobotSimulationOptions options,
                                string? gcode,
                                (List<Vec3> pts, List<bool> ext, List<int>? layers)? sampled,
                                double width)
        {
            List<Vec3> sourcePoints;
            List<bool> extruding;
            List<int>? layerIds = null;
            ParsedProgram? parsedProgram = null;
            string sourceInfo;
            if (gcode != null)
            {
                try
                {
                    Vec3? home = profile.Mechanism == RobotMechanism.Scara
                        ? profile.CreateScaraLimits().CalibratedHomePose : null;
                    parsedProgram = GcodeParser.Parse(gcode.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), home,
                        allowPerAxisHome: profile.Mechanism == RobotMechanism.Cartesian);
                    double? arcTol = profile.Mechanism == RobotMechanism.Scara
                        ? profile.CreateScaraLimits().ArcChordTol : null;
                    var moves = ToolpathSampler.SampleMovesWithFeeds(parsedProgram.Moves, arcTol);
                    sourcePoints = moves.pts;
                    extruding = moves.ext;
                    sourceInfo = $"gcode moves:{parsedProgram.Moves.Count}";
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Toolpath parse failed: " + ex.Message);
                    return null;
                }
            }
            else
            {
                sourcePoints = sampled!.Value.pts;
                extruding = sampled.Value.ext;
                layerIds = sampled.Value.layers;
                sourceInfo = $"toolpath pts:{sourcePoints.Count}";
            }
            if (sourcePoints.Count == 0) return null;

            // G-code points are already in the emitted machine frame; curves arrive
            // in the displayed design frame. Convert only for the calibrated legacy
            // SCARA profile.
            List<Vec3> solvePoints;
            List<Vec3> drawPoints;
            if (profile.GcodeCoordinateFrame == GcodeCoordinateFrame.LegacyScara)
            {
                if (gcode != null)
                {
                    solvePoints = sourcePoints;
                    drawPoints = sourcePoints.Select(MachineFrame.ToRhino).ToList();
                }
                else
                {
                    drawPoints = sourcePoints;
                    solvePoints = sourcePoints.Select(MachineFrame.ToMachine).ToList();
                }
            }
            else
            {
                solvePoints = new List<Vec3>(sourcePoints);
                drawPoints = new List<Vec3>(sourcePoints);
            }

            var simulation = parsedProgram != null
                ? RobotSimulation.SolveProgram(profile, parsedProgram, solvePoints,
                    GcodeFlavor.DuetRrf, arcSupport: true, options: options)
                : RobotSimulation.Solve(profile, solvePoints, options);

            return new SimState
            {
                Key = key,
                Solve = solvePoints,
                Draw = drawPoints,
                Extruding = extruding,
                LayerIds = layerIds,
                Simulation = simulation,
                SourceInfo = sourceInfo,
            };
        }

        void BuildBed(IGH_DataAccess DA, RobotProfile profile, RobotSimulationOptions options, out Curve? curve)
        {
            curve = null;
            _bed.Clear();
            // The wired Print Bed is the only source of plate geometry. The
            // profile-specific fallback plate is gone: a plate the operator never
            // configured looked exactly like one they had, and the two silently
            // disagreed with Live View's own defaults.
            BedDefinition? bed = null;
            try { bed = BedDefinitionText.Resolve(GetString(DA, "bed", "")); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Print bed: " + ex.Message); return; }
            if (bed == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No print bed wired, so no plate is drawn. Connect a Print Bed component to 'bed'");
                return;
            }

            double rMin = 0, rMax = Math.Max(bed.WidthMm, bed.DepthMm) * 4;
            Func<double, double, bool>? printable = (x, y) => true;
            if (profile.Mechanism == RobotMechanism.Scara)
            {
                var limits = options.ScaraLimits ?? profile.CreateScaraLimits();
                rMin = limits.ReachMin;
                rMax = limits.ReachMax > 0 ? limits.ReachMax : limits.L1 + limits.L2;
                printable = null;
            }
            // Reach belongs to the arm, so the robot profile stays the sole authority -
            // the bed supplies plate geometry, never the annulus. The physical plate and
            // the reachable region are different things.
            _bed.Build(bed, rMin, rMax, printable);
            curve = _bed.Frame?.ToPolylineCurve();
            if (curve != null) DocUnits.FromMmInPlace(curve, _unitScale);
            if (_bed.Bounds.IsValid) _clip.Union(_bed.Bounds);
        }

        Curve? BuildRobotPose(RobotProfile profile, double[] joints, Vec3 tcp)
        {
            if (joints.Any(double.IsNaN)) return null;
            if (profile.Mechanism == RobotMechanism.Scara && joints.Length >= 3)
            {
                var model = Lugworm.Robot.RobotModel.Default;
                if (model != null)
                {
                    foreach (var (mesh, _) in model.PosedMeshes(joints[0], joints[1], joints[2]))
                    {
                        _robotMeshes.Add(mesh); _clip.Union(mesh.GetBoundingBox(false));
                    }
                    // cached on the base meshes, transformed per frame (scrub hotspot)
                    _robotEdges.AddRange(model.PosedFeatureEdges(joints[0], joints[1], joints[2], FeatureAngleRad));
                }

                var limits = profile.CreateScaraLimits();
                var (mx, my) = ScaraKinematics.Elbow(joints[0], limits.L1);
                MachineFrame.ToRhino(mx, my, out double ex, out double ey);
                var polyline = new Polyline
                {
                    new Point3d(0, 0, 0), new Point3d(0, 0, tcp.Z),
                    new Point3d(ex, ey, tcp.Z), GhConvert.ToPt(tcp),
                };
                return polyline.ToPolylineCurve();
            }
            return null;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var value = (Result)result;
            DA.SetDataTree(0, value.Joints);
            DA.SetData(1, value.Arm);
            DA.SetData(2, value.Tcp);
            DA.SetData(3, value.Bed);
            DA.SetData(4, value.ReachOk);
            DA.SetDataList(5, value.Problems);
            DA.SetDataList(6, value.Messages);
            DA.SetData(7, value.Info);
        }

        public override BoundingBox ClippingBox
        {
            get
            {
                if (!_clip.IsValid) return base.ClippingBox;
                var box = _clip;
                // preview state is mm; the viewport shows document units (AUD-COR-001)
                if (!DocUnits.IsUnity(_unitScale)) box.Transform(DocUnits.FromMmTransform(_unitScale));
                return box;
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            bool scaled = !DocUnits.IsUnity(_unitScale);
            if (scaled) args.Display.PushModelTransform(DocUnits.FromMmTransform(_unitScale));
            try
            {
                _bed.DrawMeshes(args.Display);
                if (_extrusionMesh != null) ExtrusionPreview.Draw(args.Display, _extrusionMesh);
                // after the opaque scene, so the ghost blends over the bed and the layers
                if (_showRobot) RobotPreview.DrawGhost(args.Display, _robotMeshes);
            }
            finally { if (scaled) args.Display.PopModelTransform(); }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            bool scaled = !DocUnits.IsUnity(_unitScale);
            if (scaled) args.Display.PushModelTransform(DocUnits.FromMmTransform(_unitScale));
            try
            {
                _bed.DrawWires(args.Display);
                if (_showRobot) RobotPreview.DrawEdges(args.Display, _robotEdges);
                // Travel always draws: it is the one part of the path the layer mesh cannot
                // represent, so hiding it with the mesh on would leave the jumps invisible.
                foreach (var travel in _travelRuns) args.Display.DrawPolyline(travel, TravelColor, 1);
                if (!_showExtrusion)
                {
                    if (_printedRuns.Count > 0)
                        foreach (var printed in _printedRuns) args.Display.DrawPolyline(printed, PrintedColor, 2);
                }
            }
            finally { if (scaled) args.Display.PopModelTransform(); }
        }
    }
}
