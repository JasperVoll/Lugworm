using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Scripted HttpMessageHandler standing in for a Duet board. Routes by URL
/// path; per-path responders are consumed in order (the last one repeats). Records
/// every request (URI, session key header, body) for assertions.</summary>
sealed class FakeDuet : HttpMessageHandler
{
    public sealed record Seen(string Uri, string? SessionKey, string? Body);
    public readonly List<Seen> Requests = new();
    readonly Dictionary<string, List<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new();
    readonly Dictionary<string, int> _cursor = new();

    /// <summary>What RRF 3.5 reports under move.kinematics for the lab SCARA. Values
    /// match the live M669 (P549.06 D457.23 A-41.1:319.1 B-151.2:151.2); there is
    /// deliberately no minimum-radius field, because the real board reports none.
    ///
    /// The field names are those of ScaraKinematics::objectModelTable - proximalLength,
    /// distalLength, thetaLimits, psiLimits. An earlier version of this fixture invented
    /// names the firmware never emits, so the fake agreed with a parser that could not
    /// read a real board. Keep this string in step with the firmware, not with the
    /// parser.</summary>
    public const string ScaraKinematics =
        "{\"key\":\"move.kinematics\",\"flags\":\"vnd4\",\"result\":{\"name\":\"Scara\","
        + "\"proximalLength\":549.06,\"distalLength\":457.23,"
        + "\"thetaLimits\":[-41.1,319.1],\"psiLimits\":[-151.2,151.2],"
        + "\"crosstalk\":[0,0,0],\"xOffset\":0,\"yOffset\":0}}";

    /// <summary>What the SAME key returns when the query carries no depth flag. rr_model
    /// defaults to depth 1: the object's scalars are reported, but anything nested
    /// collapses to "[]" - and thetaLimits/psiLimits are exactly what the kinematics parse
    /// cannot do without.
    ///
    /// The fake models this deliberately. A fixture that answers in full regardless of
    /// flags let production query with plain "v", fail on every real board, and still show
    /// a green suite - which is how "M669 kinematics are unverified" survived a field-name
    /// fix and reached the machine a second time.</summary>
    public const string ScaraKinematicsDepthLimited =
        "{\"key\":\"move.kinematics\",\"flags\":\"v\",\"result\":{\"name\":\"Scara\","
        + "\"proximalLength\":549.06,\"distalLength\":457.23,"
        + "\"thetaLimits\":[],\"psiLimits\":[],\"crosstalk\":[]}}";

    /// <summary>Picks the reply shape the requested flags earn.</summary>
    public static string KinematicsFor(string query)
        => HasDepthAtLeast(FlagsOf(query), 2) ? ScaraKinematics : ScaraKinematicsDepthLimited;

    static string FlagsOf(string query)
    {
        if (query.Length > 1)
            foreach (string pair in query.TrimStart('?').Split('&'))
                if (pair.StartsWith("flags=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair.Substring(6));
        return "";
    }

    /// <summary>RRF spells depth as `d&lt;n&gt;` inside the flags string, e.g. "vnd4".</summary>
    static bool HasDepthAtLeast(string flags, int minimum)
    {
        for (int i = 0; i < flags.Length; i++)
        {
            if (flags[i] != 'd' && flags[i] != 'D') continue;
            int j = i + 1, value = 0;
            bool anyDigit = false;
            while (j < flags.Length && flags[j] >= '0' && flags[j] <= '9')
            { value = value * 10 + (flags[j] - '0'); j++; anyDigit = true; }
            if (anyDigit && value >= minimum) return true;
        }
        return false;
    }

    public void On(string path, params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        _routes[path] = responders.ToList();
        _cursor[path] = 0;
    }

    public static Func<HttpRequestMessage, HttpResponseMessage> Json(string json)
        => _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static Func<HttpRequestMessage, HttpResponseMessage> Text(string text)
        => _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode code)
        => _ => new HttpResponseMessage(code) { Content = new StringContent("") };

    public static Func<HttpRequestMessage, HttpResponseMessage> Throw()
        => _ => throw new HttpRequestException("simulated network failure");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content == null ? null
            : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
        string? key = request.Headers.TryGetValues("X-Session-Key", out var vals) ? vals.First() : null;
        Requests.Add(new Seen(request.RequestUri!.AbsoluteUri, key, body));

        string path = request.RequestUri!.AbsolutePath.TrimStart('/');
        // rr_model multiplexes several object-model keys over one path. Routing by
        // path alone put state / move.axes / move.kinematics / job on ONE queue, so
        // adding a probe silently shifted every later scripted response (the cursor
        // clamps rather than erroring). Dispatch by "?key=" so each is independent;
        // a bare path route still serves any key not specifically registered.
        string routeKey = path;
        string query = request.RequestUri!.Query;
        if (query.Length > 1)
            foreach (string pair in query.TrimStart('?').Split('&'))
                if (pair.StartsWith("key=", StringComparison.Ordinal))
                {
                    string modelKey = pair.Substring(4);
                    string candidate = path + "?key=" + modelKey;
                    if (_routes.ContainsKey(candidate)) routeKey = candidate;
                    else if (modelKey == "move.kinematics")
                        // A real board always answers this key, so the fake does too
                        // rather than consuming a response scripted for another key.
                        // Register "rr_model?key=move.kinematics" to override.
                        // The reply shape depends on the requested depth, as on a real
                        // board - see KinematicsFor.
                        return Json(KinematicsFor(query))(request);
                    else if (modelKey == "boards")
                        // Same rule for the identity probe (AUD-SAF-004): a modern board
                        // always answers boards, so the fake must too, or the connect-time
                        // probe consumes a response scripted for another key. Register
                        // "rr_model?key=boards" to override.
                        return Json("{\"key\":\"boards\",\"flags\":\"\",\"result\":" +
                                    "[{\"uniqueId\":\"FAKE-BOARD-0001\",\"shortName\":\"2WiFi\"}]}")(request);
                    break;
                }
        if (!_routes.TryGetValue(routeKey, out var list))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
        int i = Math.Min(_cursor[routeKey], list.Count - 1);
        _cursor[routeKey] = _cursor[routeKey] + 1;
        return list[i](request);
    }
}

