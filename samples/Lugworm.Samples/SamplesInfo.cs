using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace Lugworm.Samples
{
    /// <summary>Assembly identity for the compile-only developer examples.</summary>
    public sealed class SamplesInfo : GH_AssemblyInfo
    {
        public override string Name => "Lugworm Samples";
        public override string Description => "Compile-only examples of Lugworm canvas features.";
        public override Guid Id => new Guid("a27d10dc-8e77-4754-81ce-b4af01e44820");
        public override Bitmap Icon => null!;
        public override string AuthorName => "";
        public override string AuthorContact => "";
    }
}
