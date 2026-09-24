using System;
using System.Collections.Generic;
using System.IO;
using Grasshopper.Kernel;
using Lugworm.Convert;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Lugworm.Ui;
using Rhino.Geometry;

namespace Lugworm.Components
{
    /// <summary>Scroll through an existing G-code and rebuild it so the print resumes
    /// mid-way - after an air bubble or another failure - without reprinting the part.
    /// The restart point is picked by a Z height, the Duet's file-progress percentage
    /// or a Rhino/document XYZ point at the failure. Raw Duet machine coordinates
    /// must first be mapped into that document frame; the output file keeps the original header +
    /// modal state, re-approaches the resume point safely from above, optionally
    /// primes the ram, and continues with the untouched tail.</summary>
    public class GcodeRestartComponent : IamComponentBase
    {
        public GcodeRestartComponent()
            : base("G-code Restart", "Restart",
                   "Press Compute with original G-code and a restart selector to rebuild a job, then check the approach clears the printed part.",
                   "Lugworm", "3 G-code")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0014-4aeb-9c3d-aa0000000014");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= IamIcons.GcodeRestart();
        protected override bool Heavy => true;

        // z, percent and point are ONE selector: wiring both numbers raised the "several
        // selectors" warning on every fresh component, so only z gets a slider.
        protected internal override bool AutoWires(string inputName) => inputName != "percent";

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code of the failed print as one string or a list of lines", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("gcodePath", "Gp", "Original G-code file path, overriding gcode and giving exact file positions for percent mode", "core", PType.Text),
            new ParamSpec("z", "Z", "Restart at the first extruding move at or above this machine Z in mm, used if percent and point are unset", "core", PType.Number).Hint(0, 500, 10, 1),
            new ParamSpec("percent", "P%", "Restart file progress from 0 to 100 percent (multiply a Print component's 0 to 1 progress by 100)", "core", PType.Number).Hint(0, 100, 50, 1),
            new ParamSpec("point", "Pt", "Failure point in Rhino document units, overriding percent and z to resume after the nearest printed point", "core", PType.Point),
            new ParamSpec("primeE", "PE", "Ram prime before resuming, mm of E. Refills the nozzle after an air bubble (0 = none)", "core", PType.Number).Hint(0, 20, 0, 2),
            new ParamSpec("travelSpeed", "TS", "Feedrate of the safe approach travels, mm/s", "core", PType.Number).Hint(10, 200, 60, 0),
            new ParamSpec("hopSpeed", "HS", "Feedrate of the final descent onto the part, mm/s (0 = travel speed)", "core", PType.Number).Hint(0, 100, 0, 0),
            new ParamSpec("retractSpeed", "RS", "Ram speed while priming, mm/s of ram travel", "core", PType.Number).Hint(0.1, 10, 2, 1),
            new ParamSpec("clearance", "Cl", "Clearance above everything printed so far for the approach, mm", "core", PType.Number).Hint(1, 100, 10, 0),
            new ParamSpec("savePath", "Sv", "Optional local file or folder to save on Compute, overwriting the target (folder uses restart.gcode)", "core", PType.Text),
            // appended last (2026-09-16): a saved document's wires keep their sockets
            new ParamSpec("robot", "R", "Optional Robot Definition - the coordinate frame the job's G-code was emitted in, exactly as the G-code Generator reads it. Unwired keeps the Potterbot SCARA frame (y = -x mirror) for the 'point' input and the resume / printed / remaining outputs; a generic Cartesian definition uses the machine's own coordinates", "core", PType.Text),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            // BREAKING (1.11.0): "gcode_text" removed - see the note in GcodeComponent.
            // Wires after "gcode" shift up one in saved definitions.
            pManager.AddTextParameter("gcode", "G", "Restart G-code lines", GH_ParamAccess.list);
            pManager.AddPointParameter("resume", "R", "Restart point in Rhino document units", GH_ParamAccess.item);
            pManager.AddCurveParameter("printed", "Pd", "Skipped path in Rhino document units, to compare with the part already on the bed", GH_ParamAccess.list);
            pManager.AddCurveParameter("remaining", "Rm", "Path to print after restarting, in Rhino document units", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Restart selection, source accuracy and save status", GH_ParamAccess.item);
        }

        sealed class Result
        {
            public List<string> Lines = new();
            public Point3d? Resume;
            public List<Curve> Printed = new();
            public List<Curve> Remaining = new();
            public string Info = "";
        }

        sealed class SourceRead
        {
            public byte[] Bytes = Array.Empty<byte>();
            public List<string> Lines = new();
        }

        // background file work (AUD-PERF-001): the source read and the optional save
        // leave the solver thread; last good outputs replay while they run
        readonly BackgroundFileWork<SourceRead> _reader = new("gcodePath read");
        readonly BackgroundFileWork<string> _saver = new("save");
        Result? _lastGood;

