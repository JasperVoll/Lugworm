using System;
using System.Collections.Generic;
using Lugworm.Core.Geometry;
using Lugworm.Core.Gcode;
namespace Lugworm.Core.Robot
{
    public readonly struct GcodeCoordinateState
    {
        public readonly Vec3 Logical, WorkspaceOffset, Physical;
        public readonly double LogicalE, EOffset, PhysicalE;
        public GcodeCoordinateState(Vec3 logical, Vec3 workspaceOffset, Vec3 physical,
                                    double logicalE, double eOffset, double physicalE)
        { Logical = logical; WorkspaceOffset = workspaceOffset; Physical = physical; LogicalE = logicalE; EOffset = eOffset; PhysicalE = physicalE; }
    }

    public readonly struct ParsedMove
    {
        public readonly double X, Y, Z, F, I, J, E;
        public readonly double StartX, StartY, StartZ;
        public readonly double LogicalStartX, LogicalStartY, LogicalStartZ, LogicalEStart, PhysicalEStart;
        public readonly bool Extrude, Arc;
        public readonly int G;
        public readonly GcodeCoordinateState Coordinates;
        public readonly int LineNumber;
        public ParsedMove(double x, double y, double z, double f, bool extrude, bool arc,
                          double i, double j, int g, double e = 0)
            : this(x, y, z, f, extrude, arc, i, j, g, e,
                   new GcodeCoordinateState(new Vec3(x, y, z), default,
                                            new Vec3(x, y, z), 0, 0, e), x, y, z, x, y, z, 0, 0, 0)
        { }
        internal ParsedMove(double x, double y, double z, double f, bool extrude, bool arc,
                            double i, double j, int g, double e, GcodeCoordinateState state)
            : this(x, y, z, f, extrude, arc, i, j, g, e, state, x, y, z, x, y, z, 0, 0, 0) { }
        internal ParsedMove(double x, double y, double z, double f, bool extrude, bool arc,
                            double i, double j, int g, double e, GcodeCoordinateState state,
                            double startX, double startY, double startZ,
                            double logicalStartX, double logicalStartY, double logicalStartZ,
                            double logicalEStart, double physicalEStart, int lineNumber)
        { X = x; Y = y; Z = z; F = f; Extrude = extrude; Arc = arc; I = i; J = j; G = g; E = e; Coordinates = state; StartX = startX; StartY = startY; StartZ = startZ; LogicalStartX = logicalStartX; LogicalStartY = logicalStartY; LogicalStartZ = logicalStartZ; LogicalEStart = logicalEStart; PhysicalEStart = physicalEStart; LineNumber = lineNumber; }
    }

    public sealed class ParsedProgram
    {
        public readonly List<ParsedMove> Moves = new List<ParsedMove>();
        public bool HasArcs, HasEMode, RelativeE = true, HasHome, HasPartialHome;
        // a G28 AFTER motion must not satisfy "homed before the first move"
        public bool HasHomeBeforeFirstMotion;
        public bool HomePoseKnown, HasFirstMoveStart;
        public Vec3 HomePosition, FirstMoveStart;
        public GcodeCoordinateState FinalCoordinates;
    }

    /// <summary>Fail-closed parser for the modeled Marlin/RRF subset. Unsupported
    /// executable commands are rejected with a source line number.</summary>
    public static class GcodeParser
    {
        const double ArcRadiusToleranceMm = 0.1;

        /// <param name="allowPerAxisHome">Per-axis <c>G28 X/Y/Z</c> in a job file is a
        /// kinematics question, not a universal one: on the SCARA, joint homing moves
        /// both Cartesian axes, so a per-axis home leaves the position undefined and is
        /// refused (the Jog component's attended two-press actions are the only path).
        /// On a Cartesian machine each axis homes independently and per-axis G28 is
        /// ordinary practice, so the machine profile routes <c>true</c> here. The
        /// parser still cannot know the post-home position (no calibrated pose exists
        /// per axis), so a per-axis home clears <see cref="ParsedProgram.HomePoseKnown"/>
        /// and sets <see cref="ParsedProgram.HasPartialHome"/>.</param>
        public static ParsedProgram Parse(IEnumerable<string> lines, Vec3? calibratedHomePose = null,
                                          bool allowPerAxisHome = false)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            var prog = new ParsedProgram();
            Vec3 physical = calibratedHomePose ?? new Vec3(0, 0, 0);
            Vec3 logical = physical, offset = default;
            double physicalE = 0, logicalE = 0, eOffset = 0, feed = 0;
            bool relE = true, absXYZ = true;
            prog.HomePoseKnown = calibratedHomePose.HasValue;
            int lineNumber = 0;

