using System.Diagnostics;
using System.Net;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>
/// PF-02 regression: queue state readers must finish while a durable work-store commit is held.
/// This is the inverse of the historical characterization probe, which proved the opposite coupling.
/// </summary>
public sealed class QueueDurableCommitIsolationTests
{
    [Theory]
    [InlineData(MediaWorkStatus.Succeeded)]
    [InlineData(MediaWorkStatus.Ready)]
    public async Task SnapshotPauseAndCapacityReadsCompleteWhileDurableCommitIsHeld(MediaWorkStatus heldStatus)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore realStore = new(directory.Path, lease);
        using HeldStore store = new(realStore, heldStatus);
        const string url = "https://stub.test/g/thread/123";
        WorkResource owner = store.CommitIntent(new WorkResource
        {
            Url = url, OutputRoot = directory.Path, Generation = 1, Intent = WorkIntent.Watching, IsThread = true
        });
        using StubImageBoard provider = new(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 3, 5, 7, 9]) })));
        ProbeThread thread = new(provider.Board, new Uri(url));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store);
        queue.BindWork(thread, owner);
        Task<bool> admission = Task.CompletedTask.ContinueWith(_ => queue.TryEnqueue(new DownloadItem(
            new Uri("https://stub.test/media.bin"), Path.Combine(directory.Path, "media.bin"), provider, thread, "media", expectedSize: 5)),
            CancellationToken.None, TaskContinuationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await store.Entered.Task.WaitAsync(TestWait.Timeout);
            // Dedicated threads: a starved thread pool must not hide a lock wait.
            Task<double> reads = Task.Factory.StartNew(() =>
            {
                long start = Stopwatch.GetTimestamp();
                Assert.Single(queue.GetThreadCounts([thread]));
                Assert.False(queue.IsPaused);
                _ = queue.GetCapacitySnapshot();
                _ = queue.MaxParallelDownloads;
                _ = queue.IsInQueue("https://stub.test/media.bin");
                return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // Completion before the release is the contract; no timing threshold beyond the generous test wait.
            await reads.WaitAsync(TestWait.Timeout);
            Assert.False(store.Released, "The commit gate must still be held while the readers complete.");
            Assert.False(admission.IsCompleted && heldStatus == MediaWorkStatus.Ready, "Held admission commit completed early.");
        }
        finally
        {
            store.Release();
        }

        Assert.True(await admission.WaitAsync(TestWait.Timeout));
        await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        MediaWork media = Assert.Single(realStore.LoadMedia(url, 1));
        Assert.Equal(MediaWorkStatus.Succeeded, media.Status);
        Assert.True(thread.IsMediaDownloaded("media"));
        Assert.Equal(5, queue.ReceivedMediaBytes);
        await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task OwnerRemovedDuringHeldCommitDoesNotLeaveScheduledOrActiveWork()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore realStore = new(directory.Path, lease);
        using HeldStore store = new(realStore, MediaWorkStatus.Ready);
        const string url = "https://stub.test/g/thread/456";
        WorkResource owner = store.CommitIntent(new WorkResource
        {
            Url = url, OutputRoot = directory.Path, Generation = 1, Intent = WorkIntent.Watching, IsThread = true
        });
        using StubImageBoard provider = new(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1]) })));
        ProbeThread thread = new(provider.Board, new Uri(url));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store);
        queue.BindWork(thread, owner);
        Task<bool> admission = Task.Factory.StartNew(() => queue.TryEnqueue(new DownloadItem(
            new Uri("https://stub.test/late.bin"), Path.Combine(directory.Path, "late.bin"), provider, thread, "late", expectedSize: 1)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await store.Entered.Task.WaitAsync(TestWait.Timeout);
            // Removal is not blocked by the held persistence call and supersedes the in-flight admission.
            await queue.CancelDownloadsForThreadAsync(thread).WaitAsync(TestWait.Timeout);
        }
        finally { store.Release(); }

        Assert.False(await admission.WaitAsync(TestWait.Timeout));
        Assert.False(queue.IsInQueue("https://stub.test/late.bin"));
        Assert.Equal(0, queue.ReceivedMediaBytes);
        await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
    }

    private sealed class HeldStore(IWorkStateStore inner, MediaWorkStatus heldStatus) : IWorkStateStore, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Released => _release.IsSet;
        public void Release() => _release.Set();
        public IReadOnlyList<WorkResource> LoadResources() => inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
            DateTimeOffset observedAt, string? finalSnapshotJson = null)
            => inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public bool CommitMedia(MediaWork media)
        {
            if (media.Status == heldStatus && Interlocked.Exchange(ref _entered, 1) == 0)
            {
                Entered.TrySetResult();
                if (!_release.Wait(TestWait.Timeout)) throw new TimeoutException("Commit gate was not released.");
            }
            return inner.CommitMedia(media);
        }
        public void Dispose() => _release.Dispose();
    }
}
