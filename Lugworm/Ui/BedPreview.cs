using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;

namespace Lugworm.Ui
{
    /// <summary>Arctic-styled machine bed for the robot simulators: near-white plate
    /// with a soft apron, the actual printable area (bed outline ∩ reach annulus about
    /// the robot origin) in white with a crisp slate outline, unreachable bed in light
    /// grey, 50/250 mm grid, axes and numbering.
    ///
    /// The plate's shape, containment test, outline, bounds and grid clipping all come
    /// from the Core <see cref="BedDefinition"/>, so Live View and Robot Simulation
    /// cannot draw different beds from the same definition. Everything here is
    /// millimetres; the consumers push the document-unit transform when they draw.
    /// Reach is always measured from the robot origin and stays the robot's, not the
    /// plate's.</summary>
    public sealed class BedPreview
    {
        const double GridStep = 50.0;
        const double GridMajor = 250.0;
        const int OutlineSegments = 120;

        // arctic palette
        static readonly Color PrintableColor = Color.FromArgb(248, 249, 250);   // near-white
        static readonly Color UnreachableColor = Color.FromArgb(216, 218, 221); // cool light grey
        static readonly Color PlateSideColor = Color.FromArgb(228, 229, 232);
        static readonly Color FrameColor = Color.FromArgb(148, 153, 160);
        static readonly Color BoundaryColor = Color.FromArgb(96, 116, 146);     // slate accent
        static readonly Color GridMinorColor = Color.FromArgb(42, 130, 134, 140);
        static readonly Color GridMajorColor = Color.FromArgb(90, 110, 116, 124);
        static readonly Color LabelColor = Color.FromArgb(122, 128, 138);
        static readonly Color AxisX = Color.FromArgb(190, 120, 110);
        static readonly Color AxisY = Color.FromArgb(110, 170, 130);

        Mesh? _topMesh;      // printable / unreachable per-vertex colours
        Mesh? _plateMesh;    // apron slab under the top
        readonly List<(Line ln, bool major)> _grid = new();
        readonly List<(Point3d a, Point3d b, Color c)> _axes = new();
        readonly List<(Point3d p, string t)> _labels = new();
        readonly List<Curve> _printableOutline = new();
        Polyline? _frame;
        BedDefinition? _def;
        double _xmin, _xmax, _ymin, _ymax, _rMin, _rMax;
        Func<double, double, bool>? _printableFn;   // optional exact test (e.g. UR IK)

        public Polyline? Frame => _frame;
        public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

        /// <summary>The definition this preview was last built from; null while the
        /// preview is empty (no bed wired).</summary>
        public BedDefinition? Definition => _def;

        public bool OnBed(double x, double y) => _def != null && _def.Contains(x, y);

        public bool Printable(double x, double y) => OnBed(x, y) && Reachable(x, y);

        public void Clear()
        {
            _topMesh = null; _plateMesh = null;
            _grid.Clear(); _axes.Clear(); _labels.Clear(); _printableOutline.Clear();
            _frame = null;
            _def = null;
            _printableFn = null;
            Bounds = BoundingBox.Empty;
        }

        /// <summary>Rectangular convenience overload for the hidden 2.0.0 compatibility
        /// Robot component, whose own width/depth/centre sockets are frozen. Invalid
        /// dimensions clear the preview rather than throwing into a solve.</summary>
        public void Build(Point3d bedC, double bedW, double bedD, double reachMin, double reachMax,
                          Func<double, double, bool>? printableFn = null)
        {
            if (!(bedW > 0) || !(bedD > 0) || !bedC.IsValid) { Clear(); return; }
            var centre = new Vec3(bedC.X, bedC.Y, bedC.Z);
            Build(new BedDefinition(bedW, bedD, centre, centre), reachMin, reachMax, printableFn);
        }

