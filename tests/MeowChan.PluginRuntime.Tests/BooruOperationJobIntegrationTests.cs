using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginRuntime.Fixture;
using MeowChan.PluginSdk;
using MeowChan.TestSupport;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// Desktop host path end to end: a real worker process issues several host HTTP service
/// calls inside one typed booru operation. The calls travel WorkerConnection → host services
/// shaped exactly like Desktop's PluginSession.HostServices (operation passed through) →
/// PluginHostHttp → bounded booru transport → a loopback stub server. The booru job, its
/// 20-send budget and its 120-second lifetime must span the whole operation.
/// </summary>
public sealed class BooruOperationJobIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TypedBooruOperationSharesOneJobAndTwentySendBudgetAcrossHostRequests()
    {
        ManualClock clock = new();
        await using LoopbackServer server = new(_ => Reply.Ok);
        using RewritingHandler rewriting = new(server.Port, clock, BooruHostHttp.MinimumStartSpacing);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        using SessionShapedServices services = Services(transport, clock);
        await using IPluginWorkerClient worker = await StartAsync(manager, workspace, services);

        // Two host requests in one operation: one job, both sends counted on it.
        PluginOperationContext first = new("booru/first", 1);
        BooruSearchSnapshot two = Assert.IsType<BooruSearchSnapshot>(
            await worker.SearchBooruPostsAsync(new("danbooru", "http-burst:2", 1, 20), first).WaitAsync(Bound));
        Assert.Equal(2, two.Page.Posts.Count);
        Assert.Equal(2, server.RequestCount);
        BooruJob firstJob = services.SingleJob(first, expectedCalls: 2);
        Assert.Equal(2, firstJob.Sends);
        Assert.False(firstJob.IsOpen);
        Assert.True(firstJob.Token.IsCancellationRequested);
        Assert.Null(PluginHostHttp.MetadataHistory.BooruJobFor(first, clock));

        // The 21st host request of one operation never reaches the network.
        PluginOperationContext burst = new("booru/burst", 1);
        BooruSearchFailure exhausted = Assert.IsType<BooruSearchFailure>(
            await worker.SearchBooruPostsAsync(new("danbooru", "http-burst:21", 1, 20), burst).WaitAsync(Bound));
        Assert.Equal("booru-budget", exhausted.Failure.Code);
        Assert.Equal("completed 20", exhausted.Failure.Message);
        Assert.Equal(2 + BooruJob.MaximumSends, server.RequestCount);
        BooruJob burstJob = services.SingleJob(burst, expectedCalls: BooruJob.MaximumSends + 1);
        Assert.NotSame(firstJob, burstJob);
        Assert.Equal(BooruJob.MaximumSends, burstJob.Sends);
        Assert.False(burstJob.IsOpen);

        // The budget belongs to the operation: the next operation starts fresh.
        PluginOperationContext next = new("booru/next", 1);
        Assert.IsType<BooruSearchSnapshot>(await worker.SearchBooruPostsAsync(new("danbooru", "http-burst:1", 1, 20), next).WaitAsync(Bound));
        Assert.Equal(3 + BooruJob.MaximumSends, server.RequestCount);
        Assert.NotSame(burstJob, services.SingleJob(next, expectedCalls: 1));
    }

    [Fact]
    public async Task TypedBooruOperationLifetimeOf120SecondsSpansHostRequests()
    {
        ManualClock clock = new();
        await using LoopbackServer server = new(_ => Reply.Ok);
        // Each synthetic response takes 10 s of host time (below the 15 s response timeout).
        using RewritingHandler rewriting = new(server.Port, clock, TimeSpan.FromSeconds(10));
        using HttpClient transport = new(rewriting, disposeHandler: false);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        using SessionShapedServices services = Services(transport, clock);
        await using IPluginWorkerClient worker = await StartAsync(manager, workspace, services);

        PluginOperationContext operation = new("booru/deadline", 1);
        // The runtime binds with its production System clock. Pre-bind this synthetic operation
        // with the same ManualClock as its HTTP policy, without a separate job lookup before HTTP.
        using PluginHostHttp.MetadataHistory owner = PluginHostHttp.MetadataHistory.BindOperation(operation, CancellationToken.None, clock)!;
        BooruSearchFailure expired = Assert.IsType<BooruSearchFailure>(
            await worker.SearchBooruPostsAsync(new("danbooru", "http-burst:20", 1, 20), operation).WaitAsync(Bound));
        owner.Dispose();
        // Request n starts at 10n s; the 12th (started at 110 s) is aborted by the 120 s job deadline.
        Assert.Equal("booru-budget", expired.Failure.Code);
        Assert.Equal("completed 11", expired.Failure.Message);
        Assert.Equal(12, server.RequestCount);
        BooruJob job = services.SingleJob(operation, expectedCalls: 12);
        Assert.Equal(12, job.Sends);
        Assert.False(job.IsOpen);
    }

    [Fact]
    public async Task CallerCancellationClosesTheOperationJobAndAbortsTheInFlightSocket()
    {
        ManualClock clock = new();
        await using LoopbackServer server = new(index => index == 1 ? Reply.Stall : Reply.Ok);
        using RewritingHandler rewriting = new(server.Port, clock, BooruHostHttp.MinimumStartSpacing);
        using HttpClient transport = new(rewriting, disposeHandler: false);
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        using SessionShapedServices services = Services(transport, clock);
        await using IPluginWorkerClient worker = await StartAsync(manager, workspace, services);

        PluginOperationContext operation = new("booru/cancel", 1);
        using CancellationTokenSource caller = new();
        Task<BooruSearchResult> pending = worker.SearchBooruPostsAsync(new("danbooru", "http-burst:5", 1, 20), operation, caller.Token);
        Assert.True(await server.Stalled.Task.WaitAsync(Bound));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        Assert.True(await server.ClientAborted.Task.WaitAsync(Bound));

        BooruJob job = services.SingleJob(operation, expectedCalls: 2);
        Assert.False(job.IsOpen);
        Assert.True(job.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(2, job.Sends);
    }

    private static SessionShapedServices Services(HttpClient transport, ManualClock clock) =>
        new(new PluginHostHttp(transport, new RequestPolicyState(clock), new FixturePlugin().Describe().Sources), clock);

    private static Task<IPluginWorkerClient> StartAsync(PluginWorkerManager manager, Workspace workspace, SessionShapedServices services)
    {
        string path = typeof(FixturePlugin).Assembly.Location;
        PluginArtifact artifact = new("compile1", "build1", path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "sdk1", "compiler1", [], null);
        PluginConfiguration configuration = new("config1", new("1", []),
            new Dictionary<string, SettingsSnapshot> { ["fixture"] = new("1", []), ["danbooru"] = new("1", []) });
        PluginWorkerLaunch launch = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet",
            [BuiltTestBinaries.WorkerAssembly()], workspace.Path);
        return manager.StartAsync(launch, artifact, configuration, services);
    }

    /// <summary>
    /// Same delegation as Desktop's PluginSession.HostServices: the typed operation is
    /// passed through to PluginHostHttp. It additionally records which booru job served
    /// each host request of an operation.
    /// </summary>
    private sealed class SessionShapedServices(PluginHostHttp http, TimeProvider clock) : IPluginHostServices, IDisposable
    {
        private readonly ConcurrentQueue<(PluginOperationContext Operation, BooruJob? Job)> _calls = new();

        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            _calls.Enqueue((operation, PluginHostHttp.MetadataHistory.BooruJobFor(operation, clock)));
            return http.SendAsync(request, operation, cancellationToken);
        }

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        internal BooruJob SingleJob(PluginOperationContext operation, int expectedCalls)
        {
            BooruJob?[] jobs = _calls.Where(call => ReferenceEquals(call.Operation, operation)).Select(call => call.Job).ToArray();
            Assert.Equal(expectedCalls, jobs.Length);
            BooruJob job = Assert.IsType<BooruJob>(jobs[0]);
            Assert.All(jobs, other => Assert.Same(job, other));
            return job;
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>Forwards the https booru request to the plain loopback stub and charges host time per response.</summary>
    private sealed class RewritingHandler(int port, ManualClock clock, TimeSpan perResponse) : HttpMessageHandler
    {
        private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;
            using HttpRequestMessage forwarded = new(request.Method, new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port)
                { Path = original.AbsolutePath, Query = original.Query }.Uri);
            HttpResponseMessage response = await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            clock.Advance(perResponse);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _client.Dispose(); base.Dispose(disposing); }
    }

    private enum Reply { Ok, Stall }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<int, Reply> _reply;
        private readonly Task _loop;
        private int _requests;
        internal LoopbackServer(Func<int, Reply> reply)
        {
            _reply = reply;
            _listener.Start();
            _loop = RunAsync();
        }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int RequestCount => Volatile.Read(ref _requests);
        internal TaskCompletionSource<bool> Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ClientAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task RunAsync()
        {
            try
            {
                while (true) _ = HandleAsync(await _listener.AcceptTcpClientAsync(_lifetime.Token));
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    StringBuilder pending = new();
                    while (true)
                    {
                        // One connection may carry several keep-alive requests.
                        while (!pending.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            int read = await stream.ReadAsync(buffer, _lifetime.Token);
                            if (read == 0) return;
                            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        }
                        string text = pending.ToString();
                        pending.Remove(0, text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                        int index = Interlocked.Increment(ref _requests) - 1;
                        if (_reply(index) == Reply.Stall)
                        {
                            Stalled.TrySetResult(true);
                            int read;
                            try { read = await stream.ReadAsync(buffer, _lifetime.Token); }
                            catch (IOException) { read = 0; }
                            if (read == 0) ClientAborted.TrySetResult(true);
                            return;
                        }
                        byte[] body = "[]"u8.ToArray();
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n"), _lifetime.Token);
                        await stream.WriteAsync(body, _lifetime.Token);
                        await stream.FlushAsync(_lifetime.Token);
                    }
                }
                catch (Exception) when (_lifetime.IsCancellationRequested) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
            _lifetime.Dispose();
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-booru-job-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
