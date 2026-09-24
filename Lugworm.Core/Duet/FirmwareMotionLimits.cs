using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Lugworm.Core.Gcode;

namespace Lugworm.Core.Duet
{
    public enum MotionReplyKind
    {
        Unknown,
        MaxSpeed,
        Acceleration,
        Jerk,
    }

    /// <summary>Motion limits for one firmware axis. Speeds are normalized to
    /// mm/s; acceleration remains mm/s^2. EffectiveSpeed is used only by a job
    /// scan and is the requested per-axis speed found in actual moves.</summary>
    public sealed class FirmwareAxisLimits
    {
        public double? MaxSpeed;
        public double? Acceleration;
        public double? Jerk;
        public double? EffectiveSpeed;

        public bool HasAnyValue => MaxSpeed.HasValue || Acceleration.HasValue
            || Jerk.HasValue || EffectiveSpeed.HasValue;
    }

    /// <summary>Motion values read from config.g. Every value is nullable:
    /// absent firmware settings are not treated as zero.</summary>
    public sealed class FirmwareMotionLimits
    {
        public readonly FirmwareAxisLimits X = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Y = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Z = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits E = new FirmwareAxisLimits();

        public double? PrintAccel;
        public double? TravelAccel;

        /// <summary>True when an unknown conditional block was skipped while
        /// reading the file. The known values remain usable, but callers should
        /// surface the warning instead of presenting the baseline as complete.</summary>
        public bool ConditionalCodeIgnored;
        public readonly List<string> Warnings = new List<string>();

        public FirmwareAxisLimits Axis(string axis)
        {
            if (string.IsNullOrEmpty(axis)) throw new ArgumentException("axis must be set", nameof(axis));
            return Axis(axis[0]);
        }

