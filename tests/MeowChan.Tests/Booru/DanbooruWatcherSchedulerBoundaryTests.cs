using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>Late completions must not cross the scheduler's cancellation or durable-fence boundaries.</summary>
public sealed class DanbooruWatcherSchedulerBoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static DanbooruWatcherSchedulerOptions Options() => new()
    {
        Interval = TimeSpan.FromMinutes(30), CheckStartSpacing = TimeSpan.Zero
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LateSuccessfulPageDoesNotAdvanceAfterCancelOrStop(bool stopScheduler, bool baselineEstablished)
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        CountingAdmission admission = new();
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        if (baselineEstablished)
        {
            await scheduler.RunNextAsync();
            clock.Advance(Options().Interval);
        }
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A port may complete successfully while cancellation races its final continuation.
        search.Hold = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        using CancellationTokenSource cancel = new();
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync(cancel.Token);
        Task stopping = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TestWait.Timeout);
            if (stopScheduler) stopping = scheduler.StopAsync();
            else cancel.Cancel();
            if (stopScheduler) Assert.False(stopping.IsCompleted); // cancellation is not a drain
            release.TrySetResult();
            DanbooruWatcherCheckSummary result = (await running.WaitAsync(TestWait.Timeout))!;
            await stopping.WaitAsync(TestWait.Timeout);
            Assert.Equal(DanbooruWatcherCheckKind.Cancelled, result.Kind);
            BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
            Assert.Equal("Active", stored.State);
            Assert.Equal(baselineEstablished, stored.BaselineEstablished);
            Assert.Equal(baselineEstablished ? "100" : null, stored.HighWaterMark);
            Assert.Equal(baselineEstablished ? T0 : (DateTimeOffset?)null, stored.LastCheckedAtUtc);
            Assert.Empty(admission.Findings);
            Assert.Equal(search.SessionsOpened, search.SessionsDisposed);

            search.Hold = null;
            search.Publish("cat", 101);
            await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admission, clock, Options());
            restarted.Load();
            Assert.Equal(baselineEstablished ? DanbooruWatcherCheckKind.Completed : DanbooruWatcherCheckKind.BaselineEstablished,
                (await restarted.RunNextAsync())!.Kind);
            Assert.Equal("101", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);
            Assert.Equal(baselineEstablished ? 1 : 0, admission.QueuedCount);
            Assert.Equal(DanbooruWatcherState.Active, restarted.GetStatus(id)!.State);
        }
        finally
        {
            cancel.Cancel();
            release.TrySetResult();
            await running.WaitAsync(TestWait.Timeout);
            await stopping.WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task CancellationRacingTheLastAdmissionDoesNotAdvanceAndReplaysIdempotentlyAfterRestart()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        CountingAdmission admitted = new();
        using CancellationTokenSource cancel = new();
        ReturnBoundaryAdmission admission = new(admitted, cancel.Cancel);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, admission, clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        await scheduler.RunNextAsync();
        search.Publish("cat", 101);
        clock.Advance(Options().Interval);

        DanbooruWatcherCheckSummary result = (await scheduler.RunNextAsync(cancel.Token))!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, result.Kind);
        BooruWatcherRecord stored = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal("Active", stored.State);
        Assert.Equal("100", stored.HighWaterMark);
        Assert.Equal(T0, stored.LastCheckedAtUtc);
        Assert.Single(admitted.Findings);
        await scheduler.StopAsync();

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, admitted, clock, Options());
        restarted.Load();
        Assert.Equal(DanbooruWatcherCheckKind.Completed, (await restarted.RunNextAsync())!.Kind);
        Assert.Equal("101", Assert.Single(profile.Catalog.Read().Watchers).HighWaterMark);
        Assert.Single(admitted.Findings);
        Assert.Equal(1, admitted.QueuedCount);
        Assert.Equal(2, admitted.PerPost["101"]); // replay, not a second job or finding
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDeferralRemainsDurableEvenWhenCancelOrAFenceReadRacesIt(bool fenceReadFails)
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        using CancellationTokenSource cancel = new();
        DateTimeOffset promised = T0.AddHours(2);
        search.Next((_, _) =>
        {
            if (fenceReadFails)
            {
                search.TransportCooldownFailure = new InvalidOperationException("private provider detail");
                search.TransportCooldownFailureOnce = true;
            }
            else cancel.Cancel();
            return new DanbooruWatcherSearchDeferred(promised, "booru-cooldown", 429);
        });
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string cat = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        clock.Advance(TimeSpan.FromSeconds(1));
        scheduler.AddWatcher("dog");
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await scheduler.RunNextAsync(cancel.Token))!.Kind);
        BooruWatcherRecord stored = profile.Catalog.Read().Watchers.Single(item => item.WatcherId == cat);
        Assert.Equal("Cooldown", stored.State);
        Assert.True(stored.CooldownUntilUtc >= promised);
        if (fenceReadFails) Assert.Equal("provider_cooldown_unavailable", scheduler.GetSnapshot().FaultCode);
        await scheduler.StopAsync();

        // A fresh offline transport has no memory of the old response. The durable record alone holds all watchers.
        FakeDanbooruSearch offline = new(clock);
        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, offline, new CountingAdmission(), clock, Options());
        restarted.Load();
        Assert.Null(await restarted.RunNextAsync());
        Assert.Empty(offline.Calls);
        Assert.True(restarted.GetSnapshot().ProviderCooldownUntilUtc >= promised);
    }

    [Fact]
    public async Task FaultedLoopAnswersQueuedManualWorkAndDoesNotSpinOnAnUntouchedDueWatcher()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        clock.Advance(TimeSpan.FromSeconds(1));
        string dog = scheduler.AddWatcher("dog").Watcher!.WatcherId;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, _, _) => { entered.TrySetResult(); await release.Task; };
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(clock.GetUtcNow().AddHours(2), "booru-cooldown", 429));
        int faultChanges = 0;
        scheduler.Changed += () =>
        {
            if (scheduler.GetSnapshot().FaultCode is null) return;
            // Bound the old faulty spin so a failing regression does not consume an unbounded core.
            if (Interlocked.Increment(ref faultChanges) == 10) scheduler.SetSessionPaused(true);
        };
        await scheduler.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TestWait.Timeout);
            Task<DanbooruWatcherCheckSummary> queued = scheduler.CheckNowAsync(dog);
            profile.Catalog.BeforeAdmittedMutationForTests = _ => throw new IOException("fixture write failure");
            release.TrySetResult();
            Assert.Equal("persist_failed", (await queued.WaitAsync(TestWait.Timeout)).Code);
            await Task.Delay(100); // bounded quiescence window; no provider timing is measured here
            Assert.Null(scheduler.GetStatus(dog)!.LastCheck);
            Assert.InRange(Volatile.Read(ref faultChanges), 1, 2);
            Assert.Equal(1, search.CallCount);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ADirectStepAnswersQueuedManualWorkWhenASnapshotHasLatchedAFault()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string id = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Task<DanbooruWatcherCheckSummary> queued = scheduler.CheckNowAsync(id);
        search.TransportCooldownFailure = new InvalidOperationException("fixture fence failure");
        search.TransportCooldownFailureOnce = true;
        Assert.Equal("provider_cooldown_unavailable", scheduler.GetSnapshot().FaultCode);
        Assert.Null(await scheduler.RunNextAsync());
        Assert.Equal("provider_cooldown_unavailable", (await queued.WaitAsync(TestWait.Timeout)).Code);
        Assert.Null(scheduler.GetStatus(id)!.LastCheck);
        Assert.Empty(search.Calls);
    }

    [Fact]
    public async Task StopCancelsAHostStartWaitingForTheProfileGenerationWithoutOpeningIt()
    {
        using TempDirectory directory = new();
        SchedulerCdn cdn = new();
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission);
        await using DanbooruWatcherHost host = new(profile.Catalog, profile.Originals!, search, clock, Options(), lifecycle);
        using CancellationTokenSource fallback = new();
        Task start = host.StartAsync(fallback.Token);
        Assert.False(start.IsCompleted);
        Task stop = host.StopAsync();
        try
        {
            await stop.WaitAsync(TestWait.Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(TestWait.Timeout);
            Assert.False(host.Scheduler.GetSnapshot().Running);
            Assert.True(host.Recovery.IsCompleted);
            Assert.Empty(search.Calls);
            lifecycle.Open();
            await host.StartAsync(); // stop is terminal, even after the generation opens
            Assert.False(host.Scheduler.GetSnapshot().Running);
        }
        finally
        {
            fallback.Cancel();
            try { await start.WaitAsync(TestWait.Timeout); } catch (OperationCanceledException) { }
            await stop.WaitAsync(TestWait.Timeout);
            await lifecycle.StopAsync();
        }
    }

    private sealed class ReturnBoundaryAdmission(IDanbooruWatcherAdmissionPort inner, Action beforeReturn)
        : IDanbooruWatcherAdmissionPort
    {
        public async Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
            CancellationToken cancellationToken)
        {
            DanbooruWatcherAdmissionResult result = await inner.AdmitAsync(post, watcherId, query, cancellationToken);
            beforeReturn();
            return result;
        }
    }
}
