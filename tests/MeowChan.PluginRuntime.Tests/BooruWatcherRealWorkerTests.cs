using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// B5 end-to-end with the real Danbooru plugin in the real worker process against a loopback stub
/// (same preconditions as <see cref="BooruRealWorkerTests"/>: Release builds of plugin and worker).
/// Real time, interval 1 s; no request leaves 127.0.0.1.
/// </summary>
public sealed class BooruWatcherRealWorkerTests
{
    [Fact]
    public async Task RealPluginWatcherBaselineThenNewPostIsPacedAndAdmittedOnce()
    {
        string root = FindRoot();
        string pluginPath = Path.Combine(root, "plugins/Official/Danbooru/bin/Release/net10.0/MeowChan.Plugin.Danbooru.dll");
        string workerPath = Path.Combine(root, "MeowChan.PluginWorker/bin/Release/net10.0/MeowChan.PluginWorker.dll");
        Assert.True(File.Exists(pluginPath), "Build the Danbooru plugin in Release before this test.");
        Assert.True(File.Exists(workerPath), "Build the plugin worker in Release before this test.");

        // The complete post with a separate sample (id 1001), whatever its position in the fixture.
        JsonObject template = (JsonObject)JsonNode.Parse(File.ReadAllText(
            Path.Combine(root, "tests/MeowChan.OfficialPlugins.Tests/Fixtures/danbooru/search-normal.json")))!.AsArray()
            .Single(post => (long)post!["id"]! == 1001)!;
        List<long> ids = [100, 99];
        object idsGate = new();
        await using Loopback server = new(_ =>
        {
            JsonArray page = new();
            lock (idsGate)
                foreach (long id in ids.OrderByDescending(value => value))
                {
                    JsonObject post = (JsonObject)template.DeepClone();
                    post["id"] = id;
                    page.Add(post);
                }
            return page.ToJsonString();
        });
        using Rewriting rewriting = new(server.Port);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        SourceDescription source = new("danbooru", "Danbooru", new("https://danbooru.donmai.us/"),
            SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
            [new("danbooru.donmai.us", HttpHostPurpose.Metadata), new("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)],
            new("1", []));
        using BooruHostHttp booruHttp = new(transport, new RequestPolicyState(), [source], productVersion: "1.1.0");
        BooruHostServices services = new(new NoInner(), booruHttp);
        string workspace = Path.Combine(Path.GetTempPath(), "meowchan-b5-real-" + Guid.NewGuid().ToString("N"));
        string profile = Path.Combine(workspace, "profile");
        Directory.CreateDirectory(profile);
        try
        {
            await using PluginWorkerManager manager = new();
            PluginConfiguration configuration = new("config1", new("1", []), new Dictionary<string, SettingsSnapshot> { ["danbooru"] = new("1", []) });
            await using IPluginWorkerClient worker = await manager.StartAsync(
                new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet", [workerPath], workspace),
                new PluginArtifact("compile1", "build1", pluginPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath))),
                    SdkContract.Version, "compiler1", [], null),
                configuration, services);
            using ProfileLease lease = ProfileLease.Acquire(profile);
            BooruCatalogStore catalog = new(lease.ProfileDirectory, lease);
            catalog.Read();
            lease.Admission.CompleteStartup();
            BooruWatcherSchedulerHostTests.RecordingAdmission admission = new();
            BooruWatcherSearchPort port = new(new BooruSearchService(services), services,
                _ => Task.FromResult<IPluginWorkerClient?>(worker));
            await using DanbooruWatcherScheduler scheduler = new(catalog, port, admission, TimeProvider.System,
                new DanbooruWatcherSchedulerOptions { Interval = TimeSpan.FromSeconds(1), CheckStartSpacing = TimeSpan.Zero });
            scheduler.Load();
            string watcher = scheduler.AddWatcher("cat_ears").Watcher!.WatcherId;

            Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);
            lock (idsGate) ids.Add(101);
            await Task.Delay(1100);
            DanbooruWatcherCheckSummary check = (await scheduler.RunNextAsync())!;
            Assert.Equal(DanbooruWatcherCheckKind.Completed, check.Kind);
            Assert.Equal(1, check.Queued);
            Assert.Equal(1, admission.PerPost["101"]);
            Assert.Single(admission.PerPost);
            Assert.Equal("101", catalog.Read().Watchers.Single(w => w.WatcherId == watcher).HighWaterMark);

            Assert.Equal(2, server.Requests.Count);
            Assert.All(server.Requests, request =>
            {
                Assert.Equal("/posts.json?tags=cat_ears&page=1&limit=20", request.Target);
                Assert.Equal("MeowChan/1.1.0 (+https://github.com/grassmann-it/MeowChan)", request.Headers["user-agent"]);
                Assert.False(request.Headers.ContainsKey("authorization"));
                Assert.False(request.Headers.ContainsKey("cookie"));
            });
            Assert.True(server.Requests[1].At - server.Requests[0].At >= TimeSpan.FromMilliseconds(950));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }

    private sealed class Rewriting(int port) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;
            using HttpRequestMessage forwarded = new(request.Method, new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port)
            { Path = original.AbsolutePath, Query = original.Query }.Uri);
            foreach ((string name, IEnumerable<string> values) in request.Headers) forwarded.Headers.TryAddWithoutValidation(name, values);
            HttpResponseMessage response = await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }

    private sealed record Request(string Target, Dictionary<string, string> Headers, TimeSpan At);

    /// <summary>Minimal HTTP/1.1 loopback server answering every request with a JSON body.</summary>
    private sealed class Loopback : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<string, string> _body;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly List<Request> _requests = new();
        private readonly Task _loop;

        internal Loopback(Func<string, string> body)
        {
            _body = body;
            _listener.Start();
            _loop = RunAsync();
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal IReadOnlyList<Request> Requests { get { lock (_gate) return _requests.ToArray(); } }

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
                string? line = await reader.ReadLineAsync(_lifetime.Token);
                if (line is null) return;
                TimeSpan at = _clock.Elapsed;
                string target = line.Split(' ', 3) is { Length: >= 2 } parts ? parts[1] : string.Empty;
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                while ((line = await reader.ReadLineAsync(_lifetime.Token)) is { Length: > 0 })
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                lock (_gate) _requests.Add(new Request(target, headers, at));
                byte[] body = Encoding.UTF8.GetBytes(_body(target));
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, _lifetime.Token);
                await stream.WriteAsync(body, _lifetime.Token);
                await stream.FlushAsync(_lifetime.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try { await _loop; } catch (OperationCanceledException) { }
            _lifetime.Dispose();
        }
    }
}