public class DuetClientTests
{
    const string ConnectOk =
        "{\"err\":0,\"sessionTimeout\":8000,\"boardType\":\"duetwifi102\",\"sessionKey\":12345,\"apiLevel\":1}";
    const string StateIdle = "{\"key\":\"state\",\"flags\":\"vn\",\"result\":{\"status\":\"idle\"}}";

    static DuetPollClient Make(FakeDuet fake) => new DuetPollClient(new DuetClientOptions
    {
        Host = "192.168.1.42",
        Handler = fake,
        RetryBaseDelayMs = 10,   // keep backoff tests fast
    });

    static FakeDuet WithConnect(string connectJson = ConnectOk)
    {
        var f = new FakeDuet();
        f.On("rr_connect", FakeDuet.Json(connectJson));
        f.On("rr_model", FakeDuet.Json(StateIdle));
        return f;
    }

    [Fact]
    public async Task Connect_HappyPath_SetsSessionAndProbesModel()
    {
        var fake = WithConnect();
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.True(c.IsConnected);
        Assert.False(c.Legacy);
        Assert.Equal(1, c.ApiLevel);
        Assert.Equal("duetwifi102", c.BoardType);
        Assert.Contains(fake.Requests, r => r.Uri.Contains("rr_connect") && r.Uri.Contains("password=reprap"));
        // the model probe after connect must carry the session key
        var probe = fake.Requests.First(r => r.Uri.Contains("rr_model"));
        Assert.Equal("12345", probe.SessionKey);
    }

