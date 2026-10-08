using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Settings
{
    /// <summary>What one component input is currently fed by. Only the four canvas
    /// objects a settings file stands for may be written to; everything else upstream
    /// is a definition's data and is never touched.</summary>
    public enum SettingsSourceKind { None, Slider, Toggle, Panel, ValueList, Other }

    /// <summary>The declared kind of one input, narrowed to what a settings file can
    /// carry. Anything else (geometry, points, planes, colours) is <see cref="Other"/>
    /// and cannot be auto-fed.</summary>
    public enum SettingsInputType { Number, Integer, Boolean, Text, Other }

    /// <summary>What applying a settings file does to one input.</summary>
    public enum SettingsPlanAction
    {
        /// <summary>Nothing happens and nothing is reported - the input is already
        /// wired and the file says nothing about it, or it cannot be fed at all.</summary>
        None,
        /// <summary>The input has no source: a new object carrying the value is made.</summary>
        Create,
        /// <summary>The input is fed by exactly one slider / toggle / panel / value
        /// list, which is set to the file value in place.</summary>
        Update,
        /// <summary>The input is deliberately left alone and reported.</summary>
        Skip,
    }

    /// <summary>Which canvas object a decision creates, or which one it updates.</summary>
    public enum SettingsObjectKind { None, Slider, Toggle, Panel, ValueList }

    /// <summary>One entry of a value list: the label the user sees and the expression
    /// the list emits.</summary>
    public sealed class SettingsListItem
    {
        public SettingsListItem(string? label, string? value)
        { Label = label ?? ""; Value = value ?? ""; }

        public string Label { get; }
        public string Value { get; }
    }

    /// <summary>A plain description of one component input and of whatever feeds it -
    /// everything the plan needs and nothing Grasshopper-shaped. The plugin fills this
    /// in from <c>IGH_Param.SourceCount</c> / <c>Sources</c> and its own
    /// <c>ParamSpec</c>; Core decides what happens to it.</summary>
    public sealed class SettingsInputSource
    {
        public SettingsInputSource(string? name) { Name = name ?? ""; }

        /// <summary>The input's parameter name - the key a settings file uses.</summary>
        public string Name;

        /// <summary>How many objects are wired into the input. 0 is unwired.</summary>
        public int SourceCount;

        /// <summary>What the single source is, when there is exactly one.</summary>
        public SettingsSourceKind Kind = SettingsSourceKind.None;

        /// <summary>The upstream object's display name, used in the refusal reason.</summary>
        public string SourceLabel = "";

        /// <summary>Whether the running build still declares this input.</summary>
        public bool HasSpec;

        /// <summary>The declared value kind.</summary>
        public SettingsInputType Type = SettingsInputType.Other;

        /// <summary>List access - a list input takes a panel, never a slider.</summary>
        public bool IsList;

        /// <summary>The value-list items the SPEC declares for this input, when it
        /// declares any. Drives what a create makes.</summary>
        public IReadOnlyList<SettingsListItem>? SpecItems;

        /// <summary>The items of the value list already wired into this input. Drives
        /// whether an update can select the file value.</summary>
        public IReadOnlyList<SettingsListItem>? SourceItems;

        /// <summary>The wired slider is an integer slider - its precision is the
        /// operator's choice and is never widened.</summary>
        public bool SourceSliderIsInteger;

        /// <summary>The wired slider's current decimal places.</summary>
        public int SourceSliderDecimals;

        /// <summary>The apparent settings object is itself fed by upstream data.</summary>
        public bool SourceHasInputs;
    }

    /// <summary>What the plan decided for one input, and why.</summary>
    public sealed class SettingsInputDecision
    {
        internal SettingsInputDecision(string name, SettingsPlanAction action, SettingsObjectKind objectKind,
                                       int valueListIndex, int sliderDecimals, string? reason, string? note)
        {
            Name = name;
            Action = action;
            ObjectKind = objectKind;
            ValueListIndex = valueListIndex;
            SliderDecimals = sliderDecimals;
            Reason = reason;
            Note = note;
        }

        public string Name { get; }
        public SettingsPlanAction Action { get; }

        /// <summary>The object to create, or the object being updated.</summary>
        public SettingsObjectKind ObjectKind { get; }

        /// <summary>The value-list item the file value matched, or -1.</summary>
        public int ValueListIndex { get; }

        /// <summary>The decimal places an updated slider must carry, or -1 when the
        /// plan leaves its precision alone.</summary>
        public int SliderDecimals { get; }

        /// <summary>Why this input was skipped or left alone. Always set for
        /// <see cref="SettingsPlanAction.Skip"/>.</summary>
        public string? Reason { get; }

        /// <summary>A human-readable note about a value that could not be honoured
        /// exactly - reported to the operator alongside the counts.</summary>
        public string? Note { get; }

        public override string ToString()
            => Name + ": " + Action + (Reason != null ? " (" + Reason + ")" : "");
    }

    /// <summary>The classify-and-plan half of loading a settings file (4.4.0, D8),
    /// lifted out of the plugin so the one dangerous case - silently overwriting a
    /// user's upstream component - is decided by a pure function and pinned by tests.
    ///
    /// The rule has not changed: an input with nothing wired gets a new object, an
    /// input fed by exactly ONE slider / toggle / panel / value list is set in place,
    /// and an input fed by anything else - several sources, a component output, an
    /// expression, a cluster - is left alone and named in the report. Grasshopper
    /// object creation, the undo event and the runtime message stay in the plugin.</summary>
    public static class SettingsPlan
    {
        /// <summary>A <c>GH_NumberSlider</c> stores decimals; a file value outside that
        /// range cannot be put on a slider at all.</summary>
        public const double SliderLimit = 1e12;

        /// <summary>The most decimal places a file value may widen a slider to.</summary>
        public const int MaxSliderDecimals = 6;

        /// <summary>Decides create / update / skip for every input, in the order given.
        /// <paramref name="overrides"/> is a settings file's inputs; null (or a name it
        /// does not mention) means "use the built-in defaults", which is the plain
        /// "wire inputs" behaviour.</summary>
        public static IReadOnlyList<SettingsInputDecision> Plan(
            IEnumerable<SettingsInputSource>? inputs,
            IReadOnlyDictionary<string, SettingsValue>? overrides = null)
        {
            var decisions = new List<SettingsInputDecision>();
            if (inputs == null) return decisions;
            foreach (var input in inputs)
            {
                if (input == null) continue;
                SettingsValue? value = null;
                if (overrides != null && overrides.TryGetValue(input.Name, out var found)) value = found;
                decisions.Add(Decide(input, value));
            }
            return decisions;
        }

        /// <summary>The decision for one input. <paramref name="value"/> is what the
        /// settings file holds for it, or null when the file does not mention it.</summary>
        public static SettingsInputDecision Decide(SettingsInputSource input, SettingsValue? value)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            if (value != null && input.SourceCount <= 1 && !input.SourceHasInputs &&
                (input.SourceCount == 0 || IsSettingsSource(input.Kind)))
            {
                string? invalid = ValidateValue(input, value);
                if (invalid != null)
                    return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.None, reason: invalid,
                                    note: input.Name + ": " + invalid + " - left alone");
            }

            if (input.SourceCount > 0) return DecideWired(input, value);
            return DecideUnwired(input, value);
        }

        // ---- an input that already has something upstream ----

        static SettingsInputDecision DecideWired(SettingsInputSource input, SettingsValue? value)
        {
            // Without a file value the wiring pass never touches a wired input - that
            // is the unchanged pre-4.0.0 behaviour of the "inputs" button.
            if (value == null)
                return Decision(input, SettingsPlanAction.None, SettingsObjectKind.None,
                                reason: "already wired");

            // The dangerous case. More than one source, or one source that is not one
            // of the four objects a settings file stands for, is the definition's own
            // work: it is reported, never written over.
            if (input.SourceCount > 1)
                return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.None,
                                reason: "fed by " + input.SourceCount.ToString(CultureInfo.InvariantCulture)
                                        + " sources - applying the file would overwrite them");

            if (input.SourceHasInputs)
                return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.None,
                                reason: "source carries upstream data, not an editable setting");

            switch (input.Kind)
            {
                case SettingsSourceKind.Slider:
                    {
                        double v = value.AsNumber();
                        if (!SliderCanCarry(v))
                            return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.Slider,
                                            reason: "outside a slider's range",
                                            note: input.Name + ": " + value.AsText()
                                                  + " is outside a slider's range - the slider was left alone");
                        // Widen the precision so a 0.05 in the file is not silently rounded
                        // away, but never turn the operator's integer slider into a float one.
                        int decimals = -1;
                        if (!input.SourceSliderIsInteger && input.Type != SettingsInputType.Integer)
                            decimals = Math.Max(input.SourceSliderDecimals,
                                                Math.Min(MaxSliderDecimals, DecimalsOf(value.AsText())));
                        int effectiveDecimals = input.SourceSliderIsInteger || input.Type == SettingsInputType.Integer
                            ? 0 : decimals;
                        if (!FitsPrecision(v, effectiveDecimals))
                            return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.Slider,
                                            reason: "value would be rounded by the slider");
                        return Decision(input, SettingsPlanAction.Update, SettingsObjectKind.Slider,
                                        sliderDecimals: decimals);
                    }

                case SettingsSourceKind.Toggle:
                    return Decision(input, SettingsPlanAction.Update, SettingsObjectKind.Toggle);

                case SettingsSourceKind.ValueList:
                    {
                        int index = MatchItem(input.SourceItems, value);
                        if (index < 0)
                            return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.ValueList,
                                            reason: "not one of the value list's items",
                                            note: input.Name + ": '" + value.AsText()
                                                  + "' is not one of the value list's items - it was left alone");
                        return Decision(input, SettingsPlanAction.Update, SettingsObjectKind.ValueList,
                                        valueListIndex: index);
                    }

                case SettingsSourceKind.Panel:
                    return Decision(input, SettingsPlanAction.Update, SettingsObjectKind.Panel);

                default:
                    return Decision(input, SettingsPlanAction.Skip, SettingsObjectKind.None,
                                    reason: "fed by " + SourceWord(input)
                                            + " - applying the file would overwrite it");
            }
        }

        // ---- an input with nothing upstream ----

        static SettingsInputDecision DecideUnwired(SettingsInputSource input, SettingsValue? value)
        {
            if (!input.HasSpec)
                return Decision(input, SettingsPlanAction.None, SettingsObjectKind.None,
                                reason: "this build does not declare the input");

            if (input.IsList && input.Type != SettingsInputType.Other)
                return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel);

            if (input.SpecItems != null && input.SpecItems.Count > 0)
            {
                if (value == null)
                    return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.ValueList);
                int index = MatchItem(input.SpecItems, value);
                if (index >= 0)
                    return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.ValueList,
                                    valueListIndex: index);
                return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel,
                                note: input.Name + ": '" + value.AsText()
                                      + "' is not one of its list items - a panel carries it instead");
            }

            switch (input.Type)
            {
                case SettingsInputType.Number:
                case SettingsInputType.Integer:
                    if (input.IsList)
                        return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel);
                    if (value != null && !SliderCanCarry(value.AsNumber()))
                        return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel,
                                        note: input.Name + ": " + value.AsText()
                                              + " is outside a slider's range - a panel carries it instead");
                    if (value != null && !FitsPrecision(value.AsNumber(), MaxSliderDecimals))
                        return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel,
                                        note: input.Name + ": value needs more slider precision - a panel carries it instead");
                    return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Slider);

                case SettingsInputType.Boolean:
                    return Decision(input, SettingsPlanAction.Create,
                                    input.IsList ? SettingsObjectKind.Panel : SettingsObjectKind.Toggle);

                case SettingsInputType.Text:
                    return Decision(input, SettingsPlanAction.Create, SettingsObjectKind.Panel);

                default:
                    // geometry / curves / planes / colours cannot be auto-fed
                    return Decision(input, SettingsPlanAction.None, SettingsObjectKind.None,
                                    reason: "geometry inputs cannot be auto-fed");
            }
        }

        // ---- the capture side of the same question ----

        /// <summary>Why this input's SOURCE disqualifies it from being CAPTURED into a
        /// settings file, or null when the source allows capture. The source decides,
        /// not the value: an input wired from a component output carries the
        /// definition's data, not a setting - capturing it once wrote a whole G-code
        /// list into a 2.4 MB "settings" file. This is deliberately the same
        /// unwired / one-settings-object / anything-else rule the load side uses.</summary>
        public static string? CaptureRefusal(SettingsInputSource input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.SourceHasInputs) return "source carries upstream data, not a setting";
            if (input.SourceCount > 1) return "several sources";
            if (input.SourceCount == 1 && !IsSettingsSource(input.Kind))
                return "wired from " + SourceWord(input) + " - data, not a setting";
            return null;
        }

        /// <summary>The four canvas objects a settings file stands for - the same four
        /// the loader is allowed to update in place.</summary>
        public static bool IsSettingsSource(SettingsSourceKind kind)
            => kind == SettingsSourceKind.Slider || kind == SettingsSourceKind.Toggle
               || kind == SettingsSourceKind.Panel || kind == SettingsSourceKind.ValueList;

        // ---- value feasibility and matching ----

        /// <summary>Every recipient must participate in this apply and request the
        /// same value. A null request represents a recipient that is left alone.</summary>
        public static bool SharedSourceAgrees(SettingsValue value, IEnumerable<SettingsValue?> recipientValues)
        {
            foreach (var requested in recipientValues)
                if (requested == null || !value.Equals(requested)) return false;
            return true;
        }

        static string? ValidateValue(SettingsInputSource input, SettingsValue value)
        {
            if (value.IsList && !input.IsList) return "a list cannot replace a scalar setting";
            if (value.IsList && input.SourceCount == 1 &&
                (input.Kind == SettingsSourceKind.Slider || input.Kind == SettingsSourceKind.Toggle ||
                 input.Kind == SettingsSourceKind.ValueList))
                return "this source cannot carry a list";

            for (int i = 0; i < value.Count; i++)
            {
                if (value.IsList && value.Kind == SettingsKind.Text &&
                    value.TextAt(i).IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    return "a panel cannot preserve newlines inside individual list items";
                if (input.Type == SettingsInputType.Number || input.Type == SettingsInputType.Integer ||
                    input.Kind == SettingsSourceKind.Slider)
                {
                    double number;
                    if (value.Kind == SettingsKind.Bool) number = value.BoolValues[i] ? 1 : 0;
                    else if (!double.TryParse(value.TextAt(i), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                        return "value is not a number";
                    if (double.IsNaN(number) || double.IsInfinity(number)) return "value must be finite";
                    if (input.Type == SettingsInputType.Integer &&
                        (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue))
                        return "value is not a supported integer";
                }
                if ((input.Type == SettingsInputType.Boolean || input.Kind == SettingsSourceKind.Toggle) &&
                    value.Kind == SettingsKind.Text && !bool.TryParse(value.TextAt(i), out _))
                    return "value is not a boolean";
            }
            return null;
        }

        static bool FitsPrecision(double value, int decimals)
            => Math.Round(value, Math.Max(0, Math.Min(15, decimals))).Equals(value);

        /// <summary>Whether a slider can hold this number at all.</summary>
        public static bool SliderCanCarry(double v)
            => !double.IsNaN(v) && !double.IsInfinity(v) && Math.Abs(v) <= SliderLimit;

        /// <summary>Index of the value-list item whose VALUE equals the settings value,
        /// or -1. Numbers are compared numerically (so "0" matches 0.0), text after
        /// stripping the quotes value lists store string expressions in.</summary>
        public static int MatchItem(IReadOnlyList<SettingsListItem>? items, SettingsValue? value)
        {
            if (items == null || value == null) return -1;
            string wanted = value.AsText();
            for (int i = 0; i < items.Count; i++)
            {
                string raw = (items[i]?.Value ?? "").Trim();
                if (string.Equals(raw, wanted, StringComparison.Ordinal)) return i;
                if (string.Equals(Unquote(raw), wanted, StringComparison.Ordinal)) return i;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
                    && double.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                    && a.Equals(b)) return i;
            }
            return -1;
        }

        /// <summary>Decimal places carried by an invariant-culture number text.</summary>
        public static int DecimalsOf(string? invariantText)
        {
            string text = invariantText ?? "";
            int e = text.IndexOfAny(new[] { 'E', 'e' });
            int exponent = 0;
            if (e >= 0)
            {
                int.TryParse(text.Substring(e + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out exponent);
                text = text.Substring(0, e);
            }
            int dot = text.IndexOf('.');
            int fraction = dot < 0 ? 0 : text.Length - dot - 1;
            return (int)Math.Max(0L, Math.Min(100L, (long)fraction - exponent));
        }

        static string Unquote(string s)
            => s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"'
               ? s.Substring(1, s.Length - 2) : s;

        static string SourceWord(SettingsInputSource input)
        {
            string name = input.SourceLabel ?? "";
            return "'" + (name.Length == 0 ? "a component" : name) + "'";
        }

        static SettingsInputDecision Decision(SettingsInputSource input, SettingsPlanAction action,
                                              SettingsObjectKind objectKind, int valueListIndex = -1,
                                              int sliderDecimals = -1, string? reason = null,
                                              string? note = null)
            => new SettingsInputDecision(input.Name, action, objectKind, valueListIndex,
                                         sliderDecimals, reason, note);
    }
}
