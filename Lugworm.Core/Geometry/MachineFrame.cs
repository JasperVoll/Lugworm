namespace Lugworm.Core.Geometry
{
    /// <summary>The Rhino design frame is the plugin's ground truth (maintainer
    /// directive, 2026-08-26): geometry is drawn, sliced, previewed and reported in
    /// Rhino coordinates, and the machine's own Cartesian frame exists only inside
    /// G-code words, firmware limits and Duet telemetry.
    ///
    /// The lab Potterbot's Cartesian frame is the Rhino frame REFLECTED about the
    /// line y = -x. Established on the machine 2026-08-26: the post-home pose the
    /// firmware reports as X200 Y-500 Z400 is the point the operator sees at Rhino
    /// X500 Y-200 Z400.
    ///
    ///     machine (x, y, z)  &lt;-&gt;  Rhino (-y, -x, z)
    ///
    /// Two properties matter for callers:
    /// <list type="bullet">
    /// <item>The map is its own inverse, so one negation pair converts either way
    /// and a round trip is bit-exact (negation is exact in IEEE-754).</item>
    /// <item>It is a REFLECTION, not a rotation. Distances, radii and speeds are
    /// preserved, but anything orientation-sensitive flips: arc direction
    /// (G2 &lt;-&gt; G3) and contour winding. Use <see cref="FlipArcCode"/> and
    /// <see cref="ToMachineArcOffset"/> when emitting arcs.</item>
    /// </list>
    ///
    /// Rule of thumb for where to convert: anything that VALIDATES or COMMANDS the
    /// machine (SCARA kinematics, joint ranges, the M208 envelope, JogPlanner,
    /// GcodeParser, JobGate) stays in machine coordinates so it can be audited
    /// against config.g line for line. Everything a person draws or reads is
    /// converted at the component boundary.</summary>
    public static class MachineFrame
    {
        /// <summary>Negation that keeps zero positive. Without it the map turns every
        /// 0 into -0, which formats as "-0.000" in emitted G-code and in every
        /// coordinate a person reads.</summary>
        static double Neg(double v) => v == 0.0 ? 0.0 : -v;

        /// <summary>Rhino point -&gt; machine (RRF Cartesian) point.</summary>
        public static Vec3 ToMachine(Vec3 rhino) => new Vec3(Neg(rhino.Y), Neg(rhino.X), rhino.Z);

        /// <summary>Machine (RRF Cartesian) point -&gt; Rhino point.</summary>
        public static Vec3 ToRhino(Vec3 machine) => new Vec3(Neg(machine.Y), Neg(machine.X), machine.Z);

        /// <summary>Rhino XY -&gt; machine XY.</summary>
        public static void ToMachine(double rx, double ry, out double mx, out double my)
        { mx = Neg(ry); my = Neg(rx); }

        /// <summary>Machine XY -&gt; Rhino XY.</summary>
        public static void ToRhino(double mx, double my, out double rx, out double ry)
        { rx = Neg(my); ry = Neg(mx); }

        /// <summary>Arc centre offsets (I/J) are a vector, so they map with the same
        /// reflection: I_machine = -J_rhino, J_machine = -I_rhino.</summary>
        public static void ToMachineArcOffset(double i, double j, out double mi, out double mj)
        { mi = Neg(j); mj = Neg(i); }

        /// <summary>A reflection reverses the sense of rotation, so an arc drawn
        /// clockwise in Rhino is counter-clockwise for the machine. Maps 2 &lt;-&gt; 3
        /// and leaves anything else alone.</summary>
        public static int FlipArcCode(int g) => g == 2 ? 3 : g == 3 ? 2 : g;

        /// <summary>True when the map reverses orientation. Constant, but named so
        /// call sites that depend on the property document why they flip.</summary>
        public const bool ReversesOrientation = true;
    }
}
