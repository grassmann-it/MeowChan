using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using SkiaSharp;

namespace MeowChan.DesktopTests;

// Pins the fixtures and fakes of the Booru browse-to-download pipeline (package B2): later packages (host fetch path,
// ports, grid, viewer, smokes) test against these, so what each name promises must hold. No request, no window.
public sealed class BooruBrowseFixturesTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("Image", true, true, "Available")]
    [InlineData("SampleIsOriginal", false, true, "Available")]
    [InlineData("NoSample", false, true, "Available")]
    [InlineData("Gif", false, true, "Available")]
    [InlineData("Video", true, true, "Available")]
    [InlineData("Ugoira", true, true, "Available")]
    [InlineData("Deleted", false, true, "Restricted")]
    [InlineData("Banned", false, false, "Restricted")]
    [InlineData("MissingFile", false, false, "Restricted")]
    public void EveryVariantKindOffersExactlyTheMediaItsNamePromises(string kindName, bool sample, bool preview, string accessName)
    {
        BooruFixtureKind kind = Enum.Parse<BooruFixtureKind>(kindName);
        BooruOriginalAccess access = Enum.Parse<BooruOriginalAccess>(accessName);
        BooruPostView post = BooruUiFixtures.Variant("9100", kind);
        Assert.Equal(sample, post.Post.Media!.Any(variant => variant.Role == BooruMediaRole.Sample && variant.Url is not null));
        Assert.Equal(preview, post.PreviewUrl is not null);
        Assert.Equal(access, post.OriginalAccess);
        Assert.Equal(kind == BooruFixtureKind.Deleted, post.IsDeleted);
        Assert.Equal(kind == BooruFixtureKind.Banned, post.IsBanned);
        // Every address is on the CDN host and every variant of a withheld original keeps its claim without an address.
        Assert.All(post.Post.Media!, variant => Assert.True(variant.Url is null || variant.Url.Host == "cdn.donmai.us"));
        Assert.NotNull(post.Original);
        Assert.Equal(access == BooruOriginalAccess.Available, post.Original!.Url is not null);
    }

    [Fact]
    public void VideoAndUgoiraSamplesAreNotImagesWhileTheirPreviewIsAStill()
    {
        foreach ((BooruFixtureKind kind, string sampleExtension, string originalExtension) in new[]
            { (BooruFixtureKind.Video, ".mp4", ".mp4"), (BooruFixtureKind.Ugoira, ".webm", ".zip") })
        {
            BooruPostView post = BooruUiFixtures.Variant("9101", kind);
            Assert.EndsWith(sampleExtension, post.Post.Media!.Single(variant => variant.Role == BooruMediaRole.Sample).Url!.AbsolutePath);
            Assert.Equal(originalExtension, post.Original!.Extension);
            Assert.EndsWith(".jpg", post.PreviewUrl!.AbsolutePath);
        }
    }

    [Fact]
    public void GeneratedImagesDecodeAtTheirSizeAndAreDeterministic()
    {
        Assert.Equal(BooruUiFixtures.Png(3), BooruUiFixtures.Png(3));
        Assert.NotEqual(BooruUiFixtures.Png(3), BooruUiFixtures.Png(4));
        Assert.Equal(BooruUiFixtures.Sample(3), BooruUiFixtures.Sample(3));
        foreach ((byte[] bytes, SKEncodedImageFormat format, int width, int height) in new[]
        {
            (BooruUiFixtures.Png(3), SKEncodedImageFormat.Png, 96, 96),
            (BooruUiFixtures.Jpeg(3), SKEncodedImageFormat.Jpeg, 96, 96),
            (BooruUiFixtures.WebP(3), SKEncodedImageFormat.Webp, 96, 96),
            (BooruUiFixtures.Sample(3), SKEncodedImageFormat.Png, 640, 360),
            (BooruUiFixtures.Jpeg(3, 800, 450), SKEncodedImageFormat.Jpeg, 800, 450)
        })
        {
            using SKCodec codec = SKCodec.Create(new MemoryStream(bytes))!;
            Assert.Equal(format, codec.EncodedFormat);
            using SKBitmap bitmap = SKBitmap.Decode(bytes);
            Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
            Assert.InRange(bytes.Length, 100, 64 * 1024);
        }
    }

    [Fact]
    public async Task FullPagesServeTwentyHitsAndAFollowingPageInTheShapesOfTheJsonFixtures()
    {
        BooruUiHarness harness = new();
        harness.ServeFullPages(pages: 2);
        using CancellationTokenSource cancellation = new(Deadline);
        BooruResultPage first = (await harness.Search.SearchAsync("cat", 1, cancellation.Token)).Value!;
        BooruResultPage second = (await harness.Search.SearchAsync("cat", 2, cancellation.Token)).Value!;
        Assert.Equal(20, first.Posts.Length);
        Assert.Equal("9220", first.Posts[0].PostId);
        Assert.Equal("9201", first.Posts[^1].PostId);
        Assert.False(first.IsEnd);
        Assert.Equal("9200", second.Posts[0].PostId);
        Assert.Equal("9181", second.Posts[^1].PostId);
        Assert.True(second.IsEnd);
        Assert.Empty(first.Posts.Select(post => post.PostId).Intersect(second.Posts.Select(post => post.PostId)));
        // Mixed shapes: with and without a sample, and a provider rating other than general.
        Assert.Contains(first.Posts, post => post.Post.Media!.Any(variant => variant.Role == BooruMediaRole.Sample));
        Assert.Contains(first.Posts, post => post.Post.Media!.All(variant => variant.Role != BooruMediaRole.Sample));
        Assert.Contains(first.Posts, post => post.Rating == BooruRating.Sensitive);
        Assert.Equal(0, harness.Previews.Requests + harness.Samples.Requests);
    }

    [Fact]
    public async Task SamplePortServesSamplesForPostsThatHaveOneAndNothingForThoseThatDoNot()
    {
        BooruUiHarness harness = new();
        using CancellationTokenSource cancellation = new(Deadline);
        byte[]? sample = await harness.Samples.LoadSampleAsync(BooruUiFixtures.Variant("9200", BooruFixtureKind.Image), cancellation.Token);
        using SKBitmap decoded = SKBitmap.Decode(sample!);
        Assert.Equal((640, 360), (decoded.Width, decoded.Height));
        Assert.Null(await harness.Samples.LoadSampleAsync(BooruUiFixtures.Variant("9201", BooruFixtureKind.SampleIsOriginal), cancellation.Token));
        Assert.Null(await harness.Samples.LoadSampleAsync(BooruUiFixtures.Variant("9202", BooruFixtureKind.Banned), cancellation.Token));
        Assert.Equal(new[] { "9200" }, harness.Samples.Loaded);
        Assert.Equal(new[] { "9201", "9202" }, harness.Samples.WithoutSample);
        Assert.Equal(new[] { ("9200", BooruImageRole.Sample) }, harness.Cdn.Requests);

        harness.Samples.OnSample = _ => new byte[] { 1, 2, 3 };
        Assert.Equal(new byte[] { 1, 2, 3 }, await harness.Samples.LoadSampleAsync(BooruUiFixtures.Variant("9203", BooruFixtureKind.Image), cancellation.Token));
    }

    [Fact]
    public async Task ARateLimitFencesAllImageRequestsForAtLeastSixtySecondsAndTheRetryAfterWhenLonger()
    {
        BooruUiHarness harness = new();
        using CancellationTokenSource cancellation = new(Deadline);
        BooruPostView a = BooruUiFixtures.Variant("9300", BooruFixtureKind.Image);
        BooruPostView b = BooruUiFixtures.Variant("9301", BooruFixtureKind.Image);
        harness.Cdn.Script("9300", BooruImageRole.Sample, FakeCdnOutcome.RateLimited(TimeSpan.FromSeconds(300)));

        Assert.Null(await harness.Samples.LoadSampleAsync(a, cancellation.Token));
        Assert.Equal(harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(300), harness.Cdn.FenceUntil);
        // Everything is refused without reaching the CDN: another sample and a preview alike.
        Assert.Null(await harness.Samples.LoadSampleAsync(b, cancellation.Token));
        Assert.Null(await harness.Previews.LoadPreviewAsync(b, cancellation.Token));
        Assert.Equal(new[] { ("9301", BooruImageRole.Sample), ("9301", BooruImageRole.Preview) }, harness.Cdn.Fenced);
        Assert.Single(harness.Cdn.Requests);

        harness.Clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(await harness.Previews.LoadPreviewAsync(b, cancellation.Token));
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await harness.Previews.LoadPreviewAsync(b, cancellation.Token));
        Assert.NotNull(await harness.Samples.LoadSampleAsync(b, cancellation.Token));
    }

    [Theory]
    [InlineData("RateLimited", null, 60)]
    [InlineData("RateLimited", 10, 60)]
    [InlineData("ServiceUnavailable", null, 60)]
    [InlineData("ServiceUnavailable", 90, 90)]
    public async Task TheFenceFloorIsSixtySecondsForThrottlingAndServiceUnavailable(string kindName, int? retryAfterSeconds, int fenceSeconds)
    {
        FakeCdnOutcomeKind kind = Enum.Parse<FakeCdnOutcomeKind>(kindName);
        BooruUiHarness harness = new();
        TimeSpan? retryAfter = retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        harness.Cdn.ScriptAll(BooruImageRole.Preview, kind == FakeCdnOutcomeKind.RateLimited
            ? FakeCdnOutcome.RateLimited(retryAfter) : FakeCdnOutcome.ServiceUnavailable(retryAfter));
        Assert.Null(await harness.Previews.LoadPreviewAsync(BooruUiFixtures.Post("9400"), CancellationToken.None));
        Assert.Equal(harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(fenceSeconds), harness.Cdn.FenceUntil);
    }

    [Fact]
    public async Task OtherFailuresAreNoBytesWithoutAFenceAndAreRecorded()
    {
        BooruUiHarness harness = new();
        FakeCdnOutcome[] failures = { FakeCdnOutcome.Missing(), FakeCdnOutcome.Oversized(), FakeCdnOutcome.WrongContentType(), FakeCdnOutcome.Redirect() };
        for (int index = 0; index < failures.Length; index++)
            harness.Cdn.Script((9500 + index).ToString(), BooruImageRole.Sample, failures[index]);
        for (int index = 0; index < failures.Length; index++)
            Assert.Null(await harness.Samples.LoadSampleAsync(BooruUiFixtures.Variant((9500 + index).ToString(), BooruFixtureKind.Image), CancellationToken.None));
        Assert.Null(harness.Cdn.FenceUntil);
        Assert.Equal(failures.Select(failure => failure.Kind), harness.Cdn.Answers.Select(answer => answer.Kind));
        Assert.Empty(harness.Cdn.Fenced);
    }

    [Fact]
    public async Task AHeldSampleWaitsForItsGateAndCancellationEndsTheWaitWithTheCallersCancellation()
    {
        BooruUiHarness harness = new();
        BooruPostView post = BooruUiFixtures.Variant("9600", BooruFixtureKind.Image);
        TaskCompletionSource gate = harness.Cdn.Hold("9600", BooruImageRole.Sample);
        using CancellationTokenSource released = new(Deadline);
        Task<byte[]?> load = harness.Samples.LoadSampleAsync(post, released.Token);
        Assert.False(load.IsCompleted);
        Assert.Equal(1, harness.Cdn.MaxInFlight);
        gate.TrySetResult();
        Assert.NotNull(await load.WaitAsync(Deadline));

        harness.Cdn.Hold("9600", BooruImageRole.Sample);
        using CancellationTokenSource cancelled = new();
        Task<byte[]?> second = harness.Samples.LoadSampleAsync(post, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task TheCompositionStubServesADistinctSampleAndLetsAScriptAnswerSingleCdnAddresses()
    {
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new DanbooruStubPost(9700));
        DanbooruStubPost throttled = stub.Add(new DanbooruStubPost(9701));
        stub.Cdn.On(throttled.PreviewUrl, BooruCdn.RateLimited(90)).On(throttled.SampleUrl, BooruCdn.ServiceUnavailable(30));
        using HttpClient client = stub.Client();
        using CancellationTokenSource cancellation = new(Deadline);

        Assert.Equal(post.Preview, await client.GetByteArrayAsync(post.PreviewUrl, cancellation.Token));
        byte[] sample = await client.GetByteArrayAsync(post.SampleUrl, cancellation.Token);
        Assert.Equal(post.Sample, sample);
        Assert.NotEqual(post.Preview, sample);
        using SKBitmap decoded = SKBitmap.Decode(sample);
        Assert.Equal((240, 180), (decoded.Width, decoded.Height));

        using HttpResponseMessage preview = await client.GetAsync(throttled.PreviewUrl, cancellation.Token);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, preview.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(90), preview.Headers.RetryAfter!.Delta);
        using HttpResponseMessage scripted = await client.GetAsync(throttled.SampleUrl, cancellation.Token);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, scripted.StatusCode);
        Assert.Empty(stub.Violations);
        Assert.Equal(2, stub.SampleRequests);
    }

    // The two BooruUiFakes copies (DesktopTests and DesktopSmoke) are separate files; everything the browse pipeline
    // added must stay identical in both so a smoke scene and a unit test see the same fixtures and fakes.
    [Fact]
    public void BothFakeCopiesCarryIdenticalBrowseFixturesFakesAndHarness()
    {
        string root = DanbooruTestPlugin.RepositoryRoot();
        string tests = File.ReadAllText(Path.Combine(root, "tests", "MeowChan.DesktopTests", "BooruUiFakes.cs")).ReplaceLineEndings("\n");
        string smoke = File.ReadAllText(Path.Combine(root, "tests", "MeowChan.DesktopSmoke", "BooruUiFakes.cs")).ReplaceLineEndings("\n");
        foreach ((string start, string end) in new[]
        {
            ("internal enum BooruFixtureKind", "internal sealed class FakeBooruSearchPort"),
            ("internal sealed class FakeBooruPreviewPort", "/// <summary>\n/// In-memory watcher scheduler stand-in"),
            ("/// <summary>The four fakes and the clock", null!)
        })
            Assert.Equal(Region(tests, start, end), Region(smoke, start, end));
        Assert.Contains("FakeSamplePort", tests);
    }

    private static string Region(string text, string start, string? end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = end is null ? text.Length : text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }
}
