using System;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;
using Xunit;

namespace Lugworm.Core.Tests;

// Locks the seam mode -> target-point mapping extracted from MoverComponent.SetSeam.
// These assertions mirror the pre-refactor branch logic exactly, so a behaviour
// change in SeamPlacer fails here instead of silently moving seams on the machine.
public class SeamPlacerTests
{
    static readonly Vec3 Centroid = new(5, 7, 3);
    const double BboxDiagonal = 40.0;

    [Fact]
    public void Keep_ReturnsNull()
        => Assert.Null(SeamPlacer.TargetFor(SeamMode.Keep, Centroid, BboxDiagonal, 90, 4,
            new Vec3(1, 2, 3), new Vec3(4, 5, 6), new Vec3(7, 8, 9)));

    [Fact]
    public void Align_ProjectsAlongAngle_AtBboxPlusClearance_KeepingCentroidZ()
    {
        // radius = bboxDiagonal + 10 (the seam ray must clear the bounding box)
        var t = SeamPlacer.TargetFor(SeamMode.Align, Centroid, BboxDiagonal, 0, 5,
            seamPoint: null, guideClosestPoint: null, trackTarget: null);
        Assert.NotNull(t);
        Assert.Equal(Centroid.X + 50.0, t!.Value.X, 9); // cos(0)*50
        Assert.Equal(Centroid.Y + 0.0, t.Value.Y, 9);   // sin(0)*50
        Assert.Equal(Centroid.Z, t.Value.Z, 9);         // layer index ignored for Align
    }

    [Fact]
    public void Align_IgnoresLayerIndex_ButStaggerMultipliesByIt()
    {
        var align = SeamPlacer.TargetFor(SeamMode.Align, Centroid, BboxDiagonal, 90, 3,
            null, null, null)!.Value;
        var stagger = SeamPlacer.TargetFor(SeamMode.Stagger, Centroid, BboxDiagonal, 90, 3,
            null, null, null)!.Value;

        // Align: 90 deg -> +Y. Stagger: 90*3 = 270 deg -> -Y. Different by construction.
        double r = BboxDiagonal + 10.0;
        Assert.Equal(Centroid.Y + r, align.Y, 6);
        Assert.Equal(Centroid.Y - r, stagger.Y, 6);
    }

    [Fact]
    public void Nearest_ReturnsSeamPoint_OrNullWhenAbsent()
    {
        var p = new Vec3(11, 12, 13);
        Assert.Equal(p, SeamPlacer.TargetFor(SeamMode.Nearest, Centroid, BboxDiagonal, 0, 0, p, null, null));
        Assert.Null(SeamPlacer.TargetFor(SeamMode.Nearest, Centroid, BboxDiagonal, 0, 0, null, null, null));
    }

    [Fact]
    public void NearestCandidateIndex_UsesSmallestFiniteDistance_AndKeepsTieOrder()
    {
        Assert.Equal(1, SeamPlacer.NearestCandidateIndex(new[] { 8.0, 2.0, 5.0 }));
        Assert.Equal(0, SeamPlacer.NearestCandidateIndex(new[] { 2.0, 2.0 }));
        Assert.Equal(1, SeamPlacer.NearestCandidateIndex(new[] { double.NaN, 3.0 }));
        Assert.Equal(-1, SeamPlacer.NearestCandidateIndex(new[] { double.NaN, -1.0 }));
    }

    [Fact]
    public void ReverseOpenPath_UsesWallDistanceWithoutTarget()
    {
        var start = new Vec3(0, 0, 0);
        var end = new Vec3(10, 0, 0);
        Assert.False(SeamPlacer.ReverseOpenPath(start, end, 1, 9, null));
        Assert.True(SeamPlacer.ReverseOpenPath(start, end, 9, 1, null));
        Assert.False(SeamPlacer.ReverseOpenPath(start, end, 1, 1, null));
    }

    [Fact]
    public void ReverseOpenPath_UserTargetChoosesOneOfTheOpenEndpoints()
    {
        var start = new Vec3(0, 0, 0);
        var end = new Vec3(10, 0, 0);
        Assert.True(SeamPlacer.ReverseOpenPath(start, end, 1, 9, new Vec3(9, 1, 0)));
        Assert.False(SeamPlacer.ReverseOpenPath(start, end, 9, 1, new Vec3(1, 1, 0)));
    }

