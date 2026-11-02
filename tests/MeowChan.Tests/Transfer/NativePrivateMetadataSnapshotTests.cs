using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateMetadataSnapshotTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static readonly ResourceId Other = new("synthetic", "b", "200");
    private const string Secret = "private-note-雪-<script>must-not-execute</script>";
    private static FavoriteEntry Favorite(ResourceId id, string media = "1", string variant = "original") =>
        new(new(id, media, variant, new string('a', 64)), 123, "private-original-name.jpg");

    private static void Seed(ProfileLease lease)
    {
        string path = lease.ProfileDirectory;
        ThreadFavoriteStore thread = new(path, lease);
        thread.Set(Thread, true);
        thread.Set(Other, true);
        FavoriteStore media = new(path, lease);
        media.SetFavorite(Favorite(Thread), true);
        media.SetFavorite(Favorite(Other), true);
        PostBookmarkStore notes = new(path, lease);
        notes.Upsert(new(Thread, "101"), Secret, 0, false);
        notes.Upsert(new(Other, "201"), "out-of-scope-private-note", 1, false);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EveryCategoryCombinationIsExplicitAndProjectedToExactScope(bool thread, bool media, bool notes)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        var selection = new NativePrivateMetadataSelection(thread, media, notes);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        Assert.Equal(selection, snapshot.Selection);
        Assert.Equal(Thread, Assert.Single(snapshot.Threads));
        Assert.Equal(thread ? 1 : 0, snapshot.ThreadFavorites.Length);
        Assert.Equal(media ? 1 : 0, snapshot.MediaFavorites.Length);
        Assert.Equal(notes ? 1 : 0, snapshot.PostBookmarksAndNotes.Length);
        if (thread) Assert.Equal(Thread, snapshot.ThreadFavorites[0]);
        if (media) Assert.Equal(Favorite(Thread), snapshot.MediaFavorites[0]);
        if (notes) Assert.Equal(new PostBookmark(new(Thread, "101"), Secret), snapshot.PostBookmarksAndNotes[0]);
        Assert.True(snapshot.IsCurrent(lease));
        Assert.DoesNotContain(Secret, snapshot.ToString());
        Assert.DoesNotContain("private-original-name", snapshot.ToString());
        Assert.DoesNotContain(directory.Path, snapshot.ToString());
    }

    [Fact]
    public void DefaultAndSelectedEmptyAreDistinctAndCreateNoFiles()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string[] before = Directory.GetFileSystemEntries(directory.Path).Order().ToArray();
        NativePrivateMetadataSnapshot unselected = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        NativePrivateMetadataSnapshot selected = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
        Assert.Equal(default, unselected.Selection);
        Assert.Equal(new(true, true, true), selected.Selection);
        Assert.Empty(selected.ThreadFavorites); Assert.Empty(selected.MediaFavorites); Assert.Empty(selected.PostBookmarksAndNotes);
        Assert.True(unselected.IsCurrent(lease)); Assert.True(selected.IsCurrent(lease));
        Assert.Equal(before, Directory.GetFileSystemEntries(directory.Path).Order().ToArray());
    }

    [Theory]
    [InlineData("thread-favorites.json")]
    [InlineData("media-favorites.json")]
    [InlineData("post-bookmarks.json")]
    public void PublicOnlyDoesNotReadEvenCorruptPrivateStoresOnCaptureOrRecheck(string name)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string path = Path.Combine(directory.Path, name);
        File.WriteAllText(path, "invalid-" + Secret);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        File.WriteAllBytes(path, [0xff, 0xfe]);
        Assert.True(snapshot.IsCurrent(lease));
        Assert.Empty(snapshot.ThreadFavorites); Assert.Empty(snapshot.MediaFavorites); Assert.Empty(snapshot.PostBookmarksAndNotes);
        Assert.Equal(new byte[] { 0xff, 0xfe }, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EachCategoryReadsOnlyItsOwnStore(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string[] names = ["thread-favorites.json", "media-favorites.json", "post-bookmarks.json"];
        for (int i = 0; i < names.Length; i++)
            if (i != category) File.WriteAllText(Path.Combine(directory.Path, names[i]), "broken");
        var selected = new NativePrivateMetadataSelection(category == 0, category == 1, category == 2);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selected);
        Assert.True(snapshot.IsCurrent(lease));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SelectedCorruptStoreFailsClosedWithoutRepairOrPlaintextErrors(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        string[] names = ["thread-favorites.json", "media-favorites.json", "post-bookmarks.json"];
        string path = Path.Combine(directory.Path, names[category]);
        var selected = new NativePrivateMetadataSelection(category == 0, category == 1, category == 2);
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selected);
        byte[] corrupt = System.Text.Encoding.UTF8.GetBytes("{\"secret\":\"" + Secret + "\"}");
        File.WriteAllBytes(path, corrupt);
        var first = Assert.Throws<InvalidDataException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread], selected));
        var second = Assert.Throws<InvalidDataException>(() => empty.IsCurrent(lease));
        Assert.DoesNotContain(Secret, first.Message); Assert.DoesNotContain(Secret, second.Message);
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void ScopeIsDefensiveAndReturnedArraysCannotBeChanged()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        List<ResourceId> input = [Thread];
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, input, new(true, true, true));
        input[0] = Other;
        input.Clear();
        Assert.Equal(Thread, Assert.Single(snapshot.Threads));
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)snapshot.Threads).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)snapshot.ThreadFavorites)[0] = Other);
        Assert.Throws<NotSupportedException>(() => ((IList<FavoriteEntry>)snapshot.MediaFavorites).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PostBookmark>)snapshot.PostBookmarksAndNotes).Clear());
        // A record 'with' creates another value; it cannot mutate the retained key or note.
        _ = snapshot.MediaFavorites[0] with { Length = 999 };
        _ = snapshot.PostBookmarksAndNotes[0] with { Note = "changed" };
        Assert.Equal(123, snapshot.MediaFavorites[0].Length);
        Assert.Equal(Secret, snapshot.PostBookmarksAndNotes[0].Note);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SelectedStoreChangeEvenOutsideScopeInvalidatesButDoesNotMutateSnapshot(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        var selected = new NativePrivateMetadataSelection(category == 0, category == 1, category == 2);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selected);
        switch (category)
        {
            case 0: new ThreadFavoriteStore(directory.Path, lease).Set(Other, false); break;
            case 1: new FavoriteStore(directory.Path, lease).SetFavorite(Favorite(Other), false); break;
            case 2: new PostBookmarkStore(directory.Path, lease).Remove(new(Other, "201"), 2); break;
        }
        Assert.False(snapshot.IsCurrent(lease));
        Assert.Equal(category == 0 ? 1 : 0, snapshot.ThreadFavorites.Length);
        Assert.Equal(category == 1 ? 1 : 0, snapshot.MediaFavorites.Length);
        Assert.Equal(category == 2 ? 1 : 0, snapshot.PostBookmarksAndNotes.Length);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnselectedStoreMutationsDoNotInvalidatePublicSnapshot(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        switch (category)
        {
            case 0: new ThreadFavoriteStore(directory.Path, lease).Set(Thread, true); break;
            case 1: new FavoriteStore(directory.Path, lease).SetFavorite(Favorite(Thread), true); break;
            case 2: new PostBookmarkStore(directory.Path, lease).Upsert(new(Thread, "101"), Secret, 0, false); break;
        }
        Assert.True(snapshot.IsCurrent(lease));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CooperativeRemoveAndReAddCannotRestoreAnOldFence(int category)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        var selected = new NativePrivateMetadataSelection(category == 0, category == 1, category == 2);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selected);
        switch (category)
        {
            case 0:
                ThreadFavoriteStore threads = new(directory.Path, lease);
                threads.Set(Thread, false); threads.Set(Thread, true); break;
            case 1:
                FavoriteStore media = new(directory.Path, lease);
                media.SetFavorite(Favorite(Thread), false); media.SetFavorite(Favorite(Thread), true); break;
            case 2:
                PostBookmarkStore bookmarks = new(directory.Path, lease);
                bookmarks.Remove(new(Thread, "101"), 2); bookmarks.Upsert(new(Thread, "101"), Secret, 3, false); break;
        }
        Assert.False(snapshot.IsCurrent(lease));
    }

    [Fact]
    public void NoOpFavoriteWritesAndFreshStoreObjectsKeepFenceCurrent()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
        Assert.False(new ThreadFavoriteStore(directory.Path, lease).Set(Thread, true).Changed);
        Assert.False(new FavoriteStore(directory.Path, lease).SetFavorite(Favorite(Thread), true));
        Assert.True(snapshot.IsCurrent(lease));
    }

    [Fact]
    public void OwnerBindingRejectsAnotherProfileAndSamePathAfterRestart()
    {
        using TempDirectory directory = new();
        using TempDirectory other = new();
        NativePrivateMetadataSnapshot snapshot;
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            Seed(lease);
            snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
            using ProfileLease foreign = ProfileLease.Acquire(other.Path);
            Assert.False(snapshot.IsCurrent(foreign));
        }
        using ProfileLease restarted = ProfileLease.Acquire(directory.Path);
        Assert.False(snapshot.IsCurrent(restarted));
        Assert.True(NativePrivateMetadataSnapshot.Capture(restarted, [Thread], new(true, true, true)).IsCurrent(restarted));
        Assert.Equal(Secret, Assert.Single(snapshot.PostBookmarksAndNotes).Note);
    }

    [Fact]
    public void DisposedOwnerFailsEvenWhenNothingWasSelected()
    {
        using TempDirectory directory = new();
        ProfileLease lease = ProfileLease.Acquire(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.IsCurrent(lease));
        Assert.Throws<ObjectDisposedException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread]));
    }

    [Fact]
    public void SnapshotReadsDoNotRemoveBookmarkPendingFilesOrChangeAnyStoreBytes()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Seed(lease);
        string pending = Path.Combine(directory.Path, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(pending, "incomplete-owned-synthetic-note");
        Dictionary<string, byte[]> before = Directory.GetFiles(directory.Path)
            .Where(path => !Path.GetFileName(path).EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllBytes);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
        Assert.True(snapshot.IsCurrent(lease));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory.Path)
            .Where(path => !Path.GetFileName(path).EndsWith(".lock", StringComparison.Ordinal)).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void MediaIdentityIsNotCollapsedBySharedShaOrThreadNumber()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ResourceId foreignBoard = Thread with { Board = "different-board" };
        ResourceId foreignSource = Thread with { Source = "different-source" };
        FavoriteStore media = new(directory.Path, lease);
        FavoriteEntry first = Favorite(Thread), second = Favorite(Thread, "2"), third = Favorite(Thread, "1", "preview");
        foreach (FavoriteEntry entry in new[] { first, second, third, Favorite(foreignBoard), Favorite(foreignSource) })
            media.SetFavorite(entry, true);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(MediaFavorites: true));
        Assert.Equal(3, snapshot.MediaFavorites.Length);
        Assert.Contains(first, snapshot.MediaFavorites); Assert.Contains(second, snapshot.MediaFavorites); Assert.Contains(third, snapshot.MediaFavorites);
    }

    [Fact]
    public void BookmarksStayUnresolvedObservationsNotFabricatedPosts()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostBookmarkStore store = new(directory.Path, lease);
        store.Upsert(new(Thread, "never-seen-post"), "", 0, false);
        var snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(PostBookmarksAndNotes: true));
        Assert.Equal(new PostBookmark(new(Thread, "never-seen-post"), ""), Assert.Single(snapshot.PostBookmarksAndNotes));
        Assert.False(File.Exists(Path.Combine(directory.Path, "archive.sqlite")));
    }

    [Fact]
    public void ScopeLimitIsBoundedWithoutEnumeratingTheRest()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        ResourceId[] maximum = Enumerable.Range(0, NativePrivateMetadataSnapshot.MaximumThreads)
            .Select(i => Thread with { Thread = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        Assert.Equal(maximum.Length, NativePrivateMetadataSnapshot.Capture(lease, maximum).Threads.Length);
        int visited = 0;
        IEnumerable<ResourceId> TooMany()
        {
            foreach (ResourceId id in maximum.Append(Other with { Thread = "last" })) { visited++; yield return id; }
            throw new InvalidOperationException("must not enumerate past the bounded rejection");
        }
        Assert.Throws<ArgumentException>(() => NativePrivateMetadataSnapshot.Capture(lease, TooMany()));
        Assert.Equal(NativePrivateMetadataSnapshot.MaximumThreads + 1, visited);
        Assert.Throws<ArgumentException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread, Thread]));
        Assert.Empty(NativePrivateMetadataSnapshot.Capture(lease, [], new(true, true, true)).Threads);
    }

    [Theory]
    [InlineData("", "b", "1")]
    [InlineData("s", "\n", "1")]
    [InlineData("s", "b", null)]
    [InlineData("s", "b", "\t")]
    public void InvalidScopeFailsBeforePrivateStoreReads(string source, string board, string? thread)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "post-bookmarks.json"), "broken");
        Assert.ThrowsAny<ArgumentException>(() => NativePrivateMetadataSnapshot.Capture(lease, [new(source, board, thread)], new(true, true, true)));
    }

    [Fact]
    public void InvalidUnicodeScopeAndOversizeIdentityAreRejected()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Assert.Throws<ArgumentException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread with { Board = "\ud800" }]));
        Assert.Throws<ArgumentException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread with { Thread = new string('a', 257) }]));
    }

    [Fact]
    public void PreCancelledCaptureAndRecheckDoNotReadCorruptStores()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        NativePrivateMetadataSnapshot snapshot = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
        File.WriteAllText(Path.Combine(directory.Path, "thread-favorites.json"), "broken");
        using CancellationTokenSource cts = new(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true), cts.Token));
        Assert.Throws<OperationCanceledException>(() => snapshot.IsCurrent(lease, cts.Token));
    }
}
