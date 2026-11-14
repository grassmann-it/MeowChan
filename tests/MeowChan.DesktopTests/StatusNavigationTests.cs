using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>
/// Statistics and Diagnostics are the two segments of one Status page: the last main page, Primary+7, wired in the shell's
/// markup. The old page commands stay as commands without a digit (Primary+8 is free) that open their segment, so a binding the
/// user saved for them keeps working.
/// </summary>
[Collection("Localization")]
public sealed class StatusNavigationTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Earlier =
        ["navigation.downloads", "navigation.archive", "navigation.media", "navigation.rules", "navigation.sources", "navigation.plugins"];

    public StatusNavigationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void StatusIsTheLastMainPageAndTheEarlierSixFollowTheFooterGroups()
    {
        Assert.Equal([.. Earlier, "navigation.status"], MainPageOrder.CommandIds);
        for (int index = 0; index < Earlier.Length; index++) Assert.Equal(index + 1, MainPageOrder.Digit(Earlier[index]));
        Assert.Equal(7, MainPageOrder.Digit("navigation.status"));
        Assert.Equal("Primary+7", MainPageOrder.DefaultShortcut("navigation.status"));
        Assert.Equal("Primary+7", CommandCatalog.Get("navigation.status").Shortcut);
        Assert.Equal("nav.status", MainPageOrder.Tag("navigation.status"));
        Assert.Equal("Status", MainPageOrder.ControlStem("navigation.status"));
        Assert.Equal("activity", CommandCatalog.Get("navigation.status").Icon);
    }

    [Fact]
    public void TheSegmentCommandsStayWithoutADigitAndPrimaryPlusEightIsFree()
    {
        Assert.Equal(["navigation.statistics", "navigation.diagnostics"], MainPageOrder.StatusSegmentCommandIds);
        foreach (string id in MainPageOrder.StatusSegmentCommandIds)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.False(MainPageOrder.Contains(id));
            Assert.Null(command.Shortcut);
            Assert.True(command.IsImplemented);
            Assert.True(CommandCatalog.HasGenericScope(id));
        }
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            var bindings = EffectiveShortcutBindings.Build(platform: platform);
            Assert.All(MainPageOrder.StatusSegmentCommandIds, id => Assert.Empty(bindings.For(id)));
            ShortcutGesture eight = ShortcutGesture.Parse("Primary+8");
            Assert.DoesNotContain(CommandCatalog.All, command => bindings.For(command.Id).Any(g => EffectiveShortcutBindings.Equivalent(eight, g, platform)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => MainWindow.PageFor("navigation.statistics"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MainWindow.PageFor("navigation.diagnostics"));
    }

    [Fact]
    public void StatusLeavesTheFooterForTheMoreMenuBeforeAnyOtherPage()
    {
        Assert.Equal("navigation.status", MainPageOrder.OverflowOrder[0]);
        Assert.Equal(MainPageOrder.CommandIds.Where(id => id != "navigation.downloads").Order(), MainPageOrder.OverflowOrder.Order());
    }

    [Fact]
    public void StatusOpensItsOwnWorkspacePage()
    {
        Assert.Equal(MainWindow.WorkspacePage.Status, MainWindow.PageFor("navigation.status"));
        Assert.Equal(MainPageOrder.CommandIds.Count, MainPageOrder.CommandIds.Select(id => MainWindow.PageFor(id)).Distinct().Count());
        Assert.DoesNotContain("Diagnostics", Enum.GetNames<MainWindow.WorkspacePage>());
        Assert.DoesNotContain("Statistics", Enum.GetNames<MainWindow.WorkspacePage>());
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+7")]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+7")]
    [InlineData(ShortcutPlatform.MacOS, "⌘7")]
    public void TheShortcutIsPrimaryPlusSevenWithoutAConflict(ShortcutPlatform platform, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display("navigation.status"));
        Assert.Empty(bindings.Issues);
        ShortcutGesture gesture = Assert.Single(bindings.For("navigation.status"));
        foreach (var command in CommandCatalog.All.Where(command => command.Id != "navigation.status"))
            Assert.DoesNotContain(bindings.For(command.Id), other => EffectiveShortcutBindings.Equivalent(gesture, other, platform));
    }

    [Fact]
    public void TheNewDefaultYieldsToASavedBindingOnTheSameKey()
    {
        Assert.Contains("navigation.status", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 7);
        var section = new ShortcutBindingsSection().Set("thread.read", [ShortcutGesture.Parse("Primary+7")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("navigation.status"));
        Assert.Equal(1, bindings.For("thread.read").Count);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    // A profile that saved the old digits for Diagnostics (Primary+7) and Statistics (Primary+8) keeps both: the segment
    // commands own them, Status yields its new default instead of silencing either.
    [Fact]
    public void SavedBindingsOfTheOldPageCommandsKeepWorking()
    {
        var section = new ShortcutBindingsSection()
            .Set("navigation.diagnostics", [ShortcutGesture.Parse("Primary+7")])
            .Set("navigation.statistics", [ShortcutGesture.Parse("Primary+8")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+7", bindings.Display("navigation.diagnostics"));
        Assert.Equal("Ctrl+8", bindings.Display("navigation.statistics"));
        Assert.Empty(bindings.For("navigation.status"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "navigation.status" && issue.OtherCommandId == "navigation.diagnostics"
            && issue.Reason?.Code == "defaultYields");
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Theory]
    [InlineData("en", "Status")]
    [InlineData("de", "Status")]
    [InlineData("es", "Estado")]
    [InlineData("fr", "État")]
    [InlineData("ru", "Состояние")]
    [InlineData("it", "Stato")]
    [InlineData("pl", "Stan")]
    [InlineData("cs", "Stav")]
    [InlineData("pt-BR", "Status")]
    [InlineData("ja", "状態")]
    [InlineData("ko", "상태")]
    [InlineData("zh-Hans", "状态")]
    public void TheCommandAndTheNavigationLabelAreTranslatedInAllTwelveLanguages(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CommandCatalog.Label("navigation.status"));
        Assert.Equal(expected, Texts.T("nav.status"));
    }

    [Theory]
    [InlineData("en", "Statistics")]
    [InlineData("de", "Statistik")]
    [InlineData("es", "Estadísticas")]
    [InlineData("fr", "Statistiques")]
    [InlineData("ru", "Статистика")]
    public void TheStatisticsSegmentKeepsItsTranslatedName(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CommandCatalog.Label("navigation.statistics"));
        Assert.Equal(expected, Texts.T("nav.statistics"));
    }

    [Fact]
    public void TheShellMarkupHasOneStatusButtonOneMenuEntryAndOneHostInTheMainPageOrder()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Shell/MainWindow.axaml"));
        string? Name(XElement e) => (string?)e.Attribute(X + "Name");
        XElement footer = Assert.Single(markup.Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] buttons = footer.Elements().Select(Name).OfType<string>().Where(name => name.EndsWith("NavigationButton", StringComparison.Ordinal)).ToArray();
        Assert.Equal("StatusNavigationButton", buttons[^3]);
        Assert.Equal(["ToolsNavigationButton", "SettingsNavigationButton"], buttons[^2..]);
        Assert.Contains(markup.Descendants(), e => Name(e) == "StatusPageHost");
        Assert.Contains(markup.Descendants(), e => Name(e) == "StatusMenuItem");
        foreach (string gone in new[] { "DiagnosticsNavigationButton", "StatisticsNavigationButton", "DiagnosticsMenuItem", "StatisticsMenuItem",
            "DiagnosticsPageHost", "StatisticsPageHost" })
            Assert.DoesNotContain(markup.Descendants(), e => Name(e) == gone);
        XElement tools = Assert.Single(markup.Descendants(), e => Name(e) == "ToolsNavigationButton");
        string[] flyout = tools.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string?)e.Attribute("Tag")).OfType<string>().ToArray();
        Assert.Equal("nav.status", flyout.Last());
        Assert.DoesNotContain("nav.diagnostics", flyout);
        Assert.DoesNotContain("nav.statistics", flyout);
    }

    [Fact]
    public void TheStatusPageHasTheSegmentBarWithStatisticsFirstAndBothHosts()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Views/Status/StatusPage.axaml"));
        string? Name(XElement e) => (string?)e.Attribute(X + "Name");
        XElement bar = Assert.Single(markup.Descendants(), e => Name(e) == "StatusSegments");
        Assert.Equal("TabControl", bar.Name.LocalName);
        Assert.Equal(["StatisticsSegmentTab", "DiagnosticsSegmentTab"], bar.Elements().Where(e => e.Name.LocalName == "TabItem").Select(Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, new[] { StatusSegment.Statistics, StatusSegment.Diagnostics }.Select(segment => (int)segment));
        foreach (string host in new[] { "StatisticsPageHost", "DiagnosticsPageHost" })
            Assert.Equal("ContentControl", Assert.Single(markup.Descendants(), e => Name(e) == host).Name.LocalName);
        Assert.Contains(markup.Descendants(), e => Name(e) == "StatusStrip");
    }

    [Fact]
    public void TheHeaderGraphIsAButtonThatOpensTheStatisticsAndKeepsTheNamesTheSmokesUse()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Shell/MainWindow.axaml"));
        string? Name(XElement e) => (string?)e.Attribute(X + "Name");
        XElement speed = Assert.Single(markup.Descendants(), e => Name(e) == "TransferSpeed");
        Assert.Equal("Button", speed.Name.LocalName);
        Assert.Equal("OnTransferSpeedClick", (string?)speed.Attribute("Click"));
        string[] inner = speed.Descendants().Select(Name).OfType<string>().ToArray();
        Assert.Contains("TransferSpeedGraph", inner);
        Assert.Contains("TransferSpeedLabel", inner);
        Assert.Contains("TransferDiskLabel", inner);
        Assert.Contains("TransferDiskGraph", inner);
        Assert.Contains("TransferLayoutGrid", inner);
        // Exactly two header graph surfaces: network (download + upload) and disk (write + read), never a combined sum.
        XElement[] graphs = speed.Descendants().Where(e => e.Name.LocalName == "IoGraph").ToArray();
        Assert.Equal(["DiskIo", "NetworkIo"], graphs.Select(e => (string?)e.Attribute("Source")).OrderBy(x => x).ToArray());
        Assert.Equal(2, graphs.Length);
    }

    [Fact]
    public void TheStatisticsPageDrawsNetworkWriteAndReadAsThreeSeparateGraphs()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Views/Statistics/StatisticsPage.axaml"));
        XElement[] graphs = markup.Descendants().Where(e => e.Name.LocalName == "IoGraph").ToArray();
        Assert.Equal(["DiskRead", "DiskWrite", "NetDown"], graphs.Select(e => (string?)e.Attribute("Source")).OrderBy(x => x).ToArray());
        foreach (string name in new[] { "NetTitle", "NetScale", "DiskWriteTitle", "DiskWriteScale", "DiskReadTitle", "DiskReadScale" })
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == name);
    }

    [Fact]
    public void TheStatusLineCountsAndAddsTheRatesOnlyWhenTheyAreKnown()
    {
        IoRates rates = new(NetDown: 2.4 * 1024 * 1024, NetUp: 0, DiskWrite: 1.9 * 1024 * 1024, DiskRead: 0, Requests: 0);
        Assert.Equal("Active 3 · Waiting 41 · Problems 2", StatusPage.ComposeStrip(3, 41, 2, null));
        Assert.Equal("Active 3 · Waiting 41 · Problems 2 · ↓ 2.4 MiB/s · W 1.9 MiB/s", StatusPage.ComposeStrip(3, 41, 2, rates));
        Texts.SetLanguage("de");
        Assert.Equal("Aktiv 0 · Wartend 0 · Probleme 0 · ↓ 2,4 MiB/s · S 1,9 MiB/s", StatusPage.ComposeStrip(0, 0, 0, rates));
    }

    [Fact]
    public void TheDiagnosticsSegmentNamesTheNumberOfProblems()
    {
        Assert.Equal("Diagnostics", StatusPage.DiagnosticsHeader(0));
        Assert.Equal("Diagnostics (2)", StatusPage.DiagnosticsHeader(2));
        Texts.SetLanguage("de");
        Assert.Equal("Diagnose (5)", StatusPage.DiagnosticsHeader(5));
    }

    // Decision 4: the footer opens the segment used last, per profile. Not a setting; anything unknown reads as Statistics.
    [Fact]
    public void TheLastUsedSegmentIsAProfilePreferenceThatSurvivesCloneAndJson()
    {
        Config config = new();
        Assert.Equal(Config.StatusPageSegmentStatistics, config.StatusPageSegment);
        config.StatusPageSegment = "unknown";
        Assert.Equal(Config.StatusPageSegmentStatistics, config.StatusPageSegment);
        config.StatusPageSegment = Config.StatusPageSegmentDiagnostics;
        Assert.Equal(Config.StatusPageSegmentDiagnostics, config.Clone().StatusPageSegment);
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config);
        Assert.Contains("StatusPageSegment", json);
        Config read = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;
        Assert.Equal(Config.StatusPageSegmentDiagnostics, read.StatusPageSegment);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
