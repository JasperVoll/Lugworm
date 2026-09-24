using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Lugworm.Ui
{
    public enum PType { Number, Integer, Boolean, Point, Vector, Curve, Geometry, Text, Time, Generic, Plane, Colour }

    /// <summary>Declarative description of one input parameter, tagged by feature group.
    /// Group "core" is always shown; other groups appear only when toggled on.
    /// Optional wire hints (Hint/HintBool/HintText) tell the one-click input wiring
    /// which slider range / toggle state / panel text to create for this input.</summary>
    public sealed class ParamSpec
    {
        public string Name, Nick, Desc, Group;
        public PType Type;
        public GH_ParamAccess Access;

        // wire hints (see InputWiringHelper)
        public double? WireMin, WireMax, WireDef;
        public int WireDecimals = 2;
        public bool WireBoolDef;
        public string? WireText;

        public ParamSpec(string name, string nick, string desc, string group, PType type,
                         GH_ParamAccess access = GH_ParamAccess.item)
        { Name = name; Nick = nick; Desc = desc; Group = group; Type = type; Access = access; }

        /// <summary>Slider range + default for a Number/Integer input.</summary>
        public ParamSpec Hint(double min, double max, double def, int decimals = 2)
        { WireMin = min; WireMax = max; WireDef = def; WireDecimals = Type == PType.Integer ? 0 : decimals; return this; }

        /// <summary>Default state for a Boolean toggle.</summary>
        public ParamSpec HintBool(bool def) { WireBoolDef = def; return this; }

        /// <summary>Panel content for a Text input (or number-list input; one value per line).</summary>
        public ParamSpec HintText(string text) { WireText = text; return this; }
    }

    /// <summary>Declarative description of one OUTPUT parameter, tagged by category.
    /// A component with output categories registers only the sockets a definition asked
    /// for: <see cref="Pinned"/> ones always, and the rest when their category is switched
    /// on from the context menu. Native Grasshopper types throughout - a hidden category
    /// is a socket that is not there, never a value flattened into text.</summary>
    public sealed class OutputSpec
    {
        public string Name, Nick, Desc, Category;
        public PType Type;
        public GH_ParamAccess Access;
        /// <summary>Always registered, whatever the categories say. The few outputs a
        /// console is useless without.</summary>
        public bool Pinned;

        public OutputSpec(string name, string nick, string desc, string category, PType type,
                          GH_ParamAccess access = GH_ParamAccess.item, bool pinned = false)
        { Name = name; Nick = nick; Desc = desc; Category = category; Type = type; Access = access; Pinned = pinned; }

        /// <summary>The same spec, always visible.</summary>
        public OutputSpec Pin() { Pinned = true; return this; }
    }

    /// <summary>One input socket a saved document still carries and the current build
    /// no longer declares, with whatever it was carrying: the objects wired into it and
    /// the literal value it held. Handed to
    /// <see cref="IamComponentBase.MigrateRetiredInputs"/> once, after the document has
    /// finished loading and its wires have resolved.</summary>
    public sealed class RetiredSocket
    {
        public RetiredSocket(string name, IReadOnlyList<IGH_Param> sources, object? value)
        { Name = name; Sources = sources; Value = value; }

        public string Name { get; }
        /// <summary>The objects that were wired into the retired socket. Handing these
        /// to a replacement input is the one lossless migration there is.</summary>
        public IReadOnlyList<IGH_Param> Sources { get; }
        /// <summary>The literal the socket held (a <c>double</c>, a <c>Point3d</c>, …),
        /// or null when it had none.</summary>
        public object? Value { get; }
        public bool CarriedSomething => Sources.Count > 0 || Value != null;
    }

    /// <summary>Base for Lugworm components: right-click feature-group toggles that
    /// add/remove their input params on the canvas (wire-preserving by name), enum
    /// value-list helper, and an optional on-canvas Compute button with a per-instance
    /// result cache and Auto/Manual mode. Subclasses provide specs + Compute/Emit.</summary>
    public abstract class IamComponentBase : GH_Component, IGH_VariableParameterComponent, IComputeButtonHost
    {
        static int _redrawPending;
        protected IamComponentBase(string name, string nick, string desc, string cat, string sub)
            : base(name, nick, desc, cat, sub) { }

        // ---- subclass contract ----
        protected abstract IReadOnlyList<ParamSpec> InputSpecs { get; }
        protected virtual IReadOnlyList<(string group, string label)> ToggleGroups
            => Array.Empty<(string, string)>();
        /// <summary>Declared outputs. A component that leaves this empty registers its
        /// outputs the ordinary way (its own <c>RegisterOutputParams</c> override) and
        /// gets no output categories.</summary>
        protected virtual IReadOnlyList<OutputSpec> OutputSpecs => Array.Empty<OutputSpec>();
        /// <summary>Output categories offered in the context menu, in menu order.</summary>
        protected virtual IReadOnlyList<(string group, string label)> OutputToggleGroups
            => Array.Empty<(string, string)>();
        protected virtual IReadOnlyList<(string param, string label, (string label, string val)[] items)> ValueLists
            => Array.Empty<(string, string, (string, string)[])>();
        protected virtual bool Heavy => false;
        protected abstract object? Compute(IGH_DataAccess DA);
        protected abstract void Emit(IGH_DataAccess DA, object result);

        protected readonly HashSet<string> Active = new HashSet<string>();
        readonly Dictionary<string, IGH_Param> _dormantParams = new Dictionary<string, IGH_Param>(StringComparer.Ordinal);

        // ---- output categories (mirrors the input groups above) ----
        /// <summary>Output categories switched on for this instance. Empty means only the
        /// pinned outputs are registered, which is what a new component starts as.</summary>
        protected readonly HashSet<string> ActiveOutputs = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, IGH_Param> _dormantOutputs = new Dictionary<string, IGH_Param>(StringComparer.Ordinal);

        // ---- compute gate ----
        bool _auto;
        bool _dirty = true;
        bool _force;
        double _lastMs = -1;
        bool _staleManualRemarkShown;

        public bool AutoCompute => _auto;
        public bool CacheStale => Heavy && !_auto && _dirty;
        public string ButtonLabel
            => _auto ? "Auto"
             : _dirty ? "Compute *"
             : _lastMs >= 0 ? $"Compute  ({_lastMs:0} ms)" : "Compute";

        public override void CreateAttributes()
        {
            m_attributes = new IamAttributes(this);
        }

        // accessors for the shared UI helpers (wiring, attributes, settings files)
        internal bool IsHeavy => Heavy;
        internal IReadOnlyList<ParamSpec> SpecsForUi => InputSpecs;
        internal IReadOnlyList<(string param, string label, (string label, string val)[] items)> ValueListsForUi => ValueLists;
        /// <summary>Whether "inputs" may create a source for this input when it has none. A
        /// settings file that carries the input still applies it. Default: every input.</summary>
        protected internal virtual bool AutoWires(string inputName) => true;
        internal IReadOnlyCollection<string> ActiveGroupsForUi => Active;

        /// <summary>Stable identity of this component CLASS in a settings file: the type
        /// name without a trailing "Component", lower-cased (SlicerComponent -> "slicer",
        /// DuetPrintConsoleComponent -> "duetprintconsole"). It is the key a settings
        /// file is matched by, so it must stay put across renames of the display name -
        /// override only to keep an already-shipped key alive.</summary>
        public virtual string SettingsKey => KeyForType(GetType());

        /// <summary>The default <see cref="SettingsKey"/> rule, so the Load Settings
        /// component can enumerate the known keys without instantiating components.</summary>
        internal static string KeyForType(Type type)
        {
            string name = type.Name;
            const string suffix = "Component";
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - suffix.Length);
            return name.ToLowerInvariant();
        }

        /// <summary>Switches a feature group ON without its own undo record or solution -
        /// the settings loader groups every change of one file into one undo event and
        /// one recompute (see <see cref="SettingsIo"/>).</summary>
        internal bool ActivateGroupForUi(string group)
        {
            if (group == null || group.Length == 0 || group == "core") return false;
            if (!ToggleGroups.Any(g => g.group == group)) return false;
            if (Active.Contains(group)) return true;
            Active.Add(group);
            OnGroupsChanged();
            SyncInputs(recordUndo: false, recompute: false);
            return true;
        }

        // ---- input registration ----
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            foreach (var s in InputSpecs)
                if (s.Group == "core" || Active.Contains(s.Group))
                    pManager.AddParameter(CreateParam(s));
        }

        static IGH_Param CreateParam(ParamSpec s)
        {
            IGH_Param p = ParamOfType(s.Type);
            p.Name = s.Name; p.NickName = s.Nick; p.Description = s.Desc;
            p.Access = s.Access; p.Optional = true;
            return p;
        }

        static IGH_Param ParamOfType(PType type)
        {
            IGH_Param p = type switch
            {
                PType.Number => new Param_Number(),
                PType.Integer => new Param_Integer(),
                PType.Boolean => new Param_Boolean(),
                PType.Point => new Param_Point(),
                PType.Vector => new Param_Vector(),
                PType.Curve => new Param_Curve(),
                PType.Geometry => new Param_Geometry(),
                PType.Text => new Param_String(),
                PType.Time => new Param_Time(),
                PType.Generic => new Param_GenericObject(),
                PType.Plane => new Param_Plane(),
                PType.Colour => new Param_Colour(),
                _ => (IGH_Param)new Param_Number(),
            };
            return p;
        }

        // ---- output registration (only for components that declare OutputSpecs) ----

        /// <summary>Registers the pinned outputs plus every output whose category is on.
        /// A component with no <see cref="OutputSpecs"/> overrides this itself, exactly as
        /// before.</summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            foreach (var s in VisibleOutputSpecs()) pManager.AddParameter(CreateOutputParam(s));
        }

        List<OutputSpec> VisibleOutputSpecs()
        {
            var visible = new List<OutputSpec>();
            foreach (var s in OutputSpecs)
                if (s.Pinned || ActiveOutputs.Contains(s.Category)) visible.Add(s);
            return visible;
        }

        /// <summary>The output names currently registered, for a projection that must not
        /// pay for values nobody can read.</summary>
        internal IReadOnlyList<string> VisibleOutputNames()
        {
            var names = new List<string>();
            foreach (var s in VisibleOutputSpecs()) names.Add(s.Name);
            return names;
        }

        static IGH_Param CreateOutputParam(OutputSpec s)
        {
            var p = ParamOfType(s.Type);
            p.Name = s.Name; p.NickName = s.Nick; p.Description = s.Desc; p.Access = s.Access;
            return p;
        }

        /// <summary>Switches one output category. A category with a WIRED socket is never
        /// switched off: unregistering it would silently cut a definition's wire, so the
        /// refusal is reported and the sockets stay.</summary>
        protected void ToggleOutputGroup(string group)
        {
            if (group == null || group.Length == 0) return;
            if (ActiveOutputs.Contains(group))
            {
                var wired = new List<string>();
                foreach (var s in OutputSpecs)
                {
                    if (s.Pinned || s.Category != group) continue;
                    int i = Params.IndexOfOutputParam(s.Name);
                    if (i >= 0 && Params.Output[i].Recipients.Count > 0) wired.Add(s.Name);
                }
                if (wired.Count > 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "Output(s) " + string.Join(", ", wired) + " are wired, so this category stays visible. "
                        + "Disconnect them first");
                    Grasshopper.Instances.RedrawCanvas();
                    return;
                }
                ActiveOutputs.Remove(group);
            }
            else ActiveOutputs.Add(group);
            SyncOutputs();
        }

        void SyncOutputs(bool recordUndo = true, bool recompute = true)
        {
            if (OutputSpecs.Count == 0) return;
            if (recordUndo) RecordUndoEvent("Lugworm: outputs shown");
            var desired = VisibleOutputSpecs();

            for (int i = Params.Output.Count - 1; i >= 0; i--)
            {
                var prm = Params.Output[i];
                if (desired.Any(d => d.Name == prm.Name)) continue;
                // Never unregister a socket a definition is reading: a wire is data.
                if (prm.Recipients.Count > 0) continue;
                _dormantOutputs[prm.Name] = prm;
                Params.UnregisterOutputParameter(prm, false);
            }
            for (int di = 0; di < desired.Count; di++)
            {
                var s = desired[di];
                if (Params.Output.Any(pp => pp.Name == s.Name)) continue;
                if (!_dormantOutputs.TryGetValue(s.Name, out var prm)) prm = CreateOutputParam(s);
                _dormantOutputs.Remove(s.Name);
                Params.RegisterOutputParam(prm, Math.Min(di, Params.Output.Count));
            }
            ReorderOutputs();
            Params.OnParametersChanged();
            VariableParameterMaintenance();
            Attributes?.ExpireLayout();
            if (recompute) ExpireSolution(true);
        }

        /// <summary>Puts the registered outputs back into declaration order. A socket that
        /// survived because it is wired keeps its wire: unregistering without isolating
        /// leaves the recipients attached while the socket moves.</summary>
        void ReorderOutputs()
        {
            var order = new List<IGH_Param>();
            foreach (var s in OutputSpecs)
            {
                int i = Params.IndexOfOutputParam(s.Name);
                if (i >= 0) order.Add(Params.Output[i]);
            }
            foreach (var p in Params.Output) if (!order.Contains(p)) order.Add(p);
            if (order.SequenceEqual(Params.Output)) return;
            foreach (var p in order) Params.UnregisterOutputParameter(p, false);
            foreach (var p in order) Params.RegisterOutputParam(p);
        }

        // ---- emission by NAME (indices move as categories toggle) ----

        /// <summary>Sets one output by name. Returns false when the output is not
        /// registered, which is the whole point: an <c>Emit</c> written against names can
        /// never write a value into the wrong socket after a category was hidden.</summary>
        protected bool SetOut(IGH_DataAccess DA, string name, object? value)
        {
            int i = Params.IndexOfOutputParam(name);
            if (i < 0 || value == null) return false;
            return DA.SetData(i, value);
        }

        protected bool SetOutList(IGH_DataAccess DA, string name, System.Collections.IEnumerable? values)
        {
            int i = Params.IndexOfOutputParam(name);
            if (i < 0) return false;
            return DA.SetDataList(i, values);
        }

        /// <summary>True when the named output is registered right now.</summary>
        protected bool OutputShown(string name) => Params.IndexOfOutputParam(name) >= 0;

        /// <summary>Lists the sockets a category carries, so the menu says what it adds.</summary>
        string OutputCategoryTooltip(string group)
        {
            var names = new List<string>();
            foreach (var s in OutputSpecs) if (s.Category == group && !s.Pinned) names.Add(s.Name);
            return names.Count == 0 ? "" : string.Join(", ", names);
        }

        // ---- group toggles ----
        protected void ToggleGroup(string group)
        {
            if (Active.Contains(group)) Active.Remove(group);
            else Active.Add(group);
            OnGroupsChanged();
            SyncInputs();
        }

        /// <summary>Override to cascade dependent groups (e.g. arcs off -> arcwelder off).</summary>
        protected virtual void OnGroupsChanged() { }

        void SyncInputs(bool recordUndo = true, bool recompute = true)
        {
            if (recordUndo) RecordUndoEvent("Lugworm: toggle inputs");
            var desired = InputSpecs.Where(s => s.Group == "core" || Active.Contains(s.Group)).ToList();

            for (int i = Params.Input.Count - 1; i >= 0; i--)
            {
                var prm = Params.Input[i];
                if (!desired.Any(d => d.Name == prm.Name))
                {
                    _dormantParams[prm.Name] = prm;
                    // isolate:false keeps the param's sources attached to the dormant
                    // instance, so re-enabling the group restores its wires in-session
                    Params.UnregisterInputParameter(prm, false);
                }
            }
            for (int di = 0; di < desired.Count; di++)
            {
                var s = desired[di];
                if (!Params.Input.Any(pp => pp.Name == s.Name))
                {
                    int pos = Math.Min(di, Params.Input.Count);
                    if (!_dormantParams.TryGetValue(s.Name, out var prm)) prm = CreateParam(s);
                    Params.RegisterInputParam(prm, pos);
                    _dormantParams.Remove(s.Name);
                }
            }
            Params.OnParametersChanged();
            VariableParameterMaintenance();
            // like SyncOutputs: a freshly registered socket has no position until the
            // attributes are laid out again, and the input wiring reads those positions
            Attributes?.ExpireLayout();
            if (recompute) ExpireSolution(true);
        }

        // ---- right-click menu ----
        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            Menu_AppendItem(menu, "Wire inputs (sliders / toggles / lists)",
                (s, e) => SettingsIo.WireInputs(this));
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Save settings…", (s, e) => SettingsIo.Save(this));
            Menu_AppendItem(menu, "Load settings…", (s, e) => SettingsIo.Load(this));
            Menu_AppendSeparator(menu);

            foreach (var (group, label) in ToggleGroups)
                Menu_AppendItem(menu, label, (s, e) => ToggleGroup(group), true, Active.Contains(group));

            if (OutputToggleGroups.Count > 0)
            {
                Menu_AppendSeparator(menu);
                var outputs = Menu_AppendItem(menu, "Outputs shown");
                outputs.ToolTipText = "Adds or removes whole categories of output sockets. "
                                    + "A category with a wired socket stays visible.";
                foreach (var (group, label) in OutputToggleGroups)
                {
                    var item = Menu_AppendItem(outputs.DropDown, label,
                        (s, e) => ToggleOutputGroup(group), true, ActiveOutputs.Contains(group));
                    item.ToolTipText = OutputCategoryTooltip(group);
                }
            }

            // NOTE: no per-param "create value list" items - the "⇤ inputs" button
            // creates the value lists (and everything else) in one click

            if (Heavy)
            {
                Menu_AppendSeparator(menu);
                Menu_AppendItem(menu, "Auto-compute", (s, e) =>
                {
                    _auto = !_auto;
                    if (_auto) { _dirty = true; ExpireSolution(true); }
                    else Grasshopper.Instances.RedrawCanvas();
                }, true, _auto);
            }
        }

        // ---- compute button ----
        public void OnComputePressed()
        {
            _staleManualRemarkShown = false;
            _force = true;
            ExpireSolution(true);
        }

        /// <summary>A background operation completed on the UI thread. Heavy/manual
        /// components must consume that completion in the resulting solve; treating it
        /// like an ordinary input expiry would only replay the stale cache and require
        /// a second Compute press.</summary>
        internal void OnBackgroundWorkCompleted()
        {
            _force = true;
            ExpireSolution(true);
        }

        // the force flag must survive ALL iterations of one solution (GH re-enters
        // SolveInstance per list/tree branch); consuming it inside SolveInstance made
        // a forced multi-iteration solve compute only iteration 1
        bool _forceThisSolution;

        protected override void BeforeSolveInstance()
        {
            _forceThisSolution = _force;
            _force = false;
            if (_normalizePending || _outputsPending) ScheduleArchiveNormalization(OnPingDocument());
            foreach (var (level, text) in _archiveNotices) AddRuntimeMessage(level, text);
            base.BeforeSolveInstance();
        }

        // ---- solve with optional manual gate + cache ----
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool manual = Heavy && !_auto;
            int iteration = CacheIteration(DA);
            string cacheIdentity = CacheIdentity(DA, iteration);
            if (manual && !_forceThisSolution)
            {
                // an expire reached us => inputs changed => mark stale, re-emit last good
                _dirty = true;
                if (SolveCache.TryGet(InstanceGuid, iteration, cacheIdentity, out var cached) && cached != null)
                    Emit(DA, cached);
                else if (!_staleManualRemarkShown)
                {
                    _staleManualRemarkShown = true;
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "No cached result. Press Compute");
                }
                return;
            }

            var sw = Stopwatch.StartNew();
            object? result = Compute(DA);
            sw.Stop();
            _lastMs = sw.Elapsed.TotalMilliseconds;
            _staleManualRemarkShown = false;

            if (Heavy)
            {
                _dirty = false;
                if (result != null) SolveCache.Store(InstanceGuid, iteration, cacheIdentity, result);
                else SolveCache.Clear(InstanceGuid, iteration, cacheIdentity);
            }
            if (result != null)
            {
                Emit(DA, result);
                RequestRedraw();
            }
        }

        static void RequestRedraw()
        {
            if (Interlocked.CompareExchange(ref _redrawPending, 1, 0) != 0) return;
            try
            {
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try { Grasshopper.Instances.RedrawCanvas(); }
                    finally { Interlocked.Exchange(ref _redrawPending, 0); }
                }));
            }
            // best effort: a canvas redraw. Rhino does not document what the UI-thread
            // marshal throws while the host is closing, so this stays broad - but the
            // pending flag MUST be released or no later solve ever asks for a redraw again.
            catch (Exception) { Interlocked.Exchange(ref _redrawPending, 0); }
        }

        // ---- read-by-name helpers (params shift as groups toggle) ----

        /// <summary>True when the named input exists and has something wired into it.
        /// Needed where a default carries meaning that a newly wired definition must
        /// not silently override - see MoverComponent's print centre.</summary>
        protected bool IsWired(string name)
        {
            int i = Params.IndexOfInputParam(name);
            return i >= 0 && Params.Input[i].SourceCount > 0;
        }

        protected double GetNumber(IGH_DataAccess DA, string name, double def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            double v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected int GetInt(IGH_DataAccess DA, string name, int def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            int v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected bool GetBool(IGH_DataAccess DA, string name, bool def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            bool v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected Point3d GetPoint(IGH_DataAccess DA, string name, Point3d def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            Point3d v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected Vector3d GetVector(IGH_DataAccess DA, string name, Vector3d def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            Vector3d v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected Curve? GetCurve(IGH_DataAccess DA, string name)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return null;
            Curve? c = null; DA.GetData(i, ref c); return c;
        }
        protected GeometryBase? GetGeometry(IGH_DataAccess DA, string name)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return null;
            GeometryBase? g = null; DA.GetData(i, ref g); return g;
        }
        protected List<GeometryBase?> GetGeometryList(IGH_DataAccess DA, string name)
        {
            var outp = new List<GeometryBase?>();
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return outp;

            try
            {
                DA.GetDataList(i, outp);
            }
            catch (System.InvalidOperationException)
            {
                // Parameter Access is set to item instead of list. Try single value.
                GeometryBase? g = null;
                if (DA.GetData(i, ref g)) outp.Add(g);
            }
            return outp;
        }
        protected List<double> GetNumberList(IGH_DataAccess DA, string name)
        {
            var outp = new List<double>();
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return outp;
            try { DA.GetDataList(i, outp); }
            catch (System.InvalidOperationException)
            {
                // Parameter Access is set to item instead of list. Try single value.
                double v = 0;
                if (DA.GetData(i, ref v)) outp.Add(v);
            }
            return outp;
        }
        protected List<object?> GetObjectList(IGH_DataAccess DA, string name)
        {
            var raw = new List<Grasshopper.Kernel.Types.IGH_Goo>();
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return new List<object?>();
            DA.GetDataList(i, raw);
            var outp = new List<object?>();
            foreach (var goo in raw)
            {
                if (goo is Grasshopper.Kernel.Types.GH_ObjectWrapper w) outp.Add(w.Value);
                else if (goo is Grasshopper.Kernel.Types.GH_String s) outp.Add(s.Value);
                else outp.Add(goo?.ScriptVariable());
            }
            return outp;
        }
        protected Plane GetPlane(IGH_DataAccess DA, string name, Plane def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            Plane v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected System.Drawing.Color GetColour(IGH_DataAccess DA, string name, System.Drawing.Color def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            System.Drawing.Color v = def; return DA.GetData(i, ref v) ? v : def;
        }
        protected List<System.Drawing.Color> GetColourList(IGH_DataAccess DA, string name)
        {
            var outp = new List<System.Drawing.Color>();
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return outp;
            try { DA.GetDataList(i, outp); }
            catch (System.InvalidOperationException)
            {
                // Parameter Access is set to item instead of list. Try single value.
                System.Drawing.Color v = System.Drawing.Color.Empty;
                if (DA.GetData(i, ref v)) outp.Add(v);
            }
            return outp;
        }
        protected List<int> GetIntList(IGH_DataAccess DA, string name)
        {
            var outp = new List<int>();
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return outp;

            try
            {
                DA.GetDataList(i, outp);
            }
            catch (System.InvalidOperationException)
            {
                // Parameter Access is set to item instead of list. Try single value.
                int val = 0;
                if (DA.GetData(i, ref val)) outp.Add(val);
            }
            return outp;
        }
        protected GH_Structure<GH_Curve>? GetCurveTree(IGH_DataAccess DA, string name)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return null;
            return DA.GetDataTree(i, out GH_Structure<GH_Curve> tree) ? tree : null;
        }
        protected string GetString(IGH_DataAccess DA, string name, string def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            string v = def; return DA.GetData(i, ref v) ? v : def;
        }

        static int CacheIteration(IGH_DataAccess da)
        {
            try
            {
                var p = da.GetType().GetProperty("Iteration");
                if (p != null) return System.Convert.ToInt32(p.GetValue(da, null), System.Globalization.CultureInfo.InvariantCulture);
            }
            // best effort: IGH_DataAccess does not expose Iteration on the interface, so
            // this reads a Grasshopper implementation detail by reflection. Broad because
            // a host that renames, retypes or throws from that property must all read the
            // same way here: iteration 0, which only ever widens the cache identity.
            catch (Exception) { }
            return 0;
        }
        static string CacheIdentity(IGH_DataAccess da, int iteration)
        {
            // the document's unit scale is part of the identity (AUD-COR-001): a result
            // cached for one unit system must not replay after the user rescales the doc
            string unit = ":u" + Convert.DocUnits.ScaleToMm().ToString(
                "R", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                var p = da.GetType().GetProperty("IterationPath");
                var value = p?.GetValue(da, null);
                if (value != null) return iteration.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value + unit;
            }
            // best effort: same reflection caveat as CacheIteration. Falling back to the
            // iteration alone makes the identity COARSER, never wrong - two branches that
            // would have had distinct keys share one and the later solve recomputes.
            catch (Exception) { }
            return iteration.ToString(System.Globalization.CultureInfo.InvariantCulture) + unit;
        }
        protected List<string> GetStringList(IGH_DataAccess DA, string name)
        {
            var outp = new List<string>();
            int i = Params.IndexOfInputParam(name);
            if (i >= 0) DA.GetDataList(i, outp);
            return outp;
        }
        protected DateTime GetTime(IGH_DataAccess DA, string name, DateTime def)
        {
            int i = Params.IndexOfInputParam(name);
            if (i < 0) return def;
            DateTime v = def; return DA.GetData(i, ref v) ? v : def;
        }

        // ---- IGH_VariableParameterComponent (toggles drive params, not the +/- UI) ----
        public bool CanInsertParameter(GH_ParameterSide side, int index) => false;
        public bool CanRemoveParameter(GH_ParameterSide side, int index) => false;
        public IGH_Param CreateParameter(GH_ParameterSide side, int index) => null!;
        public bool DestroyParameter(GH_ParameterSide side, int index) => false;
        public void VariableParameterMaintenance()
        {
            foreach (var p in Params.Input) p.Optional = true;
            // deserialized .gh files may restore params with a stale Access (e.g. the
            // geo input saved as item-access by an older build) - re-enforce the specs
            foreach (var spec in InputSpecs)
            {
                int idx = Params.IndexOfInputParam(spec.Name);
                if (idx >= 0 && Params.Input[idx].Access != spec.Access)
                    Params.Input[idx].Access = spec.Access;
            }
        }

        // ---- lifecycle: evict the heavy-solve cache with the component so cached
        // results (whole curve trees) can't outlive the component or its document ----
        public override void RemovedFromDocument(GH_Document document)
        {
            SolveCache.Clear(InstanceGuid);
            base.RemovedFromDocument(document);
        }

        // RemovedFromDocument fires only on delete/cut - document CLOSE is signalled
        // here; without this, closed documents' heavy results leaked for the session
        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            if (context == GH_DocumentContext.Close) SolveCache.Clear(InstanceGuid);
            base.DocumentContextChanged(document, context);
        }

        // ---- legacy archive normalization (once per out-of-date archive) --------
        //
        // Grasshopper restores a saved component's inputs from the archive, and this
        // base class never reconciled what came back against the specs the running
        // build declares. Removing or moving a socket therefore left an old document
        // with sockets that no longer exist, or with wires one index off. Components
        // that change their input layout raise InputContractVersion; an archive written
        // below that version is normalized once, after the document has finished
        // loading and every wire has resolved, and whatever cannot be placed is
        // reported rather than guessed at.

        /// <summary>Archive key holding the input-contract version a document was saved
        /// with. Internal so a component whose OUTPUTS also changed can read it in its own
        /// <c>Read</c> and say so - the base normalizes inputs only, because Grasshopper
        /// drops a wire to an output that no longer exists without asking anyone.</summary>
        internal const string ContractKey = "iamInputContract";
        const string OutputContractKey = "iamOutputContract";
        bool _normalizePending;
        bool _outputsPending;
        string[]? _archivedInputNames;
        string[]? _archivedOutputNames;
        readonly List<(GH_RuntimeMessageLevel Level, string Text)> _archiveNotices = new();

        /// <summary>Says what a saved document's normalization did or could not do.
        /// Grasshopper clears runtime messages at the start of every solve and the
        /// normalization runs before one, so these are held and re-emitted. They stop
        /// on their own: once the document is saved and reopened there is nothing left
        /// to migrate and nothing to say.</summary>
        internal void AddArchiveNotice(GH_RuntimeMessageLevel level, string text)
        {
            if (!_archiveNotices.Any(n => n.Level == level && n.Text == text))
                _archiveNotices.Add((level, text));
            AddRuntimeMessage(level, text);
        }

        /// <summary>Raise when this component's input layout changes in a way a saved
        /// document cannot absorb on its own. 0 (the default) never normalizes.</summary>
        protected virtual int InputContractVersion => 0;

        /// <summary>Raise when this component starts hiding outputs a saved document still
        /// carries. An archive written below this version is reconciled once, after the
        /// document has finished loading: every category with a WIRED socket is switched
        /// on, so no existing wire is lost, and only unwired sockets go. 0 (the default)
        /// never migrates outputs.</summary>
        protected virtual int OutputContractVersion => 0;

        /// <summary>Input sockets this component used to declare and no longer does.
        /// Needed to tell a genuine legacy socket from an unknown one, and to know what
        /// its data meant.</summary>
        protected virtual IReadOnlyList<ParamSpec> RetiredInputSpecs => Array.Empty<ParamSpec>();

        /// <summary>Called once per out-of-date archive with every retired socket that
        /// carried something. Implementations must not throw and must not change
        /// geometry silently - report what they could not carry across.</summary>
        protected virtual void MigrateRetiredInputs(GH_Document doc, IReadOnlyList<RetiredSocket> sockets) { }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            ScheduleArchiveNormalization(document);
        }

        /// <summary>Both entry points exist on purpose: whether a loading document reads
        /// a component before or after it joins the document is Grasshopper's business,
        /// so the first solve arms it too if joining the document did not.</summary>
        void ScheduleArchiveNormalization(GH_Document? document)
        {
            if ((!_normalizePending && !_outputsPending) || document == null) return;
            bool inputs = _normalizePending, outputs = _outputsPending;
            _normalizePending = false;
            _outputsPending = false;
            // A scheduled solution runs on the UI thread once the document is complete:
            // sources AND recipients are resolved by then, which is exactly what the output
            // reconciliation has to read before it removes anything.
            try
            {
                document.ScheduleSolution(10, doc =>
                {
                    if (inputs)
                    {
                        try { NormalizeArchivedInputs(doc); }
                        catch (Exception ex)
                        {
                            AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                                "This document was saved by an older build and its inputs could not be "
                                + "normalized (" + ex.Message + "). Check the component's wires");
                        }
                    }
                    if (outputs)
                    {
                        try { NormalizeArchivedOutputs(); }
                        catch (Exception ex)
                        {
                            AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                                "This document's output sockets could not be reconciled (" + ex.Message
                                + "). They were left as they were saved");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // a host without a scheduler: arm the next entry point again and say so,
                // rather than leaving an older archive silently unreconciled
                _normalizePending = inputs;
                _outputsPending = outputs;
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "This document was saved by an older build and could not be normalized yet ("
                    + ex.Message + "). Check the component's wires");
            }
        }

        /// <summary>Reconciles a saved document's OUTPUTS with the categories this build
        /// declares. The rule is one sentence: a category that has a wired socket is
        /// switched on, and an unwired socket the current categories do not ask for is
        /// removed. So an old document that wired <c>pos</c> and <c>bed</c> reopens with
        /// Motion and Temperatures visible and every wire intact, while an old document
        /// that wired nothing reopens compact.</summary>
        void NormalizeArchivedOutputs()
        {
            if (OutputSpecs.Count == 0) return;
            bool changed = false;
            var restored = new List<string>();
            foreach (var param in Params.Output)
            {
                if (param.Recipients.Count == 0) continue;
                OutputSpec? spec = null;
                foreach (var s in OutputSpecs) if (s.Name == param.Name) { spec = s; break; }
                if (spec == null || spec.Pinned) continue;
                if (ActiveOutputs.Add(spec.Category)) { changed = true; restored.Add(spec.Category); }
            }
            // Fail safe: a socket the archive listed that did not come back as a parameter
            // object cannot be asked whether it was wired, so its category is switched on
            // and the socket is recreated. Losing a wire is worse than showing a socket.
            var archived = _archivedOutputNames;
            _archivedOutputNames = null;
            if (archived != null)
                foreach (string name in archived)
                {
                    if (Params.IndexOfOutputParam(name) >= 0) continue;
                    foreach (var s in OutputSpecs)
                        if (s.Name == name && !s.Pinned && ActiveOutputs.Add(s.Category))
                        { changed = true; restored.Add(s.Category); }
                }
            // Whether or not a category was switched on, the socket set still has to be
            // brought to what this build declares: the new outputs have to appear.
            SyncOutputs(recordUndo: false, recompute: false);
            foreach (var spec in OutputSpecs)
            {
                int i = Params.IndexOfOutputParam(spec.Name);
                if (i < 0) continue;
                Params.Output[i].Description = spec.Desc;   // tooltips follow the running build
                Params.Output[i].Access = spec.Access;
            }
            Params.OnParametersChanged();
            Attributes?.ExpireLayout();
            if (changed)
                AddArchiveNotice(GH_RuntimeMessageLevel.Remark,
                    "This document wired outputs that are now in the "
                    + string.Join(", ", restored.Distinct()) + " categories, so those stay visible. "
                    + "The rest are hidden and can be switched on again from the context menu");
            ExpireSolution(false);
        }

        /// <summary>The input names the archive held, read straight from the parameter
        /// chunk. Null whenever the layout is not the one this code understands, which
        /// makes every caller fall back to the by-name reconciliation.</summary>
        static string[]? ArchivedInputNames(GH_IReader reader) => ArchivedNames(reader, "InputCount", "param_input");

        static string[]? ArchivedOutputNames(GH_IReader reader) => ArchivedNames(reader, "OutputCount", "param_output");

        static string[]? ArchivedNames(GH_IReader reader, string countKey, string chunkName)
        {
            try
            {
                var data = reader.FindChunk("ParameterData");
                if (data == null) return null;
                int count = 0;
                if (!data.TryGetInt32(countKey, ref count) || count < 0 || count > 256) return null;
                var names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    var chunk = data.FindChunk(chunkName, i);
                    string name = "";
                    if (chunk == null || !chunk.TryGetString("Name", ref name) || name == null) return null;
                    names[i] = name;
                }
                return names;
            }
            // An archive we cannot read the names out of is reported as UNKNOWN (null),
            // which is what every checked failure above returns too - the caller then
            // reconciles by index instead of guessing at names. Broad because a malformed
            // chunk surfaces from GH_IO in several shapes and they all mean "unknown".
            catch (Exception) { return null; }
        }

        void NormalizeArchivedInputs(GH_Document doc)
        {
            var desired = InputSpecs.Where(s => s.Group == "core" || Active.Contains(s.Group)).ToList();
            var archived = _archivedInputNames;
            _archivedInputNames = null;
            var current = Params.Input.Select(p => p.Name).ToArray();
            bool archiveRestored = archived != null && archived.Length == current.Length
                                   && archived.SequenceEqual(current, StringComparer.Ordinal);

            if (archiveRestored || archived == null) ReconcileByName(doc, desired);
            else if (Params.Input.Count == desired.Count && TypesMatch(desired)) RepairByIndex(doc, desired, archived);
            else
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "This document was saved with a different set of inputs (" + string.Join(", ", archived)
                    + ") and they could not be matched to the current ones. Check this component's wires");
        }

        bool TypesMatch(List<ParamSpec> desired)
        {
            for (int i = 0; i < desired.Count; i++)
                if (!TypeMatches(Params.Input[i], desired[i])) return false;
            return true;
        }

        static bool TypeMatches(IGH_Param param, ParamSpec spec)
            => param != null && param.GetType() == ParamType(spec.Type);

        static Type ParamType(PType type) => type switch
        {
            PType.Number => typeof(Param_Number),
            PType.Integer => typeof(Param_Integer),
            PType.Boolean => typeof(Param_Boolean),
            PType.Point => typeof(Param_Point),
            PType.Vector => typeof(Param_Vector),
            PType.Curve => typeof(Param_Curve),
            PType.Geometry => typeof(Param_Geometry),
            PType.Text => typeof(Param_String),
            PType.Time => typeof(Param_Time),
            PType.Generic => typeof(Param_GenericObject),
            PType.Plane => typeof(Param_Plane),
            PType.Colour => typeof(Param_Colour),
            _ => typeof(Param_Number),
        };

        /// <summary>The archive brought its own parameter objects back, so every wire is
        /// on the socket it was saved on: drop the retired ones (after capturing what
        /// they held), add the sockets this build declares and no archive had, and put
        /// the survivors back in declaration order.</summary>
        void ReconcileByName(GH_Document doc, List<ParamSpec> desired)
        {
            var retiredSpecs = RetiredInputSpecs;
            var captured = new List<RetiredSocket>();
            var unexplained = new List<string>();
            for (int i = Params.Input.Count - 1; i >= 0; i--)
            {
                var param = Params.Input[i];
                if (desired.Any(s => s.Name == param.Name)) continue;
                var retired = retiredSpecs.FirstOrDefault(s => s.Name == param.Name && TypeMatches(param, s));
                if (retired == null) { unexplained.Add(param.Name); continue; }
                captured.Add(Capture(param, retired.Name));
                // isolate: the sources move to the replacement, they must not stay here
                Params.UnregisterInputParameter(param, true);
            }
            for (int di = 0; di < desired.Count; di++)
            {
                var spec = desired[di];
                if (Params.Input.Any(p => p.Name == spec.Name)) continue;
                if (!_dormantParams.TryGetValue(spec.Name, out var param)) param = CreateParam(spec);
                _dormantParams.Remove(spec.Name);
                Params.RegisterInputParam(param, Math.Min(di, Params.Input.Count));
            }
            Reorder(desired);
            Finish(doc, desired, captured, unexplained, Array.Empty<string>());
        }

        /// <summary>The constructor's parameter objects survived and the archive only
        /// filled them in by index, so each socket is carrying the data of whatever the
        /// archive had at that position. Move every wire to the socket that owns it -
        /// all at once, so no wire is dropped halfway - and hand the retired positions
        /// over.</summary>
        void RepairByIndex(GH_Document doc, List<ParamSpec> desired, string[] archived)
        {
            var retiredSpecs = RetiredInputSpecs;
            int shared = Math.Min(archived.Length, Params.Input.Count);
            bool misaligned = false;
            for (int i = 0; i < shared; i++)
                if (!string.Equals(archived[i], desired[i].Name, StringComparison.Ordinal)) misaligned = true;
            if (!misaligned) { Finish(doc, desired, Array.Empty<RetiredSocket>(), Array.Empty<string>(), Array.Empty<string>()); return; }

            var unexplained = archived.Where(n => desired.All(s => s.Name != n)
                                                  && retiredSpecs.All(s => s.Name != n)).Distinct().ToList();
            if (unexplained.Count > 0)
            {
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "This document's inputs (" + string.Join(", ", archived) + ") include sockets this build "
                    + "does not know (" + string.Join(", ", unexplained) + "), so nothing was moved. "
                    + "Check this component's wires");
                return;
            }

            // capture every wire first, then clear, then re-attach: a socket that moves
            // past another must not lose its sources to the intermediate state
            var carried = new List<(string name, List<IGH_Param> sources)>();
            for (int i = 0; i < shared; i++)
                carried.Add((archived[i], Params.Input[i].Sources.ToList()));
            for (int i = 0; i < shared; i++) Params.Input[i].RemoveAllSources();

            var captured = new List<RetiredSocket>();
            foreach (var (name, sources) in carried)
            {
                int target = Params.IndexOfInputParam(name);
                if (target >= 0) foreach (var source in sources) Params.Input[target].AddSource(source);
                else captured.Add(new RetiredSocket(name, sources, null));
            }
            // sockets the archive had beyond the restored count lost their data before
            // this code ever saw them - name them rather than pretend they migrated
            var dropped = archived.Skip(shared).ToList();
            Finish(doc, desired, captured, Array.Empty<string>(), dropped);
            AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                "This document was saved with a different socket order. Its wires were moved to the "
                + "inputs that own them, but any value typed straight into a moved socket was not. "
                + "Check this component's inputs once");
        }

        void Reorder(List<ParamSpec> desired)
        {
            var ordered = new List<IGH_Param>();
            foreach (var spec in desired)
            {
                int index = Params.IndexOfInputParam(spec.Name);
                if (index >= 0) ordered.Add(Params.Input[index]);
            }
            foreach (var param in Params.Input) if (!ordered.Contains(param)) ordered.Add(param);
            if (ordered.SequenceEqual(Params.Input)) return;
            // isolate: false keeps every source attached while the socket moves
            foreach (var param in ordered) Params.UnregisterInputParameter(param, false);
            foreach (var param in ordered) Params.RegisterInputParam(param);
        }

        static RetiredSocket Capture(IGH_Param param, string name)
        {
            object? value = null;
            try
            {
                if (param is Param_Number number)
                    value = number.PersistentData.AllData(true).OfType<GH_Number>().FirstOrDefault()?.Value;
                else if (param is Param_Point point)
                {
                    var goo = point.PersistentData.AllData(true).OfType<GH_Point>().FirstOrDefault();
                    if (goo != null) value = goo.Value;
                }
                else if (param is Param_Integer integer)
                    value = integer.PersistentData.AllData(true).OfType<GH_Integer>().FirstOrDefault()?.Value;
            }
            catch (Exception) { /* a literal we cannot read is reported as absent, never invented */ }
            return new RetiredSocket(name, param.Sources.ToList(), value);
        }

        void Finish(GH_Document doc, List<ParamSpec> desired, IReadOnlyList<RetiredSocket> captured,
                    IReadOnlyList<string> unexplained, IReadOnlyList<string> dropped)
        {
            foreach (var spec in desired)
            {
                int index = Params.IndexOfInputParam(spec.Name);
                if (index < 0) continue;
                var param = Params.Input[index];
                param.Description = spec.Desc;      // tooltips follow the running build
                param.Optional = true;
                param.Access = spec.Access;
            }
            Params.OnParametersChanged();
            VariableParameterMaintenance();
            Attributes?.ExpireLayout();

            var carrying = captured.Where(s => s.CarriedSomething).ToList();
            if (carrying.Count > 0) MigrateRetiredInputs(doc, carrying);
            if (unexplained.Count > 0)
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "This document has input(s) this build does not declare (" + string.Join(", ", unexplained)
                    + "). They were left in place and are ignored. Delete them once you have checked the wires");
            if (dropped.Count > 0)
                AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                    "Input(s) " + string.Join(", ", dropped) + " of this saved document could not be restored "
                    + "at all. Set them again on the components that own them now");
            // the solution this callback runs in front of picks the change up;
            // asking for another one from inside it would re-enter NewSolution
            ExpireSolution(false);
        }

        // ---- persistence ----
        public override bool Write(GH_IWriter writer)
        {
            if (InputContractVersion > 0) writer.SetInt32(ContractKey, InputContractVersion);
            if (OutputContractVersion > 0) writer.SetInt32(OutputContractKey, OutputContractVersion);
            writer.SetInt32("iamGroupVersion", 2);
            writer.SetString("iamActiveGroups", string.Join(",", Active));
            writer.SetString("iamActiveOutputGroups", string.Join(",", ActiveOutputs));
            writer.SetBoolean("iamAutoCompute", _auto);
            // dormant params (group toggled off at save time): persist their own state
            // (nickname, persistent data, properties) so a save while hidden does not
            // destroy them. Canvas wires to a hidden param do not survive a save -
            // GH reconnects sources only for registered params during document load.
            writer.SetInt32("iamDormantCount", _dormantParams.Count);
            int k = 0;
            foreach (var kv in _dormantParams)
            {
                if (kv.Value == null) continue;
                var chunk = writer.CreateChunk("iamDormantParam", k++);
                if (chunk == null) continue;
                chunk.SetString("name", kv.Key);
                // a dormant param that was never doc-registered (restored by Read) can
                // have incomplete state (e.g. null Attributes) that makes GH_Param.Write
                // throw - a failed param loses its persisted extras but the DOCUMENT
                // SAVE must never fail. Read() tolerates the missing "param" payload.
                try { kv.Value.Write(chunk.CreateChunk("param")); }
                catch (Exception)
                {
                    chunk.SetBoolean("paramWriteFailed", true);
                }
            }
            return base.Write(writer);
        }
        public override bool Read(GH_IReader reader)
        {
            if (InputContractVersion > 0)
            {
                int stored = 0;
                // absent key = an archive from before this component versioned its
                // inputs, which is exactly the case that needs normalizing
                if (!reader.TryGetInt32(ContractKey, ref stored) || stored < InputContractVersion)
                {
                    _normalizePending = true;
                    _archivedInputNames = ArchivedInputNames(reader);
                }
            }
            if (OutputContractVersion > 0)
            {
                int storedOutputs = 0;
                // absent key = an archive from before this component had output categories,
                // which is exactly the one whose 27 sockets have to be reconciled
                if (!reader.TryGetInt32(OutputContractKey, ref storedOutputs) || storedOutputs < OutputContractVersion)
                {
                    _outputsPending = true;
                    _archivedOutputNames = ArchivedOutputNames(reader);
                }
            }
            string outputGroups = "";
            if (reader.TryGetString("iamActiveOutputGroups", ref outputGroups) && outputGroups != null)
            {
                ActiveOutputs.Clear();
                var knownOutputs = new HashSet<string>(OutputToggleGroups.Select(g => g.group), StringComparer.Ordinal);
                foreach (var t in outputGroups.Split(','))
                    if (t.Length > 0 && knownOutputs.Contains(t)) ActiveOutputs.Add(t);
            }
            string groups = "";
            if (reader.TryGetString("iamActiveGroups", ref groups) && groups != null)
            {
                Active.Clear();
                var known = new HashSet<string>(ToggleGroups.Select(g => g.group), StringComparer.Ordinal);
                foreach (var t in groups.Split(','))
                {
                    if (t.Length == 0) continue;
                    if (known.Contains(t)) Active.Add(t);
                    else AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Unknown Lugworm feature group '" + t + "' was ignored");
                }
            }
            bool a = false;
            if (reader.TryGetBoolean("iamAutoCompute", ref a)) _auto = a;
            _dormantParams.Clear();
            int dormant = 0;
            if (reader.TryGetInt32("iamDormantCount", ref dormant))
            {
                for (int k = 0; k < dormant; k++)
                {
                    var chunk = reader.FindChunk("iamDormantParam", k);
                    if (chunk == null) continue;
                    string name = "";
                    if (!chunk.TryGetString("name", ref name) || name.Length == 0) continue;
                    var spec = InputSpecs.FirstOrDefault(s => s.Name == name);
                    if (spec == null) continue;   // spec removed in a newer build - drop silently
                    var prm = CreateParam(spec);
                    // dormant params are not doc-registered, so nothing else creates
                    // their attributes - GH_Param.Write NREs on a param without them
                    prm.CreateAttributes();
                    var payload = chunk.FindChunk("param");
                    if (payload != null)
                    {
                        try { prm.Read(payload); }
                        catch (Exception) { prm = CreateParam(spec); prm.CreateAttributes(); }
                    }
                    _dormantParams[name] = prm;
                }
            }
            return base.Read(reader);
        }
    }
}
