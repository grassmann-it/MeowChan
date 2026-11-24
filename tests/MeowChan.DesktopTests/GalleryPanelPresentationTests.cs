using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class GalleryPanelPresentationTests
{
    public static IEnumerable<object[]> ActionCases() =>
        from language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes())
        from width in new[] { 280, 320 }
        from queue in new[] { false, true }
        select new object[] { language, width, queue };

    [Theory, MemberData(nameof(ActionCases))]
    public void SavedPostActionsFitAndOpenTheDisplayedThread(string language, int width, bool queue) =>
        WithViewer(language, 1200, width, (window, viewer, owner) =>
        {
            using TaggingWalk walk = new(owner.Entries.Select(e => new TaggingWalkItem(e.EntryKey,
                new(e.Key.Thread, e.Key.Directory, e.Key.AssetId, e.Post?.PostId, null, null, null), e.Asset.OriginalFileName!)).ToArray(),
                0, TagQueueSnapshot.Empty, new TaggingDecisionLogTests.FakeWriter());
            if (queue) viewer.StartTaggingMode(walk, owner);
            List<GalleryEntry> opened = [];
            viewer.ReadPostRequested += opened.Add;
            foreach (bool tags in new[] { false, true })
            {
                if (tags) viewer.ShowTagPanel(owner);
                else viewer.SidePanel.PostButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                var frame = Assert.IsType<Border>(viewer.SidePanel.Parent);
                Assert.Equal(width, frame.Bounds.Width, 0.5);
                var actions = Assert.Single(viewer.SidePanel.GetVisualDescendants().OfType<PanelActionStack>(),
                    p => p.IsEffectivelyVisible && p.Name == "GalleryPostActions");
                Button[] pair = actions.Children.OfType<Button>().ToArray();
                Assert.Equal(2, pair.Length);
                Assert.Equal(pair[0].Bounds.Y, pair[1].Bounds.Y, 0.5);
                Assert.Equal(pair[0].Bounds.Right + 8, pair[1].Bounds.Left, 0.5);
                Assert.True(pair[1].Bounds.Right <= actions.Bounds.Width + 0.5);
                foreach (Button button in pair)
                {
                    var label = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("panel-action-label"));
                    if (label.IsEffectivelyVisible) Assert.DoesNotContain(label.TextLayout.TextLines, line => line.HasCollapsed);
                    else
                    {
                        Assert.Same(pair[0], button);
                        Assert.Equal(32, button.Bounds.Width, 0.5);
                    }
                }
                string name = CommandCatalog.Label("gallery.show-in-thread");
                Assert.Equal(name, AutomationProperties.GetName(pair[0]));
                Assert.Contains(name, ToolTip.GetTip(pair[0])?.ToString());
                pair[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(owner.Entries[0], Assert.Single(opened));
                opened.Clear();
            }
        });

    [Theory]
    [InlineData(1200, false)]
    [InlineData(800, true)]
    public void MountedPostPanelFollowsTheWindowIntoLightTheme(int viewerWidth, bool overlay) =>
        WithViewer("de", viewerWidth, 320, (window, viewer, _) =>
        {
            var frame = Assert.IsType<Border>(viewer.SidePanel.Parent);
            Assert.True(viewer.IsVisualAncestorOf(frame));
            Assert.Equal(overlay, viewer.IsContextOverPicture);
            var dark = Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color;
            window.RequestedThemeVariant = ThemeVariant.Light;
            Settle(window);
            Assert.Equal(ThemeVariant.Light, viewer.ActualThemeVariant);
            Assert.Equal(ThemeVariant.Light, frame.ActualThemeVariant);
            var background = Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color;
            Assert.NotEqual(dark, background);
            Color Token(string key) => Assert.IsAssignableFrom<ISolidColorBrush>(frame.FindResource(ThemeVariant.Light, key)).Color;
            var surface = Token(overlay ? "Workspace.Overlay" : "Workspace.Panel");
            Assert.Equal((surface.R, surface.G, surface.B), (background.R, background.G, background.B));
            Assert.Equal(overlay ? 238 : 255, (int)background.A);
            var facts = Assert.Single(frame.GetVisualDescendants().OfType<MediaFactGrid>(), f => f.IsEffectivelyVisible);
            Assert.Equal(Token("Workspace.Muted"), Assert.IsAssignableFrom<ISolidColorBrush>(((TextBlock)facts.Children[0]).Foreground).Color);
            Assert.Equal(Token("Workspace.Text"), Assert.IsAssignableFrom<ISolidColorBrush>(((TextBlock)facts.Children[1]).Foreground).Color);
        });

    private static void WithViewer(string language, int viewerWidth, int panelWidth, Action<Window, GalleryViewer, IGalleryTagPanelOwner> inspect) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Texts.Initialize();
            string previous = Texts.Language;
            using TempDirectory temp = new();
            using ProfileLease lease = ProfileLease.Acquire(temp.Path);
            var owner = GalleryRatingKeyTests.LayoutOwner(new PrivateTagStore(temp.Path, lease));
            GalleryViewer viewer = new() { PanelWidth = panelWidth, DecodeImageAsync = (_, _, _) =>
                Task.FromResult<(Bitmap, GalleryImageInfo)>((new RenderTargetBitmap(new PixelSize(64, 64)), new(64, 64, "PNG", false))) };
            Window window = new() { Width = viewerWidth, Height = 900, Content = viewer,
                RequestedThemeVariant = ThemeVariant.Dark, Classes = { "motion-off" } };
            try
            {
                Texts.SetLanguage(language);
                window.Show();
                window.UpdateLayout();
                viewer.SetInteractionAvailable(true);
                BooruComponentDispatcher.Wait(viewer.ShowAsync(owner.Entries[0], CancellationToken.None));
                viewer.SetMediaTags([]);
                viewer.SetFavorite(false);
                viewer.SetRating(3, null);
                viewer.Configure(true, false, false, true, 0);
                Settle(window);
                inspect(window, viewer, owner);
            }
            finally
            {
                BooruComponentDispatcher.Wait(viewer.StopAsync());
                window.Close();
                Texts.SetLanguage(previous);
            }
        });

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
