using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Lugworm.Core.Calibration;
using Lugworm.Core.Geometry;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Parametric test geometries for calibration prints, selected from an
    /// on-canvas dropdown. Three families:
    ///
    /// <para><b>Free shapes</b> - cylinder, sinusoidal vase, twisted vase, frustum, wall
    /// and box, built from native RhinoCommon primitives (lofts, revolves, boxes) and
    /// sized from the component's own inputs.</para>
    ///
    /// <para><b>Standard specimens</b> (4.2.0) - flexural and compressive test bodies at
    /// the nominal dimensions of DIN EN and ASTM standards, so a printed batch can be
    /// tested against a published method instead of an ad-hoc block. Their dimensions
    /// come from the standard, not from the `height`/`radius`/`length` inputs; the one
    /// control is <c>specimenScale</c>, which exists because a 4 mm ISO 178 bar is
    /// thinner than a single LDM layer and has to be scaled up to be printable at all.
    /// The component reports the three-point bending span alongside the dimensions,
    /// because that is the number the test rig is set to.</para>
    ///
    /// <para><b>Extrusion test path</b> (4.2.0) - a boustrophedon zig-zag line for flow,
    /// line-width and cornering checks. It is a toolpath, not a solid: it leaves
    /// `geometry` empty and comes out of the `path` output, which goes straight into the
    /// G-code generator without being sliced.</para></summary>
    public class SampleGeometryComponent : IamComponentBase, IDropdownHost
    {
        /// <summary>Entries are APPEND-ONLY and never reordered: the selection persists
        /// as its integer value, so a saved document would silently change shape.</summary>
        public enum Shape
        {
            Cylinder, Vase, TwistedVase, Frustum, Wall, Box,
            // --- standard specimens (4.2.0) ---
            BendingEN196, BendingISO178, BendingASTMD790,
            CompressionEN12390Cube, CompressionEN12390Cylinder, CompressionASTMD695,
            // --- extrusion test path (4.2.0) ---
            ZigzagLine,
        }

        /// <summary>The Core specimen behind a standard shape, or null for the free
        /// shapes and the extrusion path. The dimensions themselves live in
        /// <see cref="TestSpecimens"/> so they are Rhino-free and unit-tested.</summary>
        static StandardSpecimen? StandardOf(Shape shape)
        {
            switch (shape)
            {
                case Shape.BendingEN196: return StandardSpecimen.En196Prism;
                case Shape.BendingISO178: return StandardSpecimen.Iso178Bar;
                case Shape.BendingASTMD790: return StandardSpecimen.AstmD790Bar;
                case Shape.CompressionEN12390Cube: return StandardSpecimen.En12390Cube;
                case Shape.CompressionEN12390Cylinder: return StandardSpecimen.En12390Cylinder;
                case Shape.CompressionASTMD695: return StandardSpecimen.AstmD695Prism;
                default: return null;
            }
        }

        Shape _shape = Shape.Vase;

        public SampleGeometryComponent()
            : base("Test Geometry", "TestGeo",
                   "Choose a shape to generate test geometry or a zigzag toolpath using the current inputs and defaults.",
                   "Lugworm", "6 Calibrate")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0010-4aab-9c3d-aa0000000010");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.TestGeo();

        // ---- dropdown ----
        public string DropdownLabel => _shape.ToString();

        public void FillDropdown(ToolStripDropDown menu)
        {
            foreach (Shape s in Enum.GetValues(typeof(Shape)))
            {
                var captured = s;
                var item = new ToolStripMenuItem(s.ToString()) { Checked = s == _shape };
                item.Click += (o, e)
                    =>
                {
                    RecordUndoEvent("Lugworm: test shape");
                    _shape = captured;
                    ExpireSolution(true);
                };
                menu.Items.Add(item);
            }
        }

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("plane", "Pl", "Base plane (default World XY)", "placement", PType.Plane),
            new ParamSpec("height", "H", "Overall sample height in mm", "core", PType.Number).Hint(10, 300, 100, 0),
            new ParamSpec("radius", "R", "Base radius, mm, for cylinder, vase and frustum", "core", PType.Number).Hint(10, 150, 40, 0),
            // profile detail
            new ParamSpec("waveAmp", "Wa", "Vase: radial wave amplitude as fraction of radius (0 = straight)", "profile", PType.Number).Hint(0, 0.5, 0.15, 2),
            new ParamSpec("waveCount", "Wc", "Vase: number of radial waves over the height", "profile", PType.Number).Hint(0, 12, 3, 1),
            new ParamSpec("twistDeg", "Tw", "Twisted vase: total twist over the height, deg", "profile", PType.Number).Hint(0, 360, 90, 0),
            new ParamSpec("sides", "Si", "Vase or frustum section sides (0 to 2 = circle, 3 or more = polygon, twisted vase defaults to 6)", "profile", PType.Integer).Hint(0, 12, 0, 0),
            new ParamSpec("topRatio", "Tr", "Frustum: top radius divided by base radius", "profile", PType.Number).Hint(0.1, 2, 0.6, 2),
            // wall / box dims
            new ParamSpec("length", "L", "Wall length or square box side length in mm", "wallbox", PType.Number).Hint(20, 400, 150, 0),
            new ParamSpec("width", "W", "Wall thickness in mm (the box uses length for both sides)", "wallbox", PType.Number).Hint(2, 200, 8, 0),
            // standard specimens
            new ParamSpec("specimenScale", "Sc", "Uniform multiplier for standard specimen dimensions (1 = nominal size)", "standard", PType.Number).Hint(0.25, 10, 1, 2),
            // extrusion test path
            new ParamSpec("zzLength", "Zl", "Zigzag: length of one straight pass, mm", "zigzag", PType.Number).Hint(20, 500, 150, 0),
            new ParamSpec("zzPitch", "Zp", "Zigzag: distance between passes, mm. Set it to the layer spacing you are testing", "zigzag", PType.Number).Hint(1, 50, 10, 1),
            new ParamSpec("zzPasses", "Zn", "Number of straight zigzag passes, at least 2", "zigzag", PType.Integer).Hint(2, 60, 8),
            new ParamSpec("zzLayers", "Zy", "Number of stacked zigzag layers, at least 1", "zigzag", PType.Integer).Hint(1, 50, 1),
            new ParamSpec("zzLayerHeight", "Zh", "Vertical spacing between zigzag layers in mm, used when zzLayers exceeds 1", "zigzag", PType.Number).Hint(0.2, 20, 3, 2),
            new ParamSpec("zzArcTurns", "Za", "Use semicircular zigzag turns when true, or sharp reversals when false", "zigzag", PType.Boolean).HintBool(true),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("placement", "Base-plane input"),
            ("profile", "Profile detail inputs (waves, twist, sides, frustum ratio)"),
            ("wallbox", "Wall / box dimension inputs"),
            ("standard", "Standard specimen scale (DIN EN / ASTM shapes)"),
            ("zigzag", "Extrusion test path inputs (zig-zag line)"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("geometry", "G", "Test geometry as a Brep. Wire into the slicer", GH_ParamAccess.item);
            pManager.AddCurveParameter("sections", "C", "Cross section curves used for the loft. Empty for box and wall", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Shape summary", GH_ParamAccess.item);
            pManager.AddCurveParameter("path", "P", "Zigzag toolpath in Rhino document units for G-code Generator (empty for solid shapes)", GH_ParamAccess.list);
        }

        sealed class Result
        {
            public Brep? Geo;
            public List<Curve> Sections = new();
            public List<Curve> Path = new();
            public string Info = "";
            /// <summary>True for a shape that is a toolpath, so an empty `geometry` is
            /// the correct result rather than a failure.</summary>
            public bool PathOnly;
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var plane = GetPlane(DA, "plane", Plane.WorldXY);
            double h = Math.Max(1, GetNumber(DA, "height", 100));
            double r = Math.Max(1, GetNumber(DA, "radius", 40));
            double amp = Math.Max(0, GetNumber(DA, "waveAmp", 0.15));
            double waves = Math.Max(0, GetNumber(DA, "waveCount", 3));
            double twist = GetNumber(DA, "twistDeg", 90);
            int sides = Math.Max(0, GetInt(DA, "sides", 0));
            double topRatio = Math.Max(0.05, GetNumber(DA, "topRatio", 0.6));
            double len = Math.Max(1, GetNumber(DA, "length", 150));
            double wid = Math.Max(0.5, GetNumber(DA, "width", 8));
            double specimenScale = Math.Max(0.01, GetNumber(DA, "specimenScale", 1.0));
            // document tolerance converts to mm for the mm-scale build (AUD-COR-001)
            double tol = (Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.01)
                         * Convert.DocUnits.ScaleToMm();

            try
            {
                switch (_shape)
                {
                    case Shape.Cylinder:
                        res.Geo = new Cylinder(new Circle(plane, r), h).ToBrep(true, true);
                        res.Info = $"cylinder r={r:0.#} h={h:0.#}";
                        break;

                    case Shape.Vase:
                        res.Geo = Loft(plane, h, z => r * (1 + amp * Math.Sin(2 * Math.PI * waves * z)), 0, sides, res.Sections, tol);
                        res.Info = $"vase r={r:0.#} h={h:0.#} amp={amp:0.##} waves={waves:0.#}" + (sides >= 3 ? $" sides={sides}" : "");
                        break;

                    case Shape.TwistedVase:
                        int ts = sides >= 3 ? sides : 6;   // twist needs a polygon to be visible
                        res.Geo = Loft(plane, h, z => r * (1 + amp * Math.Sin(2 * Math.PI * waves * z)),
                                       twist * Math.PI / 180.0, ts, res.Sections, tol);
                        res.Info = $"twisted vase r={r:0.#} h={h:0.#} twist={twist:0}° sides={ts}";
                        break;

                    case Shape.Frustum:
                        res.Geo = Loft(plane, h, z => r * (1 + (topRatio - 1) * z), 0, sides, res.Sections, tol);
                        res.Info = $"frustum r={r:0.#}→{r * topRatio:0.#} h={h:0.#}";
                        break;

                    case Shape.Wall:
                        res.Geo = new Box(plane, new Interval(-len / 2, len / 2),
                                          new Interval(-wid / 2, wid / 2), new Interval(0, h)).ToBrep();
                        res.Info = $"wall {len:0.#}×{wid:0.#} h={h:0.#}";
                        break;

                    case Shape.Box:
                        res.Geo = new Box(plane, new Interval(-len / 2, len / 2),
                                          new Interval(-len / 2, len / 2), new Interval(0, h)).ToBrep();
                        res.Info = $"box {len:0.#}×{len:0.#} h={h:0.#}";
                        break;

                    case Shape.ZigzagLine:
                        res.PathOnly = true;
                        BuildZigzag(DA, plane, res);
                        break;

                    default:
                        // every remaining member is a standard specimen
                        BuildStandard(_shape, plane, specimenScale, res);
                        break;
                }
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return null;
            }
            if (res.Geo == null && !res.PathOnly)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Geometry generation failed. Check the inputs");
                return null;
            }
            if (res.PathOnly && res.Path.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Test path generation failed. Check the inputs");
                return null;
            }
            // document-unit adapter (AUD-COR-001): the dimensions above are mm, so in a
            // non-mm document the built geometry scales about the base plane's origin
            // to keep its physical print size
            double unitScale = Convert.DocUnits.ScaleToMm();
            if (!Convert.DocUnits.IsUnity(unitScale))
            {
                var shrink = Transform.Scale(plane.Origin, 1.0 / unitScale);
                res.Geo?.Transform(shrink);
                foreach (var c in res.Sections) c.Transform(shrink);
                foreach (var c in res.Path) c.Transform(shrink);
            }
            return res;
        }

        /// <summary>A standard flexural or compressive specimen at its nominal
        /// dimensions times <paramref name="scale"/>, sitting on the base plane. Bending
        /// bars lie flat with the long axis along X, which is how they are cast and how
        /// they are loaded; compression bodies stand on their base.</summary>
        void BuildStandard(Shape shape, Plane plane, double scale, Result res)
        {
            var which = StandardOf(shape);
            if (which == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"No standard specimen is defined for {shape}");
                return;
            }
            var nominal = TestSpecimens.Nominal(which.Value);
            var q = nominal.Scaled(scale);

            if (q.Round)
                res.Geo = new Cylinder(new Circle(plane, q.X / 2), q.Z).ToBrep(true, true);
            else
                res.Geo = new Box(plane, new Interval(-q.X / 2, q.X / 2),
                                  new Interval(-q.Y / 2, q.Y / 2), new Interval(0, q.Z)).ToBrep();

            res.Info = q.Describe();
            if (Math.Abs(scale - 1.0) > 1e-9)
                res.Info += $"  (scale {scale:0.###} of the nominal {nominal.Describe()})";

            // A section thinner than a layer cannot be printed, and the failure is silent -
            // the slicer just emits a wall or nothing at all. Say so here.
            if (q.Thinnest < TestSpecimens.ThinWarningMm)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"The smallest dimension of this specimen is {q.Thinnest:0.##} mm, thinner than a typical " +
                    "LDM layer. Raise specimenScale, or accept that the printed body will not match the standard");
            if (Math.Abs(scale - 1.0) > 1e-9)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"specimenScale {scale:0.###}: this body is no longer at the dimensions {q.Standard} " +
                    "specifies, so results are comparable within your own series but not to the standard");
        }

        /// <summary>Boustrophedon zig-zag extrusion test path: straight passes along X,
        /// joined alternately at each end, optionally stacked. This is a toolpath, not a
        /// solid - it goes to the G-code generator directly, so its Z is real height and
        /// the caller must not slice it.
        ///
        /// <para>The 180 degree reversals are the point of the test: a straight line only
        /// shows steady-state flow, while the turns show whether the layer tears on
        /// reversal and whether the corner over-deposits. Rounded turns (a half circle of
        /// radius pitch/2) keep the tangent continuous; sharp turns are the harsher
        /// case.</para></summary>
        void BuildZigzag(IGH_DataAccess DA, Plane plane, Result res)
        {
            var options = new ZigzagPathOptions
            {
                PassLength = Math.Max(1, GetNumber(DA, "zzLength", 150)),
                Pitch = Math.Max(0.1, GetNumber(DA, "zzPitch", 10)),
                Passes = Math.Max(2, GetInt(DA, "zzPasses", 8)),
                Layers = Math.Max(1, GetInt(DA, "zzLayers", 1)),
                LayerHeight = Math.Max(0.01, GetNumber(DA, "zzLayerHeight", 3)),
                ArcTurns = GetBool(DA, "zzArcTurns", true),
            };
            var layers = ZigzagPath.Build(options);
            foreach (var layer in layers)
            {
                var pts = new List<Point3d>(layer.Count);
                foreach (var v in layer) pts.Add(plane.PointAt(v.X, v.Y, v.Z));
                res.Path.Add(new PolylineCurve(pts));
            }
            if (res.Path.Count > 0) res.Sections.Add(res.Path[0].DuplicateCurve());

            res.Info = $"zig-zag extrusion test: {options.Passes} passes × {options.PassLength:0.#} mm at " +
                       $"{options.Pitch:0.##} mm pitch, {options.Layers} layer(s), " +
                       $"{(options.ArcTurns ? "rounded" : "sharp")} turns, " +
                       $"{ZigzagPath.TotalLength(layers):0.#} mm of path. " +
                       "Wire `path` into the G-code generator, not the slicer";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                "The zigzag is a toolpath, so geometry is empty on purpose. Wire path straight into the " +
                "crvs input of the G-code generator. Sending it through the slicer would section a curve, not a solid");
        }

        /// <summary>Loft of horizontal sections: radiusAt(z01) with optional linear
        /// twist; circle or polygon cross-sections; capped at the bottom.</summary>
        static Brep? Loft(Plane plane, double h, Func<double, double> radiusAt,
                          double twistRad, int sides, List<Curve> sectionsOut, double tol)
        {
            const int nSec = 24;
            var sections = new List<Curve>();
            for (int i = 0; i <= nSec; i++)
            {
                double z01 = (double)i / nSec;
                double rad = Math.Max(0.1, radiusAt(z01));
                var pl = new Plane(plane.Origin + plane.ZAxis * (z01 * h), plane.XAxis, plane.YAxis);
                pl.Rotate(twistRad * z01, pl.ZAxis, pl.Origin);
                Curve c;
                if (sides >= 3)
                {
                    var pts = Enumerable.Range(0, sides + 1).Select(k =>
                    {
                        double a = 2 * Math.PI * k / sides;
                        return pl.PointAt(rad * Math.Cos(a), rad * Math.Sin(a));
                    }).ToArray();
                    c = new PolylineCurve(pts);
                }
                else
                    c = new Circle(pl, rad).ToNurbsCurve();
                sections.Add(c);
            }
            sectionsOut.AddRange(sections.Where((_, i) => i % 4 == 0));

            var lofts = Brep.CreateFromLoft(sections, Point3d.Unset, Point3d.Unset, LoftType.Normal, false);
            if (lofts == null || lofts.Length == 0) return null;
            var brep = lofts[0];
            var capped = brep.CapPlanarHoles(tol);
            return capped ?? brep;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Geo);
            DA.SetDataList(1, r.Sections);
            DA.SetData(2, r.Info);
            DA.SetDataList(3, r.Path);
        }

        // ---- persistence of the selected shape ----
        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("iamTestShape", (int)_shape);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            int s = 0;
            if (reader.TryGetInt32("iamTestShape", ref s) && Enum.IsDefined(typeof(Shape), s))
                _shape = (Shape)s;
            return base.Read(reader);
        }
    }
}
