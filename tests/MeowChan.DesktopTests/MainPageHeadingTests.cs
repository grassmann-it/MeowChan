using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>
/// M10 across the workspace: every main page names itself with a heading of the first level ("page-title", which
/// HeadingLevels turns into AutomationProperties.HeadingLevel 1), so a screen reader lists one page heading per page
/// and the sections below it as level two. AccessibilityAuditTests covers the two styles themselves.
/// This reads the sources that build the titles; the rendered tree is covered by the accessibility smoke.
/// </summary>
public sealed class MainPageHeadingTests
{
    // The file that builds each main page's own title, in footer order.
    private static readonly (string CommandId, string File)[] Pages =
    {
        ("navigation.downloads", "Desktop/Shell/MainWindow.axaml"),
        ("navigation.archive", "Desktop/Views/Archive/ArchivePage.axaml"),
        ("navigation.media", "Desktop/Views/Media/MediaPage.cs"),
        ("navigation.rules", "Desktop/Views/Rules/RulesPage.axaml"),
        ("navigation.sources", "Desktop/Views/Plugins/PluginsPage.axaml"),
        ("navigation.status", "Desktop/Views/Status/StatusPage.axaml"),
    };

    [Fact]
    public void EveryNumberedMainPageHasATitleFile() =>
        Assert.Equal(MainPageOrder.CommandIds, Pages.Select(page => page.CommandId));

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void EveryMainPageTitlesItselfWithAFirstLevelHeading(string file)
    {
        string source = File.ReadAllText(Root(file));
        Assert.Contains("page-title", source);
    }

    public static TheoryData<string> PageFiles()
    {
        TheoryData<string> data = new();
        foreach ((_, string file) in Pages) data.Add(file);
        return data;
    }

    [Fact]
    public void TheMediaPageTitleIsAPageTitleLikeTheOtherPagesAndNotASectionTitle()
    {
        string source = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.cs"));
        Match declaration = Regex.Match(source, @"TextBlock _title = new\(\) \{ Classes = \{ ""(?<class>[a-z-]+)"" \} \}");
        Assert.True(declaration.Success, "The media page's title is not declared as expected any more.");
        Assert.Equal("page-title", declaration.Groups["class"].Value);
    }

    [Fact]
    public void ThePagesThatAreXamlCarryTheClassOnTheirTitleTextBlock()
    {
        XNamespace a = "https://github.com/avaloniaui";
        foreach ((_, string file) in Pages.Where(page => page.File.EndsWith(".axaml", StringComparison.Ordinal)))
        {
            XDocument markup = XDocument.Load(Root(file));
            XElement[] titles = markup.Descendants().Where(element => element.Name.Namespace == a
                && element.Name.LocalName is "TextBlock" or "SelectableTextBlock"
                && ((string?)element.Attribute("Classes"))?.Split(' ').Contains("page-title") == true).ToArray();
            Assert.True(titles.Length > 0, file + " has no page title.");
        }
    }

    private static string Root(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
