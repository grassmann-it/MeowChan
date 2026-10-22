using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Lainchan;
using MeowChan.Plugins.Examples.Kohlchan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class ExamplePluginTests
{
    [Fact]
    public void DescriptionsAreInertDistinctSdkOnlyExamples()
    {
        PluginDescription[] descriptions = [new LainchanPlugin().Describe(), new KohlchanPlugin().Describe()];
        Assert.Empty(SdkValidation.ValidateDescriptions(descriptions));
        Assert.All(descriptions, description =>
        {
            Assert.Empty(description.Settings.Fields);
            Assert.Contains("example", description.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Single(description.Sources);
            SourceDescription source = description.Sources[0];
            Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
            Assert.False(source.Capabilities.HasFlag(SourceCapabilities.Previews));
            Assert.Equal("/{board}/res/{thread}.html", Assert.Single(source.Routes, route => route.Kind == SourceResourceKind.Thread).PathTemplate);
        });
        foreach (Type type in new[] { typeof(LainchanPlugin), typeof(KohlchanPlugin) })
        {
            string[] references = type.Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
            Assert.Contains("MeowChan.PluginSdk", references);
            Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
            Assert.Single(type.Assembly.GetExportedTypes(), item => typeof(IPlugin).IsAssignableFrom(item));
        }
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ThreadsTransportOrderedFilesAndIndependentPostIdentity(string source)
    {
        Harness h = new Harness(source, Fixture(source, "thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState); // locked and cyclic are not terminal.
        Assert.Equal(new[] { "123", "124" }, result.Snapshot.Posts.Select(post => post.PostId));
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal("<p>Synthetic text</p>", op.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), op.PostedAt);
        Assert.Equal(new[] { 0, 1, 2 }, op.Attachments!.Select(file => file.Position));
        Assert.Equal(new[] { "123-0", "123-1", "123-2" }, op.Attachments!.Select(file => file.MediaId));
        Assert.Equal(3, op.Attachments!.Select(file => file.MediaId).Distinct().Count());
        Assert.All(op.Attachments!, file => Assert.Equal(h.Host, file.Url!.Host));
        Assert.Equal(h.Validators, result.Snapshot.Validators);
        Assert.Equal("https://" + h.Host + "/demo/res/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(3, Assert.IsType<ThreadSnapshot>(copy).Snapshot.Posts[0].Attachments!.Count);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task CatalogUsesExplicitBoardAndCanonicalThreadsWithoutGlobalDiscovery(string source)
    {
        Harness h = new Harness(source, Fixture(source, "catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Null(result.Snapshot.NextPage);
        Assert.Equal(new[] { "123", "456" }, result.Snapshot.Threads.Select(thread => thread.Id.ThreadId));
        Assert.Equal("https://" + h.Host + "/demo/res/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
        Assert.Equal("https://" + h.Host + "/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task MissingAndExplicitEmptyFieldsStayDistinctInPartialUpdates(string source)
    {
        Harness h = new Harness(source, Fixture(source, "partial"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        SourcePost reply = Assert.Single(result.Snapshot.Posts, post => post.PostId == "124");
        Assert.Equal(SourceFieldPresence.Text, reply.Fields);
        Assert.Equal("", reply.Text);
        Assert.Null(reply.Subject);
        Assert.Null(reply.Attachments);
        Assert.False(reply.IsDeleted);
        Assert.Null(result.Snapshot.Title);
        Assert.All(result.Snapshot.Posts, post => Assert.Equal(0, post.Ordinal)); // DecimalPostId, never response index.
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task UnchangedReferencesAcceptedValidatorAndNeverProducesEmptySnapshot(string source)
    {
        Harness h = new Harness(source, "not JSON", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators accepted = new SourceValidators(EntityTag: "\"accepted\"");
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = accepted };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = accepted };
        ThreadUnchanged unchanged = Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context));
        Assert.Equal(accepted, unchanged.Validators); // The response's unaccepted validator does not replace the baseline.
        Assert.Empty(SdkValidation.ValidateThreadResult(thread, unchanged, h.Source));
        Assert.Equal(accepted, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(accepted, h.Http.Requests[^1].Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new SourceValidators(Revision: "local") }, h.Context));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task OnlyExactThreadEndpoint404MeansNotFoundWithoutInventedPosts(string source)
    {
        Harness h = new Harness(source, "<html>missing</html>", 404);
        ThreadNotFound result = Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://" + h.Host + "/login.html");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
    }

    [Theory]
    [InlineData("lainchan", 206)] [InlineData("kohlchan", 206)]
    [InlineData("lainchan", 403)] [InlineData("kohlchan", 403)]
    [InlineData("lainchan", 410)] [InlineData("kohlchan", 410)]
    [InlineData("lainchan", 429)] [InlineData("kohlchan", 429)]
    [InlineData("lainchan", 503)] [InlineData("kohlchan", 503)]
    public async Task OtherStatusesNeverBecomeTerminalOrPartialSnapshots(string source, int status)
    {
        Harness h = new Harness(source, Fixture(source, "thread"), status);
        Assert.Equal(status, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.HttpStatusCode);
        Assert.Equal(status, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure.HttpStatusCode);
        Assert.Equal(2, h.Http.Requests.Count); // one request per operation; no private retry loop.
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task DeferredAndHostFailurePreserveTheirMeaning(string source)
    {
        Harness h = new Harness(source, "");
        DateTimeOffset due = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        h.Http.Result = new HostHttpDeferred(due, "provider_cooldown", 429);
        Assert.Equal(due, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(due, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        FailureDetail failure = new FailureDetail("timeout", "Bounded fixture timeout.");
        h.Http.Result = new HostHttpFailure(failure);
        Assert.Equal(failure, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure);
        Assert.Equal(failure, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure);
    }

    [Theory]
    [InlineData("lainchan", "{")]
    [InlineData("kohlchan", "{")]
    [InlineData("lainchan", "{}")]
    [InlineData("kohlchan", "{}")]
    [InlineData("lainchan", "{\"posts\":null}")]
    [InlineData("kohlchan", "{\"threadId\":123,\"boardUri\":\"demo\",\"posts\":null}")]
    public async Task MalformedResponseCannotCommitValidators(string source, string json)
    {
        Harness h = new Harness(source, json);
        ThreadFailure result = Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.Equal("invalid_payload", result.Failure.Code);
        Assert.DoesNotContain("entityTag", Encoding.UTF8.GetString(SdkJson.Serialize<ThreadReadResult>(result)));
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task DuplicatePostsAndNegativeFileSizesRejectWholeSnapshot(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        JsonArray posts = root["posts"]!.AsArray();
        posts.Add(posts[0]!.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
        root = JsonNode.Parse(Fixture(source, "thread"))!;
        if (source == "lainchan") root["posts"]![0]!["fsize"] = -1;
        else root["files"]![0]!["size"] = -1;
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task KohlchanArchivedIncludesFinalFilesAndIgnoresTrashAsDeletion()
    {
        JsonNode root = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
        root["archived"] = true;
        root["trash"] = true;
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness("kohlchan", root.ToJsonString()).ReadThread());
        Assert.Equal(SourceThreadState.Archived, result.Snapshot.SourceState);
        Assert.Equal(3, result.Snapshot.Posts[0].Attachments!.Count);
        Assert.False(result.Snapshot.Posts[0].IsDeleted);
    }

    [Fact]
    public async Task LainchanDoesNotInventArchiveMarkerOrCompactExtraPositions()
    {
        string json = """{"posts":[{"no":123,"archived":1,"omitted_posts":20,"extra_files":[{"tim":"1001-1","ext":".png"}]}]}""";
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await new Harness("lainchan", json).ReadThread());
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        SourceAttachment file = Assert.Single(result.Snapshot.Posts[0].Attachments!);
        Assert.Equal(1, file.Position);
        Assert.Equal("123-1", file.MediaId);
        Assert.EndsWith("/demo/src/1001-1.png", file.Url!.AbsoluteUri);
    }

    [Fact]
    public async Task KohlchanRejectsCrossThreadAndUntrustedMediaPaths()
    {
        foreach (string path in new[] { "https://foreign.test/file.png", "//foreign.test/file.png", "/.media/../../file.png", "/.media/%2e%2e/file.png", "/.media/nested/file.png", "/.media/file.png?secret=1" })
        {
            JsonNode root = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
            root["files"]![0]!["path"] = path;
            Assert.IsType<ThreadFailure>(await new Harness("kohlchan", root.ToJsonString()).ReadThread());
        }
        JsonNode other = JsonNode.Parse(Fixture("kohlchan", "thread"))!;
        other["threadId"] = 999;
        Assert.IsType<ThreadFailure>(await new Harness("kohlchan", other.ToJsonString()).ReadThread());
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task EmptyAttachmentArrayIsExplicitButMissingArrayIsNotDeletion(string source)
    {
        string json = source == "lainchan" ? """{"posts":[{"no":123,"extra_files":[]}]}"""
            : """{"threadId":123,"boardUri":"demo","files":[],"posts":[]}""";
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(source, json).ReadThread()).Snapshot.Posts);
        Assert.True(post.Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Empty(post.Attachments!);
        Assert.False(post.IsDeleted);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task UnsupportedPaginationTraversalOversizeAndCancellationAreBounded(string source)
    {
        Harness h = new Harness(source, Fixture(source, "thread"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new SourceThreadId(source, "../escape", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cancellation.Token));
        Assert.Single(h.Http.Requests);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task MediaIdentityDoesNotChangeWithFilenameOrDownloadPath(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        SourceAttachment before = Assert.IsType<ThreadSnapshot>(await new Harness(source, root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        if (source == "lainchan") { root["posts"]![0]!["tim"] = "2000-0"; root["posts"]![0]!["filename"] = "renamed"; }
        else { root["files"]![0]!["path"] = "/.media/replacement.png"; root["files"]![0]!["originalName"] = "renamed.png"; }
        SourceAttachment after = Assert.IsType<ThreadSnapshot>(await new Harness(source, root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(before.MediaId, after.MediaId);
        Assert.NotEqual(before.Url, after.Url);
        Assert.NotEqual(before.OriginalFileName, after.OriginalFileName);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExplicitNullIsPresentAndDoesNotFallBackToAnotherTextField(string source)
    {
        string json = source == "lainchan" ? """{"posts":[{"no":123,"com":null,"name":null,"sub":null,"time":null}]}"""
            : """{"threadId":123,"boardUri":"demo","markdown":null,"message":"not selected","name":null,"subject":null,"creation":null,"posts":[]}""";
        SourcePost post = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(source, json).ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text | SourceFieldPresence.Author | SourceFieldPresence.Subject | SourceFieldPresence.PostedAt, post.Fields);
        Assert.Null(post.Text);
        Assert.Null(post.Author);
        Assert.Null(post.Subject);
        Assert.Null(post.PostedAt);
        Assert.Equal(SourceTextFormat.Html, post.TextFormat);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task CatalogRejectsDuplicateIdentitiesAndMalformedShape(string source)
    {
        JsonArray root = JsonNode.Parse(Fixture(source, "catalog"))!.AsArray();
        if (source == "lainchan") root[1]!["threads"]![0]!["no"] = 123;
        else root[1]!["threadId"] = 123;
        Assert.IsType<CatalogFailure>(await new Harness(source, root.ToJsonString()).ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness(source, "[{}]").ReadCatalog());
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExplicitUnicodeBoardIsEscapedOnceAndRetainsItsIdentity(string source)
    {
        Harness h = new Harness(source, Fixture(source, "catalog"));
        CatalogReadRequest request = new CatalogReadRequest(new SourceBoardId(source, "Kühl"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.Plugin.ReadCatalogAsync(request, h.Context));
        Assert.Equal("Kühl", result.Snapshot.Board.BoardId);
        Assert.Equal("/K%C3%BChl/catalog.json", Assert.Single(h.Http.Requests).Url.AbsolutePath);
        Assert.Empty(SdkValidation.ValidateCatalogResult(request, result, h.Source));
        Assert.Equal("/K%C3%BChl/res/123.html", result.Snapshot.Threads[0].Url.AbsolutePath);
    }

    [Theory]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public async Task ExcessiveAttachmentCountRejectsTheSnapshotWithoutTruncation(string source)
    {
        JsonNode root = JsonNode.Parse(Fixture(source, "thread"))!;
        JsonArray files = source == "lainchan" ? root["posts"]![0]!["extra_files"]!.AsArray() : root["files"]!.AsArray();
        JsonNode file = files[0]!.DeepClone();
        files.Clear();
        for (int index = 0; index < 1025; index++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(source, root.ToJsonString()).ReadThread());
    }

    private static string Fixture(string source, string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", source + "-" + name + ".json"));

    private sealed class Harness
    {
        internal ISourcePlugin Plugin { get; }
        internal SourceDescription Source { get; }
        internal string Host => Source.BaseUri.Host;
        internal SourceValidators Validators { get; } = new SourceValidators(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; }
        internal CatalogReadRequest CatalogRequest { get; }
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string source, string body, int status = 200)
        {
            Plugin = source == "lainchan" ? new LainchanPlugin() : new KohlchanPlugin();
            Source = Plugin.Describe().Sources[0];
            ThreadRequest = new ThreadReadRequest(new SourceThreadId(source, "demo", "123"));
            CatalogRequest = new CatalogReadRequest(new SourceBoardId(source, "demo"));
            Http = new StubHttp(Encoding.UTF8.GetBytes(body), status, Validators);
            SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
            Context = new PluginCallContext(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { [source] = empty }), Http, new NoOtherServices(), new NoOtherServices(), new NoOtherServices());
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
