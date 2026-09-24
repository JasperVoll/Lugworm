using Lugworm.Core.Gcode;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests
{
    /// <summary>0.1.0-beta stabilisation: Print Stats hands its travelFeed input to the
    /// G-code estimate. A travel move without an F word used to take twice the print feed
    /// whatever the socket said.</summary>
    public class PrintStatsTravelFeedTests
    {
        // one 100 mm extrusion and one 100 mm travel, neither carrying a feed
        static readonly string[] Lines =
        {
            "G21", "G90", "M83", "G28",
            "G1 X100 Y0 Z2 E1",
            "G1 X100 Y100 Z2",
        };

        [Fact]
        public void TravelWithoutFeed_TakesTheCallersTravelFeed()
        {
            var prog = GcodeParser.Parse(Lines);
            Assert.Equal(2, prog.Moves.Count);

            // legacy default: the 100 mm travel runs at twice the print feed (25 -> 50 mm/s) = 2 s
            var legacy = PrintStatsCalc.FromMoves(prog.Moves, 10, 3, 2.0, 25);

            // the component's travelFeed: 100 mm/s -> the same travel takes 1 s, nothing else moves
            var given = PrintStatsCalc.FromMoves(prog.Moves, 10, 3, 2.0, 25, defaultTravelFeedMmS: 100);
            Assert.Equal(legacy.TimeMin - 1.0 / 60.0, given.TimeMin, 6);
            Assert.Equal(legacy.ExtrudeLenMm, given.ExtrudeLenMm, 6);
        }
    }
}
