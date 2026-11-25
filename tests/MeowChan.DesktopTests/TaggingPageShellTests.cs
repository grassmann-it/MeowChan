using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingPageShellTests
{
    [Fact]
    public void AQueuedArchiveOccurrenceCanBeRatedAndFavoritedAfterRecapture() => WithTaggingPage(true, async (window, session, key) =>
    {
        ToggleButton favorite = Find<ToggleButton>(window, "ReaderMediaSidebarFavorite");
        ToggleButton thirdStar = Find<ToggleButton>(window, "ReaderMediaSidebarDecisionRating3");
        await WaitAsync(window, () => favorite.IsEffectivelyEnabled && thirdStar.IsEffectivelyEnabled
            && thirdStar.IsEffectivelyVisible, "sidebar decisions after verifying the saved picture");
        AssertFavoriteHelp(favorite, "viewer.favorite.add");

        Point starCenter = thirdStar.TranslatePoint(new Rect(thirdStar.Bounds.Size).Center, window)!.Value;
        window.MouseMove(starCenter);
        window.UpdateLayout();
        starCenter = thirdStar.TranslatePoint(new Rect(thirdStar.Bounds.Size).Center, window)!.Value;
        window.MouseDown(starCenter, MouseButton.Left);
        window.MouseUp(starCenter, MouseButton.Left);
        await WaitAsync(window, () => session.GetMediaRating(key) == 3 && thirdStar.IsChecked == true, "rating from the sidebar star");

        Assert.True(Find<ListBoxItem>(window, "TaggingListEntry1").Focus());
        Press(window, PhysicalKey.Digit4);
        await WaitAsync(window, () => session.GetMediaRating(key) == 4
            && Find<ToggleButton>(window, "ReaderMediaSidebarDecisionRating4").IsChecked == true, "rating from digit 4");

        Assert.True(Find<ListBoxItem>(window, "TaggingListEntry1").Focus());
        Press(window, PhysicalKey.F);
        await WaitAsync(window, () => session.GetFavorites().Any(entry => entry.Key == key) && favorite.IsChecked == true,
            "favorite from F");
        AssertFavoriteHelp(favorite, "viewer.favorite.remove");

        static void AssertFavoriteHelp(ToggleButton favorite, string labelKey)
        {
            string tip = Texts.T(labelKey) + " · F";
            Assert.Equal(tip, ToolTip.GetTip(favorite));
            Assert.Equal(tip, AutomationProperties.GetHelpText(favorite));
        }
    });

    [Fact]
    public void SidebarShortcutsWorkWhileTheTaggingSidebarIsClosed() => WithTaggingPage(false, async (window, session, _) =>
    {
        ToggleButton toggle = Find<ToggleButton>(window, "TaggingPanelToggle");
        Assert.False(toggle.IsChecked);

        bool autoHide = session.Config.MediaSidebarAutoHide;
        Press(window, PhysicalKey.A, RawInputModifiers.Shift);
        await WaitAsync(window, () => session.Config.MediaSidebarAutoHide != autoHide, "autohide choice from Shift+A");
        Assert.False(toggle.IsChecked);

        BooruReviewTestPlatform.Resize(window, 900);
        Press(window, PhysicalKey.I, RawInputModifiers.Shift);
        await WaitAsync(window, () => session.Config.TaggingPagePanel && session.Config.SidebarFactsVisible, "sidebar and facts choice from Shift+I");
        Assert.Equal(Texts.T("tagging.page.panel.narrow"), Find<TextBlock>(window, "TaggingStatus").Text);
        Assert.False(Find<Control>(window, "TaggingPanel").IsEffectivelyVisible);

        BooruReviewTestPlatform.Resize(window, 1440);
        await WaitAsync(window, () => Find<Control>(window, "ReaderMediaSidebarFacts").IsEffectivelyVisible, "sidebar facts after widening");
    });

    private static void WithTaggingPage(bool panelEnabled, Func<MainWindow, DesktopSessionHost, FavoriteAssetKey, Task> test) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(RunAsync());

            async Task RunAsync()
            {
                using TempDirectory profile = new();
                using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
                ArchivedThreadView capture = RateAndTagWindowFixture.View(media);
                new Config
                {
                    DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1, DownloadsPaused = true,
                    WorkspaceMode = "Advanced", Language = "en", TaggingPageView = "List", TaggingPagePanel = panelEnabled,
                    MediaSidebarFacts = false, MediaSidebarAutoHide = false
                }.SaveConfig(profile.Path);
                await RateAndTagWindowFixture.SeedAsync(profile.Path, capture);
                TagQueueItem item = Assert.IsType<TagQueueItem>(
                    TagQueueItem.From(Assert.Single(GallerySelection.Build(capture, [media.Snapshot]).Entries), DateTimeOffset.UnixEpoch));
                await RateAndTagWindowFixture.SeedAsync(profile.Path, capture);

                await using DesktopSessionHost session = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await session.StartAsync();
                await using (ArchiveReader reader = await session.OpenArchiveReaderAsync())
                {
                    ArchivedThreadView archived = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(item.Thread));
                    Assert.True(archived.Summary.Revision > 0);
                }
                await session.EnqueueTagQueueAsync([item]);
                await using MainWindow window = new(session) { Width = 1440, Height = 900, Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                Assert.True(window.ShowTaggingPage());
                await WaitAsync(window, () => window.TaggingPage is { IsBusy: false, Queue.Count: 1 }, "Tagging queue in the shell");
                FavoriteAssetKey key = FavoriteAssetKey.From(item.Thread, media.Asset);
                await test(window, session, key);
            }
        });

    private static T Find<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control =>
            (AutomationProperties.GetAutomationId(control) is { Length: > 0 } automationId
                ? automationId : ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId()) == id);

    private static Task WaitAsync(Window window, Func<bool> condition, string expected) => BooruTestSupport.WaitAsync(() =>
    {
        window.UpdateLayout();
        return condition();
    }, expected);

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }
}
