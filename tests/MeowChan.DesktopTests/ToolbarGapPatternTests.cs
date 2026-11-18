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
    public void TheArchiveResultsBarKeepsPrimaryActionsLeftAndPresentationControlsJoinedOnTheRight()
    {
        XElement[] items = Items(Toolbar("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveResultActions"));
        Assert.Equal(["ArchiveResultsGallery", "ArchiveResultsOpenInMedia", "ArchiveFavoritesOverview",
            "ArchiveResultsExport", "ArchiveSavedSearches", "ArchiveReaderGroups", "ArchiveThreadColumnsButton", "ArchiveLayoutList", "ArchiveLayoutTable"], items.Select(item => (string)item.Attribute(X + "Name")!));
        Assert.Equal([1, 1, 1, 2, 2, 2, 3, 3, 3], items.Select(item => int.Parse(Attribute(item, "ActionToolBar.Group") ?? "0")));
        Assert.Equal("segment segment-first", (string?)items[^2].Attribute("Classes"));
        Assert.Equal("segment segment-last", (string?)items[^1].Attribute("Classes"));
        foreach (XElement toggle in items.TakeLast(2))
        {
            Assert.Null(toggle.Attribute("Padding"));
            Assert.Equal("64", (string?)toggle.Attribute("MinWidth"));
            Assert.Equal("32", (string?)toggle.Attribute("MinHeight"));
        }
        Assert.Equal(["ArchiveThreadColumnsButton", "ArchiveLayoutList", "ArchiveLayoutTable"],
            items.Where(item => Attribute(item, "ActionToolBar.End") == "True").Select(item => (string)item.Attribute(X + "Name")!));
        // Dynamically installed actions stay before presentation controls.
        Assert.Contains("Items.Insert(ArchiveResultActions.Items.IndexOf(ArchiveThreadColumnsButton), manager)",
            File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.PostBookmarks.cs")));
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

    // ---- UX-LEDGER-01: primary actions left, layout/secondary actions right, one gap token, no empty "…" -------------------------------

    [Theory]
    [InlineData(new[] { false, false, true, true }, 2)]
    [InlineData(new[] { true, false, true }, 2)]
    [InlineData(new[] { false, true, false }, -1)]
    [InlineData(new[] { true, true }, 0)]
    [InlineData(new bool[0], -1)]
    public void TheTrailingEndRunIsFoundAmongTheShownItems(bool[] ends, int expected) =>
        Assert.Equal(expected, ActionToolBarPanel.EndRunStart(ends));

    [Theory]
    [InlineData(500, 300, 200)]
    [InlineData(300, 300, 0)]
    [InlineData(200, 300, 0)]
    [InlineData(double.NaN, 300, 0)]
    [InlineData(double.PositiveInfinity, 300, 0)]
    public void TheEndRunTakesOnlyTheSpareRoomAndNeverANegativeShift(double right, double natural, double expected) =>
        Assert.Equal(expected, ActionToolBarPanel.EndShift(right, natural));

    [Fact]
    public void TheEndPropertyIsAnOptInOfTheItemAndOffByDefault()
    {
        Avalonia.Controls.Button item = new();
        Assert.False(ActionToolBar.GetEnd(item));
        ActionToolBar.SetEnd(item, true);
        Assert.True(ActionToolBar.GetEnd(item));
    }

    [Fact]
    public void TheDownloadsBarDocksColumnsAndDetailsAtTheRightAndKeepsTheActionsLeft()
    {
        XElement[] items = Items(Toolbar("Desktop/Shell/MainWindow.axaml", "WorkspaceListActions"));
        string[] ends = items.Where(item => Attribute(item, "ActionToolBar.End") == "True").Select(item => (string)item.Attribute(X + "Name")!).ToArray();
        Assert.Equal(["DownloadsColumnsButton", "DetailsToggleButton"], ends);
        // The End run is the tail of the bar, so item order (and with it focus order) stays: primary actions first.
        Assert.Equal(["DownloadSearchButton", "CopyAllUrlsButton", "DownloadsColumnsButton", "DetailsToggleButton"], items.Select(item => (string)item.Attribute(X + "Name")!));
    }

    [Fact]
    public void TheTaggingBarDocksColumnsViewSwitchAndHelpAtTheRight()
    {
        string commands = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Commands.cs"));
        Assert.Contains("ActionToolBar.SetEnd(_help, true);", commands);
        // Columns and list/grid remain grouped in the list presentation row.
        Assert.Contains("new Control[] { _columnsButton, _viewToggle }", commands);
    }

    [Fact]
    public void MediaRowsUseTheBarItemGapTokenInsteadOfALocalEight()
    {
        string toolbar = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.Toolbar.cs"));
        Assert.Contains("private const double ToolbarGap = ActionToolBarPanel.ItemGap;", toolbar);
        Assert.DoesNotContain("Spacing = 8", toolbar);
        Assert.DoesNotContain("new Thickness(8, 0, 0, 0)", toolbar);
    }

    [Fact]
    public void BothMediaMoreButtonsHideInsteadOfOpeningAnEmptyMenu()
    {
        string workView = File.ReadAllText(Root("Desktop/Views/Media/MediaPage.WorkView.cs"));
        foreach (string more in new[] { "_more", "_selMore" })
        {
            Assert.Contains($"UpdateMore({more}, items);", workView);
            Assert.Contains($"{more}.IsVisible = false;", workView);
        }
    }

    [Fact]
    public void AnItemThatBecomesInvisibleUnderMoreReRunsTheOverflowDecision()
    {
        // A hidden item under "More" has the popup as parent: without this the button would stay with an empty menu.
        string bar = File.ReadAllText(Root("Desktop/ActionToolBar.cs"));
        Assert.Contains("e.Property == IsVisibleProperty && sender is Control", bar);
    }

    [Fact]
    public void SharedAlignmentStylesCenterToolbarCheckboxesAndGridCellsAndKeepTextFromScrollingSideways()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains(
            "WrapPanel[Orientation=Horizontal] > CheckBox, StackPanel[Orientation=Horizontal] > CheckBox",
            styles);
        Assert.Contains("<Setter Property=\"VerticalAlignment\" Value=\"Center\" />", styles);
        Assert.Contains("<Style Selector=\"DataGridCell\"><Setter Property=\"Padding\" Value=\"12,0\" /><Setter Property=\"ClipToBounds\" Value=\"True\" /><Setter Property=\"VerticalContentAlignment\" Value=\"Center\" />", styles);
        Assert.Contains("<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Disabled\" />", styles);
    }
}
