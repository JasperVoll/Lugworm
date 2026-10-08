using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Lugworm.Core.Settings;

namespace Lugworm.Ui
{
    /// <summary>What one wiring pass did, by input name.</summary>
    public readonly struct WireResult
    {
        readonly List<string>? _created, _updated, _skipped, _notes;

        internal WireResult(List<string> created, List<string> updated, List<string> skipped, List<string> notes)
        { _created = created; _updated = updated; _skipped = skipped; _notes = notes; }

        /// <summary>Inputs that had no source and got a new slider / toggle / panel / value list.</summary>
        public IReadOnlyList<string> Created => _created ?? (IReadOnlyList<string>)Array.Empty<string>();
        /// <summary>Inputs wired to a single slider / toggle / panel / value list that was set to the file value.</summary>
        public IReadOnlyList<string> Updated => _updated ?? (IReadOnlyList<string>)Array.Empty<string>();
        /// <summary>Inputs deliberately left alone, with the reason for each.</summary>
        public IReadOnlyList<string> Skipped => _skipped ?? (IReadOnlyList<string>)Array.Empty<string>();
        /// <summary>Human-readable notes about values that could not be honoured exactly.</summary>
        public IReadOnlyList<string> Notes => _notes ?? (IReadOnlyList<string>)Array.Empty<string>();

    }

    /// <summary>One-click input wiring: creates a slider (range/default from the
    /// ParamSpec wire hint), boolean toggle, value list or text panel for every
    /// currently visible input that has no source yet, placed left of the component
    /// and wired up. Triggered from the on-canvas "⇤ inputs" button or the
    /// right-click menu.
    ///
    /// With an <c>overrides</c> map (a settings file's inputs, see
    /// <see cref="ComponentSettings"/>) the created objects carry the file's values
    /// instead of the built-in defaults, and an input already wired to exactly ONE
    /// slider / toggle / panel / value list is set to the file value in place.
    /// Anything else upstream is never touched, only reported. Without overrides the
    /// behaviour is exactly what it has always been.
    ///
    /// Since 4.4.0 (D8) the create / update / skip decision itself is
    /// <see cref="SettingsPlan"/> in Core, over a plain description of what feeds each
    /// input. What is left here is the Grasshopper half: reading that description off
    /// the parameters, and making or setting the objects the plan asked for.</summary>
    public static class InputWiringHelper
    {
        const float ColX = 210f;      // distance left of the component
        const float MinGapY = 22f;    // min vertical spacing between created objects

        /// <param name="recompute">Expire the component once the objects are made.
        /// False lets a caller start the solution later; undo is still recorded.</param>
        public static WireResult WireAll(IamComponentBase comp,
                                         IReadOnlyDictionary<string, SettingsValue>? overrides = null,
                                         bool recompute = true)
        {
            var doc = comp.OnPingDocument();
            if (doc == null) return default;
            var plan = Prepare(comp, overrides);
            ProtectSharedSources(new[] { plan });
            var undo = new Grasshopper.Kernel.Undo.GH_UndoRecord("Lugworm: wire inputs");
            RecordChanges(plan, undo, new HashSet<Guid>());
            WireResult result;
            try { result = Execute(plan, undo, new HashSet<Guid>()); }
            finally { if (undo.ActionCount > 0) doc.UndoUtil.RecordEvent(undo); }
            if (recompute && result.Created.Count + result.Updated.Count > 0) comp.ExpireSolution(true);
            return result;
        }

        internal sealed class WireStep
        {
            public string Name = "";
            public IGH_Param? Parameter;
            public IGH_Param? Source;
            public SettingsValue? Value;
            public SettingsInputDecision Decision = null!;
            public IGH_DocumentObject? NewObject;
            public string? Refusal;
        }

        internal sealed class WiringPlan
        {
            public IamComponentBase Component = null!;
            public List<WireStep> Steps = new List<WireStep>();
        }

        internal static WiringPlan Prepare(IamComponentBase comp,
            IReadOnlyDictionary<string, SettingsValue>? overrides, IEnumerable<string>? groups = null)
        {
            var plan = new WiringPlan { Component = comp };
            foreach (var (spec, parameter) in comp.SettingsInputsForUi(groups))
            {
                var input = parameter == null ? new SettingsInputSource(spec.Name) : DescribeSources(parameter);
                input.HasSpec = true;
                input.Type = TypeOf(spec.Type);
                input.IsList = spec.Access != GH_ParamAccess.item;
                var list = comp.ValueListsForUi.FirstOrDefault(v => v.param == spec.Name);
                if (list.items != null && list.items.Length > 0) input.SpecItems = Items(list.items);
                SettingsValue? value = null;
                if (overrides != null && overrides.TryGetValue(spec.Name, out var found)) value = found;
                var decision = SettingsPlan.Decide(input, value);
                var step = new WireStep
                {
                    Name = spec.Name, Parameter = parameter, Value = value, Decision = decision,
                    Source = parameter?.SourceCount == 1 ? parameter.Sources[0] : null,
                };
                if (decision.Action == SettingsPlanAction.Create)
                {
                    if (value == null && !comp.AutoWires(spec.Name)) step.Refusal = "not automatically wired";
                    else step.NewObject = Make(spec.Nick, spec, comp.ValueListsForUi, value, decision);
                }
                plan.Steps.Add(step);
            }
            return plan;
        }

