using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;

namespace Lugworm.Core.Tests;

/// <summary>Loads the recorded job logs and the trimmed intended path the replay tests
/// run against, and replays a log through the shared continuity policy.</summary>
/// <remarks>
/// <para>Fixtures, with the hashes they were taken at:</para>
/// <list type="bullet">
/// <item><description><c>dome-20260909/print.csv</c>
/// (SHA-256 7847195146856fdc9f078a05ba755ca453c4a274df52bd088a930363731f1f38): the
/// 2026-09-09 dome run, 248 rows, halted by M112. Sequences 397, 405, 453, 492, 528, 535
/// and 595 are the operator-confirmed FALSE reported positions - the robot did not make
/// those excursions.</description></item>
/// <item><description><c>dome-20260909/intended-path-layers.gcode</c>
/// (SHA-256 f4a7736b51e5f76919af2c54b7ac1c7facaa91699e6f18dd0d6b42b1738e68ae): the
/// header and the five layers containing those samples, copied line for line from
/// <c>print_2026-09-09_10-20.gcode</c>
/// (SHA-256 40d500d9849591440477b22745c37b8ad011da46091105c76d3b9f4b8adeec42, 58,324
/// lines, 2,646,666 bytes) by <c>docs/dev/analysis/trim_dome_gcode.py</c>. The file's own
/// header comment records the source lines it kept.</description></item>
/// <item><description><c>log-20260902/print.csv</c>
/// (SHA-256 d129a94effb078a0cae16c006afaac127537a00c792b5153d9349d71bf60d1f6): a copy of
/// <c>docs/dev/analysis/dome-20260902/Log.txt</c>, 182 rows over two runs on 2026-09-02, with the Z=400 to
/// -0.852 discontinuity at sequence 31 to 32 and the footer tail at Z=400.</description></item>
/// </list>
/// </remarks>
static class TelemetryReplayFixtures
{
    public static string Path(params string[] parts)
    {
        string p = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures");
        foreach (string part in parts) p = System.IO.Path.Combine(p, part);
        return p;
    }

    public static IReadOnlyList<PrintLogRecord> LoadLog(params string[] parts)
    {
        string path = Path(parts);
        if (!File.Exists(path)) throw new FileNotFoundException("replay fixture missing: " + path, path);
        return PrintLogCsvReader.Parse(File.ReadAllLines(path)).Records;
    }

    /// <summary>Reads the trimmed intended path into same-elevation reference segments.
    /// Only explicit G0/G1 motion is used, G90/G91 is respected, and a G28 breaks the
    /// polyline because the macro's own motion is unknown - the same rules the analysis
    /// script applied.</summary>
    public static SameZPathIndex LoadIntendedPath(params string[] parts)
    {
        string path = Path(parts);
        if (!File.Exists(path)) throw new FileNotFoundException("intended-path fixture missing: " + path, path);
        var segments = new List<LivePathSegment>();
        double x = 0, y = 0, z = 0;
        bool absolute = true, have = false;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw;
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line.Substring(0, comment);
            line = line.Trim();
            if (line.Length == 0) continue;
            string upper = line.ToUpperInvariant();
            if (upper.StartsWith("G90", StringComparison.Ordinal)) { absolute = true; continue; }
            if (upper.StartsWith("G91", StringComparison.Ordinal)) { absolute = false; continue; }
            if (upper.StartsWith("G28", StringComparison.Ordinal)) { have = false; continue; }
            if (upper.StartsWith("G92", StringComparison.Ordinal))
            {
                if (Word(upper, 'X', out double gx)) x = gx;
                if (Word(upper, 'Y', out double gy)) y = gy;
                if (Word(upper, 'Z', out double gz)) z = gz;
                continue;
            }
            bool move = upper.StartsWith("G0 ", StringComparison.Ordinal)
                     || upper.StartsWith("G1 ", StringComparison.Ordinal)
                     || upper == "G0" || upper == "G1";
            if (!move) continue;
            double nx = x, ny = y, nz = z;
            if (Word(upper, 'X', out double vx)) nx = absolute ? vx : x + vx;
            if (Word(upper, 'Y', out double vy)) ny = absolute ? vy : y + vy;
            if (Word(upper, 'Z', out double vz)) nz = absolute ? vz : z + vz;
            if (have) segments.Add(new LivePathSegment(new Vec3(x, y, z), new Vec3(nx, ny, nz)));
            x = nx; y = ny; z = nz; have = true;
        }
        return new SameZPathIndex(segments);
    }

    /// <summary>Value of one G-code word, matched only at the start of a whitespace-
    /// separated token so an E or F value can never be read as a coordinate.</summary>
    static bool Word(string line, char letter, out double value)
    {
        value = 0;
        foreach (string token in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2 || token[0] != letter) continue;
            return double.TryParse(token.Substring(1), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    /// <summary>Replays recorded rows through the continuity policy exactly as the poll
    /// loop would, mapping the CSV's own fields onto the observation model. A recorded row
    /// carries no acquisition id (the fixtures predate them), so continuity here rests on
    /// the timestamps, the run identity and the file progress - which is precisely what
    /// makes these fixtures a test of the rules rather than of the new plumbing.</summary>
    public static List<TrailVerdict> Replay(IReadOnlyList<PrintLogRecord> records,
                                            IIntendedPathReference? path = null,
                                            TrailContinuityOptions? options = null)
    {
        var policy = new TrailContinuityPolicy(options, path);
        var verdicts = new List<TrailVerdict>(records.Count);
        foreach (var r in records)
        {
            bool printing = r.Status == "Processing" || r.Status == "Simulating"
                         || r.Status == "Paused" || r.Status == "Pausing" || r.Status == "Resuming";
            var status = Enum.TryParse(r.Status, out MachineStatus s) ? s : MachineStatus.Unknown;
            verdicts.Add(policy.Observe(new TrailObservation
            {
                ReceivedUtc = r.TakenUtc,
                ConnectionGeneration = 1,
                RunId = r.JobRun.ToString(CultureInfo.InvariantCulture),
                PositionValid = r.PositionValid,
                Position = r.Position,
                AllHomed = true,
                Status = status,
                JobActive = printing,
                Layer = r.Layer,
                FilePosition = r.FilePosition,
                FractionPrinted = r.FractionPrinted,
                RequestedSpeedMms = r.SpeedRequested,
                TopSpeedMms = r.SpeedTop,
                RawExtrusion = r.RawExtrusion,
            }));
        }
        return verdicts;
    }

    public static int IndexOfSeq(IReadOnlyList<PrintLogRecord> records, long seq)
    {
        for (int i = 0; i < records.Count; i++) if (records[i].Seq == seq) return i;
        throw new InvalidOperationException("no row with seq " + seq.ToString(CultureInfo.InvariantCulture));
    }
}
