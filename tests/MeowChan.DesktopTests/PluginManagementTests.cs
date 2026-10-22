using System.Diagnostics;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class PluginManagementTests
{
    [Fact]
    public void DraftReportsErrorsByStableFieldAndPreservesRawInvalidInput()
    {
        SettingsSchema schema = new("1", new SettingDefinition[]
        {
            new("name", SettingScope.Plugin, SettingKind.Text, "Name", IsRequired: true),
            new("count", SettingScope.Plugin, SettingKind.Integer, "Count", Minimum: 1, Maximum: 5),
            new("url", SettingScope.Plugin, SettingKind.Uri, "URL"),
            new("mode", SettingScope.Plugin, SettingKind.Enum, "Mode", Options: new[] { new SettingOption("a", "A") }),
            new("enabled", SettingScope.Plugin, SettingKind.Boolean, "Enabled")
        });
        PluginSettingsDraft draft = new(schema, SettingScope.Plugin, null);
        draft.Set("count", "1.5"); draft.Set("url", "relative"); draft.Set("mode", "unknown"); draft.Set("enabled", "true");
        _ = draft.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        Assert.Contains(errors, error => error.FieldKey == "name");
        Assert.Contains(errors, error => error.FieldKey == "count");
        Assert.Contains(errors, error => error.FieldKey == "url");
        Assert.Contains(errors, error => error.FieldKey == "mode");
        Assert.Equal("1.5", draft.Text("count"));
        draft.Set("name", "draft"); draft.Set("count", "3"); draft.Set("url", "https://example.invalid/"); draft.Set("mode", "a");
        SettingsSnapshot valid = draft.Snapshot(out errors);
        Assert.Empty(errors);
        Assert.Equal(3, valid.Values.Single(value => value.Key == "count").Integer);
        Assert.True(valid.Values.Single(value => value.Key == "enabled").Boolean);
    }

    [Fact]
    public void DefaultsAreNotDirtyAndUnsetOptionalValueRemainsUnset()
    {
        SettingsSchema schema = new("2", new SettingDefinition[]
        {
            new("flag", SettingScope.Source, SettingKind.Boolean, "Flag", Default: new("flag", SettingKind.Boolean, Boolean: false)),
            new("text", SettingScope.Source, SettingKind.Text, "Text")
        });
        PluginSettingsDraft draft = new(schema, SettingScope.Source, null);
        Assert.False(draft.Dirty);
        Assert.False(draft.IsSet("text"));
        Assert.Equal("false", draft.Text("flag"));
        draft.Set("text", "first"); draft.Set("text", "", false);
        SettingsSnapshot saved = draft.Snapshot(out _);
        Assert.DoesNotContain(saved.Values, value => value.Key == "text");
    }

    [Fact]
    public async Task EditorHandoffUsesRealChildAndLiteralSeparateArguments()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "meowchan-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "space ' $(literal); {line}-{column}-{file} source.cs");
            string capture = Path.Combine(directory, "arguments.txt");
            string launcher = Path.Combine(directory, "editor fixture");
            await File.WriteAllTextAsync(source, "// fixture");
            await File.WriteAllTextAsync(launcher, "#!/bin/sh\nprintf '%s\\n' \"$#\" \"$1\" \"$2\" \"$3\" \"$4\" > \"$5\"\n");
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await PluginEditorLauncher.OpenAsync(new(launcher, new[] { "--goto", "{file}", "{line}", "{column}", capture }), source, 27, 9);
            Assert.Equal(new[] { "5", "--goto", source, "27", "9" }, await File.ReadAllLinesAsync(capture));
            Assert.False(File.Exists(Path.Combine(directory, "literal")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingEditorAndFailedChildAreReportedWithoutClaimingHandoff()
    {
        string directory = Path.Combine(Path.GetTempPath(), "meowchan-editor-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.cs"); File.WriteAllText(source, "// fixture");
        try
        {
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => PluginEditorLauncher.OpenAsync(new(Path.Combine(directory, "missing"), new[] { "{file}" }), source));
            if (OperatingSystem.IsLinux())
            {
                string launcher = Path.Combine(directory, "editor fixture");
                File.WriteAllText(launcher, "#!/bin/sh\nexit 17\n");
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Exception error = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => PluginEditorLauncher.OpenAsync(new(launcher, new[] { "{file}" }), source));
                Assert.Contains("17", error.Message);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void EditorTemplateRequiresFileArgumentAndNoShell()
    {
        Assert.Throws<ArgumentException>(() => PluginEditorLauncher.CreateStartInfo(new("editor", new[] { "--line", "{line}" }), "/tmp/plugin.cs"));
        ProcessStartInfo info = PluginEditorLauncher.CreateStartInfo(new("editor", new[] { "{file}:{line}:{column}" }), "/tmp/a b.cs", 8, 2);
        Assert.False(info.UseShellExecute);
        Assert.Equal("/tmp/a b.cs:8:2", Assert.Single(info.ArgumentList));
    }

    [Fact]
    public void ChangedSchemaRetainsRemovedValuesAndRequiresExplicitAcknowledgement()
    {
        SettingsSchema schema = new("2", new SettingDefinition[] { new("count", SettingScope.FilterRule, SettingKind.Integer, "Count") });
        PluginSettingsDraft draft = new(schema, SettingScope.FilterRule, new("1", new SettingValue[] {
            new("removed", SettingKind.Text, Text: "preserved value"), new("count", SettingKind.Text, Text: "3") }));
        _ = draft.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        Assert.Equal("preserved value", draft.UnmappedValues["removed"]);
        Assert.Contains(errors, error => error.Code == "removed_setting");
        Assert.Contains(errors, error => error.FieldKey == "count" && error.Code == "incompatible_setting");
        Assert.False(draft.Dirty);
        draft.AcknowledgeRemovedFields();
        _ = draft.Snapshot(out errors);
        Assert.DoesNotContain(errors, error => error.Code == "removed_setting");
        Assert.Contains(errors, error => error.Code == "incompatible_setting");
        draft.Set("count", "3");
        SettingsSnapshot saved = draft.Snapshot(out errors);
        Assert.Empty(errors);
        Assert.Equal(3, Assert.Single(saved.Values).Integer);
        Assert.True(draft.Dirty);
    }

    [Fact]
    public void PluginCatalogTranslationsHaveMatchingPlaceholders() => Assert.Empty(Texts.ValidateBuiltInCatalogs());
}