        public FirmwareAxisLimits Axis(char axis)
        {
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': return X;
                case 'Y': return Y;
                case 'Z': return Z;
                case 'E': return E;
                default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "axis must be X, Y, Z or E");
            }
        }

        public bool HasAnyLimit => X.MaxSpeed.HasValue || X.Acceleration.HasValue || X.Jerk.HasValue
            || Y.MaxSpeed.HasValue || Y.Acceleration.HasValue || Y.Jerk.HasValue
            || Z.MaxSpeed.HasValue || Z.Acceleration.HasValue || Z.Jerk.HasValue
            || E.MaxSpeed.HasValue || E.Acceleration.HasValue || E.Jerk.HasValue
            || PrintAccel.HasValue || TravelAccel.HasValue;

        public static FirmwareMotionLimits ParseConfig(IEnumerable<string> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            var result = new FirmwareMotionLimits();
            ScanSettings(lines, result, peak: false);
            return result;
        }

        /// <summary>Parses one bare M203/M201/M566 reply. RRF wording varies by
        /// release, so only axis-letter/number pairs are required. Unknown prose
        /// produces an empty result, never an exception.</summary>
        public static FirmwareMotionLimits ParseReply(string reply)
        {
            return ParseReply(reply, GuessReplyKind(reply));
        }

        public static FirmwareMotionLimits ParseReply(string reply, MotionReplyKind kind)
        {
            var result = new FirmwareMotionLimits();
            if (string.IsNullOrEmpty(reply) || kind == MotionReplyKind.Unknown) return result;

            const string number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
            foreach (Match match in Regex.Matches(reply, $@"(?<![A-Za-z])(?<axis>[XYZE])\s*:?\s*(?<value>{number})",
                                                   RegexOptions.IgnoreCase))
            {
                if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out double value)
                    || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    continue;

                char axis = char.ToUpperInvariant(match.Groups["axis"].Value[0]);
                FirmwareAxisLimits target = result.Axis(axis);
                if (kind == MotionReplyKind.MaxSpeed) target.MaxSpeed = value / 60.0;
                else if (kind == MotionReplyKind.Acceleration) target.Acceleration = value;
                else target.Jerk = value / 60.0;
            }
            return result;
        }

        static MotionReplyKind GuessReplyKind(string reply)
        {
            string text = (reply ?? "").ToLowerInvariant();
            if (text.Contains("accel")) return MotionReplyKind.Acceleration;
            if (text.Contains("jerk") || text.Contains("instantaneous")) return MotionReplyKind.Jerk;
            if (text.Contains("speed")) return MotionReplyKind.MaxSpeed;
            return MotionReplyKind.Unknown;
        }

        internal static void ScanSettings(IEnumerable<string> lines, FirmwareMotionLimits result, bool peak)
        {
            int lineNumber = 0;
            int conditionalDepth = 0;
            bool skipConditionalLine = false;

            foreach (string raw in lines)
            {
                lineNumber++;
                string text;
                try { text = GcodeText.Strip(raw); }
                catch (FormatException ex)
                {
                    result.Warnings.Add($"line {lineNumber}: ignored malformed text ({ex.Message})");
                    continue;
                }
                if (text.Length == 0) continue;

                if (SkipConditional(text, lineNumber, result, ref conditionalDepth, ref skipConditionalLine))
                    continue;

                GcodeLine line;
                try { line = GcodeText.Lex(text, lineNumber); }
                catch (FormatException ex)
                {
                    // The shared lexer intentionally does not model quoted RRF
                    // parameters (M550 P"name", M563 S"tool", ...). They are
                    // ordinary config commands, not malformed motion settings;
                    // only report a lexing problem when the line was meant to
                    // set one of the limits we are extracting.
                    if (LooksLikeMotionCommand(text))
                        result.Warnings.Add($"line {lineNumber}: ignored malformed motion setting ({ex.Message})");
                    continue;
                }
                if (line.CommandLetter != 'M') continue;
                ApplyCommand(line, result, peak, lineNumber);
            }
        }

        static bool LooksLikeMotionCommand(string text)
        {
            return Regex.IsMatch(text,
                @"^(?:N\d+\s*)?M(?:201|203|204|205|566)(?:\s|[XYZEPST]|$)",
                RegexOptions.IgnoreCase);
        }

        static bool SkipConditional(string text, int lineNumber, FirmwareMotionLimits result,
                                    ref int conditionalDepth, ref bool skipConditionalLine)
        {
            int open = Count(text, '{');
            int close = Count(text, '}');
            if (conditionalDepth > 0)
            {
                conditionalDepth = Math.Max(0, conditionalDepth + open - close);
                return true;
            }
            if (skipConditionalLine)
            {
                skipConditionalLine = false;
                if (open > close) conditionalDepth = open - close;
                return true;
            }

            string first = FirstToken(text);
            if (first == "if" || first == "elif" || first == "else" || first == "while")
            {
                result.ConditionalCodeIgnored = true;
                result.Warnings.Add($"line {lineNumber}: conditional block ignored while reading motion limits");
                if (open > close) conditionalDepth = open - close;
                else skipConditionalLine = true;
                return true;
            }
            if (open > 0 || close > 0)
            {
                result.ConditionalCodeIgnored = true;
                result.Warnings.Add($"line {lineNumber}: braced block ignored while reading motion limits");
                conditionalDepth = Math.Max(0, open - close);
                return true;
            }
            return false;
        }

        static int Count(string text, char wanted)
        {
            int count = 0;
            foreach (char c in text) if (c == wanted) count++;
            return count;
        }

        static string FirstToken(string text)
        {
            int end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            return text.Substring(0, end).Trim().TrimStart('{').ToLowerInvariant();
        }

        static void ApplyCommand(GcodeLine line, FirmwareMotionLimits result, bool peak, int lineNumber)
        {
            switch (line.CommandNumber)
            {
                case 201:
                    foreach (GcodeWord word in Parameters(line))
                        ApplyAxisValue(result.AxisOrNull(word.Letter), word, MotionField.Acceleration,
                                       divideBy60: false, peak, result, lineNumber);
                    break;
                case 203:
                    {
                        // M203 is mm/min UNLESS S1 is given, in which case it is mm/sec:
                        // RepRapFirmware GCodes2.cpp case 203 - "Units are mm/sec if S1 is
                        // given, else mm/min". Dividing unconditionally reads `M203 S1 X100`
                        // as 1.67 mm/s, understating it 60x and letting a job that raises the
                        // ceiling slip past the comparison unwarned.
                        bool mmPerSec = UsesMmPerSecond(Parameters(line));
                        foreach (GcodeWord word in Parameters(line))
                            ApplyAxisValue(result.AxisOrNull(word.Letter), word, MotionField.MaxSpeed,
                                           divideBy60: !mmPerSec, peak, result, lineNumber);
                        break;
                    }
                case 205:
                    foreach (GcodeWord word in Parameters(line))
                        ApplyAxisValue(result.AxisOrNull(word.Letter), word, MotionField.Jerk,
                                       divideBy60: false, peak, result, lineNumber);
                    break;
                case 566:
                    foreach (GcodeWord word in Parameters(line))
                        ApplyAxisValue(result.AxisOrNull(word.Letter), word, MotionField.Jerk,
                                       divideBy60: true, peak, result, lineNumber);
                    break;
                case 204:
                    foreach (GcodeWord word in Parameters(line))
                    {
                        if (word.Value < 0 || double.IsNaN(word.Value) || double.IsInfinity(word.Value))
                        {
                            result.Warnings.Add($"line {lineNumber}: ignored negative or non-finite M204 value");
                            continue;
                        }
                        if (word.Letter == 'P' || word.Letter == 'S')
                            result.PrintAccel = peak ? Max(result.PrintAccel, word.Value) : word.Value;
                        if (word.Letter == 'T' || word.Letter == 'S')
                            result.TravelAccel = peak ? Max(result.TravelAccel, word.Value) : word.Value;
                    }
                    break;
            }
        }

        /// <summary>True when an M203 carries `S1`, which switches its units from mm/min
        /// to mm/sec. Any other S value - including S0 - leaves the default mm/min, which
        /// matches RepRapFirmware's own test (`gb.Seen('S') &amp;&amp; gb.GetIValue() == 1`).
        /// Shared with <see cref="JobMotionDemand"/>: the config side and the job side must
        /// normalise identically or they are compared in different units.</summary>
        internal static bool UsesMmPerSecond(IEnumerable<GcodeWord> words)
        {
            foreach (GcodeWord word in words)
                if (word.Letter == 'S' && Math.Abs(word.Value - 1.0) < 1e-9) return true;
            return false;
        }

        enum MotionField { MaxSpeed, Acceleration, Jerk }

        static void ApplyAxisValue(FirmwareAxisLimits? target, GcodeWord word, MotionField field,
                                   bool divideBy60, bool peak, FirmwareMotionLimits result, int lineNumber)
        {
            if (target == null) return;
            if (word.Value < 0 || double.IsNaN(word.Value) || double.IsInfinity(word.Value))
            {
                result.Warnings.Add($"line {lineNumber}: ignored negative or non-finite M limit value");
                return;
            }
            double value = divideBy60 ? word.Value / 60.0 : word.Value;
            if (field == MotionField.MaxSpeed)
                target.MaxSpeed = peak ? Max(target.MaxSpeed, value) : value;
            else if (field == MotionField.Acceleration)
                target.Acceleration = peak ? Max(target.Acceleration, value) : value;
            else
                target.Jerk = peak ? Max(target.Jerk, value) : value;
        }

        static IEnumerable<GcodeWord> Parameters(GcodeLine line)
        {
            bool commandSeen = false;
            foreach (GcodeWord word in line.Words)
            {
                if (!commandSeen && word.Letter == 'N') continue;
                if (!commandSeen && word.Letter == line.CommandLetter)
                {
                    commandSeen = true;
                    continue;
                }
                if (word.Letter != 'N') yield return word;
            }
        }

        static double? Max(double? first, double value)
            => !first.HasValue || value > first.Value ? value : first;
    }

    public sealed class LiveMotionState
    {
        public readonly FirmwareAxisLimits X = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Y = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Z = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits E = new FirmwareAxisLimits();

        public FirmwareAxisLimits Axis(char axis)
        {
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': return X;
                case 'Y': return Y;
                case 'Z': return Z;
                case 'E': return E;
                default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "axis must be X, Y, Z or E");
            }
        }

        public FirmwareAxisLimits Axis(string axis)
        {
            if (string.IsNullOrEmpty(axis)) throw new ArgumentException("axis must be set", nameof(axis));
            return Axis(axis[0]);
        }

        public bool HasAnyValue => X.HasAnyValue || Y.HasAnyValue || Z.HasAnyValue || E.HasAnyValue;

        internal void Merge(FirmwareMotionLimits source)
        {
            MergeAxis(X, source.X); MergeAxis(Y, source.Y); MergeAxis(Z, source.Z); MergeAxis(E, source.E);
        }

        static void MergeAxis(FirmwareAxisLimits target, FirmwareAxisLimits source)
        {
            if (source.MaxSpeed.HasValue) target.MaxSpeed = source.MaxSpeed;
            if (source.Acceleration.HasValue) target.Acceleration = source.Acceleration;
            if (source.Jerk.HasValue) target.Jerk = source.Jerk;
        }
    }

    public sealed class JobMotionDemand
    {
        public readonly FirmwareAxisLimits X = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Y = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits Z = new FirmwareAxisLimits();
        public readonly FirmwareAxisLimits E = new FirmwareAxisLimits();

        public double? PrintAccel;
        public double? TravelAccel;
        public readonly List<double?> ModalAcceleration = new List<double?>();

        public FirmwareAxisLimits Axis(string axis)
        {
            if (string.IsNullOrEmpty(axis)) throw new ArgumentException("axis must be set", nameof(axis));
            switch (char.ToUpperInvariant(axis[0]))
            {
                case 'X': return X;
                case 'Y': return Y;
                case 'Z': return Z;
                case 'E': return E;
                default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "axis must be X, Y, Z or E");
            }
        }

        public static JobMotionDemand ScanJob(IEnumerable<string> lines)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            var demand = new JobMotionDemand();
            double?[] modalAxisAcceleration = new double?[4];
            double? modalPrint = null, modalTravel = null;
            double logicalX = 0, logicalY = 0, logicalZ = 0, logicalE = 0;
            double offsetX = 0, offsetY = 0, offsetZ = 0, offsetE = 0;
            double physicalX = 0, physicalY = 0, physicalZ = 0, physicalE = 0;
            double feed = 0;
            bool absolute = true, relativeE = true;
            int lineNumber = 0;

            foreach (string raw in lines)
            {
                lineNumber++;
                GcodeLine line;
                try { line = GcodeText.Lex(raw, lineNumber); }
                catch (FormatException) { continue; }
                if (line.CommandLetter == '\0') continue;

                List<GcodeWord> words = new List<GcodeWord>(Parameters(line));
                if (line.CommandLetter == 'M')
                {
                    if (line.CommandNumber == 82) relativeE = false;
                    else if (line.CommandNumber == 83) relativeE = true;
                    ApplyJobLimitCommand(line, words, demand, modalAxisAcceleration,
                                         ref modalPrint, ref modalTravel);
                    continue;
                }
                if (line.CommandLetter != 'G') continue;
                if (line.CommandNumber == 90) { absolute = true; continue; }
                if (line.CommandNumber == 91) { absolute = false; continue; }
                if (line.CommandNumber == 92)
                {
                    ApplyG92(words, ref logicalX, ref logicalY, ref logicalZ, ref logicalE,
                             ref offsetX, ref offsetY, ref offsetZ, ref offsetE,
                             physicalX, physicalY, physicalZ, physicalE);
                    continue;
                }
                if (line.CommandNumber < 0 || line.CommandNumber > 3) continue;

                double oldX = physicalX, oldY = physicalY, oldZ = physicalZ, oldE = physicalE;
                double nextX = logicalX, nextY = logicalY, nextZ = logicalZ, eWord = 0;
                double i = 0, j = 0;
                bool hasE = false, hasI = false, hasJ = false;
                foreach (GcodeWord word in words)
                {
                    switch (word.Letter)
                    {
                        case 'X': nextX = absolute ? word.Value : logicalX + word.Value; break;
                        case 'Y': nextY = absolute ? word.Value : logicalY + word.Value; break;
                        case 'Z': nextZ = absolute ? word.Value : logicalZ + word.Value; break;
                        case 'E': eWord = word.Value; hasE = true; break;
                        case 'I': i = word.Value; hasI = true; break;
                        case 'J': j = word.Value; hasJ = true; break;
                        case 'F': if (word.Value >= 0) feed = word.Value / 60.0; break;
                    }
                }

                double nextLogicalE = hasE ? (relativeE ? logicalE + eWord : eWord) : logicalE;
                double de = nextLogicalE - logicalE;
                double targetX = nextX - offsetX, targetY = nextY - offsetY, targetZ = nextZ - offsetZ;
                bool[] participating = new bool[4];
                double[] speeds;
                if (line.CommandNumber >= 2 && (hasI || hasJ))
                    speeds = ArcSpeeds(oldX, oldY, oldZ, targetX, targetY, targetZ, de,
                                       i, j, line.CommandNumber == 2, feed, participating);
                else
                    speeds = LinearSpeeds(oldX, oldY, oldZ, targetX, targetY, targetZ, de,
                                          feed, participating);

                if (feed > 0)
                {
                    // Only axes this move actually drives. Recording 0 for the rest would
                    // read as "moved at zero" instead of "did not move", contradicting the
                    // nullable convention the rest of this file keeps (absent, never zero).
                    if (participating[0]) demand.X.EffectiveSpeed = Max(demand.X.EffectiveSpeed, speeds[0]);
                    if (participating[1]) demand.Y.EffectiveSpeed = Max(demand.Y.EffectiveSpeed, speeds[1]);
                    if (participating[2]) demand.Z.EffectiveSpeed = Max(demand.Z.EffectiveSpeed, speeds[2]);
                    if (participating[3]) demand.E.EffectiveSpeed = Max(demand.E.EffectiveSpeed, speeds[3]);
                }
                bool printing = de > 1e-9;
                demand.ModalAcceleration.Add(ResolveAcceleration(printing ? modalPrint : modalTravel,
                                                                  modalAxisAcceleration, participating));

                logicalX = nextX; logicalY = nextY; logicalZ = nextZ; logicalE = nextLogicalE;
                physicalX = targetX; physicalY = targetY; physicalZ = targetZ;
                physicalE += de; offsetE = logicalE - physicalE;
            }
            return demand;
        }

        static void ApplyJobLimitCommand(GcodeLine line, List<GcodeWord> words, JobMotionDemand demand,
                                          double?[] modalAxisAcceleration, ref double? modalPrint,
                                          ref double? modalTravel)
        {
            switch (line.CommandNumber)
            {
                case 201:
                    foreach (GcodeWord word in words)
                        ApplyJobAxis(demand.AxisOrNull(word.Letter), word, JobMotionField.Acceleration,
                                     divideBy60: false, modalAxisAcceleration, word.Letter);
                    break;
                case 203:
                    {
                        // See ApplyCommand: S1 switches M203 to mm/sec (RRF GCodes2.cpp
                        // case 203). Both scanners must agree or the job side and the
                        // config side are compared in different units.
                        bool mmPerSec = FirmwareMotionLimits.UsesMmPerSecond(words);
                        foreach (GcodeWord word in words)
                            ApplyJobAxis(demand.AxisOrNull(word.Letter), word, JobMotionField.MaxSpeed,
                                         divideBy60: !mmPerSec, null, word.Letter);
                        break;
                    }
                case 205:
                    foreach (GcodeWord word in words)
                        ApplyJobAxis(demand.AxisOrNull(word.Letter), word, JobMotionField.Jerk,
                                     divideBy60: false, null, word.Letter);
                    break;
                case 566:
                    foreach (GcodeWord word in words)
                        ApplyJobAxis(demand.AxisOrNull(word.Letter), word, JobMotionField.Jerk,
                                     divideBy60: true, null, word.Letter);
                    break;
                case 204:
                    foreach (GcodeWord word in words)
                    {
                        if (word.Value < 0 || double.IsNaN(word.Value) || double.IsInfinity(word.Value)) continue;
                        if (word.Letter == 'P' || word.Letter == 'S')
                        {
                            demand.PrintAccel = Max(demand.PrintAccel, word.Value);
                            modalPrint = word.Value;
                        }
                        if (word.Letter == 'T' || word.Letter == 'S')
                        {
                            demand.TravelAccel = Max(demand.TravelAccel, word.Value);
                            modalTravel = word.Value;
                        }
                    }
                    break;
            }
        }

        static IEnumerable<GcodeWord> Parameters(GcodeLine line)
        {
            bool commandSeen = false;
            foreach (GcodeWord word in line.Words)
            {
                if (!commandSeen && word.Letter == 'N') continue;
                if (!commandSeen && word.Letter == line.CommandLetter)
                {
                    commandSeen = true;
                    continue;
                }
                if (word.Letter != 'N') yield return word;
            }
        }

        enum JobMotionField { MaxSpeed, Acceleration, Jerk }

        static void ApplyJobAxis(FirmwareAxisLimits? target, GcodeWord word, JobMotionField field,
                                 bool divideBy60, double?[]? modalAxisAcceleration, char axis)
        {
            if (target == null || word.Value < 0 || double.IsNaN(word.Value) || double.IsInfinity(word.Value)) return;
            double value = divideBy60 ? word.Value / 60.0 : word.Value;
            if (field == JobMotionField.MaxSpeed) target.MaxSpeed = Max(target.MaxSpeed, value);
            else if (field == JobMotionField.Acceleration)
            {
                target.Acceleration = Max(target.Acceleration, value);
                if (modalAxisAcceleration != null)
                    modalAxisAcceleration[AxisIndex(axis)] = value;
            }
            else target.Jerk = Max(target.Jerk, value);
        }

        static int AxisIndex(char axis)
        {
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': return 0;
                case 'Y': return 1;
                case 'Z': return 2;
                case 'E': return 3;
                default: return -1;
            }
        }

        static void ApplyG92(List<GcodeWord> words, ref double logicalX, ref double logicalY,
                             ref double logicalZ, ref double logicalE, ref double offsetX,
                             ref double offsetY, ref double offsetZ, ref double offsetE,
                             double physicalX, double physicalY, double physicalZ, double physicalE)
        {
            if (words.Count == 0)
            {
                logicalX = logicalY = logicalZ = logicalE = 0;
                offsetX = -physicalX; offsetY = -physicalY; offsetZ = -physicalZ; offsetE = -physicalE;
                return;
            }
            foreach (GcodeWord word in words)
            {
                switch (word.Letter)
                {
                    case 'X': logicalX = word.Value; offsetX = logicalX - physicalX; break;
                    case 'Y': logicalY = word.Value; offsetY = logicalY - physicalY; break;
                    case 'Z': logicalZ = word.Value; offsetZ = logicalZ - physicalZ; break;
                    case 'E': logicalE = word.Value; offsetE = logicalE - physicalE; break;
                }
            }
        }

        static double? ResolveAcceleration(double? modal, double?[] perAxis, bool[] participating)
        {
            double? ceiling = null;
            for (int i = 0; i < perAxis.Length; i++)
                if (participating[i] && perAxis[i].HasValue)
                    ceiling = !ceiling.HasValue || perAxis[i]!.Value < ceiling.Value ? perAxis[i] : ceiling;
            if (modal.HasValue && ceiling.HasValue) return Math.Min(modal.Value, ceiling.Value);
            return modal ?? ceiling;
        }

        static double[] LinearSpeeds(double x0, double y0, double z0, double x1, double y1,
                                     double z1, double de, double feed, bool[] participating)
        {
            double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
            double spatial = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double[] speed = new double[4];
            if (spatial > 1e-12)
            {
                if (Math.Abs(dx) > 1e-12) { participating[0] = true; speed[0] = feed * Math.Abs(dx) / spatial; }
                if (Math.Abs(dy) > 1e-12) { participating[1] = true; speed[1] = feed * Math.Abs(dy) / spatial; }
                if (Math.Abs(dz) > 1e-12) { participating[2] = true; speed[2] = feed * Math.Abs(dz) / spatial; }
                if (Math.Abs(de) > 1e-12) { participating[3] = true; speed[3] = feed * Math.Abs(de) / spatial; }
            }
            else if (Math.Abs(de) > 1e-12)
            {
                participating[3] = true;
                speed[3] = feed;
            }
            return speed;
        }

        static double[] ArcSpeeds(double x0, double y0, double z0, double x1, double y1, double z1,
                                  double de, double i, double j, bool clockwise, double feed,
                                  bool[] participating)
        {
            double cx = x0 + i, cy = y0 + j;
            double r0 = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
            double r1 = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
            if (r0 <= 1e-12 || r1 <= 1e-12)
                return LinearSpeeds(x0, y0, z0, x1, y1, z1, de, feed, participating);

            double start = Math.Atan2(y0 - cy, x0 - cx);
            double end = Math.Atan2(y1 - cy, x1 - cx);
            double sweep = DirectedSweep(start, end, clockwise);
            double radius = (r0 + r1) * 0.5;
            double planarLength = radius * sweep;
            double dz = z1 - z0;
            double pathLength = Math.Sqrt(planarLength * planarLength + dz * dz);
            double[] speed = new double[4];
            if (pathLength <= 1e-12) return speed;

            double maxX = MaxAbsSin(start, sweep, clockwise);
            double maxY = MaxAbsCos(start, sweep, clockwise);
            double planarFactor = planarLength / pathLength;
            if (maxX > 1e-12) { participating[0] = true; speed[0] = feed * maxX * planarFactor; }
            if (maxY > 1e-12) { participating[1] = true; speed[1] = feed * maxY * planarFactor; }
            if (Math.Abs(dz) > 1e-12) { participating[2] = true; speed[2] = feed * Math.Abs(dz) / pathLength; }
            if (Math.Abs(de) > 1e-12) { participating[3] = true; speed[3] = feed * Math.Abs(de) / pathLength; }
            return speed;
        }

        static double DirectedSweep(double start, double end, bool clockwise)
        {
            double twoPi = 2.0 * Math.PI;
            double sweep = clockwise ? start - end : end - start;
            while (sweep < 0) sweep += twoPi;
            while (sweep > twoPi) sweep -= twoPi;
            if (sweep <= 1e-12) sweep = twoPi;
            return sweep;
        }

        static double MaxAbsSin(double start, double sweep, bool clockwise)
        {
            double max = Math.Abs(Math.Sin(start));
            max = Math.Max(max, Math.Abs(Math.Sin(start + (clockwise ? -sweep : sweep))));
            for (int k = -8; k <= 8; k++)
            {
                double candidate = k * Math.PI * 0.5;
                if (OnSweep(start, candidate, sweep, clockwise)) max = Math.Max(max, Math.Abs(Math.Sin(candidate)));
            }
            return max;
        }

        static double MaxAbsCos(double start, double sweep, bool clockwise)
        {
            double max = Math.Abs(Math.Cos(start));
            max = Math.Max(max, Math.Abs(Math.Cos(start + (clockwise ? -sweep : sweep))));
            for (int k = -8; k <= 8; k++)
            {
                double candidate = k * Math.PI * 0.5;
                if (OnSweep(start, candidate, sweep, clockwise)) max = Math.Max(max, Math.Abs(Math.Cos(candidate)));
            }
            return max;
        }

        static bool OnSweep(double start, double candidate, double sweep, bool clockwise)
        {
            double twoPi = 2.0 * Math.PI;
            double progress = clockwise ? start - candidate : candidate - start;
            while (progress < 0) progress += twoPi;
            while (progress >= twoPi) progress -= twoPi;
            return progress <= sweep + 1e-10 || sweep >= twoPi - 1e-10;
        }

        static double? Max(double? first, double value)
            => !first.HasValue || value > first.Value ? value : first;
    }

    static class FirmwareMotionLimitAxisExtensions
    {
        internal static FirmwareAxisLimits? AxisOrNull(this FirmwareMotionLimits limits, char axis)
        {
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': return limits.X;
                case 'Y': return limits.Y;
                case 'Z': return limits.Z;
                case 'E': return limits.E;
                default: return null;
            }
        }

        internal static FirmwareAxisLimits? AxisOrNull(this JobMotionDemand demand, char axis)
        {
            switch (char.ToUpperInvariant(axis))
            {
                case 'X': return demand.X;
                case 'Y': return demand.Y;
                case 'Z': return demand.Z;
                case 'E': return demand.E;
                default: return null;
            }
        }
    }
}
