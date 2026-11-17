using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Desktop.Booru;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using SkiaSharp;

namespace MeowChan.Tests.Shared;

// Shared by DesktopTests and the DesktopSmoke: a terminal, in-memory Danbooru (metadata + CDN) and the real
// Danbooru plugin called in-process behind the real BooruHostServices/BooruHostHttp. Nothing here opens a socket;
// an unknown host or any non-GET/credentialed request is recorded as a violation and refused.

/// <summary>One invented Danbooru post with a real PNG original and a real PNG preview.</summary>
internal sealed record DanbooruStubPost(long Id, string Rating = "g", string General = "cat_ears solo", bool Restricted = false,
    uint Color = 0xFF4A90D9)
{
    internal string PostId => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal byte[] Original { get; } = DanbooruStub.Png(64, 48, Color, Id);
    internal byte[] Preview { get; } = DanbooruStub.Png(150, 150, Color, Id + 1);
    /// <summary>The sample the CDN serves at <see cref="SampleUrl"/>: larger than the preview and distinct from it (synthetic, not sized like the original).</summary>
    internal byte[] Sample { get; } = DanbooruStub.Png(240, 180, Color, Id + 2);
    internal string Md5Hex => Convert.ToHexString(MD5.HashData(Original)).ToLowerInvariant();
    internal string OriginalUrl => $"https://cdn.donmai.us/original/{Md5Hex[..2]}/{Md5Hex[2..4]}/{Md5Hex}.png";
    internal string SampleUrl => $"https://cdn.donmai.us/sample/{Md5Hex[..2]}/{Md5Hex[2..4]}/sample-{Md5Hex}.jpg";
    internal string PreviewUrl => $"https://cdn.donmai.us/180x180/{Md5Hex[..2]}/{Md5Hex[2..4]}/{Md5Hex}.jpg";

    internal JsonObject ToJson()
    {
        JsonObject post = new()
        {
            ["id"] = Id, ["created_at"] = "2026-11-01T10:00:00.000Z", ["score"] = 5, ["fav_count"] = 1,
            ["source"] = $"https://example.invalid/art/{Id}", ["rating"] = Rating, ["image_width"] = 64, ["image_height"] = 48,
            ["file_ext"] = "png", ["is_deleted"] = false, ["is_banned"] = false,
            ["tag_string_general"] = General, ["tag_string_character"] = "", ["tag_string_copyright"] = "original",
            ["tag_string_artist"] = "artist_stub", ["tag_string_meta"] = "", ["file_size"] = Original.Length,
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

internal sealed class DanbooruStub : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<long, DanbooruStubPost> _posts = new();
    private readonly Dictionary<string, List<long>> _queries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _cdn = new(StringComparer.Ordinal);
    private TaskCompletionSource? _originalHold;
    private TaskCompletionSource? _metadataHold;
    private TaskCompletionSource _metadataEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ConcurrentQueue<string> Requests { get; } = new();
    internal ConcurrentQueue<string> Violations { get; } = new();
    internal bool Offline { get; set; }

    /// <summary>
    /// Scripted CDN cases (429/503 with Retry-After, oversized, wrong type, redirect, held answers) for single addresses:
    /// an address with a rule is answered by the script, every other CDN address behaves as before.
    /// </summary>
    internal BooruCdnScript Cdn { get; } = new();

    internal int MetadataRequests => Requests.Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal));
    internal int OriginalRequests => Requests.Count(url => url.StartsWith("https://cdn.donmai.us/original/", StringComparison.Ordinal));
    internal int PreviewRequests => Requests.Count(url => url.StartsWith("https://cdn.donmai.us/180x180/", StringComparison.Ordinal));
    internal int SampleRequests => Requests.Count(url => url.StartsWith("https://cdn.donmai.us/sample/", StringComparison.Ordinal));

    internal HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    internal DanbooruStubPost Add(DanbooruStubPost post)
    {
        lock (_gate)
        {
            _posts[post.Id] = post;
            _cdn[post.OriginalUrl] = post.Original;
            _cdn[post.PreviewUrl] = post.Preview;
            _cdn[post.SampleUrl] = post.Sample;
        }
        return post;
    }

    /// <summary>Makes posts visible for an exact provider tag string; the provider orders newest first.</summary>
    internal void Publish(string tags, params long[] ids)
    {
        lock (_gate)
        {
            if (!_queries.TryGetValue(tags, out List<long>? list)) _queries[tags] = list = new();
            foreach (long id in ids) if (!list.Contains(id)) list.Add(id);
            list.Sort((left, right) => right.CompareTo(left));
        }
    }

    /// <summary>Original transfers wait until <see cref="ReleaseOriginals"/> (keeps a job visibly queued).</summary>
    internal void HoldOriginals() { lock (_gate) _originalHold ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }

    internal void ReleaseOriginals()
    {
        TaskCompletionSource? hold;
        lock (_gate) { hold = _originalHold; _originalHold = null; }
        hold?.TrySetResult();
    }

    /// <summary>Metadata requests wait (honouring cancellation) until <see cref="ReleaseMetadata"/>.</summary>
    internal Task HoldMetadata()
    {
        lock (_gate)
        {
            _metadataHold ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_metadataEntered.Task.IsCompleted) _metadataEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _metadataEntered.Task;
        }
    }

    internal void ReleaseMetadata()
    {
        TaskCompletionSource? hold;
        lock (_gate) { hold = _metadataHold; _metadataHold = null; }
        hold?.TrySetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri url = request.RequestUri!;
        Requests.Enqueue(url.AbsoluteUri);
        if (request.Method != HttpMethod.Get) Violations.Enqueue("method " + request.Method + " " + url);
        if (request.Headers.Authorization is not null) Violations.Enqueue("authorization " + url);
        if (request.Headers.Contains("Cookie")) Violations.Enqueue("cookie " + url);
        if (url.Host is not ("danbooru.donmai.us" or "cdn.donmai.us"))
        {
            Violations.Enqueue("host " + url);
            throw new HttpRequestException("Danbooru stub refused an unknown host: " + url.Host);
        }
        if (Offline) throw new HttpRequestException("synthetic: no network");
        await Task.Yield();
        if (url.Host == "cdn.donmai.us")
        {
            if (Cdn.TryRespondAsync(request, cancellationToken) is { } scripted) return await scripted.ConfigureAwait(false);
            TaskCompletionSource? hold;
            byte[]? body;
            lock (_gate)
            {
                hold = url.AbsolutePath.StartsWith("/original/", StringComparison.Ordinal) ? _originalHold : null;
                _cdn.TryGetValue(url.AbsoluteUri, out body);
            }
            if (hold != null) await hold.Task.WaitAsync(cancellationToken);
            if (body == null) return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            ByteArrayContent content = new(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
        TaskCompletionSource? metadataHold;
        lock (_gate)
        {
            metadataHold = _metadataHold;
            if (metadataHold != null) _metadataEntered.TrySetResult();
        }
        if (metadataHold != null) await metadataHold.Task.WaitAsync(cancellationToken);
        (HttpStatusCode status, string json) = Render(url);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"), RequestMessage = request
        };
    }

    private (HttpStatusCode, string) Render(Uri url)
    {
        Dictionary<string, string> query = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : string.Empty, StringComparer.Ordinal);
        lock (_gate)
        {
            if (url.AbsolutePath == "/posts.json" && query.TryGetValue("tags", out string? tags)
                && query.TryGetValue("page", out string? pageText) && query.TryGetValue("limit", out string? limitText))
            {
                List<long> ids = _queries.TryGetValue(tags, out List<long>? list) ? list : new();
                IEnumerable<long> selected;
                if (pageText.StartsWith('b') && long.TryParse(pageText[1..], out long before))
                    selected = ids.Where(id => id < before);
                else
                    selected = ids.Skip((int.Parse(pageText) - 1) * int.Parse(limitText));
                JsonArray array = new();
                foreach (long id in selected.Take(int.Parse(limitText))) array.Add(_posts[id].ToJson());
                return (HttpStatusCode.OK, array.ToJsonString());
            }
            if (url.AbsolutePath.StartsWith("/posts/", StringComparison.Ordinal) && url.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)
                && long.TryParse(url.AbsolutePath["/posts/".Length..^".json".Length], out long postId))
                return _posts.TryGetValue(postId, out DanbooruStubPost? post)
                    ? (HttpStatusCode.OK, post.ToJson().ToJsonString()) : (HttpStatusCode.NotFound, "{\"success\":false}");
        }
        return (HttpStatusCode.NotFound, "{}");
    }

    /// <summary>A real, decodable PNG: a colored square with a lighter diagonal band.</summary>
    internal static byte[] Png(int width, int height, uint color, long seed)
    {
        using SKBitmap bitmap = new(width, height);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(new SKColor(color));
            using SKPaint band = new() { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x60), StrokeWidth = Math.Max(4, width / 8f) };
            float offset = seed % 7 * width / 14f;
            canvas.DrawLine(offset, 0, width, height - offset, band);
        }
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>The shipped Danbooru plugin, loaded from its current build output and called in-process.</summary>
internal static class DanbooruTestPlugin
{
    private static readonly Lazy<IBooruSourcePlugin> Shared = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IBooruSourcePlugin Plugin => Shared.Value;

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    internal static string SourcePath => Path.Combine(RepositoryRoot(), "plugins", "Official", "Danbooru", "Danbooru.cs");

