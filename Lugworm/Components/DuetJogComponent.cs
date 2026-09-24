using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Duet;
using Lugworm.Ui;
using Rhino.Geometry;

namespace Lugworm.Components
{
    /// <summary>On-canvas jog pendant for a Duet machine. Every press is planned by
    /// Core's JogPlanner, which hard-refuses when the machine isn't idle+homed, the
    /// status is stale, the step isn't whitelisted, the feed exceeds the cap, or the
    /// absolute target violates the machine's limits. With the default (unwired)
    /// Potterbot profile that is the full SCARA check set (reach annulus, dead zone,
    /// Z, joint ranges, mast clearance) plus the tracked arm configuration; with a
    /// generic-duet-cartesian Robot Definition it is the M208 envelope, with no
    /// SCARA checks and no Rhino/machine frame mirror. The E-STOP capsule ignores
    /// every gate. "Set bed origin" captures the current position for the
    /// slicer and simulation - they do NOT write work offsets to the firmware.</summary>
    public class DuetJogComponent : MachineComponentBase, IDropdownHost
    {
        public DuetJogComponent()
            : base("Duet Jog", "Jog",
                   "Move a connected Duet with the buttons after verified homing, keeping the physical emergency stop within reach.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0017-4b1b-9c3d-aa0000000017");
        // 3.0.0: superseded by the Duet Console (…0029). Kept, unchanged, so
        // existing definitions keep loading and solving; hidden from the ribbon.
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetJog();

        // hard ceiling for jogging regardless of inputs (mm/s)
        const double JogFeedCeiling = 50.0;

        double _step = 1.0;
        double _feed = 20.0;
        RobotLimits _lim = PotterbotLimits();
        RobotProfile _profile = RobotCatalog.Get("potterbot-scara-v4");
        string? _profileError;
        // Cartesian-profile axes whose envelope could be resolved neither from a
        // wired input nor from the firmware's M208 report - jogging refuses while
        // any remain, because an unknown envelope cannot be validated against.
        // Swapped by reference like _lim (worker lambdas read it), never mutated.
        IReadOnlyList<string> _envelopeUnknown = Array.Empty<string>();
        int _elbow;
        Point3d _bedOrigin = Point3d.Unset;
        double _zZero = double.NaN;
        DateTime _resetArmedUntil = DateTime.MinValue;
        DateTime _homeAxisArmedUntil = DateTime.MinValue;
        DateTime _maintenanceArmedUntil = DateTime.MinValue;
        // set at every motion send; the next jog/home is refused until the poll
        // loop delivers a snapshot taken AFTER it (post-move position latch)
        DateTime? _lastCmdSentUtc;

        static RobotLimits PotterbotLimits() => new RobotLimits { ReachMin = 235.0 };

