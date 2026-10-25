using System.Net;
using System.Net.Http.Headers;
using MeowChan.Http;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.OriginalMedia.Tests;

public sealed class OriginalMediaHttpTests
{
    private static SourceDescription Source => new("2ch", "2ch", new("https://2ch.hk/"), SourceCapabilities.OriginalMedia,
        [], [new("2ch.hk", HttpHostPurpose.OriginalMedia | HttpHostPurpose.Metadata), new("2ch.su", HttpHostPurpose.OriginalMedia),
            new("metadata.test", HttpHostPurpose.Metadata), new("preview.test", HttpHostPurpose.PreviewMedia)], new("1", []));
    private static readonly Uri Initial = new("https://2ch.hk/demo/src/123/file.png");

    [Theory]
    [InlineData(301)] [InlineData(302)] [InlineData(303)] [InlineData(307)] [InlineData(308)]
    public async Task DeclaredCrossHostOriginalRedirectPreservesBytesAndDisposesReplacedResponse(int status)
    {
        TrackingContent redirectContent = new([99]), finalContent = new([1, 2, 3, 4]);
        List<Uri> sent = [];
        using HttpClient transport = new(new Handler((request, _) =>
        {
            sent.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("MeowChan", request.Headers.UserAgent.ToString());
            return Task.FromResult(sent.Count == 1 ? Redirect(request, status, "https://2ch.su/demo/src/123/file.png", redirectContent)
                : Response(request, 200, finalContent));
        }));
        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(new[] { Initial, new Uri("https://2ch.su/demo/src/123/file.png") }, sent);
        Assert.True(redirectContent.Disposed); Assert.False(finalContent.Disposed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        response.Dispose(); Assert.True(finalContent.Disposed);
    }

    [Theory]
    [InlineData("/next.png", "https://2ch.hk/next.png")]
    [InlineData("//2ch.su/file.png", "https://2ch.su/file.png")]
    public async Task RelativeRedirectsResolveBeforePurposeValidation(string location, string expected)
    {
        List<Uri> sent = [];
        using HttpClient transport = new(new Handler((request, _) =>
        {
            sent.Add(request.RequestUri!);
            return Task.FromResult(sent.Count == 1 ? Redirect(request, 302, location) : Response(request));
        }));
        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(expected, sent[1].AbsoluteUri);
    }

    [Theory]
    [InlineData("https://foreign.invalid/file")]
    [InlineData("https://metadata.test/file")]
    [InlineData("https://preview.test/file")]
    [InlineData("https://sub.2ch.su/file")]
    [InlineData("https://2ch.su.foreign.invalid/file")]
    [InlineData("http://2ch.su/file")]
    [InlineData("https://user:secret@2ch.su/file")]
    [InlineData("https://2ch.su/file#secret")]
    [InlineData("https://2ch.su:8443/file")]
    [InlineData("ftp://2ch.su/file")]
    public async Task ForbiddenRedirectNeverReachesTransport(string location)
    {
        int sent = 0; TrackingContent content = new([1]);
        using HttpClient transport = new(new Handler((request, _) =>
        { sent++; return Task.FromResult(Redirect(request, 302, location, content)); }));
        using HttpClient client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(1, sent); Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("https://metadata.test/file")]
    [InlineData("https://foreign.invalid/file")]
    [InlineData("https://user@2ch.hk/file")]
    public async Task InitialDestinationAlsoRequiresAnOriginalHost(string uri)
    {
        int sent = 0;
        using HttpClient transport = new(new Handler((request, _) => { sent++; return Task.FromResult(Response(request)); }));
        using HttpClient client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task LoopsAreBoundedAndAllUnreturnedResponsesDisposed()
    {
        List<TrackingContent> contents = [];
        using HttpClient transport = new(new Handler((request, _) =>
        { TrackingContent content = new([1]); contents.Add(content); return Task.FromResult(Redirect(request, 301, Initial.AbsoluteUri, content)); }));
        using HttpClient client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead));
        Assert.Equal(PluginOriginalMediaHttp.MaximumRedirects + 1, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Fact]
    public async Task MissingLocationAndHiddenTransportRedirectFailWithoutReturningBodies()
    {
        foreach (bool hidden in new[] { false, true })
        {
            TrackingContent content = new([1]);
            using HttpClient transport = new(new Handler((request, _) => Task.FromResult(
                Response(hidden ? new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/file") : request, hidden ? 200 : 302, content))));
            using HttpClient client = Client(transport);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead));
            Assert.True(content.Disposed);
        }
    }

    [Theory]
    [InlineData("Authorization")] [InlineData("Proxy-Authorization")] [InlineData("Cookie")] [InlineData("Host")] [InlineData("Referer")]
    public async Task SensitiveOrUnsupportedHeadersCannotReachEitherHop(string header)
    {
        foreach (bool onTransport in new[] { false, true })
        {
            int sent = 0;
            using HttpClient transport = new(new Handler((request, _) => { sent++; return Task.FromResult(Response(request)); }));
            using HttpClient client = Client(transport);
            (onTransport ? transport.DefaultRequestHeaders : client.DefaultRequestHeaders).TryAddWithoutValidation(header, "secret");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead));
            Assert.Equal(0, sent);
        }
    }

    [Fact]
    public async Task HeaderAndRequestPolicyOptionsRemainAvailableForEachHop()
    {
        int sent = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        {
            sent++; Assert.Equal("image/*", request.Headers.Accept.ToString());
            Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<string>("fixture"), out string? value)); Assert.Equal("retained", value);
            return Task.FromResult(sent == 1 ? Redirect(request, 307, "/next") : Response(request));
        }));
        using HttpClient client = Client(transport);
        using HttpRequestMessage initial = new(HttpMethod.Head, Initial);
        initial.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        initial.Options.Set(new HttpRequestOptionsKey<string>("fixture"), "retained");
        using HttpResponseMessage result = await client.SendAsync(initial, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(Initial, initial.RequestUri); Assert.Equal(2, sent);
    }

    [Fact]
    public async Task CancellationBeforeSendAndAfterLateResponseCannotReturnABody()
    {
        using CancellationTokenSource before = new(); before.Cancel(); int sent = 0;
        using HttpClient untouched = new(new Handler((request, _) => { sent++; return Task.FromResult(Response(request)); }));
        using (HttpClient client = Client(untouched))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead, before.Token));
        Assert.Equal(0, sent);
        using CancellationTokenSource late = new(); TrackingContent content = new([1]);
        using HttpClient transport = new(new Handler((request, _) =>
        { late.Cancel(); return Task.FromResult(Response(request, 200, content)); }));
        using HttpClient lateClient = Client(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lateClient.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead, late.Token));
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task AllHopsAndPolicyRetriesShareOneOverallHeaderDeadline()
    {
        ManualTime clock = new(); List<TrackingContent> contents = []; int sent = 0;
        using HttpClient transport = new(new Handler((request, _) =>
        {
            sent++; clock.Advance(TimeSpan.FromSeconds(9));
            TrackingContent content = new([1]); contents.Add(content);
            if (sent != 1) return Task.FromResult(Redirect(request, 302, "/next-" + sent, content));
            HttpResponseMessage unavailable = Response(request, 503, content);
            unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return Task.FromResult(unavailable);
        }));
        using HttpClient client = PluginOriginalMediaHttp.CreateClient(transport, Source, new RequestPolicyState(clock, () => 0));
        Task<HttpResponseMessage> pending = client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(1, sent); Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(20));
        for (int second = 0; second < 80 && !pending.IsCompleted; second++)
        { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1); }
        await Assert.ThrowsAsync<TimeoutException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(5, sent); Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Fact]
    public async Task DisposingWrapperDoesNotDisposeBorrowedTransportAndFinalFailuresRemainCallerOwned()
    {
        TrackingContent content = new([1]);
        int sent = 0;
        using HttpClient transport = new(new Handler((request, _) => Task.FromResult(Response(request, 404, ++sent == 1 ? content : new ByteArrayContent([])))));
        using (HttpClient client = Client(transport))
        {
            using HttpResponseMessage response = await client.GetAsync(Initial, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.False(content.Disposed);
        }
        Assert.True(content.Disposed);
        using HttpResponseMessage direct = await transport.GetAsync("https://foreign.invalid/direct", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
    }

    private static HttpClient Client(HttpClient transport) => PluginOriginalMediaHttp.CreateClient(transport, Source, new RequestPolicyState());
    private static HttpResponseMessage Response(HttpRequestMessage request, int status = 200, HttpContent? content = null) =>
        new((HttpStatusCode)status) { RequestMessage = request, Content = content ?? new ByteArrayContent([]) };
    private static HttpResponseMessage Redirect(HttpRequestMessage request, int status, string location, HttpContent? content = null)
    { HttpResponseMessage response = Response(request, status, content); response.Headers.Location = new(location, UriKind.RelativeOrAbsolute); return response; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class TrackingContent(byte[] bytes) : ByteArrayContent(bytes)
    { internal bool Disposed { get; private set; } protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); } }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new(); private readonly List<Timer> _timers = []; private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock (_sync) { Timer timer = new(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; } }
        internal void Advance(TimeSpan duration)
        {
            List<Timer> due;
            lock (_sync) { _ticks += duration.Ticks; due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList(); foreach (Timer timer in due) timer.Due = long.MaxValue; }
            foreach (Timer timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback { get; } = callback; internal object? State { get; } = state;
            internal long Due { get; set; } internal bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner._sync) { Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks; return !Disposed; } }
            public void Dispose() { lock (owner._sync) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
