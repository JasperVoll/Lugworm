using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>
/// The simulation viewer colours deposition and travel differently, so the travel runs
/// must be the exact complement of the layer runs - anything counted twice or dropped
/// would show as a mis-coloured or missing segment.
/// </summary>
public class TravelRunsTests
{
    // 6 points, 5 segments: travel, print, print, travel, print
    static readonly List<Vec3> Pts = new()
    {
        new(0,0,0), new(1,0,0), new(2,0,0), new(3,0,0), new(4,0,0), new(5,0,0),
    };
    static readonly List<bool> Ext = new() { false, false, true, true, false, true };

    [Fact]
    public void TravelRunsAreTheSegmentsThatDoNotDeposit()
    {
        var travel = ExtrusionHistory.TravelRuns(Pts, Ext, 5);
        // segment 1 (0->1) and segment 4 (3->4)
        Assert.Equal(2, travel.Count);
        Assert.Equal(new Vec3(0, 0, 0), travel[0][0]);
        Assert.Equal(new Vec3(1, 0, 0), travel[0][1]);
        Assert.Equal(new Vec3(3, 0, 0), travel[1][0]);
        Assert.Equal(new Vec3(4, 0, 0), travel[1][1]);
    }

    [Fact]
    public void DepositionRunsAreUnchanged()
    {
        var runs = ExtrusionHistory.VisibleRuns(Pts, Ext, 5);
        Assert.Equal(2, runs.Count);
        Assert.Equal(3, runs[0].Count);                  // 1->2->3 is one continuous layer
        Assert.Equal(new Vec3(1, 0, 0), runs[0][0]);
        Assert.Equal(2, runs[1].Count);                  // 4->5
    }

    /// <summary>Together the two sets must cover every segment exactly once.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TheTwoSetsPartitionTheWalkedPrefix(int scrub)
    {
        static int Segments(List<List<Vec3>> runs) => runs.Sum(r => r.Count - 1);

        int printed = Segments(ExtrusionHistory.VisibleRuns(Pts, Ext, scrub));
        int travel = Segments(ExtrusionHistory.TravelRuns(Pts, Ext, scrub));
        Assert.Equal(scrub, printed + travel);
    }

    [Fact]
    public void AnAllTravelPathHasNoLayers()
    {
        var ext = new List<bool> { false, false, false, false, false, false };
        Assert.Empty(ExtrusionHistory.VisibleRuns(Pts, ext, 5));
        Assert.Single(ExtrusionHistory.TravelRuns(Pts, ext, 5));
    }

    [Fact]
    public void AnAllPrintPathHasNoTravel()
    {
        var ext = new List<bool> { false, true, true, true, true, true };
        Assert.Single(ExtrusionHistory.VisibleRuns(Pts, ext, 5));
        Assert.Empty(ExtrusionHistory.TravelRuns(Pts, ext, 5));
    }

    [Fact]
    public void ScrubbingBackwardsShortensBothSets()
    {
        Assert.Empty(ExtrusionHistory.TravelRuns(Pts, Ext, 0));
        Assert.Empty(ExtrusionHistory.VisibleRuns(Pts, Ext, 0));
        Assert.Single(ExtrusionHistory.TravelRuns(Pts, Ext, 1));
        Assert.Empty(ExtrusionHistory.VisibleRuns(Pts, Ext, 1));
    }
}