    [Fact]
    public void OrientOpenPath_SpiralWallGateRejectsTheCentreEnd()
    {
        var centre = new Vec3(0, 0, 0);
        var outer = new Vec3(20, 0, 0);

        var choice = SeamPlacer.OrientOpenPath(
            centre, outer, 15, 3, new Vec3(0.1, 0, 0), null, new Vec3(0, 0, 0), 3);

        Assert.True(choice.Reverse);
        Assert.Equal(outer, choice.Start);
    }

    [Fact]
    public void OrientOpenPath_ZigzagUsesClearSeamTarget()
    {
        var start = new Vec3(-20, 0, 0);
        var end = new Vec3(20, 0, 0);

        var choice = SeamPlacer.OrientOpenPath(
            start, end, 3, 3, new Vec3(-18, 0, 0), null, new Vec3(0, 0, 0), 3);

        Assert.False(choice.Reverse);
        Assert.Equal(start, choice.Start);
    }

    [Fact]
    public void OrientOpenPath_TargetTieUsesPreviousLayerThenPositiveXRule()
    {
        var negativeX = new Vec3(-10, 0, 1);
        var positiveX = new Vec3(10, 0, 1);
        var target = new Vec3(0, 0, 1);

        var tracked = SeamPlacer.OrientOpenPath(
            negativeX, positiveX, 3, 3, target, new Vec3(-9, 0, 0),
            new Vec3(0, 0, 1), 3);
        var firstLayer = SeamPlacer.OrientOpenPath(
            negativeX, positiveX, 3, 3, target, null,
            new Vec3(0, 0, 1), 3);

        Assert.False(tracked.Reverse);
        Assert.Equal(negativeX, tracked.Start);
        Assert.True(firstLayer.Reverse);
        Assert.Equal(positiveX, firstLayer.Start);
    }

    [Fact]
    public void OrientOpenPath_AlternatingInputDirectionKeepsTwelveLayerStartsIdentical()
    {
        Vec3? previous = null;
        for (int layer = 0; layer < 12; layer++)
        {
            var left = new Vec3(-10, 0, layer);
            var right = new Vec3(10, 0, layer);
            var start = layer % 2 == 0 ? left : right;
            var end = layer % 2 == 0 ? right : left;

            var choice = SeamPlacer.OrientOpenPath(
                start, end, 3, 3, null, previous, new Vec3(0, 0, layer), 3);

            Assert.Equal(10.0, choice.Start.X, 12);
            Assert.Equal(0.0, choice.Start.Y, 12);
            previous = choice.Start;
        }
    }

    [Fact]
    public void Guide_ReturnsGuideClosestPoint_OrNullWhenAbsent()
    {
        var g = new Vec3(-4, 8, 2);
        Assert.Equal(g, SeamPlacer.TargetFor(SeamMode.Guide, Centroid, BboxDiagonal, 0, 0, null, g, null));
        Assert.Null(SeamPlacer.TargetFor(SeamMode.Guide, Centroid, BboxDiagonal, 0, 0, null, null, null));
    }

    [Fact]
    public void Track_ReturnsTrackTarget_OrNullWhenAbsent()
    {
        var tr = new Vec3(3, -3, 9);
        Assert.Equal(tr, SeamPlacer.TargetFor(SeamMode.Track, Centroid, BboxDiagonal, 0, 0, null, null, tr));
        Assert.Null(SeamPlacer.TargetFor(SeamMode.Track, Centroid, BboxDiagonal, 0, 0, null, null, null));
    }

    [Theory]
    [InlineData(0, SeamMode.Keep)]
    [InlineData(1, SeamMode.Align)]
    [InlineData(2, SeamMode.Stagger)]
    [InlineData(3, SeamMode.Nearest)]
    [InlineData(4, SeamMode.Guide)]
    [InlineData(5, SeamMode.Track)]
    public void EnumValues_MatchTheLegacyIntegerModes(int legacy, SeamMode mode)
        => Assert.Equal(legacy, (int)mode); // saved .gh definitions pass ints; must not drift
}
