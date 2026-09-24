using System;

namespace Lugworm.Core.Duet
{
    /// <summary>Changes that matter to consumers of an object-model snapshot.
    /// Motion telemetry is deliberately not part of this mask: it changes on
    /// virtually every poll and must not turn the canvas into a redraw loop.</summary>
    [Flags]
    public enum SnapshotDelta
    {
        None = 0,
        Status = 1,
        Job = 2,
        Reply = 4,
    }

    /// <summary>Pure comparison of two snapshots. Keep this predicate in Core so
    /// its behavior can be pinned without loading the Rhino/Grasshopper host.</summary>
    public static class ObjectModelDiff
    {
        public static SnapshotDelta Compare(ObjectModelSnapshot? oldSnap, ObjectModelSnapshot snap)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (oldSnap == null) return SnapshotDelta.Status | SnapshotDelta.Job;

            SnapshotDelta delta = SnapshotDelta.None;
            if (oldSnap.Status != snap.Status || oldSnap.RawStatus != snap.RawStatus
                || oldSnap.AllHomed != snap.AllHomed || AxesChanged(oldSnap, snap))
                delta |= SnapshotDelta.Status;

            if (!JobsEqual(oldSnap.Job, snap.Job)) delta |= SnapshotDelta.Job;
            if (!string.Equals(oldSnap.LastReply, snap.LastReply, StringComparison.Ordinal))
                delta |= SnapshotDelta.Reply;
            return delta;
        }

        static bool AxesChanged(ObjectModelSnapshot a, ObjectModelSnapshot b)
        {
            if (a.Axes.Count != b.Axes.Count) return true;
            for (int i = 0; i < a.Axes.Count; i++)
            {
                var x = a.Axes[i];
                var y = b.Axes[i];
                if (x.Letter != y.Letter || x.MachinePosition != y.MachinePosition
                    || x.UserPosition != y.UserPosition || x.Homed != y.Homed)
                    return true;
            }
            return false;
        }

        static bool JobsEqual(JobState? a, JobState? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.FileName == b.FileName && a.FilePosition == b.FilePosition && a.FileSize == b.FileSize
                && a.FractionPrinted == b.FractionPrinted && a.Layer == b.Layer && a.TimePrinting == b.TimePrinting;
        }
    }
}
