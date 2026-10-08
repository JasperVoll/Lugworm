using System.Collections.Generic;
using System.Linq;
using Lugworm.Core.Settings;
using Xunit;

namespace Lugworm.Core.Tests;

/// <summary>The classify-and-plan half of applying a settings file (4.4.0, D8),
/// extracted from <c>Lugworm/Ui/InputWiringHelper.cs</c>. These are the rows of
/// section 6 of the 4.0.0 test round that no longer need Rhino to check: 6.3 (an
/// unwired input gets a new object and a slider range is widened, never clamped),
/// 6.4 (an input fed by exactly one slider / toggle / panel / value list is set in
/// place) and above all 6.5 - an input fed by a component, an expression or several
/// sources is LEFT ALONE and named in the report. That last one is the only case here
/// that can destroy a user's work, and it is why this plan is pure.</summary>
public class SettingsPlanTests
{
    static SettingsInputSource Unwired(string name, SettingsInputType type, bool list = false)
        => new SettingsInputSource(name) { SourceCount = 0, HasSpec = true, Type = type, IsList = list };

    static SettingsInputSource FedBy(string name, SettingsSourceKind kind, string label = "a slider")
        => new SettingsInputSource(name)
        { SourceCount = 1, Kind = kind, SourceLabel = label, HasSpec = true, Type = SettingsInputType.Number };

    static IReadOnlyList<SettingsListItem> Items(params (string label, string value)[] items)
        => items.Select(i => new SettingsListItem(i.label, i.value)).ToList();

    static IReadOnlyDictionary<string, SettingsValue> File(params (string name, SettingsValue value)[] values)
        => values.ToDictionary(v => v.name, v => v.value);

    // ---- 6.3: nothing wired -> a new object carrying the file value ----

    [Fact]
    public void UnwiredNumberInput_GetsANewSlider()
    {
        var decision = SettingsPlan.Decide(Unwired("layerHeight", SettingsInputType.Number),
                                           SettingsValue.Number(0.8));

        Assert.Equal(SettingsPlanAction.Create, decision.Action);
        Assert.Equal(SettingsObjectKind.Slider, decision.ObjectKind);
        Assert.Null(decision.Note);
    }

    [Fact]
    public void UnwiredInputs_GetTheObjectTheirTypeCallsFor()
    {
        Assert.Equal(SettingsObjectKind.Toggle,
            SettingsPlan.Decide(Unwired("spiral", SettingsInputType.Boolean), null).ObjectKind);
        Assert.Equal(SettingsObjectKind.Panel,
            SettingsPlan.Decide(Unwired("header", SettingsInputType.Text), null).ObjectKind);
        // a LIST of numbers is one item per line, which is a panel and never a slider
        Assert.Equal(SettingsObjectKind.Panel,
            SettingsPlan.Decide(Unwired("heights", SettingsInputType.Number, list: true), null).ObjectKind);
    }

    [Fact]
    public void UnwiredValueListInput_GetsAValueListWithTheFileValueSelected()
    {
        var input = Unwired("seam", SettingsInputType.Text);
        input.SpecItems = Items(("Nearest", "\"nearest\""), ("Aligned", "\"aligned\""));

        var decision = SettingsPlan.Decide(input, SettingsValue.Text("aligned"));

        Assert.Equal(SettingsPlanAction.Create, decision.Action);
        Assert.Equal(SettingsObjectKind.ValueList, decision.ObjectKind);
        Assert.Equal(1, decision.ValueListIndex);
    }

    [Fact]
    public void UnwiredValueListInput_WithAValueTheListLacks_FallsBackToAPanelAndSaysSo()
    {
        var input = Unwired("seam", SettingsInputType.Text);
        input.SpecItems = Items(("Nearest", "\"nearest\""), ("Aligned", "\"aligned\""));

        var decision = SettingsPlan.Decide(input, SettingsValue.Text("spiral"));

        Assert.Equal(SettingsPlanAction.Create, decision.Action);
        Assert.Equal(SettingsObjectKind.Panel, decision.ObjectKind);
        Assert.Equal("seam: 'spiral' is not one of its list items - a panel carries it instead",
                     decision.Note);
    }

    [Fact]
    public void UnwiredNumberInput_WithAValueNoSliderCanHold_FallsBackToAPanel()
    {
        var decision = SettingsPlan.Decide(Unwired("reach", SettingsInputType.Number),
                                           SettingsValue.Number(1e13));

        Assert.Equal(SettingsPlanAction.Create, decision.Action);
        Assert.Equal(SettingsObjectKind.Panel, decision.ObjectKind);
        Assert.Contains("outside a slider's range - a panel carries it instead", decision.Note);
    }

