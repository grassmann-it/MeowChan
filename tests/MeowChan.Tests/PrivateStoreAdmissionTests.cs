using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests;

// Synthetic, local-only tests of the three P08d store boundaries. Host propagation
// and the kernel's Dispose/use-pin races are separate integration test slices.
public sealed class PrivateStoreAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static ResourceId Thread => new("synthetic", "board", "42");
    private static FavoriteEntry Favorite => new(
        new FavoriteAssetKey(Thread, "image-1", "original", new string('a', 64)), 12, "image-1.jpg");
    private static PostBookmarkKey BookmarkKey => new(Thread, "43");
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string Plan = new('b', 64);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void StandaloneStartingWritesAndLegacyCommitsReleaseTheirLocalAdmissions(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        if (family == 2) _ = new PostBookmarkStore(temp.Path, lease); // Startup cleanup has its own local entry.
        store.Set(true);
        store.Set(true); // Same-value call is still a mutator, not a passive read.
        store.Set(false);
        if (family == 0) Assert.Equal(0, store.Favorites.RemoveThreads([Thread]));
        PrivateStoreBatch batch = store.Prepare();
        Assert.Equal(PrivateStoreBatchInspection.ExactBefore, store.Inspect(batch));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Commit(batch));
        Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.Commit(batch));
        Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
        lease.Admission.CompleteStartup(); // No leaked local constructor/write/commit token.
        Assert.Equal(ProfileAdmissionPhase.Ready, lease.Admission.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PendingExclusiveRejectsSecondStoreAndNoOpsButAcceptedTokenFinishes(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        store.Set(true);
        lease.Admission.CompleteStartup();
        ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Store second = new(family, lease); // Constructor-free bookmark view only.
            string[] before = Inventory(temp.Path);
            Assert.Throws<InvalidOperationException>(() => second.Set(true));
            Assert.Throws<InvalidOperationException>(() => second.Set(false));
            if (family == 0)
            {
                Assert.Throws<InvalidOperationException>(() => second.Favorites.RemoveThreads([Thread]));
                Assert.Throws<InvalidOperationException>(() => second.Favorites.RemoveThreads([]));
            }
            Assert.Equal(before, Inventory(temp.Path));
            second.Read();
            Assert.Equal(before, Inventory(temp.Path));

            if (family == 0)
            {
                Assert.Equal(1, store.Favorites.RemoveThreadsAdmitted([Thread], accepted));
                Assert.Equal(0, store.Favorites.RemoveThreadsAdmitted([], accepted));
            }
            else store.SetAdmitted(false, accepted);
            store.SetAdmitted(true, accepted);
            Assert.False(pending.IsCompleted);
            accepted.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => second.Set(true));
            Assert.Throws<InvalidOperationException>(() => second.Set(false));
            if (family == 0)
                Assert.Throws<InvalidOperationException>(() => second.Favorites.RemoveThreads([]));
            second.Read();
        }
        finally
        {
            accepted.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(0, "failed")]
    [InlineData(1, "failed")]
    [InlineData(2, "failed")]
    [InlineData(0, "stopping")]
    [InlineData(1, "stopping")]
    [InlineData(2, "stopping")]
    [InlineData(0, "stopped")]
    [InlineData(1, "stopped")]
    [InlineData(2, "stopped")]
    public void ClosedPhasesRejectMutatorsButNotPassiveReads(int family, string phase)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        store.Set(true);
        PrivateStoreBatch noop = store.Prepare(noChange: true);
        if (phase == "failed") lease.Admission.FailStartup();
        else
        {
            lease.Admission.BeginShutdown();
            if (phase == "stopped") lease.Admission.CompleteShutdown();
        }
        string[] before = Inventory(temp.Path);
        Assert.Throws<InvalidOperationException>(() => store.Set(true));
        Assert.Throws<InvalidOperationException>(() => store.Set(false));
        Assert.Throws<InvalidOperationException>(() => store.Commit(noop));
        if (family == 0) Assert.Throws<InvalidOperationException>(() => store.Favorites.RemoveThreads([]));
        Assert.Throws<InvalidOperationException>(() => new PostBookmarkStore(temp.Path, lease));
        store.Read();
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.Inspect(noop));
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.Inspect(store.Prepare(noChange: true)));
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PassivePrepareInspectAndReadStayTokenFreeAcrossReadyExclusiveAndStopping(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        lease.Admission.CompleteStartup();

        PrivateStoreBatch ready = store.Prepare(noChange: true);
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.Inspect(ready));
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        PrivateStoreBatch duringExclusive = store.Prepare(noChange: true);
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.Inspect(duringExclusive));
        store.Read();
        lease.Admission.BeginShutdown();
        PrivateStoreBatch stopping = store.Prepare(noChange: true);
        Assert.Equal(PrivateStoreBatchInspection.NoChange, store.Inspect(stopping));
        store.Read();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task PrivateCommitRequiresStartupOrExclusiveEvenForNoChangeAndExactAfter(int family, bool noChange)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare(noChange);
        lease.Admission.CompleteStartup();
        string[] before = Inventory(temp.Path);
        Assert.Throws<InvalidOperationException>(() => store.Commit(batch));
        using (ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter())
            Assert.Throws<InvalidOperationException>(() => store.CommitAdmitted(batch, ordinary));
        Assert.Equal(before, Inventory(temp.Path));
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        Assert.Throws<InvalidOperationException>(() => store.SetAdmitted(true, exclusive));
        lease.Admission.BeginShutdown(); // Already accepted Exclusive may finish; never enter Ordinary inside.
        PrivateStoreBatchInspection expected = noChange
            ? PrivateStoreBatchInspection.NoChange : PrivateStoreBatchInspection.ExactAfterWithMarker;
        Assert.Equal(expected, store.CommitAdmitted(batch, exclusive));
        string[] after = Inventory(temp.Path);
        Assert.Equal(expected, store.CommitAdmitted(batch, exclusive));
        Assert.Equal(after, Inventory(temp.Path));
        Assert.Equal(expected, store.Inspect(batch));
        Assert.Throws<InvalidOperationException>(() => store.Commit(batch));
        exclusive.Dispose();
        await lease.Admission.DrainAsync().WaitAsync(Deadline);
        lease.Admission.CompleteShutdown();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ValidAdmissionDoesNotReplaceBatchOwnershipOrMarkerFence(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        Store second = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        lease.Admission.CompleteStartup();
        using (ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync())
        {
            string[] before = Inventory(temp.Path);
            Assert.Throws<InvalidDataException>(() => second.CommitAdmitted(batch, exclusive));
            Assert.Equal(before, Inventory(temp.Path));
            Assert.Equal(PrivateStoreBatchInspection.ExactAfterWithMarker, store.CommitAdmitted(batch, exclusive));
        }
        string[] after = Inventory(temp.Path);
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        Assert.Throws<InvalidOperationException>(() => store.Commit(batch));
        Assert.Throws<InvalidOperationException>(() => store.CommitAdmitted(batch, ordinary));
        Assert.Throws<InvalidDataException>(() => store.SetAdmitted(true, ordinary));
        Assert.Throws<InvalidDataException>(() => store.Set(true));
        if (family == 0) Assert.Throws<InvalidDataException>(() => store.Favorites.RemoveThreadsAdmitted([], ordinary));
        Assert.Equal(after, Inventory(temp.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidBorrowedTokensAreRejectedBeforeStoreIO(int family)
    {
        using TempDirectory temp = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(other.Path);
        Store store = new(family, lease);
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        ProfileAdmissionToken disposed = lease.Admission.EnterStartupWriter();
        disposed.Dispose();
        PrivateStoreBatch batch = store.Prepare();
        string[] before = Inventory(temp.Path);
        foreach (ProfileAdmissionToken invalid in new[] { foreign, unissued, disposed, null! })
        {
            Assert.Throws<InvalidOperationException>(() => store.SetAdmitted(true, invalid));
            Assert.Throws<InvalidOperationException>(() => store.SetAdmitted(false, invalid));
            Assert.Throws<InvalidOperationException>(() => store.CommitAdmitted(batch, invalid));
            if (family == 0)
                Assert.Throws<InvalidOperationException>(() => store.Favorites.RemoveThreadsAdmitted([], invalid));
        }
        Assert.Equal(before, Inventory(temp.Path));
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            store.SetAdmitted(true, startup);
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancelledStoreEntriesLeaveNoFilesOrReservations(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        string[] before = Inventory(temp.Path);
        Assert.Throws<OperationCanceledException>(() => store.Set(true, new CancellationToken(true)));
        if (family == 0)
            Assert.Throws<OperationCanceledException>(() => store.Favorites.RemoveThreads([], new CancellationToken(true)));
        Assert.Equal(before, Inventory(temp.Path));
        lease.Admission.CompleteStartup();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ValidExclusiveDoesNotOverrideDriftedBatch(int family)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Store store = new(family, lease);
        PrivateStoreBatch batch = store.Prepare();
        store.Set(true); // Different unmarked bytes, not the prepared after-image.
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        string[] before = Inventory(temp.Path);
        Assert.Equal(PrivateStoreBatchInspection.Drifted, store.Inspect(batch));
        Assert.Throws<InvalidDataException>(() => store.CommitAdmitted(batch, exclusive));
        Assert.Equal(before, Inventory(temp.Path));
    }

    [Fact]
    public void BookmarkConstructorWithoutPendingFilesStillNeedsWriterAdmission()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        lease.Admission.FailStartup();
        Assert.Throws<InvalidOperationException>(() => new PostBookmarkStore(temp.Path, lease));
        Assert.Empty(PostBookmarkStore.OpenWithoutCleanup(temp.Path, lease).Read().Bookmarks);
        Assert.Empty(Inventory(temp.Path));
    }

    [Fact]
    public async Task BookmarkCleanupRequiresWriterWhilePassiveViewsKeepAllPendingEvidence()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        string legacy = Path.Combine(temp.Path, "post-bookmarks.json.pending-11111111111111111111111111111111");
        string privatePending = Path.Combine(temp.Path, "post-bookmarks.json.private-pending-22222222222222222222222222222222");
        string foreign = Path.Combine(temp.Path, "post-bookmarks.json.pending-not-ours");
        File.WriteAllText(legacy, "synthetic legacy");
        File.WriteAllText(foreign, "synthetic foreign");
        File.WriteAllText(privatePending, "synthetic private");
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            string[] before = Inventory(temp.Path);
            Assert.Throws<InvalidOperationException>(() => new PostBookmarkStore(temp.Path, lease));
            Assert.Empty(PostBookmarkStore.ReadOnly(temp.Path, lease).Bookmarks);
            Assert.Empty(PostBookmarkStore.OpenWithoutCleanup(temp.Path, lease).Read().Bookmarks);
            Assert.Equal(before, Inventory(temp.Path));
            _ = new PostBookmarkStore(temp.Path, lease, ordinary); // Same accepted token, no reacquisition.
            Assert.Equal(before, Inventory(temp.Path)); // Private pending protects legacy evidence.
            File.Delete(privatePending);
            _ = new PostBookmarkStore(temp.Path, lease, ordinary);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(foreign));
            Assert.False(pending.IsCompleted);
            ordinary.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => new PostBookmarkStore(temp.Path, lease));
            Assert.Throws<InvalidOperationException>(() => new PostBookmarkStore(temp.Path, lease, exclusive));
        }
        finally
        {
            ordinary.Dispose();
            cancel.Cancel();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    private static string[] Inventory(string profile) => Directory.EnumerateFiles(profile)
        .Where(path => Path.GetFileName(path).StartsWith("media-favorites.json", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith("thread-favorites.json", StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith("post-bookmarks.json", StringComparison.Ordinal))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();

    private sealed class Store
    {
        private readonly int _family;
        private readonly ProfileLease _lease;
        internal FavoriteStore Favorites { get; }
        private readonly ThreadFavoriteStore _threads;
        private readonly PostBookmarkStore _bookmarks;

        internal Store(int family, ProfileLease lease)
        {
            _family = family;
            _lease = lease;
            Favorites = new(lease.ProfileDirectory, lease);
            _threads = new(lease.ProfileDirectory, lease);
            _bookmarks = PostBookmarkStore.OpenWithoutCleanup(lease.ProfileDirectory, lease);
        }

        internal void Set(bool favorite, CancellationToken token = default)
        {
            if (_family == 0) Favorites.SetFavorite(Favorite, favorite, token);
            else if (_family == 1) _threads.Set(Thread, favorite, token);
            else
            {
                PostBookmarkSnapshot current = _bookmarks.Read();
                if (favorite) _bookmarks.Upsert(BookmarkKey, "note", current.Revision, current.Bookmarks.Count != 0, token);
                else _bookmarks.Remove(BookmarkKey, current.Revision, token);
            }
        }

        internal void SetAdmitted(bool favorite, ProfileAdmissionToken admission)
        {
            if (_family == 0) Favorites.SetFavoriteAdmitted(Favorite, favorite, admission);
            else if (_family == 1) _threads.SetAdmitted(Thread, favorite, admission);
            else
            {
                PostBookmarkSnapshot current = _bookmarks.Read();
                if (favorite) _bookmarks.UpsertAdmitted(BookmarkKey, "note", current.Revision, current.Bookmarks.Count != 0, admission);
                else _bookmarks.RemoveAdmitted(BookmarkKey, current.Revision, admission);
            }
        }

        internal PrivateStoreBatch Prepare(bool noChange = false) => _family switch
        {
            0 => Favorites.PrepareAdditiveBatch(Operation, Plan, noChange ? [] : [Favorite]),
            1 => _threads.PrepareAdditiveBatch(Operation, Plan, noChange ? [] : [Thread]),
            _ => _bookmarks.PrepareAdditiveBatch(Operation, Plan, noChange ? [] : [new PostBookmark(BookmarkKey, "note")])
        };

        internal PrivateStoreBatchInspection Commit(PrivateStoreBatch batch) => _family switch
        {
            0 => Favorites.CommitPreparedBatch(batch),
            1 => _threads.CommitPreparedBatch(batch),
            _ => _bookmarks.CommitPreparedBatch(batch)
        };

        internal PrivateStoreBatchInspection CommitAdmitted(PrivateStoreBatch batch, ProfileAdmissionToken admission) => _family switch
        {
            0 => Favorites.CommitPreparedBatchAdmitted(batch, admission),
            1 => _threads.CommitPreparedBatchAdmitted(batch, admission),
            _ => _bookmarks.CommitPreparedBatchAdmitted(batch, admission)
        };

        internal PrivateStoreBatchInspection Inspect(PrivateStoreBatch batch) => _family switch
        {
            0 => Favorites.InspectPreparedBatch(batch),
            1 => _threads.InspectPreparedBatch(batch),
            _ => _bookmarks.InspectPreparedBatch(batch)
        };

        internal void Read()
        {
            if (_family == 0)
            {
                _ = Favorites.List();
                _ = Favorites.Contains(Favorite.Key);
                Assert.True(Favorites.IsTransferSnapshotCurrent(Favorites.ReadTransferSnapshot()));
            }
            else if (_family == 1) _ = _threads.ReadSnapshot();
            else
            {
                _ = _bookmarks.Read();
                _ = PostBookmarkStore.ReadOnly(_lease.ProfileDirectory, _lease);
                Assert.True(PostBookmarkStore.IsTransferSnapshotCurrent(_lease.ProfileDirectory, _lease,
                    PostBookmarkStore.ReadTransferSnapshot(_lease.ProfileDirectory, _lease)));
            }
        }
    }
}
