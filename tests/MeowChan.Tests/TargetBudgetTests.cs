using System.Net;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class TargetBudgetTests
{
    [Fact]
    public async Task BlockedTargetUsesNoPendingOrActiveSlotAndExplicitProbeResumesSameBytes()
    {
        using TempDirectory directory = new();
        byte[] payload = [4, 3, 2, 1];
        int requests = 0;
        using StubImageBoard board = Provider(() => Interlocked.Increment(ref requests), payload);
        DownloadQueue queue = new(1, 1L << 30, 1);
        bool full = true;
        queue.TargetGate.ProbeOverride = (path, reserve, size, token) => Task.FromResult<string?>(path.EndsWith("full") && full ? "free-space-reserve" : null);
        DownloadItem blocked = Item(board, directory, "full", 1);
        DownloadItem healthy = Item(board, directory, "healthy", 2);
        try
        {
            Task<bool> waiting = queue.EnqueueAsync(blocked);
            await TestWait.UntilAsync(() => queue.TargetWaitStates.Count == 1);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(0, queue.GetCapacitySnapshot().Active);
            Assert.Equal(0, queue.GetCapacitySnapshot().Pending);
            Assert.True(await queue.EnqueueAsync(healthy));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, requests);
            Assert.Equal(payload, File.ReadAllBytes(healthy.DestinationPath));
            full = false;
            await queue.CheckTargetsAsync();
            Assert.True(await waiting.WaitAsync(TestWait.Timeout));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(payload, File.ReadAllBytes(blocked.DestinationPath));
            Assert.Equal(2, requests);
            Assert.Empty(queue.TargetWaitStates);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task PollsAfterSixtySecondsAndCancellationClosesBlockedAdmissions()
    {
        using TempDirectory directory = new();
        BudgetTime clock = new();
        using StubImageBoard board = Provider(() => { }, [1]);
        DownloadQueue queue = new(1, 1L << 30, timeProvider: clock);
        int probes = 0;
        bool offline = true;
        queue.TargetGate.ProbeOverride = (_, _, _, _) =>
        { Interlocked.Increment(ref probes); return Task.FromResult<string?>(offline ? "target-unavailable" : null); };
        try
        {
            DownloadItem item = Item(board, directory, "offline", 1);
            Task<bool> waiting = queue.EnqueueAsync(item);
            await TestWait.UntilAsync(() => queue.TargetWaitStates.Count == 1);
            Assert.Equal(clock.GetUtcNow().AddSeconds(60), Assert.Single(queue.TargetWaitStates).NextCheckAt);
            await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(60));
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(1, probes);
            offline = false;
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await waiting.WaitAsync(TestWait.Timeout));
            // The real transfer also owns a byte-budget timer; pay that separate debt explicitly.
            await clock.WaitForTimerAtAsync(TimeSpan.FromSeconds(60) + TimeSpan.FromMilliseconds(1));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(File.Exists(item.DestinationPath));
            offline = true;
            DownloadItem cancelled = Item(board, directory, "cancelled", 2);
            Task<bool> cancelledAdmission = queue.EnqueueAsync(cancelled);
            await TestWait.UntilAsync(() => queue.TargetWaitStates.Count == 1);
            await queue.CancelDownloadsForThreadAsync(cancelled.Thread);
            Assert.False(await cancelledAdmission.WaitAsync(TestWait.Timeout));
            Task<bool> stopped = queue.EnqueueAsync(Item(board, directory, "stopped", 3));
            await TestWait.UntilAsync(() => queue.TargetWaitStates.Count == 2);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            Assert.False(await stopped.WaitAsync(TestWait.Timeout));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task PreTransferBlockReleasesWorkerWhileKeepingBoundedJobForRecovery()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = Provider(() => { }, [1, 2, 3]);
        DownloadQueue queue = new(1, 1L << 30, 1);
        bool full = true;
        queue.TargetGate.ProbeOverride = (path, _, _, _) => Task.FromResult<string?>(path.EndsWith("full") && full ? "disk-full" : null);
        DownloadItem blocked = Item(board, directory, "full", 1);
        DownloadItem healthy = Item(board, directory, "healthy", 2);
        try
        {
            Assert.True(queue.TryEnqueue(blocked));
            await TestWait.UntilAsync(() => queue.TargetWaitStates.Count == 1 && queue.GetCapacitySnapshot().Active == 0);
            Assert.True(queue.TryEnqueue(healthy));
            await TestWait.UntilAsync(() => File.Exists(healthy.DestinationPath) && queue.GetCapacitySnapshot().Active == 0);
            Assert.True(queue.IsInQueue(blocked.DownloadUri.AbsoluteUri));
            Assert.False(File.Exists(blocked.DestinationPath));
            full = false;
            await queue.CheckTargetsAsync();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(blocked.DestinationPath));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task UnavailableFilterCapacityDoesNotBlockOrdinaryTransferAndWakeStartsFilter()
    {
        using TempDirectory directory = new();
        using StubImageBoard board = Provider(() => { }, [1]);
        DownloadQueue queue = new(1, 1L << 30, 2);
        CapacityFilter filter = new();
        DownloadItem filtered = Item(board, directory, "filtered", 1);
        filtered.Filter = filter;
        DownloadItem ordinary = Item(board, directory, "ordinary", 2);
        try
        {
            Assert.True(queue.TryEnqueue(filtered));
            Assert.Equal(0, queue.GetCapacitySnapshot().Active);
            Assert.True(queue.TryEnqueue(ordinary));
            await TestWait.UntilAsync(() => File.Exists(ordinary.DestinationPath) && queue.GetCapacitySnapshot().Active == 0);
            Assert.Equal(0, filter.Starts);
            filter.Open();
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, filter.Starts);
            Assert.False(filtered.Thread.IsMediaDownloaded(filtered.MediaIdentifier));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task RealDiskProbeHonorsReserveAndExpectedSize()
    {
        using TempDirectory directory = new();
        DownloadTargetGate gate = new(TimeProvider.System, default);
        gate.UpdateReserve(long.MaxValue);
        Assert.False(await gate.CheckAsync(directory.Path, null, default));
        Assert.Equal("free-space-reserve", Assert.Single(gate.Snapshot()).Reason);
        gate.UpdateReserve(0);
        Assert.True(await gate.CheckAsync(directory.Path, 1, default, true));
        Assert.Empty(gate.Snapshot());
        Assert.False(await gate.CheckAsync(directory.Path, long.MaxValue, default, true));
    }

    [Fact]
    public async Task RealPublicationIoFailureRetainsJournalDeadlineAndRestartsWithoutDuplicateBytes()
    {
        using TempDirectory profile = new();
        using TempDirectory destination = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore store = new(profile.Path, lease);
        using StubImageBoard board = Provider(() => { }, [7, 8, 9]);
        DownloadItem first = Item(board, destination, "full", 1);
        DateTimeOffset ended = DateTimeOffset.UtcNow.AddHours(-1);
        WorkResource owner = store.CommitIntent(new WorkResource { Url = first.Thread.Url, OutputRoot = Path.GetDirectoryName(first.DestinationPath)!,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.NotFound, EndObservedAt = ended });
        DateTimeOffset due = DateTimeOffset.UtcNow.AddSeconds(-1);
        MediaWork previous = new() { ResourceUrl = owner.Url, Generation = 1, MediaIdentifier = first.MediaIdentifier,
            DestinationPath = first.DestinationPath, DownloadUrl = first.DownloadUri.AbsoluteUri,
            Status = MediaWorkStatus.Waiting, Attempts = 2, ConsecutiveNotFound = 2, HttpStatus = 404,
            LastAttemptAt = due.AddMinutes(-30), DueAt = due };
        Assert.True(store.CommitMedia(previous));
        MediaStore failingStore = new((_, _, _) => throw new IOException("Controlled disk full at publication", unchecked((int)0x80070070)));
        DownloadQueue firstQueue = new(1, 1L << 30, mediaStore: failingStore);
        firstQueue.TargetGate.ProbeOverride = (_, _, _, _) => Task.FromResult<string?>(null);
        firstQueue.ConfigureWorkState(store);
        firstQueue.BindWork(first.Thread, owner);
        await TestWait.UntilAsync(() => firstQueue.TargetWaitStates.Count == 1);
        Assert.Equal("disk-full", Assert.Single(firstQueue.TargetWaitStates).Reason);
        Assert.Equal(previous, Assert.Single(store.LoadMedia(owner.Url, 1)));
        Assert.Equal(ended, Assert.Single(store.LoadResources()).EndObservedAt);
        await firstQueue.StopAllAsync().WaitAsync(TestWait.Timeout);

        DownloadItem restored = Item(board, destination, "full", 1);
        DownloadQueue secondQueue = new(1, 1L << 30);
        secondQueue.ConfigureWorkState(store);
        try
        {
            secondQueue.BindWork(restored.Thread, owner);
            await TestWait.UntilAsync(() => store.LoadMedia(owner.Url, 1).Single().Status == MediaWorkStatus.Succeeded);
            await secondQueue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(restored.DestinationPath));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(restored.DestinationPath)!, "*.bin"));
            Assert.Equal(ended, Assert.Single(store.LoadResources()).EndObservedAt);
        }
        finally { await secondQueue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(true, MediaWorkStatus.Excluded)]
    [InlineData(false, MediaWorkStatus.NeedsIntervention)]
    public async Task FilterOmissionsAreNotDownloadedAndDoNotCreateNetworkRetryLoops(bool noMatch, MediaWorkStatus status)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using StubImageBoard board = Provider(() => { }, [1, 2]);
        DownloadItem item = Item(board, directory, "media", 1);
        item.Filter = new OmitFilter(noMatch);
        WorkResource owner = store.CommitIntent(new WorkResource { Url = item.Thread.Url, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(store); queue.BindWork(item.Thread, owner);
        try
        {
            Assert.True(await queue.EnqueueAsync(item));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.False(File.Exists(item.DestinationPath));
            MediaWork work = Assert.Single(store.LoadMedia(owner.Url, 1));
            Assert.Equal(status, work.Status);
            Assert.Null(work.DueAt);
            Assert.Equal(0, work.Attempts);
            if (noMatch) Assert.Equal(0, queue.RequestMediaRetry(item.Thread));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    private static StubImageBoard Provider(Action requested, byte[] payload) => new(new DelegateHandler((_, _) =>
    { requested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }); }));
    private static DownloadItem Item(StubImageBoard board, TempDirectory directory, string target, int id)
    {
        IThread thread = board.GetThread(new Uri($"https://stub.test/g/thread/{id}"));
        return new(new Uri($"https://stub.test/media/{id}.bin"), Path.Combine(directory.Path, target, $"{id}.bin"), board, thread, $"media{id}");
    }
    private sealed class CapacityFilter : IOriginalMediaFilter
    {
        public bool CanStart { get; private set; }
        public int Starts;
        public event Action? CapacityChanged;
        public void Open() { CanStart = true; CapacityChanged?.Invoke(); }
        public Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken)
        { Starts++; return Task.FromResult<IOriginalMediaFilterLease>(new OmitFilter(true)); }
    }
    private sealed class OmitFilter(bool noMatch) : IOriginalMediaFilter, IOriginalMediaFilterLease
    {
        public bool CanStart => true;
        public event Action? CapacityChanged { add { } remove { } }
        public Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken) => Task.FromResult<IOriginalMediaFilterLease>(this);
        public Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
            Func<int, CancellationToken, Task> throttle, CancellationToken cancellationToken) => throw new MediaFilterOmissionException(noMatch, noMatch ? "no-match" : "worker-failure");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
