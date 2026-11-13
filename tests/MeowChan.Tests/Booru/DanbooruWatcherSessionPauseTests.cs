using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// F3: the global session pause is a transient gate of the watcher scheduler. It stops automatic and manual metadata
/// checks (the explicit-check contract of chan watches under the queue session pause), lets a running check finish
/// only its page in flight, never rewrites stored watcher state and wakes the loop on resume. In-memory provider only.
/// </summary>
public sealed class DanbooruWatcherSessionPauseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static DanbooruWatcherSchedulerOptions Options() => new() { Interval = Interval, CheckStartSpacing = TimeSpan.Zero };

    private static long[] Range(long newest, int count) => Enumerable.Range(0, count).Select(index => newest - index).ToArray();

    [Fact]
    public async Task APausedSessionStartsNoAutomaticOrManualCheckAndResumeRunsExactlyOneBaseline()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.SetSessionPaused(true); // set from the confirmed session state before the first start
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId; // commands stay available
        BooruWatcherRecord before = Assert.Single(profile.Catalog.Read().Watchers);

        Assert.Null(await scheduler.RunNextAsync());
        DanbooruWatcherCheckSummary manual = await scheduler.CheckNowAsync(id).WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, manual.Kind);
        Assert.Equal(DanbooruWatcherScheduler.SessionPausedCode, manual.Code);
        Assert.True(manual.Manual);
        Assert.Equal(0, search.CallCount);
        Assert.Equal(before, Assert.Single(profile.Catalog.Read().Watchers));
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus(id)!.State); // no persisted pause

        scheduler.SetSessionPaused(false);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(1, search.CallCount);
    }

    [Fact]
    public async Task ResumingTheSessionWakesTheRunningLoopWithoutAClockStep()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.SetSessionPaused(true);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.StartAsync();
        Assert.True(scheduler.GetSnapshot().Running);

        await Task.Delay(100);
        Assert.Equal(0, search.CallCount); // due at once, but the gate holds it

        scheduler.SetSessionPaused(false);
        await TestWait.UntilAsync(() => scheduler.GetStatus(id)!.BaselineEstablished, "the resumed loop runs the baseline");
        await Task.Delay(50);
        Assert.Equal(1, search.CallCount); // no second baseline

        // Pausing again stops the next automatic check that becomes due.
        scheduler.SetSessionPaused(true);
        search.Publish("cat", 101);
        clock.Advance(Interval);
        await Task.Delay(100);
        Assert.Equal(1, search.CallCount);
        scheduler.SetSessionPaused(false);
        await TestWait.UntilAsync(() => search.CallCount == 2, "the resumed loop runs the due check");
    }

    [Fact]
    public async Task APauseDuringAnInFlightCheckFinishesThatPageDurablyAndStopsBeforeTheNext()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();

        search.Publish("cat", Range(160, 60)); // three pages of new posts
        clock.Advance(Interval);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);
        scheduler.SetSessionPaused(true);
        search.Hold = null;
        release.SetResult();

        DanbooruWatcherCheckSummary paused = (await running.WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, paused.Kind);
        Assert.Equal(DanbooruWatcherScheduler.SessionPausedCode, paused.Code);
        Assert.Equal(1, paused.PagesRead);
        Assert.Equal(20, paused.Queued); // the answered page is not lost
        Assert.Equal(2, search.CallCount); // no second page was requested
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(nameof(DanbooruWatcherState.Active), stored.State);
        Assert.Equal(2, stored.NextPage);
        Assert.Equal("141", stored.CursorBeforePostId);
        Assert.Equal("100", stored.CheckStartHighWaterMark);

        Assert.Null(await scheduler.RunNextAsync());
        scheduler.SetSessionPaused(false);
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal("141", search.Cursors[2]); // continues below the stored cursor
        Assert.Equal(60, admission.QueuedCount);
        Assert.Equal("160", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus(id)!.State);
    }

    [Fact]
    public async Task IndividualPauseCursorHighWaterMarkAndCooldownAreUntouchedBySessionPauseAndResume()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        search.Publish("dog", 200);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string cat = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string dog = scheduler.AddWatcher("dog").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        await scheduler.RunNextAsync();
        Assert.True(scheduler.Pause(cat).Accepted);
        clock.Advance(Interval);
        DateTimeOffset retryAt = clock.GetUtcNow() + TimeSpan.FromHours(3);
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await scheduler.RunNextAsync())!.Kind);
        BooruWatcherRecord[] before = profile.Catalog.Read().Watchers.OrderBy(item => item.WatcherId).ToArray();

        scheduler.SetSessionPaused(true);
        Assert.Null(await scheduler.RunNextAsync());
        scheduler.SetSessionPaused(false);

        Assert.Equal(before, profile.Catalog.Read().Watchers.OrderBy(item => item.WatcherId).ToArray());
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(cat)!.State);
        Assert.Equal(DanbooruWatcherState.Cooldown, scheduler.GetStatus(dog)!.State);
        Assert.Equal(retryAt, scheduler.GetSnapshot().ProviderCooldownUntilUtc);
        int calls = search.CallCount;
        clock.Advance(Interval);
        Assert.Null(await scheduler.RunNextAsync()); // the individually paused watcher stays paused, the cooldown holds
        Assert.Equal(calls, search.CallCount);
    }

    [Fact]
    public async Task AQueuedManualCheckIsAnsweredWhenTheSessionIsPaused()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Task<DanbooruWatcherCheckSummary> queued = scheduler.CheckNowAsync(id);
        Assert.False(queued.IsCompleted);

        scheduler.SetSessionPaused(true);
        DanbooruWatcherCheckSummary answered = await queued.WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, answered.Kind);
        Assert.Equal(DanbooruWatcherScheduler.SessionPausedCode, answered.Code);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(0, search.CallCount);
    }

    /// <summary>
    /// The pick and the check registration are separate critical sections: a pause or a cut that lands exactly in
    /// between must still prevent the request (the registration re-checks the gates) without a stored Checking state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGateClosedBetweenPickAndRegistrationSendsNothing(bool profileCut)
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options(), lifecycle);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        BooruWatcherRecord before = Assert.Single(profile.Catalog.Read().Watchers);

        int reads = 0;
        Task cut = Task.CompletedTask;
        clock.OnGetUtcNow = () =>
        {
            // 1: the pick (under the scheduler lock), 2: the registration start (outside it).
            if (Interlocked.Increment(ref reads) != 2) return;
            if (profileCut) cut = lifecycle.CutAndDrainAsync();
            else scheduler.SetSessionPaused(true);
        };
        DanbooruWatcherCheckSummary rejected = (await scheduler.RunNextAsync())!;
        clock.OnGetUtcNow = null;

        Assert.Equal(DanbooruWatcherCheckKind.Rejected, rejected.Kind);
        Assert.Equal(profileCut ? DanbooruWatcherScheduler.ProfileSuspendedCode : DanbooruWatcherScheduler.SessionPausedCode,
            rejected.Code);
        Assert.Equal(0, search.CallCount);
        Assert.Equal(before, Assert.Single(profile.Catalog.Read().Watchers));
        Assert.Null(scheduler.GetStatus(id)!.LastCheck); // a transient hold is not recorded as this watcher's check
        await cut.WaitAsync(TestWait.Timeout);
        if (profileCut)
        {
            lifecycle.ReleaseDrained();
            lifecycle.Open();
        }
        else scheduler.SetSessionPaused(false);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
    }
}
