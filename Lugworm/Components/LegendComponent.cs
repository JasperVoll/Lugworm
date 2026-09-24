using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Core.Analysis;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>The legend Grasshopper never had: explains any data at any range as a
    /// continuous ramp, thresholded classes, named groups or a size scale, with
    /// named or custom colour schemes, fonts, sizes and three placements - anchored
    /// to the viewport, fixed to a plane, or floating as a camera-facing billboard
    /// at a point. Consumes the <c>iamlab-legend:v1</c> wire that Overhang
    /// and Interlayer Time emit, so a legend can never disagree with the colours
    /// those components painted; every explicit input overrides the wire. Also a
    /// colouriser: the <c>colors</c> output paints any geometry with the same scale.
    /// Schemes come from the Core palette registry - 35 matplotlib and R <c>grDevices</c>
    /// palettes, by name or frozen number - and the <c>gradient</c> input reads Grasshopper's own Gradient component
    /// grip by grip, positions included. The model-space <c>mesh</c> and <c>frames</c>
    /// outputs are preview-hidden so only this component's own draw shows.
    /// The scale itself is Core (<see cref="LegendSpec"/>); this component only
    /// resolves inputs, lays out and draws.</summary>
    public sealed class LegendComponent : IamComponentBase
    {
        public LegendComponent()
            : base("Legend", "Legend",
                   "Draw a legend for a color scale or value range.",
                   "Lugworm", "5 Analyze")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0032-4c0b-9c3d-aa0000000032");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Legend();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("legend", "Lg", "Legend definition text from Overhang or Interlayer Time, with wired inputs overriding its matching fields", "core", PType.Text),
            new ParamSpec("values", "V", "Data to color. Also sets the domain when min and max are unwired. The colors output gives one color per value", "core", PType.Number, GH_ParamAccess.list),
            new ParamSpec("min", "Mn", "Domain start. Unwired takes the wire's, else the values' minimum, else 0", "core", PType.Number).Hint(0, 100, 0, 1),
            new ParamSpec("max", "Mx", "Domain end. Unwired takes the wire's, else the values' maximum, else 1", "core", PType.Number).Hint(0, 1000, 100, 1),
            new ParamSpec("type", "Ty", "0 = continuous ramp, 1 = classes (threshold bands), 2 = groups (named categories), 3 = size (circle size scale)", "core", PType.Integer).Hint(0, 3, 0, 0),
            new ParamSpec("scheme", "Sc", "Palette name or number from the palette table, or custom to use colors or gradient", "core", PType.Text).HintText("blues"),
            new ParamSpec("title", "T", "Title above the legend", "core", PType.Text),
            new ParamSpec("show", "S", "Draw the legend in the viewport (outputs solve either way)", "core", PType.Boolean).HintBool(true),
            // style
            new ParamSpec("colors", "Cs", "Custom colors: ramp stops for continuous and size, or one per class or group", "style", PType.Colour, GH_ParamAccess.list),
            new ParamSpec("gradient", "Gr", "Grasshopper Gradient or color list for custom colors, overriding colors and preserving Gradient stop positions", "style", PType.Colour, GH_ParamAccess.list),
            new ParamSpec("breaks", "Bk", "Classes: ascending thresholds, k thresholds give k+1 classes. Size: the sample values shown", "style", PType.Number, GH_ParamAccess.list),
            new ParamSpec("labels", "Lb", "Class or group names. Classes without a name show their range", "style", PType.Text, GH_ParamAccess.list),
            new ParamSpec("unit", "U", "Unit appended to every number", "style", PType.Text),
            new ParamSpec("reverse", "Rv", "Flip the scale direction", "style", PType.Boolean),
            new ParamSpec("naLabel", "Na", "Label of the grey 'unavailable' swatch (empty = no swatch unless the wire carries one)", "style", PType.Text),
            // text
            new ParamSpec("font", "F", "Installed font name for legend labels (default Segoe UI)", "text", PType.Text).HintText("Segoe UI"),
            new ParamSpec("textSize", "Ts", "Text height: px for viewport and floating, model units for a plane", "text", PType.Number).Hint(1, 200, 11, 1),
            new ParamSpec("textColor", "Tc", "Text and tick color", "text", PType.Colour),
            new ParamSpec("decimals", "Dc", "Decimal places (-1 = from the tick step)", "text", PType.Integer).Hint(-1, 6, -1, 0),
            new ParamSpec("ticks", "Tk", "Number of continuous-scale ticks (0 = automatic spacing)", "text", PType.Integer).Hint(0, 20, 0, 0),
            new ParamSpec("frame", "Fr", "Draw a panel behind the legend", "text", PType.Boolean),
            // placement
            new ParamSpec("placement", "Pl", "0 = viewport overlay, 1 = fixed to plane in model units, 2 = floating billboard at point, facing the camera at constant screen size", "placement", PType.Integer).Hint(0, 2, 0, 0),
            new ParamSpec("anchor", "An", "Anchor index: 0 = TL, 1 = TR, 2 = BL, 3 = BR, 4 = top, 5 = bottom, 6 = left, 7 = right", "placement", PType.Integer).Hint(0, 7, 2, 0),
            new ParamSpec("orientation", "Or", "0 = horizontal, 1 = vertical", "placement", PType.Integer).Hint(0, 1, 0, 0),
            new ParamSpec("plane", "P", "Plane for placement 1 (origin = the anchored corner)", "placement", PType.Plane),
            new ParamSpec("point", "Pt", "Anchor point for placement 2", "placement", PType.Point),
            new ParamSpec("length", "L", "Bar length: px for viewport and floating, model units for a plane", "placement", PType.Number).Hint(10, 5000, 220, 0),
            new ParamSpec("thickness", "Th", "Bar thickness, swatch size or largest size circle radius, same unit as length", "placement", PType.Number).Hint(1, 1000, 14, 0),
            new ParamSpec("margin", "Mg", "Viewport: distance from the viewport edge, px", "placement", PType.Number).Hint(0, 500, 18, 0),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("style", "Style inputs (custom colors, breaks, labels, unit, reverse, n/a swatch)"),
            ("text", "Text inputs (font, size, color, decimals, ticks, frame)"),
            ("placement", "Placement inputs (viewport / plane / floating, anchor, orientation, geometry)"),
        };

        protected override IReadOnlyList<(string param, string label, (string label, string val)[] items)> ValueLists => new[]
        {
            ("type", "Legend type", new[] { ("continuous", "0"), ("classes", "1"), ("groups", "2"), ("size", "3") }),
            ("scheme", "Color scheme", SchemeItems),
            ("font", "Font", new[]
            {
                ("Segoe UI", "\"Segoe UI\""), ("Arial", "\"Arial\""), ("Calibri", "\"Calibri\""),
                ("Consolas", "\"Consolas\""), ("Cambria", "\"Cambria\""), ("Verdana", "\"Verdana\""),
                ("Times New Roman", "\"Times New Roman\""),
            }),
            ("placement", "Placement", new[] { ("viewport overlay", "0"), ("fixed to plane", "1"), ("floating at point", "2") }),
            ("anchor", "Anchor", new[]
            {
                ("top-left", "0"), ("top-right", "1"), ("bottom-left", "2"), ("bottom-right", "3"),
                ("top", "4"), ("bottom", "5"), ("left", "6"), ("right", "7"),
            }),
            ("orientation", "Orientation", new[] { ("horizontal", "0"), ("vertical", "1") }),
        };

        /// <summary>The scheme value list, built once from the Core registry: every palette
        /// in number order, which groups them by kind, labelled with its kind and number
        /// ("Diverging: coolwarm (14)") and valued by its bare name, then "custom" last.
        /// Values stay names so settings files and saved lists keep matching.</summary>
        static readonly (string, string)[] SchemeItems = BuildSchemeItems();

        static (string, string)[] BuildSchemeItems()
        {
            var all = ColorSchemes.All;
            var items = new (string, string)[all.Count + 1];
            for (int i = 0; i < all.Count; i++)
                items[i] = (all[i].Kind + ": " + all[i].Name + " (" + i.ToString(CultureInfo.InvariantCulture) + ")",
                            "\"" + all[i].Name + "\"");
            items[all.Count] = ("Custom (colors / gradient input)", "\"custom\"");
            return items;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddColourParameter("colors", "C", "One color per values entry from this scale. Paints any geometry consistently with the legend", GH_ParamAccess.list);
            pManager.AddMeshParameter("mesh", "M", "The legend as a vertex colored mesh in model space for plane and floating placements. Bakeable", GH_ParamAccess.item);
            pManager.AddTextParameter("labels", "Lb", "Legend texts, one per frames plane, for plane and floating placements. Feed a Text Tag 3D or bake", GH_ParamAccess.list);
            pManager.AddPlaneParameter("frames", "Fp", "Text planes in Rhino document units, one per labels entry", GH_ParamAccess.list);
            pManager.AddTextParameter("spec", "Sp", "Resolved legend definition text to forward to another Legend", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Resolved scale, range and placement details", GH_ParamAccess.item);
            // the mesh and its text planes are drawn by this component itself - Grasshopper's
            // own preview would double them (wire edges on a plane, a second legend when floating)
            pManager.HideParameter(1);
            pManager.HideParameter(3);
        }

        sealed class Result
        {
            public LegendSpec Spec = new LegendSpec(LegendType.Continuous);
            public LegendStyle Style = new LegendStyle();
            public LegendPlacement Placement;
            public LegendAnchor Anchor = LegendAnchor.BottomLeft;
            public Plane Plane = Plane.WorldXY;
            public Point3d Point = Point3d.Origin;
            public bool Show = true;
            public List<Color> Colors = new();
            public Mesh? Mesh;
            public List<string> Labels = new();
            public List<Plane> Frames = new();
            /// <summary>The plane placement's texts, kept from the solve so the viewport
            /// draw does not rebuild the whole mesh for them on every frame.</summary>
            public List<Text3d> Texts = new();
            public BoundingBox Box = BoundingBox.Empty;
            public string Info = "";
            public LegendLayout? Layout;
        }

        Result? _last;
        /// <summary>The result the previous <see cref="Emit"/> published - kept apart from
        /// <see cref="_last"/>, which <see cref="Compute"/> has already replaced by the
        /// time Emit compares the two.</summary>
        Result? _emitted;

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var notes = new List<string>();

            // 1. the wire, when present and valid
            LegendSpec? wire = null;
            string wireText = GetString(DA, "legend", "");
            if (!string.IsNullOrWhiteSpace(wireText))
            {
                if (LegendSpec.TryParse(wireText, out wire, out string? err)) notes.Add("wire");
                else AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Legend wire ignored: " + err);
            }

            // 2. explicit inputs override the wire field by field; unwired inputs keep it
            var values = GetNumberList(DA, "values");
            double vMin = double.NaN, vMax = double.NaN;
            foreach (double v in values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                vMin = double.IsNaN(vMin) ? v : Math.Min(vMin, v);
                vMax = double.IsNaN(vMax) ? v : Math.Max(vMax, v);
            }
            LegendType type = wire?.Type ?? LegendType.Continuous;
            if (IsWired("type")) type = (LegendType)Math.Max(0, Math.Min(3, GetInt(DA, "type", 0)));
            double min = IsWired("min") ? GetNumber(DA, "min", 0) : wire?.Min ?? (double.IsNaN(vMin) ? 0 : vMin);
            double max = IsWired("max") ? GetNumber(DA, "max", 1) : wire?.Max ?? (double.IsNaN(vMax) ? 1 : vMax);
            if (!IsWired("min") && !IsWired("max") && wire == null && !double.IsNaN(vMin) && Math.Abs(vMax - vMin) < 1e-12)
            { max = vMin + 1; notes.Add("flat values: domain widened by 1"); }
            string scheme = IsWired("scheme") ? GetString(DA, "scheme", "blues") : wire?.Scheme ?? ColorSchemes.Blues;
            if (!ColorSchemes.IsKnown(scheme))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Unknown scheme '{scheme}'. Using blues. Give a palette name or a number 0 to "
                    + (ColorSchemes.All.Count - 1).ToString(CultureInfo.InvariantCulture)
                    + " from the palette table. The ggsci palettes and the rarely used matplotlib and R ones were removed");
                scheme = ColorSchemes.Blues;
            }
            else scheme = ColorSchemes.Normalize(scheme);
            var customColors = new List<Rgb>();
            var customStops = new List<double>();
            if (IsWired("colors"))
            {
                foreach (var c in GetColourList(DA, "colors")) customColors.Add(new Rgb(c.R, c.G, c.B));
                if (customColors.Count > 0 && !IsWired("scheme")) scheme = ColorSchemes.Custom;
            }
            else if (wire != null) { customColors.AddRange(wire.Colors); customStops.AddRange(wire.Stops); }
            if (IsWired("gradient"))
            {
                var gColors = new List<Rgb>();
                var gStops = new List<double>();
                int grips = ReadGradientGrips(gColors, gStops);
                if (grips == 0)
                {
                    // any other colour list: evenly spaced stops, no positions
                    gColors.Clear(); gStops.Clear();
                    foreach (var c in GetColourList(DA, "gradient")) gColors.Add(new Rgb(c.R, c.G, c.B));
                }
                if (gColors.Count > 0)
                {
                    if (IsWired("colors"))
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Gradient wins over colors");
                    customColors = gColors;
                    customStops = gStops;
                    if (!IsWired("scheme")) scheme = ColorSchemes.Custom;
                    if (grips > 0) notes.Add($"gradient: {grips} grips");
                }
            }
            var breaks = IsWired("breaks") ? GetNumberList(DA, "breaks") : new List<double>(wire?.Breaks ?? Array.Empty<double>());
            var labels = IsWired("labels") ? GetStringList(DA, "labels") : new List<string>(wire?.Labels ?? Array.Empty<string>());
            string title = IsWired("title") ? GetString(DA, "title", "") : wire?.Title ?? "";
            string unit = IsWired("unit") ? GetString(DA, "unit", "") : wire?.Unit ?? "";
            bool reverse = IsWired("reverse") ? GetBool(DA, "reverse", false) : wire?.Reverse ?? false;
            int decimals = IsWired("decimals") ? GetInt(DA, "decimals", -1) : wire?.Decimals ?? -1;
            int ticks = IsWired("ticks") ? GetInt(DA, "ticks", 0) : wire?.Ticks ?? 0;
            (Rgb, string)? na = wire?.Unavailable;
            string naLabel = GetString(DA, "naLabel", "");
            if (IsWired("naLabel"))
                na = naLabel.Length > 0 ? (Rgb.Of(PlasmaColorScale.Unavailable), naLabel) : null;
            var marks = wire?.Marks;

            res.Spec = new LegendSpec(type, title, unit, min, max, scheme, customColors, breaks, labels, marks, na, reverse, decimals, ticks, stops: customStops);

            // 3. style and placement
            var style = res.Style;
            style.Orientation = GetInt(DA, "orientation", 0) == 1 ? LegendOrientation.Vertical : LegendOrientation.Horizontal;
            style.Length = Math.Max(1, GetNumber(DA, "length", 220));
            style.Thickness = Math.Max(1, GetNumber(DA, "thickness", 14));
            style.TextSize = Math.Max(1, GetNumber(DA, "textSize", 11));
            string font = GetString(DA, "font", "Segoe UI").Trim();
            style.Font = font.Length > 0 ? font : "Segoe UI";
            var tc = GetColour(DA, "textColor", Color.Empty);
            if (!tc.IsEmpty) style.TextColor = tc;
            style.Frame = GetBool(DA, "frame", false);
            style.Margin = Math.Max(0, GetNumber(DA, "margin", 18));
            res.Placement = (LegendPlacement)Math.Max(0, Math.Min(2, GetInt(DA, "placement", 0)));
            res.Anchor = (LegendAnchor)Math.Max(0, Math.Min(7, GetInt(DA, "anchor", 2)));
            res.Plane = GetPlane(DA, "plane", Plane.WorldXY);
            res.Point = GetPoint(DA, "point", Point3d.Origin);
            res.Show = GetBool(DA, "show", true);
            if (res.Placement == LegendPlacement.Plane && !res.Plane.IsValid)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Invalid plane. Using World XY"); res.Plane = Plane.WorldXY; }

            // 4. outputs that do not depend on the camera
            foreach (double v in values)
            {
                var c = res.Spec.ColorFor(v);
                res.Colors.Add(Color.FromArgb(c.R, c.G, c.B));
            }
            res.Layout = LegendLayout.Build(res.Spec, style);
            if (res.Placement == LegendPlacement.Plane)
            {
                var (mesh, texts, frames, labelTexts, box) = LegendRenderer.Build3d(res.Layout, style, res.Plane, 1.0, res.Anchor);
                res.Mesh = mesh; res.Texts = texts; res.Frames = frames; res.Labels = labelTexts; res.Box = box;
            }
            else if (res.Placement == LegendPlacement.Floating)
            {
                // the mesh output uses a nominal scale (one model unit per pixel) - the
                // viewport draw rescales per frame to hold the screen size
                var (mesh, _, frames, labelTexts, box) = LegendRenderer.Build3d(res.Layout, style, BillboardPlane(res.Point, null), 1.0, res.Anchor);
                res.Mesh = mesh; res.Frames = frames; res.Labels = labelTexts; res.Box = box;
                notes.Add("floating: mesh/frames at 1 unit per px, viewport draw keeps the screen size");
            }

            var inv = CultureInfo.InvariantCulture;
            res.Info = string.Join(" - ", new[]
            {
                res.Spec.Type.ToString().ToLowerInvariant(),
                res.Spec.Type == LegendType.Continuous || res.Spec.Type == LegendType.Size
                    ? $"[{res.Spec.Min.ToString("0.###", inv)}, {res.Spec.Max.ToString("0.###", inv)}]"
                    : $"{res.Spec.ClassCount} classes",
                res.Spec.Scheme,
                $"{values.Count} value(s)",
                res.Placement.ToString().ToLowerInvariant() + "/" + res.Anchor.ToString().ToLowerInvariant(),
                string.Join(", ", notes),
            });
            Message = res.Spec.Type.ToString();
            _last = res;
            return res;
        }

        /// <summary>Reads the grips of a Grasshopper Gradient component wired into
        /// 'gradient': one colour and its position per grip, ascending. A discontinuous
        /// grip contributes both of its colours at the same position, which the Core ramp
        /// reads as a hard break. Returns the grip count, or 0 when the source is not a
        /// Gradient component (the caller then takes the plain colour list) - anything
        /// unexpected on the canvas ends up as that same 0, never as an exception.</summary>
        int ReadGradientGrips(List<Rgb> colors, List<double> stops)
        {
            try
            {
                var param = Params.Input.First(p => p.Name == "gradient");
                if (param.SourceCount == 0) return 0;
                var source = param.Sources[0];
                var obj = source?.Attributes?.GetTopLevel?.DocObject;
                if (!(obj is Grasshopper.Kernel.Special.GH_GradientControl control)) return 0;
                var gradient = control.Gradient;
                if (gradient == null || gradient.GripCount < 1) return 0;

                var grips = new List<(double T, Rgb Left, Rgb Right)>(gradient.GripCount);
                for (int i = 0; i < gradient.GripCount; i++)
                {
                    var grip = gradient[i];
                    if (grip == null) continue;
                    double t = grip.Parameter;
                    if (double.IsNaN(t) || double.IsInfinity(t)) continue;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    var l = grip.ColourLeft; var r = grip.ColourRight;
                    grips.Add((t, new Rgb(l.R, l.G, l.B), new Rgb(r.R, r.G, r.B)));
                }
                if (grips.Count == 0) return 0;
                foreach (var g in grips.OrderBy(g => g.T))   // OrderBy is stable: equal parameters keep their grip order
                {
                    colors.Add(g.Left); stops.Add(g.T);
                    if (!g.Right.Equals(g.Left)) { colors.Add(g.Right); stops.Add(g.T); }
                }
                return grips.Count;
            }
            catch (Exception ex)
            {
                // 0 still means "take the plain colour list", as the summary promises, so
                // the fallback below is unchanged. But a Gradient component we FAILED to
                // read is not the same thing as a source that was never one, and the
                // operator cannot tell the two apart from the drawn legend. Say which.
                // Broad on purpose: GH_GradientControl's grip accessors are Grasshopper
                // internals with no documented exception set.
                colors.Clear(); stops.Clear();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The wired Gradient could not be read (" + ex.Message
                    + "). Its grip positions were ignored and its colors spaced evenly");
                return 0;
            }
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            bool drawChanged = DrawStateChanged(_emitted, r);
            _emitted = r;
            _last = r;
            // Grasshopper redraws the Rhino views after every top-level solution on its
            // own; this is the explicit preview invalidation for the one transition an
            // operator watches for - the legend going away, or moving to another
            // placement - posted after the solve so it never interleaves with it.
            if (drawChanged) RequestPreviewExpiry();
            DA.SetDataList(0, r.Colors);
            DA.SetData(1, r.Mesh);
            DA.SetDataList(2, r.Labels);
            DA.SetDataList(3, r.Frames);
            DA.SetData(4, r.Spec.Serialize());
            DA.SetData(5, r.Info);
        }

        /// <summary>True when what the viewport shows changed in kind: the legend
        /// appeared or disappeared, or moved between the overlay and a model-space
        /// placement. Colour and text changes need no special case - the ordinary
        /// post-solution redraw repaints a legend that is still there.</summary>
        static bool DrawStateChanged(Result? previous, Result next)
        {
            if (previous == null) return false;
            bool drewBefore = previous.Show && previous.Layout != null;
            bool drawsNow = next.Show && next.Layout != null;
            return drewBefore != drawsNow || (drawsNow && previous.Placement != next.Placement);
        }

        static int _expiryPending;

        /// <summary>ExpirePreview(true) on the UI thread once the solution has returned:
        /// Grasshopper drops its preview caches and asks Rhino to redraw the views.</summary>
        void RequestPreviewExpiry()
        {
            if (Interlocked.CompareExchange(ref _expiryPending, 1, 0) != 0) return;
            try
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try { ExpirePreview(true); }
                    finally { Interlocked.Exchange(ref _expiryPending, 0); }
                }));
            }
            // best effort: a preview redraw. Rhino does not document what the UI-thread
            // marshal throws while the host is closing, so this stays broad - but the
            // pending flag MUST be released or no later toggle ever asks for one again.
            catch (Exception) { Interlocked.Exchange(ref _expiryPending, 0); }
        }

        // ---- viewport ----

        static Plane BillboardPlane(Point3d at, RhinoViewport? vp)
        {
            if (vp == null) return new Plane(at, Vector3d.XAxis, Vector3d.ZAxis);
            var x = vp.CameraX; var y = vp.CameraY;
            if (!x.IsValid || !y.IsValid || x.IsZero || y.IsZero) return new Plane(at, Vector3d.XAxis, Vector3d.ZAxis);
            return new Plane(at, x, y);
        }

        /// <summary>A degenerate but VALID box at the world origin: what the component
        /// reports whenever it has no model-space extent (the viewport overlay always,
        /// every placement while 'show' is off). Grasshopper unions the clipping boxes of
        /// its preview objects and hands the union to Rhino's display pipeline
        /// (<c>DisplayPipeline.CalculateBoundingBox</c>, skipped when the union is
        /// invalid - read from Grasshopper 8.34's IL, 2026-09-16), and Rhino 8 skips the
        /// regeneration of a preview that reports no box (the documented Rhino 8
        /// regression for custom draws without a clipping box) - so a legend that
        /// returned <c>base.ClippingBox</c> (empty here: the mesh and frame outputs are
        /// preview-hidden) stayed on screen after 'show' went off until a camera change
        /// forced a full redraw. The origin costs nothing to include and sits at the
        /// print bed in every lab document.</summary>
        static BoundingBox OriginBox => new BoundingBox(Point3d.Origin, Point3d.Origin);

        public override BoundingBox ClippingBox
        {
            get
            {
                var last = _last;
                if (last == null) return base.ClippingBox;   // nothing drawn yet: no claim on the view
                if (!last.Show || last.Layout == null) return OriginBox;
                switch (last.Placement)
                {
                    case LegendPlacement.Plane:
                        return last.Box.IsValid ? last.Box : OriginBox;
                    case LegendPlacement.Floating:
                        {
                            // the screen-constant size has no fixed model extent - a generous
                            // box around the anchor keeps the redraw region honest at any zoom
                            var b = new BoundingBox(last.Point, last.Point);
                            b.Inflate(Math.Max(1, last.Style.Length));
                            return b;
                        }
                    default:
                        return OriginBox;   // viewport overlay: screen space, no model extent
                }
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            if (_last == null || !_last.Show || Hidden || Locked || _last.Layout == null) return;
            if (_last.Placement == LegendPlacement.Plane)
            {
                if (_last.Mesh != null && _last.Mesh.Faces.Count > 0) args.Display.DrawMeshFalseColors(_last.Mesh);
            }
            else if (_last.Placement == LegendPlacement.Floating)
            {
                var (mesh, _, _, _, _) = FloatingBuild(args.Viewport);
                if (mesh.Faces.Count > 0) args.Display.DrawMeshFalseColors(mesh);
            }
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (_last == null || !_last.Show || Hidden || Locked || _last.Layout == null) return;
            switch (_last.Placement)
            {
                case LegendPlacement.Viewport:
                    LegendRenderer.Draw2d(args.Display, args.Viewport.Size, _last.Layout, _last.Style, _last.Anchor);
                    break;
                case LegendPlacement.Plane:
                    foreach (var t in _last.Texts) args.Display.Draw3dText(t, _last.Style.TextColor);
                    break;
                default:
                    {
                        var (_, texts, _, _, _) = FloatingBuild(args.Viewport);
                        foreach (var t in texts) args.Display.Draw3dText(t, _last.Style.TextColor);
                        break;
                    }
            }
        }

        /// <summary>Floating: a camera-facing plane at the point, scaled so one layout
        /// pixel is one screen pixel at that depth (constant screen size while orbiting).</summary>
        (Mesh, List<Text3d>, List<Plane>, List<string>, BoundingBox) FloatingBuild(RhinoViewport vp)
        {
            var plane = BillboardPlane(_last!.Point, vp);
            double scale = 1.0;
            if (vp.GetWorldToScreenScale(_last.Point, out double pixelsPerUnit) && pixelsPerUnit > 1e-9)
                scale = 1.0 / pixelsPerUnit;
            return LegendRenderer.Build3d(_last.Layout!, _last.Style, plane, scale, _last.Anchor);
        }
    }
}
