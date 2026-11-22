using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// No plugin assembly, worker process, native image codec, socket or user profile. These tests exercise escaped
// production handles over a real catalog/queue/scheduler, with terminal in-memory worker and HTTP fakes.
public sealed class BooruPortAvailabilityTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 1, 2, 3, 4];

    [Fact]
    public async Task HeldPortsRejectEveryMutationWhileDisabledAndReuseTheOriginalQueueOnReenable()
    {
        await using Fixture f = new();
        string watcher = f.SeedPausedWatcher();
        await f.StartAsync();
        BooruWorkbenchServices held = f.Composition.Services;
        BooruOriginalQueue originals = f.Composition.Originals;
        Assert.Equal(BooruLoadStatus.Queued, (await held.Queue.LoadOriginalAsync(Post("1"), BooruLoadOrigin.Search,
            CancellationToken.None)).Status);
        Assert.NotNull(await held.Previews.LoadPreviewAsync(Post("1"), CancellationToken.None));
        DanbooruWatcherHost oldHost = f.Composition.WatcherHost;

        f.SetEnabled(false);
        await WaitAsync(() => !oldHost.Scheduler.GetSnapshot().Running);
        long writes = f.Catalog.Counters.Writes;
        int previews = f.Http.Previews;
        AssertUnavailable(await held.Queue.LoadOriginalAsync(Post("2"), BooruLoadOrigin.OneShot, CancellationToken.None));
        Assert.Null(await held.Previews.LoadPreviewAsync(Post("2"), CancellationToken.None));
        await AssertCommandsUnavailable(held.Watchers, watcher);
        Assert.Equal(writes, f.Catalog.Counters.Writes);
        Assert.Equal(previews, f.Http.Previews);
        Assert.Single(f.Catalog.Read().Posts);
        Assert.Equal(1, originals.ActiveCount); // accepted while paused, not retroactively cancelled
        Assert.Single(await held.Watchers.ListAsync(CancellationToken.None));
        Assert.Equal(BooruLocalState.Queued,
            (await held.Queue.GetLocalStatesAsync(["1"], CancellationToken.None))["1"]);
        Assert.NotNull(await held.Watchers.GetCatalogUsageAsync(CancellationToken.None));

        f.SetEnabled(true);
        await WaitAsync(() => f.Composition.IsAvailable && f.Composition.WatcherHost.Scheduler.GetSnapshot().Running);
        Assert.Same(held, f.Composition.Services);
        Assert.Same(originals, f.Composition.Originals);
        Assert.NotSame(oldHost, f.Composition.WatcherHost); // B5 stop is terminal, its manual queue is not reused
        Assert.Equal(BooruLoadStatus.AlreadyQueued,
            (await held.Queue.LoadOriginalAsync(Post("1"), BooruLoadOrigin.OneShot, CancellationToken.None)).Status);
        Assert.Equal(BooruLoadStatus.Queued,
            (await held.Queue.LoadOriginalAsync(Post("2"), BooruLoadOrigin.OneShot, CancellationToken.None)).Status);
        Assert.NotNull(await held.Previews.LoadPreviewAsync(Post("1"), CancellationToken.None));
        Assert.Equal(previews + 1, f.Http.Previews); // the disabled generation's cache was retired
        Assert.Equal(BooruWatcherCommandStatus.Done, (await held.Watchers.RemoveAsync(watcher, CancellationToken.None)).Status);
        Assert.Equal(2, originals.ActiveCount);
        Assert.Equal(0, f.Http.Originals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HeldSearchAndPostPortsRejectBeforeResolvingAWorker(bool readPost, bool stop)
    {
        await using Fixture f = new();
        await f.StartAsync();
        IBooruSearchPort held = f.Composition.Services.Search;
        Assert.Equal(BooruCallStatus.Ok, await InvokeSearch(held, readPost));
        int resolutions = f.WorkerResolutions, calls = f.Worker.Calls;
        if (stop) f.Composition.ClosePortIngress();
        else f.SetEnabled(false);

        Assert.Equal(BooruCallStatus.Unavailable, await InvokeSearch(held, readPost));
        Assert.Equal(resolutions, f.WorkerResolutions);
        Assert.Equal(calls, f.Worker.Calls);
        if (!stop)
        {
            f.SetEnabled(true);
            await WaitAsync(() => f.Composition.IsAvailable);
            Assert.Equal(BooruCallStatus.Ok, await InvokeSearch(held, readPost));
            Assert.Equal(calls + 1, f.Worker.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResolutionFromTheDisabledGenerationIsDrainedWithoutStartingAProviderJob(bool readPost)
    {
        await using Fixture f = new();
        await f.StartAsync();
        f.HoldWorkerResolution = true;
        Task<BooruCallStatus> pending = InvokeSearch(f.Composition.Services.Search, readPost);
        await f.WorkerResolutionEntered.Task.WaitAsync(Deadline);
        f.SetEnabled(false);
        f.SetEnabled(true);
        Assert.True(f.WorkerResolutionToken.IsCancellationRequested);
        Assert.False(f.Composition.IsAvailable);
        Assert.False(pending.IsCompleted); // the old resolution still owns the availability lease
        Assert.Equal(0, f.Worker.Calls);

        f.HoldWorkerResolution = false;
        f.ReleaseWorkerResolution.TrySetResult();
        Assert.Equal(BooruCallStatus.Unavailable, await pending.WaitAsync(Deadline));
        await WaitAsync(() => f.Composition.IsAvailable);
        Assert.Equal(0, f.Worker.Calls);
        Assert.Equal(BooruCallStatus.Ok, await InvokeSearch(f.Composition.Services.Search, readPost));
        Assert.Equal(1, f.Worker.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SearchAndPostRejectALateProviderPageButPreserveCallerCancellation(bool readPost, bool cancelCaller)
    {
        await using Fixture f = new();
        await f.StartAsync();
        using CancellationTokenSource caller = new();
        f.Worker.Hold = true;
        Task<BooruCallStatus> pending = InvokeSearch(f.Composition.Services.Search, readPost, caller.Token);
        await f.Worker.Entered.Task.WaitAsync(Deadline);
        if (cancelCaller) caller.Cancel();
        f.Composition.ClosePortIngress();
        Assert.True(f.Worker.Token.IsCancellationRequested);
        if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        else Assert.Equal(BooruCallStatus.Unavailable, await pending.WaitAsync(Deadline));
        f.Worker.Release.TrySetResult(); // the fake intentionally ignored cancellation; its result cannot escape
        await f.Composition.StopAsync().WaitAsync(Deadline);
        Assert.Equal(1, f.Worker.Calls);
        Assert.Empty(f.Catalog.Read().Posts);
        Assert.Equal(BooruCallStatus.Unavailable, await InvokeSearch(f.Composition.Services.Search, readPost));
        Assert.Equal(1, f.WorkerResolutions);
    }

    private static async Task<BooruCallStatus> InvokeSearch(IBooruSearchPort port, bool readPost,
        CancellationToken token = default) => readPost
            ? (await port.ReadPostAsync("1", token).WaitAsync(Deadline)).Status
            : (await port.SearchAsync("cat", 1, token).WaitAsync(Deadline)).Status;

    [Fact]
    public async Task ReenabledHostKeepsAutomaticChecksPausedButAllowsAnExplicitManualCheck()
    {
        await using Fixture f = new();
        f.Composition.WatcherHost.Scheduler.Load();
        Assert.True(f.Composition.WatcherHost.Scheduler.AddWatcher("cat").Accepted);
        string watcher = Assert.Single(f.Composition.WatcherHost.Scheduler.GetSnapshot().Watchers).WatcherId;
        await f.StartAsync();
        DanbooruWatcherHost old = f.Composition.WatcherHost;
        Assert.True(old.Scheduler.SessionPaused);
        Assert.Null(await old.Scheduler.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(0, f.Worker.Calls);

        f.SetEnabled(false);
        f.SetEnabled(true);
        await WaitAsync(() => f.Composition.IsAvailable && !ReferenceEquals(old, f.Composition.WatcherHost)
            && f.Composition.WatcherHost.Scheduler.GetSnapshot().Running);
        DanbooruWatcherScheduler replacement = f.Composition.WatcherHost.Scheduler;
        Assert.True(replacement.SessionPaused);
        Assert.Equal(BooruProducerPhase.Open, f.Producers.Phase); // disable is not a terminal session cut
        Assert.Equal(1L, f.Producers.Generation);
        Assert.Null(await replacement.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(0, f.Worker.Calls);
        Assert.False(replacement.GetSnapshot().Watchers.Single().BaselineEstablished);

        // The explicit Check now exception must survive host replacement, without unpausing the session.
        Assert.Null((await replacement.CheckNowAsync(watcher).WaitAsync(Deadline)).Code);
        Assert.True(replacement.GetSnapshot().Watchers.Single().BaselineEstablished);
        Assert.True(replacement.SessionPaused);
        Assert.Equal(1, f.Worker.Calls);
        Assert.True(replacement.AddWatcher("dog").Accepted);
        Assert.Null(await replacement.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(1, f.Worker.Calls);

        await f.Queue.SetPausedAsync(false);
        f.Composition.SyncSessionPause();
        await WaitAsync(() => replacement.GetSnapshot().Watchers.Single(w => w.Query == "dog").BaselineEstablished);
        Assert.Equal(2, f.Worker.Calls);
    }

    [Fact]
    public async Task StopPermanentlyRevokesHeldPortsButAnAcceptedDownloadCanStillFinish()
    {
        await using Fixture f = new();
        string watcher = f.SeedPausedWatcher();
        await f.StartAsync();
        BooruWorkbenchServices held = f.Composition.Services;
        Assert.Equal(BooruLoadStatus.Queued,
            (await held.Queue.LoadOriginalAsync(Post("1"), BooruLoadOrigin.Search, CancellationToken.None)).Status);
        await f.Composition.StopAsync().WaitAsync(Deadline);
        long writes = f.Catalog.Counters.Writes;
        await AssertCommandsUnavailable(held.Watchers, watcher);
        AssertUnavailable(await held.Queue.LoadOriginalAsync(Post("2"), BooruLoadOrigin.Search, CancellationToken.None));
        Assert.Null(await held.Previews.LoadPreviewAsync(Post("2"), CancellationToken.None));
        Assert.Equal(writes, f.Catalog.Counters.Writes);
        f.SetEnabled(false);
        f.SetEnabled(true);
        Assert.False(f.Composition.IsAvailable);
        AssertUnavailable(await held.Queue.LoadOriginalAsync(Post("2"), BooruLoadOrigin.Search, CancellationToken.None));

        // Session/queue shutdown owns the transport lifetime. Stopping just the ports must not dispose it or
        // cancel a previously accepted queue owner, including one whose physical HTTP starts after this stop.
        await f.Queue.SetPausedAsync(false);
        await WaitAsync(() => f.Catalog.Read().Posts.Single().LocalAssetId != null);
        Assert.Equal(1, f.Http.Originals);
        Assert.Equal(BooruLocalState.Loaded,
            (await held.Queue.GetLocalStatesAsync(["1"], CancellationToken.None))["1"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationCancelsANotYetAcceptedOriginalAndItDoesNotAppearAfterQueueResume(bool stop)
    {
        await using Fixture f = new();
        await f.StartAsync();
        await f.Queue.SuspendProfileProducersAsync();
        Task<BooruLoadResult> loading = f.Composition.Services.Queue.LoadOriginalAsync(Post("3"), BooruLoadOrigin.Search,
            CancellationToken.None);
        await WaitAsync(() => f.Composition.Originals.ActiveCount == 1);
        Task stopping = stop ? f.Composition.StopAsync() : Task.CompletedTask;
        if (!stop) f.SetEnabled(false);
        AssertUnavailable(await loading.WaitAsync(Deadline));
        await stopping.WaitAsync(Deadline);
        Assert.Equal(0, f.Composition.Originals.ActiveCount);
        Assert.Equal(BooruOriginalIntent.NotAdmitted, Assert.Single(f.Catalog.Read().Posts).OriginalIntent);
        f.Queue.ResumeProfileProducers();
        await f.Queue.SetPausedAsync(false);
        Assert.Equal(0, f.Http.Originals);
        Assert.Equal(0, f.Composition.Originals.ActiveCount);

        f.SetEnabled(true);
        if (stop)
        {
            Assert.False(f.Composition.IsAvailable);
            AssertUnavailable(await f.Composition.Services.Queue.LoadOriginalAsync(Post("3"), BooruLoadOrigin.Search,
                CancellationToken.None));
            return;
        }
        await WaitAsync(() => f.Composition.IsAvailable);
        Assert.Equal(BooruLoadStatus.Queued, (await f.Composition.Services.Queue.LoadOriginalAsync(Post("3"),
            BooruLoadOrigin.OneShot, CancellationToken.None)).Status);
        await WaitAsync(() => f.Catalog.Read().Posts.Single().LocalAssetId != null);
        Assert.Equal(1, f.Http.Originals);
    }

    [Fact]
    public async Task ADelayedAcceptedAcknowledgementIsNotCancelledOrReusedByTheNextGeneration()
    {
        await using Fixture f = new();
        await f.StartAsync();
        TaskCompletionSource accepted = Signal(), release = Signal();
        f.Composition.Originals.AfterQueueAcceptedForTests = _ => { accepted.TrySetResult(); return release.Task; };
        Task<BooruLoadResult> loading = f.Composition.Services.Queue.LoadOriginalAsync(Post("4"), BooruLoadOrigin.Search,
            CancellationToken.None);
        try
        {
            await accepted.Task.WaitAsync(Deadline);
            f.SetEnabled(false);
            f.SetEnabled(true);
            Assert.False(f.Composition.IsAvailable); // don't race the old owner's pending acknowledgement
            Assert.False(loading.IsCompleted);
        }
        finally { release.TrySetResult(); }
        AssertUnavailable(await loading.WaitAsync(Deadline)); // obsolete UI completion only, not queue cancellation
        await WaitAsync(() => f.Composition.IsAvailable);
        Assert.Equal(1, f.Composition.Originals.ActiveCount);
        Assert.True(Assert.Single(f.Catalog.Read().Posts).HasPendingOriginal);
        await f.Queue.SetPausedAsync(false);
        await WaitAsync(() => f.Catalog.Read().Posts.Single().LocalAssetId != null);
        Assert.Equal(1, f.Http.Originals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightAndWaitingPreviewsAreRevokedAndLateBytesNeverEscape(bool stop)
    {
        await using Fixture f = new();
        await f.StartAsync();
        BooruWorkbenchServices held = f.Composition.Services;
        f.Http.HoldPreviews = true; // intentionally ignores HTTP cancellation until explicitly released
        Task<byte[]?> first = held.Previews.LoadPreviewAsync(Post("10"), CancellationToken.None);
        Task<byte[]?> second = held.Previews.LoadPreviewAsync(Post("11"), CancellationToken.None);
        await WaitAsync(() => f.Http.Previews == 2);
        Task<byte[]?> waiting = held.Previews.LoadPreviewAsync(Post("12"), CancellationToken.None);
        Task stopping = Task.CompletedTask, repeatedStop = Task.CompletedTask;
        try
        {
            if (stop)
            {
                stopping = f.Composition.StopAsync();
                repeatedStop = f.Composition.StopAsync();
                Assert.False(repeatedStop.IsCompleted); // every stop caller observes the physical drain
            }
            else { f.SetEnabled(false); f.SetEnabled(true); }
            Assert.Null(await waiting.WaitAsync(Deadline));
            Assert.Null(await held.Previews.LoadPreviewAsync(Post("13"), CancellationToken.None));
            Assert.Equal(2, f.Http.Previews);
            Assert.False(f.Composition.IsAvailable);
            Assert.True(f.Http.PreviewToken.IsCancellationRequested);
        }
        finally { f.Http.ReleasePreviews.TrySetResult(); }
        Assert.Null(await first.WaitAsync(Deadline));
        Assert.Null(await second.WaitAsync(Deadline));
        await stopping.WaitAsync(Deadline);
        await repeatedStop.WaitAsync(Deadline);
        if (stop)
        {
            f.SetEnabled(false);
            f.SetEnabled(true);
            Assert.Null(await held.Previews.LoadPreviewAsync(Post("10"), CancellationToken.None));
            Assert.Equal(2, f.Http.Previews);
        }
        else
        {
            await WaitAsync(() => f.Composition.IsAvailable);
            Assert.Equal(Image, await held.Previews.LoadPreviewAsync(Post("10"), CancellationToken.None));
            Assert.Equal(3, f.Http.Previews);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AManualCheckDrainsItsOldWorkerAndDiscardsTheLatePageBeforeReenable(bool stop)
    {
        await using Fixture f = new(paused: false);
        string watcher = f.SeedPausedWatcher();
        await f.StartAsync();
        f.Worker.Hold = true;
        BooruWorkbenchServices held = f.Composition.Services;
        DanbooruWatcherHost old = f.Composition.WatcherHost;
        Task<BooruWatcherCommandResult> check = held.Watchers.CheckNowAsync(watcher, CancellationToken.None);
        Task stopping = Task.CompletedTask;
        try
        {
            await f.Worker.Entered.Task.WaitAsync(Deadline);
            if (stop) stopping = f.Composition.StopAsync();
            else { f.SetEnabled(false); f.SetEnabled(true); }
            AssertUnavailable(await check.WaitAsync(Deadline));
            Assert.False(f.Composition.IsAvailable);
            await AssertCommandsUnavailable(held.Watchers, watcher);
            Assert.True(f.Worker.Token.IsCancellationRequested);
            Assert.Equal(1, f.Worker.Calls);
        }
        finally { f.Worker.Release.TrySetResult(); }
        await stopping.WaitAsync(Deadline);
        if (!stop) await WaitAsync(() => f.Composition.IsAvailable);
        else Assert.False(f.Composition.IsAvailable);
        Assert.False(old.Scheduler.GetSnapshot().Running);
        Assert.Empty(f.Catalog.Read().Posts);
        BooruWatcherView view = Assert.Single(await held.Watchers.ListAsync(CancellationToken.None));
        Assert.False(view.BaselineEstablished); // the stale page containing post 99 never established the baseline
        Assert.Equal(BooruWatcherStatus.Paused, view.Status);
        Assert.Equal(0, f.Http.Originals);
        if (!stop)
        {
            Assert.Equal(BooruWatcherCommandStatus.Done,
                (await held.Watchers.CheckNowAsync(watcher, CancellationToken.None).WaitAsync(Deadline)).Status);
            Assert.True(Assert.Single(await held.Watchers.ListAsync(CancellationToken.None)).BaselineEstablished);
            Assert.Equal(2, f.Worker.Calls);
        }
    }

    [Fact]
    public async Task ProfileShutdownAlreadyRefusesNewCallsBeforeTheAsyncCompositionStopRuns()
    {
        await using Fixture f = new();
        string watcher = f.SeedPausedWatcher();
        await f.StartAsync();
        f.BeginProfileShutdown();
        long writes = f.Catalog.Counters.Writes;
        Assert.False(f.Composition.IsAvailable);
        AssertUnavailable(await f.Composition.Services.Queue.LoadOriginalAsync(Post("1"), BooruLoadOrigin.Search,
            CancellationToken.None));
        Assert.Null(await f.Composition.Services.Previews.LoadPreviewAsync(Post("1"), CancellationToken.None));
        await AssertCommandsUnavailable(f.Composition.Services.Watchers, watcher);
        Assert.Equal(writes, f.Catalog.Counters.Writes);
        Assert.Equal(0, f.Http.Previews + f.Http.Originals);
    }

    [Fact]
    public async Task SynchronousSessionCutRevokesWaitingPreviewsBeforeTheAsyncStopRuns()
    {
        await using Fixture f = new();
        await f.StartAsync();
        f.Http.HoldPreviews = true;
        Task<byte[]?> first = f.Composition.Services.Previews.LoadPreviewAsync(Post("1"), CancellationToken.None);
        Task<byte[]?> second = f.Composition.Services.Previews.LoadPreviewAsync(Post("2"), CancellationToken.None);
        await WaitAsync(() => f.Http.Previews == 2);
        Task<byte[]?> waiting = f.Composition.Services.Previews.LoadPreviewAsync(Post("3"), CancellationToken.None);
        try
        {
            f.Composition.ClosePortIngress(); // the parent shutdown ingress hook, independent of StopAsync scheduling
            Assert.Null(await waiting.WaitAsync(Deadline));
            f.SetEnabled(false);
            f.SetEnabled(true);
            Assert.False(f.Composition.IsAvailable);
            Assert.Null(await f.Composition.Services.Previews.LoadPreviewAsync(Post("4"), CancellationToken.None));
            Assert.Equal(2, f.Http.Previews);
        }
        finally { f.Http.ReleasePreviews.TrySetResult(); }
        Assert.Null(await first.WaitAsync(Deadline));
        Assert.Null(await second.WaitAsync(Deadline));
    }

    [Fact]
    public async Task AQueuedManualRequestCannotSurviveDisableAndRunOnReenable()
    {
        await using Fixture f = new(paused: false);
        string watcher = f.SeedPausedWatcher(); // loaded scheduler, but its loop has not started yet
        TaskCompletionSource registered = Signal();
        f.Composition.Services.Watchers.Changed += () => registered.TrySetResult();
        Task<BooruWatcherCommandResult> pending = f.Composition.Services.Watchers.CheckNowAsync(watcher, CancellationToken.None);
        await registered.Task.WaitAsync(Deadline);
        Assert.False(pending.IsCompleted);
        f.SetEnabled(false);
        AssertUnavailable(await pending.WaitAsync(Deadline));
        f.SetEnabled(true);
        await WaitAsync(() => f.Composition.IsAvailable);
        await f.StartAsync();
        // All watchers are paused and the disabled scheduler's manual queue was drained, not replayed.
        Assert.Equal(0, f.Worker.Calls);
        Assert.False(Assert.Single(await f.Composition.Services.Watchers.ListAsync(CancellationToken.None)).BaselineEstablished);
        Assert.Equal(BooruWatcherCommandStatus.Done, (await f.Composition.Services.Watchers.CheckNowAsync(watcher,
            CancellationToken.None).WaitAsync(Deadline)).Status);
        Assert.Equal(1, f.Worker.Calls);
    }

    [Fact]
    public async Task SearchShowsAnUnsavedRefusalAndRetrySavesIt()
    {
        await using Fixture f = new();
        await f.StartAsync();
        using BooruSearchModel model = await OpenOriginalSearchAsync(f);
        await f.Queue.StopAllAsync();
        f.Composition.Originals.OutcomeChanged += outcome =>
        {
            if (outcome.State != BooruOriginalDownloadState.Queued) return;
            f.Catalog.BeforeAdmittedMutationForTests = _ =>
            {
                f.Catalog.BeforeAdmittedMutationForTests = null;
                throw new IOException("refusal write failed");
            };
        };

        await model.LoadOriginalAsync("1");
        BooruResultRowView pending = Assert.Single(model.View.Rows);
        Assert.Equal(BooruRowLoad.RefusalPending, pending.Load);
        Assert.True(pending.CanRetry);
        Assert.True(Assert.Single(f.Catalog.Read().Posts).HasPendingOriginal);

        await model.RetryOriginalAsync("1");
        BooruResultRowView saved = Assert.Single(model.View.Rows);
        Assert.Equal(BooruRowLoad.Failed, saved.Load);
        Assert.Equal(BooruCodes.NotAdmitted, saved.LoadCode);
        Assert.Equal(BooruOriginalIntent.NotAdmitted, Assert.Single(f.Catalog.Read().Posts).OriginalIntent);

        long writes = f.Catalog.Counters.Writes;
        BooruLoadResult repeated = await f.Composition.Services.Queue.SaveOriginalRefusalAsync("1", CancellationToken.None);
        Assert.Equal(BooruCodes.NotAdmitted, repeated.Code);
        Assert.Equal(writes, f.Catalog.Counters.Writes);
        Assert.Equal(0, f.Http.Originals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompletedOriginalFailureReachesTheSearchAndRetryCanFinishIt(bool failLink)
    {
        await using Fixture f = new();
        await f.StartAsync();
        using BooruSearchModel model = await OpenOriginalSearchAsync(f);
        if (failLink)
            f.Composition.Originals.LinkingForTests = (_, _) => throw new IOException("link failed");
        else
            f.Http.BadOriginal = true;

        await model.LoadOriginalAsync("1");
        await f.Queue.SetPausedAsync(false);
        BooruRowLoad expected = failLink ? BooruRowLoad.LinkFailed : BooruRowLoad.DownloadFailed;
        await WaitAsync(() => model.View.Rows.Single().Load == expected);
        Assert.True(Assert.Single(model.View.Rows).CanRetry);
        Assert.True(Assert.Single(f.Catalog.Read().Posts).HasPendingOriginal);
        int requests = f.Http.Originals;
        Assert.True(requests > 0);

        f.Composition.Originals.LinkingForTests = null;
        f.Http.BadOriginal = false;
        await model.RetryOriginalAsync("1");
        await WaitAsync(() => model.View.Rows.Single().Load == BooruRowLoad.AlreadyLoaded);
        Assert.False(Assert.Single(model.View.Rows).CanRetry);
        Assert.Equal(BooruOriginalIntent.Linked, Assert.Single(f.Catalog.Read().Posts).OriginalIntent);
        Assert.Equal(failLink ? 1 : requests + 1, f.Http.Originals);
    }

    [Fact]
    public async Task CallerCancellationIsStillCancellationAndLegacyConstructorDefaultsRemainUsable()
    {
        await using Fixture f = new();
        f.Composition.WatcherHost.Scheduler.Load();
        using BooruOriginalQueuePort queue = new(f.Composition.Originals, f.Catalog, "danbooru");
        using BooruSchedulerWatcherPort watchers = new(f.Composition.WatcherHost.Scheduler, f.Catalog);
        BooruHostPreviewPort previews = new(f.Composition.Previews, "danbooru");
        Assert.NotNull(await previews.LoadPreviewAsync(Post("1"), CancellationToken.None));
        Assert.Equal(BooruLoadStatus.Queued,
            (await queue.LoadOriginalAsync(Post("1"), BooruLoadOrigin.Search, CancellationToken.None)).Status);
        Assert.True(await queue.CancelOriginalAsync("1", CancellationToken.None));
        Assert.Equal(BooruLoadStatus.Queued,
            (await queue.RetryOriginalAsync("1", CancellationToken.None)).Status);
        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.CreateAsync("cat", CancellationToken.None)).Status);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previews.LoadPreviewAsync(Post("2"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.LoadOriginalAsync(Post("2"), BooruLoadOrigin.Search, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchers.CreateAsync("dog", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watchers.CheckNowAsync("missing", cancelled.Token));
    }

    private static async Task AssertCommandsUnavailable(IBooruWatcherPort watchers, string id)
    {
        AssertUnavailable(await watchers.CreateAsync("dog", CancellationToken.None));
        AssertUnavailable(await watchers.CheckNowAsync(id, CancellationToken.None));
        AssertUnavailable(await watchers.PauseAsync(id, CancellationToken.None));
        AssertUnavailable(await watchers.ResumeAsync(id, CancellationToken.None));
        AssertUnavailable(await watchers.RemoveAsync(id, CancellationToken.None));
        AssertUnavailable(await watchers.RestartFromNowAsync(id, CancellationToken.None));
    }

    private static void AssertUnavailable(BooruLoadResult result) =>
        Assert.Equal((BooruLoadStatus.Failed, BooruCodes.SourceUnavailable), (result.Status, result.Code));
    private static void AssertUnavailable(BooruWatcherCommandResult result) =>
        Assert.Equal((BooruWatcherCommandStatus.Failed, BooruCodes.SourceUnavailable), (result.Status, result.Code));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task WaitAsync(Func<bool> predicate) => BooruTestSupport.WaitAsync(predicate, "booru availability boundary", 15000);

    private static async Task<BooruSearchModel> OpenOriginalSearchAsync(Fixture f)
    {
        FakeBooruSearchPort search = new();
        search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, Post("1"))));
        FakeBooruPreviewPort previews = new() { OnPreview = _ => null };
        BooruSearchModel model = new(search, f.Composition.Services.Queue, previews, TimeProvider.System);
        try
        {
            await BooruTestSupport.SearchAsync(model, "cat");
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private static BooruPostView Post(string id) => BooruPostView.From(new BooruPost(new BooruPostId("danbooru", id),
        BooruFieldPresence.Media, Media:
        [
            new BooruMediaVariant(BooruMediaRole.Preview, new Uri($"https://cdn.donmai.us/preview/{id}.jpg")),
            new BooruMediaVariant(BooruMediaRole.Original, new Uri($"https://cdn.donmai.us/original/{id}.jpg"),
                ".jpg", Image.Length, ExpectedMd5: Convert.ToBase64String(MD5.HashData(Image)))
        ]));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly ProfileLease _lease;
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _metadata;
        private volatile bool _enabled = true;
        private readonly BooruProviderBinding _binding;

        internal Fixture(bool paused = true)
        {
            string profile = Path.Combine(_directory.Path, "profile");
            _lease = ProfileLease.Acquire(profile);
            profile = _lease.ProfileDirectory;
            Queue = new DownloadQueue(1, 1L << 30);
            Queue.ConfigureWorkState(new WorkStateStore(profile, _lease));
            Queue.BindProfileLease(_lease);
            Catalog = new BooruCatalogStore(profile, _lease);
            Catalog.Read();
            _lease.Admission.CompleteStartup();
            Producers = new BooruProducerLifecycle(_lease.Admission);
            Producers.Open();
            Queue.SetPaused(paused);
            Http = new MemoryHttp();
            _transport = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
            RequestPolicyState policy = new();
            SourceDescription source = new("danbooru", "Danbooru", new Uri("https://danbooru.donmai.us/"),
                SourceCapabilities.PostSearch | SourceCapabilities.Previews | SourceCapabilities.OriginalMedia, [],
                [new HttpHostDeclaration("danbooru.donmai.us", HttpHostPurpose.Metadata),
                 new HttpHostDeclaration("cdn.donmai.us", HttpHostPurpose.PreviewMedia | HttpHostPurpose.OriginalMedia)],
                new SettingsSchema("1", []), PostOrderKind.DecimalPostId);
            _metadata = new BooruHostHttp(_transport, policy, [source]);
            BooruHostServices services = new(new NoInnerHttp(), _metadata);
            Worker = new MemoryWorker();
            _binding = new BooruProviderBinding(source, services, _transport, policy,
                ResolveWorkerAsync, () => _enabled);
            Composition = BooruSessionComposition.Create(_binding, Queue, Catalog, Path.Combine(_directory.Path, "media"), Producers);
            Composition.SyncSessionPause();
        }

        private int _workerResolutions;
        internal int WorkerResolutions => Volatile.Read(ref _workerResolutions);
        internal bool HoldWorkerResolution { get; set; }
        internal TaskCompletionSource WorkerResolutionEntered { get; } = Signal();
        internal TaskCompletionSource ReleaseWorkerResolution { get; } = Signal();
        internal CancellationToken WorkerResolutionToken { get; private set; }

        private async Task<IPluginWorkerClient?> ResolveWorkerAsync(CancellationToken token)
        {
            WorkerResolutionToken = token;
            Interlocked.Increment(ref _workerResolutions);
            if (HoldWorkerResolution)
            {
                WorkerResolutionEntered.TrySetResult();
                await ReleaseWorkerResolution.Task; // deliberately uncooperative resolver
            }
            return _enabled ? Worker : null;
        }

        internal MemoryHttp Http { get; }
        internal MemoryWorker Worker { get; }
        internal DownloadQueue Queue { get; }
        internal BooruCatalogStore Catalog { get; }
        internal BooruProducerLifecycle Producers { get; }
        internal BooruSessionComposition Composition { get; }

        internal string SeedPausedWatcher()
        {
            Composition.WatcherHost.Scheduler.Load();
            Assert.True(Composition.WatcherHost.Scheduler.AddWatcher("cat").Accepted);
            string id = Assert.Single(Composition.WatcherHost.Scheduler.GetSnapshot().Watchers).WatcherId;
            Assert.True(Composition.WatcherHost.Scheduler.Pause(id).Accepted);
            return id;
        }

        internal async Task StartAsync()
        {
            await Composition.StartAsync(CancellationToken.None).WaitAsync(Deadline);
        }

        internal void BeginProfileShutdown()
        {
            Producers.BeginStop();
            _lease.Admission.BeginShutdown();
        }

        internal void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            _binding.RaiseAvailabilityChanged();
        }

        public async ValueTask DisposeAsync()
        {
            Http.ReleasePreviews.TrySetResult();
            ReleaseWorkerResolution.TrySetResult();
            Worker.Release.TrySetResult();
            try
            {
                Producers.BeginStop();
                await Composition.StopAsync().WaitAsync(Deadline);
                await Producers.StopAsync().WaitAsync(Deadline);
                await Queue.StopAllAsync().WaitAsync(Deadline);
            }
            finally
            {
                Composition.ReleaseTransport();
                _metadata.Dispose();
                _transport.Dispose();
                _lease.Dispose();
                _directory.Dispose();
            }
        }
    }

    private sealed class MemoryHttp : HttpMessageHandler
    {
        private int _previews, _originals;
        internal bool HoldPreviews { get; set; }
        internal bool BadOriginal { get; set; }
        internal TaskCompletionSource ReleasePreviews { get; } = Signal();
        internal CancellationToken PreviewToken { get; private set; }
        internal int Previews => Volatile.Read(ref _previews);
        internal int Originals => Volatile.Read(ref _originals);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("cdn.donmai.us", request.RequestUri!.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri.AbsolutePath.StartsWith("/preview/", StringComparison.Ordinal))
            {
                PreviewToken = cancellationToken;
                Interlocked.Increment(ref _previews);
                if (HoldPreviews) await ReleasePreviews.Task; // deliberate non-cooperative completion
            }
            else
            {
                Interlocked.Increment(ref _originals);
                if (BadOriginal)
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            }
            // Original downloads are MIME-validated before the queue publishes the verified asset.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Image)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") }
                },
                RequestMessage = request
            };
        }
    }

    private sealed class MemoryWorker : IPluginWorkerClient
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool Hold { get; set; }
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal CancellationToken Token { get; private set; }
        public PluginDescription Description => throw new NotSupportedException();
        public PluginWorkerIdentity Identity { get; } = new("memory", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);

        public async Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref _calls);
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task; // deliberate late page even after cancellation
                Hold = false;
                return new BooruSearchSnapshot(new BooruSearchPage("danbooru", request.Query, request.Page,
                    [Post("99").Post], true));
            }
            return new BooruSearchSnapshot(new BooruSearchPage("danbooru", request.Query, request.Page, [], true));
        }

        public async Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Interlocked.Increment(ref _calls);
            if (Hold)
            {
                Entered.TrySetResult();
                await Release.Task; // deliberate late post even after cancellation
                Hold = false;
            }
            return new BooruPostSnapshot(Post(request.Post.PostId).Post);
        }

        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoInnerHttp : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No fallback HTTP.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No image service.");
    }
}
