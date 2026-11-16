using System.Globalization;
using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The Potterbot's J1 limits A-41.1:319.1 span 360.2 degrees, so the firmware
/// wraps a proximal angle just below -41.1 to just below +319 instead of refusing it, and
/// the arm turns a full revolution. A colleague's job placed further from the base did
/// exactly that. A job must therefore keep J1 a margin away from both ends of the range;
/// the IK still models the firmware's own limits.</summary>
public class J1SeamMarginTests
{
    static readonly Vec3 HomePose = new Vec3(200, -500, 400);

    static RobotLimits Limits() => new RobotLimits
    {
        ReachMin = 235.0,
        HomedArmMode = 1,
        CalibratedHomePose = HomePose,
        RequireCalibratedHomePose = true,
    };

    static string[] Job(double x, double y)
    {
        string X = x.ToString("0.###", CultureInfo.InvariantCulture);
        string Y = y.ToString("0.###", CultureInfo.InvariantCulture);
        return new[]
        {
            "M83", "G28", "G90",
            $"G1 X{X} Y{Y} Z10 F3000",
            $"G1 X{X} Y{Y} Z10.5 E1 F600",
        };
    }

    // Mode 1 J1 at the target, hand-computed from L1 549.06 / L2 457.23:
    // bearing -60 deg at r 930 mm gives J1 -39.6 deg (1.5 deg inside the firmware limit);
    // bearing -50 deg at r 900 mm gives J1 -25.9 deg.
    [Theory]
    [InlineData(465.0, -805.4, false)]
    [InlineData(578.5, -689.4, true)]
    public void JobGate_RefusesTargetsWithinTheJ1Margin(double x, double y, bool allowed)
    {
        var res = JobGate.Validate(Job(x, y), Limits(), elbow: 1);

        Assert.Equal(allowed, res.Allowed);
        if (!allowed)
            Assert.Contains(res.Checks.Problems,
                p => p.Sev == Severity.Error && p.Message.StartsWith("J1 ", System.StringComparison.Ordinal));
    }
}
