using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginSettingsFormContractTests
{
    [Theory]
    [InlineData(SettingScope.Plugin)]
    [InlineData(SettingScope.Source)]
    [InlineData(SettingScope.FilterRule)]
    public void RejectedRawValuesRemainCorrectableInEveryScope(SettingScope scope)
    {
        PluginSettingsDraft draft = new(new("1", new SettingDefinition[]
        {
            new("integer", scope, SettingKind.Integer, "Number"),
            new("uri", scope, SettingKind.Uri, "URI"),
            new("mode", scope, SettingKind.Enum, "Mode", Options: new[] { new SettingOption("a", "Label") })
        }), scope, null);
        foreach (string raw in new[] { "1.5", "9223372036854775808", "-", " 2 " })
        {
            draft.Set("integer", raw); draft.Set("uri", " https://[invalid"); draft.Set("mode", "obsolete");
            _ = draft.Snapshot(out IReadOnlyList<FieldValidationError> errors);
            Assert.Contains(errors, error => error.FieldKey == "integer");
            Assert.Contains(errors, error => error.FieldKey == "uri");
            Assert.Contains(errors, error => error.FieldKey == "mode");
            Assert.Equal(raw, draft.Text("integer"));
            Assert.Equal(" https://[invalid", draft.Text("uri"));
        }
        draft.Set("integer", "-42"); draft.Set("uri", "https://example.invalid/"); draft.Set("mode", "a");
        SettingsSnapshot saved = draft.Snapshot(out IReadOnlyList<FieldValidationError> corrected);
        Assert.Empty(corrected);
        Assert.Equal(-42, saved.Values.Single(value => value.Key == "integer").Integer);
    }

    [Fact]
    public void OptionalExplicitEmptyFalseAndZeroAreDistinctFromUnset()
    {
        PluginSettingsDraft draft = new(new("1", new SettingDefinition[]
        {
            new("text", SettingScope.Plugin, SettingKind.Text, "Text"),
            new("flag", SettingScope.Plugin, SettingKind.Boolean, "Flag"),
            new("number", SettingScope.Plugin, SettingKind.Integer, "Number")
        }), SettingScope.Plugin, null);
        draft.Set("text", ""); draft.Set("flag", "false"); draft.Set("number", "0");
        SettingsSnapshot explicitValues = draft.Snapshot(out _);
        Assert.Equal(3, explicitValues.Values.Count);
        draft.Set("text", "", false); draft.Set("flag", "false", false); draft.Set("number", "0", false);
        SettingsSnapshot unset = draft.Snapshot(out _);
        Assert.Empty(unset.Values);
        Assert.Equal("false", draft.Text("flag"));
        Assert.Equal("0", draft.Text("number"));
    }

    [Fact]
    public void FieldIdentityKeepsExactKeysAndScopeBoundaries()
    {
        string[] identities =
        {
            PluginSettingsEditor.FieldIdentity(SettingScope.Source, "a", "b/c"),
            PluginSettingsEditor.FieldIdentity(SettingScope.Source, "a/b", "c"),
            PluginSettingsEditor.FieldIdentity(SettingScope.Source, "a", "b.c"),
            PluginSettingsEditor.FieldIdentity(SettingScope.Source, "a", "b-c"),
            PluginSettingsEditor.FieldIdentity(SettingScope.Source, "a", "b_c"),
            PluginSettingsEditor.FieldIdentity(SettingScope.Plugin, "a", "b/c")
        };
        Assert.Equal(identities.Length, identities.Distinct(StringComparer.Ordinal).Count());
    }
}
