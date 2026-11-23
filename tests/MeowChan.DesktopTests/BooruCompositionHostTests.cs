using System.Net.Http;
using System.Reflection;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// G1: the desktop session composes the Danbooru services over the real queue, catalog and scheduler. Network is
// the in-memory DanbooruStub; the Danbooru plugin runs in-process behind the real booru host services.
[Collection("Avalonia global state")]
public sealed class BooruCompositionHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AnEnabledBindingComposesAndStartsTheWatcherHostWithoutAnyRequest()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);

        BooruSessionComposition composition = Assert.IsType<BooruSessionComposition>(host.BooruComposition);
        Assert.True(composition.WatcherHost.Scheduler.GetSnapshot().Running);
        Assert.Same(composition.Services, host.BooruServices);
        Assert.Equal("danbooru", host.BooruServices!.Search.ProviderId);
        Assert.Empty(stub.Requests);
        Assert.Equal(0, binding.Worker.SearchCalls);
    }

    [Fact]
    public async Task ActiveBindingsExposeSeparateWatcherPortsAndCanBeDisabledIndependently()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding legacy = new(stub);
        SourceDescription source = legacy.Source with
        {
            SourceId = "test-source", Name = "Fixture", BooruProfile = BooruEndpointPolicy.Danbooru
        };
        using BooruHostHttp http = new(legacy.Transport, legacy.Policy, [source]);
        BooruHostServices services = new(legacy.Services, http);
        bool enabled = true;
        BooruProviderBinding second = new(source, services, legacy.Transport, legacy.Policy,
            _ => Task.FromResult<IPluginWorkerClient?>(null), () => Volatile.Read(ref enabled));
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => null, paused: true,
            bindings: [legacy.Binding, second]);
        await WaitAsync(() => host.ActiveBooruServices.Count == 2, "both Booru compositions");

        IReadOnlyList<BooruWorkbenchServices> active = host.ActiveBooruServices;
        Assert.Equal(["danbooru", "test-source"], active.Select(item => item.Search.ProviderId));
        foreach (BooruWorkbenchServices item in active)
            Assert.Equal(BooruWatcherCommandStatus.Done, (await item.Watchers.CreateAsync("cat", CancellationToken.None)).Status);
        string first = Assert.Single(await active[0].Watchers.ListAsync(CancellationToken.None)).WatcherId;
        string other = Assert.Single(await active[1].Watchers.ListAsync(CancellationToken.None)).WatcherId;
        Assert.NotEqual(first, other);
        Assert.Contains(ReadCatalog(host).Watchers, watcher => watcher.WatcherId == other && watcher.SourceId == "test-source");

        Volatile.Write(ref enabled, false);
        second.RaiseAvailabilityChanged();
        Assert.Same(active[0], Assert.Single(host.ActiveBooruServices));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ADisabledOrMissingPluginConstructsNothingAndHidesTheWindowUntilEnabled()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        await using (DesktopSessionHost none = await StartAsync(profile.Path, stub, () => null))
        {
            Assert.Null(none.BooruComposition);
            Assert.Null(none.BooruServices);
        }

        using TempDirectory second = new();
        using DanbooruTestBinding binding = new(stub, enabled: false);
        await using DesktopSessionHost host = await StartAsync(second.Path, stub, () => binding.Binding);
        Assert.Null(host.BooruComposition);
        Assert.Null(host.BooruServices);
        Assert.False(File.Exists(Path.Combine(second.Path, BooruCatalogStore.FileName)));

        // Enabling the plugin in the running session composes then, still without a provider request.
        binding.SetEnabled(true);
        await WaitAsync(() => host.BooruServices != null, "composition after enable");
        Assert.True(host.BooruComposition!.WatcherHost.Scheduler.GetSnapshot().Running);
        // Disabling again hides the window; the search reports "unavailable" without a request.
        binding.SetEnabled(false);
        Assert.Null(host.BooruServices);
        BooruCallResult<BooruResultPage> search = await host.BooruComposition.Services.Search.SearchAsync("cat_ears", 1, BooruSearchModel.PageSize, CancellationToken.None);
        Assert.Equal(BooruCallStatus.Unavailable, search.Status);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task ShutdownStopsTheWatchersBeforeTheQueueAndTheRegistry()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub);
        DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);
        BooruSessionComposition composition = host.BooruComposition!;
        bool? queueStoppingWhenWatchersStopped = null;
        bool? schedulerRunningThen = null;
        host.AfterBooruStoppedForTests = queueStopping =>
        {
            queueStoppingWhenWatchersStopped = queueStopping;
            schedulerRunningThen = composition.WatcherHost.Scheduler.GetSnapshot().Running;
        };
        await host.StopAsync().WaitAsync(Deadline);
        await host.DisposeAsync();
        Assert.False(queueStoppingWhenWatchersStopped, "The queue was already stopping when the watchers stopped.");
        Assert.False(schedulerRunningThen);
        Assert.Null(host.BooruServices);
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task AfterShutdownTheCompositionIsUnavailableStopsIdempotentlyAndNeverComposesAgain()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub, enabled: false);
        DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);
        binding.SetEnabled(true);
        await WaitAsync(() => host.BooruComposition != null, "composition after enable");
        BooruSessionComposition composition = host.BooruComposition!;
        await host.StopAsync().WaitAsync(Deadline);

        Assert.False(composition.IsAvailable);
        Assert.Null(host.BooruServices);
        // A second stop and a late transport release are harmless.
        await composition.StopAsync().WaitAsync(Deadline);
        composition.ReleaseTransport();
        // The availability event is unhooked at shutdown: toggling the plugin cannot revive anything.
        binding.SetEnabled(false);
        binding.SetEnabled(true);
        await Task.Delay(200);
        Assert.Same(composition, host.BooruComposition);
        Assert.Null(host.BooruServices);
        await host.DisposeAsync();
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedStartsRetainEveryUnpublishedTransportUntilSessionShutdown(bool cancelled)
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub, enabled: false);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding, paused: true);
        await Field<Task>(host, "_booruStart").WaitAsync(Deadline); // Finish the initial disabled-binding probe.
        // The existing clock seam fails scheduler load before publication, without a new lifecycle test hook.
        host.BooruClockForTests = new AbortedStartClock(cancelled);
        List<BooruSessionComposition> aborted = new();
        List<HttpClient> clients = new();
        using CancellationTokenSource probe = new();
        probe.Cancel();
        const string original = "https://cdn.donmai.us/original/transport-lifetime.png";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt == 1) binding.SetEnabled(true);
            else binding.Binding.RaiseAvailabilityChanged(); // Retry an optional start, not a second shared queue.
            await WaitAsync(() => TransportOwners(host).Length == attempt, "the aborted composition's transport owner");
            SemaphoreSlim gate = Field<SemaphoreSlim>(host, "_booruGate");
            using CancellationTokenSource gateDeadline = new(Deadline);
            await gate.WaitAsync(gateDeadline.Token);
            try
            {
                // The gate is released only after the failed start's StopAsync cleanup really finished.
                BooruSessionComposition composition = TransportOwners(host)[^1];
                aborted.Add(composition);
                clients.Add(Field<HttpClient>(composition, "_mediaClient"));
                Assert.False(composition.IsAvailable);
                Assert.Null(host.BooruComposition);
                Assert.Null(host.BooruServices);
                Assert.Same(Field<DownloadQueue>(host, "_downloadQueue"),
                    Field<DownloadQueue>(composition.Originals, "_queue"));
            }
            finally { gate.Release(); }

            // An already-cancelled send probes HttpClient lifetime without reaching even the in-memory transport.
            foreach (HttpClient client in clients)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(original, probe.Token));
        }

        await host.StopAsync().WaitAsync(Deadline);
        Assert.Empty(TransportOwners(host));
        foreach (HttpClient client in clients)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync(original));
        // Session stop and a late composition cleanup remain idempotent.
        await host.StopAsync().WaitAsync(Deadline);
        foreach (BooruSessionComposition composition in aborted)
        {
            await composition.StopAsync().WaitAsync(Deadline);
            composition.ReleaseTransport();
        }
        Assert.Empty(stub.Requests);
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task GlobalPauseReachesAnUnpublishedCompositionBeforeItsFirstAutomaticCheck()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub, enabled: false);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);
        await Field<Task>(host, "_booruStart").WaitAsync(Deadline);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeBooruStartForTests = token =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(token);
        };
        binding.SetEnabled(true);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            BooruSessionComposition pending = Assert.Single(TransportOwners(host));
            Assert.Null(host.BooruComposition);
            await host.SetPausedAsync(true).WaitAsync(Deadline);
            // The session must synchronize registered transport owners, not only the published composition.
            Assert.True(pending.WatcherHost.Scheduler.SessionPaused);
        }
        finally { release.TrySetResult(); }
        await WaitAsync(() => host.BooruComposition != null, "the paused composition after its start was released");
        DanbooruWatcherScheduler scheduler = host.BooruComposition!.WatcherHost.Scheduler;
        Assert.True(scheduler.SessionPaused); // the older pre-pause snapshot did not overwrite the confirmed pause
        Assert.True(scheduler.AddWatcher("cat_ears").Accepted);
        string watcher = scheduler.GetSnapshot().Watchers.Single().WatcherId;
        Assert.Null(await scheduler.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(0, stub.MetadataRequests);
        Assert.Null((await scheduler.CheckNowAsync(watcher).WaitAsync(Deadline)).Code);
        Assert.True(scheduler.SessionPaused);
        Assert.Equal(1, stub.MetadataRequests);
    }

    [Fact]
    public async Task StartupFailureAfterOpeningTheBooruGenerationDrainsItAndCannotLeaveReadyAuthority()
    {
        using TempDirectory profile = new(); // A genuine new profile reaches the post-Ready initial save seam.
        using DanbooruStub stub = new();
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(profile.Path, Array.Empty<IImageBoard>(),
            updateSessionFactory: (path, config) => new UpdateSession(path, config, stub.Client(), disableEnvironment: () => "1")));
        host.Config.AutomaticUpdateChecks = false;
        host.Config.SaveUrlsOnExit = false;
        host.Config.DownloadPath = Path.Combine(profile.Path, "media");
        bool observerUnderHostMonitor = false;
        host.BooruProducers.Changed += () => observerUnderHostMonitor |= Monitor.IsEntered(Field<object>(host, "_lifecycle"));
        host.BeforeStartupInitialSaveForTests = () =>
        {
            Assert.Equal(BooruProducerPhase.Open, host.BooruProducers.Phase);
            throw new InvalidOperationException("Synthetic post-activation startup failure.");
        };
        await host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.Contains("Synthetic post-activation startup failure", host.StartupError);
        Assert.Equal((BooruProducerPhase.Stopped, 1L), (host.BooruProducers.Phase, host.BooruProducers.Generation));
        Assert.True(Field<ProfileLease>(host, "_profileLease").Admission.DrainAsync().IsCompletedSuccessfully);
        Assert.Throws<InvalidOperationException>(host.BooruProducers.Open);
        await host.StopAsync().WaitAsync(Deadline);
        Assert.False(observerUnderHostMonitor);
        Assert.Empty(stub.Requests);
    }

    private sealed class AbortedStartClock(bool cancelled) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            if (cancelled) throw new OperationCanceledException("Synthetic Booru start cancellation.");
            throw new InvalidOperationException("Synthetic Booru start failure.");
        }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static BooruSessionComposition[] TransportOwners(DesktopSessionHost host)
    {
        lock (Field<object>(host, "_booruSync"))
            return Field<List<BooruSessionComposition>>(host, "_booruTransportOwners").ToArray();
    }

    [Fact]
    public async Task SearchPreviewsAndLoadOriginalRunThroughTheRealQueueExactlyOnce()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost first = stub.Add(new(4003, Rating: "e", Color: 0xFFD94A4A));
        DanbooruStubPost second = stub.Add(new(4002, Rating: "s", Color: 0xFF4AD97A));
        DanbooruStubPost restricted = stub.Add(new(4001, Restricted: true));
        stub.Publish("cat_ears", first.Id, second.Id, restricted.Id);
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding, paused: true);
        BooruWorkbenchServices services = host.BooruServices!;
        int changes = 0;
        services.Queue.LocalStatesChanged += () => Interlocked.Increment(ref changes);

        BooruCallResult<BooruResultPage> result = await services.Search.SearchAsync("cat_ears", 1, BooruSearchModel.PageSize, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(BooruCallStatus.Ok, result.Status);
        BooruResultPage page = result.Value!;
        Assert.Equal(new[] { "4003", "4002", "4001" }, page.Posts.Select(post => post.PostId));
        // No hidden rating filter: explicit, sensitive and general posts all come back, and the wire query is the user's.
        Assert.Equal("https://danbooru.donmai.us/posts.json?tags=cat_ears&page=1&limit=20", Assert.Single(stub.Requests));
        Assert.Equal(1, binding.Worker.SearchCalls);

        // Previews only through the bounded preview path, from the declared CDN host.
        byte[]? preview = await services.Previews.LoadPreviewAsync(page.Posts[0], CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(first.Preview, preview);
        Assert.Equal(first.Preview, await services.Previews.LoadPreviewAsync(page.Posts[0], CancellationToken.None)); // cached
        Assert.Equal(1, stub.PreviewRequests);
        Assert.Equal(0, stub.OriginalRequests + stub.SampleRequests);

        Assert.Empty(await services.Queue.GetLocalStatesAsync(new[] { "4003" }, CancellationToken.None));
        BooruLoadResult queued = await services.Queue.LoadOriginalAsync(page.Posts[0], BooruLoadOrigin.Search, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(BooruLoadStatus.Queued, queued.Status);
        BooruLoadResult again = await services.Queue.LoadOriginalAsync(page.Posts[0], BooruLoadOrigin.Search, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(BooruLoadStatus.AlreadyQueued, again.Status);
        BooruLoadResult withheld = await services.Queue.LoadOriginalAsync(page.Posts[2], BooruLoadOrigin.Search, CancellationToken.None);
        Assert.Equal(BooruLoadStatus.Restricted, withheld.Status);
        IReadOnlyDictionary<string, BooruLocalState> states = await services.Queue.GetLocalStatesAsync(new[] { "4003", "4002" }, CancellationToken.None);
        Assert.Equal(BooruLocalState.Queued, states["4003"]);
        Assert.False(states.ContainsKey("4002"));
        Assert.True(Volatile.Read(ref changes) > 0);
        Assert.Equal(1, host.BooruComposition!.Originals.ActiveCount);
        Assert.Equal(0, stub.OriginalRequests); // the queue is paused

        await host.SetPausedAsync(false);
        await WaitAsync(async () => (await services.Queue.GetLocalStatesAsync(new[] { "4003" }, CancellationToken.None))
            .GetValueOrDefault("4003") == BooruLocalState.Loaded, "the original to be linked");
        Assert.Equal(1, stub.OriginalRequests);
        Assert.Equal(0, stub.SampleRequests);
        Assert.Equal(BooruLoadStatus.AlreadyLoaded,
            (await services.Queue.LoadOriginalAsync(page.Posts[0], BooruLoadOrigin.OneShot, CancellationToken.None)).Status);
        Assert.Equal(1, stub.OriginalRequests);
        Assert.Empty(host.BooruComposition.Originals.GetPendingOriginals());
        BooruPostRecord record = Assert.Single(ReadCatalog(host).Posts, post => post.PostId == "4003");
        Assert.NotNull(record.LocalAssetId);
        Assert.Contains(record.Findings, finding => finding.Kind == BooruFindingKind.Search);
        Assert.Contains(record.Findings, finding => finding.Kind == BooruFindingKind.OneShot);
        Assert.True(File.Exists(record.LocalPath ?? throw new InvalidOperationException("no local path")));
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task APostLinkReadsExactlyThatPost()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(4242));
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding, paused: true);
        BooruCallResult<BooruPostView> post = await host.BooruServices!.Search.ReadPostAsync("4242", CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(BooruCallStatus.Ok, post.Status);
        Assert.Equal("4242", post.Value!.PostId);
        Assert.Equal("https://danbooru.donmai.us/posts/4242.json", Assert.Single(stub.Requests));
        Assert.Equal(BooruCallStatus.NotFound,
            (await host.BooruServices.Search.ReadPostAsync("4243", CancellationToken.None).WaitAsync(Deadline)).Status);
    }

    [Fact]
    public async Task WatcherCommandsRoundTripThroughTheScheduler()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(5001));
        stub.Publish("cat_ears", 5001);
        using DanbooruTestBinding binding = new(stub);
        // F3: a globally paused session no longer checks watchers. Admitted originals stay visibly queued because
        // the stub holds their transfer, not because the session is paused.
        stub.HoldOriginals();
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);
        IBooruWatcherPort watchers = host.BooruServices!.Watchers;
        int changed = 0;
        watchers.Changed += () => Interlocked.Increment(ref changed);
        Assert.Empty(await watchers.ListAsync(CancellationToken.None));

        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.CreateAsync("cat_ears", CancellationToken.None)).Status);
        BooruWatcherCommandResult duplicate = await watchers.CreateAsync("cat_ears", CancellationToken.None);
        Assert.Equal((BooruWatcherCommandStatus.Rejected, BooruCodes.WatcherExists), (duplicate.Status, duplicate.Code));
        BooruWatcherView created = Assert.Single(await watchers.ListAsync(CancellationToken.None));
        Assert.Equal("cat_ears", created.Query);
        Assert.True(Volatile.Read(ref changed) > 0);

        // The scheduler's own first check only sets the baseline ("from now"): nothing is queued.
        await WaitAsync(async () => (await watchers.ListAsync(CancellationToken.None)).Single() is
            { BaselineEstablished: true, Status: BooruWatcherStatus.Active }, "the automatic baseline check");
        Assert.Equal(1, stub.MetadataRequests);
        Assert.Equal(0, host.BooruComposition!.Originals.ActiveCount);
        BooruWatcherCommandResult manual = await watchers.CheckNowAsync(created.WatcherId, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal((BooruWatcherCommandStatus.Done, 0, 0), (manual.Status, manual.NewPosts, manual.Queued));

        // A paused watcher may be checked once and stays paused; new posts go to the shared queue.
        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.PauseAsync(created.WatcherId, CancellationToken.None)).Status);
        Assert.Equal(BooruWatcherStatus.Paused, Assert.Single(await watchers.ListAsync(CancellationToken.None)).Status);
        stub.Add(new(5003));
        stub.Add(new(5002));
        stub.Publish("cat_ears", 5003, 5002);
        BooruWatcherCommandResult found = await watchers.CheckNowAsync(created.WatcherId, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal((BooruWatcherCommandStatus.Done, 2, 2), (found.Status, found.NewPosts, found.Queued));
        BooruWatcherView paused = Assert.Single(await watchers.ListAsync(CancellationToken.None));
        Assert.Equal((BooruWatcherStatus.Paused, 2), (paused.Status, paused.HitCount));
        Assert.Equal(2, host.BooruComposition.Originals.ActiveCount);

        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.ResumeAsync(created.WatcherId, CancellationToken.None)).Status);
        BooruWatcherView resumed = Assert.Single(await watchers.ListAsync(CancellationToken.None));
        Assert.Equal(BooruWatcherStatus.Active, resumed.Status);
        Assert.NotNull(resumed.NextCheckAt);

        // Restart from now replaces the watcher with a fresh one for the same query.
        BooruWatcherCommandResult restarted = await watchers.RestartFromNowAsync(created.WatcherId, CancellationToken.None);
        Assert.Equal(BooruWatcherCommandStatus.Done, restarted.Status);
        BooruWatcherView fresh = Assert.Single(await watchers.ListAsync(CancellationToken.None));
        Assert.NotEqual(created.WatcherId, fresh.WatcherId);
        Assert.Equal("cat_ears", fresh.Query);

        // Removal stops observation only: the queued originals and their findings stay.
        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.RemoveAsync(fresh.WatcherId, CancellationToken.None)).Status);
        Assert.Empty(await watchers.ListAsync(CancellationToken.None));
        BooruWatcherCommandResult gone = await watchers.CheckNowAsync(fresh.WatcherId, CancellationToken.None);
        Assert.Equal((BooruWatcherCommandStatus.Rejected, BooruCodes.WatcherRemoved), (gone.Status, gone.Code));
        Assert.Equal(2, host.BooruComposition.Originals.ActiveCount);
        Assert.Equal(2, ReadCatalog(host).Posts.Count(post => post.Findings.Any(finding => finding.Kind == BooruFindingKind.Watcher)));
        Assert.Equal(new[] { "https://danbooru.donmai.us/posts.json?tags=cat_ears&page=1&limit=20" },
            stub.Requests.Where(url => new Uri(url).AbsolutePath.EndsWith(".json", StringComparison.Ordinal)).Distinct());
        // Baseline, one manual check, the paused one-time check and at most the new watcher's own baseline.
        Assert.InRange(stub.MetadataRequests, 3, 4);
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task TheGlobalSessionPauseHoldsAutomaticWatcherChecksButAllowsCheckNow()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(5001));
        stub.Publish("cat_ears", 5001);
        stub.Publish("dog", 5001);
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding, paused: true);
        DanbooruWatcherScheduler scheduler = host.BooruComposition!.WatcherHost.Scheduler;
        Assert.True(scheduler.SessionPaused); // set from the confirmed session pause before the first pick
        IBooruWatcherPort watchers = host.BooruServices!.Watchers;
        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.CreateAsync("cat_ears", CancellationToken.None)).Status);
        BooruWatcherView created = Assert.Single(await watchers.ListAsync(CancellationToken.None));
        Assert.Null(await scheduler.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(0, stub.MetadataRequests);
        Assert.Equal(BooruWatcherCommandStatus.Done,
            (await watchers.CheckNowAsync(created.WatcherId, CancellationToken.None).WaitAsync(Deadline)).Status);
        Assert.True(scheduler.SessionPaused);
        Assert.Equal(1, stub.MetadataRequests);
        Assert.True(Assert.Single(await watchers.ListAsync(CancellationToken.None)).BaselineEstablished);

        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.CreateAsync("dog", CancellationToken.None)).Status);
        Assert.Null(await scheduler.RunNextAsync().WaitAsync(Deadline));
        Assert.Equal(1, stub.MetadataRequests);
        await host.SetPausedAsync(false).WaitAsync(Deadline);
        Assert.False(scheduler.SessionPaused);
        await WaitAsync(async () => (await watchers.ListAsync(CancellationToken.None)).Single(w => w.Query == "dog").BaselineEstablished,
            "the automatic baseline after resume");
        Assert.Equal(2, stub.MetadataRequests);
        await host.SetPausedAsync(true).WaitAsync(Deadline);
        Assert.True(scheduler.SessionPaused);
        Assert.Equal(BooruWatcherCommandStatus.Done,
            (await watchers.CheckNowAsync(created.WatcherId, CancellationToken.None).WaitAsync(Deadline)).Status);
        Assert.True(scheduler.SessionPaused);
        Assert.Equal(3, stub.MetadataRequests);
        Assert.Empty(stub.Violations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AProfileExclusiveOperationDrainsABlockedWatcherCheckBeforeItsDelegate(bool safeToResume)
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(5001));
        stub.Publish("cat_ears", 5001);
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await StartAsync(profile.Path, stub, () => binding.Binding);
        DanbooruWatcherScheduler scheduler = host.BooruComposition!.WatcherHost.Scheduler;
        Task entered = stub.HoldMetadata();
        Assert.True(scheduler.AddWatcher("cat_ears").Accepted);
        await entered.WaitAsync(Deadline); // the automatic baseline is in flight
        Assert.Equal(1, host.BooruProducers.Generation);

        bool checkEndedBeforeDelegate = false;
        ProfileExclusiveOutcome<int> outcome = await host.RunWithProfileProducerExclusiveAsync(_ =>
        {
            checkEndedBeforeDelegate = scheduler.GetSnapshot().CheckingWatcherId is null
                && host.BooruProducers.Phase == BooruProducerPhase.Suspended;
            return Task.FromResult(safeToResume ? ProfileExclusiveOutcome<int>.SafeToResume(0)
                : ProfileExclusiveOutcome<int>.RecoveryRequired(0));
        }).WaitAsync(Deadline);
        Assert.True(checkEndedBeforeDelegate);
        Assert.Equal(nameof(DanbooruWatcherState.Active), Assert.Single(ReadCatalog(host).Watchers).State);
        stub.ReleaseMetadata();
        if (safeToResume)
        {
            Assert.Equal((BooruProducerPhase.Open, 2L), (host.BooruProducers.Phase, host.BooruProducers.Generation));
            await WaitAsync(() => scheduler.GetSnapshot().Watchers.Single().BaselineEstablished, "the replayed baseline");
            Assert.Equal(2, stub.MetadataRequests); // the cut request and exactly one replay
        }
        else
        {
            Assert.Equal(BooruProducerPhase.RecoveryRequired, host.BooruProducers.Phase);
            Assert.Equal(1, stub.MetadataRequests);
        }
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task ASchedulerLoadFaultIsShownWithItsCodeInsteadOfAnEmptyList()
    {
        using TempDirectory profile = new();
        await File.WriteAllTextAsync(Path.Combine(profile.Path, BooruCatalogStore.FileName), "{ not json");
        using MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(profile.Path);
        BooruCatalogStore catalog = new(profile.Path, lease);
        lease.Admission.CompleteStartup();
        await using DanbooruWatcherScheduler scheduler = new(catalog, new NoSearch(), new NoAdmission());
        scheduler.Load();
        Assert.Equal(BooruCodes.CatalogUnreadable, scheduler.GetSnapshot().FaultCode);
        using BooruSchedulerWatcherPort port = new(scheduler, catalog);
        BooruPortException fault = await Assert.ThrowsAsync<BooruPortException>(() => port.ListAsync(CancellationToken.None));
        Assert.Equal(BooruCodes.CatalogUnreadable, fault.Code);
        BooruWatcherCommandResult create = await port.CreateAsync("cat_ears", CancellationToken.None);
        Assert.Equal((BooruWatcherCommandStatus.Rejected, BooruCodes.CatalogUnreadable), (create.Status, create.Code));
        using BooruWatcherListModel model = new(port, TimeProvider.System);
        await model.RefreshAsync();
        Assert.Equal((BooruWatcherListStatus.Failed, BooruCodes.CatalogUnreadable), (model.View.Status, model.View.Code));
    }

    private sealed class NoSearch : IDanbooruWatcherSearchPort
    {
        public int MaximumPage => 5;
        public int PageSize => 20;
        public DateTimeOffset? GetProviderCooldownUntilUtc() => null;
        public IDanbooruWatcherSearchSession OpenCheck() => throw new InvalidOperationException("No check may run.");
    }

    private sealed class NoAdmission : IDanbooruWatcherAdmissionPort
    {
        public Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Nothing may be admitted.");
    }

    [Fact]
    public async Task ThePreviewPathRefusesUndeclaredHostsRedirectsAndNonImages()
    {
        using DanbooruStub stub = new();
        using DanbooruTestBinding binding = new(stub);
        using BooruPreviewHttp previews = new(binding.Transport, binding.Source, productVersion: "1.1.0");
        Assert.Null(await previews.LoadAsync(new Uri("https://evil.example/preview.jpg")));
        Assert.Null(await previews.LoadAsync(new Uri("http://cdn.donmai.us/180x180/x.jpg")));
        Assert.Null(await previews.LoadAsync(new Uri("https://cdn.donmai.us:8443/180x180/x.jpg")));
        Assert.Null(await previews.LoadAsync(new Uri("https://user:pass@cdn.donmai.us/180x180/x.jpg")));
        Assert.Empty(stub.Requests);
        Assert.Null(await previews.LoadAsync(new Uri("https://cdn.donmai.us/180x180/missing.jpg")));
        Assert.Single(stub.Requests);
        Assert.False(BooruPreviewHttp.LooksLikeImage("<html>"u8));
        Assert.True(BooruPreviewHttp.LooksLikeImage(DanbooruStub.Png(4, 4, 0xFF000000, 1)));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previews.LoadAsync(new Uri("https://cdn.donmai.us/180x180/x.jpg"), cancelled.Token));
    }

    private static BooruCatalogSnapshot ReadCatalog(DesktopSessionHost host) => host.BooruComposition!.Catalog.Read();

    internal static async Task<DesktopSessionHost> StartAsync(string profile, DanbooruStub stub, Func<BooruProviderBinding?> binding,
        bool paused = false, bool writeConfig = true, DanbooruWatcherSchedulerOptions? options = null,
        BooruProviderBinding[]? bindings = null)
    {
        if (writeConfig) new Config
        {
            DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = paused, SaveUrlsOnExit = true,
            AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile);
        Directory.CreateDirectory(Path.Combine(profile, "media"));
        DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(profile, Array.Empty<IImageBoard>(),
            updateSessionFactory: (path, config) => new UpdateSession(path, config, stub.Client(), disableEnvironment: () => "1"))
        {
            BooruBindingForTests = bindings is null ? binding : null,
            BooruBindingsForTests = bindings,
            BooruSchedulerOptionsForTests = options ?? new DanbooruWatcherSchedulerOptions { Interval = TimeSpan.FromMinutes(30), CheckStartSpacing = TimeSpan.Zero }
        });
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            if (binding() is { } live && live.IsEnabled())
                await WaitAsync(() => host.BooruComposition != null, "the Danbooru composition");
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    internal static Task WaitAsync(Func<bool> condition, string what) => WaitAsync(() => Task.FromResult(condition()), what);

    internal static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        using CancellationTokenSource deadline = new(Deadline);
        while (!await condition())
        {
            if (deadline.IsCancellationRequested) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }
}
