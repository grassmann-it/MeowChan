using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed partial class DownloadQueueTests
{
    private static WorkResource BindPriorityWatch(DownloadQueue queue, WorkStateStore store,
        IThread thread, TempDirectory directory, UserDownloadPriority priority)
    {
        WorkResource owner = store.CommitIntent(new WorkResource
        {
            Url = thread.Url, OutputRoot = directory.Path, Generation = 1,
            Intent = WorkIntent.Watching, IsThread = true
        });
        queue.BindWork(thread, owner);
        Assert.True(queue.SetUserPriority(thread, owner, priority));
        Assert.Equal(priority, queue.GetUserPriority(thread));
        return owner;
    }

    [Fact]
    public async Task UserHighWatchesShareRealDispatchAndNormalGetsFourthContendedReservation()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(1, UnthrottledBandwidth, maxPendingDownloads: 32);
        queue.ConfigureWorkState(store);
        IThread blocker = Thread(board, 100), first = Thread(board, 101), second = Thread(board, 102), normal = Thread(board, 103);
        try
        {
            BindPriorityWatch(queue, store, blocker, directory, UserDownloadPriority.Normal);
            BindPriorityWatch(queue, store, first, directory, UserDownloadPriority.High);
            BindPriorityWatch(queue, store, second, directory, UserDownloadPriority.High);
            BindPriorityWatch(queue, store, normal, directory, UserDownloadPriority.Normal);
            Assert.True(await queue.EnqueueAsync(CreateItem(board, blocker, directory, "block.bin", "block")));
            await handler.WaitForStartsAsync(1);
            // Deliberately cluster the first High watch: FIFO would starve the second.
            foreach ((IThread thread, string prefix) in new[] { (first, "a"), (second, "b"), (normal, "n") })
                for (int i = 0; i < 6; i++)
                    Assert.True(await queue.EnqueueAsync(CreateItem(board, thread, directory, $"{prefix}{i}.bin", $"{prefix}{i}")));
            for (int i = 0; i < 18; i++)
            {
                handler.Release(1);
                await handler.WaitForStartsAsync(1);
            }
            string[] turns = handler.StartedUris.Skip(1).Select(uri => Path.GetFileNameWithoutExtension(uri.AbsolutePath)).ToArray();
            Assert.Equal(new[] { "a0", "b0", "a1", "n0" }, turns.Take(4));
            Assert.Equal(new[] { "a", "b", "a", "b", "a", "b", "a", "b", "a", "b", "a", "b" },
                turns.Where(name => !name.StartsWith('n')).Select(name => name[..1]));
            int consecutiveHigh = 0;
            foreach (string turn in turns.TakeWhile(name => name != "n5"))
            {
                consecutiveHigh = turn.StartsWith('n') ? 0 : consecutiveHigh + 1;
                Assert.InRange(consecutiveHigh, 0, 3);
            }
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(19, handler.RequestCount);
            Assert.Equal(0, handler.CancellationCount);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task UserPriorityChangesDoNotPreemptActiveTransferOrResumeQueuedPausedWork()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(1, UnthrottledBandwidth, maxPendingDownloads: 4);
        queue.ConfigureWorkState(store);
        IThread active = Thread(board, 110), waiting = Thread(board, 111);
        try
        {
            WorkResource activeOwner = BindPriorityWatch(queue, store, active, directory, UserDownloadPriority.High);
            WorkResource waitingOwner = BindPriorityWatch(queue, store, waiting, directory, UserDownloadPriority.Normal);
            Assert.True(await queue.EnqueueAsync(CreateItem(board, active, directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            Assert.True(await queue.EnqueueAsync(CreateItem(board, waiting, directory, "waiting.bin", "waiting")));
            Assert.True(queue.SetUserPriority(waiting, waitingOwner, UserDownloadPriority.High));
            Assert.True(queue.SetUserPriority(active, activeOwner, UserDownloadPriority.Normal));
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(1, handler.ActiveRequests);
            Assert.Equal(0, handler.CancellationCount);
            handler.Release(1);
            await handler.WaitForStartsAsync(1);
            Assert.True(active.IsMediaDownloaded("active"));
            await queue.SetPausedAsync(true).WaitAsync(TestWait.Timeout);
            int starts = handler.RequestCount;
            Assert.True(queue.SetUserPriority(waiting, waitingOwner, UserDownloadPriority.Normal));
            Assert.True(queue.SetUserPriority(waiting, waitingOwner, UserDownloadPriority.High));
            Assert.True(queue.IsPaused);
            Assert.Equal(0, handler.ActiveRequests);
            Assert.Equal(starts, handler.RequestCount);
            Assert.Equal(1, handler.CancellationCount);
            await queue.SetPausedAsync(false);
            await handler.WaitForStartsAsync(1);
            handler.Release(1);
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(waiting.IsMediaDownloaded("waiting"));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserNormalAdmissionIsBoundedByThreeHighAdmissionsWithStorageReadyContenders(bool activeSuccessor)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using GatedMediaHandler handler = new();
        using StubImageBoard board = new(handler);
        DownloadQueue queue = new(1, UnthrottledBandwidth, maxPendingDownloads: 1);
        queue.ConfigureWorkState(store);
        IThread active = Thread(board, 120), high = Thread(board, 121), normal = Thread(board, 122);
        List<Task<bool>> admissions = new();
        try
        {
            BindPriorityWatch(queue, store, active, directory, UserDownloadPriority.Normal);
            BindPriorityWatch(queue, store, high, directory, UserDownloadPriority.High);
            BindPriorityWatch(queue, store, normal, directory, UserDownloadPriority.Normal);
            Assert.True(await queue.EnqueueAsync(CreateItem(board, active, directory, "active.bin", "active")));
            await handler.WaitForStartsAsync(1);
            // A changed URI for the same job reserves an active successor, not a
            // second worker. It must consume the same bounded pending budget.
            DownloadItem pending = activeSuccessor
                ? CreateItem(board, active, directory, "active.bin", "active", new Uri("https://stub.test/media/revision.bin"))
                : CreateItem(board, active, directory, "pending.bin", "pending");
            Assert.True(await queue.EnqueueAsync(pending));
            Assert.Equal((1, 1, 0), queue.GetCapacitySnapshot());
            for (int i = 0; i < 6; i++)
                admissions.Add(queue.EnqueueAsync(CreateItem(board, high, directory, $"h{i}.bin", $"h{i}")));
            Task<bool> normalAdmission = queue.EnqueueAsync(CreateItem(board, normal, directory, "normal.bin", "normal"));
            admissions.Add(normalAdmission);
            await TestWait.UntilAsync(() => AdmissionTargetsReady(queue, 7));
            for (int reservation = 1; reservation <= 4; reservation++)
            {
                handler.Release(1);
                await handler.WaitForStartsAsync(1);
                await TestWait.UntilAsync(() => admissions.Count(task => task.IsCompletedSuccessfully) == reservation);
                Assert.Equal(Math.Min(reservation, 3), admissions.Take(6).Count(task => task.IsCompletedSuccessfully));
                Assert.Equal(reservation == 4, normalAdmission.IsCompletedSuccessfully);
                Assert.InRange(queue.GetCapacitySnapshot().Pending, 0, 1);
            }
            Assert.True(await normalAdmission);
            handler.Release(16);
            Assert.All(await Task.WhenAll(admissions).WaitAsync(TestWait.Timeout), accepted => Assert.True(accepted));
            await queue.WaitForIdleAsync().WaitAsync(TestWait.Timeout);
            Assert.True(normal.IsMediaDownloaded("normal"));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }
}
