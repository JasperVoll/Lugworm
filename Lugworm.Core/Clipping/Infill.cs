using System;
using System.Collections.Generic;
using Clipper2Lib;
using Lugworm.Core.Analysis;
using Lugworm.Core.Geometry;
using Lugworm.Core.Paths;

namespace Lugworm.Core.Clipping
{
    public static class Infill
    {
        /// <summary>Clip XY line segments to the inside of closed polygons.
        /// Returns (start,end) of each inside span, at each input line's own Z -
        /// mixed-Z inputs used to be silently stamped with the first line's Z.</summary>
        public static List<(Vec3, Vec3)> ClipLines(IReadOnlyList<(Vec3, Vec3)> lines,
                                                    IReadOnlyList<IReadOnlyList<Vec3>> polys)
        {
            var outp = new List<(Vec3, Vec3)>();
            if (lines.Count == 0) return outp;

            double z0 = lines[0].Item1.Z;
            bool uniformZ = true;
            foreach (var (a, b) in lines)
                if (Math.Abs(a.Z - z0) > 1e-9 || Math.Abs(b.Z - z0) > 1e-9) { uniformZ = false; break; }

            var clip = ClipperConvert.ToPathsD(polys, minPts: 3);

            if (uniformZ)
            {
                var subj = new PathsD();
                foreach (var (a, b) in lines)
                    subj.Add(new PathD(2) { new PointD(a.X, a.Y), new PointD(b.X, b.Y) });

                var open = ClipperCompat.ClipOpenLines(subj, clip, ClipperConvert.Precision);
                foreach (var seg in open)
                    if (seg.Count >= 2)
                        outp.Add((new Vec3(seg[0].x, seg[0].y, z0),
                                  new Vec3(seg[seg.Count - 1].x, seg[seg.Count - 1].y, z0)));
                return outp;
            }

            // mixed Z: clip per line so every output span keeps its source line's Z
            foreach (var (a, b) in lines)
            {
                var subj = new PathsD { new PathD(2) { new PointD(a.X, a.Y), new PointD(b.X, b.Y) } };
                var open = ClipperCompat.ClipOpenLines(subj, clip, ClipperConvert.Precision);
                foreach (var seg in open)
                    if (seg.Count >= 2)
                        outp.Add((new Vec3(seg[0].x, seg[0].y, a.Z),
                                  new Vec3(seg[seg.Count - 1].x, seg[seg.Count - 1].y, a.Z)));
            }
            return outp;
        }

        /// <summary>Boustrophedon scan-line infill clipped to the boundary polygons.
        /// Returns one connected polyline (or empty).</summary>
        public static List<Vec3> Zigzag(IReadOnlyList<IReadOnlyList<Vec3>> boundary,
                                        double spacing, double angleDeg, double z)
            => Zigzag(boundary, spacing, angleDeg, z, out _);

        /// <summary>Zigzag with a diagnostic: when the result is empty, `diagnostic`
        /// says why (degenerate boundary vs genuinely nothing to fill) instead of the
        /// caller having to guess.</summary>
        public static List<Vec3> Zigzag(IReadOnlyList<IReadOnlyList<Vec3>> boundary,
                                        double spacing, double angleDeg, double z,
                                        out string? diagnostic)
        {
            var spans = ZigzagSpans(boundary, spacing, angleDeg, z, out diagnostic);
            var flattened = new List<Vec3>();
            // Compatibility projection for callers that still consume one polyline.
            // New toolpath code uses ZigzagSpans so a gap is represented by a
            // separate contour and therefore becomes a travel in GcodeBuilder.
            foreach (var span in spans) flattened.AddRange(span);
            return flattened;
        }

