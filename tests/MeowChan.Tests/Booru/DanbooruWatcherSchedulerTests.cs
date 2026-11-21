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
        using SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 100, 99, 98);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search,
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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

    [Theory]
    [InlineData("repeated", "cursor_did_not_advance")]
    [InlineData("at-cursor", "cursor_did_not_advance")]
    [InlineData("empty", "empty_page_did_not_advance")]
    public async Task ACursorPageThatDoesNotMoveBelowItsCursorStopsVisiblyKeepsTheCursorAndResumesWithoutGaps(
        string shape, string expectedCode)
    {
        using TempDirectory directory = new();
        SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(140, 40)); // 140..101: two full new pages, then the known post 100
        clock.Advance(Interval);
        search.Next((_, _) => null); // the newest page answers normally (140..121)
        // A misbehaving provider answers the page below 121 with posts that are not below the cursor, or
        // with an empty page although the results did not end.
        search.Next((query, before) => new DanbooruWatcherSearchSnapshot(query, before, shape switch
        {
            "repeated" => Range(125, 20).Select(FakeDanbooruSearch.MinimalClaim).ToArray(),
            "at-cursor" => Range(121, 20).Select(FakeDanbooruSearch.MinimalClaim).ToArray(),
            _ => []
        }, false));

        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal(expectedCode, failed.Code);
        Assert.Equal(new string?[] { null, null, "121" }, search.Cursors);
        Assert.Equal(20, admission.QueuedCount); // only the newest page, nothing from the rejected page
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Error", stored.State);
        Assert.Equal("121", stored.CursorBeforePostId);
        Assert.Equal(expectedCode, scheduler.GetStatus(id)!.ErrorCode);
        Assert.Equal(DanbooruWatcherActivity.Error, scheduler.GetStatus(id)!.Activity);

        // The error survives a restart with its cursor and is never retried automatically.
        await scheduler.DisposeAsync();
        await profile.DisposeAsync();
        await using SchedulerProfile reopened = new(directory.Path);
        await using DanbooruWatcherScheduler restarted = new(reopened.Catalog, search, admission, clock, Options());
        restarted.Load();
        clock.Advance(Interval);
        Assert.Null(await restarted.RunNextAsync());
        Assert.Equal(3, search.CallCount);

        // An explicit resume continues below the stored cursor: no gap, no duplicate, one HWM.
        restarted.Resume(id);
        DanbooruWatcherCheckSummary resumed = (await restarted.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal("121", search.Cursors[3]);
        Assert.Equal(40, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        Assert.Equal("140", reopened.Catalog.Read().Watchers.Single().HighWaterMark);
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler crashed = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler crashed = new(profile.Catalog, search, admission, clock, Options());
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

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
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
    public async Task LoweredCapBoundsPersistedFarFutureCooldownAndSurvivesRestart()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock)
        {
            ProviderCooldownMaximum = TimeSpan.FromHours(168)
        };
        await using DanbooruWatcherScheduler first = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        first.Load();
        string id = first.AddWatcher("cat").Watcher!.WatcherId;
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        profile.Catalog.UpsertWatcher(stored with
        {
            State = "Cooldown",
            CooldownUntilUtc = clock.GetUtcNow().AddYears(20),
            ErrorCode = "provider_cooldown"
        });

        search.ProviderCooldownMaximum = TimeSpan.FromHours(1);
        await using DanbooruWatcherScheduler bounded = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        bounded.Load();
        DateTimeOffset expected = clock.GetUtcNow() + TimeSpan.FromHours(1);
        Assert.Equal(expected, bounded.GetStatus(id)!.CooldownUntilUtc);
        Assert.Equal(expected, Assert.Single(profile.Catalog.Read().Watchers).CooldownUntilUtc);

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        restarted.Load();
        Assert.Equal(expected, restarted.GetStatus(id)!.CooldownUntilUtc);
    }

    [Fact]
    public async Task ResetProviderCooldownPersistsResumeWithoutSendingOrChangingWatcherData()
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
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        DateTimeOffset retryAt = clock.GetUtcNow() + TimeSpan.FromDays(2);
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));
        clock.Advance(Interval);
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await scheduler.RunNextAsync())!.Kind);
        Assert.True(scheduler.Pause(id).Accepted);
        int callsBeforeReset = search.CallCount;
        BooruWatcherRecord before = Assert.Single(profile.Catalog.Read().Watchers);

        DanbooruWatcherCommandResult reset = scheduler.ResetProviderCooldown();
        Assert.True(reset.Accepted, reset.Code);
        Assert.Equal(callsBeforeReset, search.CallCount);
        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Paused, status.State);
        Assert.Equal(DanbooruWatcherActivity.Paused, status.Activity);
        Assert.Null(status.CooldownUntilUtc);
        BooruWatcherRecord after = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal("Paused", after.State);
        Assert.Null(after.CooldownUntilUtc);
        Assert.Equal(before.HighWaterMark, after.HighWaterMark);
        Assert.Equal(before.CursorBeforePostId, after.CursorBeforePostId);

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal(DanbooruWatcherState.Paused, restarted.GetStatus(id)!.State);
        Assert.Null(restarted.GetStatus(id)!.CooldownUntilUtc);
    }

    [Fact]
    public async Task APausedWatcherKeepsTheProviderWaitAcrossARestartAndResumesIntoIt()
    {
        // CX2-R2: Pause cleared CooldownUntilUtc, the only durable copy of the provider's retry time. After a
        // restart Load found no Cooldown record, nothing restored the wait, and after Resume the next check asked
        // the provider again long before the time it had named.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        DateTimeOffset retryAt = T0 + TimeSpan.FromMinutes(30);
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await scheduler.RunNextAsync())!.Kind);

        DanbooruWatcherCommandResult paused = scheduler.Pause(id);
        Assert.True(paused.Accepted, paused.Code);
        Assert.Equal(DanbooruWatcherActivity.Paused, paused.Watcher!.Activity);
        Assert.Null(paused.Watcher.NextCheckAtUtc);
        Assert.Equal("watcher_already_paused", scheduler.Pause(id).Code);
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal("Cooldown", stored.State);
        Assert.Equal("Paused", stored.ResumeState);
        Assert.Equal(retryAt, stored.CooldownUntilUtc);

        // Five minutes later the app restarts; the transport cooldown is gone with the process.
        clock.Advance(TimeSpan.FromMinutes(5));
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal(retryAt, restarted.GetSnapshot().ProviderCooldownUntilUtc);
        Assert.Equal(DanbooruWatcherActivity.Paused, restarted.GetStatus(id)!.Activity);

        // Resume goes back into the wait instead of being refused or asking early.
        DanbooruWatcherCommandResult resumed = restarted.Resume(id);
        Assert.True(resumed.Accepted, resumed.Code);
        Assert.Equal(DanbooruWatcherActivity.Cooldown, resumed.Watcher!.Activity);
        Assert.Equal(DanbooruWatcherState.Active, resumed.Watcher.ResumeState);
        Assert.Equal(retryAt, resumed.Watcher.NextCheckAtUtc);
        Assert.Null(await restarted.RunNextAsync());
        DanbooruWatcherCheckSummary refused = await restarted.CheckNowAsync(id);
        Assert.Equal("provider_cooldown", refused.Code);
        Assert.Equal(retryAt, refused.RetryAtUtc);
        Assert.Equal(1, search.CallCount);

        clock.Advance(retryAt - clock.GetUtcNow());
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await restarted.RunNextAsync())!.Kind);
        Assert.Equal(2, search.CallCount);
        Assert.Equal(DanbooruWatcherState.Active, restarted.GetStatus(id)!.State);
    }

    [Fact]
    public async Task ShortProviderRetryTimesAreRaisedToTheDeferralFloor()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal("watcher_removed", (await restarted.CheckNowAsync(removed)).Code);
        Assert.Equal(DanbooruWatcherState.Paused, restarted.GetStatus(paused)!.State);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await restarted.RunNextAsync());
        Assert.Equal(calls, search.CallCount);
    }

    [Fact]
    public async Task PausingAnErrorWatcherClearsTheErrorAndPersists()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        search.Next((_, _) => new DanbooruWatcherSearchFailed("access-denied", 403));

        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal("access-denied", failed.Code);
        Assert.Equal(DanbooruWatcherState.Error, scheduler.GetStatus(id)!.State);

        DanbooruWatcherCommandResult paused = scheduler.Pause(id);
        Assert.True(paused.Accepted, paused.Code);
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(id)!.State);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single(watcher => watcher.WatcherId == id);
        Assert.Equal("Paused", stored.State);
        Assert.Null(stored.ErrorCode);
    }

    [Fact]
    public async Task AutomaticStartPersistenceFailureBacksOffInsteadOfSpinning()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherSchedulerOptions options = Options();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, options);
        scheduler.Load();
        scheduler.AddWatcher("cat");

        // Fail only the next scheduler mutation after the watcher has been created.  This models a
        // full catalog, a lost lease or a transient disk failure at the begin-check persist.
        int persistFailures = 0;
        profile.Catalog.BeforeAdmittedMutationForTests = _ =>
        {
            if (Interlocked.Exchange(ref persistFailures, 1) == 0)
            {
                // Model a slow synchronous catalog write without sleeping: the failure is observed
                // after the configured retry interval has already elapsed.
                clock.Advance(options.UnavailableRetry + TimeSpan.FromSeconds(1));
                throw new IOException("disk");
            }
        };
        DanbooruWatcherCheckSummary failed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, failed.Kind);
        Assert.Equal("persist_failed", failed.Code);
        Assert.Equal(0, search.CallCount);

        // The watcher is still due, but the automatic path must wait for the visible backoff rather
        // than immediately retrying the same non-provider operation.
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(0, search.CallCount);

        // The quiet period starts when the failed write returns, not when the attempt began.
        clock.Advance(options.UnavailableRetry - TimeSpan.FromSeconds(1));
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(0, search.CallCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Equal(1, search.CallCount);
    }

    [Fact]
    public async Task ALocalPersistBackoffIsNoProviderCooldownAndLeavesManualChecksPossible()
    {
        // CX2-R1: the backoff after a failed begin-check write landed on the provider-wide _backoffUntil. Every
        // watcher then showed a provider cooldown and CheckNowAsync was refused with provider_cooldown ("Danbooru
        // asked MeowChan to wait") although the cause was local, and the one action that would show the real cause
        // was blocked for the whole retry period.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 10);
        DanbooruWatcherSchedulerOptions options = Options();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, options);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;

        int failures = 2; // the automatic start, then the first manual check
        profile.Catalog.BeforeAdmittedMutationForTests = _ =>
        {
            if (Interlocked.Decrement(ref failures) >= 0) throw new IOException("disk");
        };
        DanbooruWatcherCheckSummary automatic = (await scheduler.RunNextAsync())!;
        Assert.Equal("persist_failed", automatic.Code);
        Assert.False(automatic.Manual);

        // Automatic starts keep quiet, but nothing claims a provider pause.
        Assert.Null(await scheduler.RunNextAsync());
        DanbooruWatcherSchedulerSnapshot snapshot = scheduler.GetSnapshot();
        Assert.Null(snapshot.ProviderCooldownUntilUtc);
        DanbooruWatcherStatus status = Assert.Single(snapshot.Watchers);
        Assert.Equal(DanbooruWatcherActivity.Scheduled, status.Activity);
        Assert.Null(status.CooldownUntilUtc);
        Assert.Equal(T0 + options.UnavailableRetry, status.NextCheckAtUtc);

        // A manual check is accepted and fails honestly while the store still refuses the write ...
        Task<DanbooruWatcherCheckSummary> stillBroken = scheduler.CheckNowAsync(id);
        Assert.False(stillBroken.IsCompleted, "The manual check was refused before it could run.");
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, (await scheduler.RunNextAsync())!.Kind);
        DanbooruWatcherCheckSummary honest = await stillBroken.WaitAsync(TestWait.Timeout);
        Assert.Equal("persist_failed", honest.Code);
        Assert.True(honest.Manual);
        Assert.Equal(0, search.CallCount);

        // ... and runs once the store is writable again; that write also ends the local backoff.
        Task<DanbooruWatcherCheckSummary> repaired = scheduler.CheckNowAsync(id);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await repaired.WaitAsync(TestWait.Timeout)).Kind);
        Assert.Equal(1, search.CallCount);
        Assert.Equal(T0 + Interval, scheduler.GetStatus(id)!.NextCheckAtUtc);
        search.Publish("dog", 20);
        scheduler.AddWatcher("dog");
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Equal(2, search.CallCount);
    }

    [Fact]
    public async Task NonRetryableFailureKeepsTheWatcherInErrorUntilResume()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
    public async Task EveryProviderRequestOfABaselineAndAMultiPageCatchUpCarriesExactlyTheUsersQuery()
    {
        // No implicit rating, order or other token may reach the provider on any page of any check.
        const string query = "cat -bird";
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish(query, 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher(query);

        await scheduler.RunNextAsync();
        search.Publish(query, Range(160, 60));
        clock.Advance(Interval);
        DanbooruWatcherCheckSummary check = (await scheduler.RunNextAsync())!;

        Assert.True(check.PagesRead > 1);
        Assert.True(search.Calls.Count > 2);
        Assert.All(search.Calls, call => Assert.Equal(query, call.Query));
    }

    [Fact]
    public async Task AllPagesOfOneCheckShareOneSearchSessionAndEachCheckGetsItsOwn()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        // The newest page, then every later page below the lowest id seen so far (page=b<id>).
        Assert.Equal(new string?[] { null, "141", "121", "101" }, search.Calls.Skip(1).Select(call => call.Before).ToArray());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
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
    public async Task ACancellationFromAForeignTokenDuringAPageIsSourceUnavailableNotAStuckCheck()
    {
        // PluginWorkerClient links the caller's token with the worker lifetime: a worker stop, eviction or
        // plugin deactivation mid-page throws an OperationCanceledException whose token is not the check's.
        // Before the fix it passed both filters, left the persisted watcher in Checking and so blocked the
        // selection of every watcher until the next restart (CX-R1).
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherSchedulerOptions options = Options();
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, options);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        search.Hold = (_, _, _) => throw new OperationCanceledException("worker lifetime ended");

        DanbooruWatcherCheckSummary summary = (await scheduler.RunNextAsync().WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, summary.Kind);
        Assert.Equal("source_unavailable", summary.Code);
        Assert.Null(scheduler.GetSnapshot().CheckingWatcherId);
        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Active, status.State);
        Assert.NotEqual(DanbooruWatcherActivity.Checking, status.Activity);
        Assert.Equal("Active", profile.Catalog.Read().Watchers.Single().State);
        Assert.Equal(1, search.SessionsDisposed);

        // Answered, not parked: the backoff rejects the manual check now and the loop retries after it.
        search.Hold = null;
        DanbooruWatcherCheckSummary manual = await scheduler.CheckNowAsync(id).WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, manual.Kind);
        clock.Advance(options.UnavailableRetry);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Equal(2, search.CallCount);
    }

    [Fact]
    public async Task TheLoopAnswersAManualRequestAndFreesTheWatcherWhenItsCheckEndsInAForeignCancellation()
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
        search.Hold = (query, _, _) => query == "cat" ? throw new OperationCanceledException("worker lifetime ended") : Task.CompletedTask;
        // Manual first, before the loop exists: the loop's first pick is the manual request for "cat".
        Task<DanbooruWatcherCheckSummary> manual = scheduler.CheckNowAsync(cat);
        TaskCompletionSource catLeftChecking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Changed += () =>
        {
            if (scheduler.GetStatus(cat) is { } status && status.Activity != DanbooruWatcherActivity.Checking
                && status.LastCheck is not null)
                catLeftChecking.TrySetResult();
        };
        await scheduler.StartAsync();

        DanbooruWatcherCheckSummary answered = await manual.WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, answered.Kind);
        await catLeftChecking.Task.WaitAsync(TestWait.Timeout);
        await scheduler.StopAsync();

        Assert.Null(scheduler.GetSnapshot().CheckingWatcherId);
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus(cat)!.State);
        Assert.Equal(DanbooruWatcherState.Active, scheduler.GetStatus(dog)!.State);
        Assert.All(profile.Catalog.Read().Watchers, record => Assert.Equal("Active", record.State));
        // The unavailable search path backs the whole provider off; no further request was sent meanwhile.
        Assert.Equal(1, search.CallCount);
    }

    [Fact]
    public async Task ADefectInsideACheckStopsTheWatcherVisiblyInsteadOfLeavingItChecking()
    {
        // Any other exception (here: a fault raised on the scheduler's own clock read after the page arrived)
        // must end the check with a visible Error that Resume clears; the watcher must never stay Checking.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        int armed = 0;
        search.Hold = (_, _, _) => { Interlocked.CompareExchange(ref armed, 1, 0); return Task.CompletedTask; };
        clock.OnGetUtcNow = () =>
        {
            // The first time read after the page was answered is the scheduler's, under its lock.
            if (Interlocked.CompareExchange(ref armed, 2, 1) == 1) throw new InvalidOperationException("defect");
        };

        DanbooruWatcherCheckSummary summary = (await scheduler.RunNextAsync().WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, summary.Kind);
        Assert.Equal("check_faulted", summary.Code);
        Assert.Null(scheduler.GetSnapshot().CheckingWatcherId);
        DanbooruWatcherStatus status = scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Error, status.State);
        Assert.Equal("check_faulted", status.ErrorCode);
        Assert.Same(summary, status.LastCheck);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single();
        Assert.Equal("Error", stored.State);
        Assert.Equal("check_faulted", stored.ErrorCode);
        Assert.Equal(1, search.SessionsDisposed);

        Assert.True(scheduler.Resume(id).Accepted);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
        Assert.Equal(2, search.CallCount);
    }

    [Fact]
    public async Task UserCancelledOriginalIsNotRequeuedByAWatcherButItsFindingIsKept()
    {
        using TempDirectory directory = new();
        using SchedulerCdn cdn = new();
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
        // Nothing is due yet. Wait (on a condition, not on real time) until the loop is idle on its timer again,
        // then the call count must be unchanged (CX-R6).
        clock.Advance(Interval - spacing - TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => clock.PendingTimers > 0);
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
        // CX-R6: no real-time polling or sleeping, and no reliance on thread order. Every search call announces its
        // start and waits for an explicit release; the number of releases already given is recorded at every start,
        // so a call that starts while an earlier one is still held shows up as a wrong count whatever the load.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        foreach (string query in new[] { "a", "b", "c" }) search.Publish(query, 10);
        using SemaphoreSlim release = new(0);
        using SemaphoreSlim started = new(0);
        List<int> releasesBeforeStart = new();
        int releases = 0;
        search.Hold = async (_, _, token) =>
        {
            lock (releasesBeforeStart) releasesBeforeStart.Add(Volatile.Read(ref releases));
            started.Release();
            await release.WaitAsync(token);
        };
        async Task AwaitStartAsync() =>
            Assert.True(await started.WaitAsync(TestWait.Timeout), "No further search call started.");
        void ReleaseOne()
        {
            Interlocked.Increment(ref releases);
            release.Release();
        }
        async Task ReleaseNextAsync()
        {
            await AwaitStartAsync();
            ReleaseOne();
        }
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string[] queries = { "a", "b", "c" };
        string[] ids = queries.Select(query => scheduler.AddWatcher(query).Watcher!.WatcherId).ToArray();

        // Phase 1, manual first: requests queued after Load and before StartAsync are served before any automatic
        // selection, in request order. That ordering is deterministic; a request issued after StartAsync races the
        // first pick by design.
        Task<DanbooruWatcherCheckSummary>[] manual = ids.Select(scheduler.CheckNowAsync).ToArray();
        await scheduler.StartAsync();
        for (int index = 0; index < 3; index++) await ReleaseNextAsync();
        DanbooruWatcherCheckSummary[] baselines = await Task.WhenAll(manual).WaitAsync(TestWait.Timeout);
        Assert.All(baselines, summary => Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, summary.Kind));
        Assert.Equal(queries, search.Calls.Select(call => call.Query));

        // Phase 2, manual requests while the loop runs an automatic check: the running watcher is refused, the
        // other two are served one after another and are then no longer due, so no further call follows.
        clock.Advance(Interval);
        await AwaitStartAsync();
        int running = Array.IndexOf(queries, search.Calls[3].Query);
        Task<DanbooruWatcherCheckSummary>[] during = ids.Select(scheduler.CheckNowAsync).ToArray();
        Assert.True(during[running].IsCompleted, "A manual request for the running watcher was queued.");
        DanbooruWatcherCheckSummary refused = await during[running];
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, refused.Kind);
        Assert.Equal("check_already_running", refused.Code);
        ReleaseOne();
        for (int index = 0; index < 2; index++) await ReleaseNextAsync();
        DanbooruWatcherCheckSummary[] served = await Task.WhenAll(during).WaitAsync(TestWait.Timeout);
        Assert.Equal(2, served.Count(summary => summary.Kind == DanbooruWatcherCheckKind.Completed && summary.Manual));
        await TestWait.UntilAsync(() => scheduler.GetSnapshot().CheckingWatcherId is null && clock.PendingTimers > 0);
        await scheduler.StopAsync();

        lock (releasesBeforeStart) Assert.Equal(Enumerable.Range(0, 6), releasesBeforeStart);
        Assert.Equal(1, search.MaxConcurrent);
        Assert.Equal(6, search.CallCount);
        Assert.Equal(queries, search.Calls.Skip(3).Select(call => call.Query).Order());
    }

    [Fact]
    public async Task ManualRequestForAWatcherAlreadyPickedForACheckIsRejectedNotQueuedAgain()
    {
        // The loop (or a direct step) picks a watcher under the lock and only later registers the running
        // check. A manual request for the same watcher in that gap must see the check as already running;
        // otherwise it queues and the watcher is checked a second time right after the first check.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 10);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;

        using ManualResetEventSlim proceed = new(false);
        Task<DanbooruWatcherCheckSummary?> step = ParkAStepBetweenPickAndRegistration(scheduler, clock, proceed,
            out Task parked);
        await parked.WaitAsync(TestWait.Timeout);

        Task<DanbooruWatcherCheckSummary> manual = scheduler.CheckNowAsync(id);
        bool completedImmediately = manual.IsCompleted;
        proceed.Set();
        DanbooruWatcherCheckSummary? first = await step.WaitAsync(TestWait.Timeout);

        Assert.True(completedImmediately, "The manual request was queued behind the check that was already picked.");
        DanbooruWatcherCheckSummary rejected = await manual;
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, rejected.Kind);
        Assert.Equal("check_already_running", rejected.Code);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, first!.Kind);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(1, search.CallCount);
    }

    [Fact]
    public async Task ADirectStepWhileAnotherStepIsBetweenPickAndRegistrationRunsNothing()
    {
        // CX-R10: RunNextAsync checked _running under the lock, RunCheckAsync set it without checking again.
        // A second step in the gap used to start a second check of the same (or another) watcher: two
        // checks in flight, the first no longer cancellable through Pause/Remove.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 10);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");

        using ManualResetEventSlim proceed = new(false);
        Task<DanbooruWatcherCheckSummary?> step = ParkAStepBetweenPickAndRegistration(scheduler, clock, proceed,
            out Task parked);
        await parked.WaitAsync(TestWait.Timeout);

        DanbooruWatcherCheckSummary? second = await scheduler.RunNextAsync().WaitAsync(TestWait.Timeout);
        proceed.Set();
        DanbooruWatcherCheckSummary? first = await step.WaitAsync(TestWait.Timeout);

        Assert.Null(second);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, first!.Kind);
        Assert.Equal(1, search.CallCount);
        Assert.Equal(1, search.MaxConcurrent);
        Assert.Null(await scheduler.RunNextAsync());
    }

    [Fact]
    public async Task AStepThatPickedWorkBeforeStopSendsNothingAfterIt()
    {
        // CX-R10: StopAsync can run between the pick and the registration of the check. It found no running
        // check to cancel or drain, and RunCheckAsync did not look at _stopping, so the step started a check
        // (and sent) after StopAsync had returned and the profile could be released.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 10);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;

        using ManualResetEventSlim proceed = new(false);
        Task<DanbooruWatcherCheckSummary?> step = ParkAStepBetweenPickAndRegistration(scheduler, clock, proceed,
            out Task parked);
        await parked.WaitAsync(TestWait.Timeout);

        await scheduler.StopAsync().WaitAsync(TestWait.Timeout);
        proceed.Set();
        DanbooruWatcherCheckSummary? late = await step.WaitAsync(TestWait.Timeout);

        Assert.Equal(DanbooruWatcherCheckKind.Rejected, late!.Kind);
        Assert.Equal("scheduler_stopped", late.Code);
        Assert.Equal(0, search.CallCount);
        Assert.Equal(0, search.SessionsOpened);
        Assert.Null(scheduler.GetSnapshot().CheckingWatcherId);
        Assert.Equal("Active", profile.Catalog.Read().Watchers.Single(watcher => watcher.WatcherId == id).State);
    }

    /// <summary>
    /// Starts a direct step on its own thread and parks it after the pick and before the running check is
    /// registered. The first time read of the step picks the work (under the lock); the second read is the
    /// start time of the check, after the pick. The probe parks the stepping thread on that second read.
    /// </summary>
    private static Task<DanbooruWatcherCheckSummary?> ParkAStepBetweenPickAndRegistration(DanbooruWatcherScheduler scheduler,
        SchedulerClock clock, ManualResetEventSlim proceed, out Task parked)
    {
        TaskCompletionSource parkedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int stepThread = -1;
        int reads = 0;
        clock.OnGetUtcNow = () =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref stepThread)) return;
            if (Interlocked.Increment(ref reads) != 2) return;
            parkedSignal.TrySetResult();
            proceed.Wait(TestWait.Timeout);
        };
        parked = parkedSignal.Task;
        return Task.Run(() =>
        {
            Volatile.Write(ref stepThread, Environment.CurrentManagedThreadId);
            return scheduler.RunNextAsync();
        });
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
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
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
        restarted.Load();
        Assert.Equal(DanbooruWatcherCheckKind.Completed, (await restarted.RunNextAsync())!.Kind);
        Assert.Equal(40, admission.QueuedCount);
        Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsAndDrainsADirectCheckBeforeReturning(bool startLoopDuringCheck)
    {
        // CX-R10 (from 5bb7c1b7): a direct step started before the loop has a token that is not linked to the
        // lifetime. StopAsync used to return while that check kept sending, after the profile was released.
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();
        search.Publish("cat", Range(140, 40));
        clock.Advance(Interval);

        TaskCompletionSource onPage2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, before, token) =>
        {
            if (before != "121") return;
            onPage2.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                cancelled.TrySetResult();
                // Model asynchronous transport cleanup: cancellation alone is not a drain.
                await releaseCleanup.Task;
            }
        };
        using CancellationTokenSource fallback = new();
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync(fallback.Token);
        try
        {
            await onPage2.Task.WaitAsync(TestWait.Timeout);
            if (startLoopDuringCheck) await scheduler.StartAsync();
            Task stopping = scheduler.StopAsync();
            Assert.False(stopping.IsCompleted);
            await cancelled.Task.WaitAsync(TestWait.Timeout);
            Assert.False(stopping.IsCompleted);
            releaseCleanup.TrySetResult();
            await stopping.WaitAsync(TestWait.Timeout);

            // Stop itself, not a later await of RunNextAsync, must guarantee durable recovery.
            Assert.Null(scheduler.GetSnapshot().CheckingWatcherId);
            Assert.Equal(search.SessionsOpened, search.SessionsDisposed);
            BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
            Assert.Equal("Active", stored.State);
            Assert.Equal(2, stored.NextPage);
            Assert.Equal("121", stored.CursorBeforePostId);
            Assert.Equal("100", stored.CheckStartHighWaterMark);
            Assert.Equal(20, admission.QueuedCount);
            Assert.Equal(DanbooruWatcherCheckKind.Cancelled, (await running.WaitAsync(TestWait.Timeout))!.Kind);
            clock.Advance(Interval);
            Assert.Null(await scheduler.RunNextAsync());

            search.Hold = null;
            await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
            restarted.Load();
            Assert.Equal(DanbooruWatcherCheckKind.Completed, (await restarted.RunNextAsync())!.Kind);
            Assert.Equal("121", search.Calls[3].Before);
            Assert.Equal(40, admission.QueuedCount);
            Assert.All(admission.PerPost.Values, count => Assert.Equal(1, count));
        }
        finally
        {
            fallback.Cancel();
            releaseCleanup.TrySetResult();
            await running.WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task HostStartRecoversWatchersAndPendingOriginalsBeforeTheLoopAndStopIsIdempotent()
    {
        using TempDirectory directory = new();
        using SchedulerCdn cdn = new();
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
        await using DanbooruWatcherHost host = new(profile.Catalog, profile.Originals!, search, clock, Options());
        await host.StartAsync();
        await TestWait.UntilAsync(() => profile.Catalog.Read().Posts.Single().LocalAssetId != null);
        Assert.Equal([SchedulerCdn.OriginalUrl("7")], cdn.Requests.ToArray());
        Assert.True(host.Scheduler.GetSnapshot().Running);
        // Restart recovery restores Active before the loop issues a search.
        // Wait for the completed check and then verify its durable state.
        await TestWait.UntilAsync(() => host.Scheduler.GetSnapshot().Watchers.Single().LastCheck != null);
        Assert.Equal(DanbooruWatcherCheckKind.Completed, host.Scheduler.GetSnapshot().Watchers.Single().LastCheck!.Kind);
        Assert.Equal("Active", profile.Catalog.Read().Watchers.Single().State);
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
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, new FakeDanbooruSearch(clock), new CountingAdmission(), clock, Options());
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