    [Fact]
    public async Task Connect_ModernBoard_VerifiesKinematicsFromObjectModel()
    {
        // The upload/start gate requires KinematicsProbed unconditionally, but the
        // M669 gcode probe is legacy-only. Without an object-model read, every print
        // on a modern board (this lab runs RRF 3.5.1) was refused.
        var fake = WithConnect();
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.False(c.Legacy);
        Assert.True(c.KinematicsProbed);
        Assert.NotNull(c.Kinematics);
        Assert.Equal(549.06, c.Kinematics!.P, 3);
        Assert.Equal(457.23, c.Kinematics!.D, 3);
        Assert.False(c.Kinematics!.HasR);
        // and it must not have used a bare M669 gcode round-trip to get there
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Contains("M669"));
    }

    /// <summary>RRF 3.5.1 answers rr_model but its ScaraKinematics object-model table has
    /// NO proximalLength/distalLength/thetaLimits/psiLimits - those entries arrive in 3.6.
    /// Verified against the board's own Duet2CombinedFirmware.bin, where none of those
    /// literals occur while machinePosition/speedFactor/seqs all do.
    ///
    /// So on the lab machine the model probe cannot succeed at any depth or spelling, and
    /// the gcode M669 reply is the only source. Without this fallback KinematicsProbed
    /// stays false, which does not merely warn - it refuses every upload and start.</summary>
    [Fact]
    public async Task Connect_Rrf351_NoScaraFieldsInModel_FallsBackToM669Gcode()
    {
        var fake = WithConnect();
        fake.On("rr_model?key=move.kinematics",
            FakeDuet.Json("{\"key\":\"move.kinematics\",\"result\":{\"name\":\"Scara\"}}"));
        // the reply drain watches seqs.reply to know a new reply has landed
        fake.On("rr_model?key=seqs.reply",
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":5}"),    // before the send
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":6}"));   // after the send
        fake.On("rr_gcode", FakeDuet.Json("{\"buff\":250}"));
        fake.On("rr_reply", FakeDuet.Text(
            "Kinematics is Scara, proximal arm 549.06mm range -41.1 to 319.1° (continuous), "
            + "distal arm 457.23mm range -151.2 to 151.2° (continuous), "
            + "crosstalk 0.0:0.0:0.0, bed origin (0.0, 0.0), segments/sec 100, "
            + "min. segment length 1.00\n"));

        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.False(c.Legacy);
        Assert.True(c.KinematicsProbed);
        Assert.NotNull(c.Kinematics);
        Assert.Equal(549.06, c.Kinematics!.P, 3);
        Assert.Equal(457.23, c.Kinematics!.D, 3);
        Assert.Equal(-41.1, c.Kinematics!.AMin, 3);
        Assert.Equal(319.1, c.Kinematics!.AMax, 3);
        Assert.Equal(-151.2, c.Kinematics!.BMin, 3);
        Assert.Equal(151.2, c.Kinematics!.BMax, 3);
        Assert.False(c.Kinematics!.HasR);   // the prose report carries no minimum radius
        Assert.Contains(fake.Requests, r => r.Uri.Contains("M669"));
    }

    [Fact]
    public async Task Connect_ModernBoard_UnreadableKinematics_FailsClosed()
    {
        var fake = WithConnect();
        fake.On("rr_model?key=move.kinematics",
            FakeDuet.Json("{\"key\":\"move.kinematics\",\"result\":{\"name\":\"cartesian\"}}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.False(c.KinematicsProbed);
        Assert.Null(c.Kinematics);
    }

    [Fact]
    public async Task Connect_WrongPassword_Throws()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json("{\"err\":1}"));
        using var c = Make(fake);
        var ex = await Assert.ThrowsAsync<DuetException>(() => c.ConnectAsync(CancellationToken.None));
        Assert.Contains("password", ex.Message);
    }

    [Fact]
    public async Task Connect_NoFreeSession_ExplainsDwcTabs()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json("{\"err\":2}"));
        using var c = Make(fake);
        var ex = await Assert.ThrowsAsync<DuetException>(() => c.ConnectAsync(CancellationToken.None));
        Assert.Contains("Duet Web Control", ex.Message);
    }

    [Fact]
    public async Task Connect_LegacyFirmware_FallsBackToRrStatus()
    {
        // rr_connect response of RRF 3.1.1 - no sessionKey, no apiLevel
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json("{\"err\":0,\"sessionTimeout\":8000,\"boardType\":\"duetwifi102\"}"));
        // no rr_model route => 404 on the probe => legacy mode (RRF 3.1.1 path)
        fake.On("rr_status", FakeDuet.Json(
            "{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
            "\"xyz\":[200.0,600.0,400.0],\"machine\":[200.0,600.0,400.0],\"extr\":[0.0]},\"seq\":3}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.True(c.Legacy);
        var snap = await c.GetSnapshotAsync(includeJob: true, CancellationToken.None);

        Assert.Equal(MachineStatus.Idle, snap.Status);
        Assert.True(snap.AllHomed);
        Assert.Equal(600.0, snap.Axis("Y")!.MachinePosition);
        Assert.Contains(fake.Requests, r => r.Uri.Contains("rr_status?type=3"));
        // idle => no rr_fileinfo call
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Contains("rr_fileinfo"));
    }

    [Fact]
    public async Task Legacy_PrintingSnapshot_FetchesJobFileName()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json("{\"err\":0,\"boardType\":\"duetwifi102\"}"));
        fake.On("rr_status", FakeDuet.Json(
            "{\"status\":\"P\",\"coords\":{\"axesHomed\":[1,1,1]," +
            "\"xyz\":[500.0,-100.0,20.0],\"machine\":[500.0,-100.0,20.0],\"extr\":[9.9]}," +
            "\"seq\":4,\"fractionPrinted\":50.0,\"currentLayer\":3,\"printDuration\":120.0}"));
        fake.On("rr_fileinfo", FakeDuet.Json("{\"err\":0,\"size\":1000,\"fileName\":\"0:/gcodes/bowl.gcode\"}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var snap = await c.GetSnapshotAsync(includeJob: true, CancellationToken.None);

        Assert.Equal(MachineStatus.Processing, snap.Status);
        Assert.Equal(0.5, snap.Job!.FractionPrinted, 9);
        Assert.Equal("0:/gcodes/bowl.gcode", snap.Job.FileName);
    }

    [Fact]
    public async Task Legacy_ReplyDrain_UsesSnapshotSeq()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json("{\"err\":0,\"boardType\":\"duetwifi102\"}"));
        fake.On("rr_status",
            FakeDuet.Json("{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
                "\"xyz\":[0,0,0],\"machine\":[0,0,0]},\"seq\":4}"), // legacy M208 probe
            FakeDuet.Json("{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
                "\"xyz\":[0,0,0],\"machine\":[0,0,0]},\"seq\":5}"), // legacy M669 kinematics probe
            FakeDuet.Json("{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
                "\"xyz\":[0,0,0],\"machine\":[0,0,0]},\"seq\":6}"),
            FakeDuet.Json("{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
                "\"xyz\":[0,0,0],\"machine\":[0,0,0]},\"seq\":7}"));
        fake.On("rr_reply", FakeDuet.Text("FIRMWARE_NAME: RepRapFirmware for Duet 2 WiFi 3.1.1\n"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.GetSnapshotAsync(false, CancellationToken.None);           // seq 6
        Assert.Null(await c.FetchNewReplyAsync(CancellationToken.None));   // baseline established

        await c.GetSnapshotAsync(false, CancellationToken.None);           // seq 7
        string? reply = await c.FetchNewReplyAsync(CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Contains("3.1.1", reply);
        // legacy mode must not touch rr_model for the seq
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Contains("rr_model") && r.Uri.Contains("seqs"));
    }

    [Fact]
    public async Task AuthLoss_401_ReconnectsOnceAndRetries()
    {
        var fake = WithConnect();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.Unauthorized),
            FakeDuet.Json("{\"buff\":240}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.SendCodeNoReplyAsync("G28", CancellationToken.None);

        int connects = fake.Requests.Count(r => r.Uri.Contains("rr_connect"));
        Assert.Equal(2, connects);   // initial + re-auth
        int gcodes = fake.Requests.Count(r => r.Uri.Contains("rr_gcode"));
        Assert.Equal(2, gcodes);     // 401 then success
    }

    [Fact]
    public async Task BufferFull_503_BacksOffAndRetries()
    {
        var fake = WithConnect();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.ServiceUnavailable),
            FakeDuet.Json("{\"buff\":100}"));
        fake.On("rr_reply", FakeDuet.Text(""));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.SendCodeNoReplyAsync("G1 X1", CancellationToken.None));

        Assert.Contains("ambiguous", ex.Message);
        Assert.Equal(1, fake.Requests.Count(r => r.Uri.Contains("rr_gcode")));
        Assert.Contains(fake.Requests, r => r.Uri.Contains("rr_reply"));   // drained before surfacing ambiguity
    }

    [Fact]
    public async Task BufferFull_503_DrainedReplyIsKeptNotDiscarded()
    {
        var fake = WithConnect();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.ServiceUnavailable),
            FakeDuet.Json("{\"buff\":100}"));
        fake.On("rr_reply", FakeDuet.Text("Error: G1: target position outside machine limits\n"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.SendCodeNoReplyAsync("G1 X1", CancellationToken.None));
        Assert.Contains("ambiguous", ex.Message);

        string? drained = c.TakeDrainedReply();
        Assert.NotNull(drained);
        Assert.Contains("outside machine limits", drained);
        Assert.Null(c.TakeDrainedReply());   // read-and-clear
    }

    [Fact]
    public async Task SendCode_CorrelatesReplyViaSeq()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json(ConnectOk));
        fake.On("rr_model",
            FakeDuet.Json(StateIdle),                                              // connect probe
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":5}"),                // before send
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":6}"));               // after send
        fake.On("rr_gcode", FakeDuet.Json("{\"buff\":250}"));
        fake.On("rr_reply", FakeDuet.Text("FIRMWARE_NAME: RepRapFirmware for Duet 2 WiFi\n"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        string reply = await c.SendCodeAsync("M115", CancellationToken.None);

        Assert.Contains("RepRapFirmware", reply);
        Assert.Contains(fake.Requests, r => r.Uri.Contains("rr_gcode") && r.Uri.Contains("M115"));
    }

    [Fact]
    public async Task EmergencyStop_DeadSession_ReconnectsAndRepeats()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json(ConnectOk));
        fake.On("rr_model", FakeDuet.Json(StateIdle));
        fake.On("rr_gcode",
            FakeDuet.Throw(),                    // first M112: network failure
            FakeDuet.Throw(),                    // immediate retry also fails
            FakeDuet.Json("{\"buff\":240}"));    // third M112 after re-connect
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.EmergencyStopAsync(CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count(r => r.Uri.Contains("rr_connect")));
        Assert.Equal(3, fake.Requests.Count(r => r.Uri.Contains("M112")));
    }

    [Fact]
    public async Task EmergencyStop_CancelledCallerToken_StillSends()
    {
        var fake = WithConnect();
        fake.On("rr_gcode", FakeDuet.Json("{\"buff\":240}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();   // a stopped session's lifetime token

        await c.EmergencyStopAsync(cts.Token);

        Assert.Equal(1, fake.Requests.Count(r => r.Uri.Contains("M112")));
    }

    [Fact]
    public async Task EmergencyStop_503_RetriesImmediatelyNoBackoffNoDrain()
    {
        var fake = WithConnect();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.ServiceUnavailable),   // buffer full
            FakeDuet.Json("{\"buff\":100}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.EmergencyStopAsync(CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count(r => r.Uri.Contains("M112")));
        // never drains rr_reply and never re-auths on the E-stop path
        Assert.DoesNotContain(fake.Requests, r => r.Uri.Contains("rr_reply"));
        Assert.Equal(1, fake.Requests.Count(r => r.Uri.Contains("rr_connect")));
    }

    [Fact]
    public async Task EmergencyStop_401Twice_ReconnectsThenSends()
    {
        var fake = WithConnect();
        fake.On("rr_gcode",
            FakeDuet.Status(HttpStatusCode.Unauthorized),
            FakeDuet.Status(HttpStatusCode.Unauthorized),
            FakeDuet.Json("{\"buff\":240}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.EmergencyStopAsync(CancellationToken.None);

        Assert.Equal(2, fake.Requests.Count(r => r.Uri.Contains("rr_connect")));   // initial + fresh
        Assert.Equal(3, fake.Requests.Count(r => r.Uri.Contains("M112")));
    }

    [Fact]
    public async Task Upload_SendsCrc_AndVerifiesStoredSize()
    {
        byte[] payload = Encoding.ASCII.GetBytes("G28\nG1 X100 Y0 Z5 F3000\n");
        string expectedCrc = Crc32.HexLower(Crc32.Compute(payload));

        var fake = WithConnect();
        fake.On("rr_upload", FakeDuet.Json("{\"err\":0}"));
        fake.On("rr_fileinfo", FakeDuet.Json($"{{\"err\":0,\"size\":{payload.Length}}}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.UploadAsync("0:/gcodes/test.gcode", payload, CancellationToken.None);

        var up = fake.Requests.First(r => r.Uri.Contains("rr_upload"));
        Assert.Contains($"crc32={expectedCrc}", up.Uri);
        Assert.Contains("0%3A%2Fgcodes%2Ftest.gcode", up.Uri);   // name url-encoded
        Assert.Equal("G28\nG1 X100 Y0 Z5 F3000\n", up.Body);
    }

    [Fact]
    public async Task Upload_ErrNonZero_Throws()
    {
        var fake = WithConnect();
        fake.On("rr_upload", FakeDuet.Json("{\"err\":1}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.UploadAsync("0:/gcodes/t.gcode", new byte[] { 1, 2 }, CancellationToken.None));
        Assert.Contains("rr_upload failed", ex.Message);
    }

    [Fact]
    public async Task Upload_SizeMismatch_Throws()
    {
        var fake = WithConnect();
        fake.On("rr_upload", FakeDuet.Json("{\"err\":0}"));
        fake.On("rr_fileinfo", FakeDuet.Json("{\"err\":0,\"size\":9999}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.UploadAsync("0:/gcodes/t.gcode", new byte[] { 1, 2, 3 }, CancellationToken.None));
        Assert.Contains("verification failed", ex.Message);
    }

    [Fact]
    public async Task GetFileSize_MissingFile_ThrowsDuetException()
    {
        var fake = WithConnect();
        fake.On("rr_fileinfo", FakeDuet.Json("{\"err\":1}"));   // RRF: file not found
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.GetFileSizeAsync("0:/gcodes/gone.gcode", CancellationToken.None));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task Upload_EmptyFile_Refused()
    {
        var fake = WithConnect();
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DuetException>(
            () => c.UploadAsync("0:/gcodes/t.gcode", Array.Empty<byte>(), CancellationToken.None));
    }

    [Fact]
    public async Task FileList_FollowsNextContinuation_ParsesTypes()
    {
        var fake = WithConnect();
        fake.On("rr_filelist",
            FakeDuet.Json("{\"dir\":\"0:/sys\",\"files\":[],\"next\":0,\"err\":0}"), // home-macro probe
            FakeDuet.Json("{\"dir\":\"0:/gcodes\",\"first\":0,\"files\":[" +
                "{\"type\":\"d\",\"name\":\"old\",\"size\":0}," +
                "{\"type\":\"f\",\"name\":\"bowl.gcode\",\"size\":1234}],\"next\":2,\"err\":0}"),
            FakeDuet.Json("{\"dir\":\"0:/gcodes\",\"first\":2,\"files\":[" +
                "{\"type\":\"f\",\"name\":\"vase.gcode\",\"size\":99}],\"next\":0,\"err\":0}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var files = await c.ListFilesAsync("0:/gcodes", CancellationToken.None);

        Assert.Equal(3, files.Count);
        Assert.True(files[0].IsDirectory);
        Assert.Equal("bowl.gcode", files[1].Name);
        Assert.Equal(1234, files[1].Size);
        Assert.Contains(fake.Requests, r => r.Uri.Contains("first=2"));   // continuation page
    }

    [Fact]
    public async Task FileList_MissingDirectory_Throws()
    {
        var fake = WithConnect();
        fake.On("rr_filelist", FakeDuet.Json("{\"err\":2}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.ListFilesAsync("0:/nope", CancellationToken.None));
        Assert.Contains("not found", ex.Message);
    }

    // The 3DPotter Edition Rev10 build (RRF 3.1.1) omits "err" entirely on a
    // successful listing - only failures carry it. Verified on the lab machine
    // (2026-07-20): {"dir":"0:/gcodes","first":0,"files":[...],"next":0}.
    [Fact]
    public async Task FileList_NoErrField_WithFilesArray_Succeeds()
    {
        var fake = WithConnect();
        fake.On("rr_filelist",
            FakeDuet.Json("{\"dir\":\"0:/sys\",\"files\":[],\"next\":0}"),   // home-macro probe
            FakeDuet.Json("{\"dir\":\"0:/gcodes\",\"first\":0,\"files\":[" +
                "{\"type\":\"d\",\"name\":\"JV\",\"size\":0,\"date\":\"2024-10-28T12:25:00\"}," +
                "{\"type\":\"f\",\"name\":\"test.gcode\",\"size\":976300,\"date\":\"2026-07-20T11:14:38\"}]," +
                "\"next\":0}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var files = await c.ListFilesAsync("0:/gcodes", CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.True(files[0].IsDirectory);
        Assert.Equal("test.gcode", files[1].Name);
        Assert.Equal(976300, files[1].Size);
    }

    [Fact]
    public async Task FileList_NoErrAndNoFiles_StaysFailClosed()
    {
        var fake = WithConnect();
        fake.On("rr_filelist", FakeDuet.Json("{\"dir\":\"0:/gcodes\",\"next\":0}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.ListFilesAsync("0:/gcodes", CancellationToken.None));
        Assert.Contains("neither err nor files", ex.Message);
    }

    [Fact]
    public async Task Download_ReturnsBytes_404Throws()
    {
        var fake = WithConnect();
        fake.On("rr_download", FakeDuet.Text("G28\nG1 X100\n"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        byte[] bytes = await c.DownloadAsync("0:/gcodes/bowl.gcode", CancellationToken.None);
        Assert.Equal("G28\nG1 X100\n", Encoding.UTF8.GetString(bytes));

        var fake2 = WithConnect();
        fake2.On("rr_download", FakeDuet.Status(HttpStatusCode.NotFound));
        using var c2 = Make(fake2);
        await c2.ConnectAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c2.DownloadAsync("0:/gcodes/gone.gcode", CancellationToken.None));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task DeleteFile_ErrNonZero_Throws()
    {
        var fake = WithConnect();
        fake.On("rr_delete",
            FakeDuet.Json("{\"err\":0}"),
            FakeDuet.Json("{\"err\":1}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.DeleteFileAsync("0:/gcodes/a.gcode", CancellationToken.None);   // ok
        await Assert.ThrowsAsync<DuetException>(
            () => c.DeleteFileAsync("0:/gcodes/b.gcode", CancellationToken.None));
        Assert.Contains(fake.Requests, r => r.Uri.Contains("rr_delete") && r.Uri.Contains("a.gcode"));
    }

    [Fact]
    public async Task MoveFile_EncodesBothPaths_ErrThrows()
    {
        var fake = WithConnect();
        fake.On("rr_move",
            FakeDuet.Json("{\"err\":0}"),
            FakeDuet.Json("{\"err\":1}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        await c.MoveFileAsync("0:/gcodes/a.gcode", "0:/gcodes/old/a.gcode", CancellationToken.None);
        var mv = fake.Requests.First(r => r.Uri.Contains("rr_move"));
        Assert.Contains("old=0%3A%2Fgcodes%2Fa.gcode", mv.Uri);
        Assert.Contains("new=0%3A%2Fgcodes%2Fold%2Fa.gcode", mv.Uri);

        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.MoveFileAsync("0:/gcodes/x.gcode", "0:/gcodes/y.gcode", CancellationToken.None));
        Assert.Contains("move failed", ex.Message);
    }

    [Fact]
    public void MoveTarget_Resolution()
    {
        const string cur = "0:/gcodes/bowl.gcode";
        // bare name = rename in place
        Assert.Equal("0:/gcodes/vase.gcode", DuetFilePath.ResolveMoveTarget(cur, "vase.gcode"));
        // trailing slash = move keeping the name
        Assert.Equal("0:/gcodes/old/bowl.gcode", DuetFilePath.ResolveMoveTarget(cur, "0:/gcodes/old/"));
        // full path taken verbatim
        Assert.Equal("0:/macros/bowl.gcode", DuetFilePath.ResolveMoveTarget(cur, "0:/macros/bowl.gcode"));
        // invalid inputs
        Assert.Throws<ArgumentException>(() => DuetFilePath.ResolveMoveTarget(cur, ""));
        Assert.Throws<ArgumentException>(() => DuetFilePath.ResolveMoveTarget(cur, "0:/gcodes/bowl.gcode"));
        Assert.Throws<ArgumentException>(() => DuetFilePath.ResolveMoveTarget(cur, "/etc/passwd"));
    }

    [Fact]
    public async Task GetModel_FollowsNextContinuation()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json(ConnectOk));
        fake.On("rr_model",
            FakeDuet.Json(StateIdle),   // connect probe
            FakeDuet.Json("{\"key\":\"move.axes\",\"result\":[{\"letter\":\"X\"},{\"letter\":\"Y\"}],\"next\":2}"),
            FakeDuet.Json("{\"key\":\"move.axes\",\"result\":[{\"letter\":\"Z\"}],\"next\":0}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var axes = await c.GetModelAsync("move.axes", "vnd4", CancellationToken.None);

        Assert.Equal(3, axes.Count);
        Assert.Equal("Z", axes[2].Str("letter"));
        Assert.Contains(fake.Requests, r => r.Uri.Contains("flags=vnd4a2"));   // continuation page
    }

    [Fact]
    public async Task Snapshot_ParsesStateAxesJob()
    {
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json(ConnectOk));
        fake.On("rr_model",
            FakeDuet.Json(StateIdle),   // connect probe
            FakeDuet.Json("{\"key\":\"state\",\"result\":{\"status\":\"processing\"}}"),
            FakeDuet.Json("{\"key\":\"move.axes\",\"result\":[" +
                "{\"letter\":\"X\",\"machinePosition\":400,\"homed\":true}," +
                "{\"letter\":\"Y\",\"machinePosition\":-300,\"homed\":true}," +
                "{\"letter\":\"Z\",\"machinePosition\":25,\"homed\":true}]}"),
            FakeDuet.Json("{\"key\":\"job\",\"result\":{\"file\":{\"fileName\":\"0:/gcodes/a.gcode\",\"size\":100}," +
                "\"filePosition\":50}}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        var snap = await c.GetSnapshotAsync(includeJob: true, CancellationToken.None);

        Assert.Equal(MachineStatus.Processing, snap.Status);
        Assert.True(snap.AllHomed);
        Assert.Equal(400, snap.Axis("X")!.MachinePosition);
        Assert.Equal(0.5, snap.Job!.FractionPrinted, 9);
    }

    [Fact]
    public async Task ReplyDrain_PollLoopDeliversReplyToWaitingCommand()
    {
        // Phase 4: rr_reply is read-and-clear with a single-consumer contract. When
        // the poll loop is running, a command waits for the loop's single drain to
        // dispatch its reply - neither issues a competing rr_reply GET, and the text
        // reaches both the command and the loop (nothing lost to the read-and-clear).
        var fake = new FakeDuet();
        fake.On("rr_connect", FakeDuet.Json(ConnectOk));
        fake.On("rr_model",
            FakeDuet.Json(StateIdle),                                  // connect state probe
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":5}"),    // BeginReplyPolling baseline
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":5}"),    // command seqBefore
            FakeDuet.Json("{\"key\":\"seqs.reply\",\"result\":6}"));   // poll sees the advance
        fake.On("rr_gcode", FakeDuet.Json("{\"buff\":250}"));
        fake.On("rr_reply", FakeDuet.Text("FIRMWARE_NAME: RepRapFirmware for Duet 2 WiFi\n"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);
        await c.BeginReplyPollingAsync(CancellationToken.None);

        var sendTask = c.SendCodeAndWaitForReplyAsync("M115", TimeSpan.FromSeconds(5), CancellationToken.None);

        // stand in for the poll loop: drain until the waiting command is served
        string? polled = null;
        for (int i = 0; i < 100 && !sendTask.IsCompleted; i++)
        {
            polled = await c.FetchNewReplyAsync(CancellationToken.None);
            if (polled != null) break;
            await Task.Delay(10);
        }

        string? reply = await sendTask;
        Assert.Contains("RepRapFirmware", reply);   // command received it
        Assert.Contains("RepRapFirmware", polled);   // poll loop received it too - none lost
        // exactly one rr_reply GET despite two consumers
        Assert.Equal(1, fake.Requests.Count(r => r.Uri.Contains("rr_reply")));
    }

    // ---- AUD-SAF-004: machine identity probe --------------------------------------

    [Fact]
    public async Task ModernBoard_FingerprintFromUniqueId()
    {
        var fake = WithConnect();
        fake.On("rr_model?key=boards", FakeDuet.Json(
            "{\"key\":\"boards\",\"flags\":\"vnd3\",\"result\":[{\"uniqueId\":\"08DGM-9T66A-G63SJ\",\"shortName\":\"2WiFi\"}]}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.NotNull(c.Fingerprint);
        Assert.Equal(MachineFingerprintKind.UniqueId, c.Fingerprint!.Kind);
        Assert.Equal("08DGM-9T66A-G63SJ", c.Fingerprint.Value);
    }

    [Fact]
    public async Task NoUniqueId_FallsBackToConfigurationFingerprint()
    {
        // a board without a uniqueId still identifies its CONFIGURATION
        // (board type + kinematics + config.g), and the kind says so
        var fake = WithConnect();
        fake.On("rr_model?key=boards", FakeDuet.Json(
            "{\"key\":\"boards\",\"flags\":\"vnd3\",\"result\":[{\"shortName\":\"2WiFi\"}]}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        Assert.NotNull(c.Fingerprint);
        Assert.Equal(MachineFingerprintKind.Configuration, c.Fingerprint!.Kind);
        Assert.Contains("not the physical board", c.Fingerprint.Display);
    }

    [Fact]
    public async Task SameConfiguration_YieldsStableFingerprintAcrossReconnects()
    {
        var fake = WithConnect();
        fake.On("rr_model?key=boards", FakeDuet.Json(
            "{\"key\":\"boards\",\"flags\":\"vnd3\",\"result\":[{\"shortName\":\"2WiFi\"}]}"));
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);
        var first = c.Fingerprint;
        await c.ConnectAsync(CancellationToken.None);   // reconnect reprobes
        Assert.NotNull(first);
        Assert.True(first!.Matches(c.Fingerprint));
    }

    // ---- AUD-SAF-001: the 4 MiB JSON cap is enforced while streaming --------------

    [Fact]
    public async Task OversizedDeclaredBody_IsRefused()
    {
        var fake = WithConnect();
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        fake.On("rr_model?key=state", _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[5 * 1024 * 1024]) });   // > 4 MiB declared
        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.GetSnapshotAsync(includeJob: false, CancellationToken.None));
        Assert.Contains("byte limit", ex.Message);
    }

    /// <summary>A body without Content-Length (chunked) must abort AT the cap during
    /// the read, not after materializing the whole response.</summary>
    sealed class EndlessContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(System.IO.Stream stream,
            System.Net.TransportContext? context)
        {
            var chunk = new byte[64 * 1024];
            // 6 MiB total - past the cap, far below anything that could pass unnoticed
            for (int i = 0; i < 96; i++) await stream.WriteAsync(chunk, 0, chunk.Length);
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    [Fact]
    public async Task OversizedChunkedBody_AbortsAtTheCap()
    {
        var fake = WithConnect();
        using var c = Make(fake);
        await c.ConnectAsync(CancellationToken.None);

        fake.On("rr_model?key=state", _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new EndlessContent() });
        var ex = await Assert.ThrowsAsync<DuetException>(
            () => c.GetSnapshotAsync(includeJob: false, CancellationToken.None));
        Assert.Contains("byte limit", ex.Message);
    }
}
