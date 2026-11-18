using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

public sealed partial class BooruCatalogStoreTests
{
    private static IEnumerable<Action<BooruCatalogStore, ProfileAdmissionToken, CancellationToken>> AdmittedMutations()
    {
        yield return (store, token, cancel) => store.UpsertPostAdmitted(Post("123"), ContractTime, token, cancel);
        yield return (store, token, cancel) => store.AddFindingAdmitted("danbooru", "123", BooruFindingKind.Search,
            null, "cat", ContractTime, token, cancel);
        yield return (store, token, cancel) => store.LinkLocalAssetAdmitted("danbooru", "123", "asset", "/offline/asset.jpg", token, cancel);
        yield return (store, token, cancel) => store.SetOriginalIntentAdmitted("danbooru", "123", BooruOriginalIntent.Requested, token, cancel);
        yield return (store, token, cancel) => store.UpsertWatcherAdmitted(new("w1", "danbooru", "cat", "Active", ContractTime), token, cancel);
        yield return (store, token, cancel) => store.MarkWatcherRemovedAdmitted("w1", token, cancel);
    }

    [Fact]
    public async Task EveryAdmittedMutationRejectsForeignDisposedAndExclusiveReservationsWithoutIO()
    {
        using TempDirectory directory = new();
        using TempDirectory foreignDirectory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(foreignDirectory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("123"), ContractTime);
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", ContractTime));
        byte[] before = File.ReadAllBytes(store.FilePath);
        long writes = store.Counters.Writes;
        using ProfileAdmissionToken foreign = foreignLease.Admission.EnterStoreWriter();
        ProfileAdmissionToken disposed = lease.Admission.EnterStoreWriter();
        disposed.Dispose();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        foreach (var mutate in AdmittedMutations())
        {
            foreach (ProfileAdmissionToken rejected in new[] { foreign, disposed, exclusive })
                Assert.Throws<InvalidOperationException>(() => mutate(store, rejected, default));
        }
        Assert.Equal(writes, store.Counters.Writes);
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
    }

    [Fact]
    public void EveryAdmittedMutationHonorsPreCancellationWithoutChangingDiskOrCache()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("123"), ContractTime);
        store.UpsertWatcher(new("w1", "danbooru", "cat", "Active", ContractTime));
        byte[] before = File.ReadAllBytes(store.FilePath);
        BooruCatalogSnapshot cached = store.Read();
        using ProfileAdmissionToken accepted = lease.Admission.EnterStoreWriter();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        foreach (var mutate in AdmittedMutations())
        {
            OperationCanceledException cancelled = Assert.Throws<OperationCanceledException>(() => mutate(store, accepted, cancellation.Token));
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
        }
        Assert.Same(cached, store.Read());
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public async Task AcceptedCatalogWritePinsItsReservationThroughShutdownAndCallerDisposal()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("123"), ContractTime);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        store.BeforeAdmittedMutationForTests = token =>
        {
            Assert.Same(accepted, token);
            entered.TrySetResult();
            Assert.True(release.Wait(Deadline), "The synthetic catalog write was not released.");
        };
        Task<BooruPostRecord> writer = Task.Factory.StartNew(() =>
            store.UpsertPostAdmitted(Post("124"), ContractTime, accepted), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            lease.Admission.BeginShutdown();
            accepted.Dispose();
            Task drain = lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
            Assert.Throws<InvalidOperationException>(() => store.UpsertPost(Post("125"), ContractTime));
            Assert.Throws<InvalidOperationException>(() => store.UpsertPostAdmitted(Post("125"), ContractTime, accepted));
            release.Set();
            Assert.Equal("124", (await writer.WaitAsync(Deadline)).PostId);
            await drain.WaitAsync(Deadline);
            lease.Admission.CompleteShutdown();
            store.BeforeAdmittedMutationForTests = null;
            Assert.Equal(2, new BooruCatalogStore(directory.Path, lease).Read().Posts.Count);
            Assert.Null(store.TryGetPost("danbooru", "125"));
            Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(Deadline);
        }
    }

    [Fact]
    public void FailureAfterAtomicReplacementReopensTheWholeObservationWithoutPartialOrCachedState()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        BooruCatalogStore store = new(directory.Path, lease);
        store.UpsertPost(Post("123"), ContractTime);
        store.AfterAdmittedMutationForTests = _ => throw new IOException("synthetic post-replacement failure");
        Assert.Throws<IOException>(() => store.RecordObservation(CompletePost("124"), ContractTime,
            BooruFindingKind.Watcher, "w1", "cat", _ => BooruOriginalIntent.Requested));
        // Once the rename succeeded, a caller may see an error but must never see a half-written observation.
        store.AfterAdmittedMutationForTests = null;
        BooruPostRecord current = store.TryGetPost("danbooru", "124")!;
        Assert.NotNull(current);
        Assert.Equal(BooruOriginalIntent.Requested, current.OriginalIntent);
        Assert.Equal("w1", Assert.Single(current.Findings).WatcherId);
        Assert.Equal(3, current.Post.Media.Count);
        Assert.Equal(2, new BooruCatalogStore(directory.Path, lease).Read().Posts.Count);
        // The cache/index must have been rebuilt even though the failing call never published its next cache.
        store.UpsertPost(Post("125"), ContractTime);
        Assert.Equal(3, store.Read().Posts.Count);
        Assert.Empty(Directory.GetFiles(directory.Path, "." + BooruCatalogStore.FileName + ".*.tmp"));
    }

    [Theory]
    [InlineData(0, BooruCatalogStore.MinimumDocumentLimitBytes, false, BooruCatalogStore.MinimumDocumentLimitBytes)]
    [InlineData(BooruCatalogStore.DefaultDocumentLimitBytes, BooruCatalogStore.MinimumDocumentLimitBytes, false,
        BooruCatalogStore.DefaultDocumentLimitBytes)]
    [InlineData(BooruCatalogStore.DefaultDocumentLimitBytes, BooruCatalogStore.MinimumDocumentLimitBytes, true,
        BooruCatalogStore.DefaultDocumentLimitBytes + BooruCatalogStore.StateChangeSlackBytes)]
    [InlineData(BooruCatalogStore.MaximumDocumentBytes - 1, BooruCatalogStore.MinimumDocumentLimitBytes, true,
        BooruCatalogStore.MaximumDocumentBytes)]
    [InlineData(BooruCatalogStore.MaximumDocumentBytes, BooruCatalogStore.MaximumDocumentBytes, true,
        BooruCatalogStore.MaximumDocumentBytes)]
    public void WriteAllowanceNeverExceedsTheReadCeilingEvenForTerminalStateChanges(
        long previousBytes, long configuredBytes, bool stateOnly, long expected) =>
        Assert.Equal(expected, BooruCatalogStore.WriteCeiling(previousBytes, configuredBytes, stateOnly));
}