        internal static void ProtectSharedSources(IEnumerable<WiringPlan> plans)
        {
            var steps = plans.SelectMany(p => p.Steps).ToArray();
            // Freeze every request before refusing any: order must not select a winner.
            var requests = steps.Where(s => s.Parameter != null)
                .ToDictionary(s => s.Parameter!, s => s.Decision.Action == SettingsPlanAction.Update ? s.Value : null);
            foreach (var step in steps)
            {
                if (step.Decision.Action != SettingsPlanAction.Update || step.Source == null) continue;
                if (!SettingsPlan.SharedSourceAgrees(step.Value!, step.Source.Recipients.Select(
                    r => requests.TryGetValue(r, out var value) ? value : null)))
                    step.Refusal = "shared source has a recipient outside this apply or conflicting values";
            }
        }

        internal static void RecordChanges(WiringPlan plan, Grasshopper.Kernel.Undo.GH_UndoRecord undo,
                                           HashSet<Guid> recorded)
        {
            if (recorded.Add(plan.Component.InstanceGuid))
                undo.AddAction(new Grasshopper.Kernel.Undo.Actions.GH_GenericObjectAction(plan.Component));
            foreach (var step in plan.Steps)
                if (step.Refusal == null && step.Decision.Action == SettingsPlanAction.Update &&
                    step.Source != null && recorded.Add(step.Source.InstanceGuid))
                    undo.AddAction(new Grasshopper.Kernel.Undo.Actions.GH_GenericObjectAction(step.Source));
        }

        internal static WireResult Execute(WiringPlan plan, Grasshopper.Kernel.Undo.GH_UndoRecord undo,
                                           HashSet<Guid> updatedSources)
        {
            var created = new List<string>();
            var updated = new List<string>();
            var skipped = new List<string>();
            var notes = new List<string>();
            var comp = plan.Component;
            var doc = comp.OnPingDocument();
            if (doc == null) return new WireResult(created, updated, skipped, notes);
            var prms = comp.Params.Input.ToList();
            EnsureLaidOut(comp, prms);
            float lastBottom = float.MinValue;
            foreach (var step in plan.Steps)
            {
                var decision = step.Decision;
                if (decision.Note != null) notes.Add(decision.Note);
                if (step.Refusal != null || decision.Action == SettingsPlanAction.Skip)
                {
                    skipped.Add(step.Name + ": " + (step.Refusal ?? decision.Reason));
                    continue;
                }
                var prm = prms.FirstOrDefault(p => p.Name == step.Name);
                if (prm == null)
                {
                    if (step.Value != null) skipped.Add(step.Name + ": input is unavailable after activating groups");
                    continue;
                }
                if (decision.Action == SettingsPlanAction.Update && step.Source != null)
                {
                    if (updatedSources.Add(step.Source.InstanceGuid)) SetSource(step.Source, step.Value!, decision);
                    updated.Add(step.Name);
                }
                else if (decision.Action == SettingsPlanAction.Create && step.NewObject != null)
                {
                    var obj = step.NewObject;
                    float y = Math.Max(GripTop(comp, prm, prms.IndexOf(prm)), lastBottom + MinGapY);
                    obj.Attributes.Pivot = new PointF(comp.Attributes.Bounds.Left - ColX, y);
                    doc.AddObject(obj, false);
                    // Register the addition before any further mutation can fail.
                    undo.AddAction(new Grasshopper.Kernel.Undo.Actions.GH_AddObjectAction(obj));
                    prm.AddSource((IGH_Param)obj);
                    lastBottom = obj.Attributes.Bounds.Bottom;
                    created.Add(prm.Name);
                }
            }
            return new WireResult(created, updated, skipped, notes);
        }

        // ---- where the new objects go ----

