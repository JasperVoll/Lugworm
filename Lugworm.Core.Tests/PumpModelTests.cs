using System;
using System.Linq;
using Lugworm.Core.Extrusion;
using Xunit;

namespace Lugworm.Core.Tests;

public class PumpModelTests
{
    static PumpCalibration DummyCal() => PumpCalibration.ParseCsv(new[]
    {
        "# dummy calibration for tests",
        "2.0,1.0",
        "4.0,2.0",
        "6.0,3.0",
        "8.0,4.0",
    });

    // ---- flow math against hand-calculated references ----

    [Fact]
    public void RequiredFlow_HandCalculated()
    {
        // 10mm x 30mm layer at 50 mm/s = 15000 mm^3/s = 0.9 l/min
        Assert.Equal(0.9, PumpModel.RequiredFlowLpm(10, 30, 50), 9);
        // 15mm x 40mm at 100 mm/s = 60000 mm^3/s = 3.6 l/min
        Assert.Equal(3.6, PumpModel.RequiredFlowLpm(15, 40, 100), 9);
        // slip factor scales linearly
        Assert.Equal(0.99, PumpModel.RequiredFlowLpm(10, 30, 50, 1.1), 9);
    }

    [Fact]
    public void Plan_InterpolatesSettingLinearly()
    {
        // Q = 2.5 l/min sits halfway between (4V -> 2) and (6V -> 3) => setting 5.0
        // layer 10x25 at 100/60*... choose h=10, w=25, v=10 -> 2500 mm3/s = 0.15 l/min. Wrong.
        // Directly: need Q=2.5 => h*w*v = 2.5e6/60 = 41666.7 => h=10, w=41.6667, v=100
        var inp = new PumpPlanInput { LayerHeight = 10, LayerWidth = 41.6666666667, PrintSpeed = 100 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.True(plan.Ok);
        Assert.Equal(2.5, plan.QRequiredLpm, 6);
        Assert.NotNull(plan.Setting);
        Assert.Equal(5.0, plan.Setting!.Value, 6);
    }

    [Fact]
    public void Plan_ExactCalibrationPoint()
    {
        // Q = 1.0 l/min == first table point -> setting 2.0
        var inp = new PumpPlanInput { LayerHeight = 10, LayerWidth = 33.3333333333, PrintSpeed = 50 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.Equal(1.0, plan.QRequiredLpm, 6);
        Assert.True(plan.Ok);
        Assert.Equal(2.0, plan.Setting!.Value, 5);
    }

    // ---- out-of-range flow must warn, never extrapolate ----

    [Fact]
    public void Plan_FlowAboveCalibratedRange_WarnsNoSetting()
    {
        var inp = new PumpPlanInput { LayerHeight = 20, LayerWidth = 50, PrintSpeed = 200 };  // 12 l/min
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.False(plan.Ok);
        Assert.Null(plan.Setting);
        Assert.Contains(plan.Warnings, w => w.Contains("OUTSIDE the calibrated range"));
    }

    [Fact]
    public void Plan_FlowBelowCalibratedRange_WarnsNoSetting()
    {
        var inp = new PumpPlanInput { LayerHeight = 2, LayerWidth = 4, PrintSpeed = 10 };  // 0.0048 l/min
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.False(plan.Ok);
        Assert.Null(plan.Setting);
        Assert.Contains(plan.Warnings, w => w.Contains("OUTSIDE the calibrated range"));
    }

    // ---- placeholder / missing calibration must produce NEEDS_INPUT, no numbers ----

    [Fact]
    public void Plan_PlaceholderCalibration_NeedsInputWarning()
    {
        var placeholder = PumpCalibration.ParseCsv(new[]
        {
            "# placeholder",
            "2.0,NEEDS_INPUT",
            "4.0,NEEDS_INPUT",
        });
        Assert.True(placeholder.IsPlaceholder);
        var inp = new PumpPlanInput { LayerHeight = 10, LayerWidth = 40, PrintSpeed = 100 };
        var plan = PumpModel.Plan(inp, placeholder);
        Assert.False(plan.Ok);
        Assert.Null(plan.Setting);
        Assert.Contains(plan.Warnings, w => w.Contains("NEEDS_INPUT"));
        // required flow is still reported (that part needs no calibration)
        Assert.True(plan.QRequiredLpm > 0);
    }

    [Fact]
    public void Plan_NoCalibration_NeedsInputWarning()
    {
        var inp = new PumpPlanInput { LayerHeight = 10, LayerWidth = 40, PrintSpeed = 100 };
        var plan = PumpModel.Plan(inp, null);
        Assert.False(plan.Ok);
        Assert.Contains(plan.Warnings, w => w.Contains("NEEDS_INPUT"));
    }

    // ---- calibration validation ----

    [Fact]
    public void Calibration_NonMonotonicFlow_Rejected()
    {
        var bad = PumpCalibration.ParseCsv(new[] { "2.0,1.0", "4.0,3.0", "6.0,2.0" });
        Assert.Contains(bad.Validate(), p => p.Contains("must increase"));
        var plan = PumpModel.Plan(new PumpPlanInput { LayerHeight = 10, LayerWidth = 40, PrintSpeed = 100 }, bad);
        Assert.False(plan.Ok);
        Assert.Null(plan.Setting);
    }

    [Fact]
    public void Calibration_SinglePoint_Rejected()
    {
        var bad = PumpCalibration.ParseCsv(new[] { "5.0,2.5" });
        Assert.Contains(bad.Validate(), p => p.Contains("at least 2"));
    }

    [Fact]
    public void Calibration_SemicolonAndDecimalComma_Parsed()
    {
        // European Excel export: semicolon delimiter, decimal commas.
        // The old Split(',',';') shredded "2,0;1,5" into 4 tokens -> (2, 0).
        var cal = PumpCalibration.ParseCsv(new[] { "2,0;1,5", "4,0;2,5", "6;3,5" });
        Assert.Equal(3, cal.Points.Count);
        Assert.Equal(2.0, cal.Points[0].Setting, 9);
        Assert.Equal(1.5, cal.Points[0].FlowLpm, 9);
        Assert.Equal(3.5, cal.Points[2].FlowLpm, 9);
        Assert.Empty(cal.Validate());
    }

    [Fact]
    public void Calibration_ParsesCommentsAndSorts()
    {
        var cal = PumpCalibration.ParseCsv(new[]
        {
            "# pump: test", "", "6.0,3.0", "2.0,1.0", "4.0,2.0",
        });
        Assert.False(cal.IsPlaceholder);
        Assert.Equal(3, cal.Points.Count);
        Assert.Equal(2.0, cal.Points[0].Setting, 9);   // sorted ascending
        Assert.Single(cal.Metadata);
        Assert.Empty(cal.Validate());
        Assert.Equal(2.0, cal.FlowForSetting(4.0)!.Value, 9);
        Assert.Null(cal.FlowForSetting(11.0));          // no extrapolation
    }

    // ---- pump-agnostic range (5.0.0: the hard-coded rated range is gone) ----

    [Fact]
    public void Plan_RangeComesOnlyFromTheCalibrationTable()
    {
        // 20mm x 50mm at 200 mm/s = 12 l/min: outside the dummy table (1-4), and that is
        // the only range the model knows
        var inp = new PumpPlanInput { LayerHeight = 20, LayerWidth = 50, PrintSpeed = 200 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.Contains(plan.Warnings, w => w.Contains("OUTSIDE the calibrated range"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("pump's rated"));
    }

    [Fact]
    public void Plan_WithoutCalibration_NamesNoPumpAndNoRatedRange()
    {
        // 0.06 l/min and 12 l/min used to trip the MAI rated range; without a table the
        // model reports the flow and asks for a calibration, nothing else
        foreach (var inp in new[]
        {
            new PumpPlanInput { LayerHeight = 5, LayerWidth = 10, PrintSpeed = 20 },
            new PumpPlanInput { LayerHeight = 20, LayerWidth = 50, PrintSpeed = 200 },
        })
        {
            var plan = PumpModel.Plan(inp, null);
            var text = string.Join("|", plan.Warnings) + "|" + string.Join("|", plan.Summary);
            Assert.DoesNotContain("pump's rated", text);
            Assert.DoesNotContain("MAI", text);
            Assert.DoesNotContain("PICTOR", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(plan.Warnings, w => w.StartsWith("NEEDS_INPUT", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Plan_InsideCalibratedRange_NoWarnings()
    {
        // Q = 2.5 l/min: inside the dummy calibration 1-4
        var inp = new PumpPlanInput { LayerHeight = 10, LayerWidth = 41.6666666667, PrintSpeed = 100 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.Empty(plan.Warnings);
    }

    // ---- sanity warnings ----

    [Fact]
    public void Plan_LayerNarrowerThanNozzle_Warns()
    {
        var inp = new PumpPlanInput { NozzleDiameter = 25, LayerHeight = 10, LayerWidth = 20, PrintSpeed = 30 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.Contains(plan.Warnings, w => w.Contains("smaller than the nozzle"));
    }

    [Fact]
    public void Plan_FirstLayerSetting_Reported()
    {
        // Q = 2.5, first-layer factor 0.6 -> Q_fl = 1.5 -> setting 5.0 and 3.0
        var inp = new PumpPlanInput
        { LayerHeight = 10, LayerWidth = 41.6666666667, PrintSpeed = 100, FirstLayerSpeedFactor = 0.6 };
        var plan = PumpModel.Plan(inp, DummyCal());
        Assert.True(plan.Ok);
        Assert.Equal(1.5, plan.QFirstLayerLpm, 6);
        Assert.Equal(3.0, plan.SettingFirstLayer!.Value, 6);
    }
}
