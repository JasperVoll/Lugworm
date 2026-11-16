using System;
using System.Collections.Generic;
using System.Text;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class LayerMarkerTests
{
    [Fact]
    public void Format_round_trips_through_the_strict_reader()
    {
        for (int layer = 1; layer <= 4; layer++)
        {
            string line = LayerMarker.Format(layer, 4);
            Assert.True(LayerMarker.TryParseExact(line, out int n, out int m));
            Assert.Equal(layer, n);
            Assert.Equal(4, m);
        }
        Assert.False(LayerMarker.TryParseExact("; layer 01/4", out _, out _));
        Assert.False(LayerMarker.TryParseExact(" ; layer 1/4", out _, out _));
    }

    [Fact]
    public void Index_uses_byte_offsets_before_and_after_a_multibyte_comment()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("; note äöü\n; layer 1/2\nG1 X1\n; layer 2/2\n");
        var index = new LayerMarkerIndex(bytes);
        long first = Encoding.UTF8.GetByteCount("; note äöü\n");
        long second = first + Encoding.UTF8.GetByteCount("; layer 1/2\nG1 X1\n");

        Assert.True(index.CanPauseAtLayer);
        Assert.Equal(2, index.Total);
        Assert.Equal(2, index.Count);
        Assert.Equal(first, index.OffsetOf(1));
        Assert.Equal(second, index.OffsetOf(2));
        Assert.Equal(0, index.LayerAt(first - 1));
        Assert.Equal(1, index.LayerAt(first));
        Assert.Equal(1, index.LayerAt(second - 1));
        Assert.Equal(2, index.LayerAt(second));
    }

    [Theory]
    [InlineData("; layer 1/1\n")]
    [InlineData("; layer 1/3\n; layer 3/3\n")]
    [InlineData("; layer 1/2\n; layer 1/2\n")]
    [InlineData("; layer 2/2\n; layer 1/2\n")]
    [InlineData("; layer 1/2\n; layer 2/3\n")]
    [InlineData(";LAYER:0\n")]
    public void Invalid_marker_sets_fail_closed(string text)
    {
        var index = new LayerMarkerIndex(Encoding.UTF8.GetBytes(text));
        Assert.False(index.CanPauseAtLayer);
        Assert.False(string.IsNullOrEmpty(index.Reason));
    }

    [Fact]
    public void Builder_output_has_one_valid_marker_per_branch()
    {
        var c = new GcodeConfig
        {
            LayerHeight = 1,
            Width = 2,
            RamDiameter = 10,
            PressureCoeff = 1,
            PrintSpeed = 20,
            CoordinateFrame = GcodeCoordinateFrame.Identity,
        };
        var square0 = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(10, 0, 0), new Vec3(0, 0, 0) };
        var square1 = new List<Vec3> { new Vec3(0, 0, 1), new Vec3(10, 0, 1), new Vec3(0, 0, 1) };
        var result = GcodeBuilder.Build(new[] { new[] { square0 }, new[] { square1 } }, c);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", result.Lines) + "\n");
        var index = new LayerMarkerIndex(bytes);

        Assert.True(index.CanPauseAtLayer);
        Assert.Equal(2, index.Total);
        Assert.Equal(1, index.LayerAt(index.OffsetOf(2)) - 1);
    }

    [Fact]
    public void Tolerant_timeline_and_restart_still_read_written_markers()
    {
        var lines = new[]
        {
            "G21", "G90", "M83", "G0 X0 Y0 Z0 F600", "; layer 1/2",
            "G1 X10 Y0 Z0 F600 E1", "; layer 2/2", "G1 X20 Y0 Z1 F600 E1",
        };
        var markers = GcodeLayerMarkers.Scan(lines);
        Assert.Equal(1, markers[5]);
        Assert.Equal(2, markers[7]);

        var plan = GcodeRestart.Plan(lines, RestartMode.Percent, 0, 50, default,
                                     new RestartOptions { OriginalBytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"), ExactByteProvenance = true });
        Assert.Contains("layer 1/2", plan.LayerAtCut);
    }
}
