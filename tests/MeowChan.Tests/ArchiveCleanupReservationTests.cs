using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class ArchiveCleanupReservationTests
{
    private static readonly ResourceId Id = new("stub", "g", "123");
    private const string Url = "https://stub.test/g/thread/123";

    [Fact]
    public async Task ExactAdmissionWaitsWhileOtherIdsRunAndReservationDoesNotMutateIntent()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        MemoryWorkStateStore work = new();
        WorkResource original = work.CommitIntent(Completed(temp.Path));
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        IAsyncDisposable reservation = Assert.IsAssignableFrom<IAsyncDisposable>(
            await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]));
        try
        {
            Assert.Equal(0, await registry.RetryCompletedMediaAsync(Url));
            Assert.Equal(original, Assert.Single(work.LoadResources()));
            Task<AdmissionResult> waiting = registry.AddAsync(Url);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync("https://stub.test/g/thread/124")).Status);
            Assert.False(registry.IsPaused);
            Assert.Equal(original, work.LoadResources().Single(resource => resource.Url == Url));
            Assert.Null(await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]));
            await reservation.DisposeAsync();
            Assert.Equal(AdmissionStatus.Added, (await waiting.WaitAsync(TestWait.Timeout)).Status);
        }
        finally { await reservation.DisposeAsync(); }
    }

    [Fact]
    public async Task DifferentIdentityCannotRegisterIntoReservedMediaPath()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60);
        ArchiveCleanupResource target = Target(temp.Path) with { Id = new("offline-source", "b", "456"), Url = "https://offline.invalid/456" };
        await using IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([target]);
        Assert.NotNull(reservation);
        Task<AdmissionResult> waiting = registry.AddAsync(Url);
        Assert.False(waiting.IsCompleted);
        Assert.Empty(registry.Threads);
        await reservation.DisposeAsync();
        Assert.Equal(AdmissionStatus.Added, (await waiting.WaitAsync(TestWait.Timeout)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalRepairAndAddTargetsAreFencedBeforeWorkMutation(bool legacy)
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        MemoryWorkStateStore work = new();
        WorkResource original = work.CommitIntent(Completed(temp.Path));
        string reservedDirectory = Path.Combine(temp.Path, "historical");
        string historical = Path.Combine(reservedDirectory, "old.bin");
        MediaWork media = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "one",
            DestinationPath = legacy ? Path.Combine(temp.Path, "current.bin") : historical,
            LegacyPath = legacy ? historical : null, DownloadUrl = "https://offline.invalid/one", Status = MediaWorkStatus.Expired };
        Assert.True(work.CommitMedia(media));
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        ArchiveCleanupResource target = new(new ResourceId("offline", "b", "456"), "https://offline.invalid/456", new[] { reservedDirectory });
        await using IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([target]);
        Assert.NotNull(reservation);
        Assert.Equal(0, await registry.RetryCompletedMediaAsync(Url));
        Assert.Equal(original, Assert.Single(work.LoadResources()));
        Assert.Equal(media, Assert.Single(work.LoadMedia(Url, 1)));
        Task<AdmissionResult> waiting = registry.AddAsync(Url);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(original, Assert.Single(work.LoadResources()));
        await reservation.DisposeAsync();
        Assert.Equal(AdmissionStatus.Added, (await waiting.WaitAsync(TestWait.Timeout)).Status);
    }

    [Theory]
    [InlineData(WorkIntent.Watching, false, MediaWorkStatus.Succeeded)]
    [InlineData(WorkIntent.Repairing, false, MediaWorkStatus.Succeeded)]
    [InlineData(WorkIntent.Completed, true, MediaWorkStatus.Succeeded)]
    [InlineData(WorkIntent.Completed, false, MediaWorkStatus.NeedsIntervention)]
    [InlineData(WorkIntent.Completed, false, MediaWorkStatus.Ready)]
    [InlineData(WorkIntent.Completed, false, MediaWorkStatus.Waiting)]
    public async Task UnfinishedDurableWorkBlocksWithoutChangingIt(WorkIntent intent, bool capture, MediaWorkStatus status)
    {
        using TempDirectory temp = new();
        MemoryWorkStateStore work = new();
        WorkResource resource = work.CommitIntent(Completed(temp.Path) with { Intent = intent, CapturePending = capture });
        MediaWork media = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "one",
            DestinationPath = Path.Combine(temp.Path, "one.bin"), DownloadUrl = "https://offline.invalid/one", Status = status };
        Assert.True(work.CommitMedia(media));
        await using Registry registry = new([], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        Assert.Null(await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]));
        Assert.Equal(resource, Assert.Single(work.LoadResources()));
        Assert.Equal(media, Assert.Single(work.LoadMedia(Url, 1)));
        Assert.False(registry.IsPaused);
    }

    [Theory]
    [InlineData(WorkIntent.Completed)]
    [InlineData(WorkIntent.Suppressed)]
    public async Task TerminalExpiredWorkWithoutPendingCaptureOrActivityMayReserve(WorkIntent intent)
    {
        using TempDirectory temp = new();
        MemoryWorkStateStore work = new();
        WorkResource original = work.CommitIntent(Completed(temp.Path));
        MediaWork media = new() { ResourceUrl = Url, Generation = 1, MediaIdentifier = "expired",
            DestinationPath = Path.Combine(temp.Path, "expired.bin"), DownloadUrl = "https://offline.invalid/expired", Status = MediaWorkStatus.Expired };
        Assert.True(work.CommitMedia(media));
        // Terminal media predates suppression; a suppressed owner correctly rejects new media commits.
        if (intent == WorkIntent.Suppressed)
        {
            original = Assert.IsType<WorkResource>(work.CommitState(original with { Intent = intent }));
            Assert.False(work.CommitMedia(media with { Failure = "late write after suppression" }));
        }
        await using Registry registry = new([], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        await using IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]);
        Assert.NotNull(reservation);
        Assert.Equal(original, Assert.Single(work.LoadResources()));
        Assert.Equal(media, Assert.Single(work.LoadMedia(Url, 1)));
    }

    [Fact]
    public async Task PausedWatchIsNotMadeEligible()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), temp.Path, TimeSpan.FromMinutes(1), 60);
        await registry.SetPausedAsync(true);
        await registry.AddAsync(Url);
        Assert.Null(await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]));
        Assert.True(registry.IsPaused);
        Assert.Single(registry.Threads);
    }

    [Fact]
    public async Task OfflineArchiveWithNoWorkCanReserveAndReleaseWithoutCreatingWork()
    {
        using TempDirectory temp = new();
        MemoryWorkStateStore work = new();
        await using Registry registry = new([], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        await using IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]);
        Assert.NotNull(reservation);
        Assert.Empty(work.LoadResources());
        Assert.Empty(registry.Threads);
        await reservation.DisposeAsync();
        await using IAsyncDisposable? again = await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task QueuedOtherIdentityIntoTargetSubtreeBlocksReservationWithoutStoppingJob()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1L << 30);
        await queue.SetPausedAsync(true);
        try
        {
            ProbeThread other = new(provider.Board, new Uri("https://stub.test/g/thread/456"));
            DownloadItem item = new(new Uri("https://offline.invalid/image"), Path.Combine(Target(temp.Path).MediaDirectories[0], "image.bin"),
                provider, other, "image");
            Assert.True(await queue.EnqueueAsync(item));
            Assert.Null(queue.TryReserveArchiveCleanup([Target(temp.Path)]));
            Assert.Equal(1, queue.GetCapacitySnapshot().Pending);
            Assert.Equal(new ResourceId("stub", "g", "456"), Assert.Single(queue.ReadArchiveCleanupActiveThreadIds()));
            Assert.True(queue.IsPaused);
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task FutureQueueAdmissionWaitsAndCancellationDoesNotCreateWork()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1L << 30);
        await queue.SetPausedAsync(true);
        try
        {
            using IDisposable reservation = Assert.IsAssignableFrom<IDisposable>(queue.TryReserveArchiveCleanup([Target(temp.Path)]));
            ProbeThread thread = new(provider.Board, new Uri(Url));
            DownloadItem item = new(new Uri("https://offline.invalid/image"), Path.Combine(Target(temp.Path).MediaDirectories[0], "image.bin"),
                provider, thread, "image");
            using CancellationTokenSource cancel = new();
            Task<bool> waiting = queue.EnqueueAsync(item, cancel.Token);
            Assert.False(waiting.IsCompleted);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Task<bool> acceptedAfterRelease = queue.EnqueueAsync(item);
            reservation.Dispose();
            Assert.True(await acceptedAfterRelease.WaitAsync(TestWait.Timeout));
            Assert.Equal(1, queue.GetCapacitySnapshot().Pending);
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task SchedulerTargetProbeTransitRemainsVisibleToCleanup()
    {
        using TempDirectory temp = new();
        using StubImageBoard provider = new();
        MemoryWorkStateStore work = new();
        WorkResource owner = work.CommitIntent(Completed(temp.Path) with { Intent = WorkIntent.Watching });
        Assert.True(work.CommitMedia(new MediaWork { ResourceUrl = Url, Generation = 1, MediaIdentifier = "waiting",
            DestinationPath = Path.Combine(Target(temp.Path).MediaDirectories[0], "waiting.bin"),
            DownloadUrl = "https://offline.invalid/waiting", Status = MediaWorkStatus.Waiting, DueAt = DateTimeOffset.UtcNow.AddSeconds(-1) }));
        DownloadQueue queue = new(1, 1L << 30);
        queue.ConfigureWorkState(work);
        await queue.SetPausedAsync(true);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TargetGate.ProbeOverride = async (_, _, _, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        };
        try
        {
            queue.BindWork(new ProbeThread(provider.Board, new Uri(Url)), owner);
            await entered.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Assert.Null(queue.TryReserveArchiveCleanup([Target(temp.Path)]));
            Assert.Equal(Id, Assert.Single(queue.ReadArchiveCleanupActiveThreadIds()));
            Assert.Equal(MediaWorkStatus.Waiting, Assert.Single(work.LoadMedia(Url, 1)).Status);
            release.TrySetResult();
            await TestWait.UntilAsync(() => queue.GetCapacitySnapshot().Pending == 1);
        }
        finally { release.TrySetResult(); await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task FailedAuthorityReadReleasesEveryFenceAndDoesNotInventEmptyWork()
    {
        using TempDirectory temp = new();
        FailingReadStore work = new();
        await using Registry registry = new([], new DownloadQueue(1, 1L << 30), temp.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        await Assert.ThrowsAsync<IOException>(() => registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]));
        work.Fail = false;
        await using IAsyncDisposable? reservation = await registry.TryReserveArchiveCleanupAsync([Target(temp.Path)]);
        Assert.NotNull(reservation);
    }

    private sealed class FailingReadStore : IWorkStateStore
    {
        internal bool Fail = true;
        public IReadOnlyList<WorkResource> LoadResources() => Fail ? throw new IOException("unavailable") : Array.Empty<WorkResource>();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => Array.Empty<MediaWork>();
        public WorkResource CommitIntent(WorkResource resource) => throw new NotSupportedException();
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt,
            string? finalSnapshotJson = null) => throw new NotSupportedException();
        public WorkResource? CommitState(WorkResource resource) => throw new NotSupportedException();
        public bool CommitMedia(MediaWork media) => throw new NotSupportedException();
    }

    private static ArchiveCleanupResource Target(string root) => new(Id, Url,
        new[] { StorageLayout.GetThreadDirectory(root, Id.Source, Id.Board, Id.Thread!) });
    private static WorkResource Completed(string root) => new()
    {
        Url = Url, OutputRoot = root, Generation = 1, Intent = WorkIntent.Completed, IsThread = true,
        Source = ThreadSourceStateValue.Archived, EndObservedAt = DateTimeOffset.UtcNow.AddDays(-1)
    };
}
