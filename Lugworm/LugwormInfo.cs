using Grasshopper;
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace Lugworm
{
    public class LugwormInfo : GH_AssemblyInfo
    {
        public override string Name => "Lugworm";   // matches the Yak package name

        public override Bitmap Icon
        {
            get
            {
                using var s = typeof(LugwormInfo).Assembly
                    .GetManifestResourceStream("Lugworm.Resources.ClaySlicer_24.png");
                if (s == null) return null!;
                using var img = Image.FromStream(s);
                return new Bitmap(img);   // detached copy - safe after the stream closes
            }
        }

        public override string Description => "Slicer, G-code generator, Duet console and robot tools for paste extrusion in Grasshopper.";

        public override Guid Id => new Guid("034d7258-98ef-46d9-868c-562fdf8c9e76");

        public override string AuthorName => "Jasper Vollmert - IDAC, Hamburg University of Technology";

        public override string AuthorContact => "jasper.vollmert@tuhh.de";

        // "unknown", not a number: a literal that parses as a version would be a
        // second version source next to the csproj <Version> single source (Phase 2b)
        public override string AssemblyVersion => GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
