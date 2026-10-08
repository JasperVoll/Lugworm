using Lugworm.Core.Settings;
using Xunit;

namespace Lugworm.Core.Tests;

public class SettingsApplyRegressionTests
{
    static SettingsInputSource Input(SettingsInputType type, SettingsSourceKind source = SettingsSourceKind.None)
        => new SettingsInputSource("value")
        {
            HasSpec = true, Type = type, Kind = source,
            SourceCount = source == SettingsSourceKind.None ? 0 : 1,
        };

    [Theory]
    [InlineData(SettingsSourceKind.None)]
    [InlineData(SettingsSourceKind.Slider)]
    [InlineData(SettingsSourceKind.Panel)]
    public void InvalidNumericText_IsSkippedInsteadOfBecomingZero(SettingsSourceKind source)
    {
        var decision = SettingsPlan.Decide(Input(SettingsInputType.Number, source), SettingsValue.Text("oops"));
        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.NotNull(decision.Reason);
    }

    [Theory]
    [InlineData(SettingsSourceKind.None)]
    [InlineData(SettingsSourceKind.Slider)]
    [InlineData(SettingsSourceKind.ValueList)]
    public void ListForScalar_IsNotTruncated(SettingsSourceKind source)
    {
        var decision = SettingsPlan.Decide(Input(SettingsInputType.Number, source), SettingsValue.Numbers(new[] { 1.0, 2.0 }));
        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
    }

    [Fact]
    public void FractionForIntegerSlider_IsSkippedWithoutChangingItsPrecision()
    {
        var input = Input(SettingsInputType.Number, SettingsSourceKind.Slider);
        input.SourceSliderIsInteger = true;
        var decision = SettingsPlan.Decide(input, SettingsValue.Number(0.125));
        Assert.Equal(SettingsPlanAction.Skip, decision.Action);
        Assert.Equal(-1, decision.SliderDecimals);
    }

    [Fact]
    public void BooleanList_CreatesAPanelContainingEveryValue()
    {
        var input = Input(SettingsInputType.Boolean);
        input.IsList = true;
        Assert.Equal(SettingsObjectKind.Panel,
            SettingsPlan.Decide(input, SettingsValue.Bools(new[] { true, false })).ObjectKind);
    }

    [Fact]
    public void ListWithValueListHints_StillPreservesEveryValue()
    {
        var input = Input(SettingsInputType.Number);
        input.IsList = true;
        input.SpecItems = new[] { new SettingsListItem("One", "1"), new SettingsListItem("Two", "2") };
        Assert.Equal(SettingsObjectKind.Panel,
            SettingsPlan.Decide(input, SettingsValue.Numbers(new[] { 1.0, 2.0 })).ObjectKind);
    }

    [Fact]
    public void TextListWithEmbeddedNewlines_IsNotSplitIntoExtraItems()
    {
        var input = Input(SettingsInputType.Text);
        input.IsList = true;
        Assert.Equal(SettingsPlanAction.Skip,
            SettingsPlan.Decide(input, SettingsValue.Texts(new[] { "first\nsecond", "third" })).Action);
    }

    [Theory]
    [InlineData("1E-05", 5)]
    [InlineData("1.25E-03", 5)]
    [InlineData("1.25E+03", 0)]
    public void ScientificNotation_RetainsSliderPrecision(string text, int decimals)
        => Assert.Equal(decimals, SettingsPlan.DecimalsOf(text));

    [Fact]
    public void MorePrecisionThanASliderSupports_DoesNotSilentlyRound()
    {
        Assert.Equal(SettingsPlanAction.Skip,
            SettingsPlan.Decide(Input(SettingsInputType.Number, SettingsSourceKind.Slider), SettingsValue.Number(0.0000001)).Action);
        Assert.Equal(SettingsObjectKind.Panel,
            SettingsPlan.Decide(Input(SettingsInputType.Number), SettingsValue.Number(0.0000001)).ObjectKind);
    }

    [Fact]
    public void PanelFedByAComponent_IsNeitherCapturedNorUpdated()
    {
        var input = Input(SettingsInputType.Text, SettingsSourceKind.Panel);
        input.SourceHasInputs = true;
        Assert.NotNull(SettingsPlan.CaptureRefusal(input));
        Assert.Equal(SettingsPlanAction.Skip, SettingsPlan.Decide(input, SettingsValue.Text("replacement")).Action);
    }

    [Theory]
    [InlineData("oops")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InvalidToggleValue_IsSkipped(string text)
        => Assert.Equal(SettingsPlanAction.Skip,
            SettingsPlan.Decide(Input(SettingsInputType.Boolean, SettingsSourceKind.Toggle), SettingsValue.Text(text)).Action);

    [Fact]
    public void ListInputFedByASlider_DoesNotLoseItsRemainingValues()
    {
        var input = Input(SettingsInputType.Number, SettingsSourceKind.Slider);
        input.IsList = true;
        Assert.Equal(SettingsPlanAction.Skip,
            SettingsPlan.Decide(input, SettingsValue.Numbers(new[] { 2.0, 3.0 })).Action);
    }

    [Fact]
    public void SharedControl_RequiresEveryRecipientToAgree()
    {
        var value = SettingsValue.Number(2);
        Assert.True(SettingsPlan.SharedSourceAgrees(value, new[] { value, SettingsValue.Number(2) }));
        Assert.False(SettingsPlan.SharedSourceAgrees(value, new[] { value, SettingsValue.Number(3) }));
        Assert.False(SettingsPlan.SharedSourceAgrees(value, new SettingsValue?[] { value, null }));
    }
}
