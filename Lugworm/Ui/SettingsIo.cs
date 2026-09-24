using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Lugworm.Core.Geometry;
using Lugworm.Core.Settings;

namespace Lugworm.Ui
{
    /// <summary>Per-component settings files (4.0.0, D4): capture the inputs a
    /// component currently solves with, write them as one versioned JSON file, and
    /// put them back on the canvas as real Grasshopper objects.
    ///
    /// Deliberately narrow: only the values a slider, toggle, panel or value list can
    /// hold are captured (numbers, integers, booleans, text and lists of those), and
    /// only from an input that is fed by one of those four objects or by nothing. A
    /// geometry, point, curve, plane or colour input, and any input wired from a
    /// component output (the generator's G-code lines, a contour list, a Range), is
    /// named in the report and left to the definition - a settings file describes
    /// process parameters, never the part being printed or the data flowing through.</summary>
    public static class SettingsIo
    {
        // ---- capture ----

        /// <summary>A setting is what one canvas object holds, so a captured list is
        /// short and a captured text is a few lines; anything beyond this is data.</summary>
        internal const int MaxListItems = 64;
        internal const int MaxTextChars = 4096;

        /// <summary>The component's current inputs as a settings object, plus the
        /// inputs that were not captured, each with its reason.</summary>
        public static (ComponentSettings settings, IReadOnlyList<string> skipped) Capture(IamComponentBase comp)
        {
            var inputs = new Dictionary<string, SettingsValue>(StringComparer.Ordinal);
            var skipped = new List<string>();
            var specs = comp.SpecsForUi;

            foreach (var prm in comp.Params.Input)
            {
                var spec = specs.FirstOrDefault(s => s.Name == prm.Name);
                if (spec == null) continue;
                if (!IsCapturable(spec.Type)) { skipped.Add(prm.Name + " (" + spec.Type.ToString().ToLowerInvariant() + ")"); continue; }

                // The source decides, not the value: an input wired from a component
                // output carries the definition's data, not a setting. Capturing it
                // once wrote Interlayer Time's whole G-code list into a 2.4 MB
                // "settings" file that the folder loader then refused. Deliberately
                // the same Core rule the load side plans with - an input wired to
                // anything but one slider / toggle / panel / value list is never
                // captured and never overwritten.
                string? refusal = SettingsPlan.CaptureRefusal(InputWiringHelper.DescribeSources(prm));
                if (refusal != null) { skipped.Add(prm.Name + " (" + refusal + ")"); continue; }

                var data = prm.VolatileData;
                if (data == null || data.IsEmpty) continue;      // never solved / nothing wired
                var goos = data.AllData(true).Where(g => g != null).ToList();
                if (goos.Count == 0) continue;
                if (goos.Count > MaxListItems)
                { skipped.Add(prm.Name + " (" + goos.Count.ToString(CultureInfo.InvariantCulture) + " items, data rather than a setting)"); continue; }
                if (spec.Type == PType.Text && TextChars(goos) > MaxTextChars)
                { skipped.Add(prm.Name + " (text longer than " + MaxTextChars.ToString(CultureInfo.InvariantCulture) + " characters, data rather than a setting)"); continue; }

                bool list = spec.Access != GH_ParamAccess.item;
                var value = list ? ListValue(spec.Type, goos) : ItemValue(spec.Type, goos[0]);
                if (value != null) inputs[prm.Name] = value;
                else skipped.Add(prm.Name + " (value not convertible)");
            }

            var settings = new ComponentSettings(
                comp.SettingsKey,
                comp.Name,
                InstalledVersion(),
                comp.ActiveGroupsForUi.ToArray(),
                inputs);
            return (settings, skipped);
        }

