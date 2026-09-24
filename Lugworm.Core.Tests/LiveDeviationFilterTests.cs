using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public class LiveDeviationFilterTests
{
    static readonly DateTime Start = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Brief_excursion_holds_the_pose_on_the_path_and_bridges_it_without_a_jump()
    {
        // 2026-09-16: a held reading used to invalidate the timeline, which hid the arm at
        // every seam. Now nothing is added for it, the last on-path pose stays drawn, and
        // the next on-path reading is interpolated ALONG the path across the gap.
        var path = LivePathDeviation.FromPolyline(new[] { new Vec3(0, 0, 0), new Vec3(100, 0, 0) });
        var f = new LivePathFollower(path);
        var timeline = new LivePoseTimeline { Path = path };
        var first = f.Observe(new Vec3(10, 0, 0), Start, true, path.Query(new Vec3(10, 0, 0)));
        Assert.Equal(LivePathFollowMode.OnPath, first.Mode);
        timeline.Add(new LivePoseSample(1, 1, 0, 10, first.Point, true, first.PathPosition));
        for (int i = 1; i <= 4; i++)
        {
            var off = new Vec3(10 + 5 * i, 20, 0);
            Assert.Equal(LivePathFollowMode.Pending, f.Observe(off, Start.AddSeconds(i * .25), true, path.Query(off)).Mode);
            var frame = timeline.Evaluate(i * 250, true);
            Assert.True(frame.Visible);                 // the arm never disappears
            Assert.Equal(10, frame.Position.X, 9);      // and holds where the file had got to
        }
        var back = f.Observe(new Vec3(35, 0, 0), Start.AddSeconds(1.25), true, path.Query(new Vec3(35, 0, 0)));
        Assert.Equal(LivePathFollowMode.OnPath, back.Mode);
        Assert.Equal(35, back.PathPosition, 9);
        timeline.Add(new LivePoseSample(6, 1, 1250, 1260, back.Point, true, back.PathPosition));
        var bridged = timeline.Evaluate(1000, true, 100);   // t = 900, inside the 1250 ms gap
        Assert.True(bridged.Smoothed);                      // bridged along the path, no hold-and-jump
        Assert.True(bridged.OnPath);
        Assert.Equal(10 + 25 * (890.0 / 1250.0), bridged.Position.X, 9);
        Assert.False(bridged.Position.Y != 0);              // never through the rejected readings
    }

    [Fact]
    public void Continuous_deviation_is_exposed_after_one_and_a_half_seconds_and_recovers()
    {
        var f = new LiveDeviationFilter();
        for (int i = 0; i < 6; i++)
            Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(i * .25), 15));
        Assert.Equal(LiveDeviationState.Confirmed, f.Observe(Start.AddSeconds(1.5), 15));
        Assert.Equal(LiveDeviationState.Confirmed, f.Observe(Start.AddSeconds(1.75), 15));
        Assert.Equal(LiveDeviationState.Normal, f.Observe(Start.AddSeconds(2), 1));
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(2.25), 15));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(5)]
    public void Unknown_or_close_reading_clears_pending_evidence(double distance)
    {
        var f = new LiveDeviationFilter();
        f.Observe(Start, 20);
        Assert.Equal(LiveDeviationState.Normal, f.Observe(Start.AddSeconds(.25), distance));
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(.5), 20));
    }

    [Fact]
    public void Gaps_repeated_or_backward_times_and_run_resets_cannot_confirm_a_deviation()
    {
        var f = new LiveDeviationFilter();
        f.Observe(Start, 20);
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(2), 20));
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(2), 20));
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start, 20));
        f.Reset();
        Assert.Equal(LiveDeviationState.Pending, f.Observe(Start.AddSeconds(3), 20));
        Assert.Equal(LiveDeviationState.Normal, f.Observe(default, 20));
    }

    [Fact]
    public void Travel_and_vertical_hops_are_included_in_the_reference()
    {
        var pts = new[] { new Vec3(0, 0, 0), new Vec3(0, 0, 10), new Vec3(50, 0, 10), new Vec3(50, 0, 3) };
        var path = LivePathDeviation.FromPolyline(pts);
        var f = new LiveDeviationFilter();
        foreach (var p in new[] { new Vec3(0, 0, 5), new Vec3(25, 0, 10), new Vec3(50, 0, 6) })
            Assert.Equal(LiveDeviationState.Normal, f.Observe(Start, path.Query(p).DistanceMm));
        Assert.False(TrailChordPolicy.Evaluate(3, 2, .25).Drawable); // still no invented sparse chord
    }

    [Fact]
    public void Newest_capture_suppresses_four_brief_outliers_and_keeps_two_hundred_samples()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "seam-20260916");
        var segments = File.ReadAllLines(Path.Combine(dir, "latest-reference.csv")).Skip(1)
            .Select(line => line.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray())
            .Select(v => new LivePathSegment(new Vec3(v[0], v[1], v[2]), new Vec3(v[3], v[4], v[5]))).ToArray();
        var reference = new LivePathDeviation(segments);
        var f = new LiveDeviationFilter();
        int pending = 0, normal = 0;
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "latest-polls.csv")).Skip(1))
        {
            var r = line.Split(',');
            var p = new Vec3(double.Parse(r[2], CultureInfo.InvariantCulture),
                double.Parse(r[3], CultureInfo.InvariantCulture), double.Parse(r[4], CultureInfo.InvariantCulture));
            var q = reference.Query(p);
            Assert.True(q.Available);
            var state = f.Observe(DateTime.Parse(r[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), q.DistanceMm);
            Assert.NotEqual(LiveDeviationState.Confirmed, state);
            if (state == LiveDeviationState.Pending) pending++; else normal++;
        }
        Assert.Equal(4, pending);
        Assert.Equal(200, normal);
    }

    [Fact]
    public void Recorded_seam_excursion_is_suppressed_without_suppressing_surrounding_layer_motion()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "seam-20260916");
        var profile = RobotCatalog.Get("potterbot-scara-v4");
        var program = GcodeParser.Parse(File.ReadAllLines(Path.Combine(dir, "first-two-layers.gcode")),
            profile.CreateScaraLimits().CalibratedHomePose);
        var sampled = MoveSampler.SampleWithBreaks(program.Moves);
        var path = LivePathDeviation.FromPolyline(sampled.pts, sampled.breakBefore);
        var f = new LiveDeviationFilter();
        int pending = 0, normal = 0;
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "seam.csv")).Skip(1))
        {
            var row = line.Split(',').Select(s => s.Trim('"')).ToArray();
            var p = new Vec3(double.Parse(row[2], CultureInfo.InvariantCulture),
                double.Parse(row[3], CultureInfo.InvariantCulture), double.Parse(row[4], CultureInfo.InvariantCulture));
            var comparison = path.Query(p);
            Assert.True(comparison.Available);
            var state = f.Observe(DateTime.Parse(row[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), comparison.DistanceMm);
            Assert.NotEqual(LiveDeviationState.Confirmed, state);
            if (state == LiveDeviationState.Pending) pending++; else normal++;
            if (row[0] == "790" || row[0] == "791") Assert.Equal(LiveDeviationState.Pending, state);
            if (row[0] == "779" || row[0] == "793") Assert.Equal(LiveDeviationState.Normal, state);
        }
        Assert.True(pending >= 2);
        Assert.True(normal >= 20);
    }
}
