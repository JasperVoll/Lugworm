using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Convert;
using Lugworm.Core.Analysis;
using Lugworm.Core.Clipping;
using Lugworm.Core.Geometry;
using Lugworm.Core.Slicing;
using Lugworm.Ui;
using Lugworm.Validate;

namespace Lugworm.Components
{
    /// <summary>Geometry -> contour tree (one branch per layer). Planar + conical
    /// (non-planar). Accepts a LIST of meshes/breps/surfaces: every object
    /// is validated (closed/manifold/normals, human-readable rejections), sliced, and
    /// printed sequentially object-by-object in input order.
    /// Single-object output keeps the original flat {layer} tree paths; multi-object
    /// output nests as {object; layer}. Heavy (compute button). Port of ClaySlicer2.py.</summary>
    public class SlicerComponent : IamComponentBase
    {
        public SlicerComponent()
            : base("Slice", "Slice",
                   "Press Compute with geometry supplied to slice it into printable layer contours.",
                   "Lugworm", "1 Slice")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0001-4a1b-9c3d-aa0000000001");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.Slicer();

        // The two distance sockets were added to the core input contract. Name-based
        // archive normalization preserves existing wires and lets old spacing remain
        // the fallback for both new values.
        protected override int InputContractVersion => 1;

        // document→mm factor of the current solve (AUD-COR-001); outputs divide by it
        double _unitScale = 1.0;
        protected override bool Heavy => true;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("geo", "G", "Meshes, Breps or surfaces to slice, one item or a list. Each object is sliced and printed in turn", "core", PType.Geometry, GH_ParamAccess.list),
            new ParamSpec("layerHeight", "LH", "Layer spacing in mm, greater than 0", "core", PType.Number).Hint(0.2, 6, 3, 2),
            new ParamSpec("firstLayer", "FL", "Height of the first section, mm (0 = half the layer spacing)", "core", PType.Number).Hint(0, 5, 3, 2),
            new ParamSpec("wallCount", "WC", "Number of perimeters, at least 1", "core", PType.Integer).Hint(1, 6, 1),
            new ParamSpec("spacing", "Sp", "Legacy curve spacing, mm. Fallback for wallDistance and infillDistance when they are unset", "core", PType.Number).Hint(0.5, 12, 10, 1),
            new ParamSpec("wallDistance", "Wd", "Distance between walls and from the last wall to infill, mm (defaults to spacing)", "core", PType.Number).Hint(0.5, 12, 10, 1),
            new ParamSpec("infillDistance", "Id", "Distance between infill passes, mm (defaults to spacing)", "core", PType.Number).Hint(0.5, 12, 10, 1),
            new ParamSpec("mode", "Md", "Slicing mode: 0 = planar, 1 = conical (enable Conical inputs to adjust the angle)", "core", PType.Integer),
            new ParamSpec("infill", "If", "0 = none, 1 = zigzag, 2 = concentric, 3 = spiral, 4 = fermat, 5 = sine wave, 6 = graph wave", "core", PType.Integer),
            new ParamSpec("cornerRadius", "Cr", "Round sharp contour corners to this radius to correct overfill, mm (0 = off)", "core", PType.Number).Hint(0, 10, 0, 1),
            new ParamSpec("tol", "T", "Slicing and linearization tolerance, mm", "core", PType.Number).Hint(0.001, 1, 0.001, 3),
            // adaptive group
            new ParamSpec("hMin", "hMin", "Adaptive minimum layer height, mm", "adaptive", PType.Number).Hint(0.2, 6, 1, 2),
            new ParamSpec("hMax", "hMax", "Adaptive maximum layer height, mm", "adaptive", PType.Number).Hint(0.2, 8, 3, 2),
            // conical group
            new ParamSpec("coneAngle", "Ca", "Cone half-angle in deg for mode 1, affecting layer spacing, inner walls and infill", "conical", PType.Number).Hint(0, 45, 20, 0),
            new ParamSpec("nozzleClear", "Nc", "Maximum conical surface angle in deg before slicing reports a nozzle-clearance warning", "conical", PType.Number).Hint(0, 60, 25, 0),
            // zigzag group
            new ParamSpec("angle", "An", "Zigzag direction, deg", "zigzag", PType.Number).Hint(0, 180, 0, 0),
            // wave infill group (infill 5 / 6)
            new ParamSpec("profile", "Pf", "Graph Mapper map output for infill 6, used with Wave inputs enabled (empty = sine wave)", "wave", PType.Text),
            new ParamSpec("waveLen", "Wl", "Wave period along a row, mm", "wave", PType.Number).Hint(1, 200, 20, 1),
            new ParamSpec("waveTangent", "Wt", "Make mirrored wave peaks touch when true, or separate them by waveClearance when false", "wave", PType.Boolean),
            new ParamSpec("waveClearance", "Wc", "Gap left between mirrored peaks when waveTangent is off, mm (0 = 0.2 x infillDistance)", "wave", PType.Number).Hint(0, 20, 0, 2),
            new ParamSpec("waveRes", "Wr", "Sampling step along a wave row, mm", "wave", PType.Number).Hint(0.1, 5, 0.5, 2),
            new ParamSpec("waveRepeat", "Wp", "Repeat the graph forward when true, or alternate forward and backward for continuous joins when false", "wave", PType.Boolean),
            // bricklayer group
            new ParamSpec("brickShift", "Bs", "Wall Z shift as a fraction from 0 to 1 of the local layer gap (0.5 = half a layer)", "bricklayer", PType.Number).Hint(0, 1, 0.5, 2),
            new ParamSpec("brickOverlap", "Bo", "Minimum shared fraction of the shorter adjacent wall loop for interlocking, from 0 to 0.95", "bricklayer", PType.Number).Hint(0, 0.95, 0.5, 2),
            new ParamSpec("brickOuter", "Bx", "Shift the outermost wall instead of leaving it flat. This changes the visible surface", "bricklayer", PType.Boolean),
            new ParamSpec("brickRamp", "Br", "Layers left flat at the bottom and the top", "bricklayer", PType.Integer).Hint(0, 10, 1),
            // non-planar infill group
            new ParamSpec("npAmp", "Na", "Infill Z wave amplitude in mm (0 = off), limited to 0.4 times each adjacent layer gap", "nonplanar", PType.Number).Hint(0, 5, 0.4, 2),
            new ParamSpec("npWave", "Nw", "Infill Z wave period, mm", "nonplanar", PType.Number).Hint(1, 200, 20, 1),
            new ParamSpec("npDir", "Nd", "Direction the Z wave travels, deg (default = the zigzag angle)", "nonplanar", PType.Number).Hint(0, 180, 0, 0),
            new ParamSpec("npRes", "Nr", "Infill resampling step before modulation, mm", "nonplanar", PType.Number).Hint(0.1, 10, 1, 2),
            new ParamSpec("npPhase", "Np", "Phase advance per layer, turns (0.5 = next layer's trough over this layer's crest)", "nonplanar", PType.Number).Hint(0, 1, 0.5, 2),
            new ParamSpec("npRamp", "Nm", "Layers over which the amplitude ramps in and out", "nonplanar", PType.Integer).Hint(0, 10, 1),
            new ParamSpec("npWallAmp", "Nwa", "Wall Z wave amplitude, mm (0 = off)", "nonplanar", PType.Number).Hint(0, 5, 0, 2),
            new ParamSpec("npLoopPhase", "Nlp", "Phase advance per wall loop, turns", "nonplanar", PType.Number).Hint(0, 1, 0.25, 2),
            new ParamSpec("npWallOuter", "Nwo", "Modulate the outermost wall. This changes the visible surface", "nonplanar", PType.Boolean),
            // multi-object group
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("adaptive", "Adaptive layer height inputs"),
            ("conical", "Conical (non-planar) inputs"),
            ("zigzag", "Zigzag angle input"),
            ("wave", "Wave infill inputs (infill 5 / 6): period, amplitude, graph curve"),
            ("bricklayer", "Bricklayer: interlock adjacent wall loops by half a layer height"),
            ("nonplanar", "Non-planar infill and walls: wave-modulate path Z"),
            ("spiralize", "Spiralize (one continuous helix)"),
        };

