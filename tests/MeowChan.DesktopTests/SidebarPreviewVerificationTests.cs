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
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SidebarPreviewVerificationTests
{
    [Theory]
    [InlineData("favorite")]
    [InlineData("rating")]
    [InlineData("menu")]
    public void FocusedStripItemUsesItsOwnOriginal(string command) => SidebarContextGestureTests.Run(async (window, media) =>
    {
        await ShowSidebarAsync(window, "Reader", new ResourceId("fixture", "g", "42"));
        ArchivePage page = window.ArchivePage;
        await WaitAsync(window, () => page.MediaSidebar?.Shown != null, "the first picture");
        ReaderMediaSidebar sidebar = page.MediaSidebar!;
        ListBox strip = Assert.Single(sidebar.GetVisualDescendants().OfType<ListBox>(), list =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(list)?.GetAutomationId() == "ReaderMediaSidebarStrip");
        ListBoxItem second = Assert.Single(strip.Items.OfType<ListBoxItem>(), item =>
            AutomationProperties.GetName(item)?.Contains("b.png", StringComparison.Ordinal) == true);
        Assert.True(second.Focus());
        Assert.Equal(0, page.MediaSidebarIndex);
        if (command == "favorite")
        {
            await ChangeBytesAsync(media[1].Path);
            ContextGestures.KeyStroke(second, Key.M, PhysicalKey.M);
            await page.FavoriteKeyTask;
            Assert.Empty(window.SessionHost.GetFavorites());
            return;
        }

        await ChangeBytesAsync(media[0].Path);
        if (command == "rating")
        {
            ContextGestures.KeyStroke(second, Key.D3, PhysicalKey.Digit3, RawInputModifiers.Shift);
            await BooruTestSupport.WaitAsync(() => window.SessionHost.GetMediaRatings().Count > 0, "the focused original's rating");
            Assert.Equal("778", Assert.Single(window.SessionHost.GetMediaRatings()).Key.MediaIdentifier);
        }
        else
        {
            ContextGestures.MenuKey(second);
            ContextMenu menu = Assert.IsType<ContextMenu>(page.ArchiveMediaMenu);
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "favorites.toggle").Activate();
            await BooruTestSupport.WaitAsync(() => window.SessionHost.GetFavorites().Count > 0, "the focused original's favorite");
            Assert.Equal("778", Assert.Single(window.SessionHost.GetFavorites()).Key.MediaIdentifier);
        }
    });

    [Fact]
    public void UnnamedAttachmentRemainsBlockedUntilItsOriginalIsVerified() => SidebarContextGestureTests.Run(async (window, media) =>
    {
        await ShowSidebarAsync(window, "Reader", new ResourceId("fixture", "g", "42"));
        ArchivePage page = window.ArchivePage;
        await WaitAsync(window, () => page.MediaSidebar?.Shown != null, "the first picture");
        ReaderMediaSidebar sidebar = page.MediaSidebar!;
        TaskCompletionSource started = new(), resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sidebar.BeforePreviewDecodeForTests = (path, token) =>
        {
            if (path != media[1].Path) return Task.CompletedTask;
            started.TrySetResult();
            return resume.Task.WaitAsync(token);
        };
        try
        {
            await ChangeBytesAsync(media[1].Path);
            page.SelectMediaSidebarIndex(1);
            await BooruTestSupport.WaitAsync(() => started.Task.IsCompleted, "verification of 778 to start");
            Assert.Contains("778", sidebar.HeaderLine);
            Assert.False(await sidebar.VerifyImageActionAsync());
            Assert.False(sidebar.FavoriteButton.IsEffectivelyEnabled);
            Assert.Empty(window.SessionHost.GetFavorites());
        }
        finally { resume.TrySetResult(); await page.MediaSidebarTask; }
        Assert.False(sidebar.CanUseMediaActions);
    }, unnamedSecond: true);

    [Fact]
    public void FilteringADuplicateTileVerifiesTheVisibleOccurrence() => SidebarContextGestureTests.Run(async (window, media) =>
    {
        Click(window, Visible<Button>(window, "MediaNavigationButton"));
        MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
        await page.BuildTask;
        await BooruTestSupport.WaitAsync(() => page.Grid.ItemCount == 1, "the grouped tile");
        page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
        page.Grid.SelectedIndex = 0;
        await BooruTestSupport.WaitAsync(() => page.Panel?.Shown != null, "the first occurrence's picture");
        string first = page.PanelRow!.Entry.Name!;
        SidebarContextGestureTests.SavedFile other = Assert.Single(media, file => Path.GetFileName(file.Path) != first);
        page.NameFilter.Text = Path.GetFileName(other.Path);
        page.NameFilter.FlushQuery();
        await WaitAsync(window, () => page.PanelRow?.Entry.Name == Path.GetFileName(other.Path), "the filtered file in the panel");
        await page.PanelTask;
        File.Delete(Assert.Single(media, file => Path.GetFileName(file.Path) == first).Path);
        Assert.True(await page.Panel!.VerifyImageActionAsync());
        await ChangeBytesAsync(other.Path);
        Assert.False(await page.Panel.VerifyImageActionAsync());
    }, copies: true);

    private static async Task ChangeBytesAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
    }

    [Fact]
    public async Task OversizedPreviewReusesOnlyTheUnchangedVerifiedOccurrence()
    {
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAJxAAAB9BCAIAAABpcsUXAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
        using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false, image: bytes);
        GalleryEntry entry = Assert.Single(GallerySelection.Build(RateAndTagWindowFixture.View(media), [media.Snapshot]).Entries);
        Task Decode() => GalleryImageDecoder.DecodePreviewAsync(entry, new GalleryDecodeRequest.Fit(40, 40),
            Config.SidebarPreviewMaximumPixelsDefault, () => { }, CancellationToken.None);
        using var run = MeowChan.Diagnostics.PerformanceDiagnostics.BeginRun("oversized-sidebar", "fixture", "png");
        await Assert.ThrowsAsync<GalleryPreviewTooLargeException>(Decode);
        await Assert.ThrowsAsync<GalleryPreviewTooLargeException>(Decode);
        await Assert.ThrowsAsync<GalleryPreviewTooLargeException>(() => GalleryImageDecoder.DecodePreviewAsync(entry,
            new GalleryDecodeRequest.Fit(40, 40), Config.SidebarPreviewMaximumPixelsDefault, null, CancellationToken.None, background: true));
        Assert.Single(run.Snapshot().Events, e => e.Phase == "media.original.verify" && e.Outcome == "verified" && e.Asset == entry.Asset.AssetId);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(media.Asset.Path, bytes);
        File.SetLastWriteTimeUtc(media.Asset.Path, DateTime.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<InvalidDataException>(Decode);
    }

    [Theory]
    [InlineData("Reader", false)]
    [InlineData("Reader", true)]
    [InlineData("Media", false)]
    [InlineData("Media", true)]
    [InlineData("Tagging", false)]
    [InlineData("Tagging", true)]
    public void LargeImageActionsDependOnTheSavedChecksum(string host, bool wrongSha) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            BooruComponentDispatcher.Wait(ExerciseAsync());

            async Task ExerciseAsync()
            {
                using TempDirectory profile = new();
                // A PNG header declares 10,000 × 8,001 pixels. The existing tiny IDAT keeps the fixture under 100 bytes.
                byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAJxAAAB9BCAIAAABpcsUXAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
                using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false, image: image);
                ArchivedThreadView thread = RateAndTagWindowFixture.View(media);
                GalleryEntry entry = Assert.Single(GallerySelection.Build(thread, [media.Snapshot]).Entries);
                FavoriteAssetKey key = FavoriteAssetKey.From(entry.Key.Thread, entry.Asset);
                MediaTagSubject subject = Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(entry));
                new Config
                {
                    DownloadPath = Path.Combine(profile.Path, "downloads"), FirstRunSetupVersion = 1, DownloadsPaused = true,
                    WorkspaceMode = "Advanced", Language = "en", WindowWidth = 1600, WindowHeight = 900,
                    ReaderMediaSidebar = true, ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
                    ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum,
                    MediaSidebarAutoHide = false, MediaSidebarFacts = true,
                    MediaLayout = (new MediaPageLayout { Panel = true }).Format(),
                    TaggingPagePanel = true, TaggingPageView = "List"
                }.SaveConfig(profile.Path);
                await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);
                if (wrongSha)
                {
                    image[^1] ^= 1;
                    await File.WriteAllBytesAsync(media.Asset.Path, image);
                }

                await using DesktopSessionHost session = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await session.StartAsync();
                if (host == "Tagging")
                    await session.EnqueueTagQueueAsync([Assert.IsType<TagQueueItem>(TagQueueItem.From(entry, DateTimeOffset.UnixEpoch))]);
                await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                try
                {
                    await ShowSidebarAsync(window, host, thread.Summary.Id);
                    string placeholder = Texts.T(wrongSha ? "reader.sidebar.unavailable" : "archive.tooLarge");
                    await WaitAsync(window, () => Find<TextBlock>(window, "ReaderMediaSidebarStatus")?.Text == placeholder
                        && Find<Image>(window, "ReaderMediaSidebarPicture", visibleOnly: false)?.Source == null, $"the {host} preview to report {placeholder}");

                    ToggleButton favorite = Visible<ToggleButton>(window, "ReaderMediaSidebarFavorite");
                    ToggleButton rating = Visible<ToggleButton>(window, "ReaderMediaSidebarDecisionRating3");
                    const string editId = "ReaderMediaSidebarEditTags";
                    if (wrongSha)
                    {
                        Assert.False(favorite.IsEffectivelyEnabled);
                        Assert.False(rating.IsEffectivelyEnabled);
                        Assert.False(Find<Button>(window, editId) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true });
                        Click(window, favorite);
                        Click(window, rating);
                        Assert.Null(QuickTagPopover.Current);
                        Assert.Empty(session.GetFavorites());
                        Assert.Empty(session.GetMediaRatings());
                        Assert.Empty(session.GetPublishedPrivateTagSnapshot().Snapshot!.Tags);
                        return;
                    }

                    await WaitAsync(window, () => favorite.IsEffectivelyEnabled && rating.IsEffectivelyEnabled
                        && Find<Button>(window, editId) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }, $"the {host} actions to enable");
                    Click(window, favorite);
                    await WaitAsync(window, () => session.GetFavorites().Any(), "the favorite write");
                    Assert.Equal(key, Assert.Single(session.GetFavorites()).Key);
                    await WaitAsync(window, () => rating.IsEffectivelyEnabled && favorite.IsChecked == true, "the saved favorite to appear");
                    Click(window, rating);
                    await WaitAsync(window, () => session.GetMediaRating(key) == 3 && rating.IsChecked == true, "the third star to persist");
                    Assert.Equal(key, Assert.Single(session.GetMediaRatings()).Key);
                    await WaitAsync(window, () => Find<Button>(window, editId) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }, "the tag editor to enable");
                    Click(window, Visible<Button>(window, editId));
                    string inputId = (host == "Reader" ? "Archive" : host) + "TagPopoverInput";
                    await WaitAsync(window, () => Find<IdentitySuggestionBox>(window, inputId) is { IsKeyboardFocusWithin: true }, "the sidebar tag field");
                    IdentitySuggestionBox input = Visible<IdentitySuggestionBox>(window, inputId);
                    const string tag = "sidebarpreviewfixture";
                    input.Text = tag;
                    TopLevel popup = TopLevel.GetTopLevel(input)!;
                    popup.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                    popup.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                    await WaitAsync(window, () => session.GetPublishedPrivateTagSnapshot().Snapshot!.TagsOf(subject).Count > 0, "the tag write");
                    var saved = session.GetPublishedPrivateTagSnapshot().Snapshot!;
                    Assert.Equal(tag, Assert.Single(saved.TagsOf(subject)).Name.Display);
                    Assert.Single(saved.Tags);
                }
                finally { QuickTagPopover.Current?.Close(); }
            }
        });

    private static async Task ShowSidebarAsync(MainWindow window, string host, ResourceId thread)
    {
        if (host == "Reader")
        {
            Click(window, Visible<Button>(window, "ArchiveNavigationButton"));
            Assert.Equal(MainWindow.WorkspacePage.Archive, window.SelectedPage);
            ArchivePage page = window.ArchivePage;
            await page.OpenReaderOnThreadAsync(thread, () => page.OpenReaderRequested!.Invoke(null));
            await WaitAsync(window, () => Find<ListBox>(window, "ArchivePosts") is { ItemCount: > 0 }, "the Reader posts");
            Visible<ListBox>(window, "ArchivePosts").SelectedIndex = 0;
            await page.MediaLoadTask;
        }
        else if (host == "Media")
        {
            Click(window, Visible<Button>(window, "MediaNavigationButton"));
            Assert.Equal(MainWindow.WorkspacePage.Media, window.SelectedPage);
            MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
            await page.BuildTask;
            await WaitAsync(window, () => page.Grid.ItemCount == 1, "the Media tile");
            page.Grid.SelectedIndex = 0;
        }
        else
        {
            Assert.True(window.ShowTaggingPage());
            await WaitAsync(window, () => window.TaggingPage is { IsBusy: false, Queue.Count: 1 }, "the Tagging queue");
        }
    }

    private static Task WaitAsync(Window window, Func<bool> condition, string expected) => BooruTestSupport.WaitAsync(() =>
    {
        window.UpdateLayout();
        return condition();
    }, expected);

    private static T Visible<T>(Window window, string id) where T : Control => Assert.IsAssignableFrom<T>(Find<T>(window, id));

    private static T? Find<T>(Window window, string id, bool visibleOnly = true) where T : Control =>
        window.GetVisualDescendants().Concat(window.OpenedPopups.SelectMany(popup =>
            popup.Child?.GetSelfAndVisualDescendants() ?? []))
            .OfType<T>().FirstOrDefault(control => (!visibleOnly || control.IsEffectivelyVisible)
                && (AutomationProperties.GetAutomationId(control) is { Length: > 0 } explicitId
                    ? explicitId : ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId()) == id);

    private static void Click(Window window, Control control)
    {
        Point center = control.TranslatePoint(new Rect(control.Bounds.Size).Center, window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }
}
