using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Endchan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class EndchanPluginTests
{
    [Fact]
    public void DescriptionIsInertSdkOnlyAndDeclaresOnlyEvidencedHostsAndCapabilities()
    {
        PluginDescription description = new EndchanPlugin().Describe();
        Assert.Equal("meowchan.source.endchan", description.PluginId);
        Assert.Empty(SdkValidation.ValidateDescriptions([description]));
        Assert.Empty(description.Settings.Fields);
        Assert.Empty(description.Filters);
        SourceDescription source = Assert.Single(description.Sources);
        Assert.Equal("endchan", source.SourceId);
        Assert.Equal(new Uri("https://endchan.net/"), source.BaseUri);
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia, source.Capabilities);
        Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal(4, source.Routes.Count);
        foreach (SourceResourceKind kind in new[] { SourceResourceKind.Board, SourceResourceKind.Thread })
        {
            UrlRoute canonical = Assert.Single(source.Routes, route => route.Kind == kind && route.IsCanonical);
            Assert.Equal("endchan.net", canonical.Host);
            UrlRoute alias = Assert.Single(source.Routes, route => route.Kind == kind && !route.IsCanonical);
            Assert.Equal("endchan.org", alias.Host);
            Assert.Equal(canonical.PathTemplate, alias.PathTemplate);
            Assert.Equal(kind == SourceResourceKind.Board ? "/{board}/" : "/{board}/res/{thread}.html", canonical.PathTemplate);
        }
        Assert.All(source.Routes, route =>
        {
            Assert.Equal(RouteIdentifierKind.AsciiWord, route.BoardIdentifierKind);
            Assert.False(route.LowercaseBoardId);
            Assert.False(route.AllowTitleSegment);
            if (route.Kind == SourceResourceKind.Thread)
            {
                Assert.Equal(RouteIdentifierKind.Decimal, route.ThreadIdentifierKind);
                Assert.False(route.AllowTrailingSlash);
            }
        });
        HttpHostDeclaration host = Assert.Single(source.HttpHosts);
        Assert.Equal("endchan.net", host.Host);
        Assert.Equal(HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia, host.Purpose);
        Type type = typeof(EndchanPlugin);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.Single(type.Assembly.GetExportedTypes(), item => typeof(IPlugin).IsAssignableFrom(item));
        string[] references = type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        Assert.Contains("MeowChan.PluginSdk", references);
        Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia" or "System.Net.Http");
    }

    [Fact]
    public void RouteOwnershipIsDistinctFromExistingExamplesAndConflictsFailExplicitly()
    {
        PluginDescription description = new EndchanPlugin().Describe();
        Assert.Empty(SdkValidation.ValidateDescriptions([
            new MeowChan.Plugins.Examples.Lainchan.LainchanPlugin().Describe(),
            new MeowChan.Plugins.Examples.Kohlchan.KohlchanPlugin().Describe(), description]));
        SourceDescription otherSource = description.Sources[0] with { SourceId = "other" };
        PluginDescription overlapping = description with { PluginId = "fixture.other", Sources = [otherSource] };
        Assert.Contains(SdkValidation.ValidateDescriptions([description, overlapping]), issue => issue.Code == "route_conflict");
    }

    [Fact]
    public async Task ThreadPreservesPostAndOrderedMediaIdentityAndRoundTripsThroughSdk()
    {
        Harness h = new Harness(Fixture("thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        Assert.Equal(new[] { "123", "124" }, result.Snapshot.Posts.Select(post => post.PostId));
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal("<p>Synthetic fixture</p>", op.Text);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), op.PostedAt);
        Assert.Equal(new[] { 0, 1 }, op.Attachments!.Select(file => file.Position));
        Assert.Equal(new[] { "123-0", "123-1" }, op.Attachments!.Select(file => file.MediaId));
        Assert.Equal(new[] { "https://endchan.net/.media/endchan-keep.png", "https://endchan.net/.media/endchan-drop.png" }, op.Attachments!.Select(file => file.Url!.AbsoluteUri));
        Assert.All(op.Attachments!, file =>
        {
            Assert.Equal("same-name.png", file.OriginalFileName);
            Assert.Equal("image/png", file.MimeType);
            Assert.Equal(".png", file.Extension);
            Assert.Null(file.ExpectedMd5);
            Assert.Null(file.ExpectedSha256);
        });
        Assert.Equal(new long?[] { 32, 48 }, op.Attachments!.Select(file => file.ExpectedSize));
        Assert.Equal(SourceTextFormat.PlainText, result.Snapshot.Posts[1].TextFormat);
        Assert.Equal(h.Validators, result.Snapshot.Validators);
        HostHttpRequest sent = Assert.Single(h.Http.Requests);
        Assert.Equal("https://endchan.net/demo/res/123.json", sent.Url.AbsoluteUri);
        Assert.Equal(HostHttpMethod.Get, sent.Method);
        Assert.Equal("application/json", sent.Headers!["Accept"]);
        Assert.Null(sent.Validators);
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(2, Assert.IsType<ThreadSnapshot>(copy).Snapshot.Posts[0].Attachments!.Count);
    }

    [Fact]
    public async Task FlatCatalogUsesKnownBoardAndNeverMislabelsReplyFilesAsTotalAttachments()
    {
        Harness h = new Harness(Fixture("catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Null(result.Snapshot.NextPage);
        Assert.Null(result.Snapshot.NextCursor);
        SourceThreadReference thread = Assert.Single(result.Snapshot.Threads);
        Assert.Equal("123", thread.Id.ThreadId);
        Assert.Equal("https://endchan.net/demo/res/123.html", thread.Url.AbsoluteUri);
        Assert.Equal(SourceTextFormat.Html, thread.OpeningTextFormat);
        Assert.Equal(1, thread.ReplyCount);
        Assert.Null(thread.AttachmentCount);
        Assert.Equal("https://endchan.net/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        CatalogReadResult copy = SdkJson.Deserialize<CatalogReadResult>(SdkJson.Serialize<CatalogReadResult>(result));
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, copy, h.Source));
    }

    [Fact]
    public async Task PartialFieldsEmptyAttachmentsAndExplicitNullRemainDistinct()
    {
        Harness h = new Harness(Fixture("partial"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        SourcePost reply = Assert.Single(result.Snapshot.Posts, post => post.PostId == "124");
        Assert.Equal(SourceFieldPresence.Text, reply.Fields);
        Assert.Equal("", reply.Text);
        Assert.Null(reply.Attachments);
        Assert.False(reply.IsDeleted);
        Assert.Equal(SourceFieldPresence.None, result.Snapshot.Posts[0].Fields);
        Assert.Null(result.Snapshot.Title);
        Assert.All(result.Snapshot.Posts, post => Assert.Equal(0, post.Ordinal));
        const string empty = """{"threadId":123,"boardUri":"demo","files":[],"posts":[]}""";
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(empty).ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Attachments, post.Fields);
        Assert.Empty(post.Attachments!);
        Assert.False(post.IsDeleted);
        const string explicitNull = """{"threadId":123,"boardUri":"demo","markdown":null,"message":"not selected","name":null,"subject":null,"creation":null,"posts":[]}""";
        post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(explicitNull).ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text | SourceFieldPresence.Author | SourceFieldPresence.Subject | SourceFieldPresence.PostedAt, post.Fields);
        Assert.Null(post.Text);
        Assert.Null(post.Author);
        Assert.Null(post.Subject);
        Assert.Null(post.PostedAt);
        Assert.Equal(SourceTextFormat.Html, post.TextFormat);
    }

    [Fact]
    public async Task UnverifiedArchiveAndOtherFlagsNeverEndOrDeleteTheThread()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        root["archived"] = true;
        root["trash"] = true;
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread());
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        Assert.Equal(2, result.Snapshot.Posts[0].Attachments!.Count);
        Assert.All(result.Snapshot.Posts, post => Assert.False(post.IsDeleted));
    }

    [Fact]
    public async Task UnchangedRetainsAcceptedLastModifiedWithoutAcceptingResponseValidator()
    {
        Harness h = new Harness("not JSON", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators accepted = new SourceValidators(LastModified: DateTimeOffset.Parse("2026-10-22T02:22:35Z"));
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = accepted };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = accepted };
        ThreadUnchanged unchanged = Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context));
        Assert.Equal(accepted, unchanged.Validators);
        Assert.Empty(SdkValidation.ValidateThreadResult(thread, unchanged, h.Source));
        Assert.Equal(accepted, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(accepted, h.Http.Requests[^1].Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new SourceValidators(Revision: "local") }, h.Context));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(catalog with { Validators = new SourceValidators() }, h.Context));
    }

    [Fact]
    public async Task OnlyExactThreadEndpoint404MeansNotFound()
    {
        Harness h = new Harness("<html>missing</html>", 404);
        ThreadNotFound result = Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        foreach (string url in new[] { "https://endchan.net/login.html", "https://endchan.org/demo/res/123.json", "https://endchan.net/demo/res/124.json", "https://endchan.net/demo/res/123.json?q=1" })
        {
            h.Http.FinalUri = new Uri(url);
            Assert.Equal("unexpected_endpoint", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        }
    }

    [Theory]
    [InlineData(206)] [InlineData(403)] [InlineData(410)] [InlineData(429)] [InlineData(503)]
    public async Task OtherStatusesNeverBecomeTerminalOrPartialSnapshots(int status)
    {
        Harness h = new Harness(Fixture("thread"), status);
        Assert.Equal(status, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.HttpStatusCode);
        Assert.Equal(status, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure.HttpStatusCode);
        Assert.Equal(2, h.Http.Requests.Count);
    }

    [Fact]
    public async Task DeferredAndHostFailureRemainHostControlled()
    {
        Harness h = new Harness("");
        DateTimeOffset due = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        h.Http.Result = new HostHttpDeferred(due, "provider_cooldown", 429);
        Assert.Equal(due, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(due, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        FailureDetail failure = new FailureDetail("timeout", "Bounded fixture timeout.");
        h.Http.Result = new HostHttpFailure(failure);
        Assert.Equal(failure, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure);
        Assert.Equal(failure, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure);
        Assert.Equal(4, h.Http.Requests.Count);
    }

    [Theory]
    [InlineData("{")] [InlineData("{}")]
    [InlineData("{\"threadId\":123,\"boardUri\":\"demo\",\"posts\":null}")]
    [InlineData("{\"threadId\":1.23e2,\"boardUri\":\"demo\",\"posts\":[]}")]
    [InlineData("{\"threadId\":123.0,\"boardUri\":\"demo\",\"posts\":[]}")]
    public async Task MalformedResponseCannotCommitValidators(string json)
    {
        ThreadFailure result = Assert.IsType<ThreadFailure>(await new Harness(json).ReadThread());
        Assert.Equal("invalid_payload", result.Failure.Code);
        Assert.DoesNotContain("entityTag", Encoding.UTF8.GetString(SdkJson.Serialize<ThreadReadResult>(result)));
    }

    [Theory]
    [InlineData("https://foreign.test/file.png")] [InlineData("//foreign.test/file.png")]
    [InlineData("/.media/../../file.png")] [InlineData("/.media/%2e%2e/file.png")]
    [InlineData("/.media/nested/file.png")] [InlineData("/.media/file.png?secret=1")]
    [InlineData("/.media/..")] [InlineData("/.media/.")]
    public async Task UntrustedMediaPathsRejectWholeSnapshot(string path)
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        root["files"]![0]!["path"] = path;
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task CrossThreadDuplicatePostsNegativeSizesAndExcessiveFilesAreRejected()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        root["threadId"] = 999;
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture("thread"))!;
        root["boardUri"] = "other";
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture("thread"))!;
        JsonArray posts = root["posts"]!.AsArray();
        posts.Add(posts[0]!.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture("thread"))!;
        root["files"]![0]!["size"] = -1;
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture("thread"))!;
        JsonArray files = root["files"]!.AsArray();
        JsonNode file = files[0]!.DeepClone();
        files.Clear();
        for (int index = 0; index < 1025; index++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task MediaIdentitySurvivesReplacementUrlAndName()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        SourceAttachment before = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        root["files"]![0]!["path"] = "/.media/replacement.png";
        root["files"]![0]!["originalName"] = "renamed.png";
        SourceAttachment after = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(before.MediaId, after.MediaId);
        Assert.NotEqual(before.Url, after.Url);
        Assert.NotEqual(before.OriginalFileName, after.OriginalFileName);
    }

    [Fact]
    public async Task CatalogRejectsDuplicatesMalformedEnvelopesAndNegativeCounts()
    {
        JsonArray root = JsonNode.Parse(Fixture("catalog"))!.AsArray();
        root.Add(root[0]!.DeepClone());
        Assert.IsType<CatalogFailure>(await new Harness(root.ToJsonString()).ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness("[{}]").ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness("{\"threads\":[]}").ReadCatalog());
        root = JsonNode.Parse(Fixture("catalog"))!.AsArray();
        root[0]!["postCount"] = -1;
        Assert.IsType<CatalogFailure>(await new Harness(root.ToJsonString()).ReadCatalog());
    }

    [Theory]
    [InlineData("Kühl")] [InlineData("demo+other")] [InlineData("../escape")]
    [InlineData("with space")] [InlineData("%64emo")] [InlineData("")]
    public async Task UnsupportedBoardAlphabetsAreRejectedBeforeHttp(string board)
    {
        Harness h = new Harness(Fixture("thread"));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId("endchan", board, "123") }, h.Context));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(new CatalogReadRequest(new SourceBoardId("endchan", board)), h.Context));
        Assert.Empty(h.Http.Requests);
    }

    [Theory]
    [InlineData("123456789012345678901234567890")]
    [InlineData("\"123456789012345678901234567890\"")]
    public async Task CaseAndLargeDecimalIdentitiesArePreservedWithoutNumericConversion(string jsonId)
    {
        const string id = "123456789012345678901234567890";
        string json = "{\"threadId\":" + jsonId + ",\"boardUri\":\"Demo_2\",\"posts\":[]}";
        Harness h = new Harness(json);
        ThreadReadRequest request = new ThreadReadRequest(new SourceThreadId("endchan", "Demo_2", id));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.Plugin.ReadThreadAsync(request, h.Context));
        Assert.Equal(request.Thread, result.Snapshot.Thread);
        Assert.Equal(id, Assert.Single(result.Snapshot.Posts).PostId);
        Assert.Equal("https://endchan.net/Demo_2/res/" + id + ".json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task UnsupportedPagesInvalidIdsOversizeAndCancellationAreBounded()
    {
        Harness h = new Harness(Fixture("thread"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Cursor = "next" }, h.Context));
        foreach (string id in new[] { "01", "-1", "1.0", "1e2", "١٢٣" })
            Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId("endchan", "demo", id) }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId("other", "demo", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        Assert.Equal("metadata_too_large", Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure.Code);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadCatalogAsync(h.CatalogRequest, h.Context, cancellation.Token));
        Assert.Equal(2, h.Http.Requests.Count);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "endchan-" + name + ".json"));

    private sealed class Harness
    {
        internal ISourcePlugin Plugin { get; } = new EndchanPlugin();
        internal SourceDescription Source { get; }
        internal SourceValidators Validators { get; } = new SourceValidators(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; } = new ThreadReadRequest(new SourceThreadId("endchan", "demo", "123"));
        internal CatalogReadRequest CatalogRequest { get; } = new CatalogReadRequest(new SourceBoardId("endchan", "demo"));
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string body, int status = 200)
        {
            Source = Plugin.Describe().Sources[0];
            Http = new StubHttp(Encoding.UTF8.GetBytes(body), status, Validators);
            SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
            Context = new PluginCallContext(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { ["endchan"] = empty }), Http, new NoOtherServices(), new NoOtherServices(), new NoOtherServices());
        }
        internal Task<ThreadReadResult> ReadThread() => Plugin.ReadThreadAsync(ThreadRequest, Context);
        internal Task<CatalogReadResult> ReadCatalog() => Plugin.ReadCatalogAsync(CatalogRequest, Context);
    }

    private sealed class StubHttp(byte[] body, int status, SourceValidators validators) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = new List<HostHttpRequest>();
        internal byte[] Body { get; set; } = body;
        internal Uri? FinalUri { get; set; }
        internal HostHttpResult? Result { get; set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result ?? new HostHttpResponse(status, FinalUri ?? request.Url, new Dictionary<string, string>(), Body, validators));
        }
    }

    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source must not read image bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Single-file source needs no resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Fixture source must not log source content.");
    }
}
