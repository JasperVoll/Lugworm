using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lugworm.Core.Duet
{
    /// <summary>What the live viewport actually cost, as counted - never as promised.</summary>
    /// <remarks>
    /// <para>Every field is a measurement of work that already happened, so the plan's
    /// acceptance conditions can be read off a running Rhino instead of argued about:
    /// zero telemetry-triggered Grasshopper solves with automatic outputs off, no job
    /// reparse without a revision change, no idle repaint loop, and at most one viewport
    /// redraw per frame callback.</para>
    /// <para>Immutable and host-free: the controller assembles one of these per solve and
    /// the component prints <see cref="Describe"/> into its <c>info</c> output. Numbers are
    /// wall-clock observations of one machine on one day; they are reported, never
    /// guaranteed.</para>
    /// </remarks>
    /// <summary>The measurements the 2026-09-09 review found missing: every rate reported as
    /// the separate thing it is, counters as measurement-window deltas rather than process
    /// lifetime totals, and the display's own validity and retention decisions made
    /// countable.</summary>
    /// <remarks>
    /// <para>Why each field is separate: a 14 ms coordinate round trip is not a 14 ms poll,
    /// a configured interval is not the interval the session actually ran at after its clamp,
    /// and neither is the age of the data on screen. Reporting one number for all of them is
    /// what let "300 ms is too slow" and "50 ms" be argued about without evidence
    /// (checklist rows 10a.12, 10a.29).</para>
    /// <para>Plain mutable fields on purpose, like <see cref="TrailObservation"/>: this is a
    /// diagnostics DTO the viewport fills in under its own lock and then hands over by
    /// reference, not a value with an invariant to protect.</para>
    /// </remarks>
    public sealed class LiveDisplayDetail
    {
        /// <summary>The poll interval the operator configured, ms.</summary>
        public int RequestedPollIntervalMs;
        /// <summary>What the session will actually target after its own clamp, ms.</summary>
        public int EffectivePollTargetMs;
        /// <summary>Non-null when the request was clamped, naming both numbers. A requested
        /// 50 ms that silently became 250 must say so.</summary>
        public string? ClampNote;
        /// <summary>Deduplicated coordinate acquisitions this display observed. The glitch
        /// filter releases a held sample together with its successor, so a callback count is
        /// not an acquisition count.</summary>
        public long CoordinateAcquisitions;
        /// <summary>Poll callbacks that carried an acquisition id already seen. Counted so a
        /// batched release cannot masquerade as a zero-length poll interval.</summary>
        public long DuplicateCallbacks;
        /// <summary>Start-to-start spacing of coordinate ACQUISITIONS, from their own
        /// acquisition stamps, ms.</summary>
        public double CoordinateIntervalMsP50, CoordinateIntervalMsP95;
        /// <summary>Round trip of the coordinate request alone, ms.</summary>
        public double CoordinateRoundTripMsP95;
        /// <summary>Elapsed time of the WHOLE poll cycle - state, axes, job, currentMove,
        /// speed factor, reply check and the periodic thermal reads - ms.</summary>
        public double FullPollElapsedMsP95;
        /// <summary>Accepted observation to the overlay publication that carried it, ms.
        /// This is the number the operator sees as trail lag.</summary>
        public double PublicationAgeMsP95;
        /// <summary>Measured spacing between overlay publications, ms.</summary>
        public double OverlayIntervalMsP95;
        /// <summary>Counters as deltas over the measurement window (since this display
        /// attached), never process lifetime totals.</summary>
        public long ParsesInWindow, CacheHitsInWindow, RedrawsInWindow, FrameCallbacksInWindow;
        /// <summary>Observations the display validity gate refused to pose from. They are
        /// preserved and counted, never deleted.</summary>
        public long ObservationsHeld;
        /// <summary>... of which were refused because they were outside the machine's own
        /// reported envelope - the 2026-09-09 approach defect. Counted apart so a sustained
        /// real fault cannot disappear among ordinary holds.</summary>
        public long RejectedByEnvelope;
        /// <summary>False when the firmware reported no usable per-axis envelope, so the
        /// bounds are UNKNOWN rather than satisfied.</summary>
        public bool EnvelopeKnown;
        /// <summary>Why drawn runs actually ended, by reason.</summary>
        public string BreakReasons = "";
        /// <summary>What the bounded retention is currently keeping and what it compacted.</summary>
        public string RetentionNote = "";

        public IReadOnlyList<string> Lines()
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>(5);
            lines.Add("cadence: requested " + RequestedPollIntervalMs.ToString(inv)
                + " ms, effective target " + EffectivePollTargetMs.ToString(inv)
                + " ms, acquisitions " + CoordinateAcquisitions.ToString(inv)
                + " (" + DuplicateCallbacks.ToString(inv) + " repeated callbacks)"
                + ", interval p50/p95 " + CoordinateIntervalMsP50.ToString("0", inv) + "/"
                + CoordinateIntervalMsP95.ToString("0", inv) + " ms"
                + (ClampNote == null ? "" : " - " + ClampNote));
            lines.Add("latency: coordinate round trip p95 " + CoordinateRoundTripMsP95.ToString("0", inv)
                + " ms, full poll p95 " + FullPollElapsedMsP95.ToString("0", inv)
                + " ms, overlay interval p95 " + OverlayIntervalMsP95.ToString("0", inv)
                + " ms, publication age p95 " + PublicationAgeMsP95.ToString("0", inv) + " ms");
            lines.Add("window: parses " + ParsesInWindow.ToString(inv)
                + ", cache hits " + CacheHitsInWindow.ToString(inv)
                + ", redraws " + RedrawsInWindow.ToString(inv) + "/"
                + FrameCallbacksInWindow.ToString(inv) + " callbacks");
            lines.Add("validity: " + ObservationsHeld.ToString(inv) + " observation(s) held, "
                + RejectedByEnvelope.ToString(inv) + " outside the reported envelope, envelope "
                + (EnvelopeKnown ? "known" : "UNKNOWN - not checked"));
            lines.Add("breaks: " + (BreakReasons.Length == 0 ? "none" : BreakReasons)
                + (RetentionNote.Length == 0 ? "" : "; retention: " + RetentionNote));
            return lines;
        }
    }

    public sealed class LiveRenderStats
    {
        /// <summary>Completed poll cycles observed by this display since it attached.</summary>
        public long PollRequests { get; }
        /// <summary>Request start to snapshot receipt of the newest poll, ms.</summary>
        public double LastRoundTripMs { get; }
        /// <summary>Mean round trip over the polls this display saw, ms.</summary>
        public double MeanRoundTripMs { get; }
        /// <summary>Observed start-to-start spacing of polls, ms - what the configured
        /// interval actually produced, not what it asked for.</summary>
        public double ActualPollIntervalMs { get; }
        /// <summary>Time from asking for a UI callback to running it, p95 ms. A rising
        /// number is Rhino's UI thread, not this display's frame work.</summary>
        public double UiCallbackWaitMsP95 { get; }
        public double FrameBuildMsP50 { get; }
        public double FrameBuildMsP95 { get; }
        /// <summary>Rhino viewport redraws this display asked for. Compare against
        /// <see cref="FrameCallbacks"/>: one per callback is the budget.</summary>
        public long Redraws { get; }
        public long FrameCallbacks { get; }
        /// <summary>Grasshopper solves this component was expired for by telemetry. Zero is
        /// the expected value with automatic outputs off.</summary>
        public long GhSolves { get; }
        /// <summary>Intended-path parses actually performed, and lookups that were served
        /// from the shared cache instead.</summary>
        public long ParseCount { get; }
        public long ParseCacheHits { get; }
        public int TrailPoints { get; }
        public int TrailSegments { get; }
        /// <summary>Observations that carried no usable position, so nothing was drawn.</summary>
        public long DroppedSamples { get; }
        /// <summary>Observations the continuity policy flagged. They stay in the trail with
        /// their raw coordinates; this is the count, not a deletion.</summary>
        public long SuspectSamples { get; }
        /// <summary>The frame budget currently in force, ms per frame.</summary>
        public int FrameIntervalMs { get; }
        /// <summary>The 2026-09-09 measurement block, or null for a caller that reports only
        /// the original counters. Optional so every existing construction keeps compiling.</summary>
        public LiveDisplayDetail? Detail { get; }

        public LiveRenderStats(long pollRequests, double lastRoundTripMs, double meanRoundTripMs,
                               double actualPollIntervalMs, double uiCallbackWaitMsP95,
                               double frameBuildMsP50, double frameBuildMsP95, long redraws,
                               long frameCallbacks, long ghSolves, long parseCount, long parseCacheHits,
                               int trailPoints, int trailSegments, long droppedSamples,
                               long suspectSamples, int frameIntervalMs,
                               LiveDisplayDetail? detail = null)
        {
            Detail = detail;
            PollRequests = pollRequests;
            LastRoundTripMs = lastRoundTripMs;
            MeanRoundTripMs = meanRoundTripMs;
            ActualPollIntervalMs = actualPollIntervalMs;
            UiCallbackWaitMsP95 = uiCallbackWaitMsP95;
            FrameBuildMsP50 = frameBuildMsP50;
            FrameBuildMsP95 = frameBuildMsP95;
            Redraws = redraws;
            FrameCallbacks = frameCallbacks;
            GhSolves = ghSolves;
            ParseCount = parseCount;
            ParseCacheHits = parseCacheHits;
            TrailPoints = trailPoints;
            TrailSegments = trailSegments;
            DroppedSamples = droppedSamples;
            SuspectSamples = suspectSamples;
            FrameIntervalMs = frameIntervalMs;
        }

        public static readonly LiveRenderStats Empty =
            new LiveRenderStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>Frames per second a frame interval buys; 0 for a stopped pump.</summary>
        public static double Fps(int intervalMs) => intervalMs <= 0 ? 0 : 1000.0 / intervalMs;

        /// <summary>Mean of a running sum; 0 rather than NaN for an empty sample, because
        /// this is printed in a diagnostics block a human reads.</summary>
        public static double Mean(long count, double sum)
            => count <= 0 || double.IsNaN(sum) ? 0 : sum / count;

        /// <summary>True when the display asked for no more than one redraw per frame
        /// callback - the plan's redraw budget, as a fact rather than a claim.</summary>
        public bool WithinRedrawBudget => Redraws <= FrameCallbacks;

        /// <summary>One line per group, invariant culture, newest facts first. The detail
        /// block is appended when the caller measured it; the original lines are unchanged so
        /// an existing reader of this text keeps working.</summary>
        public IReadOnlyList<string> Lines()
        {
            var groups = new List<string>(Core());
            if (Detail != null) groups.AddRange(Detail.Lines());
            return groups;
        }

        string[] Core()
        {
            var inv = CultureInfo.InvariantCulture;
            return new[]
            {
                "polls: " + PollRequests.ToString(inv)
                    + ", round trip last/mean " + LastRoundTripMs.ToString("0", inv) + "/"
                    + MeanRoundTripMs.ToString("0", inv) + " ms"
                    + ", actual interval " + ActualPollIntervalMs.ToString("0", inv) + " ms",
                "frames: budget " + Fps(FrameIntervalMs).ToString("0", inv) + " fps"
                    + ", build p50/p95 " + FrameBuildMsP50.ToString("0.0", inv) + "/"
                    + FrameBuildMsP95.ToString("0.0", inv) + " ms"
                    + ", ui callback wait p95 " + UiCallbackWaitMsP95.ToString("0", inv) + " ms"
                    + ", redraws " + Redraws.ToString(inv) + "/" + FrameCallbacks.ToString(inv),
                "solves: " + GhSolves.ToString(inv) + " telemetry-triggered"
                    + ", path parses " + ParseCount.ToString(inv)
                    + ", cache hits " + ParseCacheHits.ToString(inv),
                "trail: " + TrailPoints.ToString(inv) + " points in "
                    + TrailSegments.ToString(inv) + " segment(s)"
                    + ", " + SuspectSamples.ToString(inv) + " suspect (kept raw)"
                    + ", " + DroppedSamples.ToString(inv) + " without a position",
            };
        }

        public string Describe() => string.Join("\n", Lines());
    }
}
