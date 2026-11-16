using System;
using System.Globalization;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Duet
{
    /// <summary>A tracked arm configuration held across a transport loss, with the
    /// machine-frame position of the last idle poll before the loss.</summary>
    public sealed class ReconnectCarry
    {
        public ReconnectCarry(ArmConfiguration configuration, double x, double y, double z)
        {
            Configuration = configuration;
            X = x; Y = y; Z = z;
        }

        public ArmConfiguration Configuration { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    /// <summary>When the session may keep the tracked arm configuration through an event
    /// that would otherwise forget it (maintainer decision of 2026-11-04: one verified home
    /// is enough). Two events qualify. A transport reconnect keeps it when the machine is
    /// provably where it was: same identity, still homed, idle, same position. A print the
    /// session started and validated keeps its final configuration through its own G28,
    /// and hands it back when the print runs to idle on a homed machine. Everything else
    /// (pause, resume, cancel, a foreign job, a firmware reset, raw terminal motion, an
    /// uninitiated status change) still forgets it.</summary>
    public static class ArmConfigurationCarry
    {
        /// <summary>Largest per-axis difference (mm) between the last poll before the loss
        /// and the first one after it that still counts as "did not move".</summary>
        public const double PositionToleranceMm = 0.05;

        /// <summary>What may be carried across a transport loss, or null when the last poll
        /// before it cannot anchor the arm (unknown configuration, not idle, unhomed, or no
        /// readable position).</summary>
        public static ReconnectCarry? CaptureAtLoss(ArmConfiguration configuration,
                                                    ObjectModelSnapshot? lastBeforeLoss)
        {
            if (!configuration.Known || lastBeforeLoss == null) return null;
            if (lastBeforeLoss.Status != MachineStatus.Idle || !lastBeforeLoss.AllHomed) return null;
            var position = lastBeforeLoss.MachinePosition;
            if (!position.HasValue) return null;
            return new ReconnectCarry(configuration, position.Value.X, position.Value.Y, position.Value.Z);
        }

        /// <summary>Null when <paramref name="carry"/> still describes the arm on the first
        /// poll after the reconnect; otherwise why it does not.</summary>
        public static string? ReconnectRefusal(ReconnectCarry carry, ObjectModelSnapshot after,
                                               string? trustRefusal)
        {
            if (carry == null) throw new ArgumentNullException(nameof(carry));
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (trustRefusal != null) return "machine identity not confirmed: " + trustRefusal;
            if (!after.AllHomed) return "an axis is unhomed after the reconnect";
            if (after.Status != MachineStatus.Idle) return "the machine is not idle after the reconnect";
            var position = after.MachinePosition;
            if (!position.HasValue) return "the position is unreadable after the reconnect";
            double moved = Math.Max(Math.Abs(position.Value.X - carry.X),
                           Math.Max(Math.Abs(position.Value.Y - carry.Y), Math.Abs(position.Value.Z - carry.Z)));
            if (moved > PositionToleranceMm)
                return "the machine moved "
                    + moved.ToString("0.###", CultureInfo.InvariantCulture)
                    + " mm while the connection was down";
            return null;
        }

        /// <summary>True on the poll edge that ends a print: a real print (not a dry run)
        /// went straight to idle and the machine is homed. Pause, cancel and halt edges never
        /// count; they run macros the checks did not see.</summary>
        public static bool JobCompleted(MachineStatus previous, ObjectModelSnapshot current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            return previous == MachineStatus.Processing
                && current.Status == MachineStatus.Idle
                && current.AllHomed;
        }

        /// <summary>True when the first poll after a reconnect shows the print this session
        /// started still printing on a machine with a confirmed identity, so the print's
        /// final configuration stays the one to hand back when it completes.</summary>
        public static bool JobStillRunning(ObjectModelSnapshot after, string? initiatedJob,
                                           string? trustRefusal)
        {
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (trustRefusal != null || string.IsNullOrWhiteSpace(initiatedJob)) return false;
            if (after.Status != MachineStatus.Processing) return false;
            string? file = after.Job?.FileName;
            return !string.IsNullOrWhiteSpace(file)
                && string.Equals(file!.Trim(), initiatedJob!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
