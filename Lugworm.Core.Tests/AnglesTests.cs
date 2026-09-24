using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Geometry/Angles.cs exposes exactly one member, NormalizeDeg180. No
/// separate shortest-arc or degree/radian conversion helper exists in this file
/// (that logic, if any, lives elsewhere and is out of scope here) - so coverage
/// focuses on wrap-around correctness at and past the [-180, 180) boundary, large
/// multiples of 360, and the two non-finite inputs the callers occasionally pass
/// through (a NaN or infinite joint reading should never look like a valid angle).</summary>
public class AnglesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(-90, -90)]
    [InlineData(179.9, 179.9)]
    [InlineData(-179.9, -179.9)]
    public void NormalizeDeg180_LeavesInRangeValuesUnchanged(double input, double expected)
    {
        Assert.Equal(expected, Angles.NormalizeDeg180(input), 9);
    }

    [Fact]
    public void NormalizeDeg180_UpperBoundIsExclusive()
    {
        // [-180, 180): +180 wraps down to -180, but -180 itself stays put.
        Assert.Equal(-180.0, Angles.NormalizeDeg180(180.0), 9);
        Assert.Equal(-180.0, Angles.NormalizeDeg180(-180.0), 9);
    }

    [Theory]
    [InlineData(360, 0)]
    [InlineData(-360, 0)]
    [InlineData(540, -180)]
    [InlineData(-540, -180)]
    [InlineData(720, 0)]
    public void NormalizeDeg180_WrapsFullTurns(double input, double expected)
    {
        Assert.Equal(expected, Angles.NormalizeDeg180(input), 9);
    }

    [Theory]
    [InlineData(-190, 170)]
    [InlineData(190, -170)]
    [InlineData(350, -10)]
    [InlineData(-350, 10)]
    public void NormalizeDeg180_WrapsPastTheBoundaryToTheOppositeSign(double input, double expected)
    {
        Assert.Equal(expected, Angles.NormalizeDeg180(input), 9);
    }

    [Fact]
    public void NormalizeDeg180_ManyFullTurnsReduceToTheSameResidue()
    {
        // 3,600,000 deg = 10,000 full turns exactly.
        Assert.Equal(0.0, Angles.NormalizeDeg180(3_600_000.0), 6);
    }

    [Theory]
    [InlineData(-170.0)]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(90.0)]
    [InlineData(179.0)]
    public void NormalizeDeg180_IsInvariantUnderAddingAnyNumberOfFullTurns(double baseAngle)
    {
        double expected = Angles.NormalizeDeg180(baseAngle);
        for (int k = -3; k <= 3; k++)
        {
            if (k == 0) continue;
            double actual = Angles.NormalizeDeg180(baseAngle + 360.0 * k);
            Assert.Equal(expected, actual, 6);
        }
    }

    [Fact]
    public void NormalizeDeg180_NaN_PropagatesAsNaN()
    {
        Assert.True(double.IsNaN(Angles.NormalizeDeg180(double.NaN)));
    }

    [Fact]
    public void NormalizeDeg180_Infinity_PropagatesAsNaN()
    {
        // x % y is NaN when x is infinite (IEEE 754 remainder rule); the wrap
        // therefore never mistakes an infinite joint reading for a real angle.
        Assert.True(double.IsNaN(Angles.NormalizeDeg180(double.PositiveInfinity)));
        Assert.True(double.IsNaN(Angles.NormalizeDeg180(double.NegativeInfinity)));
    }
}
