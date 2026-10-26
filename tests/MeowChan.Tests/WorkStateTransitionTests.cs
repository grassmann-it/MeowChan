using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class WorkStateTransitionTests
{
    private const string Url = "https://stub.test/g/thread/123";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionalWriteRejectsReplacedAcquisitionAttachmentsSuppressionAndGeneration(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource owner = Owner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        MediaWork a = Work(Item(board, thread, directory.Path, "a"));
        MediaWork b = Work(Item(board, thread, directory.Path, "b"));
        Assert.True(store.CommitMedia(a));
        WorkStateAccess access = WorkStateAccess.For(store);
        MediaWork snapshot = access.FindMedia(MediaWorkKey.Of(a))!;
        Assert.True(store.CommitMedia(b));
        using (access.Enter(Url)) Assert.False(access.TryCommitMedia(snapshot with { Status = MediaWorkStatus.Succeeded }, snapshot));
        Assert.Equal(b.AcquisitionRevisionKey, access.FindMedia(MediaWorkKey.Of(a))!.AcquisitionRevisionKey);
        Assert.True(store.CommitMedia(a));
        MediaWork refsChanged = a with { AttachmentReferences = [] };
        Assert.True(store.CommitMedia(refsChanged));
        using (access.Enter(Url)) Assert.False(access.TryCommitMedia(a with { Status = MediaWorkStatus.Excluded }, a));
        store.CommitIntent(owner with { Generation = 2, Intent = WorkIntent.Suppressed });
        using (access.Enter(Url)) Assert.False(access.TryCommitMedia(refsChanged with { Status = MediaWorkStatus.Succeeded }, refsChanged));
        store.CommitIntent(owner with { Generation = 3 });
        using (access.Enter(Url)) Assert.False(access.TryCommitMedia(refsChanged with { Status = MediaWorkStatus.Ready }, refsChanged));
        Assert.Equal(0, access.GateCount);
        Assert.Equal(0, access.Counts.FallbackWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRetrySnapshotNeverRebindsOrSchedulesOldRevision(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore inner = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource owner = Owner(inner, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        MediaWork a = Work(Item(board, thread, directory.Path, "a")) with { Status = MediaWorkStatus.NeedsIntervention };
        MediaWork b = Work(Item(board, thread, directory.Path, "b")) with { Status = MediaWorkStatus.NeedsIntervention };
        inner.CommitMedia(a);
        SnapshotStore store = new(inner);
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        try
        {
            store.AfterNextMediaRead = () => Assert.True(inner.CommitMedia(b));
            Assert.Equal(0, queue.RequestMediaRetry(thread));
            MediaWork actual = Assert.Single(inner.LoadMedia(Url, 1));
            Assert.Equal(b, actual);
            Assert.Equal(b.AttachmentReferences, actual.AttachmentReferences);
            Assert.False(actual.ManualAttempt);
            Assert.Equal(0, queue.ReceivedMediaBytes);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Assert.True(WorkStateAccess.For(store).Counts.FallbackReads > 0);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleFinalAdmissionCannotDisplaceLatestQueuedRevision(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource owner = Owner(store, directory.Path);
        List<string> requests = new();
        using StubImageBoard board = new(new DelegateHandler((request, _) =>
        {
            lock (requests) requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadQueue queue = new(1, 1L << 30, 4);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        await queue.SetPausedAsync(true);
        DownloadItem a = Item(board, thread, directory.Path, "a");
        DownloadItem b = Item(board, thread, directory.Path, "b");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.BeforeFinalAdmission = item =>
        {
            if (item.DownloadUri != a.DownloadUri) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        Task<bool> old = queue.EnqueueAsync(a);
        try
        {
            await entered.Task.WaitAsync(TestWait.Timeout);
            Assert.True(await queue.EnqueueAsync(b).WaitAsync(TestWait.Timeout));
            Assert.Equal(1, queue.GetCapacitySnapshot().Pending);
            release.TrySetResult();
            Assert.False(await old.WaitAsync(TestWait.Timeout));
            await queue.SetPausedAsync(false);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            MediaWork actual = Assert.Single(store.LoadMedia(Url, 1));
            Assert.Equal(MediaWorkStatus.Succeeded, actual.Status);
            Assert.Equal(b.AcquisitionRevisionKey, actual.AcquisitionRevisionKey);
            Assert.Equal(b.AttachmentReferences, actual.AttachmentReferences);
            Assert.Equal([b.DownloadUri.AbsoluteUri], requests);
            Assert.True(thread.IsMediaDownloaded("media"));
        }
        finally { release.TrySetResult(); await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisionSwapDuringNetworkPublishesOnlyTheLatestAcquisitionAndAttachment(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource owner = Owner(store, directory.Path);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using StubImageBoard board = new(new DelegateHandler(async (request, _) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/a.bin") { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadItem a = Item(board, thread, directory.Path, "a");
        DownloadItem b = Item(board, thread, directory.Path, "b");
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        try
        {
            Assert.True(queue.TryEnqueue(a));
            await entered.Task.WaitAsync(TestWait.Timeout);
            // This must complete while A is still transferring: no resource fence spans HTTP.
            Assert.True(await Task.Run(() => queue.TryEnqueue(b)).WaitAsync(TestWait.Timeout));
            release.TrySetResult();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            MediaWork actual = Assert.Single(store.LoadMedia(Url, 1));
            Assert.Equal(MediaWorkStatus.Succeeded, actual.Status);
            Assert.Equal(b.AcquisitionRevisionKey, actual.AcquisitionRevisionKey);
            Assert.Equal(b.AttachmentReferences, actual.AttachmentReferences);
            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, ".meowchan-manifest.json")));
            JsonElement bindings = manifest.RootElement.GetProperty("AttachmentBindings");
            Assert.False(bindings.TryGetProperty(a.AttachmentReferences[0].RevisionKey, out _));
            Assert.True(bindings.TryGetProperty(b.AttachmentReferences[0].RevisionKey, out _));
            Assert.Equal(2, requests);
            Assert.Equal(0, actual.Attempts);
        }
        finally { release.TrySetResult(); await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FinalManifestAdoptionFencesSuppressionAndRevisionAdmissionButLeavesOtherResourcesFree(bool memory, bool replace)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource owner = Owner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        MediaStore mediaStore = new((from, to, overwrite) =>
        {
            // Hold the actual manifest replacement, after final authority and the media move.
            if (Path.GetFileName(to) == ".meowchan-manifest.json" && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                if (!release.Wait(TestWait.Timeout)) throw new TimeoutException("Manifest gate not released.");
            }
            File.Move(from, to, overwrite);
        });
        DownloadQueue queue = new(1, 1L << 30, mediaStore: mediaStore);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        Task? suppress = null;
        try
        {
            Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path, "a")));
            await entered.Task.WaitAsync(TestWait.Timeout);
            WorkStateAccess access = WorkStateAccess.For(store);
            TaskCompletionSource suppressionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            suppress = Task.Factory.StartNew(() =>
            {
                suppressionStarted.TrySetResult();
                if (replace) Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path, "b")));
                else store.CommitIntent(owner with { Generation = 2, Intent = WorkIntent.Suppressed });
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await suppressionStarted.Task.WaitAsync(TestWait.Timeout);
            // A second same-resource fence is provably unavailable while adoption is held.
            using CancellationTokenSource blocked = new();
            Task<IDisposable> probe = access.EnterAsync(Url, blocked.Token).AsTask();
            Assert.False(probe.IsCompleted);
            blocked.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using IDisposable ignored = await probe; });
            Assert.False(suppress.IsCompleted);
            Task reads = Task.Run(() => { _ = queue.GetThreadCounts([thread]); Assert.False(queue.IsPaused); _ = queue.GetCapacitySnapshot(); });
            await reads.WaitAsync(TestWait.Timeout);
            string otherUrl = "https://stub.test/g/thread/456";
            await Task.Run(() => store.CommitIntent(owner with { Url = otherUrl })).WaitAsync(TestWait.Timeout);
        }
        finally
        {
            release.Set();
            if (suppress != null) await suppress.WaitAsync(TestWait.Timeout);
            if (replace) await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
        Assert.Equal(replace ? WorkIntent.Watching : WorkIntent.Suppressed, WorkStateAccess.For(store).FindResource(Url)!.Intent);
        if (replace)
        {
            MediaWork saved = Assert.Single(store.LoadMedia(Url, 1));
            Assert.Equal(MediaWorkStatus.Succeeded, saved.Status);
            Assert.Equal(Item(board, thread, directory.Path, "b").AcquisitionRevisionKey, saved.AcquisitionRevisionKey);
        }
        Assert.Equal(0, WorkStateAccess.For(store).GateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateNetworkResultAfterRemoveReaddCannotPublishOrSucceedInNewGeneration(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        WorkResource first = Owner(store, directory.Path);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int ordinal = 0;
        using StubImageBoard board = new(new DelegateHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref ordinal) == 1) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        ProbeThread old = new(board.Board, new Uri(Url));
        ProbeThread next = new(board.Board, new Uri(Url));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(old, first);
        Task? cancelled = null;
        try
        {
            Assert.True(queue.TryEnqueue(Item(board, old, directory.Path, "a")));
            await entered.Task.WaitAsync(TestWait.Timeout);
            queue.RetireWorkOwner(old);
            store.CommitIntent(first with { Generation = 2, Intent = WorkIntent.Suppressed });
            WorkResource current = store.CommitIntent(first with { Generation = 3 });
            cancelled = queue.CancelDownloadsForThreadAsync(old);
            Assert.False(cancelled.IsCompleted);
            queue.BindWork(next, current);
            release.TrySetResult();
            await cancelled.WaitAsync(TestWait.Timeout);
            Assert.False(old.IsMediaDownloaded("media"));
            Assert.False(File.Exists(Path.Combine(directory.Path, "same.bin")));
            Assert.NotEqual(MediaWorkStatus.Succeeded, Assert.Single(store.LoadMedia(Url, 1)).Status);
            Assert.True(queue.TryEnqueue(Item(board, next, directory.Path, "b")));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(MediaWorkStatus.Succeeded, Assert.Single(store.LoadMedia(Url, 3)).Status);
            Assert.True(next.IsMediaDownloaded("media"));
        }
        finally { release.TrySetResult(); if (cancelled != null) await cancelled.WaitAsync(TestWait.Timeout); await queue.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryRemovalAcceptsSuppressionAfterAdoptionAndCleanupOnlyAfterDrain(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        using StubImageBoard board = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource scanWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowScans = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        MediaStore mediaStore = new((from, to, overwrite) =>
        {
            if (Path.GetFileName(to) == ".meowchan-manifest.json" && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                // Only the test/finally releases adoption. A local timeout could silently drain
                // this worker before the assertions intended to observe it still held.
                release.Wait();
            }
            File.Move(from, to, overwrite);
        });
        DownloadQueue queue = new(1, 1L << 30, mediaStore: mediaStore);
        await using Registry registry = new([board], queue, directory.Path, TimeSpan.FromHours(1), 3600, workStore: store);
        registry.BeforeCompletionScan = () =>
        {
            // A scan must not own Registry _gate while waiting on the held publication fence:
            // Remove needs that gate to retire the instance before adoption is released.
            scanWaiting.TrySetResult();
            return allowScans.Task;
        };
        ArchiveCleanupResource cleanup = new(new("stub", "g", "123"), Url, [directory.Path]);
        Task? removal = null;
        try
        {
            ProbeThread thread = Assert.IsType<ProbeThread>((await registry.AddAsync(Url)).Thread);
            await scanWaiting.Task.WaitAsync(TestWait.Timeout);
            Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path, "a")));
            await entered.Task.WaitAsync(TestWait.Timeout);
            removal = Task.Run(() => registry.RemoveThreadAsync(thread));
            await TestWait.UntilAsync(() => queue.IsWorkOwnerRetiring(thread));
            Assert.False(removal.IsCompleted);
            Assert.Equal(WorkIntent.Watching, WorkStateAccess.For(store).FindResource(Url)!.Intent);
            using (IDisposable? earlyReservation = queue.TryReserveArchiveCleanup([cleanup]))
                Assert.Null(earlyReservation);
            _ = queue.GetThreadCounts([thread]);
            release.Set();
            await removal.WaitAsync(TestWait.Timeout);
            Assert.Equal(WorkIntent.Suppressed, WorkStateAccess.For(store).FindResource(Url)!.Intent);
            Assert.False(thread.IsMediaDownloaded("media"));
            IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([cleanup]);
            Assert.NotNull(reservation);
            await reservation!.DisposeAsync();
            ProbeThread replacement = Assert.IsType<ProbeThread>((await registry.AddAsync(Url)).Thread);
            Assert.NotSame(thread, replacement);
            Assert.Equal(3, WorkStateAccess.For(store).FindResource(Url)!.Generation);
            Assert.True(queue.TryEnqueue(Item(board, replacement, directory.Path, "b")));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(MediaWorkStatus.Succeeded, Assert.Single(store.LoadMedia(Url, 3)).Status);
        }
        finally
        {
            release.Set();
            allowScans.TrySetResult();
            registry.BeforeCompletionScan = null;
            if (removal != null) await removal.WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData("bound")]
    [InlineData("strict")]
    [InlineData("legacy")]
    public async Task RestoreCannotPublishOrReturnSuccessAfterAuthorityChangesDuringVerification(string mode)
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        WorkResource owner = Owner(store, directory.Path);
        int network = 0;
        using StubImageBoard board = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref network);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadItem item = mode == "bound" ? Item(board, thread, directory.Path, "a")
            : new(new Uri("https://stub.test/a.bin"), Path.Combine(directory.Path, "same.bin"), board, thread, "media",
                expectedSize: 3, expectedMd5: Convert.ToBase64String(MD5.HashData(new byte[] { 1, 2, 3 })));
        if (mode == "legacy") await File.WriteAllBytesAsync(item.DestinationPath, [1, 2, 3]);
        else
        {
            using MemoryStream bytes = new([1, 2, 3]);
            await new MediaStore().CommitAsync(item, bytes, CancellationToken.None);
        }
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        MediaStore restoring = new(File.Move, onFileHashStarting: _ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TestWait.Timeout)) throw new TimeoutException("Restore gate not released.");
        });
        DownloadQueue queue = new(1, 1L << 30, mediaStore: restoring);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        try
        {
            Assert.True(queue.TryEnqueue(item));
            await entered.Task.WaitAsync(TestWait.Timeout);
            // Verification does not own a resource fence. Suppression can finish now, and final
            // restore authorization must subsequently reject the stale acquisition without HTTP.
            queue.RetireWorkOwner(thread);
            store.CommitIntent(owner with { Generation = 2, Intent = WorkIntent.Suppressed });
            release.Set();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.False(thread.IsMediaDownloaded("media"));
            Assert.NotEqual(MediaWorkStatus.Succeeded, Assert.Single(store.LoadMedia(Url, 1)).Status);
            Assert.Equal(0, network);
            if (mode == "legacy") Assert.False(File.Exists(Path.Combine(directory.Path, ".meowchan-manifest.json")));
        }
        finally { release.Set(); await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task WorkCacheRefreshMaterializesOnlyTheDirtyThread()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RecordingStore store = new(new WorkStateStore(directory.Path, lease));
        using StubImageBoard board = new();
        DownloadQueue queue = new(1, 1L << 30);
        await using Registry registry = new([board], queue, directory.Path, TimeSpan.FromHours(1), 3600, workStore: store);
        ProbeThread first = Assert.IsType<ProbeThread>((await registry.AddAsync(Url)).Thread);
        string other = "https://stub.test/g/thread/456";
        await registry.AddAsync(other);
        await TestWait.UntilAsync(() => registry.GetWorkSnapshots().Count == 2);
        await registry.DrainBackgroundTasksAsync().WaitAsync(TestWait.Timeout);
        int unrelatedReads = store.MediaReads.GetValueOrDefault(other);
        Assert.True(queue.TryEnqueue(Item(board, first, directory.Path, "a")));
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        await TestWait.UntilAsync(() => registry.GetWorkSnapshots().Single(item => item.Url == Url).SavedMedia == 1);
        Assert.Equal(unrelatedReads, store.MediaReads.GetValueOrDefault(other));
        Assert.Equal(0, WorkStateAccess.For(store).Counts.FallbackReads);
    }

    [Fact]
    public async Task AmbiguousCommittedWriteIsConfirmedBeforeResultNotification()
    {
        using TempDirectory directory = new();
        ThrowAfterCommitStore store = new(new MemoryWorkStateStore());
        WorkResource owner = Owner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        int quarantines = 0;
        queue.PersistenceFailed += _ => Interlocked.Increment(ref quarantines);
        try
        {
            Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path, "a")));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(MediaWorkStatus.Succeeded, Assert.Single(store.LoadMedia(Url, 1)).Status);
            Assert.True(thread.IsMediaDownloaded("media"));
            Assert.Equal(0, quarantines);
            Assert.Equal(1, store.ErrorsInjected);
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public void DirtySetOverflowsToOneRebuildAndResourceGatesDoNotRetainHistory()
    {
        MemoryWorkStateStore store = new();
        WorkStateAccess access = WorkStateAccess.For(store);
        Assert.True(access.TakeDirty().Full);
        for (int i = 0; i < 300; i++)
        {
            string url = $"https://stub.test/g/thread/{i}";
            access.MarkDirty(url);
            using IDisposable fence = access.Enter(url);
        }
        Assert.Equal(0, access.GateCount);
        Assert.True(access.DirtyCount.Full);
        Assert.Equal(0, access.DirtyCount.Count);
        Assert.True(access.TakeDirty().Full);
        access.MarkDirty(Url);
        (bool full, string[] urls) = access.TakeDirty();
        Assert.False(full); Assert.Equal([Url], urls);
    }

    [Fact]
    public async Task OneSqliteTransitionUsesOnlyPointsAndDoesNotMaterializeOtherThreadMedia()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource owner = Owner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        for (int i = 0; i < 40; i++)
        {
            WorkResource other = store.CommitIntent(owner with { Url = $"https://stub.test/g/thread/{1000 + i}" });
            store.CommitMedia(Work(Item(board, thread, directory.Path, "a")) with { ResourceUrl = other.Url });
        }
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        WorkStoreQueryCounts before = store.QueryCounts;
        try
        {
            Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path, "a")));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            WorkStoreQueryCounts after = store.QueryCounts;
            Assert.Equal(before.MediaBatchReads, after.MediaBatchReads);
            Assert.Equal(before.ResourceBatchReads, after.ResourceBatchReads);
            Assert.InRange(after.MaterializedRows - before.MaterializedRows, 1, 24);
            Assert.True(after.MediaPointReads > before.MediaPointReads);
            Assert.Equal(0, WorkStateAccess.For(store).Counts.FallbackReads);
        }
        finally { await queue.StopAllAsync(); }
    }

    private static WorkResource Owner(IWorkStateStore store, string root) => store.CommitIntent(new()
        { Url = Url, OutputRoot = root, Generation = 1, Intent = WorkIntent.Watching, IsThread = true });

    private static DownloadItem Item(StubImageBoard board, ProbeThread thread, string root, string revision)
    {
        string download = $"https://stub.test/{revision}.bin";
        ResourceId resource = new("stub", "g", "123");
        MediaProviderMetadata provider = new("media", "original", download, null, 3, null, null);
        AttachmentRevisionReference reference = new(resource, revision, 0,
            MediaRevisionKeys.CreateAttachmentKey(resource, revision, 0, provider));
        return new(new Uri(download), Path.Combine(root, "same.bin"), board, thread, "media", expectedSize: 3)
        {
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
            AttachmentReferences = [reference]
        };
    }

    private static MediaWork Work(DownloadItem item) => new()
    {
        ResourceUrl = Url, Generation = 1, MediaIdentifier = item.MediaIdentifier, Variant = item.Variant,
        DestinationPath = item.DestinationPath, DownloadUrl = item.DownloadUri.AbsoluteUri,
        ExpectedSize = item.ExpectedSize, AcquisitionRevisionKey = item.AcquisitionRevisionKey,
        AttachmentReferences = item.AttachmentReferences, Status = MediaWorkStatus.Ready
    };

    private sealed class ThrowAfterCommitStore(IWorkStateStore inner) : IWorkStateStore
    {
        private int _errorsInjected;
        public int ErrorsInjected => Volatile.Read(ref _errorsInjected);
        public IReadOnlyList<WorkResource> LoadResources() => inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? snapshot = null)
            => inner.CommitEnd(url, generation, source, at, snapshot);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public bool CommitMedia(MediaWork media)
        {
            bool committed = inner.CommitMedia(media);
            if (committed && media.Status == MediaWorkStatus.Succeeded && Interlocked.Exchange(ref _errorsInjected, 1) == 0)
                throw new IOException("Simulated failure after transaction adoption");
            return committed;
        }
    }

    private sealed class RecordingStore(IWorkStateStore inner) : IWorkStateStore, IConditionalWorkStateStore
    {
        private IConditionalWorkStateStore Fast => (IConditionalWorkStateStore)inner;
        internal ConcurrentDictionary<string, int> MediaReads { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<WorkResource> LoadResources() => inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation)
        {
            MediaReads.AddOrUpdate(url, 1, (_, count) => count + 1);
            return inner.LoadMedia(url, generation);
        }
        public WorkResource? FindResource(string url) => Fast.FindResource(url);
        public MediaWork? FindMedia(MediaWorkKey key) => Fast.FindMedia(key);
        public bool TryCommitMedia(MediaWork next, MediaWork? expected) => Fast.TryCommitMedia(next, expected);
        public WorkResource CommitIntentUnderFence(WorkResource resource) => Fast.CommitIntentUnderFence(resource);
        public WorkResource? CommitStateUnderFence(WorkResource resource) => Fast.CommitStateUnderFence(resource);
        public WorkResource? CommitEndUnderFence(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? snapshot)
            => Fast.CommitEndUnderFence(url, generation, source, at, snapshot);
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? snapshot = null)
            => inner.CommitEnd(url, generation, source, at, snapshot);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public bool CommitMedia(MediaWork media) => inner.CommitMedia(media);
    }

    private sealed class SnapshotStore(IWorkStateStore inner) : IWorkStateStore
    {
        internal Action? AfterNextMediaRead;
        public IReadOnlyList<WorkResource> LoadResources() => inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation)
        {
            IReadOnlyList<MediaWork> snapshot = inner.LoadMedia(url, generation);
            Interlocked.Exchange(ref AfterNextMediaRead, null)?.Invoke();
            return snapshot;
        }
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? snapshot = null)
            => inner.CommitEnd(url, generation, source, at, snapshot);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public bool CommitMedia(MediaWork media) => inner.CommitMedia(media);
    }
}
