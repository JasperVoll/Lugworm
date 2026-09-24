using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
    /// <summary>Print statistics from G-code or a moved toolpath: path/extrusion
    /// lengths, extruded volume &amp; clay mass, feed-aware time estimate and a
    /// per-layer breakdown. Print time and material use are direct DoE responses -
    /// wire the numbers straight into the calibration workflow.</summary>
    public class PrintStatsComponent : IamComponentBase
    {
        public PrintStatsComponent()
            : base("Print Stats", "Stats",
                   "Supply G-code or toolpath curves to estimate path length, material use and print time.",
                   "Lugworm", "5 Analyze")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0013-4adb-9c3d-aa0000000013");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Stats();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code as one string or a list of lines. Feeds per move are honored", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("toolpath", "C", "Toolpath curve tree in Rhino document units, used only when gcode is empty", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("width", "W", "Layer width in mm for geometric volume estimates", "core", PType.Number).Hint(0.5, 15, 10, 1),
            new ParamSpec("height", "H", "Layer height, mm (0 = auto from the Z steps)", "core", PType.Number).Hint(0, 6, 0, 2),
            new ParamSpec("density", "De", "Material density, g/cm³ (clay ≈ 2.0)", "core", PType.Number).Hint(0.5, 4, 2, 2),
            new ParamSpec("ramDiameter", "RD", "Ram cartridge bore in mm for volume from G-code E travel (0 = estimate from layer width and height)", "core", PType.Number).Hint(0, 150, 94, 1),
            new ParamSpec("printFeed", "Pf", "Default print feed where the source has none, mm/s", "feeds", PType.Number).Hint(1, 200, 25, 0),
            new ParamSpec("travelFeed", "Tf", "Default travel speed in mm/s where the source has no feedrate", "feeds", PType.Number).Hint(5, 500, 60, 0),
            new ParamSpec("robot", "R", "Optional Robot Definition. With a kinematic profile the estimate also honors the robot's joint speed limits, not the Cartesian feed alone", "robotdef", PType.Text),
            new ParamSpec("accel", "a", "XY acceleration in mm/s^2 for estimating acceleration and corner delays (0 = commanded speeds only)", "feeds", PType.Number).Hint(0, 5000, 0, 0),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("robotdef", "Robot Definition input"),
            ("feeds", "Feed override inputs"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("report", "T", "Readable statistics report. Connect a panel", GH_ParamAccess.item);
            pManager.AddNumberParameter("timeMin", "t", "Estimated duration in min, using joint speed limits when available, otherwise acceleration or commanded speeds", GH_ParamAccess.item);
            pManager.AddNumberParameter("volumeCm3", "V", "Extruded volume, cm³", GH_ParamAccess.item);
            pManager.AddNumberParameter("massG", "m", "Extruded mass, g", GH_ParamAccess.item);
            pManager.AddNumberParameter("extrudeLen", "Le", "Total nozzle path length while extruding, mm", GH_ParamAccess.item);
            pManager.AddNumberParameter("travelLen", "Lt", "Travel length, mm", GH_ParamAccess.item);
            pManager.AddIntegerParameter("layers", "L", "Layer count", GH_ParamAccess.item);
            pManager.AddNumberParameter("perLayer", "pL", "One branch per layer: Z in mm, extrusion length in mm, time in min", GH_ParamAccess.tree);
        }

        sealed class Result
        {
            public PrintStatsResult Stats = new();
            public GH_Structure<GH_Number> PerLayer = new();
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var lines = GetStringList(DA, "gcode");
            string gcode = lines.Count == 1 ? lines[0] : string.Join("\n", lines);
            var toolpath = GetCurveTree(DA, "toolpath");
            bool haveGcode = !string.IsNullOrWhiteSpace(gcode);
            bool haveCurves = toolpath != null && !toolpath.IsEmpty;
            if (!haveGcode && !haveCurves)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No gcode or toolpath wired"); return null; }
            if (haveGcode && haveCurves)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "G-code selected, toolpath ignored");

            double w = GetNumber(DA, "width", 10.0);
            double h = GetNumber(DA, "height", 0.0);
            double density = GetNumber(DA, "density", 2.0);
            double ramD = GetNumber(DA, "ramDiameter", 94.0);
            double printFeed = GetNumber(DA, "printFeed", 25.0);
            double travelFeed = GetNumber(DA, "travelFeed", 60.0);
            double accel = GetNumber(DA, "accel", 0.0);

            // A nonphysical number would flow into a negative mass or a NaN time without a word.
            static string? Invalid(double v, string name, bool positive)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) return name + " must be a finite number";
                if (positive ? v <= 0 : v < 0) return name + (positive ? " must be > 0" : " must be >= 0");
                return null;
            }
            string? invalid = Invalid(w, "width", true) ?? Invalid(h, "height", false)
                ?? Invalid(density, "density", true) ?? Invalid(ramD, "ramDiameter", false)
                ?? Invalid(printFeed, "printFeed", true) ?? Invalid(travelFeed, "travelFeed", true)
                ?? Invalid(accel, "accel", false);
            if (invalid != null) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, invalid); return null; }

            RobotProfile robot;
            try { robot = RobotCatalog.Resolve(GetString(DA, "robot", "none")); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message); return null; }

            var res = new Result();
            if (haveGcode)
            {
                var prog = GcodeParser.Parse(gcode.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries),
                    allowPerAxisHome: robot.Mechanism == RobotMechanism.Cartesian);
                if (prog.Moves.Count == 0)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No moves parsed"); return null; }
                double? arcTol = robot.Mechanism == RobotMechanism.Scara
                    ? robot.CreateScaraLimits().ArcChordTol : null;
                var sampled = ToolpathSampler.SampleMovesWithFeeds(prog.Moves, arcTol);
                var pts = sampled.pts;
                if (h <= 1e-6) h = ExtrusionPreview.AutoLayerHeight(pts, w * 0.5);
                res.Stats = PrintStatsCalc.FromMoves(prog.Moves, w, h, density, printFeed, ramD, accel,
                                                     defaultTravelFeedMmS: travelFeed);
                var jointLimits = BuildJointLimits(robot, pts, prog);
                if (jointLimits != null)
                {
                    var timing = PrintStatsCalc.FromPoints(pts, sampled.ext, w, h, density,
                        printFeed, travelFeed, sampled.feeds, accel, null, jointLimits);
                    res.Stats.JointLimitedTimeMin = timing.JointLimitedTimeMin;
                    res.Stats.JointLimitedSegments = timing.JointLimitedSegments;
                }
            }
            else
            {
                var sampled = ToolpathSampler.SampleWithMetadata(toolpath!, scaleToMm: DocUnits.ScaleToMm());
                var pts = sampled.pts; var ext = sampled.ext;
                if (pts.Count == 0)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The toolpath has no usable curves"); return null; }
                if (h <= 1e-6) h = ExtrusionPreview.AutoLayerHeight(pts, w * 0.5, sampled.layerIds);
                res.Stats = PrintStatsCalc.FromPoints(pts, ext, w, h, density, printFeed, travelFeed, null, accel,
                    sampled.layerIds, BuildJointLimits(robot, pts));
            }

            for (int i = 0; i < res.Stats.PerLayer.Count; i++)
            {
                var path = new GH_Path(i);
                var (z, len, t) = res.Stats.PerLayer[i];
                res.PerLayer.Append(new GH_Number(z), path);
                res.PerLayer.Append(new GH_Number(len), path);
                res.PerLayer.Append(new GH_Number(t), path);
            }
            Message = res.Stats.JointLimitedTimeMin > 0
                ? $"{res.Stats.JointLimitedTimeMin:0.#} min - {res.Stats.VolumeCm3:0.#} cm³ - robot"
                : $"{(res.Stats.TimeAccelMin > 0 ? res.Stats.TimeAccelMin : res.Stats.TimeMin):0.#} min - {res.Stats.VolumeCm3:0.#} cm³";
            return res;
        }

        /// <summary>Joint samples for the selected robot, or null for a Cartesian /
        /// toolpath-only profile - in which case every estimate stays exactly as it was
        /// before this input existed.</summary>
        JointSpeedLimits? BuildJointLimits(RobotProfile robot, List<Vec3> pts, ParsedProgram? program = null)
        {
            if (!robot.HasKinematics || pts.Count < 2) return null;
            try
            {
                var solved = program != null
                    ? RobotSimulation.SolveProgram(robot, program, pts,
                        GcodeFlavor.DuetRrf, arcSupport: true)
                    : RobotSimulation.Solve(robot, pts);
                if (!solved.KinematicsApplied || solved.JointsDeg.Count < 2) return null;

                // The SCARA ceilings live on the profile's own limits; UR carries its
                // datasheet elbow cap instead, so only the SCARA path has a scalar to use.
                double maxJointSpeed = 0, maxFeed = 0;
                if (robot.Mechanism == RobotMechanism.Scara)
                {
                    var limits = robot.CreateScaraLimits();
                    maxJointSpeed = limits.MaxJointSpeed;
                    maxFeed = limits.MaxFeed;
                }
                if (maxJointSpeed <= 0 && maxFeed <= 0) return null;

                if (!solved.ReachOk)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "Some targets fail the robot's reach checks. The robot limited time covers the reachable samples only");
                return new JointSpeedLimits(solved.JointsDeg, maxJointSpeed, maxFeed);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Robot time model skipped: " + ex.Message);
                return null;
            }
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Stats.Report());
            // Precedence: the robot-limited estimate is the most constrained and
            // therefore the most realistic, then the accel model, then the lower bound.
            DA.SetData(1, r.Stats.JointLimitedTimeMin > 0 ? r.Stats.JointLimitedTimeMin
                        : r.Stats.TimeAccelMin > 0 ? r.Stats.TimeAccelMin : r.Stats.TimeMin);
            DA.SetData(2, r.Stats.VolumeCm3);
            DA.SetData(3, r.Stats.MassG);
            DA.SetData(4, r.Stats.ExtrudeLenMm);
            DA.SetData(5, r.Stats.TravelLenMm);
            DA.SetData(6, r.Stats.Layers);
            DA.SetDataTree(7, r.PerLayer);
        }
    }
}
