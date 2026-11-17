using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

/// <summary>
/// UX final review 17.11.2026, PF-01 / PF-02 / PF-03 and AR-04: an unreadable favorites file turns every star into a disabled "unavailable" one with
/// one notice and without touching the file; the overview's scope loads let the newest scope win; a post attachment's star says "checking" while the
/// thread's media scan runs; and the favorites resolution remembers the originals it has verified.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class FavoritesUnavailableTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId ThreadId = new("source", "board", "42");
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    public FavoritesUnavailableTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- fixtures ----

    /// <summary>
    /// A profile whose favorites index cannot be read: invalid JSON. (A symbolic link, even a dangling one, takes the same road: the store opens the
    /// file through RegularProfileFile, which refuses anything that is not a regular file with an InvalidDataException.)
    /// </summary>
    private static byte[] DamageFavorites(string profile)
    {
        new Config().SaveConfig(profile);
        byte[] damaged = "{ this is not a favorites index"u8.ToArray();
        File.WriteAllBytes(Path.Combine(profile, "media-favorites.json"), damaged);
        return damaged;
    }

    private static async Task<(MediaAssetSnapshot Manifest, MediaAssetReference Asset, GalleryEntry Entry)> MissingOriginalAsync(string root)
    {
        string directory = Path.Combine(root, "missing-original");
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = new("asset", "image", "original", "image.png",
            Path.Combine(directory, "image.png"), 7, new string('a', 64),
            "https://example.invalid/image.png", "image.png", true);
        GalleryEntry entry = new(new(ThreadId, directory, asset.AssetId), manifest, asset, null, null, false, true);
        return (manifest, asset, entry);
    }

    // ---- PF-01: the host ----

    [Fact]
    public async Task AnUnreadableFavoritesFileIsUnavailableNeverAnExceptionOrAnEmptyList_OneNotice_AndTheFileStaysAsItIs()
    {
        using TempDirectory temp = new();
        byte[] damaged = DamageFavorites(temp.Path);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, host.State);
        (_, _, GalleryEntry entry) = await MissingOriginalAsync(temp.Path);

        Assert.True(host.FavoritesUnreadable);
        Assert.False(host.IsFavorite(entry)); // what every star reads: no exception, no mark
        Assert.Throws<InvalidDataException>(() => host.GetFavorites()); // the destructive consumers still refuse

        // One sentence for the whole session, however many stars or tiles ask.
        string? notice = host.TakeFavoritesUnreadableNotice();
        Assert.NotNull(notice);
        Assert.StartsWith("The favorites file could not be read", notice);
        for (int ask = 0; ask < 50; ask++) Assert.Null(host.TakeFavoritesUnreadableNotice());

        // A write is refused and never "repairs" the file.
        await Assert.ThrowsAnyAsync<Exception>(() => host.SetFavoriteAsync(entry, true));
        await host.DisposeAsync();
        Assert.Equal(damaged, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"), "A backup was written next to the unreadable file.");
    }

    [Fact]
    public async Task AReadableOrMissingFavoritesFileIsNeverReportedAsUnreadable()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        Assert.False(host.FavoritesUnreadable);
        Assert.Null(host.TakeFavoritesUnreadableNotice());
        Assert.Empty(host.GetFavorites());
    }

    // ---- PF-01: the stars ----

    [Fact]
    public async Task EveryStarOfTheArchiveIsDisabledWithTheReasonForAnUnreadableFavoritesFile()
    {
        using TempDirectory temp = new();
        DamageFavorites(temp.Path);
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        (MediaAssetSnapshot manifest, MediaAssetReference asset, _) = await MissingOriginalAsync(temp.Path);

        WithPage(page =>
        {
            page.IsAvailable = true;
            page.IsArchiveFavorite = host.IsFavorite;
            page.ToggleArchiveFavoriteAsync = _ => throw new InvalidOperationException("No write expected");
            bool unreadable = true;
            page.ArchiveFavoritesUnreadable = () => unreadable;
            ArchiveAssetRow tile = new(manifest, asset);
            Set(page, "_thread", new ArchivedThreadView(Row().Summary, Array.Empty<CapturedPost>(), decimalPostOrder: false));
            Set(page, "_assets", new[] { tile });

            Invoke(page, "ConfigureArchiveMediaFavorites"); // realizing every star asks the row; none of it may throw
            Assert.False(tile.FavoriteAvailable!());
            Assert.Equal(FavoriteStarHold.Unreadable, tile.FavoriteHold!());
            Assert.False(ArchiveMediaBadges.IsFavorite(tile.FavoriteAvailable, tile.IsFavorite));

            ArchiveFavoriteButton star = new();
            star.Configure(tile);
            Assert.False(star.IsEnabled);
            Assert.False(star.IsChecked);
            Assert.Equal(Texts.T("archive.favoritesUnreadable"), ToolTip.GetTip(star) as string);
            Assert.Equal(Texts.T("archive.favoritesUnreadable"), AutomationProperties.GetName(star));
            Assert.Contains(Texts.T("nav.diagnostics"), Texts.T("archive.favoritesUnreadable")); // says where to look

            // The same row with a readable file is an ordinary star: only the file decides.
            unreadable = false;
            Invoke(page, "ConfigureArchiveMediaFavorites");
            star.Refresh();
            Assert.True(tile.FavoriteAvailable!());
            Assert.True(star.IsEnabled);
        });
    }

    [Fact]
    public void TheStarsTextNamesTheReasonInEveryLanguageAndNeverReusesNotSaved()
    {
        foreach (FavoriteStarHold hold in new[] { FavoriteStarHold.Loading, FavoriteStarHold.Unreadable })
            foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
            {
                Texts.SetLanguage(language);
                string text = ArchiveFavoriteButton.HoldText(hold);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + hold);
                Assert.NotEqual(Texts.T("archive.notSaved"), text);
                Assert.DoesNotContain("archive.", text); // a missing key would show its name
                Assert.DoesNotContain("favorites.unreadable", text);
            }
        Texts.SetLanguage("en");
        Assert.Equal(Texts.T("archive.notSaved"), ArchiveFavoriteButton.HoldText(FavoriteStarHold.None));
        Assert.EndsWith("details", Texts.T("favorites.unreadable.notice", "details"));
    }

    // ---- PF-01 as a blocker: nothing else waits for the favorites file ----

    [Fact]
    public async Task AnUnreadableFavoritesFileNeverBlocksSavedMediaTheLargeReaderOrTheGalleryReads()
    {
        using TempDirectory temp = new();
        byte[] damaged = DamageFavorites(temp.Path);
        string media = Path.Combine(temp.Path, "saved-media");
        Directory.CreateDirectory(media);
        byte[] bytes = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(media, "first.png"), bytes);
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                ["first.png"] = new
                {
                    MediaIdentifier = "first.png", Variant = "original", SourceUrl = "https://example.invalid/first.png", FileName = "first.png",
                    OriginalFileName = "first.png", Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                }
            }
        }));
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
            await catalog.CaptureAsync(new(ThreadId, "https://offline.invalid/thread/42", "Unreadable favorites fixture",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, media, new[]
                {
                    new CapturedPost("42", 0, PostFieldPresence.All, Html: "Saved local post",
                        Attachments: [new CapturedAttachment(0, "first.png", Url: "https://example.invalid/first.png")])
                }));
        }
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        Assert.True(host.FavoritesUnreadable);

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            ArchivePage page = new(host.OpenArchiveReaderAsync, host.SetCaptureEnabledAsync, _ => { });
            ContentControl readerHost = new();
            try
            {
                // The wiring of the shell: the host's own reads, nothing guarded by the test.
                page.AttachThreadFavorites(host);
                page.IsArchiveFavorite = host.IsFavorite;
                page.ArchiveFavoritesUnreadable = () => host.FavoritesUnreadable;
                page.ToggleArchiveFavoriteAsync = _ => throw new InvalidOperationException("No write expected");
                page.IsAvailable = true;

                // (a) The saved media load finishes and lists the file.
                Pump(page.LoadThreadAsync(ThreadId));
                Pump(page.MediaLoadTask);
                Assert.False(page.IsMediaLoading);
                ArchiveAssetRow row = Assert.Single(page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>());
                Assert.Equal("first.png", row.Asset.OriginalFileName);

                // Every star of it (tile, attachment, sidebar header) asks the row: off with the reason, no exception.
                Assert.False(row.FavoriteAvailable!());
                Assert.Equal(FavoriteStarHold.Unreadable, row.FavoriteHold!());
                ArchiveFavoriteButton tileStar = new();
                tileStar.Configure(row);
                Assert.False(tileStar.IsEnabled);
                Assert.Equal(Texts.T("archive.favoritesUnreadable"), ToolTip.GetTip(tileStar) as string);
                ArchivePostRow post = ((IEnumerable<ArchivePostRow>)page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!).Single();
                ArchiveFavoriteButton attachmentStar = new();
                attachmentStar.Configure(post.Attachments[0]);
                Assert.False(attachmentStar.IsEnabled);
                Assert.Equal(Texts.T("archive.favoritesUnreadable"), ToolTip.GetTip(attachmentStar) as string);

                // (b) The large reader opens (its pane, frame and sidebar lay out and read the stars) and closes again.
                page.SetReaderCloseTarget("command.reader.close.toDownloads");
                Control frame = page.DetachReader();
                readerHost.Content = frame;
                page.SetReaderScope(new[] { ThreadId });
                Pump(page.LoadThreadAsync(ThreadId));
                Pump(page.MediaLoadTask);
                Assert.NotNull(readerHost.Content);
                page.IsReadable = true;
                page.RefreshArchiveFavoriteControls();
                readerHost.Content = null;
                page.AttachReader(frame);

                // (c) What the gallery asks of the favorites when it opens a saved file (its toolbar star, the tag panel, the quick tags, the
                // filmstrip's flags): the host's passive read, which answers "no mark" and never throws.
                GalleryEntry entry = new(new GalleryAssetKey(ThreadId, row.Manifest.Directory, row.Asset.AssetId), row.Manifest, row.Asset, null, null, false, true);
                Assert.False(host.IsFavorite(entry));
            }
            finally { page.EndLifetime(); readerHost.Content = null; }
        });
        // None of it rewrote the file.
        Assert.Equal(damaged, File.ReadAllBytes(Path.Combine(temp.Path, "media-favorites.json")));
    }

    // ---- AR-04 ----

    [Fact]
    public async Task APostAttachmentStarSaysItIsCheckingWhileTheMediaScanRunsAndOnlyThenNotSaved()
    {
        ArchivedThreadView view = await SeedThreadAsync();
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
            // The page runs one media scan at a time behind this gate: holding it keeps the scan of the opened thread pending.
            SemaphoreSlim scanGate = (SemaphoreSlim)typeof(ArchivePage).GetField("_mediaGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            scanGate.Wait();
            try
            {
                page.IsReadable = true;
                Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
                Assert.True(page.IsMediaLoading);
                ArchiveAttachmentRow attachment = ((IEnumerable<ArchivePostRow>)page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!).First().Attachments[0];

                // The scan has not answered: no saved file is known yet, but the star must not read as a permanent "no saved version".
                Assert.False(attachment.FavoriteAvailable!());
                Assert.Equal(FavoriteStarHold.Loading, attachment.FavoriteHold!());
                ArchiveFavoriteButton star = new();
                star.Configure(attachment);
                Assert.False(star.IsEnabled);
                Assert.Equal(Texts.T("archive.favoriteLoading"), ToolTip.GetTip(star) as string);
                Assert.NotEqual(Texts.T("archive.notSaved"), AutomationProperties.GetName(star));
                Assert.Equal(0.5, Assert.IsType<Border>(star.Content).Opacity); // dimmed, as the thread star is while it loads

                // The scan finished without a saved file for this attachment: now it is genuinely not saved.
                scanGate.Release();
                Pump(page.MediaLoadTask);
                Assert.False(page.IsMediaLoading);
                star.Refresh();
                Assert.Equal(FavoriteStarHold.None, attachment.FavoriteHold!());
                Assert.Equal(Texts.T("archive.notSaved"), ToolTip.GetTip(star) as string);
                Assert.Equal(1.0, Assert.IsType<Border>(star.Content).Opacity);
            }
            finally { page.EndLifetime(); }
        });
    }

    private static async Task<ArchivedThreadView> SeedThreadAsync()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            CapturedPost[] posts = { new("p0", 0, PostFieldPresence.All, Html: "stored text",
                Attachments: [new CapturedAttachment(0, "first.png", Url: "https://example.invalid/first.png")]) };
            await catalog.CaptureAsync(new(ThreadId, "https://example.invalid/not-acquired", "Attachment star loading",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(temp.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(ThreadId))!;
    }

    private static void Pump(Task task)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < Deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "The media scan did not settle within the test deadline.");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- PF-03 ----

    [Fact]
    public async Task ChangingTheScopeWhileALoadRunsCancelsItAndWaitsForItSoTheHostNeverSeesTwoAndNoBusyErrorAppears()
    {
        using CancellationTokenSource lifetime = new();
        LatestWinsLoad loads = new(lifetime.Token);
        // The host accepts one resolution at a time and refuses a second with its "busy" error: the fake does the same.
        int running = 0, maximum = 0;
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstMayUnwind = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Resolve(string scope, CancellationToken token, bool hold)
        {
            if (Interlocked.Increment(ref running) > 1) throw new DesktopSessionBusyException("ResolveFavoriteAssetsAsync");
            maximum = Math.Max(maximum, running);
            try
            {
                if (hold)
                {
                    firstEntered.SetResult();
                    await firstMayUnwind.Task; // a resolution that is slow to notice its cancellation
                    token.ThrowIfCancellationRequested();
                }
                return scope;
            }
            finally { Interlocked.Decrement(ref running); }
        }

        Task<string?> first = loads.RunAsync(token => Resolve("current thread", token, hold: true));
        await firstEntered.Task.WaitAsync(Deadline);
        Task<string?> second = loads.RunAsync(token => Resolve("all favorites", token, hold: false));
        await Task.Delay(50);
        Assert.False(second.IsCompleted, "The newer scope started while the older one was still running.");
        firstMayUnwind.SetResult();

        Assert.Null(await first.WaitAsync(Deadline)); // superseded: no result, no error
        Assert.Equal("all favorites", await second.WaitAsync(Deadline));
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task OnlyTheNewestScopePublishesAResultOrAFailureAndTheDialogClosingStopsEveryone()
    {
        using CancellationTokenSource lifetime = new();
        LatestWinsLoad loads = new(lifetime.Token);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> older = loads.RunAsync<string>(async token => { await gate.Task; throw new IOException("old scope failed"); });
        Task<string?> newest = loads.RunAsync<string>(_ => Task.FromResult("newest"));
        gate.SetResult();
        Assert.Null(await older.WaitAsync(Deadline)); // the older failure is not shown under the newer scope
        Assert.Equal("newest", await newest.WaitAsync(Deadline));

        // The newest one's own failure is reported.
        await Assert.ThrowsAsync<IOException>(() => loads.RunAsync<string>(_ => throw new IOException("current scope failed")).WaitAsync(Deadline));

        // The dialog closes while a load runs: it ends without a result and without an error.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> running = loads.RunAsync<string>(async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return "never"; });
        await entered.Task.WaitAsync(Deadline);
        lifetime.Cancel();
        Assert.Null(await running.WaitAsync(Deadline));
    }

    [Fact]
    public void TheOverviewLoadsItsScopesThroughTheNewestWinsLoadAndLocalizesTheBusyRefusal()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.Favorites.cs");
        Assert.Contains("LatestWinsLoad scopeLoads = new(loading.Token);", source);
        Assert.Contains("await scopeLoads.RunAsync(async token =>", source);
        Assert.Contains("DesktopSessionBusyException ? Texts.T(\"queue.error.busy\")", source);
        // A reload after Remove keeps the list; a scope change drops it at once.
        Assert.Contains("await LoadScopeAsync(scopeChanged: false);", source);
        Assert.DoesNotContain("++generation", source);
        // An unreadable file is refused with its sentence, not with an exception out of the click handler.
        Assert.Contains("catch (System.IO.InvalidDataException) { CaptureActionErrorReporter()(Texts.T(\"archive.favoritesUnreadable\")); return; }", source);
    }

    // ---- PF-02 ----

    [Fact]
    public async Task ResolvingFavoritesRemembersTheVerifiedOriginalsAndStillCatchesAFileThatChanged()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        string[] posts = { "21", "22" };
        await SeedPostsAsync(temp.Path, posts);

        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            readBooruCatalog: host.ReadBooruCatalog);
        foreach (GalleryEntry gallery in await MediaLibraryGallery.ResolveAsync(snapshot.Entries, host.OpenArchiveReaderAsync,
            MediaAssetReader.OpenAsync, default, host.ReadBooruCatalog))
            Assert.True(await host.SetFavoriteAsync(gallery, true));
        Assert.Equal(posts.Length, host.GetFavorites().Count);

        FavoriteResolutionSnapshot first = await host.ResolveFavoriteAssetsAsync();
        Assert.Equal(posts.Length, first.Available.Count);
        Assert.Empty(first.Missing);
        // Windows keeps the full hash (its change times are no safe fence); everywhere else the second look is a metadata check.
        if (!OperatingSystem.IsWindows()) Assert.Equal(posts.Length, host.FavoriteVerifiedFileCount);
        FavoriteResolutionSnapshot again = await host.ResolveFavoriteAssetsAsync();
        Assert.Equal(posts.Length, again.Available.Count);

        // The same length with other bytes: the remembered identity no longer matches, so the file is hashed again and found wrong.
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "danbooru", "21", "21.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 21 });
        FavoriteResolutionSnapshot changed = await host.ResolveFavoriteAssetsAsync();
        Assert.Equal(posts.Length - 1, changed.Available.Count);
        Assert.Single(changed.Missing);
        Assert.Equal(posts.Length, host.GetFavorites().Count); // a changed original stays a favorite
    }

    private static async Task SeedPostsAsync(string profile, params string[] postIds)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        BooruCatalogStore store = new(profile, lease);
        foreach (string id in postIds)
        {
            byte[] bytes = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, (byte)int.Parse(id) };
            string directory = Path.Combine(profile, "danbooru", id);
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, id + ".png"), bytes);
            await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1,
                Entries = new Dictionary<string, object>
                {
                    [id + ".png"] = new
                    {
                        MediaIdentifier = BooruMediaIdentity.MediaIdentifier(id), Variant = "original",
                        SourceUrl = "https://cdn.donmai.us/original/" + id + ".png", FileName = id + ".png", OriginalFileName = id + ".png",
                        Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                    }
                }
            }));
            string assetId = (await MediaAssetReader.OpenAsync(directory)).GetPage().Single().AssetId;
            store.UpsertPost(new BooruPostClaim("danbooru", id, BooruPostClaimPresence.All, "https://danbooru.donmai.us/posts/" + id,
                Rating: BooruPostClaimRating.General,
                Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/" + id + ".png", ".png", bytes.Length, 64, 64)]), Observed);
            store.LinkLocalAsset("danbooru", id, assetId, Path.Combine(directory, id + ".png"));
        }
    }

    // ---- helpers ----

    private static ArchiveThreadRow Row() => new(new ArchiveThreadSummary(ThreadId, "", "", "", ThreadSourceState.NotFound,
        true, DateTimeOffset.UnixEpoch, null, 1, 1));
    private static void Set(ArchivePage page, string name, object value) => typeof(ArchivePage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static object? Invoke(ArchivePage page, string name, params object[] args) =>
        typeof(ArchivePage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
    private static void WithPage(Action<ArchivePage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new();
        try { test(page); }
        finally { page.EndLifetime(); }
    });
}