        public override void RemovedFromDocument(GH_Document document)
        {
            _reader.CancelAndDetach();
            _saver.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) { _reader.CancelAndDetach(); _saver.CancelAndDetach(); }
            base.DocumentContextChanged(document, context);
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var raw = GetStringList(DA, "gcode");
            List<string> lines;
            byte[]? originalBytes = null;
            bool exactBytes = false;
            string sourcePath = GetString(DA, "gcodePath", "").Trim();
            if (sourcePath.Length > 0)
            {
                // keyed by canonical path + file identity: repeated solves reuse the
                // parsed read, an external file change starts one replacement read
                string readKey;
                try
                {
                    var fi = new FileInfo(sourcePath);
                    if (!fi.Exists)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "gcodePath could not be read: file not found");
                        res.Info = "invalid gcodePath";
                        return res;
                    }
                    readKey = FormattableString.Invariant($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "gcodePath could not be read: " + ex.Message);
                    res.Info = "invalid gcodePath";
                    return res;
                }
                var got = _reader.ResultFor(readKey);
                if (got == null)
                {
                    if (_reader.ErrorFor(readKey) is string readError)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, readError);
                        _reader.ForgetError();
                        res.Info = "invalid gcodePath";
                        return res;
                    }
                    string p = sourcePath;
                    _reader.Start(this, readKey, ct =>
                    {
                        byte[] bytes = File.ReadAllBytes(p);
                        string text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                        var ls = new List<string>(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
                        if (ls.Count > 0 && ls[ls.Count - 1].Length == 0) ls.RemoveAt(ls.Count - 1);
                        return new SourceRead { Bytes = bytes, Lines = ls };
                    });
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Loading " + sourcePath + " \u2026");
                    if (_lastGood != null) return _lastGood;
                    res.Info = "loading " + sourcePath + " \u2026";
                    return res;
                }
                originalBytes = got.Bytes;
                lines = new List<string>(got.Lines);   // copy: the shared trim below must not mutate the cache
                exactBytes = true;
            }
            else if (raw.Count == 1)
                lines = new List<string>(raw[0].Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            else lines = raw;
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 0) { res.Info = "no gcode"; return res; }

            // The job text is in the machine's own frame on both sides of this component.
            // Which frame that is comes from the same definition the G-code Generator
            // read: the calibrated Potterbot reflection about y = -x, or the identity of
            // a Cartesian machine. Unwired keeps the pre-definition behaviour bit for bit
            // (see GcodeComponent and GcodeConfig.CoordinateFrame).
            RobotProfile robot;
            try
            {
                string robotText = GetString(DA, "robot", "");
                robot = string.IsNullOrWhiteSpace(robotText)
                    ? RobotCatalog.Get("potterbot-scara-v4")
                    : RobotCatalog.Resolve(robotText);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                res.Info = "robot definition: " + ex.Message;
                return res;
            }
            bool mirrored = robot.GcodeCoordinateFrame == GcodeCoordinateFrame.LegacyScara;

            // ---- restart selector: exactly one of z / percent / point ----
            double zIn = GetNumber(DA, "z", double.NaN);
            double pctIn = GetNumber(DA, "percent", double.NaN);
            int ptIdx = Params.IndexOfInputParam("point");
            Point3d pt = default;
            bool hasPt = ptIdx >= 0 && DA.GetData(ptIdx, ref pt);
            int wired = (double.IsNaN(zIn) ? 0 : 1) + (double.IsNaN(pctIn) ? 0 : 1) + (hasPt ? 1 : 0);
            if (wired == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Wire one restart selector: z, percent or point");
                res.Info = "no restart selector wired";
                return res;
            }
            if (wired > 1)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Several selectors are wired. The point wins over percent, and percent wins over z");
            RestartMode mode = hasPt ? RestartMode.Point
                             : !double.IsNaN(pctIn) ? RestartMode.Percent
                             : RestartMode.ZHeight;

            var opt = new RestartOptions
            {
                TravelSpeed = Math.Max(1e-3, GetNumber(DA, "travelSpeed", 60.0)),
                HopSpeed = GetNumber(DA, "hopSpeed", 0.0),
                RetractSpeed = Math.Max(1e-3, GetNumber(DA, "retractSpeed", 2.0)),
                PrimeE = GetNumber(DA, "primeE", 0.0),
                Clearance = GetNumber(DA, "clearance", 10.0),
                OriginalBytes = originalBytes,
                ExactByteProvenance = exactBytes,
            };

            RestartPlan plan;
            try
            {
                // The `point` selector is a Rhino/document point, so it converts on the
                // way in (document units → mm → the job's frame). Raw Duet coordinates
                // must be converted to the Rhino frame before they are supplied as `point`.
                double sIn = DocUnits.ScaleToMm();
                var ptMm = new Vec3(pt.X * sIn, pt.Y * sIn, pt.Z * sIn);
                plan = GcodeRestart.Plan(lines, mode, zIn, pctIn, ToJobFrame(ptMm, mirrored), opt);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                res.Info = ex.Message;
                return res;
            }

            res.Lines = plan.Lines;
            // previews and the resume point are Rhino-frame in document units;
            // plan.Lines stay G-code (mm)
            double sOut = DocUnits.ScaleToMm();
            res.Resume = DocUnits.FromMm(GhConvert.ToPt(ToRhinoFrame(plan.Resume, mirrored)), sOut);
            foreach (var p in plan.SkippedPolys) res.Printed.Add(GhConvert.PtsToCurve(ToRhinoFrame(p, mirrored), sOut));
            foreach (var p in plan.RemainPolys) res.Remaining.Add(GhConvert.PtsToCurve(ToRhinoFrame(p, mirrored), sOut));
            string modeInfo = mode switch
            {
                RestartMode.Point => $"point ({pt.X:0.0},{pt.Y:0.0},{pt.Z:0.0})",
                RestartMode.Percent => $"duet {pctIn:0.0}%",
                _ => $"Z {zIn:0.0}mm",
            };
            var resumeShown = ToRhinoFrame(plan.Resume, mirrored);
            res.Info = $"mode:{modeInfo}  cut:line {plan.CutLine + 1}/{plan.TotalLines} ({plan.PercentAtCut:0.0}%)  " +
                       $"resume:X{resumeShown.X:0.0} Y{resumeShown.Y:0.0} Z{resumeShown.Z:0.0}  frame:{robot.GcodeCoordinateFrame}" +
                       (plan.LayerAtCut.Length > 0 ? $"  {plan.LayerAtCut}" : "") +
                       (opt.PrimeE > 0 ? $"  prime:{opt.PrimeE:0.0}mm" : "") +
                       (plan.ApproximateByteProgress && mode == RestartMode.Percent ? "  WARNING: approximate (reconstructed bytes)" : "");

            // ---- optional save (background, keyed by path + content hash) ----
            string savePath = "";
            int si = Params.IndexOfInputParam("savePath");
            if (si >= 0 && DA.GetData(si, ref savePath) && !string.IsNullOrWhiteSpace(savePath))
            {
                try
                {
                    string p = SavePath.Resolve(savePath, "restart.gcode");
                    // Joined here and nowhere else: a restart plan can hold nearly the whole
                    // remaining job, so building that string on every compute was pure waste
                    // once the gcode_text output was removed.
                    string content = string.Join("\n", res.Lines);
                    ulong contentHash = ContentHash.Fnv1a(content);
                    string saveKey = FormattableString.Invariant(
                        $"{p}|{content.Length}|{contentHash:X16}");
                    if (_saver.ResultFor(saveKey) is string savedPath)
                        res.Info += $"  saved:{savedPath}";
                    else if (_saver.ErrorFor(saveKey) is string saveError)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, saveError);
                        _saver.ForgetError();
                    }
                    else
                    {
                        _saver.Start(this, saveKey, ct =>
                        {
                            try { File.WriteAllText(p, content); return p; }
                            catch (Exception ex) { throw new IOException(SavePath.Explain(ex, p)); }
                        });
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Saving " + p + " …");
                        res.Info += "  saving…";
                    }
                }
                catch (Exception ex)
                { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Save failed: " + SavePath.Explain(ex, savePath)); }
            }
            _lastGood = res;
            return res;
        }

        /// <summary>Rhino-frame point -&gt; the job's frame: the y = -x reflection for the
        /// Potterbot (Core MachineFrame), the identity for a Cartesian definition.</summary>
        static Vec3 ToJobFrame(Vec3 rhino, bool mirrored) => mirrored ? MachineFrame.ToMachine(rhino) : rhino;

        /// <summary>Job-frame point -&gt; Rhino frame, the inverse of <see cref="ToJobFrame"/>.</summary>
        static Vec3 ToRhinoFrame(Vec3 job, bool mirrored) => mirrored ? MachineFrame.ToRhino(job) : job;

        /// <summary>Job-frame polyline -&gt; Rhino frame.</summary>
        static List<Vec3> ToRhinoFrame(IReadOnlyList<Vec3> pts, bool mirrored)
        {
            var mapped = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++) mapped.Add(ToRhinoFrame(pts[i], mirrored));
            return mapped;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            // "gcode_text" (formerly index 1) removed; every index below shifted down one.
            DA.SetDataList(0, r.Lines);
            if (r.Resume.HasValue) DA.SetData(1, r.Resume.Value);
            DA.SetDataList(2, r.Printed);
            DA.SetDataList(3, r.Remaining);
            DA.SetData(4, r.Info);
        }
    }
}
