using System;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Robot/JointSpeedLimit.cs: JointSpeedLimits.CapFeed inverts the joint
/// speed check RobotChecks.Run already does, capping a requested Cartesian feed so
/// the estimator never assumes a speed the robot cannot deliver. No other test file
/// references this type by name.</summary>
public class JointSpeedLimitTests
{
    static JointSpeedLimits Limits(double[][] jointsDeg, double maxJointSpeed, double maxFeedMmS)
        => new JointSpeedLimits(jointsDeg, maxJointSpeed, maxFeedMmS);

    [Fact]
    public void Constructor_NullJoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new JointSpeedLimits(null!, 100, 0));
    }

    [Theory]
    [InlineData(2, 100, 0, true)]
    [InlineData(2, 0, 50, true)]
    [InlineData(2, 0, 0, false)]     // neither cap configured
    [InlineData(1, 100, 50, false)]  // fewer than two samples: nothing to compare
    [InlineData(0, 100, 50, false)]
    public void Active_RequiresAtLeastTwoSamplesAndAtLeastOneConfiguredCap(
        int sampleCount, double maxJointSpeed, double maxFeedMmS, bool expected)
    {
        var joints = new double[sampleCount][];
        for (int i = 0; i < sampleCount; i++) joints[i] = new[] { (double)i };
        var limits = Limits(joints, maxJointSpeed, maxFeedMmS);
        Assert.Equal(expected, limits.Active);
    }

    [Fact]
    public void CapFeed_BelowTheJointLimit_PassesTheRequestThrough()
    {
        // one rotary joint moves 10deg over 10mm -> 1 deg/mm; a 50 mm/s ceiling
        // allows 50 mm/s of Cartesian feed for that ratio, so a 20 mm/s request
        // is not touched.
        var joints = new[] { new[] { 0.0 }, new[] { 10.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 20);
        Assert.Equal(20.0, capped, 9);
    }

    [Fact]
    public void CapFeed_AtExactlyTheJointLimit_PassesTheRequestThrough()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 10.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        // allowed = maxJointSpeed * dist / delta = 50 * 10 / 10 = 50 exactly.
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 50);
        Assert.Equal(50.0, capped, 9);
    }

    [Fact]
    public void CapFeed_AboveTheJointLimit_IsCappedToTheAllowedFeed()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        // allowed = 50 * 10 / 90 = 5.5556 mm/s, far below the 200 mm/s request.
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 200);
        Assert.Equal(50.0 * 10 / 90, capped, 9);
    }

    [Fact]
    public void CapFeed_MaxFeedAloneAppliesWhenNoJointCapIsConfigured()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 0, maxFeedMmS: 30);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 200);
        Assert.Equal(30.0, capped, 9);
    }

    [Fact]
    public void CapFeed_ZeroDistance_SkipsTheJointCheckButKeepsTheFeedCap()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 30);
        double capped = limits.CapFeed(0, 1, distMm: 0, requestedFeedMmS: 200);
        Assert.Equal(30.0, capped, 9);
    }

    [Fact]
    public void CapFeed_OutOfRangeSampleIndices_ReturnsTheFeedCappedRequestUnchanged()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        Assert.Equal(200.0, limits.CapFeed(-1, 1, distMm: 10, requestedFeedMmS: 200), 9);
        Assert.Equal(200.0, limits.CapFeed(0, 5, distMm: 10, requestedFeedMmS: 200), 9);
    }

    [Fact]
    public void CapFeed_NaNJointDelta_IsSkippedRatherThanPoisoningTheResult()
    {
        // axis 0 is unreachable at one end (NaN); axis 1 still constrains the feed.
        var joints = new[] { new[] { 0.0, 0.0 }, new[] { double.NaN, 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 200);
        Assert.Equal(50.0 * 10 / 90, capped, 9);
    }

    [Fact]
    public void CapFeed_InfiniteJointDelta_IsSkippedRatherThanPoisoningTheResult()
    {
        var joints = new[] { new[] { 0.0, 0.0 }, new[] { double.PositiveInfinity, 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: 200);
        Assert.Equal(50.0 * 10 / 90, capped, 9);
    }

    [Fact]
    public void CapFeed_NearSingularity_NeverReturnsBelowTheFloor()
    {
        // a tiny Cartesian step next to a large joint swing drives "allowed"
        // toward zero; the result is floored rather than going to zero/negative.
        var joints = new[] { new[] { 0.0 }, new[] { 179.0 } };
        var limits = Limits(joints, maxJointSpeed: 1e-9, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 1e-6, requestedFeedMmS: 100);
        Assert.Equal(1e-6, capped, 9);
    }

    [Fact]
    public void CapFeed_NaNRequestedFeed_FloorsRatherThanPropagatingNaN()
    {
        // capped starts at NaN and every "allowed < capped" comparison against a
        // NaN is false, so the joint loop never updates it; the final floor
        // guard (capped > 1e-6 ? capped : 1e-6) then converts it to the floor,
        // which is documented, observed behaviour, not a defect - the estimator
        // must never receive a NaN feed downstream.
        var joints = new[] { new[] { 0.0 }, new[] { 90.0 } };
        var limits = Limits(joints, maxJointSpeed: 50, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: double.NaN);
        Assert.Equal(1e-6, capped, 9);
    }

    [Fact]
    public void CapFeed_InfiniteRequestedFeed_IsCappedByMaxFeedWhenConfigured()
    {
        var joints = new[] { new[] { 0.0 }, new[] { 0.0 } };  // no joint motion: no joint cap applies
        var limits = Limits(joints, maxJointSpeed: 0, maxFeedMmS: 30);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: double.PositiveInfinity);
        Assert.Equal(30.0, capped, 9);
    }

    [Fact]
    public void CapFeed_InfiniteRequestedFeed_PassesThroughWhenNoCapIsConfigured()
    {
        // documents the pass-through: with MaxJointSpeedMm <= 0 the method
        // returns immediately, before the feed cap check even matters here.
        var joints = new[] { new[] { 0.0 }, new[] { 0.0 } };
        var limits = Limits(joints, maxJointSpeed: 0, maxFeedMmS: 0);
        double capped = limits.CapFeed(0, 1, distMm: 10, requestedFeedMmS: double.PositiveInfinity);
        Assert.True(double.IsPositiveInfinity(capped));
    }
}