        /// <summary>Lays the component out when any input grip has no position yet. A
        /// parameter registered since the last canvas paint - the inputs of a group a
        /// settings file has just switched on, or a document that has not been drawn -
        /// has attributes with empty bounds, and a slider placed at their Top landed at
        /// the top of the canvas instead of beside its socket.</summary>
        static void EnsureLaidOut(IamComponentBase comp, List<IGH_Param> prms)
        {
            var attr = comp.Attributes;
            if (attr == null) return;
            bool stale = attr.Bounds.IsEmpty;
            foreach (var prm in prms)
            {
                if (stale) break;
                var pa = prm.Attributes;
                stale = pa == null || pa.Bounds.IsEmpty || !attr.Bounds.IntersectsWith(pa.Bounds);
            }
            if (!stale) return;
            // best effort: the layout only reads fonts and parameter counts, but it is
            // Grasshopper's, so a failure here must not stop the wiring - the fallback in
            // GripTop still stacks the objects beside the component
            try { attr.ExpireLayout(); attr.PerformLayout(); } catch (Exception) { }
        }

        /// <summary>Canvas y of an input's grip, or a slot below the component's top edge
        /// for a parameter that still has no layout.</summary>
        static float GripTop(IamComponentBase comp, IGH_Param prm, int index)
        {
            var pa = prm.Attributes;
            if (pa != null && !pa.Bounds.IsEmpty) return pa.Bounds.Top;
            return comp.Attributes.Bounds.Top + index * MinGapY;
        }

        // ---- describing an input for the Core plan ----

        /// <summary>Only what is wired into an input - the half the capture side asks
        /// about (see <see cref="SettingsPlan.CaptureRefusal"/>).</summary>
        internal static SettingsInputSource DescribeSources(IGH_Param prm)
        {
            var input = new SettingsInputSource(prm.Name) { SourceCount = prm.SourceCount };
            if (prm.SourceCount != 1) return input;

            var source = prm.Sources[0];
            input.SourceHasInputs = source.SourceCount > 0;
            switch (source)
            {
                case GH_NumberSlider sl:
                    input.Kind = SettingsSourceKind.Slider;
                    input.SourceSliderIsInteger =
                        sl.Slider.Type == Grasshopper.GUI.Base.GH_SliderAccuracy.Integer;
                    input.SourceSliderDecimals = sl.Slider.DecimalPlaces;
                    break;
                case GH_BooleanToggle _:
                    input.Kind = SettingsSourceKind.Toggle;
                    break;
                case GH_Panel _:
                    input.Kind = SettingsSourceKind.Panel;
                    break;
                case GH_ValueList list:
                    input.Kind = SettingsSourceKind.ValueList;
                    input.SourceItems = list.ListItems
                        .Select(item => new SettingsListItem(item.Name, item.Expression)).ToList();
                    break;
                default:
                    input.Kind = SettingsSourceKind.Other;   // an expression, cluster or component
                    break;
            }
            input.SourceLabel = SourceName(source);
            return input;
        }

        /// <summary>The name the operator would recognise the upstream object by: the
        /// top-level document object that owns the wired parameter, not the socket.</summary>
        static string SourceName(IGH_Param source)
        {
            IGH_DocumentObject? owner = null;
            // best effort: naming the component a wire comes from. A param whose attributes
            // are not built yet (restored but never doc-registered) leaves owner null, and
            // the line below falls back to the param's own name - a worse label, never a
            // wrong one, and never a reason to fail the message being composed.
            try { owner = source.Attributes?.GetTopLevel?.DocObject; } catch (Exception) { }
            return owner != null && !ReferenceEquals(owner, source) ? owner.Name : source.Name;
        }

        static SettingsInputType TypeOf(PType type)
        {
            switch (type)
            {
                case PType.Number: return SettingsInputType.Number;
                case PType.Integer: return SettingsInputType.Integer;
                case PType.Boolean: return SettingsInputType.Boolean;
                case PType.Text: return SettingsInputType.Text;
                default: return SettingsInputType.Other;
            }
        }

        static IReadOnlyList<SettingsListItem> Items((string label, string val)[] items)
            => items.Select(i => new SettingsListItem(i.label, i.val)).ToList();

        // ---- creation ----

        static IGH_DocumentObject? Make(string nick, ParamSpec spec,
            IReadOnlyList<(string param, string label, (string label, string val)[] items)> valueLists,
            SettingsValue? over, SettingsInputDecision decision)
        {
            var vl = valueLists.FirstOrDefault(v => v.param == spec.Name);
            var items = vl.items ?? Array.Empty<(string label, string val)>();
            bool hasItems = items.Length > 0;

            switch (decision.ObjectKind)
            {
                case SettingsObjectKind.ValueList:
                    {
                        var list = MakeValueList(nick, items);
                        if (decision.ValueListIndex >= 0) list.SelectItem(decision.ValueListIndex);
                        return list;
                    }
                case SettingsObjectKind.Slider:
                    return MakeSlider(nick, spec, over);
                case SettingsObjectKind.Toggle:
                    return MakeToggle(nick, over != null ? over.AsBool(spec.WireBoolDef) : spec.WireBoolDef);
                case SettingsObjectKind.Panel:
                    {
                        // a numbers panel is narrower than a text or value-list one, and a
                        // list input needs the room for one item per line
                        bool numeric = !hasItems && (spec.Type == PType.Number || spec.Type == PType.Integer);
                        var panel = MakePanel(nick, over != null ? PanelText(over) : spec.WireText ?? "",
                                              numeric ? 110 : 140,
                                              spec.Access == GH_ParamAccess.item ? 20 : 60);
                        panel.Properties.Multiline = over != null ? !over.IsList : spec.Access == GH_ParamAccess.item;
                        return panel;
                    }
                default:
                    return null;   // geometry / curves etc. cannot be auto-fed
            }
        }

