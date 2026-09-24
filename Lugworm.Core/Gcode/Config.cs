using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Gcode
{
    /// <summary>G-code dialect. Marlin matches the Python reference exactly
    /// (M900/M204). DuetRrf swaps the firmware-specific lines (M572 etc.).
    /// Klipper (4.0.0) accepts Marlin's motion and temperature codes but has no
    /// M900/M201/M205/M566 - pressure advance and the velocity limits become
    /// its extended commands (SET_PRESSURE_ADVANCE, SET_VELOCITY_LIMIT) and layer
    /// changes report SET_PRINT_STATS_INFO.</summary>
    public enum GcodeFlavor { Marlin, DuetRrf, Klipper }

    /// <summary>Mapping from the Rhino/toolpath design frame to emitted XYZ words.
    /// Identity is appropriate for ordinary Cartesian machines. LegacyScara preserves
    /// the calibrated Potterbot reflection used by existing IAMLab workflows.</summary>
    public enum GcodeCoordinateFrame { Identity, LegacyScara }

    /// <summary>How branch/contour transitions are handled. Clay barely oozes and the
    /// SCARA arm dislikes the retract/prime E bursts, so NoRetraction and
    /// LightRetraction minimise retraction moves; NormalRetraction is the classic
    /// behaviour for materials or setups that need clean seams.</summary>
    public enum RetractionMode
    {
        /// <summary>The whole print is one continuous path: the ram never pulls back
        /// between branches (one prime at the start, one retract at the very end).
        /// Close seams connect with a plain move; far seams still travel over the
        /// printed material via the path-aware safe hop, just without
        /// retract/prime.</summary>
        NoRetraction,
        /// <summary>Seams that align vertically or are very close (XY distance within
        /// SeamTol, not descending) are connected without retraction; only distant
        /// jumps retract, lift to a collision-safe height and prime.</summary>
        LightRetraction,
        /// <summary>A normal amount of retraction: EVERY seam (layer change, contour
        /// jump) retracts, lifts to a collision-safe height and primes, regardless of
        /// seam distance. More ram reversals, cleanest seams.</summary>
        NormalRetraction,
    }

    /// <summary>Mirror of clay_core_v2 DEFAULT_CFG. Defaults intentionally match the
    /// Python engine (Flavor=Marlin) so parity holds; the GH component layer sets its
    /// own UI defaults (e.g. Duet/RRF) on top.</summary>
    public sealed class GcodeConfig
    {
        public double LayerHeight = 1.0;
        public double Width = 2.0;
        public double RamDiameter = 15.0;
        public double PressureCoeff = 1.0;
        public double PrintSpeed = 20.0;
        public double TravelSpeed = 60.0;
        public double RetractSpeed = 10.0;
        public double FirstLayerSpeedFactor = 0.5;
        public double PressureOffset = 0.0;
        public double PrimeExtra = 0.0;
        /// <summary>Extra clearance (mm) above the printed material during retraction
        /// travels. Travels are path-aware: they lift to the highest already-printed Z
        /// along the travel corridor plus this margin, so huge manual hops are not
        /// needed for non-planar prints. &lt;= 0 falls back to one LayerHeight.</summary>
        public double ZHop = 0.0;
        /// <summary>Feedrate (mm/s) for the Z-hop lift/descend moves. &lt;= 0 falls back to
        /// TravelSpeed. Reducing it eases the vertical direction changes on a SCARA arm.</summary>
        public double HopSpeed = 0.0;
        /// <summary>Relative Z lift (mm) emitted in the footer before homing, so the arm
        /// clears the finished print before G28 sweeps XY. &lt;= 0 disables the lift.</summary>
        public double FooterZLift = 20.0;
        public double Coast = 0.0;
        public double LeadIn = 0.0;
        public double LeadInBoost = 1.0;
        public double Dwell = 0.0;
        public double ZOffset = 0.0;
        public bool UseArcs = false;
        public double ArcTol = 0.05;
        public double? LinearAdvanceK = null;
        public double? AccelPrint = null;
        public double? AccelTravel = null;
        public int FloorLayers = 0;
        public bool PauseAfterFloor = false;
        public int PauseTime = 0;
        public double MinLayerTime = 0.0;
        public bool Skirt = false;
        public double SkirtOffset = 3.0;
        public GcodeFlavor Flavor = GcodeFlavor.Marlin;
        public GcodeCoordinateFrame CoordinateFrame = GcodeCoordinateFrame.LegacyScara;
        public bool RelativeE = true;

        /// <summary>Seam handling - see <see cref="RetractionMode"/>. Spiral /
        /// single-path input needs no flag: a single-branch print is detected
        /// automatically and only the first layer-height Z band is slowed by
        /// FirstLayerSpeedFactor instead of the whole continuous helix.</summary>
        public RetractionMode Retraction = RetractionMode.LightRetraction;

        /// <summary>Alternate layers that contain open infill and closed walls:
        /// odd layers begin at the previous layer's infill endpoint, reverse the
        /// open paths, then return to the outside wall.</summary>
        public bool AlternateInfill = false;

        /// <summary>LightRetraction: max seam XY distance (mm) still connected with a
        /// plain move instead of retract + safe-hop + prime. &lt;= 0 falls back to
        /// 2 × Width.</summary>
        public double SeamTol = 0.0;

        // ---- per-axis acceleration / jerk limits (M201 / M566) ----
        // Emitted in the header when a base value is set, and re-issued (scaled by
        // RetractAccelFactor) around retraction & travel moves so the SCARA arm's current
        // spike at each direction change stays under the drive-fault ("V drop") threshold.
        public double? AccelXY = null;   // M201 X/Y, mm/s^2
        public double? AccelZ = null;    // M201 Z,   mm/s^2 (falls back to AccelXY)
        public double? AccelE = null;    // M201 E,   mm/s^2 - caps the retract/prime step-rate burst
        public double? JerkXY = null;    // internal mm/s; emitted as M566 mm/min or M205 mm/s
        public double? JerkZ = null;     // internal mm/s (falls back to JerkXY)
        public double? JerkE = null;     // internal mm/s; caps retract/prime speed change
        /// <summary>Scale (0..1) applied to AccelXY/Z + JerkXY/Z during retraction / travel
        /// moves. 1.0 = no change; smaller softens direction changes.</summary>
        public double RetractAccelFactor = 1.0;

        /// <summary>RRF's M566 takes mm/min; Marlin's M205 and Klipper's square-corner
        /// velocity take mm/s.</summary>
        public double JerkDialect(double internalMmS)
            => Flavor == GcodeFlavor.DuetRrf ? internalMmS * 60.0 : internalMmS;

        /// <summary>Single validation chokepoint shared by the builder, component
        /// and restart path. A malformed slider value must fail before any output or
        /// timing arithmetic can turn it into Infinity/NaN.</summary>
        public void Validate()
        {
            FinitePositive(LayerHeight, nameof(LayerHeight));
            FinitePositive(Width, nameof(Width));
            FinitePositive(RamDiameter, nameof(RamDiameter));
            FinitePositive(PressureCoeff, nameof(PressureCoeff));
            FinitePositive(PrintSpeed, nameof(PrintSpeed));
            FinitePositive(TravelSpeed, nameof(TravelSpeed));
            FinitePositive(RetractSpeed, nameof(RetractSpeed));
            FiniteNonNegative(FirstLayerSpeedFactor, nameof(FirstLayerSpeedFactor));
            if (!(FirstLayerSpeedFactor > 0)) throw new ArgumentException("FirstLayerSpeedFactor must be > 0", nameof(FirstLayerSpeedFactor));
            FiniteNonNegative(PressureOffset, nameof(PressureOffset));
            FiniteNonNegative(PrimeExtra, nameof(PrimeExtra));
            FiniteNonNegative(ZHop, nameof(ZHop));
            FiniteNonNegative(HopSpeed, nameof(HopSpeed));
            FiniteNonNegative(FooterZLift, nameof(FooterZLift));
            FiniteNonNegative(Coast, nameof(Coast));
            FiniteNonNegative(LeadIn, nameof(LeadIn));
            FiniteNonNegative(LeadInBoost, nameof(LeadInBoost));
            if (LeadInBoost < 1) throw new ArgumentException("LeadInBoost must be >= 1", nameof(LeadInBoost));
            FiniteNonNegative(Dwell, nameof(Dwell));
            Finite(ZOffset, nameof(ZOffset));
            FinitePositive(ArcTol, nameof(ArcTol));
            FiniteNonNegative(MinLayerTime, nameof(MinLayerTime));
            FiniteNonNegative(SkirtOffset, nameof(SkirtOffset));
            FiniteNonNegative(SeamTol, nameof(SeamTol));
            if (FloorLayers < 0) throw new ArgumentException("FloorLayers must be >= 0", nameof(FloorLayers));
            if (PauseTime < 0) throw new ArgumentException("PauseTime must be >= 0", nameof(PauseTime));
            if (LinearAdvanceK.HasValue) FiniteNonNegative(LinearAdvanceK.Value, nameof(LinearAdvanceK));
            if (AccelPrint.HasValue) FiniteNonNegative(AccelPrint.Value, nameof(AccelPrint));
            if (AccelTravel.HasValue) FiniteNonNegative(AccelTravel.Value, nameof(AccelTravel));
            if (AccelXY.HasValue) FiniteNonNegative(AccelXY.Value, nameof(AccelXY));
            if (AccelZ.HasValue) FiniteNonNegative(AccelZ.Value, nameof(AccelZ));
            if (AccelE.HasValue) FiniteNonNegative(AccelE.Value, nameof(AccelE));
            if (JerkXY.HasValue) FiniteNonNegative(JerkXY.Value, nameof(JerkXY));
            if (JerkZ.HasValue) FiniteNonNegative(JerkZ.Value, nameof(JerkZ));
            if (JerkE.HasValue) FiniteNonNegative(JerkE.Value, nameof(JerkE));
            if (RetractAccelFactor < 0 || RetractAccelFactor > 1 || double.IsNaN(RetractAccelFactor) || double.IsInfinity(RetractAccelFactor))
                throw new ArgumentException("RetractAccelFactor must be finite and in 0..1", nameof(RetractAccelFactor));
        }

        static void Finite(double v, string name)
        { if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException(name + " must be finite", name); }
        static void FinitePositive(double v, string name)
        { Finite(v, name); if (!(v > 0)) throw new ArgumentException(name + " must be > 0", name); }
        static void FiniteNonNegative(double v, string name)
        { Finite(v, name); if (v < 0) throw new ArgumentException(name + " must be >= 0", name); }
    }

    /// <summary>Output of the G-code builder.</summary>
    public sealed class GcodeResult
    {
        public List<string> Lines = new();
        public List<List<Vec3>> PrintPolys = new();
        public List<List<Vec3>> TravelPolys = new();
        public double Length;
        public double Volume;
        public double TimeMin;
        public bool Retract;
        public bool Arcs;
        public List<string> Warnings = new();
        /// <summary>First location the nozzle prints at (X/Y/Z as commanded, incl. ZOffset).
        /// Null when there is nothing to print. Handy for hanging the extruder over the
        /// start before a run.</summary>
        public Vec3? StartPoint;
    }
}
