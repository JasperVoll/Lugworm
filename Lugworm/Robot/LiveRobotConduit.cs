using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Rhino.Display;
using Rhino.Geometry;
using Lugworm.Ui;

namespace Lugworm.Robot
{
    /// <summary>One animation frame of the live robot: the shared base meshes and edge
    /// buffers under per-link transforms. Immutable; the controller publishes a new instance
    /// and the conduit reads whichever is current. Everything is in millimetres, Rhino
    /// frame; the conduit applies document units.</summary>
    /// <remarks>There is deliberately no nozzle marker and no on-screen label. The point and
    /// its "observed / smoothed - delay N ms, sample age N ms" text sat next to the tool the
    /// whole time it printed, and the changing text alone invalidated an otherwise
    /// stationary frame, forcing a repaint per tick. Timing and staleness are facts for the
    /// component's <c>info</c> output and the Console; the viewport shows the ghosted arm.</remarks>
    public sealed class LiveRobotFrame
    {
        public IReadOnlyList<(Mesh mesh, Transform x)> Links { get; }
        public IReadOnlyList<(Line[] edges, Transform x)> Edges { get; }
        /// <summary>Schematic fallback when no robot model is loaded: thick lines.</summary>
        public IReadOnlyList<Line> Schematic { get; }
        /// <summary>Where the tool is, in mm, Rhino frame. Kept for the clipping box and for
        /// the geometric frame comparison; it is NOT drawn as a marker.</summary>
        public Point3d? Nozzle { get; }
        public bool ArmVisible { get; }
        public bool Smoothed { get; }
        /// <summary>The newest observation is older than the display's stale threshold.
        /// A stale pose is DRAWN exactly like a fresh one - the age belongs in
        /// <c>info</c> and the Console, never in the viewport (see the conduit).</summary>
        public bool Stale { get; }
        /// <summary>Why nothing (or only a ghost) is shown. Reported through the component's
        /// diagnostics; never drawn into the viewport and never part of frame equality.</summary>
        public string Reason { get; }
        public BoundingBox Bounds { get; }

        public LiveRobotFrame(IReadOnlyList<(Mesh, Transform)> links, IReadOnlyList<(Line[], Transform)> edges,
                              IReadOnlyList<Line> schematic, Point3d? nozzle, bool armVisible, bool smoothed,
                              bool stale, string reason, BoundingBox bounds)
        {
            Links = links; Edges = edges; Schematic = schematic; Nozzle = nozzle; ArmVisible = armVisible;
            Smoothed = smoothed; Stale = stale; Reason = reason ?? ""; Bounds = bounds;
        }

        public static LiveRobotFrame Hidden(string reason)
            => new LiveRobotFrame(Array.Empty<(Mesh, Transform)>(), Array.Empty<(Line[], Transform)>(),
                                  Array.Empty<Line>(), null, false, false, false, reason, BoundingBox.Empty);
    }

    /// <summary>The telemetry-cadence overlays: the observed trail, split into segments so no
    /// line implies an observation across a gap, optional per-point deviation colours, and
    /// the intended path drawn as thin neutral context - its printing runs and its travel
    /// runs kept apart. Immutable snapshot, mm, Rhino frame.</summary>
    /// <remarks>The intended path is CONTEXT - the file's own geometry. The trail is
    /// MEASUREMENT - positions the controller reported. They are drawn differently on
    /// purpose and are never merged into one curve. Within the context, printing and travel
    /// are drawn differently for the same reason: showing only one of them means showing a
    /// program the machine is not going to run.</remarks>
    public sealed class LiveTrailState
    {
        public IReadOnlyList<Point3d[]> Runs { get; }
        /// <summary>Per run, per point colour when deviation colouring is on; null otherwise.</summary>
        public IReadOnlyList<Color[]?> RunColors { get; }
        /// <summary>Per run, per point ambiguity flag (drawn dimmer); null when not coloured.</summary>
        public IReadOnlyList<bool[]?> RunAmbiguous { get; }
        /// <summary>Observed positions that stand alone - a segment of one sample, or one
        /// whose neighbour is too far away for the elapsed time to be joined by a straight
        /// line. Drawn as points so no invented movement is shown.</summary>
        public IReadOnlyList<Point3d> Isolated { get; }
        /// <summary>The PRINTING runs of the effective job, Rhino frame, simplified.</summary>
        public IReadOnlyList<Point3d[]> Context { get; }
        /// <summary>The TRAVEL runs of the same job - travels, seam approaches, coasts and
        /// layer changes - Rhino frame, simplified under the same budget. Kept apart from
        /// <see cref="Context"/> because they are drawn differently: they are motion the file
        /// commands but not shape it lays, and merging the two would show the operator a
        /// solid outline where the machine only moves through.</summary>
        public IReadOnlyList<Point3d[]> Travel { get; }
        public bool DeviationOn { get; }
        public double LegendMaxMm { get; }
        public string LegendNote { get; }
        public BoundingBox Bounds { get; }

