using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Lugworm.Core.Gcode;
using Lugworm.Duet;
using Lugworm.Ui;

namespace Lugworm.Components
{
    /// <summary>SD-card file manager for the Duet: list a directory, click through
    /// the entries, then download (save to a local path), move/rename on the board,
    /// or delete. All board traffic is capsule-driven (nothing on solve), delete is
    /// two-step armed, and delete/move refuse to touch the file of the running job -
    /// pulling the G-code out from under an active print stops it mid-air.</summary>
    public class DuetFilesComponent : MachineComponentBase
    {
        public DuetFilesComponent()
            : base("Machine Files", "Files",
                   "Manage files on a connected Duet through the buttons, with irreversible deletion requiring two presses.",
                   "7 Print")
        { }

        public override Guid ComponentGuid => new Guid("a1a2c3d4-0022-4b6b-9c3d-aa0000000022");
        static System.Drawing.Bitmap? _icon;
        protected override System.Drawing.Bitmap Icon => _icon ??= Ui.IamIcons.DuetFiles();

        // listing state (filled by the Refresh capsule, read by solves)
        List<DuetFileEntry> _files = new List<DuetFileEntry>();
        string _listedDir = "";        // directory _files was read from
        int _current;
        string _dirInput = "0:/gcodes";
        string? _dirOverride;          // set by Open/Up, cleared when the input changes
        string _lastDirInput = "";
        string _savePath = "";
        string _moveTo = "";
        DateTime _deleteArmedUntil = DateTime.MinValue;
        DateTime _maintenanceArmedUntil = DateTime.MinValue;

        string EffectiveDir => _dirOverride ?? _dirInput;

        protected override IReadOnlyList<ParamSpec> InputSpecs => new[]
        {
            new ParamSpec("host", "H", "Host output from Duet Console or Duet Connect, connected before using the file buttons", "core", PType.Text),
            new ParamSpec("dir", "Dr", "Board directory path to list on Refresh (empty = 0:/gcodes)", "core", PType.Text).HintText("0:/gcodes"),
            new ParamSpec("savePath", "Sv", "Local file or folder required before Download, which overwrites an existing file of the same name", "core", PType.Text),
            new ParamSpec("moveTo", "Mv", "Target required before Move: a new filename, a folder ending in /, or a full 0:/ path", "core", PType.Text),
        };

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("files", "F", "Last refreshed board directory listing with filenames and sizes in bytes (folders end with /)", GH_ParamAccess.list);
            pManager.AddTextParameter("selected", "S", "Full board path of the selected entry", GH_ParamAccess.item);
            pManager.AddNumberParameter("size", "Sz", "Selected file size, bytes", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "i", "Selected directory, file and last-action details", GH_ParamAccess.item);
        }

        // ---- selection helpers ---------------------------------------------------

        DuetFileEntry? Selected
            => _current >= 0 && _current < _files.Count ? _files[_current] : null;

        string SelectedPath
            => Selected == null ? "" : DuetFilePath.Join(_listedDir, Selected.Name);

        /// <summary>True when <paramref name="path"/> is the file of a job the
        /// machine is currently running / simulating / pausing on.</summary>
        bool IsActiveJobFile(string path)
        {
            return IsActiveJobFile(Session?.Latest, path);
        }

        static bool IsActiveJobFile(ObjectModelSnapshot? snap, string path)
        {
            if (snap?.Job?.FileName == null) return false;
            var st = snap.Status;
            bool jobLive = st == MachineStatus.Processing || st == MachineStatus.Simulating
                        || st == MachineStatus.Paused || st == MachineStatus.Pausing
                        || st == MachineStatus.Resuming || st == MachineStatus.Cancelling;
            return jobLive && string.Equals(snap.Job.FileName.Trim(), path,
                StringComparison.OrdinalIgnoreCase);
        }

        // ---- capsules --------------------------------------------------------------

        sealed class GateState
        {
            public readonly ActionGate Connected, File, Active, Refresh, Up, Open,
                Download, Move, Delete;

            public GateState(ActionGate connected, ActionGate file, ActionGate active,
                             ActionGate refresh, ActionGate up, ActionGate open,
                             ActionGate download, ActionGate move, ActionGate delete)
            {
                Connected = connected; File = file; Active = active;
                Refresh = refresh; Up = up; Open = open;
                Download = download; Move = move; Delete = delete;
            }
        }

        static ActionGate Gate(string action, ActionGate source)
            => new ActionGate(action, source.Reason, source.Level);