            foreach (var raw in lines)
            {
                lineNumber++;
                // Klipper extended commands are not G/M words: a job may carry exactly
                // the ones the generator emits (pressure advance, velocity limits, print
                // stats) - they move nothing. Anything else, user macros included, is
                // refused like M98: the job must be self-contained at the safety boundary.
                if (KlipperCommands.TryGetName(raw, out string extended))
                {
                    if (!KlipperCommands.IsJobAllowed(extended))
                        throw new FormatException($"line {lineNumber}: unsupported executable command {extended} - " +
                                                  $"a job may only carry {KlipperCommands.JobAllowedList}");
                    continue;
                }
                // M117/M118 carry free text ("M117 Printing...") that is not G-code words;
                // they move nothing, so the line is accepted without lexing its text
                if (GcodeText.IsFreeTextMessage(raw)) continue;
                GcodeLine line = GcodeText.Lex(
                    allowPerAxisHome ? NormalizeBareHomeAxisWords(raw) : raw, lineNumber);
                if (line.CommandLetter == '\0') continue;
                var words = Parameters(line);

                if (line.CommandLetter == 'T')
                {
                    if (line.CommandNumber > 9 || words.Count != 0)
                        Reject(line, "unsupported tool-select command");
                    continue;
                }
                if (line.CommandLetter == 'G')
                {
                    switch (line.CommandNumber)
                    {
                        case 0:
                        case 1:
                        case 2:
                        case 3:
                            ParseMove(line, words, ref physical, ref logical, ref offset,
                                ref physicalE, ref logicalE, ref eOffset, ref feed, relE, absXYZ, prog);
                            continue;
                        case 4: EnsureOnly(line, words, 'P', 'S'); continue;
                        case 20: Reject(line, "G20 (inch units) is not supported - regenerate the job in millimetres"); continue;
                        case 17: case 18: case 19: EnsureNoParameters(line, words); continue;
                        case 21: EnsureNoParameters(line, words); continue;
                        case 28: ParseHome(line, words, ref physical, ref logical, ref offset, prog, calibratedHomePose, allowPerAxisHome); continue;
                        case 90: EnsureNoParameters(line, words); absXYZ = true; continue;
                        case 91: EnsureNoParameters(line, words); absXYZ = false; continue;
                        case 92: ParseG92(line, words, physical, ref logical, ref offset, ref logicalE, ref eOffset, physicalE); continue;
                        default: Reject(line, $"unsupported executable command G{line.CommandNumber}"); continue;
                    }
                }
                if (line.CommandLetter == 'M')
                {
                    switch (line.CommandNumber)
                    {
                        case 82: EnsureNoParameters(line, words); relE = false; prog.HasEMode = true; continue;
                        case 83: EnsureNoParameters(line, words); relE = true; prog.HasEMode = true; continue;
                        case 98: Reject(line, "M98 subprogram calls are not supported at the safety boundary"); continue;
                        case 32: case 37: Reject(line, $"M{line.CommandNumber} nested job control is not allowed in an uploaded job"); continue;
                        case 999: Reject(line, "M999 firmware reset is not allowed inside a job"); continue;
                        default:
                            if (!IsAllowedNonMotionM(line.CommandNumber)) Reject(line, $"unsupported executable command M{line.CommandNumber}");
                            continue;
                    }
                }
                Reject(line, $"unsupported executable command {line.Command}");
            }
            prog.RelativeE = relE;
            prog.FinalCoordinates = new GcodeCoordinateState(logical, offset, physical, logicalE, eOffset, physicalE);
            return prog;
        }

        static List<GcodeWord> Parameters(GcodeLine line)
        {
            var result = new List<GcodeWord>();
            bool commandSeen = false;
            foreach (var word in line.Words)
            {
                if (!commandSeen && word.Letter == 'N') continue;
                if (!commandSeen && word.Letter == line.CommandLetter) { commandSeen = true; continue; }
                if (word.Letter != 'N') result.Add(word);
            }
            return result;
        }

        /// <summary>M-codes a job may carry because they move nothing: heaters and fans,
        /// motion limits, the reports and status lines every slicer emits (M105 temperature
        /// report, M73 progress, M110 line number, M113 host keepalive, M118/M119 echo and
        /// endstop report, M155 auto-report) and the program end (M2). M30 stays refused:
        /// on Marlin it deletes a file from the card.</summary>
        static bool IsAllowedNonMotionM(int code)
        {
            switch (code)
            {
                case 0:
                case 1:
                case 2:
                case 17:
                case 18:
                case 84:
                case 104:
                case 106:
                case 107:
                case 109:
                case 140:
                case 141:
                case 190:
                case 191:
                case 201:
                case 203:
                case 204:
                case 205:
                case 400:
                case 566:
                case 572:
                case 900:
                case 906:
                case 911:
                case 114:
                case 115:
                case 117:
                case 220:
                case 221:
                case 73:
                case 105:
                case 110:
                case 113:
                case 118:
                case 119:
                case 155:
                    return true;
                default: return false;
            }
        }

