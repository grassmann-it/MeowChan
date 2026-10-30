using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class UserPriorityLifetimeTests
{
    [Fact]
    public async Task PausedFlagChangeIsSymmetricAndDoesNotStartChecksOrTransfers()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using StubImageBoard board = new();
        var thread = new ProbeThread(board.Board, new Uri("https://stub.test/g/thread/123"));
        DownloadQueue queue = new(2, 1L << 20);
        try
        {
            await queue.SetPausedAsync(true);
            queue.ConfigureWorkState(store);
            WorkResource owner = store.CommitIntent(new WorkResource { Url = thread.Url, OutputRoot = directory.Path,
                Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
            queue.BindWork(thread, owner);
            Assert.True(queue.SetUserPriority(thread, owner, UserDownloadPriority.High));
            Assert.Equal(UserDownloadPriority.High, queue.GetUserPriority(thread));
            Assert.True(queue.SetUserPriority(thread, owner, UserDownloadPriority.Normal));
            Assert.Equal(UserDownloadPriority.Normal, queue.GetUserPriority(thread));
            Assert.Equal(0, thread.Checks);
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Assert.True(queue.IsPaused);
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task StaleOwnerAndReplacementCannotInheritBoostAndNothingIsPersisted()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        using StubImageBoard board = new();
        var thread = new ProbeThread(board.Board, new Uri("https://stub.test/g/thread/123"));
        DownloadQueue queue = new(1, 1L << 20);
        try
        {
            queue.ConfigureWorkState(store);
            WorkResource owner = store.CommitIntent(new WorkResource { Url = thread.Url, OutputRoot = directory.Path,
                Generation = 1, Intent = WorkIntent.Watching, IsThread = true });
            queue.BindWork(thread, owner);
            Assert.False(queue.SetUserPriority(thread, owner with { Generation = 99 }, UserDownloadPriority.High));
            Assert.True(queue.SetUserPriority(thread, owner, UserDownloadPriority.High));
            Assert.Equal(owner, Assert.Single(store.LoadResources()));
            var replacement = new ProbeThread(board.Board, thread.ThreadUri);
            Assert.Equal(UserDownloadPriority.Normal, queue.GetUserPriority(replacement));
            Assert.False(queue.SetUserPriority(replacement, owner, UserDownloadPriority.High));
            queue.UpdateWorkOwner(thread, owner with { Generation = 2 });
            Assert.Equal(UserDownloadPriority.Normal, queue.GetUserPriority(thread));
            Assert.False(queue.SetUserPriority(thread, owner, UserDownloadPriority.Normal));
        }
        finally { await queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
    }
}
