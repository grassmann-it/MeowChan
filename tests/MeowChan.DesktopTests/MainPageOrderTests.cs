using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The footer, the More flyout, the ⋯ menu and the default digits all follow MainPageOrder.</summary>
[Collection("Localization")]
public sealed class MainPageOrderTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public MainPageOrderTests() => Texts.SetLanguage("en");

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] OrderedIds = MainPageOrder.CommandIds.ToArray();

    private static XDocument Markup() => XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Shell/MainWindow.axaml"));
    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");
    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private static string Stem(string tag) => char.ToUpperInvariant(tag[4]) + tag[5..];

    [Fact]
    public void TheNthFooterButtonIsTheNthMainPageAndHasDigitN()
    {
        XElement footer = Assert.Single(Markup().Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] names = footer.Elements().Select(Name).OfType<string>().Where(name => name.EndsWith("NavigationButton", StringComparison.Ordinal)).ToArray();
        string[] pages = names.TakeWhile(name => name != "ToolsNavigationButton").ToArray();
        Assert.Equal(OrderedIds.Select(id => MainPageOrder.ControlStem(id) + "NavigationButton"), pages);
        Assert.Equal(new[] { "ToolsNavigationButton", "SettingsNavigationButton" }, names.Skip(pages.Length));
        for (int index = 0; index < pages.Length; index++)
        {
            Assert.Equal("Primary+" + (index + 1), CommandCatalog.Get(OrderedIds[index]).Shortcut);
            Assert.Equal(index + 1, MainPageOrder.Digit(OrderedIds[index]));
        }
    }

    [Fact]
    public void TheMainPagesFormThreeGroupsInTheAgreedOrderAndTheFooterSetsThemApartWithSeparators()
    {
        Assert.Equal(new[] { "navigation.downloads", "navigation.archive", "navigation.media", "navigation.rules", "navigation.sources",
            "navigation.plugins", "navigation.status" }, OrderedIds);
        Assert.Equal(new[] { 3, 3, 1 }, MainPageOrder.Groups.Select(group => group.Count));
        Assert.Equal(new[] { 6, 7 }, new[] { "navigation.plugins", "navigation.status" }.Select(id => MainPageOrder.Digit(id)!.Value));
        // Diagnostics and Statistics are segments of Status, no main pages and no digits (Primary+8 is free).
        Assert.All(MainPageOrder.StatusSegmentCommandIds, id => Assert.Null(MainPageOrder.Digit(id)));
        XElement footer = Assert.Single(Markup().Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] separators = { "FooterSeparatorWork", "FooterSeparatorSetUp", "FooterSeparatorObserve" };
        var expected = new List<string>();
        for (int group = 0; group < MainPageOrder.Groups.Count; group++)
        {
            expected.AddRange(MainPageOrder.Groups[group].Select(id => MainPageOrder.ControlStem(id) + "NavigationButton"));
            expected.Add(separators[group]);
        }
        expected.AddRange(new[] { "ToolsNavigationButton", "SettingsNavigationButton" });
        Assert.Equal(expected, footer.Elements().Select(Name).OfType<string>());
        Assert.All(separators, name => Assert.Equal("Border", Assert.Single(footer.Elements(), e => Name(e) == name).Name.LocalName));
    }

    [Fact]
    public void TheMoreFlyoutListsTheOverflowPagesInGroupsAndTheWorkspaceMenuAllPagesIncludingDownloads()
    {
        XDocument markup = Markup();
        string[] flyoutPages = OrderedIds.Where(id => id != "navigation.downloads").Select(MainPageOrder.Tag).ToArray();
        XElement tools = Assert.Single(markup.Descendants(), e => Name(e) == "ToolsNavigationButton");
        Assert.Equal(flyoutPages, tools.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string?)e.Attribute("Tag")).OfType<string>());
        // Separators of the flyout stand exactly between the groups (Downloads is not in the flyout).
        string[] flyoutShape = tools.Descendants().Where(e => e.Name.LocalName is "MenuItem" or "Separator")
            .Select(e => e.Name.LocalName == "Separator" ? "|" : (string)e.Attribute("Tag")!).ToArray();
        Assert.Equal(new[] { "nav.archive", "nav.media", "|", "nav.rules", "nav.sources", "nav.plugins", "|", "nav.status" }, flyoutShape);
        Assert.Equal(new[] { "nav.group-separator.0", "nav.group-separator.1" },
            tools.Descendants().Where(e => e.Name.LocalName == "Separator").Select(e => (string?)e.Attribute("Tag")).OfType<string>());

        XElement menu = Assert.Single(markup.Descendants(), e => Name(e) == "WorkspaceMenuItem");
        string[] allPages = OrderedIds.Select(MainPageOrder.Tag).ToArray();
        Assert.Equal(allPages, menu.Elements().Select(e => (string?)e.Attribute("Tag")).Where(tag => tag != null && allPages.Contains(tag)).OfType<string>());
        Assert.Equal(allPages.Select(tag => Stem(tag) + "MenuItem"),
            menu.Elements().Select(Name).OfType<string>().Where(name => allPages.Any(tag => name == Stem(tag) + "MenuItem")));
        // The pages come first, set apart from the tools by a separator and in the same three groups.
        string[] menuShape = menu.Elements().Select(e => e.Name.LocalName == "Separator" ? "|" : Name(e) ?? "?")
            .TakeWhile(item => item != "ManualImportMenuItem").ToArray();
        string[] pageShape = OrderedIds.Select(id => MainPageOrder.ControlStem(id) + "MenuItem").ToArray();
        Assert.Equal(new[] { pageShape[0], pageShape[1], pageShape[2], "|", pageShape[3], pageShape[4], pageShape[5], "|", pageShape[6], "|" }, menuShape);
        // Tools, then the application's own entries, then Exit.
        string[] rest = menu.Elements().Select(e => e.Name.LocalName == "Separator" ? "|" : Name(e) ?? "?").SkipWhile(item => item != "ManualImportMenuItem").ToArray();
        Assert.Equal(new[] { "ManualImportMenuItem", "ReaderGroupsMenuItem", "|", "PauseMenuItem", "SettingsMenuItem", "ShortcutHelpMenuItem", "AboutMenuItem", "|", "ExitMenuItem" }, rest);
    }

    [Fact]
    public void EveryMainPageHasADistinctWorkspacePageAndTheOverflowOrderCoversAllButDownloads()
    {
        Assert.Equal(OrderedIds.Length, OrderedIds.Select(id => MainWindow.PageFor(id)).Distinct().Count());
        Assert.Equal(OrderedIds.Where(id => id != "navigation.downloads").Order(), MainPageOrder.OverflowOrder.Order());
        foreach (string id in OrderedIds) Assert.True(CommandCatalog.TryGet(id, out _), id);
        Assert.Throws<ArgumentException>(() => MainPageOrder.DefaultShortcut("navigation.settings"));
    }

    [Fact]
    public void TheHelpAndTooltipsReadTheSameDigitsAsTheCatalog()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        for (int index = 0; index < OrderedIds.Length; index++)
            Assert.Equal("Ctrl+" + (index + 1), bindings.Display(OrderedIds[index]));
        Assert.Empty(bindings.Issues);
    }
}