        /// <summary>Returns each clipped scan-line span as its own extruding path.
        /// Rows are connected into continuous zigzag polylines. A connector that would cross a hole
        /// or leave a concave region follows the shorter arc of the boundary ring both ends lie on,
        /// one wall distance inside the last wall, as <see cref="WaveSpans"/> does; ends on
        /// different rings (separate islands) start a new path instead (a travel in GcodeBuilder).</summary>
        public static List<List<Vec3>> ZigzagSpans(IReadOnlyList<IReadOnlyList<Vec3>> boundary,
                                                   double spacing, double angleDeg, double z,
                                                   out string? diagnostic, Vec3? anchor = null)
        {
            diagnostic = null;
            var empty = new List<List<Vec3>>();
            if (boundary.Count == 0) { diagnostic = "no boundary polygons"; return empty; }
            if (!(spacing > 0)) { diagnostic = $"infill spacing must be > 0 (got {spacing})"; return empty; }
            bool anyUsable = false;
            foreach (var poly in boundary) if (poly != null && poly.Count >= 3) { anyUsable = true; break; }
            if (!anyUsable) { diagnostic = "all boundary rings are degenerate (< 3 points)"; return empty; }

            double ang = angleDeg * Math.PI / 180.0;
            double ca = Math.Cos(-ang), sa = Math.Sin(-ang);
            Vec3 Rot(Vec3 p, double cc, double ss) => new Vec3(p.X * cc - p.Y * ss, p.X * ss + p.Y * cc, p.Z);

            var rb = new List<IReadOnlyList<Vec3>>();
            double xmin = double.MaxValue, xmax = double.MinValue,
                   ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var poly in boundary)
            {
                if (poly == null || poly.Count < 3) continue;
                var rp = new List<Vec3>(poly.Count);
                foreach (var p in poly)
                {
                    var r = Rot(p, ca, sa);
                    rp.Add(r);
                    if (r.X < xmin) xmin = r.X; if (r.X > xmax) xmax = r.X;
                    if (r.Y < ymin) ymin = r.Y; if (r.Y > ymax) ymax = r.Y;
                }
                rb.Add(rp);
            }
            xmin -= 1.0; xmax += 1.0;

