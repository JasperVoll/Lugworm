using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>Every source that can actually end a drawn run.</summary>
    /// <remarks>
    /// <para>Counted separately so a false break can be ATTRIBUTED instead of guessed at.
    /// The 2026-09-09 report "there are also splits at the seams" cost a round of debate
    /// precisely because the display could say how many runs it drew but not what ended
    /// them; the operator's own theory (seams classified as Travel) turned out not to be the
    /// mechanism at all, and no counter existed to say so.</para>
    /// <para>The distinction the enum exists to keep is between a drawing GAP
    /// (<see cref="SparseSample"/>: the two samples do belong to one run, the route between
    /// them is simply unknown) and a CONTINUITY break (every other member: the two samples
    /// do not belong to one run at all). Merging them would let a sparse acquisition look
    /// like a machine event, and a machine event look like a rendering choice.</para>
    /// </remarks>
    public enum TrailBreakReason
    {
        /// <summary>The shared <see cref="TrailContinuityPolicy"/> broke the segment - a
        /// coordinate reset, a time gap, homing, relative mode, a file-progress rollback or
        /// an out-of-order acquisition.</summary>
        ContinuityVerdict,

        /// <summary>A drawing gap, not a continuity break: the acquisition gap leaves the
        /// route between two otherwise continuous samples unknown
        /// (<see cref="TrailChordPolicy"/>).</summary>
        SparseSample,

        /// <summary>The observation carried no usable position, so there was nothing to
        /// join to.</summary>
        InvalidObservation,

        /// <summary>The observation was refused by the display's own pose validity gate,
        /// which is a stricter question than "is the position a number".</summary>
        PoseIneligible,

        /// <summary>A new retention run or a new connection generation: the display started
        /// keeping a different sequence of points.</summary>
        RunOrConnection,

        /// <summary>The frame, arm or configuration changed, so two coordinates on either
        /// side of the change are not in the same space.</summary>
        FrameOrConfiguration,

        /// <summary>Retention discarded the older end of a run. The run did not end at the
        /// machine; the display stopped keeping its start.</summary>
        Pruning,

        /// <summary>An off-path episode persisted long enough to enter the displayed
        /// trail. Start afresh instead of joining across its confirmation interval.</summary>
        SustainedDeviation,
    }

    /// <summary>How many runs each break source actually ended.</summary>
    /// <remarks>
    /// <para>Deliberately dumb: a fixed array of longs, one slot per enum member, so the
    /// live trail can count on every observation without allocating. Only
    /// <see cref="Lines"/> and <see cref="Describe"/> allocate, and only the display's
    /// diagnostics block calls them.</para>
    /// <para>NOT thread-safe on its own. The intended use is from a single thread inside the
    /// caller's existing lock - the live viewport already serialises trail mutation under the
    /// poll-shared lock, so adding an interlocked increment here would buy nothing and hide
    /// the requirement that counting happen at the same place the run is actually broken.</para>
    /// </remarks>
    public sealed class TrailBreakCounters
    {
        /// <summary>Names printed for each reason, in enum order. Kept next to the enum so a
        /// new member cannot be added without a word for it in the diagnostics block.</summary>
        static readonly string[] Names =
        {
            "continuity",
            "sparse sample",
            "invalid observation",
            "pose ineligible",
            "run or connection",
            "frame or configuration",
            "pruning",
            "sustained deviation",
        };

        readonly long[] _counts = new long[Names.Length];

        /// <summary>Records one break. An unrecognised reason is ignored rather than thrown:
        /// diagnostics must never be the thing that stops a print being drawn.</summary>
        public void Count(TrailBreakReason reason)
        {
            int i = (int)reason;
            if (i < 0 || i >= _counts.Length) return;
            _counts[i]++;
        }

        /// <summary>How many runs this reason ended; 0 for an unrecognised reason.</summary>
        public long Get(TrailBreakReason reason)
        {
            int i = (int)reason;
            return (i < 0 || i >= _counts.Length) ? 0L : _counts[i];
        }

        /// <summary>Every counted break, whatever the reason.</summary>
        public long Total
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _counts.Length; i++) total += _counts[i];
                return total;
            }
        }

        public void Reset()
        {
            Array.Clear(_counts, 0, _counts.Length);
        }

        /// <summary>One "name n" term per reason that has a non-zero count, in enum order.
        /// A reason that never fired prints nothing, so the block stays short and the terms
        /// that are present are the ones worth reading. "none" when nothing broke - the
        /// empty case has to say so explicitly, because a missing line reads as a missing
        /// measurement rather than as a clean run.</summary>
        public IReadOnlyList<string> Lines()
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>(_counts.Length);
            for (int i = 0; i < _counts.Length; i++)
            {
                if (_counts[i] == 0) continue;
                lines.Add(Names[i] + " " + _counts[i].ToString(inv));
            }
            if (lines.Count == 0) lines.Add("none");
            return lines;
        }

        /// <summary>The terms as one comma-separated phrase for the diagnostics block.</summary>
        public string Describe()
        {
            return string.Join(", ", Lines());
        }
    }
}
