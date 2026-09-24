using System;
using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Duet;
using Lugworm.Core.Machine;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The machine-component gate set and its severity policy (4.4.0, D8),
/// extracted from <c>MachineComponentBase.ReportActionGates</c>. The behaviour these
/// pin is the 1.12.0 protocol in <c>docs/dev/TESTING.md</c>: an unhomed Print
/// component WARNS instead of silently greying its buttons, a homed one passes, the
/// warning disappears by itself once a verified home has established the arm
/// configuration, and a merely disconnected canvas stays quiet - at most one grey
/// remark per component, never orange.</summary>
public class ActionGatePolicyTests
{
    static ObjectModelSnapshot Snapshot(MachineStatus status = MachineStatus.Idle, bool homed = true)
        => new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            AllHomed = homed,
            TakenUtc = DateTime.UtcNow,
        };

    static MachineActionState Connected(bool homed = true, MachineStatus status = MachineStatus.Idle,
                                        bool armKnown = true)
        => new MachineActionState
        {
            HasSession = true,
            Connected = true,
            ConnectionState = "connected",
            ArmConfigurationKnown = armKnown,
            Snapshot = Snapshot(status, homed),
        };

    static string[] Texts(IEnumerable<GateMessage> messages) => messages.Select(m => m.Text).ToArray();

    // ---- 1.12.0: the unhomed Print component speaks up ----

    [Fact]
    public void AnUnhomedPrintWarns_RatherThanGreyingItsButtonsSilently()
    {
        var gate = ActionGatePolicy.MachineStateGate(Connected(homed: false), "Start",
                                                     activity: "starting a print");

        Assert.Equal("axes are not homed - home the machine first", gate.Reason);
        Assert.Equal(GateSeverity.Warning, gate.Level);

        var messages = ActionGatePolicy.Report(new[] { gate });
        Assert.Equal(new[] { "Start unavailable: axes are not homed - home the machine first" },
                     Texts(messages));
        Assert.Equal(GateSeverity.Warning, messages[0].Level);
    }

    [Fact]
    public void AHomedIdleMachinePasses_AndTheWarningGoesAwayByItself()
    {
        var gate = ActionGatePolicy.MachineStateGate(Connected(homed: true), "Start");

        Assert.Null(gate.Reason);
        Assert.Equal(GateSeverity.None, gate.Level);
        Assert.Empty(ActionGatePolicy.Report(new[] { gate }));
    }

    [Fact]
    public void AnUnknownArmConfigurationRefusesLoudly_AndAVerifiedHomeUnsilencesTheActions()
    {
        var unknown = ActionGatePolicy.ArmConfigurationGate(Connected(armKnown: false), "Validate",
                                                            "configuration-sensitive validation");
        Assert.Equal("configuration-sensitive validation refused - arm configuration is Unknown; "
                     + "complete a verified home first", unknown.Reason);
        Assert.Equal(GateSeverity.Warning, unknown.Level);

        // a verified home is the ONLY thing that establishes the branch; once it has,
        // nothing is reported any more
        var known = ActionGatePolicy.ArmConfigurationGate(Connected(armKnown: true), "Validate",
                                                          "configuration-sensitive validation");
        Assert.Null(known.Reason);
        Assert.Empty(ActionGatePolicy.Report(new[] { known }));
    }

    [Fact]
    public void TheArmGateAsksOnlyAboutTheArm_NotAboutIdleOrHomedFreshness()
    {
        // a running, unhomed machine whose arm branch IS known still passes this gate -
        // the state gate is the one that refuses it
        var state = Connected(homed: false, status: MachineStatus.Processing, armKnown: true);

        Assert.Null(ActionGatePolicy.ArmConfigurationGate(state, "Jog").Reason);
        Assert.NotNull(ActionGatePolicy.MachineStateGate(state, "Jog").Reason);
    }

    // ---- the connection gate everything else starts from ----

    [Fact]
    public void ADisconnectedComponentIsGreyNeverOrange()
    {
        var state = new MachineActionState { HasSession = false };

        var gate = ActionGatePolicy.ConnectionGate(state, "Start");

        Assert.Equal("not connected - press Connect", gate.Reason);
        Assert.Equal(GateSeverity.Remark, gate.Level);
    }

    [Fact]
    public void TheConnectionGateReportsTheLifecycleItIsIn()
    {
        var state = new MachineActionState
        { HasSession = true, Connected = false, ConnectionState = "reconnecting" };

        Assert.Equal("connection is reconnecting", ActionGatePolicy.ConnectionGate(state, "Start").Reason);
        Assert.Equal(GateSeverity.Remark, ActionGatePolicy.ConnectionGate(state, "Start").Level);
    }

    [Fact]
    public void ABusyComponentRefusesBeforeItLooksAtAnythingElse()
    {
        var state = Connected();
        state.Busy = true;

        var gate = ActionGatePolicy.ConnectionGate(state, "Start");

        Assert.Equal("another action is executing", gate.Reason);
        Assert.Equal(GateSeverity.Remark, gate.Level);
    }

    [Fact]
    public void AnUntrustedMachineIsAWarning_AndTheStateGateNeverGetsPastIt()
    {
        var state = Connected(homed: true);
        state.TrustRefusalReason = "connected machine is not the one this definition was set up for";

        var connection = ActionGatePolicy.ConnectionGate(state, "Start");
        Assert.Equal(GateSeverity.Warning, connection.Level);

        // the state gate hands the connection refusal straight through: a machine we do
        // not trust is never asked whether it is idle
        var gate = ActionGatePolicy.MachineStateGate(state, "Start");
        Assert.Equal(connection.Reason, gate.Reason);
        Assert.Equal(GateSeverity.Warning, gate.Level);
    }

    [Fact]
    public void WithoutASnapshotTheStateGateRefuses()
    {
        var state = Connected();
        state.Snapshot = null;

        Assert.Equal("no machine status available - connect first",
                     ActionGatePolicy.MachineStateGate(state, "Start").Reason);
    }

    [Fact]
    public void AStaleSnapshotRefuses_BecauseTheRealStateIsUnknown()
    {
        var state = Connected();
        state.Snapshot!.TakenUtc = DateTime.UtcNow.AddSeconds(-30);

        string? reason = ActionGatePolicy.MachineStateGate(state, "Start").Reason;

        Assert.Contains("stale", reason);
    }

    // ---- reporting: one message per reason ----

    [Fact]
    public void GatesSharingAReasonBecomeOneMessageNamingEveryAction()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("Upload", "not connected - press Connect", GateSeverity.Remark),
            new MachineActionGate("Start", "not connected - press Connect", GateSeverity.Remark),
        });

        Assert.Equal(new[] { "Upload, Start unavailable: not connected - press Connect" }, Texts(messages));
    }

    [Fact]
    public void TheLoudestSeverityOfAMergedGroupWins()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("Upload", "axes are not homed", GateSeverity.Remark),
            new MachineActionGate("Start", "axes are not homed", GateSeverity.Warning),
        });

        Assert.Single(messages);
        Assert.Equal(GateSeverity.Warning, messages[0].Level);
    }

    [Fact]
    public void OpenGatesAndReasonlessGatesSayNothing()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("Start", null, GateSeverity.None),
            new MachineActionGate("Upload", "already connected", GateSeverity.None),
            new MachineActionGate("Validate", null, GateSeverity.Warning),
        });

        Assert.Empty(messages);
    }

    [Fact]
    public void AGateWithoutAnActionNameStillReadsAsASentence()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("  ", "not connected - press Connect", GateSeverity.Remark),
        });

        Assert.Equal(new[] { "action unavailable: not connected - press Connect" }, Texts(messages));
    }

    [Fact]
    public void ReasonsAreReportedInTheOrderTheyWereFirstSeen()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("Start", "second reason", GateSeverity.Remark),
            new MachineActionGate("Upload", "first reason", GateSeverity.Remark),
            new MachineActionGate("Validate", "second reason", GateSeverity.Remark),
        });

        Assert.Equal(new[]
        {
            "Start, Validate unavailable: second reason",
            "Upload unavailable: first reason",
        }, Texts(messages));
    }

    [Fact]
    public void ALongRefusalIsCappedSoItCannotCrowdOutEveryOtherMessage()
    {
        var messages = ActionGatePolicy.Report(new[]
        {
            new MachineActionGate("Start", new string('x', 400), GateSeverity.Warning),
        });

        Assert.Equal(ActionGatePolicy.MaxMessageLength, messages[0].Text.Length);
        Assert.EndsWith("...", messages[0].Text);
    }
}
