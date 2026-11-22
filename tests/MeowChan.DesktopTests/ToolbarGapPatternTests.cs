using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// Toolbar UX review 15.11.2026: flat bar items with an 8-dip gap owned by ActionToolBarPanel, 16 with the group line between groups, the
/// List/Table toggle as the one joined segment pair, no local margin patches. The look is the toolbar smokes'; here the contract of the
/// tokens, the XAML and the styles.
/// </summary>
[Collection("Avalonia global state")]
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

    internal static void AssertJoinedToggleClasses(IEnumerable<string> classes, string edge)
    {
        Assert.Contains("toolbar-toggle", classes);
        Assert.Contains("segment", classes);
        Assert.Equal(edge, Assert.Single(classes, name => name is "segment-first" or "segment-last" or "segment-single"));
    }

    [Fact]
    public void TheBarTokensAreThePanelsGaps()
    {
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        Assert.Contains($"<x:Double x:Key=\"Workspace.Space.BarGap\">{ActionToolBarPanel.ItemGap}</x:Double>", styles);
        Assert.Contains($"<x:Double x:Key=\"Workspace.Space.BarGroupGap\">{ActionToolBarPanel.GroupGap}</x:Double>", styles);
    }

    [Fact]
    public void TheArchiveResultsBarKeepsPrimaryActionsLeftAndPresentationControlsJoinedOnTheRight() => ReaderMediaSidebarLayoutTests.OnDispatcher(() =>
    {
        ArchivePage page = ReaderMediaSidebarLayoutTests.Page();
        try
        {
            ActionToolBar bar = page.FindControl<ActionToolBar>("ArchiveResultActions")!;
            Control[] items = bar.Items.Cast<Control>().ToArray();
            Assert.Equal(["ArchiveResultsGallery", "ArchiveResultsOpenInMedia", "ArchiveFavoritesOverview",
                "ArchiveResultsExport", "ArchiveSavedSearches", "ArchiveReaderGroups", "ArchivePostBookmarks", "ArchiveThreadColumnsButton", "ArchiveLayoutList", "ArchiveLayoutTable"],
                items.Select(item => item.Name));
            Assert.Equal([1, 1, 1, 2, 2, 2, 2, 3, 3, 3], items.Select(ActionToolBar.GetGroup));
            AssertJoinedToggleClasses(items[^2].Classes, "segment-first");
            AssertJoinedToggleClasses(items[^1].Classes, "segment-last");
            foreach (Control item in items.TakeLast(2))
            {
                ToggleButton toggle = Assert.IsType<ToggleButton>(item);
                Assert.False(toggle.IsSet(TemplatedControl.PaddingProperty));
                Assert.Equal(64, toggle.MinWidth);
                Assert.Equal(32, toggle.MinHeight);
            }
            Assert.Equal(["ArchiveThreadColumnsButton", "ArchiveLayoutList", "ArchiveLayoutTable"],
                items.Where(ActionToolBar.GetEnd).Select(item => item.Name));
        }
        finally { page.EndLifetime(); }
    });

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
    public void TheFlatItemStylesLeaveTheOutlinedTheJoinedAndThePopupRowsAlone() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        // Give the workspace hover a distinct color so the native popup theme can be distinguished from it.
        Application.Current!.Resources["Workspace.Hover"] = Brushes.Magenta;
        Button plain = new() { Content = "Plain" };
        Button outlined = new() { Content = "Outlined", Classes = { "bar-button" } };
        ToggleButton toggle = new() { Content = "Toggle" };
        ToggleButton toolbarToggle = new() { Content = "Sidebar", Classes = { "toolbar-toggle" }, IsChecked = true };
        ToggleButton segment = new() { Content = "List", Classes = { "toolbar-toggle", "segment", "segment-first" } };
        SplitButton split = new() { Content = "Split" };
        ActionToolBar bar = new() { Items = { plain, outlined, toggle, toolbarToggle, segment, split } };
        Window window = new() { Width = 1000, Height = 200, Content = bar, RequestedThemeVariant = ThemeVariant.Dark, Classes = { MotionPolicy.OffClass } };

        static ContentPresenter Presenter(Control control) => control.GetVisualDescendants().OfType<ContentPresenter>()
            .First(presenter => presenter.Name == "PART_ContentPresenter");
        static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
        static object? Resource(object key)
        {
            Assert.True(Application.Current!.TryGetResource(key, ThemeVariant.Dark, out object? resource), $"Missing resource {key}");
            return resource;
        }
        static Color Token(string key) => ColorOf(Assert.IsAssignableFrom<IBrush>(Resource(key)));

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Colors.Transparent, ColorOf(Presenter(plain).Background));
            Assert.Equal(Colors.Transparent, ColorOf(Presenter(toggle).Background));
            Assert.Equal(Colors.Transparent, ColorOf(split.Background));
            Assert.Equal(Token("Workspace.Accent"), ColorOf(Presenter(toolbarToggle).BorderBrush));
            Assert.Equal(Token("Workspace.ToolbarToggleSurface"), ColorOf(Presenter(toolbarToggle).Background));
            Assert.True(segment.CornerRadius.TopLeft > 0);
            Assert.Equal(0, segment.CornerRadius.TopRight);
            Assert.Equal(0, segment.CornerRadius.BottomRight);
            ControlTheme outlinedTheme = Assert.IsType<ControlTheme>(Resource(typeof(Button)));
            Assert.Same(outlinedTheme, outlined.Theme);

            var state = (IPseudoClasses)plain.Classes;
            state.Set(":pointerover", true);
            Assert.Equal(Colors.Magenta, ColorOf(Presenter(plain).Background));
            state.Set(":pointerover", false);
            state.Set(":pressed", true);
            Assert.Equal(Token("Workspace.AccentSurface"), ColorOf(Presenter(plain).Background));
            state.Set(":pressed", false);
            plain.IsEnabled = false;
            Assert.Equal(Token("Workspace.Muted"), ColorOf(Presenter(plain).Foreground));
            plain.IsEnabled = true;

            ToolBar.SetOverflowMode(plain, OverflowMode.Always);
            ToolBar.SetOverflowMode(outlined, OverflowMode.Always);
            window.UpdateLayout();
            Assert.Contains(plain, bar.OverflowHost!.Children);
            Assert.Contains(outlined, bar.OverflowHost.Children);
            bar.More!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(outlinedTheme, outlined.Theme);
            state.Set(":pointerover", true);
            Assert.NotEqual(Colors.Magenta, ColorOf(Presenter(plain).Background));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheGalleryModeSwitchUsesTheBarGap()
    {
        Assert.Contains("_modeTools.Spacing = 8;", File.ReadAllText(Root("Desktop/GalleryViewer.Chrome.cs")));
    }

    [Fact]
    public void TheTaggingSelectionBarStaysOutlinedAndTheQueueActionsShareOneGroup()
    {
        Assert.Contains("button.Classes.Add(\"bar-button\");", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Selection.cs")));
        string commands = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Commands.cs"));
        Assert.Contains("new Control[] { _add, _help, _clearDone, _clear }", commands);
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
        Assert.Contains("_queueActionRow.TrailingChild = _actions;", File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.cs")));
        Assert.Contains("_listControls.TrailingChild = _listPresentation;", commands);
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
