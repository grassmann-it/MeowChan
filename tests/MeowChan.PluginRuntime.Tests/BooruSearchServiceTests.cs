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

    private const string BooruJobResourcePrefix = "booru-job:";
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
        public FakeWorker(Func<BooruSearchRequest, PluginOperationContext, CancellationToken, Task<BooruSearchResult>>? search = null) => _search = search;
        public int SearchCalls { get; private set; }
        public int PostCalls { get; private set; }
        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [DanbooruSource()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
        public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { ++SearchCalls; return _search?.Invoke(request, operation, cancellationToken) ?? Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true))); }
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
