using System.Text;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ThreadFavoriteCoreTests
{
    private static ResourceId Thread => new("source", "board", "42");

    [Fact]
    public void ThreadMarksPersistWithoutMediaAndSnapshotsRemainImmutable()
    {
        using TempDirectory temp = new();
        string file = Path.Combine(temp.Path, "thread-favorites.json");
        ThreadFavoriteSnapshot saved;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            ThreadFavoriteStore store = new(temp.Path, lease);
            ThreadFavoriteSnapshot empty = store.ReadSnapshot();
            Assert.Empty(empty.Threads); Assert.Equal(0, empty.Revision); Assert.False(File.Exists(file));
            Assert.False(store.Set(Thread, false).Changed); Assert.False(File.Exists(file));
            ThreadFavoriteCommit commit = store.Set(Thread, true);
            saved = commit.Snapshot;
            Assert.True(commit.Changed); Assert.True(commit.Favorite); Assert.Equal(Thread, commit.Thread);
            Assert.Equal(1, saved.Revision); Assert.NotEqual(empty.Fingerprint, saved.Fingerprint);
            Assert.Empty(empty.Threads);
            Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)saved.Threads).Add(Thread));
            byte[] bytes = File.ReadAllBytes(file);
            Assert.False(store.Set(Thread, true).Changed); Assert.Equal(bytes, File.ReadAllBytes(file));
            Assert.False(File.Exists(Path.Combine(temp.Path, "media-favorites.json")));
        }
        using ProfileLease restartedLease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore restarted = new(temp.Path, restartedLease);
        Assert.Equal(saved.Fingerprint, restarted.ReadSnapshot().Fingerprint);
        Assert.Equal(Thread, Assert.Single(restarted.ReadSnapshot().Threads));
        ThreadFavoriteCommit removed = restarted.Set(Thread, false);
        Assert.True(removed.Changed); Assert.Equal(2, removed.Snapshot.Revision); Assert.Empty(removed.Snapshot.Threads);
        Assert.Equal(Thread, Assert.Single(saved.Threads));
    }

    [Fact]
    public void SourceBoardAndThreadAreExactIndependentIdentities()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        ResourceId[] identities = [Thread, Thread with { Source = "other" }, Thread with { Source = "Source" },
            Thread with { Board = "other" }, Thread with { Thread = "43" }, new("Quelle-猫", "b", "😀")];
        foreach (ResourceId id in identities) store.Set(id, true);
        Assert.Equal(identities, store.ReadSnapshot().Threads);
        string fingerprint = store.ReadSnapshot().Fingerprint;
        Assert.Throws<ArgumentException>(() => store.Set(Thread with { Source = "broken-\uD800" }, true));
        Assert.Equal(fingerprint, store.ReadSnapshot().Fingerprint);
        store.Set(Thread, false);
        Assert.Equal(identities[1..], new ThreadFavoriteStore(temp.Path, lease).ReadSnapshot().Threads);
    }

    [Fact]
    public void InvalidFutureDuplicateAndMalformedUtf8DataNeverBecomeEmptyOrOverwriteEvidence()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        string file = Path.Combine(temp.Path, "thread-favorites.json");
        string[] documents = ["{broken", "{\"Version\":99,\"Revision\":4,\"Threads\":[]}",
            JsonSerializer.Serialize(new { Version = 1, Revision = 1, Threads = new[] { Thread, Thread } }),
            JsonSerializer.Serialize(new { Version = 1, Revision = -1, Threads = new[] { Thread } }),
            JsonSerializer.Serialize(new { Version = 1, Revision = 1, Threads = new[] { Thread with { Thread = (string?)null } } })];
        File.WriteAllText(file + ".bak", "preserved backup");
        foreach (string json in documents)
        {
            File.WriteAllText(file, json);
            Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
            Assert.Throws<InvalidDataException>(() => store.Set(Thread, true));
            Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
            Assert.Equal(json, File.ReadAllText(file)); Assert.Equal("preserved backup", File.ReadAllText(file + ".bak"));
        }
        byte[] invalidUtf8 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Version = 1, Revision = 1, Threads = new[] { Thread } }));
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'s')] = 0xFF;
        File.WriteAllBytes(file, invalidUtf8);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.Equal(invalidUtf8, File.ReadAllBytes(file));
    }

    [Fact]
    public void UnsupportedBackupBlocksChangesAndMissingPrimaryNeverResurrectsRemovedMark()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        string file = Path.Combine(temp.Path, "thread-favorites.json");
        store.Set(Thread, true); byte[] primary = File.ReadAllBytes(file);
        const string future = "{\"Version\":99,\"Threads\":[]}";
        File.WriteAllText(file + ".bak", future);
        Assert.Equal(Thread, Assert.Single(store.ReadSnapshot().Threads));
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.Equal(primary, File.ReadAllBytes(file)); Assert.Equal(future, File.ReadAllText(file + ".bak"));
        File.Delete(file + ".bak"); store.Set(Thread, false);
        Assert.Empty(store.ReadSnapshot().Threads);
        File.Delete(file);
        Assert.Throws<InvalidDataException>(() => store.ReadSnapshot());
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, true));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void CancellationAndWriteFailurePreserveCommittedSnapshotAndAllowRetry()
    {
        using TempDirectory temp = new(); using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        string file = Path.Combine(temp.Path, "thread-favorites.json");
        ThreadFavoriteSnapshot before = store.Set(Thread, true).Snapshot;
        Assert.Throws<OperationCanceledException>(() => store.Set(Thread, false, new CancellationToken(true)));
        Directory.CreateDirectory(file + ".bak");
        Assert.Throws<InvalidDataException>(() => store.Set(Thread, false));
        Assert.Equal(before.Fingerprint, store.ReadSnapshot().Fingerprint);
        Assert.Equal(before.Revision, store.ReadSnapshot().Revision);
        Directory.Delete(file + ".bak");
        Assert.True(store.Set(Thread, false).Changed);
        Assert.Empty(Directory.GetFiles(temp.Path, ".thread-favorites.json.*.tmp"));
    }

    [Fact]
    public void ProfileOwnershipAndDisposedLeaseAreEnforced()
    {
        using TempDirectory temp = new(); using TempDirectory other = new();
        ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Assert.Throws<ArgumentException>(() => new ThreadFavoriteStore(other.Path, lease));
        ThreadFavoriteStore store = new(temp.Path, lease); lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.ReadSnapshot());
        Assert.Throws<ObjectDisposedException>(() => store.Set(Thread, true));
    }
}
