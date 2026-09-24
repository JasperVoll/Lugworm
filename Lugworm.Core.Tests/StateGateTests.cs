using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class StateGateTests
{
    static ObjectModelSnapshot Snap(MachineStatus status = MachineStatus.Idle,
                                    bool homed = true, double ageS = 0)
        => new ObjectModelSnapshot
        {
            Status = status,
            RawStatus = status.ToString().ToLowerInvariant(),
            TakenUtc = DateTime.UtcNow - TimeSpan.FromSeconds(ageS),
            AllHomed = homed,
            Axes = new List<AxisState>
            {
                new AxisState { Letter = "X", MachinePosition = 600, Homed = homed },
                new AxisState { Letter = "Y", MachinePosition = 0, Homed = homed },
                new AxisState { Letter = "Z", MachinePosition = 100, Homed = homed },
            },
        };

    [Fact]
    public void FreshIdleHomed_Passes()
        => Assert.Null(StateGate.Refuse(Snap()));

    [Fact]
    public void NullSnapshot_Refused()
        => Assert.Contains("connect", StateGate.Refuse(null));

    [Fact]
    public void StaleSnapshot_Refused()
        => Assert.Contains("stale", StateGate.Refuse(Snap(ageS: 10)));

    [Theory]
    [InlineData(MachineStatus.Processing)]
    [InlineData(MachineStatus.Busy)]
    [InlineData(MachineStatus.Halted)]
    [InlineData(MachineStatus.Simulating)]
    public void NotIdle_Refused(MachineStatus status)
        => Assert.Contains("only when idle", StateGate.Refuse(Snap(status: status)));

    [Fact]
    public void Paused_RefusedByDefault_AllowedWhenOptedIn()
    {
        Assert.NotNull(StateGate.Refuse(Snap(status: MachineStatus.Paused)));
        Assert.Null(StateGate.Refuse(Snap(status: MachineStatus.Paused), allowPaused: true));
    }

    [Fact]
    public void NotHomed_RefusedUnlessHomingItself()
    {
        Assert.Contains("homed", StateGate.Refuse(Snap(homed: false)));
        Assert.Null(StateGate.Refuse(Snap(homed: false), requireHomed: false));
    }

    [Fact]
    public void SnapshotOlderThanLastCommand_Refused()
    {
        var snap = Snap(ageS: 1);   // taken 1 s ago
        var sentAfter = DateTime.UtcNow;   // command sent just now
        Assert.Contains("predates", StateGate.Refuse(snap, notBeforeUtc: sentAfter));
    }

    [Fact]
    public void SnapshotNewerThanLastCommand_Passes()
    {
        var sentBefore = DateTime.UtcNow - TimeSpan.FromSeconds(2);
        Assert.Null(StateGate.Refuse(Snap(), notBeforeUtc: sentBefore));
    }

    [Fact]
    public void RequireIdleFalse_AllowsBusyButStillChecksFreshness()
    {
        Assert.Null(StateGate.Refuse(Snap(status: MachineStatus.Processing), requireIdle: false));
        Assert.Contains("stale", StateGate.Refuse(
            Snap(status: MachineStatus.Processing, ageS: 10), requireIdle: false));
    }
}
