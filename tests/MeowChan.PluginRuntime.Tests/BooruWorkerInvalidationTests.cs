using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// A worker call that ends because the worker client itself went away (the Danbooru plugin was disabled at runtime,
/// the client was disposed mid-call or before the call) is "worker not available", never the caller's cancellation and
/// never a provider answer. <see cref="StoppingWorker"/> mirrors <c>PluginWorkerClient</c>: a disposed client throws
/// <see cref="ObjectDisposedException"/>, and an in-flight call fails with an <see cref="OperationCanceledException"/>
/// for the client's own lifetime while the caller's token stays uncancelled.
/// </summary>
public sealed class BooruWorkerInvalidationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task AWorkerStoppedDuringASearchIsAStoppedFailureInsteadOfACancellation()
    {
        using Facade facade = new();
        StoppingWorker worker = new();

        Task<BooruSearchResult> pending = facade.Search.SearchAsync(worker, "danbooru", "cat", 1);
        await worker.Entered.WaitAsync(Deadline);
        await worker.DisposeAsync();

        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await pending.WaitAsync(Deadline));
        Assert.Equal(BooruSearchService.WorkerStoppedCode, failure.Failure.Code);
        Assert.Equal("danbooru", failure.SourceId);
    }

    [Fact]
    public async Task AWorkerStoppedDuringAPostReadIsAStoppedFailureInsteadOfACancellation()
    {
        using Facade facade = new();
        StoppingWorker worker = new();
        BooruPostId post = new("danbooru", "7001");

        Task<BooruPostResult> pending = facade.Search.ReadPostAsync(worker, post);
        await worker.Entered.WaitAsync(Deadline);
        await worker.DisposeAsync();

        BooruPostFailure failure = Assert.IsType<BooruPostFailure>(await pending.WaitAsync(Deadline));
        Assert.Equal(BooruSearchService.WorkerStoppedCode, failure.Failure.Code);
        Assert.Equal(post, failure.Post);
    }

    [Fact]
    public async Task AnAlreadyDisposedWorkerIsAStoppedFailureInsteadOfAFault()
    {
        using Facade facade = new();
        StoppingWorker worker = new();
        await worker.DisposeAsync();

        Assert.Equal(BooruSearchService.WorkerStoppedCode, Assert.IsType<BooruSearchFailure>(
            await facade.Search.SearchAsync(worker, "danbooru", "cat", 1)).Failure.Code);
        Assert.Equal(BooruSearchService.WorkerStoppedCode, Assert.IsType<BooruSearchFailure>(
            await facade.Search.SearchBeforeAsync(worker, "danbooru", "cat", "1000")).Failure.Code);
        Assert.Equal(BooruSearchService.WorkerStoppedCode, Assert.IsType<BooruPostFailure>(
            await facade.Search.ReadPostAsync(worker, new("danbooru", "7001"))).Failure.Code);
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillPropagatesAsACancellation()
    {
        using Facade facade = new();
        StoppingWorker searching = new();
        using CancellationTokenSource searchCaller = new();
        Task<BooruSearchResult> search = facade.Search.SearchAsync(searching, "danbooru", "cat", 1, searchCaller.Token);
        await searching.Entered.WaitAsync(Deadline);
        searchCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(Deadline));

        StoppingWorker reading = new();
        using CancellationTokenSource readCaller = new();
        Task<BooruPostResult> read = facade.Search.ReadPostAsync(reading, new("danbooru", "7001"), readCaller.Token);
        await reading.Entered.WaitAsync(Deadline);
        readCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Deadline));
    }

    [Fact]
    public async Task AManualCheckWhoseWorkerStopsMidCallEndsAsSourceUnavailableAndTheWatcherStaysUsable()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        StoppingWorker stopping = new();
        harness.Worker = stopping;
        string id = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;

        Task<DanbooruWatcherCheckSummary> manual = harness.Scheduler.CheckNowAsync(id);
        Task<DanbooruWatcherCheckSummary?> step = harness.Scheduler.RunNextAsync();
        await stopping.Entered.WaitAsync(Deadline);
        Assert.Equal(DanbooruWatcherActivity.Checking, harness.Scheduler.GetStatus(id)!.Activity);
        Assert.Equal(nameof(DanbooruWatcherState.Checking), harness.Catalog.Read().Watchers.Single().State);

        // The user disables the Danbooru plugin while the check is in flight: the session disposes the worker client.
        await stopping.DisposeAsync();

        DanbooruWatcherCheckSummary result = (await step.WaitAsync(Deadline))!;
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, result.Kind);
        Assert.Equal(BooruSearchService.WorkerStoppedCode, result.Code);
        // The manual request is answered (no button stays busy) ...
        Assert.Equal(result, await manual.WaitAsync(Deadline));
        // ... and the watcher left Checking, in memory and on disk, without an error that would need Resume.
        DanbooruWatcherStatus status = harness.Scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Active, status.State);
        Assert.NotEqual(DanbooruWatcherActivity.Checking, status.Activity);
        Assert.Null(status.ErrorCode);
        Assert.Equal(nameof(DanbooruWatcherState.Active), harness.Catalog.Read().Watchers.Single().State);
        Assert.Equal(0, harness.Provider.RequestCount);

        // Re-enabled: after the scheduler's unavailable backoff the same watcher checks normally again.
        harness.Worker = harness.Healthy;
        harness.Clock.Advance(DanbooruWatcherSchedulerOptions.DefaultUnavailableRetry);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await harness.RunNextAsync())!.Kind);
        Assert.Equal(1, harness.Provider.RequestCount);
    }

    [Fact]
    public async Task AnAutomaticCheckOnAWorkerDisposedBeforeTheCallDoesNotTurnTheWatcherIntoAnError()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        StoppingWorker disposed = new();
        await disposed.DisposeAsync();
        harness.Worker = disposed;
        string id = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;

        DanbooruWatcherCheckSummary result = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, result.Kind);
        DanbooruWatcherStatus status = harness.Scheduler.GetStatus(id)!;
        Assert.Equal(DanbooruWatcherState.Active, status.State);
        Assert.Null(status.ErrorCode);
        Assert.Equal(nameof(DanbooruWatcherState.Active), harness.Catalog.Read().Watchers.Single().State);
        Assert.Equal(0, harness.Provider.RequestCount);
    }

    private static SourceDescription DanbooruSource() => new("danbooru", "Danbooru", new("https://danbooru.donmai.us/"),
        SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
        [new("danbooru.donmai.us", HttpHostPurpose.Metadata), new("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)],
        new("1", []), PostOrderKind.DecimalPostId);

    /// <summary>The host façade alone over a transport that must never be reached.</summary>
    private sealed class Facade : IDisposable
    {
        private readonly HttpClient _transport = new(new NoSend());
        private readonly BooruHostHttp _http;

        internal Facade()
        {
            _http = new BooruHostHttp(_transport, new RequestPolicyState(), [DanbooruSource()], productVersion: "1.1.0");
            Search = new BooruSearchService(new BooruHostServices(new NoInner(), _http));
        }

        internal BooruSearchService Search { get; }

        public void Dispose()
        {
            _http.Dispose();
            _transport.Dispose();
        }
    }

    /// <summary>
    /// Scheduler → <see cref="BooruWatcherSearchPort"/> → <see cref="BooruSearchService"/> → worker, as composed by the
    /// desktop session; <see cref="Worker"/> is what the session's worker resolver currently returns.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan-b5-invalidation-" + Guid.NewGuid().ToString("N"));
        private readonly ProfileLease _lease;
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _http;

        internal Harness()
        {
            Directory.CreateDirectory(_root);
            _lease = ProfileLease.Acquire(_root);
            Catalog = new BooruCatalogStore(_lease.ProfileDirectory, _lease);
            Catalog.Read();
            _lease.Admission.CompleteStartup();
            Provider = new BooruWatcherSchedulerHostTests.StubProvider(Clock);
            _transport = new HttpClient(Provider, disposeHandler: false);
            _http = new BooruHostHttp(_transport, new RequestPolicyState(Clock), [DanbooruSource()], productVersion: "1.1.0");
            BooruHostServices services = new(new NoInner(), _http);
            Healthy = new BooruWatcherSchedulerHostTests.PluginLikeWorker(services);
            Worker = Healthy;
            BooruWatcherSearchPort port = new(new BooruSearchService(services), services,
                _ => Task.FromResult<IPluginWorkerClient?>(Volatile.Read(ref _worker)), Clock);
            Scheduler = new DanbooruWatcherScheduler(Catalog, port, new BooruWatcherSchedulerHostTests.RecordingAdmission(), Clock,
                new DanbooruWatcherSchedulerOptions { Interval = Interval, CheckStartSpacing = TimeSpan.Zero });
            Scheduler.Load();
        }

        private IPluginWorkerClient? _worker;

        internal IPluginWorkerClient? Worker
        {
            get => Volatile.Read(ref _worker);
            set => Volatile.Write(ref _worker, value);
        }

        internal BooruWatcherSchedulerHostTests.PumpClock Clock { get; } = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        internal BooruWatcherSchedulerHostTests.StubProvider Provider { get; }
        internal BooruWatcherSchedulerHostTests.PluginLikeWorker Healthy { get; }
        internal BooruCatalogStore Catalog { get; }
        internal DanbooruWatcherScheduler Scheduler { get; }

        /// <summary>Runs one scheduler step while advancing the clock to each pending host wait (pacing, retry).</summary>
        internal async Task<DanbooruWatcherCheckSummary?> RunNextAsync()
        {
            Task<DanbooruWatcherCheckSummary?> step = Scheduler.RunNextAsync();
            DateTime deadline = DateTime.UtcNow + Deadline;
            while (!step.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The scheduler step did not finish.");
                await Task.Delay(5);
                if (!step.IsCompleted && Provider.InFlight == 0) Clock.AdvanceToNextDelay();
            }
            return await step;
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync();
            _http.Dispose();
            _transport.Dispose();
            _lease.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Worker client double with the lifetime behaviour of <c>PluginWorkerClient</c>: a call on a disposed client throws
    /// <see cref="ObjectDisposedException"/>; a call in flight when the client is disposed fails with an
    /// <see cref="OperationCanceledException"/> of the client's own (linked) lifetime, not of the caller's token. A call
    /// stays in flight until one of the two ends it and never sends anything.
    /// </summary>
    private sealed class StoppingWorker : IPluginWorkerClient
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _disposed;

        internal Task Entered => _entered.Task;

        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [DanbooruSource()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, _disposed ? "stopped" : "ready", null, null, null, []);

        public async Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            await CallAsync(cancellationToken);
            throw new InvalidOperationException("A stopping worker never answers.");
        }

        public async Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            await CallAsync(cancellationToken);
            throw new InvalidOperationException("A stopping worker never answers.");
        }

        private async Task CallAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            _lifetime.Cancel();
            return ValueTask.CompletedTask;
        }

        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No provider request is expected.");
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }
}
