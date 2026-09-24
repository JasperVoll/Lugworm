using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Settings;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>Points one Grasshopper document at a folder of per-component settings
    /// files (4.0.0, D4). From then on every "⇤ inputs" button on the canvas builds
    /// its sliders, toggles, panels and value lists from the matching file instead of
    /// the built-in ParamSpec defaults, and falls back to those defaults wherever the
    /// folder has nothing to say.
    ///
    /// Files are matched by the <c>component</c> key INSIDE each JSON, never by file
    /// name, so a folder can be called "clay-fast" and its files anything at all. One
    /// folder per document: a second Load Settings component is refused rather than
    /// silently overwriting the first one's registration. Nothing is persisted with
    /// the .gh file - the folder is a machine-local convenience, re-read on solve.</summary>
    public sealed class LoadSettingsComponent : IamComponentBase, IButtonRowHost
    {
        public LoadSettingsComponent()
            : base("Load Settings", "Settings",
                   "Set a settings folder, then press Apply to canvas to create or update matching components' inputs.",
                   "Lugworm", "1 Slice")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0033-4c1b-9c3d-aa0000000033");
        static Bitmap? _icon;
        protected override Bitmap Icon => _icon ??= IamIcons.Settings();

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("folder", "F", "Folder with *.lwset.json or older *.iamset.json files, one per component type", "core", PType.Text)
                .HintText(""),
            new ParamSpec("watch", "W", "Rescan changed settings files on each solve when true, or scan once per folder path when false", "core", PType.Boolean).HintBool(true),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("files", "F", "One line per match: the component key and the file it takes its inputs from", GH_ParamAccess.list);
            pManager.AddTextParameter("unmatched", "U", "Files that were not used: unreadable ones with their error, duplicate claims on one component, and files naming a component this build does not have", GH_ParamAccess.list);
            pManager.AddTextParameter("info", "i", "Diagnostics", GH_ParamAccess.item);
        }

        // ---- capsule ----

        public IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows => new[]
        {
            new[]
            {
                new ButtonDef("apply", "Apply to canvas", GH_Palette.Black,
                              _registered && _index != null && _index.ByComponent.Count > 0,
                              "Set folder to readable settings files first, then click to create or update matching components' inputs"),
            },
        };

        public void OnCapsulePressed(string id)
        {
            if (id == "apply") ApplyToCanvas();
        }

        // ---- scan state (session only; nothing is written to the .gh file) ----

        SettingsFolderIndex? _index;
        string _scannedFolder = "";
        string _fingerprint = "";
        bool _registered;

        sealed class Result
        {
            public List<string> Files = new List<string>();
            public List<string> Unmatched = new List<string>();
            public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            var res = new Result();
            string folder = GetString(DA, "folder", "").Trim();
            bool watch = GetBool(DA, "watch", true);
            var doc = OnPingDocument();
            Guid documentId = doc?.DocumentID ?? Guid.Empty;

            // one settings folder per document - a second component reports instead of
            // silently stealing the registration (whose folder would win is arbitrary)
            Guid owner = SettingsRegistry.OwnerFor(documentId);
            if (owner != Guid.Empty && owner != InstanceGuid && doc != null && StillInDocument(doc, owner))
            {
                _registered = false;
                _index = null;
                _scannedFolder = "";
                _fingerprint = "";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "One settings folder per document. Another Load Settings component already owns this one");
                res.Info = "refused: another Load Settings component owns this document";
                Message = "refused";
                return res;
            }

            if (folder.Length == 0)
            {
                Release(documentId);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No folder set, so components use their internal defaults");
                res.Info = "no folder set";
                Message = null;
                return res;
            }

            string? readError = null;
            try
            {
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("folder not found: " + folder);
                string fingerprint = Fingerprint(folder);
                bool reuse = _index != null
                             && string.Equals(_scannedFolder, folder, StringComparison.OrdinalIgnoreCase)
                             && (!watch || string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal));
                if (!reuse)
                {
                    var texts = Read(folder, out var unreadableFiles);
                    _index = Merge(SettingsFolder.Scan(texts), unreadableFiles);
                    _scannedFolder = folder;
                    _fingerprint = fingerprint;
                }
            }
            catch (Exception ex)
            {
                readError = ex.Message;
            }

            if (readError != null || _index == null)
            {
                Release(documentId);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Settings folder unusable: " + (readError ?? "not scanned"));
                res.Info = "unusable folder: " + (readError ?? "not scanned");
                Message = "no folder";
                return res;
            }

            var index = _index;
            if (index.ByComponent.Count == 0 && index.Unreadable.Count == 0)
            {
                Release(documentId);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No Lugworm settings files in " + folder);
                res.Info = "no settings files in " + folder;
                Message = "0 files";
                return res;
            }

            SettingsRegistry.Register(documentId, InstanceGuid, folder, index);
            _registered = true;

            var known = KnownKeys();
            foreach (var kv in index.ByComponent.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string line = kv.Key + " ← " + Path.GetFileName(kv.Value.path);
                if (known.Contains(kv.Key)) res.Files.Add(line);
                else res.Unmatched.Add(Path.GetFileName(kv.Value.path) + ": no Lugworm component is called '" + kv.Key + "'");
            }
            foreach (var (path, error) in index.Unreadable)
                res.Unmatched.Add(Path.GetFileName(path) + ": " + error);
            foreach (var (component, paths) in index.Duplicates)
                res.Unmatched.Add("'" + component + "' is claimed by " + paths.Count.ToString(CultureInfo.InvariantCulture)
                    + " files. Using " + Path.GetFileName(paths[0]) + ", ignoring "
                    + string.Join(", ", paths.Skip(1).Select(Path.GetFileName)));

            if (index.Unreadable.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    SettingsIo.Count(index.Unreadable.Count, "file") + " could not be read. See the unmatched output");
            if (index.Duplicates.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    SettingsIo.Count(index.Duplicates.Count, "component key") + " claimed by more than one file. See the unmatched output");

            res.Info = string.Join(" - ", new[]
            {
                SettingsIo.Count(res.Files.Count, "component") + " served",
                SettingsIo.Count(res.Unmatched.Count, "file") + " unmatched",
                watch ? "watching" : "scanned once",
                folder,
            });
            Message = res.Files.Count.ToString(CultureInfo.InvariantCulture) + " files";
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataList(0, r.Files);
            DA.SetDataList(1, r.Unmatched);
            DA.SetData(2, r.Info);
        }

        // ---- apply to the whole canvas ----

        void ApplyToCanvas()
        {
            var doc = OnPingDocument();
            var index = _index;
            if (doc == null || index == null || index.ByComponent.Count == 0) return;

            RecordUndoEvent("Lugworm: apply settings to canvas");
            int components = 0, created = 0, updated = 0, skipped = 0;
            var unknownGroups = new List<string>();

            foreach (var target in doc.Objects.OfType<IamComponentBase>())
            {
                if (ReferenceEquals(target, this)) continue;
                var settings = index.For(target.SettingsKey);
                if (settings == null) continue;
                var wired = SettingsIo.ApplyCore(target, settings, out var unknown);
                components++;
                created += wired.Created.Count;
                updated += wired.Updated.Count;
                skipped += wired.Skipped.Count;
                foreach (string g in unknown) unknownGroups.Add(target.SettingsKey + "/" + g);
                target.ExpireSolution(false);
            }

            if (unknownGroups.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Unknown feature group(s): " + string.Join(", ", unknownGroups));
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                "Applied to " + SettingsIo.Count(components, "component") + ": "
                + created.ToString(CultureInfo.InvariantCulture) + " created, "
                + updated.ToString(CultureInfo.InvariantCulture) + " updated, "
                + skipped.ToString(CultureInfo.InvariantCulture) + " skipped");
            ExpireSolution(true);
            Grasshopper.Instances.RedrawCanvas();
        }

        // ---- folder reading ----

        static List<(string path, string text)> Read(string folder, out List<(string path, string error)> unreadable)
        {
            unreadable = new List<(string path, string error)>();
            var texts = new List<(string path, string text)>();
            foreach (string path in Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    // the folder is the operator's; a dataset export dropped next to the
                    // settings must not be read whole on the solve thread
                    long length = new FileInfo(path).Length;
                    if (length > MaxSettingsFileBytes)
                    {
                        unreadable.Add((path, "larger than " + (MaxSettingsFileBytes / 1024) + " KB, not a settings file "
                            + "(a file saved before 4.0.0 captured wired data such as G-code lines. Save it again from the component)"));
                        continue;
                    }
                    texts.Add((path, File.ReadAllText(path)));
                }
                catch (Exception ex) { unreadable.Add((path, ex.Message)); }
            }
            return texts;
        }

        static SettingsFolderIndex Merge(SettingsFolderIndex scan, List<(string path, string error)> unreadable)
        {
            if (unreadable.Count == 0) return scan;
            var all = unreadable
                .Concat(scan.Unreadable)
                .OrderBy(u => u.path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new SettingsFolderIndex(scan.ByComponent, all, scan.Duplicates);
        }

        /// <summary>Names + sizes + write times of the folder's JSON files: cheap enough
        /// to run per solve and enough to notice an edit without a watcher thread.</summary>
        static string Fingerprint(string folder)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string path in Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
                                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                sb.Append(path).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture))
                  .Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }

        static bool StillInDocument(GH_Document doc, Guid instanceId)
            => doc.Objects.Any(o => o.InstanceGuid == instanceId);

        void Release(Guid documentId)
        {
            SettingsRegistry.Unregister(documentId, InstanceGuid);
            _registered = false;
            _index = null;
            _scannedFolder = "";
            _fingerprint = "";
        }

        // ---- the component keys this build actually has ----

        static string[]? _knownKeys;

        /// <summary>Settings keys of every IamComponentBase in the plugin assembly,
        /// derived from the type names (the default SettingsKey rule) so nothing has to
        /// be instantiated. Cached for the session.</summary>
        const long MaxSettingsFileBytes = 1 << 20;

        static HashSet<string> KnownKeys()
        {
            if (_knownKeys == null)
            {
                var keys = new List<string>();
                Type?[] types;
                try { types = typeof(LoadSettingsComponent).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }   // the loadable ones are still listed
                foreach (var type in types)
                    if (type != null && !type.IsAbstract && typeof(IamComponentBase).IsAssignableFrom(type))
                        keys.Add(IamComponentBase.KeyForType(type));
                // an empty answer is never cached: it would mark every file unknown for the session
                if (keys.Count > 0) _knownKeys = keys.ToArray();
                return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            }
            return new HashSet<string>(_knownKeys, StringComparer.OrdinalIgnoreCase);
        }

        // ---- lifecycle ----

        public override void RemovedFromDocument(GH_Document document)
        {
            SettingsRegistry.Unregister(document?.DocumentID ?? Guid.Empty, InstanceGuid);
            _registered = false;
            base.RemovedFromDocument(document!);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close)
            {
                SettingsRegistry.Unregister(document?.DocumentID ?? Guid.Empty, InstanceGuid);
                _registered = false;
            }
            base.DocumentContextChanged(document!, context);
        }
    }
}
