using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>The footer, the Tools flyout, the ⋯ menu and the default digits all follow MainPageOrder.</summary>
public sealed class MainPageOrderTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] OrderedIds = MainPageOrder.CommandIds.ToArray();

    private static XDocument Markup() => XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Shell/MainWindow.axaml"));
    private static string? Name(XElement e) => (string?)e.Attribute(X + "Name");
    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    [Fact]
    public void TheNthFooterButtonIsTheNthMainPageAndHasDigitN()
    {
        XElement footer = Assert.Single(Markup().Descendants(), e => Name(e) == "WorkspaceNavigationButtons");
        string[] names = footer.Elements().Select(Name).OfType<string>().ToArray();
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
    public void TheToolsFlyoutAndTheWorkspaceMenuListThePagesInTheSameOrder()
    {
        XDocument markup = Markup();
        string[] expected = OrderedIds.Where(id => id != "navigation.downloads").Select(MainPageOrder.Tag).ToArray();
        XElement tools = Assert.Single(markup.Descendants(), e => Name(e) == "ToolsNavigationButton");
        Assert.Equal(expected, tools.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string?)e.Attribute("Tag")).OfType<string>());
        XElement menu = Assert.Single(markup.Descendants(), e => Name(e) == "WorkspaceMenuItem");
        Assert.Equal(expected, menu.Elements().Select(e => (string?)e.Attribute("Tag")).Where(tag => tag != null && expected.Contains(tag)).OfType<string>());
        Assert.Equal(expected.Select(tag => char.ToUpperInvariant(tag[4]) + tag[5..] + "MenuItem"),
            menu.Elements().Select(Name).OfType<string>().Where(name => expected.Any(tag => name.StartsWith(char.ToUpperInvariant(tag[4]) + tag[5..], StringComparison.Ordinal))));
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