        /// <param name="printableFn">optional exact reachability test at bed level
        /// (e.g. tool-down IK); null → annulus [reachMin, reachMax] about the origin.
        /// The exact test caps the shading grid at 72×72 cells.</param>
        public void Build(BedDefinition bed, double reachMin, double reachMax,
                          Func<double, double, bool>? printableFn = null)
        {
            Clear();
            if (bed == null) return;
            _def = bed;
            _printableFn = printableFn;
            var bounds = bed.Bounds;
            double bz = bed.Center.Z;
            _xmin = bounds.MinX; _xmax = bounds.MaxX;
            _ymin = bounds.MinY; _ymax = bounds.MaxY;
            _rMin = reachMin; _rMax = reachMax;

            _frame = ToPolyline(bed.Outline(OutlineSegments));
            Bounds = _frame.BoundingBox;

            // apron: 20 mm margin, 10 mm thick, top just below the coloured surface
            const double margin = 20.0, plateH = 10.0;
            _plateMesh = BuildApron(bed, margin, plateH, bz);
            if (_plateMesh != null) Bounds = BoundingBox.Union(Bounds, _plateMesh.GetBoundingBox(false));

            _topMesh = BuildTopMesh(bed, bz);

            // grid + axes, both clipped to the outline by Core
            foreach (var (a, b) in bed.GridLines(GridStep))
            {
                bool vertical = Math.Abs(a.X - b.X) < 1e-9;
                _grid.Add((new Line(ToPt(a), ToPt(b)), IsMajor(vertical ? a.X : a.Y)));
            }
            if (bed.SpanX(0, out double axMin, out double axMax))
                _axes.Add((new Point3d(axMin, 0, bz), new Point3d(axMax, 0, bz), AxisX));
            if (bed.SpanY(0, out double ayMin, out double ayMax))
                _axes.Add((new Point3d(0, ayMin, bz), new Point3d(0, ayMax, bz), AxisY));
            BuildLabels(bz);

            // printable-area outline: plate outline ∩ outer reach disc, plus the
            // dead-zone circle where it crosses the plate
            const double tol = 0.01;
            var plate = _frame.ToPolylineCurve();
            var wsPlane = new Plane(new Point3d(0, 0, bz), Vector3d.ZAxis);
            var outer = new Circle(wsPlane, reachMax).ToNurbsCurve();
            try
            {
                var inter = Curve.CreateBooleanIntersection(plate, outer, tol);
                if (inter != null) _printableOutline.AddRange(inter);
            }
            // best effort: a printable-area outline. Rhino does not document what
            // CreateBooleanIntersection throws on a degenerate plate or reach circle, and
            // the plate frame plus the reach rings already drawn say the same thing less
            // precisely - a preview is never worth failing the solve for.
            catch (Exception) { /* keep frame + rings as fallback */ }
            if (reachMin > 1e-6)
            {
                var inner = new Circle(wsPlane, reachMin).ToNurbsCurve();
                try
                {
                    var innerOnBed = Curve.CreateBooleanIntersection(plate, inner, tol);
                    if (innerOnBed != null) _printableOutline.AddRange(innerOnBed);
                }
                // best effort: the inner reach ring, same reasoning as above.
                catch (Exception) { }
            }
        }

        static Point3d ToPt(Vec3 v) => new Point3d(v.X, v.Y, v.Z);

        static Polyline ToPolyline(IReadOnlyList<Vec3> pts)
        {
            var poly = new Polyline(pts.Count);
            foreach (var p in pts) poly.Add(ToPt(p));
            return poly;
        }

