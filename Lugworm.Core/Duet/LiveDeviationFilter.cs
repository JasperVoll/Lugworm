using System;

namespace Lugworm.Core.Duet
{
    public enum LiveDeviationState { Normal, Pending, Confirmed }

    /// <summary>Display-only persistence gate. A brief off-path reading holds the last
    /// displayed pose and is omitted from the display trail. Raw telemetry and machine
    /// controls never consume this decision. Distance must include commanded travels.</summary>
    public sealed class LiveDeviationFilter
    {
        public const double ToleranceMm = 5.0;
        public const double PersistenceSeconds = 1.5;
        DateTime _since, _previous;

        public LiveDeviationState State { get; private set; }

        public void Reset()
        {
            _since = _previous = default;
            State = LiveDeviationState.Normal;
        }

        /// <summary>Unknown comparisons pass through; they cannot establish persistence.
        /// Acquisition gaps and reversed/repeated timestamps restart the observation window.</summary>
        public LiveDeviationState Observe(DateTime receivedUtc, double distanceMm)
        {
            if (double.IsNaN(distanceMm) || double.IsInfinity(distanceMm)
                || distanceMm <= ToleranceMm || receivedUtc == default)
            {
                Reset();
                return State;
            }
            if (_previous == default || receivedUtc <= _previous
                || (receivedUtc - _previous).TotalSeconds > TrailChordPolicy.DefaultSparseGapSeconds)
            {
                _since = receivedUtc;
                State = LiveDeviationState.Pending;
            }
            _previous = receivedUtc;
            State = (receivedUtc - _since).TotalSeconds >= PersistenceSeconds
                ? LiveDeviationState.Confirmed : LiveDeviationState.Pending;
            return State;
        }
    }
}
