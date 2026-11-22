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

/// <summary>
/// Independent B1 contract proofs for the booru host transport: send budget (20 incl. retries),
/// 120-second job deadline, 429 cooldown, 502/503 single retry, no redirects, wire/decoded limits,
/// attempt timeout and cancellation, and non-interference with ordinary chan sources.
/// Fake transports and a manual clock only; no network.
/// </summary>
public sealed class BooruHostHttpContractTests
{
    private sealed class Rig : IDisposable
    {
        internal readonly ManualClock Clock = new();
        private readonly Handler _handler;
        internal readonly HttpClient Transport;
        internal readonly BooruHostHttp Http;
        internal readonly BooruHostServices Services;
        internal readonly List<HttpRequestMessage> Seen = [];
        internal int Sends;

        internal Rig(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
            Func<TimeSpan>? maximumCooldownProvider = null)
        {
            _handler = new Handler((request, token) =>
            {
                int n = Interlocked.Increment(ref Sends);
                lock (Seen) Seen.Add(request);
                return respond(n, request, token);
            });
            Transport = new HttpClient(_handler, disposeHandler: false);
            Http = new BooruHostHttp(Transport, new RequestPolicyState(Clock), [Source(), Other(), ChanSource()],
                maximumCooldownProvider: maximumCooldownProvider);
            Services = new BooruHostServices(new Inner(), Http);
        }

        internal Task<HostHttpResult> Send(BooruJob job, HostHttpRequest? request = null, CancellationToken token = default) =>
            Services.SendHttpAsync(request ?? Get(), job.Operation, token);

