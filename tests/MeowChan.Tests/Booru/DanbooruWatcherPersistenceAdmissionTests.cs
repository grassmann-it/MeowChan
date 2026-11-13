using System.IO;
using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>Regression coverage for persistence failures at the real watcher queue-admission boundary.</summary>
public sealed class DanbooruWatcherPersistenceAdmissionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task RealQueueAdmissionMapsCatalogRejectionToPersistFailedWithoutAdvancingCursor()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        await profile.Queue!.SetPausedAsync(true);

        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 100);
        DanbooruWatcherSchedulerOptions options = new()
        {
            Interval = Interval,
            CheckStartSpacing = TimeSpan.Zero
        };
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search,
            new DanbooruWatcherQueueAdmission(profile.Originals!, profile.Catalog), clock, options);
        scheduler.Load();
        string watcherId = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        // The first catch-up page is admitted and persisted. The bounded-store seam then rejects the first
        // admission on page two with the catalog's real rejection type. The queue remains paused, so this test
        // exercises admission and persistence only, without a transfer or a wall-clock wait.
        search.Publish("cat", Enumerable.Range(0, 40).Select(index => (long)(200 - index)).ToArray());
        int mutation = 0;
        profile.Catalog.BeforeAdmittedMutationForTests = _ =>
        {
            if (Interlocked.Increment(ref mutation) != 23) return;
            profile.Catalog.BeforeAdmittedMutationForTests = null;
            throw new InvalidDataException(BooruCatalogStore.FullMessage);
        };

        clock.Advance(Interval);
        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;

        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal("persist_failed", failed.Code);
        Assert.Equal(2, failed.PagesRead);
        Assert.Equal(21, failed.NewPosts); // 20 durable findings, then the rejected first finding of page two.

        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(watcherId, stored.WatcherId);
        Assert.Equal(DanbooruWatcherState.Error.ToString(), stored.State);
        Assert.Equal("persist_failed", stored.ErrorCode);
        Assert.Equal("200", stored.HighWaterMark);
        Assert.Equal("181", stored.CursorBeforePostId);
        Assert.Equal(2, stored.NextPage);
        Assert.DoesNotContain(profile.Catalog.Read().Posts, post => post.PostId == "161");
        Assert.Equal(20, profile.Catalog.Read().Posts.Count);
        Assert.Empty(cdn.Requests);

        // A local automatic backoff must not turn the explicit retry into a provider cooldown or block Check Now.
        Assert.True(scheduler.Resume(watcherId).Accepted);
        Task<DanbooruWatcherCheckSummary> manual = scheduler.CheckNowAsync(watcherId);
        DanbooruWatcherCheckSummary manualRun = (await scheduler.RunNextAsync())!;
        DanbooruWatcherCheckSummary manualResult = await manual.WaitAsync(TestWait.Timeout);
        Assert.True(manualRun.Manual);
        Assert.True(manualResult.Manual);
        Assert.NotEqual("provider_cooldown", manualResult.Code);
    }
}
