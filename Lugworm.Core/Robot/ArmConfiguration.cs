namespace Lugworm.Core.Robot
{
    /// <summary>Known SCARA arm configuration carried between ordered motion
    /// walks. Unknown is deliberately a first-class value: a path whose starting
    /// machine configuration is not verified must not be treated as safe.</summary>
    public readonly struct ArmConfiguration
    {
        public readonly int ArmMode;
        public readonly int ThetaTurns;
        public readonly bool IsKnown;

        public bool Known => IsKnown;

        public static ArmConfiguration Unknown => new ArmConfiguration(-1, 0, false);

        public ArmConfiguration(int armMode, int thetaTurns)
            : this(armMode, thetaTurns, true) { }

        ArmConfiguration(int armMode, int thetaTurns, bool known)
        {
            ArmMode = armMode;
            ThetaTurns = thetaTurns;
            IsKnown = known;
        }

        public static ArmConfiguration Seed(int armMode)
            => new ArmConfiguration(armMode == 0 ? 0 : 1, 0, true);

        public override string ToString()
            => IsKnown ? $"ArmMode {ArmMode}, theta turns {ThetaTurns}" : "Unknown";
    }
}
