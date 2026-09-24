using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Lugworm.Core.Duet
{
    /// <summary>How a machine identity was established, strongest first (AUD-SAF-004).</summary>
    public enum MachineFingerprintKind
    {
        /// <summary>RRF object model boards[0].uniqueId - a physical board identity.</summary>
        UniqueId,
        /// <summary>The board identifier from a read-only M122 diagnostic reply.</summary>
        BoardDiagnostic,
        /// <summary>A hash over board type, kinematics and config.g - proves the
        /// CONFIGURATION is the expected one, not the physical board. The UI must say so.</summary>
        Configuration,
    }

    /// <summary>Where a session's identity check currently stands (AUD-SAF-004).</summary>
    public enum MachineTrust
    {
        /// <summary>No connection yet, or evaluation has not run.</summary>
        Unknown,
        /// <summary>Connected, but no fingerprint could be probed - fail closed.</summary>
        Unavailable,
        /// <summary>No expectation on record; this user-initiated connection
        /// establishes the baseline.</summary>
        FirstConnection,
        /// <summary>The probed fingerprint matches the persisted expectation.</summary>
        Trusted,
        /// <summary>The probed fingerprint differs from the expectation - a different
        /// machine (or configuration) answers at this address. Diagnostic-only until
        /// the operator explicitly trusts it.</summary>
        Mismatch,
    }

    /// <summary>One machine identity: kind + value, with a human-readable display.
    /// Serialized into the .gh document by the Connect component so a reopened
    /// definition knows which machine it expects (AUD-SAF-004).</summary>
    public sealed class MachineFingerprint
    {
        public MachineFingerprintKind Kind { get; }
        public string Value { get; }

        public MachineFingerprint(MachineFingerprintKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("fingerprint value is empty", nameof(value));
            Kind = kind;
            Value = value.Trim();
        }

        public string Display => Kind switch
        {
            MachineFingerprintKind.UniqueId => $"board id {Value}",
            MachineFingerprintKind.BoardDiagnostic => $"board id {Value} (M122)",
            _ => $"configuration fingerprint {Shorten(Value)} (proves the configuration, not the physical board)",
        };

        static string Shorten(string v) => v.Length <= 12 ? v : v.Substring(0, 12) + "…";

        /// <summary>Identity match: same kind, same value (ordinal). A UniqueId never
        /// matches a Configuration fingerprint - a downgraded probe is a mismatch,
        /// not a silent acceptance.</summary>
        public bool Matches(MachineFingerprint? other)
            => other != null && Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public string Serialize() => KindToken(Kind) + ":" + Value;

        public static MachineFingerprint? TryParse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string text = s!;   // net48's IsNullOrWhiteSpace carries no nullability annotation
            int i = text.IndexOf(':');
            if (i <= 0 || i >= text.Length - 1) return null;
            MachineFingerprintKind kind;
            switch (text.Substring(0, i))
            {
                case "uid": kind = MachineFingerprintKind.UniqueId; break;
                case "diag": kind = MachineFingerprintKind.BoardDiagnostic; break;
                case "cfg": kind = MachineFingerprintKind.Configuration; break;
                default: return null;
            }
            string value = text.Substring(i + 1).Trim();
            return value.Length == 0 ? null : new MachineFingerprint(kind, value);
        }

        static string KindToken(MachineFingerprintKind k) => k switch
        {
            MachineFingerprintKind.UniqueId => "uid",
            MachineFingerprintKind.BoardDiagnostic => "diag",
            _ => "cfg",
        };

        /// <summary>Fallback fingerprint from configuration evidence: board type,
        /// kinematics summary, config.g content and the probed axis limits. Stable
        /// across reconnects to the same configured board; different for a different
        /// config.g or board type.</summary>
        public static MachineFingerprint Configuration(string? boardType, string? kinematicsSummary,
                                                       byte[]? configBytes,
                                                       IEnumerable<KeyValuePair<string, (double min, double max)>>? axisLimits)
        {
            using var sha = SHA256.Create();
            void Feed(byte[] data)
            {
                sha.TransformBlock(data, 0, data.Length, data, 0);
            }
            void FeedText(string text)
            {
                Feed(Encoding.UTF8.GetBytes(text));
                Feed(new byte[] { 0 });   // field separator so "ab"+"c" != "a"+"bc"
            }
            FeedText(boardType ?? "");
            FeedText(kinematicsSummary ?? "");
            Feed(configBytes ?? Array.Empty<byte>());
            Feed(new byte[] { 0 });
            if (axisLimits != null)
            {
                var rows = new List<string>();
                foreach (var kv in axisLimits)
                    rows.Add(FormattableString.Invariant($"{kv.Key}:{kv.Value.min:R}:{kv.Value.max:R}"));
                rows.Sort(StringComparer.Ordinal);
                foreach (var row in rows) FeedText(row);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string hex = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
            return new MachineFingerprint(MachineFingerprintKind.Configuration, hex);
        }
    }

    /// <summary>The one trust rule, pure and testable (AUD-SAF-004): no fingerprint
    /// fails closed, no expectation baselines, anything else must match exactly.</summary>
    public static class MachineTrustEvaluator
    {
        /// <summary>Combines the fingerprints persisted by every Connect component
        /// sharing one host session. Equal expectations have one consensus; any
        /// disagreement is an explicit fail-closed conflict, never last-writer-wins.</summary>
        public static MachineFingerprint? Consensus(IEnumerable<MachineFingerprint> expectations,
                                                     out string? conflict)
        {
            if (expectations == null) throw new ArgumentNullException(nameof(expectations));
            var distinct = new List<MachineFingerprint>();
            foreach (MachineFingerprint expected in expectations)
            {
                if (expected == null) continue;
                bool seen = false;
                foreach (MachineFingerprint existing in distinct)
                    if (existing.Matches(expected)) { seen = true; break; }
                if (!seen) distinct.Add(expected);
            }

            if (distinct.Count <= 1)
            {
                conflict = null;
                return distinct.Count == 0 ? null : distinct[0];
            }

            conflict = "conflicting persisted machine identities share this host (" +
                       string.Join(" versus ", distinct.ConvertAll(f => f.Display)) +
                       ") - use 'Trust connected machine' on each conflicting Connect component";
            return null;
        }

        public static MachineTrust Evaluate(MachineFingerprint? expected, MachineFingerprint? current)
            => current == null ? MachineTrust.Unavailable
             : expected == null ? MachineTrust.FirstConnection
             : expected.Matches(current) ? MachineTrust.Trusted
             : MachineTrust.Mismatch;

        /// <summary>Null when the trust state permits state-changing actions,
        /// otherwise the operator-actionable refusal.</summary>
        public static string? RefusalReason(MachineTrust trust, MachineFingerprint? expected,
                                            MachineFingerprint? current)
        {
            // The enum is presentation state, not the safety proof. Expected identity
            // can be replaced by another Connect component while a command is queued,
            // so a cached permissive value must still agree with the current pair.
            if (trust == MachineTrust.Trusted || trust == MachineTrust.FirstConnection)
            {
                MachineTrust currentEvaluation = Evaluate(expected, current);
                if (currentEvaluation == MachineTrust.Trusted
                    || currentEvaluation == MachineTrust.FirstConnection)
                    return null;
                trust = currentEvaluation;
            }

            return trust switch
            {
                MachineTrust.Mismatch =>
                    $"connected machine ({current?.Display}) is not the one this definition expects " +
                    $"({expected?.Display}) - verify the address, then use 'Trust connected machine' on the Connect component",
                MachineTrust.Unavailable =>
                    "the machine's identity could not be probed - reconnect and check that read-only board/configuration queries are available",
                _ => "machine identity has not been verified - connect first",
            };
        }
    }
}
