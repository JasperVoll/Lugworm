using System;
using System.Globalization;
using System.Threading;
using Lugworm.Core.Geometry;
using Lugworm.Core.Machine;
using Xunit;

namespace Lugworm.Core.Tests
{
    public class BedDefinitionTests
    {
        static BedDefinition Sample() => new BedDefinition(
            1000, 700, new Vec3(468, -441, 0), new Vec3(339, -193, 0));

        [Fact]
        public void SerializeRoundTripsGeometry()
        {
            var parsed = BedDefinitionText.Parse(BedDefinitionText.Serialize(Sample()));
            Assert.Equal(1000, parsed.WidthMm);
            Assert.Equal(700, parsed.DepthMm);
            Assert.Equal(468, parsed.Center.X);
            Assert.Equal(-441, parsed.Center.Y);
            Assert.Equal(0, parsed.Center.Z);
            Assert.Equal(339, parsed.PrintCenter.X);
            Assert.Equal(-193, parsed.PrintCenter.Y);
        }

        [Fact]
        public void SerializationIsStable()
        {
            string once = BedDefinitionText.Serialize(Sample());
            string twice = BedDefinitionText.Serialize(BedDefinitionText.Parse(once));
            Assert.Equal(once, twice);
            Assert.StartsWith("iamlab-bed:v1:", once);
        }

        [Fact]
        public void ReachOverrideIsOmittedWhenUnset()
        {
            string text = BedDefinitionText.Serialize(Sample());
            Assert.DoesNotContain("rmin", text);
            Assert.DoesNotContain("rmax", text);
            Assert.False(BedDefinitionText.Parse(text).HasReachOverride);
            Assert.True(double.IsNaN(BedDefinitionText.Parse(text).ReachMinMm));
        }

        [Fact]
        public void ReachOverrideRoundTripsWhenSet()
        {
            var bed = new BedDefinition(800, 800, new Vec3(0, 0, 0), new Vec3(0, 0, 0), 235, 1006.29);
            var parsed = BedDefinitionText.Parse(BedDefinitionText.Serialize(bed));
            Assert.True(parsed.HasReachOverride);
            Assert.Equal(235, parsed.ReachMinMm);
            Assert.Equal(1006.29, parsed.ReachMaxMm, 3);
        }

        [Fact]
        public void PartialReachOverrideIsAllowed()
        {
            var bed = new BedDefinition(800, 800, new Vec3(0, 0, 0), new Vec3(0, 0, 0), double.NaN, 900);
            var parsed = BedDefinitionText.Parse(BedDefinitionText.Serialize(bed));
            Assert.True(parsed.HasReachOverride);
            Assert.True(double.IsNaN(parsed.ReachMinMm));
            Assert.Equal(900, parsed.ReachMaxMm);
        }

        [Fact]
        public void ResolveTreatsEmptyAsUnwired()
        {
            Assert.Null(BedDefinitionText.Resolve(null));
            Assert.Null(BedDefinitionText.Resolve(""));
            Assert.Null(BedDefinitionText.Resolve("   "));
            Assert.NotNull(BedDefinitionText.Resolve(BedDefinitionText.Serialize(Sample())));
        }

        /// <summary>v1 (rectangle) and v2 (shaped) are the known versions; anything
        /// beyond them must fail loudly rather than resolve to the wrong plate.</summary>
        [Fact]
        public void UnsupportedVersionIsRejected()
        {
            var ex = Assert.Throws<FormatException>(
                () => BedDefinitionText.Parse("iamlab-bed:v3:w=1;d=1;cx=0;cy=0;cz=0;px=0;py=0;pz=0"));
            Assert.Contains("v3", ex.Message);
        }

        [Theory]
        [InlineData("not-a-bed")]
        [InlineData("iamlab-bed:v1")]
        [InlineData("iamlab-robot:v1:none")]
        public void MalformedDefinitionsAreRejected(string text)
            => Assert.Throws<FormatException>(() => BedDefinitionText.Parse(text));

        [Fact]
        public void MissingRequiredFieldIsRejected()
        {
            var ex = Assert.Throws<FormatException>(
                () => BedDefinitionText.Parse("iamlab-bed:v1:w=1000;cx=0;cy=0;cz=0;px=0;py=0;pz=0"));
            Assert.Contains("'d'", ex.Message);
        }

