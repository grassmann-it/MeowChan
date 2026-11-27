using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
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

    private static async Task<GalleryEntry> SeedFavoriteAsync(string profile, BooruCatalogStore catalog, FavoriteStore favorites,
        string id, BooruPostClaimRating rating, string raw)
    {
        string directory = Path.Combine(profile, "downloads", id);
        Directory.CreateDirectory(directory);
        const string name = "fixture.png";
        string path = Path.Combine(directory, name);
        byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR4nGMQMgnDihiGlgQALKknAeyzZJgAAAAASUVORK5CYII=");
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

    private static void SetCeiling(BooruContentProfileStore store, ProfileLease lease, BooruPostClaimRating ceiling)
    {
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
        store.SaveAdmitted(new BooruContentSettings(ceiling, string.Empty, []), store.Load().Revision, writer);
    }
}
