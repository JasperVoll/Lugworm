using System;
using System.Collections.Generic;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>Direct coverage for Duet/JsonLite.cs (JsonValue) that is not already
/// exercised indirectly through ObjectModel parsing in DuetJsonTests.cs. That file
/// already covers scalars, string escapes (including \u), nested object/array path
/// lookups, empty containers, several malformed-input shapes, non-finite number
/// rejection and the culture guard (CultureInvarianceTests.DuetJsonNumbers_
/// ParseIdenticallyUnderGermanCulture) - this file adds only what is missing:
/// negative zero, duplicate keys, more exponent forms, the integer-literal flag,
/// typed-accessor edge cases, additional truncated-input shapes, the limited
/// Parse overload's own guards, and indexer behaviour on the wrong JsonKind.</summary>
public class JsonLiteTests
{
    [Fact]
    public void Parse_NegativeZero_IsNumericallyZeroAndStillAnIntegerLiteral()
    {
        var v = JsonValue.Parse("-0");
        Assert.Equal(0.0, v.AsNumber());
        Assert.True(v.IsIntegerLiteral);
    }

    [Fact]
    public void Parse_NegativeZeroWithFraction_IsNotAnIntegerLiteral()
    {
        var v = JsonValue.Parse("-0.0");
        Assert.Equal(0.0, v.AsNumber());
        Assert.False(v.IsIntegerLiteral);
    }

    [Fact]
    public void Parse_DuplicateKeys_LastValueWins()
    {
        var v = JsonValue.Parse("{\"a\":1,\"a\":2,\"a\":3}");
        Assert.Equal(1, v.Count);           // deduplicated into one member
        Assert.Equal(3, v.Int("a"));
    }

    [Theory]
    [InlineData("1e3", 1000.0)]
    [InlineData("1E3", 1000.0)]
    [InlineData("1.5e-2", 0.015)]
    [InlineData("2e+3", 2000.0)]
    [InlineData("-2.5e+2", -250.0)]
    public void Parse_ExponentForms_AllParseToTheSameValue(string json, double expected)
    {
        Assert.Equal(expected, JsonValue.Parse(json).AsNumber(), 12);
    }

    [Theory]
    [InlineData("7", true)]
    [InlineData("-7", true)]
    [InlineData("7.0", false)]
    [InlineData("7e0", false)]
    [InlineData("7E2", false)]
    public void IsIntegerLiteral_DistinguishesBareIntegersFromDecimalOrExponentForms(string json, bool expected)
    {
        Assert.Equal(expected, JsonValue.Parse(json).IsIntegerLiteral);
    }

    [Theory]
    [InlineData("2.4", 2)]
    [InlineData("2.6", 3)]
    [InlineData("-2.6", -3)]
    public void AsInt_RoundsToNearestInteger(string json, int expected)
    {
        Assert.Equal(expected, JsonValue.Parse(json).AsInt());
    }

    [Fact]
    public void AsInt_OnNonNumber_ReturnsSuppliedDefault()
    {
        Assert.Equal(-1, JsonValue.Parse("\"x\"").AsInt(-1));
        Assert.Equal(0, JsonValue.Parse("null").AsInt());
    }

    [Theory]
    [InlineData("1e-13", false)]  // below the 1e-12 threshold: treated as zero
    [InlineData("1e-11", true)]   // above it: treated as nonzero
    [InlineData("0", false)]
    [InlineData("-5", true)]
    public void AsBool_OnNumbers_UsesMagnitudeThreshold(string json, bool expected)
    {
        Assert.Equal(expected, JsonValue.Parse(json).AsBool());
    }

    [Fact]
    public void AsBool_OnNonNumberNonBool_ReturnsSuppliedDefault()
    {
        Assert.True(JsonValue.Parse("\"x\"").AsBool(true));
        Assert.False(JsonValue.Parse("\"x\"").AsBool());
    }

    [Theory]
    [InlineData("[1,2")]                    // unterminated array
    [InlineData("{\"a\"")]                  // unterminated object, key only
    [InlineData("\"abc\\u12\"")]             // truncated \u escape (needs 4 hex digits)
    [InlineData("\"trailing backslash\\")]   // unterminated escape at end of input
    [InlineData("tru")]                      // truncated literal
    [InlineData("{\"a\":1,}")]               // trailing comma before '}'
    public void Parse_TruncatedOrMalformedInput_ThrowsFormatException(string bad)
    {
        Assert.Throws<FormatException>(() => JsonValue.Parse(bad));
    }