        bool Cartesian => _profileError == null
            && _profile.Mechanism == RobotMechanism.Cartesian;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, connected before jogging or homing", "core", PType.Text),
            new ParamSpec("feed", "F", "Jog feedrate, mm/s (capped at " +
                JogFeedCeiling.ToString("0", CultureInfo.InvariantCulture) + ")", "core", PType.Number)
                .Hint(1, JogFeedCeiling, 20, 0),
            // limits overrides - same names/defaults as the Robot simulation component
            new ParamSpec("L1", "L1", "First SCARA link length in mm", "limits", PType.Number).Hint(100, 1000, 549.06, 2),
            new ParamSpec("L2", "L2", "Second SCARA link length in mm", "limits", PType.Number).Hint(100, 1000, 457.23, 2),
            new ParamSpec("reachMax", "Rx", "Maximum SCARA reach in mm (0 = L1 + L2)", "limits", PType.Number).Hint(0, 1200, 0, 0),
            new ParamSpec("reachMin", "Rn", "Minimum SCARA reach in mm (0 = the absolute difference between L1 and L2)", "limits", PType.Number).Hint(0, 500, 235, 0),
            new ParamSpec("zMax", "Zx", "Maximum machine Z in mm, corresponding to the firmware M208 limit", "limits", PType.Number).Hint(100, 2000, 400, 0),
            new ParamSpec("xMin", "Xn", "Minimum machine X in mm, corresponding to the firmware M208 limit", "limits", PType.Number).Hint(-2000, 2000, -300, 0),
            new ParamSpec("xMax", "Xx", "Maximum machine X in mm, corresponding to the firmware M208 limit", "limits", PType.Number).Hint(-2000, 12000, 10000, 0),
            new ParamSpec("yMin", "Yn", "Minimum machine Y in mm, corresponding to the firmware M208 limit", "limits", PType.Number).Hint(-12000, 2000, -10000, 0),
            new ParamSpec("yMax", "Yx", "Maximum machine Y in mm, corresponding to the firmware M208 limit", "limits", PType.Number).Hint(-2000, 2000, 0, 0),
            new ParamSpec("checkEnvelope", "cE", "Check targets against the machine's Cartesian axis limits when true", "limits", PType.Boolean).HintBool(true),
            new ParamSpec("elbow", "E", "Fallback SCARA arm mode: 0 = up, 1 = down, overridden by the verified homing configuration", "limits", PType.Integer).Hint(0, 1, 1, 0),
            new ParamSpec("robot", "R", "Robot Definition text for the connected Duet machine (unwired = Potterbot SCARA, Cartesian profiles use firmware axis limits)", "robot", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("limits", "Machine limit overrides (L1/L2, reach, Z, elbow)"),
            ("robot", "Robot definition (generic Duet machines)"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter("pos", "P", "Reported nozzle position in Rhino coordinates, in mm even when the document uses other units", GH_ParamAccess.item);
            pManager.AddBooleanParameter("homed", "Hm", "True when X, Y and Z report homed", GH_ParamAccess.item);
            pManager.AddBooleanParameter("xHomed", "XH", "X axis homed", GH_ParamAccess.item);
            pManager.AddBooleanParameter("yHomed", "YH", "Y axis homed", GH_ParamAccess.item);
            pManager.AddBooleanParameter("zHomed", "ZH", "Z axis homed", GH_ParamAccess.item);
            pManager.AddPointParameter("bedOrigin", "Bo", "Captured plate origin in Rhino coordinates and mm for Print Bed's center input (convert to document units first)", GH_ParamAccess.item);
            pManager.AddNumberParameter("zZero", "Z0", "Legacy captured Z in mm from saved documents, with no new captures", GH_ParamAccess.item);
            pManager.AddTextParameter("reply", "R", "Last firmware reply", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Connection, homing and last-action details", GH_ParamAccess.item);
        }

        // ---- step dropdown ----------------------------------------------------

        public string DropdownLabel => "step " + _step.ToString("0.###", CultureInfo.InvariantCulture) + " mm";

        public void FillDropdown(ToolStripDropDown menu)
        {
            foreach (double s in JogPlanner.StepWhitelist)
            {
                double captured = s;
                var item = new ToolStripMenuItem(captured.ToString("0.###", CultureInfo.InvariantCulture) + " mm")
                { Checked = Math.Abs(captured - _step) < 1e-9 };
                item.Click += (o, e) => { _step = captured; ExpireFromUi(); };
                menu.Items.Add(item);
            }
        }

        // ---- capsules ----------------------------------------------------------

        sealed class GateState
        {
            public readonly ActionGate Jog, Home, HomeX, HomeY, HomeZ, Capture;
            public GateState(ActionGate jog, ActionGate home, ActionGate homeX,
                             ActionGate homeY, ActionGate homeZ, ActionGate capture)
            { Jog = jog; Home = home; HomeX = homeX; HomeY = homeY; HomeZ = homeZ; Capture = capture; }
        }

        static ActionGate Gate(string action, ActionGate source)
            => new ActionGate(action, source.Reason, source.Level);

        static ActionGate ClearGate(string action, string? tooltip = null)
            => new ActionGate(action, tooltip, GateReport.None);

        static ActionGate MacroGate(string action, string unavailable, bool available,
                                    bool reportUnavailable, ActionGate homeGate,
                                    string? enabledTooltip = null)
        {
            if (!available)
            {
                if (!reportUnavailable && homeGate.Reason != null)
                    return Gate(action, homeGate);
                return new ActionGate(action, unavailable,
                    reportUnavailable ? GateReport.Warning : GateReport.None);
            }
            if (homeGate.Reason != null) return Gate(action, homeGate);
            return ClearGate(action, enabledTooltip);
        }

        GateState BuildGateState()
        {
            var s = Session;
            bool live = s != null && s.State == DuetConnState.Connected;
            bool paused = s?.Latest?.Status == MachineStatus.Paused;
            bool homeAll = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("all"));
            bool homeX = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("X"));
            bool homeY = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("Y"));
            bool homeZ = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("Z"));

