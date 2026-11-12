using MeowChan.TestSupport;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruRealWorkerTests
{
    [Fact]
    public async Task DanbooruPluginSearchUsesLoopbackWireAndInvalidQueryDoesNotStartWorkerCall()
    {
        string root = FindRoot();
        string pluginPath = BuiltTestBinaries.LibraryOf(Path.Combine("plugins", "Official", "Danbooru"), "MeowChan.Plugin.Danbooru");
        string workerPath = BuiltTestBinaries.WorkerAssembly();
        Assert.True(File.Exists(pluginPath), "Build the Danbooru plugin in the test configuration.");
        Assert.True(File.Exists(workerPath), "Build the plugin worker in the test configuration.");

        await using LoopbackServer server = await LoopbackServer.StartAsync((_, _) => LoopbackReply.Json(200, Fixture(root, "search-normal.json")));
        using RewritingHandler rewriting = new(server.Port);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        SourceDescription source = DanbooruSource();
        using BooruHostHttp booruHttp = new(transport, new RequestPolicyState(), [source], productVersion: "1.1.0");
        BooruHostServices services = new(new Inner(), booruHttp);
        BooruSearchService search = new(services);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        PluginConfiguration configuration = new("config1", new("1", []),
            new Dictionary<string, SettingsSnapshot> { ["danbooru"] = new("1", []) });
        PluginArtifact artifact = Artifact(pluginPath);
        PluginWorkerLaunch launch = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet", [workerPath], workspace.Path);
        await using IPluginWorkerClient worker = await manager.StartAsync(launch, artifact, configuration, services);

        BooruSearchFailure invalid = Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, "danbooru", "cat dog third", 1));
        Assert.Equal(BooruSearchService.InvalidQueryCode, invalid.Failure.Code);
        Assert.Equal(0, server.RequestCount);

        BooruSearchSnapshot result = Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, "danbooru", "rating:g cat_ears", 1));
        Assert.NotEmpty(result.Page.Posts);
        Assert.Equal("rating:g cat_ears", result.Page.Query);
        LoopbackRequest request = await server.WaitForRequestAsync(0);
        Assert.Equal("/posts.json?tags=rating%3Ag%20cat_ears&page=1&limit=20", request.Target);
        Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)", request.Headers["user-agent"]);
        Assert.False(request.Headers.ContainsKey("authorization"));
        Assert.False(request.Headers.ContainsKey("cookie"));
    }

    [Fact]
    public async Task RealWorkerReceivesProvider429AsDeferredAndSecondCallDoesNotHitStub()
    {
        string root = FindRoot();
        string pluginPath = BuiltTestBinaries.LibraryOf(Path.Combine("plugins", "Official", "Danbooru"), "MeowChan.Plugin.Danbooru");
        string workerPath = BuiltTestBinaries.WorkerAssembly();
        Assert.True(File.Exists(pluginPath)); Assert.True(File.Exists(workerPath));
        await using LoopbackServer server = await LoopbackServer.StartAsync((_, _) => LoopbackReply.Json(429, "busy", "Retry-After: 120\r\n"));
        using RewritingHandler rewriting = new(server.Port);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        SourceDescription source = DanbooruSource();
        using BooruHostHttp booruHttp = new(transport, new RequestPolicyState(), [source]);
        BooruHostServices services = new(new Inner(), booruHttp);
        BooruSearchService search = new(services);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        PluginConfiguration configuration = new("config1", new("1", []), new Dictionary<string, SettingsSnapshot> { ["danbooru"] = new("1", []) });
        await using IPluginWorkerClient worker = await manager.StartAsync(
            new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet", [workerPath], workspace.Path),
            Artifact(pluginPath), configuration, services);

        BooruSearchDeferred first = Assert.IsType<BooruSearchDeferred>(await search.SearchAsync(worker, "danbooru", "cat", 1));
        BooruSearchDeferred second = Assert.IsType<BooruSearchDeferred>(await search.SearchAsync(worker, "danbooru", "cat", 1));
        Assert.Equal(first.RetryAt, second.RetryAt);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task RealWorkerMapsSinglePost404ToNotFound()
    {
        string root = FindRoot();
        string pluginPath = BuiltTestBinaries.LibraryOf(Path.Combine("plugins", "Official", "Danbooru"), "MeowChan.Plugin.Danbooru");
        string workerPath = BuiltTestBinaries.WorkerAssembly();
        Assert.True(File.Exists(pluginPath)); Assert.True(File.Exists(workerPath));
        await using LoopbackServer server = await LoopbackServer.StartAsync((target, _) =>
            target.StartsWith("/posts/", StringComparison.Ordinal) ? LoopbackReply.Json(404, "missing") : LoopbackReply.Json(500, "unexpected"));
        using RewritingHandler rewriting = new(server.Port);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        SourceDescription source = DanbooruSource();
        using BooruHostHttp booruHttp = new(transport, new RequestPolicyState(), [source]);
        BooruHostServices services = new(new Inner(), booruHttp);
        BooruSearchService search = new(services);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        PluginConfiguration configuration = new("config1", new("1", []), new Dictionary<string, SettingsSnapshot> { ["danbooru"] = new("1", []) });
        await using IPluginWorkerClient worker = await manager.StartAsync(
            new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet", [workerPath], workspace.Path),
            Artifact(pluginPath), configuration, services);

        BooruPostNotFound result = Assert.IsType<BooruPostNotFound>(await search.ReadPostAsync(worker, new("danbooru", "12345")));
        Assert.Equal("12345", result.Post.PostId);
        Assert.Equal(404, result.HttpStatusCode);
        Assert.Equal("/posts/12345.json", (await server.WaitForRequestAsync(0)).Target);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private static string Fixture(string root, string name) =>
        File.ReadAllText(Path.Combine(root, "tests/MeowChan.OfficialPlugins.Tests/Fixtures/danbooru", name));

    private static PluginArtifact Artifact(string path) => new("compile1", "build1", path,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), SdkContract.Version, "compiler1", [], null);

    private static SourceDescription DanbooruSource() => new("danbooru", "Danbooru", new("https://danbooru.donmai.us/"),
        SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
        [new("danbooru.donmai.us", HttpHostPurpose.Metadata), new("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)], new("1", []));

    private sealed class Inner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("inner")));
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class RewritingHandler(int port) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;
            Uri rewritten = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port)
            { Path = original.AbsolutePath, Query = original.Query }.Uri;
            using HttpRequestMessage forwarded = new(request.Method, rewritten);
            foreach ((string name, IEnumerable<string> values) in request.Headers)
                forwarded.Headers.TryAddWithoutValidation(name, values);
            HttpResponseMessage response = await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-booru-real-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed record LoopbackRequest(string Target, Dictionary<string, string> Headers);
    private sealed record LoopbackReply(int Status, byte[] Body, string ExtraHeaders)
    {
        internal static LoopbackReply Json(int status, string body, string extraHeaders = "") => new(status, Encoding.UTF8.GetBytes(body), "Content-Type: application/json\r\n" + extraHeaders);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, int, LoopbackReply> _reply;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _gate = new();
        private readonly List<LoopbackRequest> _requests = [];
        private readonly List<TaskCompletionSource<LoopbackRequest>> _waiters = [];
        private readonly Task _loop;
        private LoopbackServer(TcpListener listener, Func<string, int, LoopbackReply> reply)
        { _listener = listener; _reply = reply; _loop = RunAsync(); }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int RequestCount { get { lock (_gate) return _requests.Count; } }
        internal static Task<LoopbackServer> StartAsync(Func<string, int, LoopbackReply> reply)
        {
            TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
            return Task.FromResult(new LoopbackServer(listener, reply));
        }
        internal async Task<LoopbackRequest> WaitForRequestAsync(int index)
        {
            TaskCompletionSource<LoopbackRequest>? waiter = null;
            lock (_gate)
            {
                if (_requests.Count > index) return _requests[index];
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(waiter);
            }
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        private async Task RunAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true))
            {
                string? requestLine = await reader.ReadLineAsync(_lifetime.Token);
                if (requestLine is null) return;
                string[] parts = requestLine.Split(' ', 3);
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                string? line;
                while ((line = await reader.ReadLineAsync(_lifetime.Token)) is not null && line.Length != 0)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                string target = parts.Length >= 2 ? parts[1] : string.Empty;
                LoopbackRequest record = new(target, headers);
                TaskCompletionSource<LoopbackRequest>? waiter = null;
                lock (_gate)
                {
                    _requests.Add(record);
                    if (_waiters.Count > 0) { waiter = _waiters[0]; _waiters.RemoveAt(0); }
                }
                waiter?.TrySetResult(record);
                LoopbackReply reply = _reply(target, RequestCount);
                string response = $"HTTP/1.1 {reply.Status} {Reason(reply.Status)}\r\n{reply.ExtraHeaders}Content-Length: {reply.Body.Length}\r\nConnection: close\r\n\r\n";
                byte[] prefix = Encoding.ASCII.GetBytes(response);
                await stream.WriteAsync(prefix, _lifetime.Token); await stream.WriteAsync(reply.Body, _lifetime.Token); await stream.FlushAsync(_lifetime.Token);
            }
        }
        private static string Reason(int status) => status switch { 200 => "OK", 404 => "Not Found", 429 => "Too Many Requests", 500 => "Internal Server Error", _ => "Synthetic" };
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); _listener.Stop();
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _lifetime.Dispose();
        }
    }
}
