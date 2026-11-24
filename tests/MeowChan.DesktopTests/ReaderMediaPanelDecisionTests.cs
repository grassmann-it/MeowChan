using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaPanelDecisionTests
{
    [Theory]
    [InlineData("reader", 240)]
    [InlineData("reader", 280)]
    [InlineData("reader", 360)]
    [InlineData("reader", 480)]
    [InlineData("media", 240)]
    [InlineData("media", 280)]
    [InlineData("media", 360)]
    [InlineData("media", 480)]
    public void ThePanelShowsAndWritesTheCurrentRatingAndFavorite(string context, int width) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        int? rating = 3;
        bool favorite = false;
        Control? editorAnchor = null;
        ReaderMediaSidebar sidebar = new() { Width = width, DecisionKeyContext = context,
            OpenTagsAsync = (anchor, _) => { editorAnchor = anchor; return Task.CompletedTask; } };
        sidebar.Describe("No. 42", "picture.png", "640×480", true, savedBytes: 4096);
        sidebar.ConfigureRating(() => true, () => rating, value => { rating = value; return Task.CompletedTask; });
        sidebar.ConfigureFavorite(() => true, () => favorite, () => { favorite = !favorite; return Task.CompletedTask; });
        sidebar.SetTags(["sample"], null);
        Window window = new() { Width = 520, Height = 900, Content = sidebar };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<GalleryDecisionRow>(), row => row.IsEffectivelyVisible);
            Click(sidebar.InfoToggle);
            window.UpdateLayout();
            GalleryDecisionRow decision = Assert.Single(sidebar.GetVisualDescendants().OfType<GalleryDecisionRow>(), row => row.IsEffectivelyVisible);
            Assert.Equal(3, decision.View.Rating);
            Assert.Equal(0, decision.StarButtons[0].TranslatePoint(default, decision)!.Value.X, 0.5);
            Assert.True(decision.StarButtons[2].IsChecked);
            Click(decision.StarButtons[4]);
            Assert.Equal(5, rating);
            Assert.Equal(5, decision.View.Rating);
            // W112-72 §3: “Kein Entfernen-Knopf, kein ⋯: Klick auf den aktiven Stern löscht”.
            Button edit = sidebar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReaderMediaSidebarEditTags");
            Assert.False(decision.ClearButton.IsEffectivelyVisible);
            Assert.Null(edit.Flyout);
            Click(decision.StarButtons[4]);
            Assert.Null(rating);
            Assert.False(decision.FavoriteButton.IsEffectivelyVisible);
            Click(sidebar.FavoriteButton);
            Assert.True(favorite);
            Assert.True(sidebar.FavoriteButton.IsChecked);
            Control tags = sidebar.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "ReaderMediaSidebarTags");
            Assert.Contains(tags.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "sample");
            Assert.Empty(sidebar.GetVisualDescendants().OfType<MediaFactGrid>());
            Assert.True(edit.IsEffectivelyVisible);
            Assert.Equal(decision.TranslatePoint(default, sidebar)!.Value.Y, edit.TranslatePoint(default, sidebar)!.Value.Y, 0.5);
            Click(edit);
            Assert.Same(edit, editorAnchor);
            Assert.IsType<PanelCloseButton>(sidebar.Close);
            Assert.Equal(1, sidebar.Close.Opacity);
            Assert.Equal(32, sidebar.Close.Bounds.Width, 0.5);
            Assert.True(sidebar.Close.IsHitTestVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TagEditorRetiresWhenTheDisplayedOccurrenceChanges(bool differentPost) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using RenderTargetBitmap picture = new(new PixelSize(64, 64));
        MediaLibraryEntry first = MediaTagCountsTests.Entry(1) with { Directory = null, AttachmentRevisionKey = "v1" };
        MediaLibraryEntry second = differentPost ? first with { PostId = "p2" } : first with { AttachmentRevisionKey = "v2" };
        string key = MediaPage.PanelVideoKey(first);
        Assert.Equal(key, MediaPage.PanelVideoKey(second));

        MediaPage page = new(_ => throw new InvalidOperationException());
        page.ConfigurePanelInfo(ReaderMediaPreferences.Default with { MediaSidebarInfo = "Always" });
        // The page derives panel visibility from the presented tiles. Use the same rows for its grid and layout.
        MediaLibraryTile[] tiles = [new(first, [first]), new(second, [second])];
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MediaPage).GetField("_tiles", fields)!.SetValue(page, tiles);
        typeof(MediaPage).GetField("_shown", fields)!.SetValue(page, new[] { first, second });
        page.ApplyLayout(page.PageLayout with { Panel = true, PanelWidth = 280 }, save: false);
        Window window = new() { Width = 1440, Height = 800, Content = page };
        try
        {
            window.Show();
            window.UpdateLayout();
            MediaLibraryTileRow[] rows = [new(new(first, [first]), 0), new(new(second, [second]), 1)];
            page.Grid.ItemsSource = rows;
            page.Grid.SelectedIndex = 0;
            ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.Panel);
            BooruComponentDispatcher.WaitUntil(() => ReferenceEquals(page.PanelRow?.Entry, first), "first occurrence");
            BooruComponentDispatcher.Wait(page.PanelTask);
            sidebar.ShowPicture(picture, _ => { }, false, double.PositiveInfinity, double.PositiveInfinity, key, picture.PixelSize);
            // W112-72 §3: “Kein Entfernen-Knopf, kein ⋯”. Exercise the live editor's revision guard.
            List<MediaLibraryEntry> edited = [];
            TaskCompletionSource close = new();
            Func<bool>? current = null;
            page.TagPopoverAsync = (entry, _, _, isCurrent, _) =>
            {
                current = isCurrent;
                edited.Add(entry);
                return edited.Count == 1 ? close.Task : Task.CompletedTask;
            };
            window.UpdateLayout();
            var edit = sidebar.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReaderMediaSidebarEditTags");
            Assert.True(edit.IsEffectivelyVisible);
            Assert.Null(edit.Flyout);
            Task editor = sidebar.EditTagsAsync();
            Assert.Same(first, Assert.Single(edited));
            Assert.True(current!());
            page.Grid.SelectedIndex = 1;
            Assert.False(current());
            close.SetResult();
            BooruComponentDispatcher.Wait(editor);
            BooruComponentDispatcher.WaitUntil(() => ReferenceEquals(page.PanelRow?.Entry, second), "second occurrence");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(picture, sidebar.Shown);
            BooruComponentDispatcher.Wait(sidebar.EditTagsAsync());
            Assert.Collection(edited, entry => Assert.Same(first, entry), entry => Assert.Same(second, entry));
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
