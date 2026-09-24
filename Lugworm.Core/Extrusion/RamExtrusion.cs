using System;

namespace Lugworm.Core.Extrusion
{
    /// <summary>
    /// Pure ram/piston extrusion calculations. Layer geometry is kept separate from
    /// the configured machine ram and empirical pressure coefficient.
    /// </summary>
    public static class RamExtrusion
    {
        static double DepositedAreaMm2(double layerWidth, double layerHeight)
            => layerWidth * layerHeight;

        static double RamAreaMm2(double ramDiameter)
        {
            double radius = ramDiameter * 0.5;
            return Math.PI * radius * radius;
        }

        public static double RamTravelPerPathMm(
            double layerWidth,
            double layerHeight,
            double ramDiameter,
            double pressureCoefficient)
            => DepositedAreaMm2(layerWidth, layerHeight)
             / RamAreaMm2(ramDiameter)
             * pressureCoefficient;

        public static double DepositedVolumeMm3(
            double pathLength,
            double layerWidth,
            double layerHeight)
            => pathLength * DepositedAreaMm2(layerWidth, layerHeight);
    }
}
