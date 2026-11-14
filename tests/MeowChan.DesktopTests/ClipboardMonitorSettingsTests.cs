using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

public sealed class ClipboardMonitorSettingsTests
{
    [Fact]
    public void NewAndMissingProfilesDoNotOptInOrCreateSettings()
    {
        using TempDirectory directory = new();
        string missingProfile = Path.Combine(directory.Path, "missing-profile");

        Assert.False(new Config().MonitorClipboardUrls);
        Assert.False(Config.LoadConfig(missingProfile).MonitorClipboardUrls);
        Assert.False(Directory.Exists(missingProfile));
    }

    [Fact]
    public void LegacySettingsWithoutThePreferenceRemainOptedOut()
    {
        using TempDirectory directory = new();
        string settings = Path.Combine(directory.Path, "settings.json");
        const string legacy = "{\"SaveUrlsOnExit\":false,\"RememberReadingPosition\":true,\"RecheckTimer\":91}";
        File.WriteAllText(settings, legacy);

        Config loaded = Config.LoadConfig(directory.Path);

        Assert.False(loaded.MonitorClipboardUrls);
        Assert.Null(loaded.LoadWarning);
        Assert.False(loaded.SaveUrlsOnExit);
        Assert.True(loaded.RememberReadingPosition);
        Assert.Equal(91, loaded.RecheckTimer);
        Assert.Equal(legacy, File.ReadAllText(settings));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("\"invalid\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("{}")]
    [InlineData("{\"nested\":true}")]
    [InlineData("[]")]
    [InlineData("[true,false]")]
    public void MalformedPreferenceValuesFailClosedWithoutDroppingOtherSettings(string value)
    {
        using TempDirectory directory = new();
        string settings = Path.Combine(directory.Path, "settings.json");
        string json = "{\"MonitorClipboardUrls\":" + value + ",\"RecheckTimer\":91}";
        File.WriteAllText(settings, json);

        Config loaded = Config.LoadConfig(directory.Path);

        Assert.Null(loaded.LoadWarning);
        Assert.False(loaded.MonitorClipboardUrls);
        Assert.Equal(91, loaded.RecheckTimer);
        Assert.Equal(json, File.ReadAllText(settings));
        loaded.SaveConfig();
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(settings));
        Assert.False(saved.RootElement.GetProperty(nameof(Config.MonitorClipboardUrls)).GetBoolean());
        Assert.False(Config.LoadConfig(directory.Path).MonitorClipboardUrls);
    }

    [Fact]
    public void DamagedJsonWithoutABackupDoesNotOptInOrOverwriteTheFile()
    {
        using TempDirectory directory = new();
        string settings = Path.Combine(directory.Path, "settings.json");
        const string damaged = "{\"MonitorClipboardUrls\":true,";
        File.WriteAllText(settings, damaged);

        Config loaded = Config.LoadConfig(directory.Path);

        Assert.False(loaded.MonitorClipboardUrls);
        Assert.NotNull(loaded.LoadWarning);
        Assert.Equal(damaged, File.ReadAllText(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBooleanSurvivesCloneCopyAndSaveReload(bool enabled)
    {
        using TempDirectory directory = new();
        Config original = new() { MonitorClipboardUrls = enabled };
        Config clone = original.Clone();
        Assert.Equal(enabled, clone.MonitorClipboardUrls);
        clone.MonitorClipboardUrls = !enabled;
        Assert.Equal(enabled, original.MonitorClipboardUrls);
        clone.MonitorClipboardUrls = enabled;

        Config copied = new() { MonitorClipboardUrls = !enabled };
        copied.CopyFrom(clone);
        Assert.Equal(enabled, copied.MonitorClipboardUrls);
        copied.SaveConfig(directory.Path);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(enabled, saved.RootElement.GetProperty(nameof(Config.MonitorClipboardUrls)).GetBoolean());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(enabled, loaded.MonitorClipboardUrls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsCopyAppliesExplicitOptInAndOptOutWithoutOverwritingRuntimeBaselines(bool enabled)
    {
        Config live = new() { MonitorClipboardUrls = !enabled, VideoVolume = 70 };
        Config draft = live.Clone();
        draft.MonitorClipboardUrls = enabled;
        // An unsaved candidate is not the running configuration.
        Assert.Equal(!enabled, live.MonitorClipboardUrls);
        live.VideoVolume = 29;
        live.DownloadsPaused = true;
        live.WindowWidth = 1510;
        live.ColumnWidths["ThreadsDataGrid.Title"] = 333;
        var baselineProfiles = draft.SettingsBaselineProfiles;
        var baselinePlayback = draft.SettingsBaselineGalleryPlayback;

        live.CopySettingsFieldsFrom(draft);

        Assert.Equal(enabled, live.MonitorClipboardUrls);
        Assert.Equal(29, live.VideoVolume);
        Assert.True(live.DownloadsPaused);
        Assert.Equal(1510d, live.WindowWidth);
        Assert.Equal(333d, live.ColumnWidths["ThreadsDataGrid.Title"]);
        Assert.Same(baselineProfiles, draft.SettingsBaselineProfiles);
        // Validate normalizes this immutable value record into a new instance.
        // Preserve its values, not reference identity.
        Assert.Equal(baselinePlayback, draft.SettingsBaselineGalleryPlayback);
        Assert.Equal(70, draft.SettingsBaselineVideoVolume);
    }

    [Fact]
    public void DraftBindingNotifiesAndInvalidatesOnlyWhenTheValueChanges()
    {
        // CLR binding contract only, following GalleryPlaybackSettingsTests. This
        // does not construct a real Avalonia editor or claim a routed Save/UI test.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        FieldInfo revision = typeof(SettingsEditor).GetField("_draftRevision", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var notifications = new List<string?>();
        editor.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        Assert.False(editor.MonitorClipboardUrls);

        editor.MonitorClipboardUrls = true;
        Assert.True(editor.MonitorClipboardUrls);
        Assert.Equal(1L, (long)revision.GetValue(editor)!);
        Assert.Equal(new[] { nameof(SettingsEditor.MonitorClipboardUrls) }, notifications);
        editor.MonitorClipboardUrls = true;
        Assert.Single(notifications);
        Assert.Equal(1L, (long)revision.GetValue(editor)!);
        editor.MonitorClipboardUrls = false;
        Assert.False(editor.MonitorClipboardUrls);
        Assert.Equal(2L, (long)revision.GetValue(editor)!);
        Assert.Equal(2, notifications.Count);
    }

    [Fact]
    public void BehaviorMarkupBindsAnAccessibleDraftSwitchAndVisibleWrappedHint()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement checkbox = Assert.Single(markup.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "MonitorClipboardUrlsInput"));
        Assert.Equal("CheckBox", checkbox.Name.LocalName);
        // The switch sits in the behaviour group ("More settings") of the page, which carries the editability binding.
        Assert.Equal("SettingsBehaviorBody", (string?)checkbox.Parent!.Attribute(x + "Name"));
        XElement page = Assert.Single(checkbox.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsBehaviorPage");
        Assert.Equal("{Binding IsEditable}", (string?)page.Attribute("IsEnabled"));
        Assert.Equal("{Binding MonitorClipboardUrls, Mode=TwoWay}", (string?)checkbox.Attribute("IsChecked"));
        Assert.Equal("{Binding MonitorClipboardLabel}", (string?)checkbox.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding MonitorClipboardHint}", (string?)checkbox.Attribute("AutomationProperties.HelpText"));
        Assert.Contains(checkbox.Parent.Elements(), e => e.Name.LocalName == "TextBlock"
            && (string?)e.Attribute("Text") == "{Binding MonitorClipboardHint}"
            && (string?)e.Attribute("TextWrapping") == "Wrap");
    }

    [Theory]
    [InlineData("en", "Add URLs (Enter)", "Dismiss")]
    [InlineData("de", "URLs hinzufügen (Enter)", "Verwerfen")]
    public void BuiltInsExposeAllClipboardKeysAndExactlyOneCountPlaceholder(string language, string accept, string dismiss)
    {
        var catalog = language == "en" ? BuiltInCatalog.English : BuiltInCatalog.German;
        string[] keys = ["settings.monitorClipboard", "settings.monitorClipboardHint", "clipboard.monitor.found",
            "clipboard.monitor.accept", "clipboard.monitor.dismiss"];
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(catalog[key])));
        Assert.Equal(accept, catalog["clipboard.monitor.accept"]);
        Assert.Equal(dismiss, catalog["clipboard.monitor.dismiss"]);
        string found = catalog["clipboard.monitor.found"];
        Assert.Equal(2, found.Split("{0}").Length);
        string formatted = string.Format(CultureInfo.InvariantCulture, found, 7);
        Assert.Contains("7", formatted);
        Assert.DoesNotContain("{", formatted);
        Assert.DoesNotContain("}", formatted);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));
}