            // one Clipper run for all rows (was one run + one ToPathsD per row)
            var rowYs = new List<double>();
            var allLines = new List<(Vec3, Vec3)>();
            for (double y = ymin + spacing * 0.5; y < ymax; y += spacing)
            {
                rowYs.Add(y);
                allLines.Add((new Vec3(xmin, y, z), new Vec3(xmax, y, z)));
            }
            var rowSpans = new List<(double a, double b)>[rowYs.Count];
            for (int r = 0; r < rowSpans.Length; r++) rowSpans[r] = new List<(double a, double b)>();
            foreach (var (a, b) in ClipLines(allLines, rb))
            {
                int r = (int)Math.Round((a.Y - rowYs[0]) / spacing);
                if (r < 0 || r >= rowSpans.Length) continue;
                rowSpans[r].Add((Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
            }

            // connect consecutive rows at the nearest ends; a connector that would
            // leave the region (hole, concave notch) follows the shared boundary ring,
            // and only ends on different rings start a new polyline
            var spansOut = new List<List<Vec3>>();
            List<Vec3>? cur = null;
            bool flip = false;
            for (int r = 0; r < rowSpans.Length; r++)
            {
                var spans = rowSpans[r];
                if (spans.Count == 0) { flip = !flip; continue; }
                spans.Sort((p, q) => p.a.CompareTo(q.a));
                double yy = rowYs[r];
                for (int k = 0; k < spans.Count; k++)
                {
                    var (sa0, sb0) = flip ? spans[spans.Count - 1 - k] : spans[k];
                    var p0 = new Vec3(sa0, yy, z); var p1 = new Vec3(sb0, yy, z);
                    if (cur != null)
                    {
                        var last = cur[cur.Count - 1];
                        if (PolylineOps.Dist2Xy(last, p1) < PolylineOps.Dist2Xy(last, p0)) { var t = p0; p0 = p1; p1 = t; }
                        if (ConnectorInside(last, p0, rb)) { cur.Add(p0); cur.Add(p1); continue; }
                        var connector = BoundaryConnector(last, p0, rb, MaxConnectorDetourPitches * spacing);
                        if (connector != null)
                        {
                            // the arc starts at last and ends at p0
                            for (int i = 1; i < connector.Count; i++) cur.Add(connector[i]);
                            cur.Add(p1);
                            continue;
                        }
                    }
                    cur = new List<Vec3> { p0, p1 };
                    spansOut.Add(cur);
                }
                flip = !flip;
            }
            if (spansOut.Count == 0) return empty;

            FinishSpans(spansOut, ang, boundary, anchor, spacing);
            return spansOut;
        }

        /// <summary>Amplitude and shape of the wave infill's rows.</summary>
        public sealed class WaveOptions
        {
            /// <summary>Wave period along the row, mm.</summary>
            public double Wavelength = 10.0;
            /// <summary>True: the mirrored peaks of neighbouring rows meet - their
            /// centre-lines are tangent at the contact point, amplitude = spacing/2.
            /// False: they stop <see cref="Clearance"/> short of each other.</summary>
            public bool Tangent = false;
            /// <summary>Gap left between the mirrored peaks of neighbouring rows when
            /// <see cref="Tangent"/> is off, mm. &lt;= 0 falls back to 0.2 x spacing.</summary>
            public double Clearance = 0.0;
            /// <summary>Sampling step along a row, mm. The wave is only as smooth as this.</summary>
            public double Resolution = 0.5;

            /// <summary>The amplitude these options imply for a given row pitch, always
            /// strictly under half the pitch. That bound is not cosmetic: the single
            /// Clipper run below recovers a clipped piece's row from its own y, which is
            /// only unambiguous while every point stays inside its row's band.</summary>
            public double AmplitudeFor(double spacing)
            {
                double half = Math.Abs(spacing) * 0.5;
                double clearance = Clearance > 0 ? Clearance : 0.2 * Math.Abs(spacing);
                double a = Tangent ? half : (Math.Abs(spacing) - clearance) * 0.5;
                if (a < 0) a = 0;
                // strictly inside the band: at exactly half the pitch a crest lands on the
                // neighbouring centre-line and the row recovery ties
                double cap = half * (1.0 - 1e-9);
                return a > cap ? cap : a;
            }
        }

        /// <summary>Parallel-line infill whose rows follow a periodic profile instead of
        /// running straight, mirrored row to row so one row's crests face the next row's
        /// troughs. Row pitch and wall anchoring match <see cref="ZigzagSpans"/>.
        /// When a straight join leaves the region, endpoints on the same boundary
        /// ring are joined along its shorter arc to preserve wall clearance, unless that
        /// arc detours more than <see cref="MaxConnectorDetourPitches"/> row pitches.
        ///
        /// <para>Amplitude comes from <paramref name="wave"/> and is always strictly under
        /// <c>spacing/2</c>, so every point of row k stays within its own band and the one
        /// Clipper run can recover each clipped piece's row from its y. Clipping row by row
        /// instead would cost one Clipper run per row for no benefit.</para></summary>
        public static List<List<Vec3>> WaveSpans(IReadOnlyList<IReadOnlyList<Vec3>> boundary,
                                                 double spacing, double angleDeg, double z,
                                                 IWaveProfile profile, WaveOptions wave,
                                                 out string? diagnostic, Vec3? anchor = null)
        {
            diagnostic = null;
            var empty = new List<List<Vec3>>();
            if (profile == null) { diagnostic = "wave infill needs a profile"; return empty; }
            if (wave == null) { diagnostic = "wave infill needs options"; return empty; }
            if (boundary.Count == 0) { diagnostic = "no boundary polygons"; return empty; }
            if (!(spacing > 0)) { diagnostic = $"infill spacing must be > 0 (got {spacing})"; return empty; }
            if (!(wave.Wavelength > 0)) { diagnostic = $"wavelength must be > 0 (got {wave.Wavelength})"; return empty; }
            if (!(wave.Resolution > 0)) { diagnostic = $"wave resolution must be > 0 (got {wave.Resolution})"; return empty; }
            bool anyUsable = false;
            foreach (var poly in boundary) if (poly != null && poly.Count >= 3) { anyUsable = true; break; }
            if (!anyUsable) { diagnostic = "all boundary rings are degenerate (< 3 points)"; return empty; }

            double ang = angleDeg * Math.PI / 180.0;
            double ca = Math.Cos(-ang), sa = Math.Sin(-ang);
            Vec3 Rot(Vec3 p, double cc, double ss) => new Vec3(p.X * cc - p.Y * ss, p.X * ss + p.Y * cc, p.Z);

            var rb = new List<IReadOnlyList<Vec3>>();
            double xmin = double.MaxValue, xmax = double.MinValue,
                   ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var poly in boundary)
            {
                if (poly == null || poly.Count < 3) continue;
                var rp = new List<Vec3>(poly.Count);
                foreach (var p in poly)
                {
                    var r = Rot(p, ca, sa);
                    rp.Add(r);
                    if (r.X < xmin) xmin = r.X; if (r.X > xmax) xmax = r.X;
                    if (r.Y < ymin) ymin = r.Y; if (r.Y > ymax) ymax = r.Y;
                }
                rb.Add(rp);
            }
            xmin -= 1.0; xmax += 1.0;

            double amp = wave.AmplitudeFor(spacing);
            double y0 = ymin + spacing * 0.5;
            var rowYs = new List<double>();
            var subj = new PathsD();
            int steps = Math.Max(1, (int)Math.Ceiling((xmax - xmin) / wave.Resolution));
            for (double y = y0; y < ymax + amp; y += spacing)
            {
                int k = rowYs.Count;
                double sign = (k & 1) == 0 ? 1.0 : -1.0;   // mirrored row to row
                rowYs.Add(y);
                var row = new PathD(steps + 1);
                for (int i = 0; i <= steps; i++)
                {
                    double x = xmin + (xmax - xmin) * i / steps;
                    row.Add(new PointD(x, y + sign * amp * profile.At(x / wave.Wavelength)));
                }
                subj.Add(row);
            }
            if (rowYs.Count == 0) { diagnostic = "region is thinner than one infill row"; return empty; }

            var clip = ClipperConvert.ToPathsD(rb, minPts: 3);
            var rowPieces = new List<List<Vec3>>[rowYs.Count];
            for (int r = 0; r < rowPieces.Length; r++) rowPieces[r] = new List<List<Vec3>>();
            foreach (var piece in ClipperCompat.ClipOpenLines(subj, clip, ClipperConvert.Precision))
            {
                if (piece.Count < 2) continue;
                // every point of row k lies within amp < spacing/2 of y_k, so its own y
                // names its row unambiguously
                int r = (int)Math.Round((piece[0].y - y0) / spacing);
                if (r < 0 || r >= rowPieces.Length) continue;
                var pts = new List<Vec3>(piece.Count);
                foreach (var pt in piece) pts.Add(new Vec3(pt.x, pt.y, z));
                rowPieces[r].Add(pts);
            }

            var spansOut = new List<List<Vec3>>();
            List<Vec3>? cur = null;
            bool flip = false;
            for (int r = 0; r < rowPieces.Length; r++)
            {
                var pieces = rowPieces[r];
                if (pieces.Count == 0) { flip = !flip; continue; }
                pieces.Sort((p, q) => MinX(p).CompareTo(MinX(q)));
                for (int k = 0; k < pieces.Count; k++)
                {
                    var piece = flip ? pieces[pieces.Count - 1 - k] : pieces[k];
                    if (flip) piece.Reverse();
                    if (cur != null)
                    {
                        var last = cur[cur.Count - 1];
                        if (PolylineOps.Dist2Xy(last, piece[piece.Count - 1]) < PolylineOps.Dist2Xy(last, piece[0])) piece.Reverse();
                        if (ConnectorInside(last, piece[0], rb)) { cur.AddRange(piece); continue; }
                        var connector = BoundaryConnector(last, piece[0], rb, MaxConnectorDetourPitches * spacing);
                        if (connector != null)
                        {
                            cur.AddRange(connector);
                            cur.AddRange(piece);
                            continue;
                        }
                    }
                    cur = new List<Vec3>(piece);
                    spansOut.Add(cur);
                }
                flip = !flip;
            }
            if (spansOut.Count == 0) { diagnostic = "nothing to fill"; return empty; }

            FinishSpans(spansOut, ang, boundary, anchor, spacing);
            return spansOut;
        }

