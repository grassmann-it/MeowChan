using System.Text;
using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Tests.Transfer;

// Synthetic profiles only: ordinary-writer compatibility, not private apply admission.
public sealed class PrivateStoreNoopBackupCompatibilityTests
{
    private static readonly Guid Operation = Guid.Parse("d02a4b91-15be-46a8-9605-46c6cd049920");
    private static readonly string PlanHash = new('a', 64);
    private static ResourceId Thread(string id = "100") => new("synthetic", "b", id);
    private static FavoriteEntry Entry(string thread = "100")
        => new(new(Thread(thread), "1", "original", new('b', 64)), 12, "synthetic.jpg");

    private static void AssertEvidence(string path, byte[] primary, byte[] backup)
    {
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":999,\"Revision\":0,\"Threads\":[]}")]
    public void ThreadNoopsAllowInvalidBackupButMutationsRejectItWithoutChangingBytes(string rawBackup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        store.Set(Thread(), true);
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] primary = File.ReadAllBytes(path);
        byte[] backup = Encoding.UTF8.GetBytes(rawBackup);
        File.WriteAllBytes(path + ".bak", backup);
        ThreadFavoriteSnapshot before = store.ReadSnapshot();

        ThreadFavoriteCommit present = store.Set(Thread(), true);
        Assert.False(present.Changed);
        Assert.True(present.Favorite);
        Assert.Equal(before.Revision, present.Snapshot.Revision);
        Assert.Equal(before.Fingerprint, present.Snapshot.Fingerprint);
        Assert.Equal(new[] { Thread() }, present.Snapshot.Threads);
        AssertEvidence(path, primary, backup);

        ThreadFavoriteCommit absent = store.Set(Thread("missing"), false);
        Assert.False(absent.Changed);
        Assert.False(absent.Favorite);
        Assert.Equal(before.Revision, absent.Snapshot.Revision);
        Assert.Equal(before.Fingerprint, absent.Snapshot.Fingerprint);
        AssertEvidence(path, primary, backup);

        Assert.Throws<InvalidDataException>(() => store.Set(Thread("new"), true));
        AssertEvidence(path, primary, backup);
        Assert.Throws<InvalidDataException>(() => store.Set(Thread(), false));
        AssertEvidence(path, primary, backup);
        Assert.Equal(new[] { Thread() }, store.ReadSnapshot().Threads);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":999,\"Entries\":[]}")]
    public void MediaNoopsAllowInvalidBackupButMutationsRejectItWithoutChangingBytes(string rawBackup)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        store.SetFavorite(Entry(), true);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        byte[] primary = File.ReadAllBytes(path);
        byte[] backup = Encoding.UTF8.GetBytes(rawBackup);
        File.WriteAllBytes(path + ".bak", backup);
        var before = store.ReadTransferSnapshot();

        // Same identity is a no-op even if incoming descriptive metadata differs.
        Assert.False(store.SetFavorite(Entry() with { Length = 999, OriginalFileName = "different.jpg" }, true));
        AssertEvidence(path, primary, backup);
        Assert.False(store.SetFavorite(Entry("missing"), false));
        AssertEvidence(path, primary, backup);
        Assert.Equal(0, store.RemoveThreads([Thread("missing")]));
        AssertEvidence(path, primary, backup);
        Assert.Equal(0, store.RemoveThreads([]));
        AssertEvidence(path, primary, backup);
        Assert.Equal(before.Revision, store.ReadTransferSnapshot().Revision);
        Assert.Equal(before.InstanceId, store.ReadTransferSnapshot().InstanceId);
        Assert.Equal(before.Fingerprint, store.ReadTransferSnapshot().Fingerprint);
        Assert.Equal(Entry(), Assert.Single(store.List()));

        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry("new"), true));
        AssertEvidence(path, primary, backup);
        Assert.Throws<InvalidDataException>(() => store.SetFavorite(Entry(), false));
        AssertEvidence(path, primary, backup);
        Assert.Throws<InvalidDataException>(() => store.RemoveThreads([Thread()]));
        AssertEvidence(path, primary, backup);
        Assert.Equal(Entry(), Assert.Single(store.List()));
    }

    [Fact]
    public void ValidMarkedThreadBackupStillBlocksBothNoopsWithUnmarkedPrimaryAfterRestart()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "thread-favorites.json");
        byte[] primary;
        byte[] markedBackup;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            ThreadFavoriteStore store = new(temp.Path, lease);
            store.Set(Thread(), true);
            primary = File.ReadAllBytes(path);
            var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Thread("batch")]);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
            File.Copy(path, path + ".bak", overwrite: true);
            markedBackup = File.ReadAllBytes(path + ".bak");
            File.WriteAllBytes(path, primary);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore next = new(temp.Path, restarted);
        Assert.Throws<InvalidDataException>(() => next.Set(Thread(), true));
        AssertEvidence(path, primary, markedBackup);
        Assert.Throws<InvalidDataException>(() => next.Set(Thread("missing"), false));
        AssertEvidence(path, primary, markedBackup);
        Assert.Equal(new[] { Thread() }, next.ReadSnapshot().Threads);
    }

    [Fact]
    public void ValidMarkedMediaBackupStillBlocksAllNoopsWithUnmarkedPrimaryAfterRestart()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "media-favorites.json");
        byte[] primary;
        byte[] markedBackup;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            FavoriteStore store = new(temp.Path, lease);
            store.SetFavorite(Entry(), true);
            primary = File.ReadAllBytes(path);
            var batch = store.PrepareAdditiveBatch(Operation, PlanHash, [Entry("batch")]);
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitPreparedBatch(batch));
            File.Copy(path, path + ".bak", overwrite: true);
            markedBackup = File.ReadAllBytes(path + ".bak");
            File.WriteAllBytes(path, primary);
        }
        using ProfileLease restarted = ProfileLease.Acquire(temp.Path);
        FavoriteStore next = new(temp.Path, restarted);
        Assert.Throws<InvalidDataException>(() => next.SetFavorite(Entry(), true));
        AssertEvidence(path, primary, markedBackup);
        Assert.Throws<InvalidDataException>(() => next.SetFavorite(Entry("missing"), false));
        AssertEvidence(path, primary, markedBackup);
        Assert.Throws<InvalidDataException>(() => next.RemoveThreads([Thread("missing")]));
        AssertEvidence(path, primary, markedBackup);
        Assert.Throws<InvalidDataException>(() => next.RemoveThreads([]));
        AssertEvidence(path, primary, markedBackup);
        Assert.Equal(Entry(), Assert.Single(next.List()));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("remove-threads")]
    public void MediaMutationWithDirectoryBackupRetainsIOExceptionAndEvidence(string mutation)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        FavoriteStore store = new(temp.Path, lease);
        store.SetFavorite(Entry(), true);
        string path = Path.Combine(temp.Path, "media-favorites.json");
        byte[] primary = File.ReadAllBytes(path);
        Directory.CreateDirectory(path + ".bak");
        string evidencePath = Path.Combine(path + ".bak", "synthetic-evidence.bin");
        byte[] evidence = [0, 1, 255, 42];
        File.WriteAllBytes(evidencePath, evidence);

        Assert.Throws<IOException>(() =>
        {
            switch (mutation)
            {
                case "add": store.SetFavorite(Entry("new"), true); break;
                case "remove": store.SetFavorite(Entry(), false); break;
                case "remove-threads": store.RemoveThreads([Thread()]); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(path + ".bak"));
        Assert.Equal(evidence, File.ReadAllBytes(evidencePath));
        Assert.Equal(evidencePath, Assert.Single(Directory.GetFileSystemEntries(path + ".bak")));
        Assert.Equal(Entry(), Assert.Single(store.List()));
    }
}
