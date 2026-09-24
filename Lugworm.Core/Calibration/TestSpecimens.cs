using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Calibration
{
    /// <summary>The standard mechanical test bodies the Test Geometry component can
    /// build. Append-only: the component persists its selection as an integer.</summary>
    public enum StandardSpecimen
    {
        /// <summary>DIN EN 196-1 cement-mortar prism, three-point bending.</summary>
        En196Prism,
        /// <summary>DIN EN ISO 178 plastics bar, three-point bending.</summary>
        Iso178Bar,
        /// <summary>ASTM D790 plastics bar, three-point bending.</summary>
        AstmD790Bar,
        /// <summary>DIN EN 12390 concrete cube, compression.</summary>
        En12390Cube,
        /// <summary>DIN EN 12390 concrete cylinder, compression.</summary>
        En12390Cylinder,
        /// <summary>ASTM D695 rigid-plastics prism, compression.</summary>
        AstmD695Prism,
    }

    /// <summary>One standard test specimen at its nominal dimensions, in millimetres.
    /// <see cref="Span"/> is the three-point bending support span and is 0 for a
    /// compression body: it is reported because it is the number the test rig is set to
    /// and it cannot be derived from the specimen alone. <see cref="Round"/> marks a
    /// cylinder, where <see cref="X"/> is the diameter and <see cref="Y"/> is unused.</summary>
    public readonly struct TestSpecimen
    {
        public string Standard { get; }
        public string Test { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double Span { get; }
        public bool Round { get; }

        public TestSpecimen(string standard, string test, double x, double y, double z,
                            double span = 0.0, bool round = false)
        {
            Standard = standard; Test = test;
            X = x; Y = y; Z = z; Span = span; Round = round;
        }

        /// <summary>The specimen scaled uniformly. Scaling is why this is a value type
        /// the caller can transform: a specimen at 1x is often unprintable (an ISO 178
        /// bar is 4 mm thick), and the scaled body must still describe itself.</summary>
        public TestSpecimen Scaled(double scale)
        {
            if (!(scale > 0) || double.IsNaN(scale) || double.IsInfinity(scale))
                throw new ArgumentOutOfRangeException(nameof(scale), $"scale must be finite and > 0 (got {scale})");
            return new TestSpecimen(Standard, Test, X * scale, Y * scale, Z * scale, Span * scale, Round);
        }

        /// <summary>The smallest printed dimension - the one that decides whether the
        /// body can be built from layers at all.</summary>
        public double Thinnest => Round ? Math.Min(X, Z) : Math.Min(Math.Min(X, Y), Z);

        public string Describe()
        {
            string body = Round
                ? FormattableString.Invariant($"cylinder Ø{X:0.##} × {Z:0.##} mm")
                : FormattableString.Invariant($"{X:0.##} × {Y:0.##} × {Z:0.##} mm");
            return Span > 0 ? FormattableString.Invariant($"{Standard} {Test}: {body}  span={Span:0.##} mm")
                            : $"{Standard} {Test}: {body}";
        }
    }

    /// <summary>Nominal dimensions of the standard flexural and compressive specimens,
    /// so a printed batch can be tested against a published method instead of an ad-hoc
    /// block.
    ///
    /// <para><b>Sources.</b> <b>EN 196-1</b> - cement-mortar prism 40x40x160 mm, broken in
    /// three-point bending over a 100 mm support span; the two halves are then crushed
    /// between 40x40 mm platens. This is the reference method for cementitious binders and
    /// the closest fit to LDM printing. <b>EN ISO 178</b> - plastics bar 80x10x4 mm at the
    /// standard 16:1 span-to-depth ratio, so a 64 mm span. <b>ASTM D790</b> - plastics bar
    /// 127x12.7x3.2 mm, the same 16:1 ratio, so 51.2 mm. <b>EN 12390</b> - concrete cube
    /// 150 mm and cylinder 150x300 mm; the shapes and tolerances are EN 12390-1 and the
    /// compression method is EN 12390-3, which is why both parts are named. <b>ASTM
    /// D695</b> - rigid-plastics prism 12.7x12.7x25.4 mm loaded along its 25.4 mm axis
    /// (the standard's cylinder alternative is 12.7 mm diameter by the same length).</para>
    ///
    /// <para>These are nominal geometries only. Nothing here asserts that a printed body
    /// is a valid specimen under the standard: layer orientation, surface finish, curing
    /// and the anisotropy of an extruded part all sit outside the geometry, and the
    /// standards were written for cast or moulded material.</para></summary>
    public static class TestSpecimens
    {
        public static TestSpecimen Nominal(StandardSpecimen specimen)
        {
            switch (specimen)
            {
                case StandardSpecimen.En196Prism:
                    return new TestSpecimen("DIN EN 196-1", "3-point bending", 160, 40, 40, span: 100);
                case StandardSpecimen.Iso178Bar:
                    return new TestSpecimen("DIN EN ISO 178", "3-point bending", 80, 10, 4, span: 64);
                case StandardSpecimen.AstmD790Bar:
                    return new TestSpecimen("ASTM D790", "3-point bending", 127, 12.7, 3.2, span: 51.2);
                case StandardSpecimen.En12390Cube:
                    return new TestSpecimen("DIN EN 12390-1/-3", "compression", 150, 150, 150);
                case StandardSpecimen.En12390Cylinder:
                    return new TestSpecimen("DIN EN 12390-1/-3", "compression", 150, 0, 300, round: true);
                case StandardSpecimen.AstmD695Prism:
                    return new TestSpecimen("ASTM D695", "compression", 12.7, 12.7, 25.4);
                default:
                    throw new ArgumentOutOfRangeException(nameof(specimen), $"unknown specimen {specimen}");
            }
        }

        /// <summary>Below this, a printed section is thinner than one LDM layer and the
        /// body cannot honestly be called the standard's specimen.</summary>
        public const double ThinWarningMm = 5.0;
    }

    /// <summary>Shape of the zig-zag extrusion test path.</summary>
    public sealed class ZigzagPathOptions
    {
        /// <summary>Length of one straight pass, mm.</summary>
        public double PassLength = 150.0;
        /// <summary>Distance between passes, mm - normally the layer spacing under test.</summary>
        public double Pitch = 10.0;
        /// <summary>Number of straight passes (&gt;= 2).</summary>
        public int Passes = 8;
        /// <summary>Stacked layers (&gt;= 1).</summary>
        public int Layers = 1;
        /// <summary>Layer height, mm; only read when <see cref="Layers"/> &gt; 1.</summary>
        public double LayerHeight = 3.0;
        /// <summary>Round the 180 degree reversals into a half circle of radius
        /// <see cref="Pitch"/>/2 instead of turning on the spot.</summary>
        public bool ArcTurns = true;
        /// <summary>Chords per half-circle turn.</summary>
        public int ArcSteps = 8;
    }

    /// <summary>A boustrophedon zig-zag line for extrusion tests: straight passes along
    /// X joined alternately at each end, optionally stacked.
    ///
    /// <para>This is a <b>toolpath, not a solid</b>. A straight line only shows
    /// steady-state flow; the reversals are the point of the test, because that is where
    /// an LDM layer tears on the inside of the turn and over-deposits on the outside.
    /// Rounded turns keep the tangent continuous and are the gentler case; sharp turns
    /// are the harsher one.</para>
    ///
    /// <para>Coordinates are local and millimetre-scale, centred on the origin in XY with
    /// the first layer at z = 0. The plugin layer maps them onto the base plane.</para></summary>
    public static class ZigzagPath
    {
        /// <summary>One polyline per layer, in print order.</summary>
        public static List<List<Vec3>> Build(ZigzagPathOptions o)
        {
            if (o == null) throw new ArgumentNullException(nameof(o));
            if (!(o.PassLength > 0) || double.IsNaN(o.PassLength) || double.IsInfinity(o.PassLength))
                throw new ArgumentOutOfRangeException(nameof(o), $"pass length must be finite and > 0 (got {o.PassLength})");
            if (!(o.Pitch > 0) || double.IsNaN(o.Pitch) || double.IsInfinity(o.Pitch))
                throw new ArgumentOutOfRangeException(nameof(o), $"pitch must be finite and > 0 (got {o.Pitch})");
            if (o.Passes < 2)
                throw new ArgumentOutOfRangeException(nameof(o), $"a zig-zag needs at least 2 passes (got {o.Passes})");
            if (o.Layers < 1)
                throw new ArgumentOutOfRangeException(nameof(o), $"layers must be >= 1 (got {o.Layers})");
            if (o.Layers > 1 && (!(o.LayerHeight > 0) || double.IsNaN(o.LayerHeight) || double.IsInfinity(o.LayerHeight)))
                throw new ArgumentOutOfRangeException(nameof(o), $"layer height must be finite and > 0 (got {o.LayerHeight})");
            int steps = Math.Max(2, o.ArcSteps);

            var layers = new List<List<Vec3>>(o.Layers);
            for (int layer = 0; layer < o.Layers; layer++)
            {
                double z = layer * o.LayerHeight;
                // alternate the start side per layer so a stack does not seam on the same
                // end every time - the seam is itself a flow artefact
                bool flip = (layer & 1) == 1;
                var pts = new List<Vec3>();
                for (int i = 0; i < o.Passes; i++)
                {
                    double y = (i - (o.Passes - 1) / 2.0) * o.Pitch;
                    bool rightward = ((i & 1) == 0) != flip;
                    double x0 = rightward ? -o.PassLength / 2 : o.PassLength / 2;
                    double x1 = -x0;
                    pts.Add(new Vec3(x0, y, z));
                    pts.Add(new Vec3(x1, y, z));
                    if (i == o.Passes - 1) break;

                    double yNext = (i + 1 - (o.Passes - 1) / 2.0) * o.Pitch;
                    if (!o.ArcTurns) continue;   // sharp: the next pass's start point is the corner
                    double cy = 0.5 * (y + yNext), rad = 0.5 * Math.Abs(yNext - y);
                    double dir = x1 > 0 ? 1.0 : -1.0;
                    for (int k = 1; k < steps; k++)
                    {
                        double a = Math.PI * k / steps;
                        pts.Add(new Vec3(x1 + dir * rad * Math.Sin(a), cy - rad * Math.Cos(a), z));
                    }
                }
                layers.Add(pts);
            }
            return layers;
        }

        /// <summary>Total path length, mm - the extruded length the test will lay down.</summary>
        public static double TotalLength(IReadOnlyList<List<Vec3>> layers)
        {
            double total = 0.0;
            if (layers == null) return total;
            foreach (var layer in layers)
                if (layer != null)
                    total += PolylineOps.ContourLength(layer);
            return total;
        }
    }
}
