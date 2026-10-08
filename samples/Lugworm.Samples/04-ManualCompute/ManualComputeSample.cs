using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Samples
{
    /// <summary>The manual compute gate demonstrated with a deliberately cheap calculation.</summary>
    public sealed class ManualComputeSample : IamComponentBase
    {
        public ManualComputeSample()
            : base("Manual compute", "Compute", "Compute a square on demand and retain the last result.",
                   "Lugworm Samples", "04 Manual compute") { }

        public override Guid ComponentGuid => new Guid("ee6be16f-ffe5-497e-a552-09e36dee0735");
        protected override bool Heavy => true;
        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("Value", "V", "Value to square", "core", PType.Number).Hint(-10, 10, 2),
        };
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
            => pManager.AddNumberParameter("Square", "S", "Last computed square", GH_ParamAccess.item);

        protected override object Compute(IGH_DataAccess DA)
        {
            double value = GetNumber(DA, "Value", 2);
            return value * value;
        }

        // Emit must use only the result. Reading fresh inputs here would mix old and new state.
        protected override void Emit(IGH_DataAccess DA, object result) => DA.SetData(0, result);
    }
}