        /// <summary>The slab under the coloured surface. A rectangle keeps the box it
        /// always had; any other outline is extruded from an outward-grown copy of the
        /// plate, so a round plate does not sit on a square apron.</summary>
        static Mesh? BuildApron(BedDefinition bed, double margin, double plateH, double bz)
        {
            if (bed.Shape == BedShape.Rectangle)
            {
                var b = bed.Bounds;
                var slab = new BoundingBox(new Point3d(b.MinX - margin, b.MinY - margin, bz - plateH),
                                           new Point3d(b.MaxX + margin, b.MaxY + margin, bz - 0.25));
                return Mesh.CreateFromBox(slab, 1, 1, 1);
            }
            var grown = new BedDefinition(bed.WidthMm + 2 * margin, bed.DepthMm + 2 * margin,
                                          bed.Center, bed.PrintCenter, double.NaN, double.NaN, bed.Shape);
            return Prism(grown.Outline(OutlineSegments), bz - plateH, bz - 0.25);
        }

        /// <summary>Closed prism between two heights from a closed outline (its last
        /// point repeats the first). Used for non-rectangular aprons.</summary>
        static Mesh? Prism(IReadOnlyList<Vec3> loop, double zLow, double zHigh)
        {
            int n = loop.Count - 1;                  // drop the repeated closing point
            if (n < 3) return null;
            var mesh = new Mesh();
            for (int i = 0; i < n; i++) mesh.Vertices.Add(loop[i].X, loop[i].Y, zLow);
            for (int i = 0; i < n; i++) mesh.Vertices.Add(loop[i].X, loop[i].Y, zHigh);
            int cLow = mesh.Vertices.Add(Centroid(loop, n, zLow));
            int cHigh = mesh.Vertices.Add(Centroid(loop, n, zHigh));
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                mesh.Faces.AddFace(i, j, n + j, n + i);   // wall
                mesh.Faces.AddFace(cLow, j, i);           // bottom fan
                mesh.Faces.AddFace(cHigh, n + i, n + j);  // top fan
            }
            mesh.Normals.ComputeNormals();
            return mesh;
        }

        static Point3d Centroid(IReadOnlyList<Vec3> loop, int n, double z)
        {
            double x = 0, y = 0;
            for (int i = 0; i < n; i++) { x += loop[i].X; y += loop[i].Y; }
            return new Point3d(x / n, y / n, z);
        }

        Mesh BuildTopMesh(BedDefinition bed, double bz)
            => bed.Shape == BedShape.Rectangle ? BuildRectangleTop(bz) : BuildRadialTop(bed, bz);

