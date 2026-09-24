using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Convert;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Duet;
using Lugworm.Ui;
using Rhino.Geometry;

namespace Lugworm.Components
{
    /// <summary>The single control surface for the Duet machine: ONE modeless
    /// <see cref="Ui.Console.DuetConsoleForm"/> whose controls run the same shared-session
    /// commands and safety gates the 7-Print components use. The session (leased
    /// from <see cref="MachineSessionManager"/>, the same instance the other components
    /// hold) is the only source of truth; <see cref="PrintConsoleProjector"/> folds it
    /// into a <see cref="PrintConsoleState"/>, the form is a view of that state and
    /// this component its canvas projection. Nothing is sent to the machine from a
    /// solve: every command starts in a form button press.</summary>
    public class DuetPrintConsoleComponent : MachineComponentBase
    {
        public DuetPrintConsoleComponent()
            : base("Duet Console", "Console",
                   "Open the printer console without inputs, then set host to connect and supply G-code or load a recent job to print.",
                   "7 Print")
        {
            _projector = NewProjector("");
        }

        bool _loggingSubscribed;
        bool _tornDown;

        /// <summary>Starts following the retention status. Not in the constructor:
        /// Grasshopper builds instances that never enter a document (the per-type
        /// prototype at load, drag and paste previews), which would stay subscribed to a
        /// static event for the life of the process, and the first touch of the service
        /// moves folders, runs recovery and starts its worker. Called from
        /// AddedToDocument and from the first solve, whichever comes first; idempotent,
        /// and TearDown undoes it.</summary>
        void EnsureLoggingSubscribed()
        {
            if (_loggingSubscribed) return;
            var service = LogRetentionService.Instance;
            service.StatusChanged += OnLoggingStatus;
            _loggingSubscribed = true;   // only once the subscription exists, so a throw above is retried
            _projector.Apply(st => st.WithLogging(service.Status));
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            if (_tornDown)
            {
                // The same instance back after TearDown (undo of a delete): its projector
                // was disposed and would ignore every Attach and Apply. A fresh one, with
                // the rebind identity cleared so the next solve binds it to the host again.
                _projector = NewProjector("");
                _projectedKey = "";
                _tornDown = false;
            }
            EnsureLoggingSubscribed();
        }

