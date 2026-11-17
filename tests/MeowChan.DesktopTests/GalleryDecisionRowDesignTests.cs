using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Shared "Rate and tag…" decision row (UX-LEDGER-12): the rating bar (five equal full-width star cells and the remove button),
// an equally wide favourite, then a short rating caption. Hover previews, one tab stop, the label of a partly favourite
// selection, and the full help sentence stay intact. Controls without an application have no theme, so geometry is checked on the grid and
// the real layout of the cells at the popover width and the narrow width belongs to the captures run centrally.
[Collection("Localization")]
public sealed class GalleryDecisionRowDesignTests : IDisposable
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    public GalleryDecisionRowDesignTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static GalleryDecisionView View(int? rating, GalleryDecisionCoverage favorite = GalleryDecisionCoverage.None, bool mixed = false, bool available = true) =>
        new(1, favorite, rating, mixed, available);

    private static bool?[] Marks(GalleryDecisionRow row) => row.StarButtons.Select(star => FavoriteStar.MarkOf(star.Content)).ToArray();

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    // ---- the rating bar ---------------------------------------------------------------------------------------------------------

    [Theory, InlineData(336.0), InlineData(280.0), InlineData(160.0)]
    public void TheFiveStarCellsAreEqualTouchAndFillTheRowBesideTheRemoveButton(double width)
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(3));
        Grid bar = Assert.IsType<Grid>(row.RatingGroup);
        Assert.Equal(7, bar.ColumnDefinitions.Count);
        Assert.All(bar.ColumnDefinitions.Take(5), column => Assert.Equal(GridUnitType.Star, column.Width.GridUnitType));
        Assert.Equal(new GridLength(8), bar.ColumnDefinitions[5].Width);
        Assert.Equal(GridLength.Auto, bar.ColumnDefinitions[6].Width);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, row.StarButtons.Select(Grid.GetColumn));
        Assert.Equal(6, Grid.GetColumn(row.ClearButton));
        Assert.All(row.StarButtons, star => Assert.Contains("rating-star", star.Classes));
        Assert.All(row.StarButtons, star => Assert.Equal(Avalonia.Layout.HorizontalAlignment.Stretch, star.HorizontalAlignment));

        // Only the bar is laid out: a caption needs the platform's text services, which a test without an application does not have.
        bar.Measure(new Size(width, double.PositiveInfinity));
        bar.Arrange(new Rect(0, 0, width, bar.DesiredSize.Height));
        double cell = (width - 8 - row.ClearButton.Bounds.Width) / 5;
        // Layout rounding snaps each cell to whole pixels, so a cell may differ from the exact fifth by up to one pixel.
        Assert.All(row.StarButtons, star => Assert.InRange(star.Bounds.Width, cell - 1, cell + 1));
        for (int index = 0; index < 4; index++)
            Assert.Equal(row.StarButtons[index].Bounds.Right, row.StarButtons[index + 1].Bounds.Left, 3); // no gap: the hit area is continuous
        Assert.Equal(0, row.StarButtons[0].Bounds.Left, 3);
        Assert.InRange(row.ClearButton.Bounds.Right, width - 1, width + 1);
    }

    [Fact]
    public void TheStarsHaveTheirOwnClassTheStyleGivesTheSizesAndTheRemoveButtonStaysTheSquare()
    {
        GalleryDecisionRow row = new("DesignRow");
        Assert.All(row.StarButtons, star => Assert.DoesNotContain(WorkspaceIcons.SquareClass, star.Classes));
        Assert.Contains(WorkspaceIcons.SquareClass, row.ClearButton.Classes);
        Assert.Contains("subtle", row.ClearButton.Classes);
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        XElement[] all = XDocument.Parse(styles).Root!.Elements(A + "Style").ToArray();
        string? Setting(string selector, string property) => all.Where(style => (string?)style.Attribute("Selector") == selector)
            .SelectMany(style => style.Elements(A + "Setter")).Where(setter => (string?)setter.Attribute("Property") == property)
            .Select(setter => (string?)setter.Attribute("Value")).FirstOrDefault();
        Assert.Equal("{StaticResource Workspace.Size.Nav}", Setting("ToggleButton.rating-star", "Height"));
        Assert.Equal("{StaticResource Workspace.Size.ControlCompact}", Setting("ToggleButton.rating-star", "MinWidth"));
        Assert.Equal("Stretch", Setting("ToggleButton.rating-star", "HorizontalAlignment"));
        Assert.Equal("{StaticResource Workspace.Radius.Control}", Setting("ToggleButton.rating-star", "CornerRadius"));
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", Setting("ToggleButton.rating-star", "FocusAdorner"));
        Assert.Equal("Transparent", Setting("ToggleButton.rating-star", "Background"));
        Assert.Equal("Transparent", Setting("ToggleButton.rating-star", "BorderBrush"));
        const string presenter = " /template/ ContentPresenter#PART_ContentPresenter";
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("ToggleButton.rating-star" + presenter, "Foreground"));
        Assert.Equal("{DynamicResource Workspace.Hover}", Setting("ToggleButton.rating-star:pointerover" + presenter, "Background"));
        // No accent fill on the checked star: the filled stars carry the value.
        Assert.Equal("Transparent", Setting("ToggleButton.rating-star:checked" + presenter, "Background"));
        Assert.Equal("{DynamicResource Workspace.Text}", Setting("ToggleButton.rating-star.rating-preview" + presenter, "Foreground"));
        Assert.Equal("{DynamicResource Workspace.Muted}", Setting("ToggleButton.rating-star:disabled" + presenter, "Foreground"));
        // No literal colour or size in the new selectors: tokens only.
        foreach (XElement style in all.Where(style => ((string?)style.Attribute("Selector"))!.Contains("rating-star", StringComparison.Ordinal)))
            Assert.DoesNotContain(style.Elements(A + "Setter"), setter => ((string?)setter.Attribute("Value"))!.StartsWith('#'));
    }

    // ---- hover -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void HoveringStarFourShowsStarsOneToFourAsSetWithoutChangingTheRatingOrAskingForOne()
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(2));
        List<int?> requests = new();
        row.RatingRequested += requests.Add;
        Assert.Equal(new bool?[] { true, true, false, false, false }, Marks(row));
        bool?[] checkedBefore = row.StarButtons.Select(star => star.IsChecked).ToArray();

        row.Preview(4);
        Assert.Equal(new bool?[] { true, true, true, true, false }, Marks(row));
        Assert.Equal(4, row.PreviewedStar);
        Assert.Equal(checkedBefore, row.StarButtons.Select(star => (bool?)star.IsChecked)); // the checked state is the saved rating's, never the preview's
        Assert.True(row.StarButtons[1].IsChecked);
        Assert.Equal(new[] { true, true, true, true, false }, row.StarButtons.Select(star => star.Classes.Contains("rating-preview")));
        Assert.Equal(2, row.View.Rating);
        Assert.Empty(requests);

        row.Preview(1); // a lower star previews a lower rating: the stars above it are outlines again
        Assert.Equal(new bool?[] { true, false, false, false, false }, Marks(row));

        row.Preview(null); // the pointer left: the saved rating again
        Assert.Equal(new bool?[] { true, true, false, false, false }, Marks(row));
        Assert.Null(row.PreviewedStar);
        Assert.All(row.StarButtons, star => Assert.DoesNotContain("rating-preview", star.Classes));
        Assert.Empty(requests);
    }

    [Fact]
    public void ARepaintDuringTheHoverKeepsThePreviewAndARowThatCannotRateIgnoresIt()
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(null));
        row.Preview(3);
        row.Show(View(null)); // the host refreshed the view under the pointer
        Assert.Equal(new bool?[] { true, true, true, false, false }, Marks(row));
        row.Preview(null);
        Assert.Equal(new bool?[] { false, false, false, false, false }, Marks(row));

        row.Show(GalleryDecisionView.Unavailable);
        row.Preview(3);
        Assert.Null(row.PreviewedStar);
        Assert.Equal(new bool?[] { false, false, false, false, false }, Marks(row));
        row.Show(View(null, mixed: true));
        Assert.Equal(new bool?[] { false, false, false, false, false }, Marks(row)); // mixed ratings: all outlines
    }

    [Fact]
    public void EveryStarWiresItsPointerToThePreviewAndTheRowForgetsItWhenItLeavesTheTree()
    {
        string row = File.ReadAllText(Root("Desktop/Controls/GalleryDecisionRow.cs"));
        Assert.Contains("star.PointerEntered += (_, _) => Preview(number);", row);
        Assert.Contains("star.PointerExited += (_, _) => Preview(null);", row);
        Assert.Contains("_preview = null; if (!_attached) return;", row);
        Assert.Contains("FavoriteStar.Create(index < shown, StarGlyphSize, tinted: !previewing)", row);
        Assert.Contains("private const int StarGlyphSize = 20;", row);
    }

    // ---- tab stop ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 0), InlineData(1, 0), InlineData(3, 2), InlineData(5, 4)]
    public void ExactlyOneStarIsATabStopAndTheRemoveButtonIsNone(int? rating, int stop)
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(rating));
        Assert.Equal(Enumerable.Range(0, 5).Select(index => index == stop), row.StarButtons.Select(star => star.IsTabStop));
        Assert.Same(row.StarButtons[stop], row.FocusStar);
        Assert.False(row.ClearButton.IsTabStop);
        // The arrows still reach the others (Focusable stays).
        Assert.All(row.StarButtons, star => Assert.True(star.Focusable));
        Assert.True(row.ClearButton.Focusable);
    }

    [Fact]
    public void TheTabStopFollowsTheRatingAndIsTheFirstStarWhileMixedOrUnrated()
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(4));
        Assert.Equal(new[] { false, false, false, true, false }, row.StarButtons.Select(star => star.IsTabStop));
        row.Show(View(null, mixed: true));
        Assert.Equal(new[] { true, false, false, false, false }, row.StarButtons.Select(star => star.IsTabStop));
        row.Show(View(2));
        Assert.Equal(new[] { false, true, false, false, false }, row.StarButtons.Select(star => star.IsTabStop));
    }

    // ---- the favourite line --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(440.0, 32.0), InlineData(336.0, 32.0), InlineData(280.0, 40.0), InlineData(160.0, 32.0)]
    public void FavouriteSpansExactlyTheFiveStarsAndKeepsTheCaptionOutOfItsWidth(double width, double clearWidth)
    {
        GalleryDecisionRow row = new("DesignRow");
        Grid bar = Assert.IsType<Grid>(row.RatingGroup);
        Grid line = Assert.IsType<Grid>(row.Children[1]);
        Assert.True(Grid.GetIsSharedSizeScope(row));
        Assert.Equal(bar.ColumnDefinitions[6].SharedSizeGroup, line.ColumnDefinitions[6].SharedSizeGroup);
        Assert.Equal("DecisionClear", line.ColumnDefinitions[6].SharedSizeGroup);
        Assert.Equal(5, Grid.GetColumnSpan(row.FavoriteButton));
        Assert.Equal(1, Grid.GetRow(row.Summary));
        Assert.Equal(7, Grid.GetColumnSpan(row.Summary));
        // No application/text services: give both shared gutters the resolved clear width and remove text content.
        // The shared-size contract above is what supplies that width in the visual tree.
        bar.ColumnDefinitions[6].Width = new GridLength(clearWidth);
        line.ColumnDefinitions[6].Width = new GridLength(clearWidth);
        row.FavoriteButton.Content = null;
        row.Summary.IsVisible = false;
        foreach (Grid grid in new[] { bar, line })
        {
            grid.Measure(new Size(width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, width, 40));
        }
        Assert.Equal(row.StarButtons[0].Bounds.Left, row.FavoriteButton.Bounds.Left, 3);
        Assert.Equal(row.StarButtons[4].Bounds.Right, row.FavoriteButton.Bounds.Right, 3);
        Assert.Equal(width - clearWidth - 8, row.FavoriteButton.Bounds.Width, 3);
    }

    [Fact]
    public void FavouriteStyleCentersTheWholeContentAndEnlargesOnlyTheDecisionRowTarget()
    {
        GalleryDecisionRow row = new("DesignRow");
        Assert.Contains("rating-favorite", row.FavoriteButton.Classes);
        Assert.Contains("media-control", row.FavoriteButton.Classes);
        XElement style = XDocument.Parse(File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"))).Root!
            .Elements(A + "Style").Single(style => (string?)style.Attribute("Selector") == "ToggleButton.rating-favorite");
        Dictionary<string, string?> settings = style.Elements(A + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Property")!, setter => (string?)setter.Attribute("Value"));
        Assert.Equal("{StaticResource Workspace.Size.Nav}", settings["MinHeight"]);
        Assert.Equal("11,7", settings["Padding"]);
        Assert.Equal("Stretch", settings["HorizontalAlignment"]);
        Assert.Equal("Center", settings["HorizontalContentAlignment"]);
        Assert.Equal("Center", settings["VerticalContentAlignment"]);
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", settings["FocusAdorner"]);
        Assert.DoesNotContain("Height", settings.Keys); // larger text may grow; no fixed-height clipping
        Assert.DoesNotContain("Background", settings.Keys); // existing media-control theme/state colors stay intact
        Assert.DoesNotContain("Foreground", settings.Keys);
        foreach (GalleryDecisionCoverage coverage in Enum.GetValues<GalleryDecisionCoverage>())
        {
            GalleryDecisionView view = View(3, coverage);
            row.Show(view);
            Assert.Equal(GalleryDecisionText.Favorite(view), AutomationProperties.GetHelpText(row.FavoriteButton));
            StackPanel content = Assert.IsType<StackPanel>(row.FavoriteButton.Content);
            Assert.Equal(20, content.Children[0].Width);
            Assert.Equal(20, content.Children[0].Height);
        }
    }

    private static string FavoriteLabel(GalleryDecisionRow row) =>
        Assert.IsType<StackPanel>(row.FavoriteButton.Content).Children.OfType<TextBlock>().Single().Text!;

    [Fact]
    public void TheFavouriteLabelSaysSomeWhileOnlySomeOfASelectionAreFavouritesAndKeepsItsName()
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(null, GalleryDecisionCoverage.None));
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        row.Show(View(null, GalleryDecisionCoverage.Some));
        Assert.Equal(Texts.T("gallery.decision.favorite.some"), FavoriteLabel(row));
        Assert.Null(row.FavoriteButton.IsChecked);
        Assert.Equal(Texts.T("gallery.decision.favorite"), AutomationProperties.GetName(row.FavoriteButton)); // the accessible name does not move with the label
        row.Show(View(null, GalleryDecisionCoverage.All));
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        Assert.True(row.FavoriteButton.IsChecked);
        row.Localize(); // a language or key change repaints the label from the view, not from a stale text
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        row.Show(View(null, GalleryDecisionCoverage.Some));
        row.Localize();
        Assert.Equal(Texts.T("gallery.decision.favorite.some"), FavoriteLabel(row));
    }

    [Fact]
    public void TheCaptionIsTheRatingAloneWhileTheGroupHelpStaysTheFullSentence()
    {
        GalleryDecisionRow row = new("DesignRow");
        GalleryDecisionView rated = View(4, GalleryDecisionCoverage.All);
        row.Show(rated);
        Assert.Equal(MediaRatingText.Value(4), row.Summary.Text);
        Assert.DoesNotContain("·", row.Summary.Text);
        Assert.Equal(ShortcutHelp.Append(GalleryDecisionText.Summary(rated), ShortcutHelp.Commands("gallery",
            Enumerable.Range(1, 5).Select(n => "media.rating.set-" + n).Append("media.rating.clear").ToArray())),
            AutomationProperties.GetHelpText(row.RatingGroup));
        Assert.Contains("·", AutomationProperties.GetHelpText(row.RatingGroup));
        Assert.Equal(1, row.Summary.MaxLines);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, row.Summary.TextTrimming);
        Assert.IsType<TrimTipTextBlock>(row.Summary);

        row.Show(View(null));
        Assert.Equal(MediaRatingText.Value(null), row.Summary.Text);
        row.Show(View(null, mixed: true));
        Assert.Equal(Texts.T("gallery.decision.rating.mixed"), row.Summary.Text);
        row.Show(GalleryDecisionView.Unavailable);
        Assert.Equal(Texts.T("gallery.decision.status.unavailable"), row.Summary.Text);
    }

    [Fact]
    public void HidingTheFavouriteHidesOnlyTheToggleAndTheCaptionStaysOnItsLine()
    {
        GalleryDecisionRow row = new("DesignRow") { FavoriteVisible = false };
        row.Show(View(3));
        Assert.False(row.FavoriteButton.IsVisible);
        Assert.True(row.Summary.IsVisible);
        Assert.Equal(MediaRatingText.Value(3), row.Summary.Text);
        Assert.Equal(2, row.Children.Count);
        Grid line = Assert.IsType<Grid>(row.Children[1]);
        Assert.Same(row.RatingGroup, row.Children[0]);
        Assert.Contains(row.FavoriteButton, line.Children);
        Assert.Contains(row.Summary, line.Children);
        Assert.Equal(1, Grid.GetRow(row.Summary));
        Assert.Equal(7, Grid.GetColumnSpan(row.Summary));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, row.Summary.HorizontalAlignment);
        Assert.Equal(8, row.Spacing); // the two lines
    }
}
