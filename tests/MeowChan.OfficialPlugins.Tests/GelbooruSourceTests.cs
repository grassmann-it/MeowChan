using System.Text;
using MeowChan.OfficialSources.Gelbooru;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialPlugins.Tests;

// Offline tests: a recording fake host answers with canned responses; no test contacts a booru site.
public sealed class GelbooruSourceTests
{
    private static readonly GelbooruPlugin Plugin = new();
    private static readonly SourceDescription Gelbooru = Plugin.Describe().Sources.Single(s => s.SourceId == "gelbooru");
    private static readonly SourceDescription Safebooru = Plugin.Describe().Sources.Single(s => s.SourceId == "safebooru");
    private const string GelbooruBase = "https://gelbooru.com/index.php?page=dapi&s=post&q=index&json=1";

    // ---- description -------------------------------------------------------------------------------

    [Fact]
    public void DescriptionValidatesAndDeclaresTwoDecimalOrderedPostSearchSources()
    {
        PluginDescription description = Plugin.Describe();
        Assert.Empty(SdkValidation.ValidateDescriptions(new[] { description }));
        Assert.Equal("meowchan.official.gelbooru", description.PluginId);
        Assert.Equal(SdkContract.Version, description.SdkVersion);
        Assert.Empty(description.Settings.Fields);
        Assert.Equal(new[] { "gelbooru", "safebooru" }, description.Sources.Select(s => s.SourceId));
        foreach (SourceDescription source in description.Sources)
        {
            Assert.Equal(SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, source.Capabilities);
            Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
            Assert.Equal(SourceMaturity.Experimental, source.Maturity);
            Assert.Empty(source.Routes);
        }
        Assert.Equal(new Uri("https://gelbooru.com/"), Gelbooru.BaseUri);
        Assert.Equal(new Uri("https://safebooru.org/"), Safebooru.BaseUri);
        Assert.Contains(new HttpHostDeclaration("gelbooru.com", HttpHostPurpose.Metadata), Gelbooru.HttpHosts);
        Assert.Contains(new HttpHostDeclaration("img3.gelbooru.com", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia), Gelbooru.HttpHosts);
        Assert.Equal(new HttpHostDeclaration("safebooru.org", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia), Assert.Single(Safebooru.HttpHosts));
        Assert.Equal(new GelbooruPlugin().Describe(), description);
    }

    [Fact]
    public void LimitSettingsHaveDefaultMinimumAndMaximumAndCredentialsAreOptional()
    {
        foreach (SourceDescription source in new[] { Gelbooru, Safebooru })
        {
            Dictionary<string, SettingDefinition> fields = source.Settings.Fields.ToDictionary(f => f.Key);
            Assert.False(fields["user_id"].IsRequired);
            Assert.False(fields["api_key"].IsRequired);
            SettingDefinition tags = fields["maximum_tags_per_post"];
            Assert.Equal((64L, 16384L, 4096L), (tags.Minimum!.Value, tags.Maximum!.Value, tags.Default!.Integer!.Value));
            SettingDefinition size = fields["maximum_response_kib"];
            Assert.Equal((64L, 8192L, 8192L), (size.Minimum!.Value, size.Maximum!.Value, size.Default!.Integer!.Value));
        }
    }

