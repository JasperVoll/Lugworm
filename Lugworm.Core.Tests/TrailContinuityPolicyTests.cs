using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The one continuity rule set, exercised on synthetic observation sequences.
/// The rules it must keep: nothing is ever edited, deleted or projected; a break is a
/// segment boundary, not a discarded sample; and a path disagreement flags without
/// breaking, so a sustained tracking fault stays visible instead of being tidied into
/// two clean runs.</summary>
public class TrailContinuityPolicyTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 9, 8, 0, 0, DateTimeKind.Utc);

    sealed class Feed
    {
        public long Acquisition = 1;
        public double Seconds;
        public string Run = "run#1";
        public long Generation = 1;
        public long FilePosition = 1000;
        public double Extrusion = 1.0;
        public int Layer = 3;

        public TrailObservation At(double x, double y, double z, double dt = 0.4,
                                   MachineStatus status = MachineStatus.Processing,
                                   bool valid = true, bool homing = false,
                                   double extrusionStep = 0.1, long filePositionStep = 100,
                                   double? requested = 43)
        {
            Seconds += dt;
            Extrusion += extrusionStep;
            FilePosition += filePositionStep;
            return new TrailObservation
            {
                AcquisitionId = Acquisition++,
                RequestUtc = T0.AddSeconds(Seconds - 0.05),
                ReceivedUtc = T0.AddSeconds(Seconds),
                ConnectionGeneration = Generation,
                RunId = Run,
                PositionValid = valid,
                Position = new Vec3(x, y, z),
                AllHomed = true,
                Homing = homing,
                Status = status,
                JobActive = status != MachineStatus.Idle,
                Layer = Layer,
                FilePosition = FilePosition,
                FractionPrinted = 0.5,
                RequestedSpeedMms = requested,
                RawExtrusion = Extrusion,
            };
        }
    }

    /// <summary>A straight reference line along X at Z=5, so a lateral offset is exactly
    /// the observation's Y.</summary>
    static SameZPathIndex Line()
    {
        var points = new List<Vec3>();
        for (int i = 0; i <= 200; i++) points.Add(new Vec3(i, 0, 5));
        return SameZPathIndex.FromPolyline(points);
    }

    // ---- ordinary curves -----------------------------------------------------------

    [Fact]
    public void OrdinaryCurve_StaysOneSegment()
    {
        var policy = new TrailContinuityPolicy(null, Line());
        var feed = new Feed();
        var first = policy.Observe(feed.At(10, 0, 5));
        Assert.True(first.SegmentStarted);

        for (int i = 1; i <= 10; i++)
        {
            var v = policy.Observe(feed.At(10 + i * 5, 0.02 * i, 5));
            Assert.False(v.SegmentStarted);
            Assert.Equal(first.SegmentId, v.SegmentId);
            Assert.Equal(TrailQuality.Valid, v.Quality);
            Assert.Equal(TrailClass.Printing, v.Classification);
        }
    }

    // ---- breaks --------------------------------------------------------------------

    [Fact]
    public void NewRun_StartsANewSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        long a = policy.Observe(feed.At(10, 0, 5)).SegmentId;
        feed.Run = "run#2";
        var v = policy.Observe(feed.At(11, 0, 5));
        Assert.True(v.SegmentStarted);
        Assert.NotEqual(a, v.SegmentId);
        Assert.Equal(TrailQuality.Discontinuity, v.Quality);
        Assert.Contains("new run", v.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void SameFilePrintedTwice_GetsDifferentRunIdsAndSegments()
    {
        var policy = new TrailContinuityPolicy();
        var first = new Feed { Run = "0:/gcodes/print.gcode#1" };
        long a = policy.Observe(first.At(10, 0, 5)).SegmentId;
        var second = new Feed { Run = "0:/gcodes/print.gcode#2", Acquisition = 500, Seconds = 0.2 };
        var v = policy.Observe(second.At(10, 0, 5));
        Assert.True(v.SegmentStarted);
        Assert.NotEqual(a, v.SegmentId);
    }

    [Fact]
    public void ReconnectOnANewGeneration_StartsANewSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        feed.Generation = 2;
        Assert.True(policy.Observe(feed.At(11, 0, 5)).SegmentStarted);
    }

    [Fact]
    public void OutOfOrderAcquisition_StartsANewSegmentAndIsNotJoined()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        policy.Observe(feed.At(15, 0, 5));
        var stale = feed.At(20, 0, 5);
        stale.AcquisitionId = 1;      // an older acquisition arriving late
        var v = policy.Observe(stale);
        Assert.True(v.SegmentStarted);
        Assert.Contains("not newer", v.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPosition_IsFlaggedAndGuaranteesABreakOnBothSides()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        long a = policy.Observe(feed.At(10, 0, 5)).SegmentId;
        var bad = policy.Observe(feed.At(0, 0, 0, valid: false));
        Assert.Equal(TrailQuality.Invalid, bad.Quality);
        Assert.True(bad.SegmentStarted);
        var after = policy.Observe(feed.At(20, 0, 5));
        Assert.True(after.SegmentStarted);          // never stitched across the hole
        Assert.NotEqual(a, after.SegmentId);
        Assert.NotEqual(bad.SegmentId, after.SegmentId);
    }

    [Fact]
    public void Homing_BreaksTheSegmentAndIsNotClassifiedAsMotion()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var v = policy.Observe(feed.At(0, 0, 0, status: MachineStatus.Busy, homing: true));
        Assert.True(v.SegmentStarted);
        Assert.Equal(TrailClass.Unknown, v.Classification);
    }

    [Fact]
    public void TimeGap_BreaksTheSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var v = policy.Observe(feed.At(12, 0, 5, dt: 30));
        Assert.True(v.SegmentStarted);
        Assert.Contains("gap", v.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void FileProgressRollback_BreaksTheSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var v = policy.Observe(feed.At(12, 0, 5, filePositionStep: -500));
        Assert.True(v.SegmentStarted);
        Assert.Contains("file position", v.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinateReset_BreaksTheSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var v = policy.Observe(feed.At(10, 0, -400));
        Assert.True(v.SegmentStarted);
        Assert.Contains("coordinate jump", v.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeCoordinateMode_IsNeverJoined()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var relative = feed.At(12, 0, 5);
        relative.RelativeCoordinates = true;
        var v = policy.Observe(relative);
        Assert.True(v.SegmentStarted);
    }

    // ---- speed ---------------------------------------------------------------------

    [Fact]
    public void ImplausibleSpeed_SplitsUsingTheActualElapsedTime()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(0, 0, 5));
        // 200 mm in 0.4 s = 500 mm/s against 43 mm/s requested.
        var v = policy.Observe(feed.At(200, 0, 5));
        Assert.True(v.SegmentStarted);
        Assert.Equal(500.0, v.ImpliedSpeedMms, 6);
    }

    [Fact]
    public void ModeratelyHighSpeed_IsFlaggedWithoutSplitting()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        long a = policy.Observe(feed.At(0, 0, 5)).SegmentId;
        // 40 mm in 0.4 s = 100 mm/s against 43: above 43*1.5+5 but below 43*4+20.
        var v = policy.Observe(feed.At(40, 0, 5));
        Assert.False(v.SegmentStarted);
        Assert.Equal(a, v.SegmentId);
        Assert.Equal(TrailQuality.SuspectSpeed, v.Quality);
    }

    [Fact]
    public void SpeedCheckUsesElapsedTime_NotTheNominalPollInterval()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(0, 0, 5));
        // The same 40 mm over 4 s is 10 mm/s: plausible, and must not be flagged.
        var v = policy.Observe(feed.At(40, 0, 5, dt: 4));
        Assert.Equal(TrailQuality.Valid, v.Quality);
        Assert.Equal(10.0, v.ImpliedSpeedMms, 6);
    }

    // ---- composite speed metadata ----------------------------------------------------

    /// <summary>A poll assembles one observation out of several replies: the axes reply
    /// gives the coordinates and a later currentMove reply gives the requested feed, so the
    /// interval can contain a travel faster than the feed the ENDPOINT happens to report.
    /// That mismatch is about the metadata, not about continuity, so it may flag but must
    /// not split a real run.</summary>
    [Fact]
    public void CompositeSpeedMismatch_IsDiagnosticAndStaysInTheSameSegment()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        long a = policy.Observe(feed.At(0, 0, 5)).SegmentId;
        // 100 mm in 0.4 s = 250 mm/s against 43 requested: above 43*4+20 = 192, and well
        // under the 1000 mm/s ceiling.
        var composite = feed.At(100, 0, 5);
        composite.CompositeSpeedMetadata = true;
        var v = policy.Observe(composite);

        Assert.False(v.SegmentStarted);
        Assert.Equal(a, v.SegmentId);
        Assert.Equal(TrailQuality.SuspectSpeed, v.Quality);
        Assert.Equal(250.0, v.ImpliedSpeedMms, 6);
        Assert.Equal(100.0, composite.Position.X, 9);      // never edited
    }

    /// <summary>The same observation without the composite flag still breaks. The default is
    /// false, so an observation whose coordinates and feed came from one reply is judged
    /// exactly as strictly as before this rule existed.</summary>
    [Fact]
    public void TheSameMismatch_WithoutTheCompositeFlag_StillBreaks()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        long a = policy.Observe(feed.At(0, 0, 5)).SegmentId;
        var single = feed.At(100, 0, 5);
        Assert.False(single.CompositeSpeedMetadata);       // the default keeps old behaviour
        var v = policy.Observe(single);

        Assert.True(v.SegmentStarted);
        Assert.NotEqual(a, v.SegmentId);
        Assert.Equal(TrailQuality.Discontinuity, v.Quality);
        Assert.Contains("mm/s requested", v.Reason!, StringComparison.Ordinal);
    }

    /// <summary>Gross discontinuity is physics, not metadata: the absolute speed ceiling
    /// (the 2026-09-02 log's 1,432 mm/s sample) breaks a composite observation too.</summary>
    [Fact]
    public void CompositeObservationAboveTheSpeedCeiling_StillBreaks()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(0, 0, 5));
        // 240 mm in 0.2 s = 1200 mm/s, above the 1000 mm/s ceiling.
        var composite = feed.At(240, 0, 5, dt: 0.2);
        composite.CompositeSpeedMetadata = true;
        var v = policy.Observe(composite);

        Assert.True(v.SegmentStarted);
        Assert.Equal(TrailQuality.Discontinuity, v.Quality);
        Assert.Equal(1200.0, v.ImpliedSpeedMms, 6);
    }

    /// <summary>A composite observation earns no other exemption: a coordinate reset and a
    /// time gap break it exactly as they break any other observation.</summary>
    [Fact]
    public void CompositeObservation_StillBreaksOnAResetOrAGap()
    {
        var reset = new TrailContinuityPolicy();
        var a = new Feed();
        reset.Observe(a.At(10, 0, 5));
        var jumped = a.At(10, 0, -400);
        jumped.CompositeSpeedMetadata = true;
        var jump = reset.Observe(jumped);
        Assert.True(jump.SegmentStarted);
        Assert.Contains("coordinate jump", jump.Reason!, StringComparison.Ordinal);

        var gapped = new TrailContinuityPolicy();
        var b = new Feed();
        gapped.Observe(b.At(10, 0, 5));
        var late = b.At(12, 0, 5, dt: 30);
        late.CompositeSpeedMetadata = true;
        var gap = gapped.Observe(late);
        Assert.True(gap.SegmentStarted);
        Assert.Contains("gap", gap.Reason!, StringComparison.Ordinal);
    }

    // ---- path disagreement ---------------------------------------------------------

    [Fact]
    public void IsolatedPathDisagreement_FlagsAndPreservesWithoutBreaking()
    {
        var policy = new TrailContinuityPolicy(null, Line());
        var feed = new Feed();
        long a = policy.Observe(feed.At(10, 0.05, 5)).SegmentId;
        var spike = feed.At(20, 12.0, 5, dt: 1.0);
        var v = policy.Observe(spike);

        Assert.Equal(TrailQuality.SuspectPathDisagreement, v.Quality);
        Assert.False(v.SegmentStarted);                    // the excursion stays visible
        Assert.Equal(a, v.SegmentId);
        Assert.Equal(12.0, v.PathDistanceMm, 6);
        Assert.Equal(12.0, spike.Position.Y, 9);           // never projected or smoothed
        Assert.Equal(1, v.ConsecutivePathDisagreements);

        var back = policy.Observe(feed.At(30, 0.04, 5, dt: 1.0));
        Assert.Equal(TrailQuality.Valid, back.Quality);
        Assert.Equal(0, back.ConsecutivePathDisagreements);
    }

    [Fact]
    public void SustainedPathDisagreement_StaysFlaggedOnEverySample()
    {
        var policy = new TrailContinuityPolicy(null, Line());
        var feed = new Feed();
        policy.Observe(feed.At(10, 0.05, 5));
        for (int i = 1; i <= 12; i++)
        {
            var v = policy.Observe(feed.At(10 + i, 9.0, 5, dt: 1.0));
            Assert.Equal(TrailQuality.SuspectPathDisagreement, v.Quality);
            Assert.Equal(i, v.ConsecutivePathDisagreements);
        }
    }

    [Fact]
    public void NoIntendedPath_ReportsUnknownDistanceRatherThanZero()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        var v = policy.Observe(feed.At(10, 500, 5));
        Assert.True(double.IsNaN(v.PathDistanceMm));
        Assert.Equal(TrailQuality.Discontinuity, v.Quality);   // first observation
    }

    // ---- classification ------------------------------------------------------------

    [Fact]
    public void PausedAndResumedMotion_IsClassifiedAsPaused()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        Assert.Equal(TrailClass.Paused, policy.Observe(feed.At(11, 0, 5, status: MachineStatus.Paused)).Classification);
        Assert.Equal(TrailClass.Paused, policy.Observe(feed.At(11, 0, 5, status: MachineStatus.Resuming)).Classification);
        Assert.Equal(TrailClass.Printing, policy.Observe(feed.At(12, 0, 5)).Classification);
    }

    [Fact]
    public void MotionWithoutExtrusion_IsTravelNotPrinting()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        Assert.Equal(TrailClass.Travel, policy.Observe(feed.At(15, 0, 5, extrusionStep: 0)).Classification);
    }

    [Fact]
    public void JogWhileAJobIsOpenButIdle_IsClassifiedAsJogged()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed();
        policy.Observe(feed.At(10, 0, 5));
        var jog = feed.At(12, 0, 5, status: MachineStatus.Idle);
        jog.JobActive = true;
        Assert.Equal(TrailClass.Jogged, policy.Observe(jog).Classification);
    }

    [Fact]
    public void MotionBeforeTheFirstLayer_IsStartupTravel()
    {
        var policy = new TrailContinuityPolicy();
        var feed = new Feed { Layer = 0 };
        Assert.Equal(TrailClass.StartupOrFooter, policy.Observe(feed.At(10, 0, 400)).Classification);
    }

    // ---- the no-editing guarantee ---------------------------------------------------

    [Fact]
    public void ThePolicyNeverChangesAnObservation()
    {
        var policy = new TrailContinuityPolicy(null, Line());
        var feed = new Feed();
        var observations = new List<TrailObservation>
        {
            feed.At(10, 0, 5),
            feed.At(20, 40, 5),                    // far off the path
            feed.At(0, 0, 0, valid: false),        // invalid
            feed.At(300, 0, 5),                    // implausible speed
        };
        var before = observations.ConvertAll(o => o.Position);
        foreach (var o in observations) policy.Observe(o);
        for (int i = 0; i < observations.Count; i++)
        {
            Assert.Equal(before[i].X, observations[i].Position.X, 12);
            Assert.Equal(before[i].Y, observations[i].Position.Y, 12);
            Assert.Equal(before[i].Z, observations[i].Position.Z, 12);
        }
    }

    // ---- the same-Z reference -------------------------------------------------------

    [Fact]
    public void SameZIndex_IgnoresANearerPlaceOnAnotherLayer()
    {
        // The intended layer runs along X at Z=5 and is 12 mm away in Y; the NEXT layer at
        // Z=9 passes right under the sample. A 3D nearest-distance metric would answer 4 mm
        // and hide the within-layer excursion, so the index must not consider it.
        var segments = new List<LivePathSegment>();
        for (int i = 0; i < 50; i++)
            segments.Add(new LivePathSegment(new Vec3(i, 0, 5), new Vec3(i + 1, 0, 5)));
        for (int i = 0; i < 50; i++)
            segments.Add(new LivePathSegment(new Vec3(i, 12, 9), new Vec3(i + 1, 12, 9)));
        var index = new SameZPathIndex(segments);

        Assert.True(index.TryDistanceMm(new Vec3(20, 12, 5), out double d));
        Assert.Equal(12.0, d, 6);

        var solid = new LivePathDeviation(segments);
        var nearest3d = solid.Query(new Vec3(20, 12, 5));
        Assert.True(nearest3d.Available);
        Assert.Equal(4.0, nearest3d.DistanceMm, 6);
    }

    [Fact]
    public void SameZIndex_ReportsUnavailableAwayFromEveryLayer()
    {
        var index = Line();
        Assert.False(index.TryDistanceMm(new Vec3(20, 0, 900), out double d));
        Assert.True(double.IsNaN(d));
    }

    [Fact]
    public void SameZIndex_AbsorbsTheReportedRoundingWithinItsWindow()
    {
        // The dome telemetry reports Z=42.598 for a layer the G-code writes as Z42.597.
        var segments = new List<LivePathSegment>
        {
            new LivePathSegment(new Vec3(0, 0, 42.597), new Vec3(100, 0, 42.597)),
        };
        var index = new SameZPathIndex(segments);
        Assert.True(index.TryDistanceMm(new Vec3(50, 3, 42.598), out double d));
        Assert.Equal(3.0, d, 3);
    }

    [Fact]
    public void EmptyReference_IsUnavailableNotZero()
    {
        var index = new SameZPathIndex(Array.Empty<LivePathSegment>());
        Assert.False(index.Valid);
        Assert.False(index.TryDistanceMm(new Vec3(0, 0, 0), out _));
    }
}