        /// <param name="travel">Optional and last so a caller that predates travel runs keeps
        /// compiling; null means none. Its points are folded into <paramref name="bounds"/>
        /// here rather than at the call site, so the clipping box covers everything actually
        /// drawn even when the caller measured only the runs it knew about.</param>
        public LiveTrailState(IReadOnlyList<Point3d[]> runs, IReadOnlyList<Color[]?> runColors,
                              IReadOnlyList<bool[]?> runAmbiguous, IReadOnlyList<Point3d> isolated,
                              IReadOnlyList<Point3d[]> context, bool deviationOn,
                              double legendMaxMm, string legendNote, BoundingBox bounds,
                              IReadOnlyList<Point3d[]>? travel = null)
        {
            Runs = runs; RunColors = runColors; RunAmbiguous = runAmbiguous; Isolated = isolated;
            Context = context; DeviationOn = deviationOn; LegendMaxMm = legendMaxMm;
            LegendNote = legendNote ?? "";
            Travel = travel ?? Array.Empty<Point3d[]>();
            for (int r = 0; r < Travel.Count; r++)
            {
                var pts = Travel[r];
                for (int i = 0; i < pts.Length; i++) bounds.Union(pts[i]);
            }
            Bounds = bounds;
        }

        public static readonly LiveTrailState Empty = new LiveTrailState(Array.Empty<Point3d[]>(),
            Array.Empty<Color[]?>(), Array.Empty<bool[]?>(), Array.Empty<Point3d>(),
            Array.Empty<Point3d[]>(), false, 0, "", BoundingBox.Empty, Array.Empty<Point3d[]>());
    }

    /// <summary>Draws the live robot, its observed trail and the intended path into Rhino
    /// viewports through a display conduit - a display lifecycle independent of Grasshopper
    /// evaluation. The paint callback only reads two completed, immutable states; it takes no
    /// locks, creates no geometry and duplicates no mesh: the shared base meshes are drawn
    /// under their per-link transforms, ghosted by <see cref="RobotPreview"/> exactly as the
    /// Robot Simulation component draws them. Draw state (model transform, depth and colour
    /// writing) is restored in finally paths.</summary>
    public sealed class LiveRobotConduit : DisplayConduit
    {
        public static readonly Color TrailColor = Color.RoyalBlue;
        public static readonly Color LabelColor = Color.FromArgb(60, 60, 64);
        /// <summary>The intended path: thin and neutral, so it reads as the file's geometry
        /// rather than as something that was measured.</summary>
        public static readonly Color ContextColor = Color.FromArgb(176, 178, 184);
        /// <summary>The intended path's TRAVEL moves. Lighter than <see cref="ContextColor"/>
        /// and drawn dotted, so travels read as clearly secondary to the shape being printed
        /// while still being visible - the operator has to be able to see that a travel is
        /// there without mistaking it for a layer. Deliberately not
        /// <see cref="TrailColor"/>: the observed trail must stay independently
        /// identifiable from both intended-path styles.</summary>
        public static readonly Color TravelContextColor = Color.FromArgb(206, 208, 214);

        readonly Func<bool> _visible;
        readonly Func<double> _scaleToMm;

        volatile LiveRobotFrame _frame = LiveRobotFrame.Hidden("");
        volatile LiveTrailState _trail = LiveTrailState.Empty;

        /// <param name="visible">Answers, per draw, whether the owning component wants to be
        /// seen at all (preview on, not hidden/locked, its document active).</param>
        /// <param name="scaleToMm">Document units -> mm factor at draw time.</param>
        public LiveRobotConduit(Func<bool> visible, Func<double> scaleToMm)
        {
            _visible = visible ?? throw new ArgumentNullException(nameof(visible));
            _scaleToMm = scaleToMm ?? throw new ArgumentNullException(nameof(scaleToMm));
        }

        public LiveRobotFrame Frame => _frame;
        public LiveTrailState Trail => _trail;

        public void Publish(LiveRobotFrame frame) => _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        public void PublishTrail(LiveTrailState trail) => _trail = trail ?? throw new ArgumentNullException(nameof(trail));

        /// <summary>Everything drawn, mm, for the owning component's clipping box.</summary>
        public BoundingBox Bounds
        {
            get
            {
                var b = BoundingBox.Empty;
                var f = _frame; var t = _trail;
                if (f.Bounds.IsValid) b.Union(f.Bounds);
                if (t.Bounds.IsValid) b.Union(t.Bounds);
                return b;
            }
        }

