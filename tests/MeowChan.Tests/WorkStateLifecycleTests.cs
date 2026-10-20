using System.Net;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Storage;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class WorkStateLifecycleTests
{
    private const string ThreadUrl = "https://stub.test/g/thread/123";

    [Fact]
    public void AuthorityPreservesFirstEndAndRejectsOldGenerationCallbacks()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource first = store.CommitIntent(new WorkResource
        {
            Url = ThreadUrl, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true
        });
        DateTimeOffset firstSeen = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        WorkResource ended = Assert.IsType<WorkResource>(store.CommitEnd(ThreadUrl, 1,
            ThreadSourceStateValue.NotFound, firstSeen));
        Assert.Equal(firstSeen, ended.EndObservedAt);
        Assert.Equal(firstSeen, store.CommitEnd(ThreadUrl, 1, ThreadSourceStateValue.NotFound,
            firstSeen.AddHours(1))!.EndObservedAt);

        WorkResource removed = store.CommitIntent(first with { Generation = 2, Intent = WorkIntent.Suppressed });
        Assert.Null(store.CommitEnd(ThreadUrl, 1, ThreadSourceStateValue.Archived, firstSeen.AddHours(2)));
        Assert.False(store.CommitMedia(new MediaWork
        {
            ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "old",
            DestinationPath = Path.Combine(directory.Path, "old.bin"), DownloadUrl = "https://stub.test/old.bin"
        }));
        Assert.Equal(WorkIntent.Suppressed, Assert.Single(store.LoadResources()).Intent);
        Assert.Equal(2, removed.Generation);
    }

    [Fact]
    public async Task Media404KeepsDeadlineAndRetrievalAcrossFreshQueueWithoutImmediateRetry()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource resource = store.CommitIntent(new WorkResource
        {
            Url = ThreadUrl, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true
        });
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        ProbeThread firstThread = new(provider.Board, new Uri(ThreadUrl));
        DownloadItem firstItem = new(new Uri("https://stub.test/media.bin"), Path.Combine(directory.Path, "media.bin"),
            provider, firstThread, "media");
        DownloadQueue firstQueue = new(1, 1L << 30);
        firstQueue.ConfigureWorkState(store);
        firstQueue.BindWork(firstThread, resource);
        try
        {
            Assert.True(firstQueue.TryEnqueue(firstItem));
            await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).SingleOrDefault()?.Attempts == 1);
            MediaWork failed = Assert.Single(store.LoadMedia(ThreadUrl, 1));
            Assert.Equal(MediaWorkStatus.Waiting, failed.Status);
            Assert.Equal(404, failed.HttpStatus);
            Assert.Equal(1, failed.ConsecutiveNotFound);
            Assert.InRange(failed.DueAt!.Value - failed.LastAttemptAt!.Value,
                TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }
        finally { await firstQueue.StopAllAsync(); }

        ProbeThread restoredThread = new(provider.Board, new Uri(ThreadUrl));
        DownloadItem restoredItem = new(firstItem.DownloadUri, firstItem.DestinationPath, provider, restoredThread, "media");
        DownloadQueue restoredQueue = new(1, 1L << 30);
        restoredQueue.ConfigureWorkState(store);
        restoredQueue.BindWork(restoredThread, resource);
        try
        {
            Assert.False(restoredQueue.TryEnqueue(restoredItem));
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref requests));
            Assert.Equal(firstItem.DownloadUri.AbsoluteUri, Assert.Single(store.LoadMedia(ThreadUrl, 1)).DownloadUrl);
        }
        finally { await restoredQueue.StopAllAsync(); }
    }

    [Fact]
    public async Task TerminalWatchCompletesWithoutManualSuppression()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using StubImageBoard provider = new();
        TerminalProbeThread? observed = null;
        provider.ThreadFactory = uri => observed = new TerminalProbeThread(provider.Board, uri);
        DownloadQueue queue = new(1, 1L << 30);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            workStore: store);
        await registry.AddAsync(ThreadUrl);
        await TestWait.UntilAsync(() => observed?.Checks == 1);
        await TestWait.UntilAsync(() => store.LoadResources().Single().Intent == WorkIntent.Completed);
        Assert.Empty(registry.Threads);
        Assert.Equal(ThreadSourceStateValue.NotFound, store.LoadResources().Single().Source);
        Assert.DoesNotContain(store.LoadResources(), item => item.Intent == WorkIntent.Suppressed);
    }

    [Fact]
    public async Task OldActiveRevisionCannotCommitSuccessForNewPendingRevision()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource resource = store.CommitIntent(new WorkResource
        {
            Url = ThreadUrl, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true
        });
        TaskCompletionSource releaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource oldStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler(async (_, token) =>
        {
            int ordinal = Interlocked.Increment(ref requests);
            if (ordinal == 1)
            {
                oldStarted.TrySetResult();
                await releaseOld.Task.WaitAsync(token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(ordinal == 1 ? [1, 2, 3] : [1, 2, 3, 4]) };
        }));
        ProbeThread firstThread = new(provider.Board, new Uri(ThreadUrl));
        string target = Path.Combine(directory.Path, "revision.bin");
        DownloadItem old = new(new Uri("https://stub.test/old.bin"), target, provider, firstThread, "media", expectedSize: 3);
        DownloadItem latest = new(new Uri("https://stub.test/new.bin"), target, provider, firstThread, "media", expectedSize: 4);
        DownloadQueue firstQueue = new(1, 1L << 30);
        firstQueue.ConfigureWorkState(store);
        firstQueue.BindWork(firstThread, resource);
        try
        {
            Assert.True(firstQueue.TryEnqueue(old));
            await oldStarted.Task.WaitAsync(TestWait.Timeout);
            Assert.True(firstQueue.TryEnqueue(latest));
            firstQueue.SetPaused(true);
            releaseOld.TrySetResult();
            await TestWait.UntilAsync(() => File.Exists(target));
            await TestWait.UntilAsync(() => firstQueue.GetCapacitySnapshot().Active == 0);
            MediaWork pending = Assert.Single(store.LoadMedia(ThreadUrl, 1));
            Assert.Equal(4, pending.ExpectedSize);
            Assert.Equal(MediaWorkStatus.Ready, pending.Status);
            Assert.False(firstThread.IsMediaDownloaded("media"));
        }
        finally { releaseOld.TrySetResult(); await firstQueue.StopAllAsync(); }

        ProbeThread restoredThread = new(provider.Board, new Uri(ThreadUrl));
        DownloadQueue restoredQueue = new(1, 1L << 30);
        restoredQueue.ConfigureWorkState(store);
        restoredQueue.BindWork(restoredThread, resource);
        try
        {
            await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).Single().Status == MediaWorkStatus.Succeeded);
            Assert.Equal(2, Volatile.Read(ref requests));
            MediaCommitResult? verified = await new MediaStore().TryRestoreAsync(
                new DownloadItem(latest.DownloadUri, target, provider, restoredThread, "media", expectedSize: 4),
                CancellationToken.None);
            Assert.NotNull(verified);
            Assert.Equal(4, verified.Length);
        }
        finally { await restoredQueue.StopAllAsync(); }
    }

    [Fact]
    public async Task ArchivedFinalPayloadRestoresCaptureAndMediaWithoutThreadEndpoint()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        const string url = "https://boards.4chan.org/g/thread/123";
        string target = Path.Combine(directory.Path, "final-media.bin");
        WorkResource intent = work.CommitIntent(new WorkResource
        {
            Url = url, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true
        });
        CapturedThreadSnapshot final = new(new ResourceId("4chan", "g", "123"), url, "Final",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
            [new CapturedPost("123", 0, PostFieldPresence.All, Attachments:
                [new CapturedAttachment(0, "900", Url: "https://i.4cdn.org/g/900.jpg",
                    PlannedPath: target, ExpectedSize: 4)])]);
        DateTimeOffset firstEnd = DateTimeOffset.UtcNow.AddMinutes(-2);
        work.CommitEnd(url, 1, ThreadSourceStateValue.Archived, firstEnd, JsonSerializer.Serialize(final));
        SessionStore sessions = new(directory.Path, lease);
        await sessions.SaveAsync(new SessionSnapshot
        {
            ThreadUrls = [url], DownloadDirectories = new() { [url] = directory.Path }
        });
        int endpoints = 0, mediaRequests = 0;
        using HttpClient client = new(new DelegateHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/thread/", StringComparison.Ordinal))
            { Interlocked.Increment(ref endpoints); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
            Interlocked.Increment(ref mediaRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        }));
        FourChanImageBoard provider = new(client);
        DownloadQueue queue = new(1, 1L << 30);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            sessions, workStore: work);
        await registry.RestoreAsync();
        await TestWait.UntilAsync(() => work.LoadMedia(url, 1).SingleOrDefault()?.Status == MediaWorkStatus.Succeeded);
        await TestWait.UntilAsync(() => work.LoadResources().Single().Intent == WorkIntent.Completed);
        Assert.Equal(0, Volatile.Read(ref endpoints));
        Assert.Equal(1, Volatile.Read(ref mediaRequests));
        Assert.Equal(firstEnd, work.LoadResources().Single().EndObservedAt);
        Assert.Equal(4, new FileInfo(target).Length);
    }

    [Fact]
    public async Task UnchangedCatalogCannotReobserveAutomaticallyCompletedThread()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        MemoryWorkStateStore work = new();
        int created = 0;
        provider.ThreadFactory = uri => { Interlocked.Increment(ref created); return new TerminalProbeThread(provider.Board, uri); };
        provider.Board.OnCheck = () => Task.FromResult<IEnumerable<IThread>>([provider.GetThread(new Uri(ThreadUrl))]);
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30),
            directory.Path, TimeSpan.FromMinutes(1), 60, workStore: work);
        await registry.AddAsync("https://stub.test/g/catalog");
        await TestWait.UntilAsync(() => work.LoadResources().Any(item => item.Url == ThreadUrl && item.Intent == WorkIntent.Completed));
        Assert.Empty(registry.Threads);
        WorkResource completed = work.LoadResources().Single(item => item.Url == ThreadUrl);
        provider.Board.Clock.Advance();
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits >= 2);
        Assert.Empty(registry.Threads);
        Assert.Equal(completed.Generation, work.LoadResources().Single(item => item.Url == ThreadUrl).Generation);
    }

    [Fact]
    public async Task FailedCompletedRepairReturnsToCompletedWithHonestFailure()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SeedCompletedMissing(work, directory.Path);
        SessionStore sessions = new(directory.Path, lease);
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }));
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30),
            directory.Path, TimeSpan.FromMinutes(1), 60, sessions, workStore: work);
        await registry.RestoreAsync();
        Assert.Equal(1, await registry.RetryCompletedMediaAsync(ThreadUrl));
        await TestWait.UntilAsync(() => work.LoadResources().Single().Intent == WorkIntent.Completed
            && work.LoadMedia(ThreadUrl, 1).Single().Status == MediaWorkStatus.NeedsIntervention);
        Assert.Equal(1, Volatile.Read(ref requests));
        Assert.Empty(registry.Threads);
        Assert.Equal(403, work.LoadMedia(ThreadUrl, 1).Single().HttpStatus);
    }

    [Fact]
    public async Task PausedCompletedRepairResumesAfterRestartWithoutEndpoint()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SeedCompletedMissing(work, directory.Path);
        SessionStore sessions = new(directory.Path, lease);
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        DownloadQueue pausedQueue = new(1, 1L << 30);
        pausedQueue.SetPaused(true);
        await using (Registry first = new([provider], pausedQueue, directory.Path,
            TimeSpan.FromMinutes(1), 60, sessions, workStore: work))
        {
            await first.RestoreAsync();
            Assert.Equal(1, await first.RetryCompletedMediaAsync(ThreadUrl));
            Assert.Equal(0, Volatile.Read(ref requests));
            Assert.Equal(WorkIntent.Repairing, work.LoadResources().Single().Intent);
        }
        await using (Registry second = new([provider], new DownloadQueue(1, 1L << 30),
            directory.Path, TimeSpan.FromMinutes(1), 60, sessions, workStore: work))
        {
            await second.RestoreAsync();
            await TestWait.UntilAsync(() => work.LoadMedia(ThreadUrl, 1).Single().Status == MediaWorkStatus.Succeeded);
            await TestWait.UntilAsync(() => work.LoadResources().Single().Intent == WorkIntent.Completed);
            Assert.Equal(1, Volatile.Read(ref requests));
            await TestWait.UntilAsync(() => second.Threads.Count == 0);
            Assert.Empty(second.Threads);
        }
    }

    [Fact]
    public async Task ExpiredTerminalDeadlineCompletesAfterRestartWithoutFinalRequest()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        DateTimeOffset ended = DateTimeOffset.UtcNow.AddHours(-13);
        work.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true,
            Source = ThreadSourceStateValue.NotFound, EndObservedAt = ended });
        work.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "missing",
            DestinationPath = Path.Combine(directory.Path, "missing.bin"), DownloadUrl = "https://stub.test/media.bin",
            Status = MediaWorkStatus.Waiting, HttpStatus = 404, Failure = "Not found",
            Attempts = 5, ConsecutiveNotFound = 5, DueAt = DateTimeOffset.UtcNow.AddHours(2) });
        SessionStore sessions = new(directory.Path, lease);
        await sessions.SaveAsync(new SessionSnapshot { ThreadUrls = [ThreadUrl],
            DownloadDirectories = new() { [ThreadUrl] = directory.Path } });
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30),
            directory.Path, TimeSpan.FromMinutes(1), 60, sessions, workStore: work);
        await registry.RestoreAsync();
        await TestWait.UntilAsync(() => work.LoadResources().Single().Intent == WorkIntent.Completed);
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal(ended, work.LoadResources().Single().EndObservedAt);
        Assert.Equal(MediaWorkStatus.Expired, work.LoadMedia(ThreadUrl, 1).Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full404LadderSurvivesPollsAndUsesOneOuterAttempt(bool durable)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        IWorkStateStore store = durable ? new WorkStateStore(directory.Path, lease) : new MemoryWorkStateStore();
        ManualWorkTime clock = new();
        WorkResource owner = store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        ProbeThread thread = new(provider.Board, new Uri(ThreadUrl));
        DownloadItem item = new(new Uri("https://stub.test/media.bin"), Path.Combine(directory.Path, "missing.bin"), provider, thread, "missing");
        DownloadQueue queue = new(1, 1L << 30, timeProvider: clock);
        queue.ConfigureWorkState(store);
        queue.BindWork(thread, owner);
        try
        {
            Assert.True(queue.TryEnqueue(item));
            int[] minutes = [15, 30, 60, 120, 240, 240];
            for (int i = 0; i < minutes.Length; i++)
            {
                int expected = i + 1;
                await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).SingleOrDefault()?.Attempts == expected
                    && queue.GetCapacitySnapshot().Active == 0);
                MediaWork failed = Assert.Single(store.LoadMedia(ThreadUrl, 1));
                Assert.Equal(expected, requests);
                Assert.Equal(MediaWorkStatus.Waiting, failed.Status);
                Assert.Equal(TimeSpan.FromMinutes(minutes[i]), failed.DueAt - failed.LastAttemptAt);
                Assert.False(queue.TryEnqueue(item));
                await clock.WaitForTimerAtAsync(failed.DueAt!.Value - DateTimeOffset.UnixEpoch);
                if (i + 1 < minutes.Length) clock.Advance(TimeSpan.FromMinutes(minutes[i]));
            }
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task TransientBudgetStopsAtThreeAndManualClicksCoalesceUnderPauseAndActiveTransfer()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        ManualWorkTime clock = new();
        WorkResource owner = store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        TaskCompletionSource active = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref requests) == 4)
            {
                active.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            throw new HttpRequestException("temporary network error");
        }));
        ProbeThread thread = new(provider.Board, new Uri(ThreadUrl));
        DownloadItem item = new(new Uri("https://stub.test/media.bin"), Path.Combine(directory.Path, "missing.bin"), provider, thread, "missing");
        DownloadQueue queue = new(1, 1L << 30, timeProvider: clock);
        queue.ConfigureWorkState(store);
        queue.BindWork(thread, owner);
        try
        {
            Assert.True(queue.TryEnqueue(item));
            foreach (int attempt in new[] { 1, 2, 3 })
            {
                await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).SingleOrDefault()?.Attempts == attempt
                    && queue.GetCapacitySnapshot().Active == 0);
                MediaWork failed = store.LoadMedia(ThreadUrl, 1).Single();
                Assert.False(queue.TryEnqueue(item));
                if (attempt < 3)
                {
                    Assert.Equal(TimeSpan.FromMinutes(attempt == 1 ? 1 : 5), failed.DueAt - failed.LastAttemptAt);
                    await clock.WaitForTimerAtAsync(failed.DueAt!.Value - DateTimeOffset.UnixEpoch);
                    clock.Advance(TimeSpan.FromMinutes(attempt == 1 ? 1 : 5));
                }
                else Assert.Equal(MediaWorkStatus.NeedsIntervention, failed.Status);
            }
            queue.SetPaused(true);
            Assert.Equal(1, queue.RequestMediaRetry(thread));
            Assert.Equal(0, queue.RequestMediaRetry(thread));
            Assert.Equal(3, requests);
            queue.SetPaused(false);
            await active.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(0, queue.RequestMediaRetry(thread));
            release.TrySetResult();
            await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).Single().Attempts == 4
                && queue.GetCapacitySnapshot().Active == 0);
            Assert.Equal(MediaWorkStatus.NeedsIntervention, store.LoadMedia(ThreadUrl, 1).Single().Status);
            Assert.Null(store.LoadMedia(ThreadUrl, 1).Single().DueAt);
            Assert.Equal(4, requests);
        }
        finally { release.TrySetResult(); await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task ExplicitAddWithoutSessionRestoreRetainsDurableTargetRoot()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore work = new();
        string previousRoot = Path.Combine(directory.Path, "original");
        work.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = previousRoot, Generation = 7,
            Intent = WorkIntent.Completed, IsThread = true, Source = ThreadSourceStateValue.NotFound });
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30),
            Path.Combine(directory.Path, "new-default"), TimeSpan.FromMinutes(1), 60, persistSession: false, workStore: work);
        await registry.AddAsync(ThreadUrl);
        WorkResource admitted = Assert.Single(work.LoadResources());
        Assert.Equal(previousRoot, admitted.OutputRoot);
        Assert.Equal(8, admitted.Generation);
        Assert.Equal(ThreadSourceStateValue.Active, admitted.Source);
    }

    [Fact]
    public async Task Last404WaitRemainsWaitingUntilActualDeadlineWithoutFinalRequest()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        ManualWorkTime clock = new();
        WorkResource owner = store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true,
            Source = ThreadSourceStateValue.NotFound, EndObservedAt = clock.GetUtcNow().AddHours(-12).AddMinutes(10) });
        store.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "missing",
            DestinationPath = Path.Combine(directory.Path, "missing.bin"), DownloadUrl = "https://stub.test/media.bin",
            Status = MediaWorkStatus.Waiting, HttpStatus = 404, Attempts = 1, ConsecutiveNotFound = 1,
            DueAt = clock.GetUtcNow().AddMinutes(15) });
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        ProbeThread thread = new(provider.Board, new Uri(ThreadUrl));
        DownloadQueue queue = new(1, 1L << 30, timeProvider: clock);
        queue.ConfigureWorkState(store);
        queue.BindWork(thread, owner);
        try
        {
            await clock.WaitForTimerAtAsync(TimeSpan.FromMinutes(10));
            clock.Advance(TimeSpan.FromMinutes(9));
            Assert.Equal(MediaWorkStatus.Waiting, store.LoadMedia(ThreadUrl, 1).Single().Status);
            clock.Advance(TimeSpan.FromMinutes(1));
            await TestWait.UntilAsync(() => store.LoadMedia(ThreadUrl, 1).Single().Status == MediaWorkStatus.Expired);
            Assert.Equal(0, requests);
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task AuthorityCommitFailureCannotActivateOrRemoveAWatch()
    {
        using TempDirectory directory = new();
        FailingIntentStore store = new();
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), directory.Path,
            TimeSpan.FromMinutes(1), 60, workStore: store);
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync(ThreadUrl));
        Assert.Empty(registry.Threads);
        Assert.Empty(store.LoadResources());
        store.Fail = false;
        IThread thread = (await registry.AddAsync(ThreadUrl)).Thread!;
        store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => registry.RemoveThreadAsync(thread));
        Assert.Same(thread, Assert.Single(registry.Threads));
        Assert.Equal(WorkIntent.Watching, Assert.Single(store.LoadResources()).Intent);
        store.Fail = false;
        await registry.RemoveThreadAsync(thread);
        Assert.Empty(registry.Threads);
        Assert.Equal(WorkIntent.Suppressed, Assert.Single(store.LoadResources()).Intent);
    }

    [Fact]
    public async Task FailedCompletionCommitStaysVisibleWithoutSelfTriggeringRetryLoop()
    {
        using TempDirectory directory = new();
        FailingIntentStore store = new() { FailCompletion = true };
        using StubImageBoard provider = new();
        provider.ThreadFactory = uri => new TerminalProbeThread(provider.Board, uri);
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), directory.Path,
            TimeSpan.FromMinutes(1), 60, workStore: store);
        await registry.AddAsync(ThreadUrl);
        await TestWait.UntilAsync(() => registry.GetSnapshot().Warnings.Any(warning => warning.Contains("Completion could not be committed")));
        // The provider's final check notification may arrive after the first failed scan.
        // Stop the provider, allow those finite notifications to drain, then require a stable count.
        await Assert.Single(registry.Threads).StopMonitoringAsync();
        await Task.Delay(50);
        int settledAttempts = Volatile.Read(ref store.CompletionAttempts);
        Assert.InRange(settledAttempts, 1, 4);
        await Task.Delay(100);
        Assert.Equal(settledAttempts, Volatile.Read(ref store.CompletionAttempts));
        Assert.Single(registry.Threads);
        Assert.Equal(WorkIntent.Watching, Assert.Single(store.LoadResources()).Intent);
        Assert.NotNull(Assert.Single(store.LoadResources()).EndObservedAt);
    }

    private sealed class FailingIntentStore : IWorkStateStore
    {
        private readonly MemoryWorkStateStore _inner = new();
        public bool Fail { get; set; }
        public bool FailCompletion { get; set; }
        public int CompletionAttempts;
        public IReadOnlyList<WorkResource> LoadResources() => _inner.LoadResources();
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => Fail ? throw new IOException("Intent commit failed") : _inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
            DateTimeOffset observedAt, string? finalSnapshotJson = null) => _inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        public WorkResource? CommitState(WorkResource resource)
        {
            if (resource.Intent == WorkIntent.Completed)
            {
                Interlocked.Increment(ref CompletionAttempts);
                if (FailCompletion) throw new IOException("Completion commit failed");
            }
            return _inner.CommitState(resource);
        }
        public bool CommitMedia(MediaWork media) => _inner.CommitMedia(media);
    }

    [Fact]
    public async Task RemovedWatchCannotEndItsReplacementGeneration()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore store = new();
        using StubImageBoard provider = new();
        provider.ThreadFactory = uri => new EndCallbackProbe(provider.Board, uri);
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), directory.Path,
            TimeSpan.FromMinutes(1), 60, workStore: store);
        EndCallbackProbe old = Assert.IsType<EndCallbackProbe>((await registry.AddAsync(ThreadUrl)).Thread);
        await registry.RemoveThreadAsync(old);
        IThread replacement = (await registry.AddAsync(ThreadUrl)).Thread!;
        await old.PublishLateEndAsync();
        WorkResource state = Assert.Single(store.LoadResources());
        Assert.Equal(3, state.Generation);
        Assert.Equal(ThreadSourceStateValue.Active, state.Source);
        Assert.Null(state.EndObservedAt);
        Assert.Same(replacement, Assert.Single(registry.Threads));
    }

    private sealed class EndCallbackProbe(IBoard board, Uri uri) : ProbeThread(board, uri)
    {
        public Task PublishLateEndAsync() => ObserveSourceEndAsync(ThreadSourceState.NotFound);
    }

    [Fact]
    public async Task WorkAuthorityRestoresTerminalMediaWithoutAnySessionDocument()
    {
        using TempDirectory directory = new();
        MemoryWorkStateStore work = new();
        work.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.NotFound,
            EndObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        work.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "final",
            DestinationPath = Path.Combine(directory.Path, "final.bin"), DownloadUrl = "https://stub.test/media.bin",
            Status = MediaWorkStatus.Ready, ExpectedSize = 3 });
        int requests = 0;
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        await using Registry registry = new([provider], new DownloadQueue(1, 1L << 30), directory.Path,
            TimeSpan.FromMinutes(1), 60, workStore: work);
        await registry.RestoreAsync();
        await registry.RestoreAsync();
        await TestWait.UntilAsync(() => work.LoadResources().Single().Intent == WorkIntent.Completed);
        Assert.Equal(1, requests);
        Assert.Equal(MediaWorkStatus.Succeeded, work.LoadMedia(ThreadUrl, 1).Single().Status);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("root")]
    [InlineData("url")]
    [InlineData("key")]
    public void CorruptResourcePayloadCannotMasqueradeAsAuthoritativeWork(string corruption)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource resource = store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        WorkResource corrupt = corruption switch
        {
            "intent" => resource with { Intent = (WorkIntent)99 },
            "source" => resource with { Source = (ThreadSourceStateValue)99 },
            "generation" => resource with { Generation = -1 },
            "root" => resource with { OutputRoot = "relative" },
            "url" => resource with { Url = "invalid" },
            _ => resource with { Url = "https://stub.test/g/thread/456" }
        };
        ReplacePayload(directory.Path, "work_resources", corrupt);
        Assert.Throws<InvalidDataException>(() => store.LoadResources());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("attempts")]
    [InlineData("notfound")]
    [InlineData("size")]
    [InlineData("http")]
    [InlineData("due")]
    [InlineData("target")]
    [InlineData("url")]
    [InlineData("generation")]
    [InlineData("key")]
    public void CorruptMediaPayloadCannotBecomeAReadyOrCompletedJob(string corruption)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
        MediaWork media = new() { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "media",
            DestinationPath = Path.Combine(directory.Path, "media.bin"), DownloadUrl = "https://stub.test/media.bin" };
        store.CommitMedia(media);
        MediaWork corrupt = corruption switch
        {
            "status" => media with { Status = (MediaWorkStatus)99 },
            "attempts" => media with { Attempts = -1 },
            "notfound" => media with { ConsecutiveNotFound = -1 },
            "size" => media with { ExpectedSize = -1 },
            "http" => media with { HttpStatus = 999 },
            "due" => media with { LastAttemptAt = DateTimeOffset.UtcNow, DueAt = DateTimeOffset.UtcNow.AddHours(-1) },
            "target" => media with { DestinationPath = "relative" },
            "url" => media with { DownloadUrl = "invalid" },
            "generation" => media with { Generation = 2 },
            _ => media with { MediaIdentifier = "other" }
        };
        ReplacePayload(directory.Path, "work_media", corrupt);
        Assert.Throws<InvalidDataException>(() => store.LoadMedia(ThreadUrl, 1));
    }

    private static void ReplacePayload<T>(string directory, string table, T value)
    {
        using SqliteConnection db = new(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "work-state.sqlite") }.ToString());
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE " + table + " SET payload=$payload";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task EventOnlyConsumerSeesRestoredVerifiedMediaAfterWorkCacheRefresh()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Verified media must restore without a network request.")));
        string target = Path.Combine(directory.Path, "verified.bin");
        ProbeThread original = new(provider.Board, new Uri(ThreadUrl));
        DownloadItem item = new(new Uri("https://stub.test/media.bin"), target, provider, original, "verified", expectedSize: 3);
        await using (MemoryStream bytes = new([1, 2, 3]))
            await new MediaStore().CommitAsync(item, bytes, CancellationToken.None);
        using RefreshGateStore store = new();
        store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, IsThread = true, Intent = WorkIntent.Watching });
        store.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "verified",
            DestinationPath = target, DownloadUrl = item.DownloadUri.AbsoluteUri, ExpectedSize = 3, Status = MediaWorkStatus.Ready });
        DownloadQueue queue = new(1, 1L << 30);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromHours(1), 3600, workStore: store);
        await registry.RestoreAsync();
        TaskCompletionSource<ThreadSnapshot> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.StateChanged += () =>
        {
            ThreadSnapshot? snapshot = registry.GetSnapshot().Threads.SingleOrDefault();
            if (snapshot is { CompletedMedia: 1, Work.SavedMedia: 1 }) observed.TrySetResult(snapshot);
        };
        try
        {
            queue.SetPaused(false);
            await store.RefreshBlocked.Task.WaitAsync(TestWait.Timeout);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            // Let the finite queue/thread input notifications drain while the work projection is held back.
            await Task.Delay(50);
            Assert.False(observed.Task.IsCompleted);
            store.ReleaseRefresh();
            ThreadSnapshot fresh = await observed.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(MediaWorkStatus.Succeeded, Assert.Single(fresh.Work!.Media).Status);
            Assert.Equal(1, fresh.Work.SavedMedia);
        }
        finally { store.ReleaseRefresh(); }
    }

    [Theory]
    [InlineData(404, MediaWorkStatus.Waiting)]
    [InlineData(403, MediaWorkStatus.NeedsIntervention)]
    public async Task EventOnlyConsumerSeesMediaFailureAfterWorkCacheRefresh(int status, MediaWorkStatus expected)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        using RefreshGateStore store = new() { GateAfterStatus = expected };
        store.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory.Path,
            Generation = 1, IsThread = true, Intent = WorkIntent.Watching });
        store.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1, MediaIdentifier = "missing",
            DestinationPath = Path.Combine(directory.Path, "missing.bin"), DownloadUrl = "https://stub.test/media.bin",
            Status = MediaWorkStatus.Ready });
        DownloadQueue queue = new(1, 1L << 30);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromHours(1), 3600, workStore: store);
        await registry.RestoreAsync();
        TaskCompletionSource<MediaWork> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.StateChanged += () =>
        {
            MediaWork? media = registry.GetSnapshot().Threads.SingleOrDefault()?.Work?.Media.SingleOrDefault();
            if (media?.Status == expected) observed.TrySetResult(media);
        };
        try
        {
            queue.SetPaused(false);
            await store.RefreshBlocked.Task.WaitAsync(TestWait.Timeout);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            await Task.Delay(50);
            Assert.False(observed.Task.IsCompleted);
            store.ReleaseRefresh();
            MediaWork failure = await observed.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(status, failure.HttpStatus);
            Assert.Equal(1, failure.Attempts);
            Assert.NotNull(failure.Failure);
        }
        finally { store.ReleaseRefresh(); }
    }

    private sealed class RefreshGateStore : IWorkStateStore, IDisposable
    {
        private readonly MemoryWorkStateStore _inner = new();
        private readonly ManualResetEventSlim _release = new();
        private int _resultCommitted;
        public MediaWorkStatus GateAfterStatus { get; init; } = MediaWorkStatus.Succeeded;
        public TaskCompletionSource RefreshBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseRefresh() => _release.Set();
        public IReadOnlyList<WorkResource> LoadResources()
        {
            if (Volatile.Read(ref _resultCommitted) != 0)
            {
                RefreshBlocked.TrySetResult();
                if (!_release.Wait(TestWait.Timeout)) throw new TimeoutException("Work cache refresh was not released.");
            }
            return _inner.LoadResources();
        }
        public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _inner.LoadMedia(url, generation);
        public WorkResource CommitIntent(WorkResource resource) => _inner.CommitIntent(resource);
        public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
            DateTimeOffset observedAt, string? finalSnapshotJson = null) => _inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        public WorkResource? CommitState(WorkResource resource) => _inner.CommitState(resource);
        public bool CommitMedia(MediaWork media)
        {
            bool committed = _inner.CommitMedia(media);
            if (committed && media.Status == GateAfterStatus) Volatile.Write(ref _resultCommitted, 1);
            return committed;
        }
        public void Dispose() => _release.Dispose();
    }

    private static void SeedCompletedMissing(WorkStateStore work, string directory)
    {
        work.CommitIntent(new WorkResource { Url = ThreadUrl, OutputRoot = directory,
            Generation = 1, Intent = WorkIntent.Completed, IsThread = true,
            Source = ThreadSourceStateValue.NotFound, EndObservedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.True(work.CommitMedia(new MediaWork { ResourceUrl = ThreadUrl, Generation = 1,
            MediaIdentifier = "missing", DestinationPath = Path.Combine(directory, "missing.bin"),
            DownloadUrl = "https://stub.test/media.bin", Status = MediaWorkStatus.Expired,
            HttpStatus = 404, Failure = "Not found", ConsecutiveNotFound = 1 }));
    }

    private sealed class TerminalProbeThread(IBoard board, Uri uri) : ProbeThread(board, uri)
    {
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            await ObserveSourceEndAsync(ThreadSourceState.NotFound);
            await CaptureSourceStateAsync(ThreadSourceState.NotFound, cancellationToken);
            await SettleTerminalWorkAsync();
            await base.checkThreadAsync(queue, cancellationToken);
        }
    }
    private sealed class ManualWorkTime : TimeProvider
    {
        private readonly object _sync = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public int PendingTimers { get { lock (_sync) { return _timers.Count; } } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeLocked(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due)
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }

        public Task WaitForTimerAtAsync(TimeSpan elapsed)
        {
            bool observed = false;
            return TestWait.UntilAsync(() =>
            {
                lock (_sync) { return observed |= _timers.Any(timer => timer.Due == elapsed.Ticks); }
            }, $"No worker timer was scheduled for {elapsed}.");
        }

        private sealed class ManualTimer(ManualWorkTime owner, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (_disposed) return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }
            public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period;
            }
            public void Dispose()
            {
                lock (owner._sync) { _disposed = true; owner._timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
