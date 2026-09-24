using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Lugworm.Core.Machine;

namespace Lugworm.Core.Duet
{
    /// <summary>Protocol/session error talking to the Duet (wrong password, no free
    /// session, err codes, verification failures). Transport errors surface as the
    /// usual HttpRequestException / OperationCanceledException.</summary>
    public sealed class DuetException : Exception
    {
        /// <summary>The HTTP status behind a refused request, when one was involved.</summary>
        public int? StatusCode { get; }
        public DuetException(string message) : base(message) { }
        public DuetException(string message, Exception inner) : base(message, inner) { }
        public DuetException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    }

    public sealed class UploadVerification
    {
        public long Size;
        public bool CrcConfirmed;
        public string Crc32 = "";
        /// <summary>Optional transport-specific verification summary.</summary>
        public string? SummaryOverride;
        public string Summary => SummaryOverride ?? (CrcConfirmed ? "CRC + size verified" : "CRC requested; size verified");
    }

    public sealed class TransferVerification
    {
        public long Size;
        public string Crc32 = "";
        public string Sha256 = "";
    }

    /// <summary>Minimal client for a standalone Duet 2/3 running RepRapFirmware 3
    /// over the rr_* HTTP API (the Potterbot Scara v4 path). Clean-room against the
    /// protocol; the GPL DSF client was used as an endpoint reference only.
    /// Thread-safety: any number of concurrent calls is fine - session (re)auth is
    /// serialized internally; RRF itself serializes G-code per session.</summary>
    public sealed class DuetPollClient : IMachineClient
    {
        public MachineCapabilities Capabilities => MachineCapabilities.Duet;
        /// <summary>RRF boards are SCARA or Cartesian, both modelled; never refused here.</summary>
        public string? KinematicsRefusal => null;
        /// <summary>RRF keeps no temperature history; the session's own ring starts at connect.</summary>
        readonly DuetClientOptions _opt;
        readonly HttpClient _http;
        readonly SemaphoreSlim _authLock = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim _replyDrainLock = new SemaphoreSlim(1, 1);
        readonly object _replyWaiterLock = new object();
        readonly List<ReplyWaiter> _replyWaiters = new List<ReplyWaiter>();
        const int MaxJsonBodyBytes = 4 * 1024 * 1024;
        const int MaxListEntries = 100000;

        long _sessionKey = -1;          // -1 = none (header omitted)
        volatile bool _connected;
        volatile bool _replyPolling;
        long _connectionGeneration;
        readonly Dictionary<string, bool> _homeMacros = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (double min, double max)> _legacyM208 = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