            // an unresolved or non-Duet robot definition blocks every planned
            // action (fail closed); only E-STOP / Reset stay live, by design
            if (_profileError != null)
            {
                var blocked = new ActionGate("all actions", _profileError, GateReport.Warning);
                return new GateState(Gate("Jog", blocked), Gate("Home all", blocked),
                    Gate("Hm X", blocked), Gate("Hm Y", blocked), Gate("Hm Z", blocked),
                    Gate("Set bed origin", blocked));
            }

            var connected = ConnectedActionGate("");
            // the tracked arm configuration is a SCARA concept - a Cartesian
            // machine has nothing to establish, so its jog gate skips it
            ActionGate jog = connected.Reason != null
                ? Gate("Jog", connected)
                : Cartesian ? ClearGate("Jog") : ArmConfigurationActionGate("Jog", "jogging");
            if (jog.Reason == null && !paused)
                jog = StateActionGate("Jog", requireIdle: true, requireHomed: true, activity: "jogging");
            if (jog.Reason == null && Busy)
                jog = new ActionGate("Jog", "another action is executing", GateReport.Remark);
            if (jog.Reason == null && Cartesian && _envelopeUnknown.Count > 0)
                jog = new ActionGate("Jog",
                    "machine envelope unknown (" + string.Join(", ", _envelopeUnknown)
                    + "). Wait for the M208 report or wire the limit inputs", GateReport.Warning);