        static void ParseMove(GcodeLine line, List<GcodeWord> words,
                              ref Vec3 physical, ref Vec3 logical, ref Vec3 offset,
                              ref double physicalE, ref double logicalE, ref double eOffset,
                              ref double feed, bool relE, bool absXYZ, ParsedProgram prog)
        {
            double oldX = physical.X, oldY = physical.Y, oldZ = physical.Z;
            double logicalOldX = logical.X, logicalOldY = logical.Y, logicalOldZ = logical.Z;
            double logicalEOld = logicalE;
            double physicalEOld = physicalE;
            double nx = logical.X, ny = logical.Y, nz = logical.Z;
            double ni = 0, nj = 0, eValue = 0;
            bool hasE = false, hasI = false, hasJ = false;
            foreach (var word in words)
            {
                switch (word.Letter)
                {
                    case 'X': nx = absXYZ ? word.Value : logical.X + word.Value; break;
                    case 'Y': ny = absXYZ ? word.Value : logical.Y + word.Value; break;
                    case 'Z': nz = absXYZ ? word.Value : logical.Z + word.Value; break;
                    case 'F': if (word.Value < 0) Reject(line, "feed must not be negative"); feed = word.Value; break;
                    case 'I': ni = word.Value; hasI = true; break;
                    case 'J': nj = word.Value; hasJ = true; break;
                    case 'E': eValue = word.Value; hasE = true; break;
                    case 'R':
                        if (line.CommandNumber >= 2) Reject(line, "R-format arcs (R…) are not supported - post-process to I/J form");
                        Reject(line, "R is not a modeled linear-move parameter"); break;
                    default: Reject(line, $"unsupported parameter {word.Letter} on {line.Command}"); break;
                }
            }
            bool arc = line.CommandNumber >= 2;
            if (arc && !hasI && !hasJ) Reject(line, "G2/G3 without I or J - arc center is undefined and the swept path cannot be validated");
            Vec3 target = new Vec3(nx - offset.X, ny - offset.Y, nz - offset.Z);
            if (!prog.HasFirstMoveStart) { prog.HasFirstMoveStart = true; prog.FirstMoveStart = physical; }
            if (arc)
            {
                double cx = oldX + ni, cy = oldY + nj;
                double r0 = Math.Sqrt((oldX - cx) * (oldX - cx) + (oldY - cy) * (oldY - cy));
                double r1 = Math.Sqrt((target.X - cx) * (target.X - cx) + (target.Y - cy) * (target.Y - cy));
                if (Math.Abs(r0 - r1) > ArcRadiusToleranceMm)
                    Reject(line, FormattableString.Invariant($"arc radius mismatch ({r0:0.###}mm vs {r1:0.###}mm) exceeds {ArcRadiusToleranceMm:0.###}mm tolerance"));
            }
            double de = 0;
            if (hasE)
            {
                double nextLogicalE = relE ? logicalE + eValue : eValue;
                de = nextLogicalE - logicalE; logicalE = nextLogicalE; physicalE += de; eOffset = logicalE - physicalE;
            }
            logical = new Vec3(nx, ny, nz); physical = target;
            var state = new GcodeCoordinateState(logical, offset, physical, logicalE, eOffset, physicalE);
            prog.Moves.Add(new ParsedMove(target.X, target.Y, target.Z, feed, de > 1e-9, arc, ni, nj, line.CommandNumber, de, state, oldX, oldY, oldZ,
                logicalOldX, logicalOldY, logicalOldZ, logicalEOld, physicalEOld, line.LineNumber));
            if (arc) prog.HasArcs = true;
        }

