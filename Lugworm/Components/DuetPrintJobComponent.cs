using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using GH_IO.Serialization;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Robot;
using Lugworm.Duet;
using Lugworm.Robot;
using Lugworm.Ui;
using Rhino.Geometry;

namespace Lugworm.Components
{
    /// <summary>Validated upload-&-print of a G-code job. The machine's check set
    /// (JobGate → RobotChecks, or the Cartesian envelope checks when a
    /// generic-duet-cartesian Robot Definition is wired) is re-run INSIDE this
    /// component - there is no wireable danger input to forget - and Upload
    /// hard-refuses on any Error-severity problem. Start additionally requires that
    /// the uploaded bytes still match the current inputs (edit-after-upload guard),
    /// the machine is idle and homed. Dry-run mode starts the job with M37 (firmware
    /// simulation: full motion planning and time estimate, zero movement) instead of
    /// M32.</summary>
    public class DuetPrintJobComponent : MachineComponentBase
    {
        public DuetPrintJobComponent()
            : base("Duet Print", "Print",
                   "Validate supplied G-code, upload it to a connected Duet, then explicitly press Start to run the verified upload.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0019-4b3b-9c3d-aa0000000019");
        // 3.0.0: superseded by the Duet Console (…0029). Kept, unchanged, so
        // existing definitions keep loading and solving; hidden from the ribbon.
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetPrint();

        // captured at solve (always current - this component is not manually gated)
        List<string> _gcode = new List<string>();
        ulong _gcodeContentHash;
        byte[]? _jobBytes;
        ulong _jobBytesHash;
        string _gatePolicyKey = "";
        ulong _gateQuickHash;
        string _jobName = "print";
        bool _dryRun;
        int _elbow;
        bool _arcSupport = true;
        bool _blockWarnings;
        RobotLimits _lim = new RobotLimits { ReachMin = 235.0 };
        RobotProfile _profile = RobotCatalog.Get("potterbot-scara-v4");
        string? _profileError;
        // Cartesian-profile axes with neither a wired envelope input nor a
        // firmware M208 report - validation refuses while any remain.
        // Swapped by reference like _lim (worker lambdas read it), never mutated.
        IReadOnlyList<string> _envelopeUnknown = Array.Empty<string>();

        // validation cache (by content hash) + upload bookkeeping (persisted)
        JobGateResult? _gate;
        string _uploadedPath = "";
        ulong _uploadedHash;
        long _uploadedSize;
        // NOT persisted: after a .gh reopen the SD card may hold anything at the
        // stored path - the first Start must re-verify the remote file size

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code job as one string or a list of lines", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, connected before upload or job control", "core", PType.Text),
            new ParamSpec("jobName", "N", "Remote file name, stored as 0:/gcodes/<name>.gcode. Uploading again overwrites it", "core", PType.Text).HintText("print"),
            new ParamSpec("dryRun", "Dr", "Use firmware simulation on Start when true, with no movement", "core", PType.Boolean),
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
            new ParamSpec("elbow", "E", "Fallback SCARA arm mode: 0 = up, 1 = down, overridden by a job's full home or the tracked session configuration", "limits", PType.Integer).Hint(0, 1, 1, 0),
            new ParamSpec("arcSupport", "AS", "Allow G2/G3 arcs in job validation when true, or reject jobs containing arcs when false", "limits", PType.Boolean).HintBool(true),
            new ParamSpec("blockWarnings", "Bw", "Block upload and start on validation warnings when true (errors and mast clearance violations always block)", "limits", PType.Boolean),
            new ParamSpec("robot", "R", "Robot Definition text for job validation (unwired = Potterbot SCARA, Cartesian limits follow firmware unless wired)", "robot", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("limits", "Machine limit overrides (L1/L2, reach, Z, elbow, arcs)"),
            ("robot", "Robot definition (generic Duet machines)"),
        };

