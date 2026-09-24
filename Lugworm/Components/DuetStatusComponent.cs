using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Lugworm.Core.Duet;
using Lugworm.Duet;
using Lugworm.Ui;
using Rhino.Geometry;

namespace Lugworm.Components
{
    /// <summary>Live machine dashboard: position, homing, job progress and the reply
    /// log, refreshed by the shared background poll. Read-only - this component can
    /// never send anything.</summary>
    public class DuetStatusComponent : MachineComponentBase
    {
        public DuetStatusComponent()
            : base("Duet Status", "Status",
                   "Read the connected Duet session's status and position without sending printer commands.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0016-4b0b-9c3d-aa0000000016");
        // 3.0.0: superseded by the Duet Console (…0029). Kept, unchanged, so
        // existing definitions keep loading and solving; hidden from the ribbon.
        public override GH_Exposure Exposure => GH_Exposure.hidden;
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetStatus();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, used to read the shared session", "core", PType.Text),
            new ParamSpec("enable", "E", "Refresh outputs on session changes when true (false = read on the next solve)", "core", PType.Boolean)
                .HintBool(true),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("status", "S", "Machine status", GH_ParamAccess.item);
            pManager.AddBooleanParameter("homed", "Hm", "All visible axes homed", GH_ParamAccess.item);
            pManager.AddPointParameter("pos", "P", "Reported nozzle position in Potterbot Rhino coordinates, in mm even when the document uses other units", GH_ParamAccess.item);
            pManager.AddTextParameter("jobFile", "Jf", "Printing file (empty when idle)", GH_ParamAccess.item);
            pManager.AddNumberParameter("progress", "Pr", "Job progress, 0 to 1", GH_ParamAccess.item);
            pManager.AddTextParameter("reply", "R", "Last firmware reply", GH_ParamAccess.item);
            pManager.AddTextParameter("log", "L", "Session log, newest last", GH_ParamAccess.list);
            pManager.AddBooleanParameter("connected", "C", "True while connected, even if machine identity checks prevent control", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Connection and latest status details", GH_ParamAccess.item);
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
            => Array.Empty<IReadOnlyList<ButtonDef>>();
        public override void OnCapsulePressed(string id) { }

        sealed class Result
        {
            public string Status = ""; public bool Homed; public Point3d Pos = Point3d.Unset;
            public string JobFile = ""; public double Progress; public string Reply = "";
            public List<string> Log = new List<string>(); public bool Connected; public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            HostInput = GetString(DA, "host", "").Trim();
            bool enable = GetBool(DA, "enable", true);

            var session = Session;
            SubscribeTo(enable ? session : null);

            var res = new Result { Info = SessionInfo() };
            if (session == null)
            {
                Message = null;
                return res;
            }

            res.Connected = session.State == DuetConnState.Connected;
            res.Reply = session.LastReply ?? "";
            res.Log = session.LogSnapshot();

            var snap = session.Latest;
            if (snap != null)
            {
                res.Status = snap.RawStatus;
                res.Homed = snap.AllHomed;
                var x = snap.Axis("X"); var y = snap.Axis("Y"); var z = snap.Axis("Z");
                if (x != null && y != null && z != null)
                {
                    // the Duet reports its own Cartesian frame; the pos output is the
                    // Rhino-frame point that matches the viewport (Core MachineFrame)
                    Lugworm.Core.Geometry.MachineFrame.ToRhino(
                        x.MachinePosition, y.MachinePosition, out double px, out double py);
                    res.Pos = new Point3d(px, py, z.MachinePosition);
                }
                if (snap.Job != null)
                {
                    res.JobFile = snap.Job.FileName ?? "";
                    res.Progress = snap.Job.FractionPrinted;
                }
                Message = snap.Job != null && snap.Job.FractionPrinted > 0
                    ? $"{snap.RawStatus}, {snap.Job.FractionPrinted * 100:0}%"
                    : snap.RawStatus;
            }
            else Message = null;
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Status);
            DA.SetData(1, r.Homed);
            if (r.Pos.IsValid) DA.SetData(2, r.Pos);
            DA.SetData(3, r.JobFile);
            DA.SetData(4, r.Progress);
            DA.SetData(5, r.Reply);
            DA.SetDataList(6, r.Log);
            DA.SetData(7, r.Connected);
            DA.SetData(8, r.Info);
        }
    }
}
