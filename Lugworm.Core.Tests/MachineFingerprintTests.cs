using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>AUD-SAF-004: machine identity fingerprints and the trust rule that gates
/// every state-changing Duet action on them.</summary>
public class MachineFingerprintTests
{
    [Theory]
    [InlineData(MachineFingerprintKind.UniqueId, "08DGM-9T66A-G63SJ")]
    [InlineData(MachineFingerprintKind.BoardDiagnostic, "0JD0M-9K662")]
    [InlineData(MachineFingerprintKind.Configuration, "a3f9c2")]
    public void SerializeRoundTrip(MachineFingerprintKind kind, string value)
    {
        var fp = new MachineFingerprint(kind, value);
        var back = MachineFingerprint.TryParse(fp.Serialize());
        Assert.NotNull(back);
        Assert.Equal(kind, back!.Kind);
        Assert.Equal(value, back.Value);
        Assert.True(fp.Matches(back));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("uid:")]
    [InlineData(":value")]
    [InlineData("weird:value")]
    public void TryParse_Garbage_ReturnsNull(string? s)
        => Assert.Null(MachineFingerprint.TryParse(s));

    [Fact]
    public void DifferentKindSameValue_DoesNotMatch()
    {
        // a downgraded probe (uniqueId last time, config hash now) is a mismatch,
        // never a silent acceptance
        var a = new MachineFingerprint(MachineFingerprintKind.UniqueId, "X");
        var b = new MachineFingerprint(MachineFingerprintKind.Configuration, "X");
        Assert.False(a.Matches(b));
    }

    [Fact]
    public void TrustRule_Matrix()
    {
        var fp = new MachineFingerprint(MachineFingerprintKind.UniqueId, "A");
        var other = new MachineFingerprint(MachineFingerprintKind.UniqueId, "B");
        Assert.Equal(MachineTrust.Unavailable, MachineTrustEvaluator.Evaluate(fp, null));
        Assert.Equal(MachineTrust.FirstConnection, MachineTrustEvaluator.Evaluate(null, fp));
        Assert.Equal(MachineTrust.Trusted, MachineTrustEvaluator.Evaluate(fp, fp));
        Assert.Equal(MachineTrust.Mismatch, MachineTrustEvaluator.Evaluate(fp, other));
    }

    [Fact]
    public void RefusalReason_OnlyTrustStatesPass()
    {
        var fp = new MachineFingerprint(MachineFingerprintKind.UniqueId, "A");
        var other = new MachineFingerprint(MachineFingerprintKind.UniqueId, "B");
        Assert.Null(MachineTrustEvaluator.RefusalReason(MachineTrust.Trusted, fp, fp));
        Assert.Null(MachineTrustEvaluator.RefusalReason(MachineTrust.FirstConnection, null, fp));
        Assert.NotNull(MachineTrustEvaluator.RefusalReason(MachineTrust.Mismatch, fp, other));
        Assert.NotNull(MachineTrustEvaluator.RefusalReason(MachineTrust.Unavailable, fp, null));
        Assert.NotNull(MachineTrustEvaluator.RefusalReason(MachineTrust.Unknown, null, null));
    }

    [Theory]
    [InlineData(MachineTrust.Trusted)]
    [InlineData(MachineTrust.FirstConnection)]
    public void RefusalReason_CachedPermissiveStateCannotBypassCurrentMismatch(MachineTrust cachedTrust)
    {
        var expected = new MachineFingerprint(MachineFingerprintKind.UniqueId, "A");
        var current = new MachineFingerprint(MachineFingerprintKind.UniqueId, "B");

        Assert.NotNull(MachineTrustEvaluator.RefusalReason(cachedTrust, expected, current));
    }

    [Fact]
    public void RefusalReason_CachedTrustedStateCannotBypassUnavailableIdentity()
    {
        var expected = new MachineFingerprint(MachineFingerprintKind.UniqueId, "A");

        Assert.NotNull(MachineTrustEvaluator.RefusalReason(MachineTrust.Trusted, expected, null));
    }

    [Fact]
    public void MatchingPersistedExpectations_HaveAConsensus()
    {
        var a = new MachineFingerprint(MachineFingerprintKind.UniqueId, "board-a");
        var same = new MachineFingerprint(MachineFingerprintKind.UniqueId, "board-a");

        MachineFingerprint? consensus = MachineTrustEvaluator.Consensus(
            new[] { a, same }, out string? conflict);

        Assert.NotNull(consensus);
        Assert.True(consensus!.Matches(a));
        Assert.Null(conflict);
    }

    [Fact]
    public void ConflictingPersistedExpectations_HaveNoConsensus()
    {
        var a = new MachineFingerprint(MachineFingerprintKind.UniqueId, "board-a");
        var b = new MachineFingerprint(MachineFingerprintKind.UniqueId, "board-b");

        MachineFingerprint? consensus = MachineTrustEvaluator.Consensus(
            new[] { a, b }, out string? conflict);

        Assert.Null(consensus);
        Assert.Contains("conflicting", conflict);
        Assert.Contains("board id board-a", conflict);
        Assert.Contains("board id board-b", conflict);
    }

    [Fact]
    public void ConfigurationHash_StableAndSensitive()
    {
        var limits = new List<KeyValuePair<string, (double, double)>>
        {
            new("X", (0.0, 700.0)), new("Y", (-500.0, 0.0)),
        };
        var cfg = new byte[] { 1, 2, 3 };
        var a = MachineFingerprint.Configuration("duetwifi102", "Scara P549.06 D457.23", cfg, limits);
        var b = MachineFingerprint.Configuration("duetwifi102", "Scara P549.06 D457.23", cfg, limits);
        Assert.True(a.Matches(b));   // stable across reconnects

        var otherCfg = MachineFingerprint.Configuration("duetwifi102", "Scara P549.06 D457.23",
                                                        new byte[] { 1, 2, 4 }, limits);
        Assert.False(a.Matches(otherCfg));   // config.g change changes identity
        var otherBoard = MachineFingerprint.Configuration("duet3", "Scara P549.06 D457.23", cfg, limits);
        Assert.False(a.Matches(otherBoard));
    }

    [Fact]
    public void ConfigurationHash_AxisOrderIrrelevant()
    {
        var cfg = new byte[] { 9 };
        var a = MachineFingerprint.Configuration("b", "k", cfg, new List<KeyValuePair<string, (double, double)>>
        { new("X", (0.0, 1.0)), new("Y", (0.0, 2.0)) });
        var b = MachineFingerprint.Configuration("b", "k", cfg, new List<KeyValuePair<string, (double, double)>>
        { new("Y", (0.0, 2.0)), new("X", (0.0, 1.0)) });
        Assert.True(a.Matches(b));
    }

    [Fact]
    public void ConfigurationDisplay_SaysItIsNotPhysicalIdentity()
    {
        var fp = MachineFingerprint.Configuration("b", "k", null, null);
        Assert.Contains("not the physical board", fp.Display);
    }
}
