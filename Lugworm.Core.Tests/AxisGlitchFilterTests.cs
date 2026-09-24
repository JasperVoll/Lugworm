using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests
{
    /// <summary>The single-axis machinePosition glitch of the 2026-09-09 13:17 dome run: the
    /// firmware reply carries one Cartesian axis 15-53 mm off the contour while the same
    /// reply's userPosition sits on it. The filter repairs from that witness with one poll of
    /// look-ahead and leaves the ordinary "userPosition leads a long move" alone.</summary>
    public class AxisGlitchFilterTests
    {
        static readonly DateTime T0 = new DateTime(2026, 9, 9, 11, 18, 0, DateTimeKind.Utc);

        static ObjectModelSnapshot Snap(long acq, double ms, Vec3 machine, Vec3 user, MachineStatus status = MachineStatus.Processing)
        {
            var received = T0.AddMilliseconds(ms);
            return new ObjectModelSnapshot
            {
                Status = status,
                AllHomed = true,
                RequestedSpeed = 43,
                Axes = new[]
                {
                    new AxisState { Letter = "X", MachinePosition = machine.X, UserPosition = user.X, Homed = true, Min = -10000, Max = 10000 },
                    new AxisState { Letter = "Y", MachinePosition = machine.Y, UserPosition = user.Y, Homed = true, Min = -10000, Max = 10000 },
                    new AxisState { Letter = "Z", MachinePosition = machine.Z, UserPosition = user.Z, Homed = true, Min = 0, Max = 400 },
                },
                Acquisition = new AcquisitionStamp
                {
                    AcquisitionId = acq,
                    RequestStartedUtc = received.AddMilliseconds(-14),
                    ResponseReceivedUtc = received,
                    ConnectionGeneration = 1,
                },
            };
        }

        static List<GlitchResolution> Drain(AxisGlitchFilter f, params ObjectModelSnapshot[] snaps)
        {
            var all = new List<GlitchResolution>();
            foreach (var s in snaps) all.AddRange(f.Push(s));
            return all;
        }

        [Fact]
        public void A_self_consistent_reply_passes_through_at_once()
        {
            var f = new AxisGlitchFilter();
            var s = Snap(1, 0, new Vec3(100, -200, 5), new Vec3(100.1, -200.2, 5));
            var r = f.Push(s);
            Assert.Single(r);
            Assert.Same(s, r[0].Snapshot);
            Assert.Same(s, r[0].RawSnapshot);
            Assert.False(r[0].WasCorrected);   // nothing to correct, so no copy is made
            Assert.Equal(GlitchVerdict.None, r[0].Verdict);
            Assert.False(f.Holding);
            Assert.Null(s.PositionRepair);
        }

        [Fact]
        public void A_single_axis_disagreement_is_held_and_repaired_from_the_witness_when_the_successor_agrees()
        {
            var f = new AxisGlitchFilter();
            var a = Snap(1, 0, new Vec3(224.7, -236.6, 0), new Vec3(224.7, -236.6, 0));
            var b = Snap(2, 250, new Vec3(199.144, -234.225, 0), new Vec3(229.483, -234.257, 0));   // acquisition 412
            var c = Snap(3, 500, new Vec3(234.4, -232.0, 0), new Vec3(234.4, -232.0, 0));

            Assert.Single(f.Push(a));
            Assert.Empty(f.Push(b));
            Assert.True(f.Holding);
            var released = f.Push(c);
            Assert.Equal(2, released.Count);
            // The repair rides on the released VIEW of b; b itself stays as it was polled.
            Assert.Same(b, released[0].RawSnapshot);
            Assert.Same(c, released[1].Snapshot);
            Assert.Equal(GlitchVerdict.Repaired, released[0].Verdict);
            Assert.Equal("X", released[0].Axis);
            Assert.Equal(199.144, released[0].RawValue, 3);
            Assert.Equal(229.483, released[0].UsedValue, 3);
            var view = released[0].Snapshot;
            Assert.Equal(229.483, view.MachinePosition!.Value.X, 3);
            Assert.Equal(-234.225, view.MachinePosition!.Value.Y, 3);   // the exact axis is untouched
            Assert.Contains("X machinePosition 199.144 replaced by userPosition 229.483", view.PositionRepair);
            Assert.Equal(1, f.Repaired);
            Assert.Equal(GlitchVerdict.None, released[1].Verdict);
        }

        /// <summary>The filter is handed snapshots the session has already published, so a
        /// repair must never be a write into one: the raw reading is the evidence the
        /// 2026-09-09 false-position investigation compares the filtered one against.</summary>
        [Fact]
        public void Filtering_never_writes_into_the_snapshot_it_was_given()
        {
            var f = new AxisGlitchFilter();
            var a = Snap(1, 0, new Vec3(224.7, -236.6, 0), new Vec3(224.7, -236.6, 0));
            var b = Snap(2, 250, new Vec3(199.144, -234.225, 0), new Vec3(229.483, -234.257, 0));
            var c = Snap(3, 500, new Vec3(234.4, -232.0, 0), new Vec3(234.4, -232.0, 0));
            f.Push(a);
            f.Push(b);
            var repaired = f.Push(c)[0];

            // b, as polled: every axis still carries exactly what the firmware reported.
            Assert.Equal(199.144, b.Axis("X")!.MachinePosition, 6);
            Assert.Equal(229.483, b.Axis("X")!.UserPosition, 6);
            Assert.Equal(-234.225, b.Axis("Y")!.MachinePosition, 6);
            Assert.Equal(199.144, b.MachinePosition!.Value.X, 6);
            Assert.Null(b.PositionRepair);

            // the released view: a different object carrying the correction and the note.
            Assert.True(repaired.WasCorrected);
            Assert.Same(b, repaired.RawSnapshot);
            Assert.NotSame(b, repaired.Snapshot);
            Assert.NotSame(b.Axes, repaired.Snapshot.Axes);
            Assert.Equal(229.483, repaired.Snapshot.Axis("X")!.MachinePosition, 6);
            Assert.Equal(229.483, repaired.Snapshot.Axis("X")!.UserPosition, 6);   // the witness is kept
            Assert.NotNull(repaired.Snapshot.PositionRepair);
            // everything the copy does not correct comes across untouched
            Assert.Equal(b.Status, repaired.Snapshot.Status);
            Assert.Equal(b.AllHomed, repaired.Snapshot.AllHomed);
            Assert.Equal(b.RequestedSpeed, repaired.Snapshot.RequestedSpeed);
            Assert.Equal(b.AcquisitionId, repaired.Snapshot.AcquisitionId);
            Assert.Same(b.Acquisition, repaired.Snapshot.Acquisition);
            Assert.Equal(b.PositionValidity, repaired.Snapshot.PositionValidity);
            Assert.Equal(-234.225, repaired.Snapshot.MachinePosition!.Value.Y, 6);

            // a disagreement nothing corroborates is flagged on the view, not on the reply.
            var g = new AxisGlitchFilter();
            var s2 = Snap(2, 250, new Vec3(130, -195, 5), new Vec3(150, -195, 5));
            var suspect = Drain(g,
                Snap(1, 0, new Vec3(100, -200, 5), new Vec3(100, -200, 5)),
                s2,
                Snap(3, 500, new Vec3(120, -190, 5), new Vec3(120, -190, 5)))[1];
            Assert.Equal(GlitchVerdict.SuspectUnrepaired, suspect.Verdict);
            Assert.True(suspect.WasCorrected);   // the flag is a correction of the view too
            Assert.Null(s2.PositionRepair);
            Assert.Equal(130, s2.Axis("X")!.MachinePosition, 6);
            Assert.Contains("left as reported", suspect.Snapshot.PositionRepair);
            Assert.Equal(130, suspect.Snapshot.Axis("X")!.MachinePosition, 6);   // never repaired
        }

        [Fact]
        public void A_long_move_where_the_user_position_leads_is_not_repaired()
        {
            // Startup travel of the same capture: userPosition sits at the destination
            // (200, -500) for seconds while machinePosition advances - both axes disagree,
            // so it is not even a candidate; a single-axis travel (Z lift) IS a candidate
            // but its machine value sits between its neighbours and stays as reported.
            var f = new AxisGlitchFilter();
            var travel = Drain(f,
                Snap(1, 0, new Vec3(147.96, -491.99, 400), new Vec3(200, -500, 400)),
                Snap(2, 250, new Vec3(119.62, -484.86, 400), new Vec3(200, -500, 400)),
                Snap(3, 500, new Vec3(91.54, -475.64, 400), new Vec3(200, -500, 400)));
            Assert.Equal(3, travel.Count);
            Assert.All(travel, r => Assert.Equal(GlitchVerdict.None, r.Verdict));
            Assert.Equal(0, f.Candidates);

            var g = new AxisGlitchFilter();
            var lift = Drain(g,
                Snap(1, 0, new Vec3(200, -500, 396.19), new Vec3(200, -500, 380)),
                Snap(2, 250, new Vec3(200, -500, 397.43), new Vec3(200, -500, 380)),
                Snap(3, 500, new Vec3(200, -500, 398.68), new Vec3(200, -500, 380)));
            // the first has no neighbour behind it and passes; the second is held and then
            // released unrepaired (its machine value sits between its neighbours); the third
            // is held until the flush
            Assert.Equal(2, lift.Count);
            lift.AddRange(g.Flush());
            Assert.Equal(3, lift.Count);
            Assert.Equal(397.43, lift[1].Snapshot.MachinePosition!.Value.Z, 3);
            Assert.Equal(GlitchVerdict.None, lift[1].Verdict);
            Assert.Equal(GlitchVerdict.Unresolved, lift[2].Verdict);   // no successor for the last one
            Assert.Equal(0, g.Repaired);
            Assert.Equal(2, g.Candidates);   // the first had nothing behind it to bracket
        }

        [Fact]
        public void A_disagreement_without_an_agreeing_witness_is_flagged_and_left_as_reported()
        {
            var f = new AxisGlitchFilter();
            var released = Drain(f,
                Snap(1, 0, new Vec3(100, -200, 5), new Vec3(100, -200, 5)),
                Snap(2, 250, new Vec3(130, -195, 5), new Vec3(150, -195, 5)),   // machine 20 off, user 40 off
                Snap(3, 500, new Vec3(120, -190, 5), new Vec3(120, -190, 5)));
            Assert.Equal(3, released.Count);
            Assert.Equal(GlitchVerdict.SuspectUnrepaired, released[1].Verdict);
            Assert.Equal(130, released[1].Snapshot.MachinePosition!.Value.X, 3);
            Assert.Contains("left as reported", released[1].Snapshot.PositionRepair);
            Assert.Equal(1, f.Suspect);
        }

        [Fact]
        public void Flush_and_reset_release_a_held_candidate_untouched()
        {
            var f = new AxisGlitchFilter();
            f.Push(Snap(1, 0, new Vec3(100, -200, 5), new Vec3(100, -200, 5)));
            Assert.Empty(f.Push(Snap(2, 250, new Vec3(130, -195, 5), new Vec3(110, -195, 5))));
            var flushed = f.Flush();
            Assert.Single(flushed);
            Assert.Equal(GlitchVerdict.Unresolved, flushed[0].Verdict);
            Assert.Equal(130, flushed[0].Snapshot.MachinePosition!.Value.X, 3);
            Assert.False(f.Holding);
            f.Reset();
            // after a reset there is no previous neighbour, so nothing can be bracketed
            var r = f.Push(Snap(3, 500, new Vec3(130, -195, 5), new Vec3(110, -195, 5)));
            Assert.Single(r);
            Assert.Equal(GlitchVerdict.None, r[0].Verdict);
        }

        [Fact]
        public void The_continuity_policy_reports_a_repaired_sample_as_a_machine_position_glitch()
        {
            var policy = new TrailContinuityPolicy();
            TrailObservation Obs(long acq, double ms, Vec3 p, string? note = null) => new TrailObservation
            {
                AcquisitionId = acq,
                RequestUtc = T0.AddMilliseconds(ms - 14),
                ReceivedUtc = T0.AddMilliseconds(ms),
                ConnectionGeneration = 1,
                RunId = "run",
                PositionValid = true,
                Position = p,
                AllHomed = true,
                Status = MachineStatus.Processing,
                JobActive = true,
                RequestedSpeedMms = 43,
                FilePosition = acq * 100,
                RepairNote = note,
            };
            policy.Observe(Obs(1, 0, new Vec3(224.7, -236.6, 0)));
            var v = policy.Observe(Obs(2, 250, new Vec3(229.483, -234.257, 0), "X machinePosition 199.144 replaced by userPosition 229.483"));
            Assert.Equal(TrailQuality.MachinePositionGlitch, v.Quality);
            Assert.Contains("199.144", v.Reason);
            Assert.False(v.SegmentStarted);
            var w = policy.Observe(Obs(3, 500, new Vec3(234.4, -232.0, 0)));
            Assert.Equal(TrailQuality.Valid, w.Quality);
            Assert.Equal(v.SegmentId, w.SegmentId);
        }

        // ---- replay of the whole 13:17 capture ----------------------------------------

        static List<ObjectModelSnapshot> LoadCapture()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "dome-20260909", "polls-20260909-1317.csv");
            var list = new List<ObjectModelSnapshot>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("acq,", StringComparison.Ordinal)) continue;
                var c = line.Split(',');
                double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
                var received = DateTime.Parse(c[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                var requested = DateTime.Parse(c[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                var snap = new ObjectModelSnapshot
                {
                    Status = c[3] == "Processing" ? MachineStatus.Processing : c[3] == "Idle" ? MachineStatus.Idle : MachineStatus.Busy,
                    AllHomed = c[4] == "1",
                    RequestedSpeed = c[11].Length > 0 ? D(11) : (double?)null,
                    Axes = new[]
                    {
                        new AxisState { Letter = "X", MachinePosition = D(5), UserPosition = D(8), Homed = c[4] == "1", Min = -10000, Max = 10000 },
                        new AxisState { Letter = "Y", MachinePosition = D(6), UserPosition = D(9), Homed = c[4] == "1", Min = -10000, Max = 10000 },
                        new AxisState { Letter = "Z", MachinePosition = D(7), UserPosition = D(10), Homed = c[4] == "1", Min = 0, Max = 400 },
                    },
                    Acquisition = new AcquisitionStamp
                    {
                        AcquisitionId = long.Parse(c[0], CultureInfo.InvariantCulture),
                        RequestStartedUtc = requested,
                        ResponseReceivedUtc = received,
                        ConnectionGeneration = 1,
                    },
                };
                list.Add(snap);
            }
            return list;
        }

        /// <summary>Samples more than 5 mm off the chord between their neighbours, at the
        /// same elevation, during the print - the excursions the operator never saw the arm
        /// make. Neighbour artefacts are excluded by requiring the neighbours themselves to
        /// sit on THEIR chords, so one spike counts once.</summary>
        static int Excursions(IReadOnlyList<Vec3> pts, IReadOnlyList<bool> printing)
        {
            double Dev(int i)
            {
                var a = pts[i - 1]; var b = pts[i]; var c = pts[i + 1];
                double lx = c.X - a.X, ly = c.Y - a.Y, len2 = lx * lx + ly * ly;
                double t = len2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((b.X - a.X) * lx + (b.Y - a.Y) * ly) / len2));
                double px = a.X + t * lx, py = a.Y + t * ly;
                return Math.Sqrt((b.X - px) * (b.X - px) + (b.Y - py) * (b.Y - py));
            }
            int n = 0;
            for (int i = 2; i < pts.Count - 2; i++)
            {
                if (!printing[i]) continue;
                if (Math.Abs(pts[i - 1].Z - pts[i].Z) > 0.05 || Math.Abs(pts[i + 1].Z - pts[i].Z) > 0.05) continue;
                if (Dev(i) > 5 && Dev(i - 1) < 5 && Dev(i + 1) < 5) n++;
                else if (Dev(i) > 5 && (Dev(i - 1) > 5 || Dev(i + 1) > 5))
                {
                    // two adjacent large deviations: count the larger one once
                    if (Dev(i) >= Dev(i - 1) && Dev(i) > Dev(i + 1)) n++;
                }
            }
            return n;
        }

        [Fact]
        public void Replay_of_the_1317_capture_removes_the_single_axis_excursions_and_keeps_the_travel_moves()
        {
            var polls = LoadCapture();
            Assert.Equal(1132, polls.Count);
            var rawPts = polls.Select(p => p.MachinePosition!.Value).ToList();
            var printing = polls.Select(p => p.Status == MachineStatus.Processing && p.AllHomed
                                            && p.RequestedSpeed.HasValue && p.RequestedSpeed.Value >= 40).ToList();
            int before = Excursions(rawPts, printing);

            var filter = new AxisGlitchFilter();
            var released = new List<GlitchResolution>();
            foreach (var p in polls) released.AddRange(filter.Push(p));
            released.AddRange(filter.Flush());

            Assert.Equal(1132, released.Count);
            for (int i = 1; i < released.Count; i++)
                Assert.True(released[i].Snapshot.AcquisitionId > released[i - 1].Snapshot.AcquisitionId, "released out of order");

            var repairedPts = released.Select(r => r.Snapshot.MachinePosition!.Value).ToList();
            int after = Excursions(repairedPts, printing);

            // The capture itself is evidence: replaying it must leave all 1132 polls
            // reporting exactly what the firmware sent, or raw and filtered can no longer
            // be compared (rawPts was read before the replay ran).
            for (int i = 0; i < polls.Count; i++)
            {
                Assert.Null(polls[i].PositionRepair);
                Assert.Equal(rawPts[i].X, polls[i].MachinePosition!.Value.X, 6);
                Assert.Equal(rawPts[i].Y, polls[i].MachinePosition!.Value.Y, 6);
                Assert.Equal(rawPts[i].Z, polls[i].MachinePosition!.Value.Z, 6);
            }
            Assert.All(released, r => Assert.Equal(r.Verdict == GlitchVerdict.Repaired
                                               || r.Verdict == GlitchVerdict.SuspectUnrepaired, r.WasCorrected));

            // 24 distinct single-axis excursions by this count on 2026-09-09; the filter must
            // remove nearly all of them and must not invent any.
            Assert.True(before >= 20, "expected the raw capture to hold the excursions: " + before);
            Assert.True(after <= 3, "excursions left after the repair: " + after + " (before " + before + ", repaired " + filter.Repaired + ")");
            Assert.True(filter.Repaired >= 15, "repaired: " + filter.Repaired + " of " + filter.Candidates + " candidates");
            // One reply of the capture disagrees on one axis with neither its neighbours nor
            // its own witness; it is left as reported and flagged, never invented around.
            var suspects = released.Where(r => r.Verdict == GlitchVerdict.SuspectUnrepaired)
                                   .Select(r => r.Snapshot.AcquisitionId).ToList();
            Assert.True(suspects.Count <= 2, "left as reported: " + string.Join(", ", suspects));

            // The startup travel (user position parked at the destination) is untouched.
            foreach (var r in released.Where(r => r.Snapshot.AcquisitionId >= 245 && r.Snapshot.AcquisitionId <= 256))
                Assert.Equal(GlitchVerdict.None, r.Verdict);
            // Every repair names the axis and both raw values and uses the same reply's witness.
            foreach (var r in released.Where(r => r.Verdict == GlitchVerdict.Repaired))
            {
                Assert.Contains(r.Axis + " machinePosition", r.Snapshot.PositionRepair);
                var axis = r.Snapshot.Axis(r.Axis!);
                Assert.Equal(axis!.UserPosition, axis.MachinePosition, 6);
            }
        }
    }
}