    private static IBooruSourcePlugin Load()
    {
        DirectoryInfo output = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string configuration = output.Parent?.Name ?? throw new InvalidOperationException("Unexpected test output layout.");
        string assembly = Path.Combine(RepositoryRoot(), "plugins", "Official", "Danbooru", "bin", configuration, output.Name,
            "MeowChan.Plugin.Danbooru.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException($"Build the Danbooru plugin first: dotnet build plugins/Official/Danbooru -c {configuration}", assembly);
        if (File.GetLastWriteTimeUtc(assembly) < File.GetLastWriteTimeUtc(SourcePath))
            throw new InvalidOperationException($"The Danbooru plugin build '{assembly}' is older than its source; rebuild it.");
        Type type = Assembly.LoadFrom(assembly).GetType("MeowChan.OfficialSources.Danbooru.DanbooruPlugin", throwOnError: true)!;
        return (IBooruSourcePlugin)Activator.CreateInstance(type)!;
    }
}

internal sealed class InProcessDanbooruWorker(IBooruSourcePlugin plugin, IPluginHostServices host) : IPluginWorkerClient
{
    private static readonly SettingsSnapshot Empty = new("1", []);
    private static readonly PluginConfiguration Configuration = new("stub", Empty,
        new Dictionary<string, SettingsSnapshot> { ["danbooru"] = Empty });
    private int _searches;
    private int _posts;

