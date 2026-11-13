using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The statistics page is the new last main page: Primary+8, no existing digit moved, wired in the shell's markup.</summary>
[Collection("Localization")]
public sealed class StatisticsNavigationTests : IDisposable
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Earlier =
        ["navigation.downloads", "navigation.archive", "navigation.media", "navigation.rules", "navigation.sources", "navigation.diagnostics", "navigation.plugins"];

    public StatisticsNavigationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void StatisticsIsTheLastMainPageAndTheEarlierSevenKeepTheirPlaces()
    {
        Assert.Equal([.. Earlier, "navigation.statistics"], MainPageOrder.CommandIds);
        for (int index = 0; index < Earlier.Length; index++) Assert.Equal(index + 1, MainPageOrder.Digit(Earlier[index]));
        Assert.Equal(8, MainPageOrder.Digit("navigation.statistics"));
        Assert.Equal("Primary+8", MainPageOrder.DefaultShortcut("navigation.statistics"));
        Assert.Equal("Primary+8", CommandCatalog.Get("navigation.statistics").Shortcut);
        Assert.Equal("nav.statistics", MainPageOrder.Tag("navigation.statistics"));
        Assert.Equal("Statistics", MainPageOrder.ControlStem("navigation.statistics"));
    }

    [Fact]
    public void StatisticsLeavesTheFooterForTheToolsMenuBeforeAnyOtherPage()
    {
        Assert.Equal("navigation.statistics", MainPageOrder.OverflowOrder[0]);
        Assert.Equal(MainPageOrder.CommandIds.Where(id => id != "navigation.downloads").Order(), MainPageOrder.OverflowOrder.Order());
    }

    [Fact]
    public void StatisticsOpensItsOwnWorkspacePage()
    {
        Assert.Equal(MainWindow.WorkspacePage.Statistics, MainWindow.PageFor("navigation.statistics"));
        Assert.Equal(MainPageOrder.CommandIds.Count, MainPageOrder.CommandIds.Select(id => MainWindow.PageFor(id)).Distinct().Count());
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Ctrl+8")]
    [InlineData(ShortcutPlatform.Windows, "Ctrl+8")]
    [InlineData(ShortcutPlatform.MacOS, "⌘8")]
    public void TheShortcutIsPrimaryPlusEightWithoutAConflict(ShortcutPlatform platform, string display)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Equal(display, bindings.Display("navigation.statistics"));
        Assert.Empty(bindings.Issues);
        ShortcutGesture gesture = Assert.Single(bindings.For("navigation.statistics"));
        foreach (var command in CommandCatalog.All.Where(command => command.Id != "navigation.statistics"))
            Assert.DoesNotContain(bindings.For(command.Id), other => EffectiveShortcutBindings.Equivalent(gesture, other, platform));
    }

    [Fact]
    public void ANewDefaultOnTheSameKeyYieldsToASavedBinding()
    {
        Assert.Contains("navigation.statistics", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        var section = new ShortcutBindingsSection().Set("thread.read", [ShortcutGesture.Parse("Primary+8")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("navigation.statistics"));
        Assert.Equal(1, bindings.For("thread.read").Count);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Theory]
    [InlineData("en", "Statistics")]
    [InlineData("de", "Statistik")]
    [InlineData("es", "Estadísticas")]
    [InlineData("fr", "Statistiques")]
    [InlineData("ru", "Статистика")]
    public void TheCommandAndTheNavigationLabelAreTranslatedInAllFiveLanguages(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CommandCatalog.Label("navigation.statistics"));
        Assert.Equal(expected, Texts.T("nav.statistics"));
    }

    [Fact]
    public void TheShellMarkupHasTheButtonTheMenusAndThePageHostInTheMainPageOrder()
    {
        XDocument markup = XDocument.Load(Source("Desktop/Shell/MainWindow.axaml"));
        string? Name(XElement e) => (string?)e.Attribute(X + "Name");
        XElement footer = Assert.Single(markup.Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] buttons = footer.Elements().Select(Name).OfType<string>().ToArray();
        Assert.Equal("PluginsNavigationButton", buttons[^4]);
        Assert.Equal("StatisticsNavigationButton", buttons[^3]);
        Assert.Equal(["ToolsNavigationButton", "SettingsNavigationButton"], buttons[^2..]);
        Assert.Contains(markup.Descendants(), e => Name(e) == "StatisticsPageHost");
        Assert.Contains(markup.Descendants(), e => Name(e) == "StatisticsMenuItem");
        XElement tools = Assert.Single(markup.Descendants(), e => Name(e) == "ToolsNavigationButton");
        Assert.Equal("nav.statistics", tools.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string?)e.Attribute("Tag")).Last());
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
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}
