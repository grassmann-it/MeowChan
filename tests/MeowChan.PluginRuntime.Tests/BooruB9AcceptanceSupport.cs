using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.PluginRuntime.Tests;

// B9 acceptance support. Everything here is synthetic and in-process:
//   real Danbooru plugin (loaded from its current build output, called in-process)
//   → real BooruSearchService / BooruHostServices / BooruHostHttp (pacing, retry, cooldown, limits)
//   → in-memory provider stub that renders Danbooru-shaped JSON from invented posts
//   → real BooruClaimMapper → real BooruOriginalQueue → real DownloadQueue → in-memory CDN
//   → real BooruCatalogStore / MediaStore on a private profile directory that survives "restarts".
// No socket is opened and no request can leave the process. The only difference to the shipped
// worker path is the process boundary (covered separately by BooruRealWorkerTests).

/// <summary>One invented Danbooru post. Bytes are the exact original served by the fake CDN.</summary>
internal sealed record B9Post(long Id, byte[] Bytes, string Rating = "g", string General = "cat_ears solo",
    string Artist = "artist_b9", bool Restricted = false, bool Deleted = false, bool Banned = false)
{
    internal string PostId => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal string Md5Hex => Convert.ToHexString(MD5.HashData(Bytes)).ToLowerInvariant();
    internal string OriginalUrl => $"https://cdn.donmai.us/original/{Md5Hex[..2]}/{Md5Hex[2..4]}/{Md5Hex}.png";
    internal string SampleUrl => $"https://cdn.donmai.us/sample/{Md5Hex[..2]}/{Md5Hex[2..4]}/sample-{Md5Hex}.jpg";
    internal string PreviewUrl => $"https://cdn.donmai.us/180x180/{Md5Hex[..2]}/{Md5Hex[2..4]}/{Md5Hex}.jpg";
    internal string Sha256Hex => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    internal static byte[] SyntheticBytes(long seed, int length = 3072) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 13 + seed * 7 + 1)).ToArray();

    internal JsonObject ToJson()
    {
        JsonObject post = new()
        {
            ["id"] = Id,
            ["created_at"] = "2026-11-01T10:00:00.000Z",
            ["score"] = 5,
            ["fav_count"] = 1,
            ["source"] = $"https://example.invalid/art/{Id}",
            ["rating"] = Rating,
            ["image_width"] = 64,
            ["image_height"] = 48,
            ["file_ext"] = "png",
            ["is_deleted"] = Deleted,
            ["is_banned"] = Banned,
            ["tag_string_general"] = General,
            ["tag_string_character"] = "",
            ["tag_string_copyright"] = "original",
            ["tag_string_artist"] = Artist,
            ["tag_string_meta"] = "",
            ["file_size"] = Bytes.Length,
            ["preview_file_url"] = PreviewUrl
        };
        if (!Restricted)
        {
            post["md5"] = Md5Hex;
            post["file_url"] = OriginalUrl;
            post["large_file_url"] = SampleUrl;
        }
        return post;
    }
}

/// <summary>What one metadata request looked like on the (fake) wire.</summary>
internal sealed record B9WireRequest(string Method, string Path, string? Tags, int? Page, int? Limit,
    DateTimeOffset At, bool HasAuthorization, bool HasCookie, string? UserAgent);

