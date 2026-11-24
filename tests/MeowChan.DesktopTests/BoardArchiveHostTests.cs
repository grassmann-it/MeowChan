using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Delivered plugin hosts")]
public sealed class BoardArchiveHostTests
{
    internal const string BoardUrl = "https://boards.4chan.org/g/";
    internal static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    [Fact]
    public async Task TheDeliveredHostUsesArchiveValidatorsAndAdmitsOnlyExplicitlyChosenThreads()
    {
        using TempDirectory profile = new();
        using ArchiveTransport transport = new();
        Configure(profile.Path);
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => CreateHost(profile.Path, transport));
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(35));
        CancellationToken token = deadline.Token;

        BoardArchiveLocation fromRow = Assert.IsType<BoardArchiveLocation>(host.FindBoardArchive(BoardUrl, fromBoard: true));
        Assert.Equal(fromRow, host.FindBoardArchive("http://boards.4channel.org/G/archive/?x=ignored#top"));
        Assert.Null(host.FindBoardArchive(BoardUrl));
        Assert.Null(host.FindBoardArchive("https://boards.4chan.org.evil.test/g/archive"));
        Assert.Null(host.FindBoardArchive("https://boards.4chan.org/g%2Ftest/archive"));
        Assert.Null(host.ClassifyAdmission(fromRow.Uri.AbsoluteUri));

        var live = await host.PreviewBoardAdmissionAsync(BoardUrl, token);
        Assert.Equal("777", Assert.Single(live.Threads).Id);
        var initial = await host.ReadBoardArchiveAsync(fromRow, token);
        Assert.NotNull(initial);
        Assert.Equal(new[] { "123", "124" }, initial.Select(thread => thread.Id));
        Assert.Equal(new[] { new Request("/g/catalog.json", null), new Request("/g/archive.json", null) }, transport.Requests.ToArray());
        Assert.Empty(host.GetView().Threads);
        Assert.Empty(host.GetView().Boards);

        Assert.Same(initial, await host.ReadBoardArchiveAsync(fromRow, token));
        Assert.Same(initial, await host.ReadBoardArchiveAsync(fromRow, token));
        Assert.Equal(new DateTimeOffset?[] { null, Modified, Modified },
            transport.Requests.Where(call => call.Path == "/g/archive.json").Select(call => call.Modified));
        Assert.Empty((await host.ReadBoardArchiveAsync(Archive(host, "empty"), token))!);
        Assert.Null(Assert.Single(transport.Requests, call => call.Path == "/empty/archive.json").Modified);
        Assert.Null(await host.ReadBoardArchiveAsync(Archive(host, "unsupported"), token));
        Assert.Equal("unexpected_304", (await Assert.ThrowsAsync<PluginWorkerException>(() =>
            host.ReadBoardArchiveAsync(Archive(host, "no_baseline"), token))).Code);
        Assert.Equal("invalid_metadata", (await Assert.ThrowsAsync<PluginWorkerException>(() =>
            host.ReadBoardArchiveAsync(Archive(host, "bad"), token))).Code);
        Assert.Equal("unexpected_304", (await Assert.ThrowsAsync<PluginWorkerException>(() =>
            host.ReadBoardArchiveAsync(Archive(host, "bad"), token))).Code);
        Assert.All(transport.Requests.Where(call => call.Path == "/bad/archive.json"), call => Assert.Null(call.Modified));
        Assert.DoesNotContain(transport.Requests, call => call.Path.Contains("/thread/", StringComparison.Ordinal));

        Assert.Null(await host.ReadBoardArchiveThreadAsync(initial[1], token));
        BoardArchiveThreadDetails details = Assert.IsType<BoardArchiveThreadDetails>(await host.ReadBoardArchiveThreadAsync(initial[0], token));
        Assert.Equal("Fixture title", details.Title);
        Assert.Equal("Opening post", details.OpeningPost);
        Assert.Equal(SourceThreadState.Archived, details.State);
        Assert.False(details.AlreadyWatching);
        Assert.Empty(host.GetView().Threads);
        DesktopAdmissionResult added = await host.AddAsync(initial[0].Url);
        DesktopAdmissionResult duplicate = await host.AddAsync(initial[0].Url);
        Assert.Equal(AdmissionStatus.Added, added.Status);
        Assert.Equal(AdmissionStatus.AlreadyPresent, duplicate.Status);
        Assert.NotNull(added.WatchId);
        Assert.Equal(added.WatchId, duplicate.WatchId);
        Assert.Equal(initial[0].Url, Assert.Single(host.GetView().Threads).Snapshot.Url);
        Assert.Empty(host.GetView().Boards);

        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IReadOnlyList<BoardArchiveThread>?> pending = host.ReadBoardArchiveAsync(Archive(host, "aborted"), abort.Token);
        await transport.AbortedStarted.Task.WaitAsync(token);
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await transport.AbortedCanceled.Task.WaitAsync(token);

        SourceCheckDeferredException deferred = await Assert.ThrowsAsync<SourceCheckDeferredException>(() =>
            host.ReadBoardArchiveAsync(Archive(host, "limited"), token));
        Assert.Equal("cooldown", deferred.ReasonCode);
        Assert.True(deferred.RetryAt > DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.Equal(HttpStatusCode.TooManyRequests, (deferred.Failure as IFailureInfoProvider)?.FailureInfo.HttpStatusCode);
        var fenced = await Assert.ThrowsAsync<SourceCheckDeferredException>(() =>
            host.ReadBoardArchiveAsync(fromRow, token));
        Assert.True(fenced.InitialNoSend);
        Assert.Equal("cooldown", fenced.ReasonCode);
        Assert.Single(transport.Requests, call => call.Path == "/limited/archive.json");
        Assert.Equal(3, transport.Requests.Count(call => call.Path == "/g/archive.json"));
    }

    [Theory]
    [InlineData(PostOrderKind.StableOrdinal, "later", "a1", 10)]
    [InlineData(PostOrderKind.StableOrdinal, "123", "a1", 10)]
    [InlineData(PostOrderKind.DecimalPostId, "92233720368547758080", "9223372036854775808", 30)]
    public async Task PreviewUsesTheDeclaredPostOrder(PostOrderKind order, string last, string first, int firstOrdinal)
    {
        await using var worker = new ArchiveWorker(order: order)
        {
            ReadPosts = (_, _) => Task.FromResult<SourcePost[]>(
                [new(last, 20, SourceFieldPresence.Text, Text: last),
                 new(first, firstOrdinal, SourceFieldPresence.Text, Text: $"<b>{first}</b>")])
        };
        using PluginSourceImageBoard source = worker.CreateSource();

        BoardArchiveThreadDetails details = Assert.IsType<BoardArchiveThreadDetails>(
            await source.ReadBoardArchiveThreadAsync("https://archive.test/g/thread/123", CancellationToken.None));
        Assert.Equal(first, details.OpeningPost);
    }

    [Theory]
    [InlineData("archive-fixture", false)]
    [InlineData("4chan", true)]
    [InlineData("8kun", true)]
    public async Task PreviewHasNoOpeningTextForAnEmptySnapshotOrAMissingNumberedOp(string sourceId, bool hasReply)
    {
        await using var worker = new ArchiveWorker(sourceId)
        {
            ReadPosts = (_, _) => Task.FromResult<SourcePost[]>(hasReply
                ? [new("124", 0, SourceFieldPresence.Text, Text: "Only a reply")] : [])
        };
        using PluginSourceImageBoard source = worker.CreateSource();

        BoardArchiveThreadDetails details = Assert.IsType<BoardArchiveThreadDetails>(
            await source.ReadBoardArchiveThreadAsync("https://archive.test/g/thread/123", CancellationToken.None));
        Assert.Equal(string.Empty, details.OpeningPost);
    }

    private static BoardArchiveLocation Archive(DesktopSessionHost host, string board)
        => Assert.IsType<BoardArchiveLocation>(host.FindBoardArchive("https://boards.4chan.org/" + board + "/archive"));

    internal static void Configure(string path) => new Config
    {
        DownloadPath = Path.Combine(path, "media"), AutomaticUpdateChecks = false,
        RecheckTimer = 3600, MinimumFreeSpaceGiB = 0, FirstRunSetupVersion = 1
    }.SaveConfig(path);

    internal static DesktopSessionHost CreateHost(string path, ArchiveTransport transport) => new(path,
        updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);

    internal sealed class ArchiveWorker : IPluginWorkerClient
    {
        private const string Host = "archive.test";

        internal ArchiveWorker(string sourceId = "archive-fixture", PostOrderKind order = PostOrderKind.StableOrdinal)
        {
            SettingsSchema settings = new("1", []);
            SourceDescription source = new(sourceId, sourceId, new Uri($"https://{Host}/"),
                SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog,
                [new(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true),
                 new(SourceResourceKind.BoardArchive, Host, "/{board}/archive", IsCanonical: true),
                 new(SourceResourceKind.Thread, Host, "/{board}/thread/{thread}", IsCanonical: true)],
                [new(Host, HttpHostPurpose.Metadata)], settings, order);
            Description = new("test.board.archive", "Board archive fixture", "1.0.0", SdkContract.Version, "Offline archive fixture",
                [source], [], settings, []);
        }

        public PluginDescription Description { get; }
        public PluginWorkerIdentity Identity { get; } = new("test", "build", "config");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
        internal Func<string, CancellationToken, Task<string[]>> ReadIds { get; init; } = (_, _) => Task.FromResult<string[]>([]);
        internal Func<string, CancellationToken, Task<SourcePost[]>> ReadPosts { get; init; } = (_, _) => Task.FromResult<SourcePost[]>([]);

        internal PluginSourceImageBoard CreateSource() =>
            new(Description, Description.Sources[0].SourceId, _ => Task.FromResult<IPluginWorkerClient>(this));

        public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            Assert.True(request.IsArchive);
            string[] ids = await ReadIds(request.Board.BoardId, cancellationToken);
            return new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, ids.Select(id =>
            {
                SourceThreadId thread = new(request.Board.SourceId, request.Board.BoardId, id);
                return new SourceThreadReference(thread, ThreadUri(thread));
            }).ToArray(), IsComplete: true));
        }

        public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            SourcePost[] posts = await ReadPosts(request.Thread.ThreadId, cancellationToken);
            return new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread, ThreadUri(request.Thread),
                $"Thread {request.Thread.ThreadId}", SourceThreadState.Archived, posts));
        }

        private static Uri ThreadUri(SourceThreadId id) => new($"https://{Host}/{id.BoardId}/thread/{id.ThreadId}");
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed record Request(string Path, DateTimeOffset? Modified);

    internal sealed class ArchiveTransport : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal TaskCompletionSource AbortedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortedCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool EmptyLiveCatalog { get; init; }
        internal HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("a.4cdn.org", request.RequestUri!.Host);
            string path = request.RequestUri.AbsolutePath;
            Requests.Enqueue(new Request(path, request.Headers.IfModifiedSince));
            int count = _counts.AddOrUpdate(path, 1, (_, previous) => previous + 1);
            if (path == "/aborted/archive.json")
            {
                using CancellationTokenRegistration registration = token.Register(() => AbortedCanceled.TrySetResult());
                AbortedStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var (status, body, modified) = path switch
            {
                "/g/catalog.json" => (HttpStatusCode.OK, EmptyLiveCatalog ? "[]" : """[{"threads":[{"no":777}]}]""", Modified.AddDays(1)),
                "/g/archive.json" when count == 1 => (HttpStatusCode.OK, "[123,124,123]", (DateTimeOffset?)Modified),
                "/g/archive.json" or "/no_baseline/archive.json" => (HttpStatusCode.NotModified, "", null),
                "/empty/archive.json" => (HttpStatusCode.OK, "[]", null),
                "/unsupported/archive.json" or "/g/thread/124.json" => (HttpStatusCode.NotFound, "", null),
                "/bad/archive.json" when count == 1 => (HttpStatusCode.OK, "[3,0]", Modified),
                "/bad/archive.json" => (HttpStatusCode.NotModified, "", null),
                "/g/thread/123.json" => (HttpStatusCode.OK, """{"posts":[{"no":123,"sub":"Fixture title","com":"<p>Opening post</p>","archived":1}]}""", null),
                "/limited/archive.json" => (HttpStatusCode.TooManyRequests, "", null),
                _ => throw new InvalidOperationException("No fixture response for " + path)
            };
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
            response.Content.Headers.LastModified = modified;
            if (status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
            return response;
        }
    }
}
