using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// F2: Danbooru watcher checks, watcher commands and pending-original recovery take part in the profile-wide exclusive
/// Suspend → Drain → Resume through <see cref="BooruProducerLifecycle"/>. Every test drives the real profile admission
/// kernel (an exclusive reservation is granted only after the Danbooru generation was drained and released) and an
/// in-memory provider; nothing opens a socket.
/// </summary>
public sealed class BooruProducerLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static DanbooruWatcherSchedulerOptions Options() => new() { Interval = Interval, CheckStartSpacing = TimeSpan.Zero };

    /// <summary>
    /// The participant order of <c>Registry.RunWithProfileExclusiveAsync</c> for this family: announce
    /// (CloseTransitionCommandIngress), reserve, synchronous cut, actual drain, release, grant.
    /// </summary>
    private static async Task<ProfileAdmissionToken> CutForExclusiveAsync(BooruProducerLifecycle lifecycle,
        ProfileAdmission admission, Func<Task>? otherFamilyDrain = null)
    {
        lifecycle.BeginTransition();
        Task<ProfileAdmissionToken> reservation = admission.EnterExclusiveAsync().AsTask();
        Task drained = lifecycle.CutAndDrainAsync();
        await Task.WhenAll(drained, otherFamilyDrain?.Invoke() ?? Task.CompletedTask).WaitAsync(TestWait.Timeout);
        // The drained generation still owns its Ordinary admission until it is released.
        Assert.False(reservation.IsCompleted);
        lifecycle.ReleaseDrained();
        return await reservation.WaitAsync(TestWait.Timeout);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- The lifecycle itself -----------------------------------------------------------------------

    [Fact]
    public async Task AGenerationIsCountedCutDrainedAndReleasedBeforeTheExclusiveGrantAndResumeOpensAFreshOne()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        ProfileAdmission admission = profile.Lease.Admission;
        BooruProducerLifecycle lifecycle = new(admission);

        Assert.Null(lifecycle.TryEnter()); // Prepared: nothing may start before the first open
        lifecycle.Open();
        Assert.Equal(1, lifecycle.Generation);
        BooruProducerUse use = lifecycle.TryEnter()!;
        Assert.True(use.IsCurrent);
        Assert.True(use.IsUncut);

        lifecycle.BeginTransition();
        Assert.Null(lifecycle.TryEnter()); // nothing new after the announcement
        Assert.True(use.IsCurrent);        // the running use keeps its generation until the cut
        Assert.False(use.IsUncut);
        Task<ProfileAdmissionToken> reservation = admission.EnterExclusiveAsync().AsTask();
        Task drained = lifecycle.CutAndDrainAsync();
        Assert.Equal(BooruProducerPhase.Suspended, lifecycle.Phase);
        await TestWait.UntilAsync(() => use.Cut.IsCancellationRequested, "the cut cancels the use");
        Assert.False(use.IsCurrent);
        Assert.False(drained.IsCompleted); // the use is still counted
        Assert.Throws<InvalidOperationException>(lifecycle.ReleaseDrained);
        // A use counted before the cut may still finish its terminal write with the generation admission.
        Assert.True(profile.Lease.RunStoreWrite(use.Admission, () => true));

        use.Dispose();
        await drained.WaitAsync(TestWait.Timeout);
        Assert.False(reservation.IsCompleted);
        lifecycle.ReleaseDrained();
        ProfileAdmissionToken exclusive = await reservation.WaitAsync(TestWait.Timeout);
        Assert.Throws<InvalidOperationException>(lifecycle.Open); // the kernel refuses an Ordinary writer during exclusive
        Assert.Null(lifecycle.TryEnter());

        exclusive.Dispose();
        lifecycle.Open(); // SafeToResume
        Assert.Equal(2, lifecycle.Generation);
        using BooruProducerUse fresh = lifecycle.TryEnter()!;
        Assert.Equal(2, fresh.Generation);
        Assert.False(use.IsCurrent); // an old use never becomes current again
    }

    [Fact]
    public void ATransitionRefusedBeforeItsCutKeepsTheSameGeneration()
    {
        using TempDirectory directory = new();
        SchedulerProfile profile = new(directory.Path);
        try
        {
            BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
            lifecycle.Open();
            using BooruProducerUse use = lifecycle.TryEnter()!;
            lifecycle.BeginTransition();
            lifecycle.AbortTransition();
            Assert.Equal(BooruProducerPhase.Open, lifecycle.Phase);
            Assert.Equal(1, lifecycle.Generation);
            Assert.True(use.IsUncut);
            Assert.False(use.Cut.IsCancellationRequested);
            using BooruProducerUse again = lifecycle.TryEnter()!;
            Assert.Equal(1, again.Generation);
            Assert.Throws<InvalidOperationException>(lifecycle.Open); // already open
        }
        finally { profile.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [Fact]
    public async Task RecoveryRequiredAndStopAreNeverReopened()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        Task drained = lifecycle.CutAndDrainAsync();
        await drained.WaitAsync(TestWait.Timeout);
        lifecycle.ReleaseDrained();
        lifecycle.RequireRecovery();
        Assert.Equal(BooruProducerPhase.RecoveryRequired, lifecycle.Phase);
        Assert.Throws<InvalidOperationException>(lifecycle.Open);
        Assert.Null(lifecycle.TryEnter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.EnterAsync()).WaitAsync(TestWait.Timeout);

        BooruProducerLifecycle stopped = new(profile.Lease.Admission);
        stopped.Open();
        BooruProducerUse use = stopped.TryEnter()!;
        Task stopping = stopped.StopAsync();
        Assert.Equal(BooruProducerPhase.Stopped, stopped.Phase);
        Assert.True(use.Cut.IsCancellationRequested || await WaitCancelled(use.Cut));
        Assert.False(stopping.IsCompleted); // the stop waits for the counted use
        use.Dispose();
        await stopping.WaitAsync(TestWait.Timeout);
        Assert.Throws<InvalidOperationException>(stopped.Open);
    }

    private static async Task<bool> WaitCancelled(CancellationToken token)
    {
        await TestWait.UntilAsync(() => token.IsCancellationRequested, "cancelled");
        return true;
    }

    // ---- Watcher checks in the cut ------------------------------------------------------------------

    [Fact]
    public async Task ExclusiveIsGrantedOnlyAfterABlockedWatcherPageWasCancelledAndPersistedAndResumeRunsOneFreshCheck()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options(), lifecycle);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        search.Publish("cat", 101, 102);
        clock.Advance(Interval);
        TaskCompletionSource entered = Signal();
        search.Hold = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);
        Assert.Equal(nameof(DanbooruWatcherState.Checking), Assert.Single(profile.Catalog.Read().Watchers).State);

        ProfileAdmissionToken exclusive = await CutForExclusiveAsync(lifecycle, profile.Lease.Admission);
        // The exclusive delegate starts only after the physical request ended and the check persisted its terminal
        // transition with the admission counted before the cut.
        Assert.True(running.IsCompleted);
        DanbooruWatcherCheckSummary cancelled = (await running)!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cancelled.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, cancelled.Code);
        Assert.Equal(1, search.Cancelled);
        Assert.Equal(search.SessionsOpened, search.SessionsDisposed); // the provider scope was disposed before the grant
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(nameof(DanbooruWatcherState.Active), stored.State);
        Assert.Equal("100", stored.HighWaterMark);
        Assert.Null(stored.ErrorCode);
        Assert.Equal(0, admission.QueuedCount);

        // During the exclusive operation nothing starts and nothing is written.
        search.Hold = null;
        int calls = search.CallCount;
        Assert.Null(await scheduler.RunNextAsync());
        DanbooruWatcherCheckSummary manual = await scheduler.CheckNowAsync(id).WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, manual.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, manual.Code);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, scheduler.Pause(id).Code);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, scheduler.AddWatcher("dog").Code);
        Assert.Equal(calls, search.CallCount);
        Assert.Equal(stored, Assert.Single(profile.Catalog.Read().Watchers));

        // SafeToResume: a fresh generation; the page replays once and the watcher advances normally.
        exclusive.Dispose();
        lifecycle.Open();
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal(2, resumed.Queued);
        Assert.Equal(calls + 1, search.CallCount);
        Assert.Equal("102", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);
        Assert.Null(await scheduler.RunNextAsync());
    }

    [Fact]
    public async Task APageAnswerThatArrivesAfterTheCutIsDiscardedAndWritesNoProgress()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        CountingAdmission admission = new();
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options(), lifecycle);
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();

        search.Publish("cat", 101, 102);
        clock.Advance(Interval);
        TaskCompletionSource entered = Signal(), release = Signal();
        // The provider answer is already on its way: it ignores the cancellation and arrives after the cut.
        search.Hold = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);

        lifecycle.BeginTransition();
        Task<ProfileAdmissionToken> reservation = profile.Lease.Admission.EnterExclusiveAsync().AsTask();
        Task drained = lifecycle.CutAndDrainAsync();
        await Task.Delay(50);
        Assert.False(drained.IsCompleted); // the check still owns its use: the drain really waits for it
        release.SetResult();
        await drained.WaitAsync(TestWait.Timeout);
        lifecycle.ReleaseDrained();
        using ProfileAdmissionToken exclusive = await reservation.WaitAsync(TestWait.Timeout);

        DanbooruWatcherCheckSummary summary = (await running)!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, summary.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, summary.Code);
        Assert.Equal(0, admission.QueuedCount);
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(nameof(DanbooruWatcherState.Active), stored.State);
        Assert.Equal("100", stored.HighWaterMark);
        Assert.Equal(1, stored.NextPage);
    }

    [Fact]
    public async Task A429ThatArrivesDuringTheCutIsPersistedAndKeepsTheProviderQuietAcrossARestart()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        string id;
        DateTimeOffset retryAt = T0 + Interval + TimeSpan.FromHours(2);
        await using (DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock,
                         Options(), lifecycle))
        {
            scheduler.Load();
            id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
            await scheduler.RunNextAsync();
            clock.Advance(Interval);
            TaskCompletionSource entered = Signal(), release = Signal();
            search.Hold = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
            search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));
            Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
            await entered.Task.WaitAsync(TestWait.Timeout);

            lifecycle.BeginTransition();
            Task<ProfileAdmissionToken> reservation = profile.Lease.Admission.EnterExclusiveAsync().AsTask();
            Task drained = lifecycle.CutAndDrainAsync();
            release.SetResult();
            await drained.WaitAsync(TestWait.Timeout);
            lifecycle.ReleaseDrained();
            (await reservation.WaitAsync(TestWait.Timeout)).Dispose();

            DanbooruWatcherCheckSummary deferred = (await running)!;
            Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
            Assert.Equal(retryAt, deferred.RetryAtUtc);
            // The provider's wait is durable although it arrived after the cut (written with the pre-cut admission).
            BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
            Assert.Equal(nameof(DanbooruWatcherState.Cooldown), stored.State);
            Assert.Equal(retryAt, stored.CooldownUntilUtc);
        }

        // Restart (new scheduler, new lifecycle, no in-memory transport cooldown): nothing is sent before the promised time.
        search.Hold = null;
        int calls = search.CallCount;
        BooruProducerLifecycle restarted = new(profile.Lease.Admission);
        restarted.Open();
        await using DanbooruWatcherScheduler again = new(profile.Catalog, search, new CountingAdmission(), clock, Options(), restarted);
        again.Load();
        Assert.Equal(retryAt, again.GetSnapshot().ProviderCooldownUntilUtc);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await again.RunNextAsync());
        DanbooruWatcherCheckSummary manual = await again.CheckNowAsync(id).WaitAsync(TestWait.Timeout);
        Assert.Equal("provider_cooldown", manual.Code);
        Assert.Equal(calls, search.CallCount);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(DanbooruWatcherCheckKind.Completed, (await again.RunNextAsync())!.Kind);
        Assert.Equal(calls + 1, search.CallCount);
    }

    [Fact]
    public async Task AKernelRefusalForAnAnnouncedTransitionIsNotAStorageErrorAndDoesNotBackOffTheResumedCheck()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        await profile.Queue!.SetPausedAsync(true);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        Task<ProfileAdmissionToken>? reservation = null;
        DanbooruWatcherQueueAdmission real = new(profile.Originals!, profile.Catalog, clock);
        DelegatingAdmission announcing = new(real)
        {
            // The transition is announced and reserved exactly while the first post is being admitted: the queue
            // adapter's own per-call catalog writer is then refused by the kernel.
            Before = () =>
            {
                if (reservation != null) return;
                lifecycle.BeginTransition();
                reservation = profile.Lease.Admission.EnterExclusiveAsync().AsTask();
            }
        };
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, announcing, clock, Options(), lifecycle);
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();
        search.Publish("cat", 101);
        clock.Advance(Interval);

        DanbooruWatcherCheckSummary cut = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cut.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, cut.Code);
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(nameof(DanbooruWatcherState.Active), stored.State);
        Assert.Null(stored.ErrorCode);
        Assert.Equal("100", stored.HighWaterMark);
        Assert.Empty(profile.Catalog.Read().Posts);

        await lifecycle.CutAndDrainAsync().WaitAsync(TestWait.Timeout);
        lifecycle.ReleaseDrained();
        (await reservation!.WaitAsync(TestWait.Timeout)).Dispose();
        lifecycle.Open();
        // No local write backoff was armed: the resumed check runs at once and admits the post.
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal(1, resumed.Queued);
        Assert.Equal("101", Assert.Single(profile.Catalog.Read().Posts).PostId);
    }

    [Fact]
    public async Task AQueuedManualCheckIsAnsweredAtTheCutInsteadOfWaitingForTheExclusiveOperation()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options(), lifecycle);
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Task<DanbooruWatcherCheckSummary> manual = scheduler.CheckNowAsync(id);
        Assert.False(manual.IsCompleted);

        ProfileAdmissionToken exclusive = await CutForExclusiveAsync(lifecycle, profile.Lease.Admission);
        DanbooruWatcherCheckSummary answered = await manual.WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, answered.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, answered.Code);
        Assert.Equal(0, search.CallCount);

        // RecoveryRequired never reopens: nothing starts again in this session.
        exclusive.Dispose();
        lifecycle.RequireRecovery();
        Assert.Throws<InvalidOperationException>(lifecycle.Open);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode,
            (await scheduler.CheckNowAsync(id).WaitAsync(TestWait.Timeout)).Code);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal(0, search.CallCount);
    }

    [Fact]
    public async Task TheSessionStopPersistsTheCancelledCheckWithItsCountedAdmissionAfterTheProfileWasSealed()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options(), lifecycle);
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();
        clock.Advance(Interval);
        TaskCompletionSource entered = Signal();
        search.Hold = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);

        // Desktop order: the lifecycle stop is marked with the shutdown, the profile seals fresh writers, the
        // watchers stop, then the lifecycle releases its admission so the profile drain can complete.
        lifecycle.BeginStop();
        profile.Lease.Admission.BeginShutdown();
        Assert.Throws<InvalidOperationException>(() => profile.Lease.Admission.EnterStoreWriter());
        await scheduler.StopAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, (await running)!.Kind);
        Assert.Equal("cancelled", (await running)!.Code);
        Assert.Equal(nameof(DanbooruWatcherState.Active), Assert.Single(profile.Catalog.Read().Watchers).State);

        Task profileDrained = profile.Lease.Admission.DrainAsync();
        Assert.False(profileDrained.IsCompleted); // the generation's admission is still counted
        await lifecycle.StopAsync().WaitAsync(TestWait.Timeout);
        await profileDrained.WaitAsync(TestWait.Timeout);
    }

    // ---- Real shared queue: deadlock regressions ----------------------------------------------------

    /// <summary>A queue producer generation as the Registry installs and drains it around an exclusive operation.</summary>
    private sealed class QueueFamily(SchedulerProfile profile)
    {
        private MeowChan.Persistence.ProfileProducerEpoch? _epoch;

        internal void Install()
        {
            _epoch = new MeowChan.Persistence.ProfileProducerEpoch(profile.Lease.Admission.EnterOrdinaryWriter());
            profile.Queue!.InstallProfileEpoch(_epoch);
        }

        internal async Task SuspendAsync() => await profile.Queue!.SuspendProfileProducersAsync();

        internal async Task DrainAsync()
        {
            await profile.Queue!.SuspendProfileProducersAsync();
            await _epoch!.Close();
            profile.Queue.DetachProfileEpoch(_epoch);
            _epoch.Dispose();
            _epoch = null;
        }

        internal void Resume()
        {
            Install();
            profile.Queue!.ResumeProfileProducers();
        }
    }

    [Fact]
    public async Task AWatcherAdmissionParkedAtTheSuspendedQueueDoesNotPinTheDrainAndTheReplayedPageIsAdmittedOnce()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        QueueFamily queue = new(profile);
        queue.Install();
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock) { ClaimFactory = cdn.Claim };
        search.Publish("cat", 100);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search,
            new DanbooruWatcherQueueAdmission(profile.Originals!, profile.Catalog, clock), clock, Options(), lifecycle);
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();

        // The queue holds every new admission back (the Registry's concurrent family drain does exactly this).
        await queue.SuspendAsync();
        search.Publish("cat", 101);
        clock.Advance(Interval);
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await TestWait.UntilAsync(() => profile.Originals!.Snapshot().TryGetValue("101", out BooruOriginalDownloadOutcome? outcome)
            && outcome.State == BooruOriginalDownloadState.Queued, "admission parked at the suspended queue");
        Assert.False(running.IsCompleted);

        // The check holds its counted use while parked; the cut cancels the park, so neither family deadlocks.
        ProfileAdmissionToken exclusive = await CutForExclusiveAsync(lifecycle, profile.Lease.Admission, queue.DrainAsync);
        DanbooruWatcherCheckSummary cut = (await running.WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cut.Kind);
        Assert.Equal(DanbooruWatcherScheduler.ProfileSuspendedCode, cut.Code);
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal(nameof(DanbooruWatcherState.Active), stored.State);
        Assert.Equal("100", stored.HighWaterMark);
        Assert.Empty(cdn.Requests);

        exclusive.Dispose();
        queue.Resume();
        lifecycle.Open();
        DanbooruWatcherCheckSummary resumed = (await scheduler.RunNextAsync().WaitAsync(TestWait.Timeout))!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, resumed.Kind);
        Assert.Equal(1, resumed.NewPosts);
        Assert.Equal(1, resumed.Queued + resumed.AlreadyQueuedOrLinked);
        await TestWait.UntilAsync(() => profile.Catalog.Read().Posts.SingleOrDefault()?.LocalAssetId != null, "original linked");
        Assert.Equal([SchedulerCdn.OriginalUrl("101")], cdn.Requests.ToArray());
        Assert.Equal("101", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);
    }

    [Fact]
    public async Task PendingRecoveryParkedAtTheSuspendedQueueIsCutDrainedAndRestartedOnceInTheNewGeneration()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        BooruPostClaim post = cdn.Claim(73);
        await using (SchedulerProfile first = new(directory.Path, withQueue: true, cdn))
        {
            await first.Queue!.SetPausedAsync(true);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued,
                (await first.Originals!.AdmitAsync(post, BooruFindingKind.OneShot)).Status);
        }

        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        Assert.Single(profile.Originals!.GetPendingOriginals());
        QueueFamily queue = new(profile);
        queue.Install();
        await queue.SuspendAsync();
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        lifecycle.Open();
        SchedulerClock clock = new(T0);
        await using DanbooruWatcherHost host = new(profile.Catalog, profile.Originals, new FakeDanbooruSearch(clock),
            clock, Options(), lifecycle);
        await host.StartAsync().WaitAsync(TestWait.Timeout);
        Task<IReadOnlyList<BooruOriginalAdmission>> firstRun = host.Recovery;
        await TestWait.UntilAsync(() => profile.Originals.Snapshot().TryGetValue("73", out BooruOriginalDownloadOutcome? outcome)
            && outcome.State == BooruOriginalDownloadState.Queued, "recovery parked at the suspended queue");
        Assert.False(firstRun.IsCompleted);

        ProfileAdmissionToken exclusive = await CutForExclusiveAsync(lifecycle, profile.Lease.Admission, queue.DrainAsync);
        // The cut recovery has really ended before the exclusive grant; its request is still pending.
        Assert.True(firstRun.IsCompleted);
        Assert.Same(firstRun, host.Recovery); // no new run inside the exclusive operation
        Assert.True(Assert.Single(profile.Catalog.Read().Posts).HasPendingOriginal);
        Assert.Empty(cdn.Requests);

        exclusive.Dispose();
        queue.Resume();
        lifecycle.Open();
        await TestWait.UntilAsync(() => !ReferenceEquals(firstRun, host.Recovery), "a fresh recovery run in the new generation");
        BooruOriginalAdmission admitted = Assert.Single(await host.Recovery.WaitAsync(TestWait.Timeout));
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admitted.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await admitted.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal([SchedulerCdn.OriginalUrl("73")], cdn.Requests.ToArray());
    }

    [Fact]
    public async Task TheHostStartWaitsForAnOpenGenerationAndFailsWhenTheFamilyRequiresRecovery()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        await using DanbooruWatcherHost host = new(profile.Catalog, profile.Originals!, new FakeDanbooruSearch(clock),
            clock, Options(), lifecycle);
        Task start = host.StartAsync();
        await Task.Delay(50);
        Assert.False(start.IsCompleted); // an exclusive operation in progress delays the start instead of failing it
        lifecycle.Open();
        await start.WaitAsync(TestWait.Timeout);
        Assert.True(host.Scheduler.GetSnapshot().Running);

        BooruProducerLifecycle fenced = new(profile.Lease.Admission);
        fenced.RequireRecovery();
        await using DanbooruWatcherHost blocked = new(profile.Catalog, profile.Originals!, new FakeDanbooruSearch(clock),
            clock, Options(), fenced);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.StartAsync()).WaitAsync(TestWait.Timeout);
        Assert.False(blocked.Scheduler.GetSnapshot().Running);
    }

    [Fact]
    public async Task AStartWaitingForTheGenerationIsCancellableAndStaysRetryable()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        await using DanbooruWatcherHost host = new(profile.Catalog, profile.Originals!, new FakeDanbooruSearch(clock),
            clock, Options(), lifecycle);
        using CancellationTokenSource cancel = new();
        Task start = host.StartAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(TestWait.Timeout);
        lifecycle.Open();
        await host.StartAsync().WaitAsync(TestWait.Timeout);
        Assert.True(host.Scheduler.GetSnapshot().Running);
    }

    private sealed class DelegatingAdmission(IDanbooruWatcherAdmissionPort inner) : IDanbooruWatcherAdmissionPort
    {
        internal Action? Before { get; init; }

        public Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
            CancellationToken cancellationToken)
        {
            Before?.Invoke();
            return inner.AdmitAsync(post, watcherId, query, cancellationToken);
        }
    }
}
