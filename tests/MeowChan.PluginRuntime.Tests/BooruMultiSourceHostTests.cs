using System.Net;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// P2 source contract: several post-search sources side by side on one booru host. Endpoints, query syntax, paging
/// and pacing come from each source's declared profile; Danbooru keeps its host-owned profile unchanged; a source
/// without a valid profile still fails closed. Fake transports and a manual clock only; no network.
/// </summary>
public sealed class BooruMultiSourceHostTests
{
    // ---------- Danbooru regression: exactly the endpoints accepted before profiles existed ----------

    [Theory]
    [InlineData("https://danbooru.test/posts.json", true)]
    [InlineData("https://danbooru.test/posts.json?tags=cat", true)]
    [InlineData("https://danbooru.test/posts.json?tags=cat%20-dog%20rating%3Ag%2Cs&page=5&limit=20", true)]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b123456789012345678", true)]
    [InlineData("https://danbooru.test/posts/123456789012345678.json", true)]
    [InlineData("https://danbooru.test/posts.json?tags=cat%20dog%20bird", false)]
    [InlineData("https://danbooru.test/posts.json?tags=order%3Ascore", false)]
    [InlineData("https://danbooru.test/posts.json?tags=", false)]
    [InlineData("https://danbooru.test/posts.json?page=6", false)]
    [InlineData("https://danbooru.test/posts.json?page=0", false)]
    [InlineData("https://danbooru.test/posts.json?page=b0123", false)]
    [InlineData("https://danbooru.test/posts.json?page=b1234567890123456789", false)]
    [InlineData("https://danbooru.test/posts.json?page=%62123", true)] // Uri unescapes unreserved %62 to b, as before
    [InlineData("https://danbooru.test/posts.json?limit=21", false)]
    [InlineData("https://danbooru.test/posts.json?tags=cat&tags=dog", false)]
    [InlineData("https://danbooru.test/posts.json?login=x", false)]
    [InlineData("https://danbooru.test/posts.json?api_key=x", false)]
    [InlineData("https://danbooru.test/posts/0123.json", false)]
    [InlineData("https://danbooru.test/posts/1234567890123456789.json", false)]
    [InlineData("https://danbooru.test/posts/12.json?x=1", false)]
    [InlineData("https://danbooru.test/users.json", false)]
    [InlineData("https://danbooru.test/posts.json#x", false)]
    [InlineData("https://danbooru.test:8443/posts.json", false)]
    [InlineData("http://danbooru.test/posts.json", false)]
    public void TheHostOwnedDanbooruProfileKeepsTheFormerAllowlist(string url, bool allowed)
    {
        SourceDescription source = Danbooru();
        Assert.Equal(allowed, BooruEndpointPolicy.IsAllowed(new Uri(url), source, BooruEndpointPolicy.Resolve(source)));
    }

    [Fact]
    public void ADeclaredDanbooruProfileNeverWidensTheHostOwnedOne()
    {
        BooruSourceProfile permissive = EngineProfile() with
        {
            Endpoints = [new(BooruEndpointKind.Search, "/users.json", [new("tags", BooruParameterKind.TagQuery)]),
                new(BooruEndpointKind.Post, "/posts/{post}.json", [])]
        };
        SourceDescription source = Danbooru() with { BooruProfile = permissive };
        Assert.Same(BooruEndpointPolicy.Danbooru, BooruEndpointPolicy.Resolve(source));
        Assert.False(BooruEndpointPolicy.IsAllowed(new Uri("https://danbooru.test/users.json?tags=cat"), source, BooruEndpointPolicy.Resolve(source)));
    }

    [Fact]
    public void TheHostOwnedDanbooruProfileIsAValidSdkProfile()
        => Assert.Empty(SdkValidation.ValidateBooruProfile(Danbooru() with { PostOrder = PostOrderKind.DecimalPostId, BooruProfile = BooruEndpointPolicy.Danbooru }));

    // ---------- Declared profiles ----------

