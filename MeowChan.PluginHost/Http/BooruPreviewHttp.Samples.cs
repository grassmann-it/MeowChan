namespace MeowChan.PluginHost.Http;

/// <summary>Result of a gallery sample request, including whether its response deadline expired.</summary>
internal readonly record struct BooruSampleResult(byte[]? Bytes, bool TimedOut = false);

/// <summary>
/// The reduced-size version (sample) of a post, for the gallery. It is the same bounded path as the thumbnails (declared preview
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
    private long _sampleBudget;

    /// <summary>Total bytes of the samples kept in memory right now (diagnostics and tests).</summary>
    public long CachedSampleBytes { get { lock (_sampleSync) return _sampleBytes; } }

    /// <summary>
    /// The sample bytes, or null when the URL is not allowed, the body is larger than <paramref name="maximumBytes"/> or the provider did not
    /// deliver an image. A kept sample is returned without a request. Only the caller's cancellation is thrown.
    /// </summary>
    /// <param name="maximumBytes">The largest body accepted (declared or streamed).</param>
    /// <param name="cacheBytes">How many bytes of samples may be kept; a sample larger than this is shown but not kept.</param>
    public async Task<byte[]?> LoadSampleAsync(Uri? url, int maximumBytes, long cacheBytes, CancellationToken cancellationToken = default) =>
        (await LoadSampleAsync(url, maximumBytes, cacheBytes, SampleResponseTimeout, cancellationToken).ConfigureAwait(false)).Bytes;

    /// <summary>Loads a gallery sample with the caller's response deadline.</summary>
    internal async Task<BooruSampleResult> LoadSampleAsync(Uri? url, int maximumBytes, long cacheBytes, TimeSpan responseTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? key = _disposed || maximumBytes <= 0 || !IsAllowed(url) || HasForbiddenTransportDefaults() ? null : url!.AbsoluteUri;
        if (TryGetSample(key, maximumBytes, cacheBytes, out byte[]? kept)) return new(kept);
        if (key is null) return default;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return default;
            if (TryGetSample(key, maximumBytes, cacheBytes: null, out kept)) return new(kept);
            using CancellationTokenSource deadline = new(responseTimeout, _clock);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                byte[]? bytes = await FetchAsync(url!, maximumBytes, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (bytes != null) RememberSample(key, bytes);
                return new(bytes);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(null, TimedOut: deadline.IsCancellationRequested);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
            or ObjectDisposedException) { return default; }
        finally { _gate.Release(); }
    }

    private bool TryGetSample(string? key, int maximumBytes, long? cacheBytes, out byte[]? bytes)
    {
        lock (_sampleSync)
        {
            if (cacheBytes is { } budget) _sampleBudget = Math.Max(0, budget);
            bool found = false;
            bytes = null;
            if (!_disposed && key != null && _samples.TryGetValue(key, out var node))
            {
                found = true;
                if (node.Value.Bytes.Length <= maximumBytes)
                {
                    bytes = node.Value.Bytes;
                    _sampleOrder.Remove(node);
                    _sampleOrder.AddFirst(node);
                }
            }
            TrimSamples();
            return found;
        }
    }

    private void RememberSample(string key, byte[] bytes)
    {
        lock (_sampleSync)
        {
            if (_disposed || _samples.ContainsKey(key) || bytes.Length > _sampleBudget) return;
            _samples[key] = _sampleOrder.AddFirst((key, bytes));
            _sampleBytes += bytes.Length;
            TrimSamples();
        }
    }

    private void TrimSamples()
    {
        while (_sampleBytes > _sampleBudget && _sampleOrder.Last is { } last)
        {
            _sampleOrder.RemoveLast();
            _samples.Remove(last.Value.Url);
            _sampleBytes -= last.Value.Bytes.Length;
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
