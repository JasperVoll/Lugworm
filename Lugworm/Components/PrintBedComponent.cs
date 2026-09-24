using System;
using System.Collections.Generic;
using System.Drawing;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Machine;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Single source of truth for print-bed size, shape and position. Before
    /// this existed the same plate was described independently in the placement, robot
    /// simulation and live-view components, with nothing keeping the three in step;
    /// since the 4.0.0 bed contract those components have no plate settings of their
    /// own and draw nothing without this definition. Emits one string they resolve.</summary>
    public sealed class PrintBedComponent : IamComponentBase
    {
        public PrintBedComponent()
            : base("Print Bed", "Bed",
                   "Define the print bed's size, shape and position.",
                   "Lugworm", "2 Place")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0024-4b8b-9c3d-aa0000000024");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.PrintBed();

        // Defaults are the lab's current values, so wiring this component into an
        // existing definition changes nothing that was not already implicit.
        const double DefWidth = 1000.0, DefDepth = 700.0;
        static readonly Point3d DefCenter = new Point3d(468, -441, 0);
        static readonly Point3d DefPrintCenter = new Point3d(339, -193, 0);

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("width", "W", "Bed plate width along Rhino X, mm (the full axis for an ellipse)", "core", PType.Number).Hint(100, 3000, DefWidth, 0),
            new ParamSpec("depth", "D", "Bed plate depth along Rhino Y, mm (the full axis for an ellipse)", "core", PType.Number).Hint(100, 3000, DefDepth, 0),
            new ParamSpec("center", "C", "Plate center in Rhino document units, used for the plate outline and preview", "core", PType.Point),
            new ParamSpec("printCenter", "Pt", "Default part bottom-center position in Rhino document units, forwarded to Place", "core", PType.Point),
            // appended: saved documents keep the socket indices they already had
            new ParamSpec("shape", "S", "Plate shape: 0 = rectangle (default), 1 = ellipse. Equal width and depth make a circle", "core", PType.Integer)
                .Hint(0, 1, 0, 0),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("shape", "Bed shape: create value list",
             new[] { ("Rectangle", "0"), ("Ellipse / circle", "1") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("bed", "B", "Print Bed definition. Wire it into the bed input of Place, Robot Simulation and Live View", GH_ParamAccess.item);
            pManager.AddNumberParameter("width", "W", "Bed width, mm", GH_ParamAccess.item);
            pManager.AddNumberParameter("depth", "D", "Bed depth, mm", GH_ParamAccess.item);
            pManager.AddPointParameter("center", "C", "Plate center in Rhino document units", GH_ParamAccess.item);
            pManager.AddPointParameter("printCenter", "Pt", "Default part bottom-center position in Rhino document units", GH_ParamAccess.item);
            pManager.AddCurveParameter("outline", "O", "Plate boundary curve in Rhino document units", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Bed summary", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public BedDefinition Bed = null!;
            public Curve? Outline;
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            double width = GetNumber(DA, "width", DefWidth);
            double depth = GetNumber(DA, "depth", DefDepth);
            if (width <= 0 || depth <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Bed width and depth must be positive");
                return null;
            }

            var center = GetPoint(DA, "center", DefCenter);
            var printCenter = GetPoint(DA, "printCenter", DefPrintCenter);
            int shapeValue = GetInt(DA, "shape", 0);
            if (shapeValue != 0 && shapeValue != 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Unknown bed shape {shapeValue}. Use 0 for a rectangle or 1 for an ellipse");
                return null;
            }
            var shape = shapeValue == 1 ? BedShape.Ellipse : BedShape.Rectangle;

            // document-unit adapter (AUD-COR-001): the definition is machine config in
            // mm - the point inputs are Rhino coordinates (document units) and convert
            // in; width/depth are mm-labelled. This is the one place the conversion
            // happens; every consumer reads the definition in millimetres.
            double s = DocUnits.ScaleToMm();
            var bed = new BedDefinition(width, depth,
                GhConvert.ToVec(center) * s, GhConvert.ToVec(printCenter) * s,
                double.NaN, double.NaN, shape);

            // the outline preview is the same Core geometry the simulators draw,
            // converted back to document space on the way out
            var poly = new Polyline();
            foreach (var p in bed.Outline()) poly.Add(DocUnits.FromMm(GhConvert.ToPt(p), s));

            Message = shape == BedShape.Ellipse
                ? (Math.Abs(width - depth) < 1e-9 ? $"circle ⌀{width:0}" : $"ellipse {width:0}×{depth:0}")
                : $"{width:0}×{depth:0}";
            return new Result { Bed = bed, Outline = poly.ToPolylineCurve() };
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var value = (Result)result;
            var bed = value.Bed;
            DA.SetData(0, BedDefinitionText.Serialize(bed));
            DA.SetData(1, bed.WidthMm);
            DA.SetData(2, bed.DepthMm);
            double sOut = DocUnits.ScaleToMm();
            DA.SetData(3, DocUnits.FromMm(GhConvert.ToPt(bed.Center), sOut));
            DA.SetData(4, DocUnits.FromMm(GhConvert.ToPt(bed.PrintCenter), sOut));
            DA.SetData(5, value.Outline);
            DA.SetData(6, bed.ToString());
        }
    }
}
