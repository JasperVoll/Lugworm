using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>H1 shared helpers. The 4.4.0 review caught that the first shared
/// <c>Clamp01</c> silently read NaN as 0 while three of the four private copies it
/// replaced let NaN through; the shared form now propagates NaN and the one caller that
/// wants 0 (the print-console job fraction) guards it itself.</summary>
public class NumTests
{
    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(1.0, 1.0)]
    [InlineData(7.0, 1.0)]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Clamp01_ClampsFiniteAndInfiniteInputs(double v, double expected)
        => Assert.Equal(expected, Num.Clamp01(v));

    [Fact]
    public void Clamp01_PropagatesNaN()
        => Assert.True(double.IsNaN(Num.Clamp01(double.NaN)));

    [Fact]
    public void Clamp_ClampsToTheGivenRange()
    {
        Assert.Equal(2.0, Num.Clamp(-1.0, 2.0, 5.0));
        Assert.Equal(5.0, Num.Clamp(9.0, 2.0, 5.0));
        Assert.Equal(3.5, Num.Clamp(3.5, 2.0, 5.0));
        Assert.True(double.IsNaN(Num.Clamp(double.NaN, 2.0, 5.0)));
    }

    [Fact]
    public void Finite_RejectsNaNAndInfinity()
    {
        Assert.True(Num.Finite(0.0));
        Assert.True(Num.Finite(-1e300));
        Assert.False(Num.Finite(double.NaN));
        Assert.False(Num.Finite(double.PositiveInfinity));
        Assert.False(Num.Finite(double.NegativeInfinity));
        Assert.True(Num.Finite(new Vec3(1, 2, 3)));
        Assert.False(Num.Finite(new Vec3(1, double.NaN, 3)));
        Assert.False(Num.Finite(new Vec3(double.PositiveInfinity, 2, 3)));
    }
}
