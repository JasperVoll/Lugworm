using System;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class DuetJsonTests
{
    [Fact]
    public void Parse_Scalars()
    {
        Assert.Equal(42.0, JsonValue.Parse("42").AsNumber());
        Assert.Equal(-3.5e2, JsonValue.Parse("-3.5e2").AsNumber());
        Assert.True(JsonValue.Parse("true").AsBool());
        Assert.False(JsonValue.Parse("false").AsBool(true));
        Assert.True(JsonValue.Parse("null").IsNull);
        Assert.Equal("hi", JsonValue.Parse("\"hi\"").AsString());
    }

    [Fact]
    public void Parse_StringEscapes()
    {
        var v = JsonValue.Parse("\"a\\n\\t\\\"b\\\\c\\u0041\"");
        Assert.Equal("a\n\t\"b\\cA", v.AsString());
    }

    [Fact]
    public void Parse_NestedObjectAndPath()
    {
        var v = JsonValue.Parse("{\"a\":{\"b\":[10,{\"c\":\"x\"},30]},\"d\":true}");
        Assert.Equal("x", v.Str("a.b[1].c"));
        Assert.Equal(10, v.Int("a.b[0]"));
        Assert.Equal(30.0, v.Num("a.b[2]"));
        Assert.True(v.Bool("d"));
        // absent paths return defaults, never throw
        Assert.Equal("fallback", v.Str("a.zz.q", "fallback"));
        Assert.Equal(-1, v.Int("a.b[9]", -1));
    }

    [Fact]
    public void Parse_EmptyContainers()
    {
        Assert.Equal(0, JsonValue.Parse("{}").Count);
        Assert.Equal(0, JsonValue.Parse("[]").Count);
        Assert.Equal(2, JsonValue.Parse("[1,2]").Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"a\":}")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("\"unterminated")]
    [InlineData("{'single':1}")]
    public void Parse_Malformed_Throws(string bad)
    {
        Assert.Throws<FormatException>(() => JsonValue.Parse(bad));
    }

    // rr_connect response as documented for RRF 3.4+
    [Fact]
    public void Parse_RrConnectShape()
    {
        var v = JsonValue.Parse(
            "{\"err\":0,\"sessionTimeout\":8000,\"boardType\":\"duetwifi102\"," +
            "\"sessionKey\":1587298221,\"apiLevel\":1}");
        Assert.Equal(0, v.Int("err", -1));
        Assert.Equal("duetwifi102", v.Str("boardType"));
        Assert.Equal(1587298221L, (long)v.Num("sessionKey"));
        Assert.Equal(1, v.Int("apiLevel"));
    }

    [Fact]
    public void ObjectModel_State_And_Axes()
    {
        var state = JsonValue.Parse("{\"status\":\"idle\",\"upTime\":120}");
        var axes = JsonValue.Parse(
            "[{\"letter\":\"X\",\"machinePosition\":100.5,\"userPosition\":100.5,\"homed\":true,\"min\":-1008,\"max\":1008}," +
            " {\"letter\":\"Y\",\"machinePosition\":-50.0,\"userPosition\":-50.0,\"homed\":true,\"min\":-1008,\"max\":1008}," +
            " {\"letter\":\"Z\",\"machinePosition\":10.0,\"userPosition\":10.0,\"homed\":false,\"min\":0,\"max\":1030}]");
        var snap = ObjectModelParser.FromRrModel(state, axes, null);

        Assert.Equal(MachineStatus.Idle, snap.Status);
        Assert.Equal(3, snap.Axes.Count);
        Assert.False(snap.AllHomed);                    // Z not homed
        Assert.Equal(100.5, snap.Axis("X")!.MachinePosition);
        Assert.Equal(-50.0, snap.Axis("y")!.MachinePosition);   // case-insensitive
        Assert.Null(snap.Job);
        Assert.True((DateTime.UtcNow - snap.TakenUtc).TotalSeconds < 5);
    }

    [Fact]
    public void ObjectModel_AllHomed_RequiresVisibleAxes()
    {
        var state = JsonValue.Parse("{\"status\":\"idle\"}");
        var snapEmpty = ObjectModelParser.FromRrModel(state, JsonValue.Parse("[]"), null);
        Assert.False(snapEmpty.AllHomed);   // no axes reported => never claim homed

        var axes = JsonValue.Parse(
            "[{\"letter\":\"X\",\"homed\":true},{\"letter\":\"Y\",\"homed\":true}," +
            " {\"letter\":\"U\",\"homed\":false,\"visible\":false}]");
        var snap = ObjectModelParser.FromRrModel(state, axes, null);
        Assert.True(snap.AllHomed);         // hidden unhomed axis doesn't gate
    }

    [Fact]
    public void ObjectModel_Job_Progress()
    {
        var state = JsonValue.Parse("{\"status\":\"processing\"}");
        var axes = JsonValue.Parse("[{\"letter\":\"X\",\"homed\":true}]");
        var job = JsonValue.Parse(
            "{\"file\":{\"fileName\":\"0:/gcodes/vase.gcode\",\"size\":2000}," +
            "\"filePosition\":500,\"duration\":63.5,\"layer\":7}");
        var snap = ObjectModelParser.FromRrModel(state, axes, job);

        Assert.Equal(MachineStatus.Processing, snap.Status);
        Assert.NotNull(snap.Job);
        Assert.Equal("0:/gcodes/vase.gcode", snap.Job!.FileName);
        Assert.Equal(0.25, snap.Job.FractionPrinted, 9);
        Assert.Equal(7, snap.Job.Layer);
        Assert.Equal(63.5, snap.Job.TimePrinting!.Value, 9);
    }

    [Fact]
    public void ObjectModel_UnknownStatus_MapsToUnknown()
    {
        Assert.Equal(MachineStatus.Unknown, ObjectModelParser.ParseStatus("weird"));
        Assert.Equal(MachineStatus.Halted, ObjectModelParser.ParseStatus("halted"));
        Assert.Equal(MachineStatus.ChangingTool, ObjectModelParser.ParseStatus("changingTool"));
    }

    // ---- legacy rr_status (RRF 3.1.1 "3DPotter Edition" - the actual Potterbot) ----

    // shape of rr_status?type=3 on RRF 3.x standalone with DWC2
    const string LegacyStatus3 =
        "{\"status\":\"P\",\"coords\":{\"axesHomed\":[1,1,1],\"wpl\":1," +
        "\"xyz\":[200.000,600.000,120.500],\"machine\":[200.000,600.000,120.500],\"extr\":[15.2]}," +
        "\"speeds\":{\"requested\":20.0,\"top\":25.0},\"currentTool\":0," +
        "\"params\":{\"atxPower\":-1,\"fanPercent\":[0],\"speedFactor\":100.0,\"extrFactors\":[100.0],\"babystep\":0.0}," +
        "\"seq\":7,\"sensors\":{\"probeValue\":0,\"fanRPM\":[-1]}," +
        "\"temps\":{\"current\":[2000.0],\"state\":[0],\"tools\":{\"active\":[[0.0]],\"standby\":[[0.0]]},\"extra\":[]}," +
        "\"time\":1234.0,\"currentLayer\":12,\"fractionPrinted\":37.5,\"filePosition\":5000,\"printDuration\":600.0,\"extrRaw\":[128.75]}";

    [Fact]
    public void LegacyStatus_Printing_Parses()
    {
        var snap = ObjectModelParser.FromRrStatus(JsonValue.Parse(LegacyStatus3), "0:/gcodes/vase.gcode");

        Assert.Equal(MachineStatus.Processing, snap.Status);
        Assert.Equal("P", snap.RawStatus);
        Assert.True(snap.AllHomed);
        Assert.Equal(3, snap.Axes.Count);
        Assert.Equal(200.0, snap.Axis("X")!.MachinePosition);
        Assert.Equal(120.5, snap.Axis("Z")!.MachinePosition);
        Assert.NotNull(snap.Job);
        Assert.Equal(0.375, snap.Job!.FractionPrinted, 9);     // percent -> fraction
        Assert.Equal(12, snap.Job.Layer);
        Assert.Equal(600.0, snap.Job.TimePrinting!.Value, 9);
        Assert.Equal("0:/gcodes/vase.gcode", snap.Job.FileName);
        Assert.Equal(5000L, snap.Job.FilePosition);          // was parsed but dropped before 1.10.0

        // motion telemetry the print log records
        Assert.Equal(20.0, snap.RequestedSpeed!.Value, 9);
        Assert.Equal(25.0, snap.TopSpeed!.Value, 9);
        Assert.Equal(100.0, snap.SpeedFactorPercent!.Value, 9);   // legacy already reports a percent
        Assert.Equal(100.0, snap.Overrides!.FlowFactorPercent!.Value, 9);
        Assert.Equal(128.75, snap.RawExtrusion!.Value, 9);        // extrRaw, not coords.extr
    }

    [Fact]
    public void LegacyStatus_RawExtrusion_IgnoresFactorAffectedCoordsExtr()
    {
        // coords.extr has the extrusion factor applied; only extrRaw is comparable
        // with RRF3's job.rawExtrusion, so a body without extrRaw must yield null.
        var json = JsonValue.Parse(
            "{\"status\":\"P\",\"coords\":{\"axesHomed\":[1,1,1]," +
            "\"xyz\":[0,0,0],\"machine\":[0,0,0],\"extr\":[15.2]}," +
            "\"fractionPrinted\":10.0,\"seq\":1}");
        var snap = ObjectModelParser.FromRrStatus(json, null);

        Assert.Null(snap.RawExtrusion);
    }

    [Fact]
    public void LegacyStatus_MissingTelemetry_YieldsNullsNotZeros()
    {
        var json = JsonValue.Parse(
            "{\"status\":\"I\",\"coords\":{\"axesHomed\":[1,1,1]," +
            "\"xyz\":[0,0,0],\"machine\":[0,0,0]},\"seq\":1}");
        var snap = ObjectModelParser.FromRrStatus(json, null);

        Assert.Null(snap.RequestedSpeed);
        Assert.Null(snap.TopSpeed);
        Assert.Null(snap.SpeedFactorPercent);
        Assert.Null(snap.RawExtrusion);
    }

    // ---- rr_model motion fragments (RRF 3.2+) ----

    [Fact]
    public void RrModel_MotionFragments_NormalizeSpeedFactorToPercent()
    {
        var state = JsonValue.Parse("{\"status\":\"processing\"}");
        var axes = JsonValue.Parse("[{\"letter\":\"X\",\"homed\":true}]");
        var job = JsonValue.Parse(
            "{\"file\":{\"fileName\":\"0:/gcodes/vase.gcode\",\"size\":2000}," +
            "\"filePosition\":500,\"rawExtrusion\":42.5,\"layer\":3}");
        var moveCurrent = JsonValue.Parse("{\"requestedSpeed\":18.0,\"topSpeed\":22.5}");
        // move.speedFactor is a FRACTION here, a percent on the legacy API
        var speedFactor = JsonValue.Parse("1.25");

        var extruders = JsonValue.Parse("[{\"factor\":0.85}]");
        var snap = ObjectModelParser.FromRrModel(state, axes, job, moveCurrent, speedFactor,
                                                 acquisition: null, moveExtruders: extruders);

        Assert.Equal(18.0, snap.RequestedSpeed!.Value, 9);
        Assert.Equal(22.5, snap.TopSpeed!.Value, 9);
        Assert.Equal(125.0, snap.SpeedFactorPercent!.Value, 9);
        Assert.Equal(125.0, snap.Overrides!.SpeedFactorPercent!.Value, 9);
        Assert.Equal(85.0, snap.Overrides.FlowFactorPercent!.Value, 9);
        Assert.Equal(42.5, snap.RawExtrusion!.Value, 9);
    }

    [Fact]
    public void RrModel_AbsentMotionFragments_LeaveTelemetryNull()
    {
        // a board that does not expose the optional keys still polls fine
        var state = JsonValue.Parse("{\"status\":\"processing\"}");
        var axes = JsonValue.Parse("[{\"letter\":\"X\",\"homed\":true}]");
        var snap = ObjectModelParser.FromRrModel(state, axes, null, null, null);

        Assert.Equal(MachineStatus.Processing, snap.Status);
        Assert.Null(snap.RequestedSpeed);
        Assert.Null(snap.TopSpeed);
        Assert.Null(snap.SpeedFactorPercent);
        Assert.Null(snap.RawExtrusion);
    }

    [Fact]
    public void LegacyStatus_IdleUnhomed_NoJob()
    {
        var idle = JsonValue.Parse(
            "{\"status\":\"I\",\"coords\":{\"axesHomed\":[0,0,0]," +
            "\"xyz\":[0.0,0.0,0.0],\"machine\":[0.0,0.0,0.0],\"extr\":[0.0]},\"seq\":0}");
        var snap = ObjectModelParser.FromRrStatus(idle, null);

        Assert.Equal(MachineStatus.Idle, snap.Status);
        Assert.False(snap.AllHomed);
        Assert.Null(snap.Job);
    }

    // ---- AUD-SAF-003: non-finite numbers must fail closed on every TFM ----------

    [Theory]
    [InlineData("{\"v\":1e999}")]
    [InlineData("{\"v\":-1e999}")]
    [InlineData("[1e400]")]
    public void NonFiniteNumbers_AreRejected(string json)
        // .NET Core parses exponent overflow to +/-Infinity where .NET Framework
        // fails the parse; both TFMs must refuse, or a malformed rr_model response
        // could hand the Cartesian envelope gate an unbounded M208 window
        => Assert.Throws<FormatException>(() => JsonValue.Parse(json));

    [Fact]
    public void ExponentOverflowInsideAxisLimits_RejectsTheResponse()
    {
        const string axes = "[{\"letter\":\"X\",\"machinePosition\":0,\"homed\":true," +
                            "\"min\":-1e999,\"max\":1e999}]";
        Assert.Throws<FormatException>(() => JsonValue.Parse(axes));
    }

    [Theory]
    [InlineData("I", MachineStatus.Idle)]
    [InlineData("P", MachineStatus.Processing)]
    [InlineData("S", MachineStatus.Paused)]
    [InlineData("D", MachineStatus.Pausing)]
    [InlineData("R", MachineStatus.Resuming)]
    [InlineData("H", MachineStatus.Halted)]
    [InlineData("M", MachineStatus.Simulating)]
    [InlineData("B", MachineStatus.Busy)]
    [InlineData("X", MachineStatus.Unknown)]
    public void LegacyStatusLetters_Map(string letter, MachineStatus expected)
    {
        Assert.Equal(expected, ObjectModelParser.ParseStatusLetter(letter));
    }
}
