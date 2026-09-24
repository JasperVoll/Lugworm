using System;
using Lugworm.Core.Extrusion;
using Xunit;

namespace Lugworm.Core.Tests;

public class RamExtrusionTests
{
    [Fact]
    public void RamTravelPerPathMm_SeparatesGeometryFromMachineParameters()
    {
        double actual = RamExtrusion.RamTravelPerPathMm(
            layerWidth: 2.0, layerHeight: 1.0, ramDiameter: 10.0, pressureCoefficient: 1.25);

        double expected = (2.0 / (Math.PI * 25.0)) * 1.25;
        Assert.Equal(expected, actual, 12);
    }

    [Fact]
    public void DepositedVolumeMm3_IsIndependentOfRamGeometry()
    {
        Assert.Equal(80.0, RamExtrusion.DepositedVolumeMm3(
            pathLength: 40.0, layerWidth: 2.0, layerHeight: 1.0), 12);
    }
}
