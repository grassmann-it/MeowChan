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
    public void TheNthFooterButtonIsTheNthMainPageAndItsDigitIsTheShippedOne()
    {
        XElement footer = Assert.Single(Markup().Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] names = footer.Elements().Select(Name).OfType<string>().Where(name => name.EndsWith("NavigationButton", StringComparison.Ordinal)).ToArray();
        string[] pages = names.TakeWhile(name => name != "ToolsNavigationButton").ToArray();
        Assert.Equal(OrderedIds.Select(id => MainPageOrder.ControlStem(id) + "NavigationButton"), pages);
        Assert.Equal(new[] { "ToolsNavigationButton", "SettingsNavigationButton" }, names.Skip(pages.Length));
        // The digits follow the visible footer buttons from the left (F1): with the default layout Booru is hidden, so it takes the digit
        // behind the last visible button and Tagging owns 4. Status keeps its confirmed Primary+7.
        int?[] digits = [1, 2, 3, 4, 8, 5, 6, 7];
        Assert.Equal(digits.Length, pages.Length);
        for (int index = 0; index < pages.Length; index++)
        {
            Assert.Equal(digits[index], MainPageOrder.Digit(OrderedIds[index]));
            Assert.Equal("Primary+" + digits[index], MainPageOrder.DefaultShortcut(OrderedIds[index]));
            // The catalog holds no fixed digit any more: the default gesture is derived from the numbering.
            Assert.Null(CommandCatalog.Get(OrderedIds[index]).Shortcut);
        }
        Assert.Equal(MainPageOrder.DefaultNumbering, MainPageOrder.NumberedCommandIds);
        Assert.Equal(OrderedIds.Order(), MainPageOrder.NumberedCommandIds.Order());
    }

    [Fact]
    public void TheMainPagesFormThreeGroupsInTheAgreedOrderAndTheFooterSetsThemApartWithSeparators()
    {
        Assert.Equal(new[] { "navigation.downloads", "navigation.archive", "navigation.media", "navigation.tagging", "navigation.booru", "navigation.rules",
            "navigation.sources", "navigation.status" }, OrderedIds);
        Assert.Equal(new[] { 5, 2, 1 }, MainPageOrder.Groups.Select(group => group.Count));
        Assert.Null(MainPageOrder.Digit("navigation.plugins"));
        Assert.Equal(7, MainPageOrder.Digit("navigation.status"));
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
    public void TheMoreFlyoutListsTheOverflowPagesInGroupsAndTheWorkspaceMenuHoldsNoPages()
    {
        XDocument markup = Markup();
        string[] flyoutPages = OrderedIds.Where(id => id != "navigation.downloads").Select(MainPageOrder.Tag).ToArray();
        XElement tools = Assert.Single(markup.Descendants(), e => Name(e) == "ToolsNavigationButton");
        Assert.Equal(flyoutPages, tools.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string?)e.Attribute("Tag")).OfType<string>());
        // Separators of the flyout stand exactly between the groups (Downloads is not in the flyout).
        string[] flyoutShape = tools.Descendants().Where(e => e.Name.LocalName is "MenuItem" or "Separator")
            .Select(e => e.Name.LocalName == "Separator" ? "|" : (string)e.Attribute("Tag")!).ToArray();
        Assert.Equal(new[] { "nav.archive", "nav.media", "nav.tagging", "nav.booru", "|", "nav.rules", "nav.sources", "|", "nav.status" }, flyoutShape);
        Assert.Equal(new[] { "nav.group-separator.0", "nav.group-separator.1" },
            tools.Descendants().Where(e => e.Name.LocalName == "Separator").Select(e => (string?)e.Attribute("Tag")).OfType<string>());

        // F1 (Tom, 14.11.2026): the ⋯ menu holds tools and the application only. The pages are reached through the footer, its More menu
        // (every page the person or a work view left out of the footer) and the Ctrl/Cmd+digit shortcuts - not through a page group here.
        XElement menu = Assert.Single(markup.Descendants(), e => Name(e) == "WorkspaceMenuItem");
        string[] allPages = OrderedIds.Select(MainPageOrder.Tag).ToArray();
        Assert.DoesNotContain(menu.Elements(), e => allPages.Contains((string?)e.Attribute("Tag") ?? ""));
        Assert.DoesNotContain(menu.Elements(), e => allPages.Any(tag => Name(e) == Stem(tag) + "MenuItem") || Name(e) is "PagesGroupSeparatorWork" or "PagesGroupSeparatorSetUp" or "MenuPagesToolsSeparator");
        Assert.Empty(menu.Elements().Where(e => Name(e)?.StartsWith("Pages", StringComparison.Ordinal) == true));
        Assert.Equal("ManualImportMenuItem", Name(menu.Elements().First())); // the tool entries insert themselves in front of it (MainWindow.MenuToolsAnchor)
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
        foreach (string id in MainPageOrder.NumberedCommandIds)
            Assert.StartsWith("Ctrl+" + MainPageOrder.Digit(id), bindings.Display(id), StringComparison.Ordinal);   // the digit first, then the fixed page letter
        Assert.Equal(OrderedIds.Length, MainPageOrder.NumberedCommandIds.Count);
        Assert.Equal("Ctrl+4 / Ctrl+Shift+K", bindings.Display("navigation.tagging"));
        Assert.Empty(bindings.Issues);
    }
}
