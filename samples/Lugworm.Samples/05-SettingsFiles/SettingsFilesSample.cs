using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Samples
{
    /// <summary>Inherited settings menus with an explicit stable component key.</summary>
    public sealed class SettingsFilesSample : IamComponentBase
    {
        public SettingsFilesSample()
            : base("Settings files", "Settings", "Save and restore editable input settings.",
                   "Lugworm Samples", "05 Settings files") { }

        public override Guid ComponentGuid => new Guid("a7cb29ee-c91b-425d-97e8-669b722b9206");
        // Keep this key when renaming the class or its display name.
        public override string SettingsKey => "samples.settingsfiles";
        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("Width", "W", "Width, mm", "core", PType.Number).Hint(1, 20, 5, 1),
            new ParamSpec("Enabled", "E", "Enable the width output", "core", PType.Boolean).HintBool(true),
            new ParamSpec("Label", "L", "Label for the output", "core", PType.Text).HintText("Preset"),
            new ParamSpec("Mode", "M", "Width multiplier", "core", PType.Integer).Hint(0, 1, 0),
            new ParamSpec("Geometry", "G", "Geometry excluded from settings", "core", PType.Geometry),
        };
        protected override IReadOnlyList<(string param, string label, (string label, string val)[] items)> ValueLists
            => new[] { ("Mode", "Width mode", new[] { ("Single", "0"), ("Double", "1") }) };

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Width", "W", "Resulting width, mm", GH_ParamAccess.item);
            pManager.AddTextParameter("Label", "L", "Output label", GH_ParamAccess.item);
        }

        protected override object Compute(IGH_DataAccess DA)
        {
            double width = GetNumber(DA, "Width", 5);
            if (!GetBool(DA, "Enabled", true)) width = 0;
            if (GetInt(DA, "Mode", 0) == 1) width *= 2;
            return (width, GetString(DA, "Label", "Preset"));
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var (width, label) = ((double, string))result;
            DA.SetData(0, width);
            DA.SetData(1, label);
        }
    }
}
