using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Saves G-code to a folder with a date/time-stamped filename. Uses the
    /// current date/time by default; toggle the override group to feed a Grasshopper
    /// date/time component and/or a custom format string. Writes only when `write` is
    /// true (wire a Button), so it doesn't spam files on every recompute.</summary>
    public class GcodeSaveComponent : IamComponentBase
    {
        public GcodeSaveComponent()
            : base("Save G-code", "Save",
                   "Save supplied G-code to a timestamped file when write changes to true, overwriting any file with the same name.",
                   "Lugworm", "3 G-code")
        { }

        public override Guid ComponentGuid => new Guid("e1a2c3d4-0005-4a5b-9c3d-aa0000000005");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.GcodeSave();

        // minute precision (no seconds): the filename is stable within a minute, so the
        // Button-triggered save doesn't churn a new name on every recompute
        const string DefaultFormat = "yyyy-MM-dd_HH-mm";

        // cache of the last successful write so the outputs keep reporting it after the
        // Button releases (write flips back to false) - persisted with the document
        string _lastPath = "";
        string _lastInfo = "";
        bool _writeWasHigh;

        // background writer (AUD-PERF-001): the file write leaves the solver thread;
        // while it runs the component keeps its last outputs and shows a remark
        readonly BackgroundFileWork<string> _writer = new("write");
        string? _writeKey;
        int _writeEdge;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("gcode", "G", "G-code as one string or a list of lines", "core", PType.Text, GH_ParamAccess.list),
            new ParamSpec("folder", "F", "Local folder required with nonempty gcode before triggering write", "core", PType.Text),
            new ParamSpec("prefix", "P", "Filename prefix (default 'print')", "core", PType.Text).HintText("print"),
            new ParamSpec("write", "W", "Save once when false changes to true, with gcode and folder set (wire a Button)", "core", PType.Boolean),
            // override group
            new ParamSpec("dateTime", "Dt", "Override the date and time (default now)", "override", PType.Time),
            new ParamSpec("format", "Fmt", "Timestamp format text (default yyyy-MM-dd_HH-mm), for example yyyyMMdd_HHmmss", "override", PType.Text),
        };

        protected override IReadOnlyList<(string, string)> ToggleGroups => new[]
        {
            ("override", "Custom date/time + format inputs"),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("path", "P", "Full target path, retaining the last successful save after write returns to false", GH_ParamAccess.item);
            pManager.AddBooleanParameter("written", "W", "True if the file was written this solve", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Save result or the reason writing was refused", GH_ParamAccess.item);
        }

        sealed class Result { public string Path = ""; public bool Written; public string Info = ""; }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            var lines = GetStringList(DA, "gcode");
            string text = lines.Count == 1 ? lines[0] : string.Join("\n", lines);

            string folder = GetString(DA, "folder", "");
            string prefix = GetString(DA, "prefix", "print");
            bool write = GetBool(DA, "write", false);
            bool risingWrite = write && !_writeWasHigh;
            _writeWasHigh = write;
            DateTime dt = GetTime(DA, "dateTime", DateTime.Now);
            string fmt = GetString(DA, "format", DefaultFormat);

            string stamp;
            try { stamp = dt.ToString(fmt, CultureInfo.InvariantCulture); }
            catch (FormatException)
            {
                stamp = dt.ToString(DefaultFormat, CultureInfo.InvariantCulture);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Invalid format '{fmt}'. Used {DefaultFormat} instead");
            }

            string fileName = (string.IsNullOrEmpty(prefix) ? "" : prefix + "_") + stamp + ".gcode";
            res.Path = string.IsNullOrWhiteSpace(folder) ? fileName : Path.Combine(folder, fileName);

            if (risingWrite)
            {
                if (string.IsNullOrWhiteSpace(folder))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Set folder before pressing the Button wired to write");
                else if (string.IsNullOrEmpty(text))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Supply gcode before pressing the Button wired to write");
                else
                {
                    // rising edge starts a background write (AUD-PERF-001); the edge
                    // counter keeps one write per press even for identical content
                    _writeEdge++;
                    _writer.ForgetError();
                    string path = res.Path, dir = folder, content = text;
                    ulong contentHash = Lugworm.Core.Robot.ContentHash.Fnv1a(content);
                    _writeKey = FormattableString.Invariant(
                        $"{path}|{content.Length}|{contentHash:X16}|e{_writeEdge}");
                    _writer.Start(this, _writeKey, ct =>
                    {
                        try
                        {
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(path, content);
                            return path;
                        }
                        catch (Exception ex)
                        {
                            // keep the friendly explanation the sync path used to give
                            throw new IOException(Lugworm.Core.Gcode.SavePath.Explain(ex, path));
                        }
                    });
                }
            }

            if (_writer.IsPending)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Writing " + res.Path + " …");
                if (!string.IsNullOrEmpty(_lastPath)) { res.Path = _lastPath; res.Info = "writing… last: " + _lastInfo; }
                else res.Info = "writing " + res.Path + " …";
                return res;
            }
            if (_writeKey != null && _writer.ErrorFor(_writeKey) is string writeError)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, writeError);
            }
            else if (_writeKey != null && _writer.ResultFor(_writeKey) is string writtenPath)
            {
                res.Written = true;
                _writeKey = null;   // report the completed write exactly once
                _lastPath = writtenPath;
                _lastInfo = "wrote " + writtenPath;
                res.Path = writtenPath;
                res.Info = _lastInfo;
                return res;
            }

            if (write)
            {
                res.Info = "write armed. Release the Button, then press it again to write";
            }
            else if (!string.IsNullOrEmpty(_lastPath))
            {
                // Button released: replay the cached write instead of resetting to a fresh
                // (new-timestamp) preview path
                res.Path = _lastPath;
                res.Info = "cached: " + _lastInfo;
            }
            else
            {
                res.Info = "preview only. Set write to true: " + res.Path;
            }
            return res;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _writer.CancelAndDetach();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) _writer.CancelAndDetach();
            base.DocumentContextChanged(document, context);
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetData(0, r.Path);
            DA.SetData(1, r.Written);
            DA.SetData(2, r.Info);
        }

        // persist the cached last-write so it survives a document save/reopen
        public override bool Write(GH_IWriter writer)
        {
            if (!string.IsNullOrEmpty(_lastPath)) writer.SetString("iamLastSavePath", _lastPath);
            if (!string.IsNullOrEmpty(_lastInfo)) writer.SetString("iamLastSaveInfo", _lastInfo);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            string p = ""; if (reader.TryGetString("iamLastSavePath", ref p) && p != null) _lastPath = p;
            string ii = ""; if (reader.TryGetString("iamLastSaveInfo", ref ii) && ii != null) _lastInfo = ii;
            return base.Read(reader);
        }
    }
}
