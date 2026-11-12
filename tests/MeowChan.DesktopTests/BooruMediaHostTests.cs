using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// B7: a downloaded Danbooru original is local media. The media page index, the gallery and the favorites agree on it;
// the provider's tags are claims in the catalog and never private tags; removing a watcher or restarting without any
// provider keeps the local view. Everything here is offline: the host is started with no source at all.
[Collection("Localization")]
public sealed class BooruMediaHostTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };
    private static readonly byte[] Bytes = { 0x89, 0x50, 0x4E, 0x47, 5, 4, 3, 2, 1 };
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
    private static readonly HashSet<string> MayEqualEnglish = new(StringComparer.Ordinal) { "booru.media.source", "booru.media.badge", "booru.media.variants.heading" };

    private readonly string _previous = Texts.Language;
    public BooruMediaHostTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static BooruPostClaim Claim(string id) => new("danbooru", id, BooruPostClaimPresence.All,
        $"https://danbooru.donmai.us/posts/{id}", CreatedAtUtc: Observed.AddHours(-2), Rating: BooruPostClaimRating.General, Score: 11, FavoriteCount: 4,
        Tags: [new BooruPostClaimTag("secret_provider_tag", BooruPostClaimTagCategory.General),
               new BooruPostClaimTag("artist_name", BooruPostClaimTagCategory.Artist)],
        Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/a.png", ".png", Bytes.Length, 640, 480),
                new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, "https://cdn.donmai.us/sample/a.jpg", ".jpg")],
        SourceClaim: "https://source.example/" + id);

    // Seeds the profile exactly as a finished original download leaves it: the file, a manifest next to it, the catalog link.
    private static async Task<string> SeedAsync(string profile)
    {
        string directory = Path.Combine(profile, "danbooru", "123");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "123.png"), Bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                ["123.png"] = new
                {
                    MediaIdentifier = BooruMediaIdentity.MediaIdentifier("123"), Variant = "original",
                    SourceUrl = "https://cdn.donmai.us/original/a.png", FileName = "123.png", OriginalFileName = "123.png",
                    Length = Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(),
                    Md5 = Convert.ToBase64String(MD5.HashData(Bytes))
                }
            }
        }));
        string assetId = (await MediaAssetReader.OpenAsync(directory)).GetPage().Single().AssetId;
        using ProfileLease lease = ProfileLease.Acquire(profile);
        BooruCatalogStore store = new(profile, lease);
        store.UpsertPost(Claim("123"), Observed);
        store.LinkLocalAsset("danbooru", "123", assetId, Path.Combine(directory, "123.png"));
        store.AddFinding("danbooru", "123", BooruFindingKind.Search, null, "cat", Observed);
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "cat", Observed.AddMinutes(1));
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", Observed));
        return assetId;
    }

    private static Task<MediaLibrarySnapshot> BuildAsync(DesktopSessionHost host) =>
        MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync, readBooruCatalog: host.ReadBooruCatalog);

    [Fact]
    public async Task MediaPageGalleryAndFavoritesAgreeOnTheSameDownloadedPostAcrossAnOfflineRestart()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        string assetId = await SeedAsync(temp.Path);
        FavoriteAssetKey? favorite = null;

        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            MediaLibrarySnapshot snapshot = await BuildAsync(host);
            MediaLibraryEntry entry = Assert.Single(snapshot.Entries);
            Assert.True(snapshot.Complete);
            Assert.Equal(("123", assetId, MediaLibraryPresence.Present), (entry.BooruPostId, entry.AssetId, entry.Presence));

            // The gallery shows the very same file as a post-less local file with the provider page as its address.
            GalleryEntry gallery = Assert.Single(await MediaLibraryGallery.ResolveAsync(snapshot.Entries, host.OpenArchiveReaderAsync,
                MediaAssetReader.OpenAsync, default, host.ReadBooruCatalog));
            Assert.Equal(assetId, gallery.Asset.AssetId);
            Assert.Equal("https://danbooru.donmai.us/posts/123", gallery.ThreadUrl);
            favorite = FavoriteAssetKey.From(gallery.Key.Thread, gallery.Asset);
            Assert.Equal(MediaLibraryFilterEngine.AssetKey(entry), favorite);

            // A favorite set in the gallery is the favorite the media page shows, and the favorites list can resolve it.
            Assert.True(await host.SetFavoriteAsync(gallery, true));
            Assert.True(host.IsFavorite(gallery));
            MediaLibraryUserData data = new(null, host.GetFavorites(), null);
            Assert.Same(entry, Assert.Single(MediaLibraryFilterEngine.Apply(snapshot.Entries, new MediaLibraryFilter(favoritesOnly: true), data)));
            FavoriteResolutionSnapshot resolved = await host.ResolveFavoriteAssetsAsync();
            Assert.Equal(assetId, Assert.Single(resolved.Available).AssetId);
            Assert.Empty(resolved.Missing);

            // Provider tags and ratings are claims, never the profile's own tags.
            PrivateTagLoadSnapshot tags = host.GetPrivateTagSnapshot();
            Assert.Equal(PrivateTagLoadState.Ready, tags.State);
            Assert.Empty(tags.Snapshot!.Tags);
            Assert.Null(host.GetMediaRating(favorite));
            Assert.Null(MeowChan.Tags.MediaTagSubjects.Of(entry));
        }

        // The user removes the watcher; later the app starts again with no provider, no network and no plugin.
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
            Assert.True(new BooruCatalogStore(temp.Path, lease).MarkWatcherRemoved("w1"));

        await using (DesktopSessionHost restored = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await restored.StartAsync();
            MediaLibrarySnapshot snapshot = await BuildAsync(restored);
            MediaLibraryEntry entry = Assert.Single(snapshot.Entries);
            Assert.Equal(MediaLibraryPresence.Present, entry.Presence);
            Assert.Contains(favorite!, restored.GetFavorites().Select(item => item.Key));
            Assert.Equal(assetId, Assert.Single((await restored.ResolveFavoriteAssetsAsync()).Available).AssetId);

            BooruMediaDetails details = BooruMediaProjection.Find(restored.ReadBooruCatalog()!, "123")!;
            Assert.Equal(2, details.Findings.Count);
            Assert.Equal(BooruMediaWatcherStatus.Removed, Assert.Single(details.Findings, item => item.Kind == BooruFindingKind.Watcher).Watcher);
            Assert.Equal(new[] { "artist_name", "secret_provider_tag" }, details.Tags.Select(tag => tag.Name));
            Assert.Empty(restored.GetPrivateTagSnapshot().Snapshot!.Tags);
        }
        string privateTags = Path.Combine(temp.Path, "private-tags.json");
        if (File.Exists(privateTags)) Assert.DoesNotContain("secret_provider_tag", await File.ReadAllTextAsync(privateTags));
    }

    [Fact]
    public async Task AnUnreadableCatalogNeverTurnsAnExistingFavoriteIntoMissingMedia()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        string assetId = await SeedAsync(temp.Path);
        await using DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        MediaLibrarySnapshot snapshot = await BuildAsync(host);
        GalleryEntry gallery = Assert.Single(await MediaLibraryGallery.ResolveAsync(snapshot.Entries, host.OpenArchiveReaderAsync,
            MediaAssetReader.OpenAsync, default, host.ReadBooruCatalog));
        await host.SetFavoriteAsync(gallery, true);

        // The catalog is read-only evidence: when it cannot be read the media list keeps its entries (incomplete) and the
        // favorite stays a favorite, reported unavailable only for this resolution, never removed.
        MediaLibrarySnapshot degraded = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            snapshot, readBooruCatalog: () => null);
        Assert.False(degraded.Complete);
        Assert.Equal(snapshot.Entries, degraded.Entries);
        Assert.Single(host.GetFavorites());
        Assert.Equal(assetId, Assert.Single((await host.ResolveFavoriteAssetsAsync()).Available).AssetId);
    }

    [Fact]
    public void TileAndOccurrenceTextsNameTheProviderPostInsteadOfAnEmptyChanThread()
    {
        MediaLibraryEntry provider = new(BooruMediaIdentity.Thread("123"), "Danbooru #123", 0, Observed, Observed, null, null, null, null, null,
            "/x", "asset", new string('a', 64), 1_500_000, "booru-danbooru-123", "original", "123.png", SavedMediaKind.Image,
            MediaLibraryPresence.Present, true, false) { BooruPostId = "123" };
        MediaLibraryEntry chan = provider with { Thread = new("4chan", "g", "9"), ThreadTitle = "Thread", BooruPostId = null, Unassigned = false };
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            MediaLibraryTileRow row = new(new MediaLibraryTile(provider, new[] { provider }), 0);
            Assert.True(row.IsProviderMedium);
            Assert.Contains("123", row.ThreadLabel);
            Assert.Equal(Texts.T("booru.media.tile.post", "123"), row.ThreadLabel);
            // The post-less "no post" detail is a chan notion; a provider medium shows its size only.
            Assert.DoesNotContain(Texts.T("media.tile.unassigned"), row.Detail);
            Assert.Contains("1.5", row.Detail.Replace(',', '.'));
            MediaLibraryTileRow ordinary = new(new MediaLibraryTile(chan, new[] { chan }), 0);
            Assert.False(ordinary.IsProviderMedium);
            Assert.DoesNotContain(Texts.T("booru.media.tile.post", "123"), ordinary.ThreadLabel);
        }
    }

    [Fact]
    public void FindingTextsStateTheWatcherStandingInEveryLanguageWithoutRawCodes()
    {
        BooruMediaFinding watcherRemoved = new(BooruFindingKind.Watcher, "w1", "cat", Observed, BooruMediaWatcherStatus.Removed, "Removed");
        BooruMediaFinding watcherActive = new(BooruFindingKind.Watcher, "w2", "cat", Observed, BooruMediaWatcherStatus.Observing, "Active");
        BooruMediaFinding search = new(BooruFindingKind.Search, null, "rating:g", Observed, BooruMediaWatcherStatus.NotWatcher, null);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            string removed = BooruMediaDetailsDialog.FindingText(watcherRemoved);
            Assert.Contains(Texts.T("booru.media.finding.watcherRemoved"), removed);
            Assert.Contains(Texts.T("booru.finding.Watcher"), removed);
            Assert.Contains("“cat”", removed);
            Assert.DoesNotContain("booru.", removed);
            Assert.Contains(Texts.T("booru.media.finding.watcherState", Texts.T("booru.watcher.state.Active")), BooruMediaDetailsDialog.FindingText(watcherActive));
            string text = BooruMediaDetailsDialog.FindingText(search);
            Assert.Contains("rating:g", text);
            Assert.DoesNotContain("{", text);
        }
    }

    [Fact]
    public void EveryBooruMediaTextExistsInAllFiveLanguagesAndKeepsItsPlaceholders()
    {
        Assert.Equal(BooruMediaCatalog.Entries.Length, BooruMediaCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach ((string key, string english, string german) in BooruMediaCatalog.Entries)
        {
            Assert.StartsWith("booru.media.", key);
            Assert.Equal(german, BuiltInCatalog.German[key]);
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string text = Texts.T(key);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
                Assert.NotEqual(key, text);
                Assert.Equal(Placeholders(english), Placeholders(text));
                if (language != "en" && !MayEqualEnglish.Contains(key)) Assert.True(text != english, language + " is untranslated: " + key);
            }
        }
    }

    [Fact]
    public void EveryBooruMediaKeyTheDesktopCodeUsesIsInTheCatalogAndEveryVariantStateHasAText()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../.."));
        Regex literal = new("\"(booru\\.media\\.[A-Za-z0-9.]+)\"", RegexOptions.CultureInvariant);
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (Match match in literal.Matches(File.ReadAllText(file)))
                if (!match.Groups[1].Value.EndsWith('.')) used.Add(match.Groups[1].Value);
        Assert.NotEmpty(used);
        foreach (string key in used) Assert.True(BuiltInCatalog.English.ContainsKey(key), "no text for " + key);
        foreach (BooruMediaVariantState state in Enum.GetValues<BooruMediaVariantState>())
            Assert.True(BuiltInCatalog.English.ContainsKey("booru.media.variant." + state), state.ToString());
        // The catalog has no orphans: every text is used by the code (a dynamic prefix counts for its states).
        foreach (string key in BooruMediaCatalog.Entries.Select(entry => entry.Key))
            Assert.True(used.Contains(key) || key.StartsWith("booru.media.variant.", StringComparison.Ordinal), "unused text " + key);
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