    internal int SearchCalls => Volatile.Read(ref _searches);
    internal int PostCalls => Volatile.Read(ref _posts);

    public PluginDescription Description => plugin.Describe();
    public PluginWorkerIdentity Identity { get; } = new("in-process", "1", "1");
    public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);

    public Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _searches);
        return plugin.SearchPostsAsync(request, Context(operation), cancellationToken);
    }

    public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _posts);
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
        public void Write(PluginLogLevel level, string message) { }
    }
}

/// <summary>
/// A production <see cref="BooruProviderBinding"/> over the stub: real booru host services and transport bounds, the
/// in-process plugin as the worker, and an enable switch that behaves like the plugin's lifecycle toggle.
/// </summary>
internal sealed class DanbooruTestBinding : IDisposable
{
    private volatile bool _enabled;

    internal DanbooruTestBinding(DanbooruStub stub, bool enabled = true, TimeProvider? clock = null)
    {
        _enabled = enabled;
        Transport = stub.Client();
        Policy = new RequestPolicyState(clock);
        IBooruSourcePlugin plugin = DanbooruTestPlugin.Plugin;
        IReadOnlyList<SourceDescription> sources = plugin.Describe().Sources;
        Source = sources.Single(source => source.SourceId == "danbooru");
        Http = new BooruHostHttp(Transport, Policy, sources, productVersion: "1.1.0");
        Services = new BooruHostServices(new NoInner(), Http);
        Worker = new InProcessDanbooruWorker(plugin, Services);
        Binding = new BooruProviderBinding(Source, Services, Transport, Policy,
            _ => Task.FromResult<IPluginWorkerClient?>(_enabled ? Worker : null), () => _enabled);
    }

    internal HttpClient Transport { get; }
    internal RequestPolicyState Policy { get; }
    internal SourceDescription Source { get; }
    internal BooruHostHttp Http { get; }
    internal BooruHostServices Services { get; }
    internal InProcessDanbooruWorker Worker { get; }
    internal BooruProviderBinding Binding { get; }
    internal bool Enabled => _enabled;

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Binding.RaiseAvailabilityChanged();
    }

    public void Dispose()
    {
        Http.Dispose();
        Transport.Dispose();
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A booru source never reads image bytes.");
    }
}