/// <summary>
/// In-process Danbooru metadata stub: <c>/posts.json?tags=&amp;page=&amp;limit=</c> (newest first per query)
/// and <c>/posts/{id}.json</c>. A scripted <see cref="Respond"/> answer wins; <see cref="Offline"/> makes
/// every attempt a network failure. Nothing here opens a socket.
/// </summary>
internal sealed class B9Provider(TimeProvider clock) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<long, B9Post> _posts = new();
    private readonly Dictionary<string, List<long>> _queries = new(StringComparer.Ordinal);
    private readonly List<B9WireRequest> _requests = new();
    private int _inFlight;
    private int _offlineAttempts;

    internal Func<B9WireRequest, int, HttpResponseMessage?>? Respond { get; set; }
    internal bool Offline { get; set; }
    internal int InFlight => Volatile.Read(ref _inFlight);
    internal int OfflineAttempts => Volatile.Read(ref _offlineAttempts);
    internal int MaxConcurrent { get; private set; }
    internal IReadOnlyList<B9WireRequest> Requests { get { lock (_gate) return _requests.ToArray(); } }
    internal int RequestCount { get { lock (_gate) return _requests.Count; } }

    internal void Add(B9Post post) { lock (_gate) _posts[post.Id] = post; }

    /// <summary>Makes posts visible for a query (the provider orders newest first).</summary>
    internal void Publish(string providerQuery, params long[] ids)
    {
        lock (_gate)
        {
            if (!_queries.TryGetValue(providerQuery, out List<long>? list)) _queries[providerQuery] = list = new();
            foreach (long id in ids) if (!list.Contains(id)) list.Add(id);
            list.Sort((left, right) => right.CompareTo(left));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int concurrent = Interlocked.Increment(ref _inFlight);
        try
        {
            if (Offline)
            {
                Interlocked.Increment(ref _offlineAttempts);
                throw new HttpRequestException("synthetic: no network");
            }
            Uri url = request.RequestUri!;
            Dictionary<string, string> query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty, StringComparer.Ordinal);
            B9WireRequest wire = new(request.Method.Method, url.AbsolutePath,
                query.GetValueOrDefault("tags"),
                query.TryGetValue("page", out string? page) ? int.Parse(page) : null,
                query.TryGetValue("limit", out string? limit) ? int.Parse(limit) : null,
                clock.GetUtcNow(), request.Headers.Authorization is not null, request.Headers.Contains("Cookie"),
                request.Headers.UserAgent.ToString());
            int index;
            lock (_gate)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
                index = _requests.Count;
                _requests.Add(wire);
            }
            if (Respond?.Invoke(wire, index) is { } scripted)
            {
                scripted.RequestMessage = request;
                return scripted;
            }
            await Task.Yield();
            return Json(request, Render(wire));
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private (HttpStatusCode Status, string Body) Render(B9WireRequest wire)
    {
        lock (_gate)
        {
            if (wire.Path == "/posts.json" && wire.Tags is { } tags && wire.Page is int page && wire.Limit is int limit)
            {
                long[] slice = (_queries.TryGetValue(tags, out List<long>? list) ? list : new())
                    .Skip((page - 1) * limit).Take(limit).ToArray();
                JsonArray array = new();
                foreach (long id in slice) array.Add(_posts[id].ToJson());
                return (HttpStatusCode.OK, array.ToJsonString());
            }
            if (wire.Path.StartsWith("/posts/", StringComparison.Ordinal) && wire.Path.EndsWith(".json", StringComparison.Ordinal)
                && long.TryParse(wire.Path["/posts/".Length..^".json".Length], out long postId))
                return _posts.TryGetValue(postId, out B9Post? post)
                    ? (HttpStatusCode.OK, post.ToJson().ToJsonString())
                    : (HttpStatusCode.NotFound, "{\"success\":false}");
            return (HttpStatusCode.NotFound, "{}");
        }
    }

    private static HttpResponseMessage Json(HttpRequestMessage request, (HttpStatusCode Status, string Body) reply) =>
        new(reply.Status)
        {
            Content = new StringContent(reply.Body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

    internal static HttpResponseMessage Status(HttpStatusCode status, string body = "synthetic", string mediaType = "text/plain",
        int? retryAfterSeconds = null)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        if (retryAfterSeconds is { } seconds) response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString());
        if ((int)status is >= 300 and < 400) response.Headers.Location = new Uri("https://elsewhere.invalid/");
        return response;
    }
}

/// <summary>In-memory CDN for originals, samples and previews. Never opens a socket.</summary>
internal sealed class B9Cdn : HttpMessageHandler
{
    internal ConcurrentDictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, bool> Corrupt { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<string> Requests { get; } = new();
    internal ConcurrentQueue<string> Violations { get; } = new();
    internal bool Offline { get; set; }
    /// <summary>When set, each response waits for one release of this gate (pause/cancel tests).</summary>
    internal SemaphoreSlim? Gate { get; set; }

    internal void Add(B9Post post)
    {
        Responses[post.OriginalUrl] = post.Bytes;
        Responses[post.SampleUrl] = [1, 2, 3];
        Responses[post.PreviewUrl] = [4, 5];
    }

    internal int CountFor(string url) => Requests.Count(item => item == url);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(url);
        if (request.Method != HttpMethod.Get) Violations.Enqueue("method " + request.Method + " " + url);
        if (request.Headers.Authorization is not null) Violations.Enqueue("authorization " + url);
        if (request.Headers.Contains("Cookie")) Violations.Enqueue("cookie " + url);
        if (Offline) throw new HttpRequestException("synthetic: no network");
        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
            gate.Release();
        }
        if (!Responses.TryGetValue(url, out byte[]? body)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (Corrupt.ContainsKey(url))
        {
            body = (byte[])body.Clone();
            body[body.Length / 2] ^= 0xFF;
        }
        ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>
/// Manual clock. The pump may jump to the next pending delay (pacing, retry). Per-attempt response
/// deadlines (timers whose state is a <see cref="CancellationTokenSource"/>) are never jumped to; a test
/// that wants a timeout advances explicitly.
/// </summary>
internal sealed class B9Clock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Entry> _timers = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Entry entry = new(this, callback, state);
        lock (_gate) { entry.Schedule(dueTime); _timers.Add(entry); }
        if (dueTime == TimeSpan.Zero) ThreadPool.QueueUserWorkItem(_ => entry.Fire());
        return entry;
    }

