using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// S3: the watcher host start re-admits pending originals in the background, so a queue that holds admissions back
/// (paused producers, free-space gate) or an unreadable catalog never blocks or fails the start; stop cancels it.
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task TheHostStartDoesNotWaitForAQueueThatHoldsRecoveryAndItsStopCancelsTheRecovery()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("73", Bytes(73), cdn);
        await using (Session first = new(directory.Path, cdn))
        {
            await first.Queue.SetPausedAsync(true);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, (await first.Booru.AdmitAsync(post, BooruFindingKind.OneShot)).Status);
        }

        await using Session second = new(directory.Path, cdn);
        Assert.Single(second.Booru.GetPendingOriginals());
        // The shared queue now holds every new admission back (as the free-space gate or a profile transition does).
        await second.Queue.SuspendProfileProducersAsync();
        DanbooruWatcherHost host = new(second.Catalog, second.Booru, new FakeDanbooruSearch(TimeProvider.System));
        await host.StartAsync().WaitAsync(TestWait.Timeout);
        Assert.True(host.Scheduler.GetSnapshot().Running);
        Assert.Null(host.Scheduler.GetSnapshot().FaultCode);

        // The recovery reached the queue (Queued is raised right before the queue offer) and is parked there.
        await TestWait.UntilAsync(() => second.Booru.Snapshot().TryGetValue("73", out BooruOriginalDownloadOutcome? outcome)
            && outcome.State == BooruOriginalDownloadState.Queued, "recovery parked at the queue");
        Assert.False(host.Recovery.IsCompleted);

        await host.StopAsync().WaitAsync(TestWait.Timeout);
        Assert.True(host.Recovery.IsCompletedSuccessfully);
        Assert.Empty(await host.Recovery);
        // A stopped recovery is not a cancellation by the user: the request stays pending for the next start.
        Assert.True(Assert.Single(second.Catalog.Read().Posts).HasPendingOriginal);
        Assert.Equal(BooruOriginalDownloadState.Interrupted, second.Booru.Snapshot()["73"].State);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task AnUnreadableCatalogNoLongerFailsTheHostStartSoTheLoadFaultStaysVisible()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        await using Session session = new(directory.Path, cdn);
        File.WriteAllText(session.Catalog.FilePath, "{ not json");
        await using DanbooruWatcherHost host = new(session.Catalog, session.Booru, new FakeDanbooruSearch(TimeProvider.System));

        // Before S3 the pending-original scan rethrew the InvalidDataException and the composition dropped the window.
        await host.StartAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal("catalog_unreadable", host.Scheduler.GetSnapshot().FaultCode);
        Assert.Empty(await host.Recovery.WaitAsync(TestWait.Timeout));
    }
}
