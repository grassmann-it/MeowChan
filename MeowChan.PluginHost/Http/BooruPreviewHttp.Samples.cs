namespace MeowChan.PluginHost.Http;

/// <summary>
/// The reduced-size version (sample) of a post, for the inline gallery only. It is the same bounded path as the thumbnails (declared preview
/// host, GET, no redirects, identity encoding, image signature, the same request gate of <see cref="MaximumConcurrentRequests"/>), with two
/// differences the caller sets: the largest body it accepts and how many bytes of samples are kept in memory. Both come from settings, so the
/// 1 MiB thumbnail limit stays what it was. A user action asks for exactly one sample at a time; nothing here fetches a page ahead.
/// </summary>
public sealed partial class BooruPreviewHttp
{
    /// <summary>The response deadline of a sample (it is larger than a thumbnail).</summary>
    public static readonly TimeSpan SampleResponseTimeout = TimeSpan.FromSeconds(60);

    private readonly object _sampleSync = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, byte[] Bytes)>> _samples = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, byte[] Bytes)> _sampleOrder = new();
    private long _sampleBytes;

    /// <summary>Total bytes of the samples kept in memory right now (diagnostics and tests).</summary>
    public long CachedSampleBytes { get { lock (_sampleSync) return _sampleBytes; } }

    /// <summary>
    /// The sample bytes, or null when the URL is not allowed, the body is larger than <paramref name="maximumBytes"/> or the provider did not
    /// deliver an image. A kept sample is returned without a request. Only the caller's cancellation is thrown.
    /// </summary>
    /// <param name="maximumBytes">The largest body accepted (declared or streamed).</param>
    /// <param name="cacheBytes">How many bytes of samples may be kept; a sample larger than this is shown but not kept.</param>
    public async Task<byte[]?> LoadSampleAsync(Uri? url, int maximumBytes, long cacheBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes <= 0) return null;
        if (_disposed || !IsAllowed(url) || HasForbiddenTransportDefaults()) return null;
        string key = url!.AbsoluteUri;
        if (TryGetSample(key, out byte[]? kept)) return kept;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            if (TryGetSample(key, out kept)) return kept;
            using CancellationTokenSource deadline = new(SampleResponseTimeout, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            byte[]? bytes = await FetchAsync(url, maximumBytes, linked.Token).ConfigureAwait(false);
            if (bytes != null) RememberSample(key, bytes, cacheBytes);
            return bytes;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
            or ObjectDisposedException) { return null; }
        finally { _gate.Release(); }
    }

    private bool TryGetSample(string key, out byte[]? bytes)
    {
        lock (_sampleSync)
        {
            if (_samples.TryGetValue(key, out LinkedListNode<(string Url, byte[] Bytes)>? node))
            {
                _sampleOrder.Remove(node);
                _sampleOrder.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }
        bytes = null;
        return false;
    }

    private void RememberSample(string key, byte[] bytes, long cacheBytes)
    {
        lock (_sampleSync)
        {
            if (_disposed || _samples.ContainsKey(key) || bytes.Length > cacheBytes) return;
            _samples[key] = _sampleOrder.AddFirst((key, bytes));
            _sampleBytes += bytes.Length;
            // The newest sample always stays; the least recently shown ones go until the budget holds.
            while (_sampleBytes > cacheBytes && _sampleOrder.Last is { } last && !ReferenceEquals(last, _sampleOrder.First))
            {
                _sampleOrder.RemoveLast();
                _samples.Remove(last.Value.Url);
                _sampleBytes -= last.Value.Bytes.Length;
            }
        }
    }

    private void ClearSamples()
    {
        lock (_sampleSync)
        {
            _samples.Clear();
            _sampleOrder.Clear();
            _sampleBytes = 0;
        }
    }
}