    internal void Advance(TimeSpan amount)
    {
        lock (_gate) _now += amount;
        FireDue();
    }

    /// <summary>Jumps to the earliest pending non-deadline timer, at most <paramref name="limit"/> ahead.</summary>
    internal bool AdvanceToNextDelay(TimeSpan limit)
    {
        lock (_gate)
        {
            Entry? next = _timers.Where(timer => !timer.Disposed && timer.Due is not null && timer.State is not CancellationTokenSource)
                .OrderBy(timer => timer.Due).FirstOrDefault();
            if (next?.Due is not { } due || due - _now > limit) return false;
            if (due > _now) _now = due;
        }
        FireDue();
        return true;
    }

    private void FireDue()
    {
        Entry[] due;
        lock (_gate)
        {
            due = _timers.Where(timer => !timer.Disposed && timer.Due is { } at && at <= _now).ToArray();
            _timers.RemoveAll(timer => timer.Disposed);
        }
        foreach (Entry timer in due) timer.Fire();
    }

    private sealed class Entry(B9Clock owner, TimerCallback callback, object? state) : ITimer
    {
        internal object? State => state;
        internal bool Disposed { get; private set; }
        internal DateTimeOffset? Due { get; private set; }
        internal void Schedule(TimeSpan dueTime) =>
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
        internal void Fire()
        {
            lock (owner._gate)
            {
                if (Disposed || Due is not { } at || at > owner._now) return;
                Due = null;
            }
            callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._gate) { if (Disposed) return false; Schedule(dueTime); return true; } }
        public void Dispose() { lock (owner._gate) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>
/// The shipped Danbooru plugin called in-process: its assembly is loaded from the current build output
/// of <c>plugins/Official/Danbooru</c> (same configuration as this test assembly) and every metadata
/// request goes through <see cref="BooruHostServices"/> with the live job's operation context, exactly
/// like the worker's host-HTTP bridge.
/// </summary>
internal sealed class InProcessDanbooruWorker(IBooruSourcePlugin plugin, IPluginHostServices host) : IPluginWorkerClient
{
    private static readonly Lazy<IBooruSourcePlugin> Shared = new(LoadPlugin, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly SettingsSnapshot Empty = new("1", []);
    private static readonly PluginConfiguration Configuration = new("b9", Empty,
        new Dictionary<string, SettingsSnapshot> { ["danbooru"] = Empty });

    internal static IBooruSourcePlugin Plugin => Shared.Value;

    public PluginDescription Description => plugin.Describe();
    public PluginWorkerIdentity Identity { get; } = new("b9-in-process", "1", "1");
    public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);
    internal int SearchCalls;
    internal int PostCalls;

    public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref SearchCalls);
        return plugin.SearchPostsAsync(request, Context(operation), cancellationToken);
    }

    public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref PostCalls);
        return plugin.ReadPostAsync(request, Context(operation), cancellationToken);
    }

    private PluginCallContext Context(PluginOperationContext operation) =>
        new(Configuration, new Bridge(host, operation), NoServices.Instance, NoServices.Instance, NoServices.Instance);

    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IBooruSourcePlugin LoadPlugin()
    {
        string root = B9World.RepositoryRoot();
        DirectoryInfo output = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string configuration = output.Parent?.Name ?? throw new InvalidOperationException("Unexpected test output layout.");
        string source = Path.Combine(root, "plugins", "Official", "Danbooru", "Danbooru.cs");
        string assembly = Path.Combine(root, "plugins", "Official", "Danbooru", "bin", configuration, output.Name, "MeowChan.Plugin.Danbooru.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException(
                $"B9 gates need the current Danbooru plugin build: dotnet build plugins/Official/Danbooru -c {configuration}", assembly);
        if (File.GetLastWriteTimeUtc(assembly) < File.GetLastWriteTimeUtc(source))
            throw new InvalidOperationException($"The Danbooru plugin build '{assembly}' is older than its source; rebuild it with -c {configuration}.");
        Type type = Assembly.LoadFrom(assembly).GetType("MeowChan.OfficialSources.Danbooru.DanbooruPlugin", throwOnError: true)!;
        return (IBooruSourcePlugin)Activator.CreateInstance(type)!;
    }

    private sealed class Bridge(IPluginHostServices host, PluginOperationContext operation) : IHostHttp
    {
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default) =>
            host.SendHttpAsync(request, operation, cancellationToken);
    }

    private sealed class NoServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        internal static readonly NoServices Instance = new();
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A booru source never reads image bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No plugin resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("A booru source must not log provider content.");
    }
}

