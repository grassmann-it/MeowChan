using System.Net;
using System.Net.Sockets;
using System.Text;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// In-flight lifetime of a <see cref="BooruJob"/>: Close (and the job's linked operation
/// lifetime) must abort every wait and transport phase, and no network operation may
/// start after Close returned. Synthetic handlers and a loopback socket only.
/// </summary>
public sealed class BooruJobLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public void CloseCancelsTheJobTokenAndRefusesFurtherReservations()
    {
        ManualClock clock = new();
        BooruJob job = new(clock);
        Assert.False(job.Token.IsCancellationRequested);
        Assert.True(job.TryReserveSend());
        job.Close();
        Assert.True(job.Token.IsCancellationRequested);
        Assert.False(job.IsOpen);
        Assert.False(job.TryReserveSend());
        Assert.Equal(1, job.Sends);
        job.Close(); // idempotent
    }

    [Fact]
    public void JobTokenIsLinkedToOperationLifetimeAndDeadline()
    {
        ManualClock clock = new();
        using CancellationTokenSource operation = new();
        BooruJob linked = new(clock, operation.Token);
        operation.Cancel();
        Assert.True(linked.Token.IsCancellationRequested);
        Assert.False(linked.TryReserveSend());
        linked.Close();

        BooruJob expiring = new(clock);
        clock.Advance(BooruJob.MaximumLifetime - TimeSpan.FromTicks(1));
        Assert.False(expiring.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(expiring.Token.IsCancellationRequested);
        Assert.False(expiring.TryReserveSend());
        expiring.Close();
    }

    [Fact]
    public async Task CloseDuringRunningRequestAbortsTransportAndNeverSendsAgain()
    {
        ManualClock clock = new();
        TaskCompletionSource<bool> transportCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        using HttpClient transport = new(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref sends);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { transportCancelled.TrySetResult(true); throw; }
            return Response(HttpStatusCode.OK, "late");
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();

        Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
        await EventuallyAsync(() => Volatile.Read(ref sends) == 1);
        services.CloseJob(job);

        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound));
        Assert.Equal("booru-job", failure.Failure.Code);
        Assert.True(await transportCancelled.Task.WaitAsync(Bound));
        // Neither the single transport retry nor a later call reaches the network.
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await http.SendAsync(Get(), Source(), job, default)).Failure.Code);
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Equal(1, job.Sends);
    }

    [Theory]
    [InlineData("status-503")]
    [InlineData("transport-exception")]
    public async Task CloseDuringRetryWaitStopsBeforeTheSecondSend(string firstFailure)
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref sends) == 1 && firstFailure == "transport-exception")
                throw new HttpRequestException("synthetic reset");
            return Task.FromResult(Interlocked.CompareExchange(ref sends, 0, 0) == 1
                ? Response(HttpStatusCode.ServiceUnavailable, "busy") : Response(HttpStatusCode.OK, "second"));
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();

        Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
        await EventuallyAsync(() => Volatile.Read(ref sends) == 1);
        await Task.Delay(20); // let the attempt reach the retry delay (the clock is frozen)
        Assert.False(pending.IsCompleted);
        services.CloseJob(job);

        HostHttpFailure failure = Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound));
        Assert.Equal("booru-job", failure.Failure.Code);
        clock.Advance(BooruHostHttp.RetryDelay + TimeSpan.FromSeconds(30));
        await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Equal(1, job.Sends);
    }

    [Fact]
    public async Task CloseWhileBodyIsReadAbortsTheReadWithoutRetry()
    {
        ManualClock clock = new();
        BlockingContent content = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((_, _) =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();

        Task<HostHttpResult> pending = services.SendHttpAsync(Get(), job.Operation, default);
        Assert.True(await content.ReadStarted.Task.WaitAsync(Bound));
        services.CloseJob(job);

        Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
        Assert.True(await content.ReadCancelled.Task.WaitAsync(Bound));
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task CloseWhileWaitingForSendGateAdmissionNeverSends()
    {
        ManualClock clock = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        using HttpClient transport = new(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref sends);
            await release.Task.WaitAsync(token);
            return Response(HttpStatusCode.OK, "ok");
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob holder = services.OpenJob();
        BooruJob waiter = services.OpenJob();
        try
        {
            Task<HostHttpResult> first = services.SendHttpAsync(Get(), holder.Operation, default);
            await EventuallyAsync(() => Volatile.Read(ref sends) == 1);
            Task<HostHttpResult> queued = services.SendHttpAsync(Get(), waiter.Operation, default);
            await Task.Delay(20);
            Assert.False(queued.IsCompleted);

            services.CloseJob(waiter);
            Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await queued.WaitAsync(Bound)).Failure.Code);

            release.TrySetResult();
            Assert.IsType<HostHttpResponse>(await first.WaitAsync(Bound));
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(20);
            Assert.Equal(1, Volatile.Read(ref sends));
            Assert.Equal(0, waiter.Sends);
        }
        finally { services.CloseJob(holder); services.CloseJob(waiter); }
    }

    [Fact]
    public async Task CloseDuringProviderSpacingNeverSends()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler((_, _) =>
        { Interlocked.Increment(ref sends); return Task.FromResult(Response(HttpStatusCode.OK, "ok")); }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruHostServices services = new(new Inner(), http);
        BooruJob job = services.OpenJob();

        Assert.IsType<HostHttpResponse>(await services.SendHttpAsync(Get(), job.Operation, default));
        Task<HostHttpResult> spaced = services.SendHttpAsync(Get(), job.Operation, default);
        await Task.Delay(20);
        Assert.False(spaced.IsCompleted);
        services.CloseJob(job);
        Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await spaced.WaitAsync(Bound)).Failure.Code);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task DeadlineDuringRunningRequestAbortsTransportAsBudget()
    {
        ManualClock clock = new();
        TaskCompletionSource<bool> transportCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sends = 0;
        using HttpClient transport = new(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref sends);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { transportCancelled.TrySetResult(true); throw; }
            return Response(HttpStatusCode.OK, "late");
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruJob job = new(clock);
        clock.Advance(BooruJob.MaximumLifetime - TimeSpan.FromSeconds(5));

        Task<HostHttpResult> pending = http.SendAsync(Get(), Source(), job, default);
        await EventuallyAsync(() => Volatile.Read(ref sends) == 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("booru-budget", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
        Assert.True(await transportCancelled.Task.WaitAsync(Bound));
        Assert.Equal(1, Volatile.Read(ref sends));
        job.Close();
    }

    [Fact]
    public async Task CallerCancellationStillPropagatesAsCancellation()
    {
        ManualClock clock = new();
        int sends = 0;
        using HttpClient transport = new(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref sends);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, "late");
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        BooruJob job = new(clock);
        using CancellationTokenSource caller = new();
        Task<HostHttpResult> pending = http.SendAsync(Get(), Source(), job, caller.Token);
        await EventuallyAsync(() => Volatile.Read(ref sends) == 1);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        Assert.True(job.IsOpen);
        job.Close();
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task NoTransportCallStartsAfterCloseReturnedEvenWhenRacingReservation()
    {
        ManualClock clock = new();
        int closedReturned = 0, lateSends = 0, sends = 0;
        using HttpClient transport = new(new Handler((_, token) =>
        {
            Interlocked.Increment(ref sends);
            // Close cancels before it returns, so a transport entered afterwards only ever
            // sees a cancelled token; conforming handlers (SocketsHttpHandler) then perform no I/O.
            if (Volatile.Read(ref closedReturned) != 0 && !token.IsCancellationRequested) Interlocked.Increment(ref lateSends);
            return Task.FromResult(Response(HttpStatusCode.OK, "ok"));
        }));
        using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
        for (int iteration = 0; iteration < 200; ++iteration)
        {
            Volatile.Write(ref closedReturned, 0);
            BooruJob job = new(clock);
            using ManualResetEventSlim go = new(false);
            Task<HostHttpResult> send = Task.Run(async () => { go.Wait(); return await http.SendAsync(Get(), Source(), job, default); });
            Task close = Task.Run(() => { go.Wait(); job.Close(); Volatile.Write(ref closedReturned, 1); });
            go.Set();
            await Task.WhenAll(send, close).WaitAsync(Bound);
            HostHttpResult result = await send;
            Assert.True(result is HostHttpResponse or HostHttpFailure { Failure.Code: "booru-job" }, result.ToString());
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Equal(0, Volatile.Read(ref lateSends));
    }

    [Fact]
    public async Task CloseDuringLoopbackHeaderWaitAndBodyReadAbortsTheSocket()
    {
        foreach (bool sendHeaders in new[] { false, true })
        {
            await using StallingServer server = new(sendHeaders);
            using SocketsHttpHandler sockets = new()
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = async (_, token) =>
                {
                    Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(IPAddress.Loopback, server.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };
            using HttpClient transport = new(new PlainTextHandler(sockets));
            ManualClock clock = new();
            using BooruHostHttp http = new(transport, new RequestPolicyState(clock), [Source()]);
            BooruJob job = new(clock);

            Task<HostHttpResult> pending = http.SendAsync(Get(), Source(), job, default);
            Assert.True(await server.Stalled.Task.WaitAsync(Bound));
            job.Close();
            Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(Bound)).Failure.Code);
            Assert.True(await server.ClientAborted.Task.WaitAsync(Bound), sendHeaders ? "body read" : "header wait");
            clock.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(50);
            Assert.Equal(1, server.Connections);
        }
    }

    private static HostHttpRequest Get() => new("danbooru", HostHttpMethod.Get, new("https://danbooru.test/posts.json"));
    // One instance: direct BooruHostHttp calls must pass the declared source itself.
    private static readonly SourceDescription Declared = new("danbooru", "Danbooru", new("https://danbooru.test/"),
        SourceCapabilities.PostSearch, [], [new("danbooru.test", HttpHostPurpose.Metadata)], new("1", []));
    private static SourceDescription Source() => Declared;
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        for (int i = 0; i < 500 && !predicate(); ++i) await Task.Delay(2);
        Assert.True(predicate());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    /// <summary>Speaks plain HTTP to the loopback stub while the booru request keeps its https URL.</summary>
    private sealed class PlainTextHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri original = request.RequestUri!;
            request.RequestUri = new UriBuilder(original) { Scheme = Uri.UriSchemeHttp, Port = 80 }.Uri;
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            request.RequestUri = original;
            return response;
        }
    }

    private sealed class Inner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), "inner"u8.ToArray()));
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => Task.FromResult(new ImageAssetChunk(offset, [], true));
    }

    private sealed class BlockingContent : HttpContent
    {
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream(this));

        private sealed class BlockingStream(BlockingContent owner) : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                owner.ReadStarted.TrySetResult(true);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { owner.ReadCancelled.TrySetResult(true); throw; }
                return 0;
            }
        }
    }

    /// <summary>
    /// Loopback stub that accepts one request and then stalls: either before the response
    /// headers, or after the headers and a partial body. It reports when the client aborts.
    /// </summary>
    private sealed class StallingServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly bool _sendHeaders;
        private readonly Task _loop;
        private int _connections;
        internal StallingServer(bool sendHeaders)
        {
            _sendHeaders = sendHeaders;
            _listener.Start();
            _loop = RunAsync();
        }
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int Connections => Volatile.Read(ref _connections);
        internal TaskCompletionSource<bool> Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ClientAborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                    Interlocked.Increment(ref _connections);
                    _ = HandleAsync(client);
                }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];
                StringBuilder head = new();
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(buffer, _lifetime.Token);
                    if (read == 0) return;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                if (_sendHeaders)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n[{\"id\":"), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                }
                Stalled.TrySetResult(true);
                try
                {
                    // The client never gets more bytes; a zero read or reset proves it aborted.
                    int read = await stream.ReadAsync(buffer, _lifetime.Token);
                    if (read == 0) ClientAborted.TrySetResult(true);
                }
                catch (IOException) { ClientAborted.TrySetResult(true); }
                catch (SocketException) { ClientAborted.TrySetResult(true); }
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
}
