using System.Collections.Concurrent;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class DownloadQueueLoopFailureTests
{
    private const string Url = "https://stub.test/g/thread/123";

    [Fact]
    public async Task ScheduleLoopFailureIsReportedAndTheNextScheduleSignalRunsAgain()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        WorkResource owner = ExpiredOwner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        RecordingSink sink = new();
        using Logger logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        DownloadQueue queue = new(1, 1L << 30, logger: logger);
        ConcurrentQueue<string> warnings = new();
        queue.PersistenceFailed += warnings.Enqueue;
        int passes = 0;
        queue.BeforeSchedulePass = () =>
        {
            if (Interlocked.Increment(ref passes) == 1) throw new InvalidOperationException("Synthetic schedule failure");
        };
        queue.ConfigureWorkState(store);
        try
        {
            queue.BindWork(thread, owner);
            await TestWait.UntilAsync(() => !warnings.IsEmpty, "The schedule loop failure was not surfaced.");
            Assert.Contains("Synthetic schedule failure", Assert.Single(warnings));
            Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Error && e.Exception?.Message == "Synthetic schedule failure");
            Assert.Equal(MediaWorkStatus.Waiting, Assert.Single(store.LoadMedia(Url, 1)).Status);
            // The fault released the loop; the next schedule signal starts a fresh pass.
            queue.BindWork(thread, owner);
            await TestWait.UntilAsync(() => store.LoadMedia(Url, 1).Single().Status == MediaWorkStatus.Expired,
                "The schedule loop did not run again after its failure.");
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ScheduleScanClockFailureReleasesTransitionAndProducerUse()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MemoryWorkStateStore store = new();
        WorkResource owner = ExpiredOwner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        ThrowingTimeProvider clock = new();
        DownloadQueue queue = new(1, 1L << 30, timeProvider: clock);
        queue.BindProfileLease(lease);
        lease.Admission.CompleteStartup();
        using ProfileProducerEpoch epoch = new(lease.Admission.EnterOrdinaryWriter());
        queue.InstallProfileEpoch(epoch);
        ConcurrentQueue<string> warnings = new();
        queue.PersistenceFailed += warnings.Enqueue;
        queue.BeforeSchedulePass = clock.Arm;
        queue.ConfigureWorkState(store);
        try
        {
            queue.BindWork(thread, owner);
            await TestWait.UntilAsync(() => !warnings.IsEmpty, "The scan clock failure was not surfaced.");
            Assert.Contains("Synthetic scan clock failure", Assert.Single(warnings));
            Assert.Equal(MediaWorkStatus.Waiting, Assert.Single(store.LoadMedia(Url, 1)).Status);
            await epoch.Close().WaitAsync(TestWait.Timeout);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            await epoch.Close().WaitAsync(TestWait.Timeout);
            queue.DetachProfileEpoch(epoch);
        }
    }

    [Fact]
    public async Task RepeatedScheduleLoopFailuresStopVisiblyInsteadOfRestartingInACircle()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        WorkResource owner = ExpiredOwner(store, directory.Path);
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadQueue queue = new(1, 1L << 30);
        ConcurrentQueue<string> warnings = new();
        queue.PersistenceFailed += warnings.Enqueue;
        int passes = 0;
        queue.BeforeSchedulePass = () =>
        {
            Interlocked.Increment(ref passes);
            throw new InvalidOperationException("Synthetic schedule failure");
        };
        queue.ConfigureWorkState(store);
        try
        {
            for (int failure = 1; failure <= 3; failure++)
            {
                queue.BindWork(thread, owner);
                await TestWait.UntilAsync(() => warnings.Count == failure);
            }
            Assert.Contains("stopped", warnings.Last());
            queue.BindWork(thread, owner);
            await Task.Delay(100);
            Assert.Equal(3, Volatile.Read(ref passes));
            Assert.Equal(3, warnings.Count);
            Assert.Equal(MediaWorkStatus.Waiting, Assert.Single(store.LoadMedia(Url, 1)).Status);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ThrowingPersistenceFailedSubscriberDoesNotLeakAWorkerSlot()
    {
        using TempDirectory directory = new();
        FailingSuccessStore store = new(new MemoryWorkStateStore());
        WorkResource owner = store.CommitIntent(new()
            { Url = Url, OutputRoot = directory.Path, Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        using StubImageBoard board = new();
        ProbeThread thread = new(board.Board, new Uri(Url));
        DownloadQueue queue = new(1, 1L << 30);
        int quarantines = 0;
        queue.PersistenceFailed += _ =>
        {
            Interlocked.Increment(ref quarantines);
            throw new InvalidOperationException("Synthetic subscriber failure");
        };
        queue.ConfigureWorkState(store); queue.BindWork(thread, owner);
        try
        {
            Assert.True(queue.TryEnqueue(Item(board, thread, directory.Path)));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(0, queue.GetCapacitySnapshot().Active);
            Assert.Equal(1, Volatile.Read(ref quarantines));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public void SqliteIoFailureIsActionableWithoutExposingItsRawPath()
    {
        string warning = DownloadQueue.PersistenceWarning("media-1", new SqliteException("disk I/O error at /home/private/profile/work-state.sqlite", 10, 266));

        Assert.Contains("SQLite I/O error 10/266", warning);
        Assert.Contains("free space", warning);
        Assert.Contains("write access", warning);
        Assert.Contains("filesystem", warning);
        Assert.Contains("Automatic retries are suspended", warning);
        Assert.DoesNotContain("/home/private", warning);
    }

    /// <summary>A source end 13 hours ago makes the 404 wait expire on the first schedule pass without HTTP.</summary>
    private static WorkResource ExpiredOwner(IWorkStateStore store, string root)
    {
        WorkResource owner = store.CommitIntent(new WorkResource { Url = Url, OutputRoot = root, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.NotFound,
            EndObservedAt = DateTimeOffset.UtcNow.AddHours(-13) });
        store.CommitMedia(new MediaWork { ResourceUrl = Url, Generation = 1, MediaIdentifier = "missing",
            DestinationPath = Path.Combine(root, "missing.bin"), DownloadUrl = "https://stub.test/media.bin",
            Status = MediaWorkStatus.Waiting, HttpStatus = 404, Failure = "Not found",
            Attempts = 1, ConsecutiveNotFound = 1, DueAt = DateTimeOffset.UtcNow.AddHours(2) });
        return owner;
    }

    private static DownloadItem Item(StubImageBoard board, ProbeThread thread, string root)
    {
        const string download = "https://stub.test/a.bin";
        ResourceId resource = new("stub", "g", "123");
        MediaProviderMetadata provider = new("media", "original", download, null, 3, null, null);
        AttachmentRevisionReference reference = new(resource, "a", 0, MediaRevisionKeys.CreateAttachmentKey(resource, "a", 0, provider));
        return new(new Uri(download), Path.Combine(root, "same.bin"), board, thread, "media", expectedSize: 3)
        {
            AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
            AttachmentReferences = [reference]
        };
    }

    /// <summary>Rejects every successful result before it is written, so the queue quarantines the media.</summary>
    private sealed class FailingSuccessStore(IWorkStateStore inner) : IWorkStateStore
    {
        public IReadOnlyList<WorkResource> LoadResources() => inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset at, string? snapshot = null)
            => inner.CommitEnd(url, generation, source, at, snapshot);
        public WorkResource? CommitState(WorkResource resource) => inner.CommitState(resource);
        public bool CommitMedia(MediaWork media) => media.Status == MediaWorkStatus.Succeeded
            ? throw new IOException("Simulated success commit failure") : inner.CommitMedia(media);
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        private int _armed;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        public override DateTimeOffset GetUtcNow() => Interlocked.Exchange(ref _armed, 0) == 1
            ? throw new InvalidOperationException("Synthetic scan clock failure") : base.GetUtcNow();
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
