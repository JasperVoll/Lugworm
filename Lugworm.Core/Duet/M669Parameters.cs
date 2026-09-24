namespace Lugworm.Core.Duet
{
    /// <summary>Validated kinematic parameters reported by a bare M669 query.
    /// These are the firmware values that the slicer must agree with before it
    /// treats its SCARA model as authoritative.</summary>
    public sealed class M669Parameters
    {
        public double P;
        public double D;
        public double AMin;
        public double AMax;
        public double BMin;
        public double BMax;

        /// <summary>Minimum permitted radius. RRF omits the R word from an M669
        /// report (and the field from the object model) when it is unset, so an
        /// absent R must never be read as "the firmware says 0" - that would
        /// manufacture a mismatch against any non-zero software ReachMin.
        /// <see cref="HasR"/> distinguishes reported-as-0 from not-reported.</summary>
        public double R;
        public bool HasR;
    }
}
