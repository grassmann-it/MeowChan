using System.Net;
using System.Text;
using MeowChan.Http;

namespace MeowChan.Tests;

/// <summary>In-memory transport/serialization tests only: no sockets, listener or live source.</summary>
public sealed class TransferNetUploadMeteringTests
{
    private readonly SourceRegistry _registry = new(TransferMeter.MaximumSources);

    private MeteringHandler Metered(HttpMessageHandler inner) => new(inner, _registry);

    private static string NewKey() => "upload-" + Guid.NewGuid().ToString("N");
    private static long Read(ref long field) => Volatile.Read(ref field);

    private static HttpRequestMessage Request(string key, HttpContent? content = null, HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Post, "https://upload.test/body") { Content = content };
        request.Options.Set(MeteringHandler.SourceKey, key);
        return request;
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.Accepted) { Content = new ByteArrayContent([9, 8]) };

    [Theory]
    [InlineData("bytes", false)]
    [InlineData("bytes", true)]
    [InlineData("string", false)]
    [InlineData("string", true)]
    [InlineData("stream", false)]
    [InlineData("stream", true)]
    [InlineData("streaming", false)]
    [InlineData("streaming", true)]
    [InlineData("prebuffered", false)]
    [InlineData("prebuffered", true)]
    public async Task SerializesActualPayloadWithoutChangingHeadersOrResponse(string kind, bool sync)
    {
        string key = NewKey();
        byte[] payload = Encoding.UTF8.GetBytes("aä😀z");
        HttpContent original = kind switch
        {
            "string" => new StringContent("aä😀z", Encoding.UTF8, "text/plain"),
            "stream" => new StreamContent(new MemoryStream(payload)),
            "streaming" or "prebuffered" => new ScriptedContent(payload),
            _ => new ByteArrayContent(payload)
        };
        if (kind == "prebuffered") await original.LoadIntoBufferAsync();
        original.Headers.TryAddWithoutValidation("X-Content-Test", "unchanged");
        original.Headers.ContentDisposition = new("attachment") { FileName = "body.bin" };
        long? length = original.Headers.ContentLength;
        string headers = original.Headers.ToString();
        using MemoryStream sent = new();
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            Assert.Equal(length, request.Content!.Headers.ContentLength);
            Assert.Equal(headers, request.Content.Headers.ToString());
            Assert.Equal(0, Read(ref _registry.For(key).NetUp));
            await request.Content.CopyToAsync(sent, token);
            return Response();
        }, (request, token) =>
        {
            Assert.Equal(length, request.Content!.Headers.ContentLength);
            Assert.Equal(headers, request.Content.Headers.ToString());
            Assert.Equal(0, Read(ref _registry.For(key).NetUp));
            request.Content.CopyTo(sent, null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, original);
        using HttpResponseMessage response = sync
            ? client.Send(request, HttpCompletionOption.ResponseHeadersRead)
            : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(payload, sent.ToArray());
        Assert.True(sent.CanWrite); // Disposing the metering destination must not dispose the transport's stream.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new byte[] { 9, 8 }, await response.Content.ReadAsByteArrayAsync());
        AssertCounters(key, payload.Length, requests: 1, errors: 0, down: 2);
        Assert.Equal(headers, original.Headers.ToString());
        if (original is ScriptedContent scripted)
        {
            Assert.Equal(1, scripted.Serializations);
            Assert.False(scripted.Disposed);
            request.Dispose();
            Assert.True(scripted.Disposed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedWriteOverloadsAndEmptyChunksAreCountedExactlyOnce(bool sync)
    {
        string key = NewKey();
        using MixedWritesContent content = new();
        using YieldingSink sent = new();
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(sent, token);
            return Response();
        }, (request, token) =>
        {
            request.Content!.CopyTo(sent, null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        using HttpResponseMessage response = sync
            ? client.Send(request, HttpCompletionOption.ResponseHeadersRead)
            : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, sent.ToArray());
        Assert.Equal(1, content.Serializations);
        AssertCounters(key, 7, requests: 1, errors: 0);
    }

    [Fact]
    public async Task EverySerializationBelowOneSendAndEveryNewSendCountsAgainWithoutNestedMeters()
    {
        string firstKey = NewKey(), secondKey = NewKey();
        using ScriptedContent content = new([1, 2, 3]);
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, token) =>
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using MemoryStream sent = new();
                await request.Content!.CopyToAsync(sent, token);
                Assert.Equal(new byte[] { 1, 2, 3 }, sent.ToArray());
            }
            return Response();
        })));
        // HttpClient prohibits resending a message; HttpMessageInvoker exercises the handler's retry contract.
        using HttpRequestMessage request = Request(firstKey, content);
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        request.Options.Set(MeteringHandler.SourceKey, secondKey);
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        Assert.Equal(4, content.Serializations);
        Assert.False(content.Disposed);
        AssertCounters(firstKey, 6, requests: 1, errors: 0);
        AssertCounters(secondKey, 6, requests: 1, errors: 0);
    }

    [Fact]
    public async Task ANonReplayableStreamStillFailsOnTheNextAttemptWithoutInventingBytes()
    {
        string key = NewKey();
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, new StreamContent(new NonSeekableStream([1, 2, 3])));
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.SendAsync(request, CancellationToken.None));
        AssertCounters(key, 3, requests: 2, errors: 1);
    }

    [Fact]
    public async Task BufferedReadingOfTheWrapperDoesNotHideTheNextSendOrDoubleCountThatRead()
    {
        string key = NewKey();
        using ScriptedContent content = new([1, 2, 3]);
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, _) =>
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content.ReadAsByteArrayAsync());
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        Assert.Equal(2, content.Serializations);
        AssertCounters(key, 6, requests: 2, errors: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingSerializedContentDoesNotCountTheSameBytesAgain(bool sync)
    {
        string key = NewKey();
        using ScriptedContent content = new([1, 2, 3]);
        using HttpClient client = new(Metered(new Handler(async (request, _) =>
        {
            using Stream source = sync ? request.Content!.ReadAsStream() : await request.Content!.ReadAsStreamAsync();
            using MemoryStream sent = new();
            await source.CopyToAsync(sent);
            Assert.Equal(new byte[] { 1, 2, 3 }, sent.ToArray());
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(1, content.Serializations);
        AssertCounters(key, 3, requests: 1, errors: 0);
    }

    [Fact]
    public async Task RemovingAnExplicitContentLengthBetweenSendsIsNotRecreatedFromTheOriginal()
    {
        string key = NewKey();
        using ScriptedContent content = new([1, 2, 3]);
        content.Headers.ContentLength = 99;
        List<long?> lengths = new();
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, token) =>
        {
            lengths.Add(request.Content!.Headers.ContentLength);
            await request.Content.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        request.Content!.Headers.ContentLength = null;
        using (await invoker.SendAsync(request, CancellationToken.None)) { }

        Assert.Equal(new long?[] { 99, null }, lengths);
        AssertCounters(key, 6, requests: 2, errors: 0);
    }

    [Fact]
    public async Task HeaderEditsBetweenSendsAreNotLostWhenReplacingThePreviousMeter()
    {
        string key = NewKey();
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, new ByteArrayContent([1, 2]));
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        request.Content!.Headers.ContentType = new("application/example");
        request.Content.Headers.TryAddWithoutValidation("X-Retry", "second");
        string headers = request.Content.Headers.ToString();
        using (await invoker.SendAsync(request, CancellationToken.None)) { }
        Assert.Equal(headers, request.Content.Headers.ToString());
        AssertCounters(key, 4, requests: 2, errors: 0);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", false)]
    [InlineData("HEAD", true)]
    public async Task GetAndHeadWithoutContentNeverInventUploadBytes(string method, bool sync)
    {
        string key = NewKey();
        HttpResponseMessage reply = Response();
        HttpContent originalResponseContent = reply.Content;
        using HttpClient client = new(Metered(new Handler((request, _) =>
        {
            Assert.Null(request.Content);
            return Task.FromResult(reply);
        }, (request, _) =>
        {
            Assert.Null(request.Content);
            return reply;
        })));
        using HttpRequestMessage request = Request(key, method: new HttpMethod(method));
        using HttpResponseMessage response = sync
            ? client.Send(request, HttpCompletionOption.ResponseHeadersRead)
            : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (method == "HEAD") Assert.Same(originalResponseContent, response.Content);
        Assert.Equal(new byte[] { 9, 8 }, await response.Content.ReadAsByteArrayAsync());
        AssertCounters(key, 0, requests: 1, errors: 0, down: method == "HEAD" ? 0 : 2);
    }

    [Fact]
    public async Task ContentWhichTheTransportNeverConsumesCountsNothingEvenWithAKnownLength()
    {
        string key = NewKey();
        using HttpClient client = new(Metered(new Handler((request, _) =>
        {
            Assert.Equal(1234, request.Content!.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        })));
        using HttpRequestMessage request = Request(key, new ByteArrayContent(new byte[1234]));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertCounters(key, 0, requests: 1, errors: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSerializationKeepsCompletedChunksAndCountsOnlyTheExistingTransportError(bool sync)
    {
        string key = NewKey();
        InvalidOperationException failure = new("synthetic producer failure");
        using ScriptedContent content = new([1, 2, 3], failure: failure);
        using MemoryStream sent = new();
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(sent, token);
            return Response();
        }, (request, token) =>
        {
            request.Content!.CopyTo(sent, null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        if (sync)
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => client.Send(request, HttpCompletionOption.ResponseHeadersRead)));
        else
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)));
        Assert.Equal(new byte[] { 1, 2, 3 }, sent.ToArray());
        Assert.False(content.Disposed);
        Assert.True(sent.CanWrite);
        AssertCounters(key, 3, requests: 1, errors: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringSerializationKeepsCompletedChunksWithoutAnError(bool sync)
    {
        string key = NewKey();
        using CancellationTokenSource cancellation = new();
        using ScriptedContent content = new([1, 2, 3], afterPrefix: cancellation.Cancel);
        using MemoryStream sent = new();
        using HttpMessageInvoker invoker = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(sent, token);
            return Response();
        }, (request, token) =>
        {
            request.Content!.CopyTo(sent, null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        OperationCanceledException observed;
        if (sync)
            observed = Assert.ThrowsAny<OperationCanceledException>(() => invoker.Send(request, cancellation.Token));
        else
            observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.SendAsync(request, cancellation.Token));
        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.False(content.Disposed);
        Assert.True(sent.CanWrite);
        Assert.Equal(new byte[] { 1, 2, 3 }, sent.ToArray());
        AssertCounters(key, 3, requests: 1, errors: 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledDestinationWriteIsNotCounted(bool sync, bool cancel)
    {
        string key = NewKey();
        using CancellationTokenSource cancellation = new();
        using MixedWritesContent content = new();
        using YieldingSink sent = new(failAfterFirstWrite: true, cancellation: cancel ? cancellation : null);
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(sent, token);
            return Response();
        }, (request, token) =>
        {
            request.Content!.CopyTo(sent, null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, content);
        if (sync)
        {
            if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => client.Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token));
            else Assert.Throws<HttpRequestException>(() => client.Send(request, HttpCompletionOption.ResponseHeadersRead));
        }
        else
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token));
            else await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead));
        }
        Assert.Equal(new byte[] { 1 }, sent.ToArray());
        AssertCounters(key, 1, requests: 1, errors: cancel ? 0 : 1);
    }

    [Fact]
    public async Task EmptyContentAndFailureBeforeConsumptionNeverCountTheDeclaredLength()
    {
        string key = NewKey();
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            if (request.Content!.Headers.ContentLength > 0) throw new HttpRequestException("before serialization");
            await request.Content.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage empty = Request(key, new ByteArrayContent([]));
        using (await client.SendAsync(empty, HttpCompletionOption.ResponseHeadersRead)) { }
        using HttpRequestMessage unread = Request(key, new ByteArrayContent([1, 2, 3]));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(unread, HttpCompletionOption.ResponseHeadersRead));
        AssertCounters(key, 0, requests: 2, errors: 1);
    }

    [Fact]
    public async Task UploadUsesTheSameInterlockedLongWraparoundAsDownload()
    {
        string key = NewKey();
        SourceCounters counters = _registry.For(key);
        Interlocked.Exchange(ref counters.NetUp, long.MaxValue - 1);
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage request = Request(key, new ByteArrayContent([1, 2, 3]));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        AssertCounters(key, long.MinValue + 1, requests: 1, errors: 0);
    }

    [Fact]
    public async Task ParallelUploadsAddUpAndKeepDiskCountersUntouched()
    {
        string key = NewKey();
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            using YieldingSink sent = new();
            await request.Content!.CopyToAsync(sent, token);
            return Response();
        })));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            using HttpRequestMessage request = Request(key, new MixedWritesContent());
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }));
        AssertCounters(key, 16 * 7, requests: 16, errors: 0);
    }

    [Fact]
    public async Task UploadUsesTheExistingHostFallbackWhenThereIsNoSourceOption()
    {
        string host = NewKey() + ".test";
        using HttpClient client = new(Metered(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Response();
        })));
        using HttpRequestMessage request = new(HttpMethod.Post, "https://" + host + "/body") { Content = new ByteArrayContent([1, 2]) };
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        AssertCounters(host, 2, requests: 1, errors: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedRegistryKeepsItsOwnCountersAndOverflowAfterAnotherRegistryIsSaturated(bool sync)
    {
        // Model a process-wide registry filled by unrelated tests, without touching Shared at all.
        SourceRegistry saturated = new(TransferMeter.MaximumSources);
        for (int index = 0; index < TransferMeter.MaximumSources; index++) saturated.For("foreign-" + index);
        SourceCounters foreignOverflow = saturated.For("kept");
        Interlocked.Add(ref foreignOverflow.NetUp, 4711);

        SourceRegistry isolated = new(1);
        SourceCounters kept = isolated.For("kept");
        using HttpMessageInvoker foreign = new(CreateMeter(saturated));
        using HttpMessageInvoker local = new(CreateMeter(isolated));
        await Send(foreign, "kept", [1, 2, 3]);
        await Send(local, "kept", [1, 2, 3]);
        await Send(local, "overflow-a", [4, 5]);
        await Send(local, "overflow-b", [6, 7, 8, 9]);

        SourceCounters overflow = isolated.For("overflow-a");
        Assert.Same(kept, isolated.For("kept"));
        Assert.Same(overflow, isolated.For("overflow-b"));
        Assert.NotSame(kept, overflow);
        Assert.NotSame(foreignOverflow, overflow);
        Assert.Equal(3, Read(ref kept.NetUp));
        Assert.Equal(2, Read(ref kept.NetDown));
        Assert.Equal(1, Read(ref kept.Requests));
        Assert.Equal(6, Read(ref overflow.NetUp));
        Assert.Equal(4, Read(ref overflow.NetDown));
        Assert.Equal(2, Read(ref overflow.Requests));
        Assert.Equal(0, Read(ref overflow.Errors));
        Assert.Equal(0, Read(ref overflow.DiskRead));
        Assert.Equal(0, Read(ref overflow.DiskWritten));
        Assert.Equal(4714, Read(ref foreignOverflow.NetUp));
        Assert.Equal(1, Read(ref foreignOverflow.Requests));
        Assert.Equal(TransferMeter.MaximumSources + 1, saturated.All.Count);
        Assert.Equal(new[] { "kept", TransferMeter.OtherKey }, isolated.All.Select(pair => pair.Key).Order());

        static MeteringHandler CreateMeter(SourceRegistry registry) => new(new Handler(async (request, token) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, token);
            return Response();
        }, (request, token) =>
        {
            request.Content!.CopyTo(Stream.Null, null, token);
            return Response();
        }), registry);

        async Task Send(HttpMessageInvoker invoker, string key, byte[] payload)
        {
            using HttpRequestMessage request = Request(key, new ByteArrayContent(payload));
            using HttpResponseMessage response = sync
                ? invoker.Send(request, CancellationToken.None)
                : await invoker.SendAsync(request, CancellationToken.None);
            Assert.Equal(new byte[] { 9, 8 }, await response.Content.ReadAsByteArrayAsync());
        }
    }

    private void AssertCounters(string key, long up, long requests, long errors, long down = 0)
    {
        SourceCounters counters = _registry.For(key);
        Assert.Equal(up, Read(ref counters.NetUp));
        Assert.Equal(down, Read(ref counters.NetDown));
        Assert.Equal(requests, Read(ref counters.Requests));
        Assert.Equal(errors, Read(ref counters.Errors));
        Assert.Equal(0, Read(ref counters.DiskRead));
        Assert.Equal(0, Read(ref counters.DiskWritten));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>? send = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => send is null ? throw new NotSupportedException() : send(request, cancellationToken);
    }

    /// <summary>Unknown-length producer. ReadAsStream is deliberately unsupported: serialization must be delegated.</summary>
    private sealed class ScriptedContent(byte[] prefix, Exception? failure = null, Action? afterPrefix = null) : HttpContent
    {
        public int Serializations { get; private set; }
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => throw new NotSupportedException("Do not pre-read uploads");
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => throw new NotSupportedException("Do not pre-read uploads");
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Serializations++;
            stream.Write(prefix);
            afterPrefix?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Serializations++;
            await Task.Yield();
            await stream.WriteAsync(prefix, cancellationToken);
            afterPrefix?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class MixedWritesContent : HttpContent
    {
        public int Serializations { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 7; return true; }
        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Serializations++;
            stream.WriteByte(1);
            stream.Write(new byte[] { 0, 2, 3, 0 }, 1, 2);
            stream.Write(new byte[] { 4, 5, 6, 7 }.AsSpan());
            stream.Write(Array.Empty<byte>(), 0, 0);
            stream.Flush();
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Serializations++;
            await stream.WriteAsync(new byte[] { 1 }.AsMemory(), cancellationToken);
            await stream.WriteAsync(new byte[] { 0, 2, 3, 0 }, 1, 2, cancellationToken);
            stream.WriteByte(4);
            stream.Write(new byte[] { 5, 6 }.AsSpan());
            stream.Write(new byte[] { 0, 7, 0 }, 1, 1);
            await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] payload) : MemoryStream(payload)
    {
        public override bool CanSeek => false;
    }

    /// <summary>Exercises both synchronously completing and genuinely suspended/faulted ValueTask writes.</summary>
    private sealed class YieldingSink(bool failAfterFirstWrite = false, CancellationTokenSource? cancellation = null) : MemoryStream
    {
        private void BeforeWrite()
        {
            if (!failAfterFirstWrite || Length == 0) return;
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("synthetic destination failure");
        }
        public override void WriteByte(byte value) { BeforeWrite(); base.WriteByte(value); }
        public override void Write(ReadOnlySpan<byte> buffer) { BeforeWrite(); base.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            BeforeWrite();
            base.Write(buffer.Span);
        }
    }
}
