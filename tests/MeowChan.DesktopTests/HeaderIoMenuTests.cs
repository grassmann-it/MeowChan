using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>The header display's context menu: the setting value and the checked choice are the same thing, and the menu says what the settings page says.</summary>
[Collection("Localization")]
public sealed class HeaderIoMenuTests : IDisposable
{
    public HeaderIoMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ContextMenuModel Model(string? current, Action<string>? choose = null, Action? statistics = null, Action? settings = null) =>
        HeaderIoMenu.Compose(current, choose ?? (_ => { }), statistics ?? (() => { }), settings ?? (() => { })).ToModel();

    [Fact]
    public void TheChoicesAreTheSettingsValuesInTheSettingsOrderAndOneIsChecked()
    {
        ContextMenuModel model = Model(IoStatsDisplay.HeaderGraphSideBySide);
        ContextMenuEntry[] radios = model.Sections[0].ToArray();
        Assert.Equal(IoStatsDisplay.HeaderGraphChoices.Select(HeaderIoMenu.EntryId), radios.Select(entry => entry.Id));
        Assert.All(radios, entry => Assert.Equal(ContextMenuEntryKind.Radio, entry.Kind));
        Assert.Equal(HeaderIoMenu.EntryId(IoStatsDisplay.HeaderGraphSideBySide), Assert.Single(radios, entry => entry.Checked).Id);
    }

    [Theory]
    [InlineData("Auto", "Auto")]
    [InlineData("Network", "Network")]
    [InlineData("Disk", "Disk")]
    [InlineData("NetworkAndDisk", "NetworkAndDisk")]
    [InlineData("SideBySide", "SideBySide")]
    [InlineData("Text", "Text")]
    [InlineData("Off", "Off")]
    [InlineData("off", "Off")]
    [InlineData("text", "Text")]          // the stored value is normalized like the header does
    [InlineData("", "Auto")]
    [InlineData(null, "Auto")]
    [InlineData("Sideways", "Auto")]      // anything unknown reads as Auto
    public void TheCheckedChoiceIsWhatTheHeaderShows(string? stored, string expected)
    {
        ContextMenuEntry checkedEntry = Assert.Single(Model(stored).Sections[0], entry => entry.Checked);
        Assert.Equal(HeaderIoMenu.EntryId(expected), checkedEntry.Id);
    }

    [Fact]
    public void OffIsTheLastChoiceWithTheSettingsTextAndReportsTheOffValue()
    {
        List<string> chosen = [];
        ContextMenuEntry off = Model("Auto", chosen.Add).Sections[0][^1];
        Assert.Equal(HeaderIoMenu.EntryId("Off"), off.Id);
        Assert.Equal(ContextMenuEntryKind.Radio, off.Kind);
        Assert.Equal("Off, hide the display", off.Label);
        Assert.False(off.Checked);
        off.Invoke!();
        Assert.Equal(["Off"], chosen);
        // Selected, Off stays the one checked radio and reports nothing again.
        chosen.Clear();
        ContextMenuEntry selected = Model("Off", chosen.Add).Sections[0][^1];
        Assert.True(selected.Checked);
        selected.Invoke!();
        Assert.Empty(chosen);
        Texts.SetLanguage("de");
        Assert.Equal("Aus, Anzeige ausblenden", Model("Auto").Sections[0][^1].Label);
    }

    [Fact]
    public void ChoosingAValueReportsItAndTheValueAlreadySelectedReportsNothing()
    {
        List<string> chosen = [];
        ContextMenuModel model = Model("Network", chosen.Add);
        foreach (ContextMenuEntry entry in model.Sections[0]) entry.Invoke!();
        // Every value but the selected one, in the list's order.
        Assert.Equal(IoStatsDisplay.HeaderGraphChoices.Where(value => value != "Network"), chosen);
    }

    [Fact]
    public void EveryChoiceWritesAValueTheSettingAccepts()
    {
        foreach (string value in IoStatsDisplay.HeaderGraphChoices)
            Assert.Equal(value, IoStatsDisplay.NormalizeHeaderGraph(value));
        Config config = new() { StatsHeaderGraph = IoStatsDisplay.HeaderGraphText };
        Assert.Equal(IoStatsDisplay.HeaderGraphText, Assert.Single(Model(config.StatsHeaderGraph).Sections[0], entry => entry.Checked).Id[(HeaderIoMenu.Group.Length + 1)..]);
    }

    [Fact]
    public void TheStatisticsAndSettingsEntriesAreCatalogCommandsWithTheirOwnLabels()
    {
        int statistics = 0, settings = 0;
        ContextMenuModel model = Model("Auto", statistics: () => statistics++, settings: () => settings++);
        Assert.Equal(2, model.Sections.Count);
        ContextMenuEntry[] actions = model.Sections[1].ToArray();
        Assert.Equal(new[] { "navigation.statistics", "navigation.settings" }, actions.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Open statistics", "Settings: Statistics…" }, actions.Select(entry => entry.Label));
        Assert.All(actions, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
        actions[0].Invoke!();
        actions[1].Invoke!();
        actions[1].Invoke!();
        Assert.Equal((1, 2), (statistics, settings));
        Assert.Empty(model.OversizedSections);
    }

    [Theory]
    [InlineData("en", "Network only", "Open statistics")]
    [InlineData("de", "Nur Netz", "Statistik öffnen")]
    public void TheChoicesUseTheTextsOfTheSettingsList(string language, string network, string openStatistics)
    {
        Texts.SetLanguage(language);
        ContextMenuModel model = Model("Auto");
        Assert.Equal(network, model.Sections[0].Single(entry => entry.Id == HeaderIoMenu.EntryId("Network")).Label);
        Assert.Equal(openStatistics, model.Sections[1][0].Label);
        // Every label is a real text, not a missing key.
        Assert.All(model.Entries, entry => Assert.DoesNotContain("header.io.", entry.Label));
        Assert.All(model.Entries, entry => Assert.DoesNotContain("stats.headerGraph.", entry.Label));
    }

    [Fact]
    public void TheContextKeyCommandNamesTheHeaderDisplayAndStaysConflictFree()
    {
        Assert.Contains("header-io-monitor", CommandCatalog.Get("menu.context-open").ExecutionContexts);
        Assert.Contains("header-io-monitor", CommandCatalog.Get("menu.context-open").Contexts);
        // The default gestures are unchanged: Shift+F10 (and the Menu key) and nothing else.
        Assert.Equal("Shift+F10", CommandCatalog.Get("menu.context-open").Shortcut);
    }
}
