using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Geometry;
using Lugworm.Core.Duet;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;

namespace Lugworm.Duet
{
    /// <summary>Where a live view's intended path came from. Decides what may be drawn and
    /// what has to be said instead.</summary>
    public enum IntendedPathOrigin
    {
        /// <summary>Nothing is wired and no job is known.</summary>
        None,
        /// <summary>The session's effective job - the printing one while a print runs, the
        /// prepared one otherwise. This is the source that wins.</summary>
        SessionJob,
        /// <summary>The component's own <c>gcode</c> socket, used only while the session has
        /// no effective job.</summary>
        GcodeInput,
        /// <summary>A job is running that this session did not start, so its bytes are not
        /// known. No path is drawn and none is substituted.</summary>
        Unavailable,
    }

    /// <summary>What to parse, and the identity that decides whether it has to be parsed at
    /// all. Immutable; built once per solve and compared by <see cref="Key"/>.</summary>
    public sealed class IntendedPathRequest
    {
        /// <summary>Cache identity: the job's SHA-256 (or the input's content hash) plus the
        /// frame the samples will be converted into. "" when there is nothing to parse.</summary>
        public string Key { get; }
        public IntendedPathOrigin Origin { get; }
        /// <summary>Lower-case hex SHA-256 when the source is a job snapshot; "" otherwise.</summary>
        public string Sha256 { get; }
        public bool Cartesian { get; }
        /// <summary>The lines to parse, produced on the PARSE thread. Never called on the UI
        /// or poll thread; null when there is nothing to parse.</summary>
        public Func<IReadOnlyList<string>>? Lines { get; }
        /// <summary>Human-readable one-liner for the component's <c>info</c> output.</summary>
        public string Note { get; }

        public IntendedPathRequest(string key, IntendedPathOrigin origin, string sha256, bool cartesian,
                                   Func<IReadOnlyList<string>>? lines, string note)
        {
            Key = key ?? "";
            Origin = origin;
            Sha256 = sha256 ?? "";
            Cartesian = cartesian;
            Lines = lines;
            Note = note ?? "";
        }

        public static readonly IntendedPathRequest None =
            new IntendedPathRequest("", IntendedPathOrigin.None, "", false, null,
                                    "intended path: none (no job on the Console and no gcode wired)");

        public bool Parseable => Key.Length > 0 && Lines != null;
    }

    /// <summary>One parsed intended path, ready to draw and to compare against. Immutable
    /// once published; shared by every Live View looking at the same job.</summary>
    public sealed class ParsedJobPath
    {
        public string Key { get; }
        public string Sha256 { get; }
        /// <summary>Sampled positions in the MACHINE frame - what observed telemetry is
        /// compared against, because telemetry is machine coordinates.</summary>
        public IReadOnlyList<Vec3> Machine { get; }
        /// <summary>The PRINTING runs in the RHINO frame, simplified to a bounded number of
        /// drawn points: the shape the file lays down, drawn thin and neutral.</summary>
        public IReadOnlyList<Point3d[]> ContextRuns { get; }
        /// <summary>The TRAVEL runs in the RHINO frame - travels, seam approaches, coasts and
        /// layer changes - simplified under the same budget as <see cref="ContextRuns"/>.
        /// They used to be omitted entirely, and the operator saw a renderer that was showing
        /// a different program than the one it was about to run (test-round row 10a.8). They
        /// are drawn in their own secondary style, never merged with the printing runs.</summary>
        public IReadOnlyList<Point3d[]> TravelRuns { get; }
        /// <summary>Non-null when the drawn runs were simplified for display; names the
        /// applied tolerance and the resulting point count so a viewport can SAY the path is
        /// thinned rather than let it be read as the file.</summary>
        public string? DetailNote { get; }
        /// <summary>The deviation index over <see cref="Machine"/>, built on the parse thread
        /// so no consumer ever builds one on the UI or poll thread. Immutable after
        /// construction, so every Live View on this job shares it.</summary>
        public LivePathDeviation? Deviation { get; }
        public BoundingBox Bounds { get; }
        public int MoveCount { get; }
        public int SampleCount { get; }
        /// <summary>Content identity of the LINES this path was parsed from, so a component
        /// can tell a wire that carries the same file from one that carries a different one
        /// without hashing the job's bytes on every solve. "" when nothing was parsed.</summary>
        public string LineIdentity { get; }
        /// <summary>Non-null when the bytes could not be parsed. The path is then empty and
        /// nothing is drawn; the reason is reported, never guessed around.</summary>
        public string? Error { get; }

