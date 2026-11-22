using System.Net;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

// The inline gallery's sample path: the thumbnail rules with a caller-set body limit and a byte budget for the in-memory samples.
public sealed class BooruSampleHttpTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task ASampleLargerThanTheCallersLimitIsRefusedWhileAThumbnailKeepsItsOwnOneMiBLimit()
    {
        using Sizes handler = new(2 * MiB);
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source(), productVersion: "1.1.0");
        Uri url = new("https://cdn.example.test/a.png");

        Assert.Null(await http.LoadAsync(url));                                  // the thumbnail path still stops at 1 MiB
        Assert.Null(await http.LoadSampleAsync(url, MiB, 8 * MiB));              // declared length above the sample limit
        byte[]? sample = await http.LoadSampleAsync(url, 4 * MiB, 8 * MiB);
        Assert.Equal(2 * MiB, sample!.Length);
        Assert.Equal(2 * MiB, http.CachedSampleBytes);
    }

    [Fact]
    public async Task AKeptSampleIsNotFetchedAgainAndTheBudgetEvictsTheLeastRecentlyShownOne()
    {
        using Sizes handler = new(MiB);
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source(), productVersion: "1.1.0");
        Uri a = new("https://cdn.example.test/a.png"), b = new("https://cdn.example.test/b.png"), c = new("https://cdn.example.test/c.png");

        await http.LoadSampleAsync(a, 2 * MiB, 2 * MiB);
        await http.LoadSampleAsync(b, 2 * MiB, 2 * MiB);
        await http.LoadSampleAsync(a, 2 * MiB, 2 * MiB);
        Assert.Equal(2, handler.Requests);                                       // a came from memory
        await http.LoadSampleAsync(c, 2 * MiB, 2 * MiB);                         // over budget: b (least recent) goes
        Assert.Equal(2 * MiB, http.CachedSampleBytes);
        await http.LoadSampleAsync(a, 2 * MiB, 2 * MiB);
        Assert.Equal(3, handler.Requests);
        await http.LoadSampleAsync(b, 2 * MiB, 2 * MiB);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task ASampleLargerThanTheWholeBudgetIsShownButNotKeptAndDisposeClearsTheMemory()
    {
        using Sizes handler = new(2 * MiB);
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source(), productVersion: "1.1.0");
        Assert.NotNull(await http.LoadSampleAsync(new("https://cdn.example.test/big.png"), 4 * MiB, MiB));
        Assert.Equal(0, http.CachedSampleBytes);
        Assert.NotNull(await http.LoadSampleAsync(new("https://cdn.example.test/kept.png"), 4 * MiB, 4 * MiB));
        Assert.Equal(2 * MiB, http.CachedSampleBytes);
        http.Dispose();
        Assert.Equal(0, http.CachedSampleBytes);
        Assert.Null(await http.LoadSampleAsync(new("https://cdn.example.test/kept.png"), 4 * MiB, 4 * MiB));
    }

    [Fact]
    public async Task ASampleOnAnUndeclaredHostOrWithoutALimitSendsNothing()
    {
        using Sizes handler = new(1024);
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source(), productVersion: "1.1.0");
        Assert.Null(await http.LoadSampleAsync(new("https://other.example.test/a.png"), MiB, MiB));
        Assert.Null(await http.LoadSampleAsync(new("https://cdn.example.test/a.png"), 0, MiB));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CacheHitsEnforceLowerSampleAndCacheLimitsImmediately()
    {
        using Sizes handler = new(2 * MiB);
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source());
        Uri a = new("https://cdn.example.test/a.png"), b = new("https://cdn.example.test/b.png");
        Assert.NotNull(await http.LoadSampleAsync(a, 4 * MiB, 4 * MiB));
        Assert.NotNull(await http.LoadSampleAsync(b, 4 * MiB, 4 * MiB));

        Assert.Null(await http.LoadSampleAsync(a, MiB, 4 * MiB));
        Assert.Equal(2, handler.Requests);
        Assert.NotNull(await http.LoadSampleAsync(b, 4 * MiB, 2 * MiB));
        Assert.Equal(2 * MiB, http.CachedSampleBytes);
        Assert.Equal(2, handler.Requests);
        Assert.NotNull(await http.LoadSampleAsync(a, 4 * MiB, 2 * MiB));
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task TheConfiguredDeadlineReportsTimeoutAndCallerCancellationStillPropagates()
    {
        ManualClock clock = new();
        using Pending handler = new();
        using HttpClient transport = new(handler);
        using BooruPreviewHttp http = new(transport, Source(), timeProvider: clock);
        Uri url = new("https://cdn.example.test/a.png");
        Task<BooruSampleResult> timed = http.LoadSampleAsync(url, MiB, MiB, TimeSpan.FromSeconds(3), default);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(timed.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new BooruSampleResult(null, TimedOut: true), await timed.WaitAsync(TimeSpan.FromSeconds(5)));

        using CancellationTokenSource caller = new();
        Task<BooruSampleResult> canceled = http.LoadSampleAsync(url, MiB, MiB, TimeSpan.FromSeconds(3), caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class Pending : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private static SourceDescription Source() => new("example-booru", "Example booru", new("https://booru.example.test/"),
        SourceCapabilities.PostSearch, [], [new("cdn.example.test", HttpHostPurpose.PreviewMedia)], new("1", []));

    private sealed class Sizes(int length) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            byte[] body = new byte[length];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(body, 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(body) });
        }
    }
}
