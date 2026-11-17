using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

// Booru B3: the host's remote preview/sample path. Fake transports only (no live provider, no network). The B2 fixture
// branch had no committed fixtures when this was written, so the image bodies are minimal in-test signatures.
public sealed class BooruRemoteImageHttpTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string Cdn = "https://cdn.example.test/";

    [Fact]
    public async Task OnlyDeclaredPreviewMediaHostsServeThePreviewAndSampleRoles()
    {
        using FakeCdn cdn = FakeCdn.Serving(Png(64));
        using BooruRemoteImageHttp images = Create(cdn);

        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"))).Status);
        Assert.Equal(2, cdn.Count);

        // The SDK validates Sample like Preview against PreviewMedia hosts; an OriginalMedia or Metadata host is never a
        // preview host, and the Original role never uses this path at all.
        Uri[] refused =
        [
            new("https://original.example.test/s.png"), new("https://booru.example.test/s.png"),
            new("https://elsewhere.example.test/s.png"), new("https://user:pw@cdn.example.test/s.png"),
            new("https://cdn.example.test:8443/s.png"), new("https://cdn.example.test/s.png#x"), new("/s.png", UriKind.Relative),
        ];
        foreach (Uri url in refused)
        {
            Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Preview, url)).Status);
            Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Sample, url)).Status);
        }
        Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Original, new(Cdn + "o.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Preview, null)).Status);
        Assert.False(images.IsAllowed(BooruMediaRole.Original, new(Cdn + "o.png")));
        Assert.Equal(2, cdn.Count);
    }

    [Fact]
    public async Task PlainHttpIsRefusedBeforeTheTransport()
    {
        using FakeCdn cdn = FakeCdn.Serving(Png(64));
        using BooruRemoteImageHttp images = Create(cdn);

        Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Sample, new("http://cdn.example.test/s.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await images.LoadAsync(BooruMediaRole.Preview, new("ftp://cdn.example.test/p.png"))).Status);
        Assert.Equal(0, cdn.Count);
    }

    [Fact]
    public async Task RedirectsAreNeverFollowed()
    {
        using FakeCdn cdn = new((request, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Found) { RequestMessage = request };
            response.Headers.Location = new(Cdn + "elsewhere.png");
            return Task.FromResult(response);
        });
        using BooruRemoteImageHttp images = Create(cdn);

        BooruRemoteImageResult result = await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"));
        Assert.Equal(BooruRemoteImageStatus.Redirected, result.Status);
        Assert.Equal(302, result.HttpStatus);
        Assert.Equal(1, cdn.Count);
        Assert.Equal(Cdn + "s.png", cdn.Requests.Single().Url.AbsoluteUri);

        // A transport that followed a redirect on its own is detected from the final request URI.
        using FakeCdn following = new((request, _) => Task.FromResult(Ok(new HttpRequestMessage(HttpMethod.Get, Cdn + "moved.png"), Png(64))));
        using BooruRemoteImageHttp followed = Create(following);
        Assert.Equal(BooruRemoteImageStatus.Redirected, (await followed.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).Status);
        Assert.Equal(0, followed.CachedBytes(BooruMediaRole.Preview));
    }

    [Fact]
    public async Task RequestsCarryNoCredentialsAndCredentialedTransportsAreRefused()
    {
        using FakeCdn cdn = FakeCdn.Serving(Png(64));
        using BooruRemoteImageHttp images = Create(cdn);
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"))).Status);
        Recorded sent = cdn.Requests.Single();
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.False(sent.HasBody);
        Assert.Equal(new[] { "Accept", "Accept-Encoding", "User-Agent" }, sent.HeaderNames.Order(StringComparer.Ordinal));
        Assert.Equal("identity", sent.AcceptEncoding);
        Assert.StartsWith("MeowChan/1.1.0", sent.UserAgent, StringComparison.Ordinal);

        foreach ((string name, string value) in new[]
                 { ("Cookie", "session=1"), ("Authorization", "Bearer x"), ("Proxy-Authorization", "Basic eA=="), ("X-Api-Key", "k") })
        {
            using FakeCdn guarded = FakeCdn.Serving(Png(64));
            using HttpClient transport = new(guarded);
            transport.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            using BooruRemoteImageHttp refused = new(transport, Source(), productVersion: "1.1.0");
            Assert.Equal(BooruRemoteImageStatus.NotAllowed, (await refused.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).Status);
            Assert.Equal(0, guarded.Count);
        }
    }

    [Fact]
    public async Task BodiesAboveTheRoleLimitAreRefusedWithoutReadingOn()
    {
        int preview = BooruRemoteImageHttp.MaximumPreviewBytes;
        using FakeCdn cdn = new((request, _) => Task.FromResult(Ok(request, Png(request.RequestUri!.AbsolutePath switch
        {
            "/exact.png" => preview,
            "/over.png" => preview + 1,
            _ => 2 * 1024 * 1024,
        }))));
        using BooruRemoteImageHttp images = Create(cdn);

        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "exact.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.TooLarge, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "over.png"))).Status);
        // A 2 MiB sample is inside the default 8 MiB sample limit, a preview never.
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "big.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.TooLarge, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "big.png"))).Status);
        images.ApplyLimits(Limits(sampleBytes: 1024 * 1024));
        Assert.Equal(BooruRemoteImageStatus.TooLarge, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "big2.png"))).Status);

        // Without a Content-Length the streamed body is cut one byte past the limit.
        CountingStream endless = new(Png(16), long.MaxValue);
        using FakeCdn streaming = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StreamContent(endless) }));
        using BooruRemoteImageHttp streamed = Create(streaming);
        Assert.Equal(BooruRemoteImageStatus.TooLarge, (await streamed.LoadAsync(BooruMediaRole.Preview, new(Cdn + "s.png"))).Status);
        Assert.Equal(preview + 1, endless.Served);
        Assert.True(endless.Disposed);
    }

    [Fact]
    public async Task NonImageContentTypesAndEncodedBodiesAreRefused()
    {
        using FakeCdn cdn = new((request, _) =>
        {
            HttpResponseMessage response = Ok(request, Png(64), null);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/html.png": response.Content.Headers.ContentType = new("text/html"); break;
                case "/video.webm": response.Content.Headers.ContentType = new("video/webm"); break;
                case "/garbage.png": response.Content.Headers.TryAddWithoutValidation("Content-Type", "not a type;;"); break;
                case "/gzip.png": response.Content.Headers.ContentType = new("image/png"); response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "/png-with-params.png": response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png; q=1"); break;
            }
            return Task.FromResult(response);
        });
        using BooruRemoteImageHttp images = Create(cdn);

        foreach (string path in new[] { "html.png", "video.webm", "garbage.png", "gzip.png" })
            Assert.Equal(BooruRemoteImageStatus.WrongType, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + path))).Status);
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "png-with-params.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "untyped.png"))).Status);
    }

    [Fact]
    public async Task OnlyImageSignaturesReachTheCaller()
    {
        byte[] html = "<html><body>not an image</body></html>"u8.ToArray();
        using FakeCdn cdn = new((request, _) => Task.FromResult(Ok(request, html, "image/png")));
        using BooruRemoteImageHttp images = Create(cdn);
        Assert.Equal(BooruRemoteImageStatus.WrongType, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"))).Status);
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));

        Assert.True(BooruRemoteImageHttp.LooksLikeImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.True(BooruRemoteImageHttp.LooksLikeImage(Png(8)));
        Assert.True(BooruRemoteImageHttp.LooksLikeImage("GIF89a.."u8));
        Assert.True(BooruRemoteImageHttp.LooksLikeImage("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.False(BooruRemoteImageHttp.LooksLikeImage("RIFF\0\0\0\0AVI LIST"u8));
        Assert.False(BooruRemoteImageHttp.LooksLikeImage([0x1A, 0x45, 0xDF, 0xA3])); // WebM/Matroska
        Assert.False(BooruRemoteImageHttp.LooksLikeImage([0x89, 0x50]));
    }

    [Fact]
    public async Task OneGateBoundsPreviewsAndSamplesTogetherAndIdenticalLoadsShareOneRequest()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FakeCdn cdn = new(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return Ok(request, Png(64));
        });
        using BooruRemoteImageHttp images = Create(cdn, Limits(concurrency: 1));
        using CancellationTokenSource deadline = new(Deadline);
        try
        {
            Task<BooruRemoteImageResult> preview = images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"), deadline.Token);
            Task<BooruRemoteImageResult> again = images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"), deadline.Token);
            Task<BooruRemoteImageResult> sample = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), deadline.Token);
            Assert.Equal(1, images.RequestCount);
            Assert.False(sample.IsCompleted);

            release.TrySetResult();
            BooruRemoteImageResult[] results = await Task.WhenAll(preview, again, sample).WaitAsync(deadline.Token);
            Assert.All(results, result => Assert.Equal(BooruRemoteImageStatus.Loaded, result.Status));
            Assert.Same(results[0].Bytes, results[1].Bytes);
            Assert.Equal(2, cdn.Count);
        }
        finally { release.TrySetResult(); }

        using BooruRemoteImageHttp clamped = Create(cdn, Limits(concurrency: 99));
        Assert.Equal(BooruRemoteImageLimits.MaximumConcurrentRequestsCeiling, clamped.MaximumConcurrentRequests);
        using BooruRemoteImageHttp defaults = Create(cdn);
        Assert.Equal(BooruRemoteImageLimits.DefaultConcurrentRequests, defaults.MaximumConcurrentRequests);
    }

    [Fact]
    public async Task A429RaisesTheMediaFenceForRetryAfterAndStopsBothRoles()
    {
        ManualClock clock = new();
        int status = 429;
        using FakeCdn cdn = new((request, _) =>
        {
            if (status == 200) return Task.FromResult(Ok(request, Png(64)));
            HttpResponseMessage response = new((HttpStatusCode)status) { RequestMessage = request };
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return Task.FromResult(response);
        });
        using BooruRemoteImageHttp images = Create(cdn, clock: clock);
        List<DateTimeOffset> raised = [];
        images.FenceRaised += raised.Add;
        DateTimeOffset start = clock.GetUtcNow();

        BooruRemoteImageResult fenced = await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"));
        Assert.Equal(BooruRemoteImageStatus.Fenced, fenced.Status);
        Assert.Equal(429, fenced.HttpStatus);
        Assert.Equal(start.AddSeconds(120), fenced.RetryAt);
        Assert.Equal(new[] { start.AddSeconds(120) }, raised);
        Assert.Equal(start.AddSeconds(120), images.FenceUntil);

        // While the fence is up nothing is requested, for either role; there is no retry.
        status = 200;
        Assert.Equal(BooruRemoteImageStatus.Fenced, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"))).Status);
        Assert.Equal(BooruRemoteImageStatus.Fenced, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).Status);
        Assert.Equal(1, cdn.Count);

        // Expiry itself requests nothing; the next explicit load does.
        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Null(images.FenceUntil);
        Assert.Equal(1, cdn.Count);
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"))).Status);
        Assert.Equal(2, cdn.Count);
    }

    [Theory]
    [InlineData(503, null, 60)]         // no Retry-After: the 60 s minimum
    [InlineData(503, "5", 60)]          // shorter than the minimum
    [InlineData(429, "garbage", 60)]    // unparseable provider input
    [InlineData(429, "99999999999999999999999", 3600)] // saturating, capped by the configured maximum (1 h here)
    [InlineData(503, "600", 600)]
    public async Task TheFenceHonorsRetryAfterBetweenTheMinimumAndTheConfiguredCap(int status, string? retryAfter, int seconds)
    {
        ManualClock clock = new();
        using FakeCdn cdn = new((request, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)status) { RequestMessage = request };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });
        using BooruRemoteImageHttp images = new(cdn.Client, Source(), productVersion: "1.1.0", timeProvider: clock,
            maximumCooldownProvider: () => TimeSpan.FromMinutes(1)); // below the 1 h floor: clamped like the metadata path

        BooruRemoteImageResult result = await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"));
        Assert.Equal(BooruRemoteImageStatus.Fenced, result.Status);
        Assert.Equal(clock.GetUtcNow().AddSeconds(seconds), result.RetryAt);
    }

    [Fact]
    public async Task AnHttpDateRetryAfterIsHonored()
    {
        ManualClock clock = new();
        string retryAfter = clock.GetUtcNow().AddMinutes(10).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        using FakeCdn cdn = new((request, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });
        using BooruRemoteImageHttp images = Create(cdn, clock: clock);
        Assert.Equal(clock.GetUtcNow().AddMinutes(10), (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).RetryAt);
    }

    [Fact]
    public async Task LoadsQueuedBehindTheGateDoNotStartAfterTheFenceWentUp()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FakeCdn cdn = new(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        });
        using BooruRemoteImageHttp images = Create(cdn, Limits(concurrency: 1));
        using CancellationTokenSource deadline = new(Deadline);
        try
        {
            Task<BooruRemoteImageResult> first = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "a.png"), deadline.Token);
            Task<BooruRemoteImageResult> queued = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "b.png"), deadline.Token);
            release.TrySetResult();
            BooruRemoteImageResult[] results = await Task.WhenAll(first, queued).WaitAsync(deadline.Token);
            Assert.All(results, result => Assert.Equal(BooruRemoteImageStatus.Fenced, result.Status));
            Assert.Equal(1, cdn.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellingTheLastCallerAbortsTheRequestButNotWhileAnotherCallerWaits()
    {
        TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FakeCdn cdn = new(async (request, token) =>
        {
            try { await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { aborted.TrySetResult(); throw; }
            return Ok(request, Png(64));
        });
        using BooruRemoteImageHttp images = Create(cdn);
        using CancellationTokenSource deadline = new(Deadline);
        try
        {
            using CancellationTokenSource leaving = new();
            Task<BooruRemoteImageResult> stays = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), deadline.Token);
            Task<BooruRemoteImageResult> leaves = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), leaving.Token);
            leaving.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaves.WaitAsync(deadline.Token));
            Assert.False(aborted.Task.IsCompleted);
            release.TrySetResult();
            Assert.Equal(BooruRemoteImageStatus.Loaded, (await stays.WaitAsync(deadline.Token)).Status);
            Assert.Equal(1, cdn.Count);

            // A sole caller's cancellation reaches the transport's token: the request is really aborted.
            using CancellationTokenSource sole = new();
            TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = hold;
            Task<BooruRemoteImageResult> alone = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "other.png"), sole.Token);
            sole.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => alone.WaitAsync(deadline.Token));
            await aborted.Task.WaitAsync(deadline.Token);
            Assert.Equal(Png(64).Length, images.CachedBytes(BooruMediaRole.Sample));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellingDuringTheBodyAbortsTheReadAndReleasesTheResponse()
    {
        BlockingStream body = new(Png(16));
        using FakeCdn cdn = new((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = new StreamContent(body) }));
        using BooruRemoteImageHttp images = Create(cdn);
        using CancellationTokenSource deadline = new(Deadline);
        using CancellationTokenSource caller = new();

        Task<BooruRemoteImageResult> load = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), caller.Token);
        await body.Blocked.Task.WaitAsync(deadline.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(deadline.Token));
        await body.Cancelled.Task.WaitAsync(deadline.Token);
        await body.Closed.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));
    }

    [Fact]
    public async Task EachRoleCacheEvictsTheLeastRecentlyUsedBytesWithinItsBudget()
    {
        using FakeCdn cdn = new((request, _) =>
            Task.FromResult(Ok(request, Png(request.RequestUri!.AbsolutePath == "/huge.png" ? 4000 : 1000))));
        using BooruRemoteImageHttp images = Create(cdn, Limits(previewCache: 3000, sampleCache: 1000));
        async Task<BooruRemoteImageResult> Preview(string name) => await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + name));

        foreach (string name in new[] { "a.png", "b.png", "c.png" }) await Preview(name);
        Assert.Equal(3000, images.CachedBytes(BooruMediaRole.Preview));
        Assert.True((await Preview("a.png")).FromCache);   // a is now the most recent
        await Preview("d.png");                            // evicts b, the least recently used
        Assert.Equal(4, cdn.Count);
        Assert.True((await Preview("a.png")).FromCache);
        Assert.True((await Preview("c.png")).FromCache);
        Assert.False((await Preview("b.png")).FromCache);
        Assert.Equal(5, cdn.Count);
        Assert.Equal(3000, images.CachedBytes(BooruMediaRole.Preview));

        // An entry above the whole budget is returned but neither cached nor allowed to evict the others.
        Assert.Equal(BooruRemoteImageStatus.Loaded, (await Preview("huge.png")).Status);
        Assert.Equal(3000, images.CachedBytes(BooruMediaRole.Preview));

        // Budgets are per role: a sample never evicts a preview.
        await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s1.png"));
        await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s2.png"));
        Assert.Equal(1000, images.CachedBytes(BooruMediaRole.Sample));
        Assert.Equal(3000, images.CachedBytes(BooruMediaRole.Preview));

        // Lowering a budget trims at once; a zero budget caches nothing.
        images.ApplyLimits(Limits(previewCache: 1000, sampleCache: 0));
        Assert.Equal(1000, images.CachedBytes(BooruMediaRole.Preview));
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));
        await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s3.png"));
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));
    }

    [Fact]
    public async Task ClearEmptiesBothCachesAndKeepsBytesRequestedBeforeItOut()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release.TrySetResult();
        using FakeCdn cdn = new(async (request, token) =>
        {
            await release.Task.WaitAsync(token);
            return Ok(request, Png(500));
        });
        using BooruRemoteImageHttp images = Create(cdn);
        using CancellationTokenSource deadline = new(Deadline);
        await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"), deadline.Token);
        await images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), deadline.Token);
        Assert.Equal(500, images.CachedBytes(BooruMediaRole.Preview));
        Assert.Equal(500, images.CachedBytes(BooruMediaRole.Sample));

        images.Clear();
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Preview));
        Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));
        Assert.False((await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"), deadline.Token)).FromCache);
        Assert.Equal(3, cdn.Count);

        // A request running across Clear() still answers its caller, but leaves the cleared cache empty.
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task<BooruRemoteImageResult> running = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "late.png"), deadline.Token);
            images.Clear();
            release.TrySetResult();
            Assert.Equal(BooruRemoteImageStatus.Loaded, (await running.WaitAsync(deadline.Token)).Status);
            Assert.Equal(0, images.CachedBytes(BooruMediaRole.Sample));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task PrefetchLoadsAtMostThreeNeighboursOneAfterAnotherAndStopsAtTheFence()
    {
        int inFlight = 0, peak = 0;
        using FakeCdn cdn = new(async (request, _) =>
        {
            int now = Interlocked.Increment(ref inFlight);
            peak = Math.Max(peak, now);
            await Task.Yield();
            Interlocked.Decrement(ref inFlight);
            if (request.RequestUri!.AbsolutePath == "/busy.png")
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            return Ok(request, Png(64));
        });
        using BooruRemoteImageHttp images = Create(cdn);
        Uri?[] neighbours = [new(Cdn + "1.png"), null, new(Cdn + "3.png"), new(Cdn + "4.png"), new(Cdn + "5.png")];

        Assert.Equal(0, await images.PrefetchSamplesAsync(neighbours, 0));
        Assert.Equal(0, cdn.Count);
        Assert.Equal(2, await images.PrefetchSamplesAsync(neighbours, 99)); // clamped to 3 positions: 1, null, 3
        Assert.Equal(2, cdn.Count);
        Assert.Equal(1, peak);

        Assert.Equal(0, await images.PrefetchSamplesAsync([new(Cdn + "busy.png"), new(Cdn + "4.png")], 2));
        Assert.Equal(3, cdn.Count);
        Assert.NotNull(images.FenceUntil);
    }

    [Fact]
    public async Task DisposeAbortsRunningRequestsAndRefusesLaterLoads()
    {
        TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FakeCdn cdn = new(async (request, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { aborted.TrySetResult(); throw; }
            return Ok(request, Png(64));
        });
        BooruRemoteImageHttp images = Create(cdn);
        using CancellationTokenSource deadline = new(Deadline);
        Task<BooruRemoteImageResult> running = images.LoadAsync(BooruMediaRole.Sample, new(Cdn + "s.png"), deadline.Token);

        images.Dispose();
        await aborted.Task.WaitAsync(deadline.Token);
        Assert.Equal(BooruRemoteImageStatus.Unavailable, (await running.WaitAsync(deadline.Token)).Status);
        Assert.Equal(BooruRemoteImageStatus.Unavailable, (await images.LoadAsync(BooruMediaRole.Preview, new(Cdn + "p.png"))).Status);
        Assert.Equal(1, cdn.Count);
        images.Dispose();
    }

    private static BooruRemoteImageHttp Create(FakeCdn cdn, BooruRemoteImageLimits? limits = null, TimeProvider? clock = null) =>
        new(cdn.Client, Source(), limits, productVersion: "1.1.0", timeProvider: clock);

    private static BooruRemoteImageLimits Limits(long previewCache = BooruRemoteImageLimits.DefaultPreviewCacheBytes,
        long sampleCache = BooruRemoteImageLimits.DefaultSampleCacheBytes, int sampleBytes = BooruRemoteImageLimits.DefaultSampleBytes,
        int concurrency = BooruRemoteImageLimits.DefaultConcurrentRequests) => new(previewCache, sampleCache, sampleBytes, concurrency);

    private static SourceDescription Source() => new("example-booru", "Example booru", new("https://booru.example.test/"),
        SourceCapabilities.PostSearch | SourceCapabilities.Previews, [],
        [
            new("booru.example.test", HttpHostPurpose.Metadata),
            new("original.example.test", HttpHostPurpose.OriginalMedia),
            new("cdn.example.test", HttpHostPurpose.PreviewMedia),
        ], new("1", []));

    /// <summary>A body of <paramref name="length"/> bytes that starts with the PNG signature.</summary>
    private static byte[] Png(int length)
    {
        byte[] bytes = new byte[Math.Max(length, 8)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static HttpResponseMessage Ok(HttpRequestMessage request, byte[] body, string? type = "image/png")
    {
        ByteArrayContent content = new(body);
        if (type is not null) content.Headers.ContentType = new(type);
        return new(HttpStatusCode.OK) { RequestMessage = request, Content = content };
    }

    private sealed record Recorded(HttpMethod Method, Uri Url, bool HasBody, string[] HeaderNames, string AcceptEncoding, string UserAgent);

    private sealed class FakeCdn(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private HttpClient? _client;

        internal static FakeCdn Serving(byte[] body) => new((request, _) => Task.FromResult(Ok(request, body)));

        internal ConcurrentQueue<Recorded> Requests { get; } = new();
        internal int Count => Requests.Count;
        internal HttpClient Client => _client ??= new HttpClient(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.Method, request.RequestUri!, request.Content is not null,
                request.Headers.Select(header => header.Key).ToArray(), request.Headers.AcceptEncoding.ToString(),
                request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? agent) ? string.Join(" ", agent) : ""));
            return respond(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _client?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Non-seekable body (so no Content-Length) that serves <paramref name="prefix"/> and then zeros up to a length.</summary>
    private sealed class CountingStream(byte[] prefix, long length) : Stream
    {
        internal long Served { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int served = (int)Math.Min(count, length - Served);
            for (int index = 0; index < served; index++)
                buffer[offset + index] = Served + index < prefix.Length ? prefix[Served + index] : (byte)0;
            Served += served;
            return served;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>Serves a prefix, then blocks every read until the read's token is cancelled.</summary>
    private sealed class BlockingStream(byte[] prefix) : Stream
    {
        private bool _served;
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                prefix.CopyTo(buffer);
                return prefix.Length;
            }
            Blocked.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return 0;
        }
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
    }
}