        static ActionGate ClearGate(string action, string? reason = null)
            => new ActionGate(action, reason, GateReport.None);

        GateState BuildGateState()
        {
            var sel = Selected;
            bool haveFile = sel != null && !sel.IsDirectory;
            bool deleteArmed = DateTime.Now < _deleteArmedUntil;
            bool maintenanceArmed = DateTime.Now < _maintenanceArmedUntil;
            var connected = ConnectedActionGate("");
            var file = haveFile ? ClearGate("") : ClearGate("", "select a file first");
            var active = haveFile && IsActiveJobFile(SelectedPath)
                ? new ActionGate("", "the running job owns this file", GateReport.Warning)
                : ClearGate("");

            ActionGate refresh = Gate("Refresh", connected);
            ActionGate up = connected.Reason != null
                ? Gate("Up", connected)
                : ClearGate("Up", _dirOverride != null ? null : "already at the input directory");
            string? openRoutine = sel == null ? "select a directory first" :
                sel.IsDirectory ? null : "selection is a file";
            ActionGate open = connected.Reason != null
                ? Gate("Open", connected) : ClearGate("Open", openRoutine);
            ActionGate download = connected.Reason != null
                ? Gate("Download", connected)
                : active.Reason != null ? Gate("Download", active)
                : Gate("Download", file);
            ActionGate move = connected.Reason != null
                ? Gate("Move", connected)
                : active.Reason != null ? Gate("Move", active)
                : file.Reason != null ? Gate("Move", file)
                : ClearGate("Move", maintenanceArmed
                    ? "armed. Click Move again to confirm"
                    : "set moveTo before clicking Move, with a second press within 3 s required for protected paths");
            ActionGate delete = connected.Reason != null
                ? Gate("Delete", connected)
                : active.Reason != null ? Gate("Delete", active)
                : file.Reason != null ? Gate("Delete", file)
                : ClearGate("Delete", deleteArmed
                    ? "armed. Click Delete again to confirm"
                    : "press Delete twice within 3 s to permanently remove the selected board file");
            return new GateState(connected, file, active, refresh, up, open,
                download, move, delete);
        }

        protected override IEnumerable<ActionGate> ActionGates()
        {
            var gates = BuildGateState();
            return new[] { gates.Refresh, gates.Up, gates.Open, gates.Download,
                gates.Move, gates.Delete };
        }

        public override IReadOnlyList<IReadOnlyList<ButtonDef>> ButtonRows
        {
            get
            {
                var sel = Selected;
                bool haveFile = sel != null && !sel.IsDirectory;
                bool deleteArmed = DateTime.Now < _deleteArmedUntil;
                bool maintenanceArmed = DateTime.Now < _maintenanceArmedUntil;
                var gates = BuildGateState();
                string? connectedReason = gates.Connected.Reason;
                string? activeReason = gates.Active.Reason;
                return new[]
                {
                    new[]
                    {
                        ActionButton("refresh", Busy ? BusyLabel ?? "…" : "Refresh", GH_Palette.Black,
                            connectedReason == null, connectedReason ?? "List dir on the connected machine using the current input or default directory"),
                        ActionButton("up", "Up", GH_Palette.Grey,
                            connectedReason == null && _dirOverride != null,
                            gates.Up.Reason ?? "Return to the parent of the directory opened with Open"),
                        ActionButton("open", "Open", GH_Palette.Grey,
                            connectedReason == null && sel != null && sel.IsDirectory,
                            gates.Open.Reason ?? "List the selected folder on the connected machine"),
                    },
                    new[]
                    {
                        new ButtonDef("prev", "◀ Prev", GH_Palette.Grey, _files.Count > 0 && _current > 0, "Select the previous entry after Refresh, without changing a file"),
                        new ButtonDef("next", "Next ▶", GH_Palette.Grey, _files.Count > 0 && _current < _files.Count - 1, "Select the next entry after Refresh, without changing a file"),
                    },
                    new[]
                    {
                        ActionButton("download", "Download", GH_Palette.Black,
                            connectedReason == null && haveFile && activeReason == null,
                            gates.Download.Reason ?? "Download the selected file with savePath set, overwriting any local file of the same name"),
                        ActionButton("move", maintenanceArmed ? "Move?" : "Move", GH_Palette.Brown,
                            connectedReason == null && haveFile && activeReason == null,
                            gates.Move.Reason),
                        ActionButton("delete", deleteArmed ? "Delete?" : "Delete", GH_Palette.Error,
                            connectedReason == null && haveFile && activeReason == null,
                            gates.Delete.Reason),
                    },
                };
            }
        }

