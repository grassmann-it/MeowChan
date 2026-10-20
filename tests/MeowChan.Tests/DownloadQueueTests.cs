using System.Net;
using System.Threading.Channels;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class DownloadQueueTests
{
    private const long UnthrottledBandwidth = 1L << 30;
    private static readonly byte[] Payload = [1, 3, 5, 7, 9];

    [Fact]
    public async Task PauseCancelsActiveTransferAndBoundsPendingAdmissionUntilResume()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(3, UnthrottledBandwidth, maxPendingDownloads: 1);
        DownloadItem first = CreateItem(board, Thread(board, 1), directory, "1/media.bin", "one");
        DownloadItem second = CreateItem(board, Thread(board, 2), directory, "2/media.bin", "two");
        DownloadItem third = CreateItem(board, Thread(board, 3), directory, "3/media.bin", "three");
        try
        {
            Assert.True(queue.TryEnqueue(first));
            await handler.WaitForStartsAsync(1);
            await queue.SetPausedAsync(true);
            Assert.Equal(0, handler.ActiveRequests);
            Assert.False(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.False(File.Exists(first.DestinationPath));
            Assert.True(queue.TryEnqueue(second));
            Assert.False(queue.TryEnqueue(third));
            Task<bool> admission = queue.EnqueueAsync(third);
            Assert.False(admission.IsCompleted);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, queue.GetCapacitySnapshot().Pending);
            Assert.False(admission.IsCompleted);
            queue.SetPaused(false);
            Assert.True(await admission.WaitAsync(TestWait.Timeout));
            await handler.WaitForStartsAsync(3);
            handler.Release(3);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(4, handler.RequestCount);
            Assert.True(first.Thread.IsMediaDownloaded(first.MediaIdentifier));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task PauseWaitsForPhysicalCancellationAndResumeCannotOvertakeIt()
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new(delayCancellationCompletion: true);
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "one");
        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            Task pause = queue.SetPausedAsync(true);
            await handler.WaitForCancellationAsync();
            Assert.False(pause.IsCompleted);
            Task resume = queue.SetPausedAsync(false);
            Assert.False(resume.IsCompleted);
            Assert.Equal(1, handler.RequestCount);
            handler.CompleteCancellation();
            await Task.WhenAll(pause, resume).WaitAsync(TestWait.Timeout);
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(1, handler.MaximumActiveRequests);
        }
        finally { handler.CompleteCancellation(); await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task RemovingAnInterruptedReservationPreventsItsRestart()
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "one");
        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            await queue.SetPausedAsync(true).WaitAsync(TestWait.Timeout);
            await queue.CancelDownloadsForThreadAsync(item.Thread).WaitAsync(TestWait.Timeout);
            await queue.SetPausedAsync(false);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, handler.RequestCount);
            Assert.False(File.Exists(item.DestinationPath));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task PausedReservationCannotStartHttpAfterStorageAdmissionIsReleased()
    {
        using TempDirectory directory = new();
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        MediaStore store = new();
        DownloadQueue queue = new(1, UnthrottledBandwidth, mediaStore: store);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "blocked/media.bin", "one");
        DownloadItem seed = CreateItem(board, item.Thread, directory, "blocked/seed.bin", "seed");
        using (MemoryStream payload = new(Payload, writable: false))
            await store.CommitAsync(seed, payload, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".meowchan-manifest.json")));
        Assert.False(File.Exists(item.DestinationPath));
        using FileStream lease = new(Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".meowchan-store.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource probeCancellation = new();
        // This call executes through the real restore-input check into the held file lease.
        // It also holds this store's directory semaphore, gating the queue's restore worker.
        Task<MediaCommitResult?> restoreProbe = store.TryRestoreAsync(item, probeCancellation.Token);
        try
        {
            Assert.False(restoreProbe.IsCompleted);
            Assert.True(queue.TryEnqueue(item));
            Assert.Equal(1, queue.GetCapacitySnapshot().Active);
            await queue.SetPausedAsync(true).WaitAsync(TestWait.Timeout);
            Assert.False(restoreProbe.IsCompleted);
            probeCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoreProbe.WaitAsync(TestWait.Timeout));
            lease.Dispose();
            Assert.Equal(0, handler.RequestCount);
            Assert.Equal(0, queue.GetCapacitySnapshot().Active);
            await queue.SetPausedAsync(false);
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            probeCancellation.Cancel();
            try { await restoreProbe; } catch (OperationCanceledException) { }
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task PauseGateIsCancellationAwareAndStopReleasesWaiters()
    {
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        queue.SetPaused(true);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task cancelled = queue.WaitUntilResumedAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task stopped = queue.WaitUntilResumedAsync();
        await queue.StopAllAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TestWait.Timeout));
    }

    [Fact]
    public async Task OriginalConstructorMetadataRemainsCompatibleAndUsesBoundedDefaults()
    {
        System.Reflection.ConstructorInfo? constructor = typeof(DownloadQueue).GetConstructor(new[]
        {
            typeof(int), typeof(long), typeof(MeowChan.Storage.MediaStore), typeof(TimeSpan?), typeof(TimeProvider), typeof(Serilog.ILogger)
        });
        Assert.NotNull(constructor);
        DownloadQueue queue = (DownloadQueue)constructor.Invoke(new object?[] { 1, UnthrottledBandwidth, null, null, null, null });
        Assert.Equal(256, queue.MaxPendingDownloads);
        await queue.StopAllAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 0));
    }

    [Fact]
    public async Task ManyThreadsShareTheConfiguredWorkerLimit()
    {
        const int workerLimit = 3;
        const int jobCount = 24;
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(workerLimit, UnthrottledBandwidth);
        DownloadItem[] items = Enumerable.Range(1, jobCount)
            .Select(id => CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}"))
            .ToArray();

        try
        {
            bool[] admissions = await Task.WhenAll(items.Select(item => Task.Run(() => queue.TryEnqueue(item))))
                .WaitAsync(TestWait.Timeout);
            Assert.All(admissions, accepted => Assert.True(accepted));

            await handler.WaitForStartsAsync(workerLimit);
            Assert.Equal(workerLimit, handler.ActiveRequests);
            Assert.Equal(workerLimit, handler.RequestCount);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);

            handler.Release(jobCount);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(jobCount, handler.RequestCount);
            Assert.Equal(workerLimit, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.ActiveRequests);
            foreach (DownloadItem item in items)
            {
                Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
                Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            }
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentEquivalentJobsAreAdmittedOnceWhileQueuedOrRunning(bool keepJobQueued)
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread thread = Thread(board, 1);
        DownloadItem item = CreateItem(board, thread, directory, "target/media.bin", "same-medium");

        try
        {
            if (keepJobQueued)
            {
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 2), directory, "blocker/media.bin", "blocker")));
                await handler.WaitForStartsAsync(1);
            }

            TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>[] attempts = Enumerable.Range(0, 32).Select(async attempt =>
            {
                await start.Task;
                // A refreshed URL still represents the same thread/medium/destination job.
                return queue.TryEnqueue(new DownloadItem(
                    new Uri($"https://stub.test/media/shared.bin?attempt={attempt}"),
                    item.DestinationPath, board, thread, item.MediaIdentifier));
            }).ToArray();
            start.SetResult();

            bool[] admissions = await Task.WhenAll(attempts).WaitAsync(TestWait.Timeout);
            Assert.Equal(1, admissions.Count(accepted => accepted));
            Assert.False(queue.TryEnqueue(item));

            if (keepJobQueued)
            {
                Assert.Equal(1, handler.RequestCount);
                handler.Release(1);
            }

            await handler.WaitForStartsAsync(1);
            Assert.False(queue.TryEnqueue(item));
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(keepJobQueued ? 2 : 1, handler.RequestCount);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task TheSameMediaUriCanBeDownloadedToSeparateLegitimateDestinations(bool useDifferentThread,
        int workerLimit)
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(workerLimit, UnthrottledBandwidth);
        IThread firstThread = Thread(board, 1);
        IThread secondThread = useDifferentThread ? Thread(board, 2) : firstThread;
        Uri sharedUri = new Uri("https://stub.test/media/shared.bin");
        DownloadItem first = CreateItem(board, firstThread, directory, "first/media.bin", "same-medium", sharedUri);
        DownloadItem second = CreateItem(board, secondThread, directory, "second/media.bin", "same-medium", sharedUri);

        try
        {
            Assert.True(queue.TryEnqueue(first));
            Assert.True(queue.TryEnqueue(second));
            if (workerLimit == 1)
            {
                await handler.WaitForStartsAsync(1);
                handler.Release(1);
                await handler.WaitForStartsAsync(1);
                Assert.True(firstThread.IsMediaDownloaded(first.MediaIdentifier));
                handler.Release(1);
            }
            else
            {
                await handler.WaitForStartsAsync(2);
                handler.Release(2);
            }
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(first.DestinationPath));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(second.DestinationPath));
            Assert.True(firstThread.IsMediaDownloaded(first.MediaIdentifier));
            Assert.True(secondThread.IsMediaDownloaded(second.MediaIdentifier));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task CompletedJobsReleaseTheirReservation()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "medium");

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            Assert.False(queue.TryEnqueue(item));
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.True(queue.TryEnqueue(item));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            // Reservation lifetime ends with the job; the worker may reuse its completed file.
            Assert.Equal(1, handler.RequestCount);
            Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task FailedJobsReleaseTheirReservationForAnotherAttempt()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(HttpStatusCode.ServiceUnavailable);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem item = CreateItem(board, Thread(board, 1), directory, "media.bin", "medium");

        try
        {
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.False(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.True(queue.TryEnqueue(item));
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ThreadCancellationRejectsLateJobsAndKeepsItsIdentityUntilTheWorkerFinishes()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(delayCancellationCompletion: true);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        IThread oldThread = Thread(board, 1);
        IThread newThread = Thread(board, 1);
        DownloadItem active = CreateItem(board, oldThread, directory, "old/active.bin", "active");
        DownloadItem pending = CreateItem(board, oldThread, directory, "old/pending.bin", "pending");
        DownloadItem unrelated = CreateItem(board, Thread(board, 2), directory, "other/media.bin", "other");
        DownloadItem replacement = CreateItem(board, newThread, directory, "old/active.bin", "active");
        DownloadItem newGenerationJob = CreateItem(board, newThread, directory, "new/media.bin", "new-medium");
        DownloadItem lateOldJob = CreateItem(board, oldThread, directory, "old/late.bin", "late");

        try
        {
            Assert.True(queue.TryEnqueue(active));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(pending));
            Assert.True(queue.TryEnqueue(unrelated));

            Task cancellation = queue.CancelDownloadsForThreadAsync(oldThread);
            await handler.WaitForCancellationAsync();

            Assert.False(cancellation.IsCompleted);
            Assert.False(queue.TryEnqueue(lateOldJob));
            Assert.False(queue.TryEnqueue(replacement));
            Assert.False(queue.TryEnqueue(newGenerationJob));
            Assert.Equal(1, handler.RequestCount);

            handler.CompleteCancellation();
            await cancellation.WaitAsync(TestWait.Timeout);
            await handler.WaitForStartsAsync(1);

            Assert.True(queue.TryEnqueue(replacement));
            Assert.False(queue.TryEnqueue(lateOldJob));
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);

            Assert.Equal(1, handler.CancellationCount);
            Assert.Equal(3, handler.RequestCount);
            Assert.False(File.Exists(pending.DestinationPath));
            Assert.False(oldThread.IsMediaDownloaded(active.MediaIdentifier));
            Assert.True(newThread.IsMediaDownloaded(replacement.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(unrelated.DestinationPath));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(replacement.DestinationPath));
        }
        finally
        {
            handler.CompleteCancellation();
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task RepeatedStopCallsAwaitTheSameOperationAndCloseAdmission()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler(delayCancellationCompletion: true);
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth);
        DownloadItem active = CreateItem(board, Thread(board, 1), directory, "active.bin", "active");
        DownloadItem pending = CreateItem(board, Thread(board, 2), directory, "pending.bin", "pending");

        try
        {
            Assert.True(queue.TryEnqueue(active));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(pending));

            Task firstStop = queue.StopAllAsync();
            await handler.WaitForCancellationAsync();
            Task secondStop = queue.StopAllAsync();

            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            Assert.False(queue.WaitForIdleAsync().IsCompleted);
            Assert.False(queue.TryEnqueue(active));
            Assert.False(queue.TryEnqueue(CreateItem(board, Thread(board, 3), directory, "late.bin", "late")));

            handler.CompleteCancellation();
            await Task.WhenAll(firstStop, secondStop).WaitAsync(TestWait.Timeout);

            Assert.Same(firstStop, queue.StopAllAsync());
            Assert.True(queue.WaitForIdleAsync().IsCompletedSuccessfully);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, handler.CancellationCount);
            Assert.Equal(0, handler.ActiveRequests);
            Assert.False(File.Exists(pending.DestinationPath));
            Assert.False(queue.TryEnqueue(pending));
        }
        finally
        {
            handler.CompleteCancellation();
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ResizingAnIdleQueueUsesTheFinalWorkerLimit()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(5, UnthrottledBandwidth);

        try
        {
            foreach (int workerLimit in new int[] { 5, 3, 6 })
            {
                queue.UpdateMaxParallelDownloads(workerLimit);
                Assert.Equal(workerLimit, queue.MaxParallelDownloads);
            }

            foreach (int id in Enumerable.Range(1, 6))
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}")));

            await handler.WaitForStartsAsync(6);
            Assert.Equal(6, handler.ActiveRequests);
            handler.Release(6);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(6, handler.MaximumActiveRequests);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ReducingTheWorkerLimitWaitsForActiveJobsBeforeStartingMore()
    {
        const int jobCount = 8;
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(5, UnthrottledBandwidth);

        try
        {
            foreach (int id in Enumerable.Range(1, jobCount))
                Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, id), directory, $"{id}/media.bin", $"media-{id}")));
            await handler.WaitForStartsAsync(5);

            queue.UpdateMaxParallelDownloads(2);
            Assert.Equal(2, queue.MaxParallelDownloads);
            Assert.Equal(5, handler.ActiveRequests);

            for (int remainingActive = 4; remainingActive >= 2; remainingActive--)
            {
                handler.Release(1);
                Uri completedUri = await handler.WaitForReturnAsync();
                await TestWait.UntilAsync(() => !queue.IsInQueue(completedUri.ToString()));
                Assert.Equal(remainingActive, handler.ActiveRequests);
                Assert.Equal(5, handler.RequestCount);
                Assert.Equal(0, handler.CancellationCount);
            }

            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal(2, handler.ActiveRequests);
            Assert.Equal(6, handler.RequestCount);

            handler.Release(jobCount);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(jobCount, handler.RequestCount);
            Assert.Equal(5, handler.MaximumActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task FullCapacityWaitsWithoutAcceptingOrDroppingMediaAndCancellationOnlyEndsTheWait()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 1);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 1), directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 2), directory, "pending.bin", "pending")));
            DownloadItem blocked = CreateItem(board, Thread(board, 3), directory, "blocked.bin", "blocked");
            Assert.False(queue.TryEnqueue(blocked));
            Assert.Throws<InvalidOperationException>(() => queue.EnqueueDownload(blocked));
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            Task<bool> cancelledWait = queue.EnqueueAsync(blocked, cancelled.Token);
            Assert.False(cancelledWait.IsCompleted);
            Assert.Equal((1, 1, 1), queue.GetCapacitySnapshot());
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
            Assert.Equal((1, 1, 0), queue.GetCapacitySnapshot());
            Task<bool> admitted = queue.EnqueueAsync(blocked);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.True(await admitted.WaitAsync(TestWait.Timeout));
            Assert.Equal((1, 1, 0), queue.GetCapacitySnapshot());
            handler.Release(2);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(blocked.DestinationPath));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task StopAndWatchCancellationWakeAdmissionsEvenWhenNoJobForThatWatchWasAdmitted()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 1);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 1), directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 2), directory, "pending.bin", "pending")));
            IThread cancelled = Thread(board, 3);
            Task<bool> first = queue.EnqueueAsync(CreateItem(board, cancelled, directory, "cancelled.bin", "cancelled"));
            Task<bool> second = queue.EnqueueAsync(CreateItem(board, Thread(board, 4), directory, "stopped.bin", "stopped"));
            await queue.CancelDownloadsForThreadAsync(cancelled);
            Assert.False(await first.WaitAsync(TestWait.Timeout));
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            Assert.False(await second.WaitAsync(TestWait.Timeout));
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Assert.Equal(1, handler.RequestCount);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ManualAndBoardWorkAlternateAndSeparateBoardWatchesReceiveTurns()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 4);
        IThread firstBoard = Thread(board, 1);
        IThread secondBoard = Thread(board, 2);
        IThread manual = Thread(board, 3);
        queue.SetThreadPriority(firstBoard, DownloadPriority.BoardDiscovery);
        queue.SetThreadPriority(secondBoard, DownloadPriority.BoardDiscovery);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, firstBoard, directory, "start.bin", "start")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, firstBoard, directory, "board-a.bin", "board-a")));
            Assert.True(queue.TryEnqueue(CreateItem(board, secondBoard, directory, "board-b.bin", "board-b")));
            Assert.True(queue.TryEnqueue(CreateItem(board, manual, directory, "manual-a.bin", "manual-a")));
            Assert.True(queue.TryEnqueue(CreateItem(board, manual, directory, "manual-b.bin", "manual-b")));
            for (int index = 0; index < 4; index++)
            {
                handler.Release(1);
                await handler.WaitForStartsAsync(1);
            }
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(new[] { "start.bin", "manual-a.bin", "board-b.bin", "manual-b.bin", "board-a.bin" },
                handler.StartedUris.Select(uri => Path.GetFileName(uri.AbsolutePath)));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ManualAdmissionGetsATurnAheadOfAnExistingBoardBacklogWithoutStarvingIt()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 1);
        IThread automatic = Thread(board, 1);
        IThread manual = Thread(board, 2);
        queue.SetThreadPriority(automatic, DownloadPriority.BoardDiscovery);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, automatic, directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, automatic, directory, "pending.bin", "pending")));
            Task<bool> boardWait = queue.EnqueueAsync(CreateItem(board, automatic, directory, "board.bin", "board"));
            Task<bool> manualWait = queue.EnqueueAsync(CreateItem(board, manual, directory, "manual.bin", "manual"));
            Assert.False(boardWait.IsCompleted);
            Assert.False(manualWait.IsCompleted);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.True(await manualWait.WaitAsync(TestWait.Timeout));
            Assert.False(boardWait.IsCompleted);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.True(await boardWait.WaitAsync(TestWait.Timeout));
            Assert.Equal("manual.bin", Path.GetFileName(handler.StartedUris.Last().AbsolutePath));
            handler.Release(2);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(automatic.IsMediaDownloaded("board"));
            Assert.True(manual.IsMediaDownloaded("manual"));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task NewlyArrivingWatchesDoNotContinuallyOvertakeOlderWaitingWork()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 3);
        IThread original = Thread(board, 1);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, original, directory, "start.bin", "start")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, original, directory, "older.bin", "older")));
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 2), directory, "new-a.bin", "new-a")));
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal("new-a.bin", Path.GetFileName(handler.StartedUris.Last().AbsolutePath));
            Assert.True(queue.TryEnqueue(CreateItem(board, Thread(board, 3), directory, "new-b.bin", "new-b")));
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal("older.bin", Path.GetFileName(handler.StartedUris.Last().AbsolutePath));
            handler.Release(2);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task PromotingAWatchReclassifiesItsExistingAdmissionWait()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 1);
        IThread active = Thread(board, 1);
        IThread older = Thread(board, 2);
        IThread promoted = Thread(board, 3);
        foreach (IThread thread in new[] { active, older, promoted }) queue.SetThreadPriority(thread, DownloadPriority.BoardDiscovery);
        try
        {
            Assert.True(queue.TryEnqueue(CreateItem(board, active, directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, active, directory, "pending.bin", "pending")));
            Task<bool> first = queue.EnqueueAsync(CreateItem(board, older, directory, "older.bin", "older"));
            Task<bool> second = queue.EnqueueAsync(CreateItem(board, promoted, directory, "promoted.bin", "promoted"));
            queue.SetThreadPriority(promoted, DownloadPriority.Manual);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.True(await second.WaitAsync(TestWait.Timeout));
            Assert.False(first.IsCompleted);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal("promoted.bin", Path.GetFileName(handler.StartedUris.Last().AbsolutePath));
            Assert.True(await first.WaitAsync(TestWait.Timeout));
            handler.Release(2);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task RestoredDiscoveryOriginStillYieldsToManualWork()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        SessionStore store = new SessionStore(directory.Path, lease);
        string discoveredUrl = "https://stub.test/g/thread/1";
        string manualUrl = "https://stub.test/g/thread/2";
        await store.SaveAsync(new SessionSnapshot
        {
            ThreadUrls = [discoveredUrl, manualUrl], DiscoveredThreadUrls = [discoveredUrl],
            DownloadDirectories = new Dictionary<string, string> { [discoveredUrl] = directory.Path, [manualUrl] = directory.Path }
        });
        DownloadQueue queue = new DownloadQueue(1, UnthrottledBandwidth, maxPendingDownloads: 2);
        await using Registry registry = new Registry([board], queue, directory.Path, TimeSpan.FromSeconds(60), 60, store);
        try
        {
            await registry.RestoreAsync();
            IThread automatic = registry.Threads.Single(thread => thread.ThreadId == "1");
            IThread manual = registry.Threads.Single(thread => thread.ThreadId == "2");
            Assert.True(queue.TryEnqueue(CreateItem(board, automatic, directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(queue.TryEnqueue(CreateItem(board, automatic, directory, "board.bin", "board")));
            Assert.True(queue.TryEnqueue(CreateItem(board, manual, directory, "manual.bin", "manual")));
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.Equal("manual.bin", Path.GetFileName(handler.StartedUris.Last().AbsolutePath));
            handler.Release(2);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
        }
        finally { await registry.StopAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task ThirtyTwoProducersRespectWorkerAndPendingBudgetsAndEveryJobCompletes()
    {
        using TempDirectory directory = new TempDirectory();
        GatedMediaHandler handler = new GatedMediaHandler();
        using StubImageBoard board = new StubImageBoard(handler);
        DownloadQueue queue = new DownloadQueue(32, UnthrottledBandwidth, maxPendingDownloads: 8);
        try
        {
            Task[] producers = Enumerable.Range(1, 32).Select(async id =>
            {
                IThread thread = Thread(board, id);
                queue.SetThreadPriority(thread, id % 2 == 0 ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery);
                for (int media = 0; media < 10; media++)
                {
                    Assert.True(await queue.EnqueueAsync(CreateItem(board, thread, directory,
                        $"{id}/{media}.bin", $"{id}-{media}")));
                    (int active, int pending, _) = queue.GetCapacitySnapshot();
                    Assert.InRange(active, 0, 32);
                    Assert.InRange(pending, 0, 8);
                }
            }).ToArray();
            await handler.WaitForStartsAsync(32);
            Assert.Equal(32, handler.ActiveRequests);
            Assert.Equal(8, queue.GetCapacitySnapshot().Pending);
            Assert.True(queue.GetCapacitySnapshot().WaitingAdmission > 0);
            handler.Release(320);
            await Task.WhenAll(producers).WaitAsync(TestWait.Timeout);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(320, handler.RequestCount);
            Assert.Equal(32, handler.MaximumActiveRequests);
            Assert.Equal(320, Directory.GetFiles(directory.Path, "*.bin", SearchOption.AllDirectories).Length);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    private static IThread Thread(StubImageBoard board, int id)
        => board.GetThread(new Uri($"https://stub.test/g/thread/{id}"));

    private static DownloadItem CreateItem(StubImageBoard board, IThread thread, TempDirectory directory,
        string relativePath, string mediaIdentifier, Uri? downloadUri = null)
        => new DownloadItem(downloadUri ?? new Uri($"https://stub.test/media/{mediaIdentifier}.bin"),
            Path.Combine(directory.Path, relativePath), board, thread, mediaIdentifier);

    private sealed class GatedMediaHandler(HttpStatusCode statusCode = HttpStatusCode.OK,
        bool delayCancellationCompletion = false) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _responses = new SemaphoreSlim(0);
        private readonly Channel<bool> _starts = Channel.CreateUnbounded<bool>();
        private readonly Channel<Uri> _returns = Channel.CreateUnbounded<Uri>();
        private readonly TaskCompletionSource _cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completeCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;
        private int _activeRequests;
        private int _maximumActiveRequests;
        private int _cancellationCount;

        public System.Collections.Concurrent.ConcurrentQueue<Uri> StartedUris { get; } = new();
        public int RequestCount => Volatile.Read(ref _requestCount);
        public int ActiveRequests => Volatile.Read(ref _activeRequests);
        public int MaximumActiveRequests => Volatile.Read(ref _maximumActiveRequests);
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public void Release(int count) => _responses.Release(count);
        public void CompleteCancellation() => _completeCancellation.TrySetResult();
        public Task WaitForCancellationAsync() => _cancellationObserved.Task.WaitAsync(TestWait.Timeout);

        public async Task<Uri> WaitForReturnAsync()
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            return await _returns.Reader.ReadAsync(timeout.Token);
        }

        public async Task WaitForStartsAsync(int count)
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TestWait.Timeout);
            for (int i = 0; i < count; i++)
                await _starts.Reader.ReadAsync(timeout.Token);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activeRequests);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumActiveRequests);
                if (observed >= active)
                    break;
            }
            while (Interlocked.CompareExchange(ref _maximumActiveRequests, active, observed) != observed);
            Interlocked.Increment(ref _requestCount);
            StartedUris.Enqueue(request.RequestUri!);
            _starts.Writer.TryWrite(true);

            try
            {
                await _responses.WaitAsync(cancellationToken);
                _returns.Writer.TryWrite(request.RequestUri!);
                return new HttpResponseMessage(statusCode) { Content = new ByteArrayContent(Payload) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancellationCount);
                _cancellationObserved.TrySetResult();
                if (delayCancellationCompletion)
                    await _completeCancellation.Task;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _responses.Dispose();
            base.Dispose(disposing);
        }
    }
}
