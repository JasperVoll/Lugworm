using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Samples
{
    /// <summary>Stateful action capsules and a dropdown using the inherited attributes.</summary>
    public sealed class CanvasButtonsSample : IamComponentBase, IButtonRowHost, IDropdownHost
    {
        int _count;
        int _step = 1;

        public CanvasButtonsSample()
            : base("Canvas buttons", "Buttons", "Adjust a counter with canvas controls.",
                   "Lugworm Samples", "01 Canvas buttons") { }

        public override Guid ComponentGuid => new Guid("adb5fa0f-9bb2-4ca1-a0a0-315e55adcf65");
        protected override IReadOnlyList<ParamSpec> InputSpecs => Array.Empty<ParamSpec>();
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
            => pManager.AddIntegerParameter("Count", "C", "Current count", GH_ParamAccess.item);

        // IamComponentBase.CreateAttributes already installs IamAttributes.
        public IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows => new[]
        {
            new[]
            {
                new ButtonDef("add", $"Add {_step}", enabled: _count <= 100 - _step,
                              tooltip: "Increase the count up to 100"),
                new ButtonDef("reset", $"Reset {_count}", enabled: _count != 0,
                              tooltip: "Set the count to zero"),
            },
        };

        public void OnCapsulePressed(string id)
        {
            if (Locked) return;
            if (id == "add" && _count <= 100 - _step)
            {
                RecordUndoEvent("Add to count");
                _count += _step;
            }
            else if (id == "reset" && _count != 0)
            {
                RecordUndoEvent("Reset count");
                _count = 0;
            }
            else return;
            Attributes?.ExpireLayout();
            ExpireSolution(true);
        }

        public string DropdownLabel => $"Step {_step}";
        public void FillDropdown(ToolStripDropDown menu)
        {
            foreach (int step in new[] { 1, 5 })
            {
                int selected = step;
                var item = new ToolStripMenuItem($"Step {step}") { Checked = _step == step, Enabled = !Locked };
                item.Click += (sender, args) =>
                {
                    if (Locked || _step == selected) return;
                    RecordUndoEvent("Change step");
                    _step = selected;
                    Attributes?.ExpireLayout();
                    ExpireSolution(true);
                };
                menu.Items.Add(item);
            }
        }

        protected override object Compute(IGH_DataAccess DA) => _count;
        protected override void Emit(IGH_DataAccess DA, object result) => DA.SetData(0, result);

        // Custom state needs serialization for both document saving and undo.
        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("SampleCount", _count);
            writer.SetInt32("SampleStep", _step);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            reader.TryGetInt32("SampleCount", ref _count);
            reader.TryGetInt32("SampleStep", ref _step);
            _count = Math.Max(0, Math.Min(100, _count));
            _step = _step == 5 ? 5 : 1;
            return base.Read(reader);
        }
    }
}