        static void ParseHome(GcodeLine line, List<GcodeWord> words,
                              ref Vec3 physical, ref Vec3 logical, ref Vec3 offset,
                              ParsedProgram prog, Vec3? calibratedHomePose, bool allowPerAxisHome)
        {
            if (words.Count == 0)
            {
                prog.HasHome = true;
                if (prog.Moves.Count == 0) prog.HasHomeBeforeFirstMotion = true;
                if (calibratedHomePose.HasValue)
                {
                    physical = calibratedHomePose.Value; logical = physical + offset;
                    prog.HomePosition = physical; prog.HomePoseKnown = true;
                }
                else prog.HomePoseKnown = false;
                return;
            }
            foreach (var word in words)
            {
                if (word.Letter == 'X' || word.Letter == 'Y' || word.Letter == 'Z')
                {
                    if (allowPerAxisHome) continue;
                    // SCARA: joint homing changes both Cartesian coordinates, so a
                    // per-axis home is refused in unattended job files.
                    // DuetJogComponent keeps these attended actions behind a
                    // two-press arm; a Cartesian profile routes allowPerAxisHome
                    // instead of relaxing this boundary for everyone.
                    Reject(line, word.Letter == 'Z'
                        ? "only a full G28 is supported at the safety boundary"
                        : "per-axis G28 X/Y is unsupported - SCARA joint homing changes both Cartesian coordinates");
                }
                else Reject(line, $"unsupported parameter {word.Letter} on G28");
            }
            // Per-axis home on a Cartesian machine: the homed axes moved to their
            // endstops, so the parser's tracked position is no longer trustworthy.
            // The position is left as-is (the same model limitation an unanchored
            // full G28 has) and the pose is marked unknown.
            prog.HasPartialHome = true;
            prog.HomePoseKnown = false;
        }

        static void ParseG92(GcodeLine line, List<GcodeWord> words, Vec3 physical,
                             ref Vec3 logical, ref Vec3 offset, ref double logicalE,
                             ref double eOffset, double physicalE)
        {
            if (words.Count == 0)
            {
                logical = new Vec3(0, 0, 0); offset = new Vec3(-physical.X, -physical.Y, -physical.Z);
                logicalE = 0; eOffset = -physicalE; return;
            }
            double x = logical.X, y = logical.Y, z = logical.Z, e = logicalE;
            foreach (var word in words)
            {
                switch (word.Letter)
                {
                    case 'X': x = word.Value; offset = new Vec3(x - physical.X, offset.Y, offset.Z); break;
                    case 'Y': y = word.Value; offset = new Vec3(offset.X, y - physical.Y, offset.Z); break;
                    case 'Z': z = word.Value; offset = new Vec3(offset.X, offset.Y, z - physical.Z); break;
                    case 'E': e = word.Value; eOffset = e - physicalE; break;
                    default: Reject(line, $"unsupported parameter {word.Letter} on G92"); break;
                }
            }
            logical = new Vec3(x, y, z); logicalE = e;
        }

        /// <summary>RRF's own grammar for per-axis homing is a bare axis letter -
        /// "G28 X" - while the strict lexer refuses any word without a numeric value,
        /// on purpose, everywhere. Rather than weaken the lexer for every command,
        /// bare X/Y/Z words on a G28 line get the explicit "0" the firmware ignores
        /// anyway ("G28 X0" homes X exactly like "G28 X"). Only reachable with
        /// allowPerAxisHome, i.e. only for Cartesian machine profiles.</summary>
        static string NormalizeBareHomeAxisWords(string raw)
        {
            string stripped = GcodeText.Strip(raw);
            if (stripped.Length < 3
                || (stripped[0] != 'G' && stripped[0] != 'g')
                || stripped[1] != '2' || stripped[2] != '8'
                || (stripped.Length > 3 && !char.IsWhiteSpace(stripped[3])))
                return raw;
            var sb = new System.Text.StringBuilder(stripped.Length + 6);
            for (int i = 0; i < stripped.Length; i++)
            {
                char c = stripped[i];
                sb.Append(c);
                if (i >= 4 && (c == 'X' || c == 'Y' || c == 'Z' || c == 'x' || c == 'y' || c == 'z')
                    && (i + 1 == stripped.Length || char.IsWhiteSpace(stripped[i + 1]))
                    && char.IsWhiteSpace(stripped[i - 1]))
                    sb.Append('0');
            }
            return sb.ToString();
        }

        static void EnsureNoParameters(GcodeLine line, List<GcodeWord> words)
        { if (words.Count > 0) Reject(line, $"{line.Command} does not accept parameters at the safety boundary"); }

        static void EnsureOnly(GcodeLine line, List<GcodeWord> words, params char[] allowed)
        {
            foreach (var word in words)
            {
                bool ok = false; foreach (char a in allowed) if (word.Letter == a) { ok = true; break; }
                if (!ok) Reject(line, $"unsupported parameter {word.Letter} on {line.Command}");
            }
        }

        static void Reject(GcodeLine line, string message)
            => throw new FormatException($"line {line.LineNumber}: {message}");
    }
}
