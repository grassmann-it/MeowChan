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
            bool narrow = width < 320;
            var edit = sidebar.GetVisualDescendants().OfType<Button>().Single(control =>
                control.Name == (narrow ? "ReaderMediaSidebarMore" : "ReaderMediaSidebarEditTags"));
            MenuItem OverflowAction(int index)
            {
                MenuFlyout menu = Assert.IsType<MenuFlyout>(edit.Flyout);
                menu.ShowAt(edit);
                window.UpdateLayout();
                var items = menu.ItemsSource!.Cast<MenuItem>().ToArray();
                Assert.Equal(2, items.Length);
                Assert.NotNull(items[index].InputGesture);
                menu.Hide();
                return items[index];
            }
            Assert.Equal(!narrow, decision.ClearButton.IsEffectivelyVisible);
            if (narrow) OverflowAction(0).Command!.Execute(null);
            else Click(decision.ClearButton);
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
            if (narrow) OverflowAction(1).Command!.Execute(null);
            else Click(edit);
            Assert.Same(edit, editorAnchor);
            if (narrow)
            {
                MenuItem stale = OverflowAction(1);
                editorAnchor = null;
                sidebar.Describe("No. 43", "other.png", "", true, mediumKey: "other");
                Assert.False(stale.Command!.CanExecute(null));
                stale.Command.Execute(null);
                Assert.Null(editorAnchor);
            }
            Assert.IsType<PanelCloseButton>(sidebar.Close);
            Assert.Equal(1, sidebar.Close.Opacity);
            Assert.Equal(32, sidebar.Close.Bounds.Width, 0.5);
            Assert.True(sidebar.Close.IsHitTestVisible);
        }
        finally { window.Close(); }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