        static GH_NumberSlider MakeSlider(string nick, ParamSpec spec, SettingsValue? over)
        {
            double min = spec.WireMin ?? 0;
            double max = spec.WireMax ?? Math.Max(1, (spec.WireDef ?? 1) * 2);
            double def = over?.AsNumber(spec.WireDef ?? 0.5 * (min + max)) ?? spec.WireDef ?? 0.5 * (min + max);
            int decimals = Math.Max(0, spec.WireDecimals);
            if (over != null && spec.Type != PType.Integer)
                decimals = Math.Max(decimals,
                    Math.Min(SettingsPlan.MaxSliderDecimals, SettingsPlan.DecimalsOf(over.AsText())));

            var sl = new GH_NumberSlider { NickName = nick };
            sl.CreateAttributes();
            sl.Slider.Minimum = (decimal)Math.Min(min, def);
            sl.Slider.Maximum = (decimal)Math.Max(max, def);
            sl.Slider.DecimalPlaces = decimals;
            if (spec.Type == PType.Integer || decimals == 0)
                sl.Slider.Type = Grasshopper.GUI.Base.GH_SliderAccuracy.Integer;
            sl.SetSliderValue((decimal)def);
            return sl;
        }

        static GH_BooleanToggle MakeToggle(string nick, bool def)
        {
            var t = new GH_BooleanToggle { NickName = nick, Value = def };
            t.CreateAttributes();
            return t;
        }

        static GH_Panel MakePanel(string nick, string text, float w, float h)
        {
            // GH panels split fragments on CRLF - bare \n keeps everything as ONE item
            // and number inputs then fail with "conversion from text to number failed"
            var p = new GH_Panel { NickName = nick, UserText = Crlf(text) };
            p.CreateAttributes();
            p.Properties.Multiline = false;      // one item per line for list inputs
            p.Attributes.Bounds = new RectangleF(0, 0, w, h);
            return p;
        }

        static GH_ValueList MakeValueList(string nick, (string label, string val)[] items)
        {
            var vl = new GH_ValueList { NickName = nick };
            vl.ListItems.Clear();
            foreach (var (label, val) in items)
                vl.ListItems.Add(new GH_ValueListItem(label, val));
            vl.CreateAttributes();
            return vl;
        }

        // ---- updating an existing single source in place ----

        /// <summary>Carries out an <see cref="SettingsPlanAction.Update"/>. The plan has
        /// already decided that this source is one of the four objects a settings file
        /// stands for and that the value fits it; only the writing is left.</summary>
        static void SetSource(IGH_Param source, SettingsValue value, SettingsInputDecision decision)
        {
            switch (decision.ObjectKind)
            {
                case SettingsObjectKind.Slider when source is GH_NumberSlider sl:
                    {
                        decimal d = (decimal)value.AsNumber();
                        if (decision.SliderDecimals >= 0) sl.Slider.DecimalPlaces = decision.SliderDecimals;
                        if (d < sl.Slider.Minimum) sl.Slider.Minimum = d;
                        if (d > sl.Slider.Maximum) sl.Slider.Maximum = d;
                        sl.SetSliderValue(d);
                        sl.ExpireSolution(false);
                        break;
                    }
                case SettingsObjectKind.Toggle when source is GH_BooleanToggle tg:
                    tg.Value = value.AsBool(tg.Value);
                    tg.ExpireSolution(false);
                    break;
                case SettingsObjectKind.ValueList when source is GH_ValueList list:
                    list.SelectItem(decision.ValueListIndex);
                    list.ExpireSolution(false);
                    break;
                case SettingsObjectKind.Panel when source is GH_Panel panel:
                    panel.Properties.Multiline = !value.IsList;
                    panel.SetUserText(Crlf(PanelText(value)));
                    panel.ExpireSolution(false);   // like the other sources: the caller's expire recollects it
                    break;
            }
        }

        // ---- value formatting ----

        /// <summary>A settings value as panel text: one item per line, CRLF, so a list
        /// input reads it as one item per line.</summary>
        internal static string PanelText(SettingsValue value)
            => string.Join("\r\n", value.AllText());

        static string Crlf(string text)
            => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