/// <summary>Shared, restart-surviving state of one acceptance run: clock, provider, CDN and the temp root.</summary>
internal sealed class B9World : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    internal B9World()
    {
        Root = Path.Combine(Path.GetTempPath(), "meowchan-b9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Provider = new B9Provider(Clock);
        Plugin = InProcessDanbooruWorker.Plugin;
        Sources = Plugin.Describe().Sources;
    }

    internal string Root { get; }
    internal string ProfileDirectory => Path.Combine(Root, "profile");
    internal string OutputRoot => Path.Combine(Root, "media");
    internal B9Clock Clock { get; } = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    internal B9Provider Provider { get; }
    internal B9Cdn Cdn { get; } = new();
    internal IBooruSourcePlugin Plugin { get; }
    internal IReadOnlyList<SourceDescription> Sources { get; }

    /// <summary>Registers an invented post with the provider and the CDN and returns it.</summary>
    internal B9Post Post(long id, string rating = "g", bool restricted = false, bool deleted = false, bool banned = false,
        string general = "cat_ears solo")
    {
        B9Post post = new(id, B9Post.SyntheticBytes(id), rating, general, Restricted: restricted, Deleted: deleted, Banned: banned);
        Provider.Add(post);
        Cdn.Add(post);
        return post;
    }

    /// <summary>Every metadata request: GET, no credentials, the product user agent, only the declared endpoints.</summary>
    internal void AssertCleanWire()
    {
        Assert.All(Provider.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.False(request.HasAuthorization);
            Assert.False(request.HasCookie);
            Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)", request.UserAgent);
            Assert.True(request.Path == "/posts.json" || request.Path.StartsWith("/posts/", StringComparison.Ordinal), request.Path);
            if (request.Path == "/posts.json") Assert.Equal(BooruSearchService.SearchLimit, request.Limit);
        });
        Assert.Empty(Cdn.Violations);
        Assert.True(Provider.MaxConcurrent <= 1, $"{Provider.MaxConcurrent} metadata requests ran concurrently.");
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// One running profile ("app session") over a <see cref="B9World"/>. Disposing it is an orderly app
/// shutdown in the documented order (watchers, queue, transport, lease); a new instance on the same world
/// is a restart. Host start order is mirrored by <see cref="StartAsync"/>; <see cref="StartHostAsync"/>
/// uses the real <see cref="DanbooruWatcherHost"/> including its background loop.
/// </summary>
internal sealed class B9Session : IAsyncDisposable
{
    private readonly B9World _world;
    private readonly ProfileLease _lease;
    private readonly HttpClient _metadataClient;
    private readonly HttpClient _mediaClient;
    private bool _disposed;

    internal B9Session(B9World world, bool queuePaused = false)
    {
        _world = world;
        Directory.CreateDirectory(world.ProfileDirectory);
        _lease = ProfileLease.Acquire(world.ProfileDirectory);
        Work = new WorkStateStore(_lease.ProfileDirectory, _lease);
        Queue = new DownloadQueue(1, 1L << 30);
        Queue.ConfigureWorkState(Work);
        if (queuePaused) Queue.SetPausedAsync(true).GetAwaiter().GetResult();
        Catalog = new BooruCatalogStore(_lease.ProfileDirectory, _lease);
        // As in Registry startup: the additive catalog is read while startup still owns the profile.
        Catalog.Read();
        _lease.Admission.CompleteStartup();
        _mediaClient = new HttpClient(world.Cdn, disposeHandler: false);
        Originals = new BooruOriginalQueue(Queue, Catalog, world.OutputRoot, _mediaClient, timeProvider: world.Clock);
        _metadataClient = new HttpClient(world.Provider, disposeHandler: false);
        Http = new BooruHostHttp(_metadataClient, new RequestPolicyState(world.Clock), world.Sources, productVersion: "1.1.0");
        Services = new BooruHostServices(new NoInner(), Http);
        Search = new BooruSearchService(Services);
        Worker = new InProcessDanbooruWorker(world.Plugin, Services);
        Port = new BooruWatcherSearchPort(Search, Services, _ => Task.FromResult<IPluginWorkerClient?>(WorkerAvailable ? Worker : null), world.Clock);
        Host = new DanbooruWatcherHost(Catalog, Originals, Port, world.Clock,
            new DanbooruWatcherSchedulerOptions { Interval = B9World.Interval, CheckStartSpacing = TimeSpan.Zero });
    }