        /// <summary>Retention status changed (worker thread): fold it into the projection,
        /// which coalesces to the window like every other change.</summary>
        void OnLoggingStatus(LogRetentionStatus status)
        {
            _projector.Apply(st => st.WithLogging(status));
            // A discarded or deleted run must reach the selected item at once, not at the
            // next solve: the window has to say "archive removed" rather than keep
            // offering a reprint of bytes that are gone. On the UI thread, because the
            // selection fields belong to it: a worker write racing Clear selection could
            // bring back a cleared run and stop the solve re-validating the wired G-code.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                if (_tornDown) return;
                RefreshRunSelection();
            }));
        }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0029-4bdb-9c3d-aa0000000029");
        static System.Drawing.Bitmap? _icon;
        // D8: the console inherits the Connect icon - it is now the entry point the
        // Connect component used to be.
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetConnect();

        const double GoToFeedMmS = 20.0;
        const double GoToFeedCeilingMmS = 50.0;
        const int GoToElbow = 1;
        /// <summary>Hard ceiling on the jog feedrate, mm/s - carried over from the Jog
        /// component unchanged.</summary>
        const double JogFeedCeiling = 50.0;
        const string PasswordNote =
            "The password is stored as plain text in the .gh file and sent as plain HTTP on the LAN, so use a trusted network. Intentional spaces are kept.";

        // ---- captured at solve; swapped by reference (worker lambdas read them) ----
        List<string> _gcode = new List<string>();
        ulong _gcodeHash;
        byte[]? _jobBytes;
        ulong _jobBytesHash;
        // ---- Recent prints: a past run loaded from history WINS over the wired input
        // until it is cleared. Everything downstream (gate, upload, start, the gcode
        // output) reads JobLines/JobHash/JobBytes, never _gcode directly. ----
        RecentPrintEntry? _historyRun;
        List<string>? _historyLines;
        byte[]? _historyBytes;
        ulong _historyHash;
        /// <summary>The immutable snapshot a Start would run. Rebuilt when the input or the
        /// history selection changes; published on the `gcode` output through the state.</summary>
        JobSnapshot? _preparedJob;
        string _password = "";
        string _jobName = "print";
        List<Point3d> _points = new List<Point3d>();          // Rhino frame
        IReadOnlyList<string> _pointLabels = Array.Empty<string>();
        bool _live;
        int _pollMs = 1000;
        bool _dryRun;
        protected override RobotControlKind ControlKind => _profile.ControlKind;
        /// <summary>The console owns the profile: its kind wins over whatever the host carried.</summary>
        bool _arcSupport = true;
        bool _blockWarnings;
        double _feed = 20.0;
        int _elbow = 1;
        RobotLimits _lim = new RobotLimits { ReachMin = 235.0 };
        RobotProfile _profile = RobotCatalog.Get("potterbot-scara-v4");
        string? _profileError;
        IReadOnlyList<string> _envelopeUnknown = Array.Empty<string>();
        Core.Duet.MachineFingerprint? _expectedFingerprint;
        DateTime? _lastCmdSentUtc;
        /// <summary>Paused-jog maintenance override: the first refusal arms a 5 s window,
        /// a second discrete press inside it proceeds. Carried over from the Jog component.</summary>
        DateTime _maintenanceArmedUntil = DateTime.MinValue;
        // ---- press-and-hold jog (Core JogRepeatPolicy owns the invariant) ----
        JogRepeatState _jogRepeat = JogRepeatState.Empty;
        double _jogDx, _jogDy, _jogDz;
        DateTime? _jogPressUtc;   // press-to-motion latency, logged behind the diagnostics toggle
        /// <summary>Window renderer preference: false = Skia CPU raster (default),
        /// true = OpenGL, which falls back to CPU if the host cannot paint it (D6).</summary>
        bool _rendererGl;

        // ---- validation cache (by quick hash, same policy key as Duet Print) ----
        JobGateResult? _gate;
        string _gatePolicyKey = "";
        ulong _gateQuickHash;

        // ---- projection + view ----
        PrintConsoleProjector _projector;
        string _projectedKey = "";   // canonical host + transport kind, the rebind identity
        Ui.Console.DuetConsoleForm? _form;
        bool _solving;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code job as text or a list of lines, overridden by a job loaded from Recent prints until cleared", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("host", "H", "Duet hostname or IP address, optionally with :port, required before connecting in the console", "core", PType.Text)
                .HintText("192.168.1.42"),
            new ParamSpec("password", "P", "Duet password (empty = 'reprap'), saved unencrypted in the .gh file and sent over HTTP", "core", PType.Text),
            new ParamSpec("pollMs", "Pm", "Status poll interval in ms, clamped to 250 to 2000, or 250 while the console is focused and the machine is active", "core", PType.Integer)
                .Hint(250, 4000, 1000, 0),
            new ParamSpec("robot", "R", "Robot Definition text for the connected Duet machine (unwired = Potterbot SCARA, Cartesian profiles disable go-to)", "core", PType.Text),
            new ParamSpec("points", "Pt", "Calibration points in Rhino document units for the SCARA go-to selector, used after connecting and homing", "core", PType.Point, GH_ParamAccess.list),
            new ParamSpec("live", "Lv", "Refresh changed visible outputs at most once per second when true (false = refresh on the next solve)", "core", PType.Boolean).HintBool(false),
            new ParamSpec("jobName", "N", "Remote file name, stored as 0:/gcodes/<name>.gcode. Uploading again overwrites it", "job", PType.Text).HintText("print"),
            new ParamSpec("dryRun", "Dr", "Use firmware simulation on Start when true, with no movement", "job", PType.Boolean),
            new ParamSpec("arcSupport", "AS", "Allow G2/G3 arcs in job validation when true, or reject jobs containing arcs when false", "job", PType.Boolean).HintBool(true),
            new ParamSpec("blockWarnings", "Bw", "Block upload and start on validation warnings when true (errors and mast clearance violations always block)", "job", PType.Boolean),
            // limits overrides - same names/hints/defaults as the Jog and Print components
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
            new ParamSpec("elbow", "E", "Fallback arm mode, 0 up or 1 down, for planning before a verified home exists. A verified home's endstop mode takes precedence", "limits", PType.Integer).Hint(0, 1, 1, 0),
            new ParamSpec("feed", "F", "Jog feedrate, mm/s (capped at " +
                JogFeedCeiling.ToString("0", CultureInfo.InvariantCulture) + ")", "jog", PType.Number)
                .Hint(1, JogFeedCeiling, 20, 0),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("job", "Job inputs (name, dry run, arcs, warnings)"),
            ("limits", "Machine limit overrides (L1/L2, reach, Z, envelope, elbow)"),
            ("jog", "Jog feedrate"),
        };

        /// <summary>The console's outputs, by category. Declaration order is the socket
        /// order and is APPEND-ONLY, as it has been since 3.0.0: the 2.6.0 outputs come
        /// first, then 3.0.0's, then the thermals, then `gcode`. Names never change - a
        /// saved document reconnects its wires by name.
        ///
        /// Only <see cref="OutputSpec.Pinned"/> outputs are registered on a new component
        /// (host, status, gcode); every other category is switched on from the context menu
        /// when a definition needs it. Native Grasshopper types throughout: a hidden
        /// category is an absent socket, never a value flattened into text.</summary>
        protected override IReadOnlyList<OutputSpec> OutputSpecs => _outputSpecs;

        static readonly IReadOnlyList<OutputSpec> _outputSpecs = new[]
        {
            new OutputSpec("status", "S", "Console status: the connection, then the pending action or last result",
                           ConsoleOutputCategories.Job, PType.Text).Pin(),
            new OutputSpec("state", "St", "Machine status", ConsoleOutputCategories.Job, PType.Text),
            new OutputSpec("progress", "Pr", "Job progress, 0 to 1", ConsoleOutputCategories.Job, PType.Number),
            new OutputSpec("pos", "P", "Reported nozzle position in Rhino document units, mirrored for Potterbot and unchanged for Cartesian profiles",
                           ConsoleOutputCategories.Motion, PType.Point),
            new OutputSpec("homed", "Hm", "True when all visible axes report homed", ConsoleOutputCategories.Motion, PType.Boolean),
            new OutputSpec("jobFile", "Jf", "Printing file (empty when idle)", ConsoleOutputCategories.Job, PType.Text),
            new OutputSpec("log", "L", "Session log, newest last", ConsoleOutputCategories.Diagnostics, PType.Text, GH_ParamAccess.list),
            new OutputSpec("info", "i", "Connection, machine identity, validation and last-action details", ConsoleOutputCategories.Diagnostics, PType.Text),
            new OutputSpec("host", "H", "Session host to wire into the other Print components",
                           ConsoleOutputCategories.Connection, PType.Text).Pin(),
            new OutputSpec("connected", "C", "True while connected, even if machine identity checks prevent control", ConsoleOutputCategories.Connection, PType.Boolean),
            new OutputSpec("reply", "Rp", "Last firmware reply", ConsoleOutputCategories.Diagnostics, PType.Text),
            new OutputSpec("xHomed", "Hx", "X axis homed", ConsoleOutputCategories.Motion, PType.Boolean),
            new OutputSpec("yHomed", "Hy", "Y axis homed", ConsoleOutputCategories.Motion, PType.Boolean),
            new OutputSpec("zHomed", "Hz", "Z axis homed", ConsoleOutputCategories.Motion, PType.Boolean),
            new OutputSpec("bedOrigin", "Bo", "Captured plate origin in Rhino document units for Print Bed's center input, without changing firmware offsets",
                           ConsoleOutputCategories.Motion, PType.Point),
            new OutputSpec("zZero", "Z0", "Legacy captured Z in Rhino document units from saved documents, with no new captures", ConsoleOutputCategories.Motion, PType.Number),
            new OutputSpec("uploaded", "Up", "Remote path of the last verified upload (empty when none)",
                           ConsoleOutputCategories.Job, PType.Text),
            new OutputSpec("danger", "!", "True when validation refuses the job", ConsoleOutputCategories.Validation, PType.Boolean),
            new OutputSpec("problems", "Pb", "Validation warning and error positions in Rhino document units",
                           ConsoleOutputCategories.Validation, PType.Point, GH_ParamAccess.list),
            new OutputSpec("messages", "M", "Validation messages", ConsoleOutputCategories.Validation, PType.Text, GH_ParamAccess.list),
            new OutputSpec("speedFactor", "Sf", "Speed override, percent (-1 = not reported)", ConsoleOutputCategories.Overrides, PType.Number),
            new OutputSpec("flowFactor", "Ff", "Flow override, percent (-1 = not reported)", ConsoleOutputCategories.Overrides, PType.Number),
            new OutputSpec("gcode", "G", "Running or prepared G-code lines, including Recent prints selections (empty for jobs started outside this session)",
                           ConsoleOutputCategories.Job, PType.Text, GH_ParamAccess.list).Pin(),
        };

        protected override IReadOnlyList<(string, string)> OutputToggleGroups => new[]
        {
            (ConsoleOutputCategories.Connection, "Connection outputs (host, connected)"),
            (ConsoleOutputCategories.Job, "Job outputs (status, state, progress, jobFile, uploaded, gcode)"),
            (ConsoleOutputCategories.Motion, "Motion outputs (pos, homed, xHomed, yHomed, zHomed, bedOrigin, zZero)"),
            (ConsoleOutputCategories.Validation, "Validation outputs (danger, problems, messages)"),
            (ConsoleOutputCategories.Overrides, "Override outputs (speed, flow)"),
            (ConsoleOutputCategories.Diagnostics, "Log and diagnostic outputs (log, info, reply)"),
        };

        /// <summary>1: the 27 fixed outputs became categories with three pinned ones. An
        /// archive below this is reconciled once after the document loads - every category
        /// with a wired socket stays visible, so no existing wire is cut.</summary>
        protected override int OutputContractVersion => 1;

        // D7: the `open` socket is gone. One capsule opens the window; pressing it again
        // fronts an already-open one (Activate()), which is what ShowConsole does.
        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows => new[]
        {
            new[] { new ButtonDef("console", "Console", GH_Palette.Black, true, "Open or bring forward the console window, with no wired inputs required") },
        };

        public override void OnCapsulePressed(string id)
        {
            if (id == "console") ShowConsole();
        }

        bool Cartesian => _profileError == null && _profile.Mechanism == RobotMechanism.Cartesian;
        RobotMechanism Mechanism => Cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara;
        bool GoToSupported => _profileError == null && _profile.Mechanism == RobotMechanism.Scara;
        bool FormVisible => _form != null && !_form.IsDisposed && _form.Visible;

        // ---- solve ---------------------------------------------------------------------

        protected override object? Compute(IGH_DataAccess DA)
        {
            _solving = true;
            try
            {
                EnsureLoggingSubscribed();
                var incoming = GetStringList(DA, "gcode");
                ulong hash = ContentHash.Fnv1aLines(incoming);
                Func<PrintConsoleState, PrintConsoleState>? solveStep = null;
                bool inputChanged = hash != _gcodeHash;
                bool prepareInput = false;
                if (inputChanged)
                {
                    _gcode = incoming;
                    _gcodeHash = hash;
                    _jobBytes = null;
                    if (_historyRun == null)
                    {
                        _gate = null;
                        _gatePolicyKey = "";
                        _gateQuickHash = 0;   // a validation in flight must not adopt the new job
                        int lines = incoming.Count;
                        solveStep = s => PrintConsoleReducer.GcodeChanged(s, lines, hash);
                        prepareInput = true;
                    }
                    else
                    {
                        // The history selection wins until it is cleared, and says so - the
                        // alternative is a Start that silently runs something else than the
                        // Validate card describes.
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "The run '" + _historyRun.JobName + "' is loaded from Recent prints, so the wired G-code is "
                            + "not what Start would run. Clear the selection on the Job page to use it");
                    }
                }

                string host = GetString(DA, "host", "").Trim();
                HostInput = host;
                _password = GetString(DA, "password", "");
                _jobName = GetString(DA, "jobName", "print");
                _pollMs = GetInt(DA, "pollMs", 1000);
                // The interval lives on the shared session (last writer wins, as the hidden
                // Connect component always did); without this line the input was inert.
                var polled = _projector.Session;
                if (polled != null) polled.PollIntervalMs = _pollMs;
                _dryRun = GetBool(DA, "dryRun", false);
                _arcSupport = GetBool(DA, "arcSupport", true);
                _blockWarnings = GetBool(DA, "blockWarnings", false);
                _feed = GetNumber(DA, "feed", 20.0);
                _live = GetBool(DA, "live", false);

                // _profile before _profileError: a worker racing this solve must never
                // see a cleared error paired with a not-yet-updated profile
                var profile = ResolveControlProfile(GetString(DA, "robot", ""), out string? profileError);
                if (profile != null) _profile = profile;
                _profileError = profileError;
                if (_profileError != null)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _profileError);

                ReadPoints(DA);

                // host rebind: the projection (and its host-bound upload record) belongs
                // to one canonical host; a different host starts from an empty state
                string canonical = MachineSessionManager.CanonicalHost(host);
                if (!string.Equals(canonical, _projectedKey, StringComparison.Ordinal))
                {
                    CancelJogHold();   // a held jog must not resume on the newly bound machine
                    _projector.Session?.ClearFocusOwner(this);
                    ReleaseLease();
                    _projector.Dispose();
                    _projector = NewProjector(host);
                    _projectedKey = canonical;
                }

                ApplySubscriptionRule();
                BuildLimits(DA);

                _projector.Live = _live;
                // Only the outputs that actually exist take part in the change decision and
                // in the projection: with Motion and Temperatures hidden, a position poll
                // never expires this component at all.
                _projector.Visible = OutputVisibility.Of(VisibleOutputNames());
                if (prepareInput || _preparedJob == null && JobLines.Count > 0) PrepareJob();
                // A session opened after the job was prepared has an empty tracker.
                var tracker = _projector.Session;
                if (tracker != null && tracker.Jobs.Prepared == null && _preparedJob != null)
                    tracker.Jobs.Prepare(_preparedJob);
                RefreshRunSelection();
                _projector.Refresh(solveStep);

                var session = _projector.Session;
                if (session != null)
                {
                    if (session.State == DuetConnState.Connected)
                    {
                        AdoptSessionFingerprint(session);
                        if (_profileError == null) ReportFirmwareFindings(_lim, _profile.Mechanism);
                    }
                    if (session.ExpectationConflict is string conflict)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, conflict);
                }
                if (Cartesian && _envelopeUnknown.Count > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "Upload and go-to are unavailable while the machine envelope is unknown (" + string.Join(", ", _envelopeUnknown)
                        + "). Wait for the firmware's M208 report");

                // The buttons must see the same planning blocks the transaction enforces,
                // and the Start label must name the mode Start will really use.
                string? profileErr = _profileError;
                var envelopeUnknown = _envelopeUnknown;
                _projector.Apply(st => st.WithPlanningFacts(profileErr, envelopeUnknown).WithDryRun(_dryRun));
                // The archive of the next run records the machine and profile it ran on.
                _projector.Session?.SetJobArchiveContext(CurrentMachineContext(), _dryRun);

                ProjectValidation();
                var state = _projector.State;
                if (OutputShown(ConsoleOutputNames.Gcode) && state.ActiveJob != null && !state.ActiveJob.IntendedPathAvailable)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "This print was started outside this session, so its exact G-code is unknown. The gcode output is "
                        + "empty rather than the job wired here");
                return PrintConsoleOutputs.From(state, _projector.Visible);
            }
            finally { _solving = false; }
        }

        /// <summary>Emission by NAME, never by index: output categories move the sockets,
        /// so an index written here would publish a temperature into a point socket the
        /// first time somebody hid Motion. A hidden output is simply not set.</summary>
        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var o = (OutputSnapshot)result;
            // Core holds the machine frame; the Rhino-frame and document-unit conversions
            // happen here, at the component boundary.
            double scale = DocUnits.ScaleToMm();

            SetOut(DA, ConsoleOutputNames.Status, o.Status);
            SetOut(DA, ConsoleOutputNames.State, o.State);
            SetOut(DA, ConsoleOutputNames.Progress, o.Progress);
            SetOut(DA, ConsoleOutputNames.JobFile, o.JobFile);
            SetOut(DA, ConsoleOutputNames.Uploaded, o.Uploaded);
            SetOutList(DA, ConsoleOutputNames.Gcode, o.Gcode);

            SetOut(DA, ConsoleOutputNames.Host, o.HostOut);
            SetOut(DA, ConsoleOutputNames.Connected, o.Connected);

            if (o.Pos.HasValue) SetOut(DA, ConsoleOutputNames.Pos, DocUnits.FromMm(ToRhinoPoint(o.Pos.Value), scale));
            SetOut(DA, ConsoleOutputNames.Homed, o.Homed);
            SetOut(DA, ConsoleOutputNames.XHomed, o.XHomed);
            SetOut(DA, ConsoleOutputNames.YHomed, o.YHomed);
            SetOut(DA, ConsoleOutputNames.ZHomed, o.ZHomed);
            if (o.BedOrigin.HasValue) SetOut(DA, ConsoleOutputNames.BedOrigin, DocUnits.FromMm(ToRhinoPoint(o.BedOrigin.Value), scale));
            // zZero is a length, not a point: scale it the same way FromMm scales a point.
            if (o.ZZero.HasValue)
                SetOut(DA, ConsoleOutputNames.ZZero, DocUnits.IsUnity(scale) ? o.ZZero.Value : o.ZZero.Value / scale);

            SetOut(DA, ConsoleOutputNames.Danger, o.Danger);
            if (OutputShown(ConsoleOutputNames.Problems))
            {
                var problems = new List<Point3d>(o.Problems.Count);
                for (int i = 0; i < o.Problems.Count; i++)
                    problems.Add(DocUnits.FromMm(ToRhinoPoint(o.Problems[i]), scale));
                SetOutList(DA, ConsoleOutputNames.Problems, problems);
            }
            SetOutList(DA, ConsoleOutputNames.Messages, o.Messages);

            SetOut(DA, ConsoleOutputNames.SpeedFactor, Reported(o.SpeedFactor));
            SetOut(DA, ConsoleOutputNames.FlowFactor, Reported(o.FlowFactor));

            SetOutList(DA, ConsoleOutputNames.Log, o.Log);
            SetOut(DA, ConsoleOutputNames.Info, o.Info);
            SetOut(DA, ConsoleOutputNames.Reply, o.Reply);
        }

        /// <summary>Not-reported temperatures keep publishing -1, as they always have.</summary>
        static double Reported(double value) => double.IsNaN(value) ? -1 : value;

        void ReadPoints(IGH_DataAccess DA)
        {
            var points = new List<Point3d>();
            int i = Params.IndexOfInputParam("points");
            if (i >= 0) DA.GetDataList(i, points);
            // document units -> millimetres at the boundary (AUD-COR-001): go-to hands
            // these to JogPlanner as mm. Identity on a millimetre document.
            double scale = DocUnits.ScaleToMm();
            if (!DocUnits.IsUnity(scale))
                for (int k = 0; k < points.Count; k++)
                    points[k] = new Point3d(points[k].X * scale, points[k].Y * scale, points[k].Z * scale);
            var labels = new string[points.Count];
            var inv = CultureInfo.InvariantCulture;
            for (int k = 0; k < points.Count; k++)
            {
                var p = points[k];
                labels[k] = k.ToString(inv) + ": (" + p.X.ToString("0.#", inv) + ", "
                          + p.Y.ToString("0.#", inv) + ", " + p.Z.ToString("0.#", inv) + ")";
            }
            if (!SamePoints(_points, points))
            {
                // A replaced list must not keep a selection that now names a different
                // target: the index is dropped and the operator picks again.
                _form?.ClearPointSelection();
            }
            _points = points;
            _pointLabels = labels;
        }

        static bool SamePoints(List<Point3d> a, List<Point3d> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].X.Equals(b[i].X) || !a[i].Y.Equals(b[i].Y) || !a[i].Z.Equals(b[i].Z)) return false;
            return true;
        }

        /// <summary>The limits both jog planning and job validation run against. 3.0.0
        /// carries the Duet Print wired-override path: SCARA = shipped Potterbot literals
        /// (overridable through the limits group) plus the calibrated home-pose contract;
        /// Cartesian = wired inputs first, then the firmware's M208 envelope from the live
        /// snapshot, and axes with neither are listed in <c>_envelopeUnknown</c> so
        /// validation and go-to fail CLOSED rather than guessing an envelope.</summary>
        void BuildLimits(IGH_DataAccess DA)
        {
            var lim = new RobotLimits { ReachMin = 235.0 };
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
            lim.CalibratedHomePose = Lugworm.Robot.RobotModel.Default?.Def.CalibratedHomePose;
            lim.HomedArmMode = Lugworm.Robot.RobotModel.Default?.Def.HomedArmMode ?? lim.HomedArmMode;
            lim.RequireCalibratedHomePose = true;
            var envelopeUnknown = new List<string>();
            if (Cartesian)
            {
                // A generic Duet machine has no calibrated SCARA home pose and must not
                // inherit the Potterbot's envelope literals. Wired limit inputs win;
                // unwired ones defer to the firmware's M208 report; axes with neither
                // block validation (fail closed, reported at the gate).
                lim.CalibratedHomePose = null;
                lim.RequireCalibratedHomePose = false;
                var live = _projector.Session?.Latest;
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
                // ZMin: no wired input, deliberately not in the envelope-unknown list -
                // 0 is the conservative bound until the firmware reports one.
                var zAxis = live?.Axis("Z");
                lim.ZMin = zAxis != null && zAxis.Max > zAxis.Min ? zAxis.Min : 0.0;
            }
            _envelopeUnknown = envelopeUnknown;
            _lim = lim;
            _elbow = GetInt(DA, "elbow", 1);
        }

        /// <summary>Machine-frame point -> the point the operator sees (Rhino frame);
        /// identity for a Cartesian profile - same rule as Live View.</summary>
        Point3d ToRhinoPoint(Vec3 machine)
        {
            if (Cartesian) return new Point3d(machine.X, machine.Y, machine.Z);
            MachineFrame.ToRhino(machine.X, machine.Y, out double x, out double y);
            return new Point3d(x, y, machine.Z);
        }

        // ---- projection lifecycle ---------------------------------------------------------

        PrintConsoleProjector NewProjector(string host)
        {
            var initial = PrintConsoleReducer.GcodeChanged(PrintConsoleState.Empty(host), _gcode.Count, _gcodeHash);
            var projector = new PrintConsoleProjector(initial);
            projector.StateChanged += PostToForm;
            projector.ExpireRequested += OnExpireRequested;
            projector.PollCompleted += OnPollCompleted;
            return projector;
        }

        void OnExpireRequested()
        {
            // the solve path adopts the state it is about to emit - a request that lands
            // inside it would only re-solve the same outputs
            if (_solving) return;
            if (OnPingDocument() != null && !Locked) ExpireSolution(true);
        }

        /// <summary>Attaches to a session that exists for the host when the form is visible
        /// or `live` is on. Peek, never acquire, so a solve cannot start a session; the lease
        /// is taken only for a session somebody else already opened (or Connect opened).
        /// Once taken, the lease is kept while the window is hidden, `live` on or off: it
        /// is released only on removal, document close, a host change and Disconnect. The
        /// last lease going stops the session, and closing the window must not end the job
        /// log of a running print or leave the E-STOP without a target.</summary>
        void ApplySubscriptionRule()
        {
            bool wanted = FormVisible || _live;
            var existing = HostInput.Length > 0 ? MachineSessionManager.Peek(HostInput) : null;
            var attached = _projector.Session;
            if (wanted && existing != null)
            {
                if (!ReferenceEquals(attached, existing))
                    AttachSession(LeaseSession(HostInput, _password, _expectedFingerprint));
            }
            else if (attached != null && !ReferenceEquals(attached, existing))
            {
                // the session was closed (Disconnect) - drop the dead subscription
                _projector.Detach();
                ReleaseLease();
            }
        }

        /// <summary>Attaches the projection to a leased session. A window that already
        /// has focus registers as focus owner here: Activated fired before there was a
        /// session to register with, so the fast poll rate would wait for the next
        /// focus change.</summary>
        void AttachSession(MachineSession session)
        {
            session.PollIntervalMs = _pollMs;
            _projector.Attach(session);
            if (_form != null && !_form.IsDisposed && _form.ContainsFocus) session.RegisterFocusOwner(this);
        }

        void OnFormHidden()
        {
            CancelJogHold();
            _projector.Session?.ClearFocusOwner(this);
        }

        void OnFormActivated(object? sender, EventArgs e) => _projector.Session?.RegisterFocusOwner(this);

        void OnFormDeactivated(object? sender, EventArgs e)
        {
            CancelJogHold();
            _projector.Session?.ClearFocusOwner(this);
        }

        /// <summary>Ends a held jog on the component side, independently of the form's own
        /// release path: hiding, deactivating or tearing down the window must leave the
        /// repeat policy unable to send another step even if the form's mouse-up never
        /// arrives. Idempotent; a step already in flight completes and reports as usual,
        /// and a fresh physical press is needed to re-arm.</summary>
        void CancelJogHold() => _jogRepeat = JogRepeatPolicy.Release(_jogRepeat);

        void ShowConsole()
        {
            if (_form == null || _form.IsDisposed)
            {
                bool reducedMotion = false;
                // best effort: an accessibility preference read while opening the window.
                // The service records an unreadable settings file as a warning on its own
                // status, which this console shows; the default (motion on) stands in.
                try { reducedMotion = LogRetentionService.Instance.Settings.ReducedMotion; } catch (Exception) { }
                _form = new Ui.Console.DuetConsoleForm(HandleConsoleAction, OnFormHidden, _rendererGl, reducedMotion);
                // Owner-keyed focus: while this window has focus the session polls fast,
                // and two consoles on one host cannot blur each other's cadence off.
                _form.Activated += OnFormActivated;
                _form.Deactivate += OnFormDeactivated;
                _form.Show();
            }
            else
            {
                if (!_form.Visible) _form.Show();
                _form.Activate();
            }
            // The rule is otherwise only evaluated at solve, and the capsule press does not
            // solve: without this a session another lease keeps alive (a running print)
            // shows as None, with Connect offered and the E-STOP disabled.
            ApplySubscriptionRule();
            PostToForm(_projector.State);
        }

        /// <summary>StateChanged sink - any thread; the form's Post coalesces.</summary>
        /// <summary>The persisted expectation follows the session's (first-connection
        /// baseline or the operator's explicit trust), never the other way - and never while
        /// the session reports an expectation conflict: adopting then would silently converge
        /// two documents' identities and dissolve the conflict without the operator (the same
        /// rule as Duet Connect). Called from the solve AND from every projected state change,
        /// because with `live` off a first connection made from the window never re-solves
        /// the component, and a definition saved before the next solve would persist no
        /// identity at all.</summary>
        void AdoptSessionFingerprint(MachineSession session)
        {
            if (session.State != DuetConnState.Connected || session.ExpectationConflict != null) return;
            var recorded = session.ExpectedFingerprint;
            if (recorded != null && !recorded.Matches(_expectedFingerprint))
                _expectedFingerprint = recorded;
        }

        void PostToForm(PrintConsoleState state)
        {
            var attached = _projector.Session;
            if (attached != null) AdoptSessionFingerprint(attached);
            var form = _form;
            if (form == null || form.IsDisposed) return;
            var points = _points;
            int selected = form.SelectedPoint;
            bool hasPoint = selected >= 0 && selected < points.Count;
            var buttons = PrintConsoleButtons.Enabled(state, HostInput.Length > 0, JobLines.Count > 0, hasPoint, GoToSupported,
                                                      armConfigurationRequired: !Cartesian);
            // The mechanism travels with the state so a page can say which way a machine
            // axis moves in the document. An unresolved profile means the frame is NOT
            // known - the pages then claim no mapping rather than assume the identity.
            form.Post(state, buttons, _pointLabels, Cartesian, frameKnown: _profileError == null);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            TearDown();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) TearDown();
            base.DocumentContextChanged(document, context);
        }

        void TearDown()
        {
            _tornDown = true;
            if (_loggingSubscribed)
            {
                LogRetentionService.Instance.StatusChanged -= OnLoggingStatus;
                _loggingSubscribed = false;
            }
            CancelJogHold();
            // Drop the focus registration before the projector goes: a stale owner would
            // otherwise hold the session at the fast cadence forever.
            _projector?.Session?.ClearFocusOwner(this);
            _projector?.Dispose();
            var form = _form;
            _form = null;
            if (form != null && !form.IsDisposed) form.Dispose();
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);

            // Absorbed from the Duet Connect component, unchanged.
            var trustSession = _projector.Session;
            bool canTrust = trustSession != null && trustSession.CurrentFingerprint != null
                            && (trustSession.Trust == Core.Duet.MachineTrust.Mismatch
                                || trustSession.Trust == Core.Duet.MachineTrust.Unavailable
                                || trustSession.Trust == Core.Duet.MachineTrust.Unknown
                                || trustSession.ExpectationConflict != null);
            Menu_AppendItem(menu, "Trust connected machine", (s, e) =>
            {
                var sess = _projector.Session;
                if (sess?.CurrentFingerprint == null) return;
                RecordUndoEvent("Trust connected machine");
                sess.TrustConnectedMachine();
                _expectedFingerprint = sess.ExpectedFingerprint;
                ExpireFromUi();
            }, canTrust);

            Menu_AppendItem(menu, "Dump raw object model (fixture capture)", (s, e) =>
            {
                // Read-only diagnostic: the non-state-changing path keeps it usable while
                // machine trust is diagnostic-only, which is the Terminal component parity.
                RunAction("dumping model", async (client, ct) =>
                {
                    string json = await client.GetModelRawAsync("", "d99fn", ct);
                    _projector.Session?.AppendLog("rr_model dump (" + json.Length + " chars): " + json);
                    return "dumped " + json.Length + " chars to the session log";
                }, stateChanging: false);
            }, _projector.Session != null);

            Menu_AppendSeparator(menu);
            // D6: CPU raster is the default. In-process OpenGL inside Rhino was never
            // verified, so it stays an opt-in the operator turns on knowingly; the window
            // falls back to CPU by itself if the host cannot paint it.
            var gl = Menu_AppendItem(menu, "Render the console window with OpenGL", (s, e) =>
            {
                RecordUndoEvent("Console renderer");
                _rendererGl = !_rendererGl;
                if (_form != null && !_form.IsDisposed)
                    _projector.Session?.AppendLog("console: renderer preference changed. Reopen the window to apply");
                ExpireFromUi();
            }, true, _rendererGl);
            gl.ToolTipText = "Off = Skia CPU raster (default, always available). "
                           + "On = OpenGL, which falls back to CPU if this host cannot paint it. "
                           + "Takes effect the next time the window is opened.";

            bool reduced = false;
            // best effort: the same preference, read to tick the context-menu item. As
            // above, an unreadable settings file is reported on the retention status line
            // and a context menu must open regardless.
            try { reduced = LogRetentionService.Instance.Settings.ReducedMotion; } catch (Exception) { }
            var motion = Menu_AppendItem(menu, "Reduce console motion", (s, e) =>
            {
                bool next = !reduced;
                LogRetentionService.Instance.SetReducedMotion(next);
                if (_form != null && !_form.IsDisposed) _form.SetReducedMotion(next);
            }, true, reduced);
            motion.ToolTipText = "Removes hover, press and page transitions in the console window, so state changes are immediate. "
                               + "A preference for this user on this machine. It never delays a command either way.";
        }

        // ---- persistence: the expected machine identity, never a connected flag ----

        public override bool Write(GH_IWriter writer)
        {
            if (!base.Write(writer)) return false;
            writer.SetString("expectedMachineFingerprint", _expectedFingerprint?.Serialize() ?? "");

            // 3.0.0: the selections and captures absorbed from Jog, plus the upload
            // bookkeeping absorbed from Print. The connection itself is still never
            // persisted - reopening a definition must not touch the machine.
            var st = _projector.State;
            writer.SetDouble("iamJogStep", st.Step);
            if (st.BedOrigin.HasValue)
            {
                writer.SetDouble("iamBedOx", st.BedOrigin.Value.X);
                writer.SetDouble("iamBedOy", st.BedOrigin.Value.Y);
                writer.SetDouble("iamBedOz", st.BedOrigin.Value.Z);
            }
            if (st.ZZero.HasValue) writer.SetDouble("iamZZero", st.ZZero.Value);
            if (st.Uploaded != null)
            {
                writer.SetString("iamUploadPath", st.Uploaded.Path);
                writer.SetString("iamUploadHash", st.Uploaded.Hash.ToString(CultureInfo.InvariantCulture));
                writer.SetInt64("iamUploadSize", st.Uploaded.Size);
                writer.SetInt64("iamUploadGen", st.Uploaded.Generation);
            }
            writer.SetBoolean("iamConsoleGl", _rendererGl);
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            if (!base.Read(reader)) return false;
            string s = "";
            reader.TryGetString("expectedMachineFingerprint", ref s);
            _expectedFingerprint = Core.Duet.MachineFingerprint.TryParse(s);

            double step = 0;
            if (reader.TryGetDouble("iamJogStep", ref step))
                _projector.Apply(st => PrintConsoleReducer.StepChanged(st, step));

            double ox = 0, oy = 0, oz = 0;
            if (reader.TryGetDouble("iamBedOx", ref ox) && reader.TryGetDouble("iamBedOy", ref oy)
                && reader.TryGetDouble("iamBedOz", ref oz))
            {
                var origin = new Vec3(ox, oy, oz);
                _projector.Apply(st => PrintConsoleReducer.BedOriginCaptured(st, origin));
            }
            double zz = 0;
            if (reader.TryGetDouble("iamZZero", ref zz))
                _projector.Apply(st => PrintConsoleReducer.ZZeroCaptured(st, zz));

            string path = "", hashText = "";
            long size = 0, generation = 0;
            if (reader.TryGetString("iamUploadPath", ref path) && path.Length > 0
                && reader.TryGetString("iamUploadHash", ref hashText)
                && ulong.TryParse(hashText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong hash))
            {
                reader.TryGetInt64("iamUploadSize", ref size);
                reader.TryGetInt64("iamUploadGen", ref generation);
                // Print semantics: Start re-verifies the remote content hash AND the
                // stored size on every run, so a restored record is never trusted on its
                // own - what is on the SD card may have changed while the document was
                // closed.
                var record = new UploadRecord(path, hash, size, generation);
                _projector.Apply(st => PrintConsoleReducer.Uploaded(st, record));
            }

            bool gl = false;
            if (reader.TryGetBoolean("iamConsoleGl", ref gl)) _rendererGl = gl;
            return true;
        }

        // ---- form actions (UI thread) --------------------------------------------------------

        void HandleConsoleAction(string id)
        {
            // Parameterised actions: the form carries only the payload, never the policy.
            if (id.StartsWith("send:", StringComparison.Ordinal))
            {
                TerminalSend(id.Substring(5));
                return;
            }
            if (id.StartsWith("step:", StringComparison.Ordinal))
            {
                if (double.TryParse(id.Substring(5), NumberStyles.Float, CultureInfo.InvariantCulture, out double step))
                    _projector.Apply(st => PrintConsoleReducer.StepChanged(st, step));
                return;
            }
            if (id.StartsWith("jogdown:", StringComparison.Ordinal))
            {
                BeginJogHold(id.Substring(8));
                return;
            }
            if (id == "jogup")
            {
                _jogRepeat = JogRepeatPolicy.Release(_jogRepeat);
                return;
            }
            if (id == "terminalchanged")
            {
                // Editing the line drops any armed confirmation, so an operator cannot arm
                // on one command and confirm a different one.
                _projector.Apply(PrintConsoleReducer.TerminalDisarmed);
                return;
            }
            if (id.StartsWith("log:", StringComparison.Ordinal))
            {
                LoggingAction(id.Substring(4));
                return;
            }
            // Recent prints. Selecting LOADS a past run; it never starts one, and the
            // active print keeps its own snapshot throughout.
            if (id.StartsWith("history:select:", StringComparison.Ordinal))
            {
                SelectRun(id.Substring(15));
                return;
            }
            if (id == "history:clear")
            {
                ClearRunSelection();
                return;
            }

            switch (id)
            {
                case "connect": Connect(); return;
                case "home": Home("G28"); return;
                case "homex": Home("G28 X"); return;
                case "homey": Home("G28 Y"); return;
                case "homez": Home("G28 Z"); return;
                case "validate": Validate(); return;
                case "upload": Upload(); return;
                case "start": Start(); return;
                case "pause":
                    JobControl("pausing", "pause", snap => snap.Status == MachineStatus.Processing || snap.Status == MachineStatus.Simulating,
                               async (session, client, ct) =>
                               {
                                   session.MarkInitiatedMotion();   // pause.g lifts the head
                                   await client.PauseAsync(ct).ConfigureAwait(false);
                                   return "pause accepted (M25)";
                               });
                    return;
                case "resume":
                    JobControl("resuming", "resume", snap => snap.Status == MachineStatus.Paused,
                               async (session, client, ct) =>
                               {
                                   session.MarkInitiatedMotion();   // resume.g re-primes and returns
                                   await client.ResumeAsync(ct).ConfigureAwait(false);
                                   return "resume accepted (M24)";
                               });
                    return;
                case "stop":
                    JobControl("cancelling", "cancel", snap =>
                                   snap.Status == MachineStatus.Processing || snap.Status == MachineStatus.Simulating
                                   || snap.Status == MachineStatus.Paused || snap.Status == MachineStatus.Pausing
                                   || snap.Status == MachineStatus.Resuming,
                               async (session, client, ct) =>
                               {
                                   session.MarkInitiatedMotion();   // cancel.g homes
                                   await client.CancelAsync(ct).ConfigureAwait(false);
                                   session.ClearInitiatedJob();
                                   return "cancel accepted (M25+M0)";
                               });
                    return;
                case "estop": EmergencyStop(); return;
                case "reset": Reset(); return;
                case "goto": GoTo(); return;
                case "gotostart": GoToStart(); return;
                case "clearlog": ClearLog(); return;
                case "disconnect": Disconnect(); return;
                case "setbedorigin": CaptureOrigin(); return;
                case "jogxminus": Jog(-1, 0, 0); return;
                case "jogxplus": Jog(1, 0, 0); return;
                case "jogyminus": Jog(0, -1, 0); return;
                case "jogyplus": Jog(0, 1, 0); return;
                case "jogzminus": Jog(0, 0, -1); return;
                case "jogzplus": Jog(0, 0, 1); return;
            }
        }

        /// <summary>Logging workspace actions. Retention is a separate subsystem from the
        /// machine: nothing here touches a session, and a logging failure never blocks a
        /// machine control - it is reported through the same status line.</summary>
        void LoggingAction(string action)
        {
            var service = LogRetentionService.Instance;
            var projector = _projector;
            void Report(string result) => projector.Apply(s => PrintConsoleReducer.ActionFinished(s, result, null));
            void Refuse(string error) => projector.Apply(s => PrintConsoleReducer.ActionFinished(s, null, error));
            try
            {
                if (action == "keepall:on")
                {
                    if (service.SetKeepAll(true)) Report("keeping all job logs at " + (service.Status.Destination ?? "?"));
                    else Refuse("choose a destination folder first");
                }
                else if (action == "keepall:off")
                {
                    service.SetKeepAll(false);
                    Report("new jobs record temporarily. Save a job to keep it");
                }
                else if (action.StartsWith("destination:", StringComparison.Ordinal))
                {
                    string folder = action.Substring(12);
                    service.SetDestination(folder);
                    Report("job logs go to " + folder);
                }
                else if (action.StartsWith("save:", StringComparison.Ordinal))
                {
                    string id = action.Substring(5);
                    if (id.Length == 0) Refuse("no job selected");
                    else if (service.SaveJob(id)) Report("saving job " + id);
                    else Refuse("cannot save job " + id + ". Choose a destination folder first, or the job is already kept or faulted");
                }
                else if (action == "retry")
                {
                    service.Retry();
                    Report("retrying synchronisation");
                }
                else if (action.StartsWith("exportto:", StringComparison.Ordinal))
                {
                    string payload = action.Substring(9);
                    int bar = payload.IndexOf('|');
                    if (bar <= 0) { Refuse("export: no target"); return; }
                    string id = payload.Substring(0, bar);
                    string target = payload.Substring(bar + 1);
                    projector.Apply(s => PrintConsoleReducer.ActionStarted(s, "exporting " + id));
                    Task.Run(() =>
                    {
                        try { service.ExportCopy(id, target); Finish(projector, "exported " + target, null); }
                        catch (Exception ex) { Finish(projector, null, "export failed: " + ex.Message); }
                    });
                }
                else if (action.StartsWith("discardconfirmed:", StringComparison.Ordinal))
                {
                    string id = action.Substring(17);
                    if (service.Discard(id)) Report("discarded the buffer of job " + id);
                    else Refuse("cannot discard job " + id + " (active, or the buffer could not be removed)");
                }
            }
            catch (Exception ex) { Refuse("logging: " + ex.Message); }
        }

        static void Outcome(Action<bool, string?>? onOutcome, bool ok, string? error)
        {
            if (onOutcome == null) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => onOutcome(ok, error)));
        }

        void Fail(string error)
            => _projector.Apply(s => PrintConsoleReducer.ActionFinished(s, null, error));

        /// <summary>The attached session for a form action; records the refusal otherwise.</summary>
        bool TryGetSession(out MachineSession session)
        {
            var attached = _projector.Session;
            if (attached == null)
            {
                Fail("not connected. Press Connect");
                session = null!;
                return false;
            }
            session = attached;
            return true;
        }

        /// <summary>Completion lands in the projector the action started in: a host rebind
        /// during a pending command must not write the old host's result into the new
        /// host's state (Apply on the disposed projector is a no-op). Finishing never
        /// releases the lease; see <see cref="ApplySubscriptionRule"/>.</summary>
        /// <param name="epoch">The action epoch this completion belongs to, or null for a
        /// caller with no action identity. A completion an emergency stop (or a newer
        /// action) superseded is dropped: it must not clear the newer action's pending
        /// state or overwrite its result.</param>
        void Finish(PrintConsoleProjector projector, string? result, string? error, bool warned = false,
                    long? epoch = null)
            => projector.Apply(s => PrintConsoleReducer.ActionFinished(s, result, error, warned, epoch));

        /// <summary>Every command: pending window, the session's serialized gated
        /// transaction, then the result or the flattened refusal into the state.</summary>
        /// <param name="warned">Read AFTER the body completes: true when the action
        /// produced motion-limit warnings, so a successful-but-warned run renders as a
        /// warning rather than a remark.</param>
        /// <param name="onOutcome">Marshalled to the UI thread after the transaction ends:
        /// (succeeded, error). Used by the jog repeat, whose policy must see refusals.</param>
        void Dispatch(string label, bool includeJob,
                      Func<ObjectModelSnapshot, IMachineClient, CancellationToken, Task<string>> body,
                      Func<bool>? warned = null, Action<bool, string?>? onOutcome = null)
        {
            if (!TryGetSession(out var session)) return;
            var projector = _projector;
            // Keep the epoch this action started under: an emergency stop opens a new one,
            // and a completion carrying the old epoch is then recognised as superseded.
            long epoch = projector.Apply(s => PrintConsoleReducer.ActionStarted(s, label)).ActionEpoch;
            Task.Run(async () =>
            {
                try
                {
                    string reply = await session.RunGatedCommandAsync(includeJob, body).ConfigureAwait(false);
                    Finish(projector, reply, null, warned != null && warned(), epoch);
                    Outcome(onOutcome, true, null);
                }
                catch (Exception ex)
                {
                    string msg = MachineSession.Flatten(ex);
                    session.AppendLog("console: " + label + " failed: " + msg);
                    Finish(projector, null, msg, false, epoch);
                    Outcome(onOutcome, false, msg);
                }
            });
        }

        void Connect()
        {
            if (_profileError != null) { Fail(_profileError); return; }
            string host = HostInput;
            if (host.Length == 0)
            {
                Fail("set the host input first");
                return;
            }
            AttachSession(LeaseSession(host, _password, _expectedFingerprint));
            _projector.Apply(s => PrintConsoleReducer.ActionFinished(
                PrintConsoleReducer.ActionStarted(s, "connect"), "connect requested for " + host, null));
        }

        void Home(string gcode)
        {
            if (!TryGetSession(out var session)) return;
            string? preflight = DuetHoming.PreflightRefusal(session, gcode);
            if (preflight != null) { Fail(preflight); return; }
            var lim = _lim;
            bool cartesian = Cartesian;
            string? profileError = _profileError;
            _lastCmdSentUtc = DateTime.UtcNow;
            var notBefore = _lastCmdSentUtc;
            string axis = gcode == "G28" ? "all axes" : gcode.Substring(4).Trim();
            Dispatch("homing " + axis, false, (snap, client, ct) =>
                DuetHoming.HomeAsync(session, snap, client, gcode, lim, cartesian, profileError, notBefore, ct));
        }

        /// <summary>The Validate button: the job gate on the wired gcode, locally. JobGate
        /// is pure, so this needs neither a session nor machine trust and works offline;
        /// only Upload and Start take the transaction gates. The verdict lands in the
        /// state like the solve-time validation, so the Job tab shows the same thing.</summary>
        void Validate()
        {
            EnsureGateAsync(gate =>
            {
                var projector = _projector;
                projector.Apply(s => PrintConsoleReducer.ActionFinished(s,
                    gate.Allowed ? "validation, " + gate.Summary : null,
                    gate.Allowed ? null : "validation refused the job: " + gate.Summary,
                    warned: gate.Warnings > 0));
                ProjectValidation();
            });
        }

        void Upload()
        {
            EnsureGateAsync(gate =>
            {
                if (!gate.Allowed) { Fail(gate.Summary + ". Upload refused"); return; }
                if (!TryGetSession(out var session)) return;
                string remote = JobSafety.RemotePath(_jobName);
                byte[] bytes = JobBytes();
                ulong contentHash = JobHash;
                var lines = JobLines;
                var lim = _lim;
                var mechanism = Mechanism;
                string? profileError = _profileError;
                var projector = _projector;   // the upload record belongs to this host's state
                bool warned = false;
                Dispatch("uploading " + remote, false, async (snap, client, ct) =>
                {
                    JobSafety.EnsureFirmwareSafe(snap, client, lim, mechanism,
                        session.ArmConfiguration, gate.InitialConfiguration, profileError,
                        jobHomesFirst: gate.HomesFirst);
                    var (notice, motionWarned) = await JobSafety.CheckMotionAtGateAsync(client, lines, false, ct).ConfigureAwait(false);
                    if (motionWarned) warned = true;
                    UploadVerification verification = await client.UploadAsync(remote, bytes, ct).ConfigureAwait(false);
                    var record = new UploadRecord(remote, contentHash, verification.Size, client.ConnectionGeneration);
                    projector.Apply(s => PrintConsoleReducer.Uploaded(s, record));
                    return (notice.Length == 0 ? "" : notice + ". ")
                        + $"uploaded {remote} ({verification.Size} bytes, {verification.Summary})";
                }, () => warned);
            });
        }

        void Start()
        {
            EnsureGateAsync(gate =>
            {
                if (!gate.Allowed) { Fail("job not valid. Validate and upload first"); return; }
                if (!TryGetSession(out var session)) return;
                var uploaded = _projector.State.Uploaded;
                if (uploaded == null || uploaded.Hash != JobHash)
                {
                    Fail("the G-code changed since the last upload. Upload again first");
                    return;
                }
                string path = uploaded.Path;
                long expectSize = uploaded.Size;
                byte[] expectedBytes = JobBytes();
                var lines = JobLines;
                // The snapshot a confirmed start promotes to ACTIVE. Prepared here as well
                // as at solve time, because a session opened after the last solve has an
                // empty tracker and a start must never run bytes nothing recorded.
                var prepared = _preparedJob;
                if (prepared != null) session.Jobs.Prepare(prepared);
                session.SetJobArchiveContext(CurrentMachineContext(), _dryRun);
                var lim = _lim;
                var mechanism = Mechanism;
                string? profileError = _profileError;
                var projector = _projector;
                bool dry = _dryRun;   // the mode the window labelled the button with
                bool warned = false;
                Dispatch(dry ? "starting dry run (M37)" : "starting print", true, async (snap, client, ct) =>
                {
                    JobSafety.EnsureFirmwareSafe(snap, client, lim, mechanism,
                        session.ArmConfiguration, gate.InitialConfiguration, profileError,
                        jobHomesFirst: gate.HomesFirst);
                    // StateGate BEFORE the motion probe (three round-trips) so the probe
                    // cannot age `snap` out of the 3 s freshness window
                    string? stateGate = StateGate.Refuse(snap, requireIdle: true,
                        requireHomed: !gate.HomesFirst, activity: "starting a job");
                    if (stateGate != null) throw new DuetException("start refused: " + stateGate);
                    var (notice, motionWarned) = await JobSafety.CheckMotionAtGateAsync(client, lines, false, ct).ConfigureAwait(false);
                    if (motionWarned) warned = true;
                    await client.VerifyRemoteContentAsync(path, expectedBytes, ct).ConfigureAwait(false);
                    long stored;
                    try { stored = await client.GetFileSizeAsync(path, ct).ConfigureAwait(false); }
                    catch (DuetException ex)
                    {
                        projector.Apply(s => s.WithUploaded(null));
                        throw new DuetException($"SD file missing since the last verified upload ({path}). Upload again", ex);
                    }
                    if (expectSize > 0 && stored != expectSize)
                    {
                        projector.Apply(s => s.WithUploaded(null));
                        throw new DuetException($"SD file changed since the last verified upload ({path}: "
                            + $"{stored} bytes, expected {expectSize}). Upload again");
                    }
                    // re-gate on a FRESH snapshot immediately before committing motion:
                    // the round trips above can outlive the gate window, and a DWC pause,
                    // cancel or E-stop meanwhile invalidates the tracked arm configuration
                    var commitSnap = await client.GetSnapshotAsync(true, ct).ConfigureAwait(false);
                    JobSafety.EnsureFirmwareSafe(commitSnap, client, lim, mechanism,
                        session.ArmConfiguration, gate.InitialConfiguration, profileError,
                        jobHomesFirst: gate.HomesFirst);
                    string? commitGate = StateGate.Refuse(commitSnap, requireIdle: true,
                        requireHomed: !gate.HomesFirst, activity: "starting a job");
                    if (commitGate != null) throw new DuetException("start refused: " + commitGate);
                    session.MarkInitiatedJob(path);
                    // The prepared snapshot becomes the ACTIVE run BEFORE the send: the poll
                    // loop can observe the job start before the M32 reply returns, and the
                    // job log that opens on that poll must archive exactly these bytes
                    // rather than record an external run (the 2026-09-09 13:17 console
                    // start was logged as "g-code bytes unknown"). A repeat of identical
                    // content is a new run, not the old one; a failed send rolls it back.
                    var active = session.Jobs.StartPrepared(DateTime.UtcNow);
                    try
                    {
                        session.MarkInitiatedMotion();
                        // Print-component parity: M37 plans the whole job without moving.
                        if (dry) await client.SimulatePrintAsync(path, ct).ConfigureAwait(false);
                        else await client.StartPrintAsync(path, ct).ConfigureAwait(false);
                        if (active != null) projector.Apply(s => s.WithActiveJob(active));
                        if (gate.Checks.FinalConfiguration.Known)
                            session.SetArmConfiguration(gate.Checks.FinalConfiguration);
                    }
                    // Fail-closed cleanup, then rethrow: whatever stopped M32/M37 - a
                    // refusal, a transport fault, a cancellation - the session must not be
                    // left believing it initiated a job. The caller still sees the original
                    // exception and reports it through Finish().
                    catch (Exception)
                    {
                        session.Jobs.AbortStart();
                        session.ClearInitiatedJob();
                        throw;
                    }
                    return (notice.Length == 0 ? "" : notice + ". ")
                        + (dry ? "dry run started (M37): " : "print started (M32): ") + path;
                }, () => warned);
            });
        }

        void JobControl(string label, string verb, Func<ObjectModelSnapshot, bool> allowed,
                        Func<MachineSession, IMachineClient, CancellationToken, Task<string>> send)
        {
            if (!TryGetSession(out var session)) return;
            Dispatch(label, true, async (snap, client, ct) =>
            {
                string? refusal = StateGate.RefuseJobControl(snap, label, allowed(snap));
                if (refusal != null) throw new DuetException(verb + " refused: " + refusal);
                return await send(session, client, ct).ConfigureAwait(false);
            });
        }

        void EmergencyStop()
        {
            var session = _projector.Session;
            if (session == null)
            {
                Fail("no session, so M112 cannot be sent. Use the machine's physical switch");
                return;
            }
            // no gate, no queue: M112 goes straight to the board
            var projector = _projector;
            // Cancel whatever is queued and record the stop BEFORE the send: a motion
            // command that was only waiting its turn must not dispatch into a machine
            // that is being stopped, and its completion must not clear this one.
            session.BeginEmergencyStop();
            long epoch = projector.Apply(s => PrintConsoleReducer.ActionStarted(s, "emergency stop (M112)")).ActionEpoch;
            Task.Run(async () =>
            {
                try
                {
                    await session.EmergencyStopAsync().ConfigureAwait(false);
                    // An acknowledgement, not a verified stop: the console says which.
                    Finish(projector, "M112 acknowledged by the board. Confirm the machine stopped, then press Reset",
                           null, false, epoch);
                }
                catch (Exception ex)
                {
                    Finish(projector, null, MachineSession.Flatten(ex) + ". Use the physical switch", false, epoch);
                }
            });
        }

        /// <summary>The footer Reset. One rule for the button, the Terminal tab and the
        /// transaction (<see cref="ResetPolicy"/>): the ordinary path needs a connected
        /// machine reporting Halted/Off, and the recovery path needs THIS session's
        /// emergency stop plus a machine whose status can no longer be read - which is
        /// the state the operator was locked out of on 2026-09-09. The blind path arms on
        /// the first press and sends on the second, because nothing can confirm the halt
        /// for it.</summary>
        void Reset()
        {
            if (!TryGetSession(out var session)) return;
            var projector = _projector;
            var state = projector.State;
            var decision = ResetPolicy.Decide(state.ResetContext(), DateTime.UtcNow);
            if (!decision.Allowed)
            {
                Fail("reset refused: " + decision.Refusal);
                return;
            }
            if (decision.NeedsArming)
            {
                session.ArmOperatorRecovery();
                projector.Apply(s => PrintConsoleReducer.ActionFinished(s,
                    "the machine is not answering. Press Reset again to send " + state.ResetCommand
                    + " anyway. This cannot repair a dead connection", null));
                return;
            }
            bool operatorRecovery = decision.Path == ResetPath.OperatorRecovery;
            long epoch = projector.Apply(s => PrintConsoleReducer.ActionStarted(
                s, "reset (" + state.ResetCommand + ")")).ActionEpoch;
            Task.Run(async () =>
            {
                try
                {
                    string text = await session.RunRecoveryResetAsync(operatorRecovery).ConfigureAwait(false);
                    Finish(projector, text, null, false, epoch);
                }
                catch (Exception ex)
                {
                    string msg = MachineSession.Flatten(ex);
                    session.AppendLog("console: reset failed: " + msg);
                    Finish(projector, null, msg, false, epoch);
                }
            });
        }

        // ---- 3.0.0: actions absorbed from Jog, Terminal and Connect ---------------------

        /// <summary>One whitelisted jog step. Ported from <c>DuetJogComponent.Jog</c>:
        /// same planner call, same gate order, same MarkInitiatedMotion /
        /// SetArmConfiguration placement.</summary>
        /// <param name="allowMaintenanceOverride">False for an auto-repeat. The
        /// paused-jog override is armed as a side effect of the refusal itself, so a held
        /// button has to be denied it BEFORE the refusal is raised, not after.</param>
        void Jog(double dx, double dy, double dz, bool allowMaintenanceOverride = true, bool repeat = false)
        {
            // The session and the planning inputs are the ones current at the press: the
            // body may wait behind a long command, and by then the host input can name a
            // different machine, whose arm configuration must not plan this move.
            if (!TryGetSession(out var session))
            {
                if (repeat) _jogRepeat = JogRepeatPolicy.SendRefused(_jogRepeat, "not connected");
                return;
            }
            bool maintenanceOverride = allowMaintenanceOverride && DateTime.UtcNow < _maintenanceArmedUntil;
            bool cartesian = Cartesian;
            string? profileError = _profileError;
            double feed = _feed;
            double step = _projector.State.Step;
            var lim = _lim;
            int elbow = _elbow;
            var envelopeUnknown = _envelopeUnknown;
            _lastCmdSentUtc = DateTime.UtcNow;
            Dispatch("jogging", false, async (snap, client, ct) =>
            {
                if (profileError != null)
                    throw new DuetException("jog refused: " + profileError);
                bool armed = maintenanceOverride && snap.Status == MachineStatus.Paused;
                if (snap.Status == MachineStatus.Paused && !armed)
                {
                    // Only a discrete press may arm the override; an auto-repeat that
                    // reached here must not leave it armed behind the operator back.
                    if (allowMaintenanceOverride) _maintenanceArmedUntil = DateTime.UtcNow.AddSeconds(5);
                    throw new DuetException("jog refused while paused. The maintenance override is now armed, click Jog again to proceed");
                }
                if (cartesian && envelopeUnknown.Count > 0)
                    throw new DuetException("jog refused: machine envelope unknown ("
                        + string.Join(", ", envelopeUnknown)
                        + "). Wait for the firmware M208 report or wire the limit inputs");
                // the tracked arm configuration exists only for the SCARA; a Cartesian
                // machine jog is gated by state + envelope alone
                var configuration = session.ArmConfiguration;
                if (!cartesian && !configuration.Known)
                    throw new DuetException("jog refused: arm configuration is unknown. Complete a verified home first");
                var plan = JogPlanner.Plan(snap,
                    new JogRequest { Dx = dx * step, Dy = dy * step, Dz = dz * step, FeedMmS = feed },
                    lim, elbow, maxFeedMmS: JogFeedCeiling,
                    notBeforeUtc: _lastCmdSentUtc, maintenanceOverride: armed,
                    sessionConfiguration: cartesian ? (ArmConfiguration?)null : configuration,
                    mechanism: cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara);
                if (!plan.Ok) throw new DuetException("jog refused: " + plan.RefusalReason);
                string warn = plan.Warnings.Count > 0 ? "  [" + string.Join(". ", plan.Warnings) + "]" : "";
                var shown = ToRhinoPoint(new Vec3(plan.TargetX, plan.TargetY, plan.TargetZ));
                session.AppendLog($"jog to ({shown.X:0.##}, {shown.Y:0.##}, {shown.Z:0.##}){warn}");
                session.MarkInitiatedMotion();
                await client.SendCodeNoReplyAsync(plan.Gcode, ct);
                if (!cartesian) session.SetArmConfiguration(plan.FinalConfiguration);
                _maintenanceArmedUntil = DateTime.MinValue;
                return $"jog sent to ({shown.X:0.##}, {shown.Y:0.##}, {shown.Z:0.##})" + warn;
            }, onOutcome: repeat ? (Action<bool, string?>)((ok, error) =>
            {
                // The acknowledgement is keyed on the poll generation current at COMPLETION,
                // so a poll that merely finished afterwards cannot release the next step.
                _jogRepeat = ok
                    ? JogRepeatPolicy.SendCompleted(_jogRepeat, _projector.Session?.PollGeneration ?? 0)
                    : JogRepeatPolicy.SendRefused(_jogRepeat, error ?? "refused");
            }) : null);
        }

        // ---- press-and-hold jog ---------------------------------------------------------

        /// <summary>Starts a hold. The first step goes immediately; every later one waits
        /// for the acknowledgement <see cref="JogRepeatPolicy"/> requires, so at most one
        /// jog is ever in flight and nothing is queued behind it.</summary>
        void BeginJogHold(string id)
        {
            switch (id)
            {
                case "jogxminus": _jogDx = -1; _jogDy = 0; _jogDz = 0; break;
                case "jogxplus": _jogDx = 1; _jogDy = 0; _jogDz = 0; break;
                case "jogyminus": _jogDx = 0; _jogDy = -1; _jogDz = 0; break;
                case "jogyplus": _jogDx = 0; _jogDy = 1; _jogDz = 0; break;
                case "jogzminus": _jogDx = 0; _jogDy = 0; _jogDz = -1; break;
                case "jogzplus": _jogDx = 0; _jogDy = 0; _jogDz = 1; break;
                default: return;
            }
            _jogPressUtc = DateTime.UtcNow;
            _jogRepeat = JogRepeatPolicy.Press(_jogRepeat);
            PumpJogRepeat();
        }

        /// <summary>Asks the policy whether the next held step may go, and sends it if so.
        /// Called on a press and after every completed poll - the poll is what supplies the
        /// post-send acknowledgement, so the repeat self-paces to the machine round trip.</summary>
        void PumpJogRepeat()
        {
            if (JogRepeatPolicy.Decide(_jogRepeat) != JogRepeatDecision.Send) return;
            bool discrete = _jogRepeat.AllowsMaintenanceOverride;
            _jogRepeat = JogRepeatPolicy.SendStarted(_jogRepeat);
            Jog(_jogDx, _jogDy, _jogDz, allowMaintenanceOverride: discrete, repeat: true);
        }

        /// <summary>Feeds every completed poll into the repeat policy. Subscribed to
        /// PollCompleted rather than Changed because Changed suppresses
        /// SessionChangeKind.None and so cannot prove an acknowledgement poll happened.</summary>
        void OnPollCompleted(long generation, ObjectModelSnapshot snapshot)
        {
            var readiness = JogRepeatPolicy.ReadinessOf(snapshot.Status,
                maintenanceOverrideActive: DateTime.UtcNow < _maintenanceArmedUntil);
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _jogRepeat = JogRepeatPolicy.PollObserved(_jogRepeat, generation, readiness);
                if (_jogPressUtc.HasValue && readiness == JogReadiness.Busy)
                {
                    // press-to-motion: the press timestamp to the first non-idle snapshot
                    double ms = (DateTime.UtcNow - _jogPressUtc.Value).TotalMilliseconds;
                    _jogPressUtc = null;
                    _projector.Session?.AppendLog($"console: press-to-motion {ms:0} ms");
                }
                PumpJogRepeat();
            }));
        }

        /// <summary>Set bed origin. Reads the machine own position; it sends
        /// nothing, so it is not trust-gated. The capture is stored in the MACHINE frame -
        /// Emit converts to the Rhino frame and document units.</summary>
        void CaptureOrigin()
        {
            var projector = _projector;
            Dispatch("capturing bed origin", false,
                async (snap, client, ct) =>
                {
                    string? gate = StateGate.Refuse(snap, requireIdle: true, requireHomed: true,
                        activity: "origin capture");
                    if (gate != null) throw new DuetException("origin capture refused: " + gate);
                    var x = snap.Axis("X"); var y = snap.Axis("Y"); var z = snap.Axis("Z");
                    if (x == null || y == null || z == null) throw new DuetException("origin capture refused: the XYZ position is unavailable");
                    var machine = new Vec3(x.MachinePosition, y.MachinePosition, z.MachinePosition);
                    projector.Apply(st => PrintConsoleReducer.BedOriginCaptured(st, machine));
                    var p = ToRhinoPoint(machine);
                    await Task.CompletedTask;
                    return $"bed origin captured at ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})";
                });
        }

        /// <summary>Terminal send. Classification and confirm arming live in Core
        /// (<see cref="TerminalPolicy"/>), shared with the hidden Terminal component.
        /// Diagnostics take the serialized non-state-changing path so they stay usable
        /// while machine trust is diagnostic-only; advanced lines take the trusted gated
        /// transaction and invalidate the tracked arm configuration.</summary>
        void TerminalSend(string line)
        {
            if (!TryGetSession(out var session)) return;
            var projector = _projector;
            var state = projector.State;

            // D4 parity, now through the one reset rule: a console-entered M999 is decided
            // by exactly the policy the footer button uses, or the Terminal tab would be a
            // bypass around it - in either direction. It also means the tab reaches the
            // recovery path when the button does, instead of drifting behind it.
            var resetDecision = ResetPolicy.Decide(state.ResetContext(), DateTime.UtcNow);

            var verdict = TerminalPolicy.Decide(line, state.TerminalConfirmUntilUtc, DateTime.UtcNow,
                                                resetDecision.Allowed,
                                                "reset (" + state.ResetCommand + ") refused: "
                                                + (resetDecision.Refusal ?? "not permitted right now"));
            if (verdict.Decision == TerminalSendDecision.Refuse)
            {
                projector.Apply(st => PrintConsoleReducer.ActionFinished(st, null, verdict.Reason));
                return;
            }
            if (verdict.Decision == TerminalSendDecision.Arm)
            {
                var until = TerminalPolicy.ArmUntil(DateTime.UtcNow);
                string armedText = verdict.Text;
                projector.Apply(st => PrintConsoleReducer.ActionFinished(
                    PrintConsoleReducer.TerminalArmed(st, until, armedText), verdict.Reason, null));
                return;
            }

            projector.Apply(st => PrintConsoleReducer.TerminalDisarmed(st));
            string text = verdict.Text;
            bool advanced = verdict.Route == TerminalRoute.Advanced;
            session.AppendLog("> " + text);

            // A confirmed terminal reset runs the BUTTON'S path, not a raw send: same
            // bounded recovery transaction, same prerequisite poll, same post-reset state
            // discard. Identical policy is not enough if the two take different routes.
            if (TerminalPolicy.IsReset(verdict.Line) || TerminalPolicy.IsReset(text))
            {
                Reset();
                return;
            }

            long epoch = projector.Apply(st => PrintConsoleReducer.ActionStarted(st, "sending " + text)).ActionEpoch;
            Task.Run(async () =>
            {
                try
                {
                    // fire-and-forget: the session poll loop drains rr_reply (single
                    // consumer), so the reply lands in `reply`/`log` within one poll tick.
                    string reply = advanced
                        ? await session.RunGatedCommandAsync(false, async (snap, client, ct) =>
                          {
                              // Defence in depth: a reset never reaches this path any more
                              // (it is routed to the button's recovery transaction above),
                              // and if a future edit ever lets one through, the transaction's
                              // own fresh snapshot still refuses it outside Halted/Off.
                              if (TerminalPolicy.IsReset(verdict.Line)
                                  && snap.Status != MachineStatus.Halted && snap.Status != MachineStatus.Off)
                                  throw new DuetException("reset (M999) refused: machine is "
                                      + snap.Status.ToString().ToLowerInvariant() + ", not halted or off");
                              // An advanced line is raw G-code that never went through the
                              // kinematic model, so its motion is opaque: it must INVALIDATE
                              // the tracked arm configuration, not authorize it. Diagnostic
                              // reads move nothing and are left alone.
                              session.InvalidateArmConfiguration("raw terminal command, unmodelled motion");
                              await client.SendCodeNoReplyAsync(text, ct);
                              return "sent. The reply appears with the next poll";
                          }).ConfigureAwait(false)
                        : await session.RunCommandAsync(async (client, ct) =>
                          {
                              await client.SendCodeNoReplyAsync(text, ct);
                              return "sent. The reply appears with the next poll";
                          }, stateChanging: false).ConfigureAwait(false);
                    Finish(projector, reply, null, false, epoch);
                }
                catch (Exception ex)
                {
                    string msg = MachineSession.Flatten(ex);
                    session.AppendLog("console: terminal send failed: " + msg);
                    Finish(projector, null, msg, false, epoch);
                }
            });
        }

        /// <summary>Explicit operator instruction to end the session. Connect-component
        /// semantics: CloseHost ends it for EVERY leaseholder, not just this component -
        /// this is deliberate and differs from releasing a lease.</summary>
        void Disconnect()
        {
            var projector = _projector;
            projector.Apply(s => PrintConsoleReducer.ActionStarted(s, "disconnecting"));
            try
            {
                MachineSessionManager.CloseHost(HostInput);   // by host: the manager canonicalises it
                // A closing session raises no further change, so the projection would keep
                // showing Connected with Disconnect offered until the next solve. Let go of
                // it here: the window then reads not connected and offers Connect, and the
                // E-STOP says there is no session rather than looking armed.
                projector.Detach();
                ReleaseLease();
                Finish(projector, "disconnected", null);
            }
            catch (Exception ex)
            {
                Finish(projector, null, MachineSession.Flatten(ex));
            }
        }

        void GoTo()
        {
            if (!GoToSupported) { Fail("go-to is available for the SCARA profile only"); return; }
            if (!TryGetSession(out var session)) return;
            var points = _points;
            int index = _form != null && !_form.IsDisposed ? _form.SelectedPoint : -1;
            if (index < 0 || index >= points.Count) { Fail("no point selected"); return; }
            var p = points[index];
            MachineFrame.ToMachine(p.X, p.Y, out double mx, out double my);
            var target = new Vec3(mx, my, p.Z);
            var lim = _lim;
            _lastCmdSentUtc = DateTime.UtcNow;
            var notBefore = _lastCmdSentUtc;
            Dispatch($"moving to point {index}", false, async (fresh, client, ct) =>
            {
                if (!session.ArmConfiguration.Known)
                    throw new DuetException("move refused: arm configuration is unknown. Complete a verified home first");
                var plan = JogPlanner.PlanPath(fresh, new[] { target }, GoToFeedMmS, lim, GoToElbow, GoToFeedCeilingMmS,
                                               notBeforeUtc: notBefore, sessionConfiguration: session.ArmConfiguration);
                if (!plan.Ok) throw new DuetException("move refused: " + plan.RefusalReason);
                session.AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "console point {0}: rhino ({1:0.##}, {2:0.##}, {3:0.##}) -> machine ({4:0.##}, {5:0.##})",
                    index, p.X, p.Y, p.Z, mx, my));
                session.MarkInitiatedMotion();
                await client.SendCodeNoReplyAsync(plan.Gcode, ct).ConfigureAwait(false);
                session.SetArmConfiguration(plan.FinalConfiguration);
                return $"move to point {index} sent";
            });
        }

        /// <summary>Drives the nozzle to the validated job's print start at Z 0: the
        /// start of its first extruding move, which the validation projection carries in
        /// the MACHINE frame (so no frame conversion happens here - the point is exactly
        /// what the checks validated). Two planned legs: across at the current height,
        /// then straight down onto the bed, both through the same JogPlanner gates as
        /// the calibration go-to.</summary>
        void GoToStart()
        {
            if (!GoToSupported) { Fail("go-to is available for the SCARA profile only"); return; }
            if (!TryGetSession(out var session)) return;
            var validation = _projector.State.Validation;
            if (validation == null || !validation.StartPoint.HasValue)
            { Fail("go to print start refused - validate the job first"); return; }
            if (validation.Danger)
            { Fail("go to print start refused - validation refused the job"); return; }
            var start = validation.StartPoint.Value;   // MACHINE frame, as validated
            var lim = _lim;
            _lastCmdSentUtc = DateTime.UtcNow;
            var notBefore = _lastCmdSentUtc;
            Dispatch("moving to print start", false, async (fresh, client, ct) =>
            {
                if (!session.ArmConfiguration.Known)
                    throw new DuetException("move refused - arm configuration is Unknown; complete a verified home first");
                var zNow = fresh.Axis("Z");
                if (zNow == null || !Num.Finite(zNow.MachinePosition))
                    throw new DuetException("move refused - no live Z position");
                // across at the current height, then down: the target is ON the bed, so the
                // descent is vertical and never a diagonal scrape across it
                var waypoints = new[]
                {
                    new Vec3(start.X, start.Y, zNow.MachinePosition),
                    new Vec3(start.X, start.Y, 0.0),
                };
                var plan = JogPlanner.PlanPath(fresh, waypoints, GoToFeedMmS, lim, GoToElbow, GoToFeedCeilingMmS,
                                               notBeforeUtc: notBefore, sessionConfiguration: session.ArmConfiguration);
                if (!plan.Ok) throw new DuetException("move refused - " + plan.RefusalReason);
                var shown = ToRhinoPoint(new Vec3(start.X, start.Y, 0.0));
                session.AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "console print start: machine ({0:0.##}, {1:0.##}) Z 0 - rhino ({2:0.##}, {3:0.##})",
                    start.X, start.Y, shown.X, shown.Y));
                session.MarkInitiatedMotion();
                await client.SendCodeNoReplyAsync(plan.Gcode, ct).ConfigureAwait(false);
                session.SetArmConfiguration(plan.FinalConfiguration);
                return "move to print start sent";
            });
        }

        void ClearLog()
        {
            var session = _projector.Session;
            long sequence = session?.LogSequence ?? 0;
            _projector.Apply(s => PrintConsoleReducer.ClearLog(s, sequence));
        }

        // ---- validation (same policy key and cache rule as Duet Print) ----

        /// <summary>Runs <see cref="JobGate.Validate"/> for the present inputs on a worker
        /// inside the pending window (cache hit by quick hash completes synchronously),
        /// then <paramref name="onReady"/> on the UI thread. Fails closed before any work
        /// on an unresolved profile or an unknown Cartesian envelope.</summary>
        void EnsureGateAsync(Action<JobGateResult> onReady)
        {
            if (JobLines.Count == 0) { Fail("upload refused: the gcode input is empty"); return; }
            if (_profileError != null) { Fail("refused: " + _profileError); return; }
            if (Cartesian && _envelopeUnknown.Count > 0)
            {
                Fail("refused: machine envelope unknown (" + string.Join(", ", _envelopeUnknown)
                    + "). Wait for the firmware's M208 report");
                return;
            }
            var mechanism = Mechanism;
            var initialConfiguration = _projector.Session?.ArmConfiguration;
            string policy = CurrentGatePolicy(mechanism, initialConfiguration);
            if (!string.Equals(policy, _gatePolicyKey, StringComparison.Ordinal))
            {
                _gateQuickHash = ContentHash.Fnv1aLinesWithSuffix(JobLines, policy);
                _gatePolicyKey = policy;
            }
            ulong quick = _gateQuickHash;
            if (_gate != null && _gate.Hash == quick) { onReady(_gate); return; }

            var gcode = JobLines; var lim = _lim;
            int elbow = _elbow; bool arcSupport = _arcSupport; bool blockWarnings = _blockWarnings;
            var configuration = initialConfiguration;
            ulong captured = quick;
            var projector = _projector;   // a host rebind while the worker runs voids the result
            projector.Apply(s => PrintConsoleReducer.ActionStarted(s, "validating"));
            Task.Run(() =>
            {
                JobGateResult? gate = null; string? err = null;
                try
                {
                    gate = JobGate.Validate(gcode, lim, elbow, arcSupport: arcSupport,
                                            blockOnWarnings: blockWarnings, mastAsError: true,
                                            initialConfiguration: configuration,
                                            mechanism: mechanism);
                }
                catch (Exception ex) { err = MachineSession.Flatten(ex); }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    // the result is current only if neither the job (quick hash, zeroed by a
                    // solve that changed the gcode) nor the policy (limits, mechanism, the
                    // session's arm configuration) moved while the worker ran
                    bool sameHost = ReferenceEquals(projector, _projector);
                    if (gate != null && sameHost && _gateQuickHash == captured
                        && string.Equals(CurrentGatePolicy(Mechanism, projector.Session?.ArmConfiguration), policy,
                                         StringComparison.Ordinal))
                    {
                        _gate = gate;
                        // the gate summary (allowed overspeed / jerk / singularity warnings included)
                        // stays in the session log; the upload result replaces it on the status line
                        projector.Session?.AppendLog("console: validation, " + gate.Summary);
                        projector.Apply(s => PrintConsoleReducer.ActionFinished(s, gate.Summary, null));
                        onReady(gate);
                    }
                    else Finish(projector, null, err != null ? "validation failed: " + err
                        : sameHost ? "inputs changed during validation. Press again"
                        : "host changed during validation. Press again");
                }));
            });
        }

        /// <summary>Decision D5: synchronous gate refresh on the solve path, cached by
        /// content hash - the same semantics as <c>DuetPrintJobComponent.RunGate(force:
        /// false)</c>, so the danger / problems / messages outputs are byte-compatible
        /// with the component they replace. The button handlers keep using
        /// <see cref="EnsureGateAsync"/>, which never blocks the canvas.</summary>
        void RunGate(bool force)
        {
            var mechanism = Mechanism;
            var configuration = _projector.Session?.ArmConfiguration;
            string policy = CurrentGatePolicy(mechanism, configuration);
            if (!string.Equals(policy, _gatePolicyKey, StringComparison.Ordinal))
            {
                _gateQuickHash = ContentHash.Fnv1aLinesWithSuffix(JobLines, policy);
                _gatePolicyKey = policy;
            }
            if (!force && _gate != null && _gate.Hash == _gateQuickHash) return;
            _gate = JobGate.Validate(JobLines, _lim, _elbow, _arcSupport,
                                     blockOnWarnings: _blockWarnings, mastAsError: true,
                                     initialConfiguration: configuration,
                                     mechanism: mechanism);
        }

        /// <summary>Folds the solve-time validation into the state so outputs 17-19 are
        /// projected like every other output. Problem points stay in the MACHINE frame;
        /// Emit converts them. Fails CLOSED: an unresolved profile or an unknown Cartesian
        /// envelope reports danger with an explanation rather than a silent pass.</summary>
        void ProjectValidation()
        {
            var messages = new List<string>();
            var problems = new List<Vec3>();
            bool danger;

            if (_profileError != null)
            {
                _gate = null;
                danger = true;
                messages.Add("validation unavailable: " + _profileError);
            }
            else if (Cartesian && _envelopeUnknown.Count > 0)
            {
                _gate = null;
                danger = true;
                messages.Add("validation unavailable: machine envelope unknown ("
                    + string.Join(", ", _envelopeUnknown)
                    + "). Connect so the M208 report arrives, or wire the limit inputs");
            }
            else if (JobLines.Count > 0)
            {
                RunGate(force: false);
                danger = !_gate!.Allowed;
                messages.Add(_gate.Summary);
                int shown = 0;
                foreach (var pr in _gate.Checks.Problems)
                {
                    if (pr.Sev == Severity.Error || pr.Sev == Severity.Warning)
                        problems.Add(new Vec3(pr.At.X, pr.At.Y, pr.At.Z));
                    if (shown++ < 50) messages.Add(pr.ToString());
                }
                if (_gate.Checks.Problems.Count > 50)
                    messages.Add($"... {_gate.Checks.Problems.Count - 50} more (see Robot component)");
                if (!_gate.Allowed)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _gate.Summary);
            }
            else
            {
                _gate = null;
                danger = false;
                messages.Add("no G-code connected");
            }

            bool warned = _gate != null && _gate.Warnings > 0;
            var projection = new ValidationProjection(JobHash, danger, problems, messages, warned,
                                                      homesFirst: _gate != null && _gate.HomesFirst,
                                                      startPoint: _gate?.StartPoint);
            _projector.Apply(st => PrintConsoleReducer.ValidationCompleted(st, projection));
        }

        /// <summary>The gate's policy key: limits, mechanism and the session's arm
        /// configuration. Recomputed when a validation completes, so a solve or a home
        /// that changed any of them while the worker ran invalidates its result.</summary>
        string CurrentGatePolicy(RobotMechanism mechanism, ArmConfiguration? initialConfiguration)
        {
            string configurationKey = initialConfiguration.HasValue && initialConfiguration.Value.Known
                ? $"|cfg:{initialConfiguration.Value.ArmMode}:{initialConfiguration.Value.ThetaTurns}"
                : "|cfg:unknown";
            return "|" + ContentHash.LimitsKey(_lim, _elbow, 0, _arcSupport)
                + "|" + _blockWarnings + "|" + true + configurationKey
                + "|mech:" + (int)mechanism;
        }

        // ---- the job under the operator's hands: wired input, or a run from history ----

        /// <summary>The lines every gate, upload and start works on: the run loaded from
        /// Recent prints while one is selected, otherwise the wired G-code.</summary>
        IReadOnlyList<string> JobLines => _historyLines ?? (IReadOnlyList<string>)_gcode;
        ulong JobHash => _historyLines != null ? _historyHash : _gcodeHash;

        byte[] JobBytes()
        {
            if (_historyBytes != null) return _historyBytes;
            if (_jobBytes != null && _jobBytesHash == _gcodeHash) return _jobBytes;
            _jobBytes = Encoding.UTF8.GetBytes(string.Join("\n", _gcode) + "\n");
            _jobBytesHash = _gcodeHash;
            return _jobBytes;
        }

        /// <summary>Rebuilds the immutable snapshot a Start would run and publishes it: to
        /// the state (which is where the `gcode` output comes from) and to the session's
        /// job tracker (which keeps it apart from whatever is actually printing).</summary>
        void PrepareJob()
        {
            var prepared = JobLines.Count > 0
                ? JobSnapshot.FromBytes(JobBytes(), JobSafety.RemotePath(_jobName),
                                        _historyRun != null ? JobSource.MachineFile : JobSource.ConsoleInput,
                                        DateTime.UtcNow)
                : null;
            _preparedJob = prepared;
            _projector.Session?.Jobs.Prepare(prepared);
            _projector.Apply(s => s.WithPreparedJob(prepared));
        }

        /// <summary>The machine and profile a run made here is recorded against, so a later
        /// reprint can say what has changed since.</summary>
        ReprintContext CurrentMachineContext()
            => new ReprintContext(HostInput, _expectedFingerprint?.Value ?? "",
                                  _profileError == null ? _profile.Id : "",
                                  Mechanism.ToString(), ControlKind.ToString());

        // ---- Recent prints ---------------------------------------------------------------

        /// <summary>Loads a past run. Loading is NOT running: the bytes become the prepared
        /// job and nothing else happens - the active print, if any, keeps its own snapshot.
        /// The archive is read on the retention worker and verified against the hash the run
        /// recorded before it is adopted.</summary>
        void SelectRun(string id)
        {
            var state = _projector.State;
            if (state.PendingAction != null) { Fail("another command is pending"); return; }
            var entry = RecentPrints.Find(state.RecentRuns, id);
            if (entry == null) { Fail("that run is no longer listed. The log may have been discarded"); return; }
            string? refusal = ReprintPolicy.RefuseReprint(entry, CurrentMachineContext());
            if (refusal != null)
            {
                _projector.Apply(s => s.WithSelectedRun(entry, refusal));
                Fail(refusal);
                return;
            }
            var projector = _projector;
            projector.Apply(s => PrintConsoleReducer.ActionStarted(s, "loading " + entry.JobName));
            LogRetentionService.Instance.ReadArchivedGcode(entry.Id, bytes =>
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => AdoptRun(projector, entry, bytes))));
        }

        void AdoptRun(PrintConsoleProjector projector, RecentPrintEntry entry, byte[]? bytes)
        {
            if (!ReferenceEquals(projector, _projector)) return;   // host rebind while reading
            string? refusal = ReprintPolicy.VerifyBytes(entry, bytes);
            if (refusal != null)
            {
                projector.Apply(s => s.WithSelectedRun(entry, refusal));
                Finish(projector, null, refusal);
                return;
            }
            // A NEW run of the same content: no remote path yet, because this job has not
            // been uploaded to this machine - the ordinary verified upload does that.
            var snapshot = JobSnapshot.FromBytes(bytes!, null, JobSource.MachineFile, DateTime.UtcNow);
            var lines = new List<string>(snapshot.Lines());
            _historyRun = entry;
            _historyLines = lines;
            _historyBytes = bytes;
            _historyHash = ContentHash.Fnv1aLines(lines);
            // A loaded run is a different job: the validation cache and any upload record
            // for the previous one must not vouch for it.
            _gate = null;
            _gatePolicyKey = "";
            _gateQuickHash = 0;
            _preparedJob = snapshot;
            projector.Session?.Jobs.Prepare(snapshot);
            string? warning = ReprintPolicy.Warn(entry, CurrentMachineContext());
            int count = lines.Count;
            ulong hash = _historyHash;
            projector.Apply(s => PrintConsoleReducer.GcodeChanged(s, count, hash)
                                                    .WithPreparedJob(snapshot)
                                                    .WithSelectedRun(entry, warning));
            Finish(projector, "loaded " + entry.JobName + " (" + entry.ShortHash + ", "
                   + count.ToString(CultureInfo.InvariantCulture) + " lines). Validate, upload and start it to print it again", null);
            OnBackgroundWorkCompleted();   // re-solve so validation runs against these bytes
        }

        /// <summary>Drops the history selection; the wired G-code is what a Start would run
        /// again.</summary>
        void ClearRunSelection()
        {
            if (_historyRun == null) return;
            if (_projector.State.PendingAction != null) { Fail("another command is pending"); return; }
            _historyRun = null;
            _historyLines = null;
            _historyBytes = null;
            _historyHash = 0;
            _gate = null;
            _gatePolicyKey = "";
            _gateQuickHash = 0;
            int count = _gcode.Count;
            ulong hash = _gcodeHash;
            _projector.Apply(s => PrintConsoleReducer.GcodeChanged(s, count, hash).WithSelectedRun(null, null));
            PrepareJob();
            _projector.Apply(s => PrintConsoleReducer.ActionFinished(s, "recent print cleared. The wired G-code is prepared again", null));
            OnBackgroundWorkCompleted();
        }

        /// <summary>Keeps a selection honest against a retention list that moved under it:
        /// a discarded or deleted archive must say so on the row rather than leave a
        /// reprint armed against bytes that are gone.</summary>
        void RefreshRunSelection()
        {
            var run = _historyRun;
            if (run == null) return;
            var current = RecentPrints.Find(_projector.State.RecentRuns, run.Id);
            string? note = current == null
                ? "the log of this run is gone. Its archive can no longer be verified, so it cannot be printed again"
                : ReprintPolicy.RefuseSelection(current) ?? ReprintPolicy.Warn(current, CurrentMachineContext());
            var shown = current ?? run;
            if (ReferenceEquals(shown, _projector.State.SelectedRun)
                && string.Equals(note, _projector.State.RunSelectionNote, StringComparison.Ordinal)) return;
            _historyRun = shown;
            _projector.Apply(s => s.WithSelectedRun(shown, note));
        }
    }
}