        /// <summary>Shared tail of <see cref="ZigzagSpans"/> and <see cref="WaveSpans"/>: rotates
        /// every span from the row frame back to world, then orients the first span onto the wall
        /// anchor - a clear target picks the nearer end, near-ties use the same fixed +X convention
        /// as walls and Place - and snaps its first vertex onto the boundary.</summary>
        static void FinishSpans(List<List<Vec3>> spansOut, double ang,
                                IReadOnlyList<IReadOnlyList<Vec3>> boundary, Vec3? anchor, double spacing)
        {
            double cca = Math.Cos(ang), ssa = Math.Sin(ang);
            foreach (var span in spansOut)
                for (int i = 0; i < span.Count; i++)
                {
                    var p = span[i];
                    span[i] = new Vec3(p.X * cca - p.Y * ssa, p.X * ssa + p.Y * cca, p.Z);
                }
            var first = spansOut[0];
            if (anchor.HasValue)
            {
                var choice = SeamPlacer.OrientOpenPath(
                    first[0], first[first.Count - 1], 0.0, 0.0, anchor, null,
                    SeamAnchor.Centroid(boundary[0]), Math.Abs(spacing));
                if (choice.Reverse) first.Reverse();
            }
            first[0] = ClosestOnBoundary(first[0], boundary);
        }