        /// <summary>The plugin version a settings file records: the informational
        /// version the build stamps from the single csproj &lt;Version&gt; source,
        /// without build metadata, else the assembly version.</summary>
        internal static string InstalledVersion()
        {
            var asm = typeof(SettingsIo).Assembly;
            string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = info.IndexOf('+');
            if (plus >= 0) info = info.Substring(0, plus);
            info = info.Trim();
            if (info.Length > 0) return info;
            return asm.GetName().Version?.ToString(3) ?? "unknown";
        }

        static bool IsCapturable(PType type)
            => type == PType.Number || type == PType.Integer || type == PType.Boolean || type == PType.Text;

        static int TextChars(List<IGH_Goo> goos)
        {
            int n = 0;
            foreach (var g in goos)
                if (GH_Convert.ToString(g, out string? s, GH_Conversion.Both) && s != null) n += s.Length;
            return n;
        }

        static SettingsValue? ItemValue(PType type, IGH_Goo goo)
        {
            switch (type)
            {
                case PType.Number:
                    return GH_Convert.ToDouble(goo, out double d, GH_Conversion.Both) && Num.Finite(d)
                        ? SettingsValue.Number(d) : null;
                case PType.Integer:
                    return GH_Convert.ToInt32(goo, out int i, GH_Conversion.Both)
                        ? SettingsValue.Integer(i) : null;
                case PType.Boolean:
                    return GH_Convert.ToBoolean(goo, out bool b, GH_Conversion.Both)
                        ? SettingsValue.Bool(b) : null;
                default:
                    return GH_Convert.ToString(goo, out string? s, GH_Conversion.Both)
                        ? SettingsValue.Text(s ?? "") : null;
            }
        }

        static SettingsValue? ListValue(PType type, List<IGH_Goo> goos)
        {
            switch (type)
            {
                case PType.Number:
                    {
                        var v = new List<double>(goos.Count);
                        foreach (var g in goos)
                        {
                            if (!GH_Convert.ToDouble(g, out double d, GH_Conversion.Both) || !Num.Finite(d)) return null;
                            v.Add(d);
                        }
                        return SettingsValue.Numbers(v);
                    }
                case PType.Integer:
                    {
                        var v = new List<long>(goos.Count);
                        foreach (var g in goos)
                        {
                            if (!GH_Convert.ToInt32(g, out int i, GH_Conversion.Both)) return null;
                            v.Add(i);
                        }
                        return SettingsValue.Integers(v);
                    }
                case PType.Boolean:
                    {
                        var v = new List<bool>(goos.Count);
                        foreach (var g in goos)
                        {
                            if (!GH_Convert.ToBoolean(g, out bool b, GH_Conversion.Both)) return null;
                            v.Add(b);
                        }
                        return SettingsValue.Bools(v);
                    }
                default:
                    {
                        var v = new List<string>(goos.Count);
                        foreach (var g in goos)
                        {
                            if (!GH_Convert.ToString(g, out string? s, GH_Conversion.Both)) return null;
                            v.Add(s ?? "");
                        }
                        return SettingsValue.Texts(v);
                    }
            }
        }

        // ---- save / load from the component's context menu ----

        public static void Save(IamComponentBase comp)
        {
            var (settings, skipped) = Capture(comp);
            string path;
            using (var dialog = new SaveFileDialog
            {
                Title = "Save Lugworm component settings",
                Filter = "Lugworm settings (*.lwset.json)|*.lwset.json",
                FileName = comp.SettingsKey + SettingsFolder.PreferredExtension,
                AddExtension = true,
                OverwritePrompt = true,
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            try
            {
                // UTF-8 without a BOM: the file is meant to be readable and diffable
                File.WriteAllText(path, settings.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not save settings: " + ex.Message);
                return;
            }

            string message = "saved " + Count(settings.Inputs.Count, "input") + " and "
                             + Count(settings.Groups.Count, "group") + " to " + Path.GetFileName(path);
            if (skipped.Count > 0) message += ". Not captured: " + string.Join(", ", skipped);
            comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, message);
            Grasshopper.Instances.RedrawCanvas();
        }

        public static void Load(IamComponentBase comp)
        {
            string path;
            using (var dialog = new OpenFileDialog
            {
                Title = "Load Lugworm component settings",
                Filter = "Lugworm settings (*.lwset.json, *.iamset.json)|*.lwset.json;*.iamset.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                path = dialog.FileName;
            }

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not read settings: " + ex.Message);
                return;
            }

            if (!ComponentSettings.TryParse(text, out var settings, out string? error) || settings == null)
            {
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    Path.GetFileName(path) + " is not a usable settings file: " + error);
                return;
            }
            if (!string.Equals(settings.Component, comp.SettingsKey, StringComparison.OrdinalIgnoreCase))
            {
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "That file is for '" + settings.Component + "', this component is '" + comp.SettingsKey + "'");
                return;
            }

            Apply(comp, settings, Path.GetFileName(path));
        }

