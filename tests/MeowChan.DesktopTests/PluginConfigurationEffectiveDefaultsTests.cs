using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginConfigurationEffectiveDefaultsTests
{
    [Fact]
    public void UnsetPluginAndSourceDefaultsAreEffectiveWithoutChangingDrafts()
    {
        PluginConfigurationDraft draft = Draft();
        draft.Plugin.Set("number", "-", false);
        draft.Plugin.Set("noDefault", "retained unset text", false);
        draft.Sources["source"].Set("number", "9223372036854775808", false);
        long generation = draft.EditGeneration;
        bool dirty = draft.Dirty, discardable = draft.HasDiscardableChanges;

        PluginConfiguration configuration = draft.Configuration(out var errors);

        Assert.Empty(errors);
        Assert.Equal(2L, Assert.Single(configuration.PluginSettings.Values, value => value.Key == "number").Integer);
        Assert.Equal(2L, Assert.Single(configuration.SourceSettings["source"].Values, value => value.Key == "number").Integer);
        Assert.DoesNotContain(configuration.PluginSettings.Values, value => value.Key == "noDefault");
        Assert.False(draft.Plugin.IsSet("number"));
        Assert.False(draft.Sources["source"].IsSet("number"));
        Assert.Equal("-", draft.Plugin.Text("number"));
        Assert.Equal("9223372036854775808", draft.Sources["source"].Text("number"));
        Assert.Equal("retained unset text", draft.Plugin.Text("noDefault"));
        Assert.Equal(generation, draft.EditGeneration);
        Assert.Equal(dirty, draft.Dirty);
        Assert.Equal(discardable, draft.HasDiscardableChanges);
    }

    [Fact]
    public void ExplicitZeroFalseAndEmptyRemainExplicitAndInvalidRawStillBlocksValidation()
    {
        PluginConfigurationDraft draft = Draft();
        draft.Plugin.Set("number", "0");
        draft.Plugin.Set("flag", "false");
        draft.Plugin.Set("text", "");
        PluginConfiguration configuration = draft.Configuration(out var errors);
        Assert.Empty(errors);
        Assert.Equal(0L, Assert.Single(configuration.PluginSettings.Values, value => value.Key == "number").Integer);
        Assert.False(Assert.Single(configuration.PluginSettings.Values, value => value.Key == "flag").Boolean);
        Assert.Equal("", Assert.Single(configuration.PluginSettings.Values, value => value.Key == "text").Text);

        draft.Sources["source"].Set("number", "-");
        _ = draft.Configuration(out errors);
        Assert.Contains(errors, error => error.Scope == "source" && error.Error.FieldKey == "number" && error.Error.Code == "integer");
        Assert.Equal("-", draft.Sources["source"].Text("number"));
        Assert.True(draft.Sources["source"].IsSet("number"));
    }

    private static PluginConfigurationDraft Draft()
    {
        SettingsSchema Schema(SettingScope scope) => new("1", new[]
        {
            new SettingDefinition("number", scope, SettingKind.Integer, "Number", Default: new("number", SettingKind.Integer, Integer: 2)),
            new SettingDefinition("flag", scope, SettingKind.Boolean, "Flag", Default: new("flag", SettingKind.Boolean, Boolean: true)),
            new SettingDefinition("text", scope, SettingKind.Text, "Text", Default: new("text", SettingKind.Text, Text: "default")),
            new SettingDefinition("noDefault", scope, SettingKind.Text, "No default")
        });
        SourceDescription source = new("source", "Source", new Uri("https://fixture.example.test/"), SourceCapabilities.ThreadRead,
            new[] { new UrlRoute(SourceResourceKind.Thread, "fixture.example.test", "/{board}/thread/{thread}", IsCanonical: true) },
            new[] { new HttpHostDeclaration("fixture.example.test", HttpHostPurpose.Metadata) }, Schema(SettingScope.Source));
        PluginDescription description = new("fixture.defaults", "Defaults", "1.0.0", SdkContract.Version, "Effective defaults fixture",
            new[] { source }, Array.Empty<FilterDescription>(), Schema(SettingScope.Plugin), Array.Empty<InformationEntry>());
        Assert.Empty(SdkValidation.ValidateDescription(description));
        PluginCatalogEntry entry = new("candidate", description.PluginId, "candidate", null, description, null, null,
            Array.Empty<MeowChan.PluginHost.Compilation.PluginBuildDiagnostic>());
        return new(entry, 1);
    }
}
