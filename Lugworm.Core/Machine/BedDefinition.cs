using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Machine
{
    /// <summary>Plate outline form. <see cref="Ellipse"/> with equal width and depth is
    /// a circle - there is deliberately no separate circle case to get out of step with
    /// it. Arbitrary boundary curves are a follow-up, which is why this is an enum with
    /// a serialized name rather than a bool.</summary>
    public enum BedShape
    {
        Rectangle = 0,
        Ellipse = 1,
    }

    /// <summary>Axis-aligned extents of a bed in mm. The bounding box of the outline,
    /// so for an ellipse it is the box the ellipse is inscribed in.</summary>
    public readonly struct BedBounds
    {
        public BedBounds(double minX, double minY, double maxX, double maxY)
        { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }

        public readonly double MinX, MinY, MaxX, MaxY;
        public double WidthMm => MaxX - MinX;
        public double DepthMm => MaxY - MinY;
    }

    /// <summary>Immutable print-bed description passed between Grasshopper components as
    /// <see cref="BedDefinitionText"/>. It is the single source of truth for plate
    /// size, shape and position: since the 4.0.0 bed contract no consumer carries
    /// width/depth/centre inputs of its own, so an unwired bed means "no plate", never
    /// a fallback plate.
    ///
    /// All dimensions are millimetres. The document-unit conversion happens exactly
    /// once, on the plugin side: at the Print Bed component's point inputs and at each
    /// consumer's drawing/output boundary.
    ///
    /// Two things it deliberately keeps separate:
    ///
    /// - <see cref="Center"/> is the plate's centre; <see cref="PrintCenter"/> is where
    ///   a part's bottom-centre lands. They are different quantities that merely live
    ///   near each other, and collapsing them would relocate parts in saved documents.
    /// - <see cref="ReachMinMm"/>/<see cref="ReachMaxMm"/>/<see cref="HasReachOverride"/>
    ///   are no longer produced by any component since 4.0.0; kept so an older
    ///   iamlab-bed:v1 wire still parses. Reach is the robot's, not the plate's.
    /// </summary>
    public sealed class BedDefinition
    {
        public BedDefinition(double widthMm, double depthMm, Vec3 center, Vec3 printCenter,
                             double reachMinMm = double.NaN, double reachMaxMm = double.NaN,
                             BedShape shape = BedShape.Rectangle)
        {
            if (!Num.Finite(widthMm) || widthMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(widthMm), "bed width must be finite and positive");
            if (!Num.Finite(depthMm) || depthMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(depthMm), "bed depth must be finite and positive");
            if (!Num.Finite(center))
                throw new ArgumentOutOfRangeException(nameof(center), "bed center must be finite");
            if (!Num.Finite(printCenter))
                throw new ArgumentOutOfRangeException(nameof(printCenter), "print center must be finite");
            if ((!double.IsNaN(reachMinMm) && !Num.Finite(reachMinMm))
                || (!double.IsNaN(reachMaxMm) && !Num.Finite(reachMaxMm)))
                throw new ArgumentOutOfRangeException(nameof(reachMinMm), "reach overrides must be finite or unset");
            if (shape != BedShape.Rectangle && shape != BedShape.Ellipse)
                throw new ArgumentOutOfRangeException(nameof(shape), "unknown bed shape");
            WidthMm = widthMm;
            DepthMm = depthMm;
            Center = center;
            PrintCenter = printCenter;
            ReachMinMm = reachMinMm;
            ReachMaxMm = reachMaxMm;
            Shape = shape;
        }

        /// <summary>Plate extent along Rhino X, mm. For an ellipse this is the full
        /// axis, so the semi-axis is half of it.</summary>
        public double WidthMm { get; }
        /// <summary>Plate extent along Rhino Y, mm.</summary>
        public double DepthMm { get; }
        public Vec3 Center { get; }
        public Vec3 PrintCenter { get; }
        /// <summary>Plate outline form; <see cref="BedShape.Rectangle"/> for every
        /// definition written by 4.0.0 and earlier.</summary>
        public BedShape Shape { get; }

        /// <summary>No longer produced by any component since 4.0.0; kept so an older
        /// iamlab-bed:v1 wire still parses. NaN = defer to the robot profile's reach
        /// annulus.</summary>
        public double ReachMinMm { get; }
        /// <summary>No longer produced by any component since 4.0.0; kept so an older
        /// iamlab-bed:v1 wire still parses. NaN = defer to the robot profile's reach
        /// annulus.</summary>
        public double ReachMaxMm { get; }

        /// <summary>No longer produced by any component since 4.0.0; kept so an older
        /// iamlab-bed:v1 wire still parses.</summary>
        public bool HasReachOverride => !double.IsNaN(ReachMinMm) || !double.IsNaN(ReachMaxMm);

        // ---- outline / containment geometry (pure math, mm) --------------------
        // One authority for the plate's shape. Live View and Robot Simulation both draw
        // through it, so their outlines, shading and grids cannot disagree.

        public double SemiWidthMm => 0.5 * WidthMm;
        public double SemiDepthMm => 0.5 * DepthMm;

        /// <summary>Axis-aligned extents; for an ellipse, the box it is inscribed in.</summary>
        public BedBounds Bounds => new BedBounds(
            Center.X - SemiWidthMm, Center.Y - SemiDepthMm,
            Center.X + SemiWidthMm, Center.Y + SemiDepthMm);

        /// <summary>Is (x, y) on the plate? <paramref name="tolMm"/> widens the plate,
        /// so a point exactly on the boundary counts as inside. For the ellipse the
        /// tolerance is applied along the axes: exact on the axes and slightly generous
        /// in between. It is a drawing/classification tolerance, not a certified
        /// clearance.</summary>
        public bool Contains(double xMm, double yMm, double tolMm = 1e-6)
        {
            double dx = xMm - Center.X, dy = yMm - Center.Y;
            if (Shape == BedShape.Rectangle)
                return Math.Abs(dx) <= SemiWidthMm + tolMm && Math.Abs(dy) <= SemiDepthMm + tolMm;
            double a = SemiWidthMm, b = SemiDepthMm;
            double limit = 1.0 + tolMm / Math.Min(a, b);
            double u = dx / a, v = dy / b;
            return u * u + v * v <= limit * limit;
        }

        /// <summary>The plate outline as a closed polyline at the plate's Z: first and
        /// last point are identical. A rectangle is exact (5 points); an ellipse is
        /// sampled with <paramref name="ellipseSegments"/> segments.</summary>
        public IReadOnlyList<Vec3> Outline(int ellipseSegments = 96)
        {
            double z = Center.Z;
            if (Shape == BedShape.Rectangle)
            {
                var b = Bounds;
                return new[]
                {
                    new Vec3(b.MinX, b.MinY, z),
                    new Vec3(b.MaxX, b.MinY, z),
                    new Vec3(b.MaxX, b.MaxY, z),
                    new Vec3(b.MinX, b.MaxY, z),
                    new Vec3(b.MinX, b.MinY, z),
                };
            }
            int n = Math.Max(12, ellipseSegments);
            var pts = new List<Vec3>(n + 1);
            for (int i = 0; i < n; i++)
            {
                double t = 2.0 * Math.PI * i / n;
                pts.Add(new Vec3(Center.X + SemiWidthMm * Math.Cos(t),
                                 Center.Y + SemiDepthMm * Math.Sin(t), z));
            }
            pts.Add(pts[0]);   // exactly closed, never a near-miss from cos(2 pi)
            return pts;
        }

        /// <summary>The plate's extent along X at the given Y - the horizontal clip of a
        /// grid line. False when the line misses the plate entirely.</summary>
        public bool SpanX(double yMm, out double xMinMm, out double xMaxMm)
        {
            xMinMm = xMaxMm = 0;
            double dy = yMm - Center.Y;
            if (Shape == BedShape.Rectangle)
            {
                if (Math.Abs(dy) > SemiDepthMm) return false;
                xMinMm = Center.X - SemiWidthMm; xMaxMm = Center.X + SemiWidthMm;
                return true;
            }
            double v = dy / SemiDepthMm;
            double rest = 1.0 - v * v;
            if (rest <= 0) return false;
            double half = SemiWidthMm * Math.Sqrt(rest);
            xMinMm = Center.X - half; xMaxMm = Center.X + half;
            return true;
        }

        /// <summary>The plate's extent along Y at the given X - the vertical clip of a
        /// grid line. False when the line misses the plate entirely.</summary>
        public bool SpanY(double xMm, out double yMinMm, out double yMaxMm)
        {
            yMinMm = yMaxMm = 0;
            double dx = xMm - Center.X;
            if (Shape == BedShape.Rectangle)
            {
                if (Math.Abs(dx) > SemiWidthMm) return false;
                yMinMm = Center.Y - SemiDepthMm; yMaxMm = Center.Y + SemiDepthMm;
                return true;
            }
            double u = dx / SemiWidthMm;
            double rest = 1.0 - u * u;
            if (rest <= 0) return false;
            double half = SemiDepthMm * Math.Sqrt(rest);
            yMinMm = Center.Y - half; yMaxMm = Center.Y + half;
            return true;
        }

        /// <summary>A square grid of the given pitch, clipped to the plate outline and
        /// laid out on absolute multiples of the pitch (so the grid does not slide when
        /// the plate moves). Each entry is one segment at the plate's Z. Segments
        /// shorter than <paramref name="minLengthMm"/> - the tangent slivers at an
        /// ellipse's extremes - are dropped.</summary>
        public IReadOnlyList<(Vec3 A, Vec3 B)> GridLines(double stepMm, double minLengthMm = 1e-6)
        {
            var lines = new List<(Vec3, Vec3)>();
            if (!Num.Finite(stepMm) || stepMm <= 0) return lines;
            var bounds = Bounds;
            double z = Center.Z;
            const double edge = 1e-6;
            for (double x = Math.Ceiling(bounds.MinX / stepMm) * stepMm; x <= bounds.MaxX + edge; x += stepMm)
                if (SpanY(x, out double y0, out double y1) && y1 - y0 >= minLengthMm)
                    lines.Add((new Vec3(x, y0, z), new Vec3(x, y1, z)));
            for (double y = Math.Ceiling(bounds.MinY / stepMm) * stepMm; y <= bounds.MaxY + edge; y += stepMm)
                if (SpanX(y, out double x0, out double x1) && x1 - x0 >= minLengthMm)
                    lines.Add((new Vec3(x0, y, z), new Vec3(x1, y, z)));
            return lines;
        }

        public override string ToString()
            => $"{F(WidthMm)}×{F(DepthMm)} mm {ShapeName(Shape)} @ ({F(Center.X)}, {F(Center.Y)}, {F(Center.Z)})";

        internal static string ShapeName(BedShape shape)
            => shape == BedShape.Ellipse ? "ellipse" : "rectangle";

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Stable string wire format for the Print Bed component output.
    /// Versioned for the same reason <c>RobotDefinitionText</c> is: an unrecognised
    /// definition must fail loudly rather than resolve to the wrong geometry.
    ///
    /// <c>v1</c> is the shipped rectangle format and is still exactly what a
    /// rectangular bed serializes to. A non-rectangular bed serializes as <c>v2</c>
    /// **because** an older build's parser ignores unknown keys: an ellipse written as
    /// v1 would be silently read as a rectangle by 4.0.0, which is the failure the
    /// version field exists to prevent.</summary>
    public static class BedDefinitionText
    {
        public const string Prefix = "iamlab-bed:";
        const string VersionRectangle = "v1";
        const string VersionShaped = "v2";

        public static string Serialize(BedDefinition bed)
        {
            if (bed == null) throw new ArgumentNullException(nameof(bed));
            bool shaped = bed.Shape != BedShape.Rectangle;
            var sb = new StringBuilder(Prefix)
                .Append(shaped ? VersionShaped : VersionRectangle).Append(':');
            sb.Append("w=").Append(N(bed.WidthMm));
            sb.Append(";d=").Append(N(bed.DepthMm));
            sb.Append(";cx=").Append(N(bed.Center.X));
            sb.Append(";cy=").Append(N(bed.Center.Y));
            sb.Append(";cz=").Append(N(bed.Center.Z));
            sb.Append(";px=").Append(N(bed.PrintCenter.X));
            sb.Append(";py=").Append(N(bed.PrintCenter.Y));
            sb.Append(";pz=").Append(N(bed.PrintCenter.Z));
            // Optional keys are omitted when unset, so the common string stays short
            // and adding a field later cannot break an older parse.
            if (shaped) sb.Append(";shape=").Append(BedDefinition.ShapeName(bed.Shape));
            if (!double.IsNaN(bed.ReachMinMm)) sb.Append(";rmin=").Append(N(bed.ReachMinMm));
            if (!double.IsNaN(bed.ReachMaxMm)) sb.Append(";rmax=").Append(N(bed.ReachMaxMm));
            return sb.ToString();
        }

        public static BedDefinition Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("bed definition is empty");
            string trimmed = text.Trim();
            int firstColon = trimmed.IndexOf(':');
            int secondColon = firstColon < 0 ? -1 : trimmed.IndexOf(':', firstColon + 1);
            if (firstColon < 0 || secondColon < 0
                || !string.Equals(trimmed.Substring(0, firstColon), "iamlab-bed", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("bed definition must use 'iamlab-bed:v1:<fields>'");

            string version = trimmed.Substring(firstColon + 1, secondColon - firstColon - 1);
            if (!string.Equals(version, VersionRectangle, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(version, VersionShaped, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"unsupported bed definition version '{version}'");

            var fields = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var shape = BedShape.Rectangle;   // a v1 bed is a rectangle, by definition
            foreach (string part in trimmed.Substring(secondColon + 1).Split(';'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    throw new FormatException($"bed definition field '{part}' is not 'key=value'");
                string key = part.Substring(0, eq).Trim();
                string value = part.Substring(eq + 1).Trim();
                if (string.Equals(key, "shape", StringComparison.OrdinalIgnoreCase))
                {
                    shape = ParseShape(value);
                    continue;
                }
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    throw new FormatException($"bed definition field '{key}' has a non-numeric value '{value}'");
                if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                    throw new FormatException($"bed definition field '{key}' must be finite");
                fields[key] = parsed;
            }

            double Req(string key)
                => fields.TryGetValue(key, out double v)
                    ? v
                    : throw new FormatException($"bed definition is missing required field '{key}'");
            double Opt(string key) => fields.TryGetValue(key, out double v) ? v : double.NaN;

            return new BedDefinition(
                Req("w"), Req("d"),
                new Vec3(Req("cx"), Req("cy"), Req("cz")),
                new Vec3(Req("px"), Req("py"), Req("pz")),
                Opt("rmin"), Opt("rmax"), shape);
        }

        /// <summary>Names and the enum's own numbers are both accepted, so the Print Bed
        /// component's integer <c>shape</c> input and a hand-written definition string
        /// cannot mean different things.</summary>
        static BedShape ParseShape(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "0":
                case "rect":
                case "rectangle": return BedShape.Rectangle;
                case "1":
                case "ellipse":
                case "circle": return BedShape.Ellipse;
                default: throw new FormatException($"unknown bed shape '{value}'");
            }
        }

        /// <summary>Null for empty input, so every consumer can read "unwired" as
        /// "no bed" without a special case at each call site.</summary>
        public static BedDefinition? Resolve(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : Parse(value!);

        static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
