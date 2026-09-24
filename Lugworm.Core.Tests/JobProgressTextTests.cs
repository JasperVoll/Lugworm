using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Lugworm.Core.Analysis;
using Lugworm.Core.Duet;
using Lugworm.Core.Extrusion;
using Lugworm.Core.Gcode;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>5.0.0: the Duet Console shows progress as "42%  layer 12/40". The total comes
/// from the job's own "; layer N/M" markers, else the firmware's job.file.numLayers, and it
/// is assigned on every poll so one job's total can never stick to the next.</summary>
public class JobProgressTextTests
{
    static readonly DateTime T0 = new DateTime(2026, 9, 23, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0.42, null, null, "42%")]
    [InlineData(0.42, 12, null, "42%  layer 12")]
    [InlineData(0.42, 12, 40, "42%  layer 12/40")]
    [InlineData(0.42, 41, 40, "42%  layer 41")]       // a total below the layer is not shown
    [InlineData(0.0, 1, 40, "0%  layer 1/40")]
    [InlineData(1.0, 40, 40, "100%  layer 40/40")]
    [InlineData(1.7, null, null, "100%")]             // clamped
    [InlineData(double.NaN, null, null, "0%")]
    public void ProgressText_Formats(double progress, int? layer, int? total, string expected)
        => Assert.Equal(expected, PrintConsoleState.ProgressText(progress, layer, total));

    [Fact]
    public void ProgressText_IsCultureInvariant()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("43%  layer 1234/5678", PrintConsoleState.ProgressText(0.4256, 1234, 5678));
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    static GcodeConfig Config() => new()
    {
        CoordinateFrame = GcodeCoordinateFrame.Identity,
        LayerHeight = 1,
        Width = 1,
        RamDiameter = 10,
        PressureCoeff = 1,
        FirstLayerSpeedFactor = 1,
    };

    static IReadOnlyList<IReadOnlyList<Vec3>> Square(double z) => new IReadOnlyList<Vec3>[]
    {
        new Vec3[] { new(0, 0, z), new(10, 0, z), new(10, 10, z), new(0, 10, z), new(0, 0, z) },
    };

    [Fact]
    public void LayerMarkers_ReadTheGeneratorsOwnTotal()
    {
        var result = GcodeBuilder.Build(Enumerable.Range(1, 7).Select(z => Square(z)).ToArray(), Config());
        Assert.Contains("; layer 1/7", result.Lines);
        Assert.Equal(7, GcodeLayerMarkers.TotalLayers(result.Lines));
    }

    [Fact]
    public void LayerMarkers_IgnoreLookalikesAndForeignFiles()
    {
        Assert.Null(GcodeLayerMarkers.TotalLayers(new[] { "G28", "; layer height 3", ";LAYER:4", "G1 X1" }));
        Assert.Null(GcodeLayerMarkers.TotalLayers(Array.Empty<string>()));
        Assert.Null(GcodeLayerMarkers.TotalLayers(new[] { "; layer 5/3" }));   // N > M is not a marker
        // a restart file starts later but keeps the original total
        Assert.Equal(40, GcodeLayerMarkers.TotalLayers(new[] { "; header", "; layer height 3", "G1 Z20", "; layer 21/40" }));
        Assert.Equal(40, GcodeLayerMarkers.TotalLayers(new[] { "  ;layer 2/40  " }));
    }

    [Fact]
    public void ObjectModel_ReadsTheFirmwareLayerCount()
    {
        var state = JsonValue.Parse("{\"status\":\"processing\"}");
        var axes = JsonValue.Parse("[{\"letter\":\"X\",\"homed\":true}]");
        ObjectModelSnapshot Parse(string file) => ObjectModelParser.FromRrModel(state, axes, JsonValue.Parse(
            "{\"file\":{\"fileName\":\"0:/gcodes/vase.gcode\",\"size\":2000" + file + "},\"filePosition\":500,\"layer\":7}"));

        Assert.Equal(40, Parse(",\"numLayers\":40").Job!.FileLayerCount);
        Assert.Null(Parse(",\"numLayers\":0").Job!.FileLayerCount);    // RRF's "unknown"
        Assert.Null(Parse("").Job!.FileLayerCount);
    }

    static ObjectModelSnapshot Printing(int layer, int? firmwareTotal = null) => new()
    {
        Status = MachineStatus.Processing,
        RawStatus = "processing",
        TakenUtc = T0,
        AllHomed = true,
        Job = new JobState
        {
            FileName = "0:/gcodes/print.gcode",
            FractionPrinted = 0.5,
            Layer = layer,
            FileLayerCount = firmwareTotal,
        },
        Axes = new[]
        {
            new AxisState { Letter = "X", MachinePosition = 1, Homed = true },
            new AxisState { Letter = "Y", MachinePosition = 2, Homed = true },
            new AxisState { Letter = "Z", MachinePosition = 3, Homed = true },
        },
    };

    static ConsoleFacts Facts(ObjectModelSnapshot? snap, JobSnapshot? active) => new()
    {
        Connection = ConsoleConnection.Connected,
        Snapshot = snap,
        Log = Array.Empty<string>(),
        ArmConfigurationKnown = true,
        JobsObserved = true,
        ActiveJob = active,
    };

    [Fact]
    public void Reducer_TotalComesFromTheJobAndNeverSticksToTheNextOne()
    {
        var generated = GcodeBuilder.Build(Enumerable.Range(1, 40).Select(z => Square(z)).ToArray(), Config());
        var jobA = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes(string.Join("\n", generated.Lines)),
                                         "0:/gcodes/print.gcode", JobSource.ConsoleInput, T0);
        var s = PrintConsoleState.Empty("192.168.1.10");

        // job A: the markers win over a firmware count that disagrees
        s = PrintConsoleReducer.Reduce(s, Facts(Printing(12, firmwareTotal: 41), jobA));
        Assert.Equal(40, s.LayerCount);
        Assert.Equal("50%  layer 12/40", PrintConsoleState.ProgressText(s.Progress, s.Layer, s.LayerCount));

        // job B started on the machine's own screen: no bytes, no markers, no firmware count
        s = PrintConsoleReducer.Reduce(s, Facts(Printing(3), JobSnapshot.External("0:/gcodes/other.gcode", T0)));
        Assert.Null(s.LayerCount);
        Assert.Equal("50%  layer 3", PrintConsoleState.ProgressText(s.Progress, s.Layer, s.LayerCount));

        // the same external job, once the firmware reports its count
        s = PrintConsoleReducer.Reduce(s, Facts(Printing(3, firmwareTotal: 18), JobSnapshot.External("0:/gcodes/other.gcode", T0)));
        Assert.Equal(18, s.LayerCount);

        // the job ends: no job, no total
        var idle = Printing(3);
        idle.Status = MachineStatus.Idle;
        idle.Job = null;
        s = PrintConsoleReducer.Reduce(s, Facts(idle, null));
        Assert.Null(s.LayerCount);
        Assert.Null(s.Layer);
    }

    [Fact]
    public void JobSnapshot_LayerTotalIsScannedOnceAndMissingForExternalJobs()
    {
        var job = JobSnapshot.FromBytes(Encoding.UTF8.GetBytes("G28\n; layer 1/3\nG1 X1\n; layer 2/3\n"),
                                        null, JobSource.ConsoleInput, T0);
        Assert.Equal(3, job.LayerTotal());
        Assert.Equal(3, job.LayerTotal());
        Assert.Null(JobSnapshot.External(null, T0).LayerTotal());
    }
}
