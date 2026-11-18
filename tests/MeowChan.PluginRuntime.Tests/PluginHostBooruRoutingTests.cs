using System.Net;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class PluginHostBooruRoutingTests
{
    [Fact]
    public async Task PluginHostHttpRoutesPostSearchThroughBoundedBooruTransport()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            ++sends;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://danbooru.test/posts.json?tags=cat&page=1&limit=20", request.RequestUri!.AbsoluteUri);
            Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)",
                request.Headers.UserAgent.ToString());
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            Assert.False(request.Headers.Authorization is not null);
            Assert.False(request.Headers.Contains("Cookie"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }));

        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source()]);
        HostHttpResponse result = Assert.IsType<HostHttpResponse>(await host.SendAsync(new(
            "danbooru", HostHttpMethod.Get,
            new("https://danbooru.test/posts.json?tags=cat&page=1&limit=20"))));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{}", Encoding.UTF8.GetString(result.Body));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task ChanSourceKeepsItsPolicyAndRedirectsBesideTheZeroRedirectBooruRoute()
    {
        List<string> sent = [];
        using HttpClient transport = new(new Handler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (sent) sent.Add(url);
            if (url == "https://chan.test/a")
            {
                Assert.Equal("MeowChan", request.Headers.UserAgent.ToString());
                HttpResponseMessage moved = new(HttpStatusCode.MovedPermanently) { RequestMessage = request };
                moved.Headers.Location = new("/b", UriKind.Relative);
                return moved;
            }
            if (url == "https://chan.test/b")
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("chan") };
            Assert.Equal("danbooru.test", request.RequestUri.Host);
            Assert.StartsWith("MeowChan/", request.Headers.UserAgent.ToString());
            HttpResponseMessage redirect = new(HttpStatusCode.Found) { RequestMessage = request };
            redirect.Headers.Location = new("https://danbooru.test/posts/2.json");
            return redirect;
        }));

        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source(), ChanSource()]);

        HostHttpResponse chan = Assert.IsType<HostHttpResponse>(await host.SendAsync(new(
            "chan", HostHttpMethod.Get, new("https://chan.test/a"))));
        Assert.Equal("https://chan.test/b", chan.FinalUri.AbsoluteUri);
        Assert.Equal("chan", Encoding.UTF8.GetString(chan.Body));

        HostHttpFailure booru = Assert.IsType<HostHttpFailure>(await host.SendAsync(new(
            "danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts/1.json"))));
        Assert.Equal("http-redirect", booru.Failure.Code);
        Assert.Equal(302, booru.Failure.HttpStatusCode);

        // The chan source followed its one redirect; the booru source never followed Location.
        Assert.Equal(["https://chan.test/a", "https://chan.test/b", "https://danbooru.test/posts/1.json"], sent);

        // A chan HEAD still works: the GET-only booru restriction does not leak into chan sources.
        Assert.IsType<HostHttpResponse>(await host.SendAsync(new("chan", HostHttpMethod.Head, new("https://chan.test/b"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredAmbientOperationCannotObtainAFreshStandaloneJob(bool sendBeforeRetirement)
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            ++sends;
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("[]") };
        }));
        using PluginHostHttp host = new(transport, new RequestPolicyState(clock), [Source(), ChanSource()]);
        using PluginHostHttp.MetadataHistory history = new();
        if (sendBeforeRetirement) Assert.IsType<HostHttpResponse>(await host.SendAsync(Get()));

        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Capture the history before retirement, just like a late host-service continuation.
        Task<HostHttpResult> late = Task.Run(async () =>
        {
            await resume.Task;
            return await host.SendAsync(Get());
        });
        history.Dispose();
        resume.SetResult();
        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await late.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("booru-job", failure.Failure.Code);
        Assert.Equal(sendBeforeRetirement ? 1 : 0, sends);

        // A genuinely context-free caller remains supported; retirement is not a provider fence.
        Assert.IsType<HostHttpResponse>(await host.SendAsync(Get()));
        // Chan does not acquire the booru job restriction.
        Assert.IsType<HostHttpResponse>(await host.SendAsync(new("chan", HostHttpMethod.Head, new("https://chan.test/a"))));
        Assert.Equal((sendBeforeRetirement ? 1 : 0) + 2, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingResumedOperationCannotBecomeAContextFreeBooruRequest(bool previouslyBound)
    {
        int sends = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            ++sends;
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("[]") };
        }));
        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source(), ChanSource()]);
        PluginOperationContext operation = new("booru/missing-binding", 1);
        if (previouslyBound)
            PluginHostHttp.MetadataHistory.BindOperation(operation, default, TimeProvider.System)!.Dispose();
        using PluginHostHttp.MetadataHistory ambient = new();
        // Some IHostHttp bridges use the context-free overload inside WorkerConnection's
        // Resume. Losing the binding must still fail closed rather than minting a new job.
        using (PluginHostHttp.MetadataHistory.Resume(operation))
        {
            Assert.Null(PluginHostHttp.MetadataHistory.Current);
            Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await host.SendAsync(Get())).Failure.Code);
            Assert.Equal(0, sends);
            Assert.IsType<HostHttpResponse>(await host.SendAsync(new("chan", HostHttpMethod.Head, new("https://chan.test/a"))));
        }
        Assert.Same(ambient, PluginHostHttp.MetadataHistory.Current);
        Assert.Null(ambient.Failure);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitOperationOwnsItsFailureObservation(bool unrelatedAmbient, bool invalidRequest)
    {
        ManualClock clock = new();
        using HttpClient transport = new(new Handler(request => new(HttpStatusCode.Found) { RequestMessage = request }));
        using PluginHostHttp host = new(transport, new RequestPolicyState(clock), [Source()]);
        PluginOperationContext operation = new("booru/explicit-history", 1);
        using PluginHostHttp.MetadataHistory owner = PluginHostHttp.MetadataHistory.BindOperation(operation, default, clock)!;
        using PluginHostHttp.MetadataHistory? ambient = unrelatedAmbient ? new() : null;

        HostHttpRequest request = invalidRequest ? Get() with { Method = HostHttpMethod.Head } : Get();
        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await host.SendAsync(request, operation));
        Assert.Equal(invalidRequest ? "http-request" : "http-redirect", failure.Failure.Code);
        Assert.NotNull(owner.Failure);
        Assert.NotNull(owner.MatchFinalFailure(failure.Failure));
        Assert.Null(ambient?.Failure);
        Assert.Same(ambient, PluginHostHttp.MetadataHistory.Current);
    }

    [Fact]
    public async Task UnboundExplicitOperationCannotPoisonAnUnrelatedAmbientHistory()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler(request =>
        {
            ++sends;
            return new(HttpStatusCode.OK) { RequestMessage = request };
        }));
        using PluginHostHttp host = new(transport, new RequestPolicyState(), [Source()]);
        using PluginHostHttp.MetadataHistory ambient = new();
        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await host.SendAsync(Get(), new PluginOperationContext("booru/unbound", 1)));
        Assert.Equal("booru-job", failure.Failure.Code);
        Assert.Null(ambient.Failure);
        Assert.Null(ambient.MatchFinalFailure(failure.Failure));
        Assert.Same(ambient, PluginHostHttp.MetadataHistory.Current);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStaysWithItsExplicitOperationWhileASiblingWaits(bool callerCancellation)
    {
        ManualClock clock = new();
        int sends = 0;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using HttpClient transport = new(new AsyncHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("[]") };
        }));
        using PluginHostHttp host = new(transport, new RequestPolicyState(clock), [Source()]);
        using CancellationTokenSource stop = new();
        PluginOperationContext first = new("booru/cancelled", 1), sibling = new("booru/sibling", 1);
        using PluginHostHttp.MetadataHistory firstOwner = PluginHostHttp.MetadataHistory.BindOperation(
            first, callerCancellation ? default : stop.Token, clock)!;
        using PluginHostHttp.MetadataHistory siblingOwner = PluginHostHttp.MetadataHistory.BindOperation(sibling, default, clock)!;
        using PluginHostHttp.MetadataHistory ambient = new();
        Task<HostHttpResult> pending = host.SendAsync(Get(), first, callerCancellation ? stop.Token : default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task<HostHttpResult> queued = host.SendAsync(Get(), sibling);
        Assert.False(queued.IsCompleted);
        stop.Cancel();
        if (callerCancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
        else
            Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(TimeSpan.FromSeconds(30))).Failure.Code);

        await clock.WaitForTimerAsync(BooruHostHttp.MinimumStartSpacing);
        clock.Advance(BooruHostHttp.MinimumStartSpacing);
        Assert.IsType<HostHttpResponse>(await queued.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(PluginHostHttp.MetadataHistory.BooruJobFor(sibling, clock)!.IsOpen);
        Assert.Null(siblingOwner.Failure);
        Assert.Null(ambient.Failure);
        Assert.Same(ambient, PluginHostHttp.MetadataHistory.Current);
        Assert.Equal(2, sends);
    }

    private static HostHttpRequest Get() => new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"));

    private static SourceDescription ChanSource() => new(
        "chan", "Chan", new("https://chan.test/"),
        SourceCapabilities.ThreadRead, [],
        [new("chan.test", HttpHostPurpose.Metadata)], new("1", []));

    private static SourceDescription Source() => new(
        "danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [],
        [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
