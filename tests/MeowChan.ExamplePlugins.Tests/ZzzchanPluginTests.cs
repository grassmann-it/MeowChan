using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Zzzchan;
using MeowChan.Plugins.Examples.SushiChan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class ZzzchanPluginTests
{
    [Fact]
    public void DescriptionDeclaresOnlyObservedRoutesAndSdkDependency()
    {
        PluginDescription description = new ZzzchanPlugin().Describe();
        SourceDescription source = Assert.Single(description.Sources);
        Assert.Equal("meowchan.source.zzzchan", description.PluginId);
        Assert.Equal("zzzchan", source.SourceId);
        Assert.Empty(SdkValidation.ValidateDescriptions([description, new SushiChanPlugin().Describe()]));
        Assert.Empty(description.Settings.Fields);
        Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia, source.Capabilities);
        Assert.Equal("/{board}/", Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Board && r.IsCanonical).PathTemplate);
        Assert.False(Assert.Single(source.Routes, r => r.PathTemplate == "/{board}/index.html").IsCanonical);
        UrlRoute thread = Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Thread);
        Assert.Equal("/{board}/thread/{thread}.html", thread.PathTemplate);
        Assert.Equal(RouteIdentifierKind.Decimal, thread.ThreadIdentifierKind);
        Assert.All(source.Routes, r => Assert.Equal("zzzchan.xyz", r.Host));
        Assert.Equal(new HttpHostDeclaration("zzzchan.xyz", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia), Assert.Single(source.HttpHosts));
        string[] references = typeof(ZzzchanPlugin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Contains("MeowChan.PluginSdk", references);
        Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
        Assert.Single(typeof(ZzzchanPlugin).Assembly.GetExportedTypes(), t => typeof(IPlugin).IsAssignableFrom(t));
    }

    [Fact]
    public async Task RootOpAndRepliesRetainPostSlotIdentitiesAndProviderSha256()
    {
        Harness h = new(Fixture("thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        ThreadSnapshot copy = Assert.IsType<ThreadSnapshot>(SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result)));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(new[] { "123", "124", "125" }, result.Snapshot.Posts.Select(p => p.PostId));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState);
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal("<span>Synthetic &amp; inert</span>", op.Text);
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T20:39:27.884Z"), op.PostedAt);
        Assert.Equal(new[] { 0, 1 }, op.Attachments!.Select(a => a.Position));
        Assert.Equal(new[] { "123-0", "123-1" }, op.Attachments!.Select(a => a.MediaId));
        Assert.Equal("https://zzzchan.xyz/file/shared.png", op.Attachments![0].Url!.AbsoluteUri);
        Assert.Equal(new string('a', 64), op.Attachments![0].ExpectedSha256);
        Assert.Null(op.Attachments![0].ExpectedMd5);
        Assert.Equal("picture.png", op.Attachments![0].OriginalFileName);
        Assert.Equal("image/png", op.Attachments![0].MimeType);
        SourceAttachment reused = Assert.Single(result.Snapshot.Posts[1].Attachments!);
        Assert.Equal(op.Attachments![0].Url, reused.Url);
        Assert.NotEqual(op.Attachments![0].MediaId, reused.MediaId);
        Assert.Equal("124-0", reused.MediaId);
        Assert.False(h.Source.Capabilities.HasFlag(SourceCapabilities.Previews));
        Assert.Equal("https://zzzchan.xyz/demo/thread/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        Assert.Equal(h.Validators, result.Snapshot.Validators);
    }

    [Fact]
    public async Task FlatCatalogUsesPublicIdsAndDoesNotInferThreadEndOrCoverage()
    {
        Harness h = new(Fixture("catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Equal(new[] { "123", "456" }, result.Snapshot.Threads.Select(t => t.Id.ThreadId));
        Assert.Equal("https://zzzchan.xyz/demo/thread/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Equal("https://zzzchan.xyz/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        Assert.Equal(2, result.Snapshot.Threads[0].ReplyCount);
        Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
    }

    [Fact]
    public async Task PartialNullEmptyAndPlainTextFieldsRemainDistinguishable()
    {
        SourcePost partial = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness("""{"postId":123,"replies":[{"postId":124,"nomarkup":"plain > text"}]}""").ReadThread()).Snapshot.Posts, p => p.PostId == "124");
        Assert.Equal(SourceFieldPresence.Text, partial.Fields);
        Assert.Equal(SourceTextFormat.PlainText, partial.TextFormat);
        Assert.Equal("plain > text", partial.Text);
        Assert.Null(partial.Attachments);
        SourcePost empty = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness("""{"postId":123,"message":null,"name":null,"subject":null,"date":null,"files":[]}""").ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.PostedAt | SourceFieldPresence.Subject | SourceFieldPresence.Author | SourceFieldPresence.Text | SourceFieldPresence.Attachments, empty.Fields);
        Assert.Null(empty.Text);
        Assert.Null(empty.PostedAt);
        Assert.Empty(empty.Attachments!);
        Assert.False(empty.IsDeleted);
    }

    [Fact]
    public async Task PhashNeverBecomesIntegrityAndFileRevisionDoesNotChangeSlotIdentity()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        root["files"]![0]!.AsObject().Remove("hash");
        SourceAttachment first = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Null(first.ExpectedSha256);
        root["files"]![0]!["filename"] = "changed.png";
        root["files"]![0]!["originalFilename"] = "renamed.png";
        SourceAttachment changed = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(first.MediaId, changed.MediaId);
        Assert.NotEqual(first.Url, changed.Url);
    }

    [Theory]
    [InlineData(403)] [InlineData(410)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(206)]
    public async Task HttpFailureDoesNotClaimThreadEnd(int status)
    {
        Harness h = new("{}", status);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
    }

    [Fact]
    public async Task ExactEndpointIsCheckedBeforeNotFound()
    {
        Harness h = new("{}", 404);
        Assert.IsType<ThreadNotFound>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        foreach (string endpoint in new[] { "https://other.invalid/demo/thread/123.json", "https://zzzchan.xyz/other/thread/123.json", "https://zzzchan.xyz/demo/thread/123.json?error=1" })
        {
            h.Http.FinalUri = new Uri(endpoint);
            Assert.IsType<ThreadFailure>(await h.ReadThread());
        }
    }

    [Fact]
    public async Task UnchangedRequiresAcceptedHttpBaselineAndPreservesIt()
    {
        Harness h = new("not JSON", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators accepted = new(EntityTag: "W/\"accepted\"");
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = accepted };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = accepted };
        Assert.Equal(accepted, Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context)).Validators);
        Assert.Equal(accepted, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(accepted, h.Http.Requests.Last().Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new SourceValidators(Revision: "local") }, h.Context));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(catalog with { Validators = null }, h.Context));
    }

    [Fact]
    public async Task DeferredFailureAndCancellationPropagateWithoutRetryOrOriginalRequest()
    {
        Harness h = new("{}");
        DateTimeOffset retry = DateTimeOffset.UtcNow.AddSeconds(30);
        h.Http.Result = new HostHttpDeferred(retry, "cooldown", 429);
        Assert.Equal(retry, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(retry, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        h.Http.Result = new HostHttpFailure(new FailureDetail("network", "Fixture"));
        Assert.Equal("network", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        int count = h.Http.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cancelled.Token));
        Assert.Equal(count, h.Http.Requests.Count);
        using CancellationTokenSource late = new(); h.Http.OnSend = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadCatalogAsync(h.CatalogRequest, h.Context, late.Token));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    [InlineData("{\"postId\":999}")]
    [InlineData("{\"postId\":123,\"board\":\"wrong\"}")]
    [InlineData("{\"postId\":123,\"replies\":[{\"postId\":124,\"thread\":999}]}")]
    [InlineData("{\"postId\":123,\"replies\":[{\"postId\":123}]}")]
    [InlineData("{\"_id\":123}")]
    [InlineData("{\"postId\":123,\"files\":null}")]
    [InlineData("{\"postId\":123,\"date\":\"not a date\"}")]
    [InlineData("{\"postId\":123,\"date\":\"2026-09-20T20:39:27\"}")]
    [InlineData("{\"postId\":123,\"files\":[{\"filename\":\"../escape.png\",\"extension\":\".png\"}]}")]
    [InlineData("{\"postId\":123,\"files\":[{\"filename\":\"a.png\",\"extension\":\".png\",\"hash\":\"invalid\"}]}")]
    public async Task MalformedOrMisroutedPayloadFailsAsAWhole(string body) => Assert.IsType<ThreadFailure>(await new Harness(body).ReadThread());

    [Fact]
    public async Task ResourceAndCollectionBudgetsFailRatherThanTruncate()
    {
        Harness h = new(Fixture("catalog"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Cursor = "next" }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new("zzzchan", "../escape", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        Assert.IsType<CatalogFailure>(await new Harness("[{\"postId\":123},{\"postId\":123}]").ReadCatalog());
        Assert.IsType<CatalogFailure>(await new Harness("[{\"postId\":123,\"thread\":99}]").ReadCatalog());
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        JsonArray files = root["files"]!.AsArray(); JsonNode file = files[0]!.DeepClone(); files.Clear();
        for (int index = 0; index < 1025; index++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "zzzchan-" + name + ".json"));
    private sealed class Harness
    {
        internal ZzzchanPlugin Plugin { get; } = new();
        internal SourceDescription Source => Plugin.Describe().Sources[0];
        internal SourceValidators Validators { get; } = new(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; } = new(new SourceThreadId("zzzchan", "demo", "123"));
        internal CatalogReadRequest CatalogRequest { get; } = new(new SourceBoardId("zzzchan", "demo"));
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string body, int status = 200)
        {
            Http = new(Encoding.UTF8.GetBytes(body), status, Validators);
            SettingsSnapshot empty = new("1", []); NoOtherServices none = new();
            Context = new(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { ["zzzchan"] = empty }), Http, none, none, none);
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
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); OnSend?.Invoke();
            return Task.FromResult(Result ?? new HostHttpResponse(status, FinalUri ?? request.Url, new Dictionary<string, string>(), Body, validators));
        }
    }
    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source must not read image bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No source resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("No post text logging.");
    }
}
