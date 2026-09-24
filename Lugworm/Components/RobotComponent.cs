using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>SCARA simulator + Marlin/Duet G-code validator (3DPotter Potterbot
    /// Scara v4). Parses the G-code, runs IK per move, draws the arctic bed plate
    /// (white printable area = bed ∩ reach annulus, grey unreachable, 50/250mm grid,
    /// axes), the toolpath / white extruded layers, and the posed assembly at the scrub
    /// position. Validates reachability / dead-zone / singularity / joint-speed /
    /// firmware syntax. Live (re-solves with the scrub).</summary>
    public class RobotComponent : IamComponentBase
    {
        public RobotComponent()
            : base("Robot", "Robot",
                   "Supply G-code or toolpath curves to preview and check the Potterbot SCARA without moving it.",
                   "Lugworm", "4 Robot")
        { }

        public override Guid ComponentGuid => new Guid("d1a2c3d4-0004-4a4b-9c3d-aa0000000004");
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.Robot();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code text or lines in Potterbot machine coordinates, converted to Rhino coordinates for preview", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("toolpath", "C", "Placed curves to visualize directly, before G-code is made. Rhino coordinates", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("L1", "L1", "First SCARA link length in mm", "core", PType.Number).Hint(100, 1000, 549.06, 2),
            new ParamSpec("L2", "L2", "Second SCARA link length in mm", "core", PType.Number).Hint(100, 1000, 457.23, 2),
            new ParamSpec("baseHeight", "B", "Base Z height, mm", "core", PType.Number).Hint(0, 500, 0, 0),
            new ParamSpec("firmware", "FW", "0 = Duet RRF, 1 = Marlin", "core", PType.Integer),
            new ParamSpec("elbow", "E", "Arm mode, 0 up or 1 down, for validation and drawing when no home anchors the path. Home anchored paths use the machine's homing mode", "core", PType.Integer),
            new ParamSpec("arcSupport", "AS", "Allow G2/G3 arcs in validation when true, or report unsupported arcs when false", "core", PType.Boolean).HintBool(true),
            new ParamSpec("sim", "S", "Scrub from 0 to 1 along the toolpath", "core", PType.Number).Hint(0, 1, 1, 3),
            new ParamSpec("bedWidth", "Bw", "Bed plate width along Rhino X, mm. The lab plate is 700 across the machine's own X, which is Rhino Y", "core", PType.Number).Hint(100, 2000, 1000, 0),
            new ParamSpec("bedDepth", "Bd", "Bed plate depth along Rhino Y, mm", "core", PType.Number).Hint(100, 2000, 700, 0),
            new ParamSpec("bedCenter", "Bc", "Plate center in Rhino coordinates and mm, used for the bed preview and bounds checks", "core", PType.Point),
            new ParamSpec("showRobot", "R?", "Show the robot arm (shaded)", "core", PType.Boolean).HintBool(true),
            new ParamSpec("showExtrusion", "Xt", "Preview the extruded material instead of the center-line toolpath", "core", PType.Boolean),
            // extrusion group
            new ParamSpec("extWidth", "Ew", "Extrusion width, mm", "extrusion", PType.Number).Hint(0.5, 15, 10, 1),
            new ParamSpec("layerHeight", "Lh", "Layer height, mm (0 or empty = auto from the toolpath Z steps)", "extrusion", PType.Number).Hint(0, 6, 0, 2),
            // reach group
            new ParamSpec("reachMax", "Rx", "Maximum SCARA reach in mm (0 = L1 + L2)", "reach", PType.Number).Hint(0, 1200, 0, 0),
            new ParamSpec("reachMin", "Rn", "Minimum SCARA reach in mm (0 = the absolute difference between L1 and L2)", "reach", PType.Number).Hint(0, 500, 235, 0),
            new ParamSpec("zMax", "Zx", "Maximum machine Z in mm, corresponding to the firmware M208 limit", "reach", PType.Number).Hint(100, 2000, 400, 0),
            new ParamSpec("xMin", "Xn", "Minimum machine X in mm, corresponding to the firmware M208 limit", "reach", PType.Number).Hint(-2000, 2000, -300, 0),
            new ParamSpec("xMax", "Xx", "Maximum machine X in mm, corresponding to the firmware M208 limit", "reach", PType.Number).Hint(-2000, 12000, 10000, 0),
            new ParamSpec("yMin", "Yn", "Minimum machine Y in mm, corresponding to the firmware M208 limit", "reach", PType.Number).Hint(-12000, 2000, -10000, 0),
            new ParamSpec("yMax", "Yx", "Maximum machine Y in mm, corresponding to the firmware M208 limit", "reach", PType.Number).Hint(-2000, 2000, 0, 0),
            new ParamSpec("checkEnvelope", "cE", "Check targets against the machine's Cartesian axis limits when true", "reach", PType.Boolean).HintBool(true),
            // speed group
            new ParamSpec("maxFeed", "Mf", "Maximum feedrate, mm/s", "speed", PType.Number).Hint(10, 300, 150, 0),
            new ParamSpec("maxJointSpeed", "Mj", "Maximum joint speed, deg/s", "speed", PType.Number).Hint(10, 360, 120, 0),
            // singularity group
            new ParamSpec("jerkThresh", "Jt", "Warning threshold for joint angle change per toolpath distance, deg/mm", "singularity", PType.Number).Hint(0.5, 20, 5, 1),
            // joints group (defaults = firmware M669 A/B + robot.xml J2 travel)
            new ParamSpec("j1Min", "1n", "J1 minimum angle, deg", "joints", PType.Number).Hint(-360, 0, -41.1, 1),
            new ParamSpec("j1Max", "1x", "J1 maximum angle, deg", "joints", PType.Number).Hint(0, 360, 319.1, 1),
            new ParamSpec("j3Min", "3n", "J3 minimum angle, deg", "joints", PType.Number).Hint(-180, 0, -151.2, 1),
            new ParamSpec("j3Max", "3x", "J3 maximum angle, deg", "joints", PType.Number).Hint(0, 180, 151.2, 1),
            new ParamSpec("j2Min", "2n", "J2 carriage minimum, mm", "joints", PType.Number).Hint(0, 500, 0, 0),
            new ParamSpec("j2Max", "2x", "J2 carriage maximum, mm", "joints", PType.Number).Hint(0, 1500, 400, 0),
            new ParamSpec("checkJoints", "cJ", "Enforce joint ranges (off = explore only)", "joints", PType.Boolean).HintBool(true),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("reach", "Reach / build-volume limit inputs"),
            ("speed", "Feed / joint-speed limit inputs"),
            ("singularity", "Singularity threshold input"),
            ("joints", "Joint range limit inputs"),
            ("extrusion", "Extrusion preview (extrusion width / layer height) inputs"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("firmware", "Firmware: create value list", new[] { ("Duet-RRF", "0"), ("Marlin", "1") }),
            ("elbow", "Elbow: create value list", new[] { ("Up", "0"), ("Down", "1") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("J1", "J1", "First joint angle per move, deg", GH_ParamAccess.list);
            pManager.AddNumberParameter("J3", "J3", "Third joint angle per move, deg", GH_ParamAccess.list);
            pManager.AddNumberParameter("J2", "J2", "Z carriage height per move, mm", GH_ParamAccess.list);
            pManager.AddCurveParameter("arm", "A", "Arm geometry at the scrub position", GH_ParamAccess.item);
            pManager.AddCurveParameter("tcp", "T", "Tool center point toolpath", GH_ParamAccess.item);
            pManager.AddCurveParameter("bed", "Bd", "Bed plate outline", GH_ParamAccess.item);
            pManager.AddBooleanParameter("reachOK", "Ok", "True when every move is reachable", GH_ParamAccess.item);
            pManager.AddBooleanParameter("inBounds", "In", "True when the toolpath is on the bed and reachable", GH_ParamAccess.item);
            pManager.AddPointParameter("problems", "P", "Flagged move positions", GH_ParamAccess.list);
            pManager.AddTextParameter("messages", "M", "Problem messages", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
            pManager.AddBooleanParameter("danger", "D!", "True when any target violates the robot limits: unreachable, joint range or travel. Do not run the job", GH_ParamAccess.item);
        }

        // ---- preview state (set each solve) ----
        readonly BedPreview _bed = new();                 // arctic bed plate + printable area
        (double, double, double, double, double, double, double, double, double, double, double, bool)? _bedCacheKey;   // rebuild only on bed-input change (like UR)
        Mesh? _objBoxMesh;
        Mesh? _extrusionMesh;                             // extruded-material layers (arctic white)
        readonly ExtrusionPreview.Cache _extrusionCache = new ExtrusionPreview.Cache();
        readonly List<Mesh> _robotMeshes = new();
        readonly List<Line> _robotEdges = new();          // silhouette + crease edges for form
        readonly List<Curve> _wsWires = new();
        Curve? _tcpWire;
        Curve? _printedWire;   // toolpath up to the scrub (the "already printed" portion)
        BoundingBox _clip = BoundingBox.Empty;
        bool _showRobot = true;
        bool _showExt;
        // arctic palette
        static readonly Color RobotColor = Color.FromArgb(236, 232, 224);  // matte off-white (flat / unlit)
        static readonly Color RobotEdgeColor = Color.FromArgb(120, 118, 112);  // soft edge overlay
        const double FeatureAngleRad = 0.55;               // ~31 deg crease threshold for edges
        static readonly Color RingColor = Color.FromArgb(162, 167, 174);   // reach rings beyond the bed

        sealed class Result
        {
            public List<double> J1 = new(), J3 = new(), J2 = new();
            public Curve? Arm, Tcp, Bed;
            public bool ReachOk = true, InBounds = true;
            public List<Point3d> Problems = new();
            public List<string> Messages = new();
            public string Info = "";
            public bool Danger;
        }

        // ---- danger overlay (limit violations must be unmissable in the viewport) ----
        // accumulated ACROSS solve iterations: if any iteration violates, the banner stays
        readonly List<Point3d> _dangerPts = new();
        string? _dangerBanner;
        Point3d _dangerBannerPos;
        int _dangerErrTotal;
        int _dangerFirstIdx = -1;
        static readonly Color DangerColor = Color.FromArgb(220, 30, 30);

        /// <summary>Preview state is cleared ONCE PER SOLUTION here - clearing at the top
        /// of Compute wiped earlier iterations' overlays when an item input received a
        /// list (the danger banner from iteration 1 vanished if iteration 2 was clean).</summary>
        protected override void BeforeSolveInstance()
        {
            // NOTE: _bed deliberately NOT cleared - it is cached by _bedCacheKey and
            // rebuilding the 160x160 reach-shaded plate every scrub tick was a hotspot
            _objBoxMesh = null; _extrusionMesh = null;
            _robotMeshes.Clear(); _robotEdges.Clear(); _wsWires.Clear();
            _tcpWire = null; _printedWire = null; _clip = BoundingBox.Empty;
            _dangerPts.Clear(); _dangerBanner = null; _dangerErrTotal = 0; _dangerFirstIdx = -1;
            base.BeforeSolveInstance();
        }

        static Mesh? Cyl(Point3d a, Point3d b, double r)
        {
            var axis = b - a;
            double len = axis.Length;
            if (len < 1e-6) return null;
            axis.Unitize();
            var plane = new Plane(a, axis);
            var cyl = new Cylinder(new Circle(plane, r), len);
            return Mesh.CreateFromCylinder(cyl, 1, 16);
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            // Retired component: it samples curves and reports geometry in millimetres without
            // the document-unit adapter Robot Simulation uses. Say so rather than scale wrongly.
            if (!DocUnits.IsUnity(DocUnits.ScaleToMm()))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "This retired Robot component assumes a millimetre document. Use Robot Simulation for other document units");
            var lines = GetStringList(DA, "gcode");
            string gcode = lines.Count == 1 ? lines[0] : string.Join("\n", lines);
            var toolpath = GetCurveTree(DA, "toolpath");
            bool haveGcode = !string.IsNullOrWhiteSpace(gcode);
            bool haveCurves = toolpath != null && !toolpath.IsEmpty;

            double l1 = GetNumber(DA, "L1", 549.06);   // firmware M669 P
            double l2 = GetNumber(DA, "L2", 457.23);   // firmware M669 D
            double baseH = GetNumber(DA, "baseHeight", 0.0);
            int firmware = GetInt(DA, "firmware", 0);
            int elbow = GetInt(DA, "elbow", 1);
            bool arcSupport = GetBool(DA, "arcSupport", firmware == 0);
            double sim = Math.Max(0.0, Math.Min(1.0, GetNumber(DA, "sim", 1.0)));
            double bedW = GetNumber(DA, "bedWidth", 1000.0);
            double bedD = GetNumber(DA, "bedDepth", 700.0);
            // Default = the lab's fixed bed plate, expressed in the RHINO frame: the
            // plate the machine sees at X441 Y-468, 700 across machine-X by 1000 along
            // machine-Y, is X468 Y-441 and 1000 x 700 here. See Core MachineFrame.
            Point3d bedC = GetPoint(DA, "bedCenter", new Point3d(468, -441, 0));
            _showRobot = GetBool(DA, "showRobot", true);
            _showExt = GetBool(DA, "showExtrusion", false);
            double extW = GetNumber(DA, "extWidth", 10.0);
            double extHin = GetNumber(DA, "layerHeight", 0.0);   // <=0 => auto from layer Z-steps
            var flavor = firmware == 1 ? GcodeFlavor.Marlin : GcodeFlavor.DuetRrf;

            var lim = new RobotLimits
            {
                L1 = l1,
                L2 = l2,
                BaseH = baseH,
                ReachMax = GetNumber(DA, "reachMax", 0.0),
                ReachMin = GetNumber(DA, "reachMin", 235.0),   // firmware M669 R (dead-zone)
                ZMax = GetNumber(DA, "zMax", 400.0),   // firmware M208 Z maximum
                XMin = GetNumber(DA, "xMin", -300.0),
                XMax = GetNumber(DA, "xMax", 10000.0),
                YMin = GetNumber(DA, "yMin", -10000.0),
                YMax = GetNumber(DA, "yMax", 0.0),
                CheckEnvelope = GetBool(DA, "checkEnvelope", true),
                MaxFeed = GetNumber(DA, "maxFeed", 150.0),
                MaxJointSpeed = GetNumber(DA, "maxJointSpeed", 120.0),
                JerkThresh = GetNumber(DA, "jerkThresh", 5.0),
                J1Min = GetNumber(DA, "j1Min", -41.1),         // firmware M669 A
                J1Max = GetNumber(DA, "j1Max", 319.1),
                J3Min = GetNumber(DA, "j3Min", -151.2),        // firmware M669 B
                J3Max = GetNumber(DA, "j3Max", 151.2),
                J2Min = GetNumber(DA, "j2Min", 0.0),
                J2Max = GetNumber(DA, "j2Max", 400.0),         // firmware M208 Z travel
                CheckJointRanges = GetBool(DA, "checkJoints", true),
            };
            double reachMax = lim.ReachMax > 0 ? lim.ReachMax : l1 + l2;
            double reachMin = lim.ReachMin > 0 ? lim.ReachMin : Math.Abs(lim.L1 - lim.L2);
            if (lim.CheckJointRanges && lim.ZMax > lim.J2Max + 1e-6)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"zMax {lim.ZMax:0}mm exceeds the J2 carriage travel {lim.J2Max:0}mm from robot.xml. Z above {lim.J2Max:0} is flagged as a joint violation");

            // ---- kinematic self-check: posing chain vs analytic FK, once per session ----
            EnsureSelfCheck();

            // This component is purely informational: it simulates and flags problems
            // to inform the printing process, but it is independent of the machine /
            // calibration state and never gates a job. Only the Duet upload path
            // (DuetPrintJobComponent) enforces the calibrated post-homeall contract.
            var homePose = Lugworm.Robot.RobotModel.Default?.Def.CalibratedHomePose;
            lim.CalibratedHomePose = homePose;
            lim.HomedArmMode = Lugworm.Robot.RobotModel.Default?.Def.HomedArmMode ?? lim.HomedArmMode;
            lim.RequireCalibratedHomePose = false;
            if (!homePose.HasValue)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "robot.xml has no calibrated pose after homing. The start position is assumed and segment checks from home are skipped. The Duet upload gate still requires it");

            // ---- bed plate (arctic) + workspace rings, built BEFORE any parsing ----
            // The bed must render even when the input is missing, rejected, or the
            // toolpath sits off the plate: without the plate in the viewport it is
            // impossible to see where the print has to move. The bed follows the
            // bedCenter point; the printable area is the bed rectangle intersected
            // with the reach annulus and, when enabled, the Cartesian envelope.
            double bz = bedC.Z;
            var bedKey = (bedC.X, bedC.Y, bedC.Z, bedW, bedD, reachMin, reachMax,
                lim.XMin, lim.XMax, lim.YMin, lim.YMax, lim.CheckEnvelope);
            if (_bedCacheKey == null || !_bedCacheKey.Value.Equals(bedKey) || _bed.Frame == null)
            {
                _bed.Clear();
                // The plate is drawn in the Rhino frame; the reach annulus is centred
                // on the shared origin so it is unchanged by the frame map, but the
                // M208 envelope is a MACHINE box - map the sample point before testing it.
                _bed.Build(bedC, bedW, bedD, reachMin, reachMax,
                    (x, y) =>
                    {
                        double radius = Math.Sqrt(x * x + y * y);
                        MachineFrame.ToMachine(x, y, out double mx, out double my);
                        return radius >= reachMin - 1e-6 && radius <= reachMax + 1e-6
                            && (!lim.CheckEnvelope
                                || (mx >= lim.XMin - 1e-6 && mx <= lim.XMax + 1e-6
                                    && my >= lim.YMin - 1e-6 && my <= lim.YMax + 1e-6));
                    });
                _bedCacheKey = bedKey;
            }
            // null only when this frozen component's own width/depth sockets are fed
            // something non-positive, which the shared BedPreview now refuses to draw
            res.Bed = _bed.Frame?.ToPolylineCurve();
            if (_bed.Bounds.IsValid) _clip.Union(_bed.Bounds);
            var wsPlane = new Plane(new Point3d(0, 0, bz), Vector3d.ZAxis);
            _wsWires.Add(new Circle(wsPlane, reachMax).ToNurbsCurve());
            if (reachMin > 1e-6) _wsWires.Add(new Circle(wsPlane, reachMin).ToNurbsCurve());

            if (!haveGcode && !haveCurves) { res.Info = "no gcode or toolpath"; return res; }

            // ---- TCP points + checks from G-code (preferred) or moved curves ----
            // _tcpExtrude[k] = "the segment ending at point k is extruding material"
            var tcpPts = new List<Vec3>();
            var tcpExtrude = new List<bool>();
            List<int>? tcpLayerIds = null;
            CheckResult check;
            string src;
            // parse/sample + IK + checks are scrub-independent: cache them keyed on an
            // order-sensitive CONTENT hash, so scrubbing skips re-validation but any real
            // edit - a reordered curve list, a moved interior vertex - recomputes.
            // (The first version fingerprinted bbox sums: order-invariant and blind to
            // interior edits, which could serve a stale danger=false. Never again.)
            string limKey = LimitsKey(lim, elbow, firmware, arcSupport);
            if (haveGcode)
            {
                string cacheKey = "g|" + Fnv1a(gcode) + "|" + limKey;
                if (cacheKey == _ckKey && _ckPts != null && _ckCheck != null)
                {
                    tcpPts = _ckPts; tcpExtrude = _ckExt!; check = _ckCheck; src = _ckSrc!;
                }
                else
                {
                    Core.Robot.ParsedProgram prog;
                    try
                    {
                        prog = GcodeParser.Parse(gcode.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), lim.CalibratedHomePose);
                    }
                    catch (FormatException ex)
                    {
                        // fail-closed parser rejected a line: report it readably and
                        // keep the bed plate visible instead of dying with a solution
                        // exception (no preview at all)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "G-code rejected: " + ex.Message);
                        res.Info = "gcode rejected: " + ex.Message;
                        return res;
                    }
                    if (prog.Moves.Count == 0) { res.Info = $"no moves parsed ({lines.Count} input item(s))"; return res; }
                    check = RobotChecks.Run(prog, lim, flavor, arcSupport, elbow: elbow);
                    (tcpPts, tcpExtrude) = ToolpathSampler.SampleMoves(prog.Moves, lim.ArcChordTol);
                    src = $"gcode moves:{prog.Moves.Count}  arcs:{prog.HasArcs}  flavor:{flavor}";
                    _ckKey = cacheKey; _ckPts = tcpPts; _ckExt = tcpExtrude; _ckCheck = check; _ckSrc = src;
                }
            }

            else
            {
                // sampling is cheap relative to IK/checks - always resample, cache only checks
                var sampled = ToolpathSampler.SampleWithMetadata(toolpath!);
                // Curves come straight out of Rhino; every check below (reach, envelope,
                // joint ranges) is expressed in the machine's own frame, so convert once
                // here. G-code, in the branch above, is already machine coordinates.
                tcpPts = ToMachineFrame(sampled.pts);
                tcpExtrude = sampled.ext; tcpLayerIds = sampled.layerIds;
                if (tcpPts.Count == 0) { res.Info = "toolpath has no usable curves"; return res; }
                string cacheKey = "t|" + Fnv1a(tcpPts, tcpExtrude) + "|" + limKey;
                if (cacheKey == _ckKey && _ckCheck != null)
                {
                    check = _ckCheck; src = _ckSrc!;
                }
                else
                {
                    check = RobotChecks.RunPoints(tcpPts, lim, elbow);
                    src = $"toolpath pts:{tcpPts.Count}";
                    _ckKey = cacheKey; _ckPts = tcpPts; _ckExt = tcpExtrude; _ckCheck = check; _ckSrc = src;
                }
            }

            // The RRF pose must be the checked pose at the identical sampled
            // path index. An arc cannot use the per-move IK list.
            System.Diagnostics.Debug.Assert(check.Iks.Count == tcpPts.Count,
                "RRF check IK samples and TCP samples must be index-for-index");
            if (check.Iks.Count != tcpPts.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Validated pose and sample counts differ ({check.Iks.Count} IK samples, {tcpPts.Count} TCP samples)");
                res.Info = "validated pose/sample indexing mismatch";
                return res;
            }

            // Everything from here down is presentation: the viewport, the bed test and
            // the point outputs all live in the Rhino frame, while tcpPts stayed in the
            // machine frame for the checks above. Convert once.
            var tcpDraw = ToRhinoFrame(tcpPts);

            res.ReachOk = check.ReachOk;
            // Publish the PER-MOVE poses, not the sampled path. Iks now carries arc
            // interior samples (arcs are validated along their swept path) plus a
            // placeholder for an unvalidated start; emitting it would change the length
            // of these lists in every saved definition. MoveIks preserves the shipped
            // one-value-per-move contract.
            foreach (var ik in check.MoveIks) { res.J1.Add(ik.J1); res.J3.Add(ik.J3); res.J2.Add(ik.J2); }

            // ---- printable-area check at the bed position (bed built above) ----
            // Off-bed targets are flagged per point (warning; the reach checks above
            // cover the hard limits).
            // only EXTRUDING targets must sit on the plate - travel moves (including
            // the post-homeall start pose, point 0) legitimately leave the bed and
            // are covered by the reach/joint checks above
            int offBed = 0, firstOffBed = -1;
            for (int i = 0; i < tcpPts.Count; i++)
            {
                if (i >= tcpExtrude.Count || !tcpExtrude[i]) continue;
                if (!_bed.OnBed(tcpDraw[i].X, tcpDraw[i].Y))
                {
                    offBed++;
                    if (firstOffBed < 0) firstOffBed = i;
                }
            }
            bool insideBed = offBed == 0;
            res.InBounds = check.ReachOk && insideBed;
            if (offBed > 0)
            {
                res.Messages.Add($"[Warning] {offBed} extruding target(s) off the bed plate (first: move {firstOffBed}). Bed {bedW:0}×{bedD:0}mm at ({bedC.X:0}, {bedC.Y:0})");
                res.Problems.Add(GhConvert.ToPt(tcpDraw[firstOffBed]));
            }

            if (tcpDraw.Count >= 2) { res.Tcp = GhConvert.PtsToCurve(tcpDraw); _tcpWire = res.Tcp; _clip.Union(res.Tcp.GetBoundingBox(false)); }

            // ---- object translucent bbox (in the in/out colour) ----
            if (tcpPts.Count >= 1)
            {
                var bb = res.Tcp != null ? res.Tcp.GetBoundingBox(false) : BoundingBox.Empty;
                if (bb.IsValid && bb.Diagonal.Length > 1e-6)
                {
                    _objBoxMesh = Mesh.CreateFromBox(bb, 1, 1, 1);
                    _clip.Union(bb);
                }
            }

            // ---- arm at scrub ----
            int idx = (int)Math.Round(sim * (tcpPts.Count - 1));
            idx = Math.Max(0, Math.Min(tcpPts.Count - 1, idx));
            if (idx >= 1) _printedWire = GhConvert.PtsToCurve(
                ExtrusionPreview.DecimatePreview(tcpDraw.GetRange(0, idx + 1), 20000));

            // ---- extruded-material layers (grow with the scrub when the robot is shown) ----
            if (_showExt)
            {
                double extH = extHin > 1e-6 ? extHin
                    : ExtrusionPreview.AutoLayerHeight(tcpDraw, 3.5, tcpLayerIds);
                int maxIdx = _showRobot ? idx : tcpDraw.Count - 1;
                _extrusionMesh = ExtrusionPreview.BuildCached(_extrusionCache, _ckKey,
                    tcpDraw, tcpExtrude, maxIdx, extW, extH);
                if (_extrusionMesh != null) _clip.Union(_extrusionMesh.GetBoundingBox(false));
            }
            var mv = tcpDraw[idx];
            var armIk = check.Iks[idx];
            if (armIk.Valid)
            {
                // Elbow() answers in machine coordinates, like the IK it comes from.
                var (mex, mey) = ScaraKinematics.Elbow(armIk.J1Resolved, l1);
                MachineFrame.ToRhino(mex, mey, out double ex, out double ey);
                var basePt = new Point3d(0, 0, bz);
                var carriage = new Point3d(0, 0, mv.Z);
                var elbowPt = new Point3d(ex, ey, mv.Z);
                var tcpPt = new Point3d(mv.X, mv.Y, mv.Z);
                var arm = new Polyline { basePt, carriage, elbowPt, tcpPt };
                res.Arm = arm.ToPolylineCurve();
                _clip.Union(arm.BoundingBox);

                if (_showRobot)
                {
                    var model = Lugworm.Robot.RobotModel.Default;
                    if (model != null)
                    {
                        foreach (var (pm, _) in model.PosedMeshes(armIk.J1, armIk.J3, armIk.J2))
                        {
                            pm.VertexColors.CreateMonotoneMesh(RobotColor);   // flat, unlit -> z-fight is same colour
                            _robotMeshes.Add(pm); _clip.Union(pm.GetBoundingBox(false));
                        }
                        // cached on the base meshes, transformed per frame (scrub hotspot)
                        _robotEdges.AddRange(model.PosedFeatureEdges(armIk.J1, armIk.J3, armIk.J2, FeatureAngleRad));
                        if (model.MissingLinks.Count > 0)
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                                "Robot model: no geometry for " + string.Join(", ", model.MissingLinks));
                        foreach (var w in model.Warnings)
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Robot model: " + w);
                    }
                    else
                    {
                        // fallback schematic if the embedded assembly failed to load
                        AddCyl(_robotMeshes, basePt, carriage, 28.0);  // vertical column
                        AddCyl(_robotMeshes, carriage, elbowPt, 16.0); // link 1
                        AddCyl(_robotMeshes, elbowPt, tcpPt, 12.0);    // link 2
                    }
                }
            }
            else AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Scrub move {idx}: {armIk.Error}");

            // ---- problems ----
            int err = 0, warn = 0;
            int firstDangerIdx = -1;
            foreach (var p in check.Problems)
            {
                // problem positions come out of the machine-frame checks
                var at = GhConvert.ToPt(MachineFrame.ToRhino(p.At));
                res.Problems.Add(at);
                res.Messages.Add(p.ToString());
                if (p.Sev == Severity.Error || p.Kind == ProblemKind.MastClearance)
                {
                    err++;
                    _dangerPts.Add(at);
                    if (firstDangerIdx < 0) firstDangerIdx = p.Index;
                }
                else if (p.Sev == Severity.Warning) warn++;
            }

            // ---- DANGER: limit violations must be impossible to miss ----
            // banner accumulates across iterations (never cleared inside a solution)
            res.Danger = err > 0;
            if (res.Danger)
            {
                _dangerErrTotal += err;
                if (_dangerFirstIdx < 0) _dangerFirstIdx = firstDangerIdx;
                _dangerBanner = $"DANGER: {_dangerErrTotal} target(s) outside robot limits. Do not run" +
                                (_dangerFirstIdx >= 0 ? $"  (first: move {_dangerFirstIdx})" : "");
                double bannerZ = Math.Max(_clip.IsValid ? _clip.Max.Z : bz, bz) + 150.0;
                _dangerBannerPos = new Point3d(bedC.X, bedC.Y, bannerZ);
                _clip.Union(_dangerBannerPos);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    _dangerBanner + ". See the messages output for the reasons per move (joint ranges, reach, Z travel)");
            }

            res.Info = $"{src}  reachOK:{check.ReachOk}  inBounds:{res.InBounds}  " +
                       (res.Danger ? "DANGER  " : "") +
                       $"errors:{err}  warnings:{warn}  scrub@{idx}";
            return res;
        }

        static void AddCyl(List<Mesh> list, Point3d a, Point3d b, double r)
        {
            var m = Cyl(a, b, r);
            if (m != null) list.Add(m);
        }

        // ---- Rhino <-> machine frame (Core MachineFrame). The map is an involution,
        // so a round trip is exact and either helper undoes the other. ----
        static List<Vec3> ToMachineFrame(IReadOnlyList<Vec3> pts)
        {
            var mapped = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++) mapped.Add(MachineFrame.ToMachine(pts[i]));
            return mapped;
        }

        static List<Vec3> ToRhinoFrame(IReadOnlyList<Vec3> pts)
        {
            var mapped = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++) mapped.Add(MachineFrame.ToRhino(pts[i]));
            return mapped;
        }

        // ---- scrub cache: parse/IK/checks results, invalidated by any non-scrub input ----
        string _ckKey = "";
        List<Vec3>? _ckPts;
        List<bool>? _ckExt;
        CheckResult? _ckCheck;
        string? _ckSrc;

        /// <summary>Every non-geometry input that influences IK/checks - the scrub slider
        /// is deliberately absent so scrubbing hits the cache. Shared with the Duet
        /// upload gate (Core ContentHash) so both invalidate identically.</summary>
        static string LimitsKey(RobotLimits lim, int elbow, int firmware, bool arcSupport)
            => ContentHash.LimitsKey(lim, elbow, firmware, arcSupport);

        static ulong Fnv1a(string s) => ContentHash.Fnv1a(s);

        static ulong Fnv1a(List<Vec3> pts, List<bool> ext) => ContentHash.Fnv1a(pts, ext);

        // ---- FK self-check: runs once per Rhino session against the shipped robot.xml ----
        static volatile bool _fkChecked;
        static List<string>? _fkFailures;
        static readonly object FkSync = new object();

        void EnsureSelfCheck()
        {
            if (!_fkChecked)
            {
                lock (FkSync)
                {
                    if (!_fkChecked)
                    {
                        var model = Lugworm.Robot.RobotModel.Default;
                        if (model != null)
                        {
                            try { _fkFailures = RobotSelfCheck.Run(model.Def, tol: 1e-6); }
                            catch (Exception ex) { _fkFailures = new List<string> { "self-check threw: " + ex.Message }; }
                        }
                        _fkChecked = true;
                    }
                }
            }
            if (_fkFailures != null && _fkFailures.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Kinematic self check failed, the posed nozzle deviates from the analytic FK:\n" +
                    string.Join("\n", _fkFailures));
            if (Lugworm.Robot.RobotModel.Default == null && Lugworm.Robot.RobotModel.LoadError != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The embedded robot assembly failed to load (" + Lugworm.Robot.RobotModel.LoadError +
                    "). Showing the schematic fallback");
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataList(0, r.J1);
            DA.SetDataList(1, r.J3);
            DA.SetDataList(2, r.J2);
            DA.SetData(3, r.Arm);
            DA.SetData(4, r.Tcp);
            DA.SetData(5, r.Bed);
            DA.SetData(6, r.ReachOk);
            DA.SetData(7, r.InBounds);
            DA.SetDataList(8, r.Problems);
            DA.SetDataList(9, r.Messages);
            DA.SetData(10, r.Info);
            DA.SetData(11, r.Danger);
        }

        // ---- viewport preview ----
        public override BoundingBox ClippingBox => _clip.IsValid ? _clip : base.ClippingBox;

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            _bed.DrawMeshes(args.Display);
            if (_extrusionMesh != null)                       // extruded material layers (arctic white)
                ExtrusionPreview.Draw(args.Display, _extrusionMesh);
            // object bbox only when neither the robot nor the layers occupy the scene
            if (_objBoxMesh != null && !_showRobot && !_showExt)
                args.Display.DrawMeshShaded(_objBoxMesh, new DisplayMaterial(Color.RoyalBlue, 0.6));
            if (_showRobot)
                foreach (var rm in _robotMeshes)
                    args.Display.DrawMeshFalseColors(rm);   // flat + unlit: coincident faces share colour, no flicker
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            _bed.DrawWires(args.Display);
            foreach (var c in _wsWires) args.Display.DrawCurve(c, RingColor, 1);
            if (_showRobot) foreach (var ln in _robotEdges) args.Display.DrawLine(ln, RobotEdgeColor, 1);
            // centre-line toolpath, hidden when the extruded layers are shown.
            // robot shown -> only the already-printed path; robot hidden -> the full toolpath
            if (!_showExt)
            {
                if (_showRobot) { if (_printedWire != null) args.Display.DrawCurve(_printedWire, Color.RoyalBlue, 2); }
                else if (_tcpWire != null) args.Display.DrawCurve(_tcpWire, Color.RoyalBlue, 2);
            }

            // ---- danger overlay: red X at every violating target + camera-facing banner ----
            foreach (var dp in _dangerPts)
                args.Display.DrawPoint(dp, Rhino.Display.PointStyle.X, 7, DangerColor);
            if (_dangerBanner != null)
            {
                // banner plane faces the camera. The plane NORMAL (x × y) must point
                // TOWARD the camera - with it pointing away the text renders mirrored.
                var cam = args.Viewport.CameraLocation;
                var toCam = cam - _dangerBannerPos; toCam.Unitize();
                var xdir = Vector3d.CrossProduct(Vector3d.ZAxis, toCam);   // screen-right
                if (!xdir.Unitize()) xdir = Vector3d.XAxis;
                var ydir = Vector3d.CrossProduct(toCam, xdir); ydir.Unitize();
                var plane = new Plane(_dangerBannerPos, xdir, ydir);
                args.Display.Draw3dText(_dangerBanner, DangerColor, plane, 40.0, "Arial",
                                        true, false,
                                        Rhino.DocObjects.TextHorizontalAlignment.Center,
                                        Rhino.DocObjects.TextVerticalAlignment.Middle);
            }
        }
    }
}
