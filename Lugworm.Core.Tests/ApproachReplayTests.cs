using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests
{
    /// <summary>Replays the recorded approach window of run 20260909-131323-d30110 through
    /// the real parser and the real eligibility rule.</summary>
    /// <remarks>
    /// <para>Fixture: <c>fixtures/approach-20260909/acquisitions.json</c>, acquisitions 6424
    /// to 6491 inclusive, produced by
    /// <c>docs/dev/analysis/approach-20260909/extract_approach_window.py</c> from that run's
    /// <c>diagnostics.jsonl</c>. The job it came from is <c>job.gcode</c> SHA-256
    /// 4ec8f1a21f6116d4e6068d47264f244a4f5df9350bb3c3c4921ffa0095112da8. Every entry carries
    /// the reply body VERBATIM, so these tests exercise Core's own parser on the machine's
    /// own bytes rather than on a hand-written approximation of them.</para>
    /// <para>Parser entry point: <see cref="ObjectModelParser.FromRrModel"/>, the modern
    /// <c>rr_model</c> path - the bodies are <c>rr_model?key=move.axes&amp;flags=vnd4</c>
    /// replies, whose axis array is the body's <c>result</c>. The other entry point,
    /// <see cref="ObjectModelParser.FromRrStatus"/>, reads the legacy
    /// <c>rr_status?type=3</c> shape, which reports no axis limits at all; it appears here
    /// only in the last test, to pin that unknown bounds never silently reject.</para>
    /// <para>The diagnostics window records the COORDINATE reply only, so <c>state.status</c>
    /// is rebuilt from the status the session logged alongside each acquisition. That is the
    /// same status the live decision saw.</para>
    /// <para>What must never happen here: a coordinate being edited, clamped, projected or
    /// substituted. The decision only answers whether the display may pose the arm; the raw
    /// numbers have to survive it untouched, or the diagnostics window, the print log and the
    /// operator's count of refused frames all stop meaning anything.</para>
    /// </remarks>
    public class ApproachReplayTests
    {
        sealed class Acquisition
        {
            public long Id;
            public string Body = "";
            public string Status = "";
            public bool AllHomed;
            public string ExpectedEligibility = "";
            public bool ExpectedRejectedByEnvelope;
            public bool ExpectedEnvelopeKnown;
            public string? ExpectedReason;
            public double[] BodyMachine = new double[3];
        }

        static JsonValue LoadFixture()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "approach-20260909",
                                       "acquisitions.json");
            Assert.True(File.Exists(path),
                        "approach replay fixture missing: " + path
                        + " - run docs/dev/analysis/approach-20260909/extract_approach_window.py");
            return JsonValue.Parse(File.ReadAllText(path));
        }

        static IReadOnlyList<Acquisition> Load(out JsonValue metadata)
        {
            var doc = LoadFixture();
            metadata = doc.At("metadata");
            var array = doc.At("acquisitions");
            var list = new List<Acquisition>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                var e = array[i];
                var body = e.At("bodyMachine");
                list.Add(new Acquisition
                {
                    Id = (long)e.Num("acq"),
                    Body = e.Str("body"),
                    Status = e.Str("status"),
                    AllHomed = e.Bool("allHomed"),
                    ExpectedEligibility = e.Str("expected.eligibility"),
                    ExpectedRejectedByEnvelope = e.Bool("expected.rejectedByEnvelope"),
                    ExpectedEnvelopeKnown = e.Bool("expected.envelopeKnown"),
                    ExpectedReason = e.At("expected.reason").IsNull ? null : e.Str("expected.reason"),
                    BodyMachine = new[] { body[0].AsNumber(), body[1].AsNumber(), body[2].AsNumber() },
                });
            }
            return list;
        }

        /// <summary>Parses one recorded reply with the modern rr_model entry point, exactly
        /// as the poll loop does: the body's <c>result</c> is the axis array, and the state
        /// key comes from the same acquisition's recorded status.</summary>
        static ObjectModelSnapshot Parse(Acquisition a)
        {
            var body = JsonValue.Parse(a.Body);
            var state = JsonValue.Parse("{\"status\":\"" + a.Status.ToLowerInvariant() + "\"}");
            return ObjectModelParser.FromRrModel(state, body.At("result"), null);
        }

        static PoseDecision Decide(Acquisition a)
            => LivePoseEligibility.Decide(Parse(a), new PoseEligibilityOptions());

        [Fact]
        public void The_fixture_covers_the_whole_window_and_states_its_provenance()
        {
            var acquisitions = Load(out var metadata);
            Assert.Equal(68, acquisitions.Count);
            Assert.Equal(6424, acquisitions[0].Id);
            Assert.Equal(6491, acquisitions[acquisitions.Count - 1].Id);
            for (int i = 1; i < acquisitions.Count; i++)
                Assert.True(acquisitions[i].Id > acquisitions[i - 1].Id, "acquisitions are not in order");
            Assert.Equal("20260909-131323-d30110", metadata.Str("sourceRunId"));
            Assert.Equal("4ec8f1a21f6116d4e6068d47264f244a4f5df9350bb3c3c4921ffa0095112da8",
                         metadata.Str("jobGcodeSha256"));
            Assert.Equal("docs/dev/analysis/approach-20260909/extract_approach_window.py",
                         metadata.Str("extractedBy"));
            Assert.Equal(2.0, metadata.Num("envelopeToleranceMm"), 9);
            Assert.Contains("firmware", metadata.Str("expectationDerivation"), StringComparison.OrdinalIgnoreCase);
            // Anonymized: the run's host IP address and machine fingerprint live in that
            // run's manifest.json and must appear nowhere in the fixture. Asserted by SHAPE,
            // never by value - writing either value into a test would check it in.
            string text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures",
                                                        "approach-20260909", "acquisitions.json"));
            Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", text);
            Assert.DoesNotMatch(@"\b[0-9A-Z]{5}(-[0-9A-Z]{5}){4}\b", text);
            Assert.DoesNotContain("\"host\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"fingerprint\"", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_coordinate_before_the_excursion_is_shown()
        {
            var acquisitions = Load(out _);
            var first = acquisitions[0];
            Assert.Equal(6424, first.Id);
            // machine (195.959, -498.675, 400): Z exactly at the firmware's own maximum
            Assert.Equal(400, first.BodyMachine[2], 9);
            var d = Decide(first);
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.True(d.MayPose);
            Assert.True(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
        }

        [Fact]
        public void The_sustained_below_envelope_run_is_held_many_acquisitions_long()
        {
            var acquisitions = Load(out _);
            int rejected = 0, longestRun = 0, run = 0;
            foreach (var a in acquisitions)
            {
                var d = Decide(a);
                if (d.RejectedByEnvelope)
                {
                    Assert.Equal(PoseEligibility.Hold, d.Eligibility);
                    Assert.False(d.MayPose);
                    Assert.True(d.EnvelopeKnown);
                    Assert.NotNull(d.Reason);
                    Assert.Contains("Z", d.Reason!, StringComparison.Ordinal);
                    rejected++;
                    run++;
                    if (run > longestRun) longestRun = run;
                }
                else run = 0;
            }
            // A single-spike fix cannot pass this: the defect is 15 s of continuous
            // below-envelope coordinates, not one bad reply.
            Assert.Equal(65, rejected);
            Assert.True(longestRun >= 60, "expected a long unbroken rejected run, got " + longestRun);
            Assert.Equal(63, longestRun);
            // and the run really is the deep one, not a cluster of near-zero rounding
            double deepest = 0;
            foreach (var a in acquisitions) if (a.BodyMachine[2] < deepest) deepest = a.BodyMachine[2];
            Assert.True(deepest < -380, "expected the recorded excursion, got " + deepest.ToString(CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Every_acquisition_decides_what_the_fixture_derived_from_the_firmwares_own_envelope()
        {
            var acquisitions = Load(out _);
            foreach (var a in acquisitions)
            {
                var d = Decide(a);
                string where = "acquisition " + a.Id.ToString(CultureInfo.InvariantCulture);
                Assert.True(a.ExpectedEligibility == d.Eligibility.ToString(),
                            where + ": expected " + a.ExpectedEligibility + ", got " + d.Eligibility);
                Assert.True(a.ExpectedRejectedByEnvelope == d.RejectedByEnvelope,
                            where + ": RejectedByEnvelope should be " + a.ExpectedRejectedByEnvelope);
                Assert.True(a.ExpectedEnvelopeKnown == d.EnvelopeKnown,
                            where + ": EnvelopeKnown should be " + a.ExpectedEnvelopeKnown);
                Assert.True(a.ExpectedReason == d.Reason,
                            where + ": reason should be " + (a.ExpectedReason ?? "<null>")
                            + ", got " + (d.Reason ?? "<null>"));
            }
        }

        [Fact]
        public void The_raw_coordinates_survive_the_decision_exactly_as_recorded()
        {
            var acquisitions = Load(out _);
            foreach (var a in acquisitions)
            {
                var snap = Parse(a);
                var before = snap.MachinePosition;
                Assert.True(before.HasValue, "acquisition " + a.Id + " parsed to no position");
                LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
                var after = snap.MachinePosition!.Value;
                string where = "acquisition " + a.Id.ToString(CultureInfo.InvariantCulture);
                // nothing clamped, projected or substituted, and no repair note invented
                Assert.True(a.BodyMachine[0] == after.X, where + " X was altered");
                Assert.True(a.BodyMachine[1] == after.Y, where + " Y was altered");
                Assert.True(a.BodyMachine[2] == after.Z, where + " Z was altered");
                Assert.Null(snap.PositionRepair);
                // the reported envelope is untouched too - the decision reads it, never writes it
                Assert.Equal(0, snap.Axis("Z")!.Min, 9);
                Assert.Equal(400, snap.Axis("Z")!.Max, 9);
            }
        }

        [Fact]
        public void A_legitimately_negative_work_coordinate_inside_the_envelope_is_shown()
        {
            // Synthetic, and deliberately so: this machine's Z minimum is 0, so the log
            // cannot supply a legal negative Z. A machine whose firmware declares Z
            // min -25 has 25 mm of legal travel below zero, and refusing it would be the
            // same mistake as accepting -389 - assuming a bed at Z=0 instead of reading the
            // firmware's own limit. X min -300 and Y max 0 are the SHIPPED values on the
            // Potterbot, so negative X and Y are normal there already.
            string body = "{\"key\":\"move.axes\",\"flags\":\"vnd4\",\"result\":["
                        + "{\"letter\":\"X\",\"homed\":true,\"machinePosition\":-284.500,\"userPosition\":-284.500,\"min\":-300.00,\"max\":10000.00,\"visible\":true},"
                        + "{\"letter\":\"Y\",\"homed\":true,\"machinePosition\":-9871.250,\"userPosition\":-9871.250,\"min\":-10000.00,\"max\":0,\"visible\":true},"
                        + "{\"letter\":\"Z\",\"homed\":true,\"machinePosition\":-18.400,\"userPosition\":-18.400,\"min\":-25.00,\"max\":400.00,\"visible\":true}"
                        + "],\"next\":0}";
            var parsed = JsonValue.Parse(body);
            var snap = ObjectModelParser.FromRrModel(JsonValue.Parse("{\"status\":\"processing\"}"),
                                                     parsed.At("result"), null);
            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.True(d.MayPose);
            Assert.True(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
            Assert.Equal(-18.4, snap.MachinePosition!.Value.Z, 9);

            // 25 mm of legal travel does not become 400 mm of illegal travel
            var tooDeep = ObjectModelParser.FromRrModel(
                JsonValue.Parse("{\"status\":\"processing\"}"),
                JsonValue.Parse(body.Replace("\"machinePosition\":-18.400", "\"machinePosition\":-389.314")
                                    .Replace("\"userPosition\":-18.400", "\"userPosition\":-389.314")).At("result"),
                null);
            var refused = LivePoseEligibility.Decide(tooDeep, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Hold, refused.Eligibility);
            Assert.True(refused.RejectedByEnvelope);
        }

        [Fact]
        public void The_legacy_path_reports_unknown_bounds_and_still_shows()
        {
            // ObjectModelParser.FromRrStatus is the legacy rr_status?type=3 entry point. It
            // never assigns AxisState.Min/Max, so every pair is 0/0 - the degenerate case.
            // Unknown bounds must be reported as unknown: neither a pass dressed up as a
            // check, nor a rejection of a machine that simply did not describe itself.
            var status = JsonValue.Parse(
                "{\"status\":\"P\",\"coords\":{\"machine\":[336.877,-298.976,-389.314],"
                + "\"xyz\":[275.740,-402.552,0],\"axesHomed\":[1,1,1]}}");
            var snap = ObjectModelParser.FromRrStatus(status, null);
            Assert.True(snap.PositionValid);
            Assert.True(snap.AllHomed);

            var envelope = MachineEnvelope.FromAxes(snap.Axes, 2.0);
            Assert.False(envelope.Known);

            var d = LivePoseEligibility.Decide(snap, new PoseEligibilityOptions());
            Assert.Equal(PoseEligibility.Show, d.Eligibility);
            Assert.False(d.EnvelopeKnown);
            Assert.False(d.RejectedByEnvelope);
            Assert.NotNull(d.Reason);
            Assert.Contains("unknown", d.Reason!, StringComparison.OrdinalIgnoreCase);
            // and the coordinates are still the reported ones, unchecked but unedited
            Assert.Equal(-389.314, snap.MachinePosition!.Value.Z, 9);
        }
    }
}
