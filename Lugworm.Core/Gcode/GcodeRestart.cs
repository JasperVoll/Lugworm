using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static System.FormattableString;
using Lugworm.Core.Arcs;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Core.Gcode
{
    public enum RestartMode { ZHeight, Percent, Point }

    public sealed class RestartOptions
    {
        public double TravelSpeed = 60.0;
        public double HopSpeed = 0.0;
        public double RetractSpeed = 2.0;
        public double PrimeE = 0.0;
        public double Clearance = 10.0;
        /// <summary>Exact source bytes, when the restart was requested from a file.</summary>
        public byte[]? OriginalBytes;
        /// <summary>True when OriginalBytes came from the source file rather than the
        /// component's documented UTF-8/LF reconstruction contract.</summary>
        public bool ExactByteProvenance;
    }

    /// <summary>Typed modal state captured immediately before the cut. Raw command
    /// lines are retained too, so firmware-specific overrides are restored without
    /// inventing a second dialect model in the restart planner.</summary>
    public sealed class RestartModalState
    {
        public int Tool;
        public bool AbsoluteXYZ = true, RelativeE = true, Millimetres = true;
        public string Plane = "G17";
        /// <summary>True only when the source job carried an explicit G17/G18/G19 word.
        /// Restart files emit a plane word only in that case (row 5.23).</summary>
        public bool PlaneExplicit;
        public double Fan, FeedOverride = 100, FlowOverride = 100, FeedMmMin;
        public Vec3 WorkspaceOffset;
        public readonly List<string> RawStateLines = new List<string>();

        public IReadOnlyList<string> RestoreLines()
        {
            var lines = new List<string>();
            lines.AddRange(RawStateLines);
            lines.Add(AbsoluteXYZ ? "G90" : "G91");
            lines.Add(RelativeE ? "M83" : "M82");
            lines.Add(Millimetres ? "G21" : "G20");
            if (PlaneExplicit) lines.Add(Plane == "G18" || Plane == "G19" ? Plane : "G17");
            if (Tool != 0) lines.Add("T" + Tool.ToString(CultureInfo.InvariantCulture));
            if (Math.Abs(Fan) > 1e-9) lines.Add("M106 S" + Fan.ToString("0.###", CultureInfo.InvariantCulture));
            lines.Add("M220 S" + FeedOverride.ToString("0.###", CultureInfo.InvariantCulture));
            lines.Add("M221 S" + FlowOverride.ToString("0.###", CultureInfo.InvariantCulture));
            if (FeedMmMin > 0) lines.Add("G1 F" + FeedMmMin.ToString("0.###", CultureInfo.InvariantCulture));
            return lines;
        }
    }

    public sealed class RestartPlan
    {
        public List<string> Lines = new List<string>();
        public int CutLine, TotalLines;
        public Vec3 Resume;
        public double PercentAtCut;
        public string LayerAtCut = "";
        public List<List<Vec3>> SkippedPolys = new List<List<Vec3>>();
        public List<List<Vec3>> RemainPolys = new List<List<Vec3>>();
        public RestartModalState Modal = new RestartModalState();
        public bool ApproximateByteProgress;
    }

    /// <summary>Restart planner shared by the GH component and the job-control
    /// layer. It uses the same fail-closed parser as the robot gate, keeps physical
    /// and logical coordinates separate, and only replaces the selected motion when
    /// a point cut falls in the middle of that motion.</summary>
    public static class GcodeRestart
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F3(double v) => GcodeText.F3(v);
        static string F5(double v) => GcodeText.F5(v);

        sealed class Motion
        {
            public ParsedMove Move;
            public int Line;
            public Vec3 Start, End;
            public bool Extrude, Arc;
            public long ByteStart, ByteEnd;
            public double EBefore, EAfter;
            public bool RelativeE, AbsoluteXYZ;
            public double T;
        }

        public static RestartPlan Plan(IReadOnlyList<string> lines, RestartMode mode,
                                       double zTarget, double percent, Vec3 point,
                                       RestartOptions? opt = null)
        {
            opt ??= new RestartOptions();
            ValidateOptions(opt);
            if (lines == null || lines.Count == 0) throw new InvalidOperationException("no G-code lines supplied");

            var source = NormalizeLines(lines);
            byte[] bytes = opt.OriginalBytes ?? Encoding.UTF8.GetBytes(Serialize(source));
            bool exact = opt.OriginalBytes != null && opt.ExactByteProvenance;
            var byteEnds = ByteEnds(source, bytes, exact);
            long totalBytes = bytes.LongLength;
            ParsedProgram program = GcodeParser.Parse(source);
            var parsedByLine = program.Moves.GroupBy(m => m.LineNumber)
                                             .ToDictionary(g => g.Key, g => g.ToList());
            var motions = new List<Motion>();
            bool absXYZ = true, relE = true;
            for (int i = 0; i < source.Count; i++)
            {
                int lineNo = i + 1;
                if (!parsedByLine.TryGetValue(lineNo, out var parsed))
                {
                    UpdateModes(source[i], ref absXYZ, ref relE);
                    continue;
                }
                foreach (var m in parsed)
                {
                    if (PolylineOps.Dist(new Vec3(m.StartX, m.StartY, m.StartZ), new Vec3(m.X, m.Y, m.Z)) <= 1e-9 && !m.Arc)
                    {
                        UpdateModes(source[i], ref absXYZ, ref relE);
                        continue;
                    }
                    long end = byteEnds[Math.Min(i, byteEnds.Length - 1)];
                    long start = i == 0 ? 0 : byteEnds[i - 1];
                    motions.Add(new Motion
                    {
                        Move = m,
                        Line = i,
                        Start = new Vec3(m.StartX, m.StartY, m.StartZ),
                        End = new Vec3(m.X, m.Y, m.Z),
                        Extrude = m.Extrude,
                        Arc = m.Arc,
                        ByteStart = start,
                        ByteEnd = end,
                        EBefore = m.PhysicalEStart,
                        EAfter = m.Coordinates.PhysicalE,
                        RelativeE = relE,
                        AbsoluteXYZ = absXYZ,
                    });
                }
                UpdateModes(source[i], ref absXYZ, ref relE);
            }
            if (motions.Count == 0) throw new InvalidOperationException("no geometric G0..G3 motion lines found in the G-code");

            Motion cut;
            double cutT = 0;
            switch (mode)
            {
                case RestartMode.ZHeight:
                    cut = motions.FirstOrDefault(m => m.Extrude && m.End.Z >= zTarget - 1e-6)
                        ?? throw new InvalidOperationException($"no extruding move at or above Z {F3(zTarget)}");
                    break;
                case RestartMode.Percent:
                    if (!(percent > 0 && percent < 100))
                        throw new InvalidOperationException($"percent must be inside (0..100), got {percent.ToString("0.###", Inv)}");
                    long target = (long)Math.Round(totalBytes * percent / 100.0);
                    cut = motions.FirstOrDefault(m => m.ByteEnd >= target) ?? motions[motions.Count - 1];
                    break;
                case RestartMode.Point:
                    cut = motions.Where(m => m.Extrude).OrderBy(m => Closest(m, point, out _)).FirstOrDefault()
                        ?? throw new InvalidOperationException("no extruding geometry is available for point restart");
                    Closest(cut, point, out cutT);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
            cut.T = mode == RestartMode.Point ? Math.Max(0, Math.Min(1, cutT)) : 0;
            Vec3 resume = cut.T > 0 ? ToMove(cut).PointAt(cut.T) : cut.Start;
            var modal = CaptureModal(source, cut.Line, cut.Move);

            var skipped = new List<List<Vec3>>();
            var remain = new List<List<Vec3>>();
            BuildPreview(motions, cut, skipped, remain);
            double maxZ = resume.Z;
            foreach (var m in motions)
                if (m.Line < cut.Line || (m.Line == cut.Line && cut.T > 0))
                    if (m.Extrude) maxZ = Math.Max(maxZ, m.End.Z);

            double safeZ = Math.Max(resume.Z, maxZ) + Math.Max(0, opt.Clearance);
            int tF = Math.Max(1, (int)Math.Round(opt.TravelSpeed * 60));
            int hF = opt.HopSpeed > 1e-9 ? Math.Max(1, (int)Math.Round(opt.HopSpeed * 60)) : tF;
            int rF = Math.Max(1, (int)Math.Round(opt.RetractSpeed * 60));
            var output = new List<string>();
            int firstMotion = motions[0].Line;
            for (int i = 0; i < firstMotion; i++) output.Add(source[i]);
            foreach (var line in modal.RestoreLines())
            {
                // The original pre-motion header is retained verbatim.  Do not
                // replay an identical modal line a second time; modal changes
                // that occurred after the header still come from the snapshot.
                if (modal.RawStateLines.Contains(line) && output.Contains(line)) continue;
                output.Add(line);
            }
            output.Add(Invariant($"; ==== LUGWORM RESTART: line {cut.Line + 1}/{source.Count}  Z {F3(resume.Z)}  ") +
                       Invariant($"{(totalBytes > 0 ? 100.0 * (cut.T > 0 ? cut.ByteStart + (cut.ByteEnd - cut.ByteStart) * cut.T : cut.ByteStart) / totalBytes : 0):0.0}%") +
                       (FindLayer(source, cut.Line) is string layer && layer.Length > 0 ? "  (" + layer + ")" : "") + " ====");
            output.Add("G90");
            output.Add(Invariant($"G0 Z{F3(safeZ)} F{tF}"));
            output.Add(Invariant($"G0 X{F3(resume.X)} Y{F3(resume.Y)} Z{F3(safeZ)} F{tF}"));
            output.Add(Invariant($"G0 X{F3(resume.X)} Y{F3(resume.Y)} Z{F3(resume.Z)} F{hF}"));

            bool split = cut.T > 1e-9 && cut.T < 1 - 1e-9;
            double logicalStartE = cut.RelativeE ? cut.Move.LogicalEStart
                                                   : cut.Move.LogicalEStart + cut.Move.E * cut.T;
            if (split)
            {
                // The machine is at physical resume, while G92 restores the
                // original logical workspace at that same point. This is the
                // distinction that keeps G92 offsets correct across restarts.
                var off = cut.Move.Coordinates.WorkspaceOffset;
                output.Add($"G92 X{F3(resume.X + off.X)} Y{F3(resume.Y + off.Y)} Z{F3(resume.Z + off.Z)}");
                output.Add(cut.AbsoluteXYZ ? "G90" : "G91");
                if (!cut.RelativeE) output.Add($"G92 E{F5(logicalStartE)}");
                AppendPrime(output, cut.RelativeE, logicalStartE, opt.PrimeE, rF);
                output.Add(BuildResidual(source[cut.Line], cut, cut.T));
                output.Add($"G92 X{F3(cut.Move.Coordinates.Logical.X)} Y{F3(cut.Move.Coordinates.Logical.Y)} Z{F3(cut.Move.Coordinates.Logical.Z)}");
                if (!cut.RelativeE) output.Add($"G92 E{F5(cut.Move.Coordinates.LogicalE)}");
            }
            else
            {
                // Set the modal workspace to the original logical coordinates at the
                // physical split point before the untouched tail resumes. A cut at the
                // move's END (t≈1, move skipped from the tail) must restore the move's
                // logical ENDPOINT - restoring its start would displace every
                // subsequent absolute move by the move's length.
                bool atEnd = cut.T >= 1 - 1e-9;
                double lx = atEnd ? cut.Move.Coordinates.Logical.X : cut.Move.LogicalStartX;
                double ly = atEnd ? cut.Move.Coordinates.Logical.Y : cut.Move.LogicalStartY;
                double lz = atEnd ? cut.Move.Coordinates.Logical.Z : cut.Move.LogicalStartZ;
                output.Add($"G92 X{F3(lx)} Y{F3(ly)} Z{F3(lz)}");
                output.Add(cut.AbsoluteXYZ ? "G90" : "G91");
                if (!cut.RelativeE) output.Add($"G92 E{F5(logicalStartE)}");
                AppendPrime(output, cut.RelativeE, logicalStartE, opt.PrimeE, rF);
            }
            if (cut.RelativeE) output.Add("M83"); else output.Add("M82");
            output.Add("; ==== original tail ====");
            int tailStart = cut.Line + (split || cut.T >= 1 - 1e-9 ? 1 : 0);
            for (int i = tailStart; i < source.Count; i++) output.Add(source[i]);

            int cutByte = (int)Math.Max(0, Math.Min(totalBytes,
                cut.T > 0 ? cut.ByteStart + (cut.ByteEnd - cut.ByteStart) * cut.T : cut.ByteStart));
            return new RestartPlan
            {
                Lines = output,
                CutLine = cut.Line,
                TotalLines = source.Count,
                Resume = resume,
                PercentAtCut = totalBytes > 0 ? 100.0 * cutByte / totalBytes : 0,
                LayerAtCut = FindLayer(source, cut.Line),
                SkippedPolys = skipped,
                RemainPolys = remain,
                Modal = modal,
                ApproximateByteProgress = !exact,
            };
        }

        static void ValidateOptions(RestartOptions o)
        {
            Positive(o.TravelSpeed, nameof(o.TravelSpeed));
            Positive(o.RetractSpeed, nameof(o.RetractSpeed));
            NonNegative(o.HopSpeed, nameof(o.HopSpeed));
            NonNegative(o.PrimeE, nameof(o.PrimeE));
            NonNegative(o.Clearance, nameof(o.Clearance));
        }
        static void Positive(double d, string n) { if (double.IsNaN(d) || double.IsInfinity(d) || !(d > 0)) throw new ArgumentException(n + " must be finite and > 0", n); }
        static void NonNegative(double d, string n) { if (double.IsNaN(d) || double.IsInfinity(d) || d < 0) throw new ArgumentException(n + " must be finite and >= 0", n); }

        static List<string> NormalizeLines(IReadOnlyList<string> lines)
            => lines.Select(s => (s ?? "").TrimEnd('\r')).ToList();
        static string Serialize(IReadOnlyList<string> lines) => string.Join("\n", lines) + "\n";

        static long[] ByteEnds(IReadOnlyList<string> lines, byte[] bytes, bool exact)
            => GcodeText.LineByteEnds(lines, exact ? bytes : null);

        static void UpdateModes(string raw, ref bool absXYZ, ref bool relE)
        {
            if (KlipperCommands.IsExtended(raw)) return;   // not a G/M word; changes no mode
            var line = GcodeText.Lex(raw);
            if (line.CommandLetter == 'G' && line.CommandNumber == 90) absXYZ = true;
            else if (line.CommandLetter == 'G' && line.CommandNumber == 91) absXYZ = false;
            else if (line.CommandLetter == 'M' && line.CommandNumber == 82) relE = false;
            else if (line.CommandLetter == 'M' && line.CommandNumber == 83) relE = true;
        }

        static RestartModalState CaptureModal(IReadOnlyList<string> lines, int cutLine, ParsedMove cut)
        {
            var s = new RestartModalState
            {
                WorkspaceOffset = cut.Coordinates.WorkspaceOffset,
                AbsoluteXYZ = true,
                RelativeE = true,
            };
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cutLine; i++)
            {
                string stripped = GcodeText.Strip(lines[i]);
                if (stripped.Length == 0) continue;
                if (KlipperCommands.TryGetName(stripped, out string extended))
                {
                    // Klipper's pressure-advance / velocity-limit settings are modal
                    // state like M900/M201 and are restored the same raw way
                    if (extended == KlipperCommands.SetPressureAdvance || extended == KlipperCommands.SetVelocityLimit)
                        raw[extended] = stripped;
                    continue;
                }
                var line = GcodeText.Lex(stripped);
                if (line.CommandLetter == 'G')
                {
                    if (line.CommandNumber == 90) s.AbsoluteXYZ = true;
                    else if (line.CommandNumber == 91) s.AbsoluteXYZ = false;
                    else if (line.CommandNumber == 20) s.Millimetres = false;
                    else if (line.CommandNumber == 21) s.Millimetres = true;
                    else if (line.CommandNumber == 17 || line.CommandNumber == 18 || line.CommandNumber == 19)
                    {
                        s.Plane = "G" + line.CommandNumber.ToString(Inv);
                        s.PlaneExplicit = true;
                    }
                }
                else if (line.CommandLetter == 'M')
                {
                    if (line.CommandNumber == 82) s.RelativeE = false;
                    else if (line.CommandNumber == 83) s.RelativeE = true;
                    else if (line.CommandNumber == 106) s.Fan = Parameter(line, 'S', s.Fan);
                    else if (line.CommandNumber == 107) s.Fan = 0;
                    else if (line.CommandNumber == 220) s.FeedOverride = Parameter(line, 'S', s.FeedOverride);
                    else if (line.CommandNumber == 221) s.FlowOverride = Parameter(line, 'S', s.FlowOverride);
                    if (line.CommandNumber == 201 || line.CommandNumber == 204 || line.CommandNumber == 205 ||
                        line.CommandNumber == 566 || line.CommandNumber == 572 || line.CommandNumber == 900)
                        raw[line.Command] = stripped;
                }
                else if (line.CommandLetter == 'T') s.Tool = line.CommandNumber;
                foreach (var w in GcodeText.Words(stripped)) if (w.w == 'F') s.FeedMmMin = w.v;
            }
            foreach (var command in new[] { "M201", "M204", "M205", "M566", "M572", "M900",
                                            KlipperCommands.SetVelocityLimit, KlipperCommands.SetPressureAdvance })
                if (raw.TryGetValue(command, out var value)) s.RawStateLines.Add(value);
            return s;
        }

        static double Parameter(GcodeLine line, char letter, double fallback)
        { foreach (var w in line.Words) if (w.Letter == letter) return w.Value; return fallback; }

        static string FindLayer(IReadOnlyList<string> lines, int line)
        {
            string layer = "";
            for (int i = 0; i < line && i < lines.Count; i++)
                if (lines[i].TrimStart().StartsWith("; layer", StringComparison.OrdinalIgnoreCase)) layer = lines[i].Trim().TrimStart(';', ' ');
            return layer;
        }

        static Move ToMove(Motion m)
        {
            if (!m.Arc) return Move.Line(m.Start, m.End);
            return Move.Arc(m.Start, m.End, m.Start.X + m.Move.I, m.Start.Y + m.Move.J, m.Move.G == 2,
                            Math.Sqrt(m.Move.I * m.Move.I + m.Move.J * m.Move.J));
        }

        static double Closest(Motion m, Vec3 p, out double t)
        {
            var move = ToMove(m);
            if (!m.Arc)
            {
                double dx = m.End.X - m.Start.X, dy = m.End.Y - m.Start.Y, dz = m.End.Z - m.Start.Z;
                double den = dx * dx + dy * dy + dz * dz;
                t = den > 1e-12 ? ((p.X - m.Start.X) * dx + (p.Y - m.Start.Y) * dy + (p.Z - m.Start.Z) * dz) / den : 0;
                t = Math.Max(0, Math.Min(1, t));
                return PolylineOps.Dist2(move.PointAt(t), p);
            }
            double a0 = Math.Atan2(m.Start.Y - move.Cy, m.Start.X - move.Cx);
            double a1 = Math.Atan2(m.End.Y - move.Cy, m.End.X - move.Cx);
            double da = a1 - a0;
            if (move.Cw) { while (da > 0) da -= 2 * Math.PI; while (da < -2 * Math.PI) da += 2 * Math.PI; }
            else { while (da < 0) da += 2 * Math.PI; while (da > 2 * Math.PI) da -= 2 * Math.PI; }
            double target = Math.Atan2(p.Y - move.Cy, p.X - move.Cx);
            double best = double.MaxValue, bestT = 0;
            for (int k = -2; k <= 2; k++)
            {
                double candidate = target + k * 2 * Math.PI;
                double u = (candidate - a0) / da;
                if (u >= 0 && u <= 1)
                {
                    double d = PolylineOps.Dist2(move.PointAt(u), p);
                    if (d < best) { best = d; bestT = u; }
                }
            }
            double d0 = PolylineOps.Dist2(m.Start, p), d1 = PolylineOps.Dist2(m.End, p);
            if (d0 < best) { best = d0; bestT = 0; }
            if (d1 < best) { best = d1; bestT = 1; }
            t = bestT; return best;
        }

        static void BuildPreview(IReadOnlyList<Motion> motions, Motion cut,
                                 List<List<Vec3>> skipped, List<List<Vec3>> remain)
        {
            List<Vec3>? a = null, b = null;
            foreach (var m in motions)
            {
                bool before = m.Line < cut.Line || (m.Line == cut.Line && cut.T >= 1 - 1e-9);
                if (!m.Extrude) { Flush(ref a, skipped); Flush(ref b, remain); continue; }
                var mv = ToMove(m);
                if (m.Line < cut.Line || (m.Line == cut.Line && cut.T > 0))
                {
                    if (m.Line == cut.Line && cut.T > 0)
                    {
                        var split = mv.PointAt(cut.T);
                        a ??= new List<Vec3> { m.Start }; a.Add(split);
                        b ??= new List<Vec3> { split }; b.Add(m.End);
                    }
                    else { a ??= new List<Vec3> { m.Start }; a.Add(m.End); }
                }
                else { b ??= new List<Vec3> { m.Start }; b.Add(m.End); }
                if (before) { Flush(ref b, remain); } else { Flush(ref a, skipped); }
            }
            Flush(ref a, skipped); Flush(ref b, remain);
        }
        static void Flush(ref List<Vec3>? p, List<List<Vec3>> into) { if (p != null && p.Count >= 2) into.Add(p); p = null; }

        static void AppendPrime(List<string> output, bool relativeE, double logicalE, double prime, int feed)
        {
            if (!(prime > 0)) return;
            if (relativeE) output.Add(Invariant($"G1 E{F5(prime)} F{feed}"));
            else
            {
                output.Add("M83");
                output.Add(Invariant($"G1 E{F5(prime)} F{feed}"));
                output.Add("M82");
                output.Add(Invariant($"G92 E{F5(logicalE)}"));
            }
        }

        static string BuildResidual(string raw, Motion m, double t)
        {
            var line = GcodeText.Lex(raw);
            bool hasX = false, hasY = false, hasZ = false, hasE = false, hasF = false;
            foreach (var w in line.Words)
            {
                if (w.Letter == 'X') hasX = true;
                else if (w.Letter == 'Y') hasY = true;
                else if (w.Letter == 'Z') hasZ = true;
                else if (w.Letter == 'E') hasE = true; else if (w.Letter == 'F') hasF = true;
            }
            var sb = new StringBuilder("G" + m.Move.G.ToString(Inv));
            double dx = m.Move.Coordinates.Logical.X - m.Move.LogicalStartX;
            double dy = m.Move.Coordinates.Logical.Y - m.Move.LogicalStartY;
            double dz = m.Move.Coordinates.Logical.Z - m.Move.LogicalStartZ;
            if (hasX) sb.Append(" X" + (m.AbsoluteXYZ ? m.Move.Coordinates.Logical.X : dx * (1 - t)).ToString("0.#####", Inv));
            if (hasY) sb.Append(" Y" + (m.AbsoluteXYZ ? m.Move.Coordinates.Logical.Y : dy * (1 - t)).ToString("0.#####", Inv));
            if (hasZ) sb.Append(" Z" + (m.AbsoluteXYZ ? m.Move.Coordinates.Logical.Z : dz * (1 - t)).ToString("0.#####", Inv));
            if (m.Arc)
            {
                var center = new Vec3(m.Start.X + m.Move.I, m.Start.Y + m.Move.J, 0);
                var split = ToMove(m).PointAt(t);
                sb.Append(" I" + (center.X - split.X).ToString("0.#####", Inv));
                sb.Append(" J" + (center.Y - split.Y).ToString("0.#####", Inv));
            }
            if (hasE)
            {
                double e = m.RelativeE ? m.Move.E * (1 - t) : m.Move.Coordinates.LogicalE;
                sb.Append(" E" + e.ToString("0.#####", Inv));
            }
            if (hasF) foreach (var w in line.Words) if (w.Letter == 'F') sb.Append(" F" + w.Value.ToString("0.#####", Inv));
            return sb.ToString();
        }
    }
}
