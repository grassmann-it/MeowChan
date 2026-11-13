using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Http;

/// <summary>
/// Counts requests, errors and response body bytes of the transport below it per source. It forwards every request and
/// response unchanged: status, headers (including <c>Content-Length</c>) and body bytes are the transport's own. The
/// source comes from <see cref="SourceKey"/> (set by <see cref="RequestPolicyHandler"/> or the booru host); a request
/// without it is counted under its host name. The per-chunk cost is one <see cref="Interlocked"/> add.
/// </summary>
internal sealed class MeteringHandler : DelegatingHandler
{
    /// <summary>The source a request belongs to, read by the metering handler at the transport.</summary>
    internal static readonly HttpRequestOptionsKey<string> SourceKey = new("MeowChan.TransferMeterSource");

    public MeteringHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SourceCounters counters = Begin(request);
        HttpResponseMessage response;
        try { response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (NoteFailure(exception, counters)) { throw; }
        return Complete(request, response, counters);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SourceCounters counters = Begin(request);
        HttpResponseMessage response;
        try { response = base.Send(request, cancellationToken); }
        catch (Exception exception) when (NoteFailure(exception, counters)) { throw; }
        return Complete(request, response, counters);
    }

    private static SourceCounters Begin(HttpRequestMessage request)
    {
        string? key = null;
        if (!request.Options.TryGetValue(SourceKey, out key))
            key = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.IdnHost : null;
        SourceCounters counters = TransferMeter.For(key);
        Interlocked.Increment(ref counters.Requests);
        return counters;
    }

    private static HttpResponseMessage Complete(HttpRequestMessage request, HttpResponseMessage response, SourceCounters counters)
    {
        if ((int)response.StatusCode >= 400) Interlocked.Increment(ref counters.Errors);
        if (request.Method != HttpMethod.Head) response.Content = new MeteringContent(response.Content, counters);
        return response;
    }

    /// <summary>Exception filter: counts the failure and never handles it.</summary>
    private static bool NoteFailure(Exception exception, SourceCounters counters)
    {
        if (exception is not OperationCanceledException) Interlocked.Increment(ref counters.Errors);
        return false;
    }

    /// <summary>Response content whose every read path goes through one counting stream; headers are copied from the original.</summary>
    private sealed class MeteringContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly SourceCounters _counters;

        public MeteringContent(HttpContent inner, SourceCounters counters)
        {
            _inner = inner;
            _counters = counters;
            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            long? known = _inner.Headers.ContentLength;
            length = known.GetValueOrDefault();
            return known.HasValue;
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
            => new CountingStream(_inner.ReadAsStream(cancellationToken), _counters);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new CountingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _counters);

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using CountingStream source = new(_inner.ReadAsStream(cancellationToken), _counters);
            source.CopyTo(stream);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using CountingStream source = new(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _counters);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Read-only pass-through that adds each non-empty read to the source's counter; it allocates nothing per read.</summary>
    internal sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly SourceCounters _counters;
        private int _failed;

        internal CountingStream(Stream inner, SourceCounters counters)
        {
            _inner = inner;
            _counters = counters;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read;
            try { read = _inner.Read(buffer); }
            catch (Exception exception) when (NoteReadFailure(exception)) { throw; }
            if (read > 0) Interlocked.Add(ref _counters.NetDown, read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> pending;
            try { pending = _inner.ReadAsync(buffer, cancellationToken); }
            catch (Exception exception) when (NoteReadFailure(exception)) { throw; }
            if (!pending.IsCompletedSuccessfully) return AwaitRead(pending);
            int read = pending.Result;
            if (read > 0) Interlocked.Add(ref _counters.NetDown, read);
            return new ValueTask<int>(read);
        }

        // Pooled state machine: a read that really suspends does not allocate either.
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<int> AwaitRead(ValueTask<int> pending)
        {
            int read;
            try { read = await pending.ConfigureAwait(false); }
            catch (Exception exception) when (NoteReadFailure(exception)) { throw; }
            if (read > 0) Interlocked.Add(ref _counters.NetDown, read);
            return read;
        }

        private bool NoteReadFailure(Exception exception)
        {
            // One failed body is one error, however many reads observe it. Cancellation is not a failure.
            if (exception is not OperationCanceledException && Interlocked.Exchange(ref _failed, 1) == 0)
                Interlocked.Increment(ref _counters.Errors);
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
