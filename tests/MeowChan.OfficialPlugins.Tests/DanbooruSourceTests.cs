using System.Globalization;
using System.Text;
using System.Text.Json;
using MeowChan.OfficialSources.Danbooru;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialPlugins.Tests;

// Offline tests: a recording fake host answers with canned responses; no test contacts a booru site.
public sealed class DanbooruSourceTests
{
    private static readonly DanbooruPlugin Plugin = new();
    private static readonly SourceDescription Source = Plugin.Describe().Sources.Single();
    private static readonly BooruPostId Seven = new("danbooru", "7");
    private const string SearchBase = "https://danbooru.donmai.us/posts.json?tags=";

    // ---- description -------------------------------------------------------------------------------

    [Fact]
    public void DescriptionValidatesIsInertAndDeclaresTheExactBooruContract()
    {
        PluginDescription description = Plugin.Describe();
        Assert.Empty(SdkValidation.ValidateDescriptions(new[] { description }));
        Assert.Equal("meowchan.official.danbooru", description.PluginId);
        Assert.Equal("Danbooru", description.Name);
        Assert.Equal("1.0.0", description.PluginVersion);
        Assert.Equal(SdkContract.Version, description.SdkVersion);
        Assert.Equal("MeowChan", description.Author);
        Assert.Equal(new Uri("https://github.com/grassmann-it/MeowChan"), description.DocumentationUrl);
        Assert.Empty(description.Filters);
        Assert.Equal("1", description.Settings.Version);
        Assert.Empty(description.Settings.Fields);
        InformationEntry information = Assert.Single(description.Information);
        Assert.Equal(InformationKind.Text, information.Kind);
        Assert.Contains("read-only", information.Value);
        Assert.Contains("API key", information.Value);
        Assert.Contains("source tags", information.Value);
        Assert.Contains("media queue", information.Value);

        Assert.Equal("danbooru", Source.SourceId);
        Assert.Equal("Danbooru", Source.Name);
        Assert.Equal(new Uri("https://danbooru.donmai.us/"), Source.BaseUri);
        Assert.Equal(SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, Source.Capabilities);
        Assert.Equal(SourceMaturity.Experimental, Source.Maturity);
        // Danbooru lists searches and "page=b<id>" continuations by descending numeric post id.
        Assert.Equal(PostOrderKind.DecimalPostId, Source.PostOrder);
        Assert.Empty(Source.Routes);
        Assert.Equal("1", Source.Settings.Version);
        Assert.Empty(Source.Settings.Fields);
        Assert.Equal(new[]
        {
            new HttpHostDeclaration("danbooru.donmai.us", HttpHostPurpose.Metadata),
            new HttpHostDeclaration("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)
        }, Source.HttpHosts);
    }

    [Fact]
    public void DescribeIsPassiveAndRepeatable()
    {
        // Describe takes no call context, so it cannot reach the host; it must also be stable.
        Assert.Equal(Plugin.Describe(), new DanbooruPlugin().Describe());
    }

    [Fact]
    public void AssemblyExportsExactlyOneBooruPluginWithoutCoreOrUiReferences()
    {
        Type type = Assert.Single(typeof(DanbooruPlugin).Assembly.GetExportedTypes(), candidate => typeof(IPlugin).IsAssignableFrom(candidate));
        Assert.Equal(typeof(DanbooruPlugin), type);
        Assert.True(typeof(IBooruSourcePlugin).IsAssignableFrom(type));
        Assert.False(typeof(ISourcePlugin).IsAssignableFrom(type));
        Assert.Equal("MeowChan.OfficialSources.Danbooru", type.Namespace);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.DoesNotContain(type.Assembly.GetReferencedAssemblies(), reference => reference.Name == "libmeowchan" || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Contains(type.Assembly.GetReferencedAssemblies(), reference => reference.Name == "MeowChan.PluginSdk");
    }

    // ---- request shape -----------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("cat", "cat")]
    [InlineData("rating:g", "rating%3Ag")]
    [InlineData("cat_ears -dog", "cat_ears%20-dog")]
    [InlineData("-negative_tag", "-negative_tag")]
    [InlineData("ねこ", "%E3%81%AD%E3%81%93")]
    [InlineData("a&b#c", "a%26b%23c")]
    [InlineData("100% =x+y?", "100%25%20%3Dx%2By%3F")]
    public async Task SearchRequestIsOneExactGetWithTheQueryEncodedOnce(string query, string encoded)
    {
        (BooruSearchResult result, RecordingHttp http) = await Search(Json("[]"), query, page: 3, limit: 40);
        BooruSearchSnapshot snapshot = Assert.IsType<BooruSearchSnapshot>(result);
        Assert.Equal(query, snapshot.Page.Query);
        HostHttpRequest request = Assert.Single(http.Requests);
        string expected = $"{SearchBase}{encoded}&page=3&limit=40";
        Assert.Equal(expected, request.Url.AbsoluteUri);
        Assert.Equal(expected, request.Url.OriginalString);
        Assert.Equal("danbooru", request.SourceId);
        Assert.Equal(HostHttpMethod.Get, request.Method);
        KeyValuePair<string, string> header = Assert.Single(request.Headers!);
        Assert.Equal("Accept", header.Key);
        Assert.Equal("application/json", header.Value);
        Assert.Null(request.Validators);
        Assert.Empty(SdkValidation.ValidateHttpRequest(request, Source));
    }

    [Fact]
    public async Task PostRequestIsOneExactGet()
    {
        (BooruPostResult _, RecordingHttp http) = await Read(Json("{\"id\":12345}"), new BooruPostId("danbooru", "12345"));
        HostHttpRequest request = Assert.Single(http.Requests);
        Assert.Equal("https://danbooru.donmai.us/posts/12345.json", request.Url.AbsoluteUri);
        Assert.Equal("danbooru", request.SourceId);
        Assert.Equal(HostHttpMethod.Get, request.Method);
        KeyValuePair<string, string> header = Assert.Single(request.Headers!);
        Assert.Equal("Accept", header.Key);
        Assert.Equal("application/json", header.Value);
        Assert.Null(request.Validators);
        Assert.Empty(SdkValidation.ValidateHttpRequest(request, Source));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("007")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("12 ")]
    [InlineData(" 12")]
    [InlineData("abc")]
    [InlineData("../1")]
    [InlineData("１２")]
    [InlineData("1234567890123456789")]
    [InlineData("")]
    public async Task InvalidPostIdsFailBeforeAnyRequest(string postId)
    {
        RecordingHttp http = Returning(Json("{}"));
        BooruPostId id = new("danbooru", postId);
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(id), Context(http)));
        Assert.Equal("post-id", failure.Failure.Code);
        Assert.Equal(id, failure.Post);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ValidPostIdsAreAcceptedWithoutNormalizing()
    {
        foreach (string postId in new[] { "1", "10", "999999999999999999" })
        {
            RecordingHttp http = Returning(Json("{}", status: 404));
            Assert.IsType<BooruPostNotFound>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("danbooru", postId)), Context(http)));
            Assert.Equal($"https://danbooru.donmai.us/posts/{postId}.json", Assert.Single(http.Requests).Url.AbsoluteUri);
        }
    }

    [Fact]
    public async Task InvalidRequestsFailBeforeAnyRequest()
    {
        RecordingHttp http = Returning(Json("[]"));
        foreach (BooruSearchRequest request in new BooruSearchRequest[]
        {
            new("other", "cat", 1, 20), new("danbooru", "cat", 0, 20), new("danbooru", "cat", 1001, 20),
            new("danbooru", "cat", 1, 0), new("danbooru", "cat", 1, 101), new("danbooru", null!, 1, 20),
            new("danbooru", "bad\nquery", 1, 20), new("danbooru", new string('x', 1025), 1, 20)
        })
            Assert.Equal("invalid-request", Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(request, Context(http))).Failure.Code);
        Assert.Equal("invalid-request", Assert.IsType<BooruPostFailure>(await Plugin.ReadPostAsync(new BooruPostRequest(new BooruPostId("other", "7")), Context(http))).Failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task EveryCallSendsExactlyOneRequestAndNeverRetries()
    {
        foreach (int status in new[] { 200, 404, 410, 429, 500, 502, 503, 301 })
        {
            RecordingHttp search = Returning(Json("[]", status));
            await Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 1, 20), Context(search));
            Assert.Single(search.Requests);
            RecordingHttp post = Returning(Json("{\"id\":7}", status));
            await Plugin.ReadPostAsync(new BooruPostRequest(Seven), Context(post));
            Assert.Single(post.Requests);
        }
    }

    // ---- fixtures ----------------------------------------------------------------------------------

    [Fact]
    public async Task SearchNormalFixtureMapsThreePostsIncludingSampleAndSampleLessPosts()
    {
        (BooruSearchResult result, _) = await Search(Json(Fixture("search-normal.json")), "cat", page: 2, limit: 3);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal("danbooru", page.SourceId);
        Assert.Equal("cat", page.Query);
        Assert.Equal(2, page.Page);
        Assert.False(page.IsEnd);
        // Newest first, like Danbooru's default order; a full page continues below its lowest id.
        Assert.Equal(new[] { "1003", "1002", "1001" }, page.Posts.Select(post => post.Id.PostId));
        Assert.Equal("1001", page.NextBeforePostId);
        Assert.All(page.Posts, post => Assert.Equal("danbooru", post.Id.SourceId));

        BooruPost first = page.Posts[2];
        Assert.Equal(new Uri("https://danbooru.donmai.us/posts/1001"), first.PostUrl);
        Assert.Equal(BooruFieldPresence.All, first.Fields);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 14, 7, 9, 123, TimeSpan.FromHours(9)), first.CreatedAt);
        Assert.Equal(BooruRating.General, first.Rating);
        Assert.Null(first.RawRating);
        Assert.Equal(12, first.Score);
        Assert.Equal(3, first.FavoriteCount);
        Assert.Equal("https://example.invalid/art/1001", first.SourceClaim);
        Assert.False(first.IsDeleted);
        Assert.False(first.IsBanned);
        Assert.Equal(new BooruTag[]
        {
            new("artist_one", BooruTagCategory.Artist), new("series_x", BooruTagCategory.Copyright),
            new("char_a", BooruTagCategory.Character), new("char_b", BooruTagCategory.Character),
            new("1girl", BooruTagCategory.General), new("solo", BooruTagCategory.General), new("smile", BooruTagCategory.General),
            new("highres", BooruTagCategory.Meta)
        }, first.Tags);
        Assert.Equal(new BooruMediaVariant[]
        {
            new(BooruMediaRole.Original, new Uri("https://cdn.donmai.us/original/00/11/00112233445566778899aabbccddeeff.jpg"), ".jpg", 123456, 1200, 1800, "ABEiM0RVZneImaq7zN3u/w=="),
            new(BooruMediaRole.Sample, new Uri("https://cdn.donmai.us/sample/00/11/sample-00112233445566778899aabbccddeeff.jpg")),
            new(BooruMediaRole.Preview, new Uri("https://cdn.donmai.us/180x180/00/11/00112233445566778899aabbccddeeff.jpg"))
        }, first.Media);

        BooruPost second = page.Posts[1];
        Assert.Equal("", second.SourceClaim);
        Assert.True(second.Fields.HasFlag(BooruFieldPresence.SourceClaim));
        Assert.Equal(0, second.Score);
        Assert.Equal(BooruRating.Sensitive, second.Rating);
        // large_file_url equals file_url: Danbooru's fallback is not a sample.
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Preview }, second.Media!.Select(variant => variant.Role));
        Assert.Equal(new BooruMediaVariant(BooruMediaRole.Original, new Uri("https://cdn.donmai.us/original/ff/ee/ffeeddccbbaa99887766554433221100.png"), ".png", 4096, 640, 480, "/+7dzLuqmYh3ZlVEMyIRAA=="), second.Media![0]);
        Assert.Equal(new[] { "artist_two", "original", "scenery", "no_humans" }, second.Tags!.Select(tag => tag.Name));

        BooruPost third = page.Posts[0];
        Assert.Equal(BooruRating.Explicit, third.Rating);
        Assert.Equal(-4, third.Score);
        // Only tag_string is present: every tag has an unknown category; the duplicate is kept once.
        Assert.Equal(new BooruTag[] { new("tag_one", BooruTagCategory.Unknown), new("tag_two", BooruTagCategory.Unknown) }, third.Tags);
        Assert.False(third.Fields.HasFlag(BooruFieldPresence.SourceClaim) || third.Fields.HasFlag(BooruFieldPresence.FavoriteCount)
            || third.Fields.HasFlag(BooruFieldPresence.IsDeleted) || third.Fields.HasFlag(BooruFieldPresence.IsBanned));
        Assert.Null(third.SourceClaim);
        Assert.Null(third.FavoriteCount);
        Assert.Equal(new BooruMediaVariant(BooruMediaRole.Original, new Uri("https://cdn.donmai.us/original/aa/bb/aabbccddeeff00112233445566778899.webm"), ".webm", 2048), third.Media![0]);
        Assert.Equal(BooruMediaRole.Sample, third.Media[1].Role);
    }

    // Regression for the 16 Nov 2026 "plugin-error" report on a "blue_eyes" search: three posts copied unchanged from
    // that live response (nested media_asset, ISO timestamps with a -05:00 offset, every current field). The parser
    // was never the fault there (see LiveResponseShape in the fixture README); this pins the live shape as one.
    [Fact]
    public async Task LiveResponseShapeParsesIntoAFinalPageWithTypedTagsAndCdnMedia()
    {
        (BooruSearchResult result, _) = await Search(Json(Fixture("search-live-shape.json")), "blue_eyes", page: 1, limit: 20);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(new[] { "12561078", "12561067", "12561043" }, page.Posts.Select(post => post.Id.PostId));
        Assert.True(page.IsEnd);
        Assert.Null(page.NextBeforePostId);
        Assert.All(page.Posts, post =>
        {
            Assert.Equal(BooruRating.General, post.Rating);
            Assert.Equal(BooruFieldPresence.All, post.Fields);
            Assert.Contains(new BooruTag("blue_eyes", BooruTagCategory.General), post.Tags!);
            Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Sample, BooruMediaRole.Preview }, post.Media!.Select(variant => variant.Role));
            Assert.All(post.Media!, variant => Assert.Equal("cdn.donmai.us", variant.Url!.Host));
        });

        BooruPost first = page.Posts[0];
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 8, 29, 21, 660, TimeSpan.FromHours(-5)), first.CreatedAt);
        Assert.Equal(0, first.Score);
        Assert.Equal(0, first.FavoriteCount);
        Assert.Equal("https://x.com/kabuyamakaigi/status/1068926529764188160", first.SourceClaim);
        Assert.Equal(28, first.Tags!.Count);
        Assert.Equal(new BooruTag("kabuyama_kaigi", BooruTagCategory.Artist), first.Tags[0]);
        Assert.Equal(new BooruTag("commentary_request", BooruTagCategory.Meta), first.Tags[^1]);
        Assert.Equal(new BooruMediaVariant(BooruMediaRole.Original,
            new Uri("https://cdn.donmai.us/original/a2/e2/a2e2ab23409d32c1f5f044c63588aa08.jpg"), ".jpg", 176663, 1200, 891,
            "ouKrI0CdMsH18ETGNYiqCA=="), first.Media![0]);
        Assert.Equal((2886, 4096, 725853L), (page.Posts[2].Media![0].Width, page.Posts[2].Media![0].Height, page.Posts[2].Media![0].ExpectedSize));
    }

    [Fact]
    public async Task SearchEmptyFixtureIsAnEndSnapshotNotAFailure()
    {
        (BooruSearchResult result, _) = await Search(Json(Fixture("search-empty.json")), "nothing", page: 5, limit: 20);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Empty(page.Posts);
        Assert.True(page.IsEnd);
        Assert.Equal(5, page.Page);
    }

    [Theory]
    [InlineData(3, 3, false)]
    [InlineData(4, 3, true)]
    [InlineData(100, 3, true)]
    [InlineData(2, 2, false)]
    [InlineData(3, 2, true)]
    [InlineData(1, 0, true)]
    public async Task IsEndIsTrueOnlyWhenFewerPostsThanTheLimitCame(int limit, int count, bool isEnd)
    {
        string body = "[" + string.Join(',', Enumerable.Range(1, count).Reverse().Select(id => $"{{\"id\":{id}}}")) + "]";
        (BooruSearchResult result, _) = await Search(Json(body), "q", 1, limit);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(count, page.Posts.Count);
        Assert.Equal(isEnd, page.IsEnd);
        // A final page offers no cursor; a full page continues below its lowest id.
        Assert.Equal(isEnd ? null : "1", page.NextBeforePostId);
    }

    // ---- cursor paging and order ------------------------------------------------------------------

    [Fact]
    public async Task CursorRequestsUseDanbooruBeforeIdPagingAndContinueBelowTheLowestId()
    {
        (BooruSearchResult result, RecordingHttp http) = await Search(Json(Fixture("search-cursor.json")), "cat rating:g", 1, 2, "1000");
        Assert.Equal($"{SearchBase}cat%20rating%3Ag&page=b1000&limit=2", Assert.Single(http.Requests).Url.AbsoluteUri);
        BooruSearchPage page = Assert.IsType<BooruSearchSnapshot>(result).Page;
        Assert.Equal(new[] { "999", "998" }, page.Posts.Select(post => post.Id.PostId));
        Assert.Equal(1, page.Page);
        Assert.False(page.IsEnd);
        Assert.Equal("998", page.NextBeforePostId);

        // The cursor's own last page: fewer posts than the limit is the end, without a cursor.
        (BooruSearchResult last, _) = await Search(Json(Fixture("search-cursor.json")), "cat", 1, 3, "1000");
        BooruSearchPage end = Assert.IsType<BooruSearchSnapshot>(last).Page;
        Assert.True(end.IsEnd);
        Assert.Null(end.NextBeforePostId);
        (BooruSearchResult empty, _) = await Search(Json(Fixture("search-empty.json")), "cat", 1, 20, "5");
        Assert.True(Assert.IsType<BooruSearchSnapshot>(empty).Page.IsEnd);
        Assert.Null(Assert.IsType<BooruSearchSnapshot>(empty).Page.NextBeforePostId);
    }

    [Fact]
    public async Task NumberedPagingStillSendsThePageNumber()
    {
        (BooruSearchResult result, RecordingHttp http) = await Search(Json(Fixture("search-normal.json")), "cat", 7, 3);
        Assert.Equal($"{SearchBase}cat&page=7&limit=3", Assert.Single(http.Requests).Url.AbsoluteUri);
        Assert.Equal(7, Assert.IsType<BooruSearchSnapshot>(result).Page.Page);
    }

    [Theory]
    [InlineData("b1000")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("-1")]
    [InlineData("1a")]
    [InlineData(" 1")]
    [InlineData("")]
    [InlineData("１")]
    public async Task MalformedCursorsAreInvalidRequestsBeforeAnyRequestWithoutEchoingThem(string cursor)
    {
        RecordingHttp http = Returning(Json("[]"));
        BooruSearchRequest request = new("danbooru", "cat", 1, 20, cursor);
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(request, Context(http)));
        Assert.Equal("invalid-request", failure.Failure.Code);
        if (cursor.Length > 2) Assert.DoesNotContain(cursor, JsonSerializer.Serialize(failure));
        Assert.Empty(SdkValidation.ValidateBooruSearchResult(request with { BeforePostId = "5" }, failure, Source));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("1234567890123456789")]
    [InlineData("99999999999999999999")]
    public async Task CursorsBeyondTheDanbooruIdRangeFailBeforeAnyRequestWithoutEchoingThem(string cursor)
    {
        RecordingHttp http = Returning(Json("[]"));
        BooruSearchRequest request = new("danbooru", "cat", 1, 20, cursor);
        // Longer than the SDK post id cap (18 digits): the SDK request validation already rejects it, and the plugin still fails closed.
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(request, Source), issue => issue.Code == "invalid_cursor");
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(request, Context(http)));
        Assert.Equal("invalid-request", failure.Failure.Code);
        Assert.DoesNotContain(cursor, JsonSerializer.Serialize(failure));
        Assert.Empty(SdkValidation.ValidateBooruSearchResult(request with { BeforePostId = "5" }, failure, Source));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task CursorCombinedWithAPageNumberIsAnInvalidRequest()
    {
        RecordingHttp http = Returning(Json("[]"));
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>(await Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 2, 20, "1000"), Context(http)));
        Assert.Equal("invalid-request", failure.Failure.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task EighteenDigitCursorsAreSentAsBeforeIdPages()
    {
        (_, RecordingHttp http) = await Search(Json("[]"), "cat", 1, 20, "999999999999999999");
        Assert.Equal($"{SearchBase}cat&page=b999999999999999999&limit=20", Assert.Single(http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task PagesOutOfDescendingIdOrderAreFailuresInsteadOfBrokenOrderClaims()
    {
        Assert.Equal("unexpected-order", Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("search-unordered.json")))).Result).Failure.Code);
        // An answer to "below 999" that contains 999 or more is not below the cursor.
        Assert.Equal("unexpected-order", Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("search-cursor.json")), "cat", 1, 2, "999")).Result).Failure.Code);
        Assert.Equal("unexpected-order", Assert.IsType<BooruSearchFailure>((await Search(Json("[{\"id\":5},{\"id\":4}]"), "cat", 1, 2, "4")).Result).Failure.Code);
        Assert.IsType<BooruSearchSnapshot>((await Search(Json("[{\"id\":5},{\"id\":4}]"), "cat", 1, 2, "6")).Result);
        Assert.IsType<BooruSearchSnapshot>((await Search(Json("[{\"id\":999999999999999999}]"), "cat", 1, 2)).Result);
    }

    [Fact]
    public async Task PostNormalFixtureMapsAllClaims()
    {
        BooruPost post = await PostFixture("post-normal.json", "2001");
        Assert.Equal(new Uri("https://danbooru.donmai.us/posts/2001"), post.PostUrl);
        Assert.Equal(BooruFieldPresence.All, post.Fields);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(-5)), post.CreatedAt);
        Assert.Equal(25, post.Score);
        Assert.Equal(9, post.FavoriteCount);
        Assert.Equal(new[] { "artist_three", "series_y", "landscape", "sky", "absurdres" }, post.Tags!.Select(tag => tag.Name));
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Sample, BooruMediaRole.Preview }, post.Media!.Select(variant => variant.Role));
        Assert.Equal("ASNFZ4mrze8BI0VniavN7w==", post.Media![0].ExpectedMd5);
        Assert.Null(post.Media[1].ExpectedMd5);
        Assert.Null(post.Media[1].ExpectedSize);
        Assert.Null(post.Media[1].Width);
        Assert.Null(post.Media[1].Extension);
        Assert.Null(post.Media[2].ExpectedMd5);
    }

    [Fact]
    public async Task PostRestrictedFixtureKeepsAnOriginalWithoutUrlAndNeverBuildsOne()
    {
        BooruPost post = await PostFixture("post-restricted.json", "3001");
        BooruMediaVariant original = post.Media!.Single(variant => variant.Role == BooruMediaRole.Original);
        Assert.Null(original.Url);
        Assert.Equal(".png", original.Extension);
        Assert.Equal(55555, original.ExpectedSize);
        Assert.Equal(800, original.Width);
        Assert.Equal(600, original.Height);
        Assert.Null(original.ExpectedMd5);
        Assert.DoesNotContain(post.Media!, variant => variant.Role == BooruMediaRole.Sample);
        Assert.Equal(new Uri("https://cdn.donmai.us/180x180/restricted-preview.jpg"), post.Media!.Single(variant => variant.Role == BooruMediaRole.Preview).Url);
        Assert.True(post.IsBanned);
    }

    [Fact]
    public async Task PostDeletedBannedFixtureCarriesBothFlagsAndValues()
    {
        BooruPost post = await PostFixture("post-deleted-banned.json", "4001");
        Assert.True(post.IsDeleted);
        Assert.True(post.IsBanned);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.IsDeleted) && post.Fields.HasFlag(BooruFieldPresence.IsBanned));
        Assert.Equal(BooruRating.Explicit, post.Rating);
        Assert.False(post.Fields.HasFlag(BooruFieldPresence.Media));
        Assert.Null(post.Media);
    }

    [Fact]
    public async Task PostNullFieldsFixtureSeparatesNullEmptyAndAbsent()
    {
        BooruPost post = await PostFixture("post-null-fields.json", "5001");
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Rating));
        Assert.Equal(BooruRating.Unknown, post.Rating);
        Assert.Null(post.RawRating);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Score));
        Assert.Null(post.Score);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.FavoriteCount));
        Assert.Null(post.FavoriteCount);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.CreatedAt));
        Assert.Null(post.CreatedAt);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.SourceClaim));
        Assert.Equal("", post.SourceClaim);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Tags));
        Assert.Empty(post.Tags!);
        // A boolean has no null value: a null boolean is not an observation.
        Assert.False(post.Fields.HasFlag(BooruFieldPresence.IsDeleted) || post.Fields.HasFlag(BooruFieldPresence.IsBanned));
        // Media keys are present but nothing is known: flag set, no variant.
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Media));
        Assert.Empty(post.Media!);
    }

    [Fact]
    public async Task PostUnknownFieldsFixtureIgnoresEverythingItDoesNotRead()
    {
        BooruPost post = await PostFixture("post-unknown-fields.json", "6001");
        Assert.Equal(BooruRating.Sensitive, post.Rating);
        Assert.Equal(BooruFieldPresence.Rating | BooruFieldPresence.Media, post.Fields);
        BooruMediaVariant original = Assert.Single(post.Media!);
        Assert.Equal(new Uri("https://cdn.donmai.us/original/66/00/example6001.gif"), original.Url);
        Assert.Equal(".gif", original.Extension);
    }

    [Fact]
    public async Task PostNonCdnUrlFixtureOmitsEveryForeignAddress()
    {
        BooruPost post = await PostFixture("post-non-cdn-url.json", "7001");
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Sample, BooruMediaRole.Preview }, post.Media!.Select(variant => variant.Role));
        Assert.All(post.Media!, variant => Assert.Null(variant.Url));
        Assert.Equal(".jpg", post.Media![0].Extension);
        Assert.Equal(1000, post.Media[0].ExpectedSize);
    }

    [Fact]
    public async Task PostBadMd5FixtureMakesTheWholeResponseMalformed()
    {
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>((await Read(Json(Fixture("post-bad-md5.json")), new BooruPostId("danbooru", "8001"))).Result);
        Assert.Equal("malformed", failure.Failure.Code);
    }

    [Fact]
    public async Task SearchMissingIdFixtureMakesTheWholeResponseMalformedInsteadOfDroppingThePost()
    {
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("post-missing-id.json")))).Result);
        Assert.Equal("malformed", failure.Failure.Code);
    }

    [Fact]
    public async Task HtmlChallengeFixtureIsNotJson()
    {
        HostHttpResponse html = Json(Fixture("html-challenge.html"), contentType: "text/html; charset=utf-8");
        Assert.Equal("not-json", Assert.IsType<BooruSearchFailure>((await Search(html)).Result).Failure.Code);
        Assert.Equal("not-json", Assert.IsType<BooruPostFailure>((await Read(html, Seven)).Result).Failure.Code);
        // Even an HTML body mislabelled as JSON is only malformed, never a snapshot.
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("html-challenge.html")))).Result).Failure.Code);
    }

    [Fact]
    public async Task MalformedJsonAndWrongShapeFixturesAreMalformed()
    {
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("malformed-json.json")))).Result).Failure.Code);
        Assert.Equal("malformed", Assert.IsType<BooruPostFailure>((await Read(Json(Fixture("malformed-json.json")), Seven)).Result).Failure.Code);
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("wrong-shape.json")))).Result).Failure.Code);
        // The post endpoint must return an object, never an array.
        Assert.Equal("malformed", Assert.IsType<BooruPostFailure>((await Read(Json(Fixture("search-normal.json")), Seven)).Result).Failure.Code);
    }

    [Fact]
    public async Task TooManyPostsFixtureFailsInsteadOfTruncating()
    {
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>((await Search(Json(Fixture("too-many-posts.json")), "q", 1, 3)).Result);
        Assert.Equal("too-many-posts", failure.Failure.Code);
        // The same body is fine when the limit allows it.
        Assert.Equal(4, Assert.IsType<BooruSearchSnapshot>((await Search(Json(Fixture("too-many-posts.json")), "q", 1, 4)).Result).Page.Posts.Count);
    }

    [Fact]
    public async Task EveryFixtureYieldsAResultThatPassesSdkValidation()
    {
        // Search and Read validate each result internally; run every fixture through both entry points.
        string[] bodies = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "danbooru"))
            .Where(path => !path.EndsWith(".md", StringComparison.Ordinal)).Select(File.ReadAllText).ToArray();
        Assert.Equal(17, bodies.Length);
        foreach (string body in bodies)
        {
            await Search(Json(body), "q", 1, 3);
            await Search(Json(body, contentType: "text/html"), "q", 1, 3);
            await Read(Json(body), new BooruPostId("danbooru", "2001"));
            await Read(Json(body, contentType: "text/html"), Seven);
        }
    }

    // ---- field mapping -----------------------------------------------------------------------------

    [Fact]
    public async Task IdMustBeAPositiveIntegerOtherwiseTheResponseIsMalformed()
    {
        foreach (string id in new[] { "\"id\":0", "\"id\":-3", "\"id\":1.5", "\"id\":\"7\"", "\"id\":null", "\"id\":true", "\"id\":99999999999999999999", "\"x\":1" })
            Assert.Equal("malformed", await MalformedCode("{" + id + "}"));
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json("[{\"id\":1},[1]]"))).Result).Failure.Code);
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json("[{\"id\":1},{\"id\":1}]"))).Result).Failure.Code);
        Assert.Equal("wrong-post", Assert.IsType<BooruPostFailure>((await Read(Json("{\"id\":8}"), Seven)).Result).Failure.Code);
    }

    [Theory]
    [InlineData("\"g\"", BooruRating.General, null)]
    [InlineData("\"s\"", BooruRating.Sensitive, null)]
    [InlineData("\"q\"", BooruRating.Questionable, null)]
    [InlineData("\"e\"", BooruRating.Explicit, null)]
    [InlineData("\"x\"", BooruRating.Unknown, "x")]
    [InlineData("\"G\"", BooruRating.Unknown, "G")]
    [InlineData("\"\"", BooruRating.Unknown, "")]
    [InlineData("null", BooruRating.Unknown, null)]
    public async Task RatingMapsProviderLettersAndKeepsUnknownText(string json, BooruRating expected, string? raw)
    {
        BooruPost post = await PostFrom($"\"rating\":{json}");
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Rating));
        Assert.Equal(expected, post.Rating);
        Assert.Equal(raw, post.RawRating);
    }

    [Fact]
    public async Task AbsentRatingLeavesTheFlagUnsetAndBadRatingsAreMalformed()
    {
        BooruPost post = await PostFrom("\"score\":1");
        Assert.False(post.Fields.HasFlag(BooruFieldPresence.Rating));
        Assert.Equal(BooruRating.Unknown, post.Rating);
        Assert.Null(post.RawRating);
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"rating\":1}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"rating\":\"" + new string('r', 17) + "\"}"));
        Assert.Equal(new string('r', 16), (await PostFrom("\"rating\":\"" + new string('r', 16) + "\"")).RawRating);
    }

    [Theory]
    [InlineData("\"created_at\":\"2024-03-05T14:07:09.123+09:00\"", 2024, 3, 5, 14, 7, 9, 123, 9)]
    [InlineData("\"created_at\":\"2024-03-05T05:07:09Z\"", 2024, 3, 5, 5, 7, 9, 0, 0)]
    [InlineData("\"created_at\":\"2024-03-05T05:07:09.5Z\"", 2024, 3, 5, 5, 7, 9, 500, 0)]
    [InlineData("\"created_at\":\"2024-03-05T05:07:09.123456-07:00\"", 2024, 3, 5, 5, 7, 9, 123, -7)]
    [InlineData("\"created_at\":\"2024-03-05T05:07:09\"", 2024, 3, 5, 5, 7, 9, 0, 0)]
    public async Task CreatedAtIsIso8601(string field, int year, int month, int day, int hour, int minute, int second, int millisecond, int offsetHours)
    {
        BooruPost post = await PostFrom(field);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.CreatedAt));
        DateTimeOffset expected = new(year, month, day, hour, minute, second, millisecond, TimeSpan.FromHours(offsetHours));
        Assert.True(post.CreatedAt >= expected && post.CreatedAt < expected.AddMilliseconds(1), $"{post.CreatedAt:O} vs {expected:O}");
    }

    [Theory]
    [InlineData("\"created_at\":\"yesterday\"")]
    [InlineData("\"created_at\":\"2024-03-05\"")]
    [InlineData("\"created_at\":\"\"")]
    [InlineData("\"created_at\":1700000000")]
    [InlineData("\"created_at\":\"2024-13-05T05:07:09Z\"")]
    public async Task UnparsableCreatedAtIsMalformed(string field)
        => Assert.Equal("malformed", await MalformedCode("{\"id\":7," + field + "}"));

    [Fact]
    public async Task ScoreAndFavoriteCountAreIntegersWithFlags()
    {
        BooruPost post = await PostFrom("\"score\":-8,\"fav_count\":14");
        Assert.Equal(-8, post.Score);
        Assert.Equal(14, post.FavoriteCount);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.Score) && post.Fields.HasFlag(BooruFieldPresence.FavoriteCount));
        BooruPost none = await PostFrom("\"rating\":\"g\"");
        Assert.False(none.Fields.HasFlag(BooruFieldPresence.Score) || none.Fields.HasFlag(BooruFieldPresence.FavoriteCount));
        foreach (string field in new[] { "\"score\":\"5\"", "\"score\":1.5", "\"score\":99999999999", "\"fav_count\":-1", "\"fav_count\":false" })
            Assert.Equal("malformed", await MalformedCode("{\"id\":7," + field + "}"));
    }

    [Fact]
    public async Task TagsKeepProviderSpellingAndDocumentedCategoryOrder()
    {
        BooruPost post = await PostFrom(
            "\"tag_string_meta\":\"Meta_One\",\"tag_string_general\":\"B_Tag a_tag\",\"tag_string_character\":\"char\",\"tag_string_copyright\":\"copy\",\"tag_string_artist\":\"art\"," +
            "\"tag_string\":\"ignored_when_categories_exist\"");
        // Order: artist, copyright, character, general, meta, then the provider's order inside each field.
        Assert.Equal(new BooruTag[]
        {
            new("art", BooruTagCategory.Artist), new("copy", BooruTagCategory.Copyright), new("char", BooruTagCategory.Character),
            new("B_Tag", BooruTagCategory.General), new("a_tag", BooruTagCategory.General), new("Meta_One", BooruTagCategory.Meta)
        }, post.Tags);
    }

    [Fact]
    public async Task DuplicateTagNamesKeepTheFirstOccurrence()
    {
        BooruPost post = await PostFrom("\"tag_string_artist\":\"dup\",\"tag_string_general\":\"dup x dup\",\"tag_string_meta\":\"x\"");
        Assert.Equal(new BooruTag[] { new("dup", BooruTagCategory.Artist), new("x", BooruTagCategory.General) }, post.Tags);
    }

    [Fact]
    public async Task TagFlagFollowsPresenceEvenForEmptyOrNullFields()
    {
        Assert.False((await PostFrom("\"score\":1")).Fields.HasFlag(BooruFieldPresence.Tags));
        Assert.Null((await PostFrom("\"score\":1")).Tags);
        foreach (string field in new[] { "\"tag_string\":\"\"", "\"tag_string_meta\":\"\"", "\"tag_string_general\":null", "\"tag_string\":null" })
        {
            BooruPost post = await PostFrom(field);
            Assert.True(post.Fields.HasFlag(BooruFieldPresence.Tags), field);
            Assert.Empty(post.Tags!);
        }
        // tag_string is only a fallback when none of the five category fields exists.
        BooruPost mixed = await PostFrom("\"tag_string_general\":\"\",\"tag_string\":\"fallback\"");
        Assert.Empty(mixed.Tags!);
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"tag_string_general\":5}"));
    }

    [Fact]
    public async Task TagsAreBoundedToTheSdkLimitsInsteadOfTruncated()
    {
        string Tags(int count) => string.Join(' ', Enumerable.Range(0, count).Select(index => "t" + index.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(4096, (await PostFrom($"\"tag_string_general\":\"{Tags(4096)}\"")).Tags!.Count);
        Assert.Equal("malformed", await MalformedCode($"{{\"id\":7,\"tag_string_general\":\"{Tags(4097)}\"}}"));
        // The cap counts distinct tags: duplicates do not push a post over it.
        Assert.Equal(4096, (await PostFrom($"\"tag_string_general\":\"{Tags(4096)} t0 t1\"")).Tags!.Count);
        Assert.Equal(256, (await PostFrom($"\"tag_string_general\":\"{new string('a', 256)}\"")).Tags![0].Name.Length);
        Assert.Equal("malformed", await MalformedCode($"{{\"id\":7,\"tag_string_general\":\"{new string('a', 257)}\"}}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"tag_string_general\":\"a\\tb\"}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"tag_string_general\":\"a\\u0000b\"}"));
    }

    [Fact]
    public async Task SourceClaimIsInertTextWithItsOwnFlag()
    {
        BooruPost post = await PostFrom("\"source\":\"https://example.invalid/a?b=c d\"");
        Assert.Equal("https://example.invalid/a?b=c d", post.SourceClaim);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.SourceClaim));
        Assert.Equal("", (await PostFrom("\"source\":\"\"")).SourceClaim);
        BooruPost nullClaim = await PostFrom("\"source\":null");
        Assert.True(nullClaim.Fields.HasFlag(BooruFieldPresence.SourceClaim));
        Assert.Null(nullClaim.SourceClaim);
        Assert.False((await PostFrom("\"score\":1")).Fields.HasFlag(BooruFieldPresence.SourceClaim));
        Assert.Equal(4096, (await PostFrom($"\"source\":\"{new string('s', 4096)}\"")).SourceClaim!.Length);
        Assert.Equal("malformed", await MalformedCode($"{{\"id\":7,\"source\":\"{new string('s', 4097)}\"}}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"source\":12}"));
    }

    [Fact]
    public async Task DeletedAndBannedAreBooleansWithFlagsAndValues()
    {
        BooruPost post = await PostFrom("\"is_deleted\":true,\"is_banned\":false");
        Assert.True(post.IsDeleted);
        Assert.False(post.IsBanned);
        Assert.True(post.Fields.HasFlag(BooruFieldPresence.IsDeleted) && post.Fields.HasFlag(BooruFieldPresence.IsBanned));
        BooruPost none = await PostFrom("\"score\":1");
        Assert.False(none.Fields.HasFlag(BooruFieldPresence.IsDeleted) || none.Fields.HasFlag(BooruFieldPresence.IsBanned));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"is_deleted\":\"true\"}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,\"is_banned\":1}"));
    }

    [Theory]
    [InlineData("jpg", ".jpg")]
    [InlineData("PNG", ".PNG")]
    [InlineData("mp4", ".mp4")]
    [InlineData("j.pg", null)]
    [InlineData("j/pg", null)]
    [InlineData("", null)]
    [InlineData("é", null)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", null)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", ".abcdefghijklmnopqrstuvwxyz012345")]
    public async Task ExtensionGetsALeadingDotOrIsOmitted(string ext, string? expected)
    {
        BooruPost post = await PostFrom($"\"file_ext\":\"{ext}\",\"file_size\":10");
        Assert.Equal(expected, Assert.Single(post.Media!).Extension);
    }

    [Fact]
    public async Task OriginalCarriesSizeDimensionsAndBase64Md5()
    {
        BooruPost post = await PostFrom("\"file_url\":\"https://cdn.donmai.us/original/x.jpg\",\"file_ext\":\"jpg\",\"file_size\":0,\"image_width\":1,\"image_height\":2,\"md5\":\"0123456789ABCDEF0123456789abcdef\"");
        Assert.Equal(new BooruMediaVariant(BooruMediaRole.Original, new Uri("https://cdn.donmai.us/original/x.jpg"), ".jpg", 0, 1, 2, "ASNFZ4mrze8BI0VniavN7w=="), Assert.Single(post.Media!));
        foreach (string field in new[] { "\"md5\":\"abc\"", "\"md5\":\"\"", "\"md5\":\"0123456789abcdef0123456789abcdeg\"", "\"md5\":\"0123456789abcdef0123456789abcdef00\"", "\"md5\":12",
            "\"file_size\":-1", "\"file_size\":\"5\"", "\"image_width\":0", "\"image_height\":-4", "\"image_width\":1.5", "\"file_url\":5", "\"preview_file_url\":[]" })
            Assert.Equal("malformed", await MalformedCode("{\"id\":7," + field + "}"));
    }

    [Fact]
    public async Task SampleExistsOnlyWhenItDiffersFromTheOriginal()
    {
        const string original = "https://cdn.donmai.us/original/a.jpg";
        BooruPost same = await PostFrom($"\"file_url\":\"{original}\",\"large_file_url\":\"{original}\"");
        Assert.Equal(new[] { BooruMediaRole.Original }, same.Media!.Select(variant => variant.Role));
        BooruPost different = await PostFrom($"\"file_url\":\"{original}\",\"large_file_url\":\"https://cdn.donmai.us/sample/a.jpg\",\"file_size\":99,\"md5\":\"0123456789abcdef0123456789abcdef\"");
        BooruMediaVariant sample = different.Media!.Single(variant => variant.Role == BooruMediaRole.Sample);
        Assert.Equal(new BooruMediaVariant(BooruMediaRole.Sample, new Uri("https://cdn.donmai.us/sample/a.jpg")), sample);
        // A sample alone (restricted original) is still a sample, never promoted to an original.
        BooruPost restricted = await PostFrom("\"large_file_url\":\"https://cdn.donmai.us/sample/b.jpg\"");
        Assert.Equal(new[] { BooruMediaRole.Sample }, restricted.Media!.Select(variant => variant.Role));
        Assert.Empty((await PostFrom("\"large_file_url\":\"\"")).Media!);
    }

    [Theory]
    [InlineData("https://CDN.DONMAI.US/original/a.jpg")]
    [InlineData("https://cdn.donmai.us:443/original/a.jpg")]
    public async Task DisplayVariantsCannotPointToTheNormalizedOriginal(string alias)
    {
        const string original = "https://cdn.donmai.us/original/a.jpg";
        BooruPost post = await PostFrom($"\"file_url\":\"{original}\",\"large_file_url\":\"{alias}\",\"preview_file_url\":\"{alias}\"");
        Assert.Equal(BooruMediaRole.Original, Assert.Single(post.Media!).Role);
    }

    [Theory]
    [InlineData("https://images.example.invalid/a.jpg")]
    [InlineData("http://cdn.donmai.us/a.jpg")]
    [InlineData("https://cdn.donmai.us.example.invalid/a.jpg")]
    [InlineData("https://evil-cdn.donmai.us/a.jpg")]
    [InlineData("https://danbooru.donmai.us/a.jpg")]
    [InlineData("https://cdn.donmai.us:8443/a.jpg")]
    [InlineData("https://user:pass@cdn.donmai.us/a.jpg")]
    [InlineData("https://cdn.donmai.us/a.jpg#frag")]
    [InlineData("//cdn.donmai.us/a.jpg")]
    [InlineData("/original/a.jpg")]
    [InlineData("ftp://cdn.donmai.us/a.jpg")]
    [InlineData("not a url")]
    public async Task OnlyHttpsCdnAddressesAreKeptForEveryRole(string url)
    {
        BooruPost post = await PostFrom($"\"file_url\":\"{url}\",\"large_file_url\":\"{url}x\",\"preview_file_url\":\"{url}\",\"file_ext\":\"jpg\"");
        Assert.Equal(new[] { BooruMediaRole.Original, BooruMediaRole.Sample, BooruMediaRole.Preview }, post.Media!.Select(variant => variant.Role));
        Assert.All(post.Media!, variant => Assert.Null(variant.Url));
    }

    [Fact]
    public async Task CdnHostMatchingIsCaseInsensitiveAndRestrictedPostsNeverGetAnInventedUrl()
    {
        BooruPost upper = await PostFrom("\"file_url\":\"https://CDN.DONMAI.US/original/a.jpg\"");
        Assert.Equal("cdn.donmai.us", Assert.Single(upper.Media!).Url!.IdnHost);
        // md5 known but no file_url: the address is not reconstructed from the hash.
        BooruPost restricted = await PostFrom("\"md5\":\"0123456789abcdef0123456789abcdef\",\"file_ext\":\"jpg\"");
        BooruMediaVariant original = Assert.Single(restricted.Media!);
        Assert.Null(original.Url);
        Assert.Equal("ASNFZ4mrze8BI0VniavN7w==", original.ExpectedMd5);
    }

    [Fact]
    public async Task MediaFlagFollowsMediaFieldPresenceAndNothingKnownMeansNoOriginal()
    {
        Assert.False((await PostFrom("\"score\":1")).Fields.HasFlag(BooruFieldPresence.Media));
        Assert.Null((await PostFrom("\"score\":1")).Media);
        foreach (string field in new[] { "\"file_url\":null", "\"file_ext\":\"\"", "\"md5\":null", "\"image_width\":null", "\"file_size\":null", "\"preview_file_url\":\"\"" })
        {
            BooruPost post = await PostFrom(field);
            Assert.True(post.Fields.HasFlag(BooruFieldPresence.Media), field);
            Assert.Empty(post.Media!);
        }
        Assert.True((await PostFrom("\"image_height\":7")).Media!.Single().Height == 7);
    }

    [Fact]
    public async Task PostUrlIsAlwaysTheCanonicalPostPage()
    {
        BooruPost post = await PostFrom("\"score\":1");
        Assert.Equal(new Uri("https://danbooru.donmai.us/posts/7"), post.PostUrl);
        Assert.Equal(new BooruPostId("danbooru", "7"), post.Id);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredAndDepthIsBounded()
    {
        BooruPost post = await PostFrom("\"future\":{\"a\":[1,2,{\"b\":null}]},\"rating\":\"g\"");
        Assert.Equal(BooruFieldPresence.Rating, post.Fields);
        BooruPost nested = await PostFrom($"\"x\":{new string('[', 20)}{new string(']', 20)}");
        Assert.Equal(BooruFieldPresence.None, nested.Fields);
        Assert.Equal("malformed", await MalformedCode($"{{\"id\":7,\"x\":{new string('[', 40)}{new string(']', 40)}}}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7,}"));
        Assert.Equal("malformed", await MalformedCode("{\"id\":7 /* c */}"));
        Assert.Equal("malformed", await MalformedCode(""));
        Assert.Equal("malformed", await MalformedCode("null"));
    }

    [Fact]
    public async Task OneBadPostMakesASearchMalformedInsteadOfLosingIt()
    {
        string body = "[{\"id\":1,\"rating\":\"g\"},{\"id\":2,\"md5\":\"bad\"},{\"id\":3}]";
        Assert.Equal("malformed", Assert.IsType<BooruSearchFailure>((await Search(Json(body))).Result).Failure.Code);
    }

    // ---- content type ------------------------------------------------------------------------------

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("Application/JSON", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("  application/json ;charset=UTF-8", true)]
    [InlineData("text/html", false)]
    [InlineData("text/html; charset=utf-8", false)]
    [InlineData("application/xml", false)]
    [InlineData("application/jsonp", false)]
    [InlineData("application/problem+json", false)]
    [InlineData("text/plain", false)]
    [InlineData("", false)]
    public async Task OnlyJsonContentTypesAreParsed(string contentType, bool accepted)
    {
        BooruSearchResult result = (await Search(Json("[]", contentType: contentType))).Result;
        if (accepted) Assert.IsType<BooruSearchSnapshot>(result);
        else Assert.Equal("not-json", Assert.IsType<BooruSearchFailure>(result).Failure.Code);
    }

    [Fact]
    public async Task ContentTypeHeaderNameIsCaseInsensitiveAndMissingMeansNotJson()
    {
        HostHttpResponse lower = new(200, new Uri("https://danbooru.donmai.us/posts.json"),
            new Dictionary<string, string> { ["content-type"] = "application/json" }, Encoding.UTF8.GetBytes("[]"));
        Assert.IsType<BooruSearchSnapshot>((await Search(lower)).Result);
        HostHttpResponse none = lower with { Headers = new Dictionary<string, string>() };
        Assert.Equal("not-json", Assert.IsType<BooruSearchFailure>((await Search(none)).Result).Failure.Code);
    }

    [Fact]
    public async Task ContentTypeIsNotCheckedForErrorStatuses()
    {
        Assert.Equal("access-denied", Assert.IsType<BooruSearchFailure>((await Search(Json("<html/>", 403, "text/html"))).Result).Failure.Code);
        Assert.IsType<BooruPostNotFound>((await Read(Json("<html/>", 404, "text/html"), Seven)).Result);
    }

    // ---- status mapping ----------------------------------------------------------------------------

    [Theory]
    [InlineData(404, "not-found")]
    [InlineData(410, "page-limit")]
    [InlineData(401, "access-denied")]
    [InlineData(403, "access-denied")]
    [InlineData(300, "redirect")]
    [InlineData(301, "redirect")]
    [InlineData(302, "redirect")]
    [InlineData(304, "redirect")]
    [InlineData(307, "redirect")]
    [InlineData(308, "redirect")]
    [InlineData(400, "http-400")]
    [InlineData(418, "http-418")]
    [InlineData(500, "http-500")]
    [InlineData(504, "http-504")]
    [InlineData(204, "http-204")]
    [InlineData(206, "http-206")]
    public async Task SearchStatusesMapToFailures(int status, string code)
    {
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>((await Search(Json("[{\"id\":1}]", status))).Result);
        Assert.Equal(code, failure.Failure.Code);
        Assert.Equal(status, failure.Failure.HttpStatusCode);
        Assert.Equal("danbooru", failure.SourceId);
    }

    [Theory]
    [InlineData(410, "http-410")]
    [InlineData(401, "access-denied")]
    [InlineData(403, "access-denied")]
    [InlineData(301, "redirect")]
    [InlineData(302, "redirect")]
    [InlineData(400, "http-400")]
    [InlineData(500, "http-500")]
    public async Task PostStatusesMapToFailures(int status, string code)
    {
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>((await Read(Json("{\"id\":7}", status), Seven)).Result);
        Assert.Equal(code, failure.Failure.Code);
        Assert.Equal(status, failure.Failure.HttpStatusCode);
        Assert.Equal(Seven, failure.Post);
    }

    [Fact]
    public async Task PostNotFoundIsOnlyAVerified404()
    {
        BooruPostNotFound notFound = Assert.IsType<BooruPostNotFound>((await Read(Json("{\"success\":false}", 404), Seven)).Result);
        Assert.Equal(404, notFound.HttpStatusCode);
        Assert.Equal(Seven, notFound.Post);
        Assert.IsNotType<BooruPostNotFound>((await Read(Json("{}", 410), Seven)).Result);
        Assert.IsNotType<BooruPostNotFound>((await Read(Json("{}", 500), Seven)).Result);
    }

    [Theory]
    [InlineData(429, "provider-throttled", 60)]
    [InlineData(502, "provider-unavailable", 5)]
    [InlineData(503, "provider-unavailable", 5)]
    public async Task ThrottleAndUnavailableDeferWithDefaultDelaysWithoutRetryAfter(int status, string reason, int seconds)
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        BooruSearchDeferred search = Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", status))).Result);
        BooruPostDeferred post = Assert.IsType<BooruPostDeferred>((await Read(Json("{}", status), Seven)).Result);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        foreach ((DateTimeOffset retryAt, string code, int? http) in new[] { (search.RetryAt, search.ReasonCode, search.HttpStatusCode), (post.RetryAt, post.ReasonCode, post.HttpStatusCode) })
        {
            Assert.Equal(reason, code);
            Assert.Equal(status, http);
            Assert.InRange(retryAt, before.AddSeconds(seconds), after.AddSeconds(seconds));
        }
        Assert.Equal("danbooru", search.SourceId);
        Assert.Equal(Seven, post.Post);
    }

    [Theory]
    [InlineData(429, "0", 0)]
    [InlineData(429, "120", 120)]
    [InlineData(503, "  17 ", 17)]
    [InlineData(502, "3600", 3600)]
    [InlineData(429, "86400", 86400)]
    [InlineData(429, "86401", 86401)]
    [InlineData(429, "99999999999999999999999", 604800)]
    public async Task RetryAfterSecondsSetTheDueTime(int status, string header, int seconds)
    {
        HostHttpResponse response = Json("[]", status, headers: ("Retry-After", header));
        DateTimeOffset before = DateTimeOffset.UtcNow;
        BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search(response)).Result);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.InRange(deferred.RetryAt, before.AddSeconds(seconds), after.AddSeconds(seconds));
        BooruPostDeferred post = Assert.IsType<BooruPostDeferred>((await Read(Json("{}", status, headers: ("retry-after", header)), Seven)).Result);
        Assert.InRange(post.RetryAt, before.AddSeconds(seconds), DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    [Fact]
    public async Task RetryAfterHttpDateSetsTheDueTime()
    {
        DateTimeOffset target = DateTimeOffset.UtcNow.AddMinutes(5);
        string header = target.ToString("r", CultureInfo.InvariantCulture);
        DateTimeOffset expected = DateTimeOffset.ParseExact(header, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.Equal(expected, Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", 429, headers: ("Retry-After", header)))).Result).RetryAt);
        Assert.Equal(expected, Assert.IsType<BooruPostDeferred>((await Read(Json("{}", 503, headers: ("Retry-After", header)), Seven)).Result).RetryAt);
    }

    [Fact]
    public async Task RetryAfterInThePastIsDueNowAndFarFutureIsCapped()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        string past = before.AddHours(-1).ToString("r", CultureInfo.InvariantCulture);
        BooruSearchDeferred now = Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", 429, headers: ("Retry-After", past)))).Result);
        Assert.InRange(now.RetryAt, before, DateTimeOffset.UtcNow);
        string far = before.AddDays(30).ToString("r", CultureInfo.InvariantCulture);
        BooruSearchDeferred capped = Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", 429, headers: ("Retry-After", far)))).Result);
        Assert.InRange(capped.RetryAt, before.AddHours(168), DateTimeOffset.UtcNow.AddHours(168));
    }

    [Theory]
    [InlineData(429, "soon", 60)]
    [InlineData(429, "-5", 60)]
    [InlineData(429, "1.5", 60)]
    [InlineData(429, "", 60)]
    [InlineData(429, "Wed, 99 Foo 2026 25:61:61 GMT", 60)]
    [InlineData(503, "later", 5)]
    [InlineData(502, "-1", 5)]
    public async Task InvalidRetryAfterFallsBackToTheDefaultDelay(int status, string header, int seconds)
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", status, headers: ("Retry-After", header)))).Result);
        Assert.InRange(deferred.RetryAt, before.AddSeconds(seconds), DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    [Fact]
    public async Task RetryAfterIsIgnoredForNonDeferredStatuses()
    {
        BooruSearchFailure failure = Assert.IsType<BooruSearchFailure>((await Search(Json("[]", 500, headers: ("Retry-After", "30")))).Result);
        Assert.Equal("http-500", failure.Failure.Code);
        Assert.Null(failure.Failure.RetryAt);
    }

    // ---- host pass-through -------------------------------------------------------------------------

    [Fact]
    public async Task HostDeferredPassesThroughUnchanged()
    {
        HostHttpDeferred host = new(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero), "host-pacing", 429);
        BooruSearchDeferred search = Assert.IsType<BooruSearchDeferred>((await Search(host)).Result);
        Assert.Equal((host.RetryAt, host.ReasonCode, host.HttpStatusCode), (search.RetryAt, search.ReasonCode, search.HttpStatusCode));
        BooruPostDeferred post = Assert.IsType<BooruPostDeferred>((await Read(host, Seven)).Result);
        Assert.Equal((host.RetryAt, host.ReasonCode, host.HttpStatusCode), (post.RetryAt, post.ReasonCode, post.HttpStatusCode));
        HostHttpDeferred noStatus = new(host.RetryAt, "host-pacing");
        Assert.Null(Assert.IsType<BooruSearchDeferred>((await Search(noStatus)).Result).HttpStatusCode);
    }

    [Fact]
    public async Task HostFailurePassesThroughWithTheSameDetail()
    {
        FailureDetail detail = new("host-timeout", "The host timed out.", null, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Same(detail, Assert.IsType<BooruSearchFailure>((await Search(new HostHttpFailure(detail))).Result).Failure);
        Assert.Same(detail, Assert.IsType<BooruPostFailure>((await Read(new HostHttpFailure(detail), Seven)).Result).Failure);
    }

    // ---- failure text ------------------------------------------------------------------------------

    [Fact]
    public async Task FailureMessagesNeverEchoTheQueryOrTagText()
    {
        const string query = "zzsecretqueryzz rating:e";
        const string leak = "zztagtextzz";
        string tagged = "{\"id\":7,\"tag_string_general\":\"" + leak + "\",\"md5\":\"bad" + leak + "\"}";
        List<FailureDetail> details = new();
        foreach (int status in new[] { 400, 401, 403, 404, 410, 301, 418, 500, 504 })
        {
            details.Add(Assert.IsType<BooruSearchFailure>((await Search(Json($"[{tagged}]", status), query)).Result).Failure);
            BooruPostResult post = (await Read(Json(tagged, status), Seven)).Result;
            if (post is BooruPostFailure postFailure) details.Add(postFailure.Failure);
        }
        details.Add(Assert.IsType<BooruSearchFailure>((await Search(Json($"[{tagged}]"), query)).Result).Failure);
        details.Add(Assert.IsType<BooruPostFailure>((await Read(Json(tagged), Seven)).Result).Failure);
        details.Add(Assert.IsType<BooruSearchFailure>((await Search(Json($"[{tagged}]", contentType: "text/html"), query)).Result).Failure);
        details.Add(Assert.IsType<BooruSearchFailure>((await Search(Json($"[{tagged},{{\"id\":8}}]"), query, 1, 1)).Result).Failure);
        details.Add(Assert.IsType<BooruSearchFailure>((await Search(Json("[{\"id\":1,\"tag_string_general\":\"" + leak + "\\t\"}]"), query)).Result).Failure);
        Assert.True(details.Count > 18);
        foreach (FailureDetail detail in details)
        {
            string text = JsonSerializer.Serialize(detail);
            Assert.DoesNotContain(leak, text);
            Assert.DoesNotContain("zzsecretqueryzz", text);
            Assert.DoesNotContain("rating", text);
            Assert.DoesNotContain("danbooru.donmai.us", text);
        }
        // Every message is a constant sentence per code.
        Assert.All(details.GroupBy(detail => detail.Code), group => Assert.Single(group.Select(detail => detail.Message).Distinct()));
    }

    [Fact]
    public async Task DeferredReasonsAreConstantCodes()
    {
        BooruSearchDeferred deferred = Assert.IsType<BooruSearchDeferred>((await Search(Json("[]", 429, headers: ("Retry-After", "5")), "zzsecretqueryzz")).Result);
        Assert.DoesNotContain("zzsecretqueryzz", JsonSerializer.Serialize(deferred));
    }

    // ---- cancellation ------------------------------------------------------------------------------

    [Fact]
    public async Task CancellationTokenPropagatesToTheHost()
    {
        using CancellationTokenSource source = new();
        RecordingHttp http = Returning(Json("[]"));
        await Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 1, 20), Context(http), source.Token);
        Assert.Equal(source.Token, http.LastToken);
        RecordingHttp postHttp = Returning(Json("{\"id\":7}"));
        await Plugin.ReadPostAsync(new BooruPostRequest(Seven), Context(postHttp), source.Token);
        Assert.Equal(source.Token, postHttp.LastToken);
    }

    [Fact]
    public async Task CancelledCallsThrowInsteadOfReturningAResult()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        RecordingHttp http = Returning(Json("[]"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 1, 20), Context(http), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.ReadPostAsync(new BooruPostRequest(Seven), Context(http), cancelled.Token));
        Assert.Empty(http.Requests);

        // Cancelled while the host call is in flight: the response is discarded.
        using CancellationTokenSource late = new();
        RecordingHttp inFlight = new(_ => { late.Cancel(); return Json("[{\"id\":1}]"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.SearchPostsAsync(new BooruSearchRequest("danbooru", "cat", 1, 20), Context(inFlight), late.Token));
        using CancellationTokenSource latePost = new();
        RecordingHttp inFlightPost = new(_ => { latePost.Cancel(); return Json("{\"id\":7}"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Plugin.ReadPostAsync(new BooruPostRequest(Seven), Context(inFlightPost), latePost.Token));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "danbooru", name));

    private static HostHttpResponse Json(string body, int status = 200, string contentType = "application/json; charset=utf-8", (string Name, string Value)? headers = null)
    {
        Dictionary<string, string> map = new() { ["Content-Type"] = contentType };
        if (headers is { } header) map[header.Name] = header.Value;
        return new HostHttpResponse(status, new Uri("https://danbooru.donmai.us/posts.json"), map, Encoding.UTF8.GetBytes(body));
    }

    private static RecordingHttp Returning(HostHttpResult result) => new(_ => result);

    private static async Task<(BooruSearchResult Result, RecordingHttp Http)> Search(HostHttpResult response, string query = "cat", int page = 1, int limit = 20, string? cursor = null)
    {
        RecordingHttp http = Returning(response);
        BooruSearchRequest request = new("danbooru", query, page, limit, cursor);
        BooruSearchResult result = await Plugin.SearchPostsAsync(request, Context(http));
        Assert.Empty(SdkValidation.ValidateBooruSearchResult(request, result, Source));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static async Task<(BooruPostResult Result, RecordingHttp Http)> Read(HostHttpResult response, BooruPostId post)
    {
        RecordingHttp http = Returning(response);
        BooruPostRequest request = new(post);
        BooruPostResult result = await Plugin.ReadPostAsync(request, Context(http));
        Assert.Empty(SdkValidation.ValidateBooruPostResult(request, result, Source));
        Assert.Single(http.Requests);
        return (result, http);
    }

    private static async Task<BooruPost> PostFixture(string fixture, string id)
        => Assert.IsType<BooruPostSnapshot>((await Read(Json(Fixture(fixture)), new BooruPostId("danbooru", id))).Result).Post;

    // A post with id 7 and the given raw JSON fields, parsed through the single-post endpoint.
    private static async Task<BooruPost> PostFrom(string fields)
        => Assert.IsType<BooruPostSnapshot>((await Read(Json("{\"id\":7" + (fields.Length == 0 ? "" : "," + fields) + "}"), Seven)).Result).Post;

    private static async Task<string> MalformedCode(string body)
    {
        BooruPostFailure failure = Assert.IsType<BooruPostFailure>((await Read(Json(body), Seven)).Result);
        return failure.Failure.Code;
    }

    private static PluginCallContext Context(IHostHttp http) => new(new PluginConfiguration("test", new SettingsSnapshot("1", []),
        new Dictionary<string, SettingsSnapshot>()), http, new NoServices(), new NoServices(), new NoServices());

    private sealed class RecordingHttp(Func<HostHttpRequest, HostHttpResult> respond) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = [];
        internal CancellationToken LastToken { get; private set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
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