    [Fact]
    public void GeometryInputs_AreNeverAutoFed()
    {
        var decision = SettingsPlan.Decide(Unwired("mesh", SettingsInputType.Other), null);

        Assert.Equal(SettingsPlanAction.None, decision.Action);
        Assert.Equal(SettingsObjectKind.None, decision.ObjectKind);
        Assert.Equal("geometry inputs cannot be auto-fed", decision.Reason);
    }

    [Fact]
    public void AnInputThisBuildNoLongerDeclares_IsLeftAlone()
    {
        var input = new SettingsInputSource("retired") { SourceCount = 0, HasSpec = false };

        var decision = SettingsPlan.Decide(input, SettingsValue.Number(1));

        Assert.Equal(SettingsPlanAction.None, decision.Action);
    }

    // ---- 6.4: exactly one slider / toggle / panel / value list -> set in place ----

    [Fact]
    public void InputFedByExactlyOneSlider_IsUpdatedInPlace()
    {
        var decision = SettingsPlan.Decide(FedBy("layerHeight", SettingsSourceKind.Slider),
                                           SettingsValue.Number(0.8));

        Assert.Equal(SettingsPlanAction.Update, decision.Action);
        Assert.Equal(SettingsObjectKind.Slider, decision.ObjectKind);
        Assert.Null(decision.Reason);
        Assert.Null(decision.Note);
    }

    [Fact]
    public void TheOtherThreeSettingsObjects_AreAlsoUpdatedInPlace()
    {
        Assert.Equal(SettingsPlanAction.Update,
            SettingsPlan.Decide(FedBy("spiral", SettingsSourceKind.Toggle), SettingsValue.Bool(true)).Action);
        var header = FedBy("header", SettingsSourceKind.Panel);
        header.Type = SettingsInputType.Text;
        Assert.Equal(SettingsPlanAction.Update,
            SettingsPlan.Decide(header, SettingsValue.Text("G28")).Action);

        var list = FedBy("seam", SettingsSourceKind.ValueList);
        list.SourceItems = Items(("Nearest", "0"), ("Aligned", "1"));
        var decision = SettingsPlan.Decide(list, SettingsValue.Number(1));
        Assert.Equal(SettingsPlanAction.Update, decision.Action);
        Assert.Equal(1, decision.ValueListIndex);   // "1" matches 1.0 numerically
    }

    [Fact]
    public void AFileValueWidensTheSlidersPrecision_ButNeverAnIntegerSliders()
    {
        var floating = FedBy("layerHeight", SettingsSourceKind.Slider);
        floating.SourceSliderDecimals = 2;
        Assert.Equal(3, SettingsPlan.Decide(floating, SettingsValue.Number(0.125)).SliderDecimals);

        // the file asks for less precision than the operator's slider already shows
        Assert.Equal(2, SettingsPlan.Decide(floating, SettingsValue.Number(0.5)).SliderDecimals);

        var integer = FedBy("walls", SettingsSourceKind.Slider);
        integer.SourceSliderIsInteger = true;
        integer.SourceSliderDecimals = 0;
        Assert.Equal(-1, SettingsPlan.Decide(integer, SettingsValue.Number(0.125)).SliderDecimals);
    }

    [Fact]
    public void ASliderThatCannotHoldTheValue_IsLeftAloneAndSaysSo()
    {
        var decision = SettingsPlan.Decide(FedBy("reach", SettingsSourceKind.Slider),
                                           SettingsValue.Number(1e13));

        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.Equal("reach: 10000000000000.0 is outside a slider's range - the slider was left alone",
                     decision.Note);
    }

    [Fact]
    public void AValueListWithoutAMatchingItem_IsLeftAloneAndSaysSo()
    {
        var input = FedBy("seam", SettingsSourceKind.ValueList);
        input.Type = SettingsInputType.Text;
        input.SourceItems = Items(("Nearest", "\"nearest\""), ("Aligned", "\"aligned\""));

        var decision = SettingsPlan.Decide(input, SettingsValue.Text("spiral"));

        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.Equal("seam: 'spiral' is not one of the value list's items - it was left alone",
                     decision.Note);
    }

    // ---- 6.5: anything else upstream is never overwritten ----

    [Fact]
    public void InputFedByAComponent_IsSkippedBecauseApplyingWouldOverwriteIt()
    {
        var decision = SettingsPlan.Decide(
            FedBy("gcode", SettingsSourceKind.Other, "G-code Generator"),
            SettingsValue.Text("G1 X0"));

        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.Equal("fed by 'G-code Generator' - applying the file would overwrite it",
                     decision.Reason);
    }

