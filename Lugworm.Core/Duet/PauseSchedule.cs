using System;
using System.Collections.Generic;
using System.Threading;

namespace Lugworm.Core.Duet
{
    public enum PauseKind { AtTime, AtLayer }
    public enum ScheduledPausePhase { Pending, Sending }
    public enum PauseSchedulerPhase { Ready, Sending, AwaitingPause, AwaitingResume }

    public sealed class ScheduledPause
    {
        static long _nextId;
        public long Id { get; }
        public PauseKind Kind { get; }
        public ScheduledPausePhase Phase { get; }
        public DateTime? DueUtc { get; }
        public int? Layer { get; }
        public string RunId { get; }
        public DateTime CreatedUtc { get; }

        public ScheduledPause(long id, PauseKind kind, DateTime? dueUtc, int? layer,
                              string runId, DateTime createdUtc,
                              ScheduledPausePhase phase = ScheduledPausePhase.Pending)
        {
            Id = id;
            Kind = kind;
            Phase = phase;
            DueUtc = dueUtc.HasValue ? Utc(dueUtc.Value) : (DateTime?)null;
            Layer = layer;
            RunId = runId ?? "";
            CreatedUtc = Utc(createdUtc);
        }

        public static ScheduledPause AtTime(long id, DateTime dueUtc, string runId, DateTime createdUtc)
            => new ScheduledPause(id, PauseKind.AtTime, dueUtc, null, runId, createdUtc);

        public static ScheduledPause AtLayer(long id, int layer, string runId, DateTime createdUtc)
            => new ScheduledPause(id, PauseKind.AtLayer, null, layer, runId, createdUtc);

        public static ScheduledPause CreateAtTime(DateTime dueUtc, string runId, DateTime createdUtc)
            => AtTime(Interlocked.Increment(ref _nextId), dueUtc, runId, createdUtc);

        public static ScheduledPause CreateAtLayer(int layer, string runId, DateTime createdUtc)
            => AtLayer(Interlocked.Increment(ref _nextId), layer, runId, createdUtc);

        public ScheduledPause WithPhase(ScheduledPausePhase phase)
            => new ScheduledPause(Id, Kind, DueUtc, Layer, RunId, CreatedUtc, phase);

        static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    public sealed class PauseScheduleFacts
    {
        public MachineStatus Status { get; set; }
        public JobSnapshot? Run { get; set; }
        public string RunId { get; set; } = "";
        public int CurrentLayer { get; set; }
        public IReadOnlyList<ScheduledPause> Pauses { get; set; } = Array.Empty<ScheduledPause>();
    }

    /// <summary>Pure result of evaluating pending pauses. It never sends M25.</summary>
    public sealed class PauseEvaluation
    {
        public ScheduledPause? ToSend { get; }
        public ScheduledPause? ConsumedWithoutSend { get; }
        public string Notes { get; }
        public PauseSchedulerPhase Phase { get; }

        public PauseEvaluation(ScheduledPause? toSend, ScheduledPause? consumedWithoutSend,
                               string? notes, PauseSchedulerPhase phase)
        {
            ToSend = toSend;
            ConsumedWithoutSend = consumedWithoutSend;
            Notes = notes ?? "";
            Phase = phase;
        }
    }

    /// <summary>Marker crossings observed for one run, kept as the clean layer intervals
    /// the time estimate's correction reads. An interval is clean when the console saw
    /// both of its crossings and every poll between them was Processing or Simulating
    /// with no reconnect, so pauses and outages never count as print time. The first
    /// layer seen after attaching mid-print is never an interval: its start was not
    /// observed. Not thread-safe: fed from the poll thread only.</summary>
    public sealed class LayerCrossings
    {
        readonly List<(int layer, double observedS)> _clean = new List<(int layer, double observedS)>();
        int _lastLayer = -1;
        DateTime? _lastCrossingUtc;
        bool _dirty;

        public string RunId { get; }
        public IReadOnlyList<(int layer, double observedS)> CleanIntervals => _clean;

        public LayerCrossings(string runId) { RunId = runId ?? ""; }

