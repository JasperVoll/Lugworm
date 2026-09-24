using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Lugworm.Components;

namespace Lugworm.Ui
{
    /// <summary>Turns the plate settings a pre-4.0.0-contract document kept on its
    /// consumers - Live View's own <c>bedWidth</c>/<c>bedDepth</c>/<c>bedCenter</c>
    /// sockets - into the one thing that configures a plate now: a Print Bed
    /// component wired to the consumer's <c>bed</c> input.
    ///
    /// Two rules it does not break:
    ///
    /// - **Lossless or nothing.** A socket that was wired hands its sources to the new
    ///   Print Bed, so the same upstream geometry still drives the plate. A socket that
    ///   held a literal hands over the literal. Anything that cannot be carried across
    ///   is reported on the component instead of being guessed at.
    /// - **Never redefine an existing bed.** If the consumer's <c>bed</c> input is
    ///   already wired, the legacy settings are reported and dropped rather than
    ///   overwriting the plate the document already has.</summary>
    static class LegacyBedMigration
    {
        public const string Width = "bedWidth";
        public const string Depth = "bedDepth";
        public const string Center = "bedCenter";

        /// <summary>The retired sockets, as their old specs. Live View used exactly
        /// these three, with the same meaning and units.</summary>
        public static IReadOnlyList<ParamSpec> RetiredBedSpecs { get; } = new[]
        {
            new ParamSpec(Width, "Bw", "Bed plate width along RHINO X, mm", "core", PType.Number),
            new ParamSpec(Depth, "Bd", "Bed plate depth along RHINO Y, mm", "core", PType.Number),
            new ParamSpec(Center, "Bc", "Bed plate center point in RHINO coordinates", "core", PType.Point),
        };

        // One inserted Print Bed per document per distinct legacy plate, so a document
        // with two consumers migrating the same plate ends up with one bed rather than
        // two that can drift apart.
        static readonly ConditionalWeakTable<GH_Document, Dictionary<string, PrintBedComponent>> Inserted = new();

        /// <summary>Wires <paramref name="owner"/>'s <c>bed</c> input to a Print Bed
        /// carrying the captured legacy settings. Reports on the component and never
        /// throws into the load.</summary>
        public static void Apply(IamComponentBase owner, GH_Document? doc,
                                 IReadOnlyList<RetiredSocket> sockets)
        {
            if (owner == null || sockets == null || sockets.Count == 0) return;
            int bedIndex = owner.Params.IndexOfInputParam("bed");
            if (bedIndex < 0)
            {
                Report(owner, sockets, "this component has no 'bed' input to migrate them to");
                return;
            }
            var bedInput = owner.Params.Input[bedIndex];
            if (bedInput.SourceCount > 0)
            {
                Report(owner, sockets, "'bed' is already wired to a Print Bed, which stays authoritative");
                return;
            }
            if (doc == null)
            {
                Report(owner, sockets, "the component is not in a document yet");
                return;
            }

            try
            {
                var table = Inserted.GetValue(doc, _ => new Dictionary<string, PrintBedComponent>(StringComparer.Ordinal));
                string key = Key(sockets);
                if (!table.TryGetValue(key, out var bed) || bed.OnPingDocument() != doc)
                {
                    bed = Create(owner, doc, sockets);
                    if (bed == null) { Report(owner, sockets, "the Print Bed component could not be created"); return; }
                    table[key] = bed;
                }
                bedInput.AddSource(bed.Params.Output[0]);
                owner.AddArchiveNotice(GH_RuntimeMessageLevel.Remark,
                    "Print bed migrated: this component's former " + Names(sockets)
                    + " now live in the Print Bed wired to 'bed'. Check its size and center. A "
                    + "document that relied on a Cartesian robot's 800x800 default plate gets the lab "
                    + "plate's defaults instead.");
            }
            catch (Exception ex)
            {
                Report(owner, sockets, "the migration failed: " + ex.Message);
            }
        }

        static PrintBedComponent? Create(IamComponentBase owner, GH_Document doc,
                                         IReadOnlyList<RetiredSocket> sockets)
        {
            var bed = new PrintBedComponent();
            bed.CreateAttributes();
            var pivot = owner.Attributes?.Pivot ?? PointF.Empty;
            bed.Attributes.Pivot = new PointF(pivot.X - 340, pivot.Y + 140);
            if (!doc.AddObject(bed, false)) return null;

            foreach (var socket in sockets)
            {
                string target = socket.Name == Width ? "width"
                              : socket.Name == Depth ? "depth"
                              : socket.Name == Center ? "center" : "";
                if (target.Length == 0) continue;
                int index = bed.Params.IndexOfInputParam(target);
                if (index < 0) continue;
                var param = bed.Params.Input[index];
                if (socket.Sources.Count > 0)
                {
                    // lossless: the same upstream objects now drive the plate
                    foreach (var source in socket.Sources) param.AddSource(source);
                }
                else if (socket.Value is double number && param is Param_Number numberParam)
                    numberParam.PersistentData.Append(new GH_Number(number));
                else if (socket.Value is Point3d point && param is Param_Point pointParam)
                    pointParam.PersistentData.Append(new GH_Point(point));
            }
            bed.Params.OnParametersChanged();
            bed.ExpireSolution(false);
            // best effort: making the inserted Print Bed one undo step. The component is
            // already in the document and wired, and the migration itself is announced as
            // an archive remark - a missing undo record must not undo the migration.
            try { doc.UndoUtil.RecordAddObjectEvent("Lugworm: print bed migration", bed); } catch (Exception) { }
            return bed;
        }

        /// <summary>Identity of one captured plate, so two consumers that carried the
        /// same settings share one inserted Print Bed and two that did not, do not.</summary>
        static string Key(IReadOnlyList<RetiredSocket> sockets)
            => string.Join("|", sockets.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s =>
                s.Name + "=" + (s.Sources.Count > 0
                    ? string.Join(",", s.Sources.Select(src => src.InstanceGuid.ToString()))
                    : System.Convert.ToString(s.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "")));

        static string Names(IReadOnlyList<RetiredSocket> sockets)
            => string.Join(", ", sockets.Select(s => s.Name));

        static void Report(IamComponentBase owner, IReadOnlyList<RetiredSocket> sockets, string why)
            => owner.AddArchiveNotice(GH_RuntimeMessageLevel.Warning,
                "Print bed not migrated: " + why + ". The removed " + Names(sockets)
                + " input(s) of this saved document were dropped. Set the plate on an Lugworm "
                + "Print Bed component and wire it to 'bed'.");
    }
}
