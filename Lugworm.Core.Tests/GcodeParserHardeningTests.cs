using System;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Lugworm.Core.Robot;
using Xunit;

namespace Lugworm.Core.Tests;

public sealed class GcodeParserHardeningTests
{
    [Fact]
    public void Lexer_AcceptsCompactZeroPaddedAndNNumberedWords()
    {
        var compact = GcodeText.Lex("N42 G01X10Y-2.5E+1*99", 7);

        Assert.Equal("G1", compact.Command);
        Assert.Equal(4, compact.Words.Count);
        Assert.Equal('N', compact.Words[0].Letter);
        Assert.Equal('G', compact.Words[1].Letter);
        Assert.Equal(1, compact.Words[1].Value);
        Assert.Equal('X', compact.Words[2].Letter);
        Assert.Equal(10, compact.Words[2].Value, 9);
        Assert.Equal('Y', compact.Words[3].Letter);
        Assert.Equal(-25, compact.Words[3].Value, 9);
    }

    [Fact]
    public void Lexer_FailedNumberParseIsLineNumberedAndRejected()
    {
        var ex = Assert.Throws<FormatException>(() => GcodeText.Lex("G1 X10. Yoops", 12));

        Assert.Contains("line 12", ex.Message);
        Assert.Contains("word Y", ex.Message);
    }

    [Theory]
    [InlineData("G53 G1 X10")]
    [InlineData("M98 P1")]
    [InlineData("M32 P1")]
    public void Parser_StrictRejectsUnsupportedExecutableCommands(string line)
        => Assert.Throws<FormatException>(() => GcodeParser.Parse(new[] { line }));

    [Theory]
    [InlineData("G28 X")]
    [InlineData("G28 Y")]
    [InlineData("G28 Z")]
    public void Parser_RejectsEveryPerAxisHomeRequest(string line)
        => Assert.Throws<FormatException>(() => GcodeParser.Parse(new[] { line }));

    [Fact]
    public void Parser_CalibratedHomePoseSeedsFirstPhysicalMove()
    {
        var home = new Vec3(125, 240, 35);
        var program = GcodeParser.Parse(new[] { "G28", "G1 X130 Y245 Z40 F600" }, home);

        Assert.True(program.HomePoseKnown);
        Assert.Equal(home, program.HomePosition);
        Assert.Equal(home, program.FirstMoveStart);
        Assert.Equal(new Vec3(130, 245, 40), program.Moves[0].Coordinates.Physical);
    }

    [Fact]
    public void LimitsKey_ChangesWhenCalibratedHomePoseChanges()
    {
        var first = new RobotLimits { RequireCalibratedHomePose = true, CalibratedHomePose = new Vec3(1, 2, 3) };
        var second = new RobotLimits { RequireCalibratedHomePose = true, CalibratedHomePose = new Vec3(1, 2, 4) };

        Assert.NotEqual(ContentHash.LimitsKey(first, 0, 0, true), ContentHash.LimitsKey(second, 0, 0, true));
    }
}