        /// <summary>One poll: the marker layer at the reported file position, the status,
        /// and whether the connection was re-established since the previous poll.</summary>
        public void Observe(int markerLayer, MachineStatus status, bool reconnected, DateTime utc)
        {
            utc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            if (reconnected || (status != MachineStatus.Processing && status != MachineStatus.Simulating))
                _dirty = true;
            if (_lastLayer < 0) { _lastLayer = markerLayer; return; }
            if (markerLayer <= _lastLayer) return;
            if (_lastCrossingUtc.HasValue && !_dirty && markerLayer == _lastLayer + 1)
            {
                double seconds = (utc - _lastCrossingUtc.Value).TotalSeconds;
                if (seconds > 0) _clean.Add((markerLayer, seconds));
            }
            _lastLayer = markerLayer;
            _lastCrossingUtc = utc;
            _dirty = false;
        }
    }

    /// <summary>State needed for the one-stop barrier between scheduled pauses.</summary>
    public sealed class PauseScheduleState
    {
        public IReadOnlyList<ScheduledPause> Pauses { get; }
        public PauseSchedulerPhase Phase { get; }
        public DateTime? AcceptedUtc { get; }

        public PauseScheduleState(IReadOnlyList<ScheduledPause>? pauses = null,
                                  PauseSchedulerPhase phase = PauseSchedulerPhase.Ready,
                                  DateTime? acceptedUtc = null)
        {
            Pauses = pauses ?? Array.Empty<ScheduledPause>();
            Phase = phase;
            AcceptedUtc = acceptedUtc;
        }

        public PauseScheduleState With(IReadOnlyList<ScheduledPause> pauses,
                                       PauseSchedulerPhase phase, DateTime? acceptedUtc)
            => new PauseScheduleState(pauses, phase, acceptedUtc);
    }

    public static class PauseSchedulePolicy
    {
        public static readonly TimeSpan MaxHorizon = TimeSpan.FromHours(24);
        public static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
        /// <summary>At most this many pending pauses, so the Job page can always list them.</summary>
        public const int MaxPending = 8;