        Transform DocTransform()
        {
            double s = _scaleToMm();
            return Math.Abs(s - 1.0) < 1e-12 ? Transform.Identity : Transform.Scale(Point3d.Origin, 1.0 / s);
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (!_visible()) return;
            var b = Bounds;
            if (!b.IsValid) return;
            var x = DocTransform();
            if (!x.Equals(Transform.Identity)) b.Transform(x);
            e.IncludeBoundingBox(b);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (!_visible()) return;
            var frame = _frame;
            var trail = _trail;
            var doc = DocTransform();
            bool scaled = !doc.Equals(Transform.Identity);
            var display = e.Display;

            // ---- intended path and observed trail: one model transform for document units ----
            if (scaled) display.PushModelTransform(doc);
            try
            {
                // Travel first, so the printed shape draws over it where the two coincide.
                for (int c = 0; c < trail.Travel.Count; c++)
                {
                    var pts = trail.Travel[c];
                    if (pts.Length >= 2) display.DrawDottedPolyline(pts, TravelContextColor, false);
                }
                for (int c = 0; c < trail.Context.Count; c++)
                {
                    var pts = trail.Context[c];
                    if (pts.Length >= 2) display.DrawPolyline(pts, ContextColor, 1);
                }
                for (int r = 0; r < trail.Runs.Count; r++)
                {
                    var pts = trail.Runs[r];
                    if (pts.Length < 2) continue;
                    var colors = r < trail.RunColors.Count ? trail.RunColors[r] : null;
                    if (colors == null)
                    {
                        display.DrawPolyline(pts, TrailColor, 2);
                        continue;
                    }
                    var ambiguous = r < trail.RunAmbiguous.Count ? trail.RunAmbiguous[r] : null;
                    for (int i = 1; i < pts.Length; i++)
                    {
                        var c = colors[i];
                        if (ambiguous != null && ambiguous[i]) c = Color.FromArgb(90, c);
                        display.DrawLine(new Line(pts[i - 1], pts[i]), c, 3);
                    }
                }
                // A sample that could not be joined to a neighbour is shown where it was
                // observed, never as a line to somewhere it may not have travelled.
                for (int i = 0; i < trail.Isolated.Count; i++)
                    display.DrawPoint(trail.Isolated[i], PointStyle.RoundSimple, 3, TrailColor);
            }
            finally { if (scaled) display.PopModelTransform(); }

            // ---- robot: per-link transform composed with document units ----
            if (frame.ArmVisible)
            {
                // 2026-09-09: a stale pose is drawn exactly like a fresh one. Staleness is
                // reported in `info` and the Console, never by a change of appearance -
                // an appearance that CHANGED with staleness read as the arm flashing at
                // telemetry cadence. The ghost below is unconditional, so it cannot.
                RobotPreview.DrawGhost(display, frame.Links, scaled ? doc : Transform.Identity);
                foreach (var (edges, x) in frame.Edges)
                {
                    var full = scaled ? doc * x : x;
                    display.PushModelTransform(full);
                    try { RobotPreview.DrawEdges(display, edges); }
                    finally { display.PopModelTransform(); }
                }
                if (frame.Schematic.Count > 0)
                {
                    if (scaled) display.PushModelTransform(doc);
                    try
                    {
                        foreach (var line in frame.Schematic)
                            display.DrawLine(line, RobotPreview.GhostEdgeColor, 4);
                    }
                    finally { if (scaled) display.PopModelTransform(); }
                }
            }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!_visible()) return;
            var trail = _trail;
            if (!trail.DeviationOn) return;
            var display = e.Display;

            // Fixed legend: the colours mean the same millimetres for the whole trail.
            var size = e.Viewport.Size;
            int w = 160, h = 10, x0 = 12, y0 = size.Height - 36;
            const int steps = 32;
            for (int i = 0; i < steps; i++)
            {
                var (r, g, b) = Core.Duet.DeviationColorScale.Map((i + 0.5) / steps);
                display.Draw2dRectangle(new Rectangle(x0 + i * w / steps, y0, Math.Max(1, w / steps + 1), h),
                                        Color.Transparent, 0, Color.FromArgb(r, g, b));
            }
            string hi = "≥ " + trail.LegendMaxMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
            display.Draw2dText("0 mm", LabelColor, new Point2d(x0, y0 - 4), false, 11);
            display.Draw2dText(hi, LabelColor, new Point2d(x0 + w - 44, y0 - 4), false, 11);
            var (ur, ug, ub) = Core.Duet.DeviationColorScale.Unavailable;
            display.Draw2dRectangle(new Rectangle(x0 + w + 12, y0, 14, h), Color.Transparent, 0, Color.FromArgb(ur, ug, ub));
            display.Draw2dText("no comparison", LabelColor, new Point2d(x0 + w + 30, y0 + 10), false, 11);
            if (trail.LegendNote.Length > 0)
                display.Draw2dText(trail.LegendNote, LabelColor, new Point2d(x0, y0 + 24), false, 11);
        }

        /// <summary>Drops the published frame and trail. Call after disabling. The base
        /// meshes belong to the shared robot model and are never copied here, so there is
        /// nothing else to release.</summary>
        public void ReleaseResources()
        {
            _frame = LiveRobotFrame.Hidden("");
            _trail = LiveTrailState.Empty;
        }
    }
}
