using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SettingsResetContractTests
{
    private static readonly (string Name, string Scope)[] SectionResets =
    [
        ("ResetInterfaceButton", "settings.interface"),
        ("ResetReadingMediaButton", "settings.reading-media"),
        ("ResetDownloadsButton", "settings.downloads"),
        ("ResetWorkerBudgetsButton", "settings.worker-budgets"),
        ("ResetUpdatesButton", "settings.updates"),
        ("ResetNotificationsButton", "settings.notifications"),
        ("ResetBehaviorButton", "settings.behavior")
    ];

    [Fact]
    public void ResetButtonsCoverEveryEditableSectionButNotTheShortcutEditor()
    {
        XDocument markup = Markup();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // "Reset all" is an entry of the head's one More button (with the export), not a button of its own; the section resets stay buttons.
        XElement more = SingleNamed(markup, x, "SettingsMoreButton");
        Assert.Equal("OnSettingsMoreClick", (string?)more.Attribute("Click"));
        Assert.Equal("{Binding IsEditable}", (string?)more.Attribute("IsEnabled"));
        Assert.DoesNotContain(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "ResetAllSettingsButton");
        Assert.Contains("OpenResetConfirmation(origin, SettingsResetScope.All)", File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Transfer.cs")));

        foreach ((string name, string scope) in SectionResets)
        {
            XElement button = SingleNamed(markup, x, name);
            Assert.Equal(scope, (string?)button.Attribute("Tag"));
            Assert.Equal("OnResetCategoryClick", (string?)button.Attribute("Click"));
            Assert.Equal("{Binding ResetLabel}", (string?)button.Attribute("Content"));
            Assert.Equal("{Binding IsEditable}", (string?)button.Attribute("IsEnabled"));
        }

        Assert.DoesNotContain(markup.Descendants(), element =>
            element.Name.LocalName == "Button" && (string?)element.Attribute("Tag") == "settings.shortcuts");
        Assert.DoesNotContain(markup.Descendants().Where(element => element.Name.LocalName == "Button"), element =>
            ((string?)element.Attribute(x + "Name"))?.StartsWith("Reset", StringComparison.Ordinal) == true
            && element.Ancestors().Any(ancestor => (string?)ancestor.Attribute(x + "Name") == "ShortcutsEditor"));
    }

    [Fact]
    public void ResetStatusIsPoliteAndResetCopyExistsInBothBuiltIns()
    {
        XDocument markup = Markup();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement status = SingleNamed(markup, x, "SettingsResetStatus");
        Assert.Equal("Polite", (string?)status.Attribute("AutomationProperties.LiveSetting"));

        string[] keys = SettingsResetCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(8, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key =>
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), "Missing English reset text: " + key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), "Missing German reset text: " + key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        });
        Assert.Contains("editable", BuiltInCatalog.English["settings.reset.all"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bearbeit", BuiltInCatalog.German["settings.reset.all"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResetUsesAFreshConfigAndDoesNotCrossThePersistenceOrShortcutBoundary()
    {
        string defaults = File.ReadAllText(Root("Desktop/Views/Settings/SettingsDefaults.cs"));
        string reset = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Reset.cs"));

        Assert.Contains("Values = new Config();", defaults, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty", defaults + reset, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperties", defaults + reset, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyInfo", defaults + reset, StringComparison.Ordinal);
        Assert.DoesNotContain("Activator.", defaults + reset, StringComparison.Ordinal);

        Assert.DoesNotContain("SaveConfig(", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutBindings", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceMediaIntegrityPermissions", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadsPaused", reset, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstRunSetup", reset, StringComparison.Ordinal);
        Assert.Contains("ResetNumber(BandwidthInput, BandwidthLimiterMB.Value);", reset, StringComparison.Ordinal);
        Assert.Contains("BandwidthLimiterMiB => Values.BandwidthLimiter / (1024m * 1024m)", defaults, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerBudgetResetIncludesTheProfileRegistryLimitDraft()
    {
        string reset = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Reset.cs"));

        // ProfileRegistryLimit is rendered in the worker-budgets page. A section reset
        // must reset both the bound draft and the NumericUpDown's raw editing surface;
        // omitting either leaves a user-visible value unchanged or saves the old one.
        Assert.Contains("ProfileRegistryLimit = defaults.ProfileRegistryLimit;", reset, StringComparison.Ordinal);
        Assert.Contains("ResetNumber(ProfileRegistryLimitInput, defaults.ProfileRegistryLimit);", reset, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsKeepConfigMappingForTheEditorUnits()
    {
        Config expected = new();
        SettingsDefaults defaults = new();

        Assert.Equal(expected.Theme, defaults.Values.Theme);
        Assert.Equal(expected.Language, defaults.Values.Language);
        Assert.Equal(expected.VideoVolume, defaults.Values.VideoVolume);
        Assert.Equal(expected.BandwidthLimiter, defaults.Values.BandwidthLimiter);
        Assert.Equal(expected.BandwidthLimiter / (1024m * 1024m), defaults.BandwidthLimiterMiB);
        Assert.Equal(expected.StatsHeaderGraph, defaults.Values.StatsHeaderGraph);
    }

    private static XElement SingleNamed(XDocument markup, XNamespace x, string name) =>
        Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == name);

    private static XDocument Markup() => XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml"));

    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}
