namespace Lugworm.Core.Machine
{
    /// <summary>Machine-level M220/M221 overrides reported by the transport.</summary>
    public sealed class MachineOverrides
    {
        public double? SpeedFactorPercent { get; }
        public double? FlowFactorPercent { get; }

        public MachineOverrides(double? speedFactorPercent, double? flowFactorPercent)
        {
            SpeedFactorPercent = speedFactorPercent;
            FlowFactorPercent = flowFactorPercent;
        }
    }
}
