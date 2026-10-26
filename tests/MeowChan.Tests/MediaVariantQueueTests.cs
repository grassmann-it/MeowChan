using System.Net;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaVariantQueueTests
{
    private const string Url = "https://stub.test/g/thread/1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariantsHaveSeparateQueueAndJournalIdentityEvenWithSameRawIdAndRequestedTarget(bool memory)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = memory ? new MemoryWorkStateStore() : new WorkStateStore(directory.Path, lease);
        using StubImageBoard board = new();
        BaseThread thread = (BaseThread)board.GetThread(new Uri(Url));
        WorkResource owner = Owner(directory, store);
        DownloadQueue queue = new(1, 1L << 30, 4);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        await queue.SetPausedAsync(true);
        DownloadItem original = Item(board, thread, directory, "x", "original", "same.bin");
        DownloadItem preview = Item(board, thread, directory, "x", "preview", "same.bin");
        original.Filter = new OmitFilter(); preview.Filter = new OmitFilter();
        try
        {
            Assert.True(queue.TryEnqueue(original));
            Assert.True(queue.TryEnqueue(preview));
            Assert.Equal(2, queue.GetCapacitySnapshot().Pending);
            Assert.Equal(2, store.LoadMedia(Url, 1).Count);
            Assert.False(queue.TryEnqueue(preview));
            await queue.SetPausedAsync(false);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.All(store.LoadMedia(Url, 1), work => Assert.Equal(MediaWorkStatus.Excluded, work.Status));
            Assert.Equal(0, thread.DownloadedMediaCount);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task FormerFlatteningCollisionRestoresRawIdsVariantsAndDifferentBytesWithoutDoubleJobs()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        int requests = 0;
        using StubImageBoard board = new(new DelegateHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("original.bin") ? [1, 2] : [7, 8, 9]) });
        }));
        BaseThread originalThread = (BaseThread)board.GetThread(new Uri(Url));
        WorkResource owner = Owner(directory, store);
        DownloadItem original = Item(board, originalThread, directory, "x--preview", "original", "x--preview.bin");
        DownloadItem preview = Item(board, originalThread, directory, "x", "preview", "variant/preview.bin");
        store.CommitMedia(Work(original)); store.CommitMedia(Work(preview));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store);
        try
        {
            queue.BindWork(originalThread, owner);
            await TestWait.UntilAsync(() => store.LoadMedia(Url, 1).All(work => work.Status == MediaWorkStatus.Succeeded));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, requests);
            Assert.Equal(2, originalThread.DownloadedMediaCount);
            Assert.True(originalThread.IsMediaDownloaded("x--preview", "original"));
            Assert.True(originalThread.IsMediaDownloaded("x", "preview"));
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(original.DestinationPath));
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(preview.DestinationPath));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
        DownloadQueue restarted = new(1, 1L << 30);
        BaseThread restoredThread = (BaseThread)board.GetThread(new Uri(Url));
        restarted.ConfigureWorkState(new WorkStateStore(directory.Path, lease));
        try
        {
            restarted.BindWork(restoredThread, owner);
            await restarted.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, restoredThread.DownloadedMediaCount);
            Assert.True(restoredThread.IsMediaDownloaded("x--preview", "original"));
            Assert.True(restoredThread.IsMediaDownloaded("x", "preview"));
            Assert.False(restoredThread.IsMediaDownloaded("x", "original"));
            Assert.Equal(2, requests);
            Assert.Equal(2, Directory.GetFiles(directory.Path, "*.bin", SearchOption.AllDirectories).Length);
        }
        finally { await restarted.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ConcurrentNoMatchCannotBeChangedBackToReadyByStaleManualRetrySnapshot()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore durable = new(directory.Path, lease);
        using StubImageBoard board = new();
        BaseThread thread = (BaseThread)board.GetThread(new Uri(Url));
        WorkResource owner = Owner(directory, durable);
        DownloadItem item = Item(board, thread, directory, "x", "preview", "preview.bin");
        MediaWork previous = Work(item) with { Status = MediaWorkStatus.NeedsIntervention, Failure = "temporary-filter-failure" };
        durable.CommitMedia(previous);
        RetryRaceStore store = new(durable);
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        try
        {
            store.AfterNextRead = () => durable.CommitMedia(previous with { Status = MediaWorkStatus.Excluded, Failure = "no-match" });
            Assert.Equal(0, queue.RequestMediaRetry(thread, "x"));
            MediaWork saved = Assert.Single(durable.LoadMedia(Url, 1));
            Assert.Equal(MediaWorkStatus.Excluded, saved.Status);
            Assert.False(saved.ManualAttempt);
            Assert.Equal(0, queue.GetCapacitySnapshot().Active);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    private static WorkResource Owner(TempDirectory directory, IWorkStateStore store) => store.CommitIntent(new WorkResource
        { Url = Url, Generation = 1, OutputRoot = directory.Path, IsThread = true, Intent = WorkIntent.Watching });
    private static DownloadItem Item(StubImageBoard board, BaseThread thread, TempDirectory directory, string id, string variant, string path)
        => new(new Uri($"https://stub.test/{variant}.bin"), Path.Combine(directory.Path, path), board, thread, id, null, null, null, null, variant);
    private static MediaWork Work(DownloadItem item) => new() { ResourceUrl = Url, Generation = 1,
        MediaIdentifier = item.MediaIdentifier, Variant = item.Variant, DestinationPath = item.DestinationPath,
        DownloadUrl = item.DownloadUri.AbsoluteUri, Status = MediaWorkStatus.Ready };
    private sealed class OmitFilter : IOriginalMediaFilter, IOriginalMediaFilterLease
    {
        public bool CanStart => true;
        public event Action? CapacityChanged { add { } remove { } }
        public Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken) => Task.FromResult<IOriginalMediaFilterLease>(this);
        public Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store, Func<int, CancellationToken, Task> throttle, CancellationToken token)
            => throw new MediaFilterOmissionException(true, "no-match");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RetryRaceStore(IWorkStateStore store) : IWorkStateStore
    {
        public Action? AfterNextRead;
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation)
        {
            IReadOnlyList<MediaWork> snapshot = store.LoadMedia(url, generation);
            Action? intervention = Interlocked.Exchange(ref AfterNextRead, null);
            intervention?.Invoke();
            return snapshot;
        }
        public IReadOnlyList<WorkResource> LoadResources() => store.LoadResources();
        public WorkResource CommitIntent(WorkResource resource) => store.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? final = null) => store.CommitEnd(url, generation, source, at, final);
        public WorkResource? CommitState(WorkResource resource) => store.CommitState(resource);
        public bool CommitMedia(MediaWork media) => store.CommitMedia(media);
    }
}
