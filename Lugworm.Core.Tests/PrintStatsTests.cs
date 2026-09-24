using System;
using System.Collections.Generic;
using System.Threading;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests
{
    // Preserved verbatim from the test file that was deleted together with
    // Universal Robots support. These cover PrintStatsCalc and the G-code
    // parser and never touched the retired robot path.
    public class PrintStatsTests
    {
        [Fact]
        public void GcodeVolume_ComesFromEValues_MatchesRamModel()
        {
            // 100 mm extrusion at E-per-mm = h×w/ramArea (the generator's model)
            double ram = 94.0, h = 2.0, w = 4.0;
            double ramArea = Math.PI * ram * ram / 4.0;
            double ePerMm = h * w / ramArea;
            var lines = new[]
            {
                "G21", "G90", "M83", "G28", "G92 E0",
                "G1 X0 Y0 Z2 F1200",
                $"G1 X100 Y0 Z2 F1200 E{(100 * ePerMm).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture)}",
            };
            var prog = GcodeParser.Parse(lines);
            var r = PrintStatsCalc.FromMoves(prog.Moves, w, h, 2.0, 25, ram);
            Assert.True(r.VolumeFromE);
            // 100 - 2 - 4 = 800 mm³ = 0.8 cm³
            Assert.Equal(0.8, r.VolumeCm3, 6);
            // geometric fallback (ram=0) must now agree with the same w×h model
            var geo = PrintStatsCalc.FromMoves(prog.Moves, w, h, 2.0, 25, 0);
            Assert.False(geo.VolumeFromE);
            Assert.Equal(r.VolumeCm3, geo.VolumeCm3, 6);
        }

        [Fact]
        public void Parser_TracksEDeltas_AbsoluteAndRelative()
        {
            var rel = GcodeParser.Parse(new[] { "M83", "G1 X10 Y0 Z1 E0.5", "G1 X20 Y0 Z1 E0.25" });
            Assert.Equal(0.5, rel.Moves[0].E, 9);
            Assert.Equal(0.25, rel.Moves[1].E, 9);
            var abs = GcodeParser.Parse(new[] { "M82", "G92 E0", "G1 X10 Y0 Z1 E0.5", "G1 X20 Y0 Z1 E0.75" });
            Assert.Equal(0.5, abs.Moves[0].E, 9);
            Assert.Equal(0.25, abs.Moves[1].E, 9);
        }

        [Fact]
        public void SquareTwoLayers_CorrectTotals()
        {
            // two square layers 100×100, one travel between them
            var pts = new List<Vec3>();
            var ext = new List<bool>();
            void Square(double z)
            {
                var c = new[] { (0.0, 0.0), (100.0, 0), (100, 100), (0, 100), (0, 0) };
                for (int i = 0; i < c.Length; i++)
                {
                    pts.Add(new Vec3(c[i].Item1, c[i].Item2, z));
                    ext.Add(pts.Count > 1 && i > 0);
                }
            }
            Square(2);
            Square(4);
            var r = PrintStatsCalc.FromPoints(pts, ext, layerWidthMm: 4, layerHeightMm: 2,
                densityGCm3: 2.0, defaultFeedMmS: 20, travelFeedMmS: 40);
            Assert.Equal(800, r.ExtrudeLenMm, 6);
            Assert.Equal(2, r.Layers);
            Assert.Equal(2, r.ZMinMm, 6);
            Assert.Equal(4, r.ZMaxMm, 6);
            // volume (geometric w×h, matches the G-code generator): 800×4×2 = 6400 mm³
            Assert.Equal(6.4, r.VolumeCm3, 3);
            Assert.Equal(12.8, r.MassG, 3);
            // time: 800mm at 20mm/s + 2mm travel at 40mm/s ≈ 40.05s
            Assert.InRange(r.TimeMin * 60, 39.9, 40.2);
            Assert.Contains("layers", r.Report());
        }

        // D4 (4.4.0): PrintStatsResult.Report is a GH panel string, not UI - it must
        // stay dot-decimal under a German ambient culture, same fixture pattern as
        // CultureInvarianceTests.
        [Fact]
        public void Report_NumbersStayDotDecimalUnderGermanAmbientCulture()
        {
            var pts = new List<Vec3>();
            var ext = new List<bool>();
            void Square(double z)
            {
                var c = new[] { (0.0, 0.0), (100.0, 0), (100, 100), (0, 100), (0, 0) };
                for (int i = 0; i < c.Length; i++)
                {
                    pts.Add(new Vec3(c[i].Item1, c[i].Item2, z));
                    ext.Add(pts.Count > 1 && i > 0);
                }
            }
            Square(2);
            Square(4);
            var r = PrintStatsCalc.FromPoints(pts, ext, layerWidthMm: 4, layerHeightMm: 2,
                densityGCm3: 2.0, defaultFeedMmS: 20, travelFeedMmS: 40);

            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                string invariant = r.Report();
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                string german = r.Report();
                Assert.Equal(invariant, german);
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }
        }
    }
}
