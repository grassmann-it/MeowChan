using System.Net;
using System.Text;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Boards.EightKun;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.EightKun;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using SdkThreadSnapshot = MeowChan.PluginSdk.ThreadSnapshot;

namespace MeowChan.OfficialPlugins.Tests;

public sealed class OfficialSourceTests
{
    private static readonly SourceValidators Validators = new("\"fixture-v1\"", DateTimeOffset.FromUnixTimeSeconds(1700000000));

    [Fact]
    public void DescriptionsAreInertSdkOnlyAndPreserveSourceIds()
    {
        ISourcePlugin[] plugins = [new FourChanPlugin(), new EightKunPlugin()];
        Assert.Empty(SdkValidation.ValidateDescriptions(plugins.Select(plugin => plugin.Describe()).ToArray()));
        Assert.Equal(new[] { "4chan", "8kun" }, plugins.Select(plugin => plugin.Describe().Sources.Single().SourceId));
        foreach (ISourcePlugin plugin in plugins)
        {
            Assert.Equal(PostOrderKind.DecimalPostId, plugin.Describe().Sources.Single().PostOrder);
            Assert.Empty(plugin.Describe().Settings.Fields);
            Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), reference => reference.Name == "libmeowchan" || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
            Assert.Single(plugin.GetType().Assembly.GetExportedTypes(), type => typeof(IPlugin).IsAssignableFrom(type));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredAliasesPreserveLibraryCanonicalUrisAndIds(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        SourceDescription source = plugin.Describe().Sources.Single();
        using HttpClient client = new(new LibraryTransport("", ""));
        IImageBoard provider = eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
        Assert.Equal(eightKun ? 5 : 6, source.Routes.Count);
        foreach (UrlRoute route in source.Routes)
        {
            Assert.Equal(RouteIdentifierKind.AsciiWord, route.BoardIdentifierKind);
            if (route.Kind == SourceResourceKind.Thread) Assert.Equal(RouteIdentifierKind.Decimal, route.ThreadIdentifierKind);
            string path = route.PathTemplate.Replace("{board}", "G", StringComparison.Ordinal)
                .Replace("{thread}", "000123", StringComparison.Ordinal);
            if (route.AllowTitleSegment) path = path.TrimEnd('/') + "/inert-title";
            Uri alias = new("https://" + route.Host + path.TrimEnd('/') + "/?ignored=yes#post123");
            if (route.Kind == SourceResourceKind.Board)
            {
                Assert.True(provider.IsBoardUri(alias));
                IBoard board = provider.GetBoard(alias);
                Assert.Equal("g", board.BoardCode);
                Assert.Equal(eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/", board.BoardUri.AbsoluteUri);
            }
            else
            {
                Assert.True(provider.IsThreadUri(alias));
                IThread thread = provider.GetThread(alias);
                Assert.Equal("000123", thread.ThreadId);
                Assert.Equal(eightKun ? "https://8kun.top/g/res/000123.html" : "https://boards.4chan.org/g/thread/000123", thread.ThreadUri.AbsoluteUri);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentIdentityAndFilenameFallbackRemainProviderSpecific(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        SdkThreadSnapshot result = Assert.IsType<SdkThreadSnapshot>(await plugin.ReadThreadAsync(Request(eightKun),
            Context(new FixtureHttp(Response(200, "{\"posts\":[{\"no\":123,\"tim\":\"000777\",\"ext\":\".png\",\"filename\":null}]}")))));
        SourceAttachment file = Assert.Single(Assert.Single(result.Snapshot.Posts).Attachments!);
        Assert.Equal(eightKun ? "000777" : "777", file.MediaId);
        Assert.Equal(eightKun ? "000777.png" : "unknown.png", file.OriginalFileName);
        Assert.Equal(eightKun ? "https://nerv.8kun.top/file_store/000777.png" : "https://i.4cdn.org/g/777.png", file.Url!.AbsoluteUri);
    }

    [Theory]
    [InlineData("\"broken\"")]
    [InlineData("9223372036854775808")]
    [InlineData("null")]
    public async Task InvalidFourChanMediaTimestampCannotCommitAFinalSnapshot(string timestampJson)
    {
        string body = "{\"posts\":[{\"no\":123,\"tim\":" + timestampJson
            + ",\"ext\":\".jpg\",\"filename\":\"photo\",\"archived\":1}]}";
        FourChanPlugin plugin = new();
        ThreadFailure failure = Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(false), Context(new FixtureHttp(Response(200, body)))));
        Assert.Equal("invalid_metadata", failure.Failure.Code);
        using HttpClient client = new(new LibraryTransport(body, "[]"));
        FourChanImageBoard provider = new(client);
        IThread thread = provider.GetThread(new Uri("https://boards.4chan.org/g/thread/123"));
        await Assert.ThrowsAsync<InvalidDataException>(() => thread.checkThreadAsync(new DownloadQueue(1, 1024)));
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        Assert.Equal(0, thread.TotalMediaCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplyMediaWithoutPostIdentityCannotBeSilentlyDropped(bool eightKun)
    {
        const string body = "{\"posts\":[{\"no\":123,\"archived\":1},{\"tim\":\"456\",\"ext\":\".jpg\",\"filename\":\"photo\"}]}";
        ISourcePlugin plugin = Plugin(eightKun);
        ThreadFailure failure = Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun), Context(new FixtureHttp(Response(200, body)))));
        Assert.Equal("invalid_metadata", failure.Failure.Code);
        using HttpClient client = new(new LibraryTransport(body, "[]"));
        IImageBoard provider = eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
        IThread thread = provider.GetThread(new Uri(eightKun ? "https://8kun.top/g/res/123.html" : "https://boards.4chan.org/g/thread/123"));
        await Assert.ThrowsAsync<InvalidDataException>(() => thread.checkThreadAsync(new DownloadQueue(1, 1024)));
        Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        Assert.Equal(0, thread.TotalMediaCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogNumericIdentityPreservesLibraryNormalization(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        CatalogReadRequest request = new(new SourceBoardId(eightKun ? "8kun" : "4chan", "g"));
        const string body = "[{\"threads\":[{\"no\":\"000123\"}]}]";
        CatalogSnapshot result = Assert.IsType<CatalogSnapshot>(await plugin.ReadCatalogAsync(request, Context(new FixtureHttp(Response(200, body)))));
        Assert.Equal("123", Assert.Single(result.Snapshot.Threads).Id.ThreadId);
        using HttpClient client = new(new LibraryTransport("", body));
        IImageBoard provider = eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
        IThread legacy = Assert.Single(await provider.GetBoard(new Uri(eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/")).GetThreadsAsync());
        Assert.Equal(legacy.ThreadUri, result.Snapshot.Threads.Single().Url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utf8BomMetadataMatchesTheLibraryReader(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        SdkThreadSnapshot thread = Assert.IsType<SdkThreadSnapshot>(await plugin.ReadThreadAsync(Request(eightKun),
            Context(new FixtureHttp(Response(200, "\uFEFF{\"posts\":[{\"no\":123,\"com\":\"BOM text\"}]}")))));
        Assert.Equal("BOM text", Assert.Single(thread.Snapshot.Posts).Text);
        CatalogReadRequest request = new(new SourceBoardId(eightKun ? "8kun" : "4chan", "g"));
        CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await plugin.ReadCatalogAsync(request,
            Context(new FixtureHttp(Response(200, "\uFEFF[{\"threads\":[{\"no\":123}]}]")))));
        Assert.Equal("123", Assert.Single(catalog.Snapshot.Threads).Id.ThreadId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecimalUrlIdentityIsNotRewrittenOrRejectedBeforeProviderResponse(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        ThreadReadRequest request = new(new SourceThreadId(eightKun ? "8kun" : "4chan", "g", "0000"));
        FixtureHttp http = new(Response(404, ""));
        ThreadNotFound result = Assert.IsType<ThreadNotFound>(await plugin.ReadThreadAsync(request, Context(http)));
        Assert.Equal("0000", result.Thread.ThreadId);
        Assert.Equal(eightKun ? "https://8kun.top/g/res/0000.json" : "https://a.4cdn.org/g/thread/0000.json", Assert.Single(http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedFixtureMatchesLibraryPostsAttachmentsAndCatalog(bool eightKun)
    {
        string json = Fixture("thread.json");
        ISourcePlugin plugin = Plugin(eightKun);
        ThreadReadRequest request = Request(eightKun);
        FixtureHttp http = new(Response(200, json));
        SdkThreadSnapshot result = Assert.IsType<SdkThreadSnapshot>(await plugin.ReadThreadAsync(request, Context(http)));
        Assert.Empty(SdkValidation.ValidateThreadResult(request, result, plugin.Describe().Sources.Single()));
        Assert.Equal(Endpoint(eightKun), Assert.Single(http.Requests).Url.AbsoluteUri);
        Assert.Equal(HostHttpMethod.Get, http.Requests.Single().Method);
        Assert.Equal("Synthetic & thread", result.Snapshot.Title);
        Assert.All(result.Snapshot.Posts, post => Assert.Equal(SourceTextFormat.Html, post.TextFormat));
        Assert.Equal("Author & name", result.Snapshot.Posts[0].Author);
        Assert.Equal(eightKun ? 3 : 1, result.Snapshot.Posts[0].Attachments!.Count);
        Assert.Equal("777", result.Snapshot.Posts[0].Attachments![0].MediaId);
        Assert.Equal(".png", result.Snapshot.Posts[0].Attachments![0].Extension);
        Assert.Null(result.Snapshot.Posts[1].Attachments);
        Assert.Equal(SourceFieldPresence.Text, result.Snapshot.Posts[1].Fields);
        Assert.True(result.Snapshot.Posts[2].Fields.HasFlag(SourceFieldPresence.Subject));
        Assert.Null(result.Snapshot.Posts[2].Subject);
        Assert.True(result.Snapshot.Posts[2].Attachments![0].IsDeleted);
        Assert.Equal(SourceFieldPresence.Attachments, result.Snapshot.Posts[3].Fields);
        Assert.Empty(result.Snapshot.Posts[3].Attachments!);

        string profile = Path.Combine(Path.GetTempPath(), "meowchan-official-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ThreadContentRuntime runtime = new(profile, lease);
            await runtime.InitializeAsync();
            await runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected))]);
            using HttpClient client = new(new LibraryTransport(json, Fixture("catalog.json")));
            IImageBoard provider = eightKun ? new EightKunImageBoard(client) : new FourChanImageBoard(client);
            DownloadQueue queue = new(1, 1L << 30);
            await using Registry registry = new([provider], queue, Path.Combine(profile, "media"), TimeSpan.FromHours(1), 60,
                persistSession: false, contentRuntime: runtime);
            IThread thread = (await registry.AddAsync(result.Snapshot.Url.AbsoluteUri)).Thread!;
            await thread.StopMonitoringAsync();
            await thread.checkThreadAsync(queue);
            await using ArchiveReader reader = await runtime.OpenReaderAsync();
            ArchivedThreadView captured = (await reader.ReadThreadAsync(new ResourceId(request.Thread.SourceId, "g", "123")))!;
            Assert.Equal(result.Snapshot.Posts.Count, captured.PostCount);
            foreach ((SourcePost expected, CapturedPost actual) in result.Snapshot.Posts.Zip(captured.GetPosts()))
            {
                Assert.Equal(expected.PostId, actual.PostId);
                Assert.Equal((int)expected.Fields, (int)actual.Fields);
                Assert.Equal(expected.PostedAt, actual.PostedAt);
                Assert.Equal(expected.Subject, actual.Subject);
                Assert.Equal(expected.Author, actual.Author);
                Assert.Equal(expected.Text, actual.Html);
                Assert.Equal(expected.IsDeleted, actual.IsDeleted);
                SourceAttachment[] expectedFiles = expected.Attachments?.ToArray() ?? [];
                CapturedAttachment[] actualFiles = actual.Attachments?.ToArray() ?? [];
                Assert.Equal(expectedFiles.Length, actualFiles.Length);
                foreach ((SourceAttachment file, CapturedAttachment stored) in expectedFiles.Zip(actualFiles))
                {
                    Assert.Equal(file.Position, stored.Position);
                    Assert.Equal(file.MediaId, stored.MediaId);
                    Assert.Equal(file.Url?.AbsoluteUri, stored.Url);
                    Assert.Equal(file.OriginalFileName, stored.OriginalFileName);
                    Assert.Equal(file.ExpectedSize, stored.ExpectedSize);
                    Assert.Equal(file.ExpectedMd5, stored.ExpectedMd5);
                    Assert.Equal(file.IsDeleted, stored.IsDeleted);
                }
            }
            CatalogReadRequest catalogRequest = new(new SourceBoardId(request.Thread.SourceId, "g"));
            CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await plugin.ReadCatalogAsync(catalogRequest, Context(new FixtureHttp(Response(200, Fixture("catalog.json"))))));
            Assert.Empty(SdkValidation.ValidateCatalogResult(catalogRequest, catalog, plugin.Describe().Sources.Single()));
            IThread[] legacyCatalog = (await provider.GetBoard(new Uri(eightKun ? "https://8kun.top/g/" : "https://boards.4chan.org/g/")).GetThreadsAsync()).ToArray();
            Assert.Equal(legacyCatalog.Select(item => item.ThreadUri), catalog.Snapshot.Threads.Select(item => item.Url));
            // SDK metadata stays absent; display fallback belongs to the host, not the provider.
            SourceThreadReference untitled = Assert.Single(catalog.Snapshot.Threads, item => item.Id.ThreadId == "124");
            Assert.Null(untitled.Title);
            Assert.Null(untitled.OpeningText);
            Assert.Equal($"{request.Thread.SourceId} /g/ 124", Assert.Single(legacyCatalog, item => item.ThreadId == "124").Title);
            Assert.Equal(legacyCatalog.Select(item => item.Title), catalog.Snapshot.Threads.Select(item => string.IsNullOrWhiteSpace(item.Title)
                ? $"{item.Id.SourceId} /{item.Id.BoardId}/ {item.Id.ThreadId}" : item.Title));
            Assert.True(catalog.Snapshot.IsComplete);
            Assert.Equal(3, catalog.Snapshot.Threads.Count);
        }
        finally { Directory.Delete(profile, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedCarriesValidatorsAndNeverCreatesAnEmptySnapshot(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        FixtureHttp http = new(Response(304, "not JSON"));
        ThreadReadRequest request = Request(eightKun) with { HasSnapshot = true, Validators = Validators };
        ThreadUnchanged unchanged = Assert.IsType<ThreadUnchanged>(await plugin.ReadThreadAsync(request, Context(http)));
        Assert.Equal(Validators, unchanged.Validators);
        Assert.Equal(Validators, http.Requests.Single().Validators);
        Assert.Empty(SdkValidation.ValidateThreadResult(request, unchanged, plugin.Describe().Sources.Single()));
        Assert.Equal("unexpected_304", Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun), Context(http))).Failure.Code);
        FixtureHttp noValidator = new(Response(304, "") with { Validators = null });
        Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun) with { HasSnapshot = true }, Context(noValidator)));
        CatalogReadRequest catalogRequest = new(new SourceBoardId(request.Thread.SourceId, "g"), Validators: Validators, HasSnapshot: true);
        CatalogUnchanged catalog = Assert.IsType<CatalogUnchanged>(await plugin.ReadCatalogAsync(catalogRequest, Context(http)));
        Assert.Empty(SdkValidation.ValidateCatalogResult(catalogRequest, catalog, plugin.Describe().Sources.Single()));
        Assert.IsType<CatalogFailure>(await plugin.ReadCatalogAsync(catalogRequest with { HasSnapshot = false }, Context(http)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyDocumentedEndSignalsAreTerminal(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        ThreadReadRequest request = Request(eightKun);
        SdkThreadSnapshot snapshot = Assert.IsType<SdkThreadSnapshot>(await plugin.ReadThreadAsync(request,
            Context(new FixtureHttp(Response(200, "{\"posts\":[{\"no\":123,\"locked\":1,\"archived\":1,\"com\":\"final\"}]}")))));
        Assert.Equal(eightKun ? SourceThreadState.Active : SourceThreadState.Archived, snapshot.Snapshot.SourceState);
        Assert.Equal("final", Assert.Single(snapshot.Snapshot.Posts).Text);
        Assert.IsType<ThreadNotFound>(await plugin.ReadThreadAsync(request, Context(new FixtureHttp(Response(404, "")))));
        foreach (int status in new[] { 206, 403, 410, 429, 500 })
            Assert.Equal(status, Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(request, Context(new FixtureHttp(Response(status, ""))))).Failure.HttpStatusCode);
        Assert.IsType<CatalogFailure>(await plugin.ReadCatalogAsync(new(new SourceBoardId(request.Thread.SourceId, "g")), Context(new FixtureHttp(Response(404, "")))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredFailureAndCancellationAreForwardedWithoutRetries(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        DateTimeOffset retry = DateTimeOffset.FromUnixTimeSeconds(2000000000);
        FixtureHttp deferred = new(new HostHttpDeferred(retry, "provider_cooldown", 429));
        ThreadDeferred result = Assert.IsType<ThreadDeferred>(await plugin.ReadThreadAsync(Request(eightKun), Context(deferred)));
        Assert.Equal(retry, result.RetryAt);
        Assert.Single(deferred.Requests);
        FailureDetail detail = new("host_failure", "Synthetic transport failure");
        FixtureHttp failed = new(new HostHttpFailure(detail));
        Assert.Same(detail, Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun), Context(failed))).Failure);
        Assert.Single(failed.Requests);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ReadThreadAsync(Request(eightKun), Context(failed), cancellation.Token));
        Assert.Equal(cancellation.Token, failed.LastToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedPartialAndOversizedBodiesCannotBecomeDestructiveSnapshots(bool eightKun)
    {
        ISourcePlugin plugin = Plugin(eightKun);
        foreach (string body in new[] { "{broken", "{}", "[]", "{\"posts\":null}", "{\"posts\":[{\"no\":123},{\"no\":123}]}", "{\"posts\":[{\"no\":123,\"time\":\"invalid\"}]}" })
            Assert.Equal("invalid_metadata", Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun), Context(new FixtureHttp(Response(200, body))))).Failure.Code);
        SdkThreadSnapshot partial = Assert.IsType<SdkThreadSnapshot>(await plugin.ReadThreadAsync(Request(eightKun), Context(new FixtureHttp(Response(200, "{\"posts\":[{\"no\":123}]}")))));
        Assert.Null(partial.Snapshot.Title);
        Assert.Equal(SourceFieldPresence.None, Assert.Single(partial.Snapshot.Posts).Fields);
        Assert.Null(partial.Snapshot.Posts[0].Attachments);
        FixtureHttp oversized = new(Response(200, "") with { Body = new byte[SdkLimits.MaximumMetadataBytes + 1] });
        Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun), Context(oversized))).Failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidIdentityOrUnsupportedCursorDoesNotRequestHttp(bool eightKun)
    {
        FixtureHttp http = new(Response(200, ""));
        ISourcePlugin plugin = Plugin(eightKun);
        Assert.IsType<ThreadFailure>(await plugin.ReadThreadAsync(Request(eightKun) with { Thread = new SourceThreadId("wrong", "../g", "123") }, Context(http)));
        Assert.IsType<CatalogFailure>(await plugin.ReadCatalogAsync(new(new SourceBoardId(eightKun ? "8kun" : "4chan", "g"), Cursor: "unexpected"), Context(http)));
        Assert.Empty(http.Requests);
    }

    private static ISourcePlugin Plugin(bool eightKun) => eightKun ? new EightKunPlugin() : new FourChanPlugin();
    private static ThreadReadRequest Request(bool eightKun) => new(new SourceThreadId(eightKun ? "8kun" : "4chan", "g", "123"));
    private static string Endpoint(bool eightKun) => eightKun ? "https://8kun.top/g/res/123.json" : "https://a.4cdn.org/g/thread/123.json";
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static HostHttpResponse Response(int status, string json) => new(status, new Uri("https://a.4cdn.org/g/thread/123.json"),
        new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json), Validators);
    private static PluginCallContext Context(IHostHttp http) => new(new PluginConfiguration("test", new SettingsSnapshot("1", []),
        new Dictionary<string, SettingsSnapshot>()), http, new NoServices(), new NoServices(), new NoServices());

    private sealed class FixtureHttp(HostHttpResult result) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = [];
        internal CancellationToken LastToken { get; private set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class NoServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source plugins cannot read original image assets.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Source plugins need no local resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Unexpected logging");
    }

    private sealed class LibraryTransport(string thread, string catalog) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            string body = path == "/boards.json" ? (request.RequestUri.Host == "8kun.top" ? "[]" : "{\"boards\":[]}")
                : path.EndsWith("/catalog.json", StringComparison.Ordinal) ? catalog : thread;
            if (!path.EndsWith(".json", StringComparison.Ordinal)) throw new InvalidOperationException("No fixture media download is permitted.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