    internal string ProfileDirectory => _lease.ProfileDirectory;
    internal WorkStateStore Work { get; }
    internal DownloadQueue Queue { get; }
    internal BooruCatalogStore Catalog { get; }
    internal BooruOriginalQueue Originals { get; }
    internal BooruHostHttp Http { get; }
    internal BooruHostServices Services { get; }
    internal BooruSearchService Search { get; }
    internal InProcessDanbooruWorker Worker { get; }
    internal BooruWatcherSearchPort Port { get; }
    internal DanbooruWatcherHost Host { get; }
    internal DanbooruWatcherScheduler Scheduler => Host.Scheduler;
    internal bool WorkerAvailable { get; set; } = true;

    /// <summary>
    /// The host start order without the background loop (deterministic steps): load and recover every
    /// stored watcher, then re-admit pending originals from local data only.
    /// </summary>
    internal async Task<IReadOnlyList<BooruOriginalAdmission>> StartAsync()
    {
        Scheduler.Load();
        return await Originals.RecoverPendingAsync().WaitAsync(B9Wait.Timeout);
    }

    /// <summary>The real <see cref="DanbooruWatcherHost.StartAsync"/> (with loop).</summary>
    internal Task StartHostAsync() => Host.StartAsync().WaitAsync(B9Wait.Timeout);

    /// <summary>User search as the UI issues it (host-validated query, page 1).</summary>
    internal Task<BooruSearchResult> SearchAsync(string query, int page = 1) =>
        PumpAsync(Search.SearchAsync(Worker, "danbooru", query, page));

    internal Task<BooruPostResult> ReadPostAsync(string postId) =>
        PumpAsync(Search.ReadPostAsync(Worker, new SourcePostId("danbooru", postId)));

    internal Task<DanbooruWatcherCheckSummary?> RunNextAsync() => PumpAsync(Scheduler.RunNextAsync());

    internal Task<DanbooruWatcherCheckSummary> CheckNowAsync(string watcherId) => PumpAsync(Scheduler.CheckNowAsync(watcherId));

    internal string AddWatcher(string query)
    {
        DanbooruWatcherCommandResult result = Scheduler.AddWatcher(query);
        Assert.True(result.Accepted, result.Code);
        return result.Watcher!.WatcherId;
    }

    /// <summary>Selects one previewed post explicitly and admits its original (search, one-shot).</summary>
    internal Task<BooruOriginalAdmission> AdmitAsync(BooruPost post, BooruFindingKind kind, string? query = null) =>
        Originals.AdmitAsync(BooruClaimMapper.Map(post), kind, query: query).WaitAsync(B9Wait.Timeout);

    internal BooruPostRecord Record(string postId) =>
        Catalog.Read().Posts.Single(item => item.SourceId == "danbooru" && item.PostId == postId);

    /// <summary>
    /// Completes a host-side task while jumping the manual clock over host waits (provider spacing, the
    /// single 502/503/network retry). It never jumps further than <paramref name="limit"/> at once and
    /// never over a response deadline.
    /// </summary>
    internal async Task<T> PumpAsync<T>(Task<T> task, TimeSpan? limit = null)
    {
        TimeSpan maximum = limit ?? TimeSpan.FromSeconds(5);
        DateTime deadline = DateTime.UtcNow + B9Wait.Timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The host step did not finish.");
            await Task.Delay(5);
            if (!task.IsCompleted && _world.Provider.InFlight == 0) _world.Clock.AdvanceToNextDelay(maximum);
        }
        return await task;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await Host.StopAsync().WaitAsync(B9Wait.Timeout);
            await Queue.StopAllAsync().WaitAsync(B9Wait.Timeout);
        }
        finally
        {
            Http.Dispose();
            _metadataClient.Dispose();
            _mediaClient.Dispose();
            _lease.Dispose();
        }
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A booru source never reads image bytes.");
    }
}

internal static class B9Wait
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static async Task UntilAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(10);
        }
    }

    /// <summary>Reads a committed asset offline through the media manifest and verifies length and SHA-256.</summary>
    internal static async Task<byte[]> ReadVerifiedAssetAsync(string outputRoot, string postId, string assetId)
    {
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(
            Path.Combine(outputRoot, BooruOriginalQueue.ProviderDirectoryName, postId));
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.NotNull(snapshot.FindById(assetId));
        await using Stream stream = await snapshot.OpenVerifiedReadAsync(assetId);
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