        public static string? RefuseAdd(PauseKind kind, DateTime? dueUtc, int? layer,
                                        DateTime nowUtc, PauseScheduleFacts facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            if (facts.Status != MachineStatus.Processing && facts.Status != MachineStatus.Simulating
                && facts.Status != MachineStatus.Paused)
                return "no running job";
            if (facts.Run == null || facts.RunId.Length == 0) return "running job identity is unavailable";
            if (facts.Pauses.Count >= MaxPending)
                return "at most " + MaxPending.ToString(System.Globalization.CultureInfo.InvariantCulture) + " scheduled pauses";
            foreach (var pause in facts.Pauses)
                if (kind == PauseKind.AtLayer && pause.Kind == kind && pause.Layer == layer)
                    return "that layer pause already exists";
            if (kind == PauseKind.AtTime)
            {
                if (!dueUtc.HasValue) return "pause time is missing";
                double seconds = (dueUtc.Value.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds;
                if (seconds <= 0) return "pause time must be in the future";
                if (seconds > MaxHorizon.TotalSeconds) return "pause time is more than 24 hours ahead";
            }
            else
            {
                if (!layer.HasValue) return "pause layer is missing";
                var markers = facts.Run.LayerMarkers;
                if (!markers.CanPauseAtLayer) return markers.Reason;
                if (layer.Value <= facts.CurrentLayer) return "pause layer must be after the current layer";
                if (layer.Value > markers.Total) return "pause layer is beyond the job total";
                if (markers.OffsetOf(layer.Value) < 0) return "pause layer is not present in the job";
            }
            return null;
        }

        public static string? RefuseAdd(PauseKind kind, DateTime dueUtc, DateTime nowUtc,
                                        PauseScheduleFacts facts)
            => RefuseAdd(kind, dueUtc, null, nowUtc, facts);

        public static string? RefuseAdd(PauseKind kind, int layer, DateTime nowUtc,
                                        PauseScheduleFacts facts)
            => RefuseAdd(kind, null, layer, nowUtc, facts);

        public static string? RefuseAdd(PauseKind kind, DateTime? dueUtc, int? layer,
                                        DateTime nowUtc, PrintConsoleState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var facts = new PauseScheduleFacts
            {
                Status = state.MachineState,
                Run = state.ActiveJob,
                RunId = state.ActiveJob?.RunId ?? "",
                CurrentLayer = state.CurrentMarkerLayer,
                Pauses = state.Pauses,
            };
            return RefuseAdd(kind, dueUtc, layer, nowUtc, facts);
        }

        public static PauseEvaluation Evaluate(IReadOnlyList<ScheduledPause> pauses, string runId,
                                               JobSnapshot? run, MachineStatus status,
                                               long filePosition, DateTime nowUtc,
                                               PauseSchedulerPhase phase = PauseSchedulerPhase.Ready,
                                               DateTime? acceptedUtc = null)
        {
            if (pauses == null) throw new ArgumentNullException(nameof(pauses));
            runId ??= "";
            if (phase == PauseSchedulerPhase.AwaitingPause)
            {
                if (status == MachineStatus.Pausing || status == MachineStatus.Paused)
                    return new PauseEvaluation(null, null, "scheduled pause observed", PauseSchedulerPhase.AwaitingResume);
                if (acceptedUtc.HasValue && (nowUtc.ToUniversalTime() - acceptedUtc.Value.ToUniversalTime()) >= ObservationTimeout)
                    return new PauseEvaluation(null, null, "scheduled pause was accepted but not observed", PauseSchedulerPhase.Ready);
                return new PauseEvaluation(null, null, "", phase);
            }
            if (phase == PauseSchedulerPhase.AwaitingResume)
            {
                if (status == MachineStatus.Processing || status == MachineStatus.Simulating)
                    return new PauseEvaluation(null, null, "scheduled pause resumed", PauseSchedulerPhase.Ready);
                return new PauseEvaluation(null, null, "", phase);
            }
            if (phase == PauseSchedulerPhase.Sending)
                return new PauseEvaluation(null, null, "", phase);
            if (status != MachineStatus.Processing && status != MachineStatus.Simulating
                && status != MachineStatus.Paused && status != MachineStatus.Pausing)
                return new PauseEvaluation(null, null, "", phase);

            var due = new List<ScheduledPause>();
            foreach (var pause in pauses)
            {
                if (pause.Phase != ScheduledPausePhase.Pending || !string.Equals(pause.RunId, runId, StringComparison.Ordinal)) continue;
                bool isDue = pause.Kind == PauseKind.AtTime
                    ? pause.DueUtc.HasValue && nowUtc.ToUniversalTime() >= pause.DueUtc.Value
                    : run != null && run.LayerMarkers.CanPauseAtLayer && pause.Layer.HasValue
                      && run.LayerMarkers.OffsetOf(pause.Layer.Value) >= 0
                      && filePosition >= run.LayerMarkers.OffsetOf(pause.Layer.Value);
                if (isDue) due.Add(pause);
            }
            due.Sort(CompareDue);
            if (due.Count == 0) return new PauseEvaluation(null, null, "", phase);
            var first = due[0];
            if (status == MachineStatus.Paused || status == MachineStatus.Pausing)
            {
                if (first.Kind == PauseKind.AtTime)
                    return new PauseEvaluation(null, first, "already paused", phase);
                return new PauseEvaluation(null, null, "", phase);
            }
            return new PauseEvaluation(first, null, "", phase);
        }

        public static PauseEvaluation Evaluate(PauseScheduleState state, string runId,
                                               JobSnapshot? run, MachineStatus status,
                                               long filePosition, DateTime nowUtc)
            => Evaluate(state.Pauses, runId, run, status, filePosition, nowUtc,
                        state.Phase, state.AcceptedUtc);

        public static bool TryClaimPause(IReadOnlyList<ScheduledPause> pauses, long id,
                                         out IReadOnlyList<ScheduledPause> claimed)
        {
            if (pauses == null) throw new ArgumentNullException(nameof(pauses));
            var list = new List<ScheduledPause>(pauses.Count);
            bool found = false;
            foreach (var pause in pauses)
            {
                if (pause.Id == id && pause.Phase == ScheduledPausePhase.Pending)
                {
                    list.Add(pause.WithPhase(ScheduledPausePhase.Sending));
                    found = true;
                }
                else list.Add(pause);
            }
            claimed = list;
            return found;
        }

        static int CompareDue(ScheduledPause a, ScheduledPause b)
        {
            if (a.Kind != b.Kind) return a.Kind == PauseKind.AtLayer ? -1 : 1;
            if (a.Kind == PauseKind.AtLayer) return (a.Layer ?? int.MaxValue).CompareTo(b.Layer ?? int.MaxValue);
            return Nullable.Compare(a.DueUtc, b.DueUtc);
        }
    }
}
