using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lugworm.Core.Duet;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The upload/start gates shared by the Print component and the Print
/// Console (WP2 "Shared gates"). The pure pieces are pinned outright; the pieces
/// that would need a live board are exercised up to the point where they must
/// refuse BEFORE touching the client, and the scripted board double proves no
/// request went out.</summary>
public class JobSafetyTests
{
    static DuetPollClient Make(FakeDuet fake) => new DuetPollClient(new DuetClientOptions
    {
        Host = "192.168.1.42",
        Handler = fake,
        RetryBaseDelayMs = 10,
    });

    static ObjectModelSnapshot Fresh() => new ObjectModelSnapshot { TakenUtc = DateTime.UtcNow };

    static RobotLimits Lim() => new RobotLimits { ReachMin = 235.0 };

    // ---- RemotePath -----------------------------------------------------------

    [Fact]
    public void RemotePath_SanitisesToSafeCharacters()
        => Assert.Equal("0:/gcodes/a_b_c.gcode", JobSafety.RemotePath("a b/c"));

    [Fact]
    public void RemotePath_EmptyOrNull_FallsBackToPrint()
    {
        Assert.Equal("0:/gcodes/print.gcode", JobSafety.RemotePath(""));
        Assert.Equal("0:/gcodes/print.gcode", JobSafety.RemotePath(null));
        Assert.Equal(JobSafety.RemotePath(null), JobSafety.RemotePath("print"));
    }

    // ---- StateGate.RefuseJobControl ---------------------------------------------

    [Fact]
    public void RefuseJobControl_NullSnapshot_AsksToConnect()
    {
        string? refusal = StateGate.RefuseJobControl(null, "pausing", allowedStatus: true);
        Assert.NotNull(refusal);
        Assert.Contains("connect", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefuseJobControl_StatusNotAllowed_RefusesForThisState()
    {
        string? refusal = StateGate.RefuseJobControl(Fresh(), "pausing", allowedStatus: false);
        Assert.NotNull(refusal);
        Assert.Contains("not valid in this state", refusal);
    }

    [Fact]
    public void RefuseJobControl_FreshAndAllowed_Passes()
        => Assert.Null(StateGate.RefuseJobControl(Fresh(), "pausing", allowedStatus: true));

    // ---- EnsureFirmwareSafe -----------------------------------------------------

    [Fact]
    public void EnsureFirmwareSafe_ProfileError_RefusesBeforeTouchingTheClient()
    {
        var fake = new FakeDuet();
        var ex = Assert.Throws<DuetException>(() => JobSafety.EnsureFirmwareSafe(
            Fresh(), Make(fake), Lim(), RobotMechanism.Scara,
            sessionConfiguration: new ArmConfiguration(0, 0),
            validatedConfiguration: new ArmConfiguration(0, 0),
            profileError: "robot definition is unsupported"));
        Assert.Contains("refused", ex.Message);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void EnsureFirmwareSafe_ScaraWithoutSessionConfiguration_RequiresVerifiedHome()
    {
        var fake = new FakeDuet();
        var ex = Assert.Throws<DuetException>(() => JobSafety.EnsureFirmwareSafe(
            Fresh(), Make(fake), Lim(), RobotMechanism.Scara,
            sessionConfiguration: null,
            validatedConfiguration: new ArmConfiguration(0, 0),
            profileError: null));
        Assert.Contains("arm configuration is Unknown", ex.Message);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void EnsureFirmwareSafe_JobThatHomesFirst_DoesNotRequireASessionConfiguration()
    {
        // D9 (2026-09-09): the file's own full G28 establishes the configuration the
        // validator walked from; the tracked one is not a precondition for such a job.
        var fake = new FakeDuet();
        var ex = Record.Exception(() => JobSafety.EnsureFirmwareSafe(
            Fresh(), Make(fake), Lim(), RobotMechanism.Scara,
            sessionConfiguration: null,
            validatedConfiguration: ArmConfiguration.Unknown,
            profileError: null,
            jobHomesFirst: true));
        if (ex != null)
        {
            Assert.IsType<DuetException>(ex);
            Assert.DoesNotContain("arm configuration", ex.Message);   // a later probe gate, never this one
        }
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public void EnsureFirmwareSafe_ValidatedConfigurationUnknown_RequiresRevalidation()
    {
        var fake = new FakeDuet();
        var ex = Assert.Throws<DuetException>(() => JobSafety.EnsureFirmwareSafe(
            Fresh(), Make(fake), Lim(), RobotMechanism.Scara,
            sessionConfiguration: new ArmConfiguration(0, 0),
            validatedConfiguration: ArmConfiguration.Unknown,
            profileError: null));
        Assert.Contains("changed since validation", ex.Message);
        Assert.Empty(fake.Requests);
    }

    // ---- CheckMotionAtGateAsync -------------------------------------------------

    [Fact]
    public async Task CheckMotionAtGate_BaselineUnprobed_ReportsInfoWithoutWarning()
    {
        var fake = new FakeDuet();
        var client = Make(fake);
        Assert.False(client.MotionBaselineProbed);
        var (notice, warned) = await JobSafety.CheckMotionAtGateAsync(
            client, new List<string> { "G1 X10 F1200" }, blockWarnings: true, CancellationToken.None);
        Assert.StartsWith("INFO: motion baseline unverified", notice);
        Assert.False(warned);
        Assert.Empty(fake.Requests);
    }
}
