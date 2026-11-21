using System.Text;
using MeowChan.OfficialSources.E621;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialPlugins.Tests;

// Offline tests: a recording fake host answers with canned responses; no test contacts a booru site.
public sealed class E621SourceTests
{
    private static readonly E621Plugin Plugin = new();
    private static readonly PluginDescription Description = Plugin.Describe();
    private static SourceDescription SourceOf(string id) => Description.Sources.Single(source => source.SourceId == id);
    private const string Md5Hex = "000000000000000000000000008565e1";

    // ---- description -------------------------------------------------------------------------------

    [Fact]
    public void DescriptionValidatesAndDeclaresTwoBooruSourcesWithoutSettings()
    {
        Assert.Empty(SdkValidation.ValidateDescriptions(new[] { Description }));
        Assert.Equal("meowchan.official.e621", Description.PluginId);
        Assert.Equal(SdkContract.Version, Description.SdkVersion);
        Assert.Empty(Description.Filters);
        Assert.Empty(Description.Settings.Fields);
        Assert.Equal(new[] { "e621", "e926" }, Description.Sources.Select(source => source.SourceId));
        foreach ((string id, string host) in new[] { ("e621", "e621.net"), ("e926", "e926.net") })
        {
            SourceDescription source = SourceOf(id);
            Assert.Equal(new Uri($"https://{host}/"), source.BaseUri);
            Assert.Equal(SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, source.Capabilities);
            Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
            Assert.Empty(source.Routes);
            Assert.Empty(source.Settings.Fields);
            Assert.Equal(new[]
            {
                new HttpHostDeclaration(host, HttpHostPurpose.Metadata),
                new HttpHostDeclaration("static1.e621.net", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)
            }, source.HttpHosts);
        }
    }

    [Fact]
    public void DescribeIsPassiveAndRepeatable() => Assert.Equal(Description, new E621Plugin().Describe());

    [Fact]
    public void AssemblyExportsExactlyOneBooruPluginWithoutCoreOrUiReferences()
    {
        Type type = Assert.Single(typeof(E621Plugin).Assembly.GetExportedTypes(), candidate => typeof(IPlugin).IsAssignableFrom(candidate));
        Assert.Equal(typeof(E621Plugin), type);
        Assert.True(typeof(IBooruSourcePlugin).IsAssignableFrom(type));
        Assert.False(typeof(ISourcePlugin).IsAssignableFrom(type));
        Assert.Equal("MeowChan.OfficialSources.E621", type.Namespace);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), reference => reference.Name == "libmeowchan" || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    // ---- request shape -----------------------------------------------------------------------------

