using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Leftypol;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class LeftypolPluginTests
{
    [Fact]
    public void DescriptionIsPassiveSdkOnlyAndDeclaresObservedRoutesAndHostPurposes()
    {
        PluginDescription description = new LeftypolPlugin().Describe();
        SourceDescription source = Assert.Single(description.Sources);
        Assert.Equal("meowchan.source.leftypol", description.PluginId);
        Assert.Equal("leftypol", source.SourceId);
        Assert.Empty(SdkValidation.ValidateDescriptions([description]));
        Assert.Empty(description.Settings.Fields);
        Assert.Empty(source.Settings.Fields);
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia, source.Capabilities);
        Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal("/{board}/", Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Board).PathTemplate);
        UrlRoute thread = Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Thread);
        Assert.Equal("/{board}/res/{thread}.html", thread.PathTemplate);
        Assert.Equal(RouteIdentifierKind.Decimal, thread.ThreadIdentifierKind);
        Assert.All(source.Routes, r => { Assert.Equal("leftypol.org", r.Host); Assert.True(r.IsCanonical); Assert.False(r.LowercaseBoardId); });
        Assert.Equal(new HttpHostDeclaration("leftypol.org", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia), Assert.Single(source.HttpHosts));
        string[] references = typeof(LeftypolPlugin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.Contains("MeowChan.PluginSdk", references);
        Assert.DoesNotContain(references, name => name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
        Assert.Single(typeof(LeftypolPlugin).Assembly.GetExportedTypes(), t => typeof(IPlugin).IsAssignableFrom(t));
    }

    [Fact]
    public async Task FilesArrayRetainsOriginalPathsOpaqueIdentitiesOrderAndProviderValues()
    {
        Harness h = new(Fixture("thread"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        ThreadSnapshot roundtrip = Assert.IsType<ThreadSnapshot>(SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result)));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, roundtrip, h.Source));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState); // locked/cyclical are not finality.
        Assert.Equal(new[] { "123", "124", "125" }, result.Snapshot.Posts.Select(p => p.PostId));
        SourcePost op = result.Snapshot.Posts[0];
        Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal("<p>Unicode: Kühl 猫</p>", op.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), op.PostedAt);
        Assert.Equal(new[] { 0, 1 }, op.Attachments!.Select(a => a.Position));
        Assert.Equal(new[] { "opaque-6", "second_file" }, op.Attachments!.Select(a => a.MediaId));
        Assert.Equal("https://leftypol.org/demo/src/original-6.png", op.Attachments[0].Url!.AbsoluteUri);
        Assert.Equal("same-name.png", op.Attachments[0].OriginalFileName);
        Assert.Equal("image/png", op.Attachments[0].MimeType);
        Assert.Equal(3L, op.Attachments[0].ExpectedSize);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(new byte[] { 1, 2, 3 })), op.Attachments[0].ExpectedMd5);
        Assert.Empty(result.Snapshot.Posts[1].Attachments!);
        Assert.Null(result.Snapshot.Posts[2].Attachments); // embed does not grant original media.
        Assert.Equal("https://leftypol.org/demo/res/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
        Assert.Equal(h.Validators, result.Snapshot.Validators);
    }

    [Fact]
    public async Task IdentityFollowsProviderFileIdAcrossRenameReorderAndPathChanges()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        SourceAttachment first = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        JsonArray files = root["posts"]![0]!["files"]!.AsArray();
        JsonNode file = files[0]!.DeepClone(); files.RemoveAt(0);
        file["filename"] = "renamed"; file["file_path"] = "/demo/src/another.png"; files.Add(file);
        SourceAttachment reordered = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![1];
        Assert.Equal(first.MediaId, reordered.MediaId);
        Assert.NotEqual(first.Position, reordered.Position);
        Assert.NotEqual(first.Url, reordered.Url);
    }

    [Fact]
    public async Task MismatchingProviderMd5IsPreservedForStrictHostRejection()
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        // Known live mismatch from NW-00, retained as provider metadata rather
        // than relabelled or replaced with the delivered file's measured MD5.
        root["posts"]![0]!["files"]![0]!["fsize"] = 7533;
        root["posts"]![0]!["files"]![0]!["md5"] = "9DIpWh+vpsvo1F0+7wLFzQ==";
        SourceAttachment file = Assert.IsType<ThreadSnapshot>(await new Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(7533L, file.ExpectedSize);
        Assert.Equal("9DIpWh+vpsvo1F0+7wLFzQ==", file.ExpectedMd5);
        Assert.NotEqual("inypgQekyZoAAp3XpQYTtg==", file.ExpectedMd5);
        // This parser test proves metadata preservation, not queue/archival acceptance.
    }

    [Fact]
    public async Task CatalogFlattensReturnedPagesWithoutInferringCompleteCoverage()
    {
        Harness h = new(Fixture("catalog"));
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog());
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.False(result.Snapshot.IsComplete);
        Assert.Null(result.Snapshot.NextCursor); Assert.Null(result.Snapshot.NextPage);
        Assert.Equal(new[] { "123", "456" }, result.Snapshot.Threads.Select(t => t.Id.ThreadId));
        Assert.Equal("https://leftypol.org/demo/res/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
        Assert.Null(result.Snapshot.Threads[0].AttachmentCount); // images does not prove total originals.
        Assert.Equal("https://leftypol.org/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task MissingEmptyAndNullTextRemainDistinctWithoutInventingDeletion()
    {
        SourcePost partial = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness(Fixture("partial")).ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text, partial.Fields); Assert.Equal("", partial.Text);
        Assert.Null(partial.Attachments); Assert.False(partial.IsDeleted);
        SourcePost explicitNull = Assert.Single(Assert.IsType<ThreadSnapshot>(await new Harness("""{"posts":[{"no":123,"com":null,"name":null,"time":null,"files":[]}]}""").ReadThread()).Snapshot.Posts);
        Assert.Equal(SourceFieldPresence.Text | SourceFieldPresence.Author | SourceFieldPresence.PostedAt | SourceFieldPresence.Attachments, explicitNull.Fields);
        Assert.Null(explicitNull.Text); Assert.Null(explicitNull.PostedAt); Assert.Empty(explicitNull.Attachments!);
        Assert.False(explicitNull.IsDeleted);
        // No verified provider tombstone schema exists. A deleted/missing file
        // path is not turned into a valid attachment or a post deletion.
        Assert.IsType<ThreadFailure>(await new Harness("""{"posts":[{"no":123,"files":[{"id":"gone","deleted":true}]}]}""").ReadThread());
    }

    [Theory]
    [InlineData(403)] [InlineData(410)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(206)]
    public async Task AccessAndServerErrorsNeverBecomeThreadEnd(int status)
    {
        Harness h = new("{}", status);
        Assert.IsType<ThreadFailure>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
    }

    [Fact]
    public async Task OnlyExactThread404IsNotFound()
    {
        Harness h = new("{}", 404);
        Assert.IsType<ThreadNotFound>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://leftypol.org/other/res/123.json");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
        h.Http.FinalUri = new Uri("https://foreign.invalid/demo/res/123.json");
        Assert.IsType<ThreadFailure>(await h.ReadThread());
    }

    [Fact]
    public async Task UnchangedRequiresAcceptedSnapshotAndRealHttpValidator()
    {
        Harness h = new("", 304);
        Assert.IsType<ThreadFailure>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        SourceValidators validators = new(EntityTag: "W/\"accepted\"");
        ThreadReadRequest thread = h.ThreadRequest with { HasSnapshot = true, Validators = validators };
        CatalogReadRequest catalog = h.CatalogRequest with { HasSnapshot = true, Validators = validators };
        Assert.Equal(validators, Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context)).Validators);
        Assert.Equal(validators, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(validators, h.Http.Requests.Last().Validators);
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(thread with { Validators = new(Revision: "not-http") }, h.Context));
    }

    [Fact]
    public async Task HostDeferralFailureAndCancellationArePropagatedWithoutRetry()
    {
        Harness h = new("{}");
        DateTimeOffset due = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        h.Http.Result = new HostHttpDeferred(due, "cooldown", 429);
        Assert.Equal(due, Assert.IsType<ThreadDeferred>(await h.ReadThread()).RetryAt);
        Assert.Equal(due, Assert.IsType<CatalogDeferred>(await h.ReadCatalog()).RetryAt);
        FailureDetail failure = new("timeout", "Synthetic timeout"); h.Http.Result = new HostHttpFailure(failure);
        Assert.Equal(failure, Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure);
        Assert.Equal(failure, Assert.IsType<CatalogFailure>(await h.ReadCatalog()).Failure);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        int before = h.Http.Requests.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadThreadAsync(h.ThreadRequest, h.Context, cancelled.Token));
        Assert.Equal(before, h.Http.Requests.Count);
        using CancellationTokenSource late = new(); h.Http.OnSend = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Plugin.ReadCatalogAsync(h.CatalogRequest, h.Context, late.Token));
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("{\"posts\":null}")]
    [InlineData("{\"posts\":[{\"no\":123},{\"no\":123}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"resto\":999}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"board\":\"other\"}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"files\":null}]}")]
    public async Task MalformedThreadCannotPublishPartialSuccessOrValidators(string json) =>
        Assert.IsType<ThreadFailure>(await new Harness(json).ReadThread());

    [Theory]
    [InlineData("https://foreign.invalid/demo/src/a.png")]
    [InlineData("https://leftypol.org/demo/src/a.png")]
    [InlineData("/other/src/a.png")]
    [InlineData("/demo/thumb/a.webp")]
    [InlineData("/demo/src/../src/a.png")]
    [InlineData("/demo/src/a.png?download=1")]
    [InlineData("/demo/src/%2fescape.png")]
    public async Task UnobservedOrForeignOriginalPathsCannotAcquireAMediaGrant(string path)
    {
        JsonNode root = JsonNode.Parse(Fixture("thread"))!; root["posts"]![0]!["files"]![0]!["file_path"] = path;
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task InvalidFileIdentityHashSizeOrDuplicateIdentityFailsAtomically()
    {
        foreach (string field in new[] { "id", "md5", "fsize", "ext" })
        {
            JsonNode root = JsonNode.Parse(Fixture("thread"))!;
            root["posts"]![0]!["files"]![0]![field] = field == "fsize" ? JsonValue.Create(-1) : JsonValue.Create(field == "id" ? "../escape" : "invalid");
            Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        }
        JsonNode duplicate = JsonNode.Parse(Fixture("thread"))!;
        duplicate["posts"]![0]!["files"]![1]!["id"] = "opaque-6";
        Assert.IsType<ThreadFailure>(await new Harness(duplicate.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task ResourceAndCollectionLimitsFailWithoutTruncation()
    {
        Harness h = new(Fixture("catalog"));
        Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context));
        Assert.IsType<ThreadFailure>(await h.Plugin.ReadThreadAsync(h.ThreadRequest with { Thread = new("leftypol", "../escape", "123") }, h.Context));
        Assert.Empty(h.Http.Requests);
        h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1];
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        JsonNode root = JsonNode.Parse(Fixture("thread"))!;
        JsonArray files = root["posts"]![0]!["files"]!.AsArray(); JsonNode file = files[0]!.DeepClone(); files.Clear();
        for (int i = 0; i < 1025; i++) files.Add(file.DeepClone());
        Assert.IsType<ThreadFailure>(await new Harness(root.ToJsonString()).ReadThread());
        JsonNode catalog = JsonNode.Parse(Fixture("catalog"))!; catalog[1]!["threads"]![0]!["no"] = 123;
        Assert.IsType<CatalogFailure>(await new Harness(catalog.ToJsonString()).ReadCatalog());
    }

    [Fact]
    public async Task ArbitraryLengthDecimalIdentityAndExplicitBoardCaseArePreserved()
    {
        const string huge = "123456789012345678901234567890";
        Harness h = new("""{"posts":[{"no":123456789012345678901234567890,"resto":0},{"no":123456789012345678901234567891,"resto":123456789012345678901234567890}]}""");
        ThreadReadRequest request = h.ThreadRequest with { Thread = h.ThreadRequest.Thread with { ThreadId = huge } };
        Assert.Empty(SdkValidation.ValidateThreadResult(request, Assert.IsType<ThreadSnapshot>(await h.Plugin.ReadThreadAsync(request, h.Context)), h.Source));
        JsonNode catalog = JsonNode.Parse(Fixture("catalog"))!;
        foreach (JsonNode? page in catalog.AsArray())
            foreach (JsonNode? entry in page!["threads"]!.AsArray()) entry!.AsObject().Remove("board");
        Harness board = new(catalog.ToJsonString());
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await board.Plugin.ReadCatalogAsync(new(new SourceBoardId("leftypol", "Kühl")), board.Context));
        Assert.Equal("Kühl", result.Snapshot.Board.BoardId);
        Assert.Equal("/K%C3%BChl/catalog.json", Assert.Single(board.Http.Requests).Url.AbsolutePath);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "leftypol-" + name + ".json"));
    private sealed class Harness
    {
        internal LeftypolPlugin Plugin { get; } = new();
        internal SourceDescription Source => Plugin.Describe().Sources[0];
        internal SourceValidators Validators { get; } = new(EntityTag: "\"unaccepted\"");
        internal ThreadReadRequest ThreadRequest { get; } = new(new SourceThreadId("leftypol", "demo", "123"));
        internal CatalogReadRequest CatalogRequest { get; } = new(new SourceBoardId("leftypol", "demo"));
        internal StubHttp Http { get; }
        internal PluginCallContext Context { get; }
        internal Harness(string body, int status = 200)
        {
            Http = new(Encoding.UTF8.GetBytes(body), status, Validators);
            SettingsSnapshot empty = new("1", Array.Empty<SettingValue>()); NoOtherServices none = new();
            Context = new(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { ["leftypol"] = empty }), Http, none, none, none);
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
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source adapters never read original bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No helper resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Do not log source content.");
    }
}
