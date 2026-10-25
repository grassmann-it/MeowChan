using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.SushiChan;
using MeowChan.Plugins.Examples.Lainchan;
using MeowChan.Plugins.Examples.Kohlchan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class SushiChanPluginTests
{
    [Fact]
    public void DescriptionIsPassiveSdkOnlyAndDeclaresOnlyProvenRoutesAndGrants()
    {
        PluginDescription description = new SushiChanPlugin().Describe();
        SourceDescription source = Assert.Single(description.Sources);
        Assert.Equal("meowchan.source.sushichan", description.PluginId);
        Assert.Equal("sushichan", source.SourceId);
        Assert.Empty(SdkValidation.ValidateDescriptions([new LainchanPlugin().Describe(), new KohlchanPlugin().Describe(), description]));
        Assert.Empty(description.Settings.Fields);
        Assert.Empty(source.Settings.Fields);
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia, source.Capabilities);
        Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal("/{board}/", Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Board && r.IsCanonical).PathTemplate);
        Assert.Equal(2, source.Routes.Count); // No unverified old-domain or index alias.
        UrlRoute thread = Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Thread);
        Assert.Equal("/{board}/res/{thread}.html", thread.PathTemplate);
        Assert.Equal(RouteIdentifierKind.Decimal, thread.ThreadIdentifierKind);
        Assert.All(source.Routes, r => { Assert.Equal("sushigirl.cafe", r.Host); Assert.False(r.AllowTitleSegment); Assert.False(r.LowercaseBoardId); });
        Assert.Equal(new HttpHostDeclaration("sushigirl.cafe", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia), Assert.Single(source.HttpHosts));
        string[] references = typeof(SushiChanPlugin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Contains("MeowChan.PluginSdk", references);
        Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
        Assert.Single(typeof(SushiChanPlugin).Assembly.GetExportedTypes(), t => typeof(IPlugin).IsAssignableFrom(t));
        SourceDescription collision = source with { SourceId = "other-sushichan" };
        Assert.NotEmpty(SdkValidation.ValidateDescriptions([description, description with { PluginId = "other.sushichan", Sources = [collision] }]));
    }

    [Fact]
    public async Task ThreadCarriesIndependentPostAndFileIdentitiesAndInertHtml()
    {
        Harness h = new(Fixture("thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        ThreadSnapshot copy = Assert.IsType<ThreadSnapshot>(SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result)));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        Assert.Equal(new[] {"123", "124", "125"}, result.Snapshot.Posts.Select(p => p.PostId).ToArray());
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal("<p>Synthetic text</p>", op.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), op.PostedAt);
        Assert.Equal(new[] {0, 1, 2, 3}, op.Attachments!.Select(a => a.Position).ToArray());
        Assert.Equal(new[] {"123-0", "123-1", "123-2", "123-3"}, op.Attachments!.Select(a => a.MediaId).ToArray());
        Assert.Equal("https://sushigirl.cafe/demo/src/1000-0.png", op.Attachments![0].Url!.AbsoluteUri);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA==", op.Attachments![0].ExpectedMd5);
        Assert.All(op.Attachments!, a => Assert.Equal("sushigirl.cafe", a.Url!.Host));
        Assert.Null(result.Snapshot.Posts[2].Attachments); // embed is not a host media grant.
        HostHttpRequest request = Assert.Single(h.Http.Requests);
        Assert.Equal("https://sushigirl.cafe/demo/res/123.json", request.Url.AbsoluteUri);
        Assert.Equal(HostHttpMethod.Get, request.Method);
        Assert.Equal(h.Validators, result.Snapshot.Validators);
    }

    [Fact]
    public async Task CatalogFlattensPagesWithoutInferringCoverageOrEnd()
    {
        Harness h = new(Fixture("catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Null(result.Snapshot.NextPage);
        Assert.Null(result.Snapshot.NextCursor);
        Assert.Equal(new[] {"123", "456"}, result.Snapshot.Threads.Select(t => t.Id.ThreadId).ToArray());
        Assert.Equal("https://sushigirl.cafe/demo/res/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
        Assert.Equal("https://sushigirl.cafe/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest,
            SdkJson.Deserialize<CatalogReadResult>(SdkJson.Serialize<CatalogReadResult>(result)), h.Source));
    }

    [Fact]
    public async Task MissingEmptyAndNullFieldsRemainDistinctAndDoNotDeleteHistory()
    {
        SourcePost partial = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(Fixture("partial")).ReadThread()).Snapshot.Posts, p => p.PostId == "124");
        Assert.Equal(SourceFieldPresence.Text, partial.Fields);
        Assert.Equal("", partial.Text);
        Assert.Null(partial.Attachments);
        SourcePost explicitNull = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness("""{"posts":[{"no":123,"com":null,"name":null,"sub":null,"time":null,"extra_files":[]}]}""").ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text | SourceFieldPresence.Author | SourceFieldPresence.Subject | SourceFieldPresence.PostedAt | SourceFieldPresence.Attachments, explicitNull.Fields);
        Assert.Null(explicitNull.Text);
        Assert.Null(explicitNull.Author);
        Assert.Null(explicitNull.PostedAt);
        Assert.Empty(explicitNull.Attachments!);
        Assert.False(explicitNull.IsDeleted);
    }

    [Fact]
    public async Task ExtraOnlyFileRetainsSlotAndUnverifiedFlagsAreNotTerminal()
    {
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness("""{"posts":[{"no":123,"archived":1,"locked":1,"cyclical":"1","omitted_posts":42,"extra_files":[{"tim":"opaque_1-2","ext":".png"}]}]}""").ReadThread());
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        SourceAttachment file = Assert.Single(result.Snapshot.Posts[0].Attachments!);
        Assert.Equal(1, file.Position);
        Assert.Equal("123-1", file.MediaId);
        Assert.Equal("https://sushigirl.cafe/demo/src/opaque_1-2.png", file.Url!.AbsoluteUri);
    }

    [Fact]
    public async Task FileIdentityDoesNotDependOnFilenameOrUrlAndLargeDecimalParentsStayStrings()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        SourceAttachment first = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        root["posts"]![0]!["filename"] = "renamed";
        root["posts"]![0]!["tim"] = "different-0";
        SourceAttachment next = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(first.MediaId, next.MediaId);
        Assert.NotEqual(first.Url, next.Url);
        const string large = "123456789012345678901234567890";
        Harness h = new("""{"posts":[{"no":123456789012345678901234567890,"resto":0},{"no":123456789012345678901234567891,"resto":123456789012345678901234567890}]}""");
        ThreadReadRequest request = h.ThreadRequest with { Thread = h.ThreadRequest.Thread with { ThreadId = large } };
        Assert.Empty(SdkValidation.ValidateThreadResult(request, Assert.IsType<ThreadSnapshot>(await h.Plugin.ReadThreadAsync(request, h.Context)), h.Source));
    }

    [Theory]
    [InlineData(403)] [InlineData(410)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(206)]
    public async Task NonSuccessIsNotAThreadEnd(int status)
    {
        Harness h = new("{}", status);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
    }

    [Fact]
    public async Task OnlyExactThread404IsNotFoundAndFinalEndpointIsCheckedFirst()
    {
        Harness h = new("{}", 404);
        Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://sushigirl.cafe/other/res/123.json");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        h.Http.FinalUri = new Uri("https://www.sushigirl.cafe/demo/res/123.json");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
    }

    [Fact]
    public async Task ConditionalReadRequiresAcceptedHttpBaselineAndKeepsItsValidators()
    {
        Harness h = new("", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators accepted = new(EntityTag: "\"accepted\"");
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = accepted };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = accepted };
        Assert.Equal(accepted, Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context)).Validators);
        Assert.Equal(accepted, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(accepted, h.Http.Requests.Last().Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new SourceValidators(Revision: "not-http") }, h.Context));
    }

    [Fact]
    public async Task DeferredFailuresAndCancellationRemainHostOwned()
    {
        Harness h = new("{}");
        DateTimeOffset retry = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        h.Http.Result = new HostHttpDeferred(retry, "cooldown", 429);
        Assert.Equal(retry, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(retry, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        FailureDetail failure = new("timeout", "Synthetic timeout");
        h.Http.Result = new HostHttpFailure(failure);
        Assert.Equal(failure, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure);
        Assert.Equal(failure, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        int before = h.Http.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cts.Token));
        Assert.Equal(before, h.Http.Requests.Count);
        using CancellationTokenSource late = new();
        h.Http.OnSend = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadCatalogAsync(h.CatalogRequest, h.Context, late.Token));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"posts\":{}}")]
    [InlineData("{\"posts\":[{\"no\":123},{\"no\":123}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"resto\":999}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"tim\":\"../escape\",\"ext\":\".png\"}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"tim\":\"ok\",\"ext\":\".png?x\"}]}")]
    public async Task MalformedThreadIsFailureRatherThanPartialSuccess(string body) => Assert.IsType<ThreadFailure>(await new Harness(body).ReadThread());

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task NonTokenJsonKindsCannotBecomePrimaryOrExtraMediaPaths(string rawToken)
    {
        string primary = "{\"posts\":[{\"no\":123,\"tim\":" + rawToken + ",\"ext\":\".png\"}]}";
        string extra = "{\"posts\":[{\"no\":123,\"extra_files\":[{\"tim\":" + rawToken + ",\"ext\":\".png\"}]}]}";
        Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(await new Harness(primary).ReadThread()).Failure.Code);
        Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(await new Harness(extra).ReadThread()).Failure.Code);
    }

    [Fact]
    public async Task LiteralOpaqueStringTokensAreNotConfusedWithJsonKeywords()
    {
        foreach (string token in new[] { "null", "true", "false" })
        {
            string body = "{\"posts\":[{\"no\":123,\"tim\":\"" + token + "\",\"ext\":\".png\"}]}";
            SourceAttachment file = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(body).ReadThread()).Snapshot.Posts[0].Attachments!);
            Assert.Equal("https://sushigirl.cafe/demo/src/" + token + ".png", file.Url!.AbsoluteUri);
        }
    }

    [Fact]
    public async Task CatalogAndResourceLimitsRejectWithoutRequestsOrTruncation()
    {
        Harness h = new(Fixture("catalog"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId("sushichan", "../escape", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        JsonNode catalog = JsonNode.Parse(Fixture("catalog"))!;
        catalog[1]!["threads"]![0]!["no"] = 123;
        Assert.IsType<CatalogFailure>(await new Harness(catalog.ToJsonString()).ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness("[{}]").ReadCatalog());
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        JsonArray files = root["posts"]![0]!["extra_files"]!.AsArray();
        JsonNode file = files[0]!.DeepClone();
        files.Clear();
        for (int i = 0; i < 1024; i++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task ExplicitBoardIdentityIsEscapedOnceAndRetainsCase()
    {
        Harness h = new(Fixture("catalog"));
        CatalogReadRequest request = new(new SourceBoardId("sushichan", "Kühl"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.Plugin.ReadCatalogAsync(request, h.Context));
        Assert.Equal("Kühl", result.Snapshot.Board.BoardId);
        Assert.Equal("/K%C3%BChl/catalog.json", Assert.Single(h.Http.Requests).Url.AbsolutePath);
        Assert.Empty(SdkValidation.ValidateCatalogResult(request, result, h.Source));
    }

    [Fact]
    public async Task ExplicitDeletedPrimaryPreservesExtraSlotsAndNonImageAttachments()
    {
        Harness h = new("""{"posts":[{"no":123,"filedeleted":1,"extra_files":[{"tim":"opaque","ext":".pdf","filename":"book","fsize":12}]}]}""");
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await h.ReadThread()).Snapshot.Posts);
        Assert.True(post.Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.True(post.Attachments![0].IsDeleted);
        Assert.Null(post.Attachments[0].Url);
        Assert.Equal(1, post.Attachments[1].Position);
        Assert.Equal("book.pdf", post.Attachments[1].OriginalFileName);
        Assert.Equal("https://sushigirl.cafe/demo/src/opaque.pdf", post.Attachments[1].Url!.AbsoluteUri);
        // File policy/gallery suitability is resolved by the host, not by dropping attachments here.
        Assert.False(h.Source.Capabilities.HasFlag(SourceCapabilities.Previews));
    }

    [Fact]
    public async Task ProviderHashesAndExactSizesRemainUnmodifiedAndMalformedHashesFail()
    {
        Harness h = new("""{"posts":[{"no":123,"tim":"one","ext":".webp","fsize":24524,"md5":"AAAAAAAAAAAAAAAAAAAAAA=="}]}""");
        SourceAttachment attachment = Assert.Single(Assert.IsType<ThreadSnapshot>(await h.ReadThread()).Snapshot.Posts[0].Attachments!);
        Assert.Equal(24524, attachment.ExpectedSize);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAA==", attachment.ExpectedMd5);
        Assert.IsType<ThreadFailure>(await new Harness("""{"posts":[{"no":123,"tim":"one","ext":".webp","md5":"not-base64"}]}""").ReadThread());
        // The adapter never downloads an original to replace an expected provider digest.
        Assert.Single(h.Http.Requests);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sushichan-" + name + ".json"));

    private sealed class Harness
    {
        internal SushiChanPlugin Plugin { get; } = new();
        internal SourceDescription Source => Plugin.Describe().Sources[0];
        internal SourceValidators Validators { get; } = new(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; } = new(new SourceThreadId("sushichan", "demo", "123"));
        internal CatalogReadRequest CatalogRequest { get; } = new(new SourceBoardId("sushichan", "demo"));
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string body, int status = 200)
        {
            Http = new(Encoding.UTF8.GetBytes(body), status, Validators);
            SettingsSnapshot empty = new("1", Array.Empty<SettingValue>());
            NoOtherServices none = new();
            Context = new(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { ["sushichan"] = empty }), Http, none, none, none);
        }
        internal Task<ThreadReadResult> ReadThread() => Plugin.ReadThreadAsync(ThreadRequest, Context);
        internal Task<CatalogReadResult> ReadCatalog() => Plugin.ReadCatalogAsync(CatalogRequest, Context);
    }

    private sealed class StubHttp(byte[] body, int status, SourceValidators validators) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = new();
        internal byte[] Body { get; set; } = body;
        internal Uri? FinalUri { get; set; }
        internal HostHttpResult? Result { get; set; }
        internal Action? OnSend { get; set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            OnSend?.Invoke();
            return Task.FromResult(Result ?? new HostHttpResponse(status, FinalUri ?? request.Url, new Dictionary<string, string>(), Body, validators));
        }
    }

    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No image reads in source parser.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No single-file resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Do not log fixture source content.");
    }
}