        Mesh BuildRectangleTop(double bz)
        {
            int n = ShadingCells(Math.Max(_xmax - _xmin, _ymax - _ymin));
            var m = new Mesh();
            for (int j = 0; j <= n; j++)
            {
                double y = _ymin + (_ymax - _ymin) * j / n;
                for (int i = 0; i <= n; i++)
                {
                    double x = _xmin + (_xmax - _xmin) * i / n;
                    m.Vertices.Add(x, y, bz);
                    m.VertexColors.Add(Reachable(x, y) ? PrintableColor : UnreachableColor);
                }
            }
            int w = n + 1;
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * w + i;
                    m.Faces.AddFace(a, a + 1, a + 1 + w, a + w);
                }
            m.Normals.ComputeNormals();
            return m;
        }

        /// <summary>Radial fan for an elliptical plate: rings of the outline scaled
        /// towards the centre, so the shaded surface ends exactly on the outline
        /// instead of stepping along a clipped square grid.</summary>
        Mesh BuildRadialTop(BedDefinition bed, double bz)
        {
            int rings = Math.Max(6, ShadingCells(Math.Max(_xmax - _xmin, _ymax - _ymin)) / 2);
            var loop = bed.Outline(OutlineSegments);
            int n = loop.Count - 1;
            var m = new Mesh();
            int centre = m.Vertices.Add(bed.Center.X, bed.Center.Y, bz);
            m.VertexColors.Add(Reachable(bed.Center.X, bed.Center.Y) ? PrintableColor : UnreachableColor);
            for (int r = 1; r <= rings; r++)
            {
                double t = (double)r / rings;
                for (int i = 0; i < n; i++)
                {
                    double x = bed.Center.X + (loop[i].X - bed.Center.X) * t;
                    double y = bed.Center.Y + (loop[i].Y - bed.Center.Y) * t;
                    m.Vertices.Add(x, y, bz);
                    m.VertexColors.Add(Reachable(x, y) ? PrintableColor : UnreachableColor);
                }
            }
            int First(int ring) => centre + 1 + (ring - 1) * n;
            for (int i = 0; i < n; i++)
                m.Faces.AddFace(centre, First(1) + i, First(1) + (i + 1) % n);
            for (int r = 1; r < rings; r++)
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    m.Faces.AddFace(First(r) + i, First(r) + j, First(r + 1) + j, First(r + 1) + i);
                }
            m.Normals.ComputeNormals();
            return m;
        }

        int ShadingCells(double span)
        {
            int cap = _printableFn != null ? 72 : 160;   // exact tests are pricier per vertex
            return Math.Max(20, Math.Min(cap, (int)Math.Ceiling(span / 12.0)));
        }

        bool Reachable(double x, double y)
        {
            if (_printableFn != null) return _printableFn(x, y);
            double r = Math.Sqrt(x * x + y * y);
            return r >= _rMin - 1e-6 && r <= _rMax + 1e-6;
        }

        void BuildLabels(double z)
        {
            bool xAxisOnBed = _ymin <= 0 && 0 <= _ymax;
            bool yAxisOnBed = _xmin <= 0 && 0 <= _xmax;
            double yRow = xAxisOnBed ? 0.0 : _ymin;
            double xCol = yAxisOnBed ? 0.0 : _xmin;
            for (double x = CeilTo(_xmin, GridStep); x <= _xmax + 1e-6; x += GridStep)
                if (IsMajor(x) || GridStep >= 100)
                    _labels.Add((new Point3d(x, yRow - 18, z), Fmt(x)));
            for (double y = CeilTo(_ymin, GridStep); y <= _ymax + 1e-6; y += GridStep)
            {
                if (Math.Abs(y) < 1e-6 && xAxisOnBed) continue;
                if (IsMajor(y) || GridStep >= 100)
                    _labels.Add((new Point3d(xCol - 60, y, z), Fmt(y)));
            }
            _labels.Add((new Point3d(_xmax + 24, yRow, z), "X"));
            _labels.Add((new Point3d(xCol, _ymax + 24, z), "Y"));
        }

        static readonly DisplayMaterial PlateMat = new DisplayMaterial(PlateSideColor)
        {
            Specular = Color.Black,
            Shine = 0.0,
            IsTwoSided = true,
            BackDiffuse = PlateSideColor,
            Emission = Color.FromArgb(46, 46, 47),
        };

        public void DrawMeshes(DisplayPipeline display)
        {
            if (_plateMesh != null) display.DrawMeshShaded(_plateMesh, PlateMat);
            if (_topMesh != null) display.DrawMeshFalseColors(_topMesh);
        }

        public void DrawWires(DisplayPipeline display)
        {
            foreach (var (ln, major) in _grid)
                display.DrawLine(ln, major ? GridMajorColor : GridMinorColor, major ? 2 : 1);
            if (_frame != null && _frame.Count > 1)
                display.DrawPolyline(_frame, FrameColor, 2);
            foreach (var (a, b, c) in _axes) display.DrawLine(new Line(a, b), c, 2);
            foreach (var c in _printableOutline) display.DrawCurve(c, BoundaryColor, 2);
            foreach (var (p, t) in _labels)
                display.Draw3dText(t, LabelColor, new Plane(p, Vector3d.ZAxis), 16.0, "Arial");
        }

        static double CeilTo(double v, double step) => Math.Ceiling(v / step) * step;
        static string Fmt(double v) => Math.Abs(v) < 1e-6 ? "0" : v.ToString("0", CultureInfo.InvariantCulture);
        static bool IsMajor(double v) => Math.Abs(Math.IEEERemainder(v, GridMajor)) < 1e-6;
    }
}
