using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Ratings;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class MediaPageContentBoundaryTests
{
    [Fact]
    public async Task RatingCorrectionRetiresTheGridWhileItsThumbnailIsDecoding()
    {
        using TempDirectory temporary = new();
        ConfigureWindow(temporary.Path);
        GalleryEntry[] entries = await SeedPostsAsync(temporary.Path, BooruPostClaimRating.Sensitive, BooruPostClaimRating.General);
        BooruComponentDispatcher.Run(ExerciseAsync);

        async Task ExerciseAsync()
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            await using DesktopSessionHost session = CreateSession(temporary.Path);
            await using MainWindow window = await ShowWindowAsync(session);
            MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
            TaskCompletionSource<CancellationToken> decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<Bitmap?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource discarded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int correctedDecodes = 0;
            page.TileThumbnailDecoder = (tile, token) =>
            {
                if (tile.Entry.BooruPostId != "102") return Task.FromResult<Bitmap?>(null);
                Interlocked.Increment(ref correctedDecodes);
                decoding.TrySetResult(token);
                return release.Task;
            };
            try
            {
                ById<Button>(window, "MediaNavigationButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await page.BuildTask;
                await page.PresentTask;
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                ListBox grid = ById<ListBox>(page, "MediaGrid");
                Assert.Equal(2, grid.ItemCount);
                CancellationToken decodingToken = await decoding.Task.WaitAsync(BooruComponentDispatcher.Cancellation);
                BooruContentProfileStore profile = new(session.ProfileLeaseForTests);
                long revision = profile.Load().Revision;

                CorrectRating(session, "102");
                // The shell posts the catalog notification at Send priority. Let it start the new presentation.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Normal);
                await page.PresentTask;
                window.UpdateLayout();

                Assert.True(decodingToken.IsCancellationRequested);
                Assert.Equal("101", Assert.IsType<MediaLibraryTileRow>(Assert.Single(grid.Items)).Entry.BooruPostId);
                Assert.Equal(revision, profile.Load().Revision);
                release.SetResult(new ObservedThumbnail(entries[1].Asset.Path, discarded));
                await discarded.Task.WaitAsync(BooruComponentDispatcher.Cancellation);
                Assert.Equal(1, Volatile.Read(ref correctedDecodes));
            }
            finally { release.TrySetResult(null); }
        }
    }

    [Fact]
    public void HostQueueChangesRefreshRealizedTilesAndReapplyTheQueueFilter() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temporary = new();
        ConfigureWindow(temporary.Path);
        GalleryEntry[] entries = await SeedPostsAsync(temporary.Path, BooruPostClaimRating.Sensitive, BooruPostClaimRating.General);
        await using DesktopSessionHost session = CreateSession(temporary.Path);
        await using MainWindow window = await ShowWindowAsync(session);
        MediaPage page = Assert.IsType<MediaPage>(window.MediaPageForTests);
        ById<Button>(window, "MediaNavigationButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await page.BuildTask;
        await page.PresentTask;
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        ListBox grid = ById<ListBox>(page, "MediaGrid");
        Assert.Equal(2, grid.ItemCount);
        MediaLibraryTileView[] realized = grid.GetVisualDescendants().OfType<MediaLibraryTileView>().ToArray();
        MediaLibraryTileView tile = Assert.Single(realized,
            view => view.DataContext is MediaLibraryTileRow { Entry.BooruPostId: "101" });
        MediaLibraryTileRow row = Assert.IsType<MediaLibraryTileRow>(tile.DataContext);
        TagQueueItem item = Assert.IsType<TagQueueItem>(TagQueueItem.From(entries[0], DateTimeOffset.UnixEpoch));
        MediaLibrarySnapshot snapshot = Assert.IsType<MediaLibrarySnapshot>(page.Snapshot);
        int presentations = page.PresentCount;
        Assert.False(tile.Badges.State.Queued);

        await session.EnqueueTagQueueAsync([item]);
        await BooruTestSupport.WaitAsync(() => tile.Badges.State.Queued, "the host queue change to reach the realized tile");
        Assert.Contains(Texts.T("media.badge.queued"), AutomationProperties.GetName(tile));
        Assert.Equal(realized.Length, page.QueueBadgeRefreshes);
        Assert.Equal(presentations, page.PresentCount);
        Assert.Same(snapshot, page.Snapshot);

        Assert.Null(page.AddFilterTerm(MediaFilterTerm.InQueue()));
        await page.PresentTask;
        Assert.Same(row.Entry, Assert.IsType<MediaLibraryTileRow>(Assert.Single(grid.Items)).Entry);
        await session.RemoveFromTagQueueAsync([item.Key]);
        await BooruTestSupport.WaitAsync(() => grid.ItemCount == 0, "the queue filter to reflect the removal");
        Assert.Same(snapshot, page.Snapshot);
    });

    private sealed class ObservedThumbnail(string path, TaskCompletionSource discarded) : Bitmap(path)
    {
        public override void Dispose()
        {
            base.Dispose();
            discarded.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RatingCorrectionAndTheFirstChoiceRetireAnOpeningGallery(bool firstChoice) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temporary = new();
        ConfigureWindow(temporary.Path);
        GalleryEntry[] entries = await SeedPostsAsync(temporary.Path, firstChoice ? null : BooruPostClaimRating.Sensitive,
            firstChoice ? BooruPostClaimRating.Explicit : BooruPostClaimRating.General);
        await using DesktopSessionHost session = CreateSession(temporary.Path);
        await using MainWindow window = await ShowWindowAsync(session);
        ContentControl host = ById<ContentControl>(window, "GalleryHost");
        TaskCompletionSource<CancellationToken> decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PropertyChanged += (_, change) =>
        {
            if (change.Property != ContentControl.ContentProperty || host.Content is not GalleryViewer viewer) return;
            viewer.DecodeImageAsync = async (entry, fit, token) =>
            {
                decoding.TrySetResult(token);
                await release.Task;
                return await GalleryImageCache.DecodeAsync(entry, fit, CancellationToken.None);
            };
        };
        Task opening = window.ArchivePage.OpenFavoritesGallery!(entries, entries[1].EntryKey, () => { });
        try
        {
            await BooruTestSupport.WaitAsync(() => decoding.Task.IsCompleted, "the first gallery decode to start");
            CancellationToken decodingToken = await decoding.Task;
            Assert.True(host.IsEffectivelyVisible);
            BooruContentProfileStore profile = new(session.ProfileLeaseForTests);
            if (firstChoice)
            {
                BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(session.ProfileLeaseForTests);
                BooruContentProfileSnapshot confirmed = SetCeiling(profile, session.ProfileLeaseForTests, BooruPostClaimRating.Sensitive);
                content.Publish(confirmed);
            }
            else
            {
                long revision = profile.Load().Revision;
                CorrectRating(session, "102");
                Assert.Equal(revision, profile.Load().Revision);
            }

            await BooruTestSupport.WaitAsync(() => !host.IsEffectivelyVisible && decodingToken.IsCancellationRequested,
                "the opening gallery to be closed while its decode is still held");
        }
        finally
        {
            release.TrySetResult();
            await opening;
        }
        await window.ArchivePage.OpenFavoritesGallery!(entries, entries[0].EntryKey, () => { });
        GalleryViewer allowed = Assert.IsType<GalleryViewer>(host.Content);
        Assert.Equal(1, allowed.Filmstrip.Count);
        Assert.Equal(entries[0].EntryKey, allowed.Filmstrip.EntryAt(0)!.EntryKey);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedTaggingEntriesUseTheSameGalleryBoundary(bool walk) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temporary = new();
        ConfigureWindow(temporary.Path);
        GalleryEntry[] entries = await SeedPostsAsync(temporary.Path, BooruPostClaimRating.Sensitive, BooruPostClaimRating.Explicit);
        await using DesktopSessionHost session = CreateSession(temporary.Path);
        await using MainWindow window = await ShowWindowAsync(session);
        TagQueueItem[] queued = entries.Select(entry => Assert.IsType<TagQueueItem>(TagQueueItem.From(entry, DateTimeOffset.UnixEpoch))).ToArray();
        await session.EnqueueTagQueueAsync(walk ? queued.Reverse().ToArray() : queued);
        ContentControl host = ById<ContentControl>(window, "GalleryHost");

        if (walk) await window.OpenTagQueueGalleryAsync();
        else
        {
            Assert.True(window.ShowTaggingPage());
            TaggingPage page = Assert.IsType<TaggingPage>(window.TaggingPage);
            await page.OpenGallery!(queued, 1);
            Assert.False(host.IsEffectivelyVisible);
            await page.OpenGallery!(queued, 0);
        }

        Assert.True(host.IsEffectivelyVisible);
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(host.Content);
        Assert.Equal(1, viewer.Filmstrip.Count);
        Assert.Equal(entries[0].EntryKey, viewer.Filmstrip.EntryAt(0)!.EntryKey);
        if (walk) Assert.Equal(queued[0].Key, Assert.Single(Assert.IsType<TaggingWalk>(window.GalleryTagging).Items).Key);
        Assert.Equal(2, session.GetTagQueue()!.Items.Count);
    });

    [Fact]
    public void FavoritesAndTheirGalleryUseTheConfirmedCeiling() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temporary = new();
        new Config
        {
            FirstRunSetupVersion = 1, DownloadsPaused = true, DownloadPath = Path.Combine(temporary.Path, "downloads"),
            Language = "en", SaveUrlsOnExit = false, GalleryFilmstripHeight = 96, GallerySlideshowSeconds = 1
        }.SaveConfig(temporary.Path);

        GalleryEntry first;
        GalleryEntry blocked;
        GalleryEntry last;
        using (ProfileLease lease = ProfileLease.Acquire(temporary.Path))
        {
            BooruCatalogStore catalog = new(temporary.Path, lease);
            FavoriteStore favorites = new(temporary.Path, lease);
            first = await SeedFavoriteAsync(temporary.Path, catalog, favorites, "101", BooruPostClaimRating.General, "g");
            blocked = await SeedFavoriteAsync(temporary.Path, catalog, favorites, "102", BooruPostClaimRating.Explicit, "e");
            last = await SeedFavoriteAsync(temporary.Path, catalog, favorites, "103", BooruPostClaimRating.Sensitive, "s");
        }

        await using DesktopSessionHost session = new(temporary.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await using MainWindow window = new(session) { Width = 1200, Height = 800 };
        window.Show();
        await window.StartupTimingRestoreTask;
        window.PlatformImpl!.Activated?.Invoke();

        Assert.Equal(3, window.ArchivePage.GetFavorites!().Count);
        ProfileLease sessionLease = session.ProfileLeaseForTests;
        SetCeiling(new BooruContentProfileStore(sessionLease), sessionLease, BooruPostClaimRating.Sensitive);
        Assert.Equal(new[] { first.Key.Thread, last.Key.Thread }, window.ArchivePage.GetFavorites!().Select(entry => entry.Key.Thread));
        Assert.Equal(3, session.GetFavorites().Count);

        GalleryEntry[] frozen = [first, blocked, last];
        bool returnedToFavorites = false;
        await window.ArchivePage.OpenFavoritesGallery!(frozen, first.EntryKey, () => returnedToFavorites = true);
        Assert.False(returnedToFavorites);
        window.UpdateLayout();
        ContentControl host = ById<ContentControl>(window, "GalleryHost");
        Assert.True(host.IsEffectivelyVisible);
        GalleryViewer viewer = Assert.IsType<GalleryViewer>(host.Content);
        Assert.Equal(2, viewer.Filmstrip.Count);
        Assert.Equal(first.EntryKey, viewer.Filmstrip.EntryAt(0)!.EntryKey);
        Assert.Equal(last.EntryKey, viewer.Filmstrip.EntryAt(1)!.EntryKey);
        viewer.RouteKeyForTests(new KeyEventArgs { Key = Key.Right });
        await BooruTestSupport.WaitAsync(() => viewer.CaptureState().Key == last.Key, "the next visible favorite");
        viewer.RouteKeyForTests(new KeyEventArgs { Key = Key.Left });
        await BooruTestSupport.WaitAsync(() => viewer.CaptureState().Key == first.Key
            && viewer.CanExecutePlaybackAction("gallery.slideshow-toggle"), "the first favorite to be ready for slideshow");
        await viewer.ExecutePlaybackActionAsync("gallery.slideshow-toggle");
        await BooruTestSupport.WaitAsync(() => viewer.CaptureState().Key == last.Key, "the slideshow to advance to the next visible favorite");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheActiveBoundaryRunsBeforeThumbnailLoadingAndRaisingItRestoresOnlyKnownMedia(bool unknown) =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory temporary = new();
            using ProfileLease lease = ProfileLease.Acquire(temporary.Path);
            lease.Admission.CompleteStartup();
            Texts.SetLanguage("de");

            BooruContentProfileStore profile = new(lease);
            SetCeiling(profile, lease, BooruPostClaimRating.Sensitive);
            string path = Path.Combine(temporary.Path, "poster.png");
            byte[] bytes = [7, 1, 5, 2, 6];
            File.WriteAllBytes(path, bytes);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            BooruPostClaim post = new("danbooru", "123", unknown ? BooruPostClaimPresence.None : BooruPostClaimPresence.Rating,
                Rating: unknown ? BooruPostClaimRating.Unknown : BooruPostClaimRating.Explicit, RawRating: unknown ? null : "e");
            MediaAssetReference asset = new(hash, BooruMediaIdentity.MediaIdentifier("123"), "original", "poster.png", path, bytes.Length, hash,
                "https://offline.invalid/poster.png", OriginalFileName: "poster.png", IsCurrent: true);
            BooruCatalogSnapshot catalog = new(1, [new("danbooru", "123", post, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, asset.AssetId, path)], []);
            MediaAssetSnapshot manifest = new(temporary.Path, MediaManifestStatus.Available, 1, "fixture", [asset]);
            FavoriteAssetKey key = FavoriteAssetKey.From(BooruMediaIdentity.Thread("123"), asset);
            FavoriteStore favorites = new(temporary.Path, lease);
            MediaRatingStore ratings = new(temporary.Path, lease);
            PrivateTagStore tags = new(temporary.Path, lease);
            FavoriteEntry favorite = new(key, bytes.Length, asset.OriginalFileName);
            favorites.SetFavorite(favorite, true);
            ratings.Set(key, 4);

            int decodes = 0;
            MediaPage page = new(_ => throw new FileNotFoundException("This profile has no archived threads."), (_, _) => Task.FromResult(manifest))
            {
                ReadContentProfile = profile.Load,
                ReadBooruCatalog = () => catalog,
                ReadUserData = () => new MediaLibraryUserData(tags.ReadSnapshot(), favorites.List(), ratings.List()),
                TileThumbnailDecoder = (_, _) =>
                {
                    Interlocked.Increment(ref decodes);
                    return Task.FromResult<Bitmap?>(null);
                }
            };
            Window window = new() { Width = 1200, Height = 800, Content = page };
            try
            {
                window.Show();
                await page.ActivateAsync();
                await page.PresentTask;
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                ListBox grid = ById<ListBox>(page, "MediaGrid");
                Assert.Empty(grid.Items);
                Assert.Equal(0, Volatile.Read(ref decodes));
                TextBlock empty = ById<TextBlock>(page, "MediaEmpty");
                Assert.True(empty.IsEffectivelyVisible);
                Assert.Equal("1 über deiner Inhaltsgrenze", empty.Text);

                MediaLibraryEntry indexed = Assert.Single(page.Snapshot!.Entries);
                MediaTagSubject subject = Assert.IsAssignableFrom<MediaTagSubject>(MediaTagSubjects.Of(indexed));
                tags.DefineAndSetMediaSubjects("keep", [subject]);
                page.RefreshUserData();
                Assert.Null(page.AddFilterTerm(MediaFilterTerm.Flag(MediaFilterField.Favorite)));
                Assert.Empty(grid.Items);

                SetCeiling(profile, lease, BooruPostClaimRating.Explicit);
                page.RefreshContentBoundary();
                await page.PresentTask;
                window.UpdateLayout();
                if (unknown)
                {
                    Assert.Empty(grid.Items);
                    Assert.Equal(0, Volatile.Read(ref decodes));
                    Assert.Contains("1 über deiner Inhaltsgrenze", ById<TextBlock>(page, "MediaStatus").Text);
                }
                else
                {
                    MediaLibraryTileRow row = Assert.IsType<MediaLibraryTileRow>(Assert.Single(grid.Items));
                    Assert.Same(indexed, row.Entry);
                    MediaLibraryEntryState state = Assert.IsType<MediaLibraryEntryState>(row.State);
                    Assert.Equal(MediaLibraryFlag.Yes, state.Favorite);
                    Assert.Equal(4, state.UserRating);
                    Assert.Equal("keep", Assert.Single(state.MediaTags));
                    await BooruTestSupport.WaitAsync(() => Volatile.Read(ref decodes) > 0, "the visible tile to request its thumbnail");
                }
                Assert.Equal(bytes, File.ReadAllBytes(path));
                Assert.Equal(favorite, Assert.Single(favorites.List()));
                Assert.Equal(4, Assert.Single(ratings.List()).Value);
                Assert.Equal("keep", Assert.Single(tags.ReadSnapshot().TagsOf(subject)).Name.Display);
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });

    private static T ById<T>(Visual view, string id) where T : Control =>
        Assert.Single(view.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static void ConfigureWindow(string path) => new Config
    {
        FirstRunSetupVersion = 1, DownloadsPaused = true, DownloadPath = Path.Combine(path, "downloads"),
        Language = "en", SaveUrlsOnExit = false, GalleryFilmstripHeight = 96, Animations = "Off"
    }.SaveConfig(path);

    private static DesktopSessionHost CreateSession(string path) => new(path, [],
        updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1"));

    private static async Task<MainWindow> ShowWindowAsync(DesktopSessionHost session)
    {
        MainWindow window = new(session) { Width = 1200, Height = 800 };
        window.Show();
        await window.StartupTimingRestoreTask;
        window.PlatformImpl!.Activated?.Invoke();
        return window;
    }

    private static async Task<GalleryEntry[]> SeedPostsAsync(string path, BooruPostClaimRating? ceiling, BooruPostClaimRating secondRating)
    {
        using ProfileLease lease = ProfileLease.Acquire(path);
        lease.Admission.CompleteStartup();
        if (ceiling is { } maximum) SetCeiling(new BooruContentProfileStore(lease), lease, maximum);
        BooruCatalogStore catalog = new(path, lease);
        FavoriteStore favorites = new(path, lease);
        GalleryEntry first = await SeedFavoriteAsync(path, catalog, favorites, "101", BooruPostClaimRating.General, "g");
        GalleryEntry second = await SeedFavoriteAsync(path, catalog, favorites, "102", secondRating,
            secondRating == BooruPostClaimRating.General ? "g" : "e");
        return [first, second];
    }

    private static void CorrectRating(DesktopSessionHost session, string id) =>
        new BooruCatalogStore(session.ProfileDirectory, session.ProfileLeaseForTests).UpsertPost(
            new("danbooru", id, BooruPostClaimPresence.Rating, Rating: BooruPostClaimRating.Explicit, RawRating: "e"),
            DateTimeOffset.UnixEpoch.AddDays(1));

    private static async Task<GalleryEntry> SeedFavoriteAsync(string profile, BooruCatalogStore catalog, FavoriteStore favorites,
        string id, BooruPostClaimRating rating, string raw)
    {
        string directory = Path.Combine(profile, "downloads", id);
        Directory.CreateDirectory(directory);
        const string name = "fixture.png";
        string path = Path.Combine(directory, name);
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGMQMgnDihiGlgQALKknAeyzZJgAAAAASUVORK5CYII=");
        if (id == "102") bytes = [.. bytes, 0]; // A distinct byte key for the two thumbnails.
        await File.WriteAllBytesAsync(path, bytes);
        var item = new
        {
            MediaIdentifier = BooruMediaIdentity.MediaIdentifier(id), Variant = "original", SourceUrl = "https://offline.invalid/" + id,
            FileName = name, OriginalFileName = name, Length = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Md5 = Convert.ToBase64String(MD5.HashData(bytes))
        };
        string manifestPath = Path.Combine(directory, ".meowchan-manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { [name] = item } }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = Assert.Single(manifest.GetPage());
        catalog.UpsertPost(new BooruPostClaim("danbooru", id, BooruPostClaimPresence.Rating, Rating: rating, RawRating: raw), DateTimeOffset.UnixEpoch);
        catalog.LinkLocalAsset("danbooru", id, asset.AssetId, path);
        ResourceId thread = BooruMediaIdentity.Thread(id);
        favorites.SetFavorite(new FavoriteEntry(FavoriteAssetKey.From(thread, asset), bytes.Length, name), true);
        return new GalleryEntry(new GalleryAssetKey(thread, directory, asset.AssetId), manifest, asset, null, null, false, true);
    }

    private static BooruContentProfileSnapshot SetCeiling(BooruContentProfileStore store, ProfileLease lease, BooruPostClaimRating ceiling)
    {
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
        return store.SaveAdmitted(new BooruContentSettings(ceiling, string.Empty, []), store.Load().Revision, writer);
    }
}