    [Theory]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat&pid=0&limit=20", true)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20-dog%20rating%3Aexplicit&pid=4", true)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20id%3A%3C123", true)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=42", true)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat&api_key=k&user_id=1", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&tags=cat", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=user&q=index&json=1&tags=cat", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat&pid=5", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat&limit=21", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20order%3Ascore", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20rating%3As", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=a%20b%20c", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20id%3A%3C1%20id%3A%3C2", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat%20id%3A%3C0123", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=042", false)]
    [InlineData("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=42&tags=cat", false)]
    [InlineData("https://engine.test/posts.json?tags=cat", false)]
    public void ADeclaredProfileAllowsOnlyItsEndpointsAndParameters(string url, bool allowed)
    {
        SourceDescription source = Engine();
        Assert.Equal(allowed, BooruEndpointPolicy.IsAllowed(new Uri(url), source, BooruEndpointPolicy.Resolve(source)));
    }

    // The request shapes of the unmerged plugin-e621 branch under a declared, Danbooru-like profile.
    [Theory]
    [InlineData("https://e621.test/posts.json?tags=wolf%20rating%3As&page=1&limit=20", true)]
    [InlineData("https://e621.test/posts.json?tags=wolf&page=b123&limit=20", true)]
    [InlineData("https://e621.test/posts/42.json", true)]
    [InlineData("https://e621.test/posts.json?tags=wolf%20rating%3Ag&page=1&limit=20", false)]
    [InlineData("https://e621.test/posts.json?tags=wolf&page=6", false)]
    [InlineData("https://e621.test/posts.json?tags=wolf&login=x&api_key=y", false)]
    [InlineData("https://e621.test/favorites.json?tags=wolf", false)]
    public void ADanbooruLikeDeclaredProfileServesTheE621Shapes(string url, bool allowed)
    {
        BooruSourceProfile profile = new(
            [
                new(BooruEndpointKind.Search, "/posts.json",
                    [new("tags", BooruParameterKind.TagQuery, true), new("page", BooruParameterKind.PageOrBeforeCursor), new("limit", BooruParameterKind.Limit)]),
                new(BooruEndpointKind.Post, "/posts/{post}.json", [])
            ],
            new BooruQueryRules(2, ["s", "q", "e", "safe", "questionable", "explicit"]),
            [BooruTagCategory.General, BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character, BooruTagCategory.Meta],
            [BooruRating.General, BooruRating.Questionable, BooruRating.Explicit], 750, 100);
        SourceDescription source = new("e621", "e621", new("https://e621.test/"), SourceCapabilities.PostSearch, [],
            [new("e621.test", HttpHostPurpose.Metadata)], new("1", []), PostOrderKind.DecimalPostId) { BooruProfile = profile };
        Assert.Empty(SdkValidation.ValidateBooruProfile(source));
        Assert.Equal(allowed, BooruEndpointPolicy.IsAllowed(new Uri(url), source, BooruEndpointPolicy.Resolve(source)));
    }

    [Fact]
    public void AnInvalidOrMissingProfileFailsClosed()
    {
        Assert.Null(BooruEndpointPolicy.Resolve(Engine() with { BooruProfile = null }));
        Assert.Null(BooruEndpointPolicy.Resolve(Engine() with { BooruProfile = EngineProfile() with { Endpoints = [] } }));
        Assert.Null(BooruEndpointPolicy.Resolve(Engine() with { Capabilities = SourceCapabilities.ThreadRead }));
        Assert.False(BooruEndpointPolicy.IsAllowed(new Uri("https://engine.test/index.php?page=dapi&s=post&q=index&json=1&tags=cat"),
            Engine(), null));
    }

    [Fact]
    public async Task TwoSourcesSendSideBySideWithTheirOwnEndpointsAndPacing()
    {
        using Rig rig = new();
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await rig.Send(job, "engine", "https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=1"));
            Assert.IsType<HostHttpResponse>(await rig.Send(job, "danbooru", "https://danbooru.test/posts/1.json"));
            Assert.Equal(2, rig.Sends);

            // The engine declares 2 s between request starts; Danbooru keeps the 1 s host floor. Each source paces alone.
            Task<HostHttpResult> engine = rig.Send(job, "engine", "https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=2");
            await rig.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(2));
            rig.Clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.IsType<HostHttpResponse>(await rig.Send(job, "danbooru", "https://danbooru.test/posts/2.json").WaitAsync(Bound));
            Assert.False(engine.IsCompleted);
            Assert.Equal(3, rig.Sends);
            rig.Clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.IsType<HostHttpResponse>(await engine.WaitAsync(Bound));
            Assert.Equal(4, rig.Sends);

            // A refused endpoint never reaches the transport, and the other source's allowlist never applies across sources.
            Assert.IsType<HostHttpFailure>(await rig.Send(job, "engine", "https://engine.test/posts/3.json"));
            Assert.IsType<HostHttpFailure>(await rig.Send(job, "danbooru", "https://danbooru.test/index.php?page=dapi&s=post&q=index&json=1&id=3"));
            Assert.Equal(4, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task ACooldownOfOneSourceDoesNotStopTheOther()
    {
        using Rig rig = new(request => request.RequestUri!.Host == "engine.test" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
        BooruJob engineJob = rig.Services.OpenJob();
        BooruJob danbooruJob = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpDeferred>(await rig.Send(engineJob, "engine", "https://engine.test/index.php?page=dapi&s=post&q=index&json=1&id=1"));
            Assert.True(rig.Services.TryGetCooldown("engine", out _));
            Assert.False(rig.Services.TryGetCooldown("danbooru", out _));
            Assert.IsType<HostHttpResponse>(await rig.Send(danbooruJob, "danbooru", "https://danbooru.test/posts/1.json"));
        }
        finally
        {
            rig.Services.CloseJob(engineJob);
            rig.Services.CloseJob(danbooruJob);
        }
    }

    // ---------- Search service per source ----------

    [Fact]
    public async Task EachSourceIsSearchedWithItsOwnSyntaxPagingAndLimit()
    {
        using Rig rig = new();
        BooruSearchService search = new(rig.Services);
        List<BooruSearchRequest> seen = [];
        await using FakeWorker worker = new(request =>
        {
            seen.Add(request);
            return new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true));
        });

        Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "engine", "cat\trating:explicit", 4));
        Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "danbooru", "cat\trating:g", 5));
        Assert.Equal(("engine", "cat rating:explicit", 4, 20), (seen[0].SourceId, seen[0].Query, seen[0].Page, seen[0].Limit));
        Assert.Equal(("danbooru", "cat rating:g", 5, 20), (seen[1].SourceId, seen[1].Query, seen[1].Page, seen[1].Limit));

        // Each source keeps its own rating vocabulary and refuses meta terms and the cursor term from the user.
        foreach ((string source, string query) in new[] { ("engine", "cat rating:g"), ("danbooru", "cat rating:explicit_only"),
                     ("engine", "cat order:score"), ("engine", "cat id:<5"), ("engine", "a b c"), ("engine", "   ") })
            Assert.Equal(BooruSearchService.InvalidQueryCode,
                Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, source, query, 1)).Failure.Code);
        Assert.Equal(2, worker.SearchCalls);
    }

    [Fact]
    public async Task AProfileLowersTheHostPageAndLimitCeilings()
    {
        BooruSourceProfile small = EngineProfile() with { MaximumPage = 2, MaximumLimit = 10 };
        using Rig rig = new(sources: [Danbooru(), Engine() with { BooruProfile = small }]);
        BooruSearchService search = new(rig.Services);
        await using FakeWorker worker = new(request =>
        {
            Assert.Equal(10, request.Limit);
            return new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true));
        });
        Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "engine", "cat", 2));
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, "engine", "cat", 3));
        Assert.Equal("invalid_page", failure.Failure.Code);
        Assert.Equal("The booru page must be between 1 and 2.", failure.Failure.Message);
        Assert.Equal(10, search.PageSize("engine"));
        Assert.Equal(BooruSearchService.SearchLimit, search.PageSize("danbooru"));
    }

    [Fact]
    public async Task ASourceWithoutProfileIsRefusedBeforeTheWorker()
    {
        using Rig rig = new(sources: [Danbooru(), Engine() with { BooruProfile = null }]);
        BooruSearchService search = new(rig.Services);
        await using FakeWorker worker = new();
        Assert.Equal("capability", Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, "engine", "cat", 1)).Failure.Code);
        Assert.Equal("capability", Assert.IsType<BooruPostFailure>(await search.ReadPostAsync(worker, new("engine", "1"))).Failure.Code);
        Assert.Equal(0, worker.SearchCalls);
        Assert.Equal(0, worker.PostCalls);
    }

    [Fact]
    public async Task DecimalSourcesTakeOnlyCanonicalPostIdsAndCursors()
    {
        using Rig rig = new();
        BooruSearchService search = new(rig.Services);
        await using FakeWorker worker = new();
        Assert.Equal(BooruSearchService.InvalidPostIdCode,
            Assert.IsType<BooruPostFailure>(await search.ReadPostAsync(worker, new("engine", "0042"))).Failure.Code);
        Assert.Equal(BooruSearchService.InvalidCursorCode,
            Assert.IsType<BooruSearchFailure>(await search.SearchBeforeAsync(worker, "engine", "cat", "1234567890123456789")).Failure.Code);
        Assert.IsType<BooruPostNotFound>(await search.ReadPostAsync(worker, new("engine", "42")));
        Assert.Equal(1, worker.PostCalls);
    }

    // ---------- helpers ----------

    internal static BooruSourceProfile EngineProfile() => new(
        [
            new(BooruEndpointKind.Search, "/index.php",
            [
                new("page", BooruParameterKind.Fixed, true, "dapi"), new("s", BooruParameterKind.Fixed, true, "post"),
                new("q", BooruParameterKind.Fixed, true, "index"), new("json", BooruParameterKind.Fixed, true, "1"),
                new("tags", BooruParameterKind.TagQuery, true), new("pid", BooruParameterKind.PageIndex),
                new("limit", BooruParameterKind.Limit)
            ]),
            new(BooruEndpointKind.Post, "/index.php",
            [
                new("page", BooruParameterKind.Fixed, true, "dapi"), new("s", BooruParameterKind.Fixed, true, "post"),
                new("q", BooruParameterKind.Fixed, true, "index"), new("json", BooruParameterKind.Fixed, true, "1"),
                new("id", BooruParameterKind.PostId, true)
            ])
        ],
        new BooruQueryRules(2, ["general", "sensitive", "questionable", "explicit"], BeforeCursorPrefix: "id:<"),
        [], [BooruRating.General, BooruRating.Sensitive, BooruRating.Questionable, BooruRating.Explicit], 5, 100,
        MinimumRequestSpacingMilliseconds: 2000);

    private static SourceDescription Danbooru() => new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));

    private static SourceDescription Engine() => new("engine", "Engine", new("https://engine.test/"),
        SourceCapabilities.PostSearch, [], [new("engine.test", HttpHostPurpose.Metadata)], new("1", []), PostOrderKind.DecimalPostId)
    { BooruProfile = EngineProfile() };

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class Rig : IDisposable
    {
        internal readonly ManualClock Clock = new();
        internal readonly BooruHostServices Services;
        internal int Sends;
        private readonly Handler _handler;
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _http;

        internal Rig(Func<HttpRequestMessage, HttpStatusCode>? status = null, SourceDescription[]? sources = null)
        {
            _handler = new Handler(request =>
            {
                Interlocked.Increment(ref Sends);
                return status?.Invoke(request) ?? HttpStatusCode.OK;
            });
            _transport = new HttpClient(_handler, disposeHandler: false);
            _http = new BooruHostHttp(_transport, new RequestPolicyState(Clock), sources ?? [Danbooru(), Engine()]);
            Services = new BooruHostServices(new Inner(), _http);
        }

        internal Task<HostHttpResult> Send(BooruJob job, string sourceId, string url) =>
            Services.SendHttpAsync(new HostHttpRequest(sourceId, HostHttpMethod.Get, new Uri(url)), job.Operation, default);

        public void Dispose()
        {
            _http.Dispose();
            _transport.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(status(request)) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) };
            return Task.FromResult(response);
        }
    }

    private sealed class Inner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult<HostHttpResult>(new HostHttpFailure(new FailureDetail("inner", "Not a booru request.")));
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class FakeWorker(Func<BooruSearchRequest, BooruSearchResult>? search = null) : IPluginWorkerClient
    {
        public int SearchCalls { get; private set; }
        public int PostCalls { get; private set; }
        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [Danbooru(), Engine()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
        public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        {
            ++SearchCalls;
            return Task.FromResult(search?.Invoke(request) ?? new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true)));
        }
        public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { ++PostCalls; return Task.FromResult<BooruPostResult>(new BooruPostNotFound(request.Post)); }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
