using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// UX-DESIGNSYSTEM P2: page grid. Header rhythm (4 title to subtitle), card gap 12, form gap 24, divider rhythm 8.
// Parsed sources only; no Avalonia application.
public sealed class PageGridStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Workspace.Space.Header", 4)]
    [InlineData("Workspace.Space.Card", 12)]
    [InlineData("Workspace.Form.Gap", 24)]
    [InlineData("Workspace.Form.ValueColumn", 280)]
    public void PageGridTokensHaveTheirSpecValues(string key, double expected)
    {
        XElement token = Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Descendants(X + "Double"),
            element => (string?)element.Attribute(X + "Key") == key);
        Assert.Equal(expected, double.Parse(token.Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void PageHeaderIsTitleAndSubtitleFourApartAndSectionHeaderStaysEight()
    {
        Assert.Equal("{StaticResource Workspace.Space.Header}", Setting("StackPanel.workspace-page-header", "Spacing"));
        Assert.Equal("{StaticResource Workspace.Space.8}", Setting("StackPanel.workspace-section-header", "Spacing"));
    }

    [Fact]
    public void DividerRowsHaveEightAboveAndBelowWithoutAPerPageOverride()
    {
        Assert.Equal("0,8", Setting("Border.settings-divider", "Padding"));
        Assert.DoesNotContain(XDocument.Load(Root("Desktop/Views/Rules/RulesPage.axaml")).Descendants(A + "Style"),
            style => ((string?)style.Attribute("Selector") ?? "").Contains("settings-divider"));
    }

    [Theory]
    [InlineData("Desktop/Views/Statistics/StatisticsPage.axaml")]
    [InlineData("Desktop/Views/Diagnostics/DiagnosticsPage.axaml")]
    [InlineData("Desktop/Views/Archive/ArchivePage.axaml")]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml")]
    [InlineData("Desktop/Views/Settings/SettingsEditor.axaml")]
    public void EveryPageTitleSitsInAWorkspacePageHeader(string file)
    {
        XElement title = XDocument.Load(Root(file)).Descendants(A + "TextBlock")
            .First(text => ((string?)text.Attribute("Classes") ?? "").Split(' ').Contains("page-title"));
        XElement header = title.Parent!;
        Assert.Contains("workspace-page-header", ((string?)header.Attribute("Classes") ?? "").Split(' '));
        Assert.Null(header.Attribute("Spacing")); // the class owns the 4 dip, no literal 5 per page
    }

    [Theory]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml", "SourcesSplit", "ColumnSpacing")]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml", "SourcesListPane", "Spacing")]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml", "PluginSplit", "ColumnSpacing")]
    public void ListAndDetailColumnsAndListCardsUseTheCardGap(string file, string name, string attribute)
    {
        XElement element = Assert.Single(XDocument.Load(Root(file)).Descendants(), e => (string?)e.Attribute(X + "Name") == name);
        Assert.Equal("{StaticResource Workspace.Space.Card}", (string?)element.Attribute(attribute));
    }

    [Fact]
    public void RulesFormRowsUseTheFormGapAndTheFooterIsOutsideThePageInset()
    {
        string rules = File.ReadAllText(Root("Desktop/Views/Rules/RulesPage.axaml"));
        Assert.DoesNotContain("ColumnSpacing=\"24\"", rules);
        Assert.DoesNotContain("ColumnSpacing=\"32\"", rules);
        XElement page = XDocument.Load(Root("Desktop/Views/Rules/RulesPage.axaml")).Root!.Elements(A + "Grid").Single();
        XElement area = page.Elements(A + "Grid").Single();
        Assert.Equal("{StaticResource Workspace.Inset.Page}", (string?)area.Attribute("Margin"));
        Assert.Null(page.Attribute("Margin"));
        Assert.Equal("1", (string?)page.Elements(A + "Border").Single(border => ((string?)border.Attribute("Classes") ?? "").Contains("workspace-dialog-footer")).Attribute("Grid.Row"));
    }

    private static XElement Style(string selector) =>
        Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style").Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