    [Fact]
    public void AnUnnamedUpstreamObject_IsStillNamedInTheReason()
    {
        var decision = SettingsPlan.Decide(FedBy("gcode", SettingsSourceKind.Other, ""),
                                           SettingsValue.Text("G1 X0"));

        Assert.Equal("fed by 'a component' - applying the file would overwrite it", decision.Reason);
    }

    [Fact]
    public void InputFedBySeveralSources_IsSkippedWhateverThoseSourcesAre()
    {
        var input = new SettingsInputSource("layerHeight")
        { SourceCount = 2, Kind = SettingsSourceKind.Slider, HasSpec = true, Type = SettingsInputType.Number };

        var decision = SettingsPlan.Decide(input, SettingsValue.Number(0.8));

        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.Equal("fed by 2 sources - applying the file would overwrite them", decision.Reason);
    }

    [Fact]
    public void AWiredInputTheFileDoesNotMention_IsLeftAloneSilently()
    {
        // the plain "inputs" button: it has never touched anything already wired
        var decision = SettingsPlan.Decide(FedBy("layerHeight", SettingsSourceKind.Slider), null);

        Assert.Equal(SettingsPlanAction.None, decision.Action);
        Assert.Equal("already wired", decision.Reason);
    }

    // ---- the whole pass ----

    [Fact]
    public void OnePassDecidesEveryInputInOrder()
    {
        var inputs = new List<SettingsInputSource>
        {
            Unwired("layerHeight", SettingsInputType.Number),
            FedBy("speed", SettingsSourceKind.Slider),
            FedBy("gcode", SettingsSourceKind.Other, "G-code Generator"),
            Unwired("mesh", SettingsInputType.Other),
        };

        var plan = SettingsPlan.Plan(inputs, File(
            ("layerHeight", SettingsValue.Number(0.8)),
            ("speed", SettingsValue.Number(30)),
            ("gcode", SettingsValue.Text("G1 X0"))));

        Assert.Equal(new[] { "layerHeight", "speed", "gcode", "mesh" }, plan.Select(d => d.Name));
        Assert.Equal(new[]
        {
            SettingsPlanAction.Create, SettingsPlanAction.Update,
            SettingsPlanAction.Skip, SettingsPlanAction.None,
        }, plan.Select(d => d.Action));
    }

    [Fact]
    public void WithoutAFileEveryWiredInputIsLeftAloneAndEveryFreeOneIsBuiltFromTheDefaults()
    {
        var inputs = new List<SettingsInputSource>
        {
            Unwired("layerHeight", SettingsInputType.Number),
            FedBy("speed", SettingsSourceKind.Slider),
            FedBy("gcode", SettingsSourceKind.Other, "G-code Generator"),
        };

        var plan = SettingsPlan.Plan(inputs);

        Assert.Equal(SettingsPlanAction.Create, plan[0].Action);
        Assert.Equal(SettingsPlanAction.None, plan[1].Action);
        Assert.Equal(SettingsPlanAction.None, plan[2].Action);   // nothing is reported either
    }

    // ---- the capture side asks the same question ----

    [Fact]
    public void CaptureTakesAnUnwiredInputAndTheFourSettingsObjects()
    {
        Assert.Null(SettingsPlan.CaptureRefusal(Unwired("layerHeight", SettingsInputType.Number)));
        Assert.Null(SettingsPlan.CaptureRefusal(FedBy("a", SettingsSourceKind.Slider)));
        Assert.Null(SettingsPlan.CaptureRefusal(FedBy("b", SettingsSourceKind.Toggle)));
        Assert.Null(SettingsPlan.CaptureRefusal(FedBy("c", SettingsSourceKind.Panel)));
        Assert.Null(SettingsPlan.CaptureRefusal(FedBy("d", SettingsSourceKind.ValueList)));
    }

    [Fact]
    public void CaptureRefusesDataTheDefinitionProduces()
    {
        // the 2.4 MB "settings" file: Interlayer Time's whole G-code list, captured
        // because the VALUE was text and nobody asked where it came from
        Assert.Equal("wired from 'G-code Generator' - data, not a setting",
            SettingsPlan.CaptureRefusal(FedBy("gcode", SettingsSourceKind.Other,
                                              "G-code Generator")));

        var several = new SettingsInputSource("layerHeight")
        { SourceCount = 3, Kind = SettingsSourceKind.Slider };
        Assert.Equal("several sources", SettingsPlan.CaptureRefusal(several));
    }
}
