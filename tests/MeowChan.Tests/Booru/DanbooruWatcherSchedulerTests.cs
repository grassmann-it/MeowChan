using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B5 watcher execution against the real B3 catalog, an in-memory search port and either a counting
/// admission double or the real B4 queue path. No socket is opened; time is a manual clock.
/// </summary>
public sealed class DanbooruWatcherSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static DanbooruWatcherSchedulerOptions Options(TimeSpan? spacing = null) => new()
    {
        Interval = Interval,
        CheckStartSpacing = spacing ?? TimeSpan.Zero
    };

    private static long[] Range(long newest, int count) => Enumerable.Range(0, count).Select(index => newest - index).ToArray();

    [Fact]
    public void DefaultIntervalIsConservativeAndSeparateFromProviderPacing()
    {
        Assert.Equal(TimeSpan.FromHours(1), DanbooruWatcherSchedulerOptions.DefaultInterval);
        Assert.True(DanbooruWatcherSchedulerOptions.DefaultInterval > TimeSpan.FromSeconds(1) * 3600 / 10);
        Assert.Equal(DanbooruWatcherSchedulerOptions.DefaultInterval, new DanbooruWatcherSchedulerOptions().Interval);
    }

    [Fact]
    public async Task NewPostIsAdmittedExactlyOnceThroughTheSharedQueueAndBaselineDownloadsNothing()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 100, 99, 98);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search,
            new DanbooruWatcherQueueAdmission(profile.Originals!, profile.Catalog, clock), clock, Options());
        scheduler.Load();

        DanbooruWatcherCommandResult added = scheduler.AddWatcher("cat");
        Assert.True(added.Accepted);
        string id = added.Watcher!.WatcherId;

        DanbooruWatcherCheckSummary baseline = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, baseline.Kind);
        Assert.Equal(0, baseline.NewPosts);
        Assert.Empty(cdn.Requests);
        Assert.Empty(profile.Catalog.Read().Posts);

        search.Publish("cat", 101);
        Assert.Null(await scheduler.RunNextAsync()); // not due before the interval
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary check = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
        Assert.Equal(1, check.NewPosts);
        Assert.Equal(1, check.Queued);
        await TestWait.UntilAsync(() => profile.Catalog.Read().Posts.SingleOrDefault()?.LocalAssetId != null);

        // Later checks and a second watcher on an overlapping query never queue the post again.
        DanbooruWatcherCommandResult second = scheduler.AddWatcher("cat_ears");
        search.Publish("cat_ears", 100);
        await scheduler.RunNextAsync(); // baseline of the second watcher
        search.Publish("cat_ears", 101);
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary first = (await scheduler.RunNextAsync())!;
        DanbooruWatcherCheckSummary other = (await scheduler.RunNextAsync())!;
        Assert.Equal(0, first.NewPosts + (first.Manual ? 1 : 0));
        Assert.Equal(1, other.NewPosts);
        Assert.Equal(1, other.AlreadyQueuedOrLinked);
        Assert.Equal([SchedulerCdn.OriginalUrl("101")], cdn.Requests.ToArray());

        BooruPostRecord record = Assert.Single(profile.Catalog.Read().Posts);
        Assert.Equal("101", record.PostId);
        Assert.Equal(BooruOriginalIntent.Linked, record.OriginalIntent);
        Assert.Equal(new[] { id, second.Watcher!.WatcherId }.Order(), record.Findings.Select(f => f.WatcherId!).Order());
    }

    [Fact]
    public async Task MoreNewPostsThanOneCheckReadsIsVisiblePartialProgressAndContinuesBelowTheCursor()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();

        search.Publish("cat", Range(250, 150)); // 250..101: 150 new posts, more than 5 pages x 20
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary partial = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.PartialBudgetLimited, partial.Kind);
        Assert.Null(partial.Code);
        Assert.Equal(5, partial.PagesRead);
        Assert.Equal(100, partial.NewPosts);
        // The newest page, then each page strictly below the lowest id seen so far.
        Assert.Equal([null, "231", "211", "191", "171"], search.Cursors.Skip(1).ToArray());
        Assert.Equal(100, admission.QueuedCount);

        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.True(status.BudgetLimited);
        Assert.True(status.HasPartialProgress);
        Assert.Equal(6, status.NextPage);
        Assert.Equal(DanbooruWatcherActivity.Scheduled, status.Activity);
        Assert.Equal(clock.GetUtcNow(), status.NextCheckAtUtc); // continues without waiting for the interval
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.True(stored.BudgetLimited);
        Assert.Equal(6, stored.NextPage);
        Assert.Equal("151", stored.CursorBeforePostId);
        Assert.Equal("100", stored.CheckStartHighWaterMark);
        Assert.Equal("250", stored.HighWaterMark);

        // The stored progress survives a restart and the next check continues below the cursor.
        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        DanbooruWatcherCheckSummary rest = (await restarted.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, rest.Kind);
        Assert.Equal(50, rest.NewPosts);
        Assert.Equal(["151", "131", "111"], search.Cursors.Skip(6).ToArray());
        Assert.Equal(Range(250, 150).Select(value => value.ToString()).Order(), admission.PerPost.Keys.Order());
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        DanbooruWatcherStatus done = restarted.GetStatus(id)!;
        Assert.False(done.HasPartialProgress);
        Assert.Equal(T0 + Interval + Interval, done.NextCheckAtUtc);
    }

    [Fact]
    public async Task WatcherCatchesUpAnyBacklogOverSeveralChecksByCursorWithoutGapsOrDuplicates()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 1000);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        // 250 new posts between two regular checks: more than two full check budgets (2 x 5 x 20).
        search.Publish("cat", Range(1250, 250));
        clock.Advance(Interval);
        List<DanbooruWatcherCheckSummary> checks = new();
        for (int run = 0; run < 10; run++)
        {
            DanbooruWatcherCheckSummary? check = await scheduler.RunNextAsync();
            if (check is null) break;
            checks.Add(check);
            Assert.NotEqual(DanbooruWatcherCheckKind.Failed, check.Kind);
            Assert.NotEqual(DanbooruWatcherCheckKind.Rejected, check.Kind);
        }

        Assert.Equal([DanbooruWatcherCheckKind.PartialBudgetLimited, DanbooruWatcherCheckKind.PartialBudgetLimited,
            DanbooruWatcherCheckKind.Completed], checks.Select(check => check.Kind).ToArray());
        Assert.Equal([5, 5, 3], checks.Select(check => check.PagesRead).ToArray());
        Assert.Equal([100, 100, 50], checks.Select(check => check.NewPosts).ToArray());
        Assert.All(checks, check => Assert.True(check.PagesRead <= DanbooruWatcher.DefaultPageBudget));
        // Every post exactly once: no gap, no duplicate.
        Assert.Equal(Range(1250, 250).Select(value => value.ToString()).Order(), admission.PerPost.Keys.Order());
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        // Only the first page of the traversal is a newest-page request; the rest continue by cursor.
        string?[] traversal = search.Cursors.Skip(1).ToArray();
        Assert.Null(traversal[0]);
        Assert.All(traversal.Skip(1), cursor => Assert.NotNull(cursor));
        Assert.Equal(13, traversal.Length);

        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherActivity.Scheduled, status.Activity);
        Assert.False(status.HasPartialProgress);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal(0, stored.NextPage);
        Assert.Equal("1250", stored.HighWaterMark);
        Assert.Null(await scheduler.RunNextAsync());

        // The next regular check sees only what is new since the catch-up started.
        search.Publish("cat", 1251);
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary next = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, next.Kind);
        Assert.Equal(1, next.NewPosts);
        Assert.Equal(251, admission.QueuedCount);
    }

    [Fact]
    public async Task PostsArrivingDuringATraversalDoNotShiftCursorPagesAndAreFoundByTheNextCheck()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(160, 60)); // 160..101
        clock.Advance(Interval);

        // A busy tag: three new posts arrive before every continuation page. With page numbers each would
        // push the last post of a page onto the next one (cursor_did_not_advance).
        long arriving = 500;
        search.Hold = (_, before, _) =>
        {
            if (before is not null) search.Publish("cat", arriving++, arriving++, arriving++);
            return Task.CompletedTask;
        };
        DanbooruWatcherCheckSummary check = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
        Assert.Null(check.Code);
        Assert.Equal(60, check.NewPosts);
        Assert.Equal([null, "141", "121", "101"], search.Cursors.Skip(1).ToArray());
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus(id)!.State);
        Assert.Null(scheduler.GetStatus(id)!.ErrorCode);

        search.Hold = null;
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary later = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, later.Kind);
        Assert.Equal(9, later.NewPosts);
        Assert.Equal(69, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        Assert.Equal("508", profile.Catalog.Read().Watchers.Single().HighWaterMark);
    }

    [Fact]
    public async Task ProviderDeferralDuringACursorPageKeepsTheCursorAndResumesBelowItAfterTheCooldown()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(160, 60));
        clock.Advance(Interval);
        DateTimeOffset retryAt = clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        search.Next((_, _) => null); // the newest page answers normally
        search.Next((_, before) => before == "141" ? new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429) : null);

        DanbooruWatcherCheckSummary deferred = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
        Assert.Equal(retryAt, deferred.RetryAtUtc);
        Assert.Equal(DanbooruWatcherActivity.Cooldown, scheduler.GetStatus(id)!.Activity);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Cooldown", stored.State);
        Assert.Equal(2, stored.NextPage);
        Assert.Equal("141", stored.CursorBeforePostId);
        Assert.Equal(20, admission.QueuedCount);

        // Only deferred: no request for any watcher until the cooldown ends, then below the same cursor.
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(3, search.CallCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal(["141", "121", "101"], search.Cursors.Skip(3).ToArray());
        Assert.Equal(60, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task RecordsWrittenBeforeCursorPagingWithAPageContinuationLoadAndFinish()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        // Exactly what the page-number scheduler stored: a budget-limited traversal parked at next page 6
        // (formerly ContinuationBlocked) and a check interrupted by a crash at page 3.
        profile.Catalog.UpsertWatcher(new BooruWatcherRecord("w-cat", "danbooru", "cat", "Active", T0,
            LastCheckedAtUtc: T0, HighWaterMark: "250", NextPage: 6, BaselineEstablished: true,
            KnownPostIds: Range(250, 100).Select(value => value.ToString()).ToArray(),
            CursorBeforePostId: "151", CheckStartHighWaterMark: "100", CheckStartHighWaterMarkCaptured: true,
            ResumeState: "Active", PageSize: 20, PageBudget: 5, PagesCheckedInRun: 0, BudgetLimited: true));
        profile.Catalog.UpsertWatcher(new BooruWatcherRecord("w-dog", "danbooru", "dog", "Checking", T0.AddSeconds(1),
            LastCheckedAtUtc: T0, HighWaterMark: "500", NextPage: 3, BaselineEstablished: true,
            KnownPostIds: Range(500, 40).Select(value => value.ToString()).ToArray(),
            CursorBeforePostId: "461", CheckStartHighWaterMark: "400", CheckStartHighWaterMarkCaptured: true,
            ResumeState: "Active", PageSize: 20, PageBudget: 5, PagesCheckedInRun: 2, BudgetLimited: false));
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", Range(250, 151));
        search.Publish("dog", Range(500, 101));
        CountingAdmission admission = new();
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        Assert.Null(scheduler.GetSnapshot().FaultCode);
        Assert.True(scheduler.GetStatus("w-cat")!.HasPartialProgress);
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus("w-dog")!.State);

        DanbooruWatcherCheckSummary cat = (await scheduler.RunNextAsync())!;
        DanbooruWatcherCheckSummary dog = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, cat.Kind);
        Assert.Equal(DanbooruWatcherCheckKind.Completed, dog.Kind);
        // Both continue below their stored cursor; neither restarts at the newest page or skips posts.
        Assert.Equal(["151", "131", "111", "461", "441", "421", "401"], search.Cursors);
        Assert.Equal(Range(150, 50).Concat(Range(460, 60)).Select(value => value.ToString()).Order(), admission.PerPost.Keys.Order());
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        Assert.All(profile.Catalog.Read().Watchers, record =>
        {
            Assert.Equal(0, record.NextPage);
            Assert.Null(record.CursorBeforePostId);
        });
        Assert.Equal(50, admission.Findings.Count(finding => finding.WatcherId == "w-cat"));
        Assert.Equal(60, admission.Findings.Count(finding => finding.WatcherId == "w-dog"));
    }

    [Fact]
    public async Task APageAnsweringAnotherCursorStopsTheWatcherWithoutAdvancingIt()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(140, 40));
        clock.Advance(Interval);
        search.Next((_, _) => null);
        // The port claims the page below 131 although the scheduler asked for the page below 121.
        search.Next((query, _) => new DanbooruWatcherSearchSnapshot(query, "131",
            Range(130, 20).Select(FakeDanbooruSearch.MinimalClaim).ToArray(), false));

        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal("page_binding_mismatch", failed.Code);
        Assert.Equal(20, admission.QueuedCount);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Error", stored.State);
        Assert.Equal("121", stored.CursorBeforePostId);
        Assert.Equal(DanbooruWatcherActivity.Error, scheduler.GetStatus(id)!.Activity);
    }

    [Fact]
    public async Task RestartFromNowAcceptsAGapNonDestructively()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(250, 150));
        clock.Advance(Interval);
        Assert.Equal(DanbooruWatcherCheckKind.PartialBudgetLimited, (await scheduler.RunNextAsync())!.Kind);

        DanbooruWatcherCommandResult restarted = scheduler.RestartFromNow(id);
        Assert.True(restarted.Accepted);
        Assert.NotEqual(id, restarted.Watcher!.WatcherId);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        BooruCatalogSnapshot stored = profile.Catalog.Read();
        Assert.Equal("Removed", stored.Watchers.Single(w => w.WatcherId == id).State);
        Assert.Equal(100, admission.QueuedCount);
    }

    [Fact]
    public async Task RestartAfterACrashMidTraversalContinuesWithoutReadmittingOrLosingPosts()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler crashed = new(profile.Catalog, search, admission, clock, Options());
        crashed.Load();
        string id = crashed.AddWatcher("cat").Watcher!.WatcherId;
        await crashed.RunNextAsync();
        search.Publish("cat", Range(160, 60)); // three full pages of new posts
        clock.Advance(Interval);

        TaskCompletionSource reachedPage3 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, before, token) =>
        {
            if (before != "121") return; // page 3: below the lowest id of pages 1 and 2
            reachedPage3.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // the process "dies" here; never resumed
        };
        _ = crashed.RunNextAsync();
        await reachedPage3.Task.WaitAsync(TestWait.Timeout);
        Assert.Equal(40, admission.QueuedCount);
        BooruWatcherRecord persisted = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Checking", persisted.State);
        Assert.Equal(3, persisted.NextPage);

        search.Hold = null;
        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        DanbooruWatcherStatus recovered = restarted.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Active, recovered.State);
        Assert.True(recovered.HasPartialProgress);
        DanbooruWatcherCheckSummary resumed = (await restarted.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal(["121", "101"], search.Cursors.Skip(4).ToArray()); // resumes below the stored cursor
        Assert.Equal(60, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task CrashBetweenAdmissionAndPersistReplaysThePageIdempotently()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler crashed = new(profile.Catalog, search, admission, clock, Options());
        crashed.Load();
        crashed.AddWatcher("cat");
        await crashed.RunNextAsync();
        search.Publish("cat", 102, 101);
        clock.Advance(Interval);
        TaskCompletionSource handedOff = new(TaskCreationOptions.RunContinuationsAsynchronously);
        admission.Hold = async (postId, token) =>
        {
            if (postId != "102") return;
            handedOff.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        _ = crashed.RunNextAsync();
        await handedOff.Task.WaitAsync(TestWait.Timeout);
        admission.Hold = null;

        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        DanbooruWatcherCheckSummary replay = (await restarted.RunNextAsync())!;
        Assert.Equal(2, replay.NewPosts);
        Assert.Equal(2, admission.QueuedCount);
        Assert.Equal(2, admission.PerPost["101"]); // at-least-once hand-off; B4 dedupes by post identity
        Assert.Equal(1, admission.Findings.Count(f => f.PostId == "101"));
    }

    [Fact]
    public async Task ProviderDeferralPausesOnlyFurtherRequestsUntilTheCooldownAndSurvivesRestart()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        search.Publish("dog", 200);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string cat = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string dog = scheduler.AddWatcher("dog").Watcher!.WatcherId;
        DateTimeOffset retryAt = T0 + TimeSpan.FromSeconds(120);
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));

        DanbooruWatcherCheckSummary deferred = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
        Assert.Equal(retryAt, deferred.RetryAtUtc);
        string throttled = search.Calls[0].Query == "cat" ? cat : dog;
        DanbooruWatcherStatus status = scheduler.GetStatus(throttled)!;
        Assert.Equal(DanbooruWatcherActivity.Cooldown, status.Activity);
        Assert.Equal(retryAt, status.CooldownUntilUtc);
        Assert.Equal(DanbooruWatcherActivity.Cooldown, scheduler.GetStatus(throttled == cat ? dog : cat)!.Activity);

        // No request for any watcher, automatic or manual, until the cooldown ends.
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal("provider_cooldown", (await scheduler.CheckNowAsync(dog)).Code);
        Assert.Equal(1, search.CallCount);

        // A restart in the cooldown keeps every watcher quiet (the transport cooldown is in memory only).
        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Null(await restarted.RunNextAsync());
        Assert.Equal(1, search.CallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await restarted.RunNextAsync());
        Assert.NotNull(await restarted.RunNextAsync());
        Assert.Equal(3, search.CallCount);
        Assert.All(restarted.GetSnapshot().Watchers, w => Assert.Equal(DanbooruWatcherState.Active, w.State));
    }

    [Fact]
    public async Task ShortProviderRetryTimesAreRaisedToTheDeferralFloor()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(T0 + TimeSpan.FromSeconds(5), "provider-unavailable", 503));
        DanbooruWatcherCheckSummary deferred = (await scheduler.RunNextAsync())!;
        Assert.Equal(T0 + DanbooruWatcherSchedulerOptions.DefaultMinimumDeferral, deferred.RetryAtUtc);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await scheduler.RunNextAsync());
    }

    [Fact]
    public async Task TransportCooldownBlocksEveryCheckWithoutARequest()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { TransportCooldown = T0 + TimeSpan.FromMinutes(2) };
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Null(await scheduler.RunNextAsync());
        DanbooruWatcherCheckSummary manual = await scheduler.CheckNowAsync(id);
        Assert.Equal("provider_cooldown", manual.Code);
        Assert.Equal(T0 + TimeSpan.FromMinutes(2), manual.RetryAtUtc);
        Assert.Equal(0, search.CallCount);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await scheduler.RunNextAsync());
        Assert.Equal(1, search.CallCount);
    }

    [Fact]
    public async Task PausedAndRemovedWatchersNeverSendARequest()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string paused = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string removed = scheduler.AddWatcher("dog").Watcher!.WatcherId;
        Assert.True(scheduler.Pause(paused).Accepted);
        Assert.True(scheduler.Remove(removed).Accepted);

        for (int day = 0; day < 5; day++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            Assert.Null(await scheduler.RunNextAsync());
        }
        Assert.Equal("watcher_removed", (await scheduler.CheckNowAsync(removed)).Code);
        Assert.False(scheduler.Resume(removed).Accepted);
        Assert.Equal(0, search.CallCount);
        Assert.Equal(DanbooruWatcherActivity.Paused, scheduler.GetStatus(paused)!.Activity);
        Assert.Null(scheduler.GetStatus(paused)!.NextCheckAtUtc);
        Assert.Null(scheduler.GetStatus(removed));

        // A restart keeps both quiet: the pause and the tombstone are durable.
        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        restarted.Load();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await restarted.RunNextAsync());
        Assert.Equal(0, search.CallCount);
        Assert.Single(restarted.GetSnapshot().Watchers);
    }

    [Fact]
    public async Task RemovingAWatcherKeepsPostsFindingsAndLocalAssetLinks()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        profile.Catalog.UpsertPost(FakeDanbooruSearch.MinimalClaim(42), T0);
        profile.Catalog.AddFinding("danbooru", "42", BooruFindingKind.Watcher, id, "cat", T0);
        profile.Catalog.LinkLocalAsset("danbooru", "42", "asset-42", Path.Combine(profile.OutputRoot, "42.png"));
        BooruPostRecord before = Assert.Single(profile.Catalog.Read().Posts);

        Assert.True(scheduler.Remove(id).Accepted);
        BooruCatalogSnapshot after = profile.Catalog.Read();
        BooruPostRecord kept = Assert.Single(after.Posts);
        Assert.Equal(before.LocalAssetId, kept.LocalAssetId);
        Assert.Equal(before.LocalPath, kept.LocalPath);
        Assert.Equal(before.Findings, kept.Findings);
        Assert.Equal("Removed", Assert.Single(after.Watchers).State);
        // The tombstone is never overwritten by a later command.
        Assert.False(scheduler.Pause(id).Accepted);
        Assert.Equal("Removed", Assert.Single(profile.Catalog.Read().Watchers).State);
    }

    [Fact]
    public async Task PauseDuringACheckCancelsTheRequestKeepsProgressAndResumeContinues()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(140, 40));
        clock.Advance(Interval);
        TaskCompletionSource onPage2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, before, token) =>
        {
            if (before != "121") return; // page 2
            onPage2.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await onPage2.Task.WaitAsync(TestWait.Timeout);

        Assert.True(scheduler.Pause(id).Accepted);
        DanbooruWatcherCheckSummary cancelled = (await running.WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cancelled.Kind);
        Assert.Equal(1, Volatile.Read(ref search.Cancelled));
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Paused", stored.State);
        Assert.Equal(2, stored.NextPage);
        Assert.Equal("121", stored.CursorBeforePostId);
        int calls = search.CallCount;
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(calls, search.CallCount);

        search.Hold = null;
        Assert.True(scheduler.Resume(id).Accepted);
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal("121", search.Calls[calls].Before);
        Assert.Equal(40, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task ManualCheckRunsOnceIndependentOfTheIntervalAndKeepsAPausedWatcherPaused()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        scheduler.Pause(id);
        search.Publish("cat", 101);

        Task<DanbooruWatcherCheckSummary> manual = scheduler.CheckNowAsync(id);
        Assert.Same(manual, scheduler.CheckNowAsync(id)); // one queued request per watcher
        Assert.Equal(DanbooruWatcherActivity.QueuedForCheck, scheduler.GetStatus(id)!.Activity);
        await scheduler.RunNextAsync();
        DanbooruWatcherCheckSummary result = await manual.WaitAsync(TestWait.Timeout);
        Assert.True(result.Manual);
        Assert.Equal(1, result.Queued);
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(id)!.State);
        Assert.Equal(2, search.CallCount);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await scheduler.RunNextAsync());

        // A manual check of a paused watcher that is deferred stays paused and is not retried automatically.
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(clock.GetUtcNow() + TimeSpan.FromMinutes(2), "booru-cooldown", 429));
        Task<DanbooruWatcherCheckSummary> deferred = scheduler.CheckNowAsync(id);
        await scheduler.RunNextAsync();
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await deferred).Kind);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(3, search.CallCount);
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(id)!.ResumeState);
    }

    // Owner decision (12.11.2026): "check now" on a paused watcher is an explicit one-time check with the
    // normal pacing/cooldown/budget/dedupe rules; it records result and progress, the watcher stays
    // paused and gets no automatic check; a removed watcher can never be checked; a cooldown is not bypassed.
    [Fact]
    public async Task OwnerDecisionManualCheckOnPausedStaysPausedRemovedIsRefusedAndCooldownIsNotBypassed()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        search.Publish("dog", 200);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string paused = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string removed = scheduler.AddWatcher("dog").Watcher!.WatcherId;
        Assert.True(scheduler.Pause(paused).Accepted);
        Assert.True(scheduler.Remove(removed).Accepted);
        await scheduler.StartAsync();

        // Paused: one explicit check, result and progress recorded, still paused, nothing scheduled.
        DanbooruWatcherCheckSummary baseline = await scheduler.CheckNowAsync(paused).WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, baseline.Kind);
        DanbooruWatcherStatus status = scheduler.GetStatus(paused)!;
        Assert.Equal(DanbooruWatcherState.Paused, status.State);
        Assert.Equal(DanbooruWatcherActivity.Paused, status.Activity);
        Assert.True(status.BaselineEstablished);
        Assert.Null(status.NextCheckAtUtc);
        Assert.Same(baseline, status.LastCheck);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single(w => w.WatcherId == paused);
        Assert.Equal("Paused", stored.State);
        Assert.Equal("100", stored.HighWaterMark);

        search.Publish("cat", 101);
        DanbooruWatcherCheckSummary found = await scheduler.CheckNowAsync(paused).WaitAsync(TestWait.Timeout);
        Assert.Equal(1, found.Queued);
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(paused)!.State);
        int calls = search.CallCount;
        Assert.Equal(2, calls);
        for (int step = 0; step < 4; step++)
        {
            clock.Advance(Interval);
            await Task.Delay(30);
        }
        Assert.Equal(calls, search.CallCount);

        // Removed: refused without a request, also after a restart.
        Assert.Equal("watcher_removed", (await scheduler.CheckNowAsync(removed)).Code);

        // Cooldown: the paused watcher's manual check is refused, not sent.
        search.TransportCooldown = clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        DanbooruWatcherCheckSummary cooling = await scheduler.CheckNowAsync(paused);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, cooling.Kind);
        Assert.Equal("provider_cooldown", cooling.Code);
        Assert.Equal(search.TransportCooldown, cooling.RetryAtUtc);
        Assert.Equal(calls, search.CallCount);
        await scheduler.StopAsync();

        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal("watcher_removed", (await restarted.CheckNowAsync(removed)).Code);
        Assert.Equal(DanbooruWatcherState.Paused, restarted.GetStatus(paused)!.State);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await restarted.RunNextAsync());
        Assert.Equal(calls, search.CallCount);
    }

    [Fact]
    public async Task NonRetryableFailureKeepsTheWatcherInErrorUntilResume()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        search.Next((_, _) => new DanbooruWatcherSearchFailed("access-denied", 403));
        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherActivity.Error, status.Activity);
        Assert.Equal("access-denied", status.ErrorCode);
        Assert.Equal("Error", profile.Catalog.Read().Watchers.Single().State);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(1, search.CallCount);
        Assert.True(scheduler.Resume(id).Accepted);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
    }

    [Fact]
    public async Task AdmissionFailureDoesNotAdvanceAndTheSamePostsAreHandedOffAfterResume()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new() { Fail = postId => postId == "102" ? new IOException("disk") : null };
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", 102, 101);
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal("admission_failed", failed.Code);
        Assert.Equal("100", profile.Catalog.Read().Watchers.Single().HighWaterMark);

        admission.Fail = null;
        scheduler.Resume(id);
        DanbooruWatcherCheckSummary replay = (await scheduler.RunNextAsync())!;
        Assert.Equal(2, replay.NewPosts);
        Assert.Equal(2, admission.QueuedCount);
        Assert.Equal("102", profile.Catalog.Read().Watchers.Single().HighWaterMark);
    }

    [Fact]
    public async Task AllPagesOfOneCheckShareOneSearchSessionAndEachCheckGetsItsOwn()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");

        await scheduler.RunNextAsync(); // baseline: one page, one session
        Assert.Equal(1, search.SessionsOpened);
        Assert.Equal(1, search.SessionsDisposed);

        search.Publish("cat", Range(160, 60)); // 160..101: three new pages, then the known post 100
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary check = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
        Assert.Equal(4, check.PagesRead);
        Assert.Equal([null, "141", "121", "101"], search.Cursors.Skip(1).ToArray());
        Assert.Equal([1, 2, 2, 2, 2], search.CallSessions.ToArray());
        Assert.Equal(2, search.SessionsOpened);
        Assert.Equal(2, search.SessionsDisposed);

        // A check that sends nothing (not due) opens no session at all.
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(2, search.SessionsOpened);
    }

    [Fact]
    public async Task SearchSessionIsDisposedWhenTheCheckIsCancelledOrFaultsAndOpenFailureIsAPortFault()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;

        search.Next((_, _) => new DanbooruWatcherSearchFailed("access-denied", 403));
        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal(1, search.SessionsOpened);
        Assert.Equal(1, search.SessionsDisposed);

        search.FailOpen = true;
        Task<DanbooruWatcherCheckSummary> manualCheck = scheduler.CheckNowAsync(id);
        await scheduler.RunNextAsync(); // the manual request is executed by the scheduler step
        DanbooruWatcherCheckSummary fault = await manualCheck;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, fault.Kind);
        Assert.Equal("search_port_fault", fault.Code);
        Assert.Equal(1, search.SessionsDisposed);
    }

    [Fact]
    public async Task UnavailableSearchPathLeavesTheWatcherUnchangedAndBacksOff()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        search.Next((_, _) => new DanbooruWatcherSearchUnavailable("worker_unavailable"));
        DanbooruWatcherCheckSummary unavailable = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, unavailable.Kind);
        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Active, status.State);
        Assert.False(status.BaselineEstablished);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(await scheduler.RunNextAsync());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await scheduler.RunNextAsync());
        Assert.Equal(2, search.CallCount);
    }

    [Fact]
    public async Task UserCancelledOriginalIsNotRequeuedByAWatcherButItsFindingIsKept()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        BooruPostClaim claim = cdn.Claim(101);
        profile.Catalog.UpsertPost(claim, T0);
        profile.Catalog.SetOriginalIntent("danbooru", "101", BooruOriginalIntent.Cancelled);
        DanbooruWatcherQueueAdmission admission = new(profile.Originals!, profile.Catalog, clock);

        Assert.Equal(DanbooruWatcherAdmissionResult.KeptCancelled,
            await admission.AdmitAsync(claim, "w-1", "cat", CancellationToken.None));
        BooruPostRecord record = Assert.Single(profile.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Cancelled, record.OriginalIntent);
        Assert.Equal("w-1", Assert.Single(record.Findings).WatcherId);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task BackgroundLoopChecksRegularlyOneAtATimeWithStartSpacing()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        search.Publish("dog", 200);
        TimeSpan spacing = TimeSpan.FromSeconds(5);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock,
            Options(spacing));
        scheduler.Load();
        scheduler.AddWatcher("cat");
        scheduler.AddWatcher("dog");
        await scheduler.StartAsync();

        await TestWait.UntilAsync(() => search.CallCount == 1 && clock.PendingTimers > 0);
        clock.Advance(spacing);
        await TestWait.UntilAsync(() => search.CallCount == 2 && clock.PendingTimers > 0);
        Assert.Equal(2, search.CallCount);
        clock.Advance(Interval - spacing - TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.Equal(2, search.CallCount);
        for (int round = 0; round < 3; round++)
        {
            int before = search.CallCount;
            clock.Advance(TimeSpan.FromSeconds(1) + (round == 0 ? TimeSpan.Zero : Interval - spacing));
            await TestWait.UntilAsync(() => search.CallCount == before + 1 && clock.PendingTimers > 0);
            clock.Advance(spacing);
            await TestWait.UntilAsync(() => search.CallCount == before + 2 && clock.PendingTimers > 0);
        }
        await scheduler.StopAsync();

        Assert.Equal(1, search.MaxConcurrent);
        Assert.Equal(8, search.CallCount);
        foreach (string query in new[] { "cat", "dog" })
        {
            DateTimeOffset[] times = search.Calls.Where(call => call.Query == query).Select(call => call.At).ToArray();
            for (int index = 1; index < times.Length; index++) Assert.True(times[index] - times[index - 1] >= Interval);
        }
        DateTimeOffset[] all = search.Calls.Select(call => call.At).ToArray();
        for (int index = 1; index < all.Length; index++) Assert.True(all[index] - all[index - 1] >= spacing);
    }

    [Fact]
    public async Task ConcurrentManualChecksAndTheLoopNeverOverlap()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        foreach (string query in new[] { "a", "b", "c" }) search.Publish(query, 10);
        SemaphoreSlim release = new(0);
        search.Hold = async (_, _, token) => await release.WaitAsync(token);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string[] ids = new[] { "a", "b", "c" }.Select(query => scheduler.AddWatcher(query).Watcher!.WatcherId).ToArray();
        await scheduler.StartAsync();
        Task<DanbooruWatcherCheckSummary>[] manual = ids.Select(scheduler.CheckNowAsync).ToArray();
        for (int index = 0; index < 3; index++)
        {
            await TestWait.UntilAsync(() => search.CallCount == index + 1);
            await Task.Delay(20);
            Assert.Equal(index + 1, search.CallCount);
            release.Release();
        }
        await Task.WhenAll(manual).WaitAsync(TestWait.Timeout);
        await scheduler.StopAsync();
        Assert.Equal(1, search.MaxConcurrent);
        Assert.Equal(3, search.CallCount);
        Assert.Equal(3, search.Calls.Select(call => call.Query).Distinct().Count());
    }

    [Fact]
    public async Task ShutdownDuringACheckLeavesAConsistentResumableRecord()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(140, 40));
        clock.Advance(Interval);
        TaskCompletionSource onPage2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, before, token) =>
        {
            if (before != "121") return; // page 2
            onPage2.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await scheduler.StartAsync();
        await onPage2.Task.WaitAsync(TestWait.Timeout);
        await scheduler.StopAsync().WaitAsync(TestWait.Timeout);

        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Active", stored.State);
        Assert.Equal(2, stored.NextPage);
        Assert.Equal("121", stored.CursorBeforePostId);
        Assert.Equal("100", stored.CheckStartHighWaterMark);
        Assert.Equal(20, admission.QueuedCount);
        Assert.Equal("scheduler_stopped", scheduler.AddWatcher("dog").Code);

        search.Hold = null;
        DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal(DanbooruWatcherCheckKind.Completed, (await restarted.RunNextAsync())!.Kind);
        Assert.Equal(40, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task HostStartRecoversWatchersAndPendingOriginalsBeforeTheLoopAndStopIsIdempotent()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        // A pending original from the previous session and a watcher left in Checking by a crash.
        BooruPostClaim pending = cdn.Claim(7);
        profile.Catalog.UpsertPost(pending, T0);
        profile.Catalog.AddFinding("danbooru", "7", BooruFindingKind.Watcher, "w-old", "cat", T0);
        profile.Catalog.SetOriginalIntent("danbooru", "7", BooruOriginalIntent.Requested);
        Assert.True(DanbooruWatcher.TryCreate("w-old", "cat", T0, out DanbooruWatcher? created, out _));
        DanbooruWatcher checking = DanbooruWatcher.ApplyPage(DanbooruWatcher.BeginCheck(created!, T0).Watcher,
            new DanbooruSearchPageObservation("danbooru", "cat", 1, ["5"], true), T0).Watcher;
        checking = DanbooruWatcher.BeginCheck(checking, T0.AddMinutes(1)).Watcher;
        profile.Catalog.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(checking));

        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 5);
        DanbooruWatcherHost host = new(profile.Catalog, profile.Originals!, search, clock, Options());
        await host.StartAsync();
        await TestWait.UntilAsync(() => profile.Catalog.Read().Posts.Single().LocalAssetId != null);
        Assert.Equal([SchedulerCdn.OriginalUrl("7")], cdn.Requests.ToArray());
        Assert.True(host.Scheduler.GetSnapshot().Running);
        // Wait for the check to finish, not merely to start: CallCount == 1 only proves the first
        // search was issued, and under parallel load the completion (and its persisted state) can
        // land later. Waiting on the durable state keeps this deterministic.
        await TestWait.UntilAsync(() => profile.Catalog.Read().Watchers.Single().State == "Active");
        Assert.Equal(1, search.CallCount);

        await host.StopAsync();
        await host.StopAsync();
        Assert.False(host.Scheduler.GetSnapshot().Running);
        Assert.Equal("scheduler_stopped", host.Scheduler.Pause("w-old").Code);
    }

    [Fact]
    public async Task UnreadableCatalogIsAVisibleFaultAndNothingIsSent()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        File.WriteAllText(profile.Catalog.FilePath, "{ not json");
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        await scheduler.StartAsync();
        Assert.Equal("catalog_unreadable", scheduler.GetSnapshot().FaultCode);
        Assert.False(scheduler.GetSnapshot().Running);
        Assert.False(scheduler.AddWatcher("cat").Accepted);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(0, search.CallCount);
    }

    [Fact]
    public async Task DuplicateAndInvalidQueriesAreRejectedWithoutCreatingAWatcher()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, new FakeDanbooruSearch(clock), new CountingAdmission(), clock, Options());
        Assert.Equal("scheduler_not_started", scheduler.AddWatcher("cat").Code);
        scheduler.Load();
        Assert.True(scheduler.AddWatcher("cat").Accepted);
        DanbooruWatcherCommandResult duplicate = scheduler.AddWatcher("cat");
        Assert.Equal("watcher_exists", duplicate.Code);
        Assert.NotNull(duplicate.Watcher);
        Assert.Equal("invalid_query", scheduler.AddWatcher("a b c d").Code);
        Assert.Single(profile.Catalog.Read().Watchers);
        await Task.CompletedTask;
    }
}