    [Fact]
    public void Parse_NullInput_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => JsonValue.Parse(null!));
    }

    // ---- limited Parse overload: explicit character/depth/node caps ----

    [Fact]
    public void Parse_ExceedingMaxCharacters_ThrowsFormatException()
    {
        string text = "\"" + new string('x', 50) + "\"";
        Assert.Throws<FormatException>(() => JsonValue.Parse(text, maxCharacters: 10, maxDepth: 64, maxNodes: 1000));
    }

    [Fact]
    public void Parse_ExceedingMaxDepth_ThrowsFormatException()
    {
        string nested = string.Concat(System.Linq.Enumerable.Repeat("[", 20)) + "1"
                       + string.Concat(System.Linq.Enumerable.Repeat("]", 20));
        Assert.Throws<FormatException>(() => JsonValue.Parse(nested, maxCharacters: 4096, maxDepth: 5, maxNodes: 1000));
    }

    [Fact]
    public void Parse_ExceedingMaxNodes_ThrowsFormatException()
    {
        string arr = "[" + string.Join(",", System.Linq.Enumerable.Repeat("1", 50)) + "]";
        Assert.Throws<FormatException>(() => JsonValue.Parse(arr, maxCharacters: 4096, maxDepth: 64, maxNodes: 10));
    }

    [Theory]
    [InlineData(0, 64, 1000)]
    [InlineData(4096, 0, 1000)]
    [InlineData(4096, 64, 0)]
    [InlineData(-1, 64, 1000)]
    public void Parse_NonPositiveLimits_ThrowArgumentOutOfRangeException(int maxChars, int maxDepth, int maxNodes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonValue.Parse("1", maxChars, maxDepth, maxNodes));
    }

    [Fact]
    public void Parse_WithinExplicitLimits_StillParsesNormally()
    {
        var v = JsonValue.Parse("{\"a\":[1,2,3]}", maxCharacters: 4096, maxDepth: 64, maxNodes: 1000);
        Assert.Equal(2, v.At("a[1]").AsInt());
    }

    // ---- indexers on the wrong JsonKind return Null rather than throw ----

    [Fact]
    public void StringIndexer_OnNonObject_ReturnsNull()
    {
        Assert.True(JsonValue.Parse("5")["key"].IsNull);
        Assert.True(JsonValue.Parse("[1,2]")["key"].IsNull);
        Assert.True(JsonValue.Parse("\"s\"")["key"].IsNull);
    }

    [Fact]
    public void IntIndexer_OnNonArray_ReturnsNull()
    {
        Assert.True(JsonValue.Parse("5")[0].IsNull);
        Assert.True(JsonValue.Parse("{}")[0].IsNull);
    }

    [Fact]
    public void IntIndexer_OutOfRange_ReturnsNull()
    {
        var arr = JsonValue.Parse("[1,2,3]");
        Assert.True(arr[-1].IsNull);
        Assert.True(arr[3].IsNull);
    }

    [Fact]
    public void At_MissingClosingBracket_ReturnsNullRatherThanThrowing()
    {
        var v = JsonValue.Parse("{\"a\":[1,2]}");
        Assert.True(v.At("a[2").IsNull);
    }

    [Fact]
    public void At_NonNumericArrayIndex_ReturnsNull()
    {
        var v = JsonValue.Parse("{\"a\":[1,2]}");
        Assert.True(v.At("a[x]").IsNull);
    }

    [Fact]
    public void Keys_OnObject_ListsEveryMemberExactlyOnce()
    {
        var v = JsonValue.Parse("{\"b\":1,\"a\":2}");
        Assert.Equal(2, v.Count);
        Assert.Equal(new HashSet<string> { "b", "a" }, new HashSet<string>(v.Keys));
    }

    [Fact]
    public void Keys_OnNonObject_IsEmpty()
    {
        Assert.Empty(JsonValue.Parse("[1,2]").Keys);
        Assert.Empty(JsonValue.Parse("5").Keys);
    }
}
