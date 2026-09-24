using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lugworm.Core.Settings;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The 4.0.0 settings-file schema (D4). A settings file is the only thing
/// that survives between a component and a folder full of presets, so every value
/// kind must round-trip exactly, the version must gate, and the folder scan must pick
/// the same winner whatever order the caller enumerated the directory in.</summary>
public class ComponentSettingsTests
{
    static readonly CultureInfo DeDe = new("de-DE");

    static T RunUnder<T>(CultureInfo culture, Func<T> f)
    {
        var c0 = CultureInfo.CurrentCulture;
        var u0 = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return f();
        }
        finally
        {
            CultureInfo.CurrentCulture = c0;
            CultureInfo.CurrentUICulture = u0;
        }
    }

    static ComponentSettings Sample(params (string name, SettingsValue value)[] inputs)
        => new ComponentSettings("gcode", "G-code Generator", "4.0.0",
            new[] { "advance" },
            inputs.ToDictionary(i => i.name, i => i.value, StringComparer.Ordinal));

    static ComponentSettings RoundTrip(ComponentSettings s) => ComponentSettings.Parse(s.ToJson());

    // ---- value kinds ----

    [Fact]
    public void EveryScalarKind_RoundTrips()
    {
        var s = RoundTrip(Sample(
            ("layerHeight", SettingsValue.Number(1.0)),
            ("printSpeed", SettingsValue.Number(20.25)),
            ("firmware", SettingsValue.Integer(2)),
            ("relativeE", SettingsValue.Bool(true)),
            ("arcs", SettingsValue.Bool(false)),
            ("savePath", SettingsValue.Text(@"C:\out\a.gcode"))));

        Assert.Equal(SettingsKind.Number, s.Inputs["layerHeight"].Kind);
        Assert.Equal(1.0, s.Inputs["layerHeight"].AsNumber());
        Assert.False(s.Inputs["layerHeight"].IsList);
        Assert.Equal(20.25, s.Inputs["printSpeed"].AsNumber());
        Assert.Equal(SettingsKind.Integer, s.Inputs["firmware"].Kind);
        Assert.Equal(2L, s.Inputs["firmware"].AsInteger());
        Assert.Equal(SettingsKind.Bool, s.Inputs["relativeE"].Kind);
        Assert.True(s.Inputs["relativeE"].AsBool());
        Assert.False(s.Inputs["arcs"].AsBool());
        Assert.Equal(SettingsKind.Text, s.Inputs["savePath"].Kind);
        Assert.Equal(@"C:\out\a.gcode", s.Inputs["savePath"].AsText());
    }

    [Fact]
    public void NumberOne_StaysANumberAndDoesNotDegradeToInteger()
    {
        // "1.0" must not read back as Integer 1: a Number input gets a decimal
        // slider, an Integer input an integer one
        var s = RoundTrip(Sample(("h", SettingsValue.Number(1.0)), ("n", SettingsValue.Integer(1))));
        Assert.Equal(SettingsKind.Number, s.Inputs["h"].Kind);
        Assert.Equal(SettingsKind.Integer, s.Inputs["n"].Kind);
    }

    [Fact]
    public void EveryListKind_RoundTrips()
    {
        var s = RoundTrip(Sample(
            ("temps", SettingsValue.Numbers(new[] { 200.5, 210.0, -0.25 })),
            ("counts", SettingsValue.Integers(new long[] { 1, 2, 30 })),
            ("flags", SettingsValue.Bools(new[] { true, false, true })),
            ("names", SettingsValue.Texts(new[] { "a", "b c", "" }))));

        Assert.True(s.Inputs["temps"].IsList);
        Assert.Equal(new[] { 200.5, 210.0, -0.25 }, s.Inputs["temps"].NumberValues);
        Assert.Equal(new long[] { 1, 2, 30 }, s.Inputs["counts"].IntegerValues);
        Assert.Equal(new[] { true, false, true }, s.Inputs["flags"].BoolValues);
        Assert.Equal(new[] { "a", "b c", "" }, s.Inputs["names"].TextValues);
    }

    [Fact]
    public void TextWithQuotesNewlinesAndBackslashes_RoundTrips()
    {
        const string nasty = "line one\r\nline \"two\"\tend\\path\nrest";
        var s = RoundTrip(Sample(("note", SettingsValue.Text(nasty)),
                                 ("lines", SettingsValue.Texts(new[] { "a\nb", "c\"d" }))));
        Assert.Equal(nasty, s.Inputs["note"].AsText());
        Assert.Equal(new[] { "a\nb", "c\"d" }, s.Inputs["lines"].TextValues);
        // newlines are escaped, never emitted raw inside the quoted value
        string quoted = SettingsValue.Text(nasty).ToJson();
        Assert.DoesNotContain("\n", quoted, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsAndMetadata_RoundTrip()
    {
        var s = RoundTrip(new ComponentSettings("slicer", "Slice", "4.0.0",
            new[] { "nonplanar", "advance", "advance" }, null));
        Assert.Equal("slicer", s.Component);
        Assert.Equal("Slice", s.DisplayName);
        Assert.Equal("4.0.0", s.Plugin);
        Assert.Equal(new[] { "advance", "nonplanar" }, s.Groups);   // sorted, de-duplicated
        Assert.Empty(s.Inputs);
    }

    [Fact]
    public void ToJson_IsDeterministicAndSortsInputsByName()
    {
        var a = Sample(("zeta", SettingsValue.Integer(1)), ("alpha", SettingsValue.Integer(2)));
        var b = Sample(("alpha", SettingsValue.Integer(2)), ("zeta", SettingsValue.Integer(1)));
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.True(a.ToJson().IndexOf("\"alpha\"", StringComparison.Ordinal)
                  < a.ToJson().IndexOf("\"zeta\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DocumentedExampleFile_Parses()
    {
        const string json = @"{ ""iamlab-settings"": 1, ""component"": ""gcode"",
            ""displayName"": ""G-code Generator"", ""plugin"": ""4.0.0"",
            ""groups"": [""advance""],
            ""inputs"": { ""layerHeight"": 1.0, ""firmware"": 2, ""relativeE"": true,
                          ""savePath"": ""C:\\out\\a.gcode"", ""printSpeed"": 20 } }";
        var s = ComponentSettings.Parse(json);
        Assert.Equal("gcode", s.Component);
        Assert.Equal(new[] { "advance" }, s.Groups);
        Assert.Equal(SettingsKind.Number, s.Inputs["layerHeight"].Kind);
        Assert.Equal(SettingsKind.Integer, s.Inputs["firmware"].Kind);
        Assert.Equal(SettingsKind.Integer, s.Inputs["printSpeed"].Kind);
        Assert.Equal(@"C:\out\a.gcode", s.Inputs["savePath"].AsText());
    }

    // ---- refusals ----

    [Fact]
    public void UnknownVersion_IsRefused()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ComponentSettings.Parse(@"{ ""iamlab-settings"": 2, ""component"": ""gcode"" }"));
        Assert.Contains("unsupported settings version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingVersion_IsRefused()
        => Assert.Throws<FormatException>(() => ComponentSettings.Parse(@"{ ""component"": ""gcode"" }"));

    [Fact]
    public void MissingComponent_IsRefused()
    {
        var ex = Assert.Throws<FormatException>(() =>
            ComponentSettings.Parse(@"{ ""iamlab-settings"": 1, ""inputs"": {} }"));
        Assert.Contains("component", ex.Message, StringComparison.Ordinal);

        Assert.Throws<FormatException>(() =>
            ComponentSettings.Parse(@"{ ""iamlab-settings"": 1, ""component"": ""  "" }"));
    }

    [Fact]
    public void MalformedInputValue_ThrowsNamingTheInput()
    {
        var mixed = Assert.Throws<FormatException>(() => ComponentSettings.Parse(
            @"{ ""iamlab-settings"": 1, ""component"": ""gcode"", ""inputs"": { ""temps"": [1, ""two""] } }"));
        Assert.Contains("temps", mixed.Message, StringComparison.Ordinal);

        var nested = Assert.Throws<FormatException>(() => ComponentSettings.Parse(
            @"{ ""iamlab-settings"": 1, ""component"": ""gcode"", ""inputs"": { ""plane"": { ""x"": 1 } } }"));
        Assert.Contains("plane", nested.Message, StringComparison.Ordinal);

        var nulled = Assert.Throws<FormatException>(() => ComponentSettings.Parse(
            @"{ ""iamlab-settings"": 1, ""component"": ""gcode"", ""inputs"": { ""width"": null } }"));
        Assert.Contains("width", nulled.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKeys_AreIgnored()
    {
        var s = ComponentSettings.Parse(
            @"{ ""iamlab-settings"": 1, ""component"": ""gcode"", ""writtenBy"": ""a newer build"",
                ""futureSection"": { ""a"": [1, 2] }, ""inputs"": { ""w"": 2.5 } }");
        Assert.Equal("gcode", s.Component);
        Assert.Single(s.Inputs);
        Assert.Equal(2.5, s.Inputs["w"].AsNumber());
    }

    [Fact]
    public void TryParse_ReportsTheErrorInsteadOfThrowing()
    {
        Assert.False(ComponentSettings.TryParse("not json at all", out var settings, out string? error));
        Assert.Null(settings);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(ComponentSettings.TryParse(
            @"{ ""iamlab-settings"": 1, ""component"": ""slicer"" }", out var ok, out _));
        Assert.Equal("slicer", ok!.Component);
    }

    [Fact]
    public void EmptyComponentKey_IsRefusedByTheConstructor()
        => Assert.Throws<ArgumentException>(() => new ComponentSettings("   "));

    [Fact]
    public void NonFiniteNumber_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsValue.Number(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsValue.Numbers(new[] { 1.0, double.PositiveInfinity }));
    }

    // ---- culture ----

    [Fact]
    public void Serialization_IsIdenticalUnderGermanCulture()
    {
        Func<string> write = () => Sample(
            ("layerHeight", SettingsValue.Number(1.25)),
            ("printSpeed", SettingsValue.Number(20.5)),
            ("temps", SettingsValue.Numbers(new[] { 200.5, -0.125 })),
            ("firmware", SettingsValue.Integer(2)),
            ("relativeE", SettingsValue.Bool(true)),
            ("savePath", SettingsValue.Text(@"C:\out\a.gcode"))).ToJson();

        string invariant = RunUnder(CultureInfo.InvariantCulture, write);
        string german = RunUnder(DeDe, write);
        Assert.Equal(invariant, german);
        Assert.Contains("\"layerHeight\": 1.25", invariant, StringComparison.Ordinal);
        Assert.DoesNotContain("1,25", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void Parsing_IsIdenticalUnderGermanCulture()
    {
        const string json = @"{ ""iamlab-settings"": 1, ""component"": ""gcode"",
            ""inputs"": { ""h"": 1.25, ""temps"": [200.5, -0.125], ""n"": 7 } }";
        double[] Values(ComponentSettings s) => new[]
        {
            s.Inputs["h"].AsNumber(), s.Inputs["temps"].NumberValues[0],
            s.Inputs["temps"].NumberValues[1], s.Inputs["n"].AsNumber(),
        };
        var invariant = RunUnder(CultureInfo.InvariantCulture, () => ComponentSettings.Parse(json));
        var german = RunUnder(DeDe, () => ComponentSettings.Parse(json));
        Assert.Equal(Values(invariant), Values(german));
        Assert.Equal(1.25, Values(german)[0]);
        Assert.Equal("1.25", RunUnder(DeDe, () => german.Inputs["h"].TextAt(0)));
    }

    // ---- folder scan ----

    static string File(string component) =>
        "{ \"iamlab-settings\": 1, \"component\": \"" + component + "\", \"inputs\": { \"a\": 1 } }";

    [Fact]
    public void Scan_MatchesByTheComponentFieldNotTheFileName()
    {
        var index = SettingsFolder.Scan(new[]
        {
            (@"C:\p\fast-clay.iamset.json", File("gcode")),
            (@"C:\p\anything.json", File("slicer")),
        });
        Assert.Equal(2, index.ByComponent.Count);
        Assert.Equal(@"C:\p\fast-clay.iamset.json", index.PathFor("gcode"));
        Assert.NotNull(index.For("slicer"));
        Assert.Empty(index.Unreadable);
        Assert.Empty(index.Duplicates);
    }

    [Fact]
    public void Scan_IgnoresNonJsonFiles()
    {
        var index = SettingsFolder.Scan(new[]
        {
            (@"C:\p\notes.txt", File("gcode")),
            (@"C:\p\a.gcode", File("slicer")),
            (@"C:\p\real.JSON", File("mover")),
        });
        Assert.Single(index.ByComponent);
        Assert.NotNull(index.For("mover"));
    }

    [Fact]
    public void Scan_AlphabeticallyFirstPathWinsAndTheRestAreDuplicates()
    {
        var files = new[]
        {
            (@"C:\p\zulu.iamset.json", File("gcode")),
            (@"C:\p\Alpha.iamset.json", File("gcode")),
            (@"C:\p\mike.iamset.json", File("gcode")),
        };
        var index = SettingsFolder.Scan(files);
        Assert.Equal(@"C:\p\Alpha.iamset.json", index.PathFor("gcode"));
        var dup = Assert.Single(index.Duplicates);
        Assert.Equal("gcode", dup.component);
        Assert.Equal(new[] { @"C:\p\Alpha.iamset.json", @"C:\p\mike.iamset.json", @"C:\p\zulu.iamset.json" },
                     dup.paths);

        // enumeration order must not change the winner
        var reversed = SettingsFolder.Scan(Enumerable.Reverse(files));
        Assert.Equal(index.PathFor("gcode"), reversed.PathFor("gcode"));
    }

    [Fact]
    public void Scan_ReportsUnreadableFilesAndKeepsTheRest()
    {
        var index = SettingsFolder.Scan(new[]
        {
            (@"C:\p\broken.iamset.json", "{ not json"),
            (@"C:\p\future.iamset.json", @"{ ""iamlab-settings"": 99, ""component"": ""gcode"" }"),
            (@"C:\p\good.iamset.json", File("slicer")),
        });
        Assert.Single(index.ByComponent);
        Assert.NotNull(index.For("slicer"));
        Assert.Equal(2, index.Unreadable.Count);
        Assert.Equal(@"C:\p\broken.iamset.json", index.Unreadable[0].path);
        Assert.Contains("unsupported settings version",
                        index.Unreadable[1].error, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_OfNothing_IsEmpty()
    {
        var index = SettingsFolder.Scan(Array.Empty<(string, string)>());
        Assert.True(index.IsEmpty);
        Assert.Null(index.For("gcode"));
        Assert.True(SettingsFolderIndex.Empty.IsEmpty);
    }

    [Fact]
    public void Scan_LookupIsCaseInsensitiveOnTheComponentKey()
    {
        var index = SettingsFolder.Scan(new[] { (@"C:\p\a.iamset.json", File("GCode")) });
        Assert.NotNull(index.For("gcode"));
    }

    [Fact]
    public void SettingsValue_AllTextIsTheWiredPanelForm()
    {
        Assert.Equal(new[] { "1.5", "2.0" }, SettingsValue.Numbers(new[] { 1.5, 2.0 }).AllText());
        Assert.Equal(new[] { "1", "2" }, SettingsValue.Integers(new long[] { 1, 2 }).AllText());
        Assert.Equal(new[] { "true", "false" }, SettingsValue.Bools(new[] { true, false }).AllText());
        Assert.Equal("plasma", SettingsValue.Text("plasma").AsText());
    }
}
