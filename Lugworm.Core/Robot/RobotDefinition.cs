using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    public enum JointKind { Fixed, Revolute, Prismatic }

    public sealed class RobotLink
    {
        public string Name = "";
        public string Layer = "";
        public string? Object;
        public double Length;
        public double AxialScale = 1.0;
    }

    public sealed class RobotJoint
    {
        public string Name = "";
        public JointKind Kind;
        public Vec3 Axis = new Vec3(0, 0, 1);
        public string Parent = "", Child = "";
        public Vec3 Pivot;
        public double Home, Min, Max;
    }

    public sealed class RobotDefinition
    {
        public string Name = "";
        public Vec3 Base;
        public double BaseYaw;
        public readonly List<RobotLink> Links = new List<RobotLink>();
        public readonly List<RobotJoint> Joints = new List<RobotJoint>();
        public double ReachMin, ReachMax;
        public Vec3? CalibratedHomePose;
        public int? HomedArmMode;
        public bool HomePosePresent, HomePoseCalibrated;
        /// <summary>Placement of the base link, the one link no joint drives: rotation
        /// (deg, CCW about the vertical J1 axis) then an XY offset (mm). Model data, so
        /// it lives beside the joints rather than in the renderer. Null when the
        /// definition predates &lt;BasePlacement&gt;; the caller then keeps its own
        /// fallback.</summary>
        public (double RotDeg, double OffsetX, double OffsetY)? BasePlacement;
        public readonly List<string> ParseWarnings = new List<string>();

        public RobotLink? Link(string name) => Links.Find(l => l.Name == name);
        public RobotJoint? Joint(string name) => Joints.Find(j => j.Name == name);

        public static RobotDefinition Parse(string xml)
        {
            var root = XDocument.Parse(xml).Root ?? throw new FormatException("robot xml: missing root element");
            var def = new RobotDefinition { Name = Str(root, "name") };
            double Num(XElement e, string a)
            {
                var attr = (string?)e.Attribute(a);
                if (attr == null) return 0;
                if (double.TryParse(attr, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
                def.ParseWarnings.Add($"<{e.Name.LocalName} name=\"{Str(e, "name")}\">: attribute {a}=\"{attr}\" is not a number - using 0");
                return 0;
            }
            Vec3 Vec(XElement e, string a, Vec3 fallback)
            {
                var s = (string?)e.Attribute(a);
                if (string.IsNullOrWhiteSpace(s)) return fallback;
                var p = s!.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3) { def.ParseWarnings.Add($"<{e.Name.LocalName}> attribute {a} needs 3 components"); return fallback; }
                double C(int i)
                {
                    if (double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
                    def.ParseWarnings.Add($"<{e.Name.LocalName}> attribute {a} component '{p[i]}' is not finite - using 0");
                    return 0;
                }
                return new Vec3(C(0), C(1), C(2));
            }
            var baseEl = root.Element("Base");
            if (baseEl != null) { def.Base = new Vec3(Num(baseEl, "x"), Num(baseEl, "y"), Num(baseEl, "z")); def.BaseYaw = Num(baseEl, "yaw"); }
            var reach = root.Element("Reach");
            if (reach != null) { def.ReachMin = Num(reach, "min"); def.ReachMax = Num(reach, "max"); }

            var home = root.Element("HomePose");
            if (home != null)
            {
                def.HomePosePresent = true;
                def.HomePoseCalibrated = string.Equals(Str(home, "calibrated"), "true", StringComparison.OrdinalIgnoreCase);
                if (def.HomePoseCalibrated)
                {
                    var x = home.Attribute("x"); var y = home.Attribute("y"); var z = home.Attribute("z");
                    if (x == null || y == null || z == null
                        || !double.TryParse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hx)
                        || !double.TryParse(y.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hy)
                        || !double.TryParse(z.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
                        || double.IsNaN(hx) || double.IsInfinity(hx)
                        || double.IsNaN(hy) || double.IsInfinity(hy)
                        || double.IsNaN(hz) || double.IsInfinity(hz))
                    { def.ParseWarnings.Add("<HomePose> is marked calibrated but does not contain finite x/y/z values"); def.HomePoseCalibrated = false; }
                    else def.CalibratedHomePose = new Vec3(hx, hy, hz);

                    if (def.HomePoseCalibrated)
                    {
                        var armMode = home.Attribute("armMode");
                        if (armMode != null)
                        {
                            if (int.TryParse(armMode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode)
                                && (mode == 0 || mode == 1))
                                def.HomedArmMode = mode;
                            else
                                def.ParseWarnings.Add($"<HomePose>: attribute armMode=\"{armMode.Value}\" must be 0 or 1 - ignoring");
                        }
                    }
                }
                else def.ParseWarnings.Add("<HomePose> is present but marked uncalibrated - machine-path validation will block until measured");
            }

            var basePlacement = root.Element("BasePlacement");
            if (basePlacement != null)
                def.BasePlacement = (Num(basePlacement, "rotDeg"),
                                     Num(basePlacement, "offsetX"),
                                     Num(basePlacement, "offsetY"));

            var links = root.Element("Links");
            if (links != null) foreach (var le in links.Elements("Link"))
                def.Links.Add(new RobotLink { Name = Str(le, "name"), Layer = Str(le, "layer"), Object = (string?)le.Attribute("object"), Length = Num(le, "length"), AxialScale = le.Attribute("axialScale") != null ? Num(le, "axialScale") : 1.0 });
            var joints = root.Element("Joints");
            if (joints != null) foreach (var je in joints.Elements("Joint"))
                def.Joints.Add(new RobotJoint { Name = Str(je, "name"), Kind = Kind(Str(je, "type")), Axis = Vec(je, "axis", new Vec3(0, 0, 1)), Parent = Str(je, "parent"), Child = Str(je, "child"), Pivot = Vec(je, "pivot", default), Home = Num(je, "home"), Min = Num(je, "min"), Max = Num(je, "max") });
            return def;
        }

        static JointKind Kind(string s) => s switch { "revolute" => JointKind.Revolute, "prismatic" => JointKind.Prismatic, _ => JointKind.Fixed };
        static string Str(XElement e, string a) => (string?)e.Attribute(a) ?? "";
    }
}
