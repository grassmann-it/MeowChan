using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using Xunit;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaToolbarLabelsTests
{
    [Fact]
    public void LabelsDependOnTheRowWidthOnlyAndTheFilterTextLeavesBeforeTheSidebarWordsAppear()
    {
        Assert.True(MediaToolbarLabels.FilterLabelWidth < MediaToolbarLabels.SidebarLabelsWidth);
        Assert.True(MediaToolbarLabels.FilterLabelWidth > MediaToolbarLayout.ReducedWidth);
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(0));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(0));
        Assert.False(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsFilterLabel(MediaToolbarLabels.FilterLabelWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth - 1));
        Assert.True(MediaToolbarLabels.ShowsSidebarLabels(MediaToolbarLabels.SidebarLabelsWidth));
        Assert.False(MediaToolbarLabels.ShowsSidebarLabels(double.NaN));
    }

    [Fact]
    public void TheDisplayedSidebarIconsKeepTheirParentsAndBothLabelsFollowResize() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("This test does not read the media library."));
        Window window = new() { Width = 950, Height = 700, Content = page };
        // The toolbar's layout is independent of transitions and their process-wide animation clock.
        window.Classes.Add(MotionPolicy.OffClass);
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tags = Assert.IsType<MediaToolbarLabels.SidebarContent>(page.BooruModeButton.Content);
            var media = Assert.IsType<MediaToolbarLabels.SidebarContent>(page.PanelToggleButton.Content);
            Control tagsIcon = Assert.IsAssignableFrom<Control>(tags.Children.OfType<ContentControl>().Single().Content);
            Control mediaIcon = Assert.IsType<Border>(media.Children.OfType<ContentControl>().Single().Content);
            var tagsLabel = Assert.Single(tags.Children.OfType<TextBlock>());
            var mediaLabel = Assert.Single(media.Children.OfType<TextBlock>());
            var tagsParent = tagsIcon.GetVisualParent();
            var mediaParent = mediaIcon.GetVisualParent();
            Assert.NotNull(tagsParent);
            Assert.NotNull(mediaParent);

            foreach ((double width, bool showsLabels) in new[] { (950d, false), (1800d, true), (950d, false) })
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(showsLabels, page.ToolbarRow.Bounds.Width >= MediaToolbarLabels.SidebarLabelsWidth);
                Assert.Equal(showsLabels, tagsLabel.IsVisible);
                Assert.Equal(showsLabels, mediaLabel.IsVisible);
                Assert.Same(tagsParent, tagsIcon.GetVisualParent());
                Assert.Same(mediaParent, mediaIcon.GetVisualParent());
            }
        }
        finally
        {
            try { window.Close(); }
            finally { page.EndLifetime(); }
        }
    });

    [Fact]
    public void TheSidebarWordsAreShortAndLocalizedInEnglishAndGerman()
    {
        Texts.Initialize();
        try
        {
            Texts.SetLanguage("en");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Media", Texts.T("media.toolbar.mediaLabel"));
            Texts.SetLanguage("de");
            Assert.Equal("Tags", Texts.T("media.toolbar.tagsLabel"));
            Assert.Equal("Medien", Texts.T("media.toolbar.mediaLabel"));
        }
        finally { Texts.SetLanguage("en"); }
    }
}
