using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

public class ExtrusionHistoryTests
{
    static List<Vec3> Points(int count)
    {
        var points = new List<Vec3>();
        for (int i = 0; i < count; i++) points.Add(new Vec3(i, 0, 0));
        return points;
    }

    [Fact]
    public void VisibleRuns_GrowsOneContinuousRunWithScrubPrefix()
    {
        var points = Points(5);
        var extruding = new List<bool> { false, true, true, true, true };

        var halfway = ExtrusionHistory.VisibleRuns(points, extruding, 2);
        var complete = ExtrusionHistory.VisibleRuns(points, extruding, 4);

        Assert.Single(halfway);
        Assert.Equal(new[] { points[0], points[1], points[2] }, halfway[0]);
        Assert.Single(complete);
        Assert.Equal(points, complete[0]);
    }

    [Fact]
    public void VisibleRuns_SplitsOnlyAtActualTravelSegments()
    {
        var points = Points(6);
        var extruding = new List<bool> { false, true, true, false, true, true };

        var runs = ExtrusionHistory.VisibleRuns(points, extruding, 5);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { points[0], points[1], points[2] }, runs[0]);
        Assert.Equal(new[] { points[3], points[4], points[5] }, runs[1]);
    }

    [Fact]
    public void VisibleRuns_BackwardsScrubReturnsExactEarlierPrefix()
    {
        var points = Points(5);
        var extruding = new List<bool> { false, true, true, true, true };

        _ = ExtrusionHistory.VisibleRuns(points, extruding, 4);
        var earlier = ExtrusionHistory.VisibleRuns(points, extruding, 1);

        Assert.Single(earlier);
        Assert.Equal(new[] { points[0], points[1] }, earlier[0]);
    }

    [Fact]
    public void VisibleRuns_ClampsMismatchedAndOutOfRangeInputsSafely()
    {
        var points = Points(5);
        var extruding = new List<bool> { false, true, true };

        var runs = ExtrusionHistory.VisibleRuns(points, extruding, 99);

        Assert.Single(runs);
        Assert.Equal(new[] { points[0], points[1], points[2] }, runs[0]);
        Assert.Empty(ExtrusionHistory.VisibleRuns(points, extruding, 0));
    }
}
