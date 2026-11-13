using System.Net;
using System.Text;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruSearchServiceTests
{
    [Fact]
    public async Task InvalidDanbooruQueryIsAConstantRefusalWithoutWorkerOrHttp()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DanbooruSource()]);
        BooruHostServices services = new(new Inner(), http);
        BooruSearchService search = new(services);
        FakeWorker worker = new();

        BooruSearchFailure result = Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, "danbooru", "cat dog third", 1));
        Assert.Equal(BooruSearchService.InvalidQueryCode, result.Failure.Code);
        Assert.Equal(BooruSearchService.InvalidQueryMessage, result.Failure.Message);
        Assert.Equal(0, worker.SearchCalls);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task ValidDanbooruQueryUsesProviderQueryFixedLimitAndHostJobResource()
    {
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DanbooruSource()]);
        BooruHostServices services = new(new Inner(), http);
        BooruSearchService search = new(services);
        FakeWorker worker = new((request, operation, _) =>
        {
            Assert.Equal("cat rating:g", request.Query);
            Assert.Equal(20, request.Limit);
            Assert.Equal(2, request.Page);
            Assert.StartsWith(BooruJobResourcePrefix, operation.ResourceId, StringComparison.Ordinal);
            Assert.Equal(1, operation.Generation);
            return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true)));
        });

        BooruSearchSnapshot result = Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "danbooru", "cat\trating:g", 2));
        Assert.Equal("cat rating:g", result.Page.Query);
        Assert.Equal(1, worker.SearchCalls);
    }

    [Fact]
    public async Task SourcesWithoutPostSearchAndInvalidPostIdsAreRefusedBeforeWorker()
    {
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DanbooruSource(), ChanSource()]);
        BooruHostServices services = new(new Inner(), http);
        BooruSearchService search = new(services);
        FakeWorker worker = new();

        BooruSearchFailure sourceFailure = Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, "chan", "cat", 1));
        Assert.Equal("capability", sourceFailure.Failure.Code);
        BooruPostFailure postFailure = Assert.IsType<BooruPostFailure>(await search.ReadPostAsync(worker, new("danbooru", "01")));
        Assert.Equal(BooruSearchService.InvalidPostIdCode, postFailure.Failure.Code);
        Assert.Equal(0, worker.SearchCalls);
        Assert.Equal(0, worker.PostCalls);
    }

    [Fact]
    public async Task JobIsClosedAfterWorkerReturnsAndLateHttpIsRejected()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }); }));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DanbooruSource()]);
        BooruHostServices services = new(new Inner(), http);
        BooruSearchService search = new(services);
        PluginOperationContext? operation = null;
        FakeWorker worker = new((request, current, _) =>
        {
            operation = current;
            return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true)));
        });

        Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "danbooru", "cat", 1));
        Assert.NotNull(operation);
        HostHttpFailure late = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(
            new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json")), operation!, default));
        Assert.Equal("booru-job", late.Failure.Code);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task CursorSearchSendsTheCursorOnPageOneAndAcceptsTheNextCursor()
    {
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DecimalDanbooruSource()]);
        BooruSearchService search = new(new BooruHostServices(new Inner(), http));
        FakeWorker worker = new((request, _, _) =>
        {
            Assert.Equal("cat", request.Query);
            Assert.Equal(1, request.Page);
            Assert.Equal("1000", request.BeforePostId);
            BooruPost[] posts = Enumerable.Range(0, 20).Select(index => new BooruPost(new("danbooru", (999 - index).ToString()), BooruFieldPresence.None)).ToArray();
            return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, 1, posts, false, "980")));
        });

        BooruSearchSnapshot result = Assert.IsType<BooruSearchSnapshot>(await search.SearchBeforeAsync(worker, "danbooru", "cat", "1000"));
        Assert.Equal("980", result.Page.NextBeforePostId);
        Assert.Equal(1, worker.SearchCalls);
    }

    [Fact]
    public async Task CursorPagesThatDoNotMoveBelowTheCursorAreRefused()
    {
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DecimalDanbooruSource()]);
        BooruSearchService search = new(new BooruHostServices(new Inner(), http));
        FakeWorker worker = new((request, _, _) => Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(
            new(request.SourceId, request.Query, 1, [new BooruPost(new("danbooru", "1000"), BooruFieldPresence.None)], true))));
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await search.SearchBeforeAsync(worker, "danbooru", "cat", "1000"));
        Assert.Equal("cursor_not_advanced", failure.Failure.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("b1000")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("1234567890123456789")]
    public async Task InvalidCursorsAreRefusedBeforeWorkerOrHttp(string cursor)
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DecimalDanbooruSource()]);
        BooruSearchService search = new(new BooruHostServices(new Inner(), http));
        FakeWorker worker = new();
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await search.SearchBeforeAsync(worker, "danbooru", "cat", cursor));
        Assert.Equal(BooruSearchService.InvalidCursorCode, failure.Failure.Code);
        Assert.Equal(BooruSearchService.InvalidCursorMessage, failure.Failure.Message);
        Assert.Equal(0, worker.SearchCalls);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task ASourceWithoutDecimalOrderIsNeverSentACursor()
    {
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using BooruHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), [DanbooruSource()]);
        BooruSearchService search = new(new BooruHostServices(new Inner(), http));
        FakeWorker worker = new();
        Assert.Equal(BooruSearchService.InvalidCursorCode,
            Assert.IsType<BooruSearchFailure>(await search.SearchBeforeAsync(worker, "danbooru", "cat", "1000")).Failure.Code);
        Assert.Equal(0, worker.SearchCalls);
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10); // hang guard, not a deadline driver

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeWorkerResultWaitEndsAtJobDeadlineEvenWhenWorkerIgnoresToken(bool postRead)
    {
        using FacadeRig rig = new();
        TaskCompletionSource<BooruSearchResult> searchResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BooruPostResult> postResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        PluginOperationContext? operation = null;
        FakeWorker worker = new((_, op, token) => { operation = op; received = token; return searchResult.Task; },
            (_, op, token) => { operation = op; received = token; return postResult.Task; });
        Task pending = postRead
            ? rig.Search.ReadPostAsync(worker, new("danbooru", "1"))
            : rig.Search.SearchAsync(worker, "danbooru", "cat", 1);
        Assert.NotNull(operation);
        Assert.True(received.CanBeCanceled);
        Assert.False(pending.IsCompleted);
        rig.Clock.Advance(BooruJob.MaximumLifetime);
        await pending.WaitAsync(Bound);
        string code = postRead
            ? Assert.IsType<BooruPostFailure>(await (Task<BooruPostResult>)pending).Failure.Code
            : Assert.IsType<BooruSearchFailure>(await (Task<BooruSearchResult>)pending).Failure.Code;
        Assert.Equal("booru-budget", code);
        Assert.True(received.IsCancellationRequested);
        // The ignored task may finish later, but cannot replace the host's terminal result.
        searchResult.SetResult(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
        postResult.SetResult(new BooruPostNotFound(new("danbooru", "1")));
        Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await rig.Services.SendHttpAsync(
            new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json")), operation!, default)).Failure.Code);
        Assert.Equal(0, rig.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredScopeDoesNotStartNumberedOrCursorWorker(bool cursor)
    {
        using FacadeRig rig = new();
        using BooruSearchScope scope = rig.Search.OpenScope();
        rig.Clock.Advance(BooruJob.MaximumLifetime);
        FakeWorker worker = new();
        BooruSearchResult result = cursor
            ? await rig.Search.SearchBeforeAsync(worker, "danbooru", "cat", "1000", scope)
            : await rig.Search.SearchAsync(worker, "danbooru", "cat", 1, scope);
        Assert.Equal("booru-budget", Assert.IsType<BooruSearchFailure>(result).Failure.Code);
        Assert.Equal(0, worker.SearchCalls);
        Assert.Equal(0, rig.Sends);
    }

    [Fact]
    public async Task ClosingScopeEndsTokenIgnoringWorkerResultWait()
    {
        using FacadeRig rig = new();
        using BooruSearchScope scope = rig.Search.OpenScope();
        TaskCompletionSource<BooruSearchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeWorker worker = new((_, _, _) => completion.Task);
        Task<BooruSearchResult> pending = rig.Search.SearchAsync(worker, "danbooru", "cat", 1, scope);
        scope.Dispose();
        Assert.Equal("booru-job", Assert.IsType<BooruSearchFailure>(await pending.WaitAsync(Bound)).Failure.Code);
        completion.SetResult(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationEndsTokenIgnoringWorkerWait(bool postRead)
    {
        using FacadeRig rig = new();
        using CancellationTokenSource caller = new();
        TaskCompletionSource<BooruSearchResult> searchResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BooruPostResult> postResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeWorker worker = new((_, _, _) => searchResult.Task, (_, _, _) => postResult.Task);
        Task pending = postRead
            ? rig.Search.ReadPostAsync(worker, new("danbooru", "1"), caller.Token)
            : rig.Search.SearchAsync(worker, "danbooru", "cat", 1, caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        searchResult.SetResult(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
        postResult.SetResult(new BooruPostNotFound(new("danbooru", "1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationWinsOverDeadlineAndCompletedWorkerResult(bool postRead)
    {
        using FacadeRig rig = new();
        using CancellationTokenSource caller = new();
        void EndBoth() { rig.Clock.Advance(BooruJob.MaximumLifetime); caller.Cancel(); }
        FakeWorker worker = new((_, _, _) =>
        {
            EndBoth();
            return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
        }, (_, _, _) =>
        {
            EndBoth();
            return Task.FromResult<BooruPostResult>(new BooruPostNotFound(new("danbooru", "1")));
        });
        Task pending = postRead
            ? rig.Search.ReadPostAsync(worker, new("danbooru", "1"), caller.Token)
            : rig.Search.SearchAsync(worker, "danbooru", "cat", 1, caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
    }

    [Fact]
    public async Task CallerCancellationWinsOverScopeCloseAndCompletedWorkerResult()
    {
        using FacadeRig rig = new();
        using BooruSearchScope scope = rig.Search.OpenScope();
        using CancellationTokenSource caller = new();
        FakeWorker worker = new((_, _, _) =>
        {
            scope.Dispose();
            caller.Cancel();
            return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Search.SearchAsync(worker, "danbooru", "cat", 1, scope, caller.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstRateLimitedFacadeWaitStaysDeferredEvenIfWorkerIgnoresCancellation(bool postRead)
    {
        using FacadeRig rig = new(rateLimited: true);
        TaskCompletionSource<BooruSearchResult> searchResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<BooruPostResult> postResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeWorker worker = new(async (_, operation, _) =>
        {
            Assert.IsType<HostHttpDeferred>(await rig.Services.SendHttpAsync(
                new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json")), operation, default));
            return await searchResult.Task; // deliberately ignores the cancelled job token
        }, async (_, operation, _) =>
        {
            Assert.IsType<HostHttpDeferred>(await rig.Services.SendHttpAsync(
                new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts/1.json")), operation, default));
            return await postResult.Task;
        });
        if (postRead)
        {
            Assert.Equal(429, Assert.IsType<BooruPostDeferred>(await rig.Search.ReadPostAsync(worker, new("danbooru", "1")).WaitAsync(Bound)).HttpStatusCode);
            Assert.IsType<BooruPostDeferred>(await rig.Search.ReadPostAsync(worker, new("danbooru", "1")));
            Assert.Equal(1, worker.PostCalls);
        }
        else
        {
            using BooruSearchScope scope = rig.Search.OpenScope();
            Assert.Equal(429, Assert.IsType<BooruSearchDeferred>(await rig.Search.SearchAsync(worker, "danbooru", "cat", 1, scope).WaitAsync(Bound)).HttpStatusCode);
            Assert.IsType<BooruSearchDeferred>(await rig.Search.SearchAsync(worker, "danbooru", "cat", 1));
            rig.Clock.Advance(BooruHostHttp.MinimumCooldown);
            Assert.Equal("booru-job", Assert.IsType<BooruSearchFailure>(await rig.Search.SearchAsync(worker, "danbooru", "cat", 1, scope)).Failure.Code);
            Assert.Equal(1, worker.SearchCalls);
        }
        Assert.Equal(1, rig.Sends);
        searchResult.SetResult(new BooruSearchSnapshot(new("danbooru", "cat", 1, [], true)));
        postResult.SetResult(new BooruPostNotFound(new("danbooru", "1")));
    }

    private sealed class FacadeRig : IDisposable
    {
        internal readonly ManualClock Clock = new();
        internal readonly BooruHostServices Services;
        internal readonly BooruSearchService Search;
        internal int Sends;
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _http;
        internal FacadeRig(bool rateLimited = false)
        {
            _transport = new(new Handler((_, _) =>
            {
                ++Sends;
                return Task.FromResult(new HttpResponseMessage(rateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK));
            }));
            _http = new(_transport, new MeowChan.Http.RequestPolicyState(Clock), [DecimalDanbooruSource()]);
            Services = new(new Inner(), _http);
            Search = new(Services);
        }
        public void Dispose() { _http.Dispose(); _transport.Dispose(); }
    }

    private const string BooruJobResourcePrefix = "booru-job:";
    private static SourceDescription DecimalDanbooruSource() => DanbooruSource() with { PostOrder = PostOrderKind.DecimalPostId };
    private static SourceDescription DanbooruSource() => new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));
    private static SourceDescription ChanSource() => new("chan", "Chan", new("https://chan.test/"),
        SourceCapabilities.ThreadRead, [], [new("chan.test", HttpHostPurpose.Metadata)], new("1", []));

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class Inner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("inner")));
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class FakeWorker : IPluginWorkerClient
    {
        private readonly Func<BooruSearchRequest, PluginOperationContext, CancellationToken, Task<BooruSearchResult>>? _search;
        private readonly Func<BooruPostRequest, PluginOperationContext, CancellationToken, Task<BooruPostResult>>? _post;
        public FakeWorker(Func<BooruSearchRequest, PluginOperationContext, CancellationToken, Task<BooruSearchResult>>? search = null,
            Func<BooruPostRequest, PluginOperationContext, CancellationToken, Task<BooruPostResult>>? post = null)
        { _search = search; _post = post; }
        public int SearchCalls { get; private set; }
        public int PostCalls { get; private set; }
        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [DanbooruSource()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
        public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { ++SearchCalls; return _search?.Invoke(request, operation, cancellationToken) ?? Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true))); }
        public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { ++PostCalls; return _post?.Invoke(request, operation, cancellationToken) ?? Task.FromResult<BooruPostResult>(new BooruPostNotFound(request.Post)); }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