        public void Dispose() { Http.Dispose(); Transport.Dispose(); _handler.Dispose(); }
    }

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(410, false)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(410, true)]
    public async Task TerminalStatusDoesNotRetryWhenItsBodyThrows(int status, bool httpException)
    {
        using Rig rig = new((_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StreamContent(new ThrowingBody(httpException)) }));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            // No clock advance: a mistaken retry cannot finish this await.
            HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await rig.Send(job).WaitAsync(Bound));
            Assert.Equal("http-network", failure.Failure.Code);
            Assert.Equal(status, failure.Failure.HttpStatusCode);
            Assert.Equal(1, rig.Sends);
            Assert.Equal(1, job.Sends);
            Assert.False(rig.Clock.HasTimer(BooruHostHttp.RetryDelay));
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task RateLimitedJobNeverResumesButNewJobsRespectSourceCooldown()
    {
        using Rig rig = new((n, _, _) => Task.FromResult(Response(n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "[]")));
        BooruJob old = rig.Services.OpenJob();
        BooruJob? fresh = null;
        try
        {
            HostHttpDeferred deferred = Assert.IsType<HostHttpDeferred>(await rig.Send(old));
            Assert.False(old.IsOpen);
            Assert.True(old.Token.IsCancellationRequested);
            fresh = rig.Services.OpenJob();
            Assert.Equal(deferred.RetryAt, Assert.IsType<HostHttpDeferred>(await rig.Send(fresh)).RetryAt);
            Assert.Equal(0, fresh.Sends);
            Assert.Equal(1, rig.Sends);
            rig.Clock.Advance(BooruHostHttp.MinimumCooldown);
            Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await rig.Send(old)).Failure.Code);
            Assert.IsType<HostHttpResponse>(await rig.Send(fresh));
            Assert.Equal(2, rig.Sends);
            Assert.Equal(1, old.Sends);
        }
        finally
        {
            rig.Services.CloseJob(old);
            if (fresh is not null) rig.Services.CloseJob(fresh);
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task UnsupportedOuterEncodingAfterBuildingADecoderIsTerminal(string inner)
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, Encode([1, 2, 3], inner));
            response.Content.Headers.ContentEncoding.Add("zstd");
            response.Content.Headers.ContentEncoding.Add(inner);
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.Equal("http-encoding", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public void PartialDecoderChainDisposesItsInputExactlyOnce(string inner)
    {
        using DisposalStream input = new();
        Assert.NotNull(Record.Exception(() => BooruHostHttp.CreateDecoder(input, ["zstd", inner])));
        Assert.Equal(1, input.DisposeCalls); // proves disposal traversed the already constructed wrapper
    }

    private sealed class DisposalStream : MemoryStream
    {
        internal int DisposeCalls;
        protected override void Dispose(bool disposing) { if (disposing) ++DisposeCalls; base.Dispose(disposing); }
    }

    private sealed class ThrowingBody(bool httpException) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(httpException ? new HttpRequestException("synthetic body failure") : new IOException("synthetic body failure"));
    }

    // ---------- Redirect = 0 ----------

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task EveryRedirectStatusIsTerminalAndSentExactlyOnce(HttpStatusCode status)
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(status, "r");
            response.Headers.Location = new("https://danbooru.test/posts.json?page=2");
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await rig.Send(job));
            Assert.Equal("http-redirect", failure.Failure.Code);
            Assert.Equal((int)status, failure.Failure.HttpStatusCode);
            Assert.Equal(1, rig.Sends);
            Assert.Equal(1, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task TransportThatFollowedARedirectIsRejectedEvenWithOkStatus()
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, "{}");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.test/posts.json");
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.Equal("http-redirect", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task OffHostEndpointNeverReachesTransport()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            HostHttpRequest request = new("danbooru", HostHttpMethod.Get, new("https://evil.test/posts.json"));
            Assert.IsType<HostHttpFailure>(await rig.Send(job, request));
            Assert.Equal(0, rig.Sends);
            Assert.Equal(0, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- Endpoint allowlist: fail closed ----------

    [Theory]
    [InlineData("https://danbooru.test/posts.json")]
    [InlineData("https://danbooru.test/posts.json?tags=cat%20rating%3Ag&page=b123&limit=20")]
    [InlineData("https://danbooru.test/posts/12345.json")]
    public async Task DanbooruEndpointsAreAllowedAsBefore(string url)
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await rig.Send(job, new("danbooru", HostHttpMethod.Get, new(url))));
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData("https://danbooru.test/users.json")]
    [InlineData("https://danbooru.test/posts.json?login=x")]
    public async Task DanbooruPathsOutsideTheProfileAreStillRefused(string url)
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpFailure>(await rig.Send(job, new("danbooru", HostHttpMethod.Get, new(url))));
            Assert.Equal(0, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData("https://other.test/posts.json")]
    [InlineData("https://other.test/index.php?page=dapi&s=post&q=index")]
    public async Task PostSearchSourceWithoutEndpointProfileIsRejectedBeforeTheTransport(string url)
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await rig.Send(job, new("other", HostHttpMethod.Get, new(url))));
            Assert.Equal("http-request", failure.Failure.Code);
            Assert.Equal(0, rig.Sends);
            Assert.Equal(0, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- Retry budget: max 20 sends including retries ----------

    [Fact]
    public async Task RetryConsumesTheTwentySendBudgetAndIsRefusedWhenExhausted()
    {
        using Rig rig = new((n, _, _) => Task.FromResult(Response(
            n < BooruHostHttp.MaximumSendsPerJob ? HttpStatusCode.OK : HttpStatusCode.BadGateway, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            for (int i = 1; i < BooruHostHttp.MaximumSendsPerJob; ++i)
            {
                Assert.IsType<HostHttpResponse>(await rig.Send(job));
                rig.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            Task<HostHttpResult> last = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == BooruHostHttp.MaximumSendsPerJob);
            await rig.Clock.WaitForTimerAsync(BooruHostHttp.RetryDelay);
            rig.Clock.Advance(BooruHostHttp.RetryDelay);
            HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await last.WaitAsync(Bound));
            Assert.Equal("booru-budget", failure.Failure.Code);
            Assert.Equal(502, failure.Failure.HttpStatusCode);
            Assert.Equal(20, rig.Sends);
            Assert.Equal(20, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task TransportRetryAlsoConsumesBudgetAndCountsAsASecondSend()
    {
        using Rig rig = new((n, _, _) => n == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"))
            : Task.FromResult(Response(HttpStatusCode.OK, "ok")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            await rig.Clock.WaitForTimerAsync(BooruHostHttp.RetryDelay);
            rig.Clock.Advance(BooruHostHttp.RetryDelay);
            Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound));
            Assert.Equal(2, rig.Sends);
            Assert.Equal(2, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task SecondTransportFailureIsNetworkFailureWithoutThirdSend()
    {
        using Rig rig = new((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("boom")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            await rig.Clock.WaitForTimerAsync(BooruHostHttp.RetryDelay);
            rig.Clock.Advance(BooruHostHttp.RetryDelay);
            Assert.Equal("http-network", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
            Assert.Equal(2, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- 502/503 retry timing ----------

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, 0)]
    [InlineData(HttpStatusCode.BadGateway, 10)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 10)]
    public async Task RetryAfterLongerThanTwoSecondsIsHonoredAndZeroStillWaitsTwoSeconds(HttpStatusCode status,
        int retryAfterSeconds)
    {
        TimeSpan header = TimeSpan.FromSeconds(retryAfterSeconds);
        TimeSpan expected = retryAfterSeconds == 0 ? BooruHostHttp.RetryDelay : header;
        using Rig rig = new((n, _, _) =>
        {
            HttpResponseMessage response = Response(n == 1 ? status : HttpStatusCode.OK, "x");
            if (n == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(header);
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            await rig.Clock.WaitForTimerAsync(expected);
            rig.Clock.Advance(expected - TimeSpan.FromMilliseconds(1));
            await Task.Delay(20);
            Assert.Equal(1, rig.Sends);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound));
            Assert.Equal(2, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task FinalFiveOhThreeRetryAfterIsNormalizedToTheLiveCap()
    {
        using Rig rig = new((n, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable, "x");
            if (n == 2) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(30));
            return Task.FromResult(response);
        }, () => TimeSpan.FromHours(1));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            await rig.Clock.WaitForTimerAsync(BooruHostHttp.RetryDelay);
            rig.Clock.Advance(BooruHostHttp.RetryDelay);
            HostHttpResponse response = Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound));
            Assert.Equal(503, response.StatusCode);
            Assert.Equal("3600", response.Headers["Retry-After"]);
            Assert.Equal(2, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task RetryAfterBeyondTheJobDeadlineFailsAsBudgetWithoutWaitingOrRetrying()
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.ServiceUnavailable, "x");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await rig.Send(job).WaitAsync(Bound));
            Assert.Equal("booru-budget", failure.Failure.Code);
            Assert.Equal(503, failure.Failure.HttpStatusCode);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task RetryWouldLandOnTheDeadlineIsRefusedAtTheEdge()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.BadGateway, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            // now + 2 s == deadline: retryAt >= deadline is a refusal.
            rig.Clock.Advance(BooruHostHttp.MaximumJobLifetime - BooruHostHttp.RetryDelay);
            Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await rig.Send(job).WaitAsync(Bound)).Failure.Code);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task OtherServerErrorsAreNeverRetried()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.GatewayTimeout, HttpStatusCode.BadRequest })
        {
            using Rig rig = new((_, _, _) => Task.FromResult(Response(status, "x")));
            BooruJob job = rig.Services.OpenJob();
            try
            {
                Assert.Equal((int)status, Assert.IsType<HostHttpResponse>(await rig.Send(job).WaitAsync(Bound)).StatusCode);
                Assert.Equal(1, rig.Sends);
            }
            finally { rig.Services.CloseJob(job); }
        }
    }

    // ---------- 120 s deadline ----------

    [Fact]
    public async Task JobDeadlineAbortsAnInFlightHeaderWaitBeforeTheFifteenSecondWindow()
    {
        using Rig rig = new((_, _, token) => BlockUntilCancelled(token));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(110));
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            rig.Clock.Advance(TimeSpan.FromSeconds(10)); // 15 s window is clamped to the 120 s deadline
            Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task ExactlyAtDeadlineNoSendIsReserved()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            rig.Clock.Advance(BooruHostHttp.MaximumJobLifetime);
            Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.Equal(0, rig.Sends);
            Assert.Equal(0, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- 429 cooldown ----------

    [Fact]
    public async Task TooManyRequestsEnforcesSixtySecondFloorAndIsNotRetried()
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, "x");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(response);
        });
        BooruJob job = rig.Services.OpenJob();
        try
        {
            DateTimeOffset start = rig.Clock.GetUtcNow();
            HostHttpDeferred deferred = Assert.IsType<HostHttpDeferred>(await rig.Send(job));
            Assert.Equal(start + BooruHostHttp.MinimumCooldown, deferred.RetryAt);
            Assert.Equal("booru-cooldown", deferred.ReasonCode);
            Assert.Equal(429, deferred.HttpStatusCode);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData(1, 3600)]
    [InlineData(24, 86400)]
    [InlineData(168, 604800)]
    public async Task RetryAfterCooldownIsCappedByTheConfiguredBound(int configuredHours, int expectedSeconds)
    {
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, "busy");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(30));
            return Task.FromResult(response);
        }, () => TimeSpan.FromHours(configuredHours));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            DateTimeOffset now = rig.Clock.GetUtcNow();
            HostHttpDeferred deferred = Assert.IsType<HostHttpDeferred>(await rig.Send(job));
            Assert.Equal(now.AddSeconds(expectedSeconds), deferred.RetryAt);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task LoweringTheLiveCapShortensAnExistingInMemoryFence()
    {
        TimeSpan maximum = TimeSpan.FromHours(168);
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.TooManyRequests, "busy");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(30));
            return Task.FromResult(response);
        }, () => maximum);
        BooruJob first = rig.Services.OpenJob();
        try
        {
            Assert.Equal(rig.Clock.GetUtcNow() + TimeSpan.FromHours(168),
                Assert.IsType<HostHttpDeferred>(await rig.Send(first)).RetryAt);
        }
        finally { rig.Services.CloseJob(first); }

        maximum = TimeSpan.FromHours(1);
        BooruJob shortened = rig.Services.OpenJob();
        try
        {
            HostHttpDeferred deferred = Assert.IsType<HostHttpDeferred>(await rig.Send(shortened));
            Assert.Equal(rig.Clock.GetUtcNow() + TimeSpan.FromHours(1), deferred.RetryAt);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(shortened); }
    }

    [Fact]
    public async Task ResetCooldownClearsOnlyTheHostFenceAndPreservesPacing()
    {
        using Rig rig = new((n, _, _) => Task.FromResult(Response(
            n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "ok")));
        BooruJob limited = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpDeferred>(await rig.Send(limited));
            Assert.True(rig.Services.ResetCooldown("danbooru"));
            Assert.Equal(1, rig.Sends); // reset itself never starts HTTP
            BooruJob fresh = rig.Services.OpenJob();
            try
            {
                Task<HostHttpResult> pending = rig.Send(fresh);
                await rig.Clock.WaitForTimerAsync(BooruHostHttp.MinimumStartSpacing);
                Assert.Equal(1, rig.Sends); // the existing start-spacing fence survived reset
                rig.Clock.Advance(BooruHostHttp.MinimumStartSpacing);
                Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound));
                Assert.Equal(2, rig.Sends);
            }
            finally { rig.Services.CloseJob(fresh); }
        }
        finally { rig.Services.CloseJob(limited); }
    }

    [Fact]
    public async Task ResetTransactionHoldsTheHostGateUntilDurableCallerCommits()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "ok")));
        BooruHostHttp.CooldownReset reset = rig.Http.BeginCooldownReset("danbooru")
            ?? throw new InvalidOperationException("The declared Danbooru source must have a reset transaction.");
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await Task.Yield();
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, rig.Sends);

            Assert.True(reset.Commit());
            Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound));
            Assert.Equal(1, rig.Sends);
        }
        finally
        {
            reset.Dispose();
            rig.Services.CloseJob(job);
        }
    }

    [Fact]
    public async Task CooldownHonorsRetryAfterDateAndSpansJobsUntilItElapses()
    {
        bool limited = true;
        using Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "x");
            if (limited) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return Task.FromResult(response);
        });
        DateTimeOffset start = rig.Clock.GetUtcNow();
        BooruJob first = rig.Services.OpenJob();
        try { Assert.Equal(start.AddSeconds(300), Assert.IsType<HostHttpDeferred>(await rig.Send(first)).RetryAt); }
        finally { rig.Services.CloseJob(first); }

        limited = false;
        rig.Clock.Advance(TimeSpan.FromSeconds(299));
        BooruJob second = rig.Services.OpenJob();
        try
        {
            HostHttpDeferred still = Assert.IsType<HostHttpDeferred>(await rig.Send(second));
            Assert.Equal(start.AddSeconds(300), still.RetryAt);
            Assert.Equal(1, rig.Sends);
            Assert.Equal(0, second.Sends);
            Assert.True(rig.Http.TryGetCooldown("danbooru", out DateTimeOffset reported));
            Assert.Equal(start.AddSeconds(300), reported);
        }
        finally { rig.Services.CloseJob(second); }

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        BooruJob third = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await rig.Send(third));
            Assert.Equal(2, rig.Sends);
            Assert.False(rig.Http.TryGetCooldown("danbooru", out _));
        }
        finally { rig.Services.CloseJob(third); }
    }

    [Fact]
    public async Task CooldownIsPerSourceAndDoesNotBlockChanForwarding()
    {
        using Rig rig = new((_, request, _) => Task.FromResult(Response(
            request.RequestUri!.Host == "danbooru.test" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpDeferred>(await rig.Send(job));
            rig.Clock.Advance(BooruHostHttp.MinimumStartSpacing);
            // A PostSearch source without a host endpoint profile is refused before the transport (fail closed),
            // so it neither sends nor shares the Danbooru cooldown.
            HostHttpRequest other = new("other", HostHttpMethod.Get, new("https://other.test/index.php"));
            BooruJob otherJob = rig.Services.OpenJob();
            try { Assert.Equal("http-request", Assert.IsType<HostHttpFailure>(await rig.Send(otherJob, other)).Failure.Code); }
            finally { rig.Services.CloseJob(otherJob); }
            Assert.False(rig.Http.TryGetCooldown("other", out _));
            Assert.Equal(1, rig.Sends);

            int before = job.Sends;
            HostHttpRequest ordinary = new("chan", HostHttpMethod.Get, new("https://chan.test/a"));
            Assert.IsType<HostHttpFailure>(await rig.Send(job, ordinary));
            HostHttpResponse chan = Assert.IsType<HostHttpResponse>(
                await rig.Services.SendHttpAsync(ordinary, new("chan-resource", 1), default));
            Assert.Equal("inner", Encoding.UTF8.GetString(chan.Body));
            Assert.Equal(before, job.Sends); // chan traffic never spends the booru job budget
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- Wire / decompression limits ----------

    [Fact]
    public async Task AcceptEncodingIsIdentityOnly()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.IsType<HostHttpResponse>(await rig.Send(job));
            HttpRequestMessage seen = rig.Seen[0];
            Assert.Equal(["identity"], seen.Headers.AcceptEncoding.Select(static e => e.Value));
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task BodiesExactlyAtTheWireAndDecodedLimitsAreAccepted()
    {
        byte[] wire = new byte[BooruHostHttp.MaximumWireBytes];
        using (Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, wire))))
        {
            BooruJob job = rig.Services.OpenJob();
            try { Assert.Equal(wire.Length, Assert.IsType<HostHttpResponse>(await rig.Send(job)).Body.Length); }
            finally { rig.Services.CloseJob(job); }
        }

        byte[] decoded = new byte[BooruHostHttp.MaximumDecodedBytes];
        byte[] compressed = Encode(decoded, "gzip");
        Assert.True(compressed.Length < BooruHostHttp.MaximumWireBytes);
        using Rig gz = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, compressed, "gzip")));
        BooruJob gzJob = gz.Services.OpenJob();
        try { Assert.Equal(decoded.Length, Assert.IsType<HostHttpResponse>(await gz.Send(gzJob)).Body.Length); }
        finally { gz.Services.CloseJob(gzJob); }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("GZIP")]
    public async Task SupportedEncodingsDecodeAndDecodedLimitIsEnforcedForEach(string encoding)
    {
        byte[] small = Encoding.UTF8.GetBytes("{\"ok\":true}");
        using (Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, Encode(small, encoding), encoding))))
        {
            BooruJob job = rig.Services.OpenJob();
            try { Assert.Equal(small, Assert.IsType<HostHttpResponse>(await rig.Send(job)).Body); }
            finally { rig.Services.CloseJob(job); }
        }

        byte[] bomb = Encode(new byte[BooruHostHttp.MaximumDecodedBytes + 1], encoding);
        using Rig big = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, bomb, encoding)));
        BooruJob bigJob = big.Services.OpenJob();
        try { Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await big.Send(bigJob)).Failure.Code); }
        finally { big.Services.CloseJob(bigJob); }
    }

    [Fact]
    public async Task StackedEncodingsDecodeInReverseOrderButMoreThanTwoAreRejected()
    {
        byte[] small = Encoding.UTF8.GetBytes("{\"stacked\":1}");
        byte[] twice = Encode(Encode(small, "gzip"), "br"); // "gzip, br"
        using (Rig rig = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, twice);
            response.Content.Headers.ContentEncoding.Add("gzip");
            response.Content.Headers.ContentEncoding.Add("br");
            return Task.FromResult(response);
        }))
        {
            BooruJob job = rig.Services.OpenJob();
            try { Assert.Equal(small, Assert.IsType<HostHttpResponse>(await rig.Send(job)).Body); }
            finally { rig.Services.CloseJob(job); }
        }

        using Rig three = new((_, _, _) =>
        {
            HttpResponseMessage response = Response(HttpStatusCode.OK, small);
            foreach (string name in new[] { "gzip", "gzip", "gzip" }) response.Content.Headers.ContentEncoding.Add(name);
            return Task.FromResult(response);
        });
        BooruJob threeJob = three.Services.OpenJob();
        try { Assert.Equal("http-encoding", Assert.IsType<HostHttpFailure>(await three.Send(threeJob)).Failure.Code); }
        finally { three.Services.CloseJob(threeJob); }
    }

    [Theory]
    [InlineData("zstd")]
    [InlineData("compress")]
    public async Task UnknownEncodingIsRejectedAsEncodingFailure(string encoding)
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, [1, 2, 3], encoding)));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.Equal("http-encoding", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.Equal(1, rig.Sends); // not a transport failure: no retry
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task CorruptCompressedBodyIsEncodingFailureWithoutRetry(string encoding)
    {
        byte[] garbage = Enumerable.Range(0, 64).Select(static i => (byte)(0xA5 ^ i)).ToArray();
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, garbage, encoding)));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.Equal("http-encoding", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task OversizedDeclaredContentLengthIsRejectedBeforeAnyBodyByte()
    {
        using TrackingContent content = new() { DeclaredLength = BooruHostHttp.MaximumWireBytes + 1 };
        using Rig rig = new((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Assert.Equal("metadata-too-large", Assert.IsType<HostHttpFailure>(await rig.Send(job)).Failure.Code);
            Assert.False(content.StreamOpened);
            Assert.Equal(1, rig.Sends); // oversize is terminal, not retried
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- Timeout / cancellation ----------

    [Fact]
    public async Task HeaderWaitIsBoundedByFifteenSecondsAndNotRetried()
    {
        using Rig rig = new((_, _, token) => BlockUntilCancelled(token));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            Task<HostHttpResult> pending = rig.Send(job);
            await EventuallyAsync(() => rig.Sends == 1);
            rig.Clock.Advance(BooruHostHttp.ResponseTimeout - TimeSpan.FromMilliseconds(1));
            await Task.Delay(20);
            Assert.False(pending.IsCompleted);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal("http-timeout", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
            Assert.Equal(1, rig.Sends);
            Assert.Equal(1, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task HeadersCompletingAfterResponseDeadlineCannotReadBodyRetryOrStartCooldown(HttpStatusCode status)
    {
        ManualClock clock = new();
        TrackingContent content = new();
        int sends = 0;
        using Handler transportHandler = new((_, token) =>
        {
            ++sends;
            // Model a transport completing successfully despite its cancellation token.
            clock.Advance(BooruHostHttp.ResponseTimeout);
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });
        using HttpClient transport = new(transportHandler, disposeHandler: false);
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();
        try
        {
            // A conditional 304 must not bypass the expired window either.
            HostHttpRequest request = Get() with { Validators = new SourceValidators("\"v1\"", null) };
            Task<HostHttpResult> pending = services.SendHttpAsync(request, job.Operation, default);
            Assert.True(pending.IsCompleted, "Expired headers must fail without a retry wait.");
            Assert.Equal("http-timeout", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
            Assert.Equal(1, sends);
            Assert.Equal(1, job.Sends);
            Assert.True(job.IsOpen);
            Assert.False(content.StreamOpened);
            Assert.False(clock.HasTimer(BooruHostHttp.RetryDelay));
            Assert.False(services.TryGetCooldown("danbooru", out _));
        }
        finally { services.CloseJob(job); }
    }

    [Fact]
    public async Task PreCancelledCallerNeverReachesTransportOrReservesASend()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.OK, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            using CancellationTokenSource cts = new();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Send(job, token: cts.Token));
            Assert.Equal(0, rig.Sends);
            Assert.Equal(0, job.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    [Fact]
    public async Task CallerCancellationDuringRetryDelayPropagatesAndNeverSendsAgain()
    {
        using Rig rig = new((_, _, _) => Task.FromResult(Response(HttpStatusCode.BadGateway, "x")));
        BooruJob job = rig.Services.OpenJob();
        try
        {
            using CancellationTokenSource cts = new();
            Task<HostHttpResult> pending = rig.Send(job, token: cts.Token);
            await EventuallyAsync(() => rig.Sends == 1);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(1, rig.Sends);
        }
        finally { rig.Services.CloseJob(job); }
    }

    // ---------- Chan non-regression ----------

    [Fact]
    public async Task ChanSourceIsForwardedUntouchedWithoutJobTransportOrBooruPolicy()
    {
        Inner inner = new();
        using Handler transportHandler = new((_, _) => throw new InvalidOperationException("booru transport must not see chan traffic"));
        using HttpClient transport = new(transportHandler, disposeHandler: false);
        using BooruHostHttp http = new(transport, new RequestPolicyState(), [Source(), ChanSource()]);
        BooruHostServices services = new(inner, http);
        HostHttpRequest request = new("chan", HostHttpMethod.Get, new("https://chan.test/a"),
            new Dictionary<string, string> { ["Accept-Encoding"] = "gzip" });
        for (int i = 0; i < 25; ++i) // far above the booru 20-send budget: chan has no job and no budget
        {
            HostHttpResponse response = Assert.IsType<HostHttpResponse>(
                await services.SendHttpAsync(request, new("chan-resource", 1), default));
            Assert.Equal("inner", Encoding.UTF8.GetString(response.Body));
        }
        Assert.Equal(25, inner.HttpCalls);
        Assert.Same(request, inner.LastRequest);
    }

    // ---------- helpers ----------

    private static HostHttpRequest Get() => new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"));
    private static SourceDescription Source() => new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));
    private static SourceDescription Other() => new("other", "Other", new("https://other.test/"),
        SourceCapabilities.PostSearch, [], [new("other.test", HttpHostPurpose.Metadata)], new("1", []));
    private static SourceDescription ChanSource() => new("chan", "Chan", new("https://chan.test/"),
        SourceCapabilities.ThreadRead, [], [new("chan.test", HttpHostPurpose.Metadata)], new("1", []));

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => Response(status, Encoding.UTF8.GetBytes(body));
    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string? encoding = null)
    {
        HttpResponseMessage response = new(status) { Content = new ByteArrayContent(body) };
        if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
        return response;
    }

    private static byte[] Encode(byte[] data, string encoding)
    {
        using MemoryStream buffer = new();
        using (Stream stream = encoding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true),
            "deflate" => new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true),
            "br" => new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        }) stream.Write(data);
        return buffer.ToArray();
    }

    private static async Task<HttpResponseMessage> BlockUntilCancelled(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("unreachable");
    }

    // Only a hang guard: every wait below ends on a signal or on the awaited state, never on this limit.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using CancellationTokenSource guard = new(Bound);
        while (!predicate() && !guard.IsCancellationRequested) await Task.Delay(2);
        Assert.True(predicate());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class Inner : IPluginHostServices
    {
        public int HttpCalls { get; private set; }
        public HostHttpRequest? LastRequest { get; private set; }
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            ++HttpCalls; LastRequest = request;
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("inner")));
        }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class TrackingContent : HttpContent
    {
        internal long DeclaredLength { get; init; }
        internal bool StreamOpened { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = DeclaredLength; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() { StreamOpened = true; return Task.FromResult<Stream>(new MemoryStream()); }
    }
}
