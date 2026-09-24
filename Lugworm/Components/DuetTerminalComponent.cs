using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Gcode;
using Lugworm.Duet;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Raw single-line G-code console - the bring-up and debug tool (M115,
    /// M122, M669…). Expert use: lines sent here BYPASS the kinematic validation the
    /// jog and print components enforce. Send is capsule-gated like every other
    /// machine action; nothing is ever sent on recompute.</summary>
    public class DuetTerminalComponent : MachineComponentBase
    {
        public DuetTerminalComponent()
            : base("Duet Terminal", "Term",
                   "Send a supplied G-code command to a connected Duet, with expert commands bypassing path safety checks.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0018-4b2b-9c3d-aa0000000018");
        // 3.0.0: superseded by the Duet Console (…0029). Kept, unchanged, so
        // existing definitions keep loading and solving; hidden from the ribbon.
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetTerminal();

        string _line = "";
        bool _expert;
        DateTime _confirmUntil = DateTime.MinValue;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, connected before pressing Send", "core", PType.Text),
            new ParamSpec("line", "G", "One G-code command as text, sent only when Send is pressed (for example M115)", "core", PType.Text).HintText("M115"),
            new ParamSpec("expert", "Ex", "Allow motion, configuration and job commands when true, with two Send presses within 5 s and no path validation", "core", PType.Boolean),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("reply", "R", "Firmware reply to the last sent line", GH_ParamAccess.item);
            pManager.AddTextParameter("log", "L", "Session log, newest last", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows => new[]
        {
            new[]
            {
                SendButton(),
            },
        };

        ButtonDef SendButton()
        {
            string? refusal = SendRefusal();
            bool confirmArmed = IsConfirmArmed();
            string label = confirmArmed ? "Confirm send" : "Send";
            if (Busy && BusyLabel != null) label = BusyLabel;
            return ActionButton("send", label,
                confirmArmed ? GH_Palette.Warning : GH_Palette.Black, refusal == null, refusal ?? "Send the supplied line on the connected host, with expert commands requiring two presses within 5 s");
        }

        bool IsConfirmArmed()
        {
            if (DateTime.UtcNow >= _confirmUntil) return false;
            try { return IsAdvanced(GcodeText.Lex(_line.Trim(), 1)); }
            // Fail closed: a line the lexer rejects is never treated as an armed advanced
            // command. BuildSendGate lexes the same text and is where the operator is told
            // the line was rejected, so this one must not report it twice.
            catch (Exception) { return false; }
        }

        ActionGate BuildSendGate()
        {
            var connection = ConnectedActionGate("Send");
            if (connection.Reason != null) return connection;
            string line = _line.Trim();
            if (line.Length == 0)
                return new ActionGate("Send", "enter one G-code line", GateReport.None);
            GcodeLine parsed;
            try { parsed = GcodeText.Lex(line, 1); }
            catch (Exception ex)
            {
                return new ActionGate("Send", "line rejected: " + ex.Message, GateReport.Remark);
            }
            bool diagnostic = IsDiagnostic(parsed);
            bool advanced = IsAdvanced(parsed);
            if (!diagnostic && !advanced)
                return new ActionGate("Send", "command is not on the diagnostic allowlist", GateReport.Remark);
            if (advanced && !_expert)
                return new ActionGate("Send", "enable expert unlock for motion/config/job commands", GateReport.Remark);
            // The first expert press is intentionally enabled: it arms the
            // confirmation window. The second press is the send gate.
            return new ActionGate("Send", null, GateReport.None);
        }

        string? SendRefusal() => BuildSendGate().Reason;

        protected override IEnumerable<ActionGate> ActionGates()
            => new[] { BuildSendGate() };

        public override void OnCapsulePressed(string id)
        {
            if (id != "send") return;
            string line = _line.Trim();
            if (line.Length == 0)
            {
                LastActionResult = "ERROR: line input is empty";
                ExpireFromUi();
                return;
            }
            GcodeLine parsed;
            try { parsed = GcodeText.Lex(line, 1); }
            catch (Exception ex)
            {
                LastActionResult = "ERROR: terminal line rejected: " + ex.Message;
                ExpireFromUi();
                return;
            }
            bool diagnostic = IsDiagnostic(parsed);
            bool advanced = IsAdvanced(parsed);
            if (!diagnostic && !advanced)
            {
                LastActionResult = "ERROR: terminal command is not on the diagnostic allowlist";
                ExpireFromUi();
                return;
            }
            if (advanced)
            {
                if (!_expert)
                {
                    LastActionResult = "ERROR: motion, config and job commands are refused. Enable the expert unlock";
                    ExpireFromUi();
                    return;
                }
                if (DateTime.UtcNow >= _confirmUntil)
                {
                    _confirmUntil = DateTime.UtcNow.AddSeconds(5);
                    LastActionResult = "expert armed. Click Send again to confirm";
                    ExpireFromUi();
                    _ = Task.Delay(5200).ContinueWith(_ =>
                        Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            if (DateTime.UtcNow >= _confirmUntil) ExpireFromUi();
                        })));
                    return;
                }
                _confirmUntil = DateTime.MinValue;
            }
            var session = Session;
            session?.AppendLog("> " + line);
            RunAction("sending " + line, async (client, ct) =>
            {
                // fire-and-forget: the session's poll loop drains rr_reply (single
                // consumer), so the reply lands in `reply`/`log` within one poll tick
                // This component is the UNCHECKED path: an expert line is raw G-code that
                // never went through the kinematic model, so its motion is opaque. It
                // must INVALIDATE the tracked arm configuration, not authorize it -
                // authorizing would leave the previous configuration trusted while the
                // machine moved somewhere the slicer never modelled. Diagnostic reads
                // (M115, M122, ...) move nothing and are left alone.
                if (advanced)
                    Session?.InvalidateArmConfiguration("raw terminal command, unmodelled motion");
                await client.SendCodeNoReplyAsync(line, ct);
                return "sent. The reply appears with the next poll";
            });
        }

        sealed class Result { public string Reply = ""; public List<string> Log = new List<string>(); public string Info = ""; }

        protected override object? Compute(IGH_DataAccess DA)
        {
            HostInput = GetString(DA, "host", "").Trim();
            _line = GetString(DA, "line", "");
            _expert = GetBool(DA, "expert", false);
            SubscribeTo(Session);

            ReportLastAction();
            ReportActionGates();

            return new Result
            {
                Reply = Session?.LastReply ?? "",
                Log = Session?.LogSnapshot() ?? new List<string>(),
                Info = SessionInfo(),
            };
        }

        // The two lists live in Core so this component and the Duet Console's Terminal
        // tab cannot drift apart; the classification is byte-identical to what this
        // component carried in 2.6.0 and is pinned by TerminalPolicyTests.
        static bool IsDiagnostic(GcodeLine line) => Core.Duet.TerminalPolicy.IsDiagnostic(line);

        static bool IsAdvanced(GcodeLine line) => Core.Duet.TerminalPolicy.IsAdvanced(line);

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Reply);
            DA.SetDataList(1, r.Log);
            DA.SetData(2, r.Info);
        }
    }
}