        /// <summary>How far a boundary connector may detour beyond the straight join, in row
        /// pitches. Two neighbouring rows' ends on one ring are a pitch apart, so following the
        /// ring out and back costs at most about two. A longer arc is a wrap around a hole, and
        /// every row through that hole would wrap the same way: on a 20 mm square hole at a
        /// 3 mm pitch the unbounded rule laid three beads over the hole's lower edge.</summary>
        internal const double MaxConnectorDetourPitches = 2.0;

        // Clipped row ends can sit on opposite sides of a concave corner or of a hole.
        // Follow the shorter arc of the offset ring both ends lie on instead of crossing
        // it, unless that arc detours more than maxDetour beyond the straight join. Ends on
        // different rings (disconnected islands) remain separate paths.
        static List<Vec3>? BoundaryConnector(Vec3 a, Vec3 b, IReadOnlyList<IReadOnlyList<Vec3>> rings,
                                             double maxDetour)
        {
            List<Vec3>? best = null;
            double bestLength = double.PositiveInfinity;
            foreach (var ring in rings)
            {
                if (ring == null || ring.Count < 3) continue;
                var start = SeamAnchor.ClosestOnRing(ring, a);
                var end = SeamAnchor.ClosestOnRing(ring, b);
                if (PolylineOps.Dist2Xy(a, start.Point) > 1e-6 ||
                    PolylineOps.Dist2Xy(b, end.Point) > 1e-6) continue;
                int count = ring.Count;
                if (PolylineOps.Dist2Xy(ring[0], ring[count - 1]) < 1e-12) count--;
                var forward = Arc(start.SegmentIndex + start.T, end.SegmentIndex + end.T,
                                  start.Point, end.Point);
                var reverse = Arc(end.SegmentIndex + end.T, start.SegmentIndex + start.T,
                                  end.Point, start.Point);
                reverse.Reverse();
                foreach (var arc in new[] { forward, reverse })
                {
                    double length = 0;
                    for (int i = 1; i < arc.Count; i++) length += PolylineOps.Dist(arc[i - 1], arc[i]);
                    if (length < bestLength) { bestLength = length; best = arc; }
                }

                List<Vec3> Arc(double from, double to, Vec3 p, Vec3 q)
                {
                    if (to < from) to += count;
                    var arc = new List<Vec3> { new Vec3(p.X, p.Y, a.Z) };
                    for (int i = (int)Math.Floor(from) + 1; i < to; i++)
                    {
                        var v = ring[i % count];
                        arc.Add(new Vec3(v.X, v.Y, a.Z));
                    }
                    arc.Add(new Vec3(q.X, q.Y, a.Z));
                    return arc;
                }
            }
            if (best == null) return null;
            return bestLength <= Math.Sqrt(PolylineOps.Dist2Xy(a, b)) + maxDetour ? best : null;
        }

