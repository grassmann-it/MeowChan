using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// The nine statistics settings in the settings editor (block "Statistics" on the Behavior page): every row is in the markup
/// with its label, hint and range, the texts exist in the five built-in languages and state the range, the dependent rows
/// are bound to their switch, and a saved change reaches the running hub. The window itself is covered by the smoke scenario
/// "settings-statistics".
/// </summary>
[Collection("Localization")]
public sealed class IoStatsSettingsEditorTests : IDisposable
{
    public IoStatsSettingsEditorTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    // Config property (also the editor property), text key stem, dependency (switch property) or null.
    private static readonly (string Property, string Key, string? DependsOn)[] Numbers =
    {
        (nameof(Config.StatsSampleIntervalMs), "sampleInterval", null),
        (nameof(Config.StatsHistorySamples), "historySamples", null),
        (nameof(Config.StatsBackgroundIntervalMs), "backgroundInterval", nameof(Config.StatsTrayTooltip)),
        (nameof(Config.StatsRetentionDays), "retentionDays", nameof(Config.StatsPersist)),
        (nameof(Config.StatsPersistIntervalMinutes), "persistInterval", nameof(Config.StatsPersist)),
        (nameof(Config.StatsMaxSources), "maxSources", null),
    };

    private static readonly (string Property, string Key)[] Switches =
    {
        (nameof(Config.StatsTrayTooltip), "trayTooltip"),
        (nameof(Config.StatsTrayMenu), "trayMenu"),
        (nameof(Config.StatsPersist), "persist"),
    };

    private static int Constant(string name) =>
        (int)typeof(Config).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private static XDocument Markup() => XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void EveryNumberHasALabelledRowWithItsRangeOnTheBehaviorPage()
    {
        XDocument markup = Markup();
        XElement section = Named(markup, "SettingsStatisticsSection");
        Assert.Contains(section.Ancestors(), e => (string?)e.Attribute(X + "Name") == "SettingsBehaviorPage");
        foreach ((string property, _, string? dependsOn) in Numbers)
        {
            XElement field = Named(markup, property + "Input");
            Assert.Contains(field.Ancestors(), e => ReferenceEquals(e, section));
            Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal(Constant(property + "Minimum").ToString(), (string?)field.Attribute("Minimum"));
            Assert.Equal(Constant(property + "Maximum").ToString(), (string?)field.Attribute("Maximum"));
            Assert.Equal("{Binding ElementName=" + property + "FieldLabel}", (string?)field.Attribute("AutomationProperties.LabeledBy"));
            Assert.Equal("{Binding " + property + "Label}", (string?)field.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding " + property + "Help}", (string?)field.Attribute("AutomationProperties.HelpText"));
            Assert.Equal("{Binding " + property + "Label}", (string?)Named(markup, property + "FieldLabel").Attribute("Text"));
            Named(markup, property + "InputError");
            Assert.Contains(section.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "Hint}");
            if (dependsOn == null)
            {
                Assert.Null(field.Attribute("IsEnabled"));
                continue;
            }
            // A dependent row is unavailable (not hidden) while its switch is off and says why, also on the disabled control.
            Assert.Equal("{Binding " + property + "Enabled}", (string?)field.Attribute("IsEnabled"));
            Assert.Equal("{Binding " + property + "Enabled}", (string?)Named(markup, property + "FieldLabel").Attribute("IsEnabled"));
            Assert.Equal("{Binding " + property + "Reason}", (string?)field.Attribute("ToolTip.Tip"));
            Assert.Equal("True", (string?)field.Attribute("ToolTip.ShowOnDisabled"));
        }
    }

    [Fact]
    public void HeaderMonitorToggleLivesInAppearanceAndUsesTheHeaderGraphDraft()
    {
        XDocument markup = Markup();
        XElement option = Named(markup, "StatsHeaderMonitorInput");
        Assert.Equal("CheckBox", option.Name.LocalName);
        Assert.Equal("{Binding StatsHeaderMonitorShown, Mode=TwoWay}", (string?)option.Attribute("IsChecked"));
        Assert.Equal("{Binding StatsHeaderMonitorLabel}", (string?)option.Attribute("AutomationProperties.Name"));
        Assert.Contains(option.Ancestors(), e => (string?)e.Attribute(X + "Name") == "SettingsInterfacePage");
        Assert.DoesNotContain(option.Ancestors(), e => (string?)e.Attribute(X + "Name") == "SettingsBehaviorPage");
        Assert.DoesNotContain(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == "HeaderMonitorCheck");

        string graph = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.StatsHeaderGraph.cs"));
        Assert.Contains("StatsHeaderMonitorShown", graph);
        Assert.Contains("HeaderMonitorToggled", graph);
        Assert.Contains("IoStatsDisplay.HeaderGraphOff", graph);
    }