    [Theory]
    [InlineData("e621", "e621.net", "cat", "cat")]
    [InlineData("e926", "e926.net", "cat", "cat")]
    [InlineData("e621", "e621.net", "rating:s -dog", "rating%3As%20-dog")]
    [InlineData("e621", "e621.net", "a&b#c", "a%26b%23c")]
    [InlineData("e926", "e926.net", "", "")]
    public async Task SearchRequestIsOneExactGetOnTheSourceHost(string source, string host, string query, string encoded)
    {
        (BooruSearchResult result, RecordingHttp http) = await Search(source, Json("{\"posts\":[]}"), query, page: 3, limit: 40);
        Assert.IsType<BooruSearchSnapshot>(result);
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"https://{host}/posts.json?tags={encoded}&page=3&limit=40", request.Url.AbsoluteUri);
        Assert.Equal(source, request.SourceId);
        Assert.Equal(HostHttpMethod.Get, request.Method);
        // User-Agent and credentials are host-owned: the plugin sends Accept only.
        KeyValuePair<string, string> header = Assert.Single(request.Headers!);
        Assert.Equal("Accept", header.Key);
        Assert.Equal("application/json", header.Value);
        Assert.Null(request.Validators);
        Assert.Empty(SdkValidation.ValidateHttpRequest(request, SourceOf(source)));
    }

    [Fact]
    public async Task CursorRequestUsesTheBeforeIdPageForm()
    {
        (BooruSearchResult result, RecordingHttp http) = await Search("e621", Json(Fixture("search-cursor.json")), "cat", 1, 2, cursor: "1000");
        Assert.Equal("https://e621.net/posts.json?tags=cat&page=b1000&limit=2", Assert.Single(http.Requests).Url.AbsoluteUri);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(new[] { "999", "998" }, page.Posts.Select(post => post.Id.PostId));
        Assert.False(page.IsEnd);
        Assert.Equal("998", page.NextBeforePostId);
    }

    [Theory]
    [InlineData("e621", "e621.net")]
    [InlineData("e926", "e926.net")]
    public async Task PostRequestIsOneExactGet(string source, string host)
    {
        (BooruPostResult _, RecordingHttp http) = await Read(Json(Fixture("post-normal.json")), new BooruPostId(source, "2001"));
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"https://{host}/posts/2001.json", request.Url.AbsoluteUri);
        Assert.Equal(source, request.SourceId);
        Assert.Equal("Accept", Assert.Single(request.Headers!).Key);
    }

    [Theory]
    [InlineData("0")] [InlineData("01")] [InlineData("-1")] [InlineData("1e3")] [InlineData("../1")]
    [InlineData("1234567890123456789")] [InlineData("")]
    public async Task InvalidPostIdsFailBeforeAnyRequest(string postId)
    {
        RecordingHttp http = Returning(Json("{}"));
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("e621", postId)), Context(http)));
        Assert.Equal("post-id", failure.Failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ForeignSourcesAndInvalidCursorsFailBeforeAnyRequest()
    {
        RecordingHttp http = Returning(Json("{}"));
        Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 1, 20), Context(http)));
        Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("danbooru", "7")), Context(http)));
        BooruSearchFailure cursor = Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("e621", "cat", 1, 20, "0123"), Context(http)));
        Assert.Equal("invalid-cursor", cursor.Failure.Code);
        Assert.Empty(http.Requests);
    }

    // ---- parsing -----------------------------------------------------------------------------------

    [Fact]
    public async Task SearchParsesPostsInDescendingOrder()
    {
        (BooruSearchResult result, _) = await Search("e621", Json(Fixture("search-normal.json")), limit: 3);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(new[] { "3003", "3002", "3001" }, page.Posts.Select(post => post.Id.PostId));
        Assert.All(page.Posts, post => Assert.Equal("e621", post.Id.SourceId));
        // A full page continues below its lowest id; fewer posts than requested end the pass.
        Assert.False(page.IsEnd);
        Assert.Equal("3001", page.NextBeforePostId);
        Assert.Equal(new[] { BooruRating.Explicit, BooruRating.Questionable, BooruRating.General }, page.Posts.Select(post => post.Rating));
        BooruMediaVariant[] noSample = page.Posts[1].Media!.ToArray();
        Assert.DoesNotContain(noSample, variant => variant.Role == BooruMediaRole.Sample);
        BooruSearchPage shortPage = Assert.IsType<BooruSearchSnapshot>((await Search("e621", Json(Fixture("search-normal.json")), limit: 20)).Result).Page;
        Assert.True(shortPage.IsEnd);
        Assert.Null(shortPage.NextBeforePostId);
    }

    [Fact]
    public async Task EmptySearchIsAnEndPage()
    {
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>((await Search("e621", Json(Fixture("search-empty.json")))).Result).Page;
        Assert.Empty(page.Posts);
        Assert.True(page.IsEnd);
    }

    [Fact]
    public async Task E926PostsCarryTheE926SourceAndHost()
    {
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>((await Search("e926", Json(Fixture("search-e926.json")))).Result).Page;
        Assert.All(page.Posts, post =>
        {
            Assert.Equal("e926", post.Id.SourceId);
            Assert.Equal(new Uri($"https://e926.net/posts/{post.Id.PostId}"), post.PostUrl);
        });
    }

    [Fact]
    public async Task SinglePostMapsEveryFieldAsAProviderClaim()
    {
        BooruPost post = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-normal.json")), new BooruPostId("e621", "2001"))).Result).Post;
        Assert.Equal(new Uri("https://e621.net/posts/2001"), post.PostUrl);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.FromHours(-4)), post.CreatedAt);
        Assert.Equal(BooruRating.General, post.Rating);
        Assert.Null(post.RawRating);
        Assert.Equal(24, post.Score);
        Assert.Equal(5, post.FavoriteCount);
        Assert.Equal("https://example.invalid/art/2001", post.SourceClaim);
        Assert.False(post.IsDeleted);
        Assert.False(post.IsBanned);
        Assert.Equal(BooruFieldPresence.CreatedAt | BooruFieldPresence.Rating | BooruFieldPresence.Score | BooruFieldPresence.FavoriteCount
            | BooruFieldPresence.Tags | BooruFieldPresence.Media | BooruFieldPresence.SourceClaim | BooruFieldPresence.IsDeleted, post.Fields);

        // artist, copyright, character, species, general, meta, lore; "invalid" is not read.
        Assert.Equal(new[]
        {
            ("artist_one", BooruTagCategory.Artist), ("series_x", BooruTagCategory.Copyright), ("hero_a", BooruTagCategory.Character),
            ("canine", BooruTagCategory.General), ("forest", BooruTagCategory.General), ("sky", BooruTagCategory.General),
            ("hi_res", BooruTagCategory.Meta), ("lore_item", BooruTagCategory.General)
        }, post.Tags!.Select(tag => (tag.Name, tag.Category)));

        BooruMediaVariant original = post.Media!.Single(variant => variant.Role == BooruMediaRole.Original);
        Assert.Equal(new Uri($"https://static1.e621.net/data/00/00/{Md5Hex}.png"), original.Url);
        Assert.Equal(".png", original.Extension);
        Assert.Equal(125457, original.ExpectedSize);
        Assert.Equal(1200, original.Width);
        Assert.Equal(800, original.Height);
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(Md5Hex)), original.ExpectedMd5);
        Assert.Equal(new Uri($"https://static1.e621.net/data/sample/00/00/{Md5Hex}.jpg"), post.Media!.Single(variant => variant.Role == BooruMediaRole.Sample).Url);
        Assert.Equal(new Uri($"https://static1.e621.net/data/preview/00/00/{Md5Hex}.jpg"), post.Media!.Single(variant => variant.Role == BooruMediaRole.Preview).Url);
    }

    [Fact]
    public async Task RestrictedPostKeepsMetadataWithoutMediaAddresses()
    {
        BooruPost post = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-restricted.json")), new BooruPostId("e621", "2002"))).Result).Post;
        Assert.Equal(BooruRating.Explicit, post.Rating);
        BooruMediaVariant original = Assert.Single(post.Media!);
        Assert.Equal(BooruMediaRole.Original, original.Role);
        Assert.Null(original.Url);
    }

    [Fact]
    public async Task DeletedFlagIsReadFromTheFlagsObject()
    {
        BooruPost post = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-deleted.json")), new BooruPostId("e621", "2003"))).Result).Post;
        Assert.True(post.IsDeleted);
        Assert.False(post.IsBanned);
    }

    [Fact]
    public async Task MediaOutsideTheDeclaredHostIsDroppedAndUnknownFieldsAreIgnored()
    {
        BooruPost foreign = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-non-cdn-url.json")), new BooruPostId("e621", "2004"))).Result).Post;
        Assert.Null(foreign.Media!.Single(variant => variant.Role == BooruMediaRole.Original).Url);
        BooruPost unknown = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-unknown-fields.json")), new BooruPostId("e621", "2006"))).Result).Post;
        Assert.DoesNotContain(unknown.Tags!, tag => tag.Name == "x");
    }

    [Theory]
    [InlineData("post-bad-md5.json", "2005")]
    [InlineData("post-wrong-shape.json", "2007")]
    [InlineData("error-envelope.json", "7")]
    [InlineData("malformed-json.json", "7")]
    public async Task UnusableBodiesFailAsMalformed(string fixture, string id)
    {
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>((await Read(Json(Fixture(fixture)), new BooruPostId("e621", id))).Result);
        Assert.Equal("malformed", failure.Failure.Code);
    }

    [Fact]
    public async Task OrderViolationsTooManyPostsAndWrongPostFail()
    {
        Assert.Equal("unexpected-order", Assert.IsType<BooruSearchFailure>((await Search("e621", Json(Fixture("search-unordered.json")))).Result).Failure.Code);
        Assert.Equal("too-many-posts", Assert.IsType<BooruSearchFailure>((await Search("e621", Json(Fixture("search-normal.json")), limit: 2)).Result).Failure.Code);
        Assert.Equal("wrong-post", Assert.IsType<BooruPostFailure>((await Read(Json(Fixture("post-normal.json")), new BooruPostId("e621", "9"))).Result).Failure.Code);
        Assert.Equal("unexpected-order", Assert.IsType<BooruSearchFailure>((await Search("e621", Json(Fixture("search-normal.json")), limit: 3, cursor: "3002")).Result).Failure.Code);
    }

    // ---- HTTP outcomes -----------------------------------------------------------------------------

    [Theory]
    [InlineData(421, "provider-throttled")]
    [InlineData(429, "provider-throttled")]
    [InlineData(502, "provider-unavailable")]
    [InlineData(503, "provider-unavailable")]
    [InlineData(504, "provider-unavailable")]
    public async Task ThrottleAndOutageAreDeferredWithRetryAfter(int status, string reason)
    {
        BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search("e621", Json("{}", status, headers: ("Retry-After", "120")))).Result);
        Assert.Equal(reason, deferred.ReasonCode);
        Assert.Equal(status, deferred.HttpStatusCode);
        Assert.InRange(deferred.RetryAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(125));
        Assert.IsType<BooruPostDeferred>((await Read(Json("{}", status), new BooruPostId("e621", "7"))).Result);
    }

    [Fact]
    public async Task StatusAndContentMappings()
    {
        Assert.IsType<BooruPostNotFound>((await Read(Json("{}", 404), new BooruPostId("e621", "7"))).Result);
        Assert.Equal("not-found", Assert.IsType<BooruSearchFailure>((await Search("e621", Json("{}", 404))).Result).Failure.Code);
        Assert.Equal("page-limit", Assert.IsType<BooruSearchFailure>((await Search("e621", Json("{}", 410))).Result).Failure.Code);
        Assert.Equal("access-denied", Assert.IsType<BooruSearchFailure>((await Search("e621", Json("{}", 403))).Result).Failure.Code);
        Assert.Equal("redirect", Assert.IsType<BooruSearchFailure>((await Search("e621", Json("{}", 302))).Result).Failure.Code);
        Assert.Equal("http-500", Assert.IsType<BooruSearchFailure>((await Search("e621", Json("{}", 500))).Result).Failure.Code);
        Assert.Equal("not-json", Assert.IsType<BooruSearchFailure>((await Search("e621", Json(Fixture("html-challenge.html"), contentType: "text/html"))).Result).Failure.Code);
    }

    [Fact]
    public async Task FailureDetailsNeverEchoTheQuery()
    {
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>((await Search("e621", Json(Fixture("malformed-json.json")), "zzsecretqueryzz")).Result);
        Assert.DoesNotContain("zzsecretqueryzz", failure.Failure.Message);
        BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search("e621", Json("{}", 429), "zzsecretqueryzz")).Result);
        Assert.DoesNotContain("zzsecretqueryzz", deferred.ReasonCode);
    }

    [Fact]
    public async Task HostFailuresAndCancellationPropagate()
    {
        HostHttpFailure hostFailure = new(new FailureDetail("booru-budget", "x"));
        Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("e621", "cat", 1, 20), Context(Returning(hostFailure))));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        RecordingHttp http = Returning(Json("{\"posts\":[]}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.SearchPostsAsync(new BooruSearchRequest("e621", "cat", 1, 20), Context(http), cancelled.Token));
        Assert.Empty(http.Requests);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "e621", name));

    private static HostHttpResponse Json(string body, int status = 200, string contentType = "application/json; charset=utf-8", (string Name, string Value)? headers = null)
    {
        Dictionary<string, string> map = new() { ["Content-Type"] = contentType };
        if (headers is { } header) map[header.Name] = header.Value;
        return new HostHttpResponse(status, new Uri("https://e621.net/posts.json"), map, Encoding.UTF8.GetBytes(body));
    }

    private static RecordingHttp Returning(HostHttpResult result) => new(_ => result);

    private static async Task<(BooruSearchResult Result, RecordingHttp Http)> Search(string source, HostHttpResult response, string query = "cat", int page = 1, int limit = 20, string? cursor = null)
    {
        RecordingHttp http = Returning(response);
        BooruSearchRequest request = new(source, query, page, limit, cursor);
        BooruSearchResult result = await Plugin.SearchPostsAsync(request, Context(http));
        Assert.Empty(SdkValidation.ValidateBooruSearchResult(request, result, SourceOf(source)));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static async Task<(BooruPostResult Result, RecordingHttp Http)> Read(HostHttpResult response, BooruPostId post)
    {
        RecordingHttp http = Returning(response);
        BooruPostRequest request = new(post);
        BooruPostResult result = await Plugin.ReadPostAsync(request, Context(http));
        Assert.Empty(SdkValidation.ValidateBooruPostResult(request, result, SourceOf(post.SourceId)));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static PluginCallContext Context(IHostHttp http) => new(new PluginConfiguration("test", new SettingsSnapshot("1", []),
        new Dictionary<string, SettingsSnapshot>()), http, new NoServices(), new NoServices(), new NoServices());

    private sealed class RecordingHttp(Func<HostHttpRequest, HostHttpResult> respond) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = [];
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class NoServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("A booru source cannot read image assets.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("A booru source needs no local resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Unexpected logging");
    }
}