        static double MinX(List<Vec3> pts)
        {
            double m = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) if (pts[i].X < m) m = pts[i].X;
            return m;
        }

        /// <summary>Boundary penetration below this, mm, is not a crossing. Row ends come
        /// out of Clipper rounded to its 0.1 um grid, so a connector between two rows on a
        /// curved or oblique boundary starts a hair outside the edge it sits on; the old
        /// parametric 1e-6 test called that a proper crossing and cut the zigzag into one
        /// piece per row on every round part (22 travels for 23 rows on a circle).</summary>
        const double ConnectorPenetrationTol = 1e-3;

        /// <summary>True when the straight connector a-b stays inside the region:
        /// no proper crossing of a boundary edge and its midpoint is inside (even-odd).
        /// A crossing is proper only when the connector's ends lie on opposite sides of
        /// the edge line by more than <see cref="ConnectorPenetrationTol"/> each.</summary>
        static bool ConnectorInside(Vec3 a, Vec3 b, IReadOnlyList<IReadOnlyList<Vec3>> polys)
        {
            const double eps = 1e-7;
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx * dx + dy * dy < eps) return true;
            double mx = 0.5 * (a.X + b.X), my = 0.5 * (a.Y + b.Y);
            bool inside = false;
            foreach (var poly in polys)
            {
                if (poly == null || poly.Count < 3) continue;
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                {
                    var p = poly[i]; var q = poly[j];
                    if ((p.Y > my) != (q.Y > my) && mx < (q.X - p.X) * (my - p.Y) / (q.Y - p.Y) + p.X) inside = !inside;
                    double ex = q.X - p.X, ey = q.Y - p.Y;
                    double el = Math.Sqrt(ex * ex + ey * ey);
                    if (el < 1e-12) continue;
                    // signed distances of the connector's ends to the edge line
                    double da = ((a.X - p.X) * ey - (a.Y - p.Y) * ex) / el;
                    double db = ((b.X - p.X) * ey - (b.Y - p.Y) * ex) / el;
                    bool penetrates = (da < -ConnectorPenetrationTol && db > ConnectorPenetrationTol)
                                   || (da > ConnectorPenetrationTol && db < -ConnectorPenetrationTol);
                    if (!penetrates) continue;
                    double den = dx * ey - dy * ex;   // nonzero: the ends straddle the line
                    double u = ((p.X - a.X) * dy - (p.Y - a.Y) * dx) / den;
                    if (u > 1e-6 && u < 1.0 - 1e-6) return false;
                }
            }
            // a connector running along the wall has its midpoint on the boundary
            if (!inside) { var mid = new Vec3(mx, my, a.Z); inside = PolylineOps.Dist2Xy(mid, ClosestOnBoundary(mid, polys)) < 1e-6; }
            return inside;
        }

        /// <summary>Closest point over every ring's XY segments, nearest ring wins - a thin
        /// loop over <see cref="SeamAnchor.ClosestOnRing"/> rather than a private copy of
        /// its point-on-segment math. Z is kept as the query point's own Z (unlike
        /// ClosestOnRing, which interpolates the ring's Z) since this is used for XY
        /// boundary-penetration tests at one input Z, never for ring geometry.</summary>
        static Vec3 ClosestOnBoundary(Vec3 p, IReadOnlyList<IReadOnlyList<Vec3>> polys)
        {
            double best = double.MaxValue; var bestPt = p;
            foreach (var poly in polys)
            {
                if (poly == null || poly.Count < 3) continue;
                var (point, _, _) = SeamAnchor.ClosestOnRing(poly, p);
                double d = PolylineOps.Dist2Xy(p, point);
                if (d < best) { best = d; bestPt = new Vec3(point.X, point.Y, p.Z); }
            }
            return bestPt;
        }
    }
}
