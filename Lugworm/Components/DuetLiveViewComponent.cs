using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;
using Lugworm.Core.Robot;
using Lugworm.Duet;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Read-only live viewport driven by the shared Duet poll: the posed robot, the
    /// wired print bed, the effective job's intended path as thin neutral context, and the
    /// observed toolpath trail.</summary>
    /// <remarks>
    /// <para>It draws a TOOLPATH, not a print. There is no extrusion mesh, no nozzle marker
    /// and no on-screen timing text: a layer preview is the G-code Generator's and Robot
    /// Simulation's business, the marker sat on the tool the whole time it printed, and the
    /// changing age label alone invalidated an otherwise stationary frame and forced a
    /// repaint every tick. A stale pose is shown as a ghosted arm and stated in
    /// <c>info</c>.</para>
    /// <para>It owns no logging. Every job is recorded by the session's always-on retention
    /// recorder that the Duet Console configures and streams to a folder, whether this
    /// component is present, disabled or deleted.</para>
    /// <para>The robot, trail and context are drawn by a Rhino display conduit owned by
    /// <see cref="LiveViewportController"/>, animated from observed samples without solving
    /// this component; the GH outputs are snapshots of the last solve unless automatic
    /// updates are switched on (context menu).</para>
    /// </remarks>
    public class DuetLiveViewComponent : MachineComponentBase
    {
        public DuetLiveViewComponent()
            : base("Live View", "Live",
                   "Show a connected machine's reported position and toolpath, with no commands sent to the printer.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0023-4b7b-9c3d-aa0000000023");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.DuetLive();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, connected before live preview", "core", PType.Text),
            new ParamSpec("enable", "E", "Read the connected session and animate its live position when true (automatic output updates are a separate menu option)", "core", PType.Boolean)
                .HintBool(true),
            new ParamSpec("gcode", "G", "Fallback G-code text or lines for the intended path, used only when the shared session has no job", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("bed", "B", "Print Bed definition text for the plate preview (unwired = no plate)", "core", PType.Text),
            // kinematics overrides
            new ParamSpec("L1", "L1", "First SCARA link length in mm", "limits", PType.Number).Hint(100, 1000, 549.06, 2),
            new ParamSpec("L2", "L2", "Second SCARA link length in mm", "limits", PType.Number).Hint(100, 1000, 457.23, 2),
            new ParamSpec("baseHeight", "Bh", "Base Z height, mm", "limits", PType.Number).Hint(0, 1000, 0, 0),
            new ParamSpec("reachMax", "Rx", "Maximum SCARA reach in mm (0 = L1 + L2)", "limits", PType.Number).Hint(0, 1200, 0, 0),
            new ParamSpec("reachMin", "Rn", "Minimum SCARA reach in mm (0 = the absolute difference between L1 and L2)", "limits", PType.Number).Hint(0, 500, 235, 0),
            new ParamSpec("elbow", "El", "Fallback SCARA arm mode for drawing: 0 = up, 1 = down, used only without a tracked session configuration", "limits", PType.Integer).Hint(0, 1, 1, 0),
            new ParamSpec("robot", "R", "Robot Definition text for the live preview (unwired = Potterbot SCARA, Cartesian profiles show the nozzle without an arm)", "robot", PType.Text),
        };

        // 1: the 4.0.0 bed contract removed bedWidth/bedDepth/bedCenter and moved 'bed' out
        //    of its toggle group.
        // 2: the extrusion preview and the print path log left this component. extWidth,
        //    layerHeight and the six log inputs are retired, and the layers/log/path/logInfo
        //    outputs are gone - the layer belongs to the G-code Generator and Robot
        //    Simulation, and the log to the Console's always-on retention.
        protected override int InputContractVersion => 2;
        protected override IReadOnlyList<ParamSpec> RetiredInputSpecs => RetiredSpecs;
        protected override void MigrateRetiredInputs(GH_Document doc, IReadOnlyList<RetiredSocket> sockets)
        {
            var bed = new List<RetiredSocket>();
            var others = new List<RetiredSocket>();
            foreach (var socket in sockets)
            {
                if (socket.Name == LegacyBedMigration.Width || socket.Name == LegacyBedMigration.Depth
                    || socket.Name == LegacyBedMigration.Center) bed.Add(socket);
                else others.Add(socket);
            }
            if (bed.Count > 0) LegacyBedMigration.Apply(this, doc, bed);
            if (others.Count > 0)
            {
                var names = new List<string>(others.Count);
                foreach (var socket in others) names.Add(socket.Name);
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "Removed Live View input(s) " + string.Join(", ", names) + " carried data in this saved "
                    + "document and were dropped. The extrusion preview now lives on the G-code Generator and Robot "
                    + "Simulation. The print path log lives on the Duet Console, which records every job "
                    + "on its own.");
            }
        }

        /// <summary>The sockets this component used to declare. The three bed ones migrate
        /// into a real Print Bed; the rest have no equivalent here and are reported.</summary>
        static readonly IReadOnlyList<ParamSpec> RetiredSpecs = BuildRetiredSpecs();

        static IReadOnlyList<ParamSpec> BuildRetiredSpecs()
        {
            var specs = new List<ParamSpec>(LegacyBedMigration.RetiredBedSpecs);
            specs.Add(new ParamSpec("extWidth", "Ew", "Extrusion width, mm", "core", PType.Number));
            specs.Add(new ParamSpec("layerHeight", "Lh", "Layer height = extrusion height, mm", "core", PType.Number));
            specs.Add(new ParamSpec("logEnable", "Le", "Arm the timestamped print path recorder", "log", PType.Boolean));
            specs.Add(new ParamSpec("logEveryN", "Ln", "Interval samples per layer", "log", PType.Integer));
            specs.Add(new ParamSpec("logFolder", "Lf", "Folder for the streaming print log CSV", "log", PType.Text));
            specs.Add(new ParamSpec("logPrefix", "Lp", "Print log filename prefix", "log", PType.Text));
            specs.Add(new ParamSpec("logLayerHeight", "Llh", "Layer height hint for the Z step fallback", "log", PType.Number));
            specs.Add(new ParamSpec("logExport", "Lx", "Export the in-memory print log to a new CSV", "log", PType.Boolean));
            return specs;
        }

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("limits", "Kinematics overrides (L1/L2, reach, elbow)"),
            ("robot", "Robot definition (generic Duet machines)"),
        };

        RobotProfile _profile = RobotCatalog.Get("potterbot-scara-v4");
        string? _profileError;
        bool Cartesian => _profileError == null
            && _profile.Mechanism == RobotMechanism.Cartesian;

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("pos", "P", "Reported nozzle position in Rhino document units, mirrored for Potterbot and unchanged for Cartesian profiles", GH_ParamAccess.item);
            pManager.AddNumberParameter("J1", "J1", "First SCARA joint angle calculated from reported position, deg (unavailable for Cartesian profiles)", GH_ParamAccess.item);
            pManager.AddNumberParameter("J3", "J3", "SCARA elbow angle calculated from reported position, deg (unavailable for Cartesian profiles)", GH_ParamAccess.item);
            pManager.AddNumberParameter("J2", "J2", "SCARA Z carriage height calculated from reported position, mm (unavailable for Cartesian profiles)", GH_ParamAccess.item);
            pManager.AddTextParameter("status", "S", "Raw machine status", GH_ParamAccess.item);
            pManager.AddNumberParameter("progress", "Pr", "Job progress, 0 to 1", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Connection, intended-path and live-display diagnostics", GH_ParamAccess.item);
            pManager.AddGeometryParameter("trail", "T", "Observed path in Rhino document units, one branch per continuous run as a polyline or a single point, with gaps preserved", GH_ParamAccess.tree);
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
            => Array.Empty<IReadOnlyList<ButtonDef>>();
        public override void OnCapsulePressed(string id) { }

        public override void RemovedFromDocument(GH_Document document)
        {
            DisposeLive();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) DisposeLive();
            base.DocumentContextChanged(document, context);
        }

        // ---- persisted display preferences (versioned keys; never sessions or frames) ----

        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("iamLiveDisplayVersion", 1);
            writer.SetBoolean("iamLiveAutoOutputs", _autoOutputs);
            writer.SetBoolean("iamLiveSmooth", _smooth);
            writer.SetBoolean("iamLiveDeviation", _deviationOn);
            writer.SetDouble("iamLiveDeviationMm", _deviationLegendMm);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            // An archive written before the setting existed had automatic outputs (every
            // poll re-solved the component). Preserve that; only new instances default off.
            bool auto = true;
            _autoOutputs = reader.TryGetBoolean("iamLiveAutoOutputs", ref auto) ? auto : true;
            bool smooth = true;
            if (reader.TryGetBoolean("iamLiveSmooth", ref smooth)) _smooth = smooth;
            bool deviation = false;
            if (reader.TryGetBoolean("iamLiveDeviation", ref deviation)) _deviationOn = deviation;
            double legend = 0;
            if (reader.TryGetDouble("iamLiveDeviationMm", ref legend) && legend > 0 && !double.IsInfinity(legend))
                _deviationLegendMm = legend;
            // The base normalizes INPUTS. Outputs are Grasshopper's: it drops a wire to an
            // output that no longer exists without telling anyone, and the four this build
            // removed sat at indices 7-10, where 'trail' now is. Say so once.
            int contract = 0;
            if (!reader.TryGetInt32(ContractKey, ref contract) || contract < 2) _announceRemovedOutputs = true;
            return base.Read(reader);
        }

        readonly BedPreview _bed = new();
        string? _bedCacheKey;
        readonly List<Curve> _wsWires = new();
        BoundingBox _clip = BoundingBox.Empty;

        // ---- live display: owned by the controller, drawn by its conduit ----
        LiveViewportController? _live;
        /// <summary>Publish GH outputs on poll changes (at most once per second). New
        /// instances default to viewport-only updates; archives that predate the setting
        /// keep their automatic behaviour (Read restores true when the key is absent).</summary>
        bool _autoOutputs;
        bool _smooth = true;
        bool _deviationOn;
        double _deviationLegendMm = DeviationColorScale.DefaultLegendMm;
        bool _announceRemovedOutputs;

        static readonly Color RingColor = Color.FromArgb(162, 167, 174);

        sealed class Result
        {
            public Point3d Pos = Point3d.Unset;
            public double? J1, J3, J2;
            public string Status = "";
            public double Progress;
            public string Info = "";
            public LiveTrailSnapshot Trail = LiveTrailSnapshot.Empty;
        }

        protected override void BeforeSolveInstance()
        {
            // The bed survives solves; the live robot, trail and context live in the
            // controller and are not touched by a solve at all.
            _wsWires.Clear();
            _clip = BoundingBox.Empty;
            base.BeforeSolveInstance();
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            if (_announceRemovedOutputs)
            {
                _announceRemovedOutputs = false;
                AddArchiveNotice(GH_RuntimeMessageLevel.Remark,
                    "This document was saved with the older Live View outputs. 'layers', 'log', 'path' and "
                    + "'logInfo' are gone, since the Duet Console records every job on its own, and output 7 "
                    + "is now 'trail', the observed toolpath. Delete any wire that used to leave one of the "
                    + "four removed outputs.");
            }

            string requestedHost = GetString(DA, "host", "").Trim();
            HostInput = requestedHost;
            bool enable = GetBool(DA, "enable", true);
            var session = enable ? Session : null;
            // enable off means no session ownership: a lease taken while it was on goes now,
            // so a disabled Live View cannot keep a session polling on its own
            if (!enable) ReleaseLease();

            // Poll-driven re-solves only in automatic-output mode, and then at most once
            // per second (ExpireMinIntervalMs). Otherwise the outputs are a snapshot of
            // the last explicit solve, and the viewport animates through the controller.
            SubscribeTo(_autoOutputs ? session : null, SessionChangeKind.Connection | SessionChangeKind.Status
                | SessionChangeKind.Job);
            var snap = session?.Latest;

            var profile = ResolveControlProfile(GetString(DA, "robot", ""), out _profileError);
            if (profile != null) _profile = profile;
            if (_profileError != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _profileError);
            // The intended path is cached per frame (the cache key carries it), so switching
            // the robot definition asks the cache for the other frame's entry instead of
            // replaying points converted for this one. Nothing to invalidate here.

            double l1 = GetNumber(DA, "L1", 549.06);
            double l2 = GetNumber(DA, "L2", 457.23);
            double baseHeight = GetNumber(DA, "baseHeight", 0.0);
            int elbow = GetInt(DA, "elbow", 1);
            // The plate is the wired Print Bed's, and only its. Since the 4.0.0 bed
            // contract this component has no width/depth/centre of its own: an unwired
            // or unparseable definition draws no plate rather than inventing one, so
            // what the operator sees is always the bed they configured. Reach stays on
            // this component's own inputs, which feed the display IK.
            string bedText = GetString(DA, "bed", "");
            BedDefinition? bedDef = null;
            try { bedDef = BedDefinitionText.Resolve(bedText); }
            catch (Exception ex) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Print bed: " + ex.Message); }
            if (bedDef == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No print bed wired, so no plate is drawn. Connect a Print Bed component to 'bed'");
            // the definition is already millimetres (its own document-unit conversion
            // happened once, on the Print Bed component's point inputs)
            double bedZ = bedDef?.Center.Z ?? 0.0;
            double reachMaxInput = GetNumber(DA, "reachMax", 0.0);
            double reachMinInput = GetNumber(DA, "reachMin", 235.0);
            double reachMax = reachMaxInput > 0 ? reachMaxInput : l1 + l2;
            double reachMin = reachMinInput > 0 ? reachMinInput : Math.Abs(l1 - l2);
            var lim = new RobotLimits
            {
                L1 = l1,
                L2 = l2,
                BaseH = baseHeight,
                ReachMax = reachMax,
                ReachMin = reachMin,
                HomedArmMode = Lugworm.Robot.RobotModel.Default?.Def.HomedArmMode ?? 1,
            };

            // a Cartesian machine has no reach annulus: the whole plate is
            // printable and no workspace circles are drawn
            bool cartesian = Cartesian;
            string bedKey = bedDef == null ? "" : BedDefinitionText.Serialize(bedDef)
                + (cartesian ? "|cartesian" : "|r" + reachMin.ToString("R", CultureInfo.InvariantCulture)
                                            + "," + reachMax.ToString("R", CultureInfo.InvariantCulture));
            if (_bedCacheKey != bedKey || (bedDef != null && _bed.Frame == null))
            {
                _bed.Clear();
                // no definition = no plate; the remark above already said so
                if (bedDef != null)
                {
                    if (cartesian) _bed.Build(bedDef, 0, 1e9, (x, y) => true);
                    else _bed.Build(bedDef, reachMin, reachMax);
                }
                _bedCacheKey = bedKey;
            }
            if (_bed.Bounds.IsValid) _clip.Union(_bed.Bounds);
            if (!cartesian && bedDef != null)
            {
                var wsPlane = new Plane(new Point3d(0, 0, bedZ), Vector3d.ZAxis);
                _wsWires.Add(new Circle(wsPlane, reachMax).ToNurbsCurve());
                if (reachMin > 1e-6) _wsWires.Add(new Circle(wsPlane, reachMin).ToNurbsCurve());
            }

            var res = new Result
            {
                Status = snap?.RawStatus ?? "",
                Progress = snap?.Job?.FractionPrinted ?? 0.0,
            };

            Point3d? livePosition = null;
            if (session != null && session.State == DuetConnState.Connected && snap != null)
            {
                var x = snap.Axis("X");
                var y = snap.Axis("Y");
                var z = snap.Axis("Z");
                if (x != null && y != null && z != null)
                {
                    // Observed telemetry, never the interpolated display pose: pos and the
                    // joint outputs are what the machine reported at this solve. The Duet
                    // reports its own Cartesian frame; livePosition stays in it so the IK
                    // below yields the machine's REAL joint angles. The point the operator
                    // sees is the Rhino twin.
                    livePosition = new Point3d(x.MachinePosition, y.MachinePosition, z.MachinePosition);
                    res.Pos = ToRhinoPoint(livePosition.Value);
                }
            }

            var pathRequest = ResolveIntendedPath(DA, session, cartesian);

            // During the job header's G28 the Duet reports its axes unhomed and
            // MachinePosition is not a nozzle position - the arm posed from it was drawn
            // diving into the bed (final test round 5.17). A home in flight reports its
            // axes unhomed as well (status busy), so AllHomed covers both cases; pos and
            // the joint outputs stay exactly as the machine reports them. Since
            // 2026-09-09 the arm is not hidden either: the controller holds the last
            // posed frame (the arm flashed in and out at every homing and cancel).
            string? armHiddenReason = null;
            if (HideArmWhileUnhomed && snap != null && !snap.AllHomed)
                armHiddenReason = snap.Status == MachineStatus.Busy ? "homing in progress" : "axes not homed";

            int preferredArmMode = session != null && session.ArmConfiguration.Known
                ? session.ArmConfiguration.ArmMode : elbow;

            // A Cartesian profile draws no arm and runs no IK - the live position
            // needs no joint interpretation.
            if (livePosition.HasValue && !cartesian)
            {
                var p = livePosition.Value;
                var ik = ScaraKinematics.InverseRrf(p.X, p.Y, p.Z, lim, preferredArmMode);
                if (ik.Valid)
                {
                    // J1/J3/J2 are published exactly as the machine holds them.
                    res.J1 = ik.J1; res.J3 = ik.J3; res.J2 = ik.J2;
                }
                else
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Live position: " + ik.Error);
            }
            if (!cartesian)
            {
                var model = Lugworm.Robot.RobotModel.Default;
                if (model != null)
                {
                    if (model.MissingLinks.Count > 0)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "Robot model: no geometry for " + string.Join(", ", model.MissingLinks));
                    foreach (var warning in model.Warnings)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Robot model: " + warning);
                }
            }

            // The display lives in the controller: it animates the Rhino viewport through
            // a conduit from observed samples, never through a solve of this component.
            // enable=false detaches it entirely - no display and no session ownership.
            var live = _live ??= new LiveViewportController(LiveVisible, DocUnits.ScaleToMm);
            live.Configure(new LiveRenderConfig(lim, elbow, cartesian, bedZ, HideArmWhileUnhomed,
                pathRequest, _smooth, _deviationOn, _deviationLegendMm));
            live.Attach(enable ? session : null);

            if (session == null || snap == null) Message = null;
            else Message = snap.Job != null && snap.Job.FractionPrinted > 0
                ? $"{snap.RawStatus}, {snap.Job.FractionPrinted * 100:0}%"
                : snap.RawStatus;

            res.Trail = live.TrailSnapshot();
            string homeNote = snap == null ? "" :
                $"\naxes homed: {(snap.AllHomed ? "yes" : "no")}" +
                (armHiddenReason != null ? $"\narm pose held: {armHiddenReason}" : "");
            string outputsNote = _autoOutputs
                ? "\noutputs: automatic (at most one re-solve per second on changed telemetry)"
                : "\noutputs: snapshot of the last solve (automatic updates off, see the context menu)";
            string trailNote = "\ntrail: " + res.Trail.PointCount.ToString(CultureInfo.InvariantCulture)
                + " observed point(s) in " + res.Trail.Segments.Count.ToString(CultureInfo.InvariantCulture)
                + " segment(s)";
            res.Info = SessionInfo(_profile.Mechanism) + homeNote
                     + "\n" + live.IntendedPathNote + outputsNote + trailNote
                     + "\n" + live.StatusText
                     + "\n" + live.Stats(TelemetrySolves).Describe();
            return res;
        }

        /// <summary>Where the intended path comes from, in the order the plan fixes: the
        /// session's effective job first, this component's <c>gcode</c> socket only as a
        /// fallback, and nothing at all for a job this session did not start.</summary>
        IntendedPathRequest ResolveIntendedPath(IGH_DataAccess DA, MachineSession? session, bool cartesian)
        {
            var inputLines = GetStringList(DA, "gcode");
            var lines = new List<string>();
            foreach (var inputLine in inputLines)
                lines.AddRange(inputLine.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
            bool haveInput = lines.Count > 0;

            var job = session?.Jobs.Effective;
            if (job != null)
            {
                if (!job.IntendedPathAvailable)
                {
                    // An externally started job: the bytes are not known, so there is no
                    // intended path. Substituting this component's own input would draw a
                    // shape the machine is not printing.
                    if (haveInput)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "The running job was started outside this session, so its G-code is unknown. "
                            + "The wired 'gcode' is not substituted for it");
                    return new IntendedPathRequest("", IntendedPathOrigin.Unavailable, "", cartesian, null,
                        "intended path: unavailable, job "
                        + (job.RemotePath ?? job.RunId) + " was started outside this session");
                }
                if (haveInput)
                {
                    // The wire may legitimately carry the file the Console uploaded, in
                    // which case there is nothing to report. Only a wire that does NOT match
                    // the job is worth saying out loud - and the job wins either way.
                    string inputIdentity = ParsedJobCache.IdentityOfLines(lines);
                    string? jobIdentity = _live?.IntendedPathLineIdentity;
                    if (!string.Equals(jobIdentity, inputIdentity, StringComparison.Ordinal))
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "The Duet Console has a job, so its own bytes are the intended path. The wired "
                            + "'gcode' " + (jobIdentity == null ? "is not used" : "differs from it and is not used"));
                }
                // The bytes are taken INSIDE the lambda, on the parse thread and only when a
                // parse is actually needed: JobSnapshot hands out copies, and copying a
                // 2.6 MB file on every solve to answer a cache hit would be the very cost
                // this cache exists to remove.
                return new IntendedPathRequest(
                    ParsedJobCache.KeyFor("sha" + job.Sha256, cartesian),
                    IntendedPathOrigin.SessionJob, job.Sha256, cartesian,
                    () => ParsedJobCache.LinesFromBytes(job.GcodeBytes()),
                    "intended path: the Console's job " + (job.RemotePath ?? job.RunId) + " ("
                    + (job.Sha256.Length >= 12 ? job.Sha256.Substring(0, 12) : job.Sha256) + ", "
                    + job.ByteCount.ToString(CultureInfo.InvariantCulture) + " B)");
            }

            if (!haveInput)
                return IntendedPathRequest.None;

            return new IntendedPathRequest(
                ParsedJobCache.KeyFor(ParsedJobCache.IdentityOfLines(lines), cartesian),
                IntendedPathOrigin.GcodeInput, "", cartesian, () => lines,
                "intended path: the wired 'gcode' (" + lines.Count.ToString(CultureInfo.InvariantCulture)
                + " lines), no job on the Duet Console");
        }

        /// <summary>Never pose the arm from coordinates reported while any axis is unhomed
        /// or a home is in flight - the Duet's MachinePosition is not a nozzle position
        /// then (final test round 5.17). Since 2026-09-09 the controller holds the last
        /// posed frame through such polls instead of hiding the arm.</summary>
        const bool HideArmWhileUnhomed = true;

        /// <summary>Machine-frame point -> the point the operator sees. The
        /// Potterbot's calibrated frames are mirrored about y = -x (Core
        /// MachineFrame); a Cartesian profile's machine frame IS the Rhino frame,
        /// so its conversion is the identity.</summary>
        Point3d ToRhinoPoint(Point3d machine)
        {
            if (Cartesian) return machine;
            MachineFrame.ToRhino(machine.X, machine.Y, out double x, out double y);
            return new Point3d(x, y, machine.Z);
        }

        // ---- live display lifecycle ----

        /// <summary>The conduit draws only when this component would be previewed by
        /// Grasshopper itself: preview on, not hidden or locked, and its document the
        /// one on the canvas. Evaluated per draw; cheap.</summary>
        bool LiveVisible()
        {
            if (Hidden || Locked) return false;
            var doc = OnPingDocument();
            if (doc == null || doc.PreviewMode == GH_PreviewMode.Disabled) return false;
            var canvasDoc = Grasshopper.Instances.ActiveCanvas?.Document;
            return canvasDoc == null || ReferenceEquals(canvasDoc, doc);
        }

        /// <summary>Automatic-output mode re-solves at most once per second; the default
        /// viewport-only mode never re-solves on a poll (the subscription is null).</summary>
        protected override int ExpireMinIntervalMs => _autoOutputs ? 1000 : 0;

        void DisposeLive()
        {
            var live = _live;
            _live = null;
            live?.Dispose();
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            // point outputs are mm machine data shown in document units (AUD-COR-001);
            // J1/J3/J2 keep the machine's own numbers
            double s = DocUnits.ScaleToMm();
            if (r.Pos.IsValid) DA.SetData(0, DocUnits.FromMm(r.Pos, s));
            DA.SetData(1, r.J1);
            DA.SetData(2, r.J3);
            DA.SetData(3, r.J2);
            DA.SetData(4, r.Status);
            DA.SetData(5, r.Progress);
            DA.SetData(6, r.Info);
            DA.SetDataTree(7, BuildTrailTree(r.Trail, s));
        }

        /// <summary>One branch per continuous segment. A segment of two or more observations
        /// becomes a polyline; a segment of one becomes that point, because a single
        /// observation supports no line at all.</summary>
        static GH_Structure<IGH_GeometricGoo> BuildTrailTree(LiveTrailSnapshot trail, double scaleToMm)
        {
            var tree = new GH_Structure<IGH_GeometricGoo>();
            bool scaled = !DocUnits.IsUnity(scaleToMm);
            for (int i = 0; i < trail.Segments.Count; i++)
            {
                var segment = trail.Segments[i];
                var path = new GH_Path(i);
                if (segment.Points.Count == 1)
                {
                    var p = segment.Points[0];
                    tree.Append(new GH_Point(scaled ? DocUnits.FromMm(p, scaleToMm) : p), path);
                    continue;
                }
                var polyline = new Polyline(segment.Points.Count);
                foreach (var p in segment.Points) polyline.Add(scaled ? DocUnits.FromMm(p, scaleToMm) : p);
                tree.Append(new GH_Curve(polyline.ToPolylineCurve()), path);
            }
            return tree;
        }

        public override BoundingBox ClippingBox
        {
            get
            {
                var box = _clip;
                var liveBox = _live?.Bounds ?? BoundingBox.Empty;
                if (liveBox.IsValid) box.Union(liveBox);
                if (!box.IsValid) return base.ClippingBox;
                // machine state is mm; the viewport shows document units (AUD-COR-001)
                double s = DocUnits.ScaleToMm();
                if (!DocUnits.IsUnity(s)) box.Transform(DocUnits.FromMmTransform(s));
                return box;
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            double s = DocUnits.ScaleToMm();
            bool scaled = !DocUnits.IsUnity(s);
            if (scaled) args.Display.PushModelTransform(DocUnits.FromMmTransform(s));
            try
            {
                // One authoritative draw path per element: the robot, trail and intended
                // path are drawn by the controller's conduit, never here as well.
                _bed.DrawMeshes(args.Display);
            }
            finally { if (scaled) args.Display.PopModelTransform(); }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            if (Hidden || Locked) return;
            double s = DocUnits.ScaleToMm();
            bool scaled = !DocUnits.IsUnity(s);
            if (scaled) args.Display.PushModelTransform(DocUnits.FromMmTransform(s));
            try
            {
                _bed.DrawWires(args.Display);
                foreach (var curve in _wsWires) args.Display.DrawCurve(curve, RingColor, 1);
            }
            finally { if (scaled) args.Display.PopModelTransform(); }
        }

        // ---- right-click menu: display preferences only ----
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            var auto = Menu_AppendItem(menu, "Automatically update GH outputs (at most once per second)", (s, e) =>
            {
                RecordUndoEvent("Live View automatic outputs");
                _autoOutputs = !_autoOutputs;
                ExpireFromUi();
            }, true, _autoOutputs);
            auto.ToolTipText = "Refresh changed outputs at most once per second with host connected and enable true (off = viewport animation only).";

            var smooth = Menu_AppendItem(menu, "Smooth live animation (delayed interpolation)", (s, e) =>
            {
                RecordUndoEvent("Live View smoothing");
                _smooth = !_smooth;
                ExpireFromUi();
            }, true, _smooth);
            smooth.ToolTipText = "Smooth the drawn arm along the intended path between reported positions, about two poll intervals behind, leaving the observed trail unchanged.";

            var dev = Menu_AppendItem(menu, "Colour the observed trail by deviation from the intended path (Viridis)", (s, e) =>
            {
                RecordUndoEvent("Live View deviation overlay");
                _deviationOn = !_deviationOn;
                ExpireFromUi();
            }, true, _deviationOn);
            dev.ToolTipText = "Color reported positions by distance to the session job or supplied gcode path, using controller readings rather than measured accuracy.";

            var legend = Menu_AppendItem(menu, "Deviation legend maximum");
            foreach (double mm in new[] { 1.0, 2.0, 5.0, 10.0, 20.0 })
            {
                double chosen = mm;
                Menu_AppendItem(legend.DropDown, mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm", (s, e) =>
                {
                    RecordUndoEvent("Live View deviation legend");
                    _deviationLegendMm = chosen;
                    ExpireFromUi();
                }, true, Math.Abs(_deviationLegendMm - chosen) < 1e-9);
            }
        }
    }
}
