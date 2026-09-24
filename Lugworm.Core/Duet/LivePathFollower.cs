using System;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Duet
{
    /// <summary>What the live display should pose from after one observation.</summary>
    public enum LivePathFollowMode
    {
        /// <summary>No intended path, no comparison, or the file is not executing: the
        /// measured position, not judged against anything.</summary>
        Measured,
        /// <summary>The reading is on the intended path within tolerance: pose ON the path
        /// at the located progress. The reading itself is evidence of progress, not of
        /// the route.</summary>
        OnPath,
        /// <summary>Off the path for less than the persistence time: hold the last
        /// progress and pose nothing from this reading. The reading stays in the raw logs.</summary>
        Pending,
        /// <summary>On the path, but further along than the last fix and a plausible
        /// advance allow. Held for one reading: the next one either corroborates it (both
        /// are then posed, the deferred one first) or it is dropped - the firmware
        /// reporting a queued move's target, or a glitch that landed on a later pass.</summary>
        Deferred,
        /// <summary>Off the path for the persistence time or longer: the measured position,
        /// shown as the deviation it is.</summary>
        Deviating,
    }

    /// <summary>One display decision of <see cref="LivePathFollower"/>.</summary>
    public readonly struct LivePathFollowResult
    {
        public LivePathFollowMode Mode { get; }
        /// <summary>Progress along the intended path, mm of arc length; NaN unless
        /// <see cref="LivePathFollowMode.OnPath"/>.</summary>
        public double PathPosition { get; }
        /// <summary>The point to pose from: on the path when on it, the observation
        /// otherwise. Machine frame, mm.</summary>
        public Vec3 Point { get; }
        /// <summary>Distance between the reading and the place it was compared against,
        /// mm; NaN when there was nothing to compare against.</summary>
        public double DistanceMm { get; }
        public bool Ambiguous { get; }
        /// <summary>True when the progress moved somewhere the window search could not
        /// reach - the machine was found elsewhere on the path, after a telemetry gap or
        /// after persistently reporting from there. A display should jump, not animate.</summary>
        public bool Relocated { get; }
        /// <summary>Progress of the PREVIOUS reading, which had been deferred and which this
        /// reading corroborates; NaN when there is none. A display poses it first, at that
        /// reading's own time, then this one.</summary>
        public double DeferredPathPosition { get; }
        /// <summary>The on-path point of the corroborated deferred reading.</summary>
        public Vec3 DeferredPoint { get; }

        public LivePathFollowResult(LivePathFollowMode mode, double pathPosition, Vec3 point,
                                    double distanceMm, bool ambiguous, bool relocated)
            : this(mode, pathPosition, point, distanceMm, ambiguous, relocated, double.NaN, default(Vec3)) { }

        public LivePathFollowResult(LivePathFollowMode mode, double pathPosition, Vec3 point,
                                    double distanceMm, bool ambiguous, bool relocated,
                                    double deferredPathPosition, Vec3 deferredPoint)
        {
            Mode = mode; PathPosition = pathPosition; Point = point; DistanceMm = distanceMm;
            Ambiguous = ambiguous; Relocated = relocated;
            DeferredPathPosition = deferredPathPosition; DeferredPoint = deferredPoint;
        }

        public bool OnPath => Mode == LivePathFollowMode.OnPath;
        public bool HasDeferred => !double.IsNaN(DeferredPathPosition);
    }

    /// <summary>Turns reported positions into PROGRESS along the intended path, for the
    /// display only. The presumption (2026-09-16) is that the machine executes its file:
    /// a reading on the path says how far the file has got, and the arm is posed on the
    /// path there - between readings it moves along the file's own route, not along a
    /// chord between two samples. A reading off the path is held rather than drawn, and
    /// only when the machine has reported itself away from the path for
    /// <see cref="PersistenceSeconds"/> is the measured position shown instead.</summary>
    /// <remarks>
    /// <para>Why a window: the 2026-09-16 captures show the seam of every layer reporting
    /// two positions 8-16 mm off the contour while the user position stands still, single
    /// readings with one axis 15-45 mm off along a straight run, and a Z that reads one
    /// layer high for two polls. A nearest-point search over the whole job cannot tell a
    /// neighbouring layer from the one being printed (the layer pitch is below the
    /// tolerance), so after the first fix the search is confined to an arc-length window
    /// behind and ahead of the last progress. Whatever is nearer in space but outside
    /// that window is not a candidate. The window ahead grows with the time since the
    /// last fix at <see cref="MaxSpeedMmPerS"/> at least, so a held reading or two never
    /// lets the machine out of it.</para>
    /// <para>Why a deferral: the same captures show the firmware reporting the target of a
    /// queued move for one poll (a whole layer start 48 mm early, a Z hop's top while the
    /// machine was still 36 mm short of it) and one-axis glitches that happen to land on a
    /// later pass of the same layer. Both look like a large advance along the path. A
    /// reading further ahead than the recent progress rate explains is therefore held for
    /// one poll: the next reading either lies at or beyond it and corroborates it - then
    /// both are posed, the deferred one first - or it is dropped. With a display delay of
    /// two poll intervals the deferral is invisible.</para>
    /// <para>Progress never runs backwards inside the window: a reading that projects behind
    /// the last progress holds it. Without a fix for <see cref="RelocateAfterSeconds"/> the
    /// window is not trusted and the next reading is located over the whole path, which
    /// the caller sees as <see cref="LivePathFollowResult.Relocated"/>. A machine that
    /// reports itself elsewhere ON the path for the persistence time is relocated there
    /// too, rather than shown as deviating.</para>
    /// <para>Nothing here edits, clamps or replaces a measurement: the trail, the logs and
    /// the numeric outputs keep consuming the observation. This decides what the ARM is
    /// posed from.</para>
    /// </remarks>
    public sealed class LivePathFollower
    {
        public const double ToleranceMm = LiveDeviationFilter.ToleranceMm;
        public const double PersistenceSeconds = LiveDeviationFilter.PersistenceSeconds;
        /// <summary>Without a fix for this long the window is not trusted and the next
        /// reading is located over the whole path.</summary>
        public const double RelocateAfterSeconds = 2.5;
        /// <summary>How far behind the last progress the window reaches. Wide enough that
        /// the true readings after a wrongly accepted advance are still found (and hold),
        /// instead of being reported off the path.</summary>
        public const double BackWindowMm = 100;
        /// <summary>The window ahead grows at this rate at least: faster than any clay
        /// printer travels, so the machine cannot leave the window between two polls, and
        /// slow enough that a reading a whole layer ahead is not a candidate.</summary>
        public const double MaxSpeedMmPerS = 200;
        /// <summary>Added to the window ahead whatever the elapsed time.</summary>
        public const double AheadSlackMm = 25;
        /// <summary>An advance beyond <c>(2 * rate + this) * dt + <see cref="AdvanceSlackMm"/></c>
        /// is deferred until the next reading corroborates it.</summary>
        public const double AdvanceMarginMmPerS = 20;
        public const double AdvanceSlackMm = 5;
        /// <summary>A reading this far behind a deferred one still corroborates it.</summary>
        public const double CorroborationBackMm = 25;

        readonly LivePathDeviation? _path;
        readonly LiveDeviationFilter _filter = new LiveDeviationFilter();
        bool _located;
        double _position, _speed;
        DateTime _utc;
        bool _haveCandidate;
        double _candidatePosition;
        Vec3 _candidatePoint;
        DateTime _candidateUtc;

        public LivePathFollower(LivePathDeviation? path)
        {
            _path = path != null && path.Valid ? path : null;
        }

        /// <summary>The mode of the last observation.</summary>
        public LivePathFollowMode Mode { get; private set; }

        /// <summary>True when there is an intended path to follow at all.</summary>
        public bool HasPath => _path != null;

        /// <summary>Last accepted progress, mm; NaN before the first fix.</summary>
        public double Position => _located ? _position : double.NaN;

        /// <summary>Progress rate estimated from the last two fixes inside the window, mm/s.</summary>
        public double SpeedMmPerS => _speed;

        public void Reset()
        {
            _filter.Reset();
            _located = false;
            _position = 0;
            _speed = 0;
            _utc = default(DateTime);
            _haveCandidate = false;
            Mode = LivePathFollowMode.Measured;
        }

        /// <summary>Classifies one reading and, when it is on the path, advances the
        /// progress.</summary>
        /// <param name="machine">The reported position, machine frame, mm.</param>
        /// <param name="utc">When the reading was taken; default when unstamped.</param>
        /// <param name="executing">True while the machine is running (or simulating) its
        /// file with every axis homed. Otherwise nothing is judged and nothing persists.</param>
        /// <param name="nearest">The caller's whole-path query for this reading, so it is
        /// not run twice; an unavailable result is queried here.</param>
        public LivePathFollowResult Observe(Vec3 machine, DateTime utc, bool executing,
                                            LivePathDeviationResult nearest)
        {
            if (_path == null || !Num.Finite(machine))
            {
                Reset();
                return Measured(machine, double.NaN, false);
            }
            if (!nearest.Available) nearest = _path.Query(machine);
            if (!nearest.Available)
            {
                _filter.Reset();
                _located = false;
                _haveCandidate = false;
                Mode = LivePathFollowMode.Measured;
                return Measured(machine, double.NaN, false);
            }

            bool recent = _located && utc != default(DateTime) && _utc != default(DateTime) && utc > _utc
                       && (utc - _utc).TotalSeconds <= RelocateAfterSeconds;
            var local = LivePathDeviationResult.Unavailable();
            if (recent)
            {
                double dt = (utc - _utc).TotalSeconds;
                double ahead = Math.Max(MaxSpeedMmPerS, 2 * _speed) * dt + AheadSlackMm;
                local = _path.Locate(machine, _position - BackWindowMm, _position + ahead);
            }
            bool inWindow = local.Available;
            var hit = inWindow ? local : nearest;

            if (!executing)
            {
                // Not running the file: no judgement and no persistence. A reading still
                // on the path keeps the progress so a resume continues from it; anything
                // else is the measured position.
                _filter.Reset();
                if (hit.DistanceMm <= ToleranceMm) return Accept(machine, hit, utc, inWindow, false);
                _located = false;
                _haveCandidate = false;
                Mode = LivePathFollowMode.Measured;
                return Measured(machine, hit.DistanceMm, hit.Ambiguous);
            }

            bool wasConfirmed = _filter.State == LiveDeviationState.Confirmed;
            var state = _filter.Observe(utc, hit.DistanceMm);
            if (state == LiveDeviationState.Normal)
                return Accept(machine, hit, utc, inWindow, relocated: _located && !inWindow);
            _haveCandidate = false;   // an off-path reading corroborates nothing
            if (state == LiveDeviationState.Confirmed)
            {
                if (!wasConfirmed && nearest.DistanceMm <= ToleranceMm)
                {
                    // Persistently somewhere else on the path: the progress moves there.
                    _filter.Reset();
                    return Accept(machine, nearest, utc, false, relocated: true);
                }
                _located = false;   // the next on-path reading is found over the whole path
                Mode = LivePathFollowMode.Deviating;
                return new LivePathFollowResult(LivePathFollowMode.Deviating, double.NaN, machine,
                                                nearest.DistanceMm, nearest.Ambiguous, false);
            }
            Mode = LivePathFollowMode.Pending;
            return new LivePathFollowResult(LivePathFollowMode.Pending, double.NaN, machine,
                                            hit.DistanceMm, hit.Ambiguous, false);
        }

        /// <summary>An on-path reading: corroborates or drops a deferred one, is deferred
        /// itself when it advances implausibly, and otherwise becomes the fix.</summary>
        LivePathFollowResult Accept(Vec3 machine, LivePathDeviationResult hit, DateTime utc, bool inWindow, bool relocated)
        {
            double s = hit.PathPosition;
            // Progress never runs backwards inside the window: a reading that projects
            // behind the last fix (one axis glitching along a straight run) holds it.
            if (inWindow && s < _position) s = _position;

            double deferredPosition = double.NaN;
            Vec3 deferredPoint = default(Vec3);
            if (_haveCandidate)
            {
                _haveCandidate = false;
                if (inWindow && s >= _candidatePosition - CorroborationBackMm)
                {
                    // The deferred reading was real: it becomes the previous fix, so the
                    // rate covers it and this reading is judged against it.
                    deferredPosition = _candidatePosition;
                    deferredPoint = _candidatePoint;
                    Fix(_candidatePosition, _candidateUtc, true);
                    if (s < _position) s = _position;
                }
            }
            if (inWindow && utc > _utc)
            {
                double dt = (utc - _utc).TotalSeconds;
                double allowance = (2 * _speed + AdvanceMarginMmPerS) * dt + AdvanceSlackMm;
                if (s - _position > allowance)
                {
                    _haveCandidate = true;
                    _candidatePosition = s;
                    _candidatePoint = s.Equals(hit.PathPosition) ? hit.Point : _path!.PointAt(s);
                    _candidateUtc = utc;
                    Mode = LivePathFollowMode.Deferred;
                    return new LivePathFollowResult(LivePathFollowMode.Deferred, double.NaN, machine,
                                                    hit.DistanceMm, hit.Ambiguous, false, deferredPosition, deferredPoint);
                }
            }
            Fix(s, utc, inWindow);
            Mode = LivePathFollowMode.OnPath;
            var point = s.Equals(hit.PathPosition) ? hit.Point : _path!.PointAt(s);
            return new LivePathFollowResult(LivePathFollowMode.OnPath, s, point, hit.DistanceMm, hit.Ambiguous,
                                            relocated, deferredPosition, deferredPoint);
        }

        void Fix(double s, DateTime utc, bool inWindow)
        {
            if (inWindow && utc > _utc)
            {
                double dt = (utc - _utc).TotalSeconds;
                _speed = dt > 0 ? (s - _position) / dt : _speed;
            }
            else if (!inWindow) _speed = 0;
            _position = s;
            _utc = utc;
            _located = true;
        }

        static LivePathFollowResult Measured(Vec3 machine, double distanceMm, bool ambiguous)
            => new LivePathFollowResult(LivePathFollowMode.Measured, double.NaN, machine, distanceMm, ambiguous, false);
    }
}
