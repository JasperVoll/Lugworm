using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Samples
{
    /// <summary>Optional inputs, an input dependency cascade and categorized outputs.</summary>
    public sealed class FeatureGroupsSample : IamComponentBase
    {
        public FeatureGroupsSample()
            : base("Feature groups", "Groups", "Show optional scale and offset inputs.",
                   "Lugworm Samples", "02 Feature groups") { }

        public override Guid ComponentGuid => new Guid("dff7d527-0b34-4f6f-8198-ef14f0df051c");
        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("Value", "V", "Value to transform", "core", PType.Number).Hint(0, 10, 2),
            new ParamSpec("Scale", "S", "Scale factor", "scale", PType.Number).Hint(0, 5, 1),
            new ParamSpec("Offset", "O", "Offset after scaling", "offset", PType.Number).Hint(-5, 5, 0),
        };
        protected override IReadOnlyList<(string group, string label)> ToggleGroups
            => new[] { ("scale", "Scale inputs"), ("offset", "Offset inputs") };

        protected override void OnGroupsChanged()
        {
            // Enable Scale first. Turning it off also removes the dependent Offset group.
            if (!Active.Contains("scale")) Active.Remove("offset");
        }

        protected override IReadOnlyList<OutputSpec> OutputSpecs => new[]
        {
            new OutputSpec("Result", "R", "Transformed value", "core", PType.Number).Pin(),
            new OutputSpec("Original", "V", "Original value", "details", PType.Number),
        };
        protected override IReadOnlyList<(string group, string label)> OutputToggleGroups
            => new[] { ("details", "Details") };

        protected override object Compute(IGH_DataAccess DA)
        {
            double value = GetNumber(DA, "Value", 2);
            return (value * GetNumber(DA, "Scale", 1) + GetNumber(DA, "Offset", 0), value);
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var (transformed, original) = ((double, double))result;
            // Output indices change when categories are hidden. Emit by name instead.
            SetOut(DA, "Result", transformed);
            SetOut(DA, "Original", original);
        }
        // Inherited Write/Read persist Active and ActiveOutputs in the .gh document.
    }
}