        public override void OnCapsulePressed(string id)
        {
            switch (id)
            {
                case "refresh": Refresh(); return;

                case "prev":
                    _current = Math.Max(0, _current - 1);
                    _deleteArmedUntil = DateTime.MinValue;
                    _maintenanceArmedUntil = DateTime.MinValue;
                    ExpireFromUi();
                    return;
                case "next":
                    _current = Math.Min(_files.Count - 1, _current + 1);
                    _deleteArmedUntil = DateTime.MinValue;
                    _maintenanceArmedUntil = DateTime.MinValue;
                    ExpireFromUi();
                    return;

                case "open":
                    {
                        var sel = Selected;
                        if (sel == null || !sel.IsDirectory) return;
                        _dirOverride = DuetFilePath.Join(_listedDir, sel.Name);
                        Refresh();
                        return;
                    }
                case "up":
                    {
                        if (_dirOverride == null) return;
                        string parent = DuetFilePath.Directory(_dirOverride);
                        _dirOverride = string.Equals(parent, _dirInput, StringComparison.OrdinalIgnoreCase)
                            ? null : parent;
                        Refresh();
                        return;
                    }

                case "download": Download(); return;
                case "move": Move(); return;
                case "delete": Delete(); return;
            }
        }

        void Refresh()
        {
            string dir;
            try { dir = DuetFilePath.Normalize(EffectiveDir); }
            catch (ArgumentException ex) { LastActionResult = "ERROR: " + ex.Message; ExpireFromUi(); return; }
            _deleteArmedUntil = DateTime.MinValue;
            _maintenanceArmedUntil = DateTime.MinValue;
            RunGatedAction("listing " + dir, false, async (snap, client, ct) =>
            {
                int n = await RelistAsync(client, ct);
                return $"{n} entries in {dir}";
            });
        }

        void Download()
        {
            var sel = Selected;
            string path = SelectedPath;
            if (sel == null || path.Length == 0) return;
            string local = _savePath;
            if (string.IsNullOrWhiteSpace(local))
            {
                LastActionResult = "ERROR: wire a savePath (file or folder) before downloading";
                ExpireFromUi();
                return;
            }
            long expect = sel.Size;
            string name = sel.Name;
            RunGatedAction("downloading " + name, true, async (snap, client, ct) =>
            {
                if (IsActiveJobFile(snap, path))
                    throw new InvalidOperationException("refusing to download the file of the RUNNING job while it is changing");
                string resolved = SavePath.Resolve(local, name);
                // Written under a temporary name and renamed only once the transfer is
                // complete: a failed or short download must neither leave a truncated file
                // under the real name nor destroy a good copy that was already there.
                string partial = resolved + ".partial";
                TransferVerification verification;
                try
                {
                    using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                        verification = await client.DownloadToAsync(path, output, ct);
                    if (expect > 0 && verification.Size != expect)
                        throw new DuetException($"download incomplete: got {verification.Size} bytes, " +
                                                $"the listing said {expect}. Retry");
                    // an existing good copy is replaced in one step, never deleted first
                    if (File.Exists(resolved)) File.Replace(partial, resolved, null);
                    else File.Move(partial, resolved);
                }
                catch (Exception)
                {
                    // best effort: the partial file is worthless, and the transfer's own
                    // error is what the operator needs to read, so it is rethrown unchanged.
                    try { File.Delete(partial); } catch (Exception) { }
                    throw;
                }
                return $"saved {path} → {resolved} ({verification.Size:#,0} B, CRC {verification.Crc32})";
            });
        }

        void Move()
        {
            string path = SelectedPath;
            if (path.Length == 0) return;
            string target;
            try { target = DuetFilePath.ResolveMoveTarget(path, _moveTo); }
            catch (ArgumentException ex)
            {
                LastActionResult = "ERROR: " + ex.Message;
                ExpireFromUi();
                return;
            }
            bool protectedEndpoint = DuetFileEntry.IsProtected(path) || DuetFileEntry.IsProtected(target);
            if (protectedEndpoint && DateTime.Now >= _maintenanceArmedUntil)
            {
                _maintenanceArmedUntil = DateTime.Now.AddSeconds(3);
                LastActionResult = "armed. Click Move again to confirm the maintenance override for a protected board path";
                ExpireFromUi();
                return;
            }
            bool maintenanceOverride = protectedEndpoint;
            _maintenanceArmedUntil = DateTime.MinValue;
            RunGatedAction($"moving {DuetFilePath.FileName(path)}", true, async (snap, client, ct) =>
            {
                if (IsActiveJobFile(snap, path))
                    throw new InvalidOperationException("refusing to move the file of the RUNNING job");
                // The explicit armed flag is the only UI path that can override
                // the Core protected-path guard; ordinary moves remain guarded.
                await client.MoveFileAsync(path, target, ct, maintenanceOverride: maintenanceOverride);
                await RelistAsync(client, ct);
                return $"moved {path} → {target}";
            });
        }