        protected override IReadOnlyList<(string, string, (string, string)[])> ValueLists => new[]
        {
            ("mode", "Mode: create value list",
             new[] { ("Planar", "0"), ("Conical", "1") }),
            ("infill", "Infill: create value list",
             new[] { ("None", "0"), ("Zigzag", "1"), ("Concentric", "2"), ("Spiral", "3"), ("Fermat", "4"),
                     ("Wave (sine)", "5"), ("Wave (graph)", "6") }),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("contours", "C", "One branch per layer, walls then infill. With several objects the branch path is object, then layer", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("layers", "L", "Number of layers over all objects", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Diagnostics and warnings", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public GH_Structure<GH_Curve> Contours = new();
            public int Layers;
            public string Info = "";
            public readonly HashSet<int> EffectiveModes = new HashSet<int>();
            public string? ActiveLayerContext;
        }

        /// <summary>Per-solve slicing parameters, read once.</summary>
        sealed class SliceParams
        {
            public double LayerHeight, FirstLayer, Spacing, WallSpacing, InfillSpacing, CornerRadius, Tol;
            public int WallCount, Mode, Infill;
            public bool Adaptive, Spiralize;
            public double HMin, HMax, ConeAngle, NozzleClear, ZigAngle;
            // wave infill (5 / 6)
            public bool Wave;
            public IWaveProfile? WaveProfile;
            public Infill.WaveOptions? WaveOptions;
            // bricklayer
            public bool Bricklayer;
            public double BrickShift, BrickOverlap;
            public bool BrickOuter;
            public int BrickRamp;
            // non-planar infill
            public bool NonPlanar;
            public double NpAmp, NpWave, NpDir, NpRes, NpPhase, NpWallAmp, NpLoopPhase;
            public bool NpWallOuter;
            public int NpRamp;
        }

        // levels, sectioning, walls + infill, the whole-stack passes -> Core
        // SlicePipeline (moved there in 4.4.0, D7, so every mode combination is covered
        // by Core tests instead of only in Rhino; walls + infill had already moved to
        // LayerBuilder in 2.6.0)

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var rawGeos = GetGeometryList(DA, "geo");
            double layerHeight = GetNumber(DA, "layerHeight", 3.0);
            int connectedGeometry = 0;
            foreach (var geo in rawGeos) if (geo != null) connectedGeometry++;
            if (rawGeos.Count > 0 && connectedGeometry == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "The geo input is connected but carries no usable geometry");
            if (rawGeos.Count == 0 || layerHeight <= 0) { res.Info = "no geometry / invalid layerHeight"; return res; }
            double spacingIn = GetNumber(DA, "spacing", 10.0);
            double wallSpacingIn = GetNumber(DA, "wallDistance", spacingIn);
            double infillSpacingIn = GetNumber(DA, "infillDistance", spacingIn);
            if (spacingIn <= 0 || wallSpacingIn <= 0 || infillSpacingIn <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Inputs spacing, wallDistance and infillDistance must be above 0 mm (got {spacingIn}, {wallSpacingIn}, {infillSpacingIn}). Nothing was sliced");
                res.Info = $"invalid spacing values (spacing={spacingIn}, wallDistance={wallSpacingIn}, infillDistance={infillSpacingIn}, all must be above 0)";
                return res;
            }

            // heat slicing (mode 2) was retired in 4.0.0 (D031, archived decision log): a saved document that
            // still carries mode = 2 solves planar with one warning rather than erroring
            int modeIn = GetInt(DA, "mode", 0);
            if (modeIn == 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Heat mode was retired in 4.0.0. Sliced planar instead");
                modeIn = 0;
            }

            var p = new SliceParams
            {
                LayerHeight = layerHeight,
                FirstLayer = GetNumber(DA, "firstLayer", 3.0),
                WallCount = GetInt(DA, "wallCount", 1),
                Spacing = GetNumber(DA, "spacing", 10.0),
                WallSpacing = wallSpacingIn,
                InfillSpacing = infillSpacingIn,
                Mode = modeIn,
                Infill = GetInt(DA, "infill", 0),
                CornerRadius = GetNumber(DA, "cornerRadius", 0.0),
                Tol = GetNumber(DA, "tol", 0.001),
                Adaptive = Active.Contains("adaptive"),
                Spiralize = Active.Contains("spiralize"),
                HMin = GetNumber(DA, "hMin", layerHeight * 0.5),
                HMax = GetNumber(DA, "hMax", layerHeight * 1.5),
                ConeAngle = GetNumber(DA, "coneAngle", 20.0),
                NozzleClear = GetNumber(DA, "nozzleClear", 25.0),
                ZigAngle = GetNumber(DA, "angle", 0.0),
            };
            p.Wave = Active.Contains("wave");
            p.Bricklayer = Active.Contains("bricklayer");
            p.NonPlanar = Active.Contains("nonplanar");
            if (p.Bricklayer)
            {
                p.BrickShift = GetNumber(DA, "brickShift", 0.5);
                p.BrickOverlap = GetNumber(DA, "brickOverlap", 0.5);
                p.BrickOuter = GetBool(DA, "brickOuter", false);
                p.BrickRamp = GetInt(DA, "brickRamp", 1);
            }
            if (p.NonPlanar)
            {
                p.NpAmp = GetNumber(DA, "npAmp", 0.0);
                p.NpWave = GetNumber(DA, "npWave", 20.0);
                p.NpDir = IsWired("npDir") ? GetNumber(DA, "npDir", 0.0) : p.ZigAngle;
                p.NpRes = GetNumber(DA, "npRes", 1.0);
                p.NpPhase = GetNumber(DA, "npPhase", 0.5);
                p.NpRamp = GetInt(DA, "npRamp", 1);
                p.NpWallAmp = GetNumber(DA, "npWallAmp", 0.0);
                p.NpLoopPhase = GetNumber(DA, "npLoopPhase", 0.25);
                p.NpWallOuter = GetBool(DA, "npWallOuter", false);
            }
            // the wave profile is read once per solve: modes 5 and 6 and the non-planar
            // pass all evaluate the same curve
            if (p.Wave || p.NonPlanar || p.Infill == 5 || p.Infill == 6)
            {
                p.WaveOptions = new Infill.WaveOptions
                {
                    Wavelength = p.Wave ? GetNumber(DA, "waveLen", 20.0) : 20.0,
                    Tangent = p.Wave && GetBool(DA, "waveTangent", false),
                    Clearance = p.Wave ? GetNumber(DA, "waveClearance", 0.0) : 0.0,
                    Resolution = p.Wave ? GetNumber(DA, "waveRes", 0.5) : 0.5,
                };
                bool repeat = p.Wave && GetBool(DA, "waveRepeat", false);
                string profileText = p.Wave ? GetString(DA, "profile", "") : "";
                if (profileText.Length > 0)
                {
                    try
                    {
                        var graph = new GraphProfile(GraphMap.Deserialize(profileText), repeat);
                        if (repeat && graph.EndsMismatch)
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                                "The graph's ends differ, so a repeated wave jumps at every period boundary. " +
                                "Turn waveRepeat off for mirrored tiling");
                        p.WaveProfile = graph;
                    }
                    catch (Exception ex)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            $"Could not read the graph profile ({ex.Message}). Using a sine instead");
                    }
                }
                if (p.WaveProfile == null)
                {
                    if (p.Infill == 6)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            "Infill 6 (graph wave) has no graph wired. Wire the map output of Graph Mapper " +
                            "into profile. Sliced with a sine");
                    p.WaveProfile = new SineProfile();
                }
            }
            string? invalid = ValidateInputs(p);
            if (invalid != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, invalid);
                res.Info = invalid;
                return res;
            }
            var warn = new List<string>();

            // ---- meshing parameters (Brep/Surface conversion) ----
            // Default is TOLERANCE-DRIVEN quality meshing, not MeshingParameters.Default:
            // the coarse Rhino default made mesh-based sections (conical mode always,
            // planar on mesh fallback) sag inward off the true surface by whole
            // millimetres on smooth vases.
            // The former "meshing" input group was removed in 2.6.0 (test round 3.1.4):
            // mesh preparation belongs upstream of the slicer, the slicer meshes Breps
            // with one tolerance-driven setting.
            var mp = new MeshingParameters(0.75)
            {
                Tolerance = Math.Min(0.05, Math.Max(0.005, p.Tol * 0.5)),   // chord deviation ≤ tol/2
                JaggedSeams = false,
                SimplePlanes = true,
            };

            // ---- validation (never a silent null: every rejection lands in info + msgs) ----
            // document-unit adapter (AUD-COR-001): geometry converts to Core's mm frame
            // on duplicated copies; mm-labelled numeric inputs above never rescale
            double unitScale = _unitScale = DocUnits.ScaleToMm();
            var prepared = GeometryInput.Prepare(rawGeos, mp, unitScale);

            // ---- print order (multi-object): input order ----
            var order = new List<int>();
            for (int i = 0; i < prepared.Count; i++) order.Add(i);

            bool multi = prepared.Count > 1;
            int totalLayers = 0, objPos = 0, sliced = 0, rejected = 0;

            foreach (int oi in order)
            {
                var prep = prepared[oi];
                foreach (var e in prep.Report.Errors)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"obj[{oi}] rejected: {e}");
                foreach (var w in prep.Report.Warnings)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"obj[{oi}]: {w}");
                if (!prep.Ok) { rejected++; warn.Add(prep.Report.Describe()); continue; }
                if (prep.Report.Warnings.Count > 0 || prep.Report.Fixes.Count > 0)
                    warn.Add(prep.Report.Describe());

                int layers;
                try
                {
                    layers = SliceObject(prep, p, multi ? objPos : -1, res, warn);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    string context = res.ActiveLayerContext ?? "preflight";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"obj[{oi}] {context} geometry rejected: {ex.Message}");
                    warn.Add($"obj[{oi}] {context} rejected: {ex.Message}");
                    rejected++;
                    layers = 0;
                }
                catch (Exception ex)
                {
                    string context = res.ActiveLayerContext ?? "preflight";
                    string message = $"obj[{oi}] {context} unexpected slicing failure: {ex.Message}";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message);
                    warn.Add(message);
                    rejected++;
                    layers = 0;
                }
                finally { res.ActiveLayerContext = null; }
                totalLayers += layers;
                if (layers > 0) sliced++;
                objPos++;
            }

            string[] modeName = { "planar", "conical" };
            string[] infName = { "none", "zigzag", "concentric", "spiral", "fermat", "wave-sine", "wave-graph" };
            string modeStr = p.Mode >= 0 && p.Mode < modeName.Length ? modeName[p.Mode] : "planar";
            var effectiveNames = new List<string>();
            for (int mi = 0; mi < modeName.Length; mi++)
                if (res.EffectiveModes.Contains(mi)) effectiveNames.Add(modeName[mi]);
            string effectiveStr = effectiveNames.Count == 0 ? "none" : string.Join(",", effectiveNames);
            res.Layers = totalLayers;
            res.Info = $"requested:{modeStr}  effective:{effectiveStr}  objects:{sliced}/{prepared.Count}" +
                       (rejected > 0 ? $" ({rejected} rejected)" : "") +
                       $"  layers:{totalLayers}  walls:{p.WallCount}  " +
                       $"infill:{(p.Infill >= 0 && p.Infill < infName.Length ? infName[p.Infill] : "none")}";
            if (warn.Count > 0) res.Info += "\n" + string.Join("\n", warn);
            // key state at a glance without opening a panel
            Message = $"{modeStr} → {effectiveStr} - {totalLayers} layers" + (rejected > 0 ? $" - {rejected}✗" : "");
            return res;
        }

        static string? ValidateInputs(SliceParams p)
        {
            bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
            if (!Finite(p.LayerHeight) || p.LayerHeight <= 0)
                return "layerHeight must be finite and > 0";
            // 0 is the documented default: Levels.Planar takes it as half the layer spacing
            if (!Finite(p.FirstLayer) || p.FirstLayer < 0)
                return "firstLayer must be finite and >= 0 (0 = half the layer spacing)";
            if (!Finite(p.Spacing) || p.Spacing <= 0) return "spacing must be finite and > 0";
            if (!Finite(p.WallSpacing) || p.WallSpacing <= 0) return "wallDistance must be finite and > 0";
            if (!Finite(p.InfillSpacing) || p.InfillSpacing <= 0) return "infillDistance must be finite and > 0";
            if (p.WallCount < 1) return "wallCount must be >= 1";
            if (p.Mode < 0 || p.Mode > 1) return "mode must be 0 (planar) or 1 (conical)";
            if (p.Infill < 0 || p.Infill > 6) return "infill mode is outside the supported 0..6 range";
            if (!Finite(p.Tol) || p.Tol <= 0) return "tol must be finite and > 0";
            if (!Finite(p.CornerRadius) || p.CornerRadius < 0) return "cornerRadius must be finite and >= 0";
            if (!Finite(p.HMin) || !Finite(p.HMax) || p.HMin <= 0 || p.HMax < p.HMin)
                return "adaptive layer-height bounds must be finite, positive, and ordered";
            if (!Finite(p.ConeAngle) || p.ConeAngle < 0 || p.ConeAngle > 90) return "coneAngle must be within 0..90 degrees";
            if (!Finite(p.NozzleClear) || p.NozzleClear <= 0) return "nozzleClear must be finite and > 0";
            if (!Finite(p.ZigAngle)) return "angle must be finite";
            if (p.Bricklayer)
            {
                if (!Finite(p.BrickShift) || p.BrickShift < 0 || p.BrickShift > 1)
                    return "brickShift must be within 0..1";
                if (!Finite(p.BrickOverlap) || p.BrickOverlap < 0 || p.BrickOverlap >= 1)
                    return "brickOverlap must be within [0, 1)";
                if (p.BrickRamp < 0) return "brickRamp must be >= 0";
            }
            if (p.NonPlanar)
            {
                if (!Finite(p.NpAmp) || p.NpAmp < 0) return "npAmp must be finite and >= 0";
                if (!Finite(p.NpWave) || p.NpWave <= 0) return "npWave must be finite and > 0";
                if (!Finite(p.NpDir)) return "npDir must be finite";
                if (!Finite(p.NpRes) || p.NpRes <= 0) return "npRes must be finite and > 0";
                if (!Finite(p.NpPhase)) return "npPhase must be finite";
                if (p.NpRamp < 0) return "npRamp must be >= 0";
                if (!Finite(p.NpWallAmp) || p.NpWallAmp < 0) return "npWallAmp must be finite and >= 0";
                if (!Finite(p.NpLoopPhase)) return "npLoopPhase must be finite";
            }
            if (p.Wave)
            {
                if (!Finite(p.WaveOptions!.Wavelength) || p.WaveOptions.Wavelength <= 0)
                    return "waveLen must be finite and > 0";
                if (!Finite(p.WaveOptions.Resolution) || p.WaveOptions.Resolution <= 0)
                    return "waveRes must be finite and > 0";
                if (!Finite(p.WaveOptions.Clearance) || p.WaveOptions.Clearance < 0)
                    return "waveClearance must be finite and >= 0";
            }
            return null;
        }

        /// <summary>Slices one validated object and appends its branches to the result.
        /// objIndex &lt; 0 keeps the original single-object flat paths {layer}; otherwise
        /// paths nest as {objIndex; layer}. Returns the number of layers emitted.
        /// The orchestration itself is Core's (<see cref="SlicePipeline"/>, extracted in
        /// 4.4.0 - D7): what is left here is the Rhino geometry conversion, the Brep
        /// section, the runtime notices and the contour tree.</summary>
        int SliceObject(PreparedInput prep, SliceParams p, int objIndex, Result res, List<string> warn)
        {
            Mesh mesh = prep.Mesh!;
            Brep? brep = prep.Brep;
            string tag = objIndex >= 0 ? $"obj[{prep.Index}] " : "";
            res.ActiveLayerContext = "preflight";

            // reference bbox: Brep when the source was brep-like (precise), else the mesh
            var refGeo = (GeometryBase?)brep ?? mesh;
            var bbox = refGeo.GetBoundingBox(true);
            if (!bbox.IsValid) { warn.Add(tag + "invalid bounding box, skipped"); return 0; }

            var vf = GhConvert.MeshVF(mesh);
            var options = new SliceOptions
            {
                LayerHeight = p.LayerHeight,
                FirstLayer = p.FirstLayer,
                Mode = p.Mode,
                ConeAngleDeg = p.ConeAngle,
                NozzleClearDeg = p.NozzleClear,
                Adaptive = p.Adaptive,
                HMin = p.HMin,
                HMax = p.HMax,
                Spiralize = p.Spiralize,
                CornerRadius = p.CornerRadius,
                Tol = p.Tol,
                ObjectTag = tag,
                ZMin = bbox.Min.Z,
                ZMax = bbox.Max.Z,
                Cx = 0.5 * (bbox.Min.X + bbox.Max.X),
                Cy = 0.5 * (bbox.Min.Y + bbox.Max.Y),
                Build = new LayerBuildOptions
                {
                    WallCount = p.WallCount,
                    Infill = p.Infill,
                    WallSpacing = p.WallSpacing,
                    InfillSpacing = p.InfillSpacing,
                    AngleDeg = p.ZigAngle,
                    WaveProfile = p.WaveProfile,
                    Wave = p.WaveOptions,
                },
                Bricklayer = p.Bricklayer ? new BricklayerOptions
                {
                    LayerHeight = p.LayerHeight,
                    LayerWidth = p.WallSpacing,
                    ShiftFraction = p.BrickShift,
                    OverlapFraction = p.BrickOverlap,
                    ShiftOuter = p.BrickOuter,
                    RampLayers = p.BrickRamp,
                } : null,
                NonPlanar = p.NonPlanar ? new NonPlanarOptions
                {
                    LayerHeight = p.LayerHeight,
                    Amplitude = p.NpAmp,
                    Wavelength = p.NpWave,
                    DirectionDeg = p.NpDir,
                    Resolution = p.NpRes,
                    PhasePerLayer = p.NpPhase,
                    RampLayers = p.NpRamp,
                    Profile = p.WaveProfile,
                    WallAmplitude = p.NpWallAmp,
                    LoopPhaseTurns = p.NpLoopPhase,
                    IncludeOuterWall = p.NpWallOuter,
                } : null,
            };

            var pipeline = new SlicePipeline();
            SliceResult sliced;
            try
            {
                // brep-like inputs section the Brep (precise), and that stays on the Rhino
                // side of the boundary: the levels come from Core, the cuts from here, and
                // everything after the cut is the pipeline's again
                if (brep != null && p.Mode == 0)
                {
                    var plan = SlicePipeline.PlanLevels(vf.V, vf.F, options);
                    warn.AddRange(plan.Warnings);
                    var perimeters = new List<IReadOnlyList<IReadOnlyList<Vec3>>>(plan.Levels.Count);
                    for (int li = 0; li < plan.Levels.Count; li++)
                    {
                        res.ActiveLayerContext = $"layer {li + 1}";
                        double z = plan.Levels[li];
                        // one bad section must degrade to a warning, never reject the object
                        try { perimeters.Add(GhConvert.Section(null, brep, z, p.Tol)); }
                        catch (Exception ex)
                        {
                            warn.Add(tag + $"layer {li + 1} (z={z:0.###}) skipped: {ex.Message}");
                            perimeters.Add(new List<List<Vec3>>());
                        }
                    }
                    sliced = pipeline.SliceFromPerimeters(perimeters, plan.Levels, options);
                }
                else sliced = pipeline.Slice(vf.V, vf.F, options);
            }
            finally
            {
                // the pipeline names the layer it was in; Compute's catch reads it
                res.ActiveLayerContext = pipeline.Result.ActiveLayerContext ?? res.ActiveLayerContext;
            }

            warn.AddRange(sliced.Warnings);
            res.EffectiveModes.Add(sliced.EffectiveMode);
            if (sliced.BricklayerVarhRemark)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Bricklayer shifts wall loops off the nominal layer height. Enable the G-code " +
                    "generator's varh group so the flow follows the local gap, or the ramp layers " +
                    "will under and over extrude");
            if (sliced.BricklayerSkippedMessage != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, sliced.BricklayerSkippedMessage);
            if (sliced.NonPlanarSkippedMessage != null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, sliced.NonPlanarSkippedMessage);

            if (sliced.Helix != null)
            {
                var hpath = objIndex >= 0 ? new GH_Path(objIndex) : new GH_Path(0);
                res.Contours.Append(new GH_Curve(GhConvert.PtsToCurve(sliced.Helix, _unitScale)), hpath);
                return sliced.LayerCount;
            }
            foreach (var layer in sliced.Layers)
            {
                var path = objIndex >= 0 ? new GH_Path(objIndex, layer.Index) : new GH_Path(layer.Index);
                foreach (var c in layer.Flatten())
                    res.Contours.Append(new GH_Curve(GhConvert.PtsToCurve(c, _unitScale)), path);
            }
            return sliced.LayerCount;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataTree(0, r.Contours);
            DA.SetData(1, r.Layers);
            DA.SetData(2, r.Info);
        }
    }
}