        /// <param name="travelRuns">Optional so the two error paths below can stay one-liners;
        /// null means "none", never "unknown".</param>
        public ParsedJobPath(string key, string sha256, IReadOnlyList<Vec3> machine,
                             IReadOnlyList<Point3d[]> contextRuns, LivePathDeviation? deviation,
                             BoundingBox bounds, int moveCount, int sampleCount,
                             string lineIdentity, string? error,
                             IReadOnlyList<Point3d[]>? travelRuns = null, string? detailNote = null)
        {
            Key = key; Sha256 = sha256; Machine = machine; ContextRuns = contextRuns;
            TravelRuns = travelRuns ?? Array.Empty<Point3d[]>();
            Deviation = deviation; Bounds = bounds; MoveCount = moveCount;
            SampleCount = sampleCount; LineIdentity = lineIdentity ?? ""; Error = error;
            DetailNote = detailNote;
        }

        public bool HasGeometry => Error == null && Machine.Count > 1;
    }

    /// <summary>The one place a job's G-code is turned into an intended path, keyed by the
    /// content that produced it.</summary>
    /// <remarks>
    /// <para>Before this, Live View parsed its own <c>gcode</c> socket on every solve whose
    /// content hash moved, and a Console output that changed on every position poll would
    /// therefore have re-parsed the whole file at telemetry cadence. Keying on the job's
    /// SHA-256 makes a re-parse impossible without a genuinely different job, whatever the
    /// wire does.</para>
    /// <para>Parsing runs on a thread-pool thread - never the Rhino UI thread and never the
    /// poll loop - and at most one parse per key is ever in flight. The cache is bounded to
    /// <see cref="Capacity"/> entries and evicts the least recently used, so a session that
    /// prepares one job after another cannot grow it without limit.</para>
    /// </remarks>
    public static class ParsedJobCache
    {
        /// <summary>Enough for the printing job, the job being prepared and the previous one
        /// in each frame. Small on purpose: these entries hold whole sampled toolpaths.</summary>
        public const int Capacity = 4;

        /// <summary>Above this the drawn runs are simplified. Counted over the PRINT and
        /// TRAVEL runs together, because both are drawn and both cost the same per point.
        /// It is a DRAWING bound only: <see cref="ParsedJobPath.Machine"/>, the deviation
        /// index and every comparison against telemetry keep the full unthinned sample
        /// list.</summary>
        const int ContextPointBudget = 40000;

        /// <summary>The largest geometric error the drawn runs may be simplified by, in mm.
        /// A quarter of a typical clay layer is below the width of the drawn line at any
        /// useful zoom, so the shape still reads as the file's. When even this cannot bring
        /// a job inside <see cref="ContextPointBudget"/> the tolerance is NOT raised: the
        /// extra points are drawn and the shortfall is reported through
        /// <see cref="ParsedJobPath.DetailNote"/>, because quietly flattening corners to hit
        /// a point count would misrepresent the program about to run.</summary>
        const double ContextMaxDeviationMm = 0.5;

        static readonly object Sync = new object();
        static readonly Dictionary<string, ParsedJobPath> Entries = new Dictionary<string, ParsedJobPath>(StringComparer.Ordinal);
        static readonly List<string> Recent = new List<string>();
        static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Everyone waiting on a parse that is already running. Guarded by
        /// <see cref="Sync"/>; emptied when the entry is published.</summary>
        static readonly Dictionary<string, List<Action<ParsedJobPath>>> Waiting =
            new Dictionary<string, List<Action<ParsedJobPath>>>(StringComparer.Ordinal);

        static long _parses, _hits, _misses, _failures;

        /// <summary>Parses actually performed since Rhino started.</summary>
        public static long ParseCount => Interlocked.Read(ref _parses);
        /// <summary>Lookups served from the cache - the number that must keep rising while a
        /// print runs, because the job's revision does not change.</summary>
        public static long CacheHits => Interlocked.Read(ref _hits);
        public static long CacheMisses => Interlocked.Read(ref _misses);
        public static long ParseFailures => Interlocked.Read(ref _failures);

