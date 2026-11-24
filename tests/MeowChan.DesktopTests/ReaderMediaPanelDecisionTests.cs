using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

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
        int edits = 0;
        ReaderMediaSidebar sidebar = new() { Width = width, DecisionKeyContext = context,
            OpenTagsAsync = (_, _) => { edits++; return Task.CompletedTask; } };
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
            Assert.Equal(width >= 320, decision.ClearButton.IsVisible);
            if (decision.ClearButton.IsVisible) Click(decision.ClearButton);
            else decision.RequestRating(null);
            Assert.Null(rating);
            Assert.False(decision.FavoriteButton.IsEffectivelyVisible);
            Click(sidebar.FavoriteButton);
            Assert.True(favorite);
            Assert.True(sidebar.FavoriteButton.IsChecked);
            Control tags = sidebar.GetVisualDescendants().OfType<StackPanel>().Single(control => control.Name == "ReaderMediaSidebarTags");
            Assert.Contains(tags.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "sample");
            Assert.Empty(sidebar.GetVisualDescendants().OfType<MediaFactGrid>());
            var edit = sidebar.GetVisualDescendants().OfType<Button>().Single(control =>
                control.Name == (width < 320 ? "ReaderMediaSidebarMore" : "ReaderMediaSidebarEditTags"));
            Assert.True(edit.IsEffectivelyVisible);
            Assert.Equal(decision.TranslatePoint(default, sidebar)!.Value.Y, edit.TranslatePoint(default, sidebar)!.Value.Y, 0.5);
            if (width < 320) BooruComponentDispatcher.Wait(sidebar.EditTagsAsync());
            else Click(edit);
            Assert.Equal(1, edits);
            Assert.IsType<PanelCloseButton>(sidebar.Close);
            Assert.Equal(1, sidebar.Close.Opacity);
            Assert.Equal(32, sidebar.Close.Bounds.Width, 0.5);
            Assert.True(sidebar.Close.IsHitTestVisible);
        }
        finally { window.Close(); }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
