using System;
using System.Collections.Generic;
using System.Text;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class JobTimeEstimateTests
{
    static readonly DateTime T0 = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    static (byte[] bytes, JobTimeEstimate estimate) Program()
    {
        var lines = new List<string>
        {
            "G21", "G90", "M83", "G0 X0 Y0 Z0 F600", "; layer 1/2",
            "G1 X10 Y0 Z0 F600 E1", "; layer 2/2", "G1 X20 Y0 Z0 F600 E1",
        };
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        var parsed = GcodeParser.Parse(lines, allowPerAxisHome: true);
        return (bytes, JobTimeEstimate.FromMoves(parsed.Moves, lines, bytes));
    }

    [Fact]
    public void Estimate_maps_markers_and_positions_to_commanded_time()
    {
        var p = Program();
        Assert.Equal(2.0, p.estimate.TotalS, 6);
        Assert.Equal(0.0, p.estimate.SecondsAtLayer(1)!.Value, 6);
        Assert.Equal(1.0, p.estimate.SecondsAtLayer(2)!.Value, 6);
        Assert.Equal(1.0, p.estimate.TimeToLayer(2, p.estimate.Markers.OffsetOf(1))!.Value, 6);
        Assert.Equal(2, p.estimate.LayerAtTime(1.0, p.estimate.Markers.OffsetOf(1))!.Value);
    }

    [Fact]
    public void Correction_uses_last_three_clean_intervals_and_clamps()
    {
        var p = Program();
        Assert.Equal(1.0, p.estimate.Correction(Array.Empty<(int layer, double observedS)>()), 6);
        Assert.Equal(3.0, p.estimate.Correction(new[] { (2, 5.0) }), 6);
    }

    [Fact]
    public void Missing_job_bytes_make_snapshot_estimate_unavailable()
    {
        Assert.Null(JobTimeEstimate.FromSnapshot(JobSnapshot.External("0:/gcodes/x.gcode", T0)));
    }
}