        public DuetPollClient(DuetClientOptions options)
        {
            _opt = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_opt.Host))
                throw new ArgumentException("Host must be set", nameof(options));
            HttpMessageHandler handler = _opt.Handler ?? new HttpClientHandler();
            if (_opt.Handler == null)
            {
                // the default limit (2 on .NET Framework) can park an emergency stop
                // behind the poll plus an upload - give the board channel headroom
                try { ((HttpClientHandler)handler).MaxConnectionsPerServer = 8; }
                catch (PlatformNotSupportedException) { /* pre-4.7.1 - ServicePoint below */ }
            }
            _http = new HttpClient(handler, disposeHandler: _opt.Handler == null)
            {
                BaseAddress = new Uri("http://" + _opt.Host.Trim().TrimEnd('/') + "/"),
                Timeout = Timeout.InfiniteTimeSpan,   // per-request CTS timeouts below
            };
#if NETFRAMEWORK
            // best effort - HttpClientHandler limit above usually suffices; a legacy
            // ServicePointManager that refuses to resolve or configure this endpoint
            // still leaves the handler-level limit set.
            try { ServicePointManager.FindServicePoint(_http.BaseAddress).ConnectionLimit = 8; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException) { }
#endif
        }

        public bool IsConnected => _connected;
        public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);
        public int ApiLevel { get; private set; }
        public string? BoardType { get; private set; }
        /// <summary>True when the board answered rr_connect but not rr_model - a
        /// pre-3.2 RepRapFirmware (the Potterbot ships RRF 3.1.1 "3DPotter Edition").
        /// Status then comes from the legacy rr_status?type=3 API.</summary>
        public bool Legacy { get; private set; }
        public bool LegacyM208Probed { get; private set; }
        public bool KinematicsProbed { get; private set; }
        public M669Parameters? Kinematics { get; private set; }
        public M669Parameters? M669 => Kinematics;
        public FirmwareMotionLimits? MotionBaseline { get; private set; }
        public bool MotionBaselineProbed { get; private set; }
        public bool HomeProbeComplete { get; private set; }
        /// <summary>Raised before a command known to invoke an opaque firmware
        /// macro (pause/resume/cancel) is sent. Session state must be discarded.</summary>
        public event Action? OpaqueMotionIssued;
        public bool HomeAvailable(string axis)
            => !HomeProbeComplete || (_homeMacros.TryGetValue(axis ?? "", out var ok) && ok);

        volatile int _legacyReplySeq = -1;   // "seq" of the last rr_status snapshot

        sealed class ReplyWaiter
        {
            public readonly TaskCompletionSource<string?> Completion =
                new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // ---- session -------------------------------------------------------

        public async Task ConnectAsync(CancellationToken ct)
        {
            await _authLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ConnectLockedAsync(ct).ConfigureAwait(false); }
            finally { _authLock.Release(); }

            // capability probe: RRF3 answers rr_model, RRF2 404s it. Transport
            // and transient server failures are retried; only a definitive 404
            // negotiates the legacy path.
            for (int attempt = 0; ; attempt++)
            {
                try { await GetModelAsync("state", "vn", ct).ConfigureAwait(false); Legacy = false; break; }
                catch (DuetException ex) when (ex.Message.Contains("HTTP 404")) { Legacy = true; break; }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(150 * (attempt + 1), ct).ConfigureAwait(false); }
                catch (DuetException ex) when (ex.Message.Contains("HTTP 5") && attempt < 2)
                { await Task.Delay(150 * (attempt + 1), ct).ConfigureAwait(false); }
            }
            // Legacy boards (RRF < 3.2) do not expose axis limits or kinematics in the
            // object model, so probe both with bare gcode queries. Modern boards carry
            // them in the model; a gcode M669 round-trip there would steal the reply
            // stream from user commands, so kinematics are read from the model instead.
            // Both paths must set KinematicsProbed: the upload/start gate requires it
            // unconditionally, so leaving it unset on modern boards refuses every print.
            if (Legacy)
            {
                await ProbeLegacyM208Async(ct).ConfigureAwait(false);
                await ProbeKinematicsAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await ProbeModelKinematicsAsync(ct).ConfigureAwait(false);
                // RRF 3.5.1 answers rr_model but does NOT publish SCARA geometry in it:
                // proximalLength / distalLength / thetaLimits / psiLimits appear in the
                // ScaraKinematics object-model table only from 3.6. Verified against the
                // board's own Duet2CombinedFirmware.bin (3.5.1), where none of those
                // literals exist while machinePosition/speedFactor/seqs all do, and the
                // M669 reply literals "proximal arm"/"distal arm"/"crosstalk" do.
                // So on 3.5.x the model probe cannot succeed at any depth or spelling and
                // the gcode M669 reply is the only source. Fall back to it rather than
                // leaving the machine permanently "unverified", which also refuses
                // upload/start. Safe here: the rr_reply drain is serialized and already
                // serves pre-poll probes on the legacy path above.
                if (!KinematicsProbed)
                    await ProbeKinematicsAsync(ct).ConfigureAwait(false);
            }
            await ProbeMotionBaselineAsync(ct).ConfigureAwait(false);
            try { await ProbeHomeMacrosAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { HomeProbeComplete = false; } // unknown is safer than disabling a real macro
            await ProbeFingerprintAsync(ct).ConfigureAwait(false);
        }

        /// <summary>The connected machine's identity (AUD-SAF-004), probed once per
        /// connection: boards[0].uniqueId on modern boards, else a configuration hash
        /// (board type + kinematics + config.g + axis limits). The approved design's
        /// M122 middle leg is deliberately omitted for now because it shares the reply
        /// drain; see the implementation note below. Null when no probe succeeded -
        /// callers fail closed.</summary>
        public MachineFingerprint? Fingerprint { get; private set; }
        public string? FingerprintProbeDetail { get; private set; }

        byte[]? _configBytesForFingerprint;

        public async Task ProbeFingerprintAsync(CancellationToken ct)
        {
            Fingerprint = null;
            FingerprintProbeDetail = null;

            if (!Legacy)
            {
                try
                {
                    var boards = await GetModelAsync("boards", "vnd3", ct).ConfigureAwait(false);
                    string uid = boards[0].Str("uniqueId", "").Trim();
                    if (uid.Length > 0)
                    {
                        Fingerprint = new MachineFingerprint(MachineFingerprintKind.UniqueId, uid);
                        return;
                    }
                    FingerprintProbeDetail = "boards[0].uniqueId is empty";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { FingerprintProbeDetail = "boards probe failed: " + ex.Message; }
            }

            // Configuration fallback: proves the configured machine, not the physical
            // board - the Display says so. An M122 board-id probe was deliberately NOT
            // added here: it would run through the shared gcode/reply drain at connect
            // time and perturb reply correlation for queued user commands, and legacy
            // boards already contribute board type, config.g and the probed M208 limits.
            string kin = Kinematics != null
                ? FormattableString.Invariant($"P{Kinematics.P:R} D{Kinematics.D:R} R{(Kinematics.HasR ? Kinematics.R : double.NaN):R}")
                : KinematicsProbeDetail ?? "";
            if (BoardType != null || _configBytesForFingerprint != null || kin.Length > 0)
                Fingerprint = MachineFingerprint.Configuration(BoardType, kin, _configBytesForFingerprint, _legacyM208);
            else
                FingerprintProbeDetail = (FingerprintProbeDetail == null ? "" : FingerprintProbeDetail + "; ")
                                         + "no configuration evidence available";
        }

        /// <summary>Reads the intended motion baseline from the board's startup
        /// file. This is diagnostic state, not a print prerequisite: a missing or
        /// malformed config.g leaves the baseline unverified and never prevents a
        /// connection or later command.</summary>
        public async Task ProbeMotionBaselineAsync(CancellationToken ct)
        {
            MotionBaseline = null;
            MotionBaselineProbed = false;
            _configBytesForFingerprint = null;
            try
            {
                byte[] bytes = await DownloadAsync("0:/sys/config.g", ct).ConfigureAwait(false);
                _configBytesForFingerprint = bytes;   // reused by the identity fallback (AUD-SAF-004)
                string text = Encoding.UTF8.GetString(bytes);
                var parsed = FirmwareMotionLimits.ParseConfig(
                    text.Split(new[] { '\r', '\n' }, StringSplitOptions.None));
                if (!parsed.HasAnyLimit) return;
                MotionBaseline = parsed;
                MotionBaselineProbed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                MotionBaseline = null;
                MotionBaselineProbed = false;
            }
        }

        /// <summary>Reads the currently effective M203/M201/M566 values. These
        /// bare commands report state and do not set limits or move the machine.
        /// A failed or wholly unrecognised probe is returned as null so callers
        /// can warn without turning this diagnostic into a new refusal gate.</summary>
        public async Task<LiveMotionState?> QueryLiveMotionStateAsync(CancellationToken ct)
        {
            try
            {
                var state = new LiveMotionState();
                state.Merge(FirmwareMotionLimits.ParseReply(
                    await SendCodeAsync("M203", ct).ConfigureAwait(false), MotionReplyKind.MaxSpeed));
                state.Merge(FirmwareMotionLimits.ParseReply(
                    await SendCodeAsync("M201", ct).ConfigureAwait(false), MotionReplyKind.Acceleration));
                state.Merge(FirmwareMotionLimits.ParseReply(
                    await SendCodeAsync("M566", ct).ConfigureAwait(false), MotionReplyKind.Jerk));
                return state.HasAnyValue ? state : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                return null;
            }
        }

        async Task ProbeLegacyM208Async(CancellationToken ct)
        {
            try
            {
                string reply = await SendCodeAsync("M208", ct).ConfigureAwait(false);
                var parsed = new Dictionary<string, (double min, double max)>(StringComparer.OrdinalIgnoreCase);
                if (!TryParseM208(reply, parsed) || parsed.Count == 0)
                {
                    _legacyM208.Clear();
                    LegacyM208Probed = false;
                    return;
                }
                _legacyM208.Clear();
                foreach (var entry in parsed) _legacyM208[entry.Key] = entry.Value;
                LegacyM208Probed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                _legacyM208.Clear();
                LegacyM208Probed = false;
            }
        }

        /// <summary>Parses both the command form (S1 minima/S0 maxima) and the
        /// paired limits returned by a bare M208 query. The destination is only
        /// changed after a complete axis range has been recognized.</summary>
        static bool TryParseM208(string reply, IDictionary<string, (double min, double max)> limits)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            var staged = new Dictionary<string, (double? min, double? max)>(StringComparer.OrdinalIgnoreCase);
            const string number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
            bool recognized = false;

            foreach (var raw in (reply ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int selector = 0; // RRF defaults omitted S to S0 (maximum).
                Match selectorMatch = Regex.Match(raw, $@"(?<![A-Za-z])S\s*(?<value>{number})",
                    RegexOptions.IgnoreCase);
                if (selectorMatch.Success)
                {
                    if (!double.TryParse(selectorMatch.Groups["value"].Value, NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out double selectorValue)
                        || (Math.Abs(selectorValue) > 1e-9 && Math.Abs(selectorValue - 1.0) > 1e-9))
                        return FailM208(limits);
                    selector = (int)Math.Round(selectorValue);
                }

                var pairs = Regex.Matches(raw,
                    $@"(?<axis>[XYZ])\s*(?<min>{number})\s*:\s*(?<max>{number})",
                    RegexOptions.IgnoreCase);
                if (pairs.Count > 0)
                {
                    foreach (Match pair in pairs)
                    {
                        if (!TryNumber(pair.Groups["min"].Value, out double min)
                            || !TryNumber(pair.Groups["max"].Value, out double max)
                            || min > max)
                            return FailM208(limits);
                        string axis = pair.Groups["axis"].Value.ToUpperInvariant();
                        staged[axis] = (min, max);
                        recognized = true;
                    }
                    continue;
                }

                var words = Regex.Matches(raw,
                    $@"(?<axis>[XYZ])\s*(?<value>{number})",
                    RegexOptions.IgnoreCase);
                foreach (Match word in words)
                {
                    if (!TryNumber(word.Groups["value"].Value, out double value))
                        return FailM208(limits);
                    string axis = word.Groups["axis"].Value.ToUpperInvariant();
                    staged.TryGetValue(axis, out var old);
                    if (selector == 0) old.max = value;
                    else old.min = value;
                    staged[axis] = old;
                    recognized = true;
                }
            }

            var complete = new Dictionary<string, (double min, double max)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in staged)
            {
                if (!entry.Value.min.HasValue || !entry.Value.max.HasValue
                    || double.IsNaN(entry.Value.min.Value) || double.IsNaN(entry.Value.max.Value)
                    || double.IsInfinity(entry.Value.min.Value) || double.IsInfinity(entry.Value.max.Value))
                    continue;
                if (entry.Value.min.Value > entry.Value.max.Value) return FailM208(limits);
                complete[entry.Key] = (entry.Value.min.Value, entry.Value.max.Value);
            }
            if (!recognized || complete.Count == 0) return FailM208(limits);
            limits.Clear();
            foreach (var entry in complete) limits[entry.Key] = entry.Value;
            return true;

            static bool TryNumber(string value, out double parsed)
                => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
            static bool FailM208(IDictionary<string, (double min, double max)> destination)
            {
                destination.Clear();
                return false;
            }
        }

        public static bool TryParseM669(string reply, out M669Parameters result)
        {
            result = new M669Parameters();
            string text = (reply ?? "").Split(new[] { ';' }, 2)[0];
            const string number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
            bool Single(char letter, out double value)
            {
                value = 0;
                Match m = Regex.Match(text, $@"(?<![A-Za-z]){letter}\s*(?<value>{number})",
                    RegexOptions.IgnoreCase);
                return m.Success && double.TryParse(m.Groups["value"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value)
                    && !double.IsNaN(value) && !double.IsInfinity(value);
            }
            bool Pair(char letter, out double min, out double max)
            {
                Match m = Regex.Match(text,
                    $@"(?<![A-Za-z]){letter}\s*(?<min>{number})\s*:\s*(?<max>{number})",
                    RegexOptions.IgnoreCase);
                if (!m.Success || !double.TryParse(m.Groups["min"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out min)
                    || !double.TryParse(m.Groups["max"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out max)
                    || double.IsNaN(min) || double.IsNaN(max)
                    || double.IsInfinity(min) || double.IsInfinity(max)
                    || min > max)
                { min = max = 0; return false; }
                return true;
            }
            if (!Single('P', out result.P) || !Single('D', out result.D)
                || !Pair('A', out result.AMin, out result.AMax)
                || !Pair('B', out result.BMin, out result.BMax))
            {
                // RRF answers a bare M669 in PROSE, not in M669 words. ScaraKinematics
                // reports "Kinematics is Scara, proximal arm 549.06mm range -41.1 to
                // 319.1 (continuous), distal arm 457.23mm range -151.2 to 151.2,
                // crosstalk 0.0:0.0:0.0, bed origin (0.0, 0.0), ..." - which is exactly
                // what this lab's 3.5.1 board returns. The P/D/A/B letter form above is
                // the SET syntax, and a parser that understood only it rejected every
                // real reply. Both are accepted.
                if (TryParseM669Prose(text, result)) return true;
                result = new M669Parameters();
                return false;
            }
            // R is OPTIONAL: RRF leaves the R word out of the M669 report when the
            // minimum radius is unset, which is the normal case on this machine
            // (live reply: "P549.06 D457.23 A-41.1:319.1 B-151.2:151.2"). Requiring
            // it made every real reply unparseable, so kinematics never verified.
            result.HasR = Single('R', out result.R);
            return true;
        }

        /// <summary>Reads the prose form of an M669 report, which is what RepRapFirmware
        /// actually answers a bare M669 with:
        /// <code>
        /// Kinematics is Scara, proximal arm 549.06mm range -41.1 to 319.1 (continuous),
        /// distal arm 457.23mm range -151.2 to 151.2, crosstalk 0.0:0.0:0.0,
        /// bed origin (0.0, 0.0), segments/sec 100, min. segment length 1.00
        /// </code>
        /// The degree sign and the "(continuous)" suffix are not required to be present,
        /// so the same pattern reads replies from firmware that omits either. There is no
        /// minimum radius in this format at all, which is why <see cref="M669Parameters.HasR"/>
        /// has to stay optional.</summary>
        static bool TryParseM669Prose(string text, M669Parameters result)
        {
            const string number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
            Match Arm(string which) => Regex.Match(text,
                which + @"\s+arm\s*(?<len>" + number + @")\s*mm\s*range\s*(?<min>" + number
                      + @")\s*(?:to|:)\s*(?<max>" + number + ")",
                RegexOptions.IgnoreCase);

            Match proximal = Arm("proximal"), distal = Arm("distal");
            if (!proximal.Success || !distal.Success) return false;

            return Num(proximal.Groups["len"], out result.P)
                && Num(distal.Groups["len"], out result.D)
                && Num(proximal.Groups["min"], out result.AMin)
                && Num(proximal.Groups["max"], out result.AMax)
                && Num(distal.Groups["min"], out result.BMin)
                && Num(distal.Groups["max"], out result.BMax)
                && result.AMin <= result.AMax && result.BMin <= result.BMax;

            static bool Num(Group group, out double value)
                => double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                   out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Reads SCARA kinematics from the object model (RRF 3.2+). This is
        /// the modern-board counterpart of the legacy <see cref="ProbeKinematicsAsync"/>
        /// gcode probe: it costs one rr_model read at connect and, unlike a bare M669,
        /// never competes for the shared rr_reply stream.</summary>
        public async Task ProbeModelKinematicsAsync(CancellationToken ct)
        {
            try
            {
                // Flags MUST carry a depth. rr_model defaults to depth 1, which reports an
                // object's scalar members but collapses anything nested to "[]" - and the
                // two values this parse cannot do without, thetaLimits and psiLimits, are
                // arrays. With plain "v" the board returns proximalLength/distalLength but
                // empty limit arrays, the parse fails, and the machine reports "kinematics
                // unverified" forever while upload/start stays refused. Depth 4 matches the
                // move.axes query, which reads nested data from this same board correctly.
                var kin = await GetModelAsync("move.kinematics", "vnd4", ct).ConfigureAwait(false);
                if (TryParseModelKinematics(kin, out var parsed))
                {
                    Kinematics = parsed;
                    KinematicsProbed = true;
                    KinematicsProbeDetail = null;
                    return;
                }
                // Say WHAT was unreadable. "did not return a parseable reply" sent two
                // separate diagnoses of this path down the wrong track.
                KinematicsProbeDetail = DescribeKinematicsReply(kin);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // fall through to unverified - the upload gate refuses, fail closed
                KinematicsProbeDetail = "move.kinematics query failed: " + ex.Message;
            }
            Kinematics = null;
            KinematicsProbed = false;
        }

        /// <summary>Why the last kinematics probe did not verify, or null when it did.
        /// Surfaced in the component warning so the failure names its own cause.</summary>
        public string? KinematicsProbeDetail { get; private set; }

        static string DescribeKinematicsReply(JsonValue? kin)
        {
            if (kin == null || kin.IsNull) return "move.kinematics was absent from the object model";
            if (kin.Kind != JsonKind.Object) return "move.kinematics was not an object";
            string name = kin.Str("name", "");
            if (name.Length > 0 && name.IndexOf("scara", StringComparison.OrdinalIgnoreCase) < 0)
                return $"the board reports '{name}' kinematics, not SCARA";
            var missing = new List<string>();
            if (kin.At("proximalLength").Kind != JsonKind.Number
                && kin.At("proximalArmLength").Kind != JsonKind.Number) missing.Add("proximalLength");
            if (kin.At("distalLength").Kind != JsonKind.Number
                && kin.At("distalArmLength").Kind != JsonKind.Number) missing.Add("distalLength");
            if (kin.At("thetaLimits").Count < 2) missing.Add("thetaLimits");
            if (kin.At("psiLimits").Count < 2) missing.Add("psiLimits");
            return missing.Count == 0
                ? "move.kinematics was readable but held no usable values"
                : "move.kinematics is missing or empty for: " + string.Join(", ", missing);
        }

        /// <summary>Maps RRF's `move.kinematics` object onto the same parameter block a
        /// bare M669 yields. Alternate spellings are accepted because the object-model
        /// field names have varied across RRF releases; an unrecognised or non-SCARA
        /// kinematics returns false, which leaves the machine "unverified" (fail closed)
        /// rather than silently comparing against zeros.</summary>
        public static bool TryParseModelKinematics(JsonValue? kin, out M669Parameters result)
        {
            result = new M669Parameters();
            if (kin == null || kin.IsNull || kin.Kind != JsonKind.Object) return false;

            string name = kin.Str("name", "");
            if (name.Length > 0 && name.IndexOf("scara", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            bool Number(out double value, params string[] paths)
            {
                foreach (string path in paths)
                {
                    var node = kin.At(path);
                    if (node.Kind != JsonKind.Number) continue;
                    value = node.AsNumber();
                    if (!double.IsNaN(value) && !double.IsInfinity(value)) return true;
                }
                value = 0;
                return false;
            }
            bool Limits(out double min, out double max, params string[] paths)
            {
                foreach (string path in paths)
                {
                    var node = kin.At(path);
                    if (node.Kind != JsonKind.Array || node.Count < 2) continue;
                    if (node[0].Kind != JsonKind.Number || node[1].Kind != JsonKind.Number) continue;
                    min = node[0].AsNumber();
                    max = node[1].AsNumber();
                    if (!double.IsNaN(min) && !double.IsNaN(max)
                        && !double.IsInfinity(min) && !double.IsInfinity(max) && min <= max)
                        return true;
                }
                min = max = 0;
                return false;
            }

            // The FIRST name in each list is what RepRapFirmware actually publishes -
            // verified against ScaraKinematics::objectModelTable, which declares exactly:
            //   crosstalk, distalLength, minRadius, name, proximalLength, psiLimits,
            //   thetaLimits, xOffset, yOffset
            // The remaining spellings are tolerated for other/older builds. Every name
            // this parser originally carried was wrong, so no real board ever parsed and
            // KinematicsProbed was permanently false - which shows as the standing
            // "kinematics are unverified" warning AND refuses upload/start.
            if (!Number(out result.P, "proximalLength", "proximalArmLength", "proximalArmLen", "armLengths.0")
                || !Number(out result.D, "distalLength", "distalArmLength", "distalArmLen", "armLengths.1")
                || !Limits(out result.AMin, out result.AMax,
                           "thetaLimits", "proximalArmAngleLimits", "proximalArmLimits")
                || !Limits(out result.BMin, out result.BMax,
                           "psiLimits", "distalArmAngleLimits", "distalArmLimits"))
                return false;

            result.HasR = Number(out result.R, "minRadius", "minimumRadius", "radiusLimits.0");
            return true;
        }

        public async Task ProbeKinematicsAsync(CancellationToken ct)
        {
            try
            {
                string reply = await SendCodeAsync("M669", ct).ConfigureAwait(false);
                if (!TryParseM669(reply, out var parsed))
                {
                    Kinematics = null;
                    KinematicsProbed = false;
                    KinematicsProbeDetail = reply == null || reply.Trim().Length == 0
                        ? "the M669 query returned an empty reply"
                        : "could not parse the M669 reply: " + reply.Trim();
                    return;
                }
                Kinematics = parsed;
                KinematicsProbed = true;
                KinematicsProbeDetail = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Kinematics = null;
                KinematicsProbed = false;
                KinematicsProbeDetail = "M669 query failed: " + ex.Message;
            }
        }

        public async Task ProbeHomeMacrosAsync(CancellationToken ct)
        {
            var files = await ListFilesAsync("0:/sys", ct).ConfigureAwait(false);
            _homeMacros.Clear();
            bool Has(params string[] names)
            {
                foreach (var f in files)
                    foreach (var name in names)
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            _homeMacros["all"] = Has("homeall.g");
            _homeMacros["X"] = Has("homeproximal.g", "homex.g");
            _homeMacros["Y"] = Has("homedistal.g", "homey.g");
            _homeMacros["Z"] = Has("homez.g");
            HomeProbeComplete = true;
        }

        async Task ConnectLockedAsync(CancellationToken ct)
        {
            string pwd = string.IsNullOrEmpty(_opt.Password) ? "reprap" : _opt.Password;
            string uri = $"rr_connect?password={Uri.EscapeDataString(pwd)}"
                       + $"&time={DateTime.Now.ToString("s", CultureInfo.InvariantCulture)}";
            string body = await GetStringRawAsync(uri, _opt.RequestTimeout, ct, includeSessionKey: false).ConfigureAwait(false);
            var json = JsonValue.Parse(body);
            int err = json.Int("err", -1);
            switch (err)
            {
                case 0: break;
                case 1: throw new DuetException("wrong password (rr_connect err 1)");
                case 2:
                    throw new DuetException(
                    "no free session on the Duet (rr_connect err 2) - close open Duet Web Control tabs and retry");
                default: throw new DuetException($"rr_connect returned unknown err {err}");
            }
            _sessionKey = json.At("sessionKey").IsNull ? -1 : (long)json.Num("sessionKey");
            ApiLevel = json.Int("apiLevel", 0);
            BoardType = json.At("boardType").IsNull ? null : json.Str("boardType");
            _connected = true;
            Interlocked.Increment(ref _connectionGeneration);
        }

        public async Task DisconnectAsync(CancellationToken ct)
        {
            try { await GetStringRawAsync("rr_disconnect", _opt.RequestTimeout, ct, includeSessionKey: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* best effort - the board drops idle sessions on its own */ }
            finally { _connected = false; _sessionKey = -1; }
        }

        // ---- commands ------------------------------------------------------

        /// <summary>Sends one G-code line and returns the firmware reply text
        /// (empty when the code produced no reply within the two-second probe
        /// budget). All rr_reply consumption goes through the shared drain.</summary>
        public async Task<string> SendCodeAsync(string gcode, CancellationToken ct)
        {
            string? reply = await SendCodeAndWaitForReplyAsync(gcode, TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(false);
            return reply ?? "";
        }

        /// <summary>Sends a command and waits for its next firmware reply through
        /// the single rr_reply drain. The poll loop owns draining while it runs;
        /// connect-time probes drive the same drain directly.</summary>
        public async Task<string?> SendCodeAndWaitForReplyAsync(string gcode, TimeSpan timeout,
                                                                 CancellationToken ct)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            int seqBefore = await GetReplySeqAsync(ct).ConfigureAwait(false);
            var waiter = SubscribeReply();
            try
            {
                await SendCodeNoReplyAsync(gcode, ct).ConfigureAwait(false);
                if (_replyPolling)
                    return await WaitForReplyAsync(waiter, timeout, ct).ConfigureAwait(false);
                return await DriveReplyDirectlyAsync(waiter, seqBefore, timeout, ct).ConfigureAwait(false);
            }
            finally
            {
                UnsubscribeReply(waiter);
            }
        }

        /// <summary>Fire-and-forget send (no rr_reply chase). Used by the poll-driven
        /// job actions where the object model, not the reply, is the truth.</summary>
        public async Task SendCodeNoReplyAsync(string gcode, CancellationToken ct)
        {
            string uri = "rr_gcode?gcode=" + Uri.EscapeDataString(gcode);
            string body = await SendAsync(HttpMethod.Get, uri, null, _opt.RequestTimeout, ct, idempotent: false).ConfigureAwait(false);
            EnsureGcodeAccepted(JsonValue.Parse(body), gcode);
        }

        static void EnsureGcodeAccepted(JsonValue json, string gcode)
        {
            // RRF versions differ: rr_gcode may acknowledge a command with an
            // err=0 field or with the remaining buffer space (buff).  Both are
            // successful protocol responses; accepting only err made valid
            // emergency-stop and fire-and-forget responses look like failures.
            if (json["err"].Kind == JsonKind.Number)
            {
                int err = json.Int("err");
                if (err != 0) throw new DuetException($"rr_gcode returned err {err} for '{gcode}'");
                return;
            }
            if (json["buff"].Kind == JsonKind.Number) return;
            throw new DuetException($"rr_gcode returned neither numeric err nor buff for '{gcode}'");
        }

        /// <summary>M112. Deliberately ignores the caller's token: an emergency stop
        /// must fire even when the session's lifetime token is already cancelled.
        /// Never routes through <see cref="SendAsync"/> - no 401 re-auth detour, no
        /// 503 backoff, no reply drain; every request carries its own hard timeout,
        /// so the whole method is bounded (~5 s worst case) before it throws and the
        /// caller falls back to the physical switch.</summary>
        public async Task EmergencyStopAsync(CancellationToken ct)
        {
            // Broad by design (see class doc comment above): EstopSendOnceAsync times
            // itself out on an internal CTS unrelated to `ct`, so any exception here -
            // including an OperationCanceledException from that internal timeout - means
            // "this attempt did not get through", never a caller cancellation to honor.
            // two immediate single-shot attempts on the current session (a 503
            // buffer-full during a fast print resolves within one planner tick)
            try { await EstopSendOnceAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false); return; }
            catch (Exception) { /* dead session, timeout or buffer full - retry immediately */ }
            try { await EstopSendOnceAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false); return; }
            catch (Exception) { /* session is likely gone - reconnect fresh below */ }

            using (var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                await _authLock.WaitAsync(authCts.Token).ConfigureAwait(false);
                try { await ConnectLockedAsync(authCts.Token).ConfigureAwait(false); }
                finally { _authLock.Release(); }
            }
            await EstopSendOnceAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        /// <summary>One M112 request: session header, own timeout, no recovery.</summary>
        async Task EstopSendOnceAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var msg = new HttpRequestMessage(HttpMethod.Get, "rr_gcode?gcode=M112");
            long key = Interlocked.Read(ref _sessionKey);
            if (key >= 0) msg.Headers.Add("X-Session-Key", key.ToString(CultureInfo.InvariantCulture));
            using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await ReadTextCappedAsync(resp.Content, cts.Token).ConfigureAwait(false);
            EnsureGcodeAccepted(JsonValue.Parse(body), "M112");
        }

        /// <summary>M999 - firmware reset. The session dies with it; callers should
        /// expect a reconnect afterwards.
        ///
        /// The old catch-all swallowed every <see cref="HttpRequestException"/> and
        /// returned as if the reset had happened. "The board resets before answering" and
        /// "the endpoint is gone" produce the same exception, so that reported success for
        /// a delivery nobody had observed. Both are now UNCERTAIN, and only a later
        /// observation - the machine live again on a new connection generation - counts as
        /// recovery. A refusal the board actually answered (a 401 after the single
        /// re-auth, any other error status, an explicit err) still throws.</summary>
        public async Task<ResetDelivery> ResetAsync(CancellationToken ct)
        {
            string body;
            try
            {
                body = await SendAsync(HttpMethod.Get, "rr_gcode?gcode=M999", null,
                                       _opt.RequestTimeout, ct, idempotent: false).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return ResetDelivery.Unconfirmed("the connection dropped before an answer arrived (" + ex.Message + ")");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ResetDelivery.Unconfirmed("the request timed out before an answer arrived");
            }
            // A DuetException that carries no HTTP status came from a timeout or a
            // transport-shaped failure: the board may well have taken the command.
            // One that carries a status was answered and refused - that is not uncertain.
            catch (DuetException ex) when (ex.StatusCode == null)
            {
                return ResetDelivery.Unconfirmed(ex.Message);
            }
            finally { _connected = false; }

            JsonValue json;
            try { json = JsonValue.Parse(body); }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentOutOfRangeException)
            { return ResetDelivery.Unconfirmed("the board's answer to M999 was not readable"); }
            if (json["err"].Kind == JsonKind.Number)
            {
                int err = json.Int("err");
                if (err != 0) throw new DuetException($"rr_gcode returned err {err} for 'M999'");
                return ResetDelivery.Confirmed("the board acknowledged M999");
            }
            if (json["buff"].Kind == JsonKind.Number) return ResetDelivery.Confirmed("the board acknowledged M999");
            return ResetDelivery.Unconfirmed("the board's answer to M999 did not acknowledge it");
        }

        // ---- object model ---------------------------------------------------

        /// <summary>Timing of one model read, so the coordinate request can be dated by
        /// its own round trip rather than by the end of the surrounding poll.</summary>
        readonly struct ModelRead
        {
            public readonly JsonValue Value;
            public readonly DateTime RequestUtc, ReceivedUtc;
            public readonly int Pages;
            public ModelRead(JsonValue value, DateTime requestUtc, DateTime receivedUtc, int pages)
            { Value = value; RequestUtc = requestUtc; ReceivedUtc = receivedUtc; Pages = pages; }
        }

        /// <summary>rr_model for one key. Follows the "next" continuation Duet 2
        /// uses for long arrays, returning the stitched array.</summary>
        public async Task<JsonValue> GetModelAsync(string key, string flags, CancellationToken ct)
            => (await GetModelTimedAsync(key, flags, ct).ConfigureAwait(false)).Value;

        async Task<ModelRead> GetModelTimedAsync(string key, string flags, CancellationToken ct)
        {
            DateTime started = DateTime.UtcNow;
            var first = await GetModelPageAsync(key, flags, ct, started, 1).ConfigureAwait(false);
            DateTime received = DateTime.UtcNow;
            var result = first.At("result");
            int next = first.Int("next", 0);
            if (next <= 0 || result.Kind != JsonKind.Array)
                return new ModelRead(result, started, received, 1);

            var items = new System.Collections.Generic.List<JsonValue>();
            for (int i = 0; i < result.Count; i++) items.Add(result[i]);
            int guard = 0;
            int pages = 1;
            while (next > 0 && guard++ < 64)
            {
                DateTime pageStart = DateTime.UtcNow;
                var page = await GetModelPageAsync(key, flags + "a" + next.ToString(CultureInfo.InvariantCulture),
                                                   ct, pageStart, pages + 1).ConfigureAwait(false);
                received = DateTime.UtcNow;
                pages++;
                var arr = page.At("result");
                for (int i = 0; i < arr.Count; i++) items.Add(arr[i]);
                next = page.Int("next", 0);
            }
            if (next > 0)
                throw new DuetException($"rr_model pagination exceeded the continuation limit for key '{key}'");
            return new ModelRead(JsonValue.OfArray(items), started, received, pages);
        }

        async Task<JsonValue> GetModelPageAsync(string key, string flags, CancellationToken ct,
                                                DateTime startedUtc = default(DateTime), int page = 1)
        {
            string uri = string.IsNullOrEmpty(key)
                ? $"rr_model?flags={flags}"
                : $"rr_model?key={Uri.EscapeDataString(key)}&flags={flags}";
            if (startedUtc == default(DateTime)) startedUtc = DateTime.UtcNow;
            string body = await GetStringAsync(uri, _opt.RequestTimeout, ct).ConfigureAwait(false);
            // Diagnostic seam: the raw body, before parsing, with the request's own timing.
            // Nothing is retained here - the subscriber owns the bound.
            PublishRawBody(uri, "rr_model", body, startedUtc, page);
            var json = JsonValue.Parse(body);
            if (json.At("result").IsNull && json.At("err").IsNull)
                throw new DuetException($"rr_model returned no result for key '{key}'");
            if (json.Int("err", 0) != 0)
                throw new DuetException($"rr_model returned err {json.Int("err")} for key '{key}'");
            return json;
        }

        /// <summary>Raised with the untouched response body of a status/coordinate request,
        /// before parsing. Subscribed by the session's bounded diagnostic window; nothing
        /// is raised while nobody listens, so an unsubscribed client pays nothing.</summary>
        public event Action<RawBodyProbe>? RawBodyObserved;

        void PublishRawBody(string endpoint, string apiMode, string body, DateTime startedUtc, int page)
        {
            var handler = RawBodyObserved;
            if (handler == null) return;
            try
            {
                handler(new RawBodyProbe(endpoint, apiMode, body, startedUtc, DateTime.UtcNow,
                                         ConnectionGeneration, page));
            }
            catch (Exception) { /* diagnostics must never break a poll */ }
        }

        /// <summary>One coherent status poll. RRF 3.2+: 2–3 small rr_model queries;
        /// pre-3.2 (Legacy): one rr_status?type=3 (+ rr_fileinfo for the job name).</summary>
        public async Task<ObjectModelSnapshot> GetSnapshotAsync(bool includeJob, CancellationToken ct)
        {
            if (Legacy) return await GetLegacySnapshotAsync(includeJob, ct).ConfigureAwait(false);
            // The acquisition id is taken BEFORE the requests, so ids order acquisitions by
            // when they were asked for. Stamping after the await would order them by
            // completion, which is exactly the ordering the publication gate must not use.
            long acquisitionId = AcquisitionCounter.Next();
            var state = await GetModelAsync("state", "vn", ct).ConfigureAwait(false);
            var axesRead = await GetModelTimedAsync("move.axes", "vnd4", ct).ConfigureAwait(false);
            var axes = axesRead.Value;
            JsonValue? job = null;
            JsonValue? moveCurrent = null;
            JsonValue? moveSpeedFactor = null;
            JsonValue? moveExtruders = null;
            if (includeJob)
            {
                job = await GetModelAsync("job", "vnd3", ct).ConfigureAwait(false);
                // This key is optional across RRF3 builds. A board that does not
                // expose it must still provide a usable status poll.
                try { moveCurrent = await GetModelAsync("move.currentMove", "vn", ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { moveCurrent = null; }
            }
            // M220/M221 are retained LDM telemetry and must not disappear between the
            // less-frequent job-detail polls. Both keys are optional across RRF3 builds.
            try { moveSpeedFactor = await GetModelAsync("move.speedFactor", "vn", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { moveSpeedFactor = null; }
            try { moveExtruders = await GetModelAsync("move.extruders", "vnd2", ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { moveExtruders = null; }
            var stamp = new AcquisitionStamp
            {
                AcquisitionId = acquisitionId,
                RequestStartedUtc = axesRead.RequestUtc,
                ResponseReceivedUtc = axesRead.ReceivedUtc,
                ConnectionGeneration = ConnectionGeneration,
                // state, move.axes and job are sequential requests on this path, so the
                // parts of the observation do NOT share one instant.
                Composite = true,
                CoordinatePages = axesRead.Pages,
            };
            return ObjectModelParser.FromRrModel(state, axes, job, moveCurrent, moveSpeedFactor, stamp, moveExtruders);
        }

        async Task<ObjectModelSnapshot> GetLegacySnapshotAsync(bool includeJob, CancellationToken ct)
        {
            long acquisitionId = AcquisitionCounter.Next();
            DateTime requestUtc = DateTime.UtcNow;
            string body = await GetStringAsync("rr_status?type=3", _opt.RequestTimeout, ct).ConfigureAwait(false);
            DateTime receivedUtc = DateTime.UtcNow;
            PublishRawBody("rr_status?type=3", "rr_status", body, requestUtc, 1);
            var json = JsonValue.Parse(body);
            if (!json.At("seq").IsNull) _legacyReplySeq = json.Int("seq");

            string? fileName = null;
            string letter = json.Str("status", "");
            bool jobActive = letter == "P" || letter == "M" || letter == "S" || letter == "D" || letter == "R";
            if (includeJob && jobActive)
            {
                try
                {
                    // rr_fileinfo without a name = info on the file being printed
                    string fi = await GetStringAsync("rr_fileinfo", _opt.RequestTimeout, ct).ConfigureAwait(false);
                    var info = JsonValue.Parse(fi);
                    if (info.Int("err", -1) == 0) fileName = info.Str("fileName", "");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* job name is cosmetic - never fail the poll over it */ }
            }
            // rr_status is ONE response: status, coordinates and job progress share an
            // instant, so this observation is not composite. The rr_fileinfo call above is
            // cosmetic metadata and deliberately does not move the coordinate timestamps.
            var snap = ObjectModelParser.FromRrStatus(json, fileName, new AcquisitionStamp
            {
                AcquisitionId = acquisitionId,
                RequestStartedUtc = requestUtc,
                ResponseReceivedUtc = receivedUtc,
                ConnectionGeneration = ConnectionGeneration,
                Composite = false,
                CoordinatePages = 1,
            });
            if (_legacyM208.Count > 0)
            {
                foreach (var axis in snap.Axes)
                    if (_legacyM208.TryGetValue(axis.Letter, out var lim)) { axis.Min = lim.min; axis.Max = lim.max; }
            }
            return snap;
        }

        int _lastReplySeq = -1;
        volatile string? _lastDrainedReply;

        /// <summary>Marks the transition from connect-time probes to the session
        /// poll loop. Establishing the sequence baseline here prevents the first
        /// poll from discarding a reply to a command issued immediately after
        /// connect.</summary>
        public async Task BeginReplyPollingAsync(CancellationToken ct)
        {
            _lastReplySeq = await GetReplySeqAsync(ct).ConfigureAwait(false);
            _replyPolling = true;
        }

        public void EndReplyPolling() => _replyPolling = false;

        /// <summary>Firmware reply text swallowed by the 503 buffer-full drain (if
        /// any) - read-and-clear so the poll loop can log it exactly once.</summary>
        public string? TakeDrainedReply()
        {
            string? r = _lastDrainedReply;
            if (r != null) _lastDrainedReply = null;
            return r;
        }

        /// <summary>Reply drain for the poll loop: when the reply sequence advanced
        /// since the last call, fetches and returns the accumulated rr_reply text
        /// (null when nothing new). Keeping ALL rr_reply consumption in one caller
        /// avoids the fetch-race two consumers would have (rr_reply clears on read).
        /// Legacy boards piggyback the sequence on the last rr_status snapshot -
        /// call this AFTER GetSnapshotAsync there.</summary>
        public async Task<string?> FetchNewReplyAsync(CancellationToken ct)
        {
            int seq = Legacy ? _legacyReplySeq : await GetReplySeqAsync(ct).ConfigureAwait(false);
            if (Legacy && seq < 0) return null;                            // no snapshot yet
            if (_lastReplySeq < 0) { _lastReplySeq = seq; return null; }   // skip pre-session backlog
            if (seq == _lastReplySeq) return null;
            _lastReplySeq = seq;
            return await DrainReplyAsync(rememberIfUnclaimed: false, ct).ConfigureAwait(false);
        }

        ReplyWaiter SubscribeReply()
        {
            var waiter = new ReplyWaiter();
            lock (_replyWaiterLock) _replyWaiters.Add(waiter);
            return waiter;
        }

        void UnsubscribeReply(ReplyWaiter waiter)
        {
            lock (_replyWaiterLock) _replyWaiters.Remove(waiter);
        }

        async Task<string?> WaitForReplyAsync(ReplyWaiter waiter, TimeSpan timeout, CancellationToken ct)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(timeout);
            Task delay = Task.Delay(Timeout.Infinite, waitCts.Token);
            Task completed = await Task.WhenAny(waiter.Completion.Task, delay).ConfigureAwait(false);
            if (completed == waiter.Completion.Task)
                return await waiter.Completion.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return null;
        }

        async Task<string?> DriveReplyDirectlyAsync(ReplyWaiter waiter, int seqBefore,
                                                     TimeSpan timeout, CancellationToken ct)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(timeout);
            try
            {
                while (!waiter.Completion.Task.IsCompleted)
                {
                    int seq = await GetReplySeqAsync(waitCts.Token).ConfigureAwait(false);
                    if (seq != seqBefore)
                    {
                        seqBefore = seq;
                        await DrainReplyAsync(rememberIfUnclaimed: false, waitCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }
                    await Task.Delay(150, waitCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            return await waiter.Completion.Task.ConfigureAwait(false);
        }

        /// <summary>The only method that performs an rr_reply GET. It serializes
        /// the read-and-clear operation and gives the text to a waiting command;
        /// otherwise the caller (poll or 503 recovery) receives it.</summary>
        async Task<string?> DrainReplyAsync(bool rememberIfUnclaimed, CancellationToken ct)
        {
            await _replyDrainLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string reply = (await GetStringRawAsync("rr_reply", _opt.RequestTimeout, ct,
                    includeSessionKey: true).ConfigureAwait(false)).Trim();
                if (reply.Length == 0) return null;

                ReplyWaiter? waiter = null;
                lock (_replyWaiterLock)
                {
                    if (_replyWaiters.Count > 0)
                    {
                        waiter = _replyWaiters[0];
                        _replyWaiters.RemoveAt(0);
                    }
                }
                if (waiter != null) waiter.Completion.TrySetResult(reply);
                else if (rememberIfUnclaimed) _lastDrainedReply = reply;
                return reply;
            }
            finally { _replyDrainLock.Release(); }
        }

        /// <summary>Raw rr_model body - debug/fixture capture, no parsing.</summary>
        public Task<string> GetModelRawAsync(string key, string flags, CancellationToken ct)
        {
            string uri = string.IsNullOrEmpty(key)
                ? $"rr_model?flags={flags}"
                : $"rr_model?key={Uri.EscapeDataString(key)}&flags={flags}";
            return GetStringAsync(uri, _opt.RequestTimeout, ct);
        }

        async Task<int> GetReplySeqAsync(CancellationToken ct)
        {
            if (Legacy)
            {
                string body = await GetStringAsync("rr_status?type=1", _opt.RequestTimeout, ct).ConfigureAwait(false);
                return JsonValue.Parse(body).Int("seq");
            }
            var v = await GetModelAsync("seqs.reply", "vn", ct).ConfigureAwait(false);
            return v.AsInt(0);
        }

        // ---- files / job ----------------------------------------------------

        /// <summary>CRC-verified upload; confirms the stored size via rr_fileinfo.</summary>
        public async Task<UploadVerification> UploadAsync(string remotePath, byte[] content, CancellationToken ct)
        {
            if (content == null || content.Length == 0)
                throw new DuetException("refusing to upload an empty file");
            using (var stream = new MemoryStream(content, writable: false))
                return await UploadAsync(remotePath, stream, content.Length, ct).ConfigureAwait(false);
        }

        public async Task<UploadVerification> UploadAsync(string remotePath, Stream content, long length,
                                                           CancellationToken ct)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (length <= 0) throw new DuetException("refusing to upload an empty file");
            string crc = "";
            if (content.CanSeek)
            {
                long origin = content.Position;
                var checksum = new Crc32.Incremental();
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0) checksum.Append(buffer, 0, read);
                content.Position = origin;
                if (checksum.Length != length)
                    throw new DuetException($"upload stream length changed: read {checksum.Length} bytes, expected {length}");
                crc = checksum.Hex;
            }
            string uri = $"rr_upload?name={Uri.EscapeDataString(remotePath)}"
                       + $"&time={DateTime.Now.ToString("s", CultureInfo.InvariantCulture)}"
                       + (crc.Length > 0 ? $"&crc32={crc}" : "");
            CrcReadStream? sent = null;
            string body = await SendAsync(HttpMethod.Post, uri,
                () => { sent = new CrcReadStream(content, length); return new StreamContent(sent); },
                _opt.UploadTimeout, ct, idempotent: false).ConfigureAwait(false);
            if (sent != null && sent.Checksum.Length != length)
                throw new DuetException($"upload stream ended at {sent.Checksum.Length} bytes, expected {length}");
            int uploadErr = JsonValue.Parse(body).Int("err", -1);
            if (uploadErr != 0)
                throw new DuetException($"rr_upload failed (err {uploadErr}) - " +
                                        "CRC mismatch or SD write error; retry the upload");

            long stored = await GetFileSizeAsync(remotePath, ct).ConfigureAwait(false);
            if (stored != length)
                throw new DuetException($"upload verification failed: board reports {stored} bytes, " +
                                        $"expected {length}");
            return new UploadVerification { Size = stored, CrcConfirmed = !Legacy && crc.Length > 0, Crc32 = crc };
        }

        public async Task VerifyRemoteContentAsync(string remotePath, byte[] expected, CancellationToken ct)
        {
            if (expected == null || expected.Length == 0)
                throw new DuetException("refusing to verify an empty job");
            long size = await GetFileSizeAsync(remotePath, ct).ConfigureAwait(false);
            if (size != expected.Length)
                throw new DuetException($"remote identity mismatch for '{remotePath}': size {size}, expected {expected.Length}");
            var actual = await DownloadToAsync(remotePath, Stream.Null, ct).ConfigureAwait(false);
            byte[] expectedHash = SHA256.Create().ComputeHash(expected);
            if (actual.Size != expected.Length || !string.Equals(actual.Crc32,
                Crc32.HexLower(Crc32.Compute(expected)), StringComparison.OrdinalIgnoreCase))
                throw new DuetException($"remote identity mismatch for '{remotePath}': downloaded {actual.Size}, expected {expected.Length}");
            string expectedSha = BitConverter.ToString(expectedHash).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(expectedSha, actual.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new DuetException($"remote identity mismatch for '{remotePath}': content changed");
        }

        public async Task<long> GetFileSizeAsync(string remotePath, CancellationToken ct)
        {
            string uri = "rr_fileinfo?name=" + Uri.EscapeDataString(remotePath);
            string body = await GetStringAsync(uri, _opt.RequestTimeout, ct).ConfigureAwait(false);
            var json = JsonValue.Parse(body);
            if (json.Int("err", -1) != 0)
                throw new DuetException($"rr_fileinfo err {json.Int("err", -1)} for '{remotePath}' (file missing?)");
            var size = json["size"];
            if (size.Kind != JsonKind.Number || size.AsNumber() < 0)
                throw new DuetException($"rr_fileinfo omitted a valid size for '{remotePath}'");
            return (long)size.AsNumber();
        }

        /// <summary>Directory listing via rr_filelist, following the "next"
        /// continuation the firmware uses for long directories.</summary>
        public async Task<System.Collections.Generic.List<DuetFileEntry>> ListFilesAsync(
            string dir, CancellationToken ct)
        {
            var outp = new System.Collections.Generic.List<DuetFileEntry>();
            int first = 0, guard = 0, next = 0;
            while (guard++ < 64)
            {
                string uri = $"rr_filelist?dir={Uri.EscapeDataString(dir)}"
                           + $"&first={first.ToString(CultureInfo.InvariantCulture)}";
                string body = await GetStringAsync(uri, _opt.RequestTimeout, ct).ConfigureAwait(false);
                var json = JsonValue.Parse(body);
                // RRF 3.1.x (verified on the 3DPotter Edition Rev10 build) omits "err"
                // entirely on success - only failures carry it. Accept a missing err
                // when the files array is present; anything else stays fail-closed.
                int err;
                if (json["err"].Kind == JsonKind.Number)
                    err = json.Int("err");
                else if (json.At("files").Kind == JsonKind.Array)
                    err = 0;
                else
                    throw new DuetException($"rr_filelist returned neither err nor files for '{dir}'");
                if (err != 0)
                    throw new DuetException(err == 2
                        ? $"directory not found on the board: {dir}"
                        : $"rr_filelist err {err} for '{dir}'");
                var files = json.At("files");
                if (files.Kind != JsonKind.Array)
                    throw new DuetException($"rr_filelist omitted the files array for '{dir}'");
                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    string name = f.Str("name", "");
                    string type = f.Str("type", "");
                    if (f.Kind != JsonKind.Object || name.Length == 0 || (type != "f" && type != "d"))
                        throw new DuetException($"rr_filelist returned an invalid entry at index {i} for '{dir}'");
                    if (outp.Count >= MaxListEntries)
                        throw new DuetException($"rr_filelist exceeded the {MaxListEntries} entry limit for '{dir}'");
                    outp.Add(new DuetFileEntry
                    {
                        Name = name,
                        Size = (long)f.Num("size"),
                        IsDirectory = type == "d",
                    });
                }
                next = json.Int("next", 0);
                if (next <= 0) break;
                first = next;
            }
            if (guard >= 64 && next > 0)
                throw new DuetException($"rr_filelist pagination exceeded the continuation limit for '{dir}'");
            return outp;
        }

        /// <summary>Raw file download (rr_download). Callers should compare the
        /// byte count against the listed size before trusting the content.</summary>
        public async Task<byte[]> DownloadAsync(string remotePath, CancellationToken ct)
        {
            using (var output = new MemoryStream())
            {
                await DownloadToAsync(remotePath, output, ct).ConfigureAwait(false);
                return output.ToArray();
            }
        }

        public async Task<TransferVerification> DownloadToAsync(string remotePath, Stream destination,
                                                                 CancellationToken ct)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            string uri = "rr_download?name=" + Uri.EscapeDataString(remotePath);
            for (int attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_opt.UploadTimeout);   // symmetric with uploads - files can be large
                using var msg = new HttpRequestMessage(HttpMethod.Get, uri);
                long key = Interlocked.Read(ref _sessionKey);
                if (key >= 0) msg.Headers.Add("X-Session-Key", key.ToString(CultureInfo.InvariantCulture));
                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new DuetException($"download timed out for {remotePath}");
                }
                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        await ReauthenticateIfCurrentAsync(key, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        throw new DuetException($"file not found on the board: {remotePath}");
                    if (!resp.IsSuccessStatusCode)
                        throw new DuetException($"HTTP {(int)resp.StatusCode} downloading {remotePath}");
                    // every body read and destination write runs under the linked
                    // transfer-timeout token, so a stalled body cannot hold the command
                    // lock forever (AUD-SAF-002)
                    try
                    {
#if NETFRAMEWORK
                        using (var source = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
#else
                        using (var source = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
#endif
                        using (var sha = SHA256.Create())
                        {
                            var crc = new Crc32.Incremental();
                            var buffer = new byte[64 * 1024];
                            long total = 0;
                            int read;
                            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false)) > 0)
                            {
                                await destination.WriteAsync(buffer, 0, read, cts.Token).ConfigureAwait(false);
                                crc.Append(buffer, 0, read);
                                sha.TransformBlock(buffer, 0, read, buffer, 0);
                                total += read;
                            }
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            return new TransferVerification
                            {
                                Size = total,
                                Crc32 = crc.Hex,
                                Sha256 = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant(),
                            };
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new DuetException($"download timed out for {remotePath}");
                    }
                }
            }
        }

        sealed class CrcReadStream : Stream
        {
            readonly Stream _inner;
            readonly long _length;
            long _read;
            public readonly Crc32.Incremental Checksum = new Crc32.Incremental();
            public CrcReadStream(Stream inner, long length) { _inner = inner; _length = length; }
            public override bool CanRead => true;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count)
            {
                int allowed = (int)Math.Min(count, _length - _read);
                if (allowed <= 0) return 0;
                int n = _inner.Read(buffer, offset, allowed);
                if (n > 0) { Checksum.Append(buffer, offset, n); _read += n; }
                return n;
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                int allowed = (int)Math.Min(count, _length - _read);
                if (allowed <= 0) return 0;
                int n = await _inner.ReadAsync(buffer, offset, allowed, ct).ConfigureAwait(false);
                if (n > 0) { Checksum.Append(buffer, offset, n); _read += n; }
                return n;
            }
            protected override void Dispose(bool disposing) { /* caller owns the source */ base.Dispose(disposing); }
        }

        public async Task DeleteFileAsync(string remotePath, CancellationToken ct,
                                          bool maintenanceOverride = false)
        {
            if (!maintenanceOverride && DuetFileEntry.IsProtected(remotePath))
                throw new DuetException($"refusing to delete protected board path '{remotePath}'");
            string body = await GetStringAsync("rr_delete?name=" + Uri.EscapeDataString(remotePath),
                _opt.RequestTimeout, ct, idempotent: false).ConfigureAwait(false);
            int err = JsonValue.Parse(body).Int("err", 0);
            if (err != 0)
                throw new DuetException($"delete failed for '{remotePath}' (err {err}) - " +
                                        "file missing, open, or write-protected");
        }

        /// <summary>rr_move. Never overwrites: an existing target makes the firmware
        /// return an error instead of silently replacing a job file.</summary>
        public async Task MoveFileAsync(string oldPath, string newPath, CancellationToken ct,
                                        bool maintenanceOverride = false)
        {
            if (!maintenanceOverride && (DuetFileEntry.IsProtected(oldPath) || DuetFileEntry.IsProtected(newPath)))
                throw new DuetException($"refusing to move protected board path '{oldPath}' or '{newPath}'");
            string uri = $"rr_move?old={Uri.EscapeDataString(oldPath)}&new={Uri.EscapeDataString(newPath)}";
            string body = await GetStringAsync(uri, _opt.RequestTimeout, ct, idempotent: false).ConfigureAwait(false);
            int err = JsonValue.Parse(body).Int("err", 0);
            if (err != 0)
                throw new DuetException($"move failed '{oldPath}' → '{newPath}' (err {err}) - " +
                                        "target exists, source missing, or directory absent");
        }

        public async Task StartPrintAsync(string remotePath, CancellationToken ct)
        {
            await SendCodeNoReplyAsync($"M32 \"{remotePath}\"", ct).ConfigureAwait(false);
            await ConfirmJobStartedAsync(remotePath, MachineStatus.Processing, ct).ConfigureAwait(false);
        }

        /// <summary>M37 - firmware dry-run: full motion planning, no movement.</summary>
        public async Task SimulatePrintAsync(string remotePath, CancellationToken ct)
        {
            await SendCodeNoReplyAsync($"M37 P\"{remotePath}\"", ct).ConfigureAwait(false);
            await ConfirmJobStartedAsync(remotePath, MachineStatus.Simulating, ct).ConfigureAwait(false);
        }

        async Task ConfirmJobStartedAsync(string remotePath, MachineStatus expected, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                var snap = await GetSnapshotAsync(includeJob: true, ct).ConfigureAwait(false);
                if (snap.Status == expected)
                {
                    string? actual = snap.Job?.FileName;
                    if (string.IsNullOrEmpty(actual) || (actual != null && string.Equals(actual.Trim(), remotePath, StringComparison.OrdinalIgnoreCase))) return;
                    throw new DuetException($"job start confirmation named '{actual}', expected '{remotePath}'");
                }
                // Idle is NOT a failure here: M32/M37 are asynchronous and the machine
                // commonly reports Idle for a few polls before Processing/Simulating -
                // keep polling until the deadline. Only Halted/Off are terminal.
                if (snap.Status == MachineStatus.Halted || snap.Status == MachineStatus.Off)
                    throw new DuetException($"job command was accepted but firmware went {snap.RawStatus}; start outcome is not confirmed");
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            throw new DuetException($"job start outcome is not confirmed for '{remotePath}'");
        }

        public async Task PauseAsync(CancellationToken ct)
        {
            OpaqueMotionIssued?.Invoke();
            await SendCodeNoReplyAsync("M25", ct).ConfigureAwait(false);
        }

        public async Task ResumeAsync(CancellationToken ct)
        {
            OpaqueMotionIssued?.Invoke();
            await SendCodeNoReplyAsync("M24", ct).ConfigureAwait(false);
        }

        /// <summary>Cancels the current job: pause first when moving, then M0.</summary>
        public async Task CancelAsync(CancellationToken ct)
        {
            OpaqueMotionIssued?.Invoke();
            var snap = await GetSnapshotAsync(false, ct).ConfigureAwait(false);
            if (snap.Status == MachineStatus.Processing || snap.Status == MachineStatus.Simulating
                || snap.Status == MachineStatus.Resuming)
            {
                await SendCodeNoReplyAsync("M25", ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            await SendCodeNoReplyAsync("M0", ct).ConfigureAwait(false);
        }

        // ---- plumbing --------------------------------------------------------

        /// <summary>GET with session header, auth-loss recovery and 503 backoff.</summary>
        Task<string> GetStringAsync(string uri, TimeSpan timeout, CancellationToken ct, bool idempotent = true)
            => SendAsync(HttpMethod.Get, uri, null, timeout, ct, idempotent);

        /// <summary>Raw GET without recovery - used by rr_connect itself.</summary>
        async Task<string> GetStringRawAsync(string uri, TimeSpan timeout, CancellationToken ct, bool includeSessionKey = true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (includeSessionKey)
            {
                long key = Interlocked.Read(ref _sessionKey);
                if (key >= 0) req.Headers.Add("X-Session-Key", key.ToString(CultureInfo.InvariantCulture));
            }
            // headers-first + streamed capped read: an oversized body aborts at the cap
            // instead of being buffered whole (AUD-SAF-001); the linked token keeps the
            // body read inside the same timeout budget as the request
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await ReadTextCappedAsync(resp.Content, cts.Token).ConfigureAwait(false);
        }

        async Task<string> SendAsync(HttpMethod method, string uri, Func<HttpContent>? contentFactory,
                                     TimeSpan timeout, CancellationToken ct, bool idempotent = true)
        {
            for (int attempt = 0; ; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                // HttpRequestMessage (and its content) is single-use - build per attempt
                using var msg = new HttpRequestMessage(method, uri);
                if (contentFactory != null) msg.Content = contentFactory();
                long key = Interlocked.Read(ref _sessionKey);
                if (key >= 0) msg.Headers.Add("X-Session-Key", key.ToString(CultureInfo.InvariantCulture));

                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new DuetException(FormattableString.Invariant($"request timed out after {timeout.TotalSeconds:0.#}s: {uri}"));
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        // session expired (board drops idle sessions ~8 s) - re-auth once
                        await ReauthenticateIfCurrentAsync(key, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && idempotent && attempt < _opt.MaxRetries)
                    {
                        // G-code buffer full - drain a pending reply, back off, retry.
                        // The drained text can be a firmware ERROR the poll loop's
                        // single-consumer path would now never see - keep it.
                        try
                        {
                            await DrainReplyAsync(rememberIfUnclaimed: true, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { /* best effort */ }
                        await Task.Delay(_opt.RetryBaseDelayMs * (1 << attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        try
                        {
                            await DrainReplyAsync(rememberIfUnclaimed: true, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        // best effort: about to throw for the 503 anyway; a failed drain
                        // here must not replace that diagnosis.
                        catch (Exception) { }
                        throw new DuetException($"HTTP 503 for {uri} - command outcome is ambiguous; it was not replayed, reconcile machine state before retrying", 503);
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        _connected = resp.StatusCode != HttpStatusCode.Unauthorized;
                        // Carry the status: a caller that has to tell "the endpoint
                        // answered and refused" from "nothing answered" must branch on the
                        // code, never on the message text (see ResetAsync).
                        throw new DuetException($"HTTP {(int)resp.StatusCode} for {uri}", (int)resp.StatusCode);
                    }
                    _connected = true;
                    try
                    {
                        return await ReadTextCappedAsync(resp.Content, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new DuetException(FormattableString.Invariant($"response body timed out after {timeout.TotalSeconds:0.#}s: {uri}"));
                    }
                }
            }
        }

        /// <summary>Streams the body under the caller's (timeout-linked) token and
        /// aborts as soon as the cap is exceeded - an oversized or stalling response
        /// can neither exhaust memory nor outlive the request budget (AUD-SAF-001).</summary>
        static async Task<string> ReadTextCappedAsync(HttpContent content, CancellationToken ct)
        {
            long? declared = content.Headers.ContentLength;
            if (declared.HasValue && declared.Value > MaxJsonBodyBytes)
                throw new DuetException($"Duet JSON response exceeds the {MaxJsonBodyBytes} byte limit");
#if NETFRAMEWORK
            using var src = await content.ReadAsStreamAsync().ConfigureAwait(false);
#else
            using var src = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
            using var ms = new MemoryStream(declared.HasValue ? (int)declared.Value : 16 * 1024);
            var buf = new byte[64 * 1024];
            int read;
            while ((read = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxJsonBodyBytes)
                    throw new DuetException($"Duet JSON response exceeds the {MaxJsonBodyBytes} byte limit");
                ms.Write(buf, 0, read);
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        async Task ReauthenticateIfCurrentAsync(long failedKey, CancellationToken ct)
        {
            await _authLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Interlocked.Read(ref _sessionKey) == failedKey)
                    await ConnectLockedAsync(ct).ConfigureAwait(false);
            }
            finally { _authLock.Release(); }
        }

        public void Dispose()
        {
            _http.Dispose();
            _authLock.Dispose();
            _replyDrainLock.Dispose();
        }
    }
}