        bool Cartesian => _profileError == null
            && _profile.Mechanism == RobotMechanism.Cartesian;

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddBooleanParameter("danger", "D", "True when validation blocks upload or start under the current warning policy", GH_ParamAccess.item);
            pManager.AddPointParameter("problems", "Pp", "Validation warning and error positions in Rhino coordinates, in mm", GH_ParamAccess.list);
            pManager.AddTextParameter("messages", "M", "Validation messages", GH_ParamAccess.list);
            pManager.AddTextParameter("uploaded", "U", "Remote path of the last verified upload", GH_ParamAccess.item);
            pManager.AddTextParameter("state", "S", "Machine status", GH_ParamAccess.item);
            pManager.AddNumberParameter("progress", "Pr", "Job progress, 0 to 1", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Validation, upload and last-action details", GH_ParamAccess.item);
        }

        // ---- capsules -----------------------------------------------------------

        sealed class GateState
        {
            public readonly ActionGate Validate, Upload, Start;
            public GateState(ActionGate validate, ActionGate upload, ActionGate start)
            { Validate = validate; Upload = upload; Start = start; }
        }

        static ActionGate Gate(string action, ActionGate source)
            => new ActionGate(action, source.Reason, source.Level);

        static ActionGate ClearGate(string action)
            => new ActionGate(action, null, GateReport.None);

        GateState BuildGateState()
        {
            var s = Session;
            bool live = s != null && s.State == DuetConnState.Connected;
            bool uploadedCurrent = _gate != null && _uploadedHash == _gate.Hash && _uploadedPath.Length > 0;
            // an unresolved or non-Duet robot definition blocks every planned
            // action (fail closed); pause/resume/cancel/E-STOP stay live
            if (_profileError != null)
            {
                var blocked = new ActionGate("all actions", _profileError, GateReport.Warning);
                return new GateState(Gate("Validate", blocked), Gate("Upload", blocked),
                    Gate("Start", blocked));
            }
            var connected = ConnectedActionGate("");
            // the tracked arm configuration is a SCARA concept; the Cartesian
            // envelope-unknown refusal takes its gating place
            var configuration = Cartesian
                ? (_envelopeUnknown.Count > 0
                    ? new ActionGate("", "machine envelope unknown ("
                        + string.Join(", ", _envelopeUnknown)
                        + "). Wait for the M208 report or wire the limit inputs", GateReport.Warning)
                    : ClearGate(""))
                : s != null && live
                    ? ArmConfigurationActionGate("", "configuration-sensitive validation")
                    : ClearGate("");

            ActionGate validate = Busy
                ? new ActionGate("Validate", "another action is executing", GateReport.Remark)
                : configuration.Reason != null
                    ? Gate("Validate", configuration) : ClearGate("Validate");

            ActionGate upload = connected.Reason != null
                ? Gate("Upload", connected)
                : configuration.Reason != null
                    ? Gate("Upload", configuration)
                    : _gate == null
                        ? ClearGate("Upload")
                        : !_gate.Allowed
                            ? new ActionGate("Upload", _gate.Summary, GateReport.Warning)
                            : ClearGate("Upload");

            ActionGate start = connected.Reason != null
                ? Gate("Start", connected)
                : configuration.Reason != null
                    ? Gate("Start", configuration)
                    : _gate == null
                        ? ClearGate("Start")
                        : !_gate.Allowed
                            ? new ActionGate("Start", _gate.Summary, GateReport.Warning)
                            : !uploadedCurrent
                                ? ClearGate("Start")
                                : StateActionGate("Start", requireIdle: true,
                                    requireHomed: true, activity: "starting a job");
            return new GateState(validate, upload, start);
        }