    [Fact]
    public void EverySwitchHasALabelAndAHintAndTheSwitchesComeBeforeTheirDependentRows()
    {
        XDocument markup = Markup();
        XElement section = Named(markup, "SettingsStatisticsSection");
        List<XElement> order = section.Descendants().ToList();
        foreach ((string property, _) in Switches)
        {
            XElement box = Named(markup, property + "Input");
            Assert.Contains(box.Ancestors(), e => ReferenceEquals(e, section));
            Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)box.Attribute("IsChecked"));
            Assert.Equal("{Binding " + property + "Label}", (string?)box.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding " + property + "Hint}", (string?)box.Attribute("AutomationProperties.HelpText"));
            Assert.Contains(box.Parent!.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "Label}");
            Assert.Contains(order, e => (string?)e.Attribute("Text") == "{Binding " + property + "Hint}");
        }
        foreach ((string property, _, string? dependsOn) in Numbers.Where(n => n.DependsOn != null))
            Assert.True(order.IndexOf(Named(markup, dependsOn! + "Input")) < order.IndexOf(Named(markup, property + "Input")));
        // The block is self-contained: only new elements, after the existing tray switches.
        Assert.True(order.Count > 0);
        List<XElement> page = Named(markup, "SettingsBehaviorPage").Descendants().ToList();
        Assert.True(page.IndexOf(Named(markup, "MinimizeToTrayInput")) < page.IndexOf(section));
    }

    [Fact]
    public void EveryRowHasItsTextsInAllFiveLanguagesAndEveryHintStatesItsRange()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                foreach (string key in new[] { "title", "intro", "unitMs", "unitDays", "unitReadings", "unitSources" })
                    Assert.NotEqual("stats.settings." + key, Texts.T("stats.settings." + key));
                foreach ((string property, string key, _) in Numbers)
                {
                    int minimum = Constant(property + "Minimum"), maximum = Constant(property + "Maximum");
                    string label = Texts.T("stats.settings." + key);
                    string hint = Texts.T("stats.settings." + key + "Hint", minimum, maximum);
                    Assert.NotEqual("stats.settings." + key, label);
                    Assert.NotEqual("stats.settings." + key + "Hint", hint);
                    Assert.Contains(minimum.ToString(), hint);
                    Assert.Contains(maximum.ToString(), hint);
                    Assert.DoesNotContain("{", hint);
                }
                foreach ((_, string key) in Switches)
                {
                    Assert.NotEqual("stats.settings." + key, Texts.T("stats.settings." + key));
                    Assert.NotEqual("stats.settings." + key + "Hint", Texts.T("stats.settings." + key + "Hint"));
                }
                // The reason names the switch the row depends on.
                foreach (string key in new[] { "persist", "trayTooltip" })
                {
                    string label = Texts.T("stats.settings." + key);
                    string reason = Texts.T("stats.settings.needsOption", label);
                    Assert.Contains(label, reason);
                    Assert.DoesNotContain("{", reason);
                }
            }
            // Wording rules of the project: du form, binary units are not touched here, the ellipsis stays attached.
            Texts.SetLanguage("de");
            foreach (var entry in IoStatsSettingsTexts.Entries)
            {
                // "Sie" is also the plural "they" ("Sie werden gelesen"), so only the formal forms are refused.
                Assert.DoesNotContain("Ihnen", entry.German);
                Assert.DoesNotContain("Ihre", entry.German);
                Assert.DoesNotContain("Ihr ", entry.German);
                Assert.DoesNotContain(" …", entry.German);
                Assert.DoesNotContain("Beitrag", entry.German);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheSettingsKeysAreUniqueAndFilled()
    {
        string[] keys = IoStatsSettingsTexts.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith("stats.settings.", key));
        Assert.All(IoStatsSettingsTexts.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
        });
    }

    [Fact]
    public async Task ASavedSettingsChangeReachesTheRunningHubAndAShorterHistoryKeepsTheNewestValues()
    {
        using TempDirectory directory = new();
        new Config
        {
            AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory.Path, "media"),
            StatsHistorySamples = 60, StatsPersist = false
        }.SaveConfig(directory.Path);
        IoManualClock clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock);
        {
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), ioStats: hub);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(60, hub.Options.HistorySamples);
            using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
            for (int i = 1; i <= 40; i++) { source.Add("a", net: i * 1024); clock.Advance(TimeSpan.FromSeconds(1)); }
            Assert.Equal(40, hub.Snapshot().Series.Count);

            // What the editor's save hands the host: a candidate cloned from the saved settings with the drafts copied in.
            Config shorter = host.Config.Clone();
            shorter.StatsHistorySamples = 30;
            shorter.StatsRetentionDays = 14;
            shorter.StatsMaxSources = 100;
            await host.ApplySettingsAsync(shorter).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((30, 14, 100), (hub.Options.HistorySamples, hub.Options.RetentionDays, hub.Options.MaxSources));
            IoStatsSnapshot snapshot = hub.Snapshot();
            Assert.Equal(30, snapshot.Series.Count);
            Assert.Equal(40f * 1024, snapshot.Series.NetDown[^1]);
            Assert.Equal(11f * 1024, snapshot.Series.NetDown[0]);

            Config slower = host.Config.Clone();
            slower.StatsSampleIntervalMs = 2000;
            slower.StatsTrayTooltip = false;
            slower.StatsTrayMenu = false;
            slower.StatsBackgroundIntervalMs = 30_000;
            await host.ApplySettingsAsync(slower).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2000, hub.SamplingIntervalMs);
            Assert.Equal((false, false, 30_000), (hub.Options.TrayTooltip, hub.Options.TrayMenu, hub.Options.BackgroundIntervalMs));
        }
    }
}
