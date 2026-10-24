using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginConfigurationFieldErrorTests
{
    [Fact]
    public void LocalErrorsRetainPluginAndOverlappingSourceIdentities()
    {
        SettingsSchema pluginSchema = Schema(SettingScope.Plugin, "a/b/c");
        SourceDescription[] sources = { Source("a", "b/c"), Source("a/b", "c") };
        PluginDescription description = new("fixture.scopes", "Scopes", "1.0.0", SdkContract.Version, "Scope error fixture",
            sources, Array.Empty<FilterDescription>(), pluginSchema, Array.Empty<InformationEntry>());
        Assert.Empty(SdkValidation.ValidateDescription(description));
        PluginCatalogEntry entry = new("candidate", description.PluginId, "candidate", null, description, null, null, Array.Empty<MeowChan.PluginHost.Compilation.PluginBuildDiagnostic>());
        PluginConfigurationDraft draft = new(entry, 1);
        draft.Plugin.Set("a/b/c", "-");
        draft.Sources["a"].Set("b/c", "9223372036854775808");
        draft.Sources["a/b"].Set("c", "1.5");
        _ = draft.Configuration(out IReadOnlyList<PluginConfigurationFieldError> errors);
        Assert.Contains(PluginConfigurationFieldError.ForScope(errors, ""), error => error.FieldKey == "a/b/c" && error.Code == "integer");
        Assert.Contains(PluginConfigurationFieldError.ForScope(errors, "a"), error => error.FieldKey == "b/c" && error.Code == "integer");
        Assert.Contains(PluginConfigurationFieldError.ForScope(errors, "a/b"), error => error.FieldKey == "c" && error.Code == "integer");
        Assert.All(PluginConfigurationFieldError.ForScope(errors, ""), error => Assert.Equal("a/b/c", error.FieldKey));
        Assert.All(PluginConfigurationFieldError.ForScope(errors, "a"), error => Assert.Equal("b/c", error.FieldKey));
        Assert.All(PluginConfigurationFieldError.ForScope(errors, "a/b"), error => Assert.Equal("c", error.FieldKey));
        Assert.Empty(PluginConfigurationFieldError.ForScope(errors, "ab"));
    }

    [Fact]
    public void ScopeSelectionDoesNotDecodeNormalizeOrTreatPrefixesAsParents()
    {
        string[] scopes = { "", "a", "a/b", "ab", "a%2Fb", "é/β", "e\u0301/β" };
        PluginConfigurationFieldError[] errors = scopes.Select((scope, index) => new PluginConfigurationFieldError(scope,
            new FieldValidationError("field/β", "worker", "scope " + index))).ToArray();
        for (int index = 0; index < scopes.Length; index++)
            Assert.Equal("scope " + index, Assert.Single(PluginConfigurationFieldError.ForScope(errors, scopes[index])).Message);
        Assert.Empty(PluginConfigurationFieldError.ForScope(errors, "A"));
    }

    private static SettingsSchema Schema(SettingScope scope, string key) => new("1", new[] { new SettingDefinition(key, scope, SettingKind.Integer, key) });
    private static SourceDescription Source(string id, string key) => new(id, id, new Uri("https://fixture.example.test/"), SourceCapabilities.ThreadRead,
        new[] { new UrlRoute(SourceResourceKind.Thread, "fixture.example.test", "/" + id + "/{board}/thread/{thread}", IsCanonical: true) },
        new[] { new HttpHostDeclaration("fixture.example.test", HttpHostPurpose.Metadata) }, Schema(SettingScope.Source, key));
}
