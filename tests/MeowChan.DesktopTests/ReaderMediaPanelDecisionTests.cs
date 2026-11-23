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
            OpenTagsAsync = _ => { edits++; return Task.CompletedTask; } };
        sidebar.Describe("No. 42", "picture.png", "640×480", true, savedBytes: 4096);
        sidebar.ConfigureRating(() => true, () => rating, value => { rating = value; return Task.CompletedTask; });
        sidebar.ConfigureFavorite(() => true, () => favorite, () => { favorite = !favorite; return Task.CompletedTask; });
        sidebar.SetTags(["sample"], null);
        Window window = new() { Width = 520, Height = 900, Content = sidebar };
        try
        {
            window.Show();
            window.UpdateLayout();
            GalleryDecisionRow decision = Assert.Single(sidebar.GetVisualDescendants().OfType<GalleryDecisionRow>());
            Assert.Equal(3, decision.View.Rating);
            Assert.True(decision.StarButtons[2].IsChecked);
            Click(decision.StarButtons[4]);
            Assert.Equal(5, rating);
            Assert.Equal(5, decision.View.Rating);
            Click(decision.ClearButton);
            Assert.Null(rating);
            Click(decision.FavoriteButton);
            Assert.True(favorite);
            Assert.True(decision.FavoriteButton.IsChecked);
            Control tags = sidebar.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "ReaderMediaSidebarTags");
            Assert.Contains(tags.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "sample");
            MediaFactGrid facts = Assert.Single(sidebar.GetVisualDescendants().OfType<MediaFactGrid>());
            Button edit = sidebar.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "ReaderMediaSidebarEditTags");
            double Top(Control control) => control.TranslatePoint(default, sidebar)!.Value.Y;
            Assert.True(Top(decision) < Top(tags));
            Assert.True(Top(tags) < Top(facts));
            Assert.True(Top(facts) < Top(edit));
            Assert.Equal(sidebar.Open.Bounds.Width, edit.Bounds.Width, 0.5);
            Click(edit);
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
