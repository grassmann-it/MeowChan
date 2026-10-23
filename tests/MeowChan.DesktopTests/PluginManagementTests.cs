using System.Diagnostics;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Filters;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
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
        string path = Path.Combine(Path.GetTempPath(), "a b.cs");
        Assert.Throws<ArgumentException>(() => PluginEditorLauncher.CreateStartInfo(new("editor", new[] { "--line", "{line}" }), path));
        ProcessStartInfo info = PluginEditorLauncher.CreateStartInfo(new("editor", new[] { "{file}:{line}:{column}" }), path, 8, 2);
        Assert.False(info.UseShellExecute);
        Assert.Equal(Path.GetFullPath(path) + ":8:2", Assert.Single(info.ArgumentList));
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
    public void InvalidLegacyBooleanIsRetainedUntilExplicitCorrection()
    {
        PluginSettingsDraft draft = new(new("2", new SettingDefinition[] { new("flag", SettingScope.FilterRule, SettingKind.Boolean, "Flag") }), SettingScope.FilterRule, null);
        draft.SetInitialText("flag", "old-text");
        _ = draft.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        Assert.Contains(errors, error => error.FieldKey == "flag" && error.Code == "boolean");
        Assert.Equal("old-text", draft.Text("flag"));
        draft.Set("flag", "false");
        _ = draft.Snapshot(out errors); Assert.Empty(errors);
    }

    [Theory]
    [InlineData("free-space-reserve", "targets.freeSpace")]
    [InlineData("disk-full", "targets.diskFull")]
    [InlineData("unavailable", "targets.unavailable")]
    public void TargetWaitDiagnosticsPreservePathReasonAndRecheckTime(string reason, string key)
    {
        DateTimeOffset next = new(2026, 10, 22, 12, 34, 0, TimeSpan.Zero);
        // The public snapshot intentionally has no public constructor; only this UI fixture creates it directly.
        MeowChan.Downloader.DownloadTargetWaitState wait = (MeowChan.Downloader.DownloadTargetWaitState)Activator.CreateInstance(
            typeof(MeowChan.Downloader.DownloadTargetWaitState), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { "/media/fixture", reason, next }, null)!;
        MeowChan.Desktop.DesktopSessionView view = new(Array.Empty<MeowChan.Desktop.DesktopThreadEntry>(), Array.Empty<MeowChan.Desktop.DesktopBoardEntry>(), Array.Empty<string>(), MeowChan.Desktop.DesktopSessionState.Ready, null);
        MeowChan.Desktop.DiagnosticEntry entry = Assert.Single(MeowChan.Desktop.DiagnosticsSnapshot.From(view, new[] { wait }).Entries);
        Assert.Equal("/media/fixture", entry.DownloadTargetPath); Assert.Null(entry.Target);
        Assert.Equal(Texts.T(key), entry.Summary);
        Assert.Contains(Texts.T("targets.nextCheck", next.ToLocalTime()), entry.Details);
    }

    [Theory]
    [InlineData("en", "Source", "All boards", "All rules", "Unresolved rule")]
    [InlineData("de", "Quelle", "Alle Boards", "Alle Regeln", "Unaufgelöste Regel")]
    public void GrantLabelsResolveOnlyKnownRuleScopesAndPreserveUnknownIds(string language, string sourceLabel, string allBoards, string allRules, string unresolved)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            MediaRuleScope[] scopes = { MediaRuleScope.Global, MediaRuleScope.ForSource("fixture-source"), MediaRuleScope.ForBoard("fixture-source", "b"), MediaRuleScope.ForThread("fixture-source", "b", "42") };
            string[] labels = { "Global", sourceLabel + " · fixture-source", "Board · fixture-source/b", "Thread · fixture-source/b/42" };
            for (int index = 0; index < scopes.Length; index++)
                Assert.Equal(labels[index], PluginGrantPresentation.RuleLabel(FilterRuleIdentity.ForScope(scopes[index]), scopes));
            Assert.Equal(allRules, PluginGrantPresentation.RuleLabel(null, scopes));
            string unknown = FilterRuleIdentity.ForScope(MediaRuleScope.ForThread("missing-source", "z", "17"));
            Assert.Equal(unresolved + " (" + unknown + ")", PluginGrantPresentation.RuleLabel(unknown, scopes));
            Assert.Equal(unresolved + " (future-rule:v9)", PluginGrantPresentation.RuleLabel("future-rule:v9", scopes));
            PluginGrantScope identity = new("fixture-source", null, FilterRuleIdentity.ForScope(MediaRuleScope.Global));
            PluginGrantSnapshot grant = new("fixture.management", "fixture-filter", identity, FilterDataAccess.OriginalImage, 1);
            string rendered = PluginGrantPresentation.Describe(grant, scopes, effective: true);
            Assert.Contains(allBoards, rendered); Assert.Contains("Global", rendered);
            Assert.DoesNotContain(identity.RuleId!, rendered); Assert.Same(identity, grant.Scope);
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void PluginCatalogTranslationsHaveMatchingPlaceholders() => Assert.Empty(Texts.ValidateBuiltInCatalogs());
}
