using System;
using System.Collections.Generic;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Robot
{
    /// <summary>Turns a parsed program into the one sampled sequence every consumer shares:
    /// TCP points, per-segment extrude flags, per-segment feeds, and the places where the
    /// file commands NO motion at all.</summary>
    /// <remarks>
    /// <para>This is the body that used to live in the plugin's <c>Ui.ToolpathSampler</c>.
    /// It moved into Core unchanged so the Robot Simulation component, the RRF validation
    /// path, print-stats and the Live View intended path all sample from exactly one
    /// implementation - two samplers that drift apart would mean the renderer showing a
    /// path the validator never checked.</para>
    /// <para>The flag lists are per SEGMENT, not per point: <c>ext[k]</c> and
    /// <c>feeds[k]</c> describe the segment that ENDS at point k, so segment k spans
    /// points[k-1] -&gt; points[k]. Element 0 has no segment before it and carries the
    /// neutral values (not extruding, zero feed). Reading these as per-point properties is
    /// what lost the first segment of every extrusion run in the Live View renderer
    /// (test-round row 10a.8).</para>
    /// </remarks>
    public static class MoveSampler
    {
        /// <summary>Two positions closer than this are the same point. A move whose start
        /// sits further than this from the previous move's end was not reached by any
        /// commanded motion - the file jumped, and nothing may be drawn across the jump.
        /// 1e-6 mm is far below any coordinate a G-code word can express (six decimals is
        /// already a nanometre) so this only ever fires on a genuine discontinuity.</summary>
        public const double ContinuityToleranceMm = 1e-6;

        /// <summary>Samples <paramref name="moves"/> into points, per-segment extrude flags,
        /// per-segment feeds (mm/s) and per-point break flags.</summary>
        /// <param name="sharedArcTol">When set, arcs are tessellated through
        /// <see cref="RobotChecks.SampleMovePath"/> - the same sequence the RRF validator
        /// walks, so what is rendered is what was checked. When null the historical
        /// 0.05 mm chord tolerance is used, which is what the UR/live callers have always
        /// consumed.</param>
        /// <returns>
        /// <c>breakBefore[k]</c> is true when there is NO commanded
        /// motion from point k-1 to point k: the first point overall, and the first point
        /// emitted for any move whose start does not coincide with the previous move's end
        /// (an unresolved <c>G28</c> that jumped the position to a calibrated home pose, a
        /// coordinate reset that displaced it). A consumer that draws segments must not
        /// draw one into a point flagged here - the machine's route across that gap is not
        /// in the file, and a straight line would be an invention.
        /// </returns>
        /// <remarks>A discontinuous move contributes its own start as a new run, then all
        /// its commanded segments. Legacy consumers retain their indexing through
        /// <see cref="SampleLegacy"/>.</remarks>
        public static (List<Vec3> pts, List<bool> ext, List<double> feeds, List<bool> breakBefore)
            SampleWithBreaks(IReadOnlyList<ParsedMove> moves, double? sharedArcTol = null)
            => Sample(moves, sharedArcTol, preserveStarts: true);

        /// <summary>Historical point indexing for simulation, validation and statistics.
        /// New renderers must use SampleWithBreaks to preserve discontinuous move starts.</summary>
        public static (List<Vec3> pts, List<bool> ext, List<double> feeds, List<bool> breakBefore)
            SampleLegacy(IReadOnlyList<ParsedMove> moves, double? sharedArcTol = null)
            => Sample(moves, sharedArcTol, preserveStarts: false);

        static (List<Vec3> pts, List<bool> ext, List<double> feeds, List<bool> breakBefore)
            Sample(IReadOnlyList<ParsedMove> moves, double? sharedArcTol, bool preserveStarts)
        {
            var pts = new List<Vec3>();
            var ext = new List<bool>();
            var feeds = new List<double>();
            var breakBefore = new List<bool>();
            if (moves == null) return (pts, ext, feeds, breakBefore);

            bool havePrevious = false;
            Vec3 previousEnd = default(Vec3);
            foreach (var parsed in moves)
            {
                var p0 = new Vec3(parsed.StartX, parsed.StartY, parsed.StartZ);
                var p1 = new Vec3(parsed.X, parsed.Y, parsed.Z);
                Move move = Move.Line(p0, p1);
                if (parsed.Arc)
                {
                    double cx = p0.X + parsed.I, cy = p0.Y + parsed.J;
                    double r = Math.Sqrt((p0.X - cx) * (p0.X - cx) + (p0.Y - cy) * (p0.Y - cy));
                    if (r > 1e-9 && !double.IsNaN(r)) move = Move.Arc(p0, p1, cx, cy, parsed.G == 2, r);
                }
                // RRF validation and rendering must consume the exact same
                // sampled sequence. Keep the historical sampler for UR/live
                // callers that omit the shared tolerance.
                var poly = sharedArcTol.HasValue
                    ? RobotChecks.SampleMovePath(parsed, sharedArcTol.Value)
                    : ArcTessellation.ToPolyline(move, 0.05, 4096);
                // The gap is measured against the move's OWN start, before any point of it
                // is emitted: the previous move ended somewhere, this one begins somewhere,
                // and only the file can say whether those are the same place.
                bool jumped = havePrevious && previousEnd.DistanceTo(poly[0]) > ContinuityToleranceMm;
                if (pts.Count == 0) { pts.Add(poly[0]); ext.Add(false); feeds.Add(0.0); breakBefore.Add(true); }
                else if (jumped && preserveStarts)
                { pts.Add(poly[0]); ext.Add(false); feeds.Add(0.0); breakBefore.Add(true); }
                for (int i = 1; i < poly.Count; i++)
                {
                    pts.Add(poly[i]);
                    ext.Add(parsed.Extrude);
                    feeds.Add(parsed.F > 0 ? parsed.F / 60.0 : 0.0);
                    breakBefore.Add(jumped && !preserveStarts && i == 1);
                }
                havePrevious = true;
                previousEnd = poly[poly.Count - 1];
            }
            return (pts, ext, feeds, breakBefore);
        }
    }
}