        [Fact]
        public void NonNumericFieldIsRejected()
            => Assert.Throws<FormatException>(
                () => BedDefinitionText.Parse("iamlab-bed:v1:w=wide;d=1;cx=0;cy=0;cz=0;px=0;py=0;pz=0"));

        [Theory]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        public void NonFiniteFieldsAreRejected(string value)
            => Assert.Throws<FormatException>(() => BedDefinitionText.Parse(
                $"iamlab-bed:v1:w=1000;d=700;cx={value};cy=0;cz=0;px=0;py=0;pz=0"));

        [Theory]
        [InlineData(0, 700)]
        [InlineData(-1, 700)]
        [InlineData(1000, 0)]
        [InlineData(1000, -1)]
        public void NonPositiveDimensionsAreRejected(double width, double depth)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new BedDefinition(
                width, depth, new Vec3(0, 0, 0), new Vec3(0, 0, 0)));

        [Fact]
        public void MalformedFieldIsRejected()
            => Assert.Throws<FormatException>(
                () => BedDefinitionText.Parse("iamlab-bed:v1:w1000;d=1;cx=0;cy=0;cz=0;px=0;py=0;pz=0"));

        [Fact]
        public void NegativeAndFractionalValuesSurvive()
        {
            var bed = new BedDefinition(1234.5, 678.25, new Vec3(-12.5, -441.75, 3.5),
                                        new Vec3(-1.25, -2.5, 0));
            var parsed = BedDefinitionText.Parse(BedDefinitionText.Serialize(bed));
            Assert.Equal(1234.5, parsed.WidthMm, 4);
            Assert.Equal(678.25, parsed.DepthMm, 4);
            Assert.Equal(-441.75, parsed.Center.Y, 4);
            Assert.Equal(3.5, parsed.Center.Z, 4);
            Assert.Equal(-1.25, parsed.PrintCenter.X, 4);
        }

