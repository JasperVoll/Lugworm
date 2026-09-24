using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Per-segment overhang analysis of a sliced toolpath (Phase 3).
    /// Two readings per segment: the angle from the LOCAL layer normal (layer-relative,
    /// the layer-on-layer stagger) and the angle from world Z (GLOBAL, gravity); the two
    /// diverge on non-planar (conical) layers. Both are always emitted (<c>angles</c>,
    /// <c>anglesWorld</c>); the <c>global</c> toggle picks which one the thresholds
    /// classify, and so which one the viewport, <c>colors</c> and <c>legend</c> show.
    /// Viewport: magma-coloured toolpath, either three classes (safe / warning /
    /// critical) or a continuous ramp from 0° to the critical threshold
    /// (<c>colorMode</c>); the <c>legend</c> wire and the <c>colors</c> output follow
    /// the same <see cref="OverhangColors"/> table the viewport paints.
    /// The analysis result is exposed as data (angles tree + CSV summary) so later
    /// slicing-parameter adaptation can consume it - no adaptation logic here.</summary>
    public class OverhangComponent : IamComponentBase
    {
        public OverhangComponent()
            : base("Overhang", "Overhang",
                   "Press Compute with layer contours to measure overhang angles against the selected reference.",
                   "Lugworm", "5 Analyze")
        { }

        // NOTE: e1a2c3d4-0005-... is taken by GcodeSaveComponent - GUIDs must be unique
        // per component or Grasshopper refuses to load the assembly
        public override Guid ComponentGuid => new Guid("f1a2c3d4-0006-4a6b-9c3d-aa0000000006");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= Ui.IamIcons.Overhang();
        protected override bool Heavy => true;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("contours", "C", "Sliced contour tree from Slice or Place, one branch per layer, or per object and layer", "core", PType.Curve, GH_ParamAccess.tree),
            new ParamSpec("warnDeg", "Wa", "Warning threshold, deg from the build direction. The global input picks the local layer normal or world Z. Calibrate per material", "core", PType.Number).Hint(5, 60, 25, 0),
            new ParamSpec("failDeg", "Fa", "Critical overhang angle in deg from the selected reference direction, calibrated for the material", "core", PType.Number).Hint(10, 80, 45, 0),
            // 'tol' keeps socket 3, its position in every shipped release: Grasshopper
            // restores inputs by index, so the 4.0.0 additions follow it
            new ParamSpec("tol", "T", "Curve linearization tolerance, mm", "core", PType.Number).Hint(0.05, 2, 0.5, 2),
            new ParamSpec("colorMode", "Cm", "0 = classes (safe, warning and critical in magma hues), 1 = continuous magma ramp from 0° to failDeg, saturating above", "core", PType.Integer).Hint(0, 1, 0, 0),
            new ParamSpec("global", "Gl", "Use world Z for thresholds and colors when true, or the local layer normal when false", "core", PType.Boolean).HintBool(false),
            // export group
            new ParamSpec("csvPath", "Sv", "Optional .csv file path for the summary per layer, written on compute", "export", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("export", "CSV export input"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("colorMode", "Color mode", new[] { ("classes", "0"), ("continuous magma", "1") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("angles", "A", "Local overhang angle per segment relative to the layer, deg, one branch per contour. Always the local reading, whatever global says", GH_ParamAccess.tree);
            pManager.AddNumberParameter("anglesWorld", "Aw", "Global overhang angle per segment against world Z, deg. Always the global reading", GH_ParamAccess.tree);
            pManager.AddTextParameter("summary", "S", "Summary per layer as CSV lines: max overhang and the percentage above each threshold. The percentages follow global", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
            pManager.AddTextParameter("legend", "Le", "Legend definition text for Legend, matching the selected angle reference, thresholds and colors", GH_ParamAccess.item);
            // 4.0.0 polish, appended
            pManager.AddColourParameter("colors", "Cl", "Color per segment in the active color mode and reference, cut like angles, for a custom preview", GH_ParamAccess.tree);
        }

        sealed class Result
        {
            public GH_Structure<GH_Number> Angles = new();
            public GH_Structure<GH_Number> AnglesWorld = new();
            public GH_Structure<GH_Colour> Colors = new();
            public List<string> Summary = new();
            public string Info = "";
            /// <summary>The wire Legend reads - the same Core colours the
            /// viewport paints, at the thresholds this solve used.</summary>
            public string Legend = "";
            public bool Continuous;
            // one list per class - complex designs reach 10^5..10^6 segments and a
            // per-segment DrawLine call each frame froze the viewport; DrawLines
            // renders each class as a single batch
            public List<Line> SafeLines = new(), WarnLines = new(), CritLines = new();
            // continuous mode: the same batching over a fixed number of magma buckets
            public List<Line>[] BucketLines = Array.Empty<List<Line>>();
            public BoundingBox Clip = BoundingBox.Empty;
        }

        Result? _last;   // for viewport drawing

        // sourced from Core so the viewport and the legend wire cannot drift apart
        static Color Of(Rgb c) => Color.FromArgb(c.R, c.G, c.B);
        static readonly Color SafeColor = Of(OverhangColors.Safe);
        static readonly Color WarnColor = Of(OverhangColors.Warning);
        static readonly Color CritColor = Of(OverhangColors.Critical);

        // continuous mode draws one DrawLines batch per bucket - the Interlayer Time
        // pattern - so a 10^5-segment part costs a few dozen calls, not one per segment
        const int Buckets = 32;
        static readonly Color[] BucketColors = BuildBucketColors();
        static Color[] BuildBucketColors()
        {
            var c = new Color[Buckets];
            for (int b = 0; b < Buckets; b++) c[b] = Of(ColorSchemes.At(ColorSchemes.Magma, null, (b + 0.5) / Buckets));
            return c;
        }
        static int BucketOf(double angleDeg, double max)
        {
            if (double.IsNaN(angleDeg)) return 0;
            int b = (int)Math.Floor(Math.Max(0, angleDeg) / max * Buckets);
            return Math.Max(0, Math.Min(Buckets - 1, b));
        }

        // document→mm factor of the current solve (AUD-COR-001)
        double _unitScale = 1.0;

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            _unitScale = DocUnits.ScaleToMm();

            double warnDeg = GetNumber(DA, "warnDeg", 25.0);
            double failDeg = GetNumber(DA, "failDeg", 45.0);
            double tol = GetNumber(DA, "tol", 0.5);
            var reference = GetBool(DA, "global", false) ? OverhangReference.Global : OverhangReference.Local;
            var opt = new OverhangOptions { WarnDeg = warnDeg, FailDeg = Math.Max(failDeg, warnDeg), Reference = reference };
            res.Continuous = GetInt(DA, "colorMode", 0) == 1;
            // the thresholds define the scale, so the legend stands even with no contours
            res.Legend = (res.Continuous
                ? OverhangColors.ContinuousLegend(warnDeg, opt.FailDeg, reference)
                : OverhangColors.Legend(warnDeg, opt.FailDeg, reference)).Serialize();
            double rampMax = OverhangColors.ContinuousMax(warnDeg, opt.FailDeg);
            if (res.Continuous)
            {
                res.BucketLines = new List<Line>[Buckets];
                for (int b = 0; b < Buckets; b++) res.BucketLines[b] = new List<Line>();
            }

            var tree = GetCurveTree(DA, "contours");
            if (tree == null || tree.IsEmpty) { res.Info = "no contours"; _last = res; return res; }

            // group branches into objects: {obj;layer} trees by first path index,
            // flat {layer} trees as one object
            var objects = new SortedDictionary<int, SortedDictionary<int, List<List<Vec3>>>>();
            for (int bi = 0; bi < tree.Branches.Count; bi++)
            {
                var path = tree.Paths[bi];
                int obj = path.Length >= 2 ? path[0] : 0;
                int layer = path.Length >= 2 ? path[1] : path[0];
                if (!objects.TryGetValue(obj, out var layers))
                { layers = new SortedDictionary<int, List<List<Vec3>>>(); objects[obj] = layers; }
                if (!layers.TryGetValue(layer, out var contours))
                { contours = new List<List<Vec3>>(); layers[layer] = contours; }
                foreach (var goo in tree.Branches[bi])
                {
                    if (goo?.Value == null) continue;
                    var pts = GhConvert.CurveToPts(goo.Value, tol, _unitScale);
                    if (pts.Count >= 2) contours.Add(pts);
                }
            }

            bool multi = objects.Count > 1;
            int totalSegs = 0, totalWarn = 0, totalCrit = 0;
            double worstDeg = 0.0;

            foreach (var kv in objects)
            {
                int obj = kv.Key;
                var layerList = new List<List<List<Vec3>>>();
                var layerKeys = new List<int>();
                foreach (var lkv in kv.Value) { layerKeys.Add(lkv.Key); layerList.Add(lkv.Value); }

                var ana = Overhang.Analyze(layerList, opt);

                // angles trees: one branch per contour, matching the input structure
                foreach (var seg in ana.Segments)
                {
                    var path = multi || tree.Paths[0].Length >= 2
                        ? new GH_Path(obj, layerKeys[seg.Layer], seg.Contour)
                        : new GH_Path(layerKeys[seg.Layer], seg.Contour);
                    res.Angles.Append(new GH_Number(seg.AngleLayerDeg), path);
                    res.AnglesWorld.Append(new GH_Number(seg.AngleWorldDeg), path);

                    var ln = new Line(DocUnits.FromMm(GhConvert.ToPt(seg.A), _unitScale),
                                      DocUnits.FromMm(GhConvert.ToPt(seg.B), _unitScale));
                    // the reading the thresholds classified - local or global - drives
                    // the colours; both readings are on the angle outputs regardless
                    double angle = seg.AngleDeg(reference);
                    if (res.Continuous)
                    {
                        int bucket = BucketOf(angle, rampMax);
                        res.BucketLines[bucket].Add(ln);
                        res.Colors.Append(new GH_Colour(Of(OverhangColors.Continuous(angle, warnDeg, opt.FailDeg))), path);
                    }
                    else
                    {
                        (seg.Class == OverhangClass.Critical ? res.CritLines
                         : seg.Class == OverhangClass.Warning ? res.WarnLines
                         : res.SafeLines).Add(ln);
                        res.Colors.Append(new GH_Colour(Of(OverhangColors.Of(seg.Class))), path);
                    }
                    res.Clip.Union(ln.From); res.Clip.Union(ln.To);

                    totalSegs++;
                    if (seg.Class != OverhangClass.Safe) totalWarn++;
                    if (seg.Class == OverhangClass.Critical) totalCrit++;
                    worstDeg = Math.Max(worstDeg, angle);
                }

                if (multi) res.Summary.Add($"# obj[{obj}]");
                res.Summary.AddRange(ana.CsvLines());
            }

            res.Info = $"segments:{totalSegs}  worst:{worstDeg:0.0}°  " +
                       $"warning:{totalWarn} ({(totalSegs > 0 ? 100.0 * totalWarn / totalSegs : 0):0.0}%)  " +
                       $"critical:{totalCrit} ({(totalSegs > 0 ? 100.0 * totalCrit / totalSegs : 0):0.0}%)  " +
                       $"thresholds warn≥{warnDeg:0.#}° fail≥{opt.FailDeg:0.#}° (calibrate these per material)  " +
                       $"reference:{(reference == OverhangReference.Global ? "global (world Z)" : "local (layer normal)")}  " +
                       $"colour:{(res.Continuous ? "continuous magma 0.." + rampMax.ToString("0.#", CultureInfo.CurrentCulture) + "°" : "classes")}";

            if (totalCrit > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{totalCrit} segment(s) exceed the critical overhang threshold {opt.FailDeg:0.#}°");

            // optional CSV export (background, keyed by path + content hash - AUD-PERF-001)
            string csvPath = GetString(DA, "csvPath", "");
            if (Active.Contains("export") && !string.IsNullOrWhiteSpace(csvPath))
            {
                try
                {
                    string p = Lugworm.Core.Gcode.SavePath.Resolve(csvPath, "overhang_summary.csv");
                    string[] summary = res.Summary.ToArray();
                    string content = string.Join("\n", summary);
                    ulong contentHash = ContentHash.Fnv1a(content);
                    string saveKey = FormattableString.Invariant(
                        $"{p}|{content.Length}|{contentHash:X16}");
                    if (_csvWriter.ResultFor(saveKey) is string savedPath)
                        res.Info += $"  saved:{savedPath}";
                    else if (_csvWriter.ErrorFor(saveKey) is string saveError)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, saveError);
                        _csvWriter.ForgetError();
                    }
                    else
                    {
                        _csvWriter.Start(this, saveKey, ct =>
                        {
                            try { File.WriteAllLines(p, summary); return p; }
                            catch (Exception ex)
                            { throw new IOException(Lugworm.Core.Gcode.SavePath.Explain(ex, p)); }
                        });
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Saving " + p + " …");
                        res.Info += "  saving…";
                    }
                }
                catch (Exception ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "CSV save failed: " + Lugworm.Core.Gcode.SavePath.Explain(ex, csvPath)); }
            }

            _last = res;
            return res;
        }

        // background CSV export (AUD-PERF-001)
        readonly BackgroundFileWork<string> _csvWriter = new("csv save");

        public override void RemovedFromDocument(GH_Document document)
        {
            _csvWriter.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) _csvWriter.CancelAndDetach();
            base.DocumentContextChanged(document, context);
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            _last = r;
            DA.SetDataTree(0, r.Angles);
            DA.SetDataTree(1, r.AnglesWorld);
            DA.SetDataList(2, r.Summary);
            DA.SetData(3, r.Info);
            DA.SetData(4, r.Legend);
            DA.SetDataTree(5, r.Colors);
        }

        public override BoundingBox ClippingBox
            => _last != null ? _last.Clip : base.ClippingBox;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (_last == null || Hidden || Locked) return;
            if (_last.Continuous)
            {
                for (int b = 0; b < _last.BucketLines.Length; b++)
                    if (_last.BucketLines[b].Count > 0) args.Display.DrawLines(_last.BucketLines[b], BucketColors[b], 2);
                return;
            }
            if (_last.SafeLines.Count > 0) args.Display.DrawLines(_last.SafeLines, SafeColor, 1);
            if (_last.WarnLines.Count > 0) args.Display.DrawLines(_last.WarnLines, WarnColor, 3);
            if (_last.CritLines.Count > 0) args.Display.DrawLines(_last.CritLines, CritColor, 3);
        }
    }
}
