using System.Net;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.PluginRuntime.Tests;

// Pins the synthetic CDN of tests/Shared/BooruCdnFixtures.cs against the real consumer that exists today (the preview
// path), so the later host work (B3) tests against fixtures that are known to do what their names say. No network.
public sealed class BooruCdnFixtureTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string Cdn = "https://cdn.donmai.us/";

    [Fact]
    public void TinyImagesAreRealFormatsAndTheNonImagesAreNot()
    {
        Assert.All(new[] { BooruTinyImages.Png(), BooruTinyImages.Jpeg(), BooruTinyImages.WebP(), BooruTinyImages.Gif(), BooruTinyImages.PngSignatureOnly() },
            bytes => Assert.True(BooruPreviewHttp.LooksLikeImage(bytes)));
        Assert.False(BooruPreviewHttp.LooksLikeImage(BooruTinyImages.Html()));
        Assert.All(new[] { BooruTinyImages.Png(), BooruTinyImages.Jpeg(), BooruTinyImages.WebP(), BooruTinyImages.Gif() },
            bytes => Assert.InRange(bytes.Length, 50, 1024));
        // A fresh array each time: a test that edits its bytes cannot affect the next one.
        Assert.NotSame(BooruTinyImages.Png(), BooruTinyImages.Png());
    }

    [Fact]
    public async Task PreviewPathAcceptsEveryTinyImageFormatWithoutCredentials()
    {
        BooruCdnHandler handler = new();
        handler.Script.On(Cdn + "a.png", BooruCdn.Png()).On(Cdn + "b.jpg", BooruCdn.Jpeg())
            .On(Cdn + "c.webp", BooruCdn.WebP()).On(Cdn + "d.gif", BooruCdn.Gif());
        using HttpClient transport = handler.Client();
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        Assert.Equal(BooruTinyImages.Png(), await previews.LoadAsync(new(Cdn + "a.png"), deadline.Token));
        Assert.Equal(BooruTinyImages.Jpeg(), await previews.LoadAsync(new(Cdn + "b.jpg"), deadline.Token));
        Assert.Equal(BooruTinyImages.WebP(), await previews.LoadAsync(new(Cdn + "c.webp"), deadline.Token));
        Assert.Equal(BooruTinyImages.Gif(), await previews.LoadAsync(new(Cdn + "d.gif"), deadline.Token));
        Assert.Empty(handler.Script.Violations);
        Assert.All(handler.Script.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.False(request.HasAuthorization || request.HasCookie);
            Assert.Equal("identity", request.AcceptEncoding);
        });
    }

    [Fact]
    public async Task EveryBrokenCdnCaseIsRefusedByThePreviewPathAfterExactlyOneRequest()
    {
        DateTimeOffset later = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        (string Name, BooruCdnResponse Response)[] cases =
        {
            ("missing", BooruCdn.Status(HttpStatusCode.NotFound)),
            ("gone", BooruCdn.Status(HttpStatusCode.Gone)),
            ("forbidden", BooruCdn.Status(HttpStatusCode.Forbidden)),
            ("throttled-seconds", BooruCdn.RateLimited(120)),
            ("throttled-date", BooruCdn.RateLimitedUntil(later)),
            ("throttled-no-header", BooruCdn.RateLimited(null)),
            ("throttled-invalid-header", BooruCdn.ThrottledWithInvalidRetryAfter()),
            ("unavailable-seconds", BooruCdn.ServiceUnavailable(30)),
            ("unavailable-date", BooruCdn.ServiceUnavailableUntil(later)),
            ("unavailable-no-header", BooruCdn.ServiceUnavailable(null)),
            ("redirect-same-host", BooruCdn.Redirect(Cdn + "elsewhere.png")),
            ("redirect-foreign", BooruCdn.Redirect("https://example.invalid/steal.png", HttpStatusCode.MovedPermanently)),
            ("redirect-followed-by-transport", BooruCdn.FollowedRedirect(Cdn + "other.png")),
            ("oversized-declared", BooruCdn.Oversized(BooruPreviewHttp.MaximumPreviewBytes + 1)),
            ("oversized-chunked", BooruCdn.Oversized(BooruPreviewHttp.MaximumPreviewBytes + 1, declareLength: false)),
            ("html-as-jpeg", BooruCdn.HtmlLabelledAsImage()),
            ("html-as-html", BooruCdn.WrongContentType(BooruTinyImages.Html(), "text/html")),
            ("empty", BooruCdn.EmptyBody()),
        };
        BooruCdnHandler handler = new();
        foreach ((string name, BooruCdnResponse response) in cases) handler.Script.On(Cdn + name + ".jpg", response);
        using HttpClient transport = handler.Client();
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        foreach ((string name, _) in cases)
        {
            Assert.True(await previews.LoadAsync(new(Cdn + name + ".jpg"), deadline.Token) is null, name);
            Assert.True(1 == handler.Script.Count(new Uri(Cdn + name + ".jpg")), name);
        }
        Assert.Empty(handler.Script.Violations);
    }

    [Fact]
    public async Task TheByteBudgetBoundaryIsExactAndAnUndeclaredLengthIsCountedWhileStreaming()
    {
        BooruCdnHandler handler = new();
        handler.Script.On(Cdn + "at-limit.png", BooruCdn.Oversized(BooruPreviewHttp.MaximumPreviewBytes))
            .On(Cdn + "at-limit-chunked.png", BooruCdn.Oversized(BooruPreviewHttp.MaximumPreviewBytes, declareLength: false));
        using HttpClient transport = handler.Client();
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        Assert.Equal(BooruPreviewHttp.MaximumPreviewBytes, (await previews.LoadAsync(new(Cdn + "at-limit.png"), deadline.Token))!.Length);
        Assert.Equal(BooruPreviewHttp.MaximumPreviewBytes, (await previews.LoadAsync(new(Cdn + "at-limit-chunked.png"), deadline.Token))!.Length);
    }

    [Fact]
    public async Task ThrottlingAnswersCarryTheRetryAfterFormsTheHostMustParse()
    {
        BooruCdnHandler handler = new();
        DateTimeOffset retryAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        handler.Script.On(Cdn + "s.jpg", BooruCdn.RateLimited(120)).On(Cdn + "d.jpg", BooruCdn.RateLimitedUntil(retryAt))
            .On(Cdn + "u.jpg", BooruCdn.ServiceUnavailable(7)).On(Cdn + "n.jpg", BooruCdn.ServiceUnavailable(null))
            .On(Cdn + "x.jpg", BooruCdn.ThrottledWithInvalidRetryAfter(HttpStatusCode.ServiceUnavailable));
        using HttpClient client = handler.Client();
        using CancellationTokenSource deadline = new(Deadline);
        using (HttpResponseMessage response = await client.GetAsync(Cdn + "s.jpg", deadline.Token))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(120), response.Headers.RetryAfter!.Delta);
        }
        using (HttpResponseMessage response = await client.GetAsync(Cdn + "d.jpg", deadline.Token))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(retryAt, response.Headers.RetryAfter!.Date);
        }
        using (HttpResponseMessage response = await client.GetAsync(Cdn + "u.jpg", deadline.Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter!.Delta);
        }
        using (HttpResponseMessage response = await client.GetAsync(Cdn + "n.jpg", deadline.Token))
            Assert.Null(response.Headers.RetryAfter);
        using (HttpResponseMessage response = await client.GetAsync(Cdn + "x.jpg", deadline.Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Null(response.Headers.RetryAfter);
            Assert.Equal("soon", Assert.Single(response.Headers.GetValues("Retry-After")));
        }
    }

    [Fact]
    public async Task OversizedBodiesAreStreamedWithOrWithoutADeclaredLength()
    {
        BooruCdnHandler handler = new();
        const long nineMiB = 9L * 1024 * 1024;
        handler.Script.On(Cdn + "declared.png", BooruCdn.Oversized(nineMiB)).On(Cdn + "chunked.png", BooruCdn.Oversized(nineMiB, declareLength: false));
        using HttpClient client = handler.Client();
        using CancellationTokenSource deadline = new(Deadline);
        foreach ((string name, bool declared) in new[] { ("declared.png", true), ("chunked.png", false) })
        {
            using HttpResponseMessage response = await client.GetAsync(Cdn + name, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Assert.Equal(declared ? (long?)nineMiB : null, response.Content.Headers.ContentLength);
            await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] first = new byte[8];
            Assert.Equal(8, await body.ReadAsync(first, deadline.Token));
            Assert.True(BooruPreviewHttp.LooksLikeImage(first));
            long total = 8;
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0) total += read;
            Assert.Equal(nineMiB, total);
        }
    }

    [Fact]
    public async Task HeldAnswersShowTheConcurrencyTheGateAllowsAndCompleteOnRelease()
    {
        BooruCdnHandler handler = new();
        TaskCompletionSource gate = BooruCdn.NewGate();
        for (int index = 0; index < 4; index++) handler.Script.On($"{Cdn}held{index}.jpg", BooruCdn.Held(gate, BooruCdn.Jpeg()));
        using HttpClient transport = handler.Client();
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        try
        {
            Task<byte[]?>[] loads = Enumerable.Range(0, 4).Select(index => previews.LoadAsync(new($"{Cdn}held{index}.jpg"), deadline.Token)).ToArray();
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Script.InFlight);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Script.MaxInFlight);
            Assert.All(loads, load => Assert.False(load.IsCompleted));
            gate.TrySetResult();
            Assert.All(await Task.WhenAll(loads).WaitAsync(deadline.Token), bytes => Assert.Equal(BooruTinyImages.Jpeg(), bytes));
            Assert.Equal(0, handler.Script.InFlight);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Script.MaxInFlight);
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task ARequestOutsideTheScriptedHostOrWithCredentialsIsAViolation()
    {
        BooruCdnHandler handler = new();
        using HttpClient client = handler.Client();
        using HttpRequestMessage post = new(HttpMethod.Post, Cdn + "x.jpg");
        using HttpRequestMessage credentialed = new(HttpMethod.Get, Cdn + "y.jpg");
        credentialed.Headers.TryAddWithoutValidation("Authorization", "Basic eDp5");
        credentialed.Headers.TryAddWithoutValidation("Cookie", "a=b");
        using HttpResponseMessage first = await client.SendAsync(post);
        using HttpResponseMessage second = await client.SendAsync(credentialed);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid/z.jpg"));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // no rule: the 404 fallback
        string[] violations = handler.Script.Violations.ToArray();
        Assert.Contains(violations, text => text.StartsWith("method POST", StringComparison.Ordinal));
        Assert.Contains(violations, text => text.StartsWith("authorization", StringComparison.Ordinal));
        Assert.Contains(violations, text => text.StartsWith("cookie", StringComparison.Ordinal));
        Assert.Contains(violations, text => text.StartsWith("host", StringComparison.Ordinal));
    }

    private static SourceDescription Source() => new("danbooru", "Danbooru", new("https://danbooru.donmai.us/"),
        SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
        [new("danbooru.donmai.us", HttpHostPurpose.Metadata), new("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)],
        new("1", []));
}
