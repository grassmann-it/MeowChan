using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using MeowChan.Http;
using Xunit.Abstractions;

namespace MeowChan.Tests;

/// <summary>
/// The counters are process-wide, so every test counts under its own unique source key and
/// asserts exact values for that key only.
/// </summary>
public sealed class TransferMeteringTests(ITestOutputHelper output)
{
    private static string NewKey() => "meter-" + Guid.NewGuid().ToString("N");

    private static HttpClient Metered(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> inner)
        => new(new MeteringHandler(new DelegateHandler(inner)));

    private static HttpRequestMessage Get(string key, string url = "https://meter.test/file", HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Get, url);
        request.Options.Set(MeteringHandler.SourceKey, key);
        return request;
    }

    private static long Read(ref long field) => Volatile.Read(ref field);

    private static byte[] Payload(int length)
    {
        byte[] bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData("stream-async")]
    [InlineData("stream-sync")]
    [InlineData("bytes")]
    [InlineData("string")]
    [InlineData("copy-to")]
    [InlineData("buffered-by-client")]
    public async Task BodyBytesAreCountedExactlyOnceOnEveryReadPath(string path)
    {
        string key = NewKey();
        byte[] payload = path == "string" ? Encoding.ASCII.GetBytes(new string('x', 150_000)) : Payload(200_000);
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        using HttpRequestMessage request = Get(key);
        HttpCompletionOption option = path == "buffered-by-client" ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead;
        using HttpResponseMessage response = await client.SendAsync(request, option);
        switch (path)
        {
            case "stream-async":
                await using (Stream body = await response.Content.ReadAsStreamAsync())
                    Assert.Equal(payload, await ReadAllAsync(body));
                break;
            case "stream-sync":
                using (Stream body = response.Content.ReadAsStream())
                {
                    using MemoryStream copy = new();
                    body.CopyTo(copy);
                    Assert.Equal(payload, copy.ToArray());
                }
                break;
            case "bytes":
                Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
                break;
            case "string":
                Assert.Equal(payload.Length, (await response.Content.ReadAsStringAsync()).Length);
                break;
            case "copy-to":
                using (MemoryStream target = new())
                {
                    await response.Content.CopyToAsync(target);
                    Assert.Equal(payload, target.ToArray());
                }
                break;
            default:
                Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
                break;
        }
        // A second buffered read of the same content never reads the transport again.
        if (path is "bytes" or "buffered-by-client") await response.Content.ReadAsByteArrayAsync();

        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(payload.Length, Read(ref counters.NetDown));
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(0, Read(ref counters.Errors));
    }

    [Fact]
    public async Task HeadersAndContentLengthPassThroughUnchanged()
    {
        string key = NewKey();
        byte[] payload = Payload(5000);
        using HttpClient client = Metered((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            response.Content.Headers.ContentType = new("image/png");
            response.Content.Headers.ContentDisposition = new("inline") { FileName = "a.png" };
            response.Headers.ETag = new("\"abc\"");
            return Task.FromResult(response);
        });
        using HttpResponseMessage response = await client.SendAsync(Get(key), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(payload.Length, response.Content.Headers.ContentLength);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("a.png", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("\"abc\"", response.Headers.ETag!.Tag);
        Assert.Equal(0, Read(ref TransferMeter.For(key).NetDown));
    }

    [Fact]
    public async Task ExplicitContentLengthOfAStreamBodySurvivesAndAnUnknownOneStaysUnknown()
    {
        string key = NewKey();
        using HttpClient client = Metered((request, _) =>
        {
            bool known = request.RequestUri!.AbsolutePath == "/known";
            StreamContent content = new(new YieldingStream(new byte[300], 100));
            if (known) content.Headers.ContentLength = 300;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using HttpResponseMessage known = await client.SendAsync(Get(key, "https://meter.test/known"), HttpCompletionOption.ResponseHeadersRead);
        using HttpResponseMessage unknown = await client.SendAsync(Get(key, "https://meter.test/unknown"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(300, known.Content.Headers.ContentLength);
        Assert.Null(unknown.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(204, 0)]
    [InlineData(304, 0)]
    [InlineData(400, 1)]
    [InlineData(404, 1)]
    [InlineData(429, 1)]
    [InlineData(503, 1)]
    public async Task OnlyStatusesFromFourHundredCountAsErrors(int status, int errors)
    {
        string key = NewKey();
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent([1, 2, 3]) }));
        using HttpResponseMessage response = await client.SendAsync(Get(key), HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsByteArrayAsync();
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(errors, Read(ref counters.Errors));
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(3, Read(ref counters.NetDown));
    }

    [Fact]
    public async Task TransportExceptionCountsRequestAndErrorAndStillPropagates()
    {
        string key = NewKey();
        using HttpClient client = Metered((_, _) => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(Get(key)));
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(1, Read(ref counters.Errors));
        Assert.Equal(0, Read(ref counters.NetDown));
    }

    [Fact]
    public async Task CancelledRequestCountsTheRequestButIsNotAnError()
    {
        string key = NewKey();
        using CancellationTokenSource cancellation = new();
        using HttpClient client = Metered(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Get(key), cancellation.Token));
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(0, Read(ref counters.Errors));
    }

    [Fact]
    public async Task HeadResponsesCountTheRequestAndLeaveContentAlone()
    {
        string key = NewKey();
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using HttpResponseMessage response = await client.SendAsync(Get(key, method: HttpMethod.Head), HttpCompletionOption.ResponseHeadersRead);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(0, Read(ref counters.NetDown));
    }

    [Fact]
    public async Task CancellationInTheMiddleOfTheBodyKeepsTheBytesAlreadyRead()
    {
        string key = NewKey();
        using CancellationTokenSource cancellation = new();
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ScriptedStream(1000, ScriptedEnd.Hang)) }));
        using HttpResponseMessage response = await client.SendAsync(Get(key), HttpCompletionOption.ResponseHeadersRead);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[4096];
        Assert.Equal(1000, await body.ReadAsync(buffer, cancellation.Token));
        Task<int> pending = body.ReadAsync(buffer, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(1000, Read(ref counters.NetDown));
        Assert.Equal(0, Read(ref counters.Errors));
    }

    [Fact]
    public async Task FailedBodyKeepsTheBytesAlreadyReadAndCountsOneError()
    {
        string key = NewKey();
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new ScriptedStream(500, ScriptedEnd.Fail)) }));
        using HttpResponseMessage response = await client.SendAsync(Get(key), HttpCompletionOption.ResponseHeadersRead);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[4096];
        Assert.Equal(500, await body.ReadAsync(buffer));
        await Assert.ThrowsAsync<IOException>(async () => await body.ReadAsync(buffer));
        await Assert.ThrowsAsync<IOException>(async () => await body.ReadAsync(buffer));
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(500, Read(ref counters.NetDown));
        Assert.Equal(1, Read(ref counters.Errors));
    }

    [Fact]
    public async Task ParallelReadersAddUpExactly()
    {
        string key = NewKey();
        const int Responses = 24, Length = 100_000;
        byte[] payload = Payload(Length);
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new YieldingStream(payload, 4096)) }));
        await Task.WhenAll(Enumerable.Range(0, Responses).Select(async _ =>
        {
            using HttpResponseMessage response = await client.SendAsync(Get(key), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        }));
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal((long)Responses * Length, Read(ref counters.NetDown));
        Assert.Equal(Responses, Read(ref counters.Requests));
    }

    [Fact]
    public async Task TheSourceOptionWinsAndWithoutItTheHostNameIsTheKey()
    {
        string optioned = NewKey();
        string host = "host-" + Guid.NewGuid().ToString("N") + ".test";
        using HttpClient client = Metered((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) }));
        using (await client.SendAsync(Get(optioned, "https://" + host + "/a"), HttpCompletionOption.ResponseHeadersRead)) { }
        using (await client.GetAsync("https://" + host + "/b")) { }
        Assert.Equal(1, Read(ref TransferMeter.For(optioned).Requests));
        Assert.Equal(1, Read(ref TransferMeter.For(host).Requests));
        Assert.Equal(2, Read(ref TransferMeter.For(host).NetDown));
        Assert.Contains(TransferMeter.All, pair => pair.Key == host && ReferenceEquals(pair.Value, TransferMeter.For(host)));
    }

    [Fact]
    public async Task ThePolicyClientAttachesItsSourceForTheTransportBelow()
    {
        string key = NewKey();
        using HttpClient transport = Metered((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(new byte[777]) }));
        using HttpClient client = ProviderHttpClientFactory.CreatePolicyClient(transport, key, new RequestPolicyState());
        using HttpResponseMessage response = await client.GetAsync("https://meter-" + Guid.NewGuid().ToString("N") + ".test/media.bin");
        Assert.Equal(777, (await response.Content.ReadAsByteArrayAsync()).Length);
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(1, Read(ref counters.Requests));
        Assert.Equal(777, Read(ref counters.NetDown));
        Assert.Equal(0, Read(ref counters.Errors));
    }

    [Fact]
    public async Task TheDefaultClientCountsDecompressedBodyBytesAgainstItsSource()
    {
        string key = NewKey();
        byte[] plain = new byte[300_000];
        using MemoryStream compressed = new();
        await using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) await gzip.WriteAsync(plain);
        byte[] wire = compressed.ToArray();
        Assert.True(wire.Length < 10_000);

        HttpListener listener = StartListener(out string prefix);
        try
        {
            Task serving = Task.Run(async () =>
            {
                HttpListenerContext context = await listener.GetContextAsync();
                context.Response.AddHeader("Content-Encoding", "gzip");
                context.Response.ContentLength64 = wire.Length;
                await context.Response.OutputStream.WriteAsync(wire);
                context.Response.Close();
            });
            HttpClient client = ProviderHttpClientFactory.GetDefaultClient(key);
            using HttpResponseMessage response = await client.GetAsync(prefix + "file.bin");
            Assert.Equal(plain.Length, (await response.Content.ReadAsByteArrayAsync()).Length);
            await serving;
        }
        finally { listener.Close(); }

        // The default transport decompresses below the metering handler: bytes are counted after decompression.
        SourceCounters counters = TransferMeter.For(key);
        Assert.Equal(plain.Length, Read(ref counters.NetDown));
        Assert.Equal(1, Read(ref counters.Requests));
    }

    [Fact]
    public void KeysBeyondTheCeilingShareTheOtherBucketAndBlankKeysAreUnknown()
    {
        SourceRegistry registry = new(3);
        SourceCounters first = registry.For("a"), second = registry.For("b");
        Assert.Same(first, registry.For("a"));
        SourceCounters unknown = registry.For(" ");
        Assert.Same(unknown, registry.For(null));
        // Three keys now exist; every new key goes to "other", known keys keep their own counters.
        SourceCounters overflow = registry.For("c");
        Assert.Same(overflow, registry.For("d"));
        Assert.Same(second, registry.For("b"));
        Assert.Equal(new[] { "a", "b", TransferMeter.OtherKey, TransferMeter.UnknownKey }.Order(),
            registry.All.Select(pair => pair.Key).Order());
        Assert.NotSame(overflow, first);
    }

    [Fact]
    public void CountingAReadAllocatesNothingPerChunk()
    {
        SourceCounters counters = new();
        using MeteringHandler.CountingStream stream = new(new EndlessStream(), counters);
        byte[] buffer = new byte[81920];

        for (int i = 0; i < 2000; i++) { _ = stream.Read(buffer, 0, buffer.Length); _ = stream.ReadAsync(buffer.AsMemory()).GetAwaiter().GetResult(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            _ = stream.Read(buffer.AsSpan());
            ValueTask<int> pending = stream.ReadAsync(buffer.AsMemory());
            Assert.True(pending.IsCompletedSuccessfully);
            _ = pending.GetAwaiter().GetResult();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2L * 102_000 * 81920, Read(ref counters.NetDown));
        // Any per-chunk allocation would show as at least 2.4 MB here.
        Assert.True(allocated < 1024, $"allocated {allocated} bytes over 200000 counted reads");

        // Overhead for the report (not asserted): counted versus uncounted reads of the same stream.
        EndlessStream raw = new();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++) _ = raw.Read(buffer.AsSpan());
        double rawNs = watch.Elapsed.TotalNanoseconds / 1_000_000;
        watch.Restart();
        for (int i = 0; i < 1_000_000; i++) _ = stream.Read(buffer.AsSpan());
        double countedNs = watch.Elapsed.TotalNanoseconds / 1_000_000;
        output.WriteLine($"overhead: raw {rawNs:F1} ns/read, counted {countedNs:F1} ns/read, delta {countedNs - rawNs:F1} ns/read, allocated {allocated} B over 200000 reads");
    }

    private static HttpListener StartListener(out string prefix)
    {
        for (int attempt = 0; ; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            HttpListener listener = new();
            prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); return listener; }
            catch (HttpListenerException) when (attempt < 20) { listener.Close(); }
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream copy = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0) copy.Write(buffer, 0, read);
        return copy.ToArray();
    }

    private enum ScriptedEnd { Hang, Fail }

    /// <summary>Delivers a fixed prefix, then hangs until cancelled or fails.</summary>
    private sealed class ScriptedStream(int length, ScriptedEnd end) : Stream
    {
        private bool _delivered;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered)
            {
                _delivered = true;
                int count = Math.Min(length, buffer.Length);
                buffer.Span[..count].Fill(9);
                return count;
            }
            await Task.Yield();
            if (end == ScriptedEnd.Fail) throw new IOException("connection reset");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>Serves a payload in small chunks, yielding between them so reads really suspend.</summary>
    private sealed class YieldingStream(byte[] payload, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            int count = Math.Min(Math.Min(chunk, buffer.Length), payload.Length - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }

    /// <summary>Completes every read synchronously with a full buffer.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override int Read(Span<byte> buffer) => buffer.Length;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(buffer.Length);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
