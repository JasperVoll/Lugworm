using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using Lugworm.Duet;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Opens/closes the WiFi session to the Duet board and reports its
    /// health. Never auto-connects - a session only ever starts from the on-canvas
    /// Connect capsule, and the connected state is deliberately NOT persisted in
    /// the .gh file (reopening a definition must never touch the machine).</summary>
    public class DuetConnectComponent : MachineComponentBase
    {
        public DuetConnectComponent()
            : base("Duet Connect", "Duet",
                   "Connect to a Duet after setting host and pressing Connect, with machine identity checked before control is allowed.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0015-4afb-9c3d-aa0000000015");
        // 3.0.0: superseded by the Duet Console (…0029). Kept, unchanged, so
        // existing definitions keep loading and solving; hidden from the ribbon.
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetConnect();

        string _password = "reprap";
        int _pollMs = 1000;
        DuetConnState _lastConnectionState = DuetConnState.Disconnected;
        // machine identity this definition expects (AUD-SAF-004); persisted in the .gh
        Core.Duet.MachineFingerprint? _expectedFingerprint;
        const string PasswordNote =
            "The password is stored as plain text in the .gh file and sent as plain HTTP on the LAN, so use a trusted network. Intentional spaces are kept.";

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Duet hostname or IP address, optionally with :port, required before pressing Connect", "core", PType.Text)
                .HintText("192.168.1.42"),
            new ParamSpec("password", "Pw", "Duet password (empty = 'reprap'), saved unencrypted in the .gh file and sent over HTTP", "core", PType.Text),
            new ParamSpec("pollMs", "P", "Status poll interval in ms, clamped to 250 to 2000 for the shared session", "core", PType.Integer)
                .Hint(250, 4000, 1000, 0),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("host", "H", "Session host to wire into the other Print components", GH_ParamAccess.item);
            pManager.AddBooleanParameter("connected", "C", "True while connected, even if machine identity checks prevent control", GH_ParamAccess.item);
            pManager.AddTextParameter("status", "S", "Machine status, for example idle, processing or halted", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Connection, machine identity and last-action details", GH_ParamAccess.item);
        }

        sealed class GateState
        {
            public readonly ActionGate Connect, Disconnect;

            public GateState(ActionGate connect, ActionGate disconnect)
            { Connect = connect; Disconnect = disconnect; }
        }

        GateState BuildGateState()
        {
            var s = Session;
            bool live = s != null && s.State == DuetConnState.Connected;
            bool connecting = s != null &&
                (s.State == DuetConnState.Connecting || s.State == DuetConnState.Reconnecting);
            bool hostSet = !string.IsNullOrWhiteSpace(HostInput);
            bool canDisconnect = s != null && s.State != DuetConnState.Disconnected;
            ActionGate connect = !hostSet
                ? new ActionGate("Connect", "set the host input first", GateReport.None)
                : connecting
                    ? new ActionGate("Connect", "connection attempt is already executing", GateReport.Remark)
                    : live
                        ? new ActionGate("Connect", "already connected", GateReport.None)
                        : new ActionGate("Connect", null, GateReport.None);
            ActionGate disconnect = canDisconnect
                ? new ActionGate("Disconnect", null, GateReport.None)
                : new ActionGate("Disconnect", "no active session to release", GateReport.None);
            return new GateState(connect, disconnect);
        }

        protected override IEnumerable<ActionGate> ActionGates()
        {
            var gates = BuildGateState();
            return new[] { gates.Connect, gates.Disconnect };
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
        {
            get
            {
                var s = Session;
                bool live = s != null && s.State == DuetConnState.Connected;
                bool connecting = s != null && (s.State == DuetConnState.Connecting || s.State == DuetConnState.Reconnecting);
                bool hostSet = !string.IsNullOrWhiteSpace(HostInput);
                var gates = BuildGateState();
                string connectLabel = s?.State == DuetConnState.Reconnecting ? "Reconnecting…" :
                                      s?.State == DuetConnState.Connecting ? "Connecting…" : "Connect";
                string? connectReason = gates.Connect.Reason;
                bool canConnect = hostSet && !live && !connecting;
                bool canDisconnect = s != null && s.State != DuetConnState.Disconnected;
                return new[]
                {
                    new[]
                    {
                        ActionButton("connect", connectLabel, Grasshopper.GUI.Canvas.GH_Palette.Black, canConnect, connectReason ?? "Connect using host, password and pollMs from the last solve, then verify the machine identity"),
                        ActionButton("disconnect", "Disconnect", Grasshopper.GUI.Canvas.GH_Palette.Grey,
                            canDisconnect, gates.Disconnect.Reason ?? "Disconnect this host for every component sharing it, without stopping a running print"),
                    },
                };
            }
        }

        public override void OnCapsulePressed(string id)
        {
            switch (id)
            {
                case "connect":
                    if (string.IsNullOrWhiteSpace(HostInput))
                    {
                        LastActionResult = "ERROR: set the host input first";
                        break;
                    }
                    var session = LeaseSession(HostInput, _password, _expectedFingerprint);
                    session.PollIntervalMs = _pollMs;
                    SubscribeTo(session);
                    SetActionLifecycle("connect", "queued and accepted");
                    break;

                case "disconnect":
                    // Releasing this component's lease is not enough: every Duet
                    // component holds its own, so the session kept polling whenever a
                    // Status/Jog/Files component shared the canvas. Disconnect means
                    // disconnect - close the host for all of them.
                    SetActionLifecycle("disconnect", "queued and accepted");
                    ReleaseLease();
                    SubscribeTo(null);
                    MachineSessionManager.CloseHost(HostInput);
                    SetActionLifecycle("disconnect", "confirmed. Session closed and released");
                    break;
            }
            ExpireFromUi();
        }

        sealed class Result
        {
            public string Host = ""; public bool Connected; public string Status = ""; public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            HostInput = GetString(DA, "host", "").Trim();
            string pwd = GetString(DA, "password", "");
            _password = pwd.Length == 0 ? "reprap" : pwd;
            _pollMs = GetInt(DA, "pollMs", 1000);

            var session = Session;
            if (session != null)
            {
                session.PollIntervalMs = _pollMs;
                SubscribeTo(session);
                if (session.State != _lastConnectionState)
                {
                    if (session.State == DuetConnState.Connecting)
                        SetActionLifecycle("connect", "executing");
                    else if (session.State == DuetConnState.Reconnecting)
                        SetActionLifecycle("reconnect", "executing. Waiting for the next poll");
                    else if (session.State == DuetConnState.Connected &&
                             (LastActionResult?.Contains("connect @") == true || LastActionResult?.Contains("reconnect @") == true))
                        SetActionLifecycle("connect", "confirmed. Session connected");
                    else if (session.State == DuetConnState.Error)
                        SetActionLifecycle("connect", "failed. Check the host and password and retry");
                    _lastConnectionState = session.State;
                }
            }

            // a first connection records the machine's identity in the session; adopt
            // it here so saving the definition persists the baseline (AUD-SAF-004)
            string trustInfo = "";
            if (session != null)
            {
                if (session.ExpectationConflict is string conflict)
                {
                    // conflicting persisted identities: do NOT adopt the session's
                    // expectation while unresolved - that would silently converge the
                    // documents' copies and dissolve the conflict without the operator
                    trustInfo = "\nMachine identity conflict: " + conflict;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, conflict);
                }
                else
                {
                    var recorded = session.ExpectedFingerprint;
                    if (recorded != null && !recorded.Matches(_expectedFingerprint))
                        _expectedFingerprint = recorded;
                    trustInfo = session.Trust switch
                    {
                        Core.Duet.MachineTrust.Trusted => $"\nmachine identity: {session.CurrentFingerprint?.Display} (trusted)",
                        Core.Duet.MachineTrust.FirstConnection => $"\nmachine identity: {session.CurrentFingerprint?.Display} (recorded on first connection)",
                        Core.Duet.MachineTrust.Mismatch => "\nMachine identity mismatch: " + session.TrustRefusalReason(),
                        Core.Duet.MachineTrust.Unavailable => "\nMachine identity unavailable: " + session.TrustRefusalReason(),
                        _ => "",
                    };
                    if (session.Trust == Core.Duet.MachineTrust.Mismatch
                        || session.Trust == Core.Duet.MachineTrust.Unavailable)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, session.TrustRefusalReason()!);
                }
            }

            ReportLastAction();
            ReportActionGates();
            return new Result
            {
                Host = HostInput,
                Connected = session != null && session.State == DuetConnState.Connected,
                Status = session?.Latest?.RawStatus ?? "",
                Info = PasswordNote + "\n" + SessionInfo() + trustInfo,
            };
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Host);
            DA.SetData(1, r.Connected);
            DA.SetData(2, r.Status);
            DA.SetData(3, r.Info);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            var trustSession = Session;
            bool canTrust = trustSession != null && trustSession.CurrentFingerprint != null
                            && (trustSession.Trust == Core.Duet.MachineTrust.Mismatch
                                || trustSession.Trust == Core.Duet.MachineTrust.Unavailable
                                || trustSession.Trust == Core.Duet.MachineTrust.Unknown
                                || trustSession.ExpectationConflict != null);
            Menu_AppendItem(menu, "Trust connected machine", (s, e) =>
            {
                var sess = Session;
                if (sess?.CurrentFingerprint == null) return;
                RecordUndoEvent("Trust connected machine");
                sess.TrustConnectedMachine();
                _expectedFingerprint = sess.ExpectedFingerprint;
                ExpireFromUi();
            }, canTrust);
            Menu_AppendItem(menu, "Dump raw object model (fixture capture)", (s, e) =>
            {
                RunAction("dumping model", async (client, ct) =>
                {
                    string json = await client.GetModelRawAsync("", "d99fn", ct);
                    Session?.AppendLog("rr_model dump (" + json.Length + " chars): " + json);
                    return "dumped " + json.Length + " chars to the session log (see Status component)";
                }, stateChanging: false);   // read-only diagnostic stays usable pre-trust
            }, Session != null);
        }

        // never persist a "connected" flag - reopening a file must not touch the machine.
        // The EXPECTED machine identity does persist: it is what lets a reopened
        // definition recognize that a different board answers at the old address.
        public override bool Write(GH_IWriter writer)
        {
            if (!base.Write(writer)) return false;
            writer.SetString("expectedMachineFingerprint", _expectedFingerprint?.Serialize() ?? "");
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            if (!base.Read(reader)) return false;
            string s = "";
            reader.TryGetString("expectedMachineFingerprint", ref s);
            _expectedFingerprint = Core.Duet.MachineFingerprint.TryParse(s);
            return true;
        }
    }
}
