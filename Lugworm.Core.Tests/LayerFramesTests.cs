using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

public class LayerFramesTests
{
    static readonly double Step10 = Math.PI / 18;

    static List<Vec3> Path(params (double x, double y)[] xy)
    {
        var pts = new List<Vec3>();
        foreach (var (x, y) in xy) pts.Add(new Vec3(x, y, 0));
        return pts;
    }

    static double AngleXy(Vec3 a, Vec3 b)
        => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);

    [Fact]
    public void StraightRun_OneUnitRingPerPoint()
    {
        var frames = LayerFrames.Build(Path((0, 0), (10, 0), (20, 0), (30, 0)), false, false, Step10);
        Assert.Equal(4, frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            Assert.Equal(i, frames[i].Source);
            Assert.Equal(1.0, frames[i].Side.Length, 9);
            // lateral axis is travel x Z: +X travel gives -Y
            Assert.Equal(-1.0, frames[i].Side.Y, 9);
            Assert.Equal(0.0, frames[i].Side.Z, 9);
        }
        Assert.Equal(0, LayerFrames.ExtraJoinFrames(Path((0, 0), (10, 0), (20, 0)), false, Step10));
    }

    [Fact]
    public void RightAngle_FansFromIncomingToOutgoingSide()
    {
        var path = Path((0, 0), (10, 0), (10, 10));
        var frames = LayerFrames.Build(path, false, false, Step10);
        // 1 + (9 + 1) + 1 rings; the corner adds 9
        Assert.Equal(12, frames.Count);
        Assert.Equal(9, LayerFrames.ExtraJoinFrames(path, false, Step10));
        var fan = frames.GetRange(1, 10);
        foreach (var f in fan)
        {
            Assert.Equal(1, f.Source);
            Assert.Equal(path[1], f.Centre);
            Assert.Equal(1.0, f.Side.Length, 9);
        }
        Assert.Equal(frames[0].Side.X, fan[0].Side.X, 9);
        Assert.Equal(frames[0].Side.Y, fan[0].Side.Y, 9);
        Assert.Equal(frames[11].Side.X, fan[9].Side.X, 9);
        Assert.Equal(frames[11].Side.Y, fan[9].Side.Y, 9);
        for (int j = 1; j < fan.Count; j++)
            Assert.Equal(Step10, AngleXy(fan[j - 1].Side, fan[j].Side), 9);
    }

    [Fact]
    public void GentleTurn_SingleMitredRingWidenedByHalfAngle()
    {
        double turn = 6 * Math.PI / 180;
        var path = Path((0, 0), (10, 0), (10 + 10 * Math.Cos(turn), 10 * Math.Sin(turn)));
        var frames = LayerFrames.Build(path, false, false, Step10);
        Assert.Equal(3, frames.Count);
        Assert.Equal(1.0 / Math.Cos(turn / 2), frames[1].Side.Length, 9);
        // the mitred axis bisects the two lateral axes
        Assert.Equal(turn / 2, AngleXy(frames[0].Side, frames[1].Side), 9);
    }

    [Fact]
    public void InfiniteStep_MitresEveryCornerLikeTheOldSweep()
    {
        var path = Path((0, 0), (10, 0), (10, 10));
        var frames = LayerFrames.Build(path, false, false, double.PositiveInfinity);
        Assert.Equal(3, frames.Count);
        Assert.Equal(Math.Sqrt(2), frames[1].Side.Length, 9);
        Assert.Equal(0, LayerFrames.ExtraJoinFrames(path, false, double.PositiveInfinity));
    }

    [Fact]
    public void UTurn_FansThroughHalfACircle()
    {
        // a hair off the line, so the turn direction is defined; the residual is 1e-7
        var path = Path((0, 0), (10, 0), (0, 0.000001));
        var frames = LayerFrames.Build(path, false, false, Step10);
        var fan = frames.GetRange(1, frames.Count - 2);
        Assert.Equal(19, fan.Count);
        Assert.Equal(-fan[0].Side.X, fan[18].Side.X, 5);
        Assert.Equal(-fan[0].Side.Y, fan[18].Side.Y, 5);
    }

    [Fact]
    public void ClosedSquare_EveryCornerFans_SourcesFollowThePoints()
    {
        var path = Path((0, 0), (10, 0), (10, 10), (0, 10));
        var frames = LayerFrames.Build(path, true, false, Step10);
        Assert.Equal(4 * 10, frames.Count);
        Assert.Equal(4 * 9, LayerFrames.ExtraJoinFrames(path, true, Step10));
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 10; j++)
                Assert.Equal(i, frames[i * 10 + j].Source);
    }

    [Fact]
    public void SplitSeam_HeadRingIsPlain_TailCarriesTheTurn()
    {
        // a closed square that repeats its first point, as a coloured run does
        var path = Path((0, 0), (10, 0), (10, 10), (0, 10), (0, 0));
        var frames = LayerFrames.Build(path, true, true, Step10);
        // head: 1 plain ring, three interior corners: 10 each, tail corner: 10
        Assert.Equal(1 + 30 + 10, frames.Count);
        Assert.Equal(0, frames[0].Source);
        Assert.Equal(1.0, frames[0].Side.Length, 9);
        // the head ring already faces the outgoing +X segment: lateral axis -Y
        Assert.Equal(-1.0, frames[0].Side.Y, 9);
        var tail = frames[frames.Count - 1];
        Assert.Equal(4, tail.Source);
        Assert.Equal(frames[0].Side.X, tail.Side.X, 9);
        Assert.Equal(frames[0].Side.Y, tail.Side.Y, 9);
    }

    [Fact]
    public void VerticalSegment_BorrowsTheNeighbourAxis_NoNaN()
    {
        var path = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(10, 0, 5), new(20, 0, 5) };
        var frames = LayerFrames.Build(path, false, false, Step10);
        Assert.Equal(4, frames.Count);
        foreach (var f in frames)
        {
            Assert.False(double.IsNaN(f.Side.X) || double.IsNaN(f.Side.Y) || double.IsNaN(f.Side.Z));
            Assert.Equal(-1.0, f.Side.Y, 9);
        }
        var vertical = new List<Vec3> { new(0, 0, 0), new(0, 0, 5) };
        var vf = LayerFrames.Build(vertical, false, false, Step10);
        Assert.Equal(2, vf.Count);
        Assert.Equal(1.0, vf[0].Side.Length, 9);
    }

    [Fact]
    public void FitJoinStep_CoarsensUntilTheBudgetHolds_ThenGivesUp()
    {
        var runs = new List<IReadOnlyList<Vec3>>();
        for (int r = 0; r < 10; r++) runs.Add(Path((0, r), (10, r), (10, r + 0.5)));   // ten right angles
        Assert.Equal(Math.PI / 18, LayerFrames.FitJoinStep(runs, 90));
        Assert.Equal(Math.PI / 12, LayerFrames.FitJoinStep(runs, 89));   // 15 deg: 6 per corner
        Assert.Equal(Math.PI / 4, LayerFrames.FitJoinStep(runs, 20));    // 45 deg: 2 per corner
        Assert.Equal(double.PositiveInfinity, LayerFrames.FitJoinStep(runs, 19));
        Assert.Equal(double.PositiveInfinity, LayerFrames.FitJoinStep(runs, 0));
    }
}
