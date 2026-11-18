using System.Net;
using System.Text;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// B5 job scope: all pages of one watcher check (and any other multi-page operation that opens a
/// <see cref="BooruSearchScope"/>) share ONE host job — one <see cref="PluginOperationContext"/>/resource id,
/// one 20-send budget and one 120 s lifetime — instead of a fresh job per page. Synthetic transport and
/// worker double on a manual clock; nothing leaves the process.
/// </summary>
public sealed class BooruCheckScopeTests
{
    [Fact]
    public async Task PagesOfOneScopeShareOneTypedOperationContextAndOneTwentySendBudget()
    {
        using Rig rig = new();
        List<PluginOperationContext> seen = new();
        using (BooruSearchScope scope = rig.Search.OpenScope())
        {
            for (int index = 0; index < BooruHostHttp.MaximumSendsPerJob; ++index)
            {
                BooruSearchResult result = await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", index % 5 + 1, scope);
                Assert.IsType<BooruSearchSnapshot>(result);
                rig.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            seen.AddRange(rig.Worker.Operations);
            Assert.Equal(BooruHostHttp.MaximumSendsPerJob, rig.Sends);

            // The 21st page of the same operation is refused by the shared budget before any transport send.
            BooruSearchFailure over = Assert.IsType<BooruSearchFailure>(
                await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 1, scope));
            Assert.Equal("booru-budget", over.Failure.Code);
            Assert.Equal(BooruHostHttp.MaximumSendsPerJob, rig.Sends);
        }

        Assert.Equal(BooruHostHttp.MaximumSendsPerJob, seen.Count);
        PluginOperationContext first = seen[0];
        Assert.StartsWith(BooruJob.ResourcePrefix, first.ResourceId, StringComparison.Ordinal);
        Assert.Equal(1, first.Generation);
        Assert.All(seen, operation => Assert.Equal(first, operation));
        Assert.Equal(first, rig.Worker.Operations.Last()); // also the refused 21st page

        // A new scope is a new job with a new resource id and a fresh budget.
        using BooruSearchScope next = rig.Search.OpenScope();
        Assert.IsType<BooruSearchSnapshot>(await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 1, next));
        Assert.NotEqual(first.ResourceId, rig.Worker.Operations.Last().ResourceId);
        Assert.Equal(BooruHostHttp.MaximumSendsPerJob + 1, rig.Sends);
    }

    [Fact]
    public async Task OnePageSearchesStillGetTheirOwnJobEachWhileAScopeSharesOne()
    {
        using Rig rig = new();
        await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 2);
        Assert.Equal(2, rig.Worker.Operations.Select(operation => operation.ResourceId).Distinct().Count());
    }

    [Fact]
    public async Task ScopeLifetimeIsOneHundredTwentySecondsAcrossPagesNotPerPage()
    {
        using Rig rig = new();
        using BooruSearchScope scope = rig.Search.OpenScope();
        Assert.IsType<BooruSearchSnapshot>(await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 1, scope));
        rig.Clock.Advance(BooruHostHttp.MaximumJobLifetime);
        BooruSearchFailure expired = Assert.IsType<BooruSearchFailure>(
            await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 2, scope));
        Assert.Equal("booru-budget", expired.Failure.Code);
        Assert.Equal(1, rig.Sends);
    }

    [Fact]
    public async Task DisposedScopeClosesTheJobAndRefusesFurtherPagesAndLateSends()
    {
        using Rig rig = new();
        BooruSearchScope scope = rig.Search.OpenScope();
        await rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 1, scope);
        PluginOperationContext operation = rig.Worker.Operations.Single();
        scope.Dispose();
        scope.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Search.SearchAsync(rig.Worker, "danbooru", "cat", 2, scope));
        HostHttpFailure late = Assert.IsType<HostHttpFailure>(await rig.Services.SendHttpAsync(
            new HostHttpRequest("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"),
                new Dictionary<string, string>()), operation, default));
        Assert.Equal("booru-job", late.Failure.Code);
        Assert.Equal(1, rig.Sends);
    }

    [Fact]
    public async Task WatcherPortSessionCoversEveryPageOfOneCheckWithOneResourceIdAndClosesIt()
    {
        using Rig rig = new();
        BooruWatcherSearchPort port = new(rig.Search, rig.Services, _ => Task.FromResult<IPluginWorkerClient?>(rig.Worker), rig.Clock);
        IDanbooruWatcherSearchPort core = port;

        // The newest page and then cursor pages below it, more than the five numbered pages: one check, one id.
        string?[] cursors = [null, "9000", "8000", "7000", "6000", "5000", "4000"];
        IDanbooruWatcherSearchSession check = core.OpenCheck();
        foreach (string? cursor in cursors)
        {
            DanbooruWatcherSearchSnapshot page = Assert.IsType<DanbooruWatcherSearchSnapshot>(await check.SearchAsync("cat", cursor, default));
            Assert.Equal(cursor, page.BeforePostId);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        string checkId = rig.Worker.Operations.Select(operation => operation.ResourceId).Distinct().Single();
        Assert.Equal(cursors.Length, rig.Worker.Operations.Count);
        Assert.Equal(["1", "b9000", "b8000", "b7000", "b6000", "b5000", "b4000"], rig.Pages);
        Assert.StartsWith(BooruJob.ResourcePrefix, checkId, StringComparison.Ordinal);
        Assert.All(rig.Worker.Operations, operation => Assert.Equal(1, operation.Generation));
        check.Dispose();

        using (IDanbooruWatcherSearchSession again = core.OpenCheck())
            Assert.IsType<DanbooruWatcherSearchSnapshot>(await again.SearchAsync("cat", null, default));
        Assert.NotEqual(checkId, rig.Worker.Operations.Last().ResourceId);

        // The closed first check can no longer send through its old resource id.
        HostHttpFailure late = Assert.IsType<HostHttpFailure>(await rig.Services.SendHttpAsync(
            new HostHttpRequest("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"),
                new Dictionary<string, string>()), rig.Worker.Operations.First(), default));
        Assert.Equal("booru-job", late.Failure.Code);
    }

    [Fact]
    public async Task WatcherPortSessionMapsTheSharedBudgetExhaustionToATransientDeferral()
    {
        using Rig rig = new();
        BooruWatcherSearchPort port = new(rig.Search, rig.Services, _ => Task.FromResult<IPluginWorkerClient?>(rig.Worker), rig.Clock);
        using IDanbooruWatcherSearchSession check = ((IDanbooruWatcherSearchPort)port).OpenCheck();
        for (int index = 0; index < BooruHostHttp.MaximumSendsPerJob; ++index)
        {
            Assert.IsType<DanbooruWatcherSearchSnapshot>(await check.SearchAsync("cat",
                index == 0 ? null : (10_000 - index).ToString(System.Globalization.CultureInfo.InvariantCulture), default));
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        DanbooruWatcherSearchDeferred deferred = Assert.IsType<DanbooruWatcherSearchDeferred>(
            await check.SearchAsync("cat", "42", default));
        Assert.Equal("booru-budget", deferred.ReasonCode);
        Assert.Equal(BooruHostHttp.MaximumSendsPerJob, rig.Sends);
    }

    private static SourceDescription DanbooruSource() => new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []),
        PostOrderKind.DecimalPostId);

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _http;
        private int _sends;

        internal Rig()
        {
            _transport = new HttpClient(new Handler(this), disposeHandler: true);
            _http = new BooruHostHttp(_transport, new RequestPolicyState(Clock), [DanbooruSource()]);
            Services = new BooruHostServices(new NoInner(), _http);
            Search = new BooruSearchService(Services);
            Worker = new RecordingWorker(Services);
        }

        internal BooruWatcherSchedulerHostTests.PumpClock Clock { get; } = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        internal BooruHostServices Services { get; }
        internal BooruSearchService Search { get; }
        internal RecordingWorker Worker { get; }
        internal int Sends => Volatile.Read(ref _sends);
        private readonly List<string> _pages = new();
        /// <summary>The raw <c>page</c> value of every request that reached the transport, in order.</summary>
        internal string[] Pages { get { lock (_pages) return _pages.ToArray(); } }

        public void Dispose() { _http.Dispose(); _transport.Dispose(); }

        private sealed class Handler(Rig owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._sends);
                string page = request.RequestUri!.Query.TrimStart('?').Split('&')
                    .Single(part => part.StartsWith("page=", StringComparison.Ordinal))["page=".Length..];
                lock (owner._pages) owner._pages.Add(page);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                });
            }
        }
    }

    /// <summary>One host GET per page (like the Danbooru plugin); records the operation of every call.</summary>
    private sealed class RecordingWorker(BooruHostServices services) : IPluginWorkerClient
    {
        private readonly List<PluginOperationContext> _operations = new();
        internal IReadOnlyList<PluginOperationContext> Operations { get { lock (_operations) return _operations.ToArray(); } }

        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [DanbooruSource()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);

        public async Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            lock (_operations) _operations.Add(operation);
            HostHttpResult result = await services.SendHttpAsync(new HostHttpRequest(request.SourceId, HostHttpMethod.Get,
                new($"https://danbooru.test/posts.json?tags={Uri.EscapeDataString(request.Query)}&page={(request.BeforePostId is null ? request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture) : "b" + request.BeforePostId)}&limit={request.Limit}"),
                new Dictionary<string, string>()), operation, cancellationToken);
            switch (result)
            {
                case HostHttpDeferred deferred:
                    return new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
                case HostHttpFailure failure:
                    return new BooruSearchFailure(request.SourceId, failure.Failure);
            }
            return new BooruSearchSnapshot(new BooruSearchPage(request.SourceId, request.Query, request.Page, [], true));
        }

        public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }
}
