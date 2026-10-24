using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginSettingsSchemaRebindTests
{
    [Fact]
    public void RepeatedSchemaRebindKeepsInvalidRawUnsetAndInitialComparison()
    {
        SettingsSchema first = Schema("1", SettingKind.Integer);
        PluginSettingsDraft draft = new(first, SettingScope.FilterRule, null, applyDefaults: false);
        draft.SetInitialText("value", "2");
        draft.Set("value", "9223372036854775808");
        draft.Set("optional", "retained while unset", false);
        long generation = draft.EditGeneration;
        foreach (string version in new[] { "2", "3", "4" })
        {
            draft = draft.RebindSchema(Schema(version, SettingKind.Integer));
            Assert.Equal(version, draft.Schema.Version);
            Assert.Equal("9223372036854775808", draft.Text("value"));
            Assert.Equal("retained while unset", draft.Text("optional"));
            Assert.False(draft.IsSet("optional"));
            Assert.Equal(generation, draft.EditGeneration);
            Assert.True(draft.HasDiscardableChanges);
            _ = draft.Snapshot(out var errors);
            Assert.Contains(errors, error => error.FieldKey == "value" && error.Code == "integer");
        }
        draft.Set("value", "2"); draft.Set("optional", "", false);
        Assert.False(draft.HasDiscardableChanges);
    }

    [Fact]
    public void RebindPreservesRemovedValuesAndRequiresTypeCorrectionWithoutMakingRenderingDirty()
    {
        PluginSettingsDraft draft = new(Schema("1", SettingKind.Integer), SettingScope.FilterRule,
            new("1", new[] { new SettingValue("value", SettingKind.Integer, Integer: 3), new SettingValue("optional", SettingKind.Text, Text: "keep") }), applyDefaults: false);
        SettingsSchema changed = new("2", new[] { new SettingDefinition("value", SettingScope.FilterRule, SettingKind.Text, "New text"),
            new SettingDefinition("added", SettingScope.FilterRule, SettingKind.Boolean, "Added", Default: new("added", SettingKind.Boolean, Boolean: true)) });
        PluginSettingsDraft rebound = draft.RebindSchema(changed);
        Assert.False(rebound.Dirty); Assert.False(rebound.HasDiscardableChanges);
        Assert.Equal("3", rebound.Text("value")); Assert.Equal("keep", rebound.UnmappedValues["optional"]);
        Assert.False(rebound.IsSet("added")); // Filter defaults stay implicit until worker execution.
        _ = rebound.Snapshot(out var errors);
        Assert.Contains(errors, error => error.Code == "removed_setting");
        Assert.Contains(errors, error => error.FieldKey == "value" && error.Code == "incompatible_setting");
        rebound.AcknowledgeRemovedFields(); rebound.Set("value", "3");
        _ = rebound.Snapshot(out errors);
        Assert.Empty(errors); Assert.True(rebound.HasDiscardableChanges);
        Assert.Equal("keep", draft.Text("optional")); // The prior draft remains untouched.
    }

    [Fact]
    public void AcknowledgedRemovalDoesNotReintroduceAnOldTypeMarkerForAnUnsetNewField()
    {
        PluginSettingsDraft draft = new(Schema("1", SettingKind.Integer), SettingScope.FilterRule,
            new("1", new[] { new SettingValue("value", SettingKind.Integer, Integer: 3) }), applyDefaults: false);
        draft = draft.RebindSchema(Schema("2", SettingKind.Text));
        draft = draft.RebindSchema(new("3", Array.Empty<SettingDefinition>()));
        draft.AcknowledgeRemovedFields();
        draft = draft.RebindSchema(Schema("4", SettingKind.Text));
        Assert.False(draft.IsSet("value"));
        Assert.Equal("", draft.Text("value"));
        _ = draft.Snapshot(out var errors);
        Assert.DoesNotContain(errors, error => error.Code == "incompatible_setting");
    }

    private static SettingsSchema Schema(string version, SettingKind kind) => new(version, new[]
    {
        new SettingDefinition("value", SettingScope.FilterRule, kind, "Value " + version),
        new SettingDefinition("optional", SettingScope.FilterRule, SettingKind.Text, "Optional")
    });
}
