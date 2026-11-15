using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Toolbar UX review 15.11.2026: flat bar items with an 8-dip gap owned by ActionToolBarPanel, 16 with the group line between groups, the
/// List/Table toggle as the one joined segment pair, no local margin patches. The look is the toolbar smokes'; here the contract of the
/// tokens, the XAML and the styles.
/// </summary>
public sealed class ToolbarGapPatternTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    private static XElement Toolbar(string file, string name) => XDocument.Load(Root(file)).Descendants()
        .Single(element => element.Name.LocalName == "ActionToolBar" && (string?)element.Attribute(X + "Name") == name);

    private static XElement[] Items(XElement toolbar) => toolbar.Elements().Where(element => !element.Name.LocalName.Contains('.')).ToArray();

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    [Fact]
    public void TheBarTokensAreThePanelsGaps()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains($"<x:Double x:Key=\"Workspace.Space.BarGap\">{ActionToolBarPanel.ItemGap}</x:Double>", styles);
        Assert.Contains($"<x:Double x:Key=\"Workspace.Space.BarGroupGap\">{ActionToolBarPanel.GroupGap}</x:Double>", styles);
    }

    [Fact]
    public void TheArchiveResultsBarStandsInThreeGroupsWithTheToggleAsJoinedSegmentsAndSavedSearchesBesideExport()
    {
        XElement[] items = Items(Toolbar("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveResultActions"));
        Assert.Equal(["ArchiveLayoutList", "ArchiveLayoutTable", "ArchiveResultsGallery", "ArchiveThreadColumnsButton", "ArchiveFavoritesOverview",
            "ArchiveResultsExport", "ArchiveSavedSearches", "ArchiveReaderGroups"], items.Select(item => (string)item.Attribute(X + "Name")!));
        Assert.Equal([0, 0, 1, 1, 1, 2, 2, 2], items.Select(item => int.Parse(Attribute(item, "ActionToolBar.Group") ?? "0")));
        Assert.Equal("segment segment-first", (string?)items[0].Attribute("Classes"));
        Assert.Equal("segment segment-last", (string?)items[1].Attribute("Classes"));
        foreach (XElement toggle in items.Take(2))
        {
            Assert.Null(toggle.Attribute("Padding"));
            Assert.Equal("64", (string?)toggle.Attribute("MinWidth"));
            Assert.Equal("32", (string?)toggle.Attribute("MinHeight"));
        }
        // Post bookmarks is added in code at the end, into the last group.
        Assert.Contains("ActionToolBar.SetGroup(manager, 2);", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.PostBookmarks.cs")));
    }

    [Theory]
    [InlineData("ReaderToolbar")]
    [InlineData("ReaderMediaTools")]
    [InlineData("ReaderMediaActions")]
    [InlineData("ArchiveResultActions")]
    [InlineData("ArchiveHeaderActions")]
    public void NoArchiveBarPatchesTheGapWithALocalMargin(string name)
    {
        XElement toolbar = Toolbar("Desktop/Views/Archive/ArchivePage.axaml", name);
        Assert.DoesNotContain(toolbar.Descendants(), element => element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") == "Margin");
        Assert.All(Items(toolbar), item => Assert.Null(item.Attribute("Margin")));
    }

    [Fact]
    public void TheReaderBarKeepsItsControlSizeWithoutTheMargin()
    {
        XElement[] setters = Toolbar("Desktop/Views/Archive/ArchivePage.axaml", "ReaderToolbar").Descendants().Where(element => element.Name.LocalName == "Setter").ToArray();
        Assert.Equal(["MinWidth", "Height"], setters.Select(setter => (string)setter.Attribute("Property")!));
    }

    [Fact]
    public void TheDownloadsBarIsFlat()
    {
        XElement[] items = Items(Toolbar("Desktop/Shell/MainWindow.axaml", "WorkspaceListActions"));
        Assert.Equal(4, items.Length);
        Assert.All(items, item => Assert.DoesNotContain("subtle", ((string?)item.Attribute("Classes") ?? "").Split(' ')));
    }

    [Fact]
    public void TheFlatItemStylesLeaveTheOutlinedTheJoinedAndThePopupRowsAlone()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        const string flat = "ursa|ToolBar > Button:not(.bar-button):not(.primary):not(.help-round):not(.segment):not(.media-control):not(:in-overflow)";
        Assert.Contains(flat + ", ursa|ToolBar > ToggleButton:not(.bar-button):not(.primary):not(.help-round):not(.segment):not(.media-control):not(:in-overflow), Button.bar-flat\">", styles);
        Assert.Contains(flat + ":pointerover /template/ ContentPresenter#PART_ContentPresenter", styles);
        Assert.Contains(flat + ":pressed /template/ ContentPresenter#PART_ContentPresenter", styles);
        Assert.Contains(flat + ":disabled /template/ ContentPresenter#PART_ContentPresenter", styles);
        Assert.Contains("ursa|ToolBar > SplitButton:not(:in-overflow)\">", styles);
        // The outlined selection-bar button stays as it was.
        Assert.Contains("<Style Selector=\"Button.bar-button:not(:in-overflow)\">", styles);
    }

    [Fact]
    public void TheGalleryModeSwitchUsesTheBarGap()
    {
        Assert.Contains("_modeTools.Spacing = 8;", File.ReadAllText(Root("Desktop/GalleryViewer.Chrome.cs")));
    }

    [Fact]
    public void TheTaggingSelectionBarStaysOutlinedAndTheTaggingBarKeepsItsGroups()
    {
        Assert.Contains("button.Classes.Add(\"bar-button\");", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Selection.cs")));
        string commands = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Commands.cs"));
        Assert.Contains("ActionToolBar.SetGroup(item, group);", commands);
        Assert.DoesNotContain("Margin = new Thickness(16", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.cs")));
    }
}
