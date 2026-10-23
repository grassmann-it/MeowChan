using System.Globalization;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;
[Collection("Localization")]
public sealed class RuleDraftStateTests
{
    [Fact]
    public void PartialPasteCanonicalizesValidTokensAndPreservesInvalidTokenSpelling()
    {
        ExtensionTagDraft input = new() { Rest = ".JPG, jpg, PNG, image/jpeg,*.jpg,..jpg,C:/a.jpg," + new string('a', 33) };
        input.Commit();
        Assert.Equal(new[] { ".jpg", ".png" }, input.Tags);
        Assert.Equal(" image/jpeg,*.jpg,..jpg,C:/a.jpg," + new string('a', 33), input.Rest);
        Assert.Throws<ArgumentException>(() => input.Build(MediaTypeSelectionMode.Selected));
        Assert.Equal(MediaTypeSelectionMode.All, input.Build(MediaTypeSelectionMode.All).Mode);
        Assert.Equal(MediaTypeSelectionMode.Inherit, input.Build(MediaTypeSelectionMode.Inherit).Mode);
        input.Rest = "webm"; Assert.Equal(new[] { ".jpg", ".png", ".webm" }, input.Build(MediaTypeSelectionMode.Selected).Extensions);
        Assert.Equal("webm", input.Rest); // Save validation itself never destructively commits the buffer.
    }
    [Fact]
    public void EmptySelectionIsNotAllAndClonesOwnTheirRawBuffers()
    {
        ExtensionTagDraft input = new(); Assert.Empty(input.Build(MediaTypeSelectionMode.Selected).Extensions);
        input.Tags.Add(".png"); input.Rest = " image/jpeg "; var copy = input.Clone();
        input.Tags.Clear(); input.Rest = "jpg";
        Assert.Equal(new[] { ".png" }, copy.Tags); Assert.Equal(" image/jpeg ", copy.Rest);
    }
    [Fact]
    public void RuleDiscardComparisonIsIndependentOfStickyDirtyAndRetainsInactiveValues()
    {
        var draft = new RulesPage.RuleDraft(MediaRuleScope.ForThread("unknown", "board", "123"), MediaRuleSet.Default);
        Assert.False(draft.HasDiscardableChanges);
        draft.Enabled = !draft.Enabled; draft.Dirty = true; Assert.True(draft.HasDiscardableChanges);
        draft.Enabled = !draft.Enabled; Assert.False(draft.HasDiscardableChanges); Assert.True(draft.Dirty);
        draft.TypesMode = MediaTypeSelectionMode.All; draft.ExtensionInput.Rest = "image/jpeg";
        draft.TypesMode = MediaTypeSelectionMode.Inherit;
        Assert.True(draft.HasDiscardableChanges); // Discard would lose the temporarily hidden raw text.
    }
    [Fact]
    public void SizeOnlyCopyAndUndoPreserveEveryOtherRawFieldAndBaseline()
    {
        var draft = new RulesPage.RuleDraft(MediaRuleScope.ForBoard("test", "a"), MediaRuleSet.Default) { Enabled = false, Dirty = true, Stale = true };
        draft.TypesMode = MediaTypeSelectionMode.Selected; draft.ExtensionInput.Tags.Add(".png"); draft.ExtensionInput.Rest = " image/jpeg ";
        draft.SizeMode = MediaSizeLimitMode.Limited; draft.Megabytes = "1,5"; draft.NumberCulture = CultureInfo.GetCultureInfo("de");
        SettingsSchema schema = new("1", new[] { new SettingDefinition("n", SettingScope.FilterRule, SettingKind.Integer, "N") });
        draft.FilterParameters = new(schema, SettingScope.FilterRule, null); draft.FilterParameters.Set("n", "1.5");
        var before = draft.DeepClone();
        RulesPage.ReplaceTemplateGroups(draft, new(Guid.NewGuid(), "Size", sizeLimit: new(MediaSizeLimitMode.Limited, 20)));
        Assert.Equal(" image/jpeg ", draft.ExtensionInput.Rest); Assert.Equal("1.5", draft.FilterParameters.Text("n"));
        Assert.False(draft.Enabled); Assert.Same(before.Baseline, draft.Baseline); Assert.True(draft.Stale);
        draft.Restore(before);
        Assert.Equal("1,5", draft.Megabytes); Assert.Equal("de", draft.NumberCulture.Name);
        Assert.Equal(" image/jpeg ", draft.ExtensionInput.Rest); Assert.Equal("1.5", draft.FilterParameters!.Text("n"));
        draft.FilterParameters.Set("n", "2"); Assert.Equal("1.5", before.FilterParameters!.Text("n"));
    }
    [Fact]
    public void MediaOnlyCopyReplacesInvalidRestWithoutEnablingOrSavingTheRule()
    {
        var draft = new RulesPage.RuleDraft(MediaRuleScope.ForBoard("test", "a"), MediaRuleSet.Default) { Enabled = false };
        draft.ExtensionInput.Rest = "image/jpeg"; draft.Megabytes = "bad size";
        RulesPage.ReplaceTemplateGroups(draft, new(Guid.NewGuid(), "No media", new(MediaTypeSelectionMode.Selected)));
        Assert.Empty(draft.ExtensionInput.Tags); Assert.Equal("", draft.ExtensionInput.Rest); Assert.Equal("bad size", draft.Megabytes);
        Assert.Equal(MediaTypeSelectionMode.Selected, draft.TypesMode); Assert.False(draft.Enabled);
        Assert.Same(MediaRuleSet.Default, draft.Baseline); Assert.True(draft.HasDiscardableChanges);
    }
    [Fact]
    public void PluginRawComparisonIncludesUnsetDefaultsUnknownsAndDeepClone()
    {
        SettingsSchema schema = new("1", new[] {
            new SettingDefinition("flag", SettingScope.Plugin, SettingKind.Boolean, "Flag", Default: new("flag", SettingKind.Boolean, Boolean: false)),
            new SettingDefinition("n", SettingScope.Plugin, SettingKind.Integer, "N") });
        PluginSettingsDraft draft = new(schema, SettingScope.Plugin, null);
        Assert.False(draft.HasDiscardableChanges);
        draft.Set("flag", "true"); draft.Set("flag", "false"); Assert.False(draft.HasDiscardableChanges); Assert.True(draft.Dirty);
        draft.Set("n", "1.5"); Assert.True(draft.HasDiscardableChanges);
        var clone = draft.DeepClone(); draft.Set("n", "", false); Assert.False(draft.HasDiscardableChanges);
        Assert.Equal("1.5", clone.Text("n")); Assert.True(clone.HasDiscardableChanges);
        clone.SetInitialText("removed", " raw "); Assert.Equal(" raw ", clone.UnmappedValues["removed"]);
        clone.AcknowledgeRemovedFields(); Assert.True(clone.HasDiscardableChanges);
        draft.Set("flag", "false", false); Assert.True(draft.HasDiscardableChanges); // Unset != explicit false.
    }
    [Fact]
    public void FilterRuleCanPreserveAbsentDefaultAndInitialInvalidRawText()
    {
        SettingsSchema schema = new("1", new[] { new SettingDefinition("n", SettingScope.FilterRule, SettingKind.Integer, "N", Default: new("n", SettingKind.Integer, Integer: 0)) });
        PluginSettingsDraft draft = new(schema, SettingScope.FilterRule, new("1", Array.Empty<SettingValue>()), applyDefaults: false);
        Assert.False(draft.IsSet("n")); Assert.False(draft.HasDiscardableChanges);
        draft.SetInitialText("n", "1.5"); Assert.False(draft.HasDiscardableChanges);
        draft.Snapshot(out var errors); Assert.NotEmpty(errors); Assert.Equal("1.5", draft.Text("n"));
        draft.Set("n", "0"); Assert.True(draft.HasDiscardableChanges);
        draft.Set("n", "1.5"); Assert.False(draft.HasDiscardableChanges);
    }
}