            ActionGate home = connected.Reason != null
                ? Gate("Home all", connected)
                : StateActionGate("Home all", requireIdle: true, requireHomed: false, activity: "homing");
            ActionGate capture = connected.Reason != null
                ? Gate("Set bed origin", connected)
                : StateActionGate("Set bed origin", requireIdle: true,
                    requireHomed: true, activity: "origin capture");
            bool reportUnavailable = live;
            return new GateState(
                jog,
                MacroGate("Home all", "home-all macro is unavailable", homeAll,
                    reportUnavailable, home),
                MacroGate("Hm X", "X home macro is unavailable", homeX,
                    reportUnavailable, home),
                MacroGate("Hm Y", "Y home macro is unavailable", homeY,
                    reportUnavailable, home),
                MacroGate("Hm Z", "Z home macro is unavailable", homeZ,
                    reportUnavailable, home),
                capture);
        }

        protected override IEnumerable<ActionGate> ActionGates()
        {
            var gates = BuildGateState();
            return new[] { gates.Jog, gates.Home, gates.HomeX, gates.HomeY, gates.HomeZ, gates.Capture };
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
        {
            get
            {
                var s = Session;
                bool live = s != null && s.State == DuetConnState.Connected;
                bool resetArmed = DateTime.Now < _resetArmedUntil;
                bool homeAxisArmed = DateTime.Now < _homeAxisArmedUntil;
                bool homeAll = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("all"));
                bool homeX = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("X"));
                bool homeY = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("Y"));
                bool homeZ = s != null && live && (!s.HomeProbeComplete || s.HomeAvailable("Z"));
                bool paused = s?.Latest?.Status == MachineStatus.Paused;
                bool maintenanceArmed = DateTime.UtcNow < _maintenanceArmedUntil;
                var gates = BuildGateState();
                string? jogReason = gates.Jog.Reason;
                // paused-override arming marker: compact so jog capsules stay narrow;
                // the tooltip explains the two-press override
                string jogSuffix = paused && !maintenanceArmed ? " ⚠" : "";
                string? jogTip = jogReason ?? (jogSuffix.Length > 0
                    ? "paused: the first press refuses and arms a 5 s override, and a second press moves the selected step"
                    : "Move one selected step in machine axes using feed, after connecting and verified homing");
                string homeAxisCaution = Cartesian
                    ? "per axis homing leaves the other axes unhomed"
                    : "a partially homed SCARA has an undefined Cartesian position";
                string homeAxisTooltip = (homeAxisArmed
                    ? "press again within 3 s to home this axis on the connected idle machine: "
                    : "press twice within 3 s to home this axis on the connected idle machine: ") + homeAxisCaution;
                string? captureGate = gates.Capture.Reason;
                return new[]
                {
                    new[]
                    {
                        ActionButton("x-", "X−" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                        ActionButton("x+", "X+" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                        ActionButton("y-", "Y−" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                        ActionButton("y+", "Y+" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                    },
                    new[]
                    {
                        ActionButton("z-", "Z−" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                        ActionButton("z+", "Z+" + jogSuffix, GH_Palette.Black, jogReason == null, jogTip),
                    },
                    new[]
                    {
                        ActionButton("home", Busy ? BusyLabel ?? "…" : "Home all", GH_Palette.Brown,
                            homeAll && gates.Home.Reason == null, gates.Home.Reason ?? "Run the full homing macro with one press on a connected idle machine, keeping the travel area clear"),
                        ActionButton("homex", "Hm X", GH_Palette.Brown,
                            homeX && gates.HomeX.Reason == null,
                            homeX ? gates.HomeX.Reason ?? homeAxisTooltip : gates.HomeX.Reason),
                        ActionButton("homey", "Hm Y", GH_Palette.Brown,
                            homeY && gates.HomeY.Reason == null,
                            homeY ? gates.HomeY.Reason ?? homeAxisTooltip : gates.HomeY.Reason),
                        ActionButton("homez", "Hm Z", GH_Palette.Brown,
                            homeZ && gates.HomeZ.Reason == null, gates.HomeZ.Reason ?? "Home Z with one press on a connected idle machine using its Z homing macro"),
                    },
                    new[]
                    {
                        ActionButton("setxy", "Set bed origin", GH_Palette.Grey, captureGate == null, captureGate ?? "Capture current XY after connecting and homing, without moving or changing firmware offsets"),
                    },
                    new[]
                    {
                        ActionButton("estop", "E-STOP", GH_Palette.Error, s != null,
                            s == null ? "no session. Use the physical emergency stop" : "Send emergency stop immediately with one press, using the physical stop if the machine does not stop"),
                        ActionButton("reset", resetArmed ? "Reset?" : "Reset (M999)", GH_Palette.Warning,
                            live || s != null, s == null ? "no session" : "Press twice within 3 s to restart firmware with a maintenance override, then verify recovery and home again"),
                    },
                };
            }
        }

        public override void OnCapsulePressed(string id)
        {
            switch (id)
            {
                case "x-": Jog(-_step, 0, 0); return;
                case "x+": Jog(+_step, 0, 0); return;
                case "y-": Jog(0, -_step, 0); return;
                case "y+": Jog(0, +_step, 0); return;
                case "z-": Jog(0, 0, -_step); return;
                case "z+": Jog(0, 0, +_step); return;

                // Per-axis homing runs the machine's SCARA macros: G28 X ->
                // homeproximal.g and G28 Y -> homedistal.g. GcodeParser deliberately
                // rejects these in unattended job files; these attended UI actions
                // remain available only behind the two-press maintenance arm.
                case "home": Home("G28"); return;
                case "homex": if (ArmPerAxisHome("X")) Home("G28 X"); return;
                case "homey": if (ArmPerAxisHome("Y")) Home("G28 Y"); return;
                case "homez": Home("G28 Z"); return;

                case "setxy":
                    CaptureOrigin(); return;

                case "estop":
                    _resetArmedUntil = DateTime.MinValue;
                    RunEmergencyStop();
                    return;

                case "reset":
                    if (DateTime.Now >= _resetArmedUntil)
                    {
                        // two-step arm: M999 restarts the firmware, don't make it a misclick
                        _resetArmedUntil = DateTime.Now.AddSeconds(3);
                        SetActionLifecycle("reset (M999)", "armed. Click Reset again to confirm");
                        ExpireFromUi();
                        return;
                    }
                    _resetArmedUntil = DateTime.MinValue;
                    var session = Session;
                    if (session == null)
                    {
                        LastActionResult = ActionFailure("reset (M999)", new InvalidOperationException("no session"));
                        ExpireFromUi();
                        return;
                    }
                    SetActionLifecycle("reset (M999)", "queued and accepted");
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        string result;
                        try
                        {
                            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                                SetActionLifecycle("reset (M999)", "executing")));
                            await session.ResetAsync(maintenanceOverride: true).ConfigureAwait(false);
                            result = $"reset (M999) @ {HostInput}: confirmed by Duet, firmware restarting, session will reconnect";
                        }
                        catch (Exception ex) { result = ActionFailure("reset (M999)", ex); }
                        Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            LastActionResult = result;
                            if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                        }));
                    });
                    return;
            }
        }

        bool ArmPerAxisHome(string axis)
        {
            if (DateTime.Now >= _homeAxisArmedUntil)
            {
                _homeAxisArmedUntil = DateTime.Now.AddSeconds(3);
                SetActionLifecycle("homing " + axis, "armed. Click the same button again to confirm. "
                    + (Cartesian ? "per axis homing leaves the other axes unhomed"
                                 : "a partially homed SCARA has an undefined Cartesian position"));
                ExpireFromUi();
                return false;
            }
            _homeAxisArmedUntil = DateTime.MinValue;
            return true;
        }

        void Home(string gcode)
        {
            var session = Session;
            string? refusal = DuetHoming.PreflightRefusal(session, gcode);
            if (refusal != null)
            {
                LastActionResult = "ERROR: " + refusal;
                ExpireFromUi();
                return;
            }
            var homeSession = session!;   // PreflightRefusal refuses a missing session
            // Freshness-gating happens inside the command transaction below, not here:
            // the poll loop keeps a stale-stamped snapshot through reconnects, and G28
            // must never fire on an unknown machine state, so the state gate runs on the
            // fresh snapshot RunGatedAction fetches under the command lock. The sequence
            // itself (gate, send, verify, arm-configuration bookkeeping) is DuetHoming,
            // shared with the Print Console.
            _lastCmdSentUtc = DateTime.UtcNow;
            bool cartesian = Cartesian;
            string? profileError = _profileError;
            RunGatedAction("homing", includeJob: false, (snap, client, ct) =>
                DuetHoming.HomeAsync(homeSession, snap, client, gcode, _lim, cartesian,
                                     profileError, _lastCmdSentUtc, ct));
        }

        void CaptureOrigin()
        {
            RunGatedAction("capturing bed origin", false,
                async (snap, client, ct) =>
                {
                    string? gate = StateGate.Refuse(snap, requireIdle: true, requireHomed: true,
                        activity: "origin capture");
                    if (gate != null) throw new DuetException("origin capture refused: " + gate);
                    var x = snap.Axis("X"); var y = snap.Axis("Y"); var z = snap.Axis("Z");
                    if (x == null || y == null || z == null) throw new DuetException("origin capture refused: the XYZ position is unavailable");
                    // bedOrigin is wired straight into Robot bedCenter / Mover, so it is
                    // captured in the Rhino frame; Z is common to both frames.
                    var p = ToRhinoPoint(x.MachinePosition, y.MachinePosition, z.MachinePosition);
                    _bedOrigin = p;
                    return $"bed origin captured at ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})";
                });
        }

        Point3d? CurrentPos()
        {
            var snap = Session?.Latest;
            var x = snap?.Axis("X"); var y = snap?.Axis("Y"); var z = snap?.Axis("Z");
            if (x == null || y == null || z == null) return null;
            return ToRhinoPoint(x.MachinePosition, y.MachinePosition, z.MachinePosition);
        }

        /// <summary>Machine-frame XYZ -> the point the operator sees. The Potterbot's
        /// calibrated frames are mirrored about y = -x (Core MachineFrame); a generic
        /// Cartesian profile has no such mirror, so its machine frame IS the Rhino
        /// frame and the conversion is the identity.</summary>
        Point3d ToRhinoPoint(double mx, double my, double mz)
        {
            if (Cartesian) return new Point3d(mx, my, mz);
            MachineFrame.ToRhino(mx, my, out double x, out double y);
            return new Point3d(x, y, mz);
        }

        /// <summary>Jog capsules are labelled in the Rhino frame, because that is the
        /// frame the operator designs in. JogPlanner speaks the machine's frame, so
        /// the requested delta is converted here (identity for a Cartesian profile).
        /// The map sends one axis to one axis, so the planner's "exactly one axis
        /// per press" rule still holds.</summary>
        void Jog(double dx, double dy, double dz)
        {
            if (!Cartesian) MachineFrame.ToMachine(dx, dy, out dx, out dy);
            var session = Session;
            if (session == null)
            {
                LastActionResult = "ERROR: not connected";
                ExpireFromUi();
                return;
            }
            bool maintenanceOverride = DateTime.UtcNow < _maintenanceArmedUntil;
            bool cartesian = Cartesian;
            string? profileError = _profileError;
            _lastCmdSentUtc = DateTime.UtcNow;
            RunGatedAction("jogging", includeJob: false, async (snap, client, ct) =>
            {
                if (profileError != null)
                    throw new DuetException("jog refused: " + profileError);
                bool armed = maintenanceOverride && snap.Status == MachineStatus.Paused;
                if (snap.Status == MachineStatus.Paused && !armed)
                {
                    _maintenanceArmedUntil = DateTime.UtcNow.AddSeconds(5);
                    throw new DuetException("jog refused while paused. The maintenance override is now armed, click Jog again to proceed");
                }
                if (cartesian && _envelopeUnknown.Count > 0)
                    throw new DuetException("jog refused: machine envelope unknown ("
                        + string.Join(", ", _envelopeUnknown)
                        + "). Wait for the firmware's M208 report or wire the limit inputs");
                // the tracked arm configuration exists only for the SCARA; a
                // Cartesian machine's jog is gated by state + envelope alone
                var configuration = session.ArmConfiguration;
                if (!cartesian && !configuration.Known)
                    throw new DuetException("jog refused: arm configuration is unknown. Complete a verified home first");
                var plan = JogPlanner.Plan(snap,
                    new JogRequest { Dx = dx, Dy = dy, Dz = dz, FeedMmS = _feed },
                    _lim, _elbow, maxFeedMmS: JogFeedCeiling,
                    notBeforeUtc: _lastCmdSentUtc, maintenanceOverride: armed,
                    sessionConfiguration: cartesian ? (ArmConfiguration?)null : configuration,
                    mechanism: cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara);
                if (!plan.Ok) throw new DuetException("jog refused: " + plan.RefusalReason);
                string warn = plan.Warnings.Count > 0 ? "  [" + string.Join(". ", plan.Warnings) + "]" : "";
                // report the target the way the operator sees it, in the Rhino frame
                var shown = ToRhinoPoint(plan.TargetX, plan.TargetY, plan.TargetZ);
                session.AppendLog($"jog to ({shown.X:0.##}, {shown.Y:0.##}, {shown.Z:0.##}){warn}");
                session.MarkInitiatedMotion();
                await client.SendCodeNoReplyAsync(plan.Gcode, ct);
                if (!cartesian) session.SetArmConfiguration(plan.FinalConfiguration);
                _maintenanceArmedUntil = DateTime.MinValue;
                return $"jog sent to ({shown.X:0.##}, {shown.Y:0.##}, {shown.Z:0.##})" + warn;
            });
        }

        // ---- solve -------------------------------------------------------------

        sealed class Result
        {
            public Point3d Pos = Point3d.Unset; public bool Homed, XHomed, YHomed, ZHomed;
            public Point3d BedOrigin = Point3d.Unset; public double ZZero = double.NaN;
            public string Reply = ""; public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            HostInput = GetString(DA, "host", "").Trim();
            _feed = Math.Min(GetNumber(DA, "feed", 20.0), JogFeedCeiling);

            // _profile before _profileError: a worker racing this solve must never
            // see a cleared error paired with a not-yet-updated profile
            var profile = ResolveControlProfile(GetString(DA, "robot", ""), out string? profileError);
            if (profile != null) _profile = profile;
            _profileError = profileError;
            if (_profileError != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _profileError);

            var lim = PotterbotLimits();
            lim.L1 = GetNumber(DA, "L1", lim.L1);
            lim.L2 = GetNumber(DA, "L2", lim.L2);
            lim.ReachMax = GetNumber(DA, "reachMax", lim.ReachMax);
            lim.ReachMin = GetNumber(DA, "reachMin", lim.ReachMin);
            lim.ZMax = GetNumber(DA, "zMax", lim.ZMax);
            lim.XMin = GetNumber(DA, "xMin", lim.XMin);
            lim.XMax = GetNumber(DA, "xMax", lim.XMax);
            lim.YMin = GetNumber(DA, "yMin", lim.YMin);
            lim.YMax = GetNumber(DA, "yMax", lim.YMax);
            lim.CheckEnvelope = GetBool(DA, "checkEnvelope", lim.CheckEnvelope);
            lim.HomedArmMode = Lugworm.Robot.RobotModel.Default?.Def.HomedArmMode ?? lim.HomedArmMode;
            var envelopeUnknown = new List<string>();
            if (Cartesian)
            {
                // A generic Duet machine must not inherit the Potterbot's envelope
                // literals. Wired limit inputs win; unwired ones defer to the
                // firmware's own M208 report from the live snapshot; axes with
                // neither block jogging (fail closed, reported at the gate).
                var live = Session?.Latest;
                void Envelope(string name, string axis, bool isMin, Action<double> set)
                {
                    if (IsWired(name)) return;
                    var a = live?.Axis(axis);
                    if (a != null && a.Max > a.Min) set(isMin ? a.Min : a.Max);
                    else envelopeUnknown.Add(name);
                }
                if (lim.CheckEnvelope)
                {
                    Envelope("xMin", "X", true, v => lim.XMin = v);
                    Envelope("xMax", "X", false, v => lim.XMax = v);
                    Envelope("yMin", "Y", true, v => lim.YMin = v);
                    Envelope("yMax", "Y", false, v => lim.YMax = v);
                }
                Envelope("zMax", "Z", false, v => lim.ZMax = v);
                // ZMin has no wired input and is deliberately absent from the
                // envelope-unknown list: without a firmware report it stays 0, which
                // is the conservative bound (a machine whose M208 allows Z < 0 loses
                // that range until connected, never gains unvalidated travel)
                var zAxis = live?.Axis("Z");
                lim.ZMin = zAxis != null && zAxis.Max > zAxis.Min ? zAxis.Min : 0.0;
            }
            _envelopeUnknown = envelopeUnknown;
            _lim = lim;
            _elbow = GetInt(DA, "elbow", 1);

            SubscribeTo(Session);
            ReportLastAction();
            ReportActionGates();

            // the firmware's own M208/M669 values are authoritative - surface
            // any drift between them and the configured software model
            if (_profileError == null)
                ReportFirmwareFindings(_lim, _profile.Mechanism);

            var res = new Result
            {
                BedOrigin = _bedOrigin,
                ZZero = _zZero,
                Reply = Session?.LastReply ?? "",
                Info = "profile " + (_profileError != null ? "INVALID" : _profile.Id) + "\n" +
                       "step " + _step.ToString("0.###", CultureInfo.InvariantCulture) +
                       " mm, feed " + _feed.ToString("0.#", CultureInfo.InvariantCulture) + " mm/s\n" +
                       SessionInfo(_profile.Mechanism),
            };
            var p = CurrentPos();
            if (p != null) res.Pos = p.Value;
            var snapshot = Session?.Latest;
            res.Homed = snapshot?.AllHomed ?? false;
            res.XHomed = snapshot?.Axis("X")?.Homed ?? false;
            res.YHomed = snapshot?.Axis("Y")?.Homed ?? false;
            res.ZHomed = snapshot?.Axis("Z")?.Homed ?? false;
            res.Info += $"\nhomed X={res.XHomed}, Y={res.YHomed}, Z={res.ZHomed}";
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            if (r.Pos.IsValid) DA.SetData(0, r.Pos);
            DA.SetData(1, r.Homed);
            DA.SetData(2, r.XHomed);
            DA.SetData(3, r.YHomed);
            DA.SetData(4, r.ZHomed);
            if (r.BedOrigin.IsValid) DA.SetData(5, r.BedOrigin);
            if (!double.IsNaN(r.ZZero)) DA.SetData(6, r.ZZero);
            DA.SetData(7, r.Reply);
            DA.SetData(8, r.Info);
        }

        // ---- persistence: captured origins + step survive save/reopen -----------

        public override bool Write(GH_IWriter writer)
        {
            writer.SetDouble("iamJogStep", _step);
            if (_bedOrigin.IsValid)
            {
                writer.SetDouble("iamBedOx", _bedOrigin.X);
                writer.SetDouble("iamBedOy", _bedOrigin.Y);
                writer.SetDouble("iamBedOz", _bedOrigin.Z);
            }
            if (!double.IsNaN(_zZero)) writer.SetDouble("iamZZero", _zZero);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            double d = 0;
            if (reader.TryGetDouble("iamJogStep", ref d)) _step = d;
            double ox = 0, oy = 0, oz = 0;
            if (reader.TryGetDouble("iamBedOx", ref ox) && reader.TryGetDouble("iamBedOy", ref oy)
                && reader.TryGetDouble("iamBedOz", ref oz))
                _bedOrigin = new Point3d(ox, oy, oz);
            double zz = 0;
            if (reader.TryGetDouble("iamZZero", ref zz)) _zZero = zz;
            return base.Read(reader);
        }
    }
}
