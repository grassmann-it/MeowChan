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
/// Counts requests, errors and request/response body bytes of the transport below it per source. It forwards every request and
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
        if (request.Content is { } content)
        {
            // A new send must not nest meters or reuse a wrapper's HttpContent buffer. The original
            // content still decides whether it can be replayed; every actual serialization counts.
            request.Content = new UploadMeteringContent(content, counters);
        }
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

    /// <summary>
    /// Counts at the serialization destination, not by pre-reading the content. HttpClient's transport
    /// uses CopyTo/CopyToAsync, including for already-buffered content and repeated serialization.
    /// The inner content keeps its own sync/async serialization, cancellation and replay behavior.
    /// Failures propagate to Send/SendAsync's existing error accounting (never counted twice here).
    /// </summary>
    private sealed class UploadMeteringContent : HttpContent
    {
        internal HttpContent Inner { get; }
        private readonly SourceCounters _counters;
        private readonly bool _contentLengthWasExplicitlyProvided;

        internal UploadMeteringContent(HttpContent inner, SourceCounters counters)
        {
            _counters = counters;
            // A previous wrapper may have had an explicit length removed by the caller. Remember
            // that fact before unwrapping: the original content still has its old header, but the
            // currently presented request must not grow it back on the next send.
            _contentLengthWasExplicitlyProvided = inner is UploadMeteringContent previous
                ? previous._contentLengthWasExplicitlyProvided
                : inner.Headers.TryGetValues("Content-Length", out _);
            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            // Preserve the currently presented headers, including edits made between sends, but
            // serialize the original content rather than a previous meter (or its cached buffer).
            while (inner is UploadMeteringContent previous) inner = previous.Inner;
            Inner = inner;
        }

        protected override bool TryComputeLength(out long length)
        {
            // Do not resurrect an explicitly removed Content-Length from the unwrapped content.
            // For normal computed lengths the inner content remains authoritative, just as it was
            // before metering was added.
            if (_contentLengthWasExplicitlyProvided && !Headers.TryGetValues("Content-Length", out _))
            {
                length = 0;
                return false;
            }
            long? known = Inner.Headers.ContentLength;
            length = known.GetValueOrDefault();
            return known.HasValue;
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using UploadCountingStream target = new(stream, _counters);
            Inner.CopyTo(target, context, cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using UploadCountingStream target = new(stream, _counters);
            await Inner.CopyToAsync(target, context, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Non-owning serialization destination: only successfully completed writes count. Like reads on
    /// CountingStream, a failed operation cannot report a partial byte count; earlier chunks remain.
    /// No per-chunk allocation on the synchronously completing path, no length-based estimates.
    /// </summary>
    private sealed class UploadCountingStream(Stream inner, SourceCounters counters) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            if (!buffer.IsEmpty) Interlocked.Add(ref counters.NetUp, buffer.Length);
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            Interlocked.Increment(ref counters.NetUp);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask pending = inner.WriteAsync(buffer, cancellationToken);
            if (!pending.IsCompletedSuccessfully) return AwaitWrite(pending, buffer.Length);
            pending.GetAwaiter().GetResult();
            if (!buffer.IsEmpty) Interlocked.Add(ref counters.NetUp, buffer.Length);
            return ValueTask.CompletedTask;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        private async ValueTask AwaitWrite(ValueTask pending, int count)
        {
            await pending.ConfigureAwait(false);
            if (count > 0) Interlocked.Add(ref counters.NetUp, count);
        }

        // Stream's base disposal is intentionally used: the transport owns the destination.
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