        void Delete()
        {
            string path = SelectedPath;
            if (path.Length == 0) return;
            bool maintenanceOverride = DuetFileEntry.IsProtected(path);
            if (DateTime.Now >= _deleteArmedUntil)
            {
                // two-step arm: deleting a job file is irreversible - never a misclick
                _deleteArmedUntil = DateTime.Now.AddSeconds(3);
                LastActionResult = maintenanceOverride
                    ? "armed. Click Delete again to confirm the maintenance override for this protected board path"
                    : $"armed. Click Delete again to remove {DuetFilePath.FileName(path)}";
                ExpireFromUi();
                return;
            }
            _deleteArmedUntil = DateTime.MinValue;
            RunGatedAction($"deleting {DuetFilePath.FileName(path)}", true, async (snap, client, ct) =>
            {
                if (IsActiveJobFile(snap, path))
                    throw new InvalidOperationException("refusing to delete the file of the RUNNING job");
                // The existing two-press Delete arm is explicit maintenance
                // authorization when the selected path is protected.
                await client.DeleteFileAsync(path, ct, maintenanceOverride: maintenanceOverride);
                await RelistAsync(client, ct);
                return $"deleted {path}";
            });
        }

        /// <summary>Re-reads the directory inside the SAME serialized action, so the
        /// on-canvas list never keeps showing a file that was just deleted or moved.
        /// Returns the entry count (the state fields update on the UI thread).</summary>
        async System.Threading.Tasks.Task<int> RelistAsync(IMachineClient client, System.Threading.CancellationToken ct)
        {
            string dir = DuetFilePath.Normalize(EffectiveDir);
            var list = await client.ListFilesAsync(dir, ct);
            list.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                _files = list;
                _listedDir = dir;
                _current = list.Count == 0 ? 0 : Math.Min(_current, list.Count - 1);
            }));
            return list.Count;
        }

        // ---- solve -----------------------------------------------------------------

        sealed class Result
        {
            public List<string> Files = new List<string>();
            public string Selected = "";
            public double Size;
            public string Info = "";
        }

        protected override object? Compute(IGH_DataAccess DA)
        {
            HostInput = GetString(DA, "host", "").Trim();
            _dirInput = GetString(DA, "dir", "0:/gcodes").Trim();
            if (_dirInput.Length == 0) _dirInput = "0:/gcodes";
            _savePath = GetString(DA, "savePath", "").Trim();
            _moveTo = GetString(DA, "moveTo", "").Trim();

            // a changed dir input invalidates the Open/Up override and the listing
            if (!string.Equals(_dirInput, _lastDirInput, StringComparison.OrdinalIgnoreCase))
            {
                _lastDirInput = _dirInput;
                _dirOverride = null;
                if (!string.Equals(_listedDir, _dirInput, StringComparison.OrdinalIgnoreCase))
                { _files = new List<DuetFileEntry>(); _current = 0; _listedDir = ""; }
            }

            SubscribeTo(Session);
            ReportLastAction();
            ReportActionGates();

            var res = new Result();
            for (int i = 0; i < _files.Count; i++)
                res.Files.Add((i == _current ? "► " : "  ") + _files[i]);
            var sel = Selected;
            if (sel != null)
            {
                res.Selected = SelectedPath;
                res.Size = sel.Size;
            }
            res.Info = $"dir {EffectiveDir}  ({_files.Count} entries"
                     + (_listedDir.Length == 0 ? ", press Refresh" : "") + ")\n"
                     + (sel == null ? "nothing selected" :
                        $"selected [{_current + 1}/{_files.Count}] {sel}") + "\n"
                     + SessionInfo();
            Message = sel == null ? EffectiveDir : DuetFilePath.FileName(res.Selected);
            return res;
        }

        protected override void Emit(IGH_DataAccess DA, object result)
        {
            var r = (Result)result;
            DA.SetDataList(0, r.Files);
            DA.SetData(1, r.Selected);
            DA.SetData(2, r.Size);
            DA.SetData(3, r.Info);
        }
    }
}
