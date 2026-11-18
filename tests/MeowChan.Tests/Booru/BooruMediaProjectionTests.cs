using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B7: a downloaded Danbooru original is a local medium of the media library. It is derived from the Booru catalog
/// and its manifest only (never from the provider), keeps its provenance, groups with equal bytes, and survives
/// watcher removal, provider errors and a restart without the provider.
/// </summary>
public sealed class BooruMediaProjectionTests
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
    private static readonly byte[] OtherPng = { 0x89, 0x50, 0x4E, 0x47, 9, 9, 9, 9, 9 };
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    private static BooruPostClaim Claim(string id, bool withOriginal = true, BooruPostClaimPresence fields = BooruPostClaimPresence.All) =>
        new("danbooru", id, fields, $"https://danbooru.donmai.us/posts/{id}",
            CreatedAtUtc: Observed.AddHours(-1), Rating: BooruPostClaimRating.Sensitive, Score: 7, FavoriteCount: 3,
            Tags:
            [
                new BooruPostClaimTag("cat", BooruPostClaimTagCategory.General),
                new BooruPostClaimTag("artist_name", BooruPostClaimTagCategory.Artist),
                new BooruPostClaimTag("some_series", BooruPostClaimTagCategory.Copyright)
            ],
            Media: withOriginal
                ?
                [
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/a.png", ".png", 8, 640, 480),
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, "https://cdn.donmai.us/sample/a.jpg", ".jpg"),
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, "https://cdn.donmai.us/preview/a.jpg", ".jpg")
                ]
                : [new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, "https://cdn.donmai.us/preview/a.jpg", ".jpg")],
            SourceClaim: "https://source.example/post/" + id);

    // Saves an original exactly like the Booru download owner does: one manifest per post directory, the media
    // identifier and variant the queue gives it, and the catalog's own link to the asset.
    private static async Task<string> SaveAsync(string profile, BooruCatalogStore store, string postId, byte[] bytes,
        string? mediaIdentifier = null)
    {
        string directory = Path.Combine(profile, "media", "danbooru", postId);
        string name = postId + ".png";
        await MediaLibraryIndexTests.WriteManifestAsync(directory, BooruMediaIdentity.Thread(postId),
            new MediaLibraryIndexTests.File_(name, mediaIdentifier ?? BooruMediaIdentity.MediaIdentifier(postId), bytes));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        string assetId = manifest.GetPage(0, 500).Single().AssetId;
        store.LinkLocalAsset("danbooru", postId, assetId, Path.Combine(directory, name));
        return assetId;
    }

    private static Task<MediaLibrarySnapshot> BuildAsync(string profile, BooruCatalogStore? store, MediaLibrarySnapshot? previous = null,
        Func<BooruCatalogSnapshot?>? override_ = null) =>
        MediaLibraryIndexBuilder.BuildAsync(token => ArchiveReader.OpenAsync(profile, token), MediaAssetReader.OpenAsync, previous,
            readBooruCatalog: override_ ?? (store == null ? null : () => store.Read()));

    [Fact]
    public async Task DownloadedPostsBecomeLocalMediaWithProviderIdentityAndWaitingPostsDoNot()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        string assetId = await SaveAsync(temp.Path, store, "123", Png);
        store.UpsertPost(Claim("124"), Observed); // seen by a search, nothing downloaded

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path, store);

        Assert.True(snapshot.Complete);
        MediaLibraryEntry entry = Assert.Single(snapshot.Entries);
        Assert.Equal(BooruMediaIdentity.Thread("123"), entry.Thread);
        Assert.Equal(new ResourceId("danbooru", "posts", "123"), entry.Thread);
        Assert.Equal("123", entry.BooruPostId);
        Assert.Equal((MediaLibraryPresence.Present, true, (string?)null, (int?)null), (entry.Presence, entry.Unassigned, entry.PostId, entry.Position));
        Assert.Equal(assetId, entry.AssetId);
        Assert.Equal(MediaLibraryIndexTests.Sha(Png), entry.Sha256);
        Assert.Equal(Png.Length, entry.Length);
        Assert.Equal(SavedMediaKind.Image, entry.Kind);
        Assert.Equal("original", entry.Variant);
        Assert.Null(entry.LocalSlot);
        Assert.Null(entry.AttachmentRevisionKey);
    }

    [Fact]
    public async Task ABookuMediumIsNeverTaggableAsAPrivateTagSubject()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);

        MediaLibraryEntry entry = Assert.Single((await BuildAsync(temp.Path, store)).Entries);

        // Provider tags are claims in the catalog. The provider medium has no private tag target, and the dialog says why.
        Assert.Null(MediaTagSubjects.Of(entry));
        Assert.Equal(MediaTagBlock.ProviderMedium, new MediaTagCandidate(entry, null).Block);
        Assert.Equal(MediaTagBlock.NoPostVersion, new MediaTagCandidate(entry with { BooruPostId = null }, null).Block);
    }

    [Fact]
    public async Task EqualBytesFormOneTileWhileEveryProviderAndArchiveOccurrenceStaysListed()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path); // threads A and B both hold the bytes {1,2,3}
        byte[] shared = { 1, 2, 3 };
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("200"), Observed);
        store.UpsertPost(Claim("201"), Observed);
        store.UpsertPost(Claim("202"), Observed);
        await SaveAsync(temp.Path, store, "200", shared);
        await SaveAsync(temp.Path, store, "201", shared);
        await SaveAsync(temp.Path, store, "202", OtherPng);

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path, store);
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(snapshot.Entries, snapshot.Entries, MediaLibrarySort.ByThread, true);

        MediaLibraryTile group = Assert.Single(tiles, tile => tile.Occurrences.Count > 1);
        Assert.Equal(4, group.Occurrences.Count); // thread A, thread B, post 200, post 201: one asset, four claims
        Assert.Equal(2, group.Occurrences.Count(entry => entry.BooruPostId != null));
        Assert.Contains(group.Occurrences, entry => entry.Thread == MediaLibraryIndexTests.A);
        Assert.Single(tiles, tile => tile.Occurrences is [{ BooruPostId: "202" }]);
        // Distinct bytes stay distinct tiles: the other post and A's loose video are not merged into the group.
        Assert.Equal(tiles.Count, MediaLibraryGrouping.CountTiles(snapshot.Entries, includeMissing: true));
    }

    [Fact]
    public async Task SeveralFindingsOfOneAssetAreAllShownAndASingleDownloadStaysOneEntry()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);
        store.AddFinding("danbooru", "123", BooruFindingKind.Search, null, "cat", Observed);
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "cat", Observed.AddMinutes(1));
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w2", "rating:s", Observed.AddMinutes(2));
        store.AddFinding("danbooru", "123", BooruFindingKind.OneShot, null, null, Observed.AddMinutes(3));
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", Observed));
        store.UpsertWatcher(new("w2", "danbooru", "rating:s", "Active", Observed));

        Assert.Single((await BuildAsync(temp.Path, store)).Entries);
        BooruMediaDetails details = BooruMediaProjection.Find(store.Read(), "123")!;

        Assert.Equal(4, details.Findings.Count);
        Assert.Equal(2, details.WatcherFindings);
        Assert.Equal(new[] { BooruFindingKind.Search, BooruFindingKind.Watcher, BooruFindingKind.Watcher, BooruFindingKind.OneShot },
            details.Findings.Select(finding => finding.Kind));
        Assert.Equal(new string?[] { "cat", "cat", "rating:s", null }, details.Findings.Select(finding => finding.Query));
    }

    [Fact]
    public async Task SourceFilterSelectsExactlyTheDownloadedProviderMedia()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path);
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);
        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path, store);

        IReadOnlyList<MediaLibraryEntry> booru = MediaLibraryFilterEngine.Apply(snapshot.Entries,
            new MediaLibraryFilter(source: "danbooru"), MediaLibraryUserData.Unavailable);
        IReadOnlyList<MediaLibraryEntry> boardOnly = MediaLibraryFilterEngine.Apply(snapshot.Entries,
            new MediaLibraryFilter(source: "danbooru", board: "posts"), MediaLibraryUserData.Unavailable);
        IReadOnlyList<MediaLibraryEntry> chan = MediaLibraryFilterEngine.Apply(snapshot.Entries,
            new MediaLibraryFilter(source: "src"), MediaLibraryUserData.Unavailable);

        Assert.Equal("123", Assert.Single(booru).BooruPostId);
        Assert.Equal(booru, boardOnly);
        Assert.DoesNotContain(chan, entry => entry.BooruPostId != null);
        Assert.NotEmpty(chan);
    }

    [Fact]
    public async Task RemovingTheWatcherKeepsTheLocalMediumItsFindingsAndItsProviderData()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "w1", "cat", Observed);
        store.AddFinding("danbooru", "123", BooruFindingKind.Watcher, "ghost", "cat", Observed.AddMinutes(1));
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", Observed));
        MediaLibrarySnapshot before = await BuildAsync(temp.Path, store);
        BooruMediaDetails live = BooruMediaProjection.Find(store.Read(), "123")!;
        Assert.Equal(BooruMediaWatcherStatus.Observing, live.Findings[0].Watcher);

        Assert.True(store.MarkWatcherRemoved("w1"));
        MediaLibrarySnapshot after = await BuildAsync(temp.Path, store);
        BooruMediaDetails removed = BooruMediaProjection.Find(store.Read(), "123")!;

        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(MediaLibraryPresence.Present, Assert.Single(after.Entries).Presence);
        Assert.All(removed.Findings, finding => Assert.Equal(BooruMediaWatcherStatus.Removed, finding.Watcher)); // removed and unknown alike
        Assert.Equal(3, removed.Tags.Count);
        Assert.Equal(BooruPostClaimRating.Sensitive, removed.Rating);
        Assert.Equal("123", removed.PostId);
    }

    [Fact]
    public async Task TheLocalViewSurvivesARestartWithoutTheProviderAndAnUnreadableCatalogKeepsTheLastIndex()
    {
        using TempDirectory temp = new();
        string cache = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot built;
        using (ProfileLease first = ProfileLease.Acquire(temp.Path))
        {
            BooruCatalogStore store = new(temp.Path, first);
            store.UpsertPost(Claim("123"), Observed);
            await SaveAsync(temp.Path, store, "123", Png);
            built = await BuildAsync(temp.Path, store);
            MediaLibraryIndexCacheSync.Save(cache, built);
        }

        // A new process: no provider, no network, only the files the profile holds.
        string json = await File.ReadAllTextAsync(cache);
        Assert.Contains("\"Version\":4", json);
        Assert.Contains("\"Bp\":\"123\"", json);
        MediaLibrarySnapshot? loaded = MediaLibraryIndexCacheSync.Load(cache);
        Assert.NotNull(loaded);
        Assert.Equal(built.Entries, loaded!.Entries);
        Assert.Equal("123", Assert.Single(loaded.Entries).BooruPostId);

        using ProfileLease second = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore reopened = new(temp.Path, second);
        MediaLibrarySnapshot rebuilt = await BuildAsync(temp.Path, reopened, loaded);
        Assert.True(rebuilt.Complete);
        Assert.Equal(built.Entries, rebuilt.Entries);
        BooruMediaDetails details = BooruMediaProjection.Find(reopened.Read(), "123")!;
        Assert.Equal(3, details.Tags.Count);
        Assert.Equal(BooruMediaVariantState.Stored, details.Original.State);

        // An unreadable or unavailable catalog is not "no booru media": the last known entries stay, flagged incomplete.
        foreach (Func<BooruCatalogSnapshot?> failing in new Func<BooruCatalogSnapshot?>[]
                 { () => null, () => throw new InvalidDataException("damaged"), () => throw new IOException("gone") })
        {
            MediaLibrarySnapshot degraded = await BuildAsync(temp.Path, null, rebuilt, failing);
            Assert.False(degraded.Complete);
            Assert.Contains(degraded.Warnings, warning => warning.Contains("Danbooru"));
            Assert.Equal("123", Assert.Single(degraded.Entries, entry => entry.BooruPostId != null).BooruPostId);
            Assert.Equal(MediaLibraryPresence.Present, Assert.Single(degraded.Entries).Presence);
        }
    }

    [Fact]
    public async Task AnUnchangedProviderManifestIsNotOpenedAgainAndTheEntryFollowsTheCatalog()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);
        string manifestPath = Path.Combine(temp.Path, "media", "danbooru", "123", ".meowchan-manifest.json");
        if (RegularProfileFile.TryGetIdentity(manifestPath) == null) return; // No file identities here: every manifest is read.
        int opens = 0;
        MediaManifestCache Counting() => new((directory, token) => { opens++; return MediaAssetReader.OpenAsync(directory, token); }, identity: true);
        Task<MediaLibrarySnapshot> Pinned(MediaLibrarySnapshot? previous) =>
            MediaLibraryIndexBuilder.BuildAsync(token => ArchiveReader.OpenAsync(temp.Path, token), Counting(), previous, readBooruCatalog: () => store.Read());

        MediaLibrarySnapshot first = await Pinned(null);
        Assert.Equal(1, opens);
        Assert.NotNull(Assert.Single(first.ProviderManifests).Identity);
        Assert.Equal((await BuildAsync(temp.Path, store)).Entries, first.Entries);

        opens = 0;
        MediaLibraryIndexCacheSync.Save(Path.Combine(temp.Path, "c.json"), first);
        MediaLibrarySnapshot second = await Pinned(MediaLibraryIndexCacheSync.Load(Path.Combine(temp.Path, "c.json")));
        Assert.Equal(0, opens);
        Assert.Equal(first.Entries, second.Entries);

        // The catalog sees the post again later: the kept entry takes the catalog's times, exactly like a full read.
        store.UpsertPost(Claim("123"), Observed.AddDays(1));
        opens = 0;
        MediaLibrarySnapshot third = await Pinned(second);
        Assert.Equal(0, opens);
        Assert.Equal((await BuildAsync(temp.Path, store)).Entries, third.Entries);

        // A replaced original changes the manifest file: read again.
        await SaveAsync(temp.Path, store, "123", OtherPng);
        opens = 0;
        MediaLibrarySnapshot fourth = await Pinned(third);
        Assert.Equal(1, opens);
        Assert.Equal((await BuildAsync(temp.Path, store)).Entries, fourth.Entries);
        Assert.Equal(MediaLibraryIndexTests.Sha(OtherPng), Assert.Single(fourth.Entries).Sha256);
    }

    [Fact]
    public async Task DamagedOrForgedBooruCacheEntriesAreRejectedAndRebuilt()
    {
        using TempDirectory temp = new();
        string cache = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            BooruCatalogStore store = new(temp.Path, lease);
            store.UpsertPost(Claim("123"), Observed);
            await SaveAsync(temp.Path, store, "123", Png);
            MediaLibraryIndexCacheSync.Save(cache, await BuildAsync(temp.Path, store));
        }
        string valid = await File.ReadAllTextAsync(cache);
        Assert.NotNull(MediaLibraryIndexCacheSync.Load(cache));

        // An older (v2) cache has no provider media and a newer one (v5) is unknown: both mean rebuild.
        foreach (string damaged in new[]
                 {
                     valid.Replace("\"Version\":4", "\"Version\":2"), valid.Replace("\"Version\":4", "\"Version\":5"),
                     valid.Replace("\"Bp\":\"123\"", "\"Bp\":\"124\""), valid.Replace("\"Bp\":\"123\"", "\"Bp\":\"01\""),
                     valid.Replace("\"Bp\":\"123\"", "\"Bp\":\"x\""), valid.Replace("\"Bp\":\"123\"", "\"Bp\":\"123\",\"Pos\":0")
                 })
        {
            await File.WriteAllTextAsync(cache, damaged);
            Assert.Null(MediaLibraryIndexCacheSync.Load(cache));
        }
    }

    [Fact]
    public async Task AnAssetTheManifestDoesNotConfirmIsAMissingEntryNeverInvented()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        // 1: the catalog link names an asset that is not in the manifest. 2: the manifest record is some other medium.
        store.UpsertPost(Claim("301"), Observed);
        await SaveAsync(temp.Path, store, "301", Png);
        store.LinkLocalAsset("danbooru", "301", "no-such-asset", Path.Combine(temp.Path, "media", "danbooru", "301", "301.png"));
        store.UpsertPost(Claim("302"), Observed);
        await SaveAsync(temp.Path, store, "302", OtherPng, mediaIdentifier: "booru-danbooru-999");
        // 3: a post whose directory is gone entirely.
        store.UpsertPost(Claim("303"), Observed);
        store.LinkLocalAsset("danbooru", "303", "any", Path.Combine(temp.Path, "media", "danbooru", "303", "303.png"));

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path, store);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.Equal(MediaLibraryPresence.Missing, entry.Presence);
            Assert.Null(entry.Sha256);
            Assert.NotNull(entry.BooruPostId);
        });
        // A missing file is still the post: its provider data and the honest variant state stay readable.
        BooruMediaDetails details = BooruMediaProjection.Find(store.Read(), "301", localFilePresent: false)!;
        Assert.Equal(BooruMediaVariantState.LocalFileMissing, details.Original.State);
    }

    [Fact]
    public async Task GalleryResolutionReturnsTheVerifiedOriginalAndRefusesAnythingTheCatalogDoesNotConfirm()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        store.UpsertPost(Claim("123"), Observed);
        string assetId = await SaveAsync(temp.Path, store, "123", Png);
        MediaLibraryEntry entry = Assert.Single((await BuildAsync(temp.Path, store)).Entries);

        IReadOnlyList<GalleryEntry> resolved = await MediaLibraryGallery.ResolveAsync(new[] { entry },
            token => ArchiveReader.OpenAsync(temp.Path, token), MediaAssetReader.OpenAsync, default, () => store.Read());

        GalleryEntry gallery = Assert.Single(resolved);
        Assert.Equal(assetId, gallery.Asset.AssetId);
        Assert.True(gallery.Unassigned);
        Assert.Null(gallery.Post);
        Assert.Equal(BooruMediaIdentity.Thread("123"), gallery.Key.Thread);
        Assert.Equal("https://danbooru.donmai.us/posts/123", gallery.ThreadUrl);
        Assert.False(gallery.LocalTagEligible);

        // No catalog, a catalog that no longer links the asset, or changed bytes: nothing is guessed.
        Assert.Empty(await MediaLibraryGallery.ResolveAsync(new[] { entry }, token => ArchiveReader.OpenAsync(temp.Path, token),
            MediaAssetReader.OpenAsync, default, () => null));
        Assert.Empty(await MediaLibraryGallery.ResolveAsync(new[] { entry }, token => ArchiveReader.OpenAsync(temp.Path, token),
            MediaAssetReader.OpenAsync, default, () => throw new InvalidDataException("damaged")));
        Assert.Empty(await MediaLibraryGallery.ResolveAsync(new[] { entry with { Sha256 = MediaLibraryIndexTests.Sha(OtherPng) } },
            token => ArchiveReader.OpenAsync(temp.Path, token), MediaAssetReader.OpenAsync, default, () => store.Read()));
        Assert.Empty(await MediaLibraryGallery.ResolveAsync(new[] { entry with { BooruPostId = "124", Thread = BooruMediaIdentity.Thread("124") } },
            token => ArchiveReader.OpenAsync(temp.Path, token), MediaAssetReader.OpenAsync, default, () => store.Read()));
    }

    [Fact]
    public async Task AProfileWithoutAnArchiveCatalogStillShowsAndResolvesItsDownloadedPosts()
    {
        using TempDirectory temp = new(); // no archive.db: a Danbooru-only profile
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore store = new(temp.Path, lease);
        // Nothing to show at all keeps the old answer: the archive is missing.
        await Assert.ThrowsAsync<FileNotFoundException>(() => BuildAsync(temp.Path, store));
        await Assert.ThrowsAsync<FileNotFoundException>(() => BuildAsync(temp.Path, null));
        store.UpsertPost(Claim("123"), Observed);
        await SaveAsync(temp.Path, store, "123", Png);

        MediaLibrarySnapshot snapshot = await BuildAsync(temp.Path, store);

        MediaLibraryEntry entry = Assert.Single(snapshot.Entries);
        Assert.Equal("123", entry.BooruPostId);
        Assert.Empty(snapshot.Threads);
        IReadOnlyList<GalleryEntry> resolved = await MediaLibraryGallery.ResolveAsync(new[] { entry },
            token => ArchiveReader.OpenAsync(temp.Path, token), MediaAssetReader.OpenAsync, default, () => store.Read());
        Assert.Single(resolved);
    }

    [Fact]
    public void OnlyTheCanonicalHttpsPostPageOfThisPostIsEverOpened()
    {
        Uri canonical = new("https://danbooru.donmai.us/posts/123");
        Assert.Equal(canonical, BooruMediaIdentity.PostUrl("123", "https://danbooru.donmai.us/posts/123"));
        Assert.Equal(canonical, BooruMediaIdentity.PostUrl("123", null));
        foreach (string hostile in new[]
                 {
                     "http://danbooru.donmai.us/posts/123", "https://evil.example/posts/123", "javascript:alert(1)",
                     "https://danbooru.donmai.us/posts/124", "file:///etc/passwd", "https://danbooru.donmai.us.evil.example/posts/123",
                     "https://user:pw@danbooru.donmai.us/posts/123", "not a url"
                 })
            Assert.Equal(canonical, BooruMediaIdentity.PostUrl("123", hostile));
        Assert.True(BooruMediaIdentity.IsBooruThread(new ResourceId("danbooru", "posts", "123")));
        Assert.False(BooruMediaIdentity.IsBooruThread(new ResourceId("danbooru", "posts", "01")));
        Assert.False(BooruMediaIdentity.IsBooruThread(new ResourceId("danbooru", "other", "123")));
        Assert.False(BooruMediaIdentity.IsBooruThread(new ResourceId("src", "posts", "123")));
        Assert.False(BooruMediaIdentity.IsBooruThread(null));
    }

    [Fact]
    public void DetailsKeepProviderDataAndVariantStatesApartFromWhatIsSaved()
    {
        BooruPostRecord record = new("danbooru", "123", Claim("123"), Observed, Observed.AddDays(1), LocalAssetId: "asset", LocalPath: "/x/123.png");

        BooruMediaDetails stored = BooruMediaProjection.Describe(record, [], localFilePresent: true);
        Assert.Equal(new[] { BooruPostClaimMediaRole.Original, BooruPostClaimMediaRole.Sample, BooruPostClaimMediaRole.Preview },
            stored.Variants.Select(variant => variant.Role));
        Assert.Equal(new[] { BooruMediaVariantState.Stored, BooruMediaVariantState.ProviderClaimOnly, BooruMediaVariantState.ProviderClaimOnly },
            stored.Variants.Select(variant => variant.State));
        Assert.Equal((640, 480), (stored.Original.Width!.Value, stored.Original.Height!.Value));
        // Source tags keep their provider category and spelling; the order is by category, then name.
        Assert.Equal(new[] { "artist_name", "some_series", "cat" }, stored.Tags.Select(tag => tag.Name));
        Assert.Equal(new[] { BooruPostClaimTagCategory.Artist, BooruPostClaimTagCategory.Copyright, BooruPostClaimTagCategory.General },
            stored.Tags.Select(tag => tag.Category));
        Assert.Equal(("https://source.example/post/123", 7, 3), (stored.SourceClaim, stored.Score, stored.FavoriteCount));

        Assert.Equal(BooruMediaVariantState.LocalFileMissing,
            BooruMediaProjection.Describe(record, [], localFilePresent: false).Original.State);
        BooruPostRecord waiting = record with { LocalAssetId = null, LocalPath = null };
        Assert.Equal(BooruMediaVariantState.NotDownloaded, BooruMediaProjection.Describe(waiting, [], true).Original.State);
        BooruPostRecord restricted = waiting with { Post = Claim("123", withOriginal: false) };
        BooruMediaDetails none = BooruMediaProjection.Describe(restricted, [], true);
        Assert.Equal(BooruMediaVariantState.NotOffered, none.Original.State);
        Assert.Equal(BooruMediaVariantState.ProviderClaimOnly, Assert.Single(none.Variants, variant => variant.Role == BooruPostClaimMediaRole.Preview).State);

        // A field the provider never reported is absent, not zero or "unrated".
        BooruPostRecord sparse = record with { Post = Claim("123", fields: BooruPostClaimPresence.Tags) with { Rating = BooruPostClaimRating.Unknown, Score = null, FavoriteCount = null } };
        BooruMediaDetails absent = BooruMediaProjection.Describe(sparse, [], true);
        Assert.False(absent.RatingKnown);
        Assert.Null(absent.Score);
        Assert.Null(absent.FavoriteCount);
        Assert.Null(absent.CreatedAtUtc);
    }
}