        /// <summary>A comma-decimal culture must not change the wire format - these
        /// strings travel between machines and get pasted into saved documents.</summary>
        [Fact]
        public void WireFormatIsCultureIndependent()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var bed = new BedDefinition(1000.5, 700, new Vec3(468.25, -441, 0), new Vec3(0, 0, 0));
                string text = BedDefinitionText.Serialize(bed);
                Assert.Contains("w=1000.5", text);
                Assert.DoesNotContain(",", text);
                Assert.Equal(468.25, BedDefinitionText.Parse(text).Center.X, 4);
            }
            finally { Thread.CurrentThread.CurrentCulture = original; }
        }

        // ---- shape (the 4.0.0 bed contract) ------------------------------------

        static BedDefinition Ellipse(double w, double d, double cx = 0, double cy = 0)
            => new BedDefinition(w, d, new Vec3(cx, cy, 0), new Vec3(cx, cy, 0),
                                 double.NaN, double.NaN, BedShape.Ellipse);

        [Fact]
        public void RectangleIsTheDefaultShapeAndStaysVersionOne()
        {
            var bed = Sample();
            Assert.Equal(BedShape.Rectangle, bed.Shape);
            string text = BedDefinitionText.Serialize(bed);
            Assert.StartsWith("iamlab-bed:v1:", text);
            Assert.DoesNotContain("shape", text);
        }

        /// <summary>An older build ignores unknown keys, so an ellipse written as v1
        /// would be read as a rectangle. The version has to rise with the shape.</summary>
        [Fact]
        public void ShapeRoundTripsAndRaisesTheVersion()
        {
            string text = BedDefinitionText.Serialize(Ellipse(600, 400, 120, -80));
            Assert.StartsWith("iamlab-bed:v2:", text);
            Assert.Contains(";shape=ellipse", text);
            var parsed = BedDefinitionText.Parse(text);
            Assert.Equal(BedShape.Ellipse, parsed.Shape);
            Assert.Equal(600, parsed.WidthMm);
            Assert.Equal(400, parsed.DepthMm);
            Assert.Equal(120, parsed.Center.X);
            Assert.Equal(text, BedDefinitionText.Serialize(parsed));
        }

        /// <summary>Every bed written before the shape field existed is a rectangle.</summary>
        [Fact]
        public void VersionOneParsesAsARectangle()
        {
            var parsed = BedDefinitionText.Parse(
                "iamlab-bed:v1:w=1000;d=700;cx=468;cy=-441;cz=0;px=339;py=-193;pz=0");
            Assert.Equal(BedShape.Rectangle, parsed.Shape);
            Assert.True(parsed.Contains(468 + 499, -441));
            Assert.False(parsed.Contains(468 + 501, -441));
        }

        [Theory]
        [InlineData("rect", BedShape.Rectangle)]
        [InlineData("rectangle", BedShape.Rectangle)]
        [InlineData("0", BedShape.Rectangle)]
        [InlineData("Ellipse", BedShape.Ellipse)]
        [InlineData("circle", BedShape.Ellipse)]
        [InlineData("1", BedShape.Ellipse)]
        public void ShapeNamesAndNumbersMeanTheSameThing(string value, BedShape expected)
            => Assert.Equal(expected, BedDefinitionText.Parse(
                $"iamlab-bed:v2:w=10;d=10;cx=0;cy=0;cz=0;px=0;py=0;pz=0;shape={value}").Shape);

        [Fact]
        public void UnknownShapeIsRejected()
        {
            var ex = Assert.Throws<FormatException>(() => BedDefinitionText.Parse(
                "iamlab-bed:v2:w=10;d=10;cx=0;cy=0;cz=0;px=0;py=0;pz=0;shape=hexagon"));
            Assert.Contains("hexagon", ex.Message);
        }

        // ---- outline / containment geometry ------------------------------------

        [Fact]
        public void OffOriginRectangleContainmentAndBounds()
        {
            var bed = new BedDefinition(1000, 700, new Vec3(468, -441, 12), new Vec3(339, -193, 0));
            var b = bed.Bounds;
            Assert.Equal(-32, b.MinX, 6);
            Assert.Equal(968, b.MaxX, 6);
            Assert.Equal(-791, b.MinY, 6);
            Assert.Equal(-91, b.MaxY, 6);
            Assert.True(bed.Contains(468, -441));
            Assert.True(bed.Contains(-32, -791));      // exactly on the corner
            Assert.True(bed.Contains(968, -91));
            Assert.False(bed.Contains(968.5, -441));
            Assert.False(bed.Contains(468, -90.5));
        }

        [Fact]
        public void RectangleOutlineIsAClosedFivePointLoopAtPlateZ()
        {
            var bed = new BedDefinition(1000, 700, new Vec3(468, -441, 12), new Vec3(0, 0, 0));
            var outline = bed.Outline();
            Assert.Equal(5, outline.Count);
            Assert.Equal(outline[0].X, outline[4].X, 9);
            Assert.Equal(outline[0].Y, outline[4].Y, 9);
            foreach (var p in outline)
            {
                Assert.Equal(12, p.Z, 9);
                Assert.True(bed.Contains(p.X, p.Y), $"outline point {p} is off the plate");
            }
        }

        [Fact]
        public void CircleContainmentUsesTheRadiusNotTheBoundingBox()
        {
            var bed = Ellipse(600, 600, 100, -50);      // r = 300 about (100, -50)
            Assert.True(bed.Contains(100, -50));
            Assert.True(bed.Contains(400, -50));        // on the boundary
            Assert.True(bed.Contains(100, 250));
            Assert.False(bed.Contains(400.5, -50));
            // the bounding-box corner is off a circular plate - the case a rectangle
            // outline would get wrong in the viewport
            Assert.False(bed.Contains(400, 250));
            Assert.True(bed.Contains(100 + 300 / Math.Sqrt(2) - 0.01, -50 + 300 / Math.Sqrt(2) - 0.01));
        }

        [Fact]
        public void EllipseContainmentFollowsBothSemiAxes()
        {
            var bed = Ellipse(800, 400, -120, 60);      // a = 400, b = 200
            Assert.True(bed.Contains(-120 + 400, 60));
            Assert.True(bed.Contains(-120, 60 + 200));
            Assert.False(bed.Contains(-120 + 400, 60 + 1));
            Assert.False(bed.Contains(-120 + 300, 60 + 200));
            Assert.True(bed.Contains(-120 + 400 * 0.6, 60 + 200 * 0.8));   // 0.36 + 0.64 = 1
        }

        [Fact]
        public void EllipseOutlineIsClosedAndOnTheBoundary()
        {
            var bed = Ellipse(800, 400, -120, 60);
            var outline = bed.Outline(48);
            Assert.Equal(49, outline.Count);
            Assert.Equal(outline[0].X, outline[48].X, 12);
            Assert.Equal(outline[0].Y, outline[48].Y, 12);
            foreach (var p in outline)
            {
                double u = (p.X + 120) / 400.0, v = (p.Y - 60) / 200.0;
                Assert.Equal(1.0, u * u + v * v, 9);
                Assert.True(bed.Contains(p.X, p.Y), $"outline point {p} is off the plate");
            }
        }

        [Fact]
        public void SpansClipToTheOutline()
        {
            var rect = new BedDefinition(1000, 700, new Vec3(468, -441, 0), new Vec3(0, 0, 0));
            Assert.True(rect.SpanX(-441, out double rx0, out double rx1));
            Assert.Equal(-32, rx0, 6);
            Assert.Equal(968, rx1, 6);
            Assert.False(rect.SpanX(0, out _, out _));            // beyond the plate

            var circle = Ellipse(600, 600);                        // r = 300 at the origin
            Assert.True(circle.SpanX(0, out double x0, out double x1));
            Assert.Equal(-300, x0, 6);
            Assert.Equal(300, x1, 6);
            Assert.True(circle.SpanX(180, out double cx0, out double cx1));
            Assert.Equal(-240, cx0, 6);                            // sqrt(300^2 - 180^2)
            Assert.Equal(240, cx1, 6);
            Assert.True(circle.SpanY(-240, out double y0, out double y1));
            Assert.Equal(-180, y0, 6);
            Assert.Equal(180, y1, 6);
            Assert.False(circle.SpanX(300.5, out _, out _));
        }

        [Fact]
        public void GridOnARectangleSpansTheWholePlate()
        {
            var bed = new BedDefinition(1000, 700, new Vec3(468, -441, 5), new Vec3(0, 0, 0));
            var lines = bed.GridLines(50);
            Assert.NotEmpty(lines);
            foreach (var (a, b) in lines)
            {
                Assert.Equal(5, a.Z, 9);
                Assert.Equal(5, b.Z, 9);
                Assert.True(bed.Contains(a.X, a.Y) && bed.Contains(b.X, b.Y));
                bool vertical = Math.Abs(a.X - b.X) < 1e-9;
                if (vertical) Assert.Equal(700, b.Y - a.Y, 6);
                else Assert.Equal(1000, b.X - a.X, 6);
            }
            // absolute multiples of the pitch, so the grid does not slide with the plate
            Assert.Contains(lines, l => Math.Abs(l.A.X) < 1e-9 && Math.Abs(l.A.X - l.B.X) < 1e-9);
        }

        [Fact]
        public void GridOnACircleIsClippedToTheOutline()
        {
            var bed = Ellipse(600, 600);                 // r = 300
            var lines = bed.GridLines(50);
            Assert.NotEmpty(lines);
            foreach (var (a, b) in lines)
            {
                Assert.True(bed.Contains(a.X, a.Y), $"grid start {a} is off the plate");
                Assert.True(bed.Contains(b.X, b.Y), $"grid end {b} is off the plate");
            }
            // the centre line is a full diameter; the one 250 out is much shorter
            var centre = Assert.Single(lines, l => Math.Abs(l.A.X) < 1e-9 && Math.Abs(l.A.X - l.B.X) < 1e-9);
            Assert.Equal(600, centre.B.Y - centre.A.Y, 6);
            var near = Assert.Single(lines, l => Math.Abs(l.A.X - 250) < 1e-9 && Math.Abs(l.A.X - l.B.X) < 1e-9);
            Assert.Equal(2 * Math.Sqrt(300.0 * 300 - 250 * 250), near.B.Y - near.A.Y, 6);
            // no tangent sliver at x = 300
            Assert.DoesNotContain(lines, l => Math.Abs(l.A.X - 300) < 1e-9);
        }

        [Fact]
        public void GridRejectsANonPositivePitchInsteadOfLooping()
        {
            var bed = Sample();
            Assert.Empty(bed.GridLines(0));
            Assert.Empty(bed.GridLines(-50));
            Assert.Empty(bed.GridLines(double.NaN));
        }

        [Fact]
        public void ToStringNamesTheShape()
        {
            Assert.Contains("rectangle", Sample().ToString());
            Assert.Contains("ellipse", Ellipse(600, 600).ToString());
        }
    }
}