        protected override IEnumerable<ActionGate> ActionGates()
        {
            var gates = BuildGateState();
            return new[] { gates.Validate, gates.Upload, gates.Start };
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
        {
            get
            {
                var s = Session;
                var snap = s?.Latest;
                var st = snap?.Status ?? MachineStatus.Unknown;
                bool uploadedCurrent = _gate != null && _uploadedHash == _gate.Hash && _uploadedPath.Length > 0;
                var gates = BuildGateState();
                string? connectedReason = ConnectedRefusal();
                string? validateReason = gates.Validate.Reason;
                string? uploadReason = gates.Upload.Reason ??
                    (_gate == null ? "validate the job first" : null);
                string? startReason = gates.Start.Reason ??
                    (_gate == null ? "validate the job first" :
                     !uploadedCurrent ? "upload the current G-code first" : null);
                string? pauseReason = connectedReason ?? JobStateRefusal(snap, "pausing",
                    st == MachineStatus.Processing || st == MachineStatus.Simulating);
                string? resumeReason = connectedReason ?? JobStateRefusal(snap, "resuming",
                    st == MachineStatus.Paused);
                string? cancelReason = connectedReason ?? JobStateRefusal(snap, "cancelling",
                    st == MachineStatus.Processing || st == MachineStatus.Simulating ||
                    st == MachineStatus.Paused || st == MachineStatus.Pausing ||
                    st == MachineStatus.Resuming);
                return new[]
                {
                    new[]
                    {
                        ActionButton("validate", ValidateLabel(), _gate == null ? GH_Palette.Warning
                            : _gate.Allowed ? GH_Palette.Black : GH_Palette.Error, validateReason == null, validateReason ?? "Check supplied gcode and limits, with verified homing required when connected to a SCARA"),
                        ActionButton("upload", Busy ? BusyLabel ?? "…" : "Upload",
                            GH_Palette.Black, uploadReason == null, uploadReason ?? "Upload the validated gcode to the connected machine, overwriting jobName without starting it"),
                        ActionButton("start", _dryRun ? "Start (dry M37)" : "Start",
                            GH_Palette.Brown, startReason == null, startReason ?? "Start the unchanged verified upload with one press on an idle, homed machine, using dryRun if set"),
                    },
                    new[]
                    {
                        ActionButton("pause", "Pause", GH_Palette.Grey, pauseReason == null, pauseReason ?? "Pause the connected machine's running job with one press"),
                        ActionButton("resume", "Resume", GH_Palette.Grey, resumeReason == null, resumeReason ?? "Resume the connected machine's paused job after its safety checks pass"),
                        ActionButton("cancel", "Cancel", GH_Palette.Grey, cancelReason == null, cancelReason ?? "Cancel the connected machine's active job with one press"),
                        ActionButton("estop", "E-STOP", GH_Palette.Error, s != null,
                            s == null ? "no session. Use the physical emergency stop" : "Send emergency stop immediately with one press, using the physical stop if the machine does not stop"),
                    },
                };
            }
        }

        static string? JobStateRefusal(ObjectModelSnapshot? snap, string activity, bool allowedStatus)
            => StateGate.RefuseJobControl(snap, activity, allowedStatus);

        string ValidateLabel()
        {
            if (_gate == null) return "Validate";
            return _gate.Allowed ? $"Valid ({_gate.Moves} moves)" : $"DANGER ({_gate.Errors} errors)";
        }

        public override void OnCapsulePressed(string id)
        {
            // The session as it is at the press, for the action bodies below: they run on
            // a worker, where the leasing Session getter could re-register a component
            // that was removed meanwhile and keep its session polling for good.
            var pressedSession = MachineSessionManager.Peek(HostInput);
            switch (id)
            {
                case "validate":
                    if (_profileError != null)
                    {
                        LastActionResult = "ERROR: validation refused: " + _profileError;
                        ExpireFromUi();
                        return;
                    }
                    if (Cartesian && _envelopeUnknown.Count > 0)
                    {
                        LastActionResult = "ERROR: validation refused: machine envelope unknown ("
                            + string.Join(", ", _envelopeUnknown)
                            + "). Wait for the firmware's M208 report or wire the limit inputs";
                        ExpireFromUi();
                        return;
                    }
                    if (!Cartesian && Session != null && Session.State == DuetConnState.Connected
                        && !Session.ArmConfiguration.Known)
                    {
                        LastActionResult = "ERROR: configuration sensitive validation refused. Complete a verified home first";
                        ExpireFromUi();
                        return;
                    }
                    EnsureGateAsync(force: true, "validating", gate =>
                    {
                        LastActionResult = gate.Summary;
                    });
                    return;

                case "upload":
                    EnsureGateAsync(force: false, "validating", gate =>
                    {
                        if (!gate.Allowed)
                        {
                            LastActionResult = "ERROR: " + gate.Summary + ". Upload refused";
                            return;
                        }
                        string remote = RemotePath();
                        byte[] bytes = JobBytes();
                        var motionLines = new List<string>(_gcode);
                        bool blockMotionWarnings = _blockWarnings;
                        RunGatedAction("uploading " + remote, includeJob: false, async (snap, client, ct) =>
                        {
                            EnsureFirmwareSafe(snap, client, gate.InitialConfiguration);
                            string motionNotice = await CheckMotionAtGateAsync(client, motionLines,
                                blockMotionWarnings, ct).ConfigureAwait(false);
                            UploadVerification verification = await client.UploadAsync(remote, bytes, ct);
                            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                            {
                                _uploadedPath = remote;
                                _uploadedHash = gate.Hash;
                                _uploadedSize = verification.Size;
                            }));
                            return (motionNotice.Length == 0 ? "" : motionNotice + ". ")
                                + $"uploaded {remote} ({verification.Size} bytes, {verification.Summary})";
                        });
                    });
                    return;

                case "start":
                    EnsureGateAsync(force: false, "validating", gate =>
                    {
                        if (!gate.Allowed)
                        { LastActionResult = "ERROR: job not valid. Validate and upload first"; return; }
                        if (_uploadedPath.Length == 0 || _uploadedHash != gate.Hash)
                        { LastActionResult = "ERROR: the G-code changed since the last upload. Upload again first"; return; }
                        string path = _uploadedPath;
                        bool dry = _dryRun;
                        bool verified = false;
                        long expectSize = _uploadedSize;
                        byte[] expectedBytes = JobBytes();
                        var motionLines = new List<string>(_gcode);
                        bool blockMotionWarnings = _blockWarnings;
                        RunGatedAction(dry ? "starting dry-run" : "starting print", includeJob: true,
                            async (snap, client, ct) =>
                        {
                            // The gated session is the one current now; the press captured
                            // another if the host input changed during validation, and a
                            // start must not run on B while A is marked as initiating it.
                            if (!ReferenceEquals(MachineSessionManager.Peek(HostInput), pressedSession))
                                throw new DuetException("start refused: the host changed since Start was pressed. Press Start again");
                            EnsureFirmwareSafe(snap, client, gate.InitialConfiguration);
                            // StateGate BEFORE the motion probe, never after: the probe makes three
                            // sequential command round-trips (2 s timeout each) while StateGate refuses
                            // snapshots older than 3 s, so probing first could age `snap` out of the
                            // freshness window and refuse every start as stale.
                            string? stateGate = StateGate.Refuse(snap, requireIdle: true,
                                requireHomed: true, activity: "starting a job");
                            if (stateGate != null) throw new DuetException("start refused: " + stateGate);
                            string motionNotice = await CheckMotionAtGateAsync(client, motionLines,
                                blockMotionWarnings, ct).ConfigureAwait(false);
                            await client.VerifyRemoteContentAsync(path, expectedBytes, ct).ConfigureAwait(false);
                            if (!verified)
                            {
                                // bookkeeping was restored from the .gh file - confirm the
                                // SD card still holds the verified upload before M32/M37
                                long stored;
                                try { stored = await client.GetFileSizeAsync(path, ct); }
                                catch (DuetException ex)
                                {
                                    ClearUploadStateOnUi();
                                    throw new DuetException(
                                        $"SD file missing since the last verified upload ({path}). Upload again", ex);
                                }
                                if (expectSize > 0 && stored != expectSize)
                                {
                                    ClearUploadStateOnUi();
                                    throw new DuetException(
                                        $"SD file changed since the last verified upload ({path}: " +
                                        $"{stored} bytes, expected {expectSize}). Upload again");
                                }
                            }
                            var session = pressedSession;
                            if (session == null) throw new DuetException("start refused: the session disappeared");
                            // Re-gate on a FRESH snapshot immediately before committing motion.
                            // The gate above ran before the motion probe, the remote-content
                            // verification and the size check - up to five sequential round
                            // trips at a 2 s timeout each - so by now `snap` can be far older
                            // than StateGate's 3 s window, and the machine may have been
                            // paused, halted or E-stopped from DWC in the meantime. Gating
                            // early keeps a slow probe from refusing every start as stale;
                            // gating again here is what makes the decision current.
                            var commitSnap = await client.GetSnapshotAsync(includeJob: true, ct)
                                                         .ConfigureAwait(false);
                            // Re-run the firmware/arm-configuration check too, not just the
                            // state gate. A foreign transition during those round trips - a
                            // DWC pause, a cancel, an E-stop - invalidates the tracked arm
                            // configuration, and once the machine settles back to Idle the
                            // state gate alone passes happily while the configuration this
                            // job was validated against is no longer the one the arm is in.
                            EnsureFirmwareSafe(commitSnap, client, gate.InitialConfiguration);
                            string? commitGate = StateGate.Refuse(commitSnap, requireIdle: true,
                                requireHomed: true, activity: "starting a job");
                            if (commitGate != null)
                                throw new DuetException("start refused: " + commitGate);
                            session.MarkInitiatedJob(path);
                            try
                            {
                                session.MarkInitiatedMotion();
                                if (dry) await client.SimulatePrintAsync(path, ct);
                                else await client.StartPrintAsync(path, ct);
                                if (gate.Checks.FinalConfiguration.Known)
                                    session.SetArmConfiguration(gate.Checks.FinalConfiguration);
                            }
                            // Fail-closed cleanup, then rethrow: as in the Duet Console, a
                            // start that did not take must not leave the session believing it
                            // initiated a job. The original exception reaches the caller.
                            catch (Exception)
                            {
                                session.ClearInitiatedJob();
                                throw;
                            }
                            return (motionNotice.Length == 0 ? "" : motionNotice + ". ")
                                + (dry ? "dry-run started: " : "print started: ") + path;
                        });
                    });
                    return;

                case "pause":
                    RunGatedAction("pausing", true, async (snap, c, ct) =>
                    {
                        string? refusal = JobStateRefusal(snap, "pausing",
                            snap.Status == MachineStatus.Processing || snap.Status == MachineStatus.Simulating);
                        if (refusal != null) throw new DuetException("pause refused: " + refusal);
                        pressedSession?.MarkInitiatedMotion();   // pause.g lifts the head
                        await c.PauseAsync(ct); return "pause accepted (M25)";
                    });
                    return;
                case "resume":
                    RunGatedAction("resuming", true, async (snap, c, ct) =>
                    {
                        string? refusal = JobStateRefusal(snap, "resuming", snap.Status == MachineStatus.Paused);
                        if (refusal != null) throw new DuetException("resume refused: " + refusal);
                        pressedSession?.MarkInitiatedMotion();   // resume.g re-primes and returns
                        await c.ResumeAsync(ct); return "resume accepted (M24)";
                    });
                    return;
                case "cancel":
                    RunGatedAction("cancelling", true, async (snap, c, ct) =>
                    {
                        bool active = snap.Status == MachineStatus.Processing || snap.Status == MachineStatus.Simulating
                                   || snap.Status == MachineStatus.Paused || snap.Status == MachineStatus.Pausing
                                   || snap.Status == MachineStatus.Resuming;
                        string? refusal = JobStateRefusal(snap, "cancelling", active);
                        if (refusal != null) throw new DuetException("cancel refused: " + refusal);
                        pressedSession?.MarkInitiatedMotion();   // cancel.g homes
                        await c.CancelAsync(ct);
                        pressedSession?.ClearInitiatedJob();
                        return "cancel accepted (M25+M0)";
                    });
                    return;
                case "estop":
                    RunEmergencyStop();
                    return;
            }
        }

        void ClearUploadStateOnUi()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _uploadedPath = "";
                _uploadedHash = 0;
                _uploadedSize = 0;
            }));
        }

        string RemotePath() => JobSafety.RemotePath(_jobName);

        /// <summary>Ensures <see cref="_gate"/> is current for the present inputs, then
        /// runs <paramref name="onReady"/> on the UI thread with the fresh gate.
        /// A cache hit completes synchronously; a recompute runs
        /// <see cref="JobGate.Validate"/> (full parse + IK + arc + segment checks) on a
        /// worker so it never freezes the Grasshopper canvas - the original defect
        /// behind the Validate/Upload lag. The result is published only if the inputs
        /// behind it are still current (guarded by the content hash).</summary>
        /// <summary>Synchronous gate refresh for the solve path (Compute), cached by
        /// content hash. Kept off the button handlers - those use
        /// <see cref="EnsureGateAsync"/> so the heavy validation never blocks the canvas.</summary>
        void RunGate(bool force)
        {
            var mechanism = Cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara;
            var configuration = Session?.ArmConfiguration;
            string configurationKey = configuration.HasValue && configuration.Value.Known
                ? $"|cfg:{configuration.Value.ArmMode}:{configuration.Value.ThetaTurns}"
                : "|cfg:unknown";
            string policy = "|" + ContentHash.LimitsKey(_lim, _elbow, 0, _arcSupport)
                + "|" + _blockWarnings + "|" + true + configurationKey
                + "|mech:" + (int)mechanism;
            if (!string.Equals(policy, _gatePolicyKey, StringComparison.Ordinal))
            {
                _gateQuickHash = ContentHash.Fnv1aLinesWithSuffix(_gcode, policy);
                _gatePolicyKey = policy;
            }
            if (!force && _gate != null && _gate.Hash == _gateQuickHash) return;
            _gate = JobGate.Validate(_gcode, _lim, _elbow, _arcSupport,
                                     blockOnWarnings: _blockWarnings, mastAsError: true,
                                     initialConfiguration: configuration,
                                     mechanism: mechanism);
        }

        void EnsureGateAsync(bool force, string busyLabel, Action<JobGateResult> onReady)
        {
            // Fail closed before any validation work: an unresolved profile or an
            // unknown Cartesian envelope means there is nothing valid to gate against.
            if (_profileError != null)
            {
                LastActionResult = "ERROR: refused: " + _profileError;
                ExpireFromUi();
                return;
            }
            if (Cartesian && _envelopeUnknown.Count > 0)
            {
                LastActionResult = "ERROR: refused: machine envelope unknown ("
                    + string.Join(", ", _envelopeUnknown)
                    + "). Wait for the firmware's M208 report or wire the limit inputs";
                ExpireFromUi();
                return;
            }
            var mechanism = Cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara;
            // Cheap policy/hash refresh on the UI thread. Must build the exact hash
            // JobGate.Validate computes (policy flags included).
            var initialConfiguration = Session?.ArmConfiguration;
            string configurationKey = initialConfiguration.HasValue && initialConfiguration.Value.Known
                ? $"|cfg:{initialConfiguration.Value.ArmMode}:{initialConfiguration.Value.ThetaTurns}"
                : "|cfg:unknown";
            string policy = "|" + ContentHash.LimitsKey(_lim, _elbow, 0, _arcSupport)
                + "|" + _blockWarnings + "|" + true + configurationKey
                + "|mech:" + (int)mechanism;
            if (!string.Equals(policy, _gatePolicyKey, StringComparison.Ordinal))
            {
                _gateQuickHash = ContentHash.Fnv1aLinesWithSuffix(_gcode, policy);
                _gatePolicyKey = policy;
            }
            ulong quick = _gateQuickHash;
            if (!force && _gate != null && _gate.Hash == quick) { onReady(_gate); ExpireFromUi(); return; }

            // Snapshot the inputs. Compute() swaps _gcode/_lim by reference and never
            // mutates them in place, so these captures are a stable view for the worker.
            var gcode = _gcode; var lim = _lim;
            int elbow = _elbow; bool arcSupport = _arcSupport; bool blockWarnings = _blockWarnings;
            var configuration = initialConfiguration;
            ulong captured = quick;
            BeginAction(busyLabel);
            Task.Run(() =>
            {
                JobGateResult? gate = null; string? err = null;
                try
                {
                    MarkExecuting(busyLabel);
                    gate = JobGate.Validate(gcode, lim, elbow, arcSupport,
                                            blockOnWarnings: blockWarnings, mastAsError: true,
                                            initialConfiguration: configuration,
                                            mechanism: mechanism);
                }
                catch (Exception ex) { err = MachineSession.Flatten(ex); }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    ClearBusy();
                    if (gate != null && _gateQuickHash == captured)
                    {
                        _gate = gate;
                        onReady(gate);
                    }
                    else if (err != null)
                        LastActionResult = "ERROR: validation failed: " + err;
                    else
                        LastActionResult = "inputs changed during validation. Press again";
                    if (OnPingDocument() != null && !Locked) ExpireSolution(true);
                    else Grasshopper.Instances.RedrawCanvas();
                }));
            });
        }

        byte[] JobBytes()
        {
            if (_jobBytes != null && _jobBytesHash == _gcodeContentHash) return _jobBytes;
            _jobBytes = Encoding.UTF8.GetBytes(string.Join("\n", _gcode) + "\n");
            _jobBytesHash = _gcodeContentHash;
            return _jobBytes;
        }

        // The gates themselves live in Core (JobSafety) so the Print Console runs the
        // SAME path; these wrappers only bind the component's state to them.
        void EnsureFirmwareSafe(ObjectModelSnapshot snap, IMachineClient client,
                                ArmConfiguration validatedConfiguration)
            => JobSafety.EnsureFirmwareSafe(snap, client, _lim,
                Cartesian ? RobotMechanism.Cartesian : RobotMechanism.Scara,
                Session?.ArmConfiguration, validatedConfiguration, _profileError);

        async Task<string> CheckMotionAtGateAsync(IMachineClient client, IReadOnlyList<string> lines,
                                                   bool blockWarnings, System.Threading.CancellationToken ct)
        {
            var (notice, warned) = await JobSafety.CheckMotionAtGateAsync(client, lines, blockWarnings, ct)
                                                  .ConfigureAwait(false);
            // The action still succeeds, so the reply gets wrapped as
            // "<action> @ <host> - confirmed by Duet: WARNING: ..." and no prefix
            // test can see the severity. Record it explicitly or the warning is
            // rendered as a grey remark.
            if (warned) LastActionWarned = true;
            return notice;
        }

        // ---- solve ---------------------------------------------------------------

        sealed class Result
        {
            public bool Danger = true;
            public List<Point3d> Problems = new List<Point3d>();
            public List<string> Messages = new List<string>();
            public string Uploaded = ""; public string State = "";
            public double Progress; public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var incomingGcode = GetStringList(DA, "gcode");
            bool changed = incomingGcode.Count != _gcode.Count;
            if (!changed)
                for (int i = 0; i < incomingGcode.Count; i++)
                    if (!string.Equals(incomingGcode[i], _gcode[i], StringComparison.Ordinal)) { changed = true; break; }
            if (changed)
            {
                _gcode = incomingGcode;
                _gcodeContentHash = ContentHash.Fnv1aLines(_gcode);
                _jobBytes = null;
                _gate = null;
                _gatePolicyKey = "";
                _uploadedPath = ""; _uploadedHash = 0; _uploadedSize = 0;
            }
            HostInput = GetString(DA, "host", "").Trim();
            _jobName = GetString(DA, "jobName", "print");
            _dryRun = GetBool(DA, "dryRun", false);

            // _profile before _profileError: a worker racing this solve must never
            // see a cleared error paired with a not-yet-updated profile
            var profile = ResolveControlProfile(GetString(DA, "robot", ""), out string? profileError);
            if (profile != null) _profile = profile;
            _profileError = profileError;
            if (_profileError != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _profileError);

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
            lim.CalibratedHomePose = RobotModel.Default?.Def.CalibratedHomePose;
            lim.HomedArmMode = RobotModel.Default?.Def.HomedArmMode ?? lim.HomedArmMode;
            lim.RequireCalibratedHomePose = true;
            var envelopeUnknown = new List<string>();
            if (Cartesian)
            {
                // A generic Duet machine has no calibrated SCARA home pose and must
                // not inherit the Potterbot's envelope literals. Wired limit inputs
                // win; unwired ones defer to the firmware's M208 report; axes with
                // neither block validation (fail closed, reported at the gate).
                lim.CalibratedHomePose = null;
                lim.RequireCalibratedHomePose = false;
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
                // ZMin: no wired input, deliberately not in the envelope-unknown
                // list - 0 is the conservative bound until the firmware reports one
                var zAxis = live?.Axis("Z");
                lim.ZMin = zAxis != null && zAxis.Max > zAxis.Min ? zAxis.Min : 0.0;
            }
            _envelopeUnknown = envelopeUnknown;
            _lim = lim;
            _elbow = GetInt(DA, "elbow", 1);
            _arcSupport = GetBool(DA, "arcSupport", true);
            _blockWarnings = GetBool(DA, "blockWarnings", false);

            SubscribeTo(Session, SessionChangeKind.Connection | SessionChangeKind.Status | SessionChangeKind.Job);

            // Surface firmware drift with its actual severity. Upload/start
            // re-check the same findings inside the command transaction below.
            if (_profileError == null)
                ReportFirmwareFindings(_lim, _profile.Mechanism);
            if (Session != null && Session.State == DuetConnState.Connected)
            {
                if (!Session.MotionBaselineProbed)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Motion baseline unverified: config.g could not be read, so the motion limit checks are skipped");
                else if (Session.MotionBaseline != null)
                    foreach (string warning in Session.MotionBaseline.Warnings)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Motion baseline: " + warning);
            }

            var res = new Result();
            if (_profileError != null)
            {
                _gate = null;
                res.Danger = true;
                res.Messages.Add("validation unavailable: " + _profileError);
            }
            else if (Cartesian && _envelopeUnknown.Count > 0)
            {
                _gate = null;
                res.Danger = true;
                res.Messages.Add("validation unavailable: machine envelope unknown ("
                    + string.Join(", ", _envelopeUnknown)
                    + "). Connect so the M208 report arrives, or wire the limit inputs");
            }
            else if (_gcode.Count > 0)
            {
                RunGate(force: false);
                res.Danger = !_gate!.Allowed;
                res.Messages.Add(_gate.Summary);
                int shown = 0;
                foreach (var p in _gate.Checks.Problems)
                {
                    if (p.Sev == Severity.Error || p.Sev == Severity.Warning)
                    {
                        // the SCARA gate validates in the machine frame, so its problem
                        // markers are mirrored back next to the Rhino geometry (Core
                        // MachineFrame); a Cartesian machine's frames are identical
                        var at = Cartesian ? p.At
                            : Lugworm.Core.Geometry.MachineFrame.ToRhino(p.At);
                        res.Problems.Add(new Point3d(at.X, at.Y, at.Z));
                    }
                    if (shown++ < 50) res.Messages.Add(p.ToString());
                }
                if (_gate.Checks.Problems.Count > 50)
                    res.Messages.Add($"… {_gate.Checks.Problems.Count - 50} more (see Robot component)");
                if (!_gate.Allowed)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _gate.Summary);
            }
            else
            {
                _gate = null;
                res.Messages.Add("no G-code connected");
            }

            ReportLastAction();
            ReportActionGates();

            var snap = Session?.Latest;
            res.State = snap?.RawStatus ?? "";
            res.Progress = snap?.Job?.FractionPrinted ?? 0.0;
            Message = snap == null ? null
                : snap.Job != null && snap.Job.FractionPrinted > 0
                    ? $"{snap.RawStatus}, {snap.Job.FractionPrinted * 100:0}%"
                    : snap.RawStatus;
            res.Uploaded = _uploadedPath;
            bool stale = _gate != null && _uploadedPath.Length > 0 && _uploadedHash != _gate.Hash;
            res.Info = (stale ? "Upload stale: the G-code changed since the upload\n" : "")
                     + "profile " + (_profileError != null ? "INVALID" : _profile.Id) + "\n"
                     + "job " + RemotePath() + (_dryRun ? " (dry-run mode)" : "") + "\n"
                     + SessionInfo(_profile.Mechanism);
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Danger);
            DA.SetDataList(1, r.Problems);
            DA.SetDataList(2, r.Messages);
            DA.SetData(3, r.Uploaded);
            DA.SetData(4, r.State);
            DA.SetData(5, r.Progress);
            DA.SetData(6, r.Info);
        }

        // ---- persistence: upload bookkeeping survives save/reopen ------------------

        public override bool Write(GH_IWriter writer)
        {
            if (_uploadedPath.Length > 0)
            {
                writer.SetString("iamUploadedPath", _uploadedPath);
                writer.SetString("iamUploadedHash", _uploadedHash.ToString(CultureInfo.InvariantCulture));
                writer.SetString("iamUploadedSize", _uploadedSize.ToString(CultureInfo.InvariantCulture));
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string p = "";
            if (reader.TryGetString("iamUploadedPath", ref p) && !string.IsNullOrEmpty(p)) _uploadedPath = p;
            string h = "";
            if (reader.TryGetString("iamUploadedHash", ref h)
                && ulong.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uh))
                _uploadedHash = uh;
            string sz = "";
            if (reader.TryGetString("iamUploadedSize", ref sz)
                && long.TryParse(sz, NumberStyles.Integer, CultureInfo.InvariantCulture, out long usz))
                _uploadedSize = usz;
            return base.Read(reader);
        }
    }
}