        /// <summary>The parsed path for this key, or null when it has not been parsed yet.
        /// Counts the lookup; never parses.</summary>
        public static ParsedJobPath? Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var entry))
                {
                    Touch(key);
                    Interlocked.Increment(ref _hits);
                    return entry;
                }
            }
            Interlocked.Increment(ref _misses);
            return null;
        }

        /// <summary>Starts a background parse for a key that is not cached yet, or subscribes
        /// to the one already in flight. <paramref name="onReady"/> runs on the parse thread
        /// when published, or the caller's thread on a cache hit. It must not solve Grasshopper; waking a viewport is
        /// what it is for.</summary>
        /// <remarks>The subscription is the fix for a second Live View on the same job. This
        /// used to return early when a parse for the key was already running, so the second
        /// consumer registered no callback, was never woken, and sat waiting for a parse it
        /// had asked for. At most one parse per key still happens - every later caller joins
        /// the one in flight instead of starting another.</remarks>
        public static void Request(IntendedPathRequest request, Action<ParsedJobPath>? onReady)
        {
            if (request == null || !request.Parseable) return;
            string key = request.Key;
            ParsedJobPath? cached;
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out cached)) Touch(key);
                else
                {
                    bool started = InFlight.Add(key);
                    if (onReady != null)
                    {
                        if (!Waiting.TryGetValue(key, out var list)) Waiting[key] = list = new List<Action<ParsedJobPath>>();
                        list.Add(onReady);
                    }
                    if (!started) return;   // someone else owns the parse; we are now subscribed to it
                }
            }
            if (cached != null)
            {
                // Publication may have won the race between Lookup and Request.
                // best effort: a display fault stays in the display - the cache has
                // already published and must not fail the solve that asked for it.
                try { onReady?.Invoke(cached); } catch (Exception) { }
                return;
            }
            var lines = request.Lines!;
            bool cartesian = request.Cartesian;
            string sha = request.Sha256;
            // The home pose is read here, on the UI/solve thread that called Request, so the
            // parse thread never touches the shared robot model.
            Vec3? home = cartesian ? null : Lugworm.Robot.RobotModel.Default?.Def.CalibratedHomePose;
            Task.Run(() =>
            {
                ParsedJobPath entry;
                try { entry = Parse(key, sha, cartesian, home, lines()); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failures);
                    entry = new ParsedJobPath(key, sha, Array.Empty<Vec3>(), Array.Empty<Point3d[]>(),
                                              null, BoundingBox.Empty, 0, 0, "",
                                              ex.GetType().Name + ": " + ex.Message);
                }
                var waiting = Publish(entry);
                // Still the parse thread, and still one try per consumer: one Live View
                // throwing must not stop the others being woken.
                for (int i = 0; i < waiting.Count; i++)
                    try { waiting[i](entry); } catch (Exception) { /* a display fault stays in the display */ }
            });
        }

        /// <summary>Forgets everything. Test and shutdown seam only.</summary>
        public static void Clear()
        {
            lock (Sync) { Entries.Clear(); Recent.Clear(); }
        }

        /// <summary>Publishes the entry and hands back everyone who asked to be told. The
        /// callbacks are invoked OUTSIDE the lock: a consumer that wakes a viewport must
        /// never do so while holding the cache.</summary>
        static List<Action<ParsedJobPath>> Publish(ParsedJobPath entry)
        {
            lock (Sync)
            {
                Entries[entry.Key] = entry;
                Touch(entry.Key);
                InFlight.Remove(entry.Key);
                List<Action<ParsedJobPath>> waiting;
                if (Waiting.TryGetValue(entry.Key, out var subscribers)) { Waiting.Remove(entry.Key); waiting = subscribers; }
                else waiting = new List<Action<ParsedJobPath>>();
                while (Recent.Count > Capacity)
                {
                    string oldest = Recent[0];
                    Recent.RemoveAt(0);
                    Entries.Remove(oldest);
                }
                return waiting;
            }
        }

        static void Touch(string key)
        {
            Recent.Remove(key);
            Recent.Add(key);
        }

        static ParsedJobPath Parse(string key, string sha, bool cartesian, Vec3? home,
                                   IReadOnlyList<string> lines)
        {
            Interlocked.Increment(ref _parses);
            string identity = IdentityOfLines(lines);
            List<Vec3> machine;
            List<bool> breaks;
            ToolpathRunSet built;
            int moves;
            try
            {
                var program = GcodeParser.Parse(lines, home, allowPerAxisHome: cartesian);
                moves = program.Moves.Count;
                if (moves == 0)
                    return new ParsedJobPath(key, sha, Array.Empty<Vec3>(), Array.Empty<Point3d[]>(),
                                             null, BoundingBox.Empty, 0, 0, identity, null);
                var sampled = MoveSampler.SampleWithBreaks(program.Moves);
                machine = sampled.pts;
                breaks = sampled.breakBefore;
                built = ToolpathRunBuilder.Build(sampled.pts, sampled.ext, sampled.breakBefore);
            }
            catch (FormatException ex)
            {
                return new ParsedJobPath(key, sha, Array.Empty<Vec3>(), Array.Empty<Point3d[]>(),
                                         null, BoundingBox.Empty, 0, 0, identity, ex.Message);
            }

            // Only the DRAWN runs are simplified. `machine` below - the list the deviation
            // index and every telemetry comparison run against - is the full unthinned
            // sample sequence and is handed on untouched.
            var drawn = ToolpathRunBuilder.Simplify(built, ContextPointBudget, ContextMaxDeviationMm);
            var bounds = BoundingBox.Empty;
            var print = ToRhinoRuns(drawn, ToolpathRunKind.Print, cartesian, ref bounds);
            var travel = ToRhinoRuns(drawn, ToolpathRunKind.Travel, cartesian, ref bounds);
            // The comparison index is built here, on the parse thread: a Live View that shows
            // the deviation overlay must never build one while a solve or a poll waits.
            var deviation = machine.Count > 1 ? LivePathDeviation.FromPolyline(machine, breaks) : null;
            return new ParsedJobPath(key, sha, machine, print, deviation, bounds, moves,
                                     machine.Count, identity, null, travel, drawn.DetailNote);
        }

        /// <summary>The runs of one kind, converted to the Rhino frame. Context, not
        /// measurement: it is the file's own geometry, never anything observed.</summary>
        static IReadOnlyList<Point3d[]> ToRhinoRuns(ToolpathRunSet set, ToolpathRunKind kind,
                                                    bool cartesian, ref BoundingBox bounds)
        {
            var runs = new List<Point3d[]>();
            foreach (var run in set.Runs)
            {
                if (run.Kind != kind) continue;
                var pts = new Point3d[run.Points.Count];
                for (int i = 0; i < run.Points.Count; i++)
                {
                    pts[i] = ToRhino(run.Points[i], cartesian);
                    bounds.Union(pts[i]);
                }
                runs.Add(pts);
            }
            return runs;
        }

        static Point3d ToRhino(Vec3 machine, bool cartesian)
        {
            if (cartesian) return new Point3d(machine.X, machine.Y, machine.Z);
            MachineFrame.ToRhino(machine.X, machine.Y, out double x, out double y);
            return new Point3d(x, y, machine.Z);
        }

        /// <summary>Bumped whenever the parsed representation changes shape. An entry cached
        /// by an older build of this class describes different geometry under the same job
        /// bytes, and a stale one would be drawn as if it were current. 2 = segment-based
        /// runs with travel and break handling (was: extruding points only).</summary>
        const string ParseSchemaVersion = "v3";

        /// <summary>Cache key for one source in one frame. The frame is part of it because a
        /// Cartesian definition and the mirrored SCARA frame give different drawn points for
        /// the same bytes.</summary>
        /// <remarks>The calibrated home pose is part of it too, and its absence was a real
        /// cache-poisoning bug: <see cref="Request"/> feeds the pose to the parser, a G28
        /// resolves to it, and so two different home poses produce two different intended
        /// paths from identical bytes - which the old key could not tell apart, serving
        /// whichever had been parsed first. Read here, on the UI/solve thread that asks for
        /// the key, exactly as <see cref="Request"/> reads it; never on the parse
        /// thread.</remarks>
        public static string KeyFor(string identity, bool cartesian)
            => KeyFor(identity, cartesian,
                      cartesian ? null : Lugworm.Robot.RobotModel.Default?.Def.CalibratedHomePose);

        /// <summary>The key composition itself, with the pose passed in so it can be tested
        /// and so no caller can accidentally read the robot model off the parse thread.</summary>
        internal static string KeyFor(string identity, bool cartesian, Vec3? home)
        {
            var sb = new StringBuilder(ParseSchemaVersion);
            sb.Append(cartesian ? "|c|" : "|s|");
            if (home.HasValue)
                sb.Append(home.Value.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(home.Value.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(home.Value.Z.ToString("R", CultureInfo.InvariantCulture));
            else sb.Append('-');
            return sb.Append('|').Append(identity).ToString();
        }

        /// <summary>Identity of a text source that has no hash of its own.</summary>
        public static string IdentityOfLines(IReadOnlyList<string> lines)
            => "fnv" + ContentHash.Fnv1aLines(lines).ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>Decodes job bytes into lines on the parse thread. UTF-8 without a BOM is
        /// what every writer in this repository emits; an unknown byte becomes the
        /// replacement character rather than throwing away the whole job.</summary>
        public static IReadOnlyList<string> LinesFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<string>();
            string text = new UTF8Encoding(false).GetString(bytes);
            return text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