        // ---- applying ----

        /// <summary>Puts one settings file on the canvas: active groups first (so the
        /// inputs the file talks about exist), then the values, all under ONE undo
        /// event and one recompute. Reports what happened as a Remark.</summary>
        public static WireResult Apply(IamComponentBase comp, ComponentSettings settings, string? source = null)
        {
            comp.RecordUndoEvent("Lugworm: load settings");
            var result = ApplyCore(comp, settings, out var unknownGroups);
            Report(comp, settings, result, unknownGroups, source);
            comp.ExpireSolution(true);
            return result;
        }

        /// <summary>The shared part of "apply a settings file to this component": switch
        /// its groups on, then wire/update its inputs. Records no undo event and starts
        /// no solution - the caller owns both, so applying one file or a whole folder is
        /// a single undoable step.</summary>
        internal static WireResult ApplyCore(IamComponentBase comp, ComponentSettings settings,
                                             out IReadOnlyList<string> unknownGroups)
        {
            var unknown = new List<string>();
            foreach (string group in settings.Groups)
                if (!comp.ActivateGroupForUi(group)) unknown.Add(group);
            unknownGroups = unknown;
            return InputWiringHelper.WireAll(comp, settings.Inputs, recompute: false);
        }

        /// <summary>The "⇤ inputs" button and the "Wire inputs" menu item: use the
        /// document's registered settings folder when it has a file for this component,
        /// otherwise the built-in ParamSpec defaults exactly as before.</summary>
        public static WireResult WireInputs(IamComponentBase comp)
        {
            Guid documentId = comp.OnPingDocument()?.DocumentID ?? Guid.Empty;
            var settings = SettingsRegistry.SettingsFor(documentId, comp.SettingsKey);
            if (settings == null) return InputWiringHelper.WireAll(comp);

            string? file = SettingsRegistry.IndexFor(documentId)?.PathFor(comp.SettingsKey);
            return Apply(comp, settings, file != null ? Path.GetFileName(file) : null);
        }

        static void Report(IamComponentBase comp, ComponentSettings settings, WireResult result,
                           IReadOnlyList<string> unknownGroups, string? source)
        {
            if (unknownGroups.Count > 0)
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Unknown feature group(s) in the settings file: " + string.Join(", ", unknownGroups));

            var parts = new List<string>
            {
                result.Created.Count.ToString(CultureInfo.InvariantCulture) + " created",
                result.Updated.Count.ToString(CultureInfo.InvariantCulture) + " updated",
                result.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped",
            };
            string message = (source != null ? source + ": " : "settings applied: ") + string.Join(", ", parts);
            if (result.Skipped.Count > 0)
                message += " (left alone: " + string.Join(", ", result.Skipped) + ")";
            int missing = settings.Inputs.Keys.Count(k => comp.Params.IndexOfInputParam(k) < 0);
            if (missing > 0)
                message += ". " + Count(missing, "value") + " in the file has no matching input here";
            comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, message);
            foreach (string note in result.Notes)
                comp.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, note);
        }

        internal static string Count(int n, string noun)
            => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
    }
}