    [Fact]
    public void AssemblyExportsExactlyOneBooruPluginWithoutCoreOrUiReferences()
    {
        Type type = Assert.Single(typeof(GelbooruPlugin).Assembly.GetExportedTypes(), candidate => typeof(IPlugin).IsAssignableFrom(candidate));
        Assert.Equal(typeof(GelbooruPlugin), type);
        Assert.True(typeof(IBooruSourcePlugin).IsAssignableFrom(type));
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), reference => reference.Name == "libmeowchan" || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    // ---- request shape -----------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("cat_ears -dog", "cat_ears%20-dog")]
    [InlineData("rating:general", "rating%3Ageneral")]
    [InlineData("a&b#c", "a%26b%23c")]
    public async Task SearchRequestIsOneExactGetWithZeroBasedPidAndTheQueryEncodedOnce(string query, string encoded)
    {
        (BooruSearchResult result, RecordingHttp http) = await Search("gelbooru", Json("{\"@attributes\":{\"limit\":40,\"offset\":0,\"count\":0}}"), query, page: 3, limit: 40);
        Assert.IsType<BooruSearchSnapshot>(result);
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"{GelbooruBase}&tags={encoded}&pid=2&limit=40", request.Url.AbsoluteUri);
        Assert.Equal("gelbooru", request.SourceId);
        Assert.Equal(HostHttpMethod.Get, request.Method);
        Assert.Equal("application/json", Assert.Single(request.Headers!).Value);
        Assert.Empty(SdkValidation.ValidateHttpRequest(request, Gelbooru));
    }

    [Fact]
    public async Task CursorRequestAddsTheIdMetaTagAndUsesPidZero()
    {
        (BooruSearchResult result, RecordingHttp http) = await Search("gelbooru", Json(Fixture("search-cursor.json")), "cat", 1, 2, "1000");
        BooruSearchSnapshot snapshot = Assert.IsType<BooruSearchSnapshot>(result);
        Assert.Equal($"{GelbooruBase}&tags=cat%20id%3A%3C1000&pid=0&limit=2", Assert.Single(http.Requests).Url.AbsoluteUri);
        Assert.Equal(new[] { "1000", "999" }, snapshot.Page.Posts.Select(p => p.Id.PostId));
        Assert.False(snapshot.Page.IsEnd);
        Assert.Equal("999", snapshot.Page.NextBeforePostId);

        (BooruSearchResult emptyQuery, RecordingHttp http2) = await Search("gelbooru", Json(Fixture("search-empty.json")), "", 1, 20, "50");
        Assert.IsType<BooruSearchSnapshot>(emptyQuery);
        Assert.Equal($"{GelbooruBase}&tags=id%3A%3C50&pid=0&limit=20", Assert.Single(http2.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task SafebooruUsesItsOwnHostAndSourceId()
    {
        (BooruSearchResult _, RecordingHttp http) = await Search("safebooru", Json(Fixture("safebooru-search.json")), "cat", 1, 20);
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.StartsWith("https://safebooru.org/index.php?page=dapi&s=post&q=index&json=1&tags=cat&pid=0&limit=20", request.Url.AbsoluteUri);
        Assert.Equal("safebooru", request.SourceId);
    }

    [Fact]
    public async Task PostRequestIsOneExactGetById()
    {
        (BooruPostResult _, RecordingHttp http) = await Read(Json("{\"@attributes\":{\"limit\":100,\"offset\":0,\"count\":1},\"post\":[{\"id\":12345}]}"), new BooruPostId("gelbooru", "12345"));
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"{GelbooruBase}&id=12345", request.Url.AbsoluteUri);
        Assert.Empty(SdkValidation.ValidateHttpRequest(request, Gelbooru));
    }

    [Fact]
    public async Task InvalidRequestsNeverReachTheHost()
    {
        RecordingHttp http = Returning(Json("[]"));
        BooruSearchRequest[] requests =
        {
            new("danbooru", "cat", 1, 20), new("gelbooru", "cat", 0, 20), new("gelbooru", "cat", 1, 101), new("gelbooru", "cat", 1, 20, "01"),
            new("gelbooru", "cat", 2, 20, "5"), new("gelbooru", "cat", 1, 20, "1234567890123456789")
        };
        foreach (BooruSearchRequest request in requests)
            Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(request, Context(http)));
        Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("other", "7")), Context(http)));
        Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("gelbooru", "0")), Context(http)));
        Assert.Empty(http.Requests);
    }

    // ---- credentials -------------------------------------------------------------------------------

    [Fact]
    public async Task CredentialsAreSentOnlyWhenBothAreSetAndNeverAppearInResults()
    {
        RecordingHttp http = Returning(Json("{\"@attributes\":{\"limit\":20,\"offset\":0,\"count\":0}}", status: 403));
        PluginCallContext context = Context(http, "gelbooru", ("user_id", "424242"), ("api_key", "abcDEF0123"));
        BooruSearchResult result = await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 20), context);
        Assert.EndsWith("&api_key=abcDEF0123&user_id=424242", Assert.Single(http.Requests).Url.AbsoluteUri);
        FailureDetail failure = Assert.IsType<BooruSearchFailure>(result).Failure;
        Assert.Equal("access-denied", failure.Code);
        Assert.DoesNotContain("abcDEF0123", failure.Message);
        Assert.DoesNotContain("424242", failure.Message);
        Assert.DoesNotContain("cat", failure.Message);
    }

    [Fact]
    public async Task OneCredentialWithoutTheOtherIsRefusedBeforeAnyRequest()
    {
        RecordingHttp http = Returning(Json("[]"));
        PluginCallContext context = Context(http, "gelbooru", ("api_key", "abcDEF0123"));
        Assert.Equal("credentials-incomplete", Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 20), context)).Failure.Code);
        Assert.Equal("credentials-incomplete", Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("gelbooru", "7")), context)).Failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task SettingsValidatorChecksShapeAndPairing()
    {
        static SettingsSnapshot Snapshot(params (string Key, string Value)[] values)
            => new("1", values.Select(v => new SettingValue(v.Key, SettingKind.Text, v.Value)).ToArray());
        PluginCallContext context = Context(Returning(Json("[]")));
        async Task<string[]> Codes(SettingsSnapshot snapshot)
            => (await Plugin.ValidateSettingsAsync(new SettingsValidationRequest(SettingScope.Source, snapshot, "gelbooru"), context)).Select(e => e.Code).ToArray();
        Assert.Empty(await Codes(Snapshot()));
        Assert.Empty(await Codes(Snapshot(("user_id", "12"), ("api_key", "abc123"))));
        Assert.Equal(new[] { "credentials-incomplete" }, await Codes(Snapshot(("user_id", "12"))));
        Assert.Contains("invalid-user-id", await Codes(Snapshot(("user_id", "x1"), ("api_key", "abc"))));
        Assert.Contains("invalid-api-key", await Codes(Snapshot(("user_id", "1"), ("api_key", "a&b"))));
    }

    // ---- parsing -----------------------------------------------------------------------------------

    [Fact]
    public async Task NormalSearchMapsPostsRatingsTagsAndMedia()
    {
        (BooruSearchResult result, RecordingHttp _) = await Search("gelbooru", Json(Fixture("search-normal.json")), "cat", 1, 3);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(new[] { "9003", "9002", "9001" }, page.Posts.Select(p => p.Id.PostId));
        Assert.False(page.IsEnd);
        Assert.Equal("9001", page.NextBeforePostId);

        BooruPost first = page.Posts[0];
        Assert.Equal(new Uri("https://gelbooru.com/index.php?page=post&s=view&id=9003"), first.PostUrl);
        Assert.Equal(BooruRating.General, first.Rating);
        Assert.Equal(12, first.Score);
        Assert.Equal(new DateTimeOffset(2025, 11, 20, 12, 30, 5, TimeSpan.FromHours(-6)), first.CreatedAt);
        Assert.Equal(new[] { "1girl", "blue_eyes", "tom_&_jerry", "d'artagnan", "solo" }, first.Tags!.Select(t => t.Name));
        Assert.All(first.Tags!, tag => Assert.Equal(BooruTagCategory.Unknown, tag.Category));
        Assert.Equal("https://example.invalid/art/1", first.SourceClaim);
        Assert.False(first.IsDeleted);
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Sample, BooruMediaRole.Preview }, first.Media!.Select(m => m.Role));
        BooruMediaVariant original = first.Media![0];
        Assert.Equal(new Uri("https://img3.gelbooru.com/images/11/11/11111111111111111111111111111111.jpg"), original.Url);
        Assert.Equal(".jpg", original.Extension);
        Assert.Equal((1200, 1800), (original.Width, original.Height));
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString("11111111111111111111111111111111")), original.ExpectedMd5);

        BooruPost second = page.Posts[1];
        Assert.Equal(BooruRating.Sensitive, second.Rating);
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Preview }, second.Media!.Select(m => m.Role)); // sample == original is no sample

        BooruPost third = page.Posts[2];
        Assert.Equal(BooruRating.Explicit, third.Rating);
        Assert.True(third.IsDeleted);
        Assert.Equal(-3, third.Score);
        Assert.Equal(TimeSpan.Zero, third.CreatedAt!.Value.Offset);
        Assert.Null(third.Media![0].Width);
        Assert.Equal(".webm", third.Media![0].Extension);
        Assert.Equal("video-cdn3.gelbooru.com", third.Media![0].Url!.Host);
    }

    [Fact]
    public async Task EmptyListingsAreEmptyPagesIncludingAnEmptyBody()
    {
        foreach (string body in new[] { Fixture("search-empty.json"), "[]", "", "  \n" })
        {
            BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>((await Search("gelbooru", Json(body), "cat", 1, 20)).Result).Page;
            Assert.Empty(page.Posts);
            Assert.True(page.IsEnd);
            Assert.Null(page.NextBeforePostId);
        }
    }

    [Fact]
    public async Task ProviderPageCapPreventsAFalseEnd()
    {
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>((await Search("gelbooru", Json(Fixture("search-capped.json")), "cat", 1, 5)).Result).Page;
        Assert.False(page.IsEnd);
        Assert.Equal("29", page.NextBeforePostId);
        BooruSearchPage shortPage = Assert.IsType<BooruSearchSnapshot>((await Search("gelbooru", Json(Fixture("search-capped.json")), "cat", 1, 3)).Result).Page;
        Assert.False(shortPage.IsEnd);
        BooruSearchPage end = Assert.IsType<BooruSearchSnapshot>((await Search("safebooru", Json(Fixture("safebooru-search.json")), "cat", 1, 20)).Result).Page;
        Assert.True(end.IsEnd);
        Assert.Null(end.NextBeforePostId);
    }

    [Fact]
    public async Task SafebooruArrayAssemblesAddressesFromValidatedParts()
    {
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>((await Search("safebooru", Json(Fixture("safebooru-search.json")), "cat", 1, 20)).Result).Page;
        Assert.Equal(new[] { "4002", "4001" }, page.Posts.Select(p => p.Id.PostId));
        BooruPost first = page.Posts[0];
        Assert.Equal(BooruRating.General, first.Rating);
        Assert.Equal(new Uri("https://safebooru.org/images/1234/55555555555555555555555555555555.jpg"), first.Media![0].Url);
        Assert.Equal(new Uri("https://safebooru.org/samples/1234/sample_55555555555555555555555555555555.jpg"), first.Media![1].Url);
        Assert.Equal(new Uri("https://safebooru.org/thumbnails/1234/thumbnail_55555555555555555555555555555555.jpg"), first.Media![2].Url);
        Assert.False(first.Fields.HasFlag(BooruFieldPresence.CreatedAt));
        BooruPost second = page.Posts[1];
        Assert.Equal(BooruRating.General, second.Rating); // "safe"
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Preview }, second.Media!.Select(m => m.Role));

        BooruSearchPage bad = Assert.IsType<BooruSearchSnapshot>((await Search("safebooru", Json(Fixture("safebooru-bad-directory.json")), "cat", 1, 20)).Result).Page;
        Assert.Null(bad.Posts[0].Media![0].Url);
    }

    [Fact]
    public async Task SinglePostObjectToleratesNumericStringsAndDropsOffHostAddresses()
    {
        BooruPost post = Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture("post-single-object.json")), new BooruPostId("gelbooru", "7"))).Result).Post;
        Assert.Equal(BooruRating.Sensitive, post.Rating);
        Assert.Equal(4, post.Score);
        BooruMediaVariant original = post.Media![0];
        Assert.Null(original.Url);
        Assert.Equal((640, 480), (original.Width, original.Height));
        Assert.Null(post.Media![1].Url);
    }

    [Fact]
    public async Task UnknownPostIsVerifiedAbsenceAndAnotherPostIsAFailure()
    {
        Assert.IsType<BooruPostNotFound>((await Read(Json(Fixture("search-empty.json")), new BooruPostId("gelbooru", "7"))).Result);
        Assert.IsType<BooruPostNotFound>((await Read(Json("", contentType: "text/html"), new BooruPostId("safebooru", "7"))).Result);
        Assert.IsType<BooruPostNotFound>((await Read(Json("{}", status: 404), new BooruPostId("gelbooru", "7"))).Result);
        Assert.Equal("wrong-post", Assert.IsType<BooruPostFailure>((await Read(Json("{\"@attributes\":{},\"post\":[{\"id\":8}]}"), new BooruPostId("gelbooru", "7"))).Result).Failure.Code);
    }

    // ---- failures ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("html-challenge.html", "text/html", "not-json")]
    [InlineData("html-challenge.html", "application/json", "malformed")]
    [InlineData("malformed-json.json", "application/json", "malformed")]
    [InlineData("wrong-shape.json", "application/json", "malformed")]
    [InlineData("too-many-posts.json", "application/json", "too-many-posts")]
    [InlineData("post-bad-md5.json", "application/json", "malformed")]
    [InlineData("search-unordered.json", "application/json", "unexpected-order")]
    public async Task UnusableResponsesAreFailuresWithStableCodes(string fixture, string contentType, string code)
    {
        RecordingHttp http = Returning(Json(Fixture(fixture), contentType: contentType));
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "secret_query", 1, 3), Context(http)));
        Assert.Equal(code, failure.Failure.Code);
        Assert.DoesNotContain("secret_query", failure.Failure.Message);
    }

    [Fact]
    public async Task JsonLabelledAsHtmlIsAcceptedOnlyWhenItStartsLikeJson()
    {
        Assert.IsType<BooruSearchSnapshot>((await Search("gelbooru", Json(Fixture("search-empty.json"), contentType: "text/html; charset=UTF-8"), "cat", 1, 20)).Result);
    }

    [Fact]
    public async Task ConfiguredLimitsAreEnforced()
    {
        RecordingHttp http = Returning(Json(Fixture("search-normal.json")));
        PluginCallContext tags = Context(http, "gelbooru", maximumTags: 64);
        Assert.IsType<BooruSearchSnapshot>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 3), tags));
        string many = "{\"id\":5,\"tags\":\"" + string.Join(' ', Enumerable.Range(0, 65).Select(i => "t" + i)) + "\"}";
        RecordingHttp bigTags = Returning(Json("[" + many + "]"));
        Assert.Equal("too-many-tags", Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 3), Context(bigTags, "gelbooru", maximumTags: 64))).Failure.Code);

        RecordingHttp small = Returning(Json(new string(' ', 70 * 1024) + "[]"));
        Assert.Equal("too-large", Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 3), Context(small, "gelbooru", maximumResponseKib: 64))).Failure.Code);
    }

    [Fact]
    public async Task HttpStatusesMapToDeferralAndFailureCodes()
    {
        foreach ((int status, string reason) in new[] { (429, "provider-throttled"), (503, "provider-unavailable"), (504, "provider-unavailable") })
        {
            BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search("gelbooru", Json("", status, headers: ("Retry-After", "30")), "cat", 1, 20)).Result);
            Assert.Equal(reason, deferred.ReasonCode);
            Assert.Equal(status, deferred.HttpStatusCode);
            Assert.InRange(deferred.RetryAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
        }
        Assert.Equal("access-denied", Assert.IsType<BooruSearchFailure>((await Search("gelbooru", Json("", 401), "cat", 1, 20)).Result).Failure.Code);
        Assert.Equal("redirect", Assert.IsType<BooruSearchFailure>((await Search("gelbooru", Json("", 302), "cat", 1, 20)).Result).Failure.Code);
        Assert.Equal("http-500", Assert.IsType<BooruSearchFailure>((await Search("gelbooru", Json("", 500), "cat", 1, 20)).Result).Failure.Code);
    }

    [Fact]
    public async Task HostDeferralAndFailureArePassedThrough()
    {
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(1);
        RecordingHttp deferred = Returning(new HostHttpDeferred(at, "host-cooldown", 429));
        Assert.Equal(at, Assert.IsType<BooruSearchDeferred>(await Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 20), Context(deferred))).RetryAt);
        RecordingHttp failing = Returning(new HostHttpFailure(new FailureDetail("net", "down")));
        Assert.Equal("net", Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("gelbooru", "7")), Context(failing))).Failure.Code);
    }

    [Fact]
    public async Task CancelledCallsThrowInsteadOfReturningAResult()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        RecordingHttp http = Returning(Json("[]"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.SearchPostsAsync(new BooruSearchRequest("gelbooru", "cat", 1, 20), Context(http), cancelled.Token));
        Assert.Empty(http.Requests);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gelbooru", name));

    private static HostHttpResponse Json(string body, int status = 200, string contentType = "application/json; charset=utf-8", (string Name, string Value)? headers = null)
    {
        Dictionary<string, string> map = new() { ["Content-Type"] = contentType };
        if (headers is { } header) map[header.Name] = header.Value;
        return new HostHttpResponse(status, new Uri("https://gelbooru.com/index.php"), map, Encoding.UTF8.GetBytes(body));
    }

    private static RecordingHttp Returning(HostHttpResult result) => new(_ => result);

    private static SourceDescription SourceOf(string id) => id == "gelbooru" ? Gelbooru : Safebooru;

    private static async Task<(BooruSearchResult Result, RecordingHttp Http)> Search(string sourceId, HostHttpResult response, string query = "cat", int page = 1, int limit = 20, string? cursor = null)
    {
        RecordingHttp http = Returning(response);
        BooruSearchRequest request = new(sourceId, query, page, limit, cursor);
        BooruSearchResult result = await Plugin.SearchPostsAsync(request, Context(http));
        if (result is BooruSearchSnapshot) Assert.Empty(SdkValidation.ValidateBooruSearchResult(request, result, SourceOf(sourceId)));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static async Task<(BooruPostResult Result, RecordingHttp Http)> Read(HostHttpResult response, BooruPostId post)
    {
        RecordingHttp http = Returning(response);
        BooruPostRequest request = new(post);
        BooruPostResult result = await Plugin.ReadPostAsync(request, Context(http));
        if (result is BooruPostSnapshot or BooruPostNotFound) Assert.Empty(SdkValidation.ValidateBooruPostResult(request, result, SourceOf(post.SourceId)));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static PluginCallContext Context(IHostHttp http, string? sourceId = null, params (string Key, string Value)[] text)
        => Context(http, sourceId, null, null, text);

    private static PluginCallContext Context(IHostHttp http, string? sourceId, int? maximumTags = null, int? maximumResponseKib = null, params (string Key, string Value)[] text)
    {
        List<SettingValue> values = text.Select(t => new SettingValue(t.Key, SettingKind.Text, t.Value)).ToList();
        if (maximumTags is int tags) values.Add(new SettingValue("maximum_tags_per_post", SettingKind.Integer, Integer: tags));
        if (maximumResponseKib is int kib) values.Add(new SettingValue("maximum_response_kib", SettingKind.Integer, Integer: kib));
        Dictionary<string, SettingsSnapshot> sources = new();
        if (sourceId is not null) sources[sourceId] = new SettingsSnapshot("1", values);
        return new(new PluginConfiguration("test", new SettingsSnapshot("1", []), sources), http, new NoServices(), new NoServices(), new NoServices());
    }

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
