using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using MeowChan.TestSupport;

namespace MeowChan.PluginRuntime.Tests;

public sealed class E621OriginalWorkerTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("meowchan-family-original-");
    private readonly B9Clock _clock = new(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
    private readonly OfflineTransport _offline = new();

    [Theory]
    [InlineData("e621", "e621.net", "static1.e621.net", "s")]
    [InlineData("e926", "e926.net", "static1.e621.net", "s")]
    [InlineData("danbooru", "danbooru.donmai.us", "cdn.donmai.us", "g")]
    public Task PackagedSourceSearchesPagesReadsAndLinksTheVerifiedOriginal(
        string sourceId, string metadataHost, string mediaHost, string rating) =>
        WithPackagedPluginAsync(sourceId, async context =>
        {
            SourceDescription source = context.Source(sourceId);
            string query = $"forest rating:{rating}";
            string encoded = Uri.EscapeDataString(query);
            string origin = $"https://{metadataHost}";
            byte[] bytes = B9Post.SyntheticBytes(42);
            string originalUrl = $"https://{mediaHost}/data/42.png";
            JsonObject original = Post(sourceId, 42, originalUrl, bytes);
            string firstUrl = $"{origin}/posts.json?tags={encoded}&page=1&limit=20";
            string nextUrl = $"{origin}/posts.json?tags={encoded}&page=b43&limit=20";
            string postUrl = $"{origin}/posts/42.json";
            _offline.Json(firstUrl, SearchBody(sourceId, Enumerable.Range(43, 20).Reverse()
                .Select(id => Post(sourceId, id, $"https://{mediaHost}/data/{id}.png", bytes)).ToArray()));
            _offline.Json(nextUrl, SearchBody(sourceId, original.DeepClone()));
            _offline.Json(postUrl, PostBody(sourceId, original));
            _offline.Bytes(originalUrl, bytes);

            Assert.Equal(origin + "/", source.BaseUri.AbsoluteUri);
            if (sourceId == "e926")
            {
                BooruSearchFailure refused = Assert.IsType<BooruSearchFailure>(
                    await context.Search.SearchAsync(context.Worker, sourceId, "forest rating:e", 1));
                Assert.Equal(BooruSearchService.InvalidQueryCode, refused.Failure.Code);
                Assert.Empty(_offline.Requests);
            }

            BooruSearchPage first = Assert.IsType<BooruSearchSnapshot>(
                await AtProviderPace(context.Search.SearchAsync(context.Worker, sourceId, query, 1))).Page;
            Assert.Equal(20, first.Posts.Count);
            Assert.Equal("43", first.NextBeforePostId);
            Assert.All(first.Posts, post => Assert.Equal(sourceId, post.Id.SourceId));
            BooruSearchPage next = Assert.IsType<BooruSearchSnapshot>(await AtProviderPace(
                context.Search.SearchBeforeAsync(context.Worker, sourceId, query, first.NextBeforePostId!))).Page;
            BooruPost result = Assert.Single(next.Posts);
            Assert.True(next.IsEnd);
            BooruPostClaim claim = await ReadClaimAsync(context, source, "42");
            Assert.Equal(new BooruPostId(sourceId, "42"), result.Id);
            Assert.Equal($"{origin}/posts/42", claim.PostUrl);
            if (sourceId == "e621")
            {
                Assert.Equal(-3, result.Score);
                Assert.Null(claim.Score);
                Assert.False(claim.Fields.HasFlag(BooruPostClaimPresence.Score));
            }

            using HttpClient media = context.OpenMedia(source);
            await using QueueSession session = new(_workspace.FullName);
            BooruOriginalQueue originals = session.Originals(source, media);
            BooruOriginalAdmission admission = await originals.AdmitAsync(claim, BooruFindingKind.OneShot, query: query);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
            await AssertLinkedAsync(session.Catalog.Read, claim.Identity, admission, bytes);

            Assert.Equal([firstUrl, nextUrl, postUrl, originalUrl], _offline.Requests.Select(request => request.Url).ToArray());
            string contact = sourceId == "danbooru" ? "MeowChan)" : "MeowChan/issues)";
            Assert.All(_offline.Requests.Take(3), request =>
                Assert.Equal("MeowChan/1.2.0 (+https://github.com/grassmann-it/" + contact, request.UserAgent));
        });

    [Theory]
    [InlineData("missing-size", (int)BooruOriginalSelectionError.MissingIntegrityClaims)]
    [InlineData("missing-md5", (int)BooruOriginalSelectionError.MissingIntegrityClaims)]
    [InlineData("foreign-host", (int)BooruOriginalSelectionError.OriginalUrlMissing)]
    [InlineData("deleted", (int)BooruOriginalSelectionError.MissingOriginal)]
    [InlineData("restricted", (int)BooruOriginalSelectionError.OriginalUrlMissing)]
    [InlineData("preview-only", (int)BooruOriginalSelectionError.MissingOriginal)]
    [InlineData("wrong-length", (int)BooruOriginalSelectionError.None)]
    [InlineData("wrong-md5", (int)BooruOriginalSelectionError.None)]
    public Task InvalidOriginalNeverPublishesOrLinks(string fault, int selectionError) =>
        WithPackagedPluginAsync("e621", async context =>
        {
            const string originalUrl = "https://static1.e621.net/data/42.png";
            byte[] expected = B9Post.SyntheticBytes(42);
            JsonObject post = Post("e621", 42, originalUrl, expected);
            JsonNode file = post["file"]!;
            byte[] delivered = expected;
            switch (fault)
            {
                case "missing-size": file["size"] = null; break;
                case "missing-md5": file["md5"] = null; break;
                case "foreign-host": file["url"] = "https://other.example/data/42.png"; break;
                case "deleted":
                    post["flags"]!["deleted"] = true;
                    post.Remove("file");
                    post.Remove("preview");
                    break;
                case "restricted": file["url"] = null; break;
                case "preview-only": post.Remove("file"); break;
                case "wrong-length": delivered = expected[..^1]; break;
                case "wrong-md5": delivered = B9Post.SyntheticBytes(43); break;
                default: throw new ArgumentException(fault);
            }
            const string postUrl = "https://e621.net/posts/42.json";
            _offline.Json(postUrl, PostBody("e621", post));
            _offline.Bytes(originalUrl, delivered);

            SourceDescription source = context.Source("e621");
            BooruPostClaim claim = await ReadClaimAsync(context, source, "42");
            using HttpClient media = context.OpenMedia(source);
            await using QueueSession session = new(_workspace.FullName);
            if (fault == "deleted")
            {
                BooruPostClaim previous = new(source.SourceId, "42", BooruPostClaimPresence.Media,
                    Media: [new(BooruPostClaimMediaRole.Original, originalUrl, ".png", expected.Length,
                        ExpectedMd5: Convert.ToBase64String(MD5.HashData(expected)))]);
                session.Catalog.UpsertPost(previous, _clock.GetUtcNow());
            }
            BooruOriginalAdmission admission = await session.Originals(source, media).AdmitAsync(claim, BooruFindingKind.OneShot);
            BooruOriginalDownloadOutcome outcome = await admission.Completion.WaitAsync(B9Wait.Timeout);
            BooruOriginalSelectionError error = (BooruOriginalSelectionError)selectionError;
            bool fetch = error == BooruOriginalSelectionError.None;
            Assert.Equal(fetch ? BooruOriginalAdmissionStatus.Queued : BooruOriginalAdmissionStatus.Unavailable, admission.Status);
            Assert.Equal(fetch ? BooruOriginalDownloadState.DownloadFailed : BooruOriginalDownloadState.Unavailable, outcome.State);
            Assert.Equal(error, admission.SelectionError);
            Assert.Null(outcome.AssetId);
            BooruPostRecord stored = Assert.Single(session.Catalog.Read().Posts);
            Assert.Null(stored.LocalAssetId);
            Assert.Null(stored.LocalPath);
            string[] expectedRequests = fetch ? [postUrl, originalUrl] : [postUrl];
            Assert.Equal(expectedRequests, _offline.Requests.Select(request => request.Url).ToArray());
            if (Directory.Exists(session.OutputRoot))
            {
                Assert.Empty(Directory.EnumerateFiles(session.OutputRoot, "*.png", SearchOption.AllDirectories));
                Assert.Empty(Directory.EnumerateFiles(session.OutputRoot, "*.part", SearchOption.AllDirectories));
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EqualPostIdsKeepTheirActionsAndBothFindingsAfterCancelAndRestart(bool sameBytes) =>
        WithPackagedPluginAsync("e621", async context =>
        {
            byte[] first = B9Post.SyntheticBytes(42);
            byte[] second = sameBytes ? first : B9Post.SyntheticBytes(43);
            const string firstUrl = "https://static1.e621.net/data/first.png";
            string secondUrl = sameBytes ? firstUrl : "https://static1.e621.net/data/second.png";
            _offline.Json("https://e926.net/posts/42.json", PostBody("e926", Post("e926", 42, firstUrl, first)));
            _offline.Json("https://e621.net/posts/42.json", PostBody("e621", Post("e621", 42, secondUrl, second)));
            _offline.Bytes(firstUrl, first);
            _offline.Bytes(secondUrl, second);
            SourceDescription e926 = context.Source("e926");
            SourceDescription e621 = context.Source("e621");
            BooruPostClaim safe = await ReadClaimAsync(context, e926, "42");
            BooruPostClaim other = await ReadClaimAsync(context, e621, "42");
            using HttpClient safeMedia = context.OpenMedia(e926);
            using HttpClient otherMedia = context.OpenMedia(e621);

            await using (QueueSession initial = new(_workspace.FullName))
            {
                await initial.Queue.SetPausedAsync(true);
                BooruOriginalQueue safeOriginals = initial.Originals(e926, safeMedia);
                BooruOriginalQueue otherOriginals = initial.Originals(e621, otherMedia);
                BooruOriginalAdmission safeRequest = await safeOriginals.AdmitAsync(safe, BooruFindingKind.OneShot, query: "safe source");
                BooruOriginalAdmission otherRequest = await otherOriginals.AdmitAsync(other, BooruFindingKind.OneShot, query: "other source");
                Assert.Equal(BooruOriginalAdmissionStatus.Queued, safeRequest.Status);
                Assert.Equal(BooruOriginalAdmissionStatus.Queued, otherRequest.Status);
                Assert.NotEqual(safeRequest.Identity, otherRequest.Identity);
                Assert.True(await otherOriginals.CancelAsync("42"));
                Assert.Equal(BooruOriginalDownloadState.Cancelled, (await otherRequest.Completion.WaitAsync(B9Wait.Timeout)).State);
                Assert.Equal(safe.Identity, Assert.Single(safeOriginals.GetPendingOriginals()).Identity);
                Assert.Empty(otherOriginals.GetPendingOriginals());
            }
            Assert.Equal(2, _offline.Requests.Count);

            await using (QueueSession restarted = new(_workspace.FullName))
            {
                BooruOriginalQueue otherOriginals = restarted.Originals(e621, otherMedia);
                Assert.Empty(await otherOriginals.RecoverPendingAsync());
                BooruOriginalAdmission recovered = Assert.Single(await restarted.Originals(e926, safeMedia).RecoverPendingAsync());
                await AssertLinkedAsync(restarted.Catalog.Read, safe.Identity, recovered, first);
                BooruOriginalAdmission retried = await otherOriginals.RetryAsync("42");
                await AssertLinkedAsync(restarted.Catalog.Read, other.Identity, retried, second);
            }

            await using QueueSession reopened = new(_workspace.FullName);
            BooruCatalogSnapshot saved = reopened.Catalog.Read();
            var (entries, unreadable) = await BooruMediaProjection.EntriesAsync(saved, MediaAssetReader.OpenAsync);
            Assert.Equal(0, unreadable);
            Assert.Equal(2, entries.Count);
            Assert.NotEqual(entries[0].Thread, entries[1].Thread);
            Assert.NotEqual(entries[0].MediaId, entries[1].MediaId);
            Assert.Equal(sameBytes, entries[0].Sha256 == entries[1].Sha256);
            foreach ((BooruPostClaim claim, byte[] bytes, string query) in new[] { (safe, first, "safe source"), (other, second, "other source") })
            {
                MediaLibraryEntry entry = Assert.Single(entries, item => item.BooruIdentity == claim.Identity);
                GalleryEntry gallery = Assert.IsType<GalleryEntry>(
                    await BooruMediaGallery.ResolveAsync(entry, saved, MediaAssetReader.OpenAsync));
                Assert.Equal(claim.PostUrl, gallery.ThreadUrl);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(gallery.Asset.Path));
                Assert.Equal(query, Assert.Single(saved.TryGetPost(claim.Identity)!.Findings).Query);
            }
            Assert.Equal([firstUrl, secondUrl], _offline.Requests.Skip(2).Select(request => request.Url).ToArray());
        });

    private async Task WithPackagedPluginAsync(string sourceId, Func<WorkerContext, Task> run)
    {
        string payload = DesktopPayload();
        string compiler = Path.Combine(payload, "plugin-compiler");
        string worker = Path.Combine(payload, "plugin-worker");
        string suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string name = sourceId == "danbooru" ? "Danbooru" : "E621";
        string bundles = Path.Combine(payload, "plugins");
        PluginInventory inventory = await new PluginInstallationInventory().ScanAsync(bundles);
        PluginInstallationCandidate candidate = Assert.Single(inventory.Candidates,
            item => Path.GetFileName(item.Path) == $"MeowChan.Plugin.{name}.plugin.json");
        Assert.Empty(candidate.Diagnostics);
        await using PluginCompilationService compilation = new(new PluginCompilerOptions(
            Path.Combine(compiler, "MeowChan.PluginCompiler" + suffix), compiler, Path.Combine(_workspace.FullName, "plugin-cache")));
        PluginBuildResult imported = await compilation.ImportBundleAsync(candidate.Path);
        Assert.True(imported.Succeeded, string.Join(Environment.NewLine, imported.Diagnostics));
        PluginArtifact artifact = imported.Artifact!;
        PluginWorkerLaunch launch = new(Path.Combine(worker, "MeowChan.PluginWorker" + suffix), [], Path.Combine(_workspace.FullName, "workers"));
        await using PluginWorkerManager manager = new();
        PluginDescription description = await manager.DescribeAsync(launch, artifact);
        Assert.Equal(sourceId == "danbooru" ? "meowchan.official.danbooru" : "meowchan.official.e621", description.PluginId);
        await PluginInstallationInventory.RecordDeliveredAsync(bundles, candidate, artifact, description);
        PluginConfiguration configuration = new("test", new("1", []),
            description.Sources.ToDictionary(source => source.SourceId, _ => new SettingsSnapshot("1", [])));
        using HttpClient transport = new(_offline, disposeHandler: false);
        using BooruHostHttp http = new(transport, new RequestPolicyState(_clock), description.Sources, productVersion: "1.2.0");
        BooruHostServices services = new(new RejectNonBooruCalls(), http);
        await using IPluginWorkerClient client = await manager.StartAsync(launch, artifact, configuration, services);
        await run(new WorkerContext(client, new BooruSearchService(services), description, transport));
    }

    private static string DesktopPayload()
    {
        DirectoryInfo workerOutput = new(Path.GetDirectoryName(BuiltTestBinaries.WorkerAssembly())!);
        DirectoryInfo parent = workerOutput.Parent!;
        if (parent.Name == "MeowChan.PluginWorker")
            return Path.Combine(parent.Parent!.FullName, "MeowChan", workerOutput.Name);
        DirectoryInfo? root = parent;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MeowChan.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root missing from test build.");
        // Use the exact matching desktop build, including manifests and both packaged helper apphosts.
        return Path.Combine(root.FullName, "bin", parent.Name, workerOutput.Name);
    }

    private async Task<T> AtProviderPace<T>(Task<T> operation)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        return await operation.WaitAsync(B9Wait.Timeout);
    }

    private async Task<BooruPostClaim> ReadClaimAsync(WorkerContext context, SourceDescription source, string postId)
    {
        BooruPostId expected = new(source.SourceId, postId);
        BooruPostSnapshot snapshot = Assert.IsType<BooruPostSnapshot>(
            await AtProviderPace(context.Search.ReadPostAsync(context.Worker, expected)));
        Assert.Equal(expected, snapshot.Post.Id);
        return BooruClaimMapper.Map(snapshot.Post, source.SourceId);
    }

    private static async Task AssertLinkedAsync(Func<BooruCatalogSnapshot> read, BooruPostKey key,
        BooruOriginalAdmission admission, byte[] expected)
    {
        BooruOriginalDownloadOutcome linked = await admission.Completion.WaitAsync(B9Wait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(BooruMediaKey.Original(key), linked.Identity);
        BooruPostRecord record = Assert.IsType<BooruPostRecord>(read().TryGetPost(key));
        Assert.Equal(linked.AssetId, record.LocalAssetId);
        Assert.Equal(linked.LocalPath, record.LocalPath);
        Assert.Equal(expected.LongLength, linked.Length);
        byte[] saved = await File.ReadAllBytesAsync(linked.LocalPath!);
        Assert.Equal(expected, saved);
        string md5 = Assert.Single(record.Post.Media, media => media.Role == BooruPostClaimMediaRole.Original).ExpectedMd5!;
        Assert.Equal(md5, Convert.ToBase64String(MD5.HashData(saved)));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(linked.LocalPath!)!);
        MediaAssetReference asset = Assert.IsType<MediaAssetReference>(manifest.FindById(linked.AssetId!));
        Assert.Equal(BooruMediaKey.Original(key).StorageId, asset.MediaIdentifier);
        await using Stream verified = await manifest.OpenVerifiedReadAsync(asset.AssetId);
        Assert.Equal(expected.LongLength, verified.Length);
    }

    private static JsonObject Post(string source, int id, string url, byte[] bytes)
    {
        if (source == "danbooru")
        {
            JsonObject post = new B9Post(id, bytes, General: "forest").ToJson();
            post["file_url"] = url;
            return post;
        }
        return new JsonObject
        {
            ["id"] = id,
            ["rating"] = "s",
            ["tags"] = new JsonObject { ["general"] = new JsonArray("forest") },
            ["score"] = new JsonObject { ["total"] = -3 },
            ["flags"] = new JsonObject { ["deleted"] = false },
            ["file"] = new JsonObject
            {
                ["url"] = url,
                ["ext"] = "png",
                ["size"] = bytes.Length,
                ["md5"] = Convert.ToHexStringLower(MD5.HashData(bytes))
            },
            ["preview"] = new JsonObject
            {
                ["url"] = "https://static1.e621.net/data/preview/" + id.ToString(CultureInfo.InvariantCulture) + ".jpg"
            }
        };
    }

    private static JsonNode PostBody(string source, JsonObject post) =>
        source == "danbooru" ? post : new JsonObject { ["post"] = post };

    private static JsonNode SearchBody(string source, params JsonNode[] posts)
    {
        JsonArray items = new(posts);
        return source == "danbooru" ? items : new JsonObject { ["posts"] = items };
    }

    public void Dispose()
    {
        _offline.Dispose();
        _workspace.Delete(recursive: true);
    }

    private sealed record WorkerContext(IPluginWorkerClient Worker, BooruSearchService Search, PluginDescription Description, HttpClient Transport)
    {
        private readonly RequestPolicyState _mediaPolicy = new();
        internal SourceDescription Source(string id) => Assert.Single(Description.Sources, source => source.SourceId == id);
        internal HttpClient OpenMedia(SourceDescription source) => PluginOriginalMediaHttp.CreateClient(Transport, source, _mediaPolicy);
    }

    private sealed class QueueSession : IAsyncDisposable
    {
        private readonly ProfileLease _profile;
        internal string OutputRoot { get; }
        internal DownloadQueue Queue { get; }
        internal BooruCatalogStore Catalog { get; }

        internal QueueSession(string workspace)
        {
            OutputRoot = Path.Combine(workspace, "media");
            _profile = ProfileLease.Acquire(Path.Combine(workspace, "profile"));
            Queue = new DownloadQueue(1, 1L << 30);
            Queue.ConfigureWorkState(new WorkStateStore(_profile.ProfileDirectory, _profile));
            Catalog = new BooruCatalogStore(_profile.ProfileDirectory, _profile);
            Catalog.Read();
            _profile.Admission.CompleteStartup();
        }

        internal BooruOriginalQueue Originals(SourceDescription source, HttpClient media) =>
            new(Queue, Catalog, OutputRoot, media,
                source.HttpHosts.Where(host => host.Purpose.HasFlag(HttpHostPurpose.OriginalMedia)).Select(host => host.Host),
                sourceId: source.SourceId);

        public async ValueTask DisposeAsync()
        {
            try { await Queue.StopAllAsync().WaitAsync(B9Wait.Timeout); }
            finally { _profile.Dispose(); }
        }
    }

    private sealed record Request(string Url, string UserAgent);

    private sealed class OfflineTransport : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Request> _requests = new();
        private readonly Dictionary<string, (byte[] Body, string Type)> _responses = new(StringComparer.Ordinal);
        internal IReadOnlyCollection<Request> Requests => _requests.ToArray();

        internal void Json(string url, JsonNode value) => _responses[url] = (Encoding.UTF8.GetBytes(value.ToJsonString()), "application/json");
        internal void Bytes(string url, byte[] value) => _responses[url] = (value, "image/png");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            string url = request.RequestUri!.AbsoluteUri;
            _requests.Enqueue(new(url, request.Headers.UserAgent.ToString()));
            Assert.True(_responses.TryGetValue(url, out var answer), $"Unexpected offline request {url}.");
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(answer.Body)
            };
            response.Content.Headers.ContentType = new(answer.Type);
            return Task.FromResult(response);
        }
    }

    private sealed class RejectNonBooruCalls : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The request bypassed the Booru host.");

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Booru metadata does not read local image assets.");
    }
}
