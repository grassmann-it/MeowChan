using System.IO;
using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>Deterministic reset/cancellation races at the scheduler's durable provider-fence boundary.</summary>
public sealed class DanbooruWatcherCooldownResetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static DanbooruWatcherSchedulerOptions Options() => new()
    {
        Interval = Interval,
        CheckStartSpacing = TimeSpan.Zero
    };

    [Fact]
    public async Task FailedResetPersistenceLeavesFaultAndCooldownOnDiskForRestart()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string watcherId = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        DateTimeOffset retryAt = T0 + TimeSpan.FromHours(2);
        search.Next((_, _) => new DanbooruWatcherSearchDeferred(retryAt, "booru-cooldown", 429));
        clock.Advance(Interval);
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, (await scheduler.RunNextAsync())!.Kind);

        profile.Catalog.BeforeAdmittedMutationForTests = _ =>
            throw new InvalidDataException(BooruCatalogStore.FullMessage);
        DanbooruWatcherCommandResult reset = scheduler.ResetProviderCooldown();

        Assert.False(reset.Accepted);
        Assert.Equal("persist_failed", reset.Code);
        Assert.Equal("persist_failed", scheduler.GetSnapshot().FaultCode);
        Assert.Equal("persist_failed", (await scheduler.CheckNowAsync(watcherId)).Code);
        Assert.Equal(2, search.CallCount);
        Assert.Equal(DanbooruWatcherState.Cooldown, scheduler.GetStatus(watcherId)!.State);
        Assert.Equal("Cooldown", Assert.Single(profile.Catalog.Read().Watchers).State);

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        restarted.Load();
        DanbooruWatcherStatus restored = restarted.GetStatus(watcherId)!;
        Assert.Equal(DanbooruWatcherState.Cooldown, restored.State);
        Assert.Equal(retryAt, restored.CooldownUntilUtc);
        Assert.Equal(retryAt, restarted.GetSnapshot().ProviderCooldownUntilUtc);
    }

    [Fact]
    public async Task ResetRefusesAnActiveCheckThenPausePersistsItsLateTransportCooldown()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        // Seed the explicit baseline this test later verifies survives pause/cooldown recovery.
        search.Publish("cat", 100);
        await using DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string watcherId = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        clock.Advance(Interval);
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);

        DateTimeOffset retryAt = clock.GetUtcNow() + TimeSpan.FromHours(3);
        search.TransportCooldown = retryAt;
        DanbooruWatcherCommandResult reset = scheduler.ResetProviderCooldown();
        Assert.False(reset.Accepted);
        Assert.Equal("check_already_running", reset.Code);

        DanbooruWatcherCommandResult paused = scheduler.Pause(watcherId);
        Assert.True(paused.Accepted, paused.Code);
        DanbooruWatcherCheckSummary cancelled = (await running)!;
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cancelled.Kind);

        BooruWatcherRecord persisted = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal("Cooldown", persisted.State);
        Assert.Equal("Paused", persisted.ResumeState);
        Assert.Equal(retryAt, persisted.CooldownUntilUtc);
        Assert.Equal("100", persisted.HighWaterMark);
        Assert.Null(persisted.CursorBeforePostId);

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        restarted.Load();
        DanbooruWatcherStatus restored = restarted.GetStatus(watcherId)!;
        Assert.Equal(DanbooruWatcherState.Cooldown, restored.State);
        Assert.Equal(DanbooruWatcherActivity.Paused, restored.Activity);
        Assert.Equal(retryAt, restored.CooldownUntilUtc);
    }

    [Fact]
    public async Task StopPersistsActiveTransportCooldownBeforeTheSchedulerCanBeRestarted()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string watcherId = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        clock.Advance(Interval);
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);
        DateTimeOffset retryAt = clock.GetUtcNow() + TimeSpan.FromHours(4);
        search.TransportCooldown = retryAt;

        await scheduler.StopAsync();
        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, (await running)!.Kind);
        BooruWatcherRecord persisted = Assert.Single(profile.Catalog.Read().Watchers);
        Assert.Equal("Cooldown", persisted.State);
        Assert.Equal(retryAt, persisted.CooldownUntilUtc);

        await using DanbooruWatcherScheduler restarted = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        restarted.Load();
        Assert.Equal(retryAt, restarted.GetSnapshot().ProviderCooldownUntilUtc);
        Assert.Equal(DanbooruWatcherState.Cooldown, restarted.GetStatus(watcherId)!.State);
    }
}
