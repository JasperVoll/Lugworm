using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Lightweight source of the shared robot-definition string consumed by
    /// robot-dependent components. It has no Rhino model or network dependency.</summary>
    public sealed class RobotDefinitionComponent : IamComponentBase
    {
        public RobotDefinitionComponent()
            : base("Robot Definition", "Robot Def",
                   "Select the machine profile the other components work against.",
                   "Lugworm", "4 Robot")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0025-4b9b-9c3d-aa0000000025");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.RobotDefinition();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("preset", "P", "Robot or machine preset. None keeps the toolpath and G-code workflows free of a model",
                "core", PType.Text).HintText("none"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("preset", "Robot preset", new[]
            {
                ("None / toolpath only", "\"none\""),
                ("Generic Cartesian", "\"generic-cartesian\""),
                ("Generic Duet / Cartesian", "\"generic-duet-cartesian\""),
                ("Potterbot SCARA v4", "\"potterbot-scara-v4\""),
                ("Generic Klipper machine", "\"generic-klipper\""),
                ("Generic Marlin machine", "\"generic-marlin\""),
            }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("robot", "R", "Robot definition. Wire it into the robot input of G-code Generator, Robot Simulation, Print Stats, Duet Console and Live View", GH_ParamAccess.item);
            pManager.AddTextParameter("mechanism", "K", "Machine mechanism name, such as SCARA, Cartesian or None", GH_ParamAccess.item);
            pManager.AddTextParameter("program", "P", "Program format supported by the selected profile", GH_ParamAccess.item);
            pManager.AddTextParameter("control", "C", "Direct printer connection type supported by the profile (None = no direct control)", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Selected profile summary", GH_ParamAccess.item);
            // 4.0.0, appended
            pManager.AddTextParameter("process", "Pr", "Material process used by the profile: paste extrusion (LDM)", GH_ParamAccess.item);
            pManager.AddTextParameter("flavor", "Fw", "G-code dialect the generator uses while its firmware input is unwired", GH_ParamAccess.item);
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            try
            {
                var profile = RobotCatalog.Resolve(GetString(DA, "preset", "none"));
                Message = profile.Mechanism == RobotMechanism.None ? "Toolpath only" : profile.DisplayName;
                // Duet Jog / Duet Print / Duet Live accept this definition since
                // 2.2.0 and gate a Cartesian profile on the M208 envelope.
                if (profile.ControlKind == RobotControlKind.Duet
                    && profile.Mechanism != RobotMechanism.Scara)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"{profile.DisplayName}: wire this definition into the Duet Console and " +
                        "Live View to control a generic Duet machine (validation against the firmware's " +
                        "M208 envelope).");
                return profile;
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return null;
            }
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var profile = (RobotProfile)result;
            DA.SetData(0, RobotDefinitionText.Serialize(profile));
            DA.SetData(1, profile.Mechanism.ToString());
            DA.SetData(2, profile.ProgramKind.ToString());
            DA.SetData(3, profile.ControlKind.ToString());
            DA.SetData(4, $"{profile.DisplayName} - frame:{profile.GcodeCoordinateFrame} - model:{(profile.HasKinematics ? profile.ModelKey : "none")}" +
                          $" - process:Paste - flavor:{profile.DefaultFlavor} - control:{profile.ControlKind}");
            DA.SetData(5, "Paste (LDM)");
            DA.SetData(6, profile.DefaultFlavor.ToString());
        }
    }
}
