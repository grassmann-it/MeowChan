using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class BooruContractTests
{
    private const string PrivateQuery = "secret_private_tag rating:safe";
    private static readonly string Md5 = Convert.ToBase64String(new byte[16]);
    private static readonly BooruPostId Id = new("booru", "42");
    private static readonly BooruSearchRequest Request = new("booru", PrivateQuery, 1, 20);
    private static readonly DateTimeOffset Due = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static SourceDescription Source(HttpHostDeclaration[]? hosts = null) => new("booru", "Synthetic booru",
        new Uri("https://booru.test/"), SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
        hosts ?? [new HttpHostDeclaration("booru.test", HttpHostPurpose.Metadata),
            new HttpHostDeclaration("original.test", HttpHostPurpose.OriginalMedia),
            new HttpHostDeclaration("preview.test", HttpHostPurpose.PreviewMedia)], TestContracts.EmptySchema);

    private static PluginDescription Plugin(SourceDescription? source = null, string sdk = "1.1.0")
        => TestContracts.Plugin(source ?? Source()) with { SdkVersion = sdk };

    private static BooruPost FullPost(string id = "42") => new(new("booru", id), BooruFieldPresence.All,
        new Uri("https://booru.test/posts/" + id), Due, BooruRating.Sensitive, "s", 12, 3,
        [new BooruTag("blue_sky", BooruTagCategory.General), new BooruTag("artist_name", BooruTagCategory.Artist)],
        [new BooruMediaVariant(BooruMediaRole.Original, new Uri("https://original.test/o.png"), ".png", 100, 640, 480, Md5),
            new BooruMediaVariant(BooruMediaRole.Sample, new Uri("https://preview.test/s.jpg"), ".jpg"),
            new BooruMediaVariant(BooruMediaRole.Preview, null)],
        "https://elsewhere.invalid/original", false, true);

    private static BooruSearchResult Page(params BooruPost[] posts) => new BooruSearchSnapshot(new("booru", PrivateQuery, 1, posts, false));
    private static IReadOnlyList<ValidationIssue> Validate(BooruSearchResult result, BooruSearchRequest? request = null, SourceDescription? source = null)
        => SdkValidation.ValidateBooruSearchResult(request ?? Request, result, source ?? Source());
    private static IReadOnlyList<ValidationIssue> ValidatePost(BooruPost post, SourceDescription? source = null)
        => SdkValidation.ValidateBooruPostResult(new(post.Id), new BooruPostSnapshot(post), source ?? Source());

    [Fact]
    public void SearchResultsOfEveryKindRoundTripAndValidate()
    {
        BooruSearchResult[] results =
        [
            Page(FullPost("1"), new BooruPost(new("booru", "2"), BooruFieldPresence.None)),
            new BooruSearchDeferred("booru", Due, "provider_cooldown", 429),
            new BooruSearchFailure("booru", new FailureDetail("http", "Provider failed.", 500))
        ];
        foreach (BooruSearchResult original in results)
        {
            BooruSearchResult copy = SdkJson.Deserialize<BooruSearchResult>(SdkJson.Serialize(original));
            Assert.Equal(original.GetType(), copy.GetType());
            Assert.Empty(Validate(copy));
        }
        BooruSearchSnapshot snapshot = Assert.IsType<BooruSearchSnapshot>(SdkJson.Deserialize<BooruSearchResult>(SdkJson.Serialize(results[0])));
        Assert.Equal(FullPost("1").Tags!.Select(tag => tag.Name), snapshot.Page.Posts[0].Tags!.Select(tag => tag.Name));
        Assert.Equal(FullPost("1").Media![0], snapshot.Page.Posts[0].Media![0]);
        Assert.Null(snapshot.Page.Posts[0].Media![2].Url);
        Assert.Null(snapshot.Page.Posts[1].Tags);
        Assert.Equal(BooruFieldPresence.None, snapshot.Page.Posts[1].Fields);
        Assert.Contains("\"kind\":\"snapshot\"", Encoding.UTF8.GetString(SdkJson.Serialize(results[0])));
    }

    [Fact]
    public void PostResultsOfEveryKindRoundTripAndValidate()
    {
        BooruPostResult[] results =
        [
            new BooruPostSnapshot(FullPost()),
            new BooruPostNotFound(Id, 404),
            new BooruPostDeferred(Id, Due, "provider_cooldown", 429),
            new BooruPostFailure(Id, new FailureDetail("http", "Provider failed.", 500))
        ];
        foreach (BooruPostResult original in results)
        {
            BooruPostResult copy = SdkJson.Deserialize<BooruPostResult>(SdkJson.Serialize(original));
            Assert.Equal(original.GetType(), copy.GetType());
            Assert.Empty(SdkValidation.ValidateBooruPostResult(new BooruPostRequest(Id), copy, Source()));
        }
        BooruPost post = Assert.IsType<BooruPostSnapshot>(SdkJson.Deserialize<BooruPostResult>(SdkJson.Serialize(results[0]))).Post;
        Assert.Equal(FullPost() with { Tags = null, Media = null }, post with { Tags = null, Media = null });
        Assert.Equal(2, post.Tags!.Count); Assert.Equal(3, post.Media!.Count); Assert.True(post.IsBanned);
        string[] kinds = ["snapshot", "notFound", "deferred", "failure"];
        for (int index = 0; index < results.Length; index++)
            Assert.Contains($"\"kind\":\"{kinds[index]}\"", Encoding.UTF8.GetString(SdkJson.Serialize(results[index])));
    }

    [Fact]
    public void RequestsPageAndPostRecordsRoundTrip()
    {
        Assert.Equal(Request, SdkJson.Deserialize<BooruSearchRequest>(SdkJson.Serialize(Request)));
        Assert.Equal(new BooruPostRequest(Id), SdkJson.Deserialize<BooruPostRequest>(SdkJson.Serialize(new BooruPostRequest(Id))));
        BooruSearchPage page = new("booru", "q", 2, [], true);
        BooruSearchPage copy = SdkJson.Deserialize<BooruSearchPage>(SdkJson.Serialize(page));
        Assert.True(copy.IsEnd); Assert.Empty(copy.Posts); Assert.Equal(2, copy.Page);
        Assert.Equal(new BooruTag("x", BooruTagCategory.Meta), SdkJson.Deserialize<BooruTag>(SdkJson.Serialize(new BooruTag("x", BooruTagCategory.Meta))));
    }

    [Theory]
    [InlineData("{\"kind\":\"futureKind\"}")]
    [InlineData("{\"kind\":\"notFound\",\"post\":{\"sourceId\":\"booru\",\"postId\":\"1\"}}")]
    public void UnknownOrWrongSearchResultKindIsRejected(string json)
        => Assert.ThrowsAny<System.Text.Json.JsonException>(() => SdkJson.Deserialize<BooruSearchResult>(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UnknownEnumNamesAndIntegersAreRejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SdkJson.Deserialize<BooruTag>(Encoding.UTF8.GetBytes("{\"name\":\"x\",\"category\":\"Nope\"}")));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SdkJson.Deserialize<BooruTag>(Encoding.UTF8.GetBytes("{\"name\":\"x\",\"category\":1}")));
    }

    [Fact]
    public void SamePostIdOnTwoSourcesStaysDistinct()
        => Assert.NotEqual(new BooruPostId("one", "42"), new BooruPostId("two", "42"));

    // Description

    [Fact]
    public void PostSearchSourceNeedsNoRoutesOrBoardCapabilities()
        => Assert.Empty(SdkValidation.ValidateDescription(Plugin()));

    [Fact]
    public void PostSearchRequiresSdk110ButStillAccepts100ForOtherSources()
    {
        Assert.Contains(SdkValidation.ValidateDescription(Plugin(sdk: "1.0.0")), issue => issue.Code == "incompatible_sdk" && issue.Path.EndsWith("sdkVersion"));
        Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin() with { SdkVersion = "1.0.0" }));
        Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin() with { SdkVersion = "1.1.0" }));
    }

    [Theory]
    [InlineData(SourceCapabilities.ThreadRead)]
    [InlineData(SourceCapabilities.BoardCatalog)]
    [InlineData(SourceCapabilities.PostCapture)]
    public void PostSearchIsNeverCombinedWithBoardThreadOrCaptureCapabilities(SourceCapabilities other)
        => Assert.Contains(SdkValidation.ValidateDescription(Plugin(Source() with { Capabilities = SourceCapabilities.PostSearch | other })),
            issue => issue.Code == "mixed_capability");

    [Fact]
    public void PostSearchRejectsBoardThreadRoutesAndRequiresAMetadataHost()
    {
        SourceDescription routed = Source() with { Routes = [new UrlRoute(SourceResourceKind.Board, "booru.test", "/{board}/", true)] };
        Assert.Contains(SdkValidation.ValidateDescription(Plugin(routed)), issue => issue.Code == "unexpected_route");
        SourceDescription noMetadata = Source([new HttpHostDeclaration("original.test", HttpHostPurpose.OriginalMedia)]);
        Assert.Contains(SdkValidation.ValidateDescription(Plugin(noMetadata)), issue => issue.Code == "missing_metadata_host");
    }

    // Requests

    [Theory]
    [InlineData("other", "q", 1, 10, "wrong_source")]
    [InlineData("booru", "a\u0001b", 1, 10, "invalid_query")]
    [InlineData("booru", "a\nb", 1, 10, "invalid_query")]
    [InlineData("booru", null, 1, 10, "missing")]
    [InlineData("booru", "q", 0, 10, "invalid_page")]
    [InlineData("booru", "q", 1001, 10, "invalid_page")]
    [InlineData("booru", "q", 1, 0, "invalid_limit")]
    [InlineData("booru", "q", 1, 101, "invalid_limit")]
    public void InvalidSearchRequestsAreRejected(string sourceId, string? query, int page, int limit, string code)
        => Assert.Contains(SdkValidation.ValidateBooruSearchRequest(new(sourceId, query!, page, limit), Source()), issue => issue.Code == code);

    [Fact]
    public void QueryLengthIsMeasuredInUtf8BytesAndBoundariesAreAccepted()
    {
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(new("booru", new string('a', 1024), 1000, 100), Source()));
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(new("booru", "", 1, 1), Source()));
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(new("booru", new string('a', 1025), 1, 1), Source()), issue => issue.Code == "invalid_query");
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(new("booru", new string('ä', 513), 1, 1), Source()), issue => issue.Code == "invalid_query");
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(new("booru", new string('ä', 512), 1, 1), Source()));
    }

    [Fact]
    public void SourceWithoutPostSearchRefusesRequests()
    {
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(Request, TestContracts.Source("booru")), issue => issue.Code == "missing_capability");
        Assert.Contains(SdkValidation.ValidateBooruPostRequest(new(Id), TestContracts.Source("booru")), issue => issue.Code == "missing_capability");
    }

    [Fact]
    public void ValidationMessagesNeverEchoTheQueryOrTagText()
    {
        BooruSearchRequest bad = Request with { Query = PrivateQuery + "\u0001" };
        BooruPost tagged = FullPost() with { Tags = [new BooruTag("secret tag", BooruTagCategory.General)] };
        IReadOnlyList<ValidationIssue> issues = [.. SdkValidation.ValidateBooruSearchRequest(bad, Source()),
            .. Validate(new BooruSearchSnapshot(new("booru", "other secret_query", 2, [tagged], false)), Request),
            .. Validate(Page(tagged))];
        Assert.NotEmpty(issues);
        foreach (ValidationIssue issue in issues)
        {
            string text = issue.Path + issue.Code + issue.Message;
            Assert.DoesNotContain("secret", text);
            Assert.DoesNotContain("rating:safe", text);
        }
    }

    // Search results

    [Fact]
    public void SearchEchoMismatchesAreRejected()
    {
        Assert.Contains(Validate(new BooruSearchSnapshot(new("booru", PrivateQuery, 2, [], false))), issue => issue.Path == "result.page.page");
        Assert.Contains(Validate(new BooruSearchSnapshot(new("booru", "other", 1, [], false))), issue => issue.Path == "result.page.query");
        Assert.Contains(Validate(new BooruSearchSnapshot(new("other", PrivateQuery, 1, [], false))), issue => issue.Path == "result.page.sourceId");
        Assert.Contains(Validate(new BooruSearchDeferred("other", Due, "x")), issue => issue.Code == "wrong_source");
        Assert.Contains(Validate(new BooruSearchFailure("other", new FailureDetail("x", "m"))), issue => issue.Code == "wrong_source");
    }

    [Fact]
    public void MoreThanLimitPostsDuplicateIdsAndForeignSourceIdsAreRejected()
    {
        Assert.Contains(Validate(Page(FullPost("1"), FullPost("2"), FullPost("3")), Request with { Limit = 2 }), issue => issue.Code == "too_large");
        Assert.Contains(Validate(Page(FullPost("1"), FullPost("1"))), issue => issue.Code == "duplicate_post");
        BooruPost foreign = FullPost("7") with { Id = new("other", "7") };
        Assert.Contains(Validate(Page(foreign)), issue => issue.Code == "wrong_source");
        Assert.Empty(Validate(Page(), Request with { Limit = 1 }));
        Assert.Empty(Validate(Page(FullPost("1"), FullPost("2")), Request with { Limit = 2 }));
    }

    [Fact]
    public void DeferredAndFailureFieldsAreBounded()
    {
        Assert.Contains(Validate(new BooruSearchDeferred("booru", default, "x")), issue => issue.Code == "missing_due_time");
        Assert.Contains(Validate(new BooruSearchDeferred("booru", Due, "")), issue => issue.Code == "invalid_identity");
        Assert.Contains(Validate(new BooruSearchDeferred("booru", Due, "x", 99)), issue => issue.Code == "invalid_http_status");
        Assert.Contains(Validate(new BooruSearchFailure("booru", new FailureDetail("x", new string('m', 3000)))), issue => issue.Code == "too_large");
    }

    [Fact]
    public void ResultValidationAlsoRejectsAnInvalidRequest()
        => Assert.Contains(Validate(Page(), Request with { Page = 0 }), issue => issue.Code == "invalid_page");

    // Post requests and results

    [Fact]
    public void PostResultsMustIdentifyTheRequestedPost()
    {
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostSnapshot(FullPost("43")), Source()), issue => issue.Code == "wrong_resource");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostNotFound(new("booru", "9"), 404), Source()), issue => issue.Code == "wrong_resource");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostNotFound(new("other", "42"), 404), Source()), issue => issue.Code == "wrong_resource");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostNotFound(Id, 403), Source()), issue => issue.Code == "not_terminal");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostDeferred(new("booru", "9"), Due, "x"), Source()), issue => issue.Code == "wrong_resource");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostFailure(new("booru", "9"), new FailureDetail("x", "m")), Source()), issue => issue.Code == "wrong_resource");
        Assert.Empty(SdkValidation.ValidateBooruPostResult(new(Id), new BooruPostNotFound(Id, 410), Source()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    [InlineData(" 42")]
    public void PostIdsMustBeNonemptyWithoutWhitespaceOrControlCharacters(string postId)
        => Assert.Contains(SdkValidation.ValidateBooruPostRequest(new(new("booru", postId)), Source()), issue => issue.Code == "invalid_identity");

    [Fact]
    public void PostIdLengthIsBounded()
    {
        Assert.Empty(SdkValidation.ValidateBooruPostRequest(new(new("booru", new string('1', SdkLimits.MaximumIdentifierLength))), Source()));
        Assert.Contains(SdkValidation.ValidateBooruPostRequest(new(new("booru", new string('1', SdkLimits.MaximumIdentifierLength + 1))), Source()), issue => issue.Code == "invalid_identity");
    }

    // Shared post checks

    [Fact]
    public void ValidFullPostAndMinimalPostPass()
    {
        Assert.Empty(ValidatePost(FullPost()));
        Assert.Empty(ValidatePost(new BooruPost(Id, BooruFieldPresence.None)));
        Assert.Empty(ValidatePost(new BooruPost(Id, BooruFieldPresence.Tags | BooruFieldPresence.Media, Tags: [], Media: [])));
    }

    [Fact]
    public void ValuesWithoutTheirPresenceFlagAreRejected()
    {
        BooruPost none = new(Id, BooruFieldPresence.None);
        BooruPost[] posts =
        [
            none with { Tags = [] }, none with { Media = [] }, none with { CreatedAt = Due }, none with { Rating = BooruRating.General },
            none with { RawRating = "g" }, none with { Score = 0 }, none with { FavoriteCount = 0 }, none with { SourceClaim = "" },
            none with { IsDeleted = true }, none with { IsBanned = true }
        ];
        foreach (BooruPost post in posts)
            Assert.Contains(ValidatePost(post), issue => issue.Code == "presence_mismatch");
    }

    [Fact]
    public void UnknownPresenceBitsAndUnknownEnumsAreRejected()
    {
        Assert.Contains(ValidatePost(new BooruPost(Id, (BooruFieldPresence)(1 << 12))), issue => issue.Code == "unknown_presence");
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.Rating, Rating: (BooruRating)99)), issue => issue.Code == "unknown_value");
        Assert.Contains(ValidatePost(FullPost() with { Tags = [new BooruTag("x", (BooruTagCategory)99)] }), issue => issue.Code == "unknown_value");
        Assert.Contains(ValidatePost(FullPost() with { Media = [new BooruMediaVariant((BooruMediaRole)9, null)] }), issue => issue.Code == "unknown_value");
    }

    [Fact]
    public void TagLimitsAndNamesAreEnforcedWithoutEchoingText()
    {
        BooruPost With(IReadOnlyList<BooruTag> tags) => new(Id, BooruFieldPresence.Tags, Tags: tags);
        Assert.Empty(ValidatePost(With(Enumerable.Range(0, 4096).Select(index => new BooruTag("t" + index, BooruTagCategory.General)).ToArray())));
        Assert.Contains(ValidatePost(With(Enumerable.Range(0, 4097).Select(index => new BooruTag("t" + index, BooruTagCategory.General)).ToArray())), issue => issue.Code == "too_large");
        foreach (string name in new[] { "", "two words", "tab\t", "ctl\u0001", new string('a', 257), new string('ä', 129) })
            Assert.Contains(ValidatePost(With([new BooruTag(name, BooruTagCategory.General)])), issue => issue.Code == "invalid_tag");
        Assert.Empty(ValidatePost(With([new BooruTag(new string('a', 256), BooruTagCategory.General), new BooruTag("äö", BooruTagCategory.Unknown)])));
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.Tags, Tags: null)), issue => issue.Code == "missing");
    }

    [Fact]
    public void MediaRolesAreUniqueAndAtMostThreeVariantsAreAllowed()
    {
        BooruMediaVariant original = new(BooruMediaRole.Original, new Uri("https://original.test/o.png"));
        BooruPost With(params BooruMediaVariant[] media) => new(Id, BooruFieldPresence.Media, Media: media);
        Assert.Contains(ValidatePost(With(original, original)), issue => issue.Code == "duplicate_role");
        Assert.Contains(ValidatePost(With(original, original, original, original)), issue => issue.Code == "too_large");
        Assert.Empty(ValidatePost(With(original, new(BooruMediaRole.Sample, null), new(BooruMediaRole.Preview, null))));
    }

    [Theory]
    [InlineData(BooruMediaRole.Original, "https://undeclared.test/o.png", "host_not_allowed")]
    [InlineData(BooruMediaRole.Original, "http://original.test/o.png", "invalid_url")]
    [InlineData(BooruMediaRole.Original, "https://preview.test/o.png", "host_not_allowed")]
    [InlineData(BooruMediaRole.Original, "https://booru.test/o.png", "host_not_allowed")]
    [InlineData(BooruMediaRole.Sample, "https://original.test/s.jpg", "host_not_allowed")]
    [InlineData(BooruMediaRole.Preview, "https://original.test/p.jpg", "host_not_allowed")]
    [InlineData(BooruMediaRole.Preview, "https://user:pw@preview.test/p.jpg", "invalid_url")]
    public void MediaUrlsMustBeHttpsOnAHostDeclaredForTheirRole(BooruMediaRole role, string url, string code)
        => Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.Media, Media: [new BooruMediaVariant(role, new Uri(url))])), issue => issue.Code == code);

    [Fact]
    public void PreviewUrlOnAnOriginalMediaOnlyHostIsRejectedButOriginalMediaHostServesOriginal()
    {
        SourceDescription source = Source([new HttpHostDeclaration("booru.test", HttpHostPurpose.Metadata), new HttpHostDeclaration("cdn.test", HttpHostPurpose.OriginalMedia)]);
        Assert.Empty(ValidatePost(new BooruPost(Id, BooruFieldPresence.Media, Media: [new BooruMediaVariant(BooruMediaRole.Original, new Uri("https://cdn.test/o.png"))]), source));
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.Media, Media: [new BooruMediaVariant(BooruMediaRole.Preview, new Uri("https://cdn.test/p.png"))]), source), issue => issue.Code == "host_not_allowed");
    }

    [Fact]
    public void MediaClaimsAreChecked()
    {
        BooruPost With(BooruMediaVariant variant) => new(Id, BooruFieldPresence.Media, Media: [variant]);
        BooruMediaVariant ok = new(BooruMediaRole.Original, new Uri("https://original.test/o.png"));
        Assert.Contains(ValidatePost(With(ok with { ExpectedSize = -1 })), issue => issue.Code == "invalid_size");
        Assert.Contains(ValidatePost(With(ok with { Width = 0 })), issue => issue.Path.EndsWith("width"));
        Assert.Contains(ValidatePost(With(ok with { Height = -5 })), issue => issue.Path.EndsWith("height"));
        Assert.Contains(ValidatePost(With(ok with { ExpectedMd5 = "not base64!" })), issue => issue.Code == "invalid_hash");
        Assert.Contains(ValidatePost(With(ok with { ExpectedMd5 = Convert.ToBase64String(new byte[15]) })), issue => issue.Code == "invalid_hash");
        Assert.Contains(ValidatePost(With(ok with { ExpectedMd5 = Convert.ToBase64String(new byte[20]) })), issue => issue.Code == "invalid_hash");
        Assert.Contains(ValidatePost(With(ok with { Extension = "png" })), issue => issue.Code == "invalid_extension");
        Assert.Empty(ValidatePost(With(ok with { ExpectedSize = 0, Width = 1, Height = 1, ExpectedMd5 = Md5, Extension = ".webm" })));
    }

    [Theory]
    [InlineData("https://undeclared.test/posts/1", "host_not_allowed")]
    [InlineData("http://booru.test/posts/1", "invalid_url")]
    [InlineData("https://original.test/posts/1", "host_not_allowed")]
    public void PostUrlMustBeHttpsOnADeclaredMetadataHost(string url, string code)
        => Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.None, new Uri(url))), issue => issue.Code == code);

    [Fact]
    public void TextClaimsAreBounded()
    {
        Assert.Empty(ValidatePost(new BooruPost(Id, BooruFieldPresence.SourceClaim | BooruFieldPresence.Rating, SourceClaim: new string('s', 4096), RawRating: new string('r', 16))));
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.SourceClaim, SourceClaim: new string('s', 4097))), issue => issue.Path.EndsWith("sourceClaim"));
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.Rating, RawRating: new string('r', 17))), issue => issue.Path.EndsWith("rawRating"));
        Assert.Contains(ValidatePost(new BooruPost(Id, BooruFieldPresence.FavoriteCount, FavoriteCount: -1)), issue => issue.Code == "invalid_count");
    }

    // Cursor paging (BeforePostId) and the decimal order declaration

    private static SourceDescription Decimal() => Source() with { PostOrder = PostOrderKind.DecimalPostId };

    private static BooruSearchResult CursorPage(bool isEnd, string? next, params BooruPost[] posts)
        => new BooruSearchSnapshot(new("booru", PrivateQuery, 1, posts, isEnd, next));

    [Fact]
    public void CursorRequestsAndPagesRoundTripAndExistingPageCallersStillWork()
    {
        BooruSearchRequest cursor = Request with { BeforePostId = "1000" };
        Assert.Equal(cursor, SdkJson.Deserialize<BooruSearchRequest>(SdkJson.Serialize(cursor)));
        Assert.Contains("\"beforePostId\":\"1000\"", Encoding.UTF8.GetString(SdkJson.Serialize(cursor)));
        Assert.Null(new BooruSearchRequest("booru", "q", 3, 20).BeforePostId);
        // A request without the field (an older host) is a numbered request.
        Assert.Null(SdkJson.Deserialize<BooruSearchRequest>(Encoding.UTF8.GetBytes("{\"sourceId\":\"booru\",\"query\":\"q\",\"page\":2,\"limit\":20}")).BeforePostId);
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(new("booru", "q", 3, 20), Source()));
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(new("booru", "q", 3, 20), Decimal()));
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(cursor, Decimal()));
        BooruSearchPage page = new("booru", "q", 1, [], false, "900");
        Assert.Equal("900", SdkJson.Deserialize<BooruSearchPage>(SdkJson.Serialize(page)).NextBeforePostId);
        Assert.Null(SdkJson.Deserialize<BooruSearchPage>(Encoding.UTF8.GetBytes("{\"sourceId\":\"booru\",\"query\":\"q\",\"page\":1,\"posts\":[],\"isEnd\":true}")).NextBeforePostId);
        Assert.Empty(Validate(CursorPage(false, "2", FullPost("3"), FullPost("2")), cursor, Decimal()));
        Assert.Empty(Validate(CursorPage(true, null, FullPost("999")), cursor, Decimal()));
        Assert.Empty(Validate(CursorPage(true, null), cursor, Decimal()));
        // A numbered request to a decimal source is told its continuation too.
        Assert.Empty(Validate(CursorPage(false, "7", FullPost("8"), FullPost("7")), source: Decimal()));
        // Opaque-id sources keep working without any cursor.
        Assert.Empty(Validate(CursorPage(false, null, FullPost("2"))));
    }

    [Fact]
    public void OnlyDecimalOrderedSourcesAcceptOrOfferACursor()
    {
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = "5" }, Source()),
            issue => issue.Code == "cursor_unsupported" && issue.Path == "request.beforePostId");
        Assert.Contains(Validate(CursorPage(false, "2", FullPost("2"))), issue => issue.Code == "cursor_unsupported" && issue.Path == "result.page.nextBeforePostId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("007")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1a")]
    [InlineData("b1000")]
    [InlineData(" 1")]
    [InlineData("1\n")]
    [InlineData("1&x=2")]
    [InlineData("%31")]
    [InlineData("１")]
    public void CursorsAreCanonicalDecimalPostIds(string value)
    {
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = value }, Decimal()),
            issue => issue.Code == "invalid_cursor" && issue.Path == "request.beforePostId");
        Assert.Contains(Validate(CursorPage(false, value, FullPost("2")), source: Decimal()),
            issue => issue.Code == "invalid_cursor" && issue.Path == "result.page.nextBeforePostId");
    }

    [Fact]
    public void CursorLengthBoundariesFollowTheIdentifierLimit()
    {
        string longest = "1" + new string('0', SdkLimits.MaximumIdentifierLength - 1);
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = "1" }, Decimal()));
        Assert.Empty(SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = longest }, Decimal()));
        Assert.Contains(SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = longest + "0" }, Decimal()),
            issue => issue.Code == "invalid_cursor");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(1000)]
    public void CursorAndPageNumberAreMutuallyExclusive(int page)
        => Assert.Contains(SdkValidation.ValidateBooruSearchRequest(Request with { Page = page, BeforePostId = "5" }, Decimal()),
            issue => issue.Code == "ambiguous_page" && issue.Path == "request.page");

    [Fact]
    public void NextCursorIsTheLowestIdOfANonFinalPage()
    {
        BooruSearchRequest cursor = Request with { BeforePostId = "10" };
        Assert.Contains(Validate(CursorPage(true, "1", FullPost("1")), cursor, Decimal()), issue => issue.Code == "end_with_cursor");
        Assert.Contains(Validate(CursorPage(false, null, FullPost("2")), cursor, Decimal()), issue => issue.Code == "missing_cursor");
        Assert.DoesNotContain(Validate(CursorPage(false, null, FullPost("2"))), issue => issue.Code == "missing_cursor");
        Assert.Contains(Validate(CursorPage(false, "3", FullPost("3"), FullPost("2")), cursor, Decimal()), issue => issue.Code == "invalid_cursor");
        Assert.Contains(Validate(CursorPage(false, "10", FullPost("2")), cursor, Decimal()), issue => issue.Code == "invalid_cursor");
        Assert.Contains(Validate(CursorPage(false, "2"), cursor, Decimal()), issue => issue.Code == "invalid_cursor");
        // An empty page that is not final has nothing to continue below; it needs no cursor.
        Assert.DoesNotContain(Validate(CursorPage(false, null), cursor, Decimal()), issue => issue.Code == "missing_cursor");
    }

    [Fact]
    public void CursorPagesListOnlyPostsBelowTheCursor()
    {
        BooruSearchRequest cursor = Request with { BeforePostId = "10" };
        Assert.Contains(Validate(CursorPage(true, null, FullPost("10")), cursor, Decimal()),
            issue => issue.Code == "cursor_not_advanced" && issue.Path == "result.page.posts[0].id.postId");
        Assert.Contains(Validate(CursorPage(true, null, FullPost("12"), FullPost("9")), cursor, Decimal()), issue => issue.Code == "cursor_not_advanced");
        Assert.Empty(Validate(CursorPage(true, null, FullPost("9"), FullPost("1")), cursor, Decimal()));
    }

    [Fact]
    public void CursorValidationNeverEchoesTheCursor()
    {
        const string secret = "8675309";
        IReadOnlyList<ValidationIssue> issues = [.. SdkValidation.ValidateBooruSearchRequest(Request with { BeforePostId = secret + "x", Page = 2 }, Decimal()),
            .. Validate(CursorPage(true, secret + "#"), source: Decimal())];
        Assert.NotEmpty(issues);
        Assert.All(issues, issue => Assert.DoesNotContain(secret, issue.Path + issue.Code + issue.Message));
    }

    [Fact]
    public void DecimalOrderedSearchPagesListStrictlyDescendingCanonicalIds()
    {
        Assert.Empty(SdkValidation.ValidateDescription(Plugin(Decimal())));
        Assert.Empty(Validate(CursorPage(false, "9", FullPost("1000"), FullPost("999"), FullPost("10"), FullPost("9")), source: Decimal()));
        Assert.Empty(Validate(CursorPage(true, null, FullPost("100000000000000000000000000002"), FullPost("100000000000000000000000000001")), source: Decimal()));
        Assert.Contains(Validate(CursorPage(true, null, FullPost("9"), FullPost("10")), source: Decimal()), issue => issue.Code == "invalid_order" && issue.Path == "result.page.posts[1].id.postId");
        Assert.Contains(Validate(CursorPage(true, null, FullPost("5"), FullPost("5")), source: Decimal()), issue => issue.Code == "invalid_order");
        // StableOrdinal (the default) promises no order: ascending or opaque ids stay valid.
        Assert.Empty(Validate(Page(FullPost("9"), FullPost("10"), FullPost("x-1"))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("007")]
    [InlineData("-1")]
    [InlineData("1a")]
    [InlineData("１")]
    public void DecimalOrderedSourcesUseCanonicalPositiveDecimalIds(string postId)
    {
        Assert.Contains(Validate(CursorPage(true, null, FullPost(postId)), source: Decimal()), issue => issue.Code == "invalid_order");
        Assert.Contains(SdkValidation.ValidateBooruPostRequest(new(new("booru", postId)), Decimal()), issue => issue.Code == "invalid_order");
        Assert.Contains(SdkValidation.ValidateBooruPostResult(new(new("booru", "1")), new BooruPostSnapshot(FullPost(postId)), Decimal()),
            issue => issue.Code == "invalid_order");
        Assert.DoesNotContain(SdkValidation.ValidateBooruPostRequest(new(new("booru", postId)), Source()), issue => issue.Code == "invalid_order");
    }
}
