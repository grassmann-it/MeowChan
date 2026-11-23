using MeowChan.Desktop.Commands;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// Shared "Rate and tag…" decision row: five equal star cells and the remove button, with the favourite
// and rating caption on the second row. Hover previews, one tab stop, the label of a partly favourite
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
    public void TheFiveStarCellsShareAWidthAndSitBesideTheRemoveButton(double width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        GalleryDecisionRow row = new("DesignRow", compact: true) { Width = width };
        row.Show(View(3));
        Grid bar = Assert.IsType<Grid>(row.RatingGroup);
        Assert.Equal(3, bar.ColumnDefinitions.Count);
        Assert.Equal(GridLength.Auto, bar.ColumnDefinitions[0].Width);
        Assert.Equal(new GridLength(8), bar.ColumnDefinitions[1].Width);
        Assert.Equal(GridLength.Star, bar.ColumnDefinitions[2].Width);
        Assert.Equal(2, Grid.GetColumn(row.ClearButton));
        Assert.Equal(WorkspaceMetrics.InputHeight, bar.ColumnDefinitions[2].MinWidth);
        Assert.All(row.StarButtons, star => Assert.Contains("rating-star", star.Classes));
        Assert.Equal(0, Grid.GetRow(row.ClearButton));
        Assert.Equal(2, Grid.GetRow(row.Summary));
        Assert.Equal(Grid.GetColumn(row.ClearButton), Grid.GetColumn(row.Summary));

        Window window = new() { Content = row, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            double cellWidth = row.StarButtons[0].Bounds.Width;
            Assert.Equal(24, cellWidth);
            Assert.All(row.StarButtons, star => Assert.Equal(cellWidth, star.Bounds.Width, 1));
            for (int index = 0; index < row.StarButtons.Count - 1; index++)
                Assert.Equal(row.StarButtons[index].Bounds.Right, row.StarButtons[index + 1].Bounds.Left, 0.5);
            Assert.Equal(row.StarButtons[^1].Bounds.Right + 8, row.ClearButton.Bounds.Left, 0.5);
            Assert.True(row.Summary.Bounds.Right <= bar.Bounds.Width + 0.5);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(240)]
    [InlineData(280)]
    [InlineData(360)]
    [InlineData(480)]
    public void PopoverKeepsTheSameAxesWithLargerStarTargets(int width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        GalleryDecisionRow row = new("PopoverRow") { Width = width - 24 };
        row.Show(View(3));
        Window window = new() { Width = 520, Height = 240, Content = row };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.All(row.StarButtons, star =>
            {
                Assert.Equal(width - 24 < 336 ? 32 : 40, star.Bounds.Width);
                Assert.Equal(star.Bounds.Width, star.Bounds.Height, 0.5);
                Assert.InRange(Math.Abs(star.Bounds.Width - row.StarButtons[0].Bounds.Width), 0, 1);
            });
            Assert.Equal(row.StarButtons[0].Bounds.Left, row.FavoriteButton.Bounds.Left, 0.5);
            Assert.Equal(row.ClearButton.Bounds.Left + 8, row.Summary.Bounds.Left, 0.5);
            Assert.True(row.FavoriteButton.Bounds.Top > row.StarButtons[0].Bounds.Bottom);
            Assert.True(row.Summary.Bounds.Top > row.ClearButton.Bounds.Bottom);
            Assert.True(row.Summary.Bounds.Right <= row.Bounds.Width + 0.5);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStarsHaveTheirOwnClassTheStyleGivesTheSizesAndTheRemoveButtonStaysTheSquare()
    {
        GalleryDecisionRow row = new("DesignRow");
        Assert.All(row.StarButtons, star => Assert.DoesNotContain(WorkspaceIcons.SquareClass, star.Classes));
        Assert.Contains(WorkspaceIcons.SquareClass, row.ClearButton.Classes);
        Assert.Contains("media-control", row.ClearButton.Classes);
        string styles = File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"));
        XElement[] all = XDocument.Parse(styles).Root!.Elements(A + "Style").ToArray();
        string? Setting(string selector, string property) => all.Where(style => (string?)style.Attribute("Selector") == selector)
            .SelectMany(style => style.Elements(A + "Setter")).Where(setter => (string?)setter.Attribute("Property") == property)
            .Select(setter => (string?)setter.Attribute("Value")).FirstOrDefault();
        Assert.Equal("{StaticResource Workspace.Size.Control}", Setting("ToggleButton.rating-star", "Height"));
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
    public void EveryStarWiresItsPointerToThePreviewAndTheRowForgetsItWhenItLeavesTheTree() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        GalleryDecisionRow row = new("DesignRow") { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        row.Show(View(2));
        Window window = new() { Width = 480, Height = 240, Content = row, Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            Dispatcher.UIThread.RunJobs();
            for (int index = 0; index < row.StarButtons.Count; index++)
            {
                ToggleButton star = row.StarButtons[index];
                Point center = star.TranslatePoint(new Point(star.Bounds.Width / 2, star.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center);
                Assert.Equal(index + 1, row.PreviewedStar);
                Assert.Equal(Enumerable.Range(0, 5).Select(i => (bool?)(i <= index)), Marks(row));

                window.MouseMove(new Point(10, 220));
                Assert.Null(row.PreviewedStar);
                Assert.Equal(new bool?[] { true, true, false, false, false }, Marks(row));
            }

            ToggleButton last = row.StarButtons[^1];
            window.MouseMove(last.TranslatePoint(new Point(last.Bounds.Width / 2, last.Bounds.Height / 2), window)!.Value);
            Assert.Equal(5, row.PreviewedStar);
            window.Content = null;
            Assert.Null(row.PreviewedStar);
        }
        finally { window.Close(); }
    });

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

    [Fact]
    public void FavouriteAndRatingCaptionShareASecondRowUnderTheirColumns()
    {
        GalleryDecisionRow row = new("DesignRow");
        Grid group = Assert.IsType<Grid>(row.RatingGroup);
        Assert.Equal(2, Grid.GetRow(row.FavoriteButton));
        Assert.Equal(0, Grid.GetColumn(row.FavoriteButton));
        Assert.Equal(1, Grid.GetColumnSpan(row.FavoriteButton));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, row.FavoriteButton.HorizontalAlignment);
        Assert.Equal(2, Grid.GetRow(row.Summary));
        Assert.Equal(Grid.GetColumn(row.ClearButton), Grid.GetColumn(row.Summary));
        Assert.Equal(new GridLength(8), group.RowDefinitions[1].Height);
    }

    [Fact]
    public void FavouriteStyleCentersTheWholeContentAndEnlargesOnlyTheDecisionRowTarget()
    {
        GalleryDecisionRow row = new("DesignRow");
        Assert.Contains("rating-favorite", row.FavoriteButton.Classes);
        Assert.DoesNotContain("media-control", row.FavoriteButton.Classes);
        XElement style = XDocument.Parse(File.ReadAllText(Root("Desktop/WorkspaceStyles.axaml"))).Root!
            .Elements(A + "Style").Single(style => (string?)style.Attribute("Selector") == "ToggleButton.rating-favorite");
        Dictionary<string, string?> settings = style.Elements(A + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Property")!, setter => (string?)setter.Attribute("Value"));
        Assert.Equal("{StaticResource Workspace.Size.Control}", settings["MinHeight"]);
        Assert.Equal("12,0", settings["Padding"]);
        Assert.Equal("Center", settings["HorizontalContentAlignment"]);
        Assert.Equal("Center", settings["VerticalContentAlignment"]);
        Assert.Equal("{StaticResource Workspace.FocusAdorner}", settings["FocusAdorner"]);
        Assert.Equal(settings["MinHeight"], settings["Height"]);
        Assert.DoesNotContain("Background", settings.Keys); // existing media-control theme/state colors stay intact
        Assert.DoesNotContain("Foreground", settings.Keys);
        foreach (GalleryDecisionCoverage coverage in Enum.GetValues<GalleryDecisionCoverage>())
        {
            GalleryDecisionView view = View(3, coverage);
            row.Show(view);
            Assert.Equal(GalleryDecisionText.Favorite(view), AutomationProperties.GetHelpText(row.FavoriteButton));
            Grid content = Assert.IsType<Grid>(row.FavoriteButton.Content);
            Assert.Equal(16, content.Children[0].Width);
            Assert.Equal(16, content.Children[0].Height);
        }
    }

    private static string FavoriteLabel(GalleryDecisionRow row) =>
        Assert.IsType<Grid>(row.FavoriteButton.Content).Children.OfType<TextBlock>().Single().Text!;

    [Fact]
    public void TheFavouriteLabelSaysSomeWhileOnlySomeOfASelectionAreFavouritesAndKeepsItsName()
    {
        GalleryDecisionRow row = new("DesignRow");
        row.Show(View(null, GalleryDecisionCoverage.None));
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        row.Show(View(null, GalleryDecisionCoverage.Some));
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        Assert.Equal(Texts.T("gallery.decision.favorite.some"), ToolTip.GetTip(row.FavoriteButton));
        Assert.Null(row.FavoriteButton.IsChecked);
        Assert.Equal(Texts.T("gallery.decision.favorite"), AutomationProperties.GetName(row.FavoriteButton)); // the accessible name does not move with the label
        row.Show(View(null, GalleryDecisionCoverage.All));
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        Assert.True(row.FavoriteButton.IsChecked);
        row.Localize(); // a language or key change repaints the label from the view, not from a stale text
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        row.Show(View(null, GalleryDecisionCoverage.Some));
        row.Localize();
        Assert.Equal(Texts.T("gallery.decision.favorite"), FavoriteLabel(row));
        Assert.Equal(Texts.T("gallery.decision.favorite.some"), ToolTip.GetTip(row.FavoriteButton));
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
        Assert.Equal(2, row.Summary.MaxLines);
        Assert.Equal(Avalonia.Media.TextTrimming.None, row.Summary.TextTrimming);
        Assert.Contains("workspace-meta", row.Summary.Classes);

        row.Show(View(null));
        Assert.Equal(MediaRatingText.Value(null), row.Summary.Text);
        row.Show(View(null, mixed: true));
        Assert.Equal(Texts.T("gallery.decision.rating.mixed"), row.Summary.Text);
        row.Show(GalleryDecisionView.Unavailable);
        Assert.False(row.Summary.IsVisible);
        Assert.Equal(Texts.T("gallery.decision.status.unavailable"), Assert.Single(row.Children.OfType<TextBlock>(), text => text.IsVisible).Text);
    }

    [Fact]
    public void HidingTheFavouriteHidesOnlyTheToggleAndTheCaptionStaysOnItsLine()
    {
        GalleryDecisionRow row = new("DesignRow") { FavoriteVisible = false };
        row.Show(View(3));
        Assert.False(row.FavoriteButton.IsVisible);
        Assert.True(row.Summary.IsVisible);
        Assert.Equal(MediaRatingText.Value(3), row.Summary.Text);
        Assert.Same(row.RatingGroup, row.Summary.Parent);
        Assert.Equal(2, Grid.GetRow(row.Summary));
    }
}
