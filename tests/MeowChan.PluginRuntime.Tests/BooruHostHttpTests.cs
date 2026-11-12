using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruHostHttpTests
{
    [Fact]
    public void UserAgentStripsBuildMetadataAndValidatesOptionalUserId()
    {
        Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)",
            BooruUserAgent.Build("1.1.0+build.42"));
        Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan) (user #12345)",
            BooruUserAgent.Build("1.1.0", "12345"));
        Assert.Throws<ArgumentException>(() => BooruUserAgent.Build("1.1.0", "0"));
        Assert.Throws<ArgumentException>(() => BooruUserAgent.Build("1.1.0", "01"));
        Assert.Throws<ArgumentException>(() => BooruUserAgent.Build("1.1.0", "1234567890123"));
    }

    [Fact]
    public async Task BooruRouteUsesExactUserAgentAndRejectsHeadBeforeTransport()
    {
        List<HttpRequestMessage> sent = [];
        using HttpClient transport = new(new Handler((request, _) =>
        {
            sent.Add(request);
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }));
        RequestPolicyState policy = new();
        using BooruHostHttp http = new(transport, policy, [Source()], productVersion: "1.1.0", danbooruUserId: "42");
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpResponse result = Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), job.Operation, default));
            Assert.Equal(200, result.StatusCode);
            HttpRequestMessage request = Assert.Single(sent);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan) (user #42)", request.Headers.UserAgent.ToString());
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));

            HostHttpFailure head = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(
                new("danbooru", HostHttpMethod.Head, new("https://danbooru.test/posts.json")), job.Operation, default));
            Assert.Equal("http-request", head.Failure.Code);
            Assert.Single(sent);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task ExplicitCredentialHeadersAreRejectedBeforeTransport()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((_, _) =>
        {
            ++sends;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            foreach (string header in new[] { "Authorization", "Cookie", "Proxy-Authorization" })
            {
                HostHttpFailure result = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(
                    Get() with { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [header] = "secret"
                    } }, job.Operation, default));
                Assert.Equal("http-request", result.Failure.Code);
            }
            Assert.Equal(0, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task RequestUserAgentOverridesTransportDefault()
    {
        using HttpClient transport = new(new Handler((request, _) =>
        {
            Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)", request.Headers.UserAgent.ToString());
            return Task.FromResult(Response(HttpStatusCode.OK, "ok"));
        }));
        transport.DefaultRequestHeaders.UserAgent.ParseAdd("Transport/0.1");
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()], productVersion: "1.1.0");
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try { Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), job.Operation, default)); }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task RedirectIsTerminalAndDoesNotFollowLocation()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        {
            ++sends;
            HttpResponseMessage response = Response(HttpStatusCode.Redirect, "redirect");
            response.Headers.Location = new("https://other.test/posts.json");
            return Task.FromResult(response);
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpFailure result = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), job.Operation, default));
            Assert.Equal("http-redirect", result.Failure.Code);
            Assert.Equal(302, result.Failure.HttpStatusCode);
            Assert.Equal(1, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task BadGatewayRetriesOnceAfterTwoSeconds()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
            Task.FromResult(Response(++sends == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK, "ok"))));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
            await EventuallyAsync(() => sends == 1);
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1.999));
            Assert.Equal(1, sends);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            HostHttpResponse result = Assert.IsType<HostHttpResponse>(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(2, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task SecondFiveOhThreeIsReturnedWithoutAnotherRetry()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
            Task.FromResult(Response(++sends == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.ServiceUnavailable, "retry"))));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
            await EventuallyAsync(() => sends == 1);
            clock.Advance(TimeSpan.FromSeconds(2));
            HostHttpResponse result = Assert.IsType<HostHttpResponse>(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(503, result.StatusCode);
            Assert.Equal(2, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task TooManyRequestsStartsSourceCooldownAndDoesNotTouchTransportAgain()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        {
            ++sends;
            HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, "busy");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruSearchService façade = new(services);
        FakeWorker worker = new(async (request, operation, token) =>
        {
            HostHttpResult result = await services.SendHttpAsync(Get(), operation, token);
            return result switch
            {
                HostHttpDeferred deferred => new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
                _ => new BooruSearchFailure(request.SourceId, new("test", "unused"))
            };
        });
        BooruSearchDeferred first = Assert.IsType<BooruSearchDeferred>(await façade.SearchAsync(worker, "danbooru", "cat", 1));
        Assert.Equal(clock.GetUtcNow().AddSeconds(120), first.RetryAt);
        BooruSearchDeferred second = Assert.IsType<BooruSearchDeferred>(await façade.SearchAsync(worker, "danbooru", "cat", 1));
        Assert.Equal(first.RetryAt, second.RetryAt);
        Assert.Equal(1, sends);
        Assert.Equal(1, worker.SearchCalls);
    }

    [Fact]
    public async Task WireAndDecodedLimitsReturnMetadataTooLarge()
    {
        byte[] wire = new byte[BooruHostHttp.MaximumWireBytes + 1];
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, wire))));
        using (BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]))
        {
            BooruHostServices services = new(new Inner(), http);
            BooruJob job = services.OpenJob();
            try { Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), job.Operation, default)).Failure.Code); }
            finally { services.CloseJob(job); }
        }

        byte[] decoded = new byte[BooruHostHttp.MaximumDecodedBytes + 1];
        byte[] compressed;
        using (MemoryStream buffer = new())
        {
            using (GZipStream gzip = new(buffer, CompressionMode.Compress, leaveOpen: true)) gzip.Write(decoded);
            compressed = buffer.ToArray();
        }
        using HttpClient compressedTransport = new(new Handler((request, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, compressed);
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }));
        using BooruHostHttp compressedHttp = new(compressedTransport, new RequestPolicyState(), [Source()]);
        BooruHostServices compressedServices = new(new Inner(), compressedHttp);
        BooruJob compressedJob = compressedServices.OpenJob();
        try { Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await compressedServices.SendHttpAsync(Get(), compressedJob.Operation, default)).Failure.Code); }
        finally { compressedServices.CloseJob(compressedJob); }
    }

    [Fact]
    public async Task WireLimitAlsoAppliesWhenContentLengthIsUnknown()
    {
        byte[] wire = new byte[BooruHostHttp.MaximumWireBytes + 1];
        using HttpClient transport = new(new Handler((request, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new UnknownLengthContent(wire) };
            return Task.FromResult(response);
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try { Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), job.Operation, default)).Failure.Code); }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task SlowMetadataBodyIsBoundedByFifteenSecondResponseDeadline()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        {
            ++sends;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() });
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
            await EventuallyAsync(() => sends == 1);
            clock.Advance(BooruHostHttp.ResponseTimeout);
            HostHttpFailure result = Assert.IsType<HostHttpFailure>(await pending.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("http-timeout", result.Failure.Code);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task UnknownOrClosedJobNeverReachesTransportAndChanIsForwarded()
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) => { ++sends; return Task.FromResult(Response(HttpStatusCode.OK, "ok")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source(), ChanSource()]);
        Inner inner = new();
        BooruHostServices services = new(inner, http);
        HostHttpFailure unknown = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), new("booru-job:unknown", 1), default));
        Assert.Equal("booru-job", unknown.Failure.Code);
        Assert.Equal(0, sends);
        HostHttpResponse chan = Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(
            new("chan", HostHttpMethod.Get, new("https://chan.test/a")), new("chan-resource", 1), default));
        Assert.Equal("inner", Encoding.UTF8.GetString(chan.Body));
        Assert.Equal(1, inner.HttpCalls);
    }


    [Fact]
    public async Task ProviderAllowsOnlyOneActiveSendAndSpacesStartsByOneSecond()
    {
        ManualClock clock = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        using HttpClient transport = new(new Handler(async (request, token) =>
        {
            Interlocked.Increment(ref sends);
            await release.Task.WaitAsync(token);
            return Response(HttpStatusCode.OK, "ok");
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob firstJob = services.OpenJob();
        BooruJob secondJob = services.OpenJob();
        try
        {
            Task<HostHttpResult> first = services.SendHttpAsync(Get(), firstJob.Operation, default);
            await EventuallyAsync(() => sends == 1);
            Task<HostHttpResult> second = services.SendHttpAsync(Get(), secondJob.Operation, default);
            await Task.Delay(5);
            Assert.Equal(1, sends);
            release.TrySetResult();
            Assert.IsType<HostHttpResponse>(await first);
            await Task.Delay(5);
            Assert.Equal(1, sends);
            clock.Advance(TimeSpan.FromSeconds(1));
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await EventuallyAsync(() => sends == 2);
            release.TrySetResult();
            Assert.IsType<HostHttpResponse>(await second);
        }
        finally { services.CloseJob(firstJob); services.CloseJob(secondJob); }
    }

    [Fact]
    public async Task CancellationDuringProviderSpacingStopsWithoutTransportCall()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(Response(HttpStatusCode.OK, "ok")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob firstJob = services.OpenJob();
        BooruJob secondJob = services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), firstJob.Operation, default));
            using CancellationTokenSource cancellation = new();
            Task<HostHttpResult> pending = services.SendHttpAsync(Get(), secondJob.Operation, cancellation.Token);
            await Task.Delay(5);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, sends);
        }
        finally { services.CloseJob(firstJob); services.CloseJob(secondJob); }
    }

    [Fact]
    public async Task CursorPagesShareTheProviderSpacingAndTheJobSendBudget()
    {
        ManualClock clock = new();
        List<string> sent = new();
        using HttpClient transport = new(new Handler((request, _) =>
        { lock (sent) sent.Add(request.RequestUri!.Query); return Task.FromResult(Response(HttpStatusCode.OK, "[]")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpRequest Cursor(int index) => new("danbooru", HostHttpMethod.Get,
                new($"https://danbooru.test/posts.json?tags=cat&page=b{1000 - index}&limit=20"));
            Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Cursor(0), job.Operation, default));
            // The next cursor page waits for the 1 s start spacing like any numbered page.
            Task<HostHttpResult> spaced = services.SendHttpAsync(Cursor(1), job.Operation, default);
            await Task.Delay(20);
            Assert.False(spaced.IsCompleted);
            Assert.Single(sent);
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.IsType<HostHttpResponse>(await spaced);
            for (int i = 2; i < BooruHostHttp.MaximumSendsPerJob; ++i)
            {
                clock.Advance(BooruHostHttp.MinimumStartSpacing);
                Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Cursor(i), job.Operation, default));
            }
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Cursor(99), job.Operation, default)).Failure.Code);
            Assert.Equal(BooruHostHttp.MaximumSendsPerJob, sent.Count);
            Assert.Equal("?tags=cat&page=b999&limit=20", sent[1]);
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task TwentyFirstSendAndExpiredJobDoNotReachTransport()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(Response(HttpStatusCode.OK, "ok")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            for (int i = 0; i < BooruHostHttp.MaximumSendsPerJob; ++i)
            {
                Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), job.Operation, default));
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.Equal(BooruHostHttp.MaximumSendsPerJob, sends);
            Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), job.Operation, default)).Failure.Code);
            Assert.Equal(BooruHostHttp.MaximumSendsPerJob, sends);
            BooruJob expired = services.OpenJob();
            try
            {
                clock.Advance(BooruHostHttp.MaximumJobLifetime);
                Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(Get(), expired.Operation, default)).Failure.Code);
            }
            finally { services.CloseJob(expired); }
        }
        finally { services.CloseJob(job); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task TerminalStatusesAreReturnedWithoutRetry(HttpStatusCode status)
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        { ++sends; return Task.FromResult(Response(status, "error")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpResponse result = Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), job.Operation, default));
            Assert.Equal((int)status, result.StatusCode);
            Assert.Equal(1, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Theory]
    [InlineData("https://danbooru.test/users.json")]
    [InlineData("https://danbooru.test/posts.json/extra")]
    [InlineData("https://danbooru.test/posts/0.json")]
    [InlineData("https://danbooru.test/posts/01.json")]
    [InlineData("https://danbooru.test/posts/-5.json")]
    [InlineData("https://danbooru.test/posts/abc.json")]
    [InlineData("https://danbooru.test/posts/1234567890123456789.json")]
    [InlineData("https://danbooru.test/posts/5.json?x=1")]
    [InlineData("https://danbooru.test/posts/5")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=6&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=0&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=1&limit=21")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=1&limit=20&only=id")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&tags=dog")]
    [InlineData("https://danbooru.test/posts.json?tags=a+b+c+d&page=1&limit=20")]
    [InlineData("https://danbooru.test:8443/posts.json")]
    [InlineData("http://danbooru.test/posts.json")]
    // Every page form other than 1..5 or "b" + a canonical post id of at most 18 digits.
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b0&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b01&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b-5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b%2B5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b5%20&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b5x&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=bb5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=B5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=a5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=5b&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=1234567890123456789&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b1234567890123456789&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b%EF%BC%91&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=-1&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=1.5&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b5&page=b6&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b5&limit=21")]
    public async Task NonAllowlistedDanbooruEndpointsNeverReachTransport(string url)
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) => { ++sends; return Task.FromResult(Response(HttpStatusCode.OK, "{}")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpFailure result = Assert.IsType<HostHttpFailure>(await services.SendHttpAsync(
                new("danbooru", HostHttpMethod.Get, new(url)), job.Operation, default));
            Assert.Equal("http-request", result.Failure.Code);
            Assert.Equal(0, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Theory]
    [InlineData("https://danbooru.test/posts.json")]
    [InlineData("https://danbooru.test/posts.json?tags=rating%3Ag%20cat_ears&page=5&limit=20")]
    [InlineData("https://danbooru.test/posts/12345.json")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b1&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=rating%3Ag%20cat_ears&page=b12345&limit=20")]
    [InlineData("https://danbooru.test/posts.json?tags=cat&page=b999999999999999999&limit=20")]
    public async Task AllowlistedDanbooruEndpointsReachTransport(string url)
    {
        int sends = 0;
        using HttpClient transport = new(new Handler((request, _) => { ++sends; return Task.FromResult(Response(HttpStatusCode.OK, "{}")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(new("danbooru", HostHttpMethod.Get, new(url)), job.Operation, default));
            Assert.Equal(1, sends);
        }
        finally { services.CloseJob(job); }
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RedirectAndCooldownDecisionsReadNoBodyBytes(HttpStatusCode status)
    {
        TrackingContent content = new();
        using HttpClient transport = new(new Handler((request, _) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = content })));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            HostHttpResult result = await services.SendHttpAsync(Get(), job.Operation, default);
            Assert.True(result is HostHttpFailure or HostHttpDeferred);
            Assert.False(content.StreamOpened);
        }
        finally { services.CloseJob(job); }
    }

    private static HostHttpRequest Get() => new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"));
    private static SourceDescription Source() => new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));
    private static SourceDescription ChanSource() => new("chan", "Chan", new("https://chan.test/"),
        SourceCapabilities.ThreadRead, [], [new("chan.test", HttpHostPurpose.Metadata)], new("1", []));
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => Response(status, Encoding.UTF8.GetBytes(body));
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body) => new(status) { Content = new ByteArrayContent(body) };

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 100 && !predicate(); ++i) await Task.Delay(1);
        Assert.True(predicate());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class Inner : IPluginHostServices
    {
        public int HttpCalls { get; private set; }
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        { ++HttpCalls; return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("inner"))); }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class FakeWorker : IPluginWorkerClient
    {
        private readonly Func<BooruSearchRequest, PluginOperationContext, CancellationToken, Task<BooruSearchResult>>? _search;
        public FakeWorker(Func<BooruSearchRequest, PluginOperationContext, CancellationToken, Task<BooruSearchResult>>? search = null) => _search = search;
        public int SearchCalls { get; private set; }
        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake",
            [Source()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
        public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { ++SearchCalls; return _search?.Invoke(request, operation, cancellationToken) ?? Task.FromResult<BooruSearchResult>(new BooruSearchFailure(request.SourceId, new("test", "unused"))); }
        public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
            => Task.FromResult<BooruPostResult>(new BooruPostNotFound(request.Post));
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class TrackingContent : HttpContent
    {
        internal bool StreamOpened { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() { StreamOpened = true; return Task.FromResult<Stream>(new MemoryStream()); }
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream());
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ContinueWith(_ => 0, TaskScheduler.Default));
    }
}
