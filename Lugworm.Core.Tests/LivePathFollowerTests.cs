using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The 2026-09-16 display model: the arm presumes the machine executes its file.
/// A reading on the intended path is progress; a reading off it is held; only a
/// deviation that persists is shown as measured.</summary>
public class LivePathFollowerTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    static DateTime At(double seconds) => T0.AddSeconds(seconds);
    static Vec3 V(double x, double y = 0, double z = 0) => new Vec3(x, y, z);
    static LivePathDeviation Line(params Vec3[] pts) => LivePathDeviation.FromPolyline(pts);

    static LivePathFollowResult Feed(LivePathFollower f, LivePathDeviation path, Vec3 p, double t, bool executing = true)
        => f.Observe(p, At(t), executing, path.Query(p));

    /// <summary>A Z hop: up 7, across 27, down 7, then 23 along the bed. Length 64.</summary>
    static LivePathDeviation Hop() => Line(V(0, 0, 0), V(0, 0, 7), V(27, 0, 7), V(27, 0, 0), V(50, 0, 0));

    static void AssertClose(Vec3 expected, Vec3 actual, double tol = 1e-9)
        => Assert.True(expected.DistanceTo(actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void Arc_length_parametrization_maps_progress_back_onto_the_route()
    {
        var path = Hop();
        Assert.Equal(64, path.Length, 9);
        AssertClose(V(0, 0, 3.5), path.PointAt(3.5));
        AssertClose(V(13.5, 0, 7), path.PointAt(20.5));
        AssertClose(V(27, 0, 0), path.PointAt(41));
        AssertClose(V(50, 0, 0), path.PointAt(100));   // clamped to the end
        AssertClose(V(0, 0, 0), path.PointAt(-1));     // and to the start

        var q = path.Query(V(13.5, 0, 9));
        Assert.True(q.Available);
        Assert.Equal(2, q.DistanceMm, 9);
        Assert.Equal(20.5, q.PathPosition, 9);
        AssertClose(V(13.5, 0, 7), q.Point);

        // A window that excludes the segment the point is nearest to in space finds the
        // nearest place INSIDE the window instead: that is what keeps a neighbouring layer
        // out of the running.
        var w = path.Locate(V(13.5, 0, 9), 40, 64);
        Assert.True(w.Available);
        Assert.Equal(40, w.PathPosition, 9);
        Assert.Equal(Math.Sqrt(13.5 * 13.5 + 64), w.DistanceMm, 9);   // (27, 0, 1): the window edge, not the corner outside it
        Assert.False(path.Locate(V(1, 0, 0), 70, 80).Available);   // empty window past the end
        Assert.True(path.Continuous(0, 60));

        var broken = LivePathDeviation.FromPolyline(
            new[] { V(0), V(10), V(50), V(60) }, new[] { true, false, true, false });
        Assert.Equal(20, broken.Length, 9);          // the uncommanded jump adds no length
        Assert.True(broken.Continuous(0, 9));
        Assert.False(broken.Continuous(5, 15));       // the break lies between
        AssertClose(V(55), broken.PointAt(15));
    }

    [Fact]
    public void Readings_on_the_path_become_progress_and_off_path_readings_hold()
    {
        var path = Line(V(0), V(100));
        var f = new LivePathFollower(path);
        Assert.True(f.HasPath);
        Assert.True(double.IsNaN(f.Position));

        var r1 = Feed(f, path, V(10), 0);
        Assert.Equal(LivePathFollowMode.OnPath, r1.Mode);
        Assert.Equal(10, r1.PathPosition, 9);
        AssertClose(V(10), r1.Point);
        Assert.False(r1.Relocated);

        Assert.Equal(20, Feed(f, path, V(20), .25).PathPosition, 9);
        Assert.Equal(40, f.SpeedMmPerS, 9);

        var off = Feed(f, path, V(30, 20), .5);
        Assert.Equal(LivePathFollowMode.Pending, off.Mode);
        Assert.True(double.IsNaN(off.PathPosition));
        Assert.Equal(20, off.DistanceMm, 9);
        Assert.Equal(20, f.Position, 9);            // nothing moved

        var back = Feed(f, path, V(40), .75);
        Assert.Equal(LivePathFollowMode.OnPath, back.Mode);
        Assert.Equal(40, back.PathPosition, 9);
        Assert.False(back.Relocated);

        // A reading that projects BEHIND the last fix (one axis glitching along a straight
        // run) holds the progress rather than running the file backwards.
        var behind = Feed(f, path, V(25), 1.0);
        Assert.Equal(LivePathFollowMode.OnPath, behind.Mode);
        Assert.Equal(40, behind.PathPosition, 9);
        AssertClose(V(40), behind.Point);
        Assert.Equal(50, Feed(f, path, V(50), 1.25).PathPosition, 9);
    }

    [Fact]
    public void A_sustained_deviation_is_shown_measured_and_a_return_resumes_on_the_path()
    {
        var path = Line(V(0), V(100));
        var f = new LivePathFollower(path);
        Assert.Equal(LivePathFollowMode.OnPath, Feed(f, path, V(10), 0).Mode);
        for (int i = 1; i <= 6; i++)
            Assert.Equal(LivePathFollowMode.Pending, Feed(f, path, V(10, 30), i * .25).Mode);
        var shown = Feed(f, path, V(10, 30), 1.75);
        Assert.Equal(LivePathFollowMode.Deviating, shown.Mode);
        AssertClose(V(10, 30), shown.Point);
        Assert.Equal(30, shown.DistanceMm, 9);
        Assert.Equal(LivePathFollowMode.Deviating, f.Mode);
        Assert.Equal(LivePathFollowMode.Deviating, Feed(f, path, V(12, 30), 2.0).Mode);

        var resumed = Feed(f, path, V(60), 2.25);
        Assert.Equal(LivePathFollowMode.OnPath, resumed.Mode);
        Assert.Equal(60, resumed.PathPosition, 9);
        Assert.False(resumed.Relocated);
    }

    [Fact]
    public void Persistently_reporting_from_elsewhere_on_the_path_relocates_instead_of_deviating()
    {
        var path = Line(V(0), V(1000));
        var f = new LivePathFollower(path);
        Feed(f, path, V(10), 0);
        for (int i = 1; i <= 6; i++)
            Assert.Equal(LivePathFollowMode.Pending, Feed(f, path, V(600), i * .25).Mode);   // far outside the window
        var moved = Feed(f, path, V(600), 1.75);
        Assert.Equal(LivePathFollowMode.OnPath, moved.Mode);
        Assert.True(moved.Relocated);
        Assert.Equal(600, moved.PathPosition, 9);
        Assert.False(Feed(f, path, V(610), 2.0).Relocated);
    }

    [Fact]
    public void An_implausible_advance_is_deferred_until_the_next_reading_corroborates_it()
    {
        var path = Line(V(0), V(1000));
        var f = new LivePathFollower(path);
        Feed(f, path, V(10), 0);
        Feed(f, path, V(20), .25);                    // rate 40 mm/s: allowance (80 + 20) * .25 + 5 = 30 mm
        var big = Feed(f, path, V(80), .5);           // 60 mm ahead in one poll
        Assert.Equal(LivePathFollowMode.Deferred, big.Mode);
        Assert.Equal(20, f.Position, 9);              // not adopted yet
        var next = Feed(f, path, V(90), .75);         // at or beyond it: corroborated
        Assert.Equal(LivePathFollowMode.OnPath, next.Mode);
        Assert.True(next.HasDeferred);
        Assert.Equal(80, next.DeferredPathPosition, 9);
        AssertClose(V(80), next.DeferredPoint);
        Assert.Equal(90, next.PathPosition, 9);
        Assert.Equal(40, f.SpeedMmPerS, 9);

        // A queued move's target, or a glitch that landed on a later pass: the next reading
        // is back near the old fix, so the deferred one is dropped and never posed.
        var leak = Feed(f, path, V(150), 1.0);
        Assert.Equal(LivePathFollowMode.Deferred, leak.Mode);
        var truth = Feed(f, path, V(100), 1.25);
        Assert.Equal(LivePathFollowMode.OnPath, truth.Mode);
        Assert.False(truth.HasDeferred);
        Assert.Equal(100, truth.PathPosition, 9);
        Assert.Equal(LivePathFollowMode.OnPath, f.Mode);
    }

    [Fact]
    public void A_telemetry_gap_relocates_over_the_whole_path()
    {
        var path = Line(V(0), V(1000));
        var f = new LivePathFollower(path);
        Feed(f, path, V(10), 0);
        var after = Feed(f, path, V(500), 5.0);   // longer than RelocateAfterSeconds
        Assert.Equal(LivePathFollowMode.OnPath, after.Mode);
        Assert.True(after.Relocated);
        Assert.Equal(500, after.PathPosition, 9);
    }

    [Fact]
    public void Not_executing_readings_are_measured_without_persistence()
    {
        var path = Line(V(0), V(100));
        var f = new LivePathFollower(path);
        Assert.Equal(LivePathFollowMode.OnPath, Feed(f, path, V(10), 0, executing: false).Mode);
        var paused = Feed(f, path, V(10, 30), .25, executing: false);
        Assert.Equal(LivePathFollowMode.Measured, paused.Mode);
        AssertClose(V(10, 30), paused.Point);
        // The persistence clock only runs while the file executes.
        Assert.Equal(LivePathFollowMode.Pending, Feed(f, path, V(10, 30), .5).Mode);

        var none = new LivePathFollower(null);
        Assert.False(none.HasPath);
        Assert.Equal(LivePathFollowMode.Measured, none.Observe(V(1), At(0), true, LivePathDeviationResult.Unavailable()).Mode);
    }

    [Fact]
    public void Timeline_moves_the_arm_along_the_route_between_anchored_samples()
    {
        var path = Hop();
        var t = new LivePoseTimeline { Path = path };
        t.Add(new LivePoseSample(1, 1, 0, 0, path.PointAt(0), true, 0));
        t.Add(new LivePoseSample(2, 1, 1000, 1000, path.PointAt(64), true, 64));
        var frame = t.Evaluate(600, true, 100);   // t = 500 -> halfway in progress
        Assert.True(frame.Smoothed);
        Assert.True(frame.OnPath);
        Assert.Equal(32, frame.PathPosition, 9);
        AssertClose(V(25, 0, 7), frame.Position);   // on top of the hop, not on the chord (25, 0, 0)

        // Two anchored samples may be up to three seconds apart; measured ones keep the
        // one-second bound.
        var far = new LivePoseTimeline { Path = path };
        far.Add(new LivePoseSample(1, 1, 0, 0, path.PointAt(0), true, 0));
        far.Add(new LivePoseSample(2, 1, 2500, 2500, path.PointAt(64), true, 64));
        Assert.True(far.Evaluate(1350, true, 100).Smoothed);
        var measured = new LivePoseTimeline { Path = path };
        measured.Add(new LivePoseSample(1, 1, 0, 0, V(0), true));
        measured.Add(new LivePoseSample(2, 1, 2500, 2500, V(50), true));
        Assert.False(measured.Evaluate(1350, true, 100).Smoothed);

        // A measured sample on either side of the pair is a straight chord as before.
        var mixed = new LivePoseTimeline { Path = path };
        mixed.Add(new LivePoseSample(1, 1, 0, 0, path.PointAt(0), true, 0));
        mixed.Add(new LivePoseSample(2, 1, 1000, 1000, V(50, 0, 0), true));
        var chord = mixed.Evaluate(600, true, 100);
        Assert.True(chord.Smoothed);
        Assert.False(chord.OnPath);
        AssertClose(V(25, 0, 0), chord.Position);

        // Progress cannot be commanded backwards: such a pair holds the earlier sample.
        var backwards = new LivePoseTimeline { Path = path };
        backwards.Add(new LivePoseSample(1, 1, 0, 0, path.PointAt(50), true, 50));
        backwards.Add(new LivePoseSample(2, 1, 1000, 1000, path.PointAt(40), true, 40));
        var held = backwards.Evaluate(600, true, 100);
        Assert.False(held.Smoothed);
        Assert.Equal(50, held.PathPosition, 9);
    }

    [Fact]
    public void Timeline_delay_covers_two_poll_intervals()
    {
        var t = new LivePoseTimeline();
        for (int i = 0; i < 6; i++) t.Add(new LivePoseSample(i + 1, 1, i * 250, i * 250, V(i), true));
        Assert.Equal(250, t.IntervalP95Ms, 9);
        Assert.Equal(520, t.AutoDelayMs, 9);
        // At the moment the newest sample arrives the drawn pose still lies inside the
        // previous bracket, so one held reading never surfaces as a jump.
        var frame = t.Evaluate(1250, true);
        Assert.True(frame.Smoothed);
        Assert.InRange(frame.Position.X, 2.9, 3.0);
    }

    static LivePathDeviation FirstTwoLayers(string dir)
    {
        var profile = RobotCatalog.Get("potterbot-scara-v4");
        var program = GcodeParser.Parse(File.ReadAllLines(Path.Combine(dir, "first-two-layers.gcode")),
            profile.CreateScaraLimits().CalibratedHomePose);
        var sampled = MoveSampler.SampleWithBreaks(program.Moves);
        return LivePathDeviation.FromPolyline(sampled.pts, sampled.breakBefore);
    }

    static IEnumerable<(string acq, DateTime utc, Vec3 machine)> Rows(string file)
    {
        foreach (var line in File.ReadAllLines(file).Skip(1))
        {
            var r = line.Split(',').Select(s => s.Trim('"')).ToArray();
            yield return (r[0], DateTime.Parse(r[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                new Vec3(double.Parse(r[2], CultureInfo.InvariantCulture), double.Parse(r[3], CultureInfo.InvariantCulture),
                         double.Parse(r[4], CultureInfo.InvariantCulture)));
        }
    }

    [Fact]
    public void Recorded_seam_excursion_is_held_on_the_path_and_never_shown_as_a_deviation()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "seam-20260916");
        var path = FirstTwoLayers(dir);
        var f = new LivePathFollower(path);
        int onPath = 0, pending = 0, deferred = 0;
        double progress = double.NegativeInfinity;
        foreach (var (acq, utc, machine) in Rows(Path.Combine(dir, "seam.csv")))
        {
            var r = f.Observe(machine, utc, true, path.Query(machine));
            Assert.NotEqual(LivePathFollowMode.Deviating, r.Mode);
            Assert.NotEqual(LivePathFollowMode.Measured, r.Mode);
            if (acq == "790" || acq == "791") Assert.Equal(LivePathFollowMode.Pending, r.Mode);
            if (r.Mode == LivePathFollowMode.Pending) { pending++; continue; }
            if (r.Mode == LivePathFollowMode.Deferred) { deferred++; continue; }
            onPath++;
            Assert.False(r.Relocated);
            if (r.HasDeferred)
            {
                Assert.True(r.DeferredPathPosition >= progress, $"deferred progress ran backwards at acquisition {acq}");
                progress = r.DeferredPathPosition;
            }
            Assert.True(r.PathPosition >= progress, $"progress ran backwards at acquisition {acq}");
            progress = r.PathPosition;
            Assert.True(path.Query(r.Point).DistanceMm < 1e-6, "the posed point must lie on the path");
        }
        Assert.Equal(2, pending);
        Assert.Equal(1, deferred);    // the layer-2 start reported 48 mm early, then corroborated
        Assert.True(onPath >= 20);
    }

    [Fact]
    public void Layer_two_capture_follows_the_seam_dwell_and_the_hop_without_leaving_the_path()
    {
        // Run 20260916-124703-99cbd9, acquisitions 450-544: the end of layer 1, the seam of
        // layer 2 with its two off-contour readings and a Z that reads a layer high, a
        // one-axis glitch along a straight run, and the Z hop into layer 3 sampled once at
        // its top. The operator saw the arm vanish at the seam and spike at the hop.
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "seam-20260916");
        var path = FirstTwoLayers(dir);
        var f = new LivePathFollower(path);
        var timeline = new LivePoseTimeline { Path = path };
        var modes = new Dictionary<string, LivePathFollowMode>();
        double progress = double.NegativeInfinity;
        double clock = 0, heldClock = double.NaN;
        long seq = 0;
        DateTime first = default;
        foreach (var (acq, utc, machine) in Rows(Path.Combine(dir, "layer2-20260916.csv")))
        {
            if (first == default) first = utc;
            var r = f.Observe(machine, utc, true, path.Query(machine));
            modes[acq] = r.Mode;
            Assert.NotEqual(LivePathFollowMode.Deviating, r.Mode);
            double now = (utc - first).TotalMilliseconds;
            if (r.Mode == LivePathFollowMode.Deferred) { heldClock = now; continue; }
            if (r.Mode != LivePathFollowMode.OnPath) continue;
            Assert.False(r.Relocated, $"relocated at acquisition {acq}");
            if (r.HasDeferred)
            {
                Assert.True(r.DeferredPathPosition >= progress, $"deferred progress ran backwards at acquisition {acq}");
                progress = r.DeferredPathPosition;
                Assert.True(timeline.Add(new LivePoseSample(++seq, 1, heldClock, heldClock, r.DeferredPoint, true, r.DeferredPathPosition)));
            }
            Assert.True(r.PathPosition >= progress, $"progress ran backwards at acquisition {acq}");
            progress = r.PathPosition;
            clock = now;
            Assert.True(timeline.Add(new LivePoseSample(++seq, 1, clock, clock, r.Point, true, r.PathPosition)));
        }
        Assert.Equal(LivePathFollowMode.Deferred, modes["460"]);   // the layer start reported 48 mm early
        Assert.Equal(LivePathFollowMode.OnPath, modes["461"]);     // and corroborated by the next reading
        Assert.Equal(LivePathFollowMode.Pending, modes["464"]);
        Assert.Equal(LivePathFollowMode.Pending, modes["465"]);
        Assert.Equal(LivePathFollowMode.Pending, modes["511"]);    // one axis 45 mm off, landing near a later pass
        Assert.Equal(LivePathFollowMode.OnPath, modes["540"]);     // the hop's top, on the travel route
        Assert.Equal(LivePathFollowMode.OnPath, modes["541"]);
        Assert.True(modes.Values.Count(m => m == LivePathFollowMode.Pending) <= 6);
        Assert.True(modes.Values.Count(m => m == LivePathFollowMode.Deferred) <= 3);

        // Every frame the display would have drawn lies on the intended path: no chord
        // through the hop, no excursion, no hidden arm.
        for (double now = 600; now <= clock; now += 33)
        {
            var frame = timeline.Evaluate(now, true);
            Assert.True(frame.Visible);
            Assert.True(frame.OnPath);
            Assert.True(path.Query(frame.Position).DistanceMm < 1e-6, $"frame at {now} ms left the path");
        }
    }
}
