using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Lugworm.Core.Geometry;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Robot
{
    /// <summary>The kinematic family selected by a robot definition.</summary>
    public enum RobotMechanism
    {
        None,
        Cartesian,
        Scara,
    }

    /// <summary>The program family a profile is intended to consume.</summary>
    public enum RobotProgramKind
    {
        RepRapGcode,
    }

    /// <summary>Optional direct-control transport associated with a profile.</summary>
    public enum RobotControlKind
    {
        None,
        Duet,
    }

    /// <summary>
    /// Immutable, Rhino-independent robot selection passed between Grasshopper
    /// components as <see cref="RobotDefinitionText"/>. It deliberately contains
    /// selection and machine defaults, not preview meshes or network sessions.
    /// </summary>
    public sealed class RobotProfile
    {
        readonly Func<RobotLimits>? _scaraLimits;

        internal RobotProfile(string id, string displayName, RobotMechanism mechanism,
                              RobotProgramKind programKind, RobotControlKind controlKind,
                              string modelKey, string visualizationKey,
                              GcodeCoordinateFrame gcodeCoordinateFrame,
                              Func<RobotLimits>? scaraLimits = null,
                              GcodeFlavor defaultFlavor = GcodeFlavor.DuetRrf)
        {
            Id = id;
            DisplayName = displayName;
            Mechanism = mechanism;
            ProgramKind = programKind;
            ControlKind = controlKind;
            ModelKey = modelKey;
            VisualizationKey = visualizationKey;
            GcodeCoordinateFrame = gcodeCoordinateFrame;
            _scaraLimits = scaraLimits;
            DefaultFlavor = defaultFlavor;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public RobotMechanism Mechanism { get; }
        public RobotProgramKind ProgramKind { get; }
        public RobotControlKind ControlKind { get; }
        public string ModelKey { get; }
        public string VisualizationKey { get; }
        public GcodeCoordinateFrame GcodeCoordinateFrame { get; }
        /// <summary>The G-code dialect the generator uses when its firmware input is
        /// unwired. Every pre-4.0.0 profile keeps Duet RRF, which was the unwired
        /// default before profiles carried one, so existing output is unchanged.</summary>
        public GcodeFlavor DefaultFlavor { get; }

        public bool HasKinematics => Mechanism == RobotMechanism.Scara;

        public RobotLimits CreateScaraLimits()
        {
            if (_scaraLimits == null)
                throw new InvalidOperationException($"robot profile '{Id}' does not define SCARA kinematics");
            return _scaraLimits();
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>Built-in robot and machine profiles. Adding a preset is one catalog
    /// entry plus, when needed, a solver/visualization implementation.</summary>
    public static class RobotCatalog
    {
        static RobotLimits PotterbotLimits() => new RobotLimits
        {
            L1 = 549.06,
            L2 = 457.23,
            BaseH = 0.0,
            ReachMin = 235.0,
            ReachMax = 1006.29,
            ZMax = 400.0,
            XMin = -300.0,
            XMax = 10000.0,
            YMin = -10000.0,
            YMax = 0.0,
            CheckEnvelope = true,
            J1Min = -41.1,
            J1Max = 319.1,
            J3Min = -151.2,
            J3Max = 151.2,
            J2Min = 0.0,
            J2Max = 400.0,
            HomedArmMode = 1,
            CalibratedHomePose = new Vec3(200.0, -500.0, 400.0),
        };

        static readonly IReadOnlyList<RobotProfile> Profiles = new ReadOnlyCollection<RobotProfile>(
            new List<RobotProfile>
            {
                new RobotProfile("none", "None / Toolpath only", RobotMechanism.None,
                    RobotProgramKind.RepRapGcode, RobotControlKind.None, "", "",
                    GcodeCoordinateFrame.Identity),
                new RobotProfile("generic-cartesian", "Generic Cartesian machine", RobotMechanism.Cartesian,
                    RobotProgramKind.RepRapGcode, RobotControlKind.None, "cartesian", "",
                    GcodeCoordinateFrame.Identity),
                new RobotProfile("generic-duet-cartesian", "Generic Duet Cartesian machine", RobotMechanism.Cartesian,
                    RobotProgramKind.RepRapGcode, RobotControlKind.Duet, "cartesian", "",
                    GcodeCoordinateFrame.Identity),
                new RobotProfile("potterbot-scara-v4", "Potterbot SCARA v4", RobotMechanism.Scara,
                    RobotProgramKind.RepRapGcode, RobotControlKind.Duet,
                    "Potterbot-Scara-v4", "potterbot-scara-v4", GcodeCoordinateFrame.LegacyScara,
                    PotterbotLimits),
                new RobotProfile("generic-klipper", "Generic Klipper machine", RobotMechanism.Cartesian,
                    RobotProgramKind.RepRapGcode, RobotControlKind.None, "cartesian", "",
                    GcodeCoordinateFrame.Identity, defaultFlavor: GcodeFlavor.Klipper),
                new RobotProfile("generic-marlin", "Generic Marlin machine", RobotMechanism.Cartesian,
                    RobotProgramKind.RepRapGcode, RobotControlKind.None, "cartesian", "",
                    GcodeCoordinateFrame.Identity, defaultFlavor: GcodeFlavor.Marlin),
            });

        static readonly Dictionary<string, RobotProfile> ById = BuildIndex();

        public static IReadOnlyList<RobotProfile> All => Profiles;

        public static RobotProfile Get(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (ById.TryGetValue(id.Trim(), out var profile)) return profile;
            throw new ArgumentException($"unknown robot profile '{Preview(id)}'", nameof(id));
        }

        /// <summary>The offending text as a message can carry it: one line, at most 48
        /// characters. The robot socket is the first Text input, so a whole G-code
        /// dropped on it must not become a multi-kilobyte error balloon.</summary>
        internal static string Preview(string value)
        {
            string s = (value ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 48 ? s : s.Substring(0, 48) + "…";
        }

        /// <summary>Resolves stable definition text, canonical IDs, and concise UI aliases.</summary>
        public static RobotProfile Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Get("none");
            string text = value!.Trim();
            if (text.StartsWith(RobotDefinitionText.Prefix, StringComparison.OrdinalIgnoreCase))
                return RobotDefinitionText.Parse(text);
            if (ById.TryGetValue(text, out var exact)) return exact;

            string key = Normalize(text);
            return key switch
            {
                "none" or "toolpathonly" or "norobot" => Get("none"),
                "cartesian" or "generic" or "genericcartesian" => Get("generic-cartesian"),
                "duet" or "duetcartesian" or "genericduet" or "genericduetcartesian" => Get("generic-duet-cartesian"),
                "scara" or "potterbot" or "potterbotscara" or "potterbotscarav4" => Get("potterbot-scara-v4"),
                "klipper" or "genericklipper" => Get("generic-klipper"),
                "marlin" or "genericmarlin" => Get("generic-marlin"),
                _ => throw new ArgumentException($"unknown robot definition '{Preview(text)}' - wire a Robot Definition or a profile id (none, potterbot, cartesian, klipper)", nameof(value)),
            };
        }

        static Dictionary<string, RobotProfile> BuildIndex()
        {
            var index = new Dictionary<string, RobotProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in Profiles) index.Add(profile.Id, profile);
            return index;
        }

        static string Normalize(string value)
        {
            var chars = new char[value.Length];
            int count = 0;
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) chars[count++] = char.ToLowerInvariant(c);
            return new string(chars, 0, count);
        }
    }

    /// <summary>Stable string wire format for Robot Definition component output.</summary>
    public static class RobotDefinitionText
    {
        public const string Prefix = "iamlab-robot:";
        const string Version = "v1";

        public static string Serialize(RobotProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return Prefix + Version + ":" + profile.Id;
        }

        public static RobotProfile Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("robot definition is empty");
            string[] parts = text.Trim().Split(':');
            if (parts.Length != 3 || !string.Equals(parts[0], "iamlab-robot", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("robot definition must use 'iamlab-robot:v1:<profile>'");
            if (!string.Equals(parts[1], Version, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"unsupported robot definition version '{parts[1]}'");
            try { return RobotCatalog.Get(parts[2]); }
            catch (ArgumentException ex) { throw new FormatException(ex.Message, ex); }
        }
    }
}
