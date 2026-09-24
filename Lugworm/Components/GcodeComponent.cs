using System;
using System.Collections.Generic;
using System.IO;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Lugworm.Convert;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Any curve/polyline tree -> Marlin/Duet G-code (material extrusion).
    /// Standalone converter - Slicer/Mover are optional upstream. Heavy (compute
    /// button). Port of ClayGcode2.py wrapping Core.Gcode.GcodeBuilder.</summary>
    public class GcodeComponent : IamComponentBase
    {
        public GcodeComponent()
            : base("G-code Generator", "G-code",
                   "Press Compute with toolpath curves supplied to generate extrusion G-code for the selected machine.",
                   "Lugworm", "3 G-code")
        { }

        public override Guid ComponentGuid => new Guid("c1a2c3d4-0003-4a3b-9c3d-aa0000000003");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.Gcode();
        protected override bool Heavy => true;
        protected override int InputContractVersion => 1;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("crvs", "C", "Toolpath curves in Rhino document units and print order, with one tree branch per layer", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("layerHeight", "LH", "Layer height in mm used with layer width to calculate extrusion", "core", PType.Number).Hint(0.2, 6, 3, 2),
            new ParamSpec("width", "W", "Layer width in mm used to calculate extrusion", "core", PType.Number).Hint(0.5, 15, 10, 1),
            new ParamSpec("ramDiameter", "RD", "Ram cartridge bore diameter in mm used to calculate ram travel (IAMLab ram: 94)", "core", PType.Number).Hint(5, 150, 94, 1),
            new ParamSpec("pressureCoeff", "PC", "Multiplier for calculated ram extrusion (1 = unchanged), calibrated for the material", "core", PType.Number).Hint(0.5, 2, 1.2, 2),
            new ParamSpec("printSpeed", "PS", "Nozzle speed while extruding, mm/s", "core", PType.Number).Hint(5, 100, 43, 0),
            new ParamSpec("travelSpeed", "TS", "Nozzle speed during travel without extrusion, mm/s", "core", PType.Number).Hint(10, 200, 30, 0),
            new ParamSpec("retractSpeed", "RS", "Ram speed during retraction and priming, mm/s of ram travel", "core", PType.Number).Hint(0.1, 10, 1, 1),
            new ParamSpec("firstLayerSpeedFactor", "FL", "Multiplier for first-layer print speed (0.5 = half speed)", "core", PType.Number).Hint(0.1, 1, 0.5, 2),
            new ParamSpec("retraction", "Rm", "Retraction mode: 0 = none, 1 = distant seams only, 2 = every seam, with clearance hops on distant travels", "core", PType.Integer),
            new ParamSpec("seamTol", "St", "Light retraction: largest seam XY distance connected without retraction, mm (0 = 2 x width)", "core", PType.Number).Hint(0, 50, 0, 1),
            new ParamSpec("alternateInfill", "AI", "Alternate layers with open infill: reverse infill and begin from the previous layer's infill endpoint", "core", PType.Boolean).HintBool(false),
            new ParamSpec("firmware", "FW", "Firmware format: 0 = Duet RRF, 1 = Marlin, 2 = Klipper (unwired = robot profile default, or Duet for Potterbot)", "core", PType.Integer),
            new ParamSpec("relativeE", "rE", "Use relative ram travel (M83) when true, or absolute ram positions (M82) when false", "core", PType.Boolean).HintBool(true),
            new ParamSpec("pressureOffset", "PO", "Ram travel pulled back for each retraction, mm (0 = none)", "core", PType.Number).Hint(0, 2, 0.2, 2),
            new ParamSpec("primeExtra", "PE", "Extra ram travel added when priming after a retraction, mm", "core", PType.Number).Hint(0, 10, 0.2, 2),
            new ParamSpec("coast", "Co", "Stop extruding this far before a path end, mm", "core", PType.Number).Hint(0, 20, 2, 1),
            new ParamSpec("zHop", "ZH", "Extra travel clearance above printed material, mm. Travels lift to the highest printed Z along their corridor plus this margin (0 = one layer height)", "core", PType.Number).Hint(0, 10, 5, 1),
            new ParamSpec("hopSpeed", "HS", "Z hop lift and descend feedrate, mm/s (0 = travel speed)", "core", PType.Number).Hint(0, 100, 10, 0),
            new ParamSpec("footerZLift", "FZ", "Relative Z lift before the homing at the end of the print, mm (0 = none)", "core", PType.Number).Hint(0, 100, 20, 0),
            new ParamSpec("dwell", "Dw", "Pause after prime, ms", "core", PType.Number).Hint(0, 5000, 0, 0),
            new ParamSpec("leadIn", "LI", "Distance over which the starting flow boost fades, mm (0 = off)", "core", PType.Number).Hint(0, 50, 0, 0),
            new ParamSpec("leadInBoost", "LB", "Starting flow multiplier, at least 1, fading over leadIn", "core", PType.Number).Hint(1, 3, 1, 2),
            new ParamSpec("minLayerTime", "MLT", "Minimum layer duration in s, enforced by slowing print moves (0 = off)", "core", PType.Number).Hint(0, 120, 0, 0),
            new ParamSpec("zOffset", "ZO", "Nozzle-to-bed correction added to all printed Z positions in mm (normally 0), also shown in the preview", "core", PType.Number).Hint(-5, 10, 0, 2),
            new ParamSpec("tol", "T", "Maximum curve-to-polyline deviation in mm", "core", PType.Number).Hint(0.01, 1, 0.05, 2),
            new ParamSpec("savePath", "Sv", "Optional local file or folder to save on Compute, overwriting the target (folder uses print.gcode)", "core", PType.Text),
            // pressure-advance group
            new ParamSpec("advanceK", "K", "Calibrated advance value for the selected firmware (Duet and Klipper: seconds, Marlin: K)", "advance", PType.Number).Hint(0, 2, 0.1, 2),
            new ParamSpec("accelPrint", "AP", "Print acceleration, mm/s^2 (M204 P)", "advance", PType.Number).Hint(50, 5000, 500, 0),
            new ParamSpec("accelTravel", "AT", "Travel acceleration, mm/s^2 (M204 T)", "advance", PType.Number).Hint(50, 5000, 500, 0),
            // per-axis acceleration / instantaneous-speed-change limits
            new ParamSpec("accelXY", "aXY", "Maximum XY acceleration in mm/s^2 (Klipper uses it as the overall limit when accelPrint is unset)", "retractaccel", PType.Number).Hint(100, 4000, 1500, 0),
            new ParamSpec("accelZ", "aZ", "Maximum Z acceleration in mm/s^2 (not emitted on Klipper, which uses printer.cfg)", "retractaccel", PType.Number).Hint(100, 3000, 1000, 0),
            new ParamSpec("jerkXY", "jXY", "Maximum XY instant speed change in mm/s, converted to mm/min for Duet M566", "retractaccel", PType.Number).Hint(60, 2400, 600, 0),
            new ParamSpec("jerkZ", "jZ", "Maximum Z instant speed change in mm/s (used with jerkXY, not emitted on Klipper)", "retractaccel", PType.Number).Hint(60, 2400, 600, 0),
            new ParamSpec("accelE", "aE", "Maximum ram acceleration in mm/s^2 for retraction and priming (not emitted on Klipper)", "retractaccel", PType.Number).Hint(50, 3000, 500, 0),
            new ParamSpec("jerkE", "jE", "Maximum ram instant speed change in mm/s, converted for Duet M566 (not emitted on Klipper)", "retractaccel", PType.Number).Hint(20, 1200, 300, 0),
            new ParamSpec("retractAccelFactor", "rAf", "Scale below 1 applied to acceleration and jerk during retraction and travel moves to tame current spikes", "retractaccel", PType.Number).Hint(0.1, 1, 0.5, 2),
            // arcs group
            new ParamSpec("arcTol", "AcT", "Maximum deviation from the toolpath when fitting arcs, mm", "arcs", PType.Number).Hint(0.01, 1, 0.05, 2),
            // arcwelder group (depends on arcs)
            new ParamSpec("arcPathTolPct", "APct", "Maximum ArcWelder path-length change in percent", "arcwelder", PType.Number).Hint(1, 20, 5, 0),
            new ParamSpec("arcMaxRadius", "AmR", "Largest arc radius ArcWelder may fit, mm", "arcwelder", PType.Number).Hint(10, 10000, 1000, 0),
            new ParamSpec("arcWelderExe", "Exe", "ArcWelder executable path required with ArcWelder enabled (also enable Arcs), with unwelded output on failure", "arcwelder", PType.Text),
            // skirt group
            new ParamSpec("skirtOffset", "SkO", "Distance from the part to the priming skirt in mm, used with Skirt enabled", "skirt", PType.Number).Hint(0, 20, 3, 1),
            // floors group
            new ParamSpec("floorLayers", "FlL", "Initial layer count before the optional floor pause (0 = none), without adding new floor geometry", "floors", PType.Integer).Hint(0, 10, 0),
            new ParamSpec("pauseAfterFloor", "PaF", "Lift and pause for pauseTime after floorLayers when true", "floors", PType.Boolean),
            new ParamSpec("pauseTime", "PaT", "Timed floor pause in ms (0 = no dwell), used when pauseAfterFloor is true", "floors", PType.Integer).Hint(0, 60000, 0),
            // header/footer override
            new ParamSpec("header", "Hd", "G-code text replacing the entire generated header (empty = automatic header)", "headeroverride", PType.Text),
            new ParamSpec("footer", "Ft", "G-code text replacing the entire generated footer (empty = automatic footer)", "headeroverride", PType.Text),
            // pump extrusion mode (any speed-controlled screw pump)
            new ParamSpec("nozzleDiameter", "Nd", "Nozzle bore in mm for pump flow checks (0 = no diameter check)", "pump", PType.Number).Hint(0, 30, 0, 1),
            new ParamSpec("pumpCalibCsv", "Cp", "Measured pump calibration CSV path with setting and flow_lpm columns, required for a usable pump recommendation", "pump", PType.Text),
            new ParamSpec("slipFactor", "Sf", "Multiplier for required pump flow (1 = no slip correction), calibrated for the material", "pump", PType.Number).Hint(0.5, 1.5, 1, 2),
            // variable layer height (adaptive slices)
            new ParamSpec("varhMin", "Vn", "Local layer height floor as a fraction of nominal, default 0.25", "varh", PType.Number).Hint(0.05, 1, 0.25, 2),
            new ParamSpec("varhMax", "Vx", "Local layer height ceiling as a fraction of nominal, default 2.5", "varh", PType.Number).Hint(1, 5, 2.5, 1),
            // slope-compensated flow (conical / non-planar, vertical nozzle)
            new ParamSpec("eCompRefMul", "Ec", "Measured flow multiplier at eCompRefDeg, required for slope compensation (1 = no change)", "ecomp", PType.Number).Hint(0.5, 2, 1, 2),
            new ParamSpec("eCompRefDeg", "Ed", "Reference stacking angle from vertical in deg for eCompRefMul (use coneAngle for conical slices)", "ecomp", PType.Number).Hint(5, 80, 45, 0),
            new ParamSpec("robot", "R", "Robot Definition text selecting the coordinate frame and default firmware (unwired = Potterbot SCARA)", "core", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("advance", "Pressure / linear advance inputs"),
            ("retractaccel", "Per-axis accel/jerk (M201/M566) limits"),
            ("arcs", "Arcs (G2/G3) inputs"),
            ("arcwelder", "ArcWelder post-process inputs"),
            ("skirt", "Skirt inputs"),
            ("floors", "Floors / pause inputs"),
            ("headeroverride", "Header/footer override inputs"),
            ("pump", "Pump extrusion mode inputs"),
            ("varh", "Variable layer-height flow (adaptive / non-planar slices)"),
            ("ecomp", "Slope-compensated flow (conical/non-planar, calibrated)"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("firmware", "Firmware: create value list",
             new[] { ("Duet-RRF", "0"), ("Marlin", "1"), ("Klipper", "2") }),
            ("retraction", "Retraction mode dropdown",
             new[] { ("No retraction (connect all)", "0"), ("Light retraction (only far jumps)", "1"), ("Normal retraction (every seam)", "2") }),
        };

        // arcwelder depends on arcs
        protected override void OnGroupsChanged()
        {
            if (!Active.Contains("arcs")) Active.Remove("arcwelder");
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            // BREAKING (1.11.0): the "gcode_text" output was removed at the user's
            // request. Emitting a whole job as one GH_String hung the canvas, and the
            // socket had no remaining use. The ComponentGuid is unchanged, so Grasshopper
            // reconnects saved definitions BY INDEX and every wire after "gcode" shifts up
            // one - silently, with no error. Accepted deliberately: the plugin is
            // unpublished, so the only affected files are in this repo.
            pManager.AddTextParameter("gcode", "G", "Generated G-code lines in machine coordinates and mm", GH_ParamAccess.list);
            pManager.AddCurveParameter("preview", "P", "Extruding-move curves in Rhino document units", GH_ParamAccess.tree);
            pManager.AddCurveParameter("travels", "Tv", "Travel-move curves in Rhino document units", GH_ParamAccess.tree);
            pManager.AddNumberParameter("length", "L", "Total nozzle path length while extruding, mm", GH_ParamAccess.item);
            pManager.AddNumberParameter("volume", "V", "Extruded volume, cm3", GH_ParamAccess.item);
            pManager.AddNumberParameter("time", "Ti", "Estimated job duration in min", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Generation settings, save status and warnings", GH_ParamAccess.item);
            pManager.AddTextParameter("pump", "Pu", "Pump mode summary from the pump toggle group: required flow in l/min, recommended setting and warnings", GH_ParamAccess.list);
            pManager.AddPointParameter("start", "St", "First print point in Rhino document units, including zOffset", GH_ParamAccess.item);
            pManager.AddTextParameter("startGcode", "Sg", "Machine-coordinate commands to approach the start at Z0, for manual review of homing and clearance before sending", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public List<string> Lines = new();
            public readonly LazyText TextModel = new LazyText();
            public string Text { get => TextModel.Get(); set => TextModel.Set(value); }
            public GH_Structure<GH_Curve> Preview = new();
            public GH_Structure<GH_Curve> Travels = new();
            public double Length, Volume, Time;
            public string Info = "";
            public List<string> Pump = new();
            public Rhino.Geometry.Point3d? Start;
            public string StartGcode = "";
        }

        sealed class LazyText
        {
            List<string>? _lines;
            string? _text;
            public void SetLines(List<string> lines) { _lines = lines; _text = null; }
            public void Set(string text) { _text = text ?? ""; _lines = null; }
            public string Get()
            {
                if (_text != null) return _text;
                _text = _lines == null ? "" : string.Join("\n", _lines);
                return _text;
            }
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            double tol = GetNumber(DA, "tol", 0.05);
            var tree = GetCurveTree(DA, "crvs");
            var res = new Result();
            if (tree == null || tree.IsEmpty) { res.Info = "no curves"; return res; }
            // document-unit adapter (AUD-COR-001): curves convert to mm on entry,
            // preview geometry converts back on exit; G-code itself is always mm
            double unitScale = DocUnits.ScaleToMm();

            // ---- input tree -> engine branches ----
            var branches = new List<IReadOnlyList<IReadOnlyList<Vec3>>>();
            var objectIds = new List<int>();
            for (int bi = 0; bi < tree.Branches.Count; bi++)
            {
                var branch = tree.Branches[bi];
                var contours = new List<IReadOnlyList<Vec3>>();
                foreach (var goo in branch)
                {
                    if (goo?.Value == null) continue;
                    var pts = GhConvert.CurveToPts(goo.Value, tol, unitScale);
                    if (pts.Count >= 2) contours.Add(pts);
                }
                if (contours.Count > 0)
                {
                    branches.Add(contours);
                    var path = tree.Paths[bi];
                    objectIds.Add(path.Indices.Length > 1 ? path.Indices[0] : 0);
                }
            }
            if (branches.Count == 0) { res.Info = "no usable curves"; return res; }

            RobotProfile robot;
            try
            {
                string robotText = GetString(DA, "robot", "");
                // Backwards compatibility: the pre-definition generator always
                // emitted the calibrated Potterbot frame.
                robot = string.IsNullOrWhiteSpace(robotText)
                    ? RobotCatalog.Get("potterbot-scara-v4")
                    : RobotCatalog.Resolve(robotText);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return null;
            }

            double ramDia = GetNumber(DA, "ramDiameter", 94.0);
            if (ramDia < 5.0 || ramDia > 200.0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"ramDiameter {ramDia:0.##} mm is outside the plausible range of 5 to 200 mm. " +
                    "Check the cartridge bore before printing, as it determines the amount extruded");

            // dialect: the wired input wins, else the profile's default (an unwired
            // robot resolves to the Potterbot, whose default is Duet RRF - unchanged)
            int firmwareIndex = IsWired("firmware")
                ? GetInt(DA, "firmware", 0)
                : robot.DefaultFlavor == GcodeFlavor.Marlin ? 1 : robot.DefaultFlavor == GcodeFlavor.Klipper ? 2 : 0;
            var flavor = firmwareIndex == 1 ? GcodeFlavor.Marlin : firmwareIndex == 2 ? GcodeFlavor.Klipper : GcodeFlavor.DuetRrf;

            // Dialect versus definition: `firmware` and `robot` are independent
            // inputs, so a wired dialect can select G-code the wired (or implicit)
            // robot's transport cannot execute, or one its own default does not expect.
            if (flavor == GcodeFlavor.Klipper && robot.ControlKind == RobotControlKind.Duet)
            {
                if (IsWired("robot"))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Klipper dialect selected for {robot.DisplayName}, a Duet RRF machine. " +
                        "SET_VELOCITY_LIMIT and SET_PRESSURE_ADVANCE are not RRF commands");
                else
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"Klipper dialect selected for {robot.DisplayName}, a Duet RRF machine. " +
                        "SET_VELOCITY_LIMIT and SET_PRESSURE_ADVANCE are not RRF commands. Wire a Klipper Robot Definition");
            }
            else if (IsWired("firmware") && flavor != robot.DefaultFlavor)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Firmware {flavor} differs from the default {robot.DefaultFlavor} of {robot.DisplayName}");

            // ---- config ----
            var cfg = new GcodeConfig
            {
                LayerHeight = GetNumber(DA, "layerHeight", 3.0),
                Width = GetNumber(DA, "width", 10.0),
                RamDiameter = ramDia,
                PressureCoeff = GetNumber(DA, "pressureCoeff", 1.2),
                PrintSpeed = GetNumber(DA, "printSpeed", 43.0),
                TravelSpeed = GetNumber(DA, "travelSpeed", 30.0),
                RetractSpeed = GetNumber(DA, "retractSpeed", 1.0),
                FirstLayerSpeedFactor = GetNumber(DA, "firstLayerSpeedFactor", 0.5),
                PressureOffset = GetNumber(DA, "pressureOffset", 0.2),
                PrimeExtra = GetNumber(DA, "primeExtra", 0.2),
                Coast = GetNumber(DA, "coast", 2.0),
                ZHop = GetNumber(DA, "zHop", 5.0),
                HopSpeed = GetNumber(DA, "hopSpeed", 10.0),
                FooterZLift = GetNumber(DA, "footerZLift", 20.0),
                Dwell = GetNumber(DA, "dwell", 0.0),
                LeadIn = GetNumber(DA, "leadIn", 0.0),
                LeadInBoost = GetNumber(DA, "leadInBoost", 1.0),
                MinLayerTime = GetNumber(DA, "minLayerTime", 0.0),
                ZOffset = GetNumber(DA, "zOffset", 0.0),   // matches GcodeConfig; the slicer owns layer height
                RelativeE = GetBool(DA, "relativeE", true),
                Flavor = flavor,
                CoordinateFrame = robot.GcodeCoordinateFrame,
                Retraction = GetInt(DA, "retraction", 1) switch
                {
                    0 => RetractionMode.NoRetraction,
                    2 => RetractionMode.NormalRetraction,
                    _ => RetractionMode.LightRetraction,
                },
                SeamTol = GetNumber(DA, "seamTol", 0.0),
                AlternateInfill = GetBool(DA, "alternateInfill", false),
            };

            if (Active.Contains("advance"))
            {
                double k = GetNumber(DA, "advanceK", double.NaN);
                double ap = GetNumber(DA, "accelPrint", double.NaN);
                double at = GetNumber(DA, "accelTravel", double.NaN);
                if (!double.IsNaN(k)) cfg.LinearAdvanceK = k;
                if (!double.IsNaN(ap)) cfg.AccelPrint = ap;
                if (!double.IsNaN(at)) cfg.AccelTravel = at;
            }
            if (Active.Contains("retractaccel"))
            {
                double axy = GetNumber(DA, "accelXY", double.NaN);
                double az = GetNumber(DA, "accelZ", double.NaN);
                double jxy = GetNumber(DA, "jerkXY", double.NaN);
                double jz = GetNumber(DA, "jerkZ", double.NaN);
                double ae = GetNumber(DA, "accelE", double.NaN);
                double je = GetNumber(DA, "jerkE", double.NaN);
                if (!double.IsNaN(axy)) cfg.AccelXY = axy;
                if (!double.IsNaN(az)) cfg.AccelZ = az;
                if (!double.IsNaN(jxy)) cfg.JerkXY = jxy;
                if (!double.IsNaN(jz)) cfg.JerkZ = jz;
                if (!double.IsNaN(ae)) cfg.AccelE = ae;
                if (!double.IsNaN(je)) cfg.JerkE = je;
                cfg.RetractAccelFactor = GetNumber(DA, "retractAccelFactor", 0.5);
            }
            if (Active.Contains("arcs"))
            {
                // built-in fitter emits G2/G3 in-engine; ArcWelder welds a G1 file instead
                cfg.UseArcs = !Active.Contains("arcwelder");
                cfg.ArcTol = GetNumber(DA, "arcTol", 0.05);
            }
            if (Active.Contains("skirt"))
            {
                cfg.Skirt = true;
                cfg.SkirtOffset = GetNumber(DA, "skirtOffset", 3.0);
            }
            if (Active.Contains("floors"))
            {
                cfg.FloorLayers = GetInt(DA, "floorLayers", 0);
                cfg.PauseAfterFloor = GetBool(DA, "pauseAfterFloor", false);
                cfg.PauseTime = GetInt(DA, "pauseTime", 0);
            }

            try { cfg.Validate(); }
            catch (ArgumentException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid G-code configuration: " + ex.Message);
                res.Info = "invalid G-code configuration: " + ex.Message;
                return res;
            }

            // ---- variable layer height (adaptive slices only - toggle) ----
            // measures the LOCAL layer height (distance to the layer below) per point and
            // substitutes it for the h in the E-kinematics (E = L×h×w/ramArea×k, the
            // GCODE_Essential_Overview model). The flow-multiplier channel stays reserved
            // for material calibration. Planar constant-height prints should leave this
            // off (output is then byte-identical).
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? heightTree = null;
            string varhInfo = "";
            if (Active.Contains("varh"))
            {
                double vmn = GetNumber(DA, "varhMin", 0.25);
                double vmx = GetNumber(DA, "varhMax", 2.5);
                List<List<List<double>>> ht;
                try { ht = Core.Extrusion.VariableFlow.LocalHeights(branches, cfg.LayerHeight, vmn, vmx, objectIds); }
                catch (ArgumentException ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid variable height input: " + ex.Message);
                    res.Info = "invalid variable-height input: " + ex.Message;
                    return res;
                }
                heightTree = ht;
                double lo = double.MaxValue, hi = double.MinValue; long n = 0;
                foreach (var b in ht) foreach (var ct in b) foreach (var h in ct)
                { lo = Math.Min(lo, h); hi = Math.Max(hi, h); n++; }
                if (n > 0) varhInfo = $"  localH:[{lo:0.00}..{hi:0.00}]mm";
                if (cfg.UseArcs)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Variable layer height: arc moves (G2/G3) use the mean height per contour. " +
                        "Disable the arcs group for full per segment heights on non-planar paths");
            }

            // slope-compensated FLOW multiplier (material channel, decision D025 of the
            // archived decision log) - independent
            // of varh's height substitution; both can be active on a conical slice.
            IReadOnlyList<IReadOnlyList<IReadOnlyList<double>>>? flowTree = null;
            if (Active.Contains("ecomp"))
            {
                double kRef = GetNumber(DA, "eCompRefMul", 1.0);
                double refDeg = GetNumber(DA, "eCompRefDeg", 45.0);
                if (Math.Abs(kRef - 1.0) < 1e-9)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Slope compensation is enabled but eCompRefMul is 1.0, so it has no effect. " +
                        "Measure the multiplier with an inclined wall calibration print");
                else
                {
                    try { flowTree = Core.Extrusion.SlopeFlow.Multipliers(branches, kRef, refDeg, objectIds); }
                    catch (ArgumentException ex)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid slope compensation input: " + ex.Message);
                        res.Info = "invalid slope-compensation input: " + ex.Message;
                        return res;
                    }
                }
                if (flowTree != null && cfg.UseArcs)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Slope compensation: arc moves (G2/G3) use the mean multiplier per contour. " +
                        "Disable the arcs group for full per segment compensation");
            }

            // ---- build ----
            var built = GcodeBuilder.Build(branches, cfg, flowTree, heightTree, objectIds);

            string header = Header.Make(cfg);
            string footer = Header.Footer(cfg);
            if (Active.Contains("headeroverride"))
            {
                int hi = Params.IndexOfInputParam("header");
                int fi = Params.IndexOfInputParam("footer");
                string h = ""; string f = "";
                if (hi >= 0 && DA.GetData(hi, ref h) && !string.IsNullOrEmpty(h)) header = h.EndsWith("\n", StringComparison.Ordinal) ? h : h + "\n";
                if (fi >= 0 && DA.GetData(fi, ref f) && !string.IsNullOrEmpty(f)) footer = f.EndsWith("\n", StringComparison.Ordinal) ? f : f + "\n";
            }

            var all = new List<string>();
            all.AddRange(header.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
            if (built.StartPoint.HasValue)
            {
                // StartPoint is a Rhino-frame point (that is what the output and the
                // preview are); the comment and the paste-able jog line are G-code, so
                // they carry the machine's own coordinates. See Core MachineFrame.
                var s = built.StartPoint.Value;
                var m = MachineFrame.ToMachine(s);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                all.Add($"; PRINT_START X{m.X.ToString("0.000", inv)} Y{m.Y.ToString("0.000", inv)} Z{m.Z.ToString("0.000", inv)}");
                res.Start = DocUnits.FromMm(GhConvert.ToPt(s), unitScale);

                // paste-able one-liner: jog to the print-start XY at Z0 (surface datum)
                // so the nozzle can be set to just touch before starting the print
                int travelF = Math.Max(1, (int)Math.Round(cfg.TravelSpeed * 60));
                res.StartGcode =
                    $"G1 X{m.X.ToString("0.000", inv)} Y{m.Y.ToString("0.000", inv)} Z0 F{travelF.ToString(inv)}";
            }
            all.AddRange(built.Lines);
            all.AddRange(footer.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
            res.Lines = all;
            res.TextModel.SetLines(all);

            for (int i = 0; i < built.PrintPolys.Count; i++)
                if (built.PrintPolys[i].Count >= 2)
                    res.Preview.Append(new GH_Curve(GhConvert.PtsToCurve(DecimatePreview(built.PrintPolys[i], 5000), unitScale)), new GH_Path(i));
            for (int i = 0; i < built.TravelPolys.Count; i++)
                if (built.TravelPolys[i].Count >= 2)
                    res.Travels.Append(new GH_Curve(GhConvert.PtsToCurve(DecimatePreview(built.TravelPolys[i], 2000), unitScale)), new GH_Path(i));

            res.Length = built.Length;
            res.Volume = built.Volume;
            res.Time = built.TimeMin;
            res.Info = $"branches:{branches.Count}  retract:{built.Retract}  arcs:{built.Arcs}  " +
                       $"len:{built.Length:0.0}mm  vol:{built.Volume:0.00}cm3  time:{built.TimeMin:0.0}min  frame:{cfg.CoordinateFrame} - flavor:{cfg.Flavor}" +
                       "  model:ram" + varhInfo;
            Message = cfg.Flavor.ToString();
            if (built.Warnings.Count > 0)
                res.Info += "\n" + string.Join("\n", built.Warnings);

            // ---- optional ArcWelder whole-file post-process ----
            if (Active.Contains("arcwelder"))
            {
                string exe = "";
                int ei = Params.IndexOfInputParam("arcWelderExe");
                if (ei >= 0) DA.GetData(ei, ref exe);
                var (welded, winfo, ran) = Core.Arcs.ArcWelderCli.Weld(
                    res.Text, exe,
                    GetNumber(DA, "arcTol", 0.05),
                    GetNumber(DA, "arcPathTolPct", 5.0),
                    GetNumber(DA, "arcMaxRadius", 1000.0),   // the slider hint default
                    !cfg.RelativeE);
                res.Text = welded;
                res.Lines = new List<string>(welded.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                res.Info += "  " + winfo;
                if (!ran) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, winfo);
            }

            // ---- pump extrusion mode: required Q + recommended setting ----
            if (Active.Contains("pump"))
            {
                var pumpInput = new Core.Extrusion.PumpPlanInput
                {
                    NozzleDiameter = GetNumber(DA, "nozzleDiameter", 0.0),
                    LayerHeight = cfg.LayerHeight,        // mm (layer height)
                    LayerWidth = cfg.Width,                // mm
                    PrintSpeed = cfg.PrintSpeed,          // mm/s
                    FirstLayerSpeedFactor = cfg.FirstLayerSpeedFactor,
                    SlipFactor = GetNumber(DA, "slipFactor", 1.0),
                };
                Core.Extrusion.PumpCalibration? cal = null;
                string calSrc;
                string calPath = GetString(DA, "pumpCalibCsv", "");
                bool calPending = false;
                try
                {
                    if (!string.IsNullOrWhiteSpace(calPath))
                    {
                        // background read (AUD-PERF-001), keyed by path + file identity
                        var fi = new FileInfo(calPath);
                        if (!fi.Exists) throw new FileNotFoundException("file not found", calPath);
                        string calKey = FormattableString.Invariant(
                            $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                        cal = _calReader.ResultFor(calKey);
                        if (cal == null)
                        {
                            if (_calReader.ErrorFor(calKey) is string calError)
                            {
                                _calReader.ForgetError();
                                throw new IOException(calError);
                            }
                            string cp = calPath;
                            _calReader.Start(this, calKey,
                                ct => Core.Extrusion.PumpCalibration.ParseCsv(File.ReadAllLines(cp)));
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                                "Loading pump calibration " + calPath + " …");
                            calPending = true;
                        }
                        calSrc = calPath;
                    }
                    else
                    {
                        using var cs = typeof(GcodeComponent).Assembly.GetManifestResourceStream(
                            "Lugworm.Resources.pump_calibration_placeholder.csv");
                        if (cs != null)
                        {
                            using var sr = new StreamReader(cs);
                            var calLines = new List<string>();
                            while (sr.ReadLine() is string ln) calLines.Add(ln);
                            cal = Core.Extrusion.PumpCalibration.ParseCsv(calLines);
                        }
                        calSrc = "embedded placeholder (pump_calibration_placeholder.csv)";
                    }
                }
                catch (Exception ex)
                {
                    calSrc = $"failed to read '{calPath}': {ex.Message}";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pump calibration: " + calSrc);
                }
                if (calPending)
                {
                    // the calibration read is still on its way - report once instead of
                    // planning with a missing table and warning about it
                    res.Pump.Add("# pump mode - calibration loading: " + calSrc);
                }
                else
                {
                    var plan = Core.Extrusion.PumpModel.Plan(pumpInput, cal);
                    res.Pump.Add($"# pump mode - calibration: {calSrc}");
                    res.Pump.AddRange(plan.Summary);
                    foreach (var w in plan.Warnings)
                    {
                        res.Pump.Add("WARNING: " + w);
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pump: " + w);
                    }
                    res.Info += $"  pumpQ:{plan.QRequiredLpm:0.000}l/min" +
                                (plan.Setting != null ? $"  pumpSet:{plan.Setting:0.00}" : "  pumpSet:n/a");
                }
            }

            // ---- optional save (background, keyed by path + content hash) ----
            string savePath = "";
            int si = Params.IndexOfInputParam("savePath");
            if (si >= 0 && DA.GetData(si, ref savePath) && !string.IsNullOrWhiteSpace(savePath))
            {
                try
                {
                    string p = SavePath.Resolve(savePath, "print.gcode");   // folders → folder\print.gcode
                    string content = res.Text;
                    ulong contentHash = ContentHash.Fnv1a(content);
                    string saveKey = FormattableString.Invariant(
                        $"{p}|{content.Length}|{contentHash:X16}");
                    if (_saver.ResultFor(saveKey) is string savedPath)
                        res.Info += $"  saved:{savedPath}";
                    else if (_saver.ErrorFor(saveKey) is string saveError)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, saveError);
                        _saver.ForgetError();
                    }
                    else
                    {
                        _saver.Start(this, saveKey, ct =>
                        {
                            try { File.WriteAllText(p, content); return p; }
                            catch (Exception ex) { throw new IOException(SavePath.Explain(ex, p)); }
                        });
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Saving " + p + " …");
                        res.Info += "  saving…";
                    }
                }
                catch (Exception ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Save failed: " + SavePath.Explain(ex, savePath)); }
            }
            return res;
        }

        // background file work (AUD-PERF-001)
        readonly BackgroundFileWork<Core.Extrusion.PumpCalibration> _calReader = new("pump calibration read");
        readonly BackgroundFileWork<string> _saver = new("save");

        public override void RemovedFromDocument(GH_Document document)
        {
            _calReader.CancelAndDetach();
            _saver.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) { _calReader.CancelAndDetach(); _saver.CancelAndDetach(); }
            base.DocumentContextChanged(document, context);
        }

        static List<Vec3> DecimatePreview(IReadOnlyList<Vec3> points, int budget)
        {
            if (points.Count <= budget) return new List<Vec3>(points);
            var result = new List<Vec3>(budget);
            double step = (double)(points.Count - 1) / (budget - 1);
            for (int i = 0; i < budget; i++) result.Add(points[(int)Math.Round(i * step)]);
            return result;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            // "gcode_text" (formerly index 1) is gone - see RegisterOutputParams. Text is
            // still built internally for ArcWelder and the savePath write, but only when
            // one of those actually runs. Every index below shifted down by one.
            DA.SetDataList(0, r.Lines);
            DA.SetDataTree(1, r.Preview);
            DA.SetDataTree(2, r.Travels);
            DA.SetData(3, r.Length);
            DA.SetData(4, r.Volume);
            DA.SetData(5, r.Time);
            DA.SetData(6, r.Info);
            DA.SetDataList(7, r.Pump);
            if (r.Start.HasValue) DA.SetData(8, r.Start.Value);
            DA.SetData(9, r.StartGcode);
        }
    }
}
