using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;

namespace MeowChan.Tests;

/// <summary>
/// The write limits of the private lists are settings carried by the profile lease: the defaults are the former fixed values,
/// a refusal names the limit, and lowering a limit below what is saved keeps everything readable and only stops growth.
/// </summary>
public sealed class ProfileStoreLimitsTests
{
    private static ResourceId Thread(int index) => new("s", "b", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static FavoriteAssetKey Key(int index) => new(new("s", "b", "1"), "m" + index, "original", new string('a', 64));

    [Fact]
    public void DefaultsAreTheFormerFixedValues()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ProfileStoreLimits limits = lease.StoreLimits;
        Assert.Equal(50_000, limits.FavoriteEntries);
        Assert.Equal(8 * 1024 * 1024, limits.FavoriteFileBytes);
        Assert.Equal(10_000, limits.Bookmarks);
        Assert.Equal(500, limits.SavedSearches);
        Assert.Equal(500, limits.ReaderGroups);
        Assert.Equal(200, limits.PostFilterRules);
        Assert.Equal(10_000, limits.HiddenPosts);
    }

    [Fact]
    public void ValuesOutsideTheSupportedRangeAreRejected()
    {
        ProfileStoreLimits limits = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.FavoriteEntries = ProfileStoreLimits.MinimumFavoriteEntries - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.FavoriteEntries = ProfileStoreLimits.FavoriteEntryCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.FavoriteFileBytes = ProfileStoreLimits.MinimumFavoriteFileBytes - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.FavoriteFileBytes = ProfileStoreLimits.FavoriteFileCeilingBytes + 1L);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.Bookmarks = ProfileStoreLimits.MinimumBookmarks - 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.SavedSearches = ProfileStoreLimits.SavedSearchCeiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.ReaderGroups = 0);
        limits.FavoriteEntries = ProfileStoreLimits.FavoriteEntryCeiling;
        Assert.Equal(ProfileStoreLimits.FavoriteEntryCeiling, limits.FavoriteEntries);
    }

    [Fact]
    public void ThreadFavoritesAtTheLimitRefuseNewEntriesNamingTheLimitAndStillAllowRemoval()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        // 12,000 saved entries, then the limit is lowered to its minimum of 10,000: everything stays readable.
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 0L, Threads = Enumerable.Range(0, 12_000).Select(Thread).ToArray() }));
        lease.StoreLimits.FavoriteEntries = ProfileStoreLimits.MinimumFavoriteEntries;
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Equal(12_000, store.ReadSnapshot().Threads.Count);
        byte[] before = File.ReadAllBytes(path);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.Set(Thread(99_999), true));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.FavoriteEntries, kind);
        Assert.Equal(10_000, limit);
        Assert.Equal(before, File.ReadAllBytes(path));

        Assert.True(store.Set(Thread(5), false).Changed);
        Assert.Equal(11_999, store.ReadSnapshot().Threads.Count);
        lease.StoreLimits.FavoriteEntries = 20_000;
        Assert.True(store.Set(Thread(99_999), true).Changed);
    }

    [Fact]
    public void RatingsAtTheLimitRefuseNewEntriesButAllowChangesAndRemovals()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "media-ratings.json");
        MediaRatingEntry[] entries = Enumerable.Range(0, 10_000).Select(index => new MediaRatingEntry(Key(index), 3)).ToArray();
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = entries }));
        lease.StoreLimits.FavoriteEntries = ProfileStoreLimits.MinimumFavoriteEntries;
        MediaRatingStore store = new(temp.Path, lease);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.Set(Key(10_000), 4));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out _));
        Assert.Equal(PrivateStoreLimitKind.FavoriteEntries, kind);
        Assert.True(store.Set(Key(1), 5).Changed);
        Assert.True(store.Set(Key(2), null).Changed);
        Assert.Equal(9_999, store.List().Count);
        Assert.True(store.Set(Key(10_000), 4).Changed);
    }

    [Fact]
    public void FavoriteFileAboveTheLoweredSizeLimitStaysReadableAndOnlyGrowthIsRefused()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        // One long identity makes the file 9 MB, above the 8 MiB limit (which is also the minimum).
        ResourceId big = new(new string('a', 9_000_000), "b", "1");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Revision = 0L, Threads = new[] { big } }));
        ThreadFavoriteStore store = new(temp.Path, lease);
        Assert.Single(store.ReadSnapshot().Threads);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.Set(Thread(1), true));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.FavoriteFile, kind);
        Assert.Equal(8L * 1024 * 1024, limit);
        Assert.True(store.Set(big, false).Changed);

        lease.StoreLimits.FavoriteFileBytes = 16L * 1024 * 1024;
        Assert.True(store.Set(big, true).Changed);
        Assert.True(store.Set(Thread(1), true).Changed);
    }

    [Fact]
    public void PostBookmarksAtTheLimitRefuseNewOnesButKeepNotesAndRemovalsWorking()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string path = Path.Combine(temp.Path, "post-bookmarks.json");
        var bookmarks = Enumerable.Range(0, 1_200).Select(index => new { source = "s", board = "b", thread = "1", post = index.ToString(), note = "" }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, revision = 3, bookmarks }));
        lease.StoreLimits.Bookmarks = ProfileStoreLimits.MinimumBookmarks;
        PostBookmarkStore store = new(temp.Path, lease);
        Assert.Equal(1_200, store.Read().Bookmarks.Count);

        PostBookmarkKey fresh = new(new ResourceId("s", "b", "1"), "new");
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.Upsert(fresh, "", 3, expectedExists: false));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.Bookmarks, kind);
        Assert.Equal(1_000, limit);

        PostBookmarkKey existing = new(new ResourceId("s", "b", "1"), "7");
        PostBookmarkSnapshot noted = store.Upsert(existing, "kept", 3, expectedExists: true);
        Assert.Equal(1_200, noted.Bookmarks.Count);
        PostBookmarkSnapshot removed = store.Remove(existing, noted.Revision);
        Assert.Equal(1_199, removed.Bookmarks.Count);
        lease.StoreLimits.Bookmarks = 5_000;
        Assert.Equal(1_200, store.Upsert(fresh, "", removed.Revision, expectedExists: false).Bookmarks.Count);
    }

    [Fact]
    public void SavedSearchesAtTheLimitRefuseNewOnesButAllowRenamesAndDeletes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        SavedArchiveSearchStore store = new(temp.Path, lease);
        store.Load();
        List<Guid> ids = new();
        long revision = 0;
        for (int index = 0; index < 60; index++)
        {
            Guid id = Guid.NewGuid();
            ids.Add(id);
            revision = store.Create(id, "search " + index, new ArchiveSearchSpec("text"), revision).Revision;
        }
        lease.StoreLimits.SavedSearches = ProfileStoreLimits.MinimumSavedSearches;

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => store.Create(Guid.NewGuid(), "one more", new ArchiveSearchSpec("text"), revision));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.SavedSearches, kind);
        Assert.Equal(50, limit);
        revision = store.Rename(ids[0], "renamed", revision).Revision;
        revision = store.Delete(ids[1], revision).Revision;
        Assert.Equal(59, store.Load().Searches.Count);
        lease.StoreLimits.SavedSearches = 100;
        Assert.Equal(60, store.Create(Guid.NewGuid(), "one more", new ArchiveSearchSpec("text"), revision).Searches.Count);
    }

    [Fact]
    public async Task ReaderGroupsAtTheLimitRefuseNewGroupsButAllowRenamesAndDeletes()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ReaderGroupStore store = new(temp.Path, lease);
        ReaderGroup Group(string name) => new(Guid.NewGuid(), name, Array.Empty<ResourceId>());
        ReaderGroup[] groups = Enumerable.Range(0, 60).Select(index => Group("group " + index)).ToArray();
        ReaderGroupsSnapshot saved = await store.SaveAsync(new ReaderGroupsSnapshot(1, 0, groups), 0);
        lease.StoreLimits.ReaderGroups = ProfileStoreLimits.MinimumReaderGroups;

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(saved with { Groups = [.. saved.Groups, Group("one more")] }, saved.Revision));
        Assert.True(PrivateStoreLimit.TryDescribe(refused, out PrivateStoreLimitKind kind, out long limit));
        Assert.Equal(PrivateStoreLimitKind.ReaderGroups, kind);
        Assert.Equal(50, limit);
        ReaderGroupsSnapshot renamed = await store.SaveAsync(saved with { Groups = [saved.Groups[0] with { Name = "renamed" }, .. saved.Groups.Skip(1)] }, saved.Revision);
        ReaderGroupsSnapshot fewer = await store.SaveAsync(renamed with { Groups = renamed.Groups.Skip(1).ToArray() }, renamed.Revision);
        Assert.Equal(59, fewer.Groups.Count);
        Assert.Equal(59, (await store.LoadAsync()).Snapshot!.Groups.Count);
        lease.StoreLimits.ReaderGroups = 100;
        ReaderGroupsSnapshot more = await store.SaveAsync(fewer with { Groups = [.. fewer.Groups, Group("one more")] }, fewer.Revision);
        Assert.Equal(60, more.Groups.Count);
    }
}
